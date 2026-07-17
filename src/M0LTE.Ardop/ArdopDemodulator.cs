namespace M0LTE.Ardop;

/// <summary>
/// ARDOP receiver: two-tone leader acquisition (±<c>TuningRange</c> Hz capture
/// honed to ~1 Hz), NCO mix to a reversed-sideband 1500 Hz passband, envelope-correlator
/// symbol framing, phase-reversal frame sync, minimal-distance frame-type decode, per-
/// symbol Goertzel 4FSK data demodulation at 50/100/600 Bd, differential 4PSK/8PSK and
/// 16QAM demodulation on 1/2/4/8 parallel 100 Bd carriers, RS + CRC block correction
/// and Memory-ARQ averaging across repeats (tone magnitudes for FSK, phase/magnitude
/// vectors for PSK/QAM). Ported from ardopcf
/// (git a7c9228, MIT, © 2014-2024 Rick Muething, John Wiseman, Peter LaRue),
/// <c>SoundInput.c</c>: <c>ProcessNewSamples</c> :810, <c>SearchFor2ToneLeader3</c>
/// :1604, <c>MixNCOFilter</c>/<c>FSMixFilter2000Hz</c> :423-650, <c>Filter75Hz</c> /
/// <c>EnvelopeCorrelator</c> / <c>Acquire2ToneLeaderSymbolFraming</c> :1862-1970,
/// <c>AcquireFrameSyncRSB</c> :1971, <c>DemodFrameType4FSK</c> /
/// <c>MinimalDistanceFrameType</c> / <c>Acquire4FSKFrameType</c> :2052-2429,
/// <c>Demod1Car4FSK(Char)</c> :2431-2739, <c>Demod1Car4FSK600(Char)</c> :2741-2852,
/// <c>Update4FSKConstellation</c> :3823, <c>UpdatePhaseConstellation</c> :3921,
/// <c>Decode1CarPSK</c> :4132, <c>ComputeAng1_Ang2</c> :4317,
/// <c>CorrectPhaseForTuningOffset</c> :4336, <c>Decode1CarQAM</c> :4436,
/// <c>InitDemodPSK</c>/<c>DemodPSK</c>/<c>Demod1CarPSKChar</c> :4508-4846,
/// <c>InitDemodQAM</c>/<c>DemodQAM</c>/<c>Demod1CarQAMChar</c> :4848-5282,
/// <c>WeightedAngleAvg</c> :4896, <c>Save{FSK,PSK,QAM}Samples</c> :4910-5032,
/// <c>Decode1Car4FSK</c> :4097, <c>DecodeFrame</c> :3349. Goertzel decoder only —
/// ardopcf's default; the opt-in SDFT decoder is a later robustness item
/// (docs/ardop-design.md §3.4). See PROVENANCE.md.
/// </summary>
/// <remarks>
/// <para>
/// Deliberate deviations from the reference (none affect the wire format): timing that
/// ardopcf takes from the wall clock (<c>Now</c>) — the 1 s frame-sync timeout, the
/// 20 s full-vs-narrow leader-search gate, Memory-ARQ staleness — derives here from the
/// consumed-sample count (12 samples/ms), making offline decodes deterministic; the
/// busy detector is not ported (it feeds ARQ ConRejBusy decisions, a Phase B concern);
/// the exactness contract is decoded-payload equivalence, not sample-exact DSP parity
/// (docs/ardop-design.md §6.1).
/// </para>
/// <para>
/// The demodulator runs unconnected (session ID 0xFF — FEC and monitor traffic) by
/// default; setting <see cref="ConnectedSessionId"/> switches the frame-type decoder to
/// the connected-session candidate acceptance rules.
/// </para>
/// </remarks>
public sealed class ArdopDemodulator
{
    /// <summary>ARDOP's native sample rate.</summary>
    public const int SampleRate = 12000;

    private const int ChunkSize = 1200;
    private const float TwoPi = 2 * MathF.PI;

    private enum RxState
    {
        SearchingForLeader,
        AcquireSymbolSync,
        AcquireFrameSync,
        AcquireFrameType,
        AcquireFrame,
    }

    private readonly int _squelch;
    private readonly int _tuningRange;

    // Leader search state.
    private RxState _state = RxState.SearchingForLeader;
    private float _offsetHz;
    private float _priorFineOffset = 1000f;
    private long _nowMs;
    private long _lastLeaderDetectMs;
    private long _lastGoodFrameTypeDecodeMs = -100000;
    private int _leaderReceivedMs = 1000;

    // Raw sample carry-over while searching for the leader (rawSamples).
    private readonly short[] _raw = new short[2 * ChunkSize + 2400];
    private int _rawLength;

    // NCO mixer state.
    private float _ncoPhase;

    // FSMixFilter2000Hz state.
    private readonly short[] _priorMixed = new short[120];
    private float _mixZin1;
    private float _mixZin2;
    private readonly float[] _mixZout0 = new float[27];
    private readonly float[] _mixZout1 = new float[27];
    private readonly float[] _mixZout2 = new float[27];

    // The filtered/mixed sample buffer (intFilteredMixedSamples).
    private readonly short[] _filtered = new short[6000];
    private int _filteredLength;
    private int _readPtr;

    // Frame decode state.
    private ArdopFrameInfo? _frame;
    private int _sampPerSym;
    private int _bytesLeft;      // SymbolsLeft — bytes still to demodulate
    private int _charIndex;
    private readonly byte[] _frameData = new byte[759];  // count+data+CRC+RS, all blocks
    private readonly int[] _toneMags = new int[16 * 759];
    private int _toneMagsIndex;
    private int _toneMagsLength;

    // PSK/QAM demod state (InitDemodPSK/InitDemodQAM, SoundInput.c:4508,4848):
    // per-carrier differential phases (milliradians, ±3142) and magnitudes per symbol.
    private const int MaxPhases = 520;
    private readonly short[][] _phases = NewPerCarrier();
    private readonly short[][] _mags = NewPerCarrier();
    private readonly short[] _pskPhaseLast = new short[8];   // intPSKPhase_1
    private readonly short[] _carMagThreshold = new short[8]; // intCarMagThreshold
    private readonly byte[][] _carrierData = [.. Enumerable.Range(0, 8).Select(_ => new byte[256])];
    private int _phasesLen;
    private bool _pskInitDone;

    // Memory ARQ: per-part/per-carrier running averages across repeats of the same
    // type — tone magnitudes for FSK, phase (+ magnitude for QAM) vectors for PSK/QAM.
    private readonly bool[] _carrierOk = new bool[8];
    private readonly int[] _sumCounts = new int[8];
    private readonly int[][] _toneMagsAvg = [new int[16 * 253], new int[16 * 253], new int[16 * 253]];
    private readonly short[][] _carPhaseAvg = NewPerCarrier();
    private readonly short[][] _carMagAvg = NewPerCarrier();
    private readonly byte[]?[] _goodPartData = new byte[8][];
    private long _memarqTimeMs;
    private int _lastDataFrameType = -1;

    private static short[][] NewPerCarrier() => [.. Enumerable.Range(0, 8).Select(_ => new short[MaxPhases])];

    /// <summary>Creates a demodulator. <paramref name="squelch"/> is the leader-detect
    /// squelch 0-10 (ardopcf default 5); <paramref name="tuningRangeHz"/> the leader
    /// capture range (ardopcf default ±100 Hz).</summary>
    public ArdopDemodulator(int squelch = 5, int tuningRangeHz = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(squelch, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tuningRangeHz, 200);
        _squelch = squelch;
        _tuningRange = tuningRangeHz;
    }

    /// <summary>Raised for every frame whose type was acquired, whether or not the body
    /// decoded (<see cref="ArdopDecodedFrame.Ok"/>).</summary>
    public event Action<ArdopDecodedFrame>? FrameDecoded;

    /// <summary>When set, frame-type decoding uses the connected-session acceptance
    /// rules with this session ID; when null (default) the unconnected/FEC rules with
    /// session ID 0xFF apply. Superseded by <see cref="Scope"/> when that is set.</summary>
    public byte? ConnectedSessionId { get; set; }

    /// <summary>Full session-identity scope for the frame-type decoder (candidate set +
    /// pending/connected session IDs). The ARQ engine keeps it in step; when null the
    /// legacy <see cref="ConnectedSessionId"/>/unconnected rules apply.</summary>
    public ArdopRxScope? Scope { get; set; }

    /// <summary>RXO (receive-only monitor) frame-type decoding: the session ID of a
    /// third-party session is unknown, so the type decode ignores the ID-XORed tones
    /// and the likely session ID is decoded separately into
    /// <see cref="RxoSessionId"/> (<c>RxoMinimalDistanceFrameType</c>, ardopcf
    /// RXO.c:116). Overrides <see cref="Scope"/>/<see cref="ConnectedSessionId"/>
    /// while true.</summary>
    public bool RxoMode { get; set; }

    /// <summary>The most recently decoded session ID in RXO mode
    /// (<c>bytSessionID</c> as maintained by <c>RxoDecodeSessionID</c>; 0xFF until a
    /// frame updates it). Indicates which session decoded frames belong to.</summary>
    public byte RxoSessionId { get; private set; } = 0xFF;

    /// <summary>Memory-ARQ staleness timeout, ms of received audio
    /// (<c>FECMemarqTimeout</c>, SoundInput.c:181 — just longer than the longest data
    /// frame times its maximum repeats).</summary>
    public int MemoryArqTimeoutMs { get; set; } = 36000;

    /// <summary>Feeds 12 kHz samples. May be called with any length; frames are raised
    /// via <see cref="FrameDecoded"/> as they complete.</summary>
    public void ProcessSamples(ReadOnlySpan<short> samples)
    {
        while (!samples.IsEmpty)
        {
            int take = Math.Min(ChunkSize, samples.Length);
            ProcessChunk(samples[..take]);
            samples = samples[take..];
        }
    }

    /// <summary>Feeds normalised float samples (−1..1) at 12 kHz.</summary>
    public void ProcessSamples(ReadOnlySpan<float> samples)
    {
        var block = new short[Math.Min(samples.Length, ChunkSize)];
        while (!samples.IsEmpty)
        {
            int take = Math.Min(block.Length, samples.Length);
            for (int i = 0; i < take; i++)
            {
                block[i] = (short)Math.Clamp(samples[i] * 32768f, short.MinValue, short.MaxValue);
            }

            ProcessChunk(block.AsSpan(0, take));
            samples = samples[take..];
        }
    }

    // ProcessNewSamples (SoundInput.c:810), minus busy detection and the ARQ
    // protocol-state shortcuts, which belong to Phase B.
    private void ProcessChunk(ReadOnlySpan<short> chunk)
    {
        _nowMs += chunk.Length * 1000L / SampleRate;
        CheckMemoryArqTime();

        // Append to any carried-over raw samples.
        chunk.CopyTo(_raw.AsSpan(_rawLength));
        _rawLength += chunk.Length;

        int consumed = 0;
        if (_state == RxState.SearchingForLeader)
        {
            while (_state == RxState.SearchingForLeader && _rawLength - consumed >= 1200)
            {
                if (SearchFor2ToneLeader3(_raw.AsSpan(consumed, _rawLength - consumed)))
                {
                    _lastLeaderDetectMs = _nowMs;
                    consumed += 480; // skip 2 symbols (ProcessNewSamples:882)
                    InitializeMixedSamples();
                    _state = RxState.AcquireSymbolSync;
                }
                else
                {
                    consumed += 240;
                }
            }

            if (_state == RxState.SearchingForLeader)
            {
                // Carry unused samples to the next call.
                Array.Copy(_raw, consumed, _raw, 0, _rawLength - consumed);
                _rawLength -= consumed;
                return;
            }
        }

        // Leader found: mix + filter everything we have.
        MixNcoFilter(_raw.AsSpan(consumed, _rawLength - consumed));
        _rawLength = 0;

        if (_state == RxState.AcquireSymbolSync
            && _filteredLength - _readPtr > 860)
        {
            Acquire2ToneLeaderSymbolFraming();
            _state = RxState.AcquireFrameSync;
        }

        if (_state == RxState.AcquireFrameSync)
        {
            bool found = AcquireFrameSyncRsb();
            if (found)
            {
                _state = RxState.AcquireFrameType;
            }

            // Remove used samples whether or not sync was found.
            _filteredLength -= _readPtr;
            Array.Copy(_filtered, _readPtr, _filtered, 0, _filteredLength);
            _readPtr = 0;

            if (!found && _nowMs - _lastLeaderDetectMs > 1000)
            {
                Reset();
                return;
            }

            _toneMagsIndex = 0;
        }

        if (_state == RxState.AcquireFrameType)
        {
            int type = Acquire4FskFrameType();
            if (type == -2)
            {
                return; // insufficient samples — wait for more
            }

            if (type == -1)
            {
                Reset();
                return;
            }

            // Consume the type symbols.
            _filteredLength -= _readPtr;
            Array.Copy(_filtered, _readPtr, _filtered, 0, _filteredLength);
            _readPtr = 0;

            var info = ArdopFrameInfo.Get((byte)type);
            if (ArdopFrameType.IsShortControl(info.Type))
            {
                // Complete already. Quality over the type symbols — ardopcf assesses
                // the first 10 tone magnitudes (intToneMagsLength = 10 quirk,
                // DemodFrameType4FSK).
                int quality = FskQuality(_toneMags, 10);
                Reset();
                Emit(new ArdopDecodedFrame { Type = info.Type, Ok = true, Data = [], Quality = quality });
                return;
            }

            _frame = info;
            _sampPerSym = 12000 / info.Baud;
            _bytesLeft = ArdopFrameType.IsData(info.Type)
                ? info.DataLength + info.RsLength + 3
                : info.DataLength + info.RsLength;
            if (info.DataLength == 600)
            {
                _bytesLeft += 6; // the two extra per-block count+CRC sets of the long frame
            }

            _toneMagsLength = 16 * _bytesLeft;
            _toneMagsIndex = 0;
            _charIndex = 0;
            _pskInitDone = false;
            _phasesLen = 0;

            if (_lastDataFrameType != info.Type)
            {
                ResetMemoryArq();
            }

            _lastDataFrameType = info.Type;
            _state = RxState.AcquireFrame;
        }

        if (_state == RxState.AcquireFrame)
        {
            if (_frame!.Modulation == ArdopModulation.Fsk4)
            {
                DemodulateFrameBytes();
                if (_state == RxState.AcquireFrame)
                {
                    return; // wait for more samples
                }

                DecodeCompletedFrame();
            }
            else
            {
                DemodulatePskFrame();
                if (_state == RxState.AcquireFrame)
                {
                    return; // wait for more samples
                }

                DecodeCompletedPskFrame();
            }
        }
    }

    private void Reset()
    {
        _state = RxState.SearchingForLeader;
        _filteredLength = 0;
        _readPtr = 0;
        _rawLength = 0;
    }

    private int _rmtLeaderMeasureMs;

    // Every emitted frame carries the timing the ARQ layer needs: the measured leader
    // (intLeaderRcvdMs) and the leader-detect→type-decode duration (intRmtLeaderMeasure).
    private void Emit(ArdopDecodedFrame frame) => FrameDecoded?.Invoke(frame with
    {
        LeaderReceivedMs = _leaderReceivedMs,
        RemoteLeaderMeasureMs = _rmtLeaderMeasureMs,
    });

    private void InitializeMixedSamples()
    {
        // intMFSReadPtr offset 30 accommodates the mix filter's delay
        // (InitializeMixedSamples, SoundInput.c:377).
        Array.Clear(_priorMixed);
        _filteredLength = 0;
        _readPtr = 30;
        _ncoPhase = 0;
        _mixZin1 = _mixZin2 = 0;
        Array.Clear(_mixZout0);
        Array.Clear(_mixZout1);
        Array.Clear(_mixZout2);
    }

    // ---------------------------------------------------------------- Goertzel

    // GoertzelRealImag (SoundInput.c:1440): N-sample single-bin DFT, bin m need not be
    // an integer. Results scaled by 2/N; the imaginary sign matches the reference.
    private static void Goertzel(ReadOnlySpan<short> samples, int ptr, int n, float m, out float real, out float imag)
    {
        float z1 = 0, z2 = 0, w = 0;
        float coeff = 2 * MathF.Cos(TwoPi * m / n);

        for (int i = 0; i <= n; i++)
        {
            w = i == n ? z1 * coeff - z2 : samples[ptr] + z1 * coeff - z2;
            z2 = z1;
            z1 = w;
            ptr++;
        }

        real = 2 * (w - MathF.Cos(TwoPi * m / n) * z2) / n;
        imag = 2 * (MathF.Sin(TwoPi * m / n) * z2) / n;
    }

    private float[] _hammingWindow = [];

    // GoertzelRealImagHamming (SoundInput.c:1535).
    private void GoertzelHamming(ReadOnlySpan<short> samples, int ptr, int n, float m, out float real, out float imag)
    {
        if (_hammingWindow.Length != n)
        {
            _hammingWindow = new float[n];
            float ang = TwoPi / (n - 1);
            for (int i = 0; i < n; i++)
            {
                _hammingWindow[i] = 0.54f - 0.46f * MathF.Cos(i * ang);
            }
        }

        float z1 = 0, z2 = 0, w = 0;
        float coeff = 2 * MathF.Cos(TwoPi * m / n);
        for (int i = 0; i <= n; i++)
        {
            w = i == n ? z1 * coeff - z2 : samples[ptr] * _hammingWindow[i] + z1 * coeff - z2;
            z2 = z1;
            z1 = w;
            ptr++;
        }

        real = 2 * (w - MathF.Cos(TwoPi * m / n) * z2) / n;
        imag = 2 * (MathF.Sin(TwoPi * m / n) * z2) / n;
    }

    private float[] _hanningWindow = [];

    // GoertzelRealImagHanning (SoundInput.c:1483) — the window the PSK/QAM symbol
    // probes use.
    private void GoertzelHanning(ReadOnlySpan<short> samples, int ptr, int n, float m, out float real, out float imag)
    {
        if (_hanningWindow.Length != n)
        {
            _hanningWindow = new float[n];
            float ang = TwoPi / (n - 1);
            for (int i = 0; i < n; i++)
            {
                _hanningWindow[i] = 0.5f - 0.5f * MathF.Cos(i * ang);
            }
        }

        float z1 = 0, z2 = 0, w = 0;
        float coeff = 2 * MathF.Cos(TwoPi * m / n);
        for (int i = 0; i <= n; i++)
        {
            w = i == n ? z1 * coeff - z2 : samples[ptr] * _hanningWindow[i] + z1 * coeff - z2;
            z2 = z1;
            z1 = w;
            ptr++;
        }

        real = 2 * (w - MathF.Cos(TwoPi * m / n) * z2) / n;
        imag = 2 * (MathF.Sin(TwoPi * m / n) * z2) / n;
    }

    // SpectralPeakLocator (SoundInput.c:1587) — 3-bin interpolator, factor 1.22
    // optimised for the Hamming window.
    private static float SpectralPeakLocator(
        float m1Re, float m1Im, float re, float im, float p1Re, float p1Im, out float centreMag)
    {
        centreMag = MathF.Sqrt(re * re + im * im);
        float left = MathF.Sqrt(m1Re * m1Re + m1Im * m1Im);
        float right = MathF.Sqrt(p1Re * p1Re + p1Im * p1Im);
        return 1.22f * (right - left) / (left + centreMag + right);
    }

    // ------------------------------------------------------------ Leader search

    // SearchFor2ToneLeader3 (SoundInput.c:1607): 10 Hz-bin Hamming Goertzel scan with
    // spectral-peak interpolation, then a narrow re-estimate requiring two consecutive
    // detects within 2.9 Hz.
    private bool SearchFor2ToneLeader3(ReadOnlySpan<short> samples)
    {
        if (samples.Length < 1200)
        {
            return false;
        }

        Span<float> gRe = stackalloc float[56];
        Span<float> gIm = stackalloc float[56];
        Span<float> mag = stackalloc float[56];
        float coarseOffset = 1000;

        if (_nowMs - _lastGoodFrameTypeDecodeMs > 20000 && _tuningRange > 0)
        {
            // Full search over the tuning range.
            int startBin = (200 - _tuningRange) / 10;
            int stopBin = 55 - startBin;
            float maxPeakSn = 0;
            int iAtMaxPeak = 0;

            for (int i = startBin; i <= stopBin; i++)
            {
                GoertzelHamming(samples, 0, 1200, i + 122.5f, out gRe[i], out gIm[i]);
                mag[i] = gRe[i] * gRe[i] + gIm[i] * gIm[i];
            }

            for (int i = startBin + 5; i <= stopBin - 10; i++)
            {
                // Product of the two tones, ratioed to noise bins either side.
                float power = MathF.Sqrt(mag[i] * mag[i + 5]);
                float avgNoise = (mag[i - 5] + mag[i - 3] + mag[i + 8] + mag[i + 10]) / 4;
                float peak = power / avgNoise;
                if (peak > maxPeakSn)
                {
                    maxPeakSn = peak;
                    iAtMaxPeak = i + 122;
                }
            }

            if (iAtMaxPeak - 123 >= startBin && iAtMaxPeak - 118 <= stopBin)
            {
                float binAdj = 0;
                int interpCount = 0;

                float adj1475 = SpectralPeakLocator(
                    gRe[iAtMaxPeak - 123], gIm[iAtMaxPeak - 123],
                    gRe[iAtMaxPeak - 122], gIm[iAtMaxPeak - 122],
                    gRe[iAtMaxPeak - 121], gIm[iAtMaxPeak - 121], out _);
                if (adj1475 is < 1.0f and > -1.0f)
                {
                    binAdj = adj1475;
                    interpCount++;
                }

                float adj1525 = SpectralPeakLocator(
                    gRe[iAtMaxPeak - 118], gIm[iAtMaxPeak - 118],
                    gRe[iAtMaxPeak - 117], gIm[iAtMaxPeak - 117],
                    gRe[iAtMaxPeak - 116], gIm[iAtMaxPeak - 116], out _);
                if (adj1525 is < 1.0f and > -1.0f)
                {
                    binAdj += adj1525;
                    interpCount++;
                }

                if (interpCount == 0)
                {
                    _priorFineOffset = 1000f;
                    return false;
                }

                binAdj /= interpCount;
                coarseOffset = 10f * (iAtMaxPeak + binAdj - 147);
            }
            else
            {
                _priorFineOffset = 1000f;
                return false;
            }
        }

        // Narrow search around the coarse (or previous) offset.
        float trialOffset = coarseOffset < 999 ? coarseOffset : _offsetHz;
        if (MathF.Abs(trialOffset) > _tuningRange && _tuningRange > 0)
        {
            _priorFineOffset = 1000f;
            return false;
        }

        float leftCar = 147.5f + trialOffset / 10f;
        float rightCar = 152.5f + trialOffset / 10f;

        GoertzelHamming(samples, 0, 1200, 142.5f + trialOffset / 10f, out float ctrR, out float ctrI);
        float avgNoisePerBin = ctrR * ctrR + ctrI * ctrI;
        GoertzelHamming(samples, 0, 1200, 145.0f + trialOffset / 10f, out ctrR, out ctrI);
        avgNoisePerBin += ctrR * ctrR + ctrI * ctrI;
        GoertzelHamming(samples, 0, 1200, 155.0f + trialOffset / 10f, out ctrR, out ctrI);
        avgNoisePerBin += ctrR * ctrR + ctrI * ctrI;
        GoertzelHamming(samples, 0, 1200, 157.5f + trialOffset / 10f, out ctrR, out ctrI);
        avgNoisePerBin += ctrR * ctrR + ctrI * ctrI;
        avgNoisePerBin *= 0.25f;

        Span<float> leftR = stackalloc float[3];
        Span<float> leftI = stackalloc float[3];
        Span<float> rightR = stackalloc float[3];
        Span<float> rightI = stackalloc float[3];
        GoertzelHamming(samples, 0, 1200, leftCar - 1, out leftR[0], out leftI[0]);
        GoertzelHamming(samples, 0, 1200, leftCar, out leftR[1], out leftI[1]);
        float leftP = leftR[1] * leftR[1] + leftI[1] * leftI[1];
        GoertzelHamming(samples, 0, 1200, leftCar + 1, out leftR[2], out leftI[2]);
        GoertzelHamming(samples, 0, 1200, rightCar - 1, out rightR[0], out rightI[0]);
        GoertzelHamming(samples, 0, 1200, rightCar, out rightR[1], out rightI[1]);
        float rightP = rightR[1] * rightR[1] + rightI[1] * rightI[1];

        // The reference computes this last bin with the un-windowed Goertzel — kept.
        Goertzel(samples, 0, 1200, rightCar + 1, out rightR[2], out rightI[2]);

        // Reject a single carrier; average the pair when within 4:1.
        float pairPower;
        if (leftP > 4 * rightP)
        {
            pairPower = rightP;
        }
        else if (rightP > 4 * leftP)
        {
            pairPower = leftP;
        }
        else
        {
            pairPower = MathF.Sqrt(leftP * rightP);
        }

        float snDbPwr = 10 * MathF.Log10(pairPower / avgNoisePerBin);

        // Early leader detect over the first 2 symbols (480 samples, 25 Hz bins).
        Goertzel(samples, 0, 480, 57.0f + trialOffset / 25f, out ctrR, out ctrI);
        float earlyNoise = ctrR * ctrR + ctrI * ctrI;
        Goertzel(samples, 0, 480, 58.0f + trialOffset / 25f, out ctrR, out ctrI);
        earlyNoise += ctrR * ctrR + ctrI * ctrI;
        Goertzel(samples, 0, 480, 62.0f + trialOffset / 25f, out ctrR, out ctrI);
        earlyNoise += ctrR * ctrR + ctrI * ctrI;
        Goertzel(samples, 0, 480, 63.0f + trialOffset / 25f, out ctrR, out ctrI);
        earlyNoise = MathF.Max(1000.0f, 0.25f * (earlyNoise + ctrR * ctrR + ctrI * ctrI));

        Goertzel(samples, 0, 480, 59 + trialOffset / 25f, out ctrR, out ctrI);
        float earlyLeft = ctrR * ctrR + ctrI * ctrI;
        Goertzel(samples, 0, 480, 61 + trialOffset / 25f, out ctrR, out ctrI);
        float earlyRight = ctrR * ctrR + ctrI * ctrI;

        float earlyPower;
        if (earlyLeft > 4 * earlyRight)
        {
            earlyPower = earlyRight;
        }
        else if (earlyRight > 4 * earlyLeft)
        {
            earlyPower = earlyLeft;
        }
        else
        {
            earlyPower = MathF.Sqrt(earlyLeft * earlyRight);
        }

        float snDbPwrEarly = 10 * MathF.Log10(earlyPower / earlyNoise);

        if (snDbPwr > 4 + _squelch && snDbPwrEarly > _squelch
            && (earlyNoise > 100.0f || _priorFineOffset != 1000.0f))
        {
            float interpLeft = SpectralPeakLocator(
                leftR[0], leftI[0], leftR[1], leftI[1], leftR[2], leftI[2], out float leftMag);
            float interpRight = SpectralPeakLocator(
                rightR[0], rightI[0], rightR[1], rightI[1], rightR[2], rightI[2], out float rightMag);

            interpLeft = interpLeft * leftMag / (leftMag + rightMag);
            interpRight = interpRight * rightMag / (leftMag + rightMag);

            if (MathF.Abs(interpLeft + interpRight) < 1.0f)
            {
                float sum = interpLeft + interpRight;
                float offset = sum > 0
                    ? trialOffset + MathF.Min(sum * 10.0f, 3)
                    : trialOffset + MathF.Max(sum * 10.0f, -3);

                // Require a second detect with a near-identical offset — this is what
                // suppresses false triggers down to Squelch 3 (rev 0.8.2.2 note).
                if (MathF.Abs(_priorFineOffset - offset) < 2.9f)
                {
                    _offsetHz = offset;
                    _priorFineOffset = 1000f;
                    return true;
                }

                _priorFineOffset = offset;
                _offsetHz = offset;
            }
        }

        return false;
    }

    // ------------------------------------------------------- Mix + 2 kHz filter

    // MixNCOFilter (SoundInput.c:585): mix by (3000 + offset) Hz — reversing the
    // sideband about 1500 Hz — then the 23-section frequency-sampling filter.
    private void MixNcoFilter(ReadOnlySpan<short> newSamples)
    {
        if (newSamples.IsEmpty)
        {
            return;
        }

        float ncoPhaseInc = (3000 + _offsetHz) * TwoPi / 12000;
        Span<short> mixed = newSamples.Length <= 2400 ? stackalloc short[newSamples.Length] : new short[newSamples.Length];
        for (int i = 0; i < newSamples.Length; i++)
        {
            mixed[i] = (short)MathF.Ceiling(newSamples[i] * MathF.Cos(_ncoPhase));
            _ncoPhase += ncoPhaseInc;
            if (_ncoPhase > TwoPi)
            {
                _ncoPhase -= TwoPi;
            }
        }

        FsMixFilter2000Hz(mixed);
    }

    // FSMixFilter2000Hz (SoundInput.c:423): comb + resonators for bins 4-26
    // (400-2600 Hz), edge sections scaled 0.389, gain rescale 1/120.
    private void FsMixFilter2000Hz(ReadOnlySpan<short> mixed)
    {
        const int N = 120;
        const float R = 0.9995f;
        float rn = MathF.Pow(R, N);
        float r2 = MathF.Pow(R, 2);

        Span<float> coef = stackalloc float[27];
        for (int i = 4; i <= 26; i++)
        {
            coef[i] = 2 * R * MathF.Cos(TwoPi * i / N);
        }

        for (int i = 0; i < mixed.Length; i++)
        {
            float zin = i < N
                ? mixed[i] - rn * _priorMixed[i]
                : mixed[i] - rn * mixed[i - N];

            float zcomb = zin - _mixZin2 * r2;
            _mixZin2 = _mixZin1;
            _mixZin1 = zin;

            float filtered = 0;
            for (int j = 4; j <= 26; j++)
            {
                _mixZout0[j] = zcomb + coef[j] * _mixZout1[j] - r2 * _mixZout2[j];
                _mixZout2[j] = _mixZout1[j];
                _mixZout1[j] = _mixZout0[j];

                if (j == 4 || j == 26)
                {
                    filtered += 0.389f * _mixZout0[j];
                }
                else if ((j & 1) == 0)
                {
                    filtered += _mixZout0[j];
                }
                else
                {
                    filtered -= _mixZout0[j];
                }
            }

            _filtered[_filteredLength++] = (short)(filtered * 0.00833333333f);
        }

        // Save the trailing N samples for the next call's comb continuity. (Guard for a
        // final sub-120-sample chunk, which ardopcf's fixed ALSA blocks never produce.)
        if (mixed.Length >= N)
        {
            for (int i = 0; i < N; i++)
            {
                _priorMixed[i] = mixed[mixed.Length - N + i];
            }
        }
        else
        {
            Array.Copy(_priorMixed, mixed.Length, _priorMixed, 0, N - mixed.Length);
            for (int i = 0; i < mixed.Length; i++)
            {
                _priorMixed[N - mixed.Length + i] = mixed[i];
            }
        }
    }

    // Filter75Hz (SoundInput.c:511): 3 50 Hz-wide sections at 1450/1500/1550 Hz over
    // the filtered buffer, output delayed 120 samples, used only by the envelope
    // correlator.
    private void Filter75Hz(Span<short> filterOut, int samplesToFilter)
    {
        const int N = 240;
        const float R = 0.9995f;
        float rn = MathF.Pow(R, N);
        float r2 = MathF.Pow(R, 2);

        Span<float> coef = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            coef[i] = 2 * R * MathF.Cos(TwoPi * (29 + i) / N);
        }

        Span<float> zout0 = stackalloc float[3];
        Span<float> zout1 = stackalloc float[3];
        Span<float> zout2 = stackalloc float[3];
        zout0.Clear();
        zout1.Clear();
        zout2.Clear();
        float zin1 = 0, zin2 = 0;
        float filterAccumulator = 0;

        for (int i = 0; i < samplesToFilter; i++)
        {
            float zin = i < N
                ? _filtered[_readPtr + i]
                : _filtered[_readPtr + i] - rn * _filtered[_readPtr + i - N];

            float zcomb = zin - zin2 * r2;
            zin2 = zin1;
            zin1 = zin;

            for (int j = 0; j < 3; j++)
            {
                zout0[j] = zcomb + coef[j] * zout1[j] - r2 * zout2[j];
                zout2[j] = zout1[j];
                zout1[j] = zout0[j];

                if (j == 0 || j == 2)
                {
                    filterAccumulator -= 0.39811f * zout0[j];
                }
                else
                {
                    filterAccumulator += zout0[j];
                }
            }

            filterOut[i] = (short)MathF.Ceiling(filterAccumulator * 0.0041f);
        }
    }

    // EnvelopeCorrelator (SoundInput.c:1917): slide the leader template over 1.5
    // symbols of 75 Hz-filtered audio; the peak correlation locates the symbol start.
    private int EnvelopeCorrelator()
    {
        if (_filteredLength < _readPtr + 720)
        {
            return -1;
        }

        Span<short> filtered75 = stackalloc short[720];
        Filter75Hz(filtered75, 720);

        float corMax = -1000000.0f;
        float corMaxProduct = 0.0f;
        int jAtMax = 0;

        for (int j = 0; j < 360; j++)
        {
            float corSum = 0;
            for (int i = 0; i < 240; i++)
            {
                // Offset 120 accommodates the 75 Hz filter's delay.
                float product = ArdopTxTemplates.Leader50Bd[i] * filtered75[120 + i + j];
                corSum += product;
                if (MathF.Abs(product) > corMaxProduct)
                {
                    corMaxProduct = MathF.Abs(product);
                }
            }

            if (MathF.Abs(corSum) > corMax)
            {
                corMax = MathF.Abs(corSum);
                jAtMax = j;
            }
        }

        return corMax > 40 * corMaxProduct ? jAtMax : -1;
    }

    // Acquire2ToneLeaderSymbolFraming (SoundInput.c:1862): position the read pointer
    // on the symbol boundary via the envelope correlation, then refine ±2 samples by
    // 1500 Hz phase error.
    private void Acquire2ToneLeaderSymbolFraming()
    {
        int localPtr = _readPtr + EnvelopeCorrelator();

        float minAbsPhaseError = 5000;
        int iAtMinError = 0;
        for (int i = -2; i <= 2; i++)
        {
            Goertzel(_filtered, localPtr + i, 120, 30, out float re, out float im);
            float phase = MathF.Atan2(im, re);
            float absError = MathF.Abs(phase - MathF.Ceiling(phase / MathF.PI) * MathF.PI);
            if (absError < minAbsPhaseError)
            {
                minAbsPhaseError = absError;
                iAtMinError = i;
            }
        }

        _readPtr = localPtr + iAtMinError;
    }

    // AcquireFrameSyncRSB (SoundInput.c:1971): the sync symbol is the one whose 1500 Hz
    // phase does NOT flip — >120° step then <60° step marks it.
    private bool AcquireFrameSyncRsb()
    {
        int localPtr = _readPtr;
        int availableSymbols = (_filteredLength - _readPtr) / 240;
        if (availableSymbols < 3)
        {
            return false;
        }

        Goertzel(_filtered, localPtr, 240, 30, out float re, out float im);
        float phase1 = MathF.Atan2(im, re);
        localPtr += 240;
        Goertzel(_filtered, localPtr, 240, 30, out re, out im);
        float phase2 = MathF.Atan2(im, re);
        localPtr += 240;

        for (int i = 0; i <= availableSymbols - 3; i++)
        {
            Goertzel(_filtered, localPtr, 240, 30, out re, out im);
            float phase3 = MathF.Atan2(im, re);

            float diff12 = BoundPhase(phase1 - phase2);
            float diff23 = BoundPhase(phase2 - phase3);

            if (MathF.Abs(diff12) > 0.6667f * MathF.PI && MathF.Abs(diff23) < 0.3333f * MathF.PI)
            {
                // 30 accommodates the initial filter-delay pointer offset.
                _leaderReceivedMs = (localPtr - 30) / 12;
                _readPtr = localPtr + 240;
                return true;
            }

            phase1 = phase2;
            phase2 = phase3;
            localPtr += 240;
        }

        _readPtr = localPtr - 480; // back up 2 symbols for the next attempt
        return false;
    }

    private static float BoundPhase(float phase)
    {
        if (phase > MathF.PI)
        {
            return phase - TwoPi;
        }

        return phase < -MathF.PI ? phase + TwoPi : phase;
    }

    /// <summary>Leader length measured from the last frame sync, in ms
    /// (<c>intLeaderRcvdMs</c>) — the value a ConAck reports back in ARQ.</summary>
    public int LeaderReceivedMs => _leaderReceivedMs;

    // --------------------------------------------------------------- Frame type

    // Acquire4FSKFrameType (SoundInput.c:2360): -2 = wait for samples, -1 = poor
    // decode, else the frame type.
    private int Acquire4FskFrameType()
    {
        if (_filteredLength - _readPtr < 240 * 10.5)
        {
            return -2;
        }

        // DemodFrameType4FSK (SoundInput.c:2052): 10 symbols × 4 tones; tone index 0 is
        // the highest audio frequency (the sideband is reversed by the mix).
        int ptr = _readPtr;
        for (int i = 0; i < 10; i++)
        {
            Goertzel(_filtered, ptr, 240, 1575 / 50.0f, out float re, out float im);
            _toneMags[4 * i] = (int)(re * re + im * im);
            Goertzel(_filtered, ptr, 240, 1525 / 50.0f, out re, out im);
            _toneMags[1 + 4 * i] = (int)(re * re + im * im);
            Goertzel(_filtered, ptr, 240, 1475 / 50.0f, out re, out im);
            _toneMags[2 + 4 * i] = (int)(re * re + im * im);
            Goertzel(_filtered, ptr, 240, 1425 / 50.0f, out re, out im);
            _toneMags[3 + 4 * i] = (int)(re * re + im * im);
            ptr += 240;
        }

        // intRmtLeaderMeasure (SoundInput.c:2388): leader detect → type decode.
        _rmtLeaderMeasureMs = (int)(_nowMs - _lastLeaderDetectMs);

        int newType = MinimalDistanceFrameType();
        _readPtr += 240 * 10;
        return newType;
    }

    // ComputeDecodeDistance (SoundInput.c:2100).
    private float DecodeDistance(int tonePtr, byte frameType, byte id)
    {
        float distance = 0;
        byte mask = 0xC0;

        for (int j = 0; j <= 4; j++)
        {
            int toneSum = 0;
            for (int k = 0; k <= 3; k++)
            {
                toneSum += _toneMags[tonePtr + 4 * j + k];
            }

            if (toneSum == 0)
            {
                toneSum = 1;
            }

            int toneIndex = j < 4
                ? ((frameType ^ id) & mask) >> (6 - 2 * j)
                : ArdopFrameType.TypeParity(frameType);
            distance += 1.0f - (1.0f * _toneMags[tonePtr + 4 * j + toneIndex] / toneSum);
            mask >>= 2;
        }

        return distance / 5;
    }

    // RxoComputeDecodeDistance (RXO.c:27): both parity symbols (tones 0-4 and 9) are
    // used but the session-ID-XORed copy (tones 5-8) is ignored — in RXO mode the
    // session ID is unknown, so those tones carry no usable type information.
    private float RxoDecodeDistance(byte frameType)
    {
        float distance = 0;
        byte mask = 0xC0;

        for (int j = 0; j < 10; j++)
        {
            if (j > 4 && j != 9)
            {
                continue;
            }

            int toneSum = 0;
            for (int k = 0; k <= 3; k++)
            {
                toneSum += _toneMags[4 * j + k];
            }

            if (toneSum == 0)
            {
                toneSum = 1;
            }

            int toneIndex = j < 4
                ? (frameType & mask) >> (6 - 2 * j)
                : ArdopFrameType.TypeParity(frameType);
            distance += 1.0f - (1.0f * _toneMags[4 * j + toneIndex] / toneSum);
            mask >>= 2;
        }

        return distance / 6;
    }

    // RxoDecodeSessionID (RXO.c:64): direct decode of tones 5-8 (type XOR ID); the ID
    // updates only when its decode distance is nearly as good as the frame type's.
    private void RxoDecodeSessionId(byte frameType, float maxDistance)
    {
        byte id = 0;
        float distance = 1.0f;

        for (int j = 20; j < 36; j += 4)
        {
            int toneSum = 0;
            int maxToneMag = 0;
            byte tone = 0;
            for (int k = 0; k <= 3; k++)
            {
                toneSum += _toneMags[j + k];
                if (_toneMags[j + k] > maxToneMag)
                {
                    tone = (byte)k;
                    maxToneMag = _toneMags[j + k];
                }
            }

            id = (byte)((id << 2) + tone);
            distance -= 0.25f * maxToneMag / Math.Max(1, toneSum);
        }

        id ^= frameType;
        if (distance <= maxDistance)
        {
            RxoSessionId = id;
        }
    }

    // RxoMinimalDistanceFrameType (RXO.c:116): search every valid type by the
    // ID-independent distance; accept below 0.3 and opportunistically refresh the
    // decoded session ID.
    private int RxoMinimalDistanceFrameType()
    {
        float minDistance = 5;
        byte atMin = 0;

        foreach (byte candidate in ArdopFrameType.ValidTypesAll)
        {
            float distance = RxoDecodeDistance(candidate);
            if (distance < minDistance)
            {
                minDistance = distance;
                atMin = candidate;
            }
        }

        if (minDistance < 0.3)
        {
            RxoDecodeSessionId(atMin, minDistance + 0.02f);
            return atMin;
        }

        return -1;
    }

    // MinimalDistanceFrameType (SoundInput.c:2137) — the full reference port:
    // unconnected/monitoring (0xFF), pending-connect and connected branches, with the
    // ISS candidate-set restriction. Session identity comes from Scope when the ARQ
    // engine drives it, otherwise from the legacy ConnectedSessionId property.
    private int MinimalDistanceFrameType()
    {
        if (RxoMode)
        {
            return RxoMinimalDistanceFrameType();
        }

        bool pending = Scope?.Pending ?? false;
        bool connected = Scope?.Connected ?? (ConnectedSessionId is not null);

        // Acquire4FSKFrameType (SoundInput.c:2392): the session ID searched against.
        byte sessionId = pending
            ? Scope!.PendingSessionId
            : connected
                ? Scope?.SessionId ?? ConnectedSessionId!.Value
                : (byte)0xFF;

        // The third distance answers ConReq repeats while pending, and DISC replays
        // from the previous session otherwise (SoundInput.c:2165).
        byte d3SessionId = pending ? (byte)0xFF : Scope?.LastSessionId ?? 0xFF;

        ReadOnlySpan<byte> candidates = Scope?.UseIssCandidates == true
            ? ArdopFrameType.ValidTypesIss
            : ArdopFrameType.ValidTypesAll;

        float minDistance1 = 5, minDistance2 = 5, minDistance3 = 5;
        int iAtMin1 = 0, iAtMin2 = 0, iAtMin3 = 0;

        foreach (byte candidate in candidates)
        {
            float d1 = DecodeDistance(0, candidate, 0);
            float d2 = DecodeDistance(20, candidate, sessionId);
            float d3 = DecodeDistance(20, candidate, d3SessionId);

            if (d1 < minDistance1)
            {
                minDistance1 = d1;
                iAtMin1 = candidate;
            }

            if (d2 < minDistance2)
            {
                minDistance2 = d2;
                iAtMin2 = candidate;
            }

            if (d3 < minDistance3)
            {
                minDistance3 = d3;
                iAtMin3 = candidate;
            }
        }

        if (sessionId == 0xFF)
        {
            // DISC from a prior session whose END we missed (protocol rule 1.6).
            if (iAtMin1 == ArdopFrameType.Disc && iAtMin3 == ArdopFrameType.Disc
                && (minDistance1 < 0.3 || minDistance3 < 0.3))
            {
                return iAtMin1;
            }

            if (iAtMin1 == iAtMin2 && (minDistance1 < 0.3 || minDistance2 < 0.3))
            {
                _lastGoodFrameTypeDecodeMs = _nowMs;
                return iAtMin1;
            }

            // Monitoring an ARQ connection whose session ID we don't know.
            if (minDistance1 < 0.3 && minDistance1 < minDistance2 && ArdopFrameType.IsData((byte)iAtMin1))
            {
                return iAtMin1;
            }

            // FEC data whose second type byte outscored the first.
            if (minDistance2 < 0.3 && minDistance2 < minDistance1 && ArdopFrameType.IsData((byte)iAtMin2))
            {
                return iAtMin2;
            }

            return -1;
        }

        if (pending)
        {
            // Expecting a ConAck; both type bytes agreeing under the pending ID
            // (SoundInput.c:2253).
            if (iAtMin1 == iAtMin2)
            {
                if (minDistance1 < 0.3 || minDistance2 < 0.3)
                {
                    _lastGoodFrameTypeDecodeMs = _nowMs;
                    return iAtMin1;
                }

                return -1;
            }

            // A repeated ConReq under 0xFF — the ISS missed our ConAck
            // (SoundInput.c:2275).
            if (iAtMin1 == iAtMin3)
            {
                if (iAtMin1 >= ArdopFrameType.ConReqMin && iAtMin1 <= ArdopFrameType.ConReqMax
                    && (minDistance1 < 0.3 || minDistance3 < 0.3))
                {
                    _lastGoodFrameTypeDecodeMs = _nowMs;
                    return iAtMin1;
                }

                return -1;
            }

            return -1;
        }

        // Connected session (blnARQConnected branch, SoundInput.c:2294).
        if (iAtMin1 != iAtMin2)
        {
            return -1;
        }

        bool critical = (iAtMin1 >= ArdopFrameType.DataAckMin)
            || iAtMin1 == ArdopFrameType.Break
            || iAtMin1 == ArdopFrameType.End
            || iAtMin1 == ArdopFrameType.Disc;
        float limit = critical ? 0.3f : 0.4f;
        if (minDistance1 < limit || minDistance2 < limit)
        {
            _lastGoodFrameTypeDecodeMs = _nowMs;
            return iAtMin1;
        }

        return -1;
    }

    // ------------------------------------------------------------ Data demod

    // Demod1Car4FSK / Demod1Car4FSK600 (SoundInput.c:2431,2741): one byte (4 symbols)
    // at a time until the frame is complete or samples run out.
    private void DemodulateFrameBytes()
    {
        var info = _frame!;
        int start = 0;

        while (_state == RxState.AcquireFrame)
        {
            if (_filteredLength < _sampPerSym * 4.5)
            {
                if (_filteredLength > 0)
                {
                    Array.Copy(_filtered, start, _filtered, 0, _filteredLength);
                }

                return; // wait for more samples
            }

            // For 50/100 Bd, skip redecoding a carrier already good from a previous
            // repeat (Memory ARQ); the sequential-block 600 Bd frame always demods.
            if (info.Baud == 600 || !_carrierOk[0])
            {
                DemodOneByte(start, info.Baud);
            }

            _charIndex++;
            _bytesLeft--;
            start += _sampPerSym * 4;
            _filteredLength -= _sampPerSym * 4;

            if (_bytesLeft == 0)
            {
                _state = RxState.SearchingForLeader;
            }
        }
    }

    // Demod1Car4FSKChar (SoundInput.c:2644) / Demod1Car4FSK600Char (:2796).
    private void DemodOneByte(int start, int baud)
    {
        float searchFreq = 1500 + 1.5f * baud; // highest tone = lowest sent (reversed sideband)
        byte data = 0;

        for (int j = 0; j < 4; j++)
        {
            byte symbol = 0;
            float maxMag = 0;
            for (int k = 0; k < 4; k++)
            {
                Goertzel(_filtered, start, _sampPerSym, (searchFreq - k * baud) / baud, out float re, out float im);
                float mag = re * re + im * im;
                _toneMags[_toneMagsIndex + 4 * j + k] = (int)mag;
                if (mag > maxMag)
                {
                    maxMag = mag;
                    symbol = (byte)k;
                }
            }

            data = (byte)((data << 2) + symbol);
            start += _sampPerSym;
        }

        _toneMagsIndex += 16;
        _frameData[_charIndex] = data;
    }

    // Decode1Car4FSK (SoundInput.c:4097): re-decide symbols from (averaged) magnitudes.
    private static void DecodeFromMagnitudes(Span<byte> decoded, ReadOnlySpan<int> magnitudes)
    {
        int index = 0;
        for (int byteNum = 0; byteNum < magnitudes.Length / 16; byteNum++)
        {
            byte value = 0;
            for (int symNum = 0; symNum < 4; symNum++)
            {
                int maxMag = 0;
                byte symbol = 0;
                for (byte tone = 0; tone < 4; tone++)
                {
                    if (magnitudes[index] > maxMag)
                    {
                        maxMag = magnitudes[index];
                        symbol = tone;
                    }

                    index++;
                }

                value = (byte)((value << 2) + symbol);
            }

            decoded[byteNum] = value;
        }
    }

    // ------------------------------------------------------- PSK/QAM data demod

    // The audio carrier probed for a given carrier slot: single-carrier modes sit on
    // 1500 Hz; multi-carrier modes start at 1400 + (numCar/2)·200 and step down 200 Hz
    // per carrier — the highest audio frequency is the lowest transmitted carrier
    // because the mix reverses the sideband (InitDemodPSK, SoundInput.c:4527).
    private static int CarrierFrequency(int carrier, int carrierCount) =>
        carrierCount == 1 ? 1500 : 1400 + carrierCount / 2 * 200 - 200 * carrier;

    // InitDemodPSK / InitDemodQAM (SoundInput.c:4508,4848): read each carrier's
    // full-scale phase-0 reference symbol for the differential starting phase and the
    // 16QAM amplitude threshold (0.75 × reference magnitude, short-truncated as the
    // reference's short[] stores it).
    private void InitDemodPsk(int carrierCount)
    {
        _phasesLen = 0;
        _pskInitDone = true;

        for (int car = 0; car < carrierCount; car++)
        {
            float bin = CarrierFrequency(car, carrierCount) / 100f;
            GoertzelHanning(_filtered, 0, 120, bin, out float re, out float im);
            _pskPhaseLast[car] = (short)(1000 * MathF.Atan2(im, re));
            _carMagThreshold[car] = (short)((short)MathF.Sqrt(re * re + im * im) * 0.75f);
        }
    }

    // DemodPSK / DemodQAM (SoundInput.c:4577,5034): consume symbols as they arrive,
    // one byte-group per pass across all carriers. PSK demodulates intPSKMode symbols
    // per pass (1 byte for 4PSK, 3 bytes for 8PSK); QAM demodulates 2 symbols (1 byte).
    private void DemodulatePskFrame()
    {
        var info = _frame!;
        bool qam = info.Modulation == ArdopModulation.Qam16;
        int mode = info.Modulation == ArdopModulation.Psk4 ? 4 : 8;
        int symbolsPerPass = qam ? 2 : mode;
        int bytesPerPass = info.Modulation == ArdopModulation.Psk8 ? 3 : 1;
        int minSamples = qam ? 8 * 120 + 10 : (int)(1.5f * mode * 120);
        int initSamples = qam ? 9 * 120 + 10 : (int)(2.5f * mode * 120);
        int start = 0;

        while (_state == RxState.AcquireFrame)
        {
            if (_filteredLength < minSamples || (!_pskInitDone && _filteredLength < initSamples))
            {
                if (_filteredLength > 0 && start > 0)
                {
                    Array.Copy(_filtered, start, _filtered, 0, _filteredLength);
                }

                return; // wait for more samples
            }

            if (!_pskInitDone)
            {
                InitDemodPsk(info.CarrierCount);
                _filteredLength -= 120;
                start += 120; // consume the reference symbol
            }

            int used = 0;
            for (int car = 0; car < info.CarrierCount; car++)
            {
                if (car > 0)
                {
                    _phasesLen -= symbolsPerPass; // each carrier writes the same slots
                }

                used = DemodCarrierPskSymbols(start, car, symbolsPerPass, info.CarrierCount);
            }

            _bytesLeft -= bytesPerPass;
            start += used;
            _filteredLength -= used;

            if (_bytesLeft <= 0)
            {
                _state = RxState.SearchingForLeader;
            }
        }
    }

    // Demod1CarPSKChar / Demod1CarQAMChar (SoundInput.c:4786,5227): Hanning Goertzel
    // per symbol; the stored phase is the negated difference from the previous symbol
    // (negated because the mix reversed the sideband). A carrier already recovered by
    // Memory ARQ skips the work but keeps the bookkeeping.
    private int DemodCarrierPskSymbols(int start, int carrier, int numSymbols, int carrierCount)
    {
        if (_carrierOk[carrier])
        {
            _phasesLen += numSymbols;
            return 120 * numSymbols;
        }

        float bin = CarrierFrequency(carrier, carrierCount) / 100f;
        int origStart = start;
        for (int i = 0; i < numSymbols; i++)
        {
            GoertzelHanning(_filtered, start, 120, bin, out float re, out float im);
            _mags[carrier][_phasesLen] = (short)MathF.Sqrt(re * re + im * im);
            short phase = (short)(1000 * MathF.Atan2(im, re));
            _phases[carrier][_phasesLen] = (short)(-AngleDifference(phase, _pskPhaseLast[carrier]));
            _pskPhaseLast[carrier] = phase;
            _phasesLen++;
            start += 120;
        }

        return start - origStart;
    }

    // ComputeAng1_Ang2 (SoundInput.c:4317): angle subtraction in milliradians,
    // bounded ±3142.
    private static int AngleDifference(int angle1, int angle2)
    {
        int diff = angle1 - angle2;
        if (diff < -3142)
        {
            return diff + 6284;
        }

        return diff > 3142 ? diff - 6284 : diff;
    }

    // Decode1CarPSK (SoundInput.c:4132): hard phase decisions in milliradians —
    // 4PSK boundaries at ±π/4, ±3π/4; 8PSK at odd multiples of π/8.
    private void DecodeCarrierPsk(byte[] decoded, int carrier, int mode)
    {
        short[] phases = _phases[carrier];
        int charIndex = 0;
        int pskStart = 0;
        int len = _phasesLen;

        while (len > 0)
        {
            if (mode == 4)
            {
                byte raw = 0;
                for (int k = 0; k < 4; k++)
                {
                    raw <<= 2;
                    short phase = phases[pskStart];
                    if (phase is < 786 and > -786)
                    {
                        // zero
                    }
                    else if (phase is >= 786 and < 2356)
                    {
                        raw += 1;
                    }
                    else if (phase is >= 2356 or <= -2356)
                    {
                        raw += 2;
                    }
                    else
                    {
                        raw += 3;
                    }

                    pskStart++;
                }

                decoded[charIndex++] = raw;
                len -= 4;
            }
            else
            {
                uint bits24 = 0;
                for (int k = 0; k < 8; k++)
                {
                    bits24 = (bits24 << 3) + Psk8Decision(phases[pskStart]);
                    pskStart++;
                }

                decoded[charIndex++] = (byte)(bits24 >> 16);
                decoded[charIndex++] = (byte)(bits24 >> 8);
                decoded[charIndex++] = (byte)bits24;
                len -= 8;
            }
        }
    }

    private static uint Psk8Decision(short phase) => phase switch
    {
        < 393 and > -393 => 0,
        >= 393 and < 1179 => 1,
        >= 1179 and < 1965 => 2,
        >= 1965 and < 2751 => 3,
        >= 2751 or < -2751 => 4,
        >= -2751 and < -1965 => 5,
        >= -1965 and <= -1179 => 6,
        _ => 7,
    };

    // Decode1CarQAM (SoundInput.c:4436): the 8PSK phase decision plus the absolute
    // amplitude bit against a rolling per-carrier threshold seeded from the reference
    // symbol (alpha 0.9/0.15 inner, 0.9/0.075 outer — ~1 dB decode gain on WGN per the
    // reference comment).
    private void DecodeCarrierQam(byte[] decoded, int carrier)
    {
        short[] phases = _phases[carrier];
        short[] mags = _mags[carrier];
        int threshold = _carMagThreshold[carrier];
        int charIndex = 0;
        int pskStart = 0;
        int len = _phasesLen;

        while (len > 0)
        {
            uint data = 0;
            for (int k = 0; k < 2; k++)
            {
                data = (data << 4) + Psk8Decision(phases[pskStart]);
                if (mags[pskStart] < threshold)
                {
                    data += 8; // inner-circle symbol
                    threshold = (threshold * 900 + mags[pskStart] * 150) / 1000;
                }
                else
                {
                    threshold = (threshold * 900 + mags[pskStart] * 75) / 1000;
                }

                _carMagThreshold[carrier] = (short)threshold;
                pskStart++;
            }

            decoded[charIndex++] = (byte)data;
            len -= 2;
        }
    }

    // CorrectPhaseForTuningOffset (SoundInput.c:4336): remove the average phase
    // rotation (a residual tuning error of 1 Hz rotates ~64 mrad/symbol), using only
    // phases within ±(160°/mode) of a nominal point; a beginning→end ramp when both
    // quarter-sections have enough contributors, otherwise a constant shift.
    private static void CorrectPhaseForTuningOffset(short[] phases, int length, bool psk4)
    {
        int mode = psk4 ? 4 : 8;
        int margin = 2793 / mode;
        int increment = 6284 / mode;

        int accCount = 0, accCountBeginning = 0, accCountEnd = 0;
        int acc = 0, accBeginning = 0, accEnd = 0;
        for (int i = 0; i < length; i++)
        {
            int offset = phases[i] - phases[i] / increment * increment;
            if (offset >= -margin && offset <= margin)
            {
                accCount++;
                acc += offset;
                if (i <= length / 4)
                {
                    accCountBeginning++;
                    accBeginning += offset;
                }
                else if (i >= 3 * length / 4)
                {
                    accCountEnd++;
                    accEnd += offset;
                }
            }
        }

        if (accCountBeginning > length / 8 && accCountEnd > length / 8)
        {
            int avgBeginning = accBeginning / accCountBeginning;
            int avgEnd = accEnd / accCountEnd;
            for (int i = 0; i < length; i++)
            {
                phases[i] = BoundMilliradians(
                    phases[i] - (avgBeginning * (length - i) / length + avgEnd * i / length));
            }
        }
        else if (accCount > length / 2)
        {
            int avg = acc / accCount;
            for (int i = 0; i < length; i++)
            {
                phases[i] = BoundMilliradians(phases[i] - avg);
            }
        }
    }

    private static short BoundMilliradians(int value)
    {
        if (value > 3142)
        {
            return (short)(value - 6284);
        }

        return value < -3142 ? (short)(value + 6284) : (short)value;
    }

    // UpdatePhaseConstellation (SoundInput.c:3921), sans plot: phase-error quality for
    // PSK; phase × two-ring radius-error quality for 16QAM. Index 0 is skipped as the
    // reference slot, as in the original.
    private int PhaseConstellationQuality(short[] phases, short[] mags, int mode, bool qam)
    {
        float phaseStep = TwoPi / (qam ? 8 : mode);
        float phaseErrorSum = 0;

        float avgRadInner = 0, avgRadOuter = 0;
        int countInner = 0, countOuter = 0;
        if (qam)
        {
            float magMax = 0;
            for (int j = 1; j < _phasesLen; j++)
            {
                magMax = MathF.Max(magMax, mags[j]);
            }

            for (int k = 1; k < _phasesLen; k++)
            {
                if (mags[k] < 0.75f * magMax)
                {
                    avgRadInner += mags[k];
                    countInner++;
                }
                else
                {
                    avgRadOuter += mags[k];
                    countOuter++;
                }
            }

            avgRadInner = countInner > 0 ? avgRadInner / countInner : 0;
            avgRadOuter = countOuter > 0 ? avgRadOuter / countOuter : 0;
        }

        float radErrorInner = 0, radErrorOuter = 0;
        for (int i = 1; i < _phasesLen; i++)
        {
            int nearest = (int)MathF.Round(0.001f * phases[i] / phaseStep);
            if (mags[i] > (avgRadInner + avgRadOuter) / 2)
            {
                radErrorOuter += MathF.Abs(avgRadOuter - mags[i]);
            }
            else
            {
                radErrorInner += MathF.Abs(avgRadInner - mags[i]);
            }

            phaseErrorSum += MathF.Abs(0.001f * phases[i] - nearest * phaseStep);
        }

        if (!qam)
        {
            return Math.Max(0, (int)(100 - 200 * (phaseErrorSum / _phasesLen) / phaseStep));
        }

        // (Guard the empty-ring divisions the reference would NaN on — quality only.)
        float radiusPenalty =
            (countInner > 0 && avgRadInner > 0 ? radErrorInner / (countInner * avgRadInner) : 0)
            + (countOuter > 0 && avgRadOuter > 0 ? radErrorOuter / (countOuter * avgRadOuter) : 0);
        return Math.Max(0, (int)((1 - radiusPenalty) * (100 - 200 * (phaseErrorSum / _phasesLen) / phaseStep)));
    }

    // The decode tail of DemodPSK/DemodQAM (SoundInput.c:4686-4781, 5122-5215) plus
    // the data-frame arm of DecodeFrame: tuning-offset correction (carrier 0, QAM
    // only — as the reference ships), constellation quality from the last carrier,
    // per-carrier RS + CRC, Memory-ARQ phase averaging and retry, payload assembly.
    private void DecodeCompletedPskFrame()
    {
        var info = _frame!;
        _frame = null;
        Reset();

        bool qam = info.Modulation == ArdopModulation.Qam16;
        int mode = info.Modulation == ArdopModulation.Psk4 ? 4 : 8;

        if (qam)
        {
            CorrectPhaseForTuningOffset(_phases[0], _phasesLen, psk4: false);
        }

        int quality = PhaseConstellationQuality(
            _phases[info.CarrierCount - 1], _mags[info.CarrierCount - 1], mode, qam);

        int blockLength = info.DataLength + info.RsLength + 3;
        int totalRsErrors = 0;

        for (int car = 0; car < info.CarrierCount; car++)
        {
            if (_carrierOk[car])
            {
                continue;
            }

            DecodeCarrier(info, car, qam, mode);
            if (TryCorrectCarrier(info, car, blockLength, ref totalRsErrors))
            {
                continue;
            }

            // Memory ARQ: fold this repeat's phases (and magnitudes for QAM) into the
            // running weighted-angle average and retry from the averaged vectors.
            if (qam)
            {
                SaveQamSamples(car);
            }
            else
            {
                SavePskSamples(car);
            }

            if (_sumCounts[car] > 1)
            {
                DecodeCarrier(info, car, qam, mode);
                TryCorrectCarrier(info, car, blockLength, ref totalRsErrors);
            }
        }

        var data = new List<byte>();
        bool okAll = true;
        for (int car = 0; car < info.CarrierCount; car++)
        {
            if (_carrierOk[car])
            {
                data.AddRange(_goodPartData[car]!);
            }
            else
            {
                okAll = false;
                // Failed carrier: pass the raw payload field (CorrectRawDataWithRS's
                // returnBad path) so the FEC layer can hand it to the host as ERR.
                data.AddRange(_carrierData[car].AsSpan(1, info.DataLength));
            }
        }

        quality = AdjustDataFrameQuality(okAll, quality, totalRsErrors, info);

        Emit(new ArdopDecodedFrame
        {
            Type = info.Type,
            Ok = okAll,
            Data = [.. data],
            Quality = quality,
        });
    }

    private void DecodeCarrier(ArdopFrameInfo info, int carrier, bool qam, int mode)
    {
        if (qam)
        {
            DecodeCarrierQam(_carrierData[carrier], carrier);
        }
        else
        {
            DecodeCarrierPsk(_carrierData[carrier], carrier, mode);
        }
    }

    private bool TryCorrectCarrier(ArdopFrameInfo info, int carrier, int blockLength, ref int totalRsErrors)
    {
        var block = _carrierData[carrier].AsSpan(0, blockLength);
        if (!ArdopFrameCodec.TryCorrectDataBlock(
            block, info.DataLength, info.RsLength, info.Type, out int netLength, out int rsCorrections))
        {
            return false;
        }

        totalRsErrors += rsCorrections;
        _carrierOk[carrier] = true;
        _goodPartData[carrier] = block.Slice(1, netLength).ToArray();
        MemoryArqUpdated();
        return true;
    }

    // The received-quality floor of DecodeFrame (SoundInput.c:3801): a data frame that
    // decoded with few RS corrections is at least quality 80, whatever the
    // constellation said.
    private static int AdjustDataFrameQuality(bool ok, int quality, int totalRsErrors, ArdopFrameInfo info) =>
        ok && totalRsErrors / info.CarrierCount < info.RsLength / 4 && quality < 80 ? 80 : quality;

    // WeightedAngleAvg (SoundInput.c:4896): vector average of two milliradian angles.
    private static short WeightedAngleAvg(short angle1, short angle2)
    {
        float sumX = MathF.Cos(angle1 / 1000f) + MathF.Cos(angle2 / 1000f);
        float sumY = MathF.Sin(angle1 / 1000f) + MathF.Sin(angle2 / 1000f);
        return (short)(1000 * MathF.Atan2(sumY, sumX));
    }

    // SavePSKSamples (SoundInput.c:4939): accumulate the running phase average for a
    // failed carrier and write it back so the retry decodes from the average.
    private void SavePskSamples(int carrier)
    {
        if (_sumCounts[carrier] == 0)
        {
            Array.Copy(_phases[carrier], _carPhaseAvg[carrier], _phasesLen);
        }
        else
        {
            for (int m = 0; m < _phasesLen; m++)
            {
                _carPhaseAvg[carrier][m] = WeightedAngleAvg(_carPhaseAvg[carrier][m], _phases[carrier][m]);
                _phases[carrier][m] = _carPhaseAvg[carrier][m];
            }
        }

        _sumCounts[carrier]++;
        MemoryArqUpdated();
    }

    // SaveQAMSamples (SoundInput.c:4910): as PSK plus a simple weighted average of the
    // magnitudes (the amplitude bit needs them).
    private void SaveQamSamples(int carrier)
    {
        if (_sumCounts[carrier] == 0)
        {
            Array.Copy(_phases[carrier], _carPhaseAvg[carrier], _phasesLen);
            Array.Copy(_mags[carrier], _carMagAvg[carrier], _phasesLen);
        }
        else
        {
            for (int m = 0; m < _phasesLen; m++)
            {
                _carPhaseAvg[carrier][m] = WeightedAngleAvg(_carPhaseAvg[carrier][m], _phases[carrier][m]);
                _phases[carrier][m] = _carPhaseAvg[carrier][m];
                _carMagAvg[carrier][m] =
                    (short)((_carMagAvg[carrier][m] * _sumCounts[carrier] + _mags[carrier][m]) / (_sumCounts[carrier] + 1));
                _mags[carrier][m] = _carMagAvg[carrier][m];
            }
        }

        _sumCounts[carrier]++;
        MemoryArqUpdated();
    }

    // Update4FSKConstellation's quality computation (SoundInput.c:3823), sans plot.
    private int FskQuality(ReadOnlySpan<int> toneMags, int length)
    {
        float distanceSum = 0;

        for (int i = 0; i < length; i += 4)
        {
            int toneSum = toneMags[i] + toneMags[i + 1] + toneMags[i + 2] + toneMags[i + 3];
            int rad = 0;

            if (toneMags[i] > toneMags[i + 1] && toneMags[i] > toneMags[i + 2] && toneMags[i] > toneMags[i + 3])
            {
                if (toneSum > 0)
                {
                    rad = Math.Max(5, 42 - (int)(80L * (toneMags[i + 1] + toneMags[i + 2] + toneMags[i + 3]) / toneSum));
                }

                distanceSum += 42 - rad;
            }
            else if (toneMags[i + 1] > toneMags[i] && toneMags[i + 1] > toneMags[i + 2] && toneMags[i + 1] > toneMags[i + 3])
            {
                if (toneSum > 0)
                {
                    rad = Math.Max(5, 42 - (int)(80L * (toneMags[i] + toneMags[i + 2] + toneMags[i + 3]) / toneSum));
                }

                distanceSum += 42 - rad;
            }
            else if (toneMags[i + 2] > toneMags[i] && toneMags[i + 2] > toneMags[i + 1] && toneMags[i + 2] > toneMags[i + 3])
            {
                if (toneSum > 0)
                {
                    rad = Math.Max(5, 42 - (int)(80L * (toneMags[i + 1] + toneMags[i] + toneMags[i + 3]) / toneSum));
                }

                distanceSum += 42 - rad;
            }
            else if (toneSum > 0)
            {
                rad = Math.Max(5, 42 - (int)(80L * (toneMags[i + 1] + toneMags[i + 2] + toneMags[i]) / toneSum));
                distanceSum += 42 - rad;
            }
        }

        int quality = (int)(100 - 2.7f * (distanceSum / (length / 4)));
        return Math.Clamp(quality, 0, 100);
    }

    // ------------------------------------------------------------- Frame decode

    // DecodeFrame (SoundInput.c:3349) for the Phase A inventory.
    private void DecodeCompletedFrame()
    {
        var info = _frame!;
        _frame = null;
        Reset();

        int quality = FskQuality(_toneMags, _toneMagsLength);

        switch (info.Type)
        {
            case >= ArdopFrameType.ConAck200 and <= ArdopFrameType.ConAck2000:
            {
                int? timing = ArdopFrameCodec.DecodeConAck(_frameData.AsSpan(0, 3));
                Emit(new ArdopDecodedFrame
                {
                    Type = info.Type,
                    Ok = timing is not null,
                    Data = [],
                    Quality = quality,
                    ConAckLeaderMs = timing,
                });
                return;
            }

            case ArdopFrameType.PingAck:
            {
                var ack = ArdopFrameCodec.DecodePingAck(_frameData.AsSpan(0, 3));
                Emit(new ArdopDecodedFrame
                {
                    Type = info.Type,
                    Ok = ack is not null,
                    Data = [],
                    Quality = quality,
                    PingAckSnDb = ack?.SnDb,
                    PingAckQuality = ack?.Quality,
                });
                return;
            }

            case ArdopFrameType.IdFrame:
            {
                bool ok = ArdopFrameCodec.TryDecodeStationBlock(
                    _frameData.AsSpan(0, 16), secondFieldIsGrid: true, out var station, out _, out string grid);
                Emit(new ArdopDecodedFrame
                {
                    Type = info.Type,
                    Ok = ok,
                    Data = [],
                    Quality = quality,
                    Caller = ok ? station.ToString() : null,
                    GridSquare = ok ? grid : null,
                });
                return;
            }

            case (>= ArdopFrameType.ConReqMin and <= ArdopFrameType.ConReqMax) or ArdopFrameType.Ping:
            {
                bool ok = ArdopFrameCodec.TryDecodeStationBlock(
                    _frameData.AsSpan(0, 16), secondFieldIsGrid: false, out var caller, out var target, out _);
                Emit(new ArdopDecodedFrame
                {
                    Type = info.Type,
                    Ok = ok,
                    Data = [],
                    Quality = quality,
                    Caller = ok ? caller.ToString() : null,
                    Target = ok ? target!.ToString() : null,
                    SnDb = Compute4FskSn(),
                });
                return;
            }

            default:
                DecodeDataFrame(info, quality);
                return;
        }
    }

    private void DecodeDataFrame(ArdopFrameInfo info, int quality)
    {
        // The 600 Bd long frame carries three sequential blocks; everything else in the
        // 4FSK family is a single block.
        (int parts, int partDataLen, int partRsLen) = info.Type is 0x7A or 0x7B
            ? (3, info.DataLength / 3, info.RsLength / 3)
            : (1, info.DataLength, info.RsLength);
        int partRawLen = partDataLen + partRsLen + 3;
        int partTonesLen = partRawLen * 16;
        int totalRsErrors = 0;

        var data = new List<byte>();
        for (int part = 0; part < parts; part++)
        {
            var rawPart = _frameData.AsSpan(part * partRawLen, partRawLen);

            if (!_carrierOk[part]
                && ArdopFrameCodec.TryCorrectDataBlock(rawPart, partDataLen, partRsLen, info.Type, out int netLen, out int corrections))
            {
                _carrierOk[part] = true;
                _goodPartData[part] = rawPart.Slice(1, netLen).ToArray();
                totalRsErrors += corrections;
                MemoryArqUpdated();
            }

            if (!_carrierOk[part])
            {
                // Memory ARQ: fold this repeat's tone magnitudes into the running
                // average and retry the RS+CRC correction from the averaged decision.
                SaveFskSamples(part, _toneMags.AsSpan(part * partTonesLen, partTonesLen));
                if (_sumCounts[part] > 1)
                {
                    DecodeFromMagnitudes(rawPart, _toneMagsAvg[part].AsSpan(0, partTonesLen));
                    if (ArdopFrameCodec.TryCorrectDataBlock(rawPart, partDataLen, partRsLen, info.Type, out netLen, out corrections))
                    {
                        _carrierOk[part] = true;
                        _goodPartData[part] = rawPart.Slice(1, netLen).ToArray();
                        totalRsErrors += corrections;
                        MemoryArqUpdated();
                    }
                }
            }

            if (_carrierOk[part])
            {
                // The payload as verified when the part first corrected. (ardopcf
                // re-copies from the freshly demodulated buffer on repeats, which for
                // the 600 Bd sequential blocks can echo corrupt bytes under an old
                // CarrierOk flag — caching the verified payload avoids that.)
                data.AddRange(_goodPartData[part]!);
            }
            else
            {
                // Failed part: pass the raw payload field (CorrectRawDataWithRS's
                // returnBad path) so the FEC layer can hand it to the host as ERR.
                data.AddRange(_frameData.AsSpan(part * partRawLen + 1, partDataLen));
            }
        }

        bool okAll = true;
        for (int part = 0; part < parts; part++)
        {
            okAll &= _carrierOk[part];
        }

        Emit(new ArdopDecodedFrame
        {
            Type = info.Type,
            Ok = okAll,
            Data = [.. data],
            Quality = AdjustDataFrameQuality(okAll, quality, totalRsErrors, info),
        });
    }

    // Compute4FSKSN (SoundInput.c:2929): per-symbol dominant tone vs the average of
    // the other three, in dB referenced to a 3 kHz noise bandwidth (−17.8 dB converts
    // from the nominal 50 Hz bin). Used for ConReq/Ping S:N reporting (PingAck).
    private int Compute4FskSn()
    {
        int numSymbols = _toneMagsLength / 4;
        if (numSymbols == 0)
        {
            return 0;
        }

        float avgSnDb = 0;
        for (int i = 0; i < numSymbols; i++)
        {
            int nonDominantSum = 10;  // protect divide by zero
            int dominant = 0;
            for (int j = 0; j < 4; j++)
            {
                dominant = Math.Max(dominant, _toneMags[4 * i + j]);
                nonDominantSum += _toneMags[4 * i + j];
            }

            float avgNonDominant = (nonDominantSum - dominant) / 3.0f;
            float ratio = (dominant - avgNonDominant) / avgNonDominant;
            // Floor at −30 dB/symbol: the reference would produce −inf on a
            // pathological all-equal symbol (numeric guard, not behaviour).
            avgSnDb += ratio <= 0.001f ? -30.0f : MathF.Min(50.0f, 10.0f * MathF.Log10(ratio));
        }

        return (int)(avgSnDb / numSymbols - 17.8f);
    }

    // ------------------------------------------------------------- Memory ARQ

    /// <summary>Clears the Memory-ARQ accumulation (<c>ResetMemoryARQ</c>,
    /// SoundInput.c:295). The FEC receiver calls this after passing a good frame so a
    /// following frame of the same type is never mistaken for a repeat.</summary>
    public void ResetMemoryArq()
    {
        Array.Clear(_carrierOk);
        Array.Clear(_sumCounts);
        Array.Clear(_goodPartData);
        foreach (int[] avg in _toneMagsAvg)
        {
            Array.Clear(avg);
        }

        foreach (short[] avg in _carPhaseAvg)
        {
            Array.Clear(avg);
        }

        foreach (short[] avg in _carMagAvg)
        {
            Array.Clear(avg);
        }

        _memarqTimeMs = 0;
    }

    private void MemoryArqUpdated()
    {
        if (_memarqTimeMs == 0)
        {
            _memarqTimeMs = _nowMs;
        }
    }

    private void CheckMemoryArqTime()
    {
        if (_memarqTimeMs != 0 && _nowMs - _memarqTimeMs > MemoryArqTimeoutMs)
        {
            ResetMemoryArq();
            _lastDataFrameType = -1;
            MemoryArqStale?.Invoke();
        }
    }

    /// <summary>Raised when Memory-ARQ state goes stale and is discarded — the FEC
    /// receiver uses this to flush pending failed data to the host.</summary>
    public event Action? MemoryArqStale;

    // SaveFSKSamples (SoundInput.c:4975): normalise each 4-tone group to a fixed sum
    // (1000) before averaging so level differences between repeats don't dominate.
    private void SaveFskSamples(int part, ReadOnlySpan<int> magnitudes)
    {
        const float Scale = 1000.0f;
        int[] avg = _toneMagsAvg[part];

        if (_sumCounts[part] == 0)
        {
            for (int m = 0; m < magnitudes.Length; m += 4)
            {
                float sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    sum += magnitudes[m + i];
                }

                for (int i = 0; i < 4; i++)
                {
                    avg[m + i] = (int)MathF.Round(magnitudes[m + i] / sum * Scale);
                }
            }
        }
        else
        {
            for (int m = 0; m < magnitudes.Length; m += 4)
            {
                float sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    sum += magnitudes[m + i];
                }

                for (int i = 0; i < 4; i++)
                {
                    int scaled = (int)MathF.Round(magnitudes[m + i] / sum * Scale);
                    avg[m + i] = (avg[m + i] * _sumCounts[part] + scaled) / (_sumCounts[part] + 1);
                }
            }
        }

        _sumCounts[part]++;
        MemoryArqUpdated();
    }
}
