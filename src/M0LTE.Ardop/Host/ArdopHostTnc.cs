using System.Text;
using System.Threading.Channels;
using M0LTE.Ardop.Arq;

namespace M0LTE.Ardop.Host;

/// <summary>The ARDOP protocol mode (ardopcf <c>enum _ProtocolMode</c>).</summary>
public enum ArdopHostProtocolMode
{
    /// <summary>Connected ARQ sessions (Winlink/Pat).</summary>
    Arq,

    /// <summary>Connectionless FEC broadcasts (ARIM/gARIM/hamChat).</summary>
    Fec,

    /// <summary>Receive-only monitor: decode every heard frame and report it.</summary>
    Rxo,
}

/// <summary>
/// The ARDOP virtual TNC behind the host interface: one station's protocol state
/// (ARQ engine + demodulator + modulator + FEC send/receive), the ardopcf host
/// command set, and per-protocol-mode frame routing. Byte-compatible with ardopcf's
/// command surface — replies, faults and asynchronous notifications reproduce
/// <c>ProcessCommandFromHost</c> (HostInterface.c:241, git a7c9228, MIT, © 2014-2024
/// Rick Muething, John Wiseman, Peter LaRue) including its quirks (the "not
/// recoginized" fault spelling, NEWSTATE's trailing space, PROTOCOLMODE's
/// accept-anything validation, PING's double reply in RXO mode). Frame routing per
/// mode ports <c>ProcessNewSamples</c> (SoundInput.c:1300-1420) and RXO reporting
/// ports <c>ProcessRXOFrame</c> (RXO.c:160). Socket transport lives in
/// <see cref="ArdopHostServer"/>; this class is transport-free and hermetically
/// testable. See PROVENANCE.md and docs/ardop-design.md §5.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> All protocol state is guarded by one lock; commands, host
/// data, received audio, the poll tick and transmit completions each take it. Events
/// (<see cref="CommandToHost"/>, <see cref="DataToHost"/>) are raised while holding
/// it — handlers must not call back in.</para>
/// <para><b>Transmission.</b> The engine's ordered transmit requests and FEC-mode
/// bursts feed one FIFO worker which renders audio, emits <c>PTT TRUE/FALSE</c>, and
/// plays through <see cref="Transmitter"/> — the host binds that to an audio device (see
/// <c>ArdopHostServer.ForAudio</c>), whose completion is the sample-domain end of playout
/// (where ardopcf's <c>SoundFlush</c> returns, arming the repeat window).</para>
/// <para><b>Deliberate deviations from ardopcf</b> (documented in README § ARDOP):
/// the busy detector is not ported, so <c>BUSY TRUE/FALSE</c> notifications are never
/// sent and BUSYDET/BUSYBLOCK are accepted but inert; CONSOLELOG/LOGLEVEL/DEBUGLOG/
/// CMDTRACE are accepted but inert (the daemon has no leveled log files); CWID is
/// accepted but no CW audio is sent after ID frames yet; TXFRAME (a development
/// command) is not implemented; CAPTURE/PLAYBACK report the daemon's audio device and
/// accept-but-ignore changes (matching ardopcf, where changing them does not reroute
/// audio); the command processor acts on any CR-terminated command rather than
/// requiring 4 buffered bytes; VERSION reports this implementation's name; the
/// FECRCV transient state is not reported (NEWSTATE FECRCV).</para>
/// </remarks>
public sealed class ArdopHostTnc : IAsyncDisposable
{
    private static readonly string[] FecModes =
    [
        // strAllDataModes (ARDOPC.c:289) — the host-selectable FEC frame types.
        "4FSK.200.50S", "4PSK.200.100S",
        "4PSK.200.100", "8PSK.200.100", "16QAM.200.100",
        "4FSK.500.100S", "4FSK.500.100",
        "4PSK.500.100", "8PSK.500.100", "16QAM.500.100",
        "4PSK.1000.100", "8PSK.1000.100", "16QAM.1000.100",
        "4PSK.2000.100", "8PSK.2000.100", "16QAM.2000.100",
        "4FSK.2000.600", "4FSK.2000.600S",
    ];

    // ARQBandwidths (ARQ.c:70). ARQBW accepts indices 0-7; CALLBW also UNDEFINED.
    private static readonly string[] BandwidthNames =
    [
        "200FORCED", "500FORCED", "1000FORCED", "2000FORCED",
        "200MAX", "500MAX", "1000MAX", "2000MAX", "UNDEFINED",
    ];

    private readonly object _sync = new();
    private readonly Func<long> _clock;
    private readonly string _version;
    private readonly Channel<TxItem> _txQueue = System.Threading.Channels.Channel.CreateUnbounded<TxItem>();
    private readonly Task _txWorker;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<byte> _fecBuffer = [];
    private readonly Random _noise = new();

    private ArdopDemodulator _demodulator = null!;
    private ArdopFecReceiver _fecReceiver = null!;
    private readonly ArdopRxScope _scope = new();
    private ArdopModulator _modulator;
    // ardopcf's ProtocolMode variable initializer says FEC (ARDOPC.c:200) but
    // ardopmain() immediately sets ARQ (ARDOPC.c:675) — ARQ is the operative boot
    // mode, confirmed against the live transcript.
    private ArdopHostProtocolMode _mode = ArdopHostProtocolMode.Arq;
    private Task? _fecSendTask;
    private bool _fecAbort;
    private bool _fecSending;
    private byte _lastFecTypeSent;

    // Host-visible settings not owned by ArdopArqConfig (ardopcf defaults cited).
    private int _driveLevel = 100;         // DriveLevel (ARDOPC.c:105)
    private int _trailerMs = 20;           // TrailerLength (ARDOPC.c:99)
    private int _squelch = 5;              // Squelch (ARDOPC.c:109)
    private int _busyDet = 5;              // BusyDet (ARDOPC.c:110) — inert, see remarks
    private bool _busyBlock;               // BusyBlock (ARQ.c:100) — inert
    private bool _monitor = true;          // Monitor (ARQ.c:97)
    private string _fecMode = "4FSK.500.100";  // strFECMode (ARDOPC.c:106)
    private int _fecRepeats;               // FECRepeats (ARDOPC.c:107)
    private bool _fecId;                   // FECId (ARDOPC.c:108)
    private bool _cwId;                    // wantCWID (ARDOPC.c:79) — accepted, no CW yet
    private bool _cwOnOff;                 // CWOnOff (ARDOPC.c:80)
    private bool _commandTrace = true;     // CommandTrace (ARDOPC.c:104) — inert
    private bool _debugLog;                // log files — the daemon writes none
    private int _consoleLog = 3;           // ZF_LOG_INFO — inert
    private int _logLevel = 2;             // ZF_LOG_DEBUG — inert
    private int _inputNoiseStdDev;         // InputNoiseStdDev — implemented (diagnostics)
    private string _captureDevice;
    private string _playbackDevice;
    private bool _initializing;

    private sealed record TxItem(ArdopTxRequest? EngineRequest, short[]? Audio, TaskCompletionSource? Done);

    /// <summary>Creates a virtual TNC.</summary>
    /// <param name="captureDevice">Audio capture device name reported by CAPTURE.</param>
    /// <param name="playbackDevice">Audio playback device name reported by PLAYBACK.</param>
    /// <param name="clock">Monotonic millisecond clock (tests inject; defaults to
    /// <see cref="Environment.TickCount64"/>).</param>
    /// <param name="randomSeed">Pins the engine's timing jitter for tests.</param>
    /// <param name="version">VERSION reply body; defaults to
    /// <c>pdn-soundmodem_&lt;assembly version&gt;</c>.</param>
    public ArdopHostTnc(
        string captureDevice = "default",
        string playbackDevice = "default",
        Func<long>? clock = null,
        int? randomSeed = null,
        string? version = null)
    {
        _captureDevice = captureDevice;
        _playbackDevice = playbackDevice;
        _clock = clock ?? (static () => Environment.TickCount64);
        _version = version ?? $"pdn-soundmodem_{typeof(ArdopHostTnc).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
        _modulator = new ArdopModulator(_driveLevel);

        Config = new ArdopArqConfig();
        Engine = new ArdopArqEngine(Config, randomSeed);
        Engine.HostNotification += Notify;
        Engine.DataReceived += data => SendDataToHost("ARQ", data);
        Engine.TransmitRequested += request =>
            _txQueue.Writer.TryWrite(new TxItem(request, null, null));
        Engine.MemoryArqResetRequested += () => _demodulator.ResetMemoryArq();

        BuildReceiveChain();
        _txWorker = Task.Run(TransmitWorkerAsync);
    }

    /// <summary>The live-settable station configuration (MYCALL, ARQBW, …).</summary>
    public ArdopArqConfig Config { get; }

    /// <summary>The ARQ protocol engine.</summary>
    public ArdopArqEngine Engine { get; }

    /// <summary>The protocol mode (PROTOCOLMODE).</summary>
    public ArdopHostProtocolMode ProtocolMode => _mode;

    /// <summary>Plays one modulated burst and completes when the audio has fully left
    /// the device — the daemon binds this to the channel's transmit path. Must be set
    /// before any command that transmits.</summary>
    public Func<short[], Task>? Transmitter { get; set; }

    /// <summary>Command-socket traffic to the host: replies and asynchronous
    /// notifications, one line per call, no trailing CR.</summary>
    public event Action<string>? CommandToHost;

    /// <summary>Data-socket traffic to the host: 3-character type tag (ARQ, FEC, ERR,
    /// IDF) plus payload (<c>AddTagToDataAndSendToHost</c>, HostInterface.c:1589).</summary>
    public event Action<string, byte[]>? DataToHost;

    /// <summary>Raised on the CLOSE command. The daemon ignores it (it hosts other
    /// services); standalone tests may stop on it.</summary>
    public event Action? CloseRequested;

    // ------------------------------------------------------------------ audio side

    /// <summary>Feeds received channel audio (12 kHz floats, the daemon's RX-tap
    /// format). INPUTNOISE, when set, adds Gaussian noise here as in ardopcf.</summary>
    public void ProcessReceive(ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_inputNoiseStdDev > 0)
            {
                Span<float> noisy = samples.Length <= 4096 ? stackalloc float[samples.Length] : new float[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    noisy[i] = samples[i] + Gaussian() * _inputNoiseStdDev / 32768f;
                }

                _demodulator.ProcessSamples(noisy);
            }
            else
            {
                _demodulator.ProcessSamples(samples);
            }

            Engine.SyncRxScope(_scope);
        }
    }

    /// <summary>Timer poll: drives the engine's repeat/timeout machinery. Call every
    /// ~20 ms (the daemon's host server runs this).</summary>
    public void Poll()
    {
        lock (_sync)
        {
            Engine.Poll(_clock());
            Engine.SyncRxScope(_scope);
        }
    }

    // ------------------------------------------------------------------- host side

    /// <summary>Accepts one CR-terminated command line from the host command socket
    /// (CR already stripped) and emits the reply/fault via
    /// <see cref="CommandToHost"/>.</summary>
    public void ProcessCommand(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0 || line.StartsWith("RDY", StringComparison.OrdinalIgnoreCase))
        {
            return;  // command ACK, swallowed (ARDOPProcessCommand, TCPHostInterface.c:273)
        }

        lock (_sync)
        {
            Dispatch(line);
        }
    }

    /// <summary>Accepts one length-prefixed data block from the host data socket
    /// (already deframed): the outbound ARQ buffer in ARQ mode, the FEC buffer in FEC
    /// mode. BUFFER is notified either way (<c>AddDataToDataToSend</c>).</summary>
    public void AcceptHostData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (_mode == ArdopHostProtocolMode.Fec)
            {
                _fecBuffer.AddRange(data);
                Notify($"BUFFER {_fecBuffer.Count}");
            }
            else
            {
                Engine.EnqueueData(data);
            }
        }
    }

    /// <summary>The host-link failsafe (<c>LostHost</c>, ARDOPCommon.c:424): if a host
    /// socket drops mid-session, request an orderly ARQ disconnect and revert to
    /// receive (spec §8.1.2.1.4).</summary>
    public void HostLinkLost()
    {
        lock (_sync)
        {
            if (Engine.State is ArdopProtocolState.Idle or ArdopProtocolState.Irs
                or ArdopProtocolState.Iss or ArdopProtocolState.IrsToIss)
            {
                Engine.Disconnect(_clock());
            }

            _fecAbort = true;
        }
    }

    // ------------------------------------------------------------ command dispatch

    private void Dispatch(string original)
    {
        // The whole line is uppercased before splitting (_strupr, HostInterface.c:263);
        // the original casing survives only in ARQCALL/PING echoes (cmdCopy).
        string upper = original.ToUpperInvariant();
        int space = upper.IndexOf(' ');
        string cmd = space < 0 ? upper : upper[..space];
        string? p = space < 0 ? null : upper[(space + 1)..];
        string? fault = null;

        void Reply(string text) => Notify(text);

        // DoTrueFalseCmd (HostInterface.c:138).
        bool TrueFalse(string name, ref bool value)
        {
            if (p is null)
            {
                Reply($"{name} {(value ? "TRUE" : "FALSE")}");
                return false;
            }

            if (p == "TRUE")
            {
                value = true;
            }
            else if (p == "FALSE")
            {
                value = false;
            }
            else
            {
                fault = $"Syntax Err: {name} {p}";
                return false;
            }

            Reply($"{name} now {(value ? "TRUE" : "FALSE")}");
            return true;
        }

        // Numeric get/set with an inclusive validity predicate.
        void IntCmd(string name, ref int value, Func<int, bool> valid, Func<int, int>? transform = null)
        {
            if (p is null)
            {
                Reply($"{name} {value}");
                return;
            }

            if (int.TryParse(p, out int i) && valid(i))
            {
                value = transform is null ? i : transform(i);
                Reply($"{name} now {value}");
            }
            else
            {
                fault = $"Syntax Err: {name} {p}";
            }
        }

        // parse_station_and_nattempts (HostInterface.c:185).
        bool ParseStationAndAttempts(out ArdopStationId target, out int attempts)
        {
            target = null!;
            attempts = 0;
            if (p is null)
            {
                fault = $"Syntax Err: {cmd}: expected \"TARGET NATTEMPTS\"";
                return false;
            }

            int split = p.IndexOf(' ');
            if (split < 0)
            {
                fault = $"Syntax Err: {cmd} {p}: expected \"TARGET NATTEMPTS\"";
                return false;
            }

            string targetText = p[..split];
            string attemptsText = p[(split + 1)..];
            if (!ArdopStationId.TryParse(targetText, out target))
            {
                fault = $"Syntax Err: {cmd} {targetText} {attemptsText}: invalid TARGET: maximum length exceeded or unsupported format";
                return false;
            }

            if (!long.TryParse(attemptsText, out long n))
            {
                fault = $"Syntax Err: {cmd} {targetText} {attemptsText}: NATTEMPTS not valid as number";
                return false;
            }

            if (n < 1)
            {
                fault = $"Syntax Err: {cmd} {targetText} {attemptsText}: NATTEMPTS must be positive";
                return false;
            }

            attempts = (int)Math.Min(n, int.MaxValue);
            return true;
        }

        long now = _clock();
        switch (cmd)
        {
            case "ABORT":
            case "DD":
                AbortAll(now);
                Reply("ABORT");
                break;

            case "ARQBW":
                if (p is null)
                {
                    Reply($"ARQBW {BandwidthName(Config.ArqBandwidth)}");
                }
                else if (Array.IndexOf(BandwidthNames, p) is >= 0 and < 8)
                {
                    ArdopBandwidthExtensions.TryParse(p, out var bw);
                    Config.ArqBandwidth = bw;
                    Reply($"ARQBW now {p}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "ARQCALL":
                if (!ParseStationAndAttempts(out var callTarget, out int callAttempts))
                {
                    break;
                }

                if (Config.MyCall is null)
                {
                    fault = "MYCALL not set";
                }
                else if (_mode == ArdopHostProtocolMode.Fec)
                {
                    fault = "Not from mode FEC";
                }
                else if (_mode == ArdopHostProtocolMode.Rxo)
                {
                    fault = "Not from mode RXO";
                }
                else
                {
                    Config.ConReqRepeats = callAttempts;
                    Reply(original);
                    Engine.ConnectRequest(callTarget, now);
                }

                break;

            case "ARQTIMEOUT":
            {
                int timeout = Config.ArqTimeoutSeconds;
                IntCmd(cmd, ref timeout, static i => i is > 29 and < 241);
                Config.ArqTimeoutSeconds = timeout;
                break;
            }

            case "AUTOBREAK":
            {
                bool autoBreak = Config.AutoBreak;
                TrueFalse(cmd, ref autoBreak);
                Config.AutoBreak = autoBreak;
                break;
            }

            case "BREAK":
                Engine.BreakRequested(now);  // no reply (HostInterface.c:376)
                break;

            case "BUFFER":
                if (p is null)
                {
                    Reply($"BUFFER {BufferCount()}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "BUSYBLOCK":
                TrueFalse(cmd, ref _busyBlock);
                break;

            case "BUSYDET":
                IntCmd(cmd, ref _busyDet, static i => i is >= 0 and <= 10);
                break;

            case "CALLBW":
                if (p is null)
                {
                    Reply($"CALLBW {BandwidthName(Config.CallBandwidth)}");
                }
                else if (Array.IndexOf(BandwidthNames, p) >= 0)
                {
                    ArdopBandwidthExtensions.TryParse(p, out var bw);
                    Config.CallBandwidth = bw;
                    Reply($"CALLBW now {p}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "CAPTURE":
                if (p is null)
                {
                    Reply($"CAPTURE {_captureDevice}");
                }
                else
                {
                    // Stored + echoed but audio is not rerouted — as in ardopcf.
                    _captureDevice = p;
                    Reply($"CAPTURE now {_captureDevice}");
                }

                break;

            case "CAPTUREDEVICES":
                Reply($"CAPTUREDEVICES {_captureDevice}");
                break;

            case "CL":
                ClearBuffers();  // async BUFFER 0, no direct reply
                break;

            case "CLOSE":
                CloseRequested?.Invoke();  // no reply
                break;

            case "CMDTRACE":
                TrueFalse(cmd, ref _commandTrace);
                break;

            case "CONSOLELOG":
                IntCmd(cmd, ref _consoleLog, static _ => true, static i => Math.Clamp(i, 1, 6));
                break;

            case "CWID":
                if (p is null)
                {
                    Reply(_cwId ? (_cwOnOff ? "CWID ONOFF" : "CWID TRUE") : "CWID FALSE");
                    break;
                }

                if (p == "TRUE")
                {
                    (_cwId, _cwOnOff) = (true, false);
                }
                else if (p == "FALSE")
                {
                    _cwId = false;
                }
                else if (p == "ONOFF")
                {
                    (_cwId, _cwOnOff) = (true, true);
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                    break;
                }

                Reply(_cwId ? (_cwOnOff ? "CWID now ONOFF" : "CWID now TRUE") : "CWID now FALSE");
                break;

            case "DATATOSEND":
                if (p is null)
                {
                    Reply($"DATATOSEND {BufferCount()}");
                }
                else if (p == "0")
                {
                    // ardopcf zeroes the buffer without the async BUFFER 0 here; our
                    // shared clear path notifies — one extra BUFFER 0 line (documented).
                    ClearBuffers();
                    Reply("DATATOSEND now 0");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "DEBUGLOG":
                TrueFalse(cmd, ref _debugLog);
                break;

            case "DISCONNECT":
                if (Engine.State is ArdopProtocolState.Idle or ArdopProtocolState.Irs
                    or ArdopProtocolState.Iss or ArdopProtocolState.IrsToIss)
                {
                    Engine.Disconnect(now);
                    Reply("DISCONNECT NOW TRUE");
                }
                else
                {
                    Reply("DISCONNECT IGNORED");
                }

                break;

            case "DRIVELEVEL":
                if (p is null)
                {
                    Reply($"DRIVELEVEL {_driveLevel}");
                }
                else if (int.TryParse(p, out int level) && level is >= 0 and <= 100)
                {
                    _driveLevel = Math.Max(1, level);
                    _modulator = new ArdopModulator(_driveLevel);
                    Reply($"DRIVELEVEL now {level}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "ENABLEPINGACK":
            {
                bool enable = Config.EnablePingAck;
                TrueFalse(cmd, ref enable);
                Config.EnablePingAck = enable;
                break;
            }

            case "EXTRADELAY":
            {
                int extra = Config.ExtraDelayMs;
                IntCmd(cmd, ref extra, static i => i >= 0);
                Config.ExtraDelayMs = extra;
                break;
            }

            case "FECID":
                TrueFalse(cmd, ref _fecId);
                break;

            case "FASTSTART":
            {
                bool fast = Config.FastStart;
                TrueFalse(cmd, ref fast);
                Config.FastStart = fast;
                break;
            }

            case "FECMODE":
                if (p is null)
                {
                    Reply($"FECMODE {_fecMode}");
                }
                else if (Array.IndexOf(FecModes, p) >= 0)
                {
                    _fecMode = p;
                    Reply($"FECMODE now {_fecMode}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "FECREPEATS":
                IntCmd(cmd, ref _fecRepeats, static i => i is >= 0 and <= 5);
                break;

            case "FECSEND":
                if (p is null)
                {
                    fault = $"Syntax Err: {cmd}";
                }
                else if (p == "TRUE")
                {
                    if (Config.MyCall is null)
                    {
                        fault = "MYCALL not set";
                    }
                    else if (!StartFecSend())
                    {
                        fault = "StartFEC failed for FECSEND TRUE.";
                    }
                    else
                    {
                        Reply("FECSEND now TRUE");
                    }
                }
                else if (p == "FALSE")
                {
                    _fecAbort = true;
                    Reply("FECSEND now FALSE");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "FSKONLY":
            {
                bool fskOnly = Config.FskOnly;
                TrueFalse(cmd, ref fskOnly);
                Config.FskOnly = fskOnly;
                break;
            }

            case "GRIDSQUARE":
                if (p is null)
                {
                    Reply($"GRIDSQUARE {Config.GridSquare}");
                }
                else if (ValidateGridSquare(p, out string? gridError))
                {
                    // Canonical form has a lowercase subsquare pair
                    // (locator_uncompress, Locator.c).
                    Config.GridSquare = p.Length >= 6
                        ? p[..4] + char.ToLowerInvariant(p[4]) + char.ToLowerInvariant(p[5]) + p[6..]
                        : p;
                    Reply($"GRIDSQUARE now {Config.GridSquare}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}: {gridError}";
                }

                break;

            case "INITIALIZE":
                _initializing = true;
                ClearBuffers();
                _initializing = false;
                Reply("INITIALIZE");
                break;

            case "INPUTNOISE":
                if (p is null)
                {
                    Reply($"INPUTNOISE {_inputNoiseStdDev}");
                }
                else if (int.TryParse(p, out int stdDev))
                {
                    _inputNoiseStdDev = stdDev;
                    Reply($"INPUTNOISE now {(short)stdDev}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "LEADER":
            {
                int leader = Config.LeaderLengthMs;
                IntCmd(cmd, ref leader, static i => i is >= 120 and <= 2500, static i => (i + 9) / 10 * 10);
                Config.LeaderLengthMs = leader;
                Config.ArqDefaultDelayMs = leader;  // intARQDefaultDlyMs (HostInterface.c:868)
                break;
            }

            case "LISTEN":
            {
                bool listen = Config.Listen;
                TrueFalse(cmd, ref listen);
                Config.Listen = listen;
                break;
            }

            case "LOGLEVEL":
                IntCmd(cmd, ref _logLevel, static _ => true, static i => Math.Clamp(i, 1, 6));
                break;

            case "MONITOR":
                TrueFalse(cmd, ref _monitor);
                break;

            case "MYAUX":
                if (p is null)
                {
                    Reply(Config.AuxCalls.Count == 0
                        ? "MYAUX"
                        : $"MYAUX {string.Join(",", Config.AuxCalls)}");
                    break;
                }

                Config.AuxCalls.Clear();
                foreach (string one in p.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (!ArdopStationId.TryParse(one, out var aux))
                    {
                        fault = $"Syntax Err: at callsign {Config.AuxCalls.Count}: maximum length exceeded or unsupported format";
                        Config.AuxCalls.Clear();  // invalid input clears MYAUX entirely
                        break;
                    }

                    Config.AuxCalls.Add(aux);
                }

                if (fault is null)
                {
                    Reply($"MYAUX now {string.Join(",", Config.AuxCalls)}");
                }

                break;

            case "MYCALL":
                if (p is null)
                {
                    Reply($"MYCALL {Config.MyCall?.ToString() ?? ""}");
                }
                else if (ArdopStationId.TryParse(p, out var myCall))
                {
                    Config.MyCall = myCall;
                    Reply($"MYCALL now {myCall}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}: maximum length exceeded or unsupported format";
                }

                break;

            case "PING":
                if (!ParseStationAndAttempts(out var pingTarget, out int pingAttempts))
                {
                    break;
                }

                if (Config.MyCall is null)
                {
                    fault = "MYCALL not set";
                    break;
                }

                if (_mode == ArdopHostProtocolMode.Rxo)
                {
                    // ardopcf's RXO branch omits the goto (HostInterface.c:1013): the
                    // echo still goes out and the FAULT follows — ported byte-for-byte,
                    // except we do not key the transmitter in a receive-only mode.
                    fault = "Not from mode RXO";
                    Reply(original);
                    break;
                }

                if (Engine.State != ArdopProtocolState.Disc)
                {
                    fault = $"No PING from state {StateName()}";
                    break;
                }

                Reply(original);
                Engine.Ping(pingTarget, pingAttempts, now);
                break;

            case "PLAYBACK":
                if (p is null)
                {
                    Reply($"PLAYBACK {_playbackDevice}");
                }
                else
                {
                    _playbackDevice = p;
                    Reply($"PLAYBACK now {_playbackDevice}");
                }

                break;

            case "PLAYBACKDEVICES":
                Reply($"PLAYBACKDEVICES {_playbackDevice}");
                break;

            case "PROTOCOLMODE":
                if (p is null)
                {
                    Reply($"PROTOCOLMODE {_mode switch
                    {
                        ArdopHostProtocolMode.Arq => "ARQ",
                        ArdopHostProtocolMode.Rxo => "RXO",
                        _ => "FEC",
                    }}");
                    break;
                }

                // ardopcf's parameter check (HostInterface.c:1065) can never trip, so
                // any parameter is accepted: ARQ/RXO/FEC switch mode, anything else
                // falls back to ARQ (setProtocolMode, ARDOPC.c:612) — ported as-is.
                SetProtocolMode(p switch
                {
                    "RXO" => ArdopHostProtocolMode.Rxo,
                    "FEC" => ArdopHostProtocolMode.Fec,
                    _ => ArdopHostProtocolMode.Arq,
                });
                Reply($"PROTOCOLMODE now {p}");
                EnsureDiscState(now);
                break;

            case "PURGEBUFFER":
                ClearBuffers();
                Reply("PURGEBUFFER");
                break;

            case "RADIOFREQ":
                if (p is null)
                {
                    fault = "RADIOFREQ command string missing";
                }

                // With a parameter ardopcf only forwards to its GUI — no host reply.
                break;

            case "RADIOHEX":
                if (p is null)
                {
                    fault = "RADIOHEX command string missing";
                }

                // No CAT device — ardopcf silently ignores the block (HostInterface.c:1143).
                break;

            case "RADIOPTTOFF":
            case "RADIOPTTON":
                fault = p is null
                    ? $"{cmd} command string missing"
                    : $"{cmd} command CAT Port not defined";
                break;

            case "SENDID":
                if (Config.MyCall is null)
                {
                    fault = "MYCALL not set";
                }
                else if (Engine.State == ArdopProtocolState.Disc && !_fecSending)
                {
                    Reply("SENDID");
                    Engine.SendId(now);
                }
                else
                {
                    fault = $"Not from State {StateName()}";
                }

                break;

            case "SQUELCH":
                if (p is null)
                {
                    Reply($"SQUELCH {_squelch}");
                }
                else if (int.TryParse(p, out int squelch) && squelch is >= 1 and <= 10)
                {
                    _squelch = squelch;
                    BuildReceiveChain();
                    Reply($"SQUELCH now {_squelch}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "STATE":
                if (p is null)
                {
                    Reply($"STATE {StateName()}");
                }
                else
                {
                    fault = $"Syntax Err: {cmd} {p}";
                }

                break;

            case "TRAILER":
                IntCmd(cmd, ref _trailerMs, static i => i is >= 0 and <= 200, static i => (i + 9) / 10 * 10);
                break;

            case "TWOTONETEST":
                if (Config.MyCall is null)
                {
                    fault = "MYCALL not set";
                }
                else if (Engine.State == ArdopProtocolState.Disc && !_fecSending)
                {
                    Reply("TWOTONETEST");
                    _txQueue.Writer.TryWrite(new TxItem(null, _modulator.TwoToneTest(), null));
                }
                else
                {
                    fault = $"Not from state {StateName()}";
                }

                break;

            case "TUNINGRANGE":
            {
                int range = Config.TuningRangeHz;
                IntCmd(cmd, ref range, static i => i is >= 0 and <= 200);
                if (range != Config.TuningRangeHz)
                {
                    Config.TuningRangeHz = range;
                    BuildReceiveChain();
                }

                break;
            }

            case "USE600MODES":
            {
                bool use600 = Config.Use600Modes;
                TrueFalse(cmd, ref use600);
                Config.Use600Modes = use600;
                break;
            }

            case "VERSION":
                Reply($"VERSION {_version}");
                break;

            default:
                // ardopcf's spelling, kept for byte-compatibility (HostInterface.c:1544).
                fault = $"CMD {cmd} not recoginized";
                break;
        }

        if (fault is not null)
        {
            Reply($"FAULT {fault}");
        }
    }

    // -------------------------------------------------------------- mode plumbing

    private void SetProtocolMode(ArdopHostProtocolMode mode)
    {
        _mode = mode;
        _demodulator.RxoMode = mode == ArdopHostProtocolMode.Rxo;
    }

    // SetARDOPProtocolState(DISC) on any PROTOCOLMODE change (HostInterface.c:1076):
    // an active session is dropped on the floor there; the engine's equivalent is an
    // abort.
    private void EnsureDiscState(long now)
    {
        if (Engine.State != ArdopProtocolState.Disc)
        {
            Engine.Abort(now);
        }

        _fecAbort = true;
    }

    private void AbortAll(long now)
    {
        Engine.Abort(now);
        _fecAbort = true;
    }

    private void ClearBuffers()
    {
        _fecBuffer.Clear();
        Engine.PurgeBuffer();  // notifies BUFFER 0 (ClearDataToSend, ARDOPC.c:1753)
    }

    private int BufferCount() => Engine.OutboundCount + _fecBuffer.Count;

    private string StateName() => _fecSending
        ? "FECSEND"
        : Engine.State switch
        {
            ArdopProtocolState.Disc => "DISC",
            ArdopProtocolState.Iss => "ISS",
            ArdopProtocolState.Irs => "IRS",
            ArdopProtocolState.Idle => "IDLE",
            _ => "IRStoISS",
        };

    private static string BandwidthName(ArdopBandwidth bandwidth) => bandwidth switch
    {
        ArdopBandwidth.B200Forced => "200FORCED",
        ArdopBandwidth.B500Forced => "500FORCED",
        ArdopBandwidth.B1000Forced => "1000FORCED",
        ArdopBandwidth.B2000Forced => "2000FORCED",
        ArdopBandwidth.B200Max => "200MAX",
        ArdopBandwidth.B500Max => "500MAX",
        ArdopBandwidth.B1000Max => "1000MAX",
        ArdopBandwidth.B2000Max => "2000MAX",
        _ => "UNDEFINED",
    };

    // locator_from_str validation (Locator.c) with its error strings; the input is
    // already uppercase (the whole command line is uppercased).
    private static bool ValidateGridSquare(string grid, out string? error)
    {
        error = null;
        if (grid.Length > 8)
        {
            error = "length exceeded";
            return false;
        }

        if (grid.Length is not (2 or 4 or 6 or 8))
        {
            error = "locator must be 2, 4, 6, or 8 characters";
            return false;
        }

        if (grid[0] is < 'A' or > 'R' || grid[1] is < 'A' or > 'R')
        {
            error = "locator has invalid field (first pair)";
            return false;
        }

        if (grid.Length >= 4 && (grid[2] is < '0' or > '9' || grid[3] is < '0' or > '9'))
        {
            error = "locator has invalid square (second pair)";
            return false;
        }

        if (grid.Length >= 6 && (grid[4] is < 'A' or > 'X' || grid[5] is < 'A' or > 'X'))
        {
            error = "locator has invalid subsquare (third pair)";
            return false;
        }

        if (grid.Length == 8 && (grid[6] is < '0' or > '9' || grid[7] is < '0' or > '9'))
        {
            error = "locator has invalid extended square (fourth pair)";
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------- receive routing

    private void BuildReceiveChain()
    {
        _demodulator = new ArdopDemodulator(_squelch, Config.TuningRangeHz)
        {
            Scope = _scope,
            RxoMode = _mode == ArdopHostProtocolMode.Rxo,
        };
        _fecReceiver = new ArdopFecReceiver(_demodulator, attach: false);
        _fecReceiver.DataReceived += (tag, data) => SendDataToHost(
            tag switch
            {
                ArdopFecTag.Fec => "FEC",
                ArdopFecTag.Err => "ERR",
                _ => "IDF",
            },
            data);
        _demodulator.FrameDecoded += OnFrameDecoded;
    }

    // The per-mode frame routing of ProcessNewSamples (SoundInput.c:1300-1420).
    private void OnFrameDecoded(ArdopDecodedFrame frame)
    {
        switch (_mode)
        {
            case ArdopHostProtocolMode.Rxo:
                // ProcessRXOFrame (RXO.c:160): report every frame on the command
                // socket; reset Memory ARQ after a good data decode.
                Notify(frame.Ok
                    ? $"STATUS [RXO {_demodulator.RxoSessionId:X2}] {frame.Name} frame received OK."
                    : $"STATUS [RXO {_demodulator.RxoSessionId:X2}] {frame.Name} frame decode FAIL.");
                if (frame.Ok && ArdopFrameType.IsData(frame.Type))
                {
                    _demodulator.ResetMemoryArq();
                }

                break;

            case ArdopHostProtocolMode.Fec:
                if (ArdopFrameType.IsData(frame.Type) || frame.Type == ArdopFrameType.IdFrame)
                {
                    _fecReceiver.ProcessFrame(frame);
                }
                else if (frame.Ok && frame.Type is >= ArdopFrameType.ConReqMin and <= ArdopFrameType.ConReqMax)
                {
                    MonitorConReq(frame);
                }
                else if (frame.Type is ArdopFrameType.Ping or ArdopFrameType.PingAck or ArdopFrameType.Disc)
                {
                    // Ping answering and late-DISC END replay behave as in ARQ DISC
                    // state (SoundInput.c:1331,1334) — the engine implements both.
                    Engine.FrameReceived(frame, _clock());
                    Engine.SyncRxScope(_scope);
                }

                break;

            default:
                Engine.FrameReceived(frame, _clock());
                Engine.SyncRxScope(_scope);

                // ARQ mode monitors like FEC while DISC (SoundInput.c:1361,1394).
                if (Engine.State == ArdopProtocolState.Disc)
                {
                    if (frame.Type == ArdopFrameType.IdFrame && _monitor)
                    {
                        _fecReceiver.ProcessFrame(frame);
                    }
                    else if (frame.Ok && _monitor
                        && frame.Type is >= ArdopFrameType.ConReqMin and <= ArdopFrameType.ConReqMax)
                    {
                        MonitorConReq(frame);
                    }
                    else if (ArdopFrameType.IsData(frame.Type) && (_monitor || !frame.Ok))
                    {
                        _fecReceiver.ProcessFrame(frame);
                    }
                }

                break;
        }
    }

    // ProcessUnconnectedConReqFrame (ARQ.c:1091): a heard connect request is passed
    // to the host as display text on the data socket, ARQ-tagged.
    private void MonitorConReq(ArdopDecodedFrame frame)
    {
        if (frame.Caller is null || frame.Target is null)
        {
            return;
        }

        SendDataToHost(
            "ARQ",
            Encoding.ASCII.GetBytes($" [{frame.Name}: {frame.Caller} > {frame.Target}]"));
    }

    // ------------------------------------------------------------------- FEC send

    // StartFEC (FEC.c:45): validate, mark FECSend, kick the send loop.
    private bool StartFecSend()
    {
        if (_fecSending)
        {
            return true;  // already sending — new data just extends the queue
        }

        if (_fecBuffer.Count == 0 || Array.IndexOf(FecModes, _fecMode) < 0)
        {
            return false;
        }

        if (Engine.State != ArdopProtocolState.Disc)
        {
            return false;
        }

        _fecAbort = false;
        _fecSending = true;
        Notify("NEWSTATE FECSEND ");
        _fecSendTask = Task.Run(FecSendLoopAsync);
        return true;
    }

    private static byte FecEvenType(string mode) => mode switch
    {
        // FrameCode(strFECMode + ".E") — the even type code per host mode name.
        "4FSK.200.50S" => 0x48,
        "4PSK.200.100S" => 0x42,
        "4PSK.200.100" => 0x40,
        "8PSK.200.100" => 0x44,
        "16QAM.200.100" => 0x46,
        "4FSK.500.100S" => 0x4C,
        "4FSK.500.100" => 0x4A,
        "4PSK.500.100" => 0x50,
        "8PSK.500.100" => 0x52,
        "16QAM.500.100" => 0x54,
        "4PSK.1000.100" => 0x60,
        "8PSK.1000.100" => 0x62,
        "16QAM.1000.100" => 0x64,
        "4PSK.2000.100" => 0x70,
        "8PSK.2000.100" => 0x72,
        "16QAM.2000.100" => 0x74,
        "4FSK.2000.600" => 0x7A,
        _ => 0x7C,  // 4FSK.2000.600S
    };

    // GetNextFECFrame (FEC.c:156): consume the buffer a frame at a time, transmit each
    // new frame 1+FECREPEATS times, toggle even/odd per new frame; FECID sends an ID
    // frame first. 400 ms between repeated transmissions (intFrameRepeatInterval,
    // FEC.c:112).
    private async Task FecSendLoopAsync()
    {
        try
        {
            if (_fecId)
            {
                ArdopStationId? myCall;
                string grid;
                lock (_sync)
                {
                    myCall = Config.MyCall;
                    grid = Config.GridSquare;
                }

                if (myCall is not null)
                {
                    await PlayAsync(ArdopFrameCodec.EncodeIdFrame(myCall, grid)).ConfigureAwait(false);
                }
            }

            byte type = FecEvenType(_fecMode);
            if ((_lastFecTypeSent & 0xFE) == (type & 0xFE))
            {
                type = (byte)(_lastFecTypeSent ^ 1);  // new send starts on the other toggle
            }

            var info = ArdopFrameInfo.Get(type);
            int capacity = info.DataLength * info.CarrierCount;
            int repeats;
            lock (_sync)
            {
                repeats = _fecRepeats;
            }

            while (!_fecAbort)
            {
                byte[] chunk;
                lock (_sync)
                {
                    if (_fecBuffer.Count == 0)
                    {
                        break;
                    }

                    int take = Math.Min(capacity, _fecBuffer.Count);
                    chunk = [.. _fecBuffer[..take]];
                    _fecBuffer.RemoveRange(0, take);
                    Notify($"BUFFER {_fecBuffer.Count}");
                }

                byte[] encoded = ArdopFrameCodec.EncodeDataFrame(type, chunk, 0xFF);
                _lastFecTypeSent = type;
                for (int i = 0; i <= repeats && !_fecAbort; i++)
                {
                    await PlayAsync(encoded).ConfigureAwait(false);
                    if (repeats > 0)
                    {
                        await Task.Delay(400).ConfigureAwait(false);
                    }
                }

                type ^= 1;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_fecAbort)
                {
                    _fecBuffer.Clear();
                    Engine.PurgeBuffer();  // ClearDataToSend on abort (FEC.c:166)
                }

                _fecSending = false;
                Notify("NEWSTATE DISC ");
            }
        }
    }

    private Task PlayAsync(byte[] encodedFrame)
    {
        short[] audio;
        lock (_sync)
        {
            audio = _modulator.Modulate(encodedFrame, Config.LeaderLengthMs, _trailerMs);
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_txQueue.Writer.TryWrite(new TxItem(null, audio, done)))
        {
            done.TrySetResult();  // shutting down — don't strand the FEC loop
        }

        return done.Task;
    }

    // ---------------------------------------------------------------- transmit side

    private async Task TransmitWorkerAsync()
    {
        var reader = _txQueue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out TxItem? item))
                {
                    await TransmitOneAsync(item).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Never strand a waiter at shutdown — a FEC send loop awaiting playout
            // of a queued burst would otherwise deadlock DisposeAsync.
            while (reader.TryRead(out TxItem? leftover))
            {
                leftover.Done?.TrySetResult();
            }
        }
    }

    private async Task TransmitOneAsync(TxItem item)
    {
        try
        {
            short[] audio;
            if (item.EngineRequest is { } request)
            {
                // Honour the engine's RX→TX turnaround gate (ArdopTxRequest.NotBeforeMs);
                // ardopcf sits in a wait loop before keying (Mod4FSKDataAndPlay).
                long wait = request.NotBeforeMs - _clock();
                if (wait > 0)
                {
                    await Task.Delay((int)Math.Min(wait, 30_000), _stopping.Token).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    audio = _modulator.Modulate(request.EncodedFrame, request.LeaderLengthMs, _trailerMs);
                }
            }
            else
            {
                audio = item.Audio!;
            }

            Func<short[], Task> transmitter = Transmitter
                ?? throw new InvalidOperationException("ArdopHostTnc.Transmitter is not set");

            // PTT notifications bracket the burst (KeyPTT, ALSASound.c:1903).
            Notify("PTT TRUE");
            try
            {
                await transmitter(audio).ConfigureAwait(false);
            }
            finally
            {
                Notify("PTT FALSE");
            }
        }
        finally
        {
            if (item.EngineRequest is not null)
            {
                lock (_sync)
                {
                    Engine.TransmitCompleted(_clock());
                    Engine.SyncRxScope(_scope);
                }
            }

            item.Done?.TrySetResult();
        }
    }

    // ------------------------------------------------------------------- plumbing

    private void Notify(string line) => CommandToHost?.Invoke(line);

    // blnInitializing suppresses data-to-host only (TCPAddTagToDataAndSendToHost,
    // TCPHostInterface.c:240); command-socket traffic flows during INITIALIZE.
    private void SendDataToHost(string tag, byte[] data)
    {
        if (!_initializing)
        {
            DataToHost?.Invoke(tag, data);
        }
    }

    private float Gaussian()
    {
        // Box-Muller; only used for INPUTNOISE diagnostics.
        double u1 = 1.0 - _noise.NextDouble();
        double u2 = _noise.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _txQueue.Writer.TryComplete();
        _fecAbort = true;
        try
        {
            await _txWorker.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (_fecSendTask is { } fec)
        {
            try
            {
                await fec.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _stopping.Dispose();
    }
}
