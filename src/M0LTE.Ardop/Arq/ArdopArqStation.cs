namespace M0LTE.Ardop.Arq;

/// <summary>
/// Binds an <see cref="ArdopArqEngine"/> to an <see cref="ArdopDemodulator"/> and
/// <see cref="ArdopModulator"/> under a single sample-count clock: one full-duplex
/// ARDOP station as a pure function of audio. Each <see cref="Step"/> consumes a block
/// of received samples and produces the same-sized block of transmit audio (silence
/// when idle), advancing the engine's clock by the block duration — so a hermetic test
/// runs whole sessions faster than real time from canned sample streams, and the live
/// path is the same code pumped by a sound-card duplex loop at its real rate. This is
/// the timing architecture the ARDOP design names as the Phase B risk control
/// (docs/ardop-design.md §7.1).
/// </summary>
/// <remarks>
/// The engine's RX→TX turnaround (<see cref="ArdopTxRequest.NotBeforeMs"/>) is honoured
/// by emitting silence until the earliest start time; frame playout is contiguous once
/// started, and <see cref="ArdopArqEngine.TransmitCompleted"/> fires at the sample on
/// which the frame ends — the repeat window therefore opens exactly where ardopcf's
/// <c>SoundFlush</c> opens it.
/// </remarks>
public sealed class ArdopArqStation
{
    private sealed class PendingTx
    {
        public required ArdopTxRequest Request { get; init; }

        public short[]? Audio { get; set; }

        public int Position { get; set; }
    }

    private readonly ArdopModulator _modulator;
    private readonly ArdopRxScope _scope = new();
    private readonly Queue<PendingTx> _txQueue = new();
    private PendingTx? _playing;
    private long _sampleClock;

    /// <summary>Creates a station. <paramref name="randomSeed"/> pins the engine's
    /// BREAK-repeat jitter for deterministic tests.</summary>
    public ArdopArqStation(ArdopArqConfig config, int? randomSeed = null, int driveLevel = 100)
    {
        Engine = new ArdopArqEngine(config, randomSeed);
        Demodulator = new ArdopDemodulator { Scope = _scope };
        _modulator = new ArdopModulator(driveLevel);

        Engine.TransmitRequested += request => _txQueue.Enqueue(new PendingTx { Request = request });
        Engine.MemoryArqResetRequested += Demodulator.ResetMemoryArq;
        Demodulator.FrameDecoded += frame =>
        {
            FrameDecoded?.Invoke(frame, NowMs);
            Engine.FrameReceived(frame, NowMs);
            Engine.SyncRxScope(_scope);
        };
    }

    /// <summary>The protocol engine (host-style commands go here).</summary>
    public ArdopArqEngine Engine { get; }

    /// <summary>The demodulator (exposed for instrumentation).</summary>
    public ArdopDemodulator Demodulator { get; }

    /// <summary>The station clock in ms — derived purely from samples stepped
    /// (12 samples/ms).</summary>
    public long NowMs => _sampleClock / 12;

    /// <summary>True while transmit audio is queued or playing.</summary>
    public bool IsTransmitting => _playing is not null || _txQueue.Count > 0;

    /// <summary>Optional per-frame transmit-audio hook — tests use it to corrupt or
    /// drop specific bursts. Receives the request and the clean modulated audio;
    /// returns what actually goes on the air.</summary>
    public Func<ArdopTxRequest, short[], short[]>? TransmitFilter { get; set; }

    /// <summary>Raised when a frame's last sample has been emitted, with the end-of-
    /// frame clock time — the instant the engine's repeat window opens.</summary>
    public event Action<ArdopTxRequest, long>? FrameTransmitted;

    /// <summary>Raised for every demodulated frame with the station clock time —
    /// test instrumentation (the engine is wired internally).</summary>
    public event Action<ArdopDecodedFrame, long>? FrameDecoded;

    /// <summary>
    /// Advances the station by one audio block: feeds <paramref name="rxSamples"/> to
    /// the demodulator, renders the same number of transmit samples, then polls the
    /// engine at the new clock. Use short blocks (≤ 240 samples / 20 ms) on live audio;
    /// hermetic tests may use larger blocks — timing only ever gets conservative.
    /// </summary>
    public short[] Step(ReadOnlySpan<short> rxSamples)
    {
        var tx = new short[rxSamples.Length];
        RenderTransmit(tx);

        Demodulator.ProcessSamples(rxSamples);
        _sampleClock += rxSamples.Length;
        Engine.Poll(NowMs);
        Engine.SyncRxScope(_scope);
        return tx;
    }

    private void RenderTransmit(short[] block)
    {
        int position = 0;
        while (position < block.Length)
        {
            if (_playing is null)
            {
                if (_txQueue.Count == 0)
                {
                    return;  // silence
                }

                // Honour the turnaround gate, then start on the next sample.
                long startSample = Math.Max(
                    _sampleClock + position, _txQueue.Peek().Request.NotBeforeMs * 12);
                if (startSample >= _sampleClock + block.Length)
                {
                    return;  // gate not yet open — silence for this block
                }

                position = (int)(startSample - _sampleClock);
                _playing = _txQueue.Dequeue();
                short[] audio = _modulator.Modulate(
                    _playing.Request.EncodedFrame, _playing.Request.LeaderLengthMs);
                _playing.Audio = TransmitFilter is null
                    ? audio
                    : TransmitFilter(_playing.Request, audio);
            }

            int copy = Math.Min(block.Length - position, _playing.Audio!.Length - _playing.Position);
            Array.Copy(_playing.Audio, _playing.Position, block, position, copy);
            _playing.Position += copy;
            position += copy;

            if (_playing.Position == _playing.Audio.Length)
            {
                long endMs = (_sampleClock + position) / 12;
                var finished = _playing.Request;
                _playing = null;
                Engine.TransmitCompleted(endMs);
                Engine.SyncRxScope(_scope);
                FrameTransmitted?.Invoke(finished, endMs);
            }
        }
    }
}
