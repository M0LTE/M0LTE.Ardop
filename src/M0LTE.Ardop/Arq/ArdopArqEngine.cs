namespace M0LTE.Ardop.Arq;

/// <summary>Session counters for tests and session logs (subset of ardopcf's tuning
/// stats, ARQ.c:155-196).</summary>
public sealed class ArdopArqStats
{
    /// <summary>ISS↔IRS role swaps (<c>intLinkTurnovers</c>).</summary>
    public int LinkTurnovers { get; internal set; }

    /// <summary>Frames re-transmitted by the repeat timer.</summary>
    public int RepeatsSent { get; internal set; }

    /// <summary>DataNAK frames sent (as IRS).</summary>
    public int NaksSent { get; internal set; }

    /// <summary>DataNAK frames received (as ISS).</summary>
    public int NaksReceived { get; internal set; }

    /// <summary>DataACK frames received for data frames (as ISS).</summary>
    public int DataAcksReceived { get; internal set; }
}

/// <summary>
/// The ARDOP ARQ session engine: connect handshake with bandwidth negotiation, the
/// data/ACK/NAK cycle with quality-driven mode shifts, IDLE/BREAK role turnover,
/// timeout/repeat logic and DISC/END teardown. A single-threaded event-driven port of
/// ardopcf's protocol machine (git a7c9228, MIT, © 2014-2024 Rick Muething, John
/// Wiseman, Peter LaRue): <c>ProcessRcvdARQFrame</c> ARQ.c:1113, <c>GetNextARQFrame</c>
/// :347, <c>SendData</c>/<c>GetNextFrameData</c> :825/:967, <c>SendARQConnectRequest</c>
/// :2432, <c>IRSNegotiateBW</c> :2318, <c>CheckForDisconnect</c> :2495,
/// <c>CheckTimers</c>/<c>MainPoll</c> ARDOPC.c:1799/:1979, the IRStoISS shortcuts of
/// <c>ProcessNewSamples</c> SoundInput.c:1053-1292, and <c>SendPING</c>/
/// <c>ProcessPingFrame</c> ARDOPC.c:2233/:2264. Matches ARDOP spec App. D; where the
/// spec and ardopcf disagree, ardopcf is ported (docs/ardop-design.md §1.2).
/// See PROVENANCE.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock.</b> The engine never reads wall time: every input carries the caller's
/// monotonic millisecond clock (<c>nowMs</c>). Offline tests drive it from
/// sample counts (12 samples/ms, the discipline Phase A set for the demodulator) so a
/// whole session runs faster than real time; the live path is the same engine driven
/// by the audio clock (<see cref="ArdopArqStation"/>).
/// </para>
/// <para>
/// <b>Transmission model.</b> ardopcf's <c>Mod4FSKDataAndPlay</c> blocks until the
/// frame has played; here the engine raises <see cref="TransmitRequested"/> and the
/// driver calls <see cref="TransmitCompleted"/> at end of playout, which arms the
/// repeat timer exactly where ardopcf's <c>SoundFlush</c> does
/// (<c>dttNextPlay = Now + intFrameRepeatInterval + extraDelay</c>, ALSASound.c:1847).
/// Requests are strictly ordered; whether a frame arms the repeat timer is captured at
/// request time, which matches the blocking reference where no state changes between
/// play start and flush. While a transmission is in flight <see cref="Poll"/> is inert,
/// as ardopcf's main loop is while blocked in playout.
/// </para>
/// <para>
/// <b>Deliberate deviations</b> (none affect the wire format): ardopcf's spectral busy
/// detector is not ported (this package has no audio device), so the busy state comes
/// from the host through <see cref="ArdopArqConfig.ChannelBusy"/>; everything ardopcf
/// drives from that state is ported on top of it, plus one addition, BUSYBLOCK also
/// blocking an outgoing call (<see cref="ArdopArqConfig.BusyBlock"/>). ardopcf's
/// END-after-DISC is encoded <i>after</i> <c>InitializeConnection()</c> resets the
/// session ID in two of its three handlers, so those ENDs go out with session ID 0xFF
/// and the peer's teardown completes on the DISC-from-DISC replay instead — that quirk
/// is ported faithfully (interop truth), not fixed.
/// </para>
/// </remarks>
public sealed class ArdopArqEngine
{
    private readonly ArdopArqConfig _config;
    private readonly Random _random;

    // Protocol state (ARQ.c state variables).
    private ArdopProtocolState _state = ArdopProtocolState.Disc;
    private ArdopArqSubState _subState = ArdopArqSubState.None;
    private bool _connected;                 // blnARQConnected
    private bool _pending;                   // blnPending
    private byte _sessionId = 0xFF;          // bytSessionID
    private byte _pendingSessionId;          // bytPendingSessionID
    private byte _lastSessionId;             // bytLastARQSessionID (0 until a session ends)
    private ArdopStationId? _remote;         // ARQStationRemote
    private ArdopStationId? _local;          // ARQStationLocal
    private ArdopStationId? _finalId;        // ARQStationFinalId
    private int _sessionBw;                  // intSessionBW

    // Repeat/timer state.
    private bool _repeatEnabled;             // blnEnbARQRpt
    private bool _discRepeating;             // blnDISCRepeating
    private int _repeatCount;                // intRepeatCount
    private int _repeatIntervalMs = 2000;    // intFrameRepeatInterval
    private long _nextPlayMs;                // dttNextPlay
    private long _timeoutTripMs;             // dttTimeoutTrip
    private bool _timeoutTriggered;          // blnTimeoutTriggered
    private long _sendTimeoutAtMs;           // tmrSendTimeout (0 = off)
    private long _pendingTimeoutAtMs;        // tmrIRSPendingTimeout (0 = off)
    private long _finalIdAtMs;               // tmrFinalID (0 = off)
    private long _lastIdFrameTimeMs;         // LastIDFrameTime (0 = none since last ID)
    private bool _abort;                     // blnAbort
    private bool _disconnectRequested;       // blnARQDisconnect
    private bool _breakCommanded;            // blnBREAKCmd
    private int _calcLeaderMs;               // intCalcLeader

    // Data exchange state.
    private readonly List<byte> _outbound = [];   // bytDataToSend
    private byte _lastDataFrameSent;         // bytLastARQDataFrameSent
    private byte _lastDataFrameAcked;        // bytLastARQDataFrameAcked
    private byte _lastAckedDataFrameType;    // bytLastACKedDataFrameType (IRS side)
    private int _lastDataFrameToHost = -1;   // intLastARQDataFrameToHost
    private bool _lastFrameSentData;         // blnLastFrameSentData
    private int _dataInProcessLength;        // bytQDataInProcessLen
    private int _remoteLeaderMeasureMs;      // intRmtLeaderMeas
    private readonly ArdopGearshift _gearshift = new();

    // PING state.
    private int _pingRepeats;                // intPINGRepeats
    private bool _pingRepeating;             // blnPINGrepeating

    // Channel-busy state (BusyDetect.c), fed by ArdopArqConfig.ChannelBusy rather than
    // by a spectrum. Sampled in DISC only; see SampleBusyDetector.
    private bool _busy;                      // blnLastBusy (BusyDetect.c:18)
    private bool _busyReported;              // blnLastBusyStatus (ARDOPC.c:2045)
    private bool _busySeeded;                // no equivalent: ardopcf's globals start zeroed
    private int _busyOnCount;                // intBusyOnCnt (BusyDetect.c:26)
    private int _busyOffCount;               // intBusyOffCnt (BusyDetect.c:27)
    private long _busySampledAtMs;           // LastBusyCheck (ARDOPC.c:2010)
    private long _busyLastRawMs;             // dttLastTrip (BusyDetect.c:31)
    private long _busyLastTripMs;            // dttLastBusyTrip (BusyDetect.c:28)
    private long _busyPriorTripMs;           // dttPriorLastBusyTrip (BusyDetect.c:29)
    private long _busyLastClearMs;           // dttLastBusyClear (BusyDetect.c:30)

    private const int BusyHoldMs = 5000;          // intHoldMs (BusyDetect.c:33)
    private const int BusySampleIntervalMs = 100;  // ARDOPC.c:2062
    private const int BusyClearMarginMs = 600;     // ARQ.c:1201-1202
    private const int BusyClearSeedMs = 610;       // ClearBusy's seed (BusyDetect.c:40)

    // A ConReq's body on the air, excluding its leader: the reference fixture
    // txframe_ConReq500M.wav is 20 940 samples at 12 kHz (1745 ms) of which ardopcf's
    // default 240 ms leader is the front. Used only to date a ConReq's transmission
    // start; see IsConReqBlockedByBusy.
    private const int BusyConReqBodyMs = 1505;

    // Transmit bookkeeping: metadata per in-flight request, dequeued at completion.
    private readonly Queue<(bool ArmRepeat, int RepeatIntervalMs)> _txInFlight = new();
    private byte[]? _lastTx;
    private long _nowMs;

    /// <summary>Creates an engine. <paramref name="randomSeed"/> pins the BREAK-repeat
    /// jitter for deterministic tests; null uses a random seed.</summary>
    public ArdopArqEngine(ArdopArqConfig config, int? randomSeed = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _random = randomSeed is null ? new Random() : new Random(randomSeed.Value);
        _calcLeaderMs = config.LeaderLengthMs;
    }

    /// <summary>The protocol state.</summary>
    public ArdopProtocolState State => _state;

    /// <summary>The ARQ sub-state.</summary>
    public ArdopArqSubState SubState => _subState;

    /// <summary>True once the session is established (<c>blnARQConnected</c>).</summary>
    public bool IsConnected => _connected;

    /// <summary>True between a ConReq/ConAck exchange starting and the session
    /// connecting or failing (<c>blnPending</c>).</summary>
    public bool IsPending => _pending;

    /// <summary>The connected session ID (0xFF when unconnected).</summary>
    public byte SessionId => _sessionId;

    /// <summary>The pending session ID (valid while <see cref="IsPending"/>).</summary>
    public byte PendingSessionId => _pendingSessionId;

    /// <summary>The previous session's ID, answering late DISC replays
    /// (<c>bytLastARQSessionID</c>).</summary>
    public byte LastSessionId => _lastSessionId;

    /// <summary>The negotiated session bandwidth in Hz (0 before negotiation).</summary>
    public int SessionBandwidthHz => _sessionBw;

    /// <summary>The remote station of the current/last session.</summary>
    public ArdopStationId? RemoteStation => _remote;

    /// <summary>Bytes queued for transmission (BUFFER).</summary>
    public int OutboundCount => _outbound.Count;

    /// <summary>Session counters.</summary>
    public ArdopArqStats Stats { get; } = new();

    /// <summary>The gearshift (exposed for tests and session logging).</summary>
    public ArdopGearshift Gearshift => _gearshift;

    /// <summary>
    /// The latched channel-busy state (<c>blnLastBusy</c>, BusyDetect.c:18): what
    /// <see cref="ArdopArqConfig.ChannelBusy"/> reported, after ardopcf's confirm-and-hold
    /// hysteresis. False forever when that seam is unset, which is the default.
    /// </summary>
    /// <remarks>
    /// Only updated while the protocol state is DISC, as in ardopcf (ARDOPC.c:2054,
    /// :2102), and only ever read by things that decide whether to <i>start</i> a session
    /// or send an unsolicited ID. Nothing inside a session consults it: an IRS must be
    /// free to ACK inside the ISS's repeat window, so a busy channel must never hold up
    /// an in-flight transmission.
    /// </remarks>
    public bool IsChannelBusy => _busy;

    /// <summary>Raised when a frame must be modulated and played. Ordered; the driver
    /// must call <see cref="TransmitCompleted"/> once per request, in order.</summary>
    public event Action<ArdopTxRequest>? TransmitRequested;

    /// <summary>In-order, exactly-once ARQ payload delivery to the host.</summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>Asynchronous host notifications in ardopcf's command-socket format
    /// (NEWSTATE/CONNECTED/DISCONNECTED/BUFFER/STATUS/REJECTED*/PENDING/…).</summary>
    public event Action<string>? HostNotification;

    /// <summary>Raised when Memory-ARQ accumulation must be cleared
    /// (<c>ResetMemoryARQ()</c> call sites in ARQ.c) — wire to
    /// <see cref="ArdopDemodulator.ResetMemoryArq"/>.</summary>
    public event Action? MemoryArqResetRequested;

    // ------------------------------------------------------------------ inputs

    /// <summary>
    /// Timer poll — <c>CheckTimers</c> (ARDOPC.c:1799) + <c>MainPoll</c> (:1979).
    /// Call at least every few tens of milliseconds of clock time. Inert while a
    /// transmission is in flight (ardopcf's loop is blocked in playout then).
    /// </summary>
    public void Poll(long nowMs)
    {
        _nowMs = nowMs;
        if (_txInFlight.Count > 0)
        {
            return;
        }

        SampleBusyDetector(nowMs);

        // Repeat timer (CheckTimers, ARDOPC.c:1803).
        if ((_repeatEnabled || _discRepeating) && nowMs > _nextPlayMs)
        {
            if (GetNextArqFrame())
            {
                Stats.RepeatsSent++;
                Transmit(_lastTx!, _calcLeaderMs, 0, isRepeat: true);
            }
            else
            {
                _repeatEnabled = false;
            }
        }

        // ARQ session timeout, one second after the trigger (tmrSendTimeout,
        // ARDOPC.c:1825; protocol rules 1.7/1.8/4.0).
        if (_sendTimeoutAtMs != 0 && nowMs > _sendTimeoutAtMs)
        {
            _sendTimeoutAtMs = 0;
            SendIdFrame(_finalId);
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ Timeout from Protocol State:  {StateName(_state)}");
            _repeatEnabled = false;
            ClearDataToSend();
            Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Disc, _sessionId), _config.LeaderLengthMs, 0);
            _repeatIntervalMs = 2000;
            SetState(ArdopProtocolState.Disc);
            InitializeConnection();
            _timeoutTriggered = false;
        }

        // IRS pending timeout (tmrIRSPendingTimeout, ARDOPC.c:1879). ardopcf assigns
        // ProtocolState directly here, bypassing SetARDOPProtocolState — ported as-is.
        if (_pendingTimeoutAtMs != 0 && nowMs > _pendingTimeoutAtMs)
        {
            _pendingTimeoutAtMs = 0;
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECT REQUEST TIMEOUT FROM PROTOCOL STATE: {StateName(_state)}");
            _repeatEnabled = false;
            _state = ArdopProtocolState.Disc;
            _pending = false;
            InitializeConnection();
        }

        // Post-session ID (tmrFinalID, ARDOPC.c:1906), deferred while the channel is
        // busy exactly as ardopcf defers it (the !blnBusyStatus term there). Housekeeping
        // in DISC, after teardown: nothing in a session waits on this.
        if (_finalIdAtMs != 0 && nowMs > _finalIdAtMs && !_busy)
        {
            SendIdFrame(_finalId);
        }

        // 10-minute ID while otherwise idle in DISC (ARDOPC.c:1959), likewise held off
        // while other traffic is on the frequency (ARDOPC.c:1962).
        if (_state == ArdopProtocolState.Disc && !_pingRepeating && !_busy
            && _lastIdFrameTimeMs != 0 && nowMs - _lastIdFrameTimeMs > 540000)
        {
            SendIdFrame(null);
        }

        // MainPoll (ARDOPC.c:1979): run GetNextARQFrame for its side effects (abort
        // processing, session-timeout trigger) when nothing is repeating.
        if (_txInFlight.Count == 0 && !_repeatEnabled && !_discRepeating)
        {
            GetNextArqFrame();
        }
    }

    /// <summary>The driver reports that the oldest outstanding transmission has
    /// finished playing — arms the repeat timer as ardopcf's <c>SoundFlush</c> does
    /// (ALSASound.c:1847).</summary>
    public void TransmitCompleted(long nowMs)
    {
        _nowMs = nowMs;
        if (_txInFlight.Count == 0)
        {
            throw new InvalidOperationException("TransmitCompleted without a transmission in flight");
        }

        (bool armRepeat, int interval) = _txInFlight.Dequeue();
        if (armRepeat)
        {
            _nextPlayMs = nowMs + interval + _config.ExtraDelayMs;
        }
    }

    /// <summary>
    /// A decoded (or decode-failed) frame arrived — <c>ProcessRcvdARQFrame</c>
    /// (ARQ.c:1113) plus the pre-decode IRStoISS/BREAK shortcuts of
    /// <c>ProcessNewSamples</c> (SoundInput.c:1053-1292). Replies are scheduled no
    /// earlier than 250 ms + EXTRADELAY after <paramref name="nowMs"/> (the link
    /// turnaround, ARQ.c:1127).
    /// </summary>
    public void FrameReceived(ArdopDecodedFrame frame, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _nowMs = nowMs;
        long notBefore = nowMs + 250 + _config.ExtraDelayMs;

        // Early PENDING notice for connect/ping openings (Acquire4FSKFrameType,
        // SoundInput.c:2401).
        if ((frame.Type is >= ArdopFrameType.ConReqMin and <= ArdopFrameType.ConReqMax)
            || frame.Type == ArdopFrameType.Ping)
        {
            Notify("PENDING");
        }

        // --- ProcessNewSamples shortcuts -----------------------------------------

        // IRStoISS + ACK: cease BREAKs, become ISS (SoundInput.c:1070; fixed 250 ms
        // turnaround there).
        if (_state == ArdopProtocolState.IrsToIss && frame.Type >= ArdopFrameType.DataAckMin)
        {
            _repeatEnabled = false;
            _lastDataFrameToHost = -1;
            if (!_gearshift.IsInitialized)
            {
                InitializeLadder();
            }

            SetState(ArdopProtocolState.Iss);
            Stats.LinkTurnovers++;
            _subState = ArdopArqSubState.IssData;
            SendData(nowMs + 250);
            return;
        }

        if (ArdopFrameType.IsData(frame.Type))
        {
            // Fast BREAK on host BREAK command, before caring about the decode
            // (rule 3.4 shortcut, SoundInput.c:1232).
            if (_breakCommanded && _state == ArdopProtocolState.Irs
                && _subState == ArdopArqSubState.IrsData
                && frame.Type != _lastAckedDataFrameType)
            {
                _repeatIntervalMs = ComputeInterFrameInterval(1000 + _random.Next(2000));
                SetState(ArdopProtocolState.IrsToIss);
                Notify("STATUS QUEUE BREAK new Protocol State IRStoISS");
                _repeatEnabled = true;
                _breakCommanded = false;
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Break, _sessionId), _config.ArqDefaultDelayMs, notBefore);
                return;
            }

            // IRStoISS answers any data frame with BREAK (SoundInput.c:1257).
            if (_state == ArdopProtocolState.IrsToIss)
            {
                _repeatIntervalMs = ComputeInterFrameInterval(1000 + _random.Next(2000));
                _repeatEnabled = true;
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Break, _sessionId), _config.ArqDefaultDelayMs, notBefore);
                return;
            }
        }

        if (_timeoutTriggered)
        {
            return;  // session timeout pending — stop processing (SoundInput.c:1357)
        }

        ProcessRcvdArqFrame(frame, notBefore);
    }

    // ------------------------------------------------------------ host commands

    /// <summary>Initiates an ARQ connection (ARQCALL; <c>SendARQConnectRequest</c>,
    /// ARQ.c:2432). Returns false when preconditions fail (no MYCALL, not in
    /// DISC).</summary>
    public bool ConnectRequest(ArdopStationId target, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(target);
        _nowMs = nowMs;
        if (_config.MyCall is null || _state != ArdopProtocolState.Disc)
        {
            return false;
        }

        // Addition, not a port: ardopcf transmits the ConReq whatever the busy state
        // (NeedConReq, ARDOPC.c:1914, into SendARQConnectRequest, ARQ.c:2432, which has
        // no busy test), leaving it to the host that was sent BUSY TRUE. Under BUSYBLOCK
        // we also refuse it here. Off unless BUSYBLOCK is on AND ArdopArqConfig.ChannelBusy
        // is wired, so the default path is untouched.
        if (_config.BusyBlock && _busy)
        {
            Notify($"STATUS ARQ CONNECT REQUEST TO {target} BLOCKED, CHANNEL BUSY.");
            return false;
        }

        InitializeConnection();
        _remoteLeaderMeasureMs = 0;
        _remote = target;
        _local = _config.MyCall;
        _finalId = _config.MyCall;

        var bandwidth = _config.CallBandwidth == ArdopBandwidth.Undefined
            ? _config.ArqBandwidth
            : _config.CallBandwidth;
        byte conReqType = bandwidth.ConReqFrameType();
        byte[] encoded = ArdopFrameCodec.EncodeConReq(conReqType, _config.MyCall, target);

        _abort = false;
        _timeoutTripMs = nowMs;
        SetState(ArdopProtocolState.Iss);
        _subState = ArdopArqSubState.IssConReq;
        _repeatCount = 1;
        _sessionId = ArdopCrc.SessionId(_config.MyCall.ToString(), target.ToString());
        _pendingSessionId = _sessionId;
        _pending = true;
        _connected = false;
        _repeatIntervalMs = 2000;
        _repeatEnabled = true;
        Transmit(encoded, _config.LeaderLengthMs, 0);
        return true;
    }

    /// <summary>Requests an orderly disconnect (DISCONNECT command): DISC is sent with
    /// up to 5 repeats awaiting END (rules 1.6-1.7). Consumed by
    /// <see cref="Poll"/>/<c>CheckForDisconnect</c>.</summary>
    public void Disconnect(long nowMs)
    {
        _nowMs = nowMs;
        _disconnectRequested = true;
    }

    /// <summary>Dirty disconnect (ABORT): drop everything and return to DISC
    /// (<c>Abort</c>, ARQ.c:2544).</summary>
    public void Abort(long nowMs)
    {
        _nowMs = nowMs;
        _abort = true;
        if (_state is ArdopProtocolState.Idle or ArdopProtocolState.Irs or ArdopProtocolState.IrsToIss)
        {
            GetNextArqFrame();
        }
    }

    /// <summary>Host BREAK command: flags the IRS to take the link at the next
    /// opportunity (<c>Break</c>, ARQ.c:2528; rule 3.4).</summary>
    public void BreakRequested(long nowMs)
    {
        _nowMs = nowMs;
        if (_state != ArdopProtocolState.Irs)
        {
            return;
        }

        _breakCommanded = true;
    }

    /// <summary>Queues outbound data (the host data socket). BUFFER is notified.</summary>
    public void EnqueueData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        foreach (byte value in data)
        {
            _outbound.Add(value);
        }

        Notify($"BUFFER {_outbound.Count}");
    }

    /// <summary>Discards queued outbound data (PURGEBUFFER).</summary>
    public void PurgeBuffer() => ClearDataToSend();

    /// <summary>Sends a PING (up to <paramref name="repeats"/> ≤ 15 transmissions,
    /// stopping on PingAck; <c>SendPING</c>, ARDOPC.c:2233). DISC state only.</summary>
    public bool Ping(ArdopStationId target, int repeats, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(target);
        _nowMs = nowMs;
        if (_config.MyCall is null || _state != ArdopProtocolState.Disc || repeats is < 1 or > 15)
        {
            return false;
        }

        byte[] encoded = ArdopFrameCodec.EncodePing(_config.MyCall, target);
        _repeatIntervalMs = 2000;
        _repeatEnabled = true;
        Transmit(encoded, _config.LeaderLengthMs, 0);
        _abort = false;
        _timeoutTripMs = nowMs;
        _repeatCount = 1;
        _pingRepeats = repeats;
        _pingRepeating = true;
        return true;
    }

    /// <summary>Sends an ID frame now if possible (SENDID).</summary>
    public void SendId(long nowMs)
    {
        _nowMs = nowMs;
        SendIdFrame(null);
    }

    // -------------------------------------------------------------- RX scope

    /// <summary>Copies the engine's session identity into the demodulator's frame-type
    /// acceptance scope. Call after every engine input (the frame-type decoder's
    /// candidate rules depend on it — MinimalDistanceFrameType, SoundInput.c:2137).</summary>
    public void SyncRxScope(ArdopRxScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.UseIssCandidates = _state == ArdopProtocolState.Iss;
        scope.Pending = _pending;
        scope.PendingSessionId = _pendingSessionId;
        scope.Connected = _connected;
        scope.SessionId = _sessionId;
        scope.LastSessionId = _lastSessionId;
    }

    // ========================================================== the state machine

    // ProcessRcvdARQFrame (ARQ.c:1113).
    private void ProcessRcvdArqFrame(ArdopDecodedFrame frame, long notBefore)
    {
        byte type = frame.Type;

        switch (_state)
        {
            case ArdopProtocolState.Disc:
                ProcessInDisc(frame, notBefore);
                return;

            case ArdopProtocolState.Irs:
                ProcessInIrs(frame, notBefore);
                return;

            case ArdopProtocolState.IrsToIss:
                // Handled entirely by the FrameReceived shortcuts (ARQ.c:1752 does
                // nothing here either).
                return;

            case ArdopProtocolState.Idle:
                ProcessInIdle(frame, notBefore);
                return;

            case ArdopProtocolState.Iss:
                ProcessInIss(frame, notBefore);
                return;

            default:
                return;
        }
    }

    // DISC state (ARQ.c:1134).
    private void ProcessInDisc(ArdopDecodedFrame frame, long notBefore)
    {
        byte type = frame.Type;

        // DISC replay from the previous session whose END we (they) missed —
        // rule 1.6's second sentence (ARQ.c:1138).
        if (frame.Ok && type == ArdopFrameType.Disc)
        {
            _finalIdAtMs = _nowMs + 3000;
            _repeatEnabled = false;
            Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.End, _lastSessionId), _config.LeaderLengthMs, notBefore);
            return;
        }

        if (frame.Ok && type == ArdopFrameType.Ping)
        {
            ProcessPingFrame(frame, notBefore);
            return;
        }

        // PingAck for a PING we are repeating (Decode4FSKPingACK, SoundInput.c:3118).
        if (frame.Ok && type == ArdopFrameType.PingAck && _pingRepeating)
        {
            Notify($"PINGACK {frame.PingAckSnDb} {frame.PingAckQuality}");
            _pingRepeating = false;
            _pingRepeats = 0;
            _repeatEnabled = false;
            return;
        }

        if (!frame.Ok || type is < ArdopFrameType.ConReqMin or > ArdopFrameType.ConReqMax)
        {
            return;
        }

        if (!_config.Listen)
        {
            return;
        }

        if (IsCallToMe(frame, out var caller, out var target, out byte replySessionId))
        {
            // BUSYBLOCK refusal (ARQ.c:1199-1226): answer ConRejBusy instead of ConAck
            // when the channel was already in use by someone other than this caller. This
            // is the only place the busy state can stop a frame being answered, and it is
            // reached only from the DISC handler, so no established session is affected.
            if (_config.BusyBlock && IsConReqBlockedByBusy(frame))
            {
                // Reset the detector so this frame and the 5 s hold do not read as a
                // continuous busy condition (ClearBusy, ARQ.c:1207).
                ClearBusyDetector(_nowMs);
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.ConRejBusy, replySessionId), _config.LeaderLengthMs, notBefore);
                Notify($"REJECTEDBUSY {caller}");
                Notify($"STATUS ARQ CONNECTION REQUEST FROM {caller} REJECTED, CHANNEL BUSY.");
                return;  // ardopcf returns here too, leaving blnEnbARQRpt alone
            }

            int reply = IrsNegotiateBw(type);
            if (reply != ArdopFrameType.ConRejBw)
            {
                Notify($"TARGET {target}");
                InitializeConnection();
                _outbound.Clear();
                _pending = true;
                _pendingSessionId = replySessionId;
                _repeatEnabled = false;
                _pendingTimeoutAtMs = _nowMs + 10000;
                _timeoutTripMs = _nowMs;
                SetState(ArdopProtocolState.Irs);
                _subState = ArdopArqSubState.IrsConAck;
                _lastDataFrameToHost = -1;
                MemoryArqResetRequested?.Invoke();
                _remote = caller;
                _local = target;
                _finalId = target;
                // The IRS's first ConAck uses the short default-delay leader so the
                // ISS can measure its round trip (ARQ.c:1267).
                Transmit(ArdopFrameCodec.EncodeConAck((byte)reply, frame.LeaderReceivedMs, replySessionId), _config.ArqDefaultDelayMs, notBefore);
            }
            else
            {
                Notify($"REJECTEDBW {caller}");
                Notify($"STATUS ARQ CONNECTION REJECTED BY {caller}");
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.ConRejBw, replySessionId), _config.LeaderLengthMs, notBefore);
            }
        }
        else
        {
            Notify("CANCELPENDING");
        }

        _repeatEnabled = false;
    }

    // IRS state (ARQ.c:1299).
    private void ProcessInIrs(ArdopDecodedFrame frame, long notBefore)
    {
        byte type = frame.Type;

        if (_subState == ArdopArqSubState.IrsConAck)
        {
            if (!frame.Ok)
            {
                return;
            }

            // Repeated ConReq — the ISS missed our first ConAck (ARQ.c:1311).
            if (type is >= ArdopFrameType.ConReqMin and <= ArdopFrameType.ConReqMax)
            {
                if (!_config.Listen)
                {
                    return;
                }

                if (IsCallToMe(frame, out var caller, out var target, out byte replySessionId))
                {
                    int reply = IrsNegotiateBw(type);
                    if (reply != ArdopFrameType.ConRejBw)
                    {
                        SetState(ArdopProtocolState.Irs);
                        _subState = ArdopArqSubState.IrsConAck;
                        _lastDataFrameToHost = -1;
                        MemoryArqResetRequested?.Invoke();
                        InitializeConnection();
                        _pendingSessionId = replySessionId;
                        _outbound.Clear();
                        _timeoutTripMs = _nowMs;
                        _pendingTimeoutAtMs = _nowMs + 10000;  // restart per ConReq
                        _remote = caller;
                        _local = target;
                        _finalId = target;
                        Transmit(ArdopFrameCodec.EncodeConAck((byte)reply, frame.LeaderReceivedMs, replySessionId), _config.LeaderLengthMs, notBefore);
                        return;
                    }

                    Notify($"REJECTEDBW {caller}");
                    Notify($"STATUS ARQ CONNECTION FROM {caller} REJECTED, INCOMPATIBLE BANDWIDTHS.");
                    Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.ConRejBw, replySessionId), _config.LeaderLengthMs, notBefore);
                }

                return;
            }

            // The ISS's confirming ConAck — session up (ARQ.c:1387; rule 1.4).
            if (type is >= ArdopFrameType.ConAck200 and <= ArdopFrameType.ConAck2000)
            {
                _sessionBw = ConAckBandwidth(type);
                _sessionId = _pendingSessionId;
                _connected = true;
                _pending = false;
                _pendingTimeoutAtMs = 0;
                _timeoutTripMs = _nowMs;
                _subState = ArdopArqSubState.IrsData;
                _lastDataFrameToHost = -1;
                _repeatEnabled = false;
                Notify($"CONNECTED {_remote} {_sessionBw}");
                Notify($"STATUS ARQ CONNECTION FROM {_remote}: SESSION BW = {_sessionBw} HZ");
                Transmit(ArdopFrameCodec.EncodeDataAck(frame.Quality, _sessionId), _config.LeaderLengthMs, notBefore);
                InitializeLadder();
                return;
            }

            return;
        }

        if (_subState is not (ArdopArqSubState.IrsData or ArdopArqSubState.IrsFromIss))
        {
            return;
        }

        // Repeated ConAck — the ISS missed our session-confirming ACK (ARQ.c:1457).
        if (type is >= ArdopFrameType.ConAck200 and <= ArdopFrameType.ConAck2000)
        {
            _sessionBw = ConAckBandwidth(type);
            _timeoutTripMs = _nowMs;
            Transmit(ArdopFrameCodec.EncodeDataAck(frame.Quality, _sessionId), _config.LeaderLengthMs, notBefore);
            return;
        }

        if (frame.Ok && type == ArdopFrameType.Disc)
        {
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECTION ENDED WITH {_remote}");
            _lastSessionId = _sessionId;
            ClearDataToSend();
            _finalIdAtMs = _nowMs + 3000;
            _discRepeating = false;
            SetState(ArdopProtocolState.Disc);
            InitializeConnection();
            _repeatEnabled = false;
            // ardopcf encodes this END after InitializeConnection() has reset the
            // session ID, so it goes out with 0xFF — ported faithfully (see class
            // remarks); the peer's teardown completes on the DISC-from-DISC replay.
            Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.End, _sessionId), _config.LeaderLengthMs, notBefore);
            return;
        }

        if (frame.Ok && type == ArdopFrameType.End)
        {
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECTION ENDED WITH {_remote}");
            _discRepeating = false;
            ClearDataToSend();
            SetState(ArdopProtocolState.Disc);
            SendIdFrame(_local, notBefore);
            InitializeConnection();
            _repeatEnabled = false;
            return;
        }

        // BREAK from a remote IRS that missed our ACK (ARQ.c:1558).
        if (frame.Ok && type == ArdopFrameType.Break)
        {
            _repeatEnabled = false;
            Transmit(ArdopFrameCodec.EncodeDataAck(100, _sessionId), _config.LeaderLengthMs, notBefore);
            _timeoutTripMs = _nowMs;
            return;
        }

        if (frame.Ok && type == ArdopFrameType.Idle)
        {
            _repeatEnabled = false;
            if (CheckForDisconnect())
            {
                return;
            }

            bool idNeeded = _lastIdFrameTimeMs != 0 && _nowMs - _lastIdFrameTimeMs > 540000;
            if ((_config.AutoBreak && _outbound.Count > 0) || _breakCommanded || idNeeded)
            {
                // Take the link (rule 3.3; BREAK is the only frame the IRS repeats).
                _repeatIntervalMs = ComputeInterFrameInterval(1000 + _random.Next(2000));
                SetState(ArdopProtocolState.IrsToIss);
                Notify("STATUS QUEUE BREAK new Protocol State IRStoISS");
                _repeatEnabled = true;
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Break, _sessionId), _config.LeaderLengthMs, notBefore);
            }
            else
            {
                _timeoutTripMs = _nowMs;
                Transmit(ArdopFrameCodec.EncodeDataAck(100, _sessionId), _config.LeaderLengthMs, notBefore);
            }

            return;
        }

        // Data frames (ARQ.c:1632).
        if (frame.Ok && ArdopFrameType.IsData(type))
        {
            if (_remoteLeaderMeasureMs == 0)
            {
                _remoteLeaderMeasureMs = frame.RemoteLeaderMeasureMs;
            }

            // Host BREAK before this frame type was ever ACKed (rule 3.4, ARQ.c:1640).
            if (_subState == ArdopArqSubState.IrsData && _breakCommanded && type != _lastAckedDataFrameType)
            {
                _timeoutTripMs = _nowMs;
                _breakCommanded = false;
                _repeatEnabled = true;
                _repeatIntervalMs = ComputeInterFrameInterval(1000 + _random.Next(2000));
                SetState(ArdopProtocolState.IrsToIss);
                Notify("STATUS QUEUE BREAK new Protocol State IRStoISS");
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Break, _sessionId), _config.LeaderLengthMs, notBefore);
                return;
            }

            if (type != _lastDataFrameToHost)
            {
                // In-order, exactly-once delivery: the even/odd toggle only advances
                // on new data (rule 2.3).
                DataReceived?.Invoke(frame.Data);
                _lastDataFrameToHost = type;
                _timeoutTripMs = _nowMs;
            }

            if (_subState == ArdopArqSubState.IrsFromIss)
            {
                _subState = ArdopArqSubState.IrsData;  // changeover complete (rule 3.5)
            }

            _repeatEnabled = false;
            Transmit(ArdopFrameCodec.EncodeDataAck(frame.Quality, _sessionId), _config.LeaderLengthMs, notBefore);
            _lastAckedDataFrameType = type;
            return;
        }

        // Failed data frame already ACKed once — re-ACK, data already delivered
        // (rev 0.4.3.1, ARQ.c:1696).
        if (!frame.Ok && type == _lastAckedDataFrameType)
        {
            _repeatEnabled = false;
            Transmit(ArdopFrameCodec.EncodeDataAck(frame.Quality, _sessionId), _config.LeaderLengthMs, notBefore);
            return;
        }

        // Failed data frame — NAK with quality (rule 2.2, ARQ.c:1709).
        if (!frame.Ok && ArdopFrameType.IsData(type))
        {
            if (_subState == ArdopArqSubState.IrsFromIss)
            {
                _subState = ArdopArqSubState.IrsData;
            }

            _repeatEnabled = false;
            Stats.NaksSent++;
            Transmit(ArdopFrameCodec.EncodeDataNak(frame.Quality, _sessionId), _config.LeaderLengthMs, notBefore);
        }
    }

    // IDLE state (ARQ.c:1763).
    private void ProcessInIdle(ArdopDecodedFrame frame, long notBefore)
    {
        byte type = frame.Type;
        if (!frame.Ok)
        {
            return;
        }

        if (type >= ArdopFrameType.DataAckMin)
        {
            if (_outbound.Count > 0)
            {
                SetState(ArdopProtocolState.Iss);
                _subState = ArdopArqSubState.IssData;
                SendData(notBefore);
                return;
            }

            bool idNeeded = _lastIdFrameTimeMs != 0 && _nowMs - _lastIdFrameTimeMs > 540000;
            if (idNeeded)
            {
                _repeatEnabled = false;
                SendIdFrame(_local, notBefore);

                // Resume repeating IDLE after the ID (ARQ.c:1793).
                _repeatEnabled = true;
                _lastFrameSentData = false;
                _repeatIntervalMs = ComputeInterFrameInterval(2000);
                Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Idle, _sessionId), _config.LeaderLengthMs, notBefore);
            }

            return;  // ACK with no data — keep idling
        }

        if (type == ArdopFrameType.Break)
        {
            _timeoutTripMs = _nowMs;
            _repeatEnabled = false;
            Transmit(ArdopFrameCodec.EncodeDataAck(100, _sessionId), _config.LeaderLengthMs, notBefore);
            Notify("STATUS BREAK received from Protocol State IDLE, new state IRS");
            SetState(ArdopProtocolState.Irs);
            _subState = ArdopArqSubState.IrsFromIss;
            Stats.LinkTurnovers++;
            _lastDataFrameToHost = -1;
            MemoryArqResetRequested?.Invoke();
            return;
        }

        if (type == ArdopFrameType.Disc)
        {
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECTION ENDED WITH {_remote}");
            _finalIdAtMs = _nowMs + 3000;
            _discRepeating = false;
            // In this handler ardopcf encodes END before the session ID reset, so it
            // carries the real session ID (ARQ.c:1865).
            Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.End, _sessionId), _config.LeaderLengthMs, notBefore);
            _lastSessionId = _sessionId;
            ClearDataToSend();
            SetState(ArdopProtocolState.Disc);
            _repeatEnabled = false;
            return;
        }

        if (type == ArdopFrameType.End)
        {
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECTION ENDED WITH {_remote}");
            ClearDataToSend();
            SendIdFrame(_local, notBefore);
            SetState(ArdopProtocolState.Disc);
            _repeatEnabled = false;
            _discRepeating = false;
        }
    }

    // ISS state (ARQ.c:1914).
    private void ProcessInIss(ArdopDecodedFrame frame, long notBefore)
    {
        byte type = frame.Type;

        if (_subState == ArdopArqSubState.IssConReq)
        {
            if (!frame.Ok)
            {
                return;
            }

            // ConAck from the IRS — bandwidth granted (rule 1.4, ARQ.c:1925).
            if (type is >= ArdopFrameType.ConAck200 and <= ArdopFrameType.ConAck2000)
            {
                _sessionBw = ConAckBandwidth(type);
                InitializeLadder();
                _repeatIntervalMs = 2000;
                _repeatEnabled = true;  // repeat the confirming ConAck until answered
                _subState = ArdopArqSubState.IssConAck;
                Transmit(ArdopFrameCodec.EncodeConAck(type, frame.LeaderReceivedMs, _sessionId), _config.LeaderLengthMs, notBefore);
                return;
            }

            if (type == ArdopFrameType.ConRejBusy)
            {
                Notify($"REJECTEDBUSY {_remote}");
                Notify($"STATUS ARQ CONNECTION REJECTED BY {_remote}, REMOTE STATION BUSY.");
                Abort(_nowMs);
                return;
            }

            if (type == ArdopFrameType.ConRejBw)
            {
                Notify($"REJECTEDBW {_remote}");
                Notify($"STATUS ARQ CONNECTION REJECTED BY {_remote}, INCOMPATIBLE BW.");
                Abort(_nowMs);
                return;
            }

            return;
        }

        if (_subState == ArdopArqSubState.IssConAck)
        {
            // ACK (or BREAK, if the IRS has data and we missed its ACK) confirms the
            // session (ARQ.c:2004).
            if ((frame.Ok && type >= ArdopFrameType.DataAckMin) || type == ArdopFrameType.Break)
            {
                if (_remoteLeaderMeasureMs == 0)
                {
                    _remoteLeaderMeasureMs = frame.RemoteLeaderMeasureMs;
                }

                _repeatEnabled = false;
                _connected = true;
                _lastDataFrameAcked = 1;  // start transmission on an even frame
                _pending = false;
                Notify($"CONNECTED {_remote} {_sessionBw}");
                Notify($"STATUS ARQ CONNECTION ESTABLISHED WITH {_remote}, SESSION BW = {_sessionBw} HZ");
                _subState = ArdopArqSubState.IssData;

                if (type == ArdopFrameType.Break && _outbound.Count == 0)
                {
                    // Implied ACK + BREAK with nothing to send: concede the link.
                    ClearDataToSend();
                    _repeatEnabled = false;
                    Transmit(ArdopFrameCodec.EncodeDataAck(100, _sessionId), _config.LeaderLengthMs, notBefore);
                    _timeoutTripMs = _nowMs;
                    SetState(ArdopProtocolState.Irs);
                    _subState = ArdopArqSubState.IrsFromIss;
                    Stats.LinkTurnovers++;
                    _lastDataFrameToHost = -1;
                    MemoryArqResetRequested?.Invoke();
                }
                else
                {
                    SendData(notBefore);
                }

                return;
            }

            if (frame.Ok && type == ArdopFrameType.ConRejBusy)
            {
                Notify($"REJECTEDBUSY {_remote}");
                Notify($"STATUS ARQ CONNECTION REJECTED BY {_remote}");
                SetState(ArdopProtocolState.Disc);
                InitializeConnection();
                return;
            }

            if (frame.Ok && type == ArdopFrameType.ConRejBw)
            {
                Notify($"REJECTEDBW {_remote}");
                Notify($"STATUS ARQ CONNECTION REJECTED BY {_remote}");
                SetState(ArdopProtocolState.Disc);
                InitializeConnection();
            }

            return;
        }

        if (_subState != ArdopArqSubState.IssData)
        {
            return;
        }

        if (CheckForDisconnect())
        {
            return;
        }

        if (!frame.Ok)
        {
            return;  // no decode — keep repeating data or idle
        }

        if (type >= ArdopFrameType.DataAckMin)
        {
            _timeoutTripMs = _nowMs;
            if (_lastFrameSentData)
            {
                Stats.DataAcksReceived++;
                _lastDataFrameAcked = _lastDataFrameSent;
                if (_dataInProcessLength > 0)
                {
                    RemoveDataFromQueue(_dataInProcessLength);
                    _dataInProcessLength = 0;
                }

                _gearshift.RecordAck(ArdopFrameCodec.AckNakQuality(type), _outbound.Count);
            }

            _repeatEnabled = false;
            SendData(notBefore);
            return;
        }

        if (type == ArdopFrameType.Break)
        {
            if (!_connected)
            {
                // We missed the IRS's session-confirming ACK; clean up (ARQ.c:2151).
                _connected = true;
                _pending = false;
                Notify($"CONNECTED {_remote} {_sessionBw}");
                Notify($"STATUS ARQ CONNECTION ESTABLISHED WITH {_remote}, SESSION BW = {_sessionBw} HZ");
            }

            // The deposed ISS purges its unsent data (rule 3.6; ardopcf's
            // SaveQueueOnBreak is a stub, so RESTOREBUFFER has nothing to restore).
            ClearDataToSend();
            _repeatEnabled = false;
            Notify("STATUS BREAK received from Protocol State ISS, new state IRS");
            Transmit(ArdopFrameCodec.EncodeDataAck(100, _sessionId), _config.LeaderLengthMs, notBefore);
            _timeoutTripMs = _nowMs;
            SetState(ArdopProtocolState.Irs);
            _subState = ArdopArqSubState.IrsFromIss;
            Stats.LinkTurnovers++;
            MemoryArqResetRequested?.Invoke();
            _lastDataFrameToHost = -1;
            return;
        }

        if (type <= ArdopFrameType.DataNakMax)
        {
            if (_lastFrameSentData)
            {
                Stats.NaksReceived++;
                bool shifted = _gearshift.RecordNak(ArdopFrameCodec.AckNakQuality(type), _outbound.Count);
                if (shifted)
                {
                    // Retrigger the timeout and resend in the shifted mode now; without
                    // a shift the repeat timer re-sends the same frame (ARQ.c:2212).
                    _timeoutTripMs = _nowMs;
                    SendData(notBefore);
                }
            }

            return;
        }

        if (type == ArdopFrameType.Disc)
        {
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECTION ENDED WITH {_remote}");
            _lastSessionId = _sessionId;
            _discRepeating = false;
            _finalIdAtMs = _nowMs + 3000;
            ClearDataToSend();
            SetState(ArdopProtocolState.Disc);
            InitializeConnection();
            _repeatEnabled = false;
            // Encoded after the session-ID reset, as in ardopcf (see class remarks).
            Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.End, _sessionId), _config.LeaderLengthMs, notBefore);
            return;
        }

        if (type == ArdopFrameType.End)
        {
            Notify("DISCONNECTED");
            Notify($"STATUS ARQ CONNECTION ENDED WITH {_remote}");
            ClearDataToSend();
            _discRepeating = false;
            SendIdFrame(_local, notBefore);
            SetState(ArdopProtocolState.Disc);
            InitializeConnection();
        }
    }

    // ------------------------------------------------------------- send side

    // SendData (ARQ.c:825): next data frame or IDLE.
    private void SendData(long notBefore)
    {
        if (_discRepeating)
        {
            return;
        }

        if (_state != ArdopProtocolState.Iss)
        {
            return;
        }

        if (CheckForDisconnect())
        {
            return;
        }

        // 10-minute ID, attempted from 9 minutes (ARQ.c:856).
        if (_lastIdFrameTimeMs != 0 && _nowMs - _lastIdFrameTimeMs > 540000)
        {
            _repeatEnabled = false;
            SendIdFrame(_local, notBefore);
        }

        if (_outbound.Count > 0)
        {
            byte typeToSend = _gearshift.NextTypeToSend(_lastDataFrameAcked);
            _lastDataFrameSent = typeToSend;
            var info = ArdopFrameInfo.Get(typeToSend);
            int length = Math.Min(ArdopDataLadder.FrameCapacity(typeToSend), _outbound.Count);
            _dataInProcessLength = length;
            _lastFrameSentData = true;

            // Repeat interval lengthens with carrier count to give the remote decoder
            // time (ARQ.c:876).
            _repeatIntervalMs = info.CarrierCount switch
            {
                1 => ComputeInterFrameInterval(1500),
                2 => ComputeInterFrameInterval(1700),
                4 => ComputeInterFrameInterval(1900),
                8 => ComputeInterFrameInterval(2100),
                _ => 2000,
            };

            _timeoutTripMs = _nowMs;
            _repeatEnabled = true;
            _subState = ArdopArqSubState.IssData;

            byte[] payload = [.. _outbound[..length]];
            Transmit(ArdopFrameCodec.EncodeDataFrame(typeToSend, payload, _sessionId), _calcLeaderMs, notBefore);
        }
        else
        {
            // Nothing to send — IDLE chirps every ~2 s (rule 2.5).
            SetState(ArdopProtocolState.Idle);
            _repeatEnabled = true;
            _timeoutTripMs = _nowMs;
            _lastFrameSentData = false;
            _repeatIntervalMs = ComputeInterFrameInterval(2000);
            ClearDataToSend();
            Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Idle, _sessionId), _config.LeaderLengthMs, notBefore);
        }
    }

    // GetNextARQFrame (ARQ.c:347): true = retransmit the last frame.
    private bool GetNextArqFrame()
    {
        if (_abort)
        {
            ClearDataToSend();
            SetState(ArdopProtocolState.Disc);
            InitializeConnection();
            _abort = false;
            _repeatEnabled = false;
            _discRepeating = false;
            _repeatCount = 0;
            return false;
        }

        if (_discRepeating)
        {
            _repeatCount++;
            _repeatEnabled = false;
            if (_repeatCount > 5)
            {
                Notify("DISCONNECTED");
                Notify($"STATUS END NOT RECEIVED CLOSING ARQ SESSION WITH {_remote}");
                _discRepeating = false;
                ClearDataToSend();
                SetState(ArdopProtocolState.Disc);
                _repeatCount = 0;
                InitializeConnection();
                return false;
            }

            _lastTx = ArdopFrameCodec.EncodeControl(ArdopFrameType.Disc, _sessionId);
            return true;
        }

        if ((_state is ArdopProtocolState.Iss or ArdopProtocolState.Idle) && CheckForDisconnect())
        {
            return false;
        }

        if (_state == ArdopProtocolState.Iss && _subState == ArdopArqSubState.IssConReq)
        {
            _repeatCount++;
            if (_repeatCount > _config.ConReqRepeats)
            {
                ClearDataToSend();
                SetState(ArdopProtocolState.Disc);
                _repeatCount = 0;
                _pending = false;
                Notify($"STATUS CONNECT TO {_remote} FAILED!");
                InitializeConnection();
                return false;
            }

            return true;
        }

        // Session timeout from a connected state (rule 1.7; ARQ.c:460).
        if (_state is ArdopProtocolState.Iss or ArdopProtocolState.Idle
            or ArdopProtocolState.Irs or ArdopProtocolState.IrsToIss)
        {
            if ((_nowMs - _timeoutTripMs) / 1000 > _config.ArqTimeoutSeconds && !_timeoutTriggered)
            {
                _repeatEnabled = false;
                _timeoutTriggered = true;
                _sendTimeoutAtMs = _nowMs + 1000;
                return false;
            }
        }

        if (_state == ArdopProtocolState.Disc && _pingRepeats > 0)
        {
            _repeatCount++;
            if (_repeatCount <= _pingRepeats && _pingRepeating)
            {
                return true;
            }

            _pingRepeats = 0;
            _pingRepeating = false;
            return false;
        }

        if (_state == ArdopProtocolState.Disc)
        {
            _disconnectRequested = false;
            _repeatCount = 0;
            return false;
        }

        return _repeatEnabled;
    }

    // CheckForDisconnect (ARQ.c:2495).
    private bool CheckForDisconnect()
    {
        if (!_disconnectRequested)
        {
            return false;
        }

        Notify("STATUS INITIATING ARQ DISCONNECT");
        _repeatIntervalMs = 2000;
        _repeatCount = 1;
        _disconnectRequested = false;
        _discRepeating = true;
        _repeatEnabled = false;

        if (_txInFlight.Count > 0)
        {
            // Mid-transmission: the DISC goes out via the repeat path instead.
            _lastTx = ArdopFrameCodec.EncodeControl(ArdopFrameType.Disc, _sessionId);
            return true;
        }

        Transmit(ArdopFrameCodec.EncodeControl(ArdopFrameType.Disc, _sessionId), _config.LeaderLengthMs, 0);
        return true;
    }

    // SendID (ARDOPC.c:1560): ID frame with grid square; deferred if transmitting.
    private void SendIdFrame(ArdopStationId? id, long notBefore = 0)
    {
        var idToSend = id ?? _config.MyCall;
        if (idToSend is null)
        {
            _finalIdAtMs = 0;
            _lastIdFrameTimeMs = 0;
            return;
        }

        if (_txInFlight.Count > 0 && notBefore == 0)
        {
            return;  // don't interrupt; timers stay set, retried later
        }

        Transmit(ArdopFrameCodec.EncodeIdFrame(idToSend, _config.GridSquare), _config.LeaderLengthMs, notBefore);
        _finalIdAtMs = 0;
        _lastIdFrameTimeMs = 0;
    }

    // ProcessPingFrame (ARDOPC.c:2264).
    private void ProcessPingFrame(ArdopDecodedFrame frame, long notBefore)
    {
        Notify($"PING {frame.Caller}>{frame.Target} {frame.SnDb} {frame.Quality}");

        if (_state == ArdopProtocolState.Disc && _config.Listen && _config.EnablePingAck
            && IsCallToMe(frame, out _, out var target, out _))
        {
            Transmit(ArdopFrameCodec.EncodePingAck(frame.SnDb, frame.Quality), _config.LeaderLengthMs, notBefore);
            Notify("PINGREPLY");

            if (_finalIdAtMs == 0)
            {
                _finalId = target;
                _finalIdAtMs = _nowMs + 3000;
            }

            return;
        }

        Notify("CANCELPENDING");
    }

    // ------------------------------------------------------------- negotiation

    // IRSNegotiateBW (ARQ.c:2318): the ConAck type answering a ConReq under our
    // ARQBW setting, or ConRejBW. Sets the session bandwidth on success.
    private int IrsNegotiateBw(byte conReqType)
    {
        int reply = Negotiate(_config.ArqBandwidth, conReqType);
        if (reply != ArdopFrameType.ConRejBw)
        {
            _sessionBw = ConAckBandwidth((byte)reply);
        }

        return reply;
    }

    private static int Negotiate(ArdopBandwidth setting, byte conReq) => setting switch
    {
        ArdopBandwidth.B200Forced when (conReq >= ArdopFrameType.ConReq200M && conReq <= ArdopFrameType.ConReq2000M) || conReq == ArdopFrameType.ConReq200F
            => ArdopFrameType.ConAck200,

        ArdopBandwidth.B500Forced when (conReq >= ArdopFrameType.ConReq500M && conReq <= ArdopFrameType.ConReq2000M) || conReq == ArdopFrameType.ConReq500F
            => ArdopFrameType.ConAck500,

        ArdopBandwidth.B1000Forced when (conReq >= ArdopFrameType.ConReq1000M && conReq <= ArdopFrameType.ConReq2000M) || conReq == ArdopFrameType.ConReq1000F
            => ArdopFrameType.ConAck1000,

        ArdopBandwidth.B2000Forced when conReq is ArdopFrameType.ConReq2000M or ArdopFrameType.ConReq2000F
            => ArdopFrameType.ConAck2000,

        ArdopBandwidth.B200Max when conReq >= ArdopFrameType.ConReq200M && conReq <= ArdopFrameType.ConReq200F
            => ArdopFrameType.ConAck200,

        ArdopBandwidth.B500Max when conReq is ArdopFrameType.ConReq200M or ArdopFrameType.ConReq200F
            => ArdopFrameType.ConAck200,
        ArdopBandwidth.B500Max when (conReq >= ArdopFrameType.ConReq500M && conReq <= ArdopFrameType.ConReq2000M) || conReq == ArdopFrameType.ConReq500F
            => ArdopFrameType.ConAck500,

        ArdopBandwidth.B1000Max when conReq is ArdopFrameType.ConReq200M or ArdopFrameType.ConReq200F
            => ArdopFrameType.ConAck200,
        ArdopBandwidth.B1000Max when conReq is ArdopFrameType.ConReq500M or ArdopFrameType.ConReq500F
            => ArdopFrameType.ConAck500,
        ArdopBandwidth.B1000Max when (conReq >= ArdopFrameType.ConReq1000M && conReq <= ArdopFrameType.ConReq2000M) || conReq == ArdopFrameType.ConReq1000F
            => ArdopFrameType.ConAck1000,

        ArdopBandwidth.B2000Max when conReq is ArdopFrameType.ConReq200M or ArdopFrameType.ConReq200F
            => ArdopFrameType.ConAck200,
        ArdopBandwidth.B2000Max when conReq is ArdopFrameType.ConReq500M or ArdopFrameType.ConReq500F
            => ArdopFrameType.ConAck500,
        ArdopBandwidth.B2000Max when conReq is ArdopFrameType.ConReq1000M or ArdopFrameType.ConReq1000F
            => ArdopFrameType.ConAck1000,
        ArdopBandwidth.B2000Max when conReq is ArdopFrameType.ConReq2000M or ArdopFrameType.ConReq2000F
            => ArdopFrameType.ConAck2000,

        _ => ArdopFrameType.ConRejBw,
    };

    private static int ConAckBandwidth(byte conAckType) => conAckType switch
    {
        ArdopFrameType.ConAck200 => 200,
        ArdopFrameType.ConAck500 => 500,
        ArdopFrameType.ConAck1000 => 1000,
        _ => 2000,
    };

    // IsCallToMe (ARQ.c:541): matches MYCALL or any MYAUX; yields the reply session ID.
    private bool IsCallToMe(ArdopDecodedFrame frame, out ArdopStationId caller, out ArdopStationId target, out byte replySessionId)
    {
        caller = null!;
        target = null!;
        replySessionId = 0;

        if (frame.Caller is null || frame.Target is null
            || !ArdopStationId.TryParse(frame.Caller, out caller)
            || !ArdopStationId.TryParse(frame.Target, out target))
        {
            return false;
        }

        string targetText = target.ToString();
        if (_config.MyCall is not null && targetText == _config.MyCall.ToString())
        {
            replySessionId = ArdopCrc.SessionId(caller.ToString(), _config.MyCall.ToString());
            return true;
        }

        foreach (var aux in _config.AuxCalls)
        {
            if (targetText == aux.ToString())
            {
                replySessionId = ArdopCrc.SessionId(caller.ToString(), aux.ToString());
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------- housekeeping

    // SetARDOPProtocolState (ARQ.c:256).
    private void SetState(ArdopProtocolState value)
    {
        if (_state == value)
        {
            return;
        }

        _state = value;

        switch (value)
        {
            case ArdopProtocolState.Disc:
                _disconnectRequested = false;
                _connected = false;
                _pending = false;
                ClearDataToSend();
                break;

            case ArdopProtocolState.Iss:
            case ArdopProtocolState.Idle:
                _repeatEnabled = false;
                break;

            case ArdopProtocolState.Irs:
            case ArdopProtocolState.IrsToIss:
                _lastAckedDataFrameType = 0;
                break;
        }

        // ardopcf's NEWSTATE carries a trailing space (SetARDOPProtocolState,
        // ARQ.c:338 — "NEWSTATE %s ") which hosts like Pat explicitly work around;
        // reproduced for byte-compatibility on the host command socket.
        Notify($"NEWSTATE {StateName(value)} ");
    }

    // InitializeConnection (ARQ.c:1060).
    private void InitializeConnection()
    {
        _local = null;
        _remote = null;
        _sessionId = 0xFF;
        _sessionBw = 0;
        _lastAckedDataFrameType = 0;
        _dataInProcessLength = 0;
        _remoteLeaderMeasureMs = 0;
        _calcLeaderMs = _config.LeaderLengthMs;
        _gearshift.Reset();
    }

    // GetNextFrameData's initialize branch (ARQ.c:982): ladder for the session BW.
    private void InitializeLadder()
    {
        bool fmModes = _sessionBw == 2000 && (_config.TuningRangeHz <= 0 || _config.Use600Modes);
        _gearshift.Initialize(_sessionBw, _config.FskOnly, _config.FastStart, fmModes);
    }

    // ComputeInterFrameInterval (ARQ.c:248).
    private int ComputeInterFrameInterval(int requestedIntervalMs) =>
        Math.Max(1000, requestedIntervalMs + _remoteLeaderMeasureMs);

    private void ClearDataToSend()
    {
        _outbound.Clear();
        Notify("BUFFER 0");
    }

    private void RemoveDataFromQueue(int length)
    {
        if (length <= 0)
        {
            return;
        }

        length = Math.Min(length, _outbound.Count);
        _outbound.RemoveRange(0, length);
        Notify($"BUFFER {_outbound.Count}");
    }

    private void Transmit(byte[] encoded, int leaderMs, long notBefore, bool isRepeat = false)
    {
        byte type = encoded[0];
        // First transmission since the last ID frame starts the 10-minute ID window
        // (Modulate.c:695); an ID frame closes it.
        if (_lastIdFrameTimeMs == 0)
        {
            _lastIdFrameTimeMs = Math.Max(1, _nowMs);
        }

        if (type == ArdopFrameType.IdFrame)
        {
            _finalIdAtMs = 0;
            _lastIdFrameTimeMs = 0;
        }

        if (!isRepeat)
        {
            _lastTx = encoded;
        }

        // Whether this frame arms the repeat timer is captured now — in the blocking
        // reference nothing changes between play start and SoundFlush.
        _txInFlight.Enqueue((_repeatEnabled || _discRepeating, _repeatIntervalMs));
        TransmitRequested?.Invoke(new ArdopTxRequest
        {
            Type = type,
            EncodedFrame = encoded,
            LeaderLengthMs = leaderMs,
            NotBeforeMs = notBefore,
            IsRepeat = isRepeat,
        });
    }

    private static string StateName(ArdopProtocolState state) => state switch
    {
        ArdopProtocolState.Disc => "DISC",
        ArdopProtocolState.Iss => "ISS",
        ArdopProtocolState.Irs => "IRS",
        ArdopProtocolState.Idle => "IDLE",
        _ => "IRStoISS",
    };

    // ------------------------------------------------------------- busy detector

    /// <summary>
    /// Resets the busy detector to "clear", the state ardopcf seeds on LISTEN TRUE so a
    /// scanning station is not held off by stale history (<c>ClearBusy</c>,
    /// BusyDetect.c:36, called from HostInterface.c:882 and from the ConRejBusy path,
    /// ARQ.c:1207).
    /// </summary>
    public void ClearBusyDetector(long nowMs)
    {
        _nowMs = nowMs;
        _busySeeded = true;
        _busyLastTripMs = nowMs;
        _busyPriorTripMs = nowMs;

        // +610 ms so the "did the last busy episode clear properly" test below passes
        // from a cold start, which is what BusyDetect.c:40 does and why.
        _busyLastClearMs = nowMs + BusyClearSeedMs;

        // Back-date the last raw busy reading past the hold time so a latched busy
        // releases immediately (BusyDetect.c:41).
        _busyLastRawMs = nowMs - BusyHoldMs;
        _busy = false;
        _busyOnCount = 0;
        _busyOffCount = 0;
    }

    /// <summary>
    /// Samples <see cref="ArdopArqConfig.ChannelBusy"/> and applies ardopcf's hysteresis
    /// and host reporting: <c>UpdateBusyDetector</c>'s busy half (ARDOPC.c:2054-2142) over
    /// <c>BusyDetect3</c>'s filtering (BusyDetect.c:123-153).
    /// </summary>
    private void SampleBusyDetector(long nowMs)
    {
        // THE SAFETY PROPERTY. ardopcf evaluates busy only in DISC: UpdateBusyDetector
        // returns early in any other protocol state (ARDOPC.c:2054) and the whole
        // detect-and-report block is inside "if (ProtocolState == DISC)"
        // (ARDOPC.c:2102-2142). So the busy state is frozen for the life of a session and
        // cannot gate anything in it. That is not incidental: an IRS has to get its ACK
        // out inside the ISS's repeat window, and a busy check that stalled a burst would
        // break the link rather than protect the channel. Do not move this guard, and do
        // not read _busy from any in-session path.
        if (_state != ArdopProtocolState.Disc)
        {
            return;
        }

        if (!_busySeeded)
        {
            // ardopcf's timestamps start at zero, and every host seeds them immediately
            // by sending LISTEN TRUE (HostInterface.c:882). Seed on first sample instead,
            // so the history is never read uninitialised.
            ClearBusyDetector(nowMs);
        }
        else if (nowMs - _busySampledAtMs < BusySampleIntervalMs)
        {
            return;  // at most one check per 100 ms (LastBusyCheck, ARDOPC.c:2062)
        }

        _busySampledAtMs = nowMs;

        // BUSYDET 0 disables detection. ardopcf forces the raw reading false and lets the
        // ordinary hysteresis release a latched busy (BusyDetect.c:120-121).
        bool raw = _config.BusyDetectLevel != 0 && (_config.ChannelBusy?.Invoke() ?? false);

        // Busy must be present on 3 consecutive checks, about 250-300 ms, to count
        // (BusyDetect.c:123-137). dttLastTrip only advances from the 4th, hence "> 3".
        if (raw)
        {
            _busyOnCount++;
            _busyOffCount = 0;
            if (_busyOnCount > 3)
            {
                _busyLastRawMs = nowMs;
            }
        }
        else
        {
            _busyOffCount++;
            _busyOnCount = 0;
        }

        if (!_busy && _busyOnCount >= 3)
        {
            _busyPriorTripMs = _busyLastTripMs;  // kept for the BUSYBLOCK test
            _busyLastTripMs = nowMs;
            _busy = true;
        }
        else if (_busy && nowMs - _busyLastRawMs > BusyHoldMs && _busyOffCount >= 3)
        {
            // Clearing needs 3 clear checks AND 5 s since the last busy reading
            // (intHoldMs, BusyDetect.c:33 and :147).
            _busyLastClearMs = nowMs;
            _busy = false;
        }

        // Report transitions of the latched state to the host, tracked separately from
        // the detector's own flag so that a ClearBusyDetector while the host believes the
        // channel is busy still produces BUSY FALSE (blnLastBusyStatus, ARDOPC.c:2045,
        // :2106-2142).
        if (_busy != _busyReported)
        {
            _busyReported = _busy;
            Notify(_busy ? "BUSY TRUE" : "BUSY FALSE");
        }
    }

    /// <summary>
    /// ardopcf's BUSYBLOCK test for an inbound ConReq (ARQ.c:1197-1202): is the channel
    /// busy with traffic this caller did not cause?
    /// </summary>
    private bool IsConReqBlockedByBusy(ArdopDecodedFrame frame)
    {
        if (!_busySeeded)
        {
            return false;  // nothing observed, so nothing to blame the caller for
        }

        // blnLeaderTrippedBusy (ARQ.c:1197). ardopcf asks whether the busy detector
        // tripped within 300 ms of this frame's leader being detected: if it did, the
        // caller's own carrier is what tripped it, so the caller is judged on the
        // PREVIOUS busy episode instead. Without that exception, a detector that hears
        // the incoming ConReq, which every real one does, would reject every caller.
        //
        // ardopcf can compare the two instants directly because its detector runs on the
        // same audio and stamps dttLastLeaderDetect at leader detect (SoundInput.c:878).
        // Here the busy reading comes from the host and the engine first sees a frame
        // once it has fully decoded, one whole frame later, so the same question is asked
        // over this frame's whole occupancy: did busy trip at any point since this
        // transmission began? The span is the frame's own measured leader (LEADER is
        // host-settable from 120 to 2500 ms) plus a ConReq body, plus ardopcf's 300 ms of
        // slack. Erring long errs toward accepting the call, which is the safe direction:
        // it degrades to today's behaviour rather than refusing legitimate callers.
        long conReqSpanMs = frame.LeaderReceivedMs + BusyConReqBodyMs + 300;
        bool leaderTrippedBusy = _nowMs - _busyLastTripMs < conReqSpanMs;

        // Accept only if the relevant busy episode has since cleared properly, which is
        // what ardopcf's 600 ms test amounts to: a real episode runs for at least the 5 s
        // hold, and a cold start is seeded to +610 ms so it passes (BusyDetect.c:40).
        return leaderTrippedBusy
            ? _busyLastClearMs - _busyPriorTripMs < BusyClearMarginMs
            : _busyLastClearMs - _busyLastTripMs < BusyClearMarginMs;
    }

    private void Notify(string message) => HostNotification?.Invoke(message);
}
