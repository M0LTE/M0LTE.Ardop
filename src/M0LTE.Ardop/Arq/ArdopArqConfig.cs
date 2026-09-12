namespace M0LTE.Ardop.Arq;

/// <summary>
/// Live-settable station configuration for the ARQ engine — the subset of ardopcf's
/// host-commandable globals the protocol machine reads (defaults from ARDOPC.c:86-119
/// and ARDOPCommon.c:69, git a7c9228, MIT, © 2014-2024 Rick Muething, John Wiseman,
/// Peter LaRue). Mutable because the host interface changes these mid-session; the
/// engine reads them at each decision point, as ardopcf does.
/// </summary>
public sealed class ArdopArqConfig
{
    /// <summary>This station's callsign (MYCALL). Required before calling or
    /// answering.</summary>
    public ArdopStationId? MyCall { get; set; }

    /// <summary>Auxiliary callsigns also answered (MYAUX).</summary>
    public List<ArdopStationId> AuxCalls { get; } = [];

    /// <summary>Maidenhead grid square for ID frames (GRIDSQUARE).</summary>
    public string GridSquare { get; set; } = "";

    /// <summary>The station bandwidth setting (ARQBW; ardopcf default 2000MAX,
    /// ARDOPC.c:111). Governs both what an IRS accepts and what an ISS requests.</summary>
    public ArdopBandwidth ArqBandwidth { get; set; } = ArdopBandwidth.B2000Max;

    /// <summary>Per-call bandwidth override (CALLBW, default UNDEFINED = use
    /// <see cref="ArqBandwidth"/>; ARDOPC.c:86).</summary>
    public ArdopBandwidth CallBandwidth { get; set; } = ArdopBandwidth.Undefined;

    /// <summary>Idle-session timeout in seconds (ARQTIMEOUT, default 120, host-settable
    /// 30-240; ARDOPC.c:101).</summary>
    public int ArqTimeoutSeconds { get; set; } = 120;

    /// <summary>ConReq repeat budget (ARQCALL repeat count, default 5;
    /// ARDOPC.c:103).</summary>
    public int ConReqRepeats { get; set; } = 5;

    /// <summary>Two-tone leader length in ms (LEADER, default 240; ARDOPC.c:98).</summary>
    public int LeaderLengthMs { get; set; } = 240;

    /// <summary>Extra RX→TX turnaround padding in ms for long paths / slow rigs
    /// (EXTRADELAY, default 0; ARDOPC.c:100).</summary>
    public int ExtraDelayMs { get; set; } = 0;

    /// <summary>Leader used for the IRS's first ConAck reply, giving the caller a
    /// round-trip measurement (<c>intARQDefaultDlyMs</c>, default 240;
    /// ARDOPCommon.c:69).</summary>
    public int ArqDefaultDelayMs { get; set; } = 240;

    /// <summary>Answer connect requests (LISTEN, default true).</summary>
    public bool Listen { get; set; } = true;

    /// <summary>IRS BREAKs automatically when it has data to send (AUTOBREAK, default
    /// true; spec rule 3.3).</summary>
    public bool AutoBreak { get; set; } = true;

    /// <summary>Answer PING frames with PingAck (ENABLEPINGACK, default true).</summary>
    public bool EnablePingAck { get; set; } = true;

    /// <summary>Restrict the data-mode ladders to 4FSK modes (FSKONLY, default false;
    /// ARDOPC.c:118).</summary>
    public bool FskOnly { get; set; } = false;

    /// <summary>Start the gearshift midway up the ladder rather than at the most
    /// robust rung (<c>fastStart</c>, default true; ARDOPC.c:119).</summary>
    public bool FastStart { get; set; } = true;

    /// <summary>Enable the 600 Bd FM-only modes in the 2000 Hz ladder (USE600MODES,
    /// default false).</summary>
    public bool Use600Modes { get; set; } = false;

    /// <summary>Leader capture range in Hz; a zero range selects the FM 2000 Hz ladder
    /// as in ardopcf (<c>TuningRange</c>, default 100; GetDataModes ARQ.c:651).</summary>
    public int TuningRangeHz { get; set; } = 100;

    // ---------------------------------------------------------- channel-busy seam

    /// <summary>
    /// The channel-busy seam: the host application's answer to "is another station using
    /// this channel right now?".
    /// </summary>
    /// <remarks>
    /// <para><b>The default is <c>null</c>, which means the channel is never busy.</b>
    /// Leave it unset and every busy-derived behaviour is dormant: no <c>BUSY</c>
    /// notification is ever sent, no inbound ConReq is ever refused, no outgoing call is
    /// ever blocked, no ID frame is ever deferred. Behaviour is then identical to the
    /// releases before this seam existed, whatever BUSYDET and BUSYBLOCK are set to.</para>
    /// <para><b>Why a seam and not a detector.</b> This package has no audio device and no
    /// spectrum of its own, so it cannot port ardopcf's detector. That detector is not an
    /// energy detector: <c>BusyDetect3</c> (BusyDetect.c:52) rank-orders the magnitude
    /// bins of a 1024-point FFT of the receive audio, averages the top N as "signal" and
    /// the remainder as "baseline", and tests that ratio (<c>SortSignals2</c>,
    /// BusyDetect.c:208). It does so twice, over the top 8 bins (about 94 Hz) and over the
    /// top 66% of the window, and calls the channel busy if either exceeds its threshold
    /// (BusyDetect.c:102-118). Being a ratio of peaks to their own baseline, it is
    /// gain-invariant and noise-floor-invariant and needs no calibration. The window is
    /// centred on 1500 Hz and spans the ARQ bandwidth plus <c>TuningRange</c> either side
    /// (ARDOPC.c:2097-2100), which at ARQBW 500 watches 1160 to 1840 Hz: wider than
    /// anything ARDOP emits at that class, deliberately, because a busy detector does not
    /// get to choose its stations. The host owns the audio, so the host owns that
    /// decision; this property is where it hands the answer over.</para>
    /// <para><b>What the engine adds.</b> Only the raw observation is wanted here. The
    /// engine samples this at most every 100 ms (<c>LastBusyCheck</c>, ARDOPC.c:2062) and
    /// applies ardopcf's hysteresis to the result: busy must be observed on 3 consecutive
    /// samples to latch, and once latched it holds until 5 s after the last busy
    /// observation and 3 consecutive clear samples (<c>intHoldMs</c>, BusyDetect.c:33;
    /// BusyDetect.c:123-152). So a plain "is there energy in the passband now" answer
    /// yields ardopcf's timing without the host reimplementing it.</para>
    /// <para><b>When it is sampled.</b> Only while the protocol state is DISC, exactly as
    /// ardopcf only runs the detector in DISC (ARDOPC.c:2054 and :2102). An established
    /// session never consults it, so a busy channel can never stall an in-flight
    /// transmission. See <see cref="ArdopArqEngine.IsChannelBusy"/>.</para>
    /// <para><b>Threading.</b> Invoked on the thread that polls the engine, and under the
    /// TNC's protocol lock when driven by <c>ArdopHostTnc</c>. It must be cheap,
    /// non-blocking, and must not call back into the TNC or the engine.</para>
    /// </remarks>
    public Func<bool>? ChannelBusy { get; set; }

    /// <summary>
    /// Busy-detector sensitivity (BUSYDET, 0-10, ardopcf default 5; <c>BusyDet</c>
    /// ARDOPC.c:110, validated HostInterface.c:415).
    /// </summary>
    /// <remarks>
    /// <para><b>0 is honoured exactly</b>: it disables busy detection, as in ardopcf,
    /// where a zero forces the raw reading false and lets the ordinary hysteresis release
    /// an already-latched busy (BusyDetect.c:120-121). <see cref="ChannelBusy"/> is then
    /// not consulted at all.</para>
    /// <para><b>1-10 cannot be meaningfully honoured through this seam</b>, and saying so
    /// is the honest answer rather than pretending to accept the value. In ardopcf these
    /// parameterise the thresholds of a spectral peak-to-baseline test that does not exist
    /// here (BusyDetect.c:102-118); a boolean has no ratio to threshold. The value is
    /// still kept, reported back to the host verbatim, and published here, so the owner of
    /// <see cref="ChannelBusy"/> may read it and set its own detector's sensitivity from
    /// what the host asked for. If it does not, 1 and 10 behave identically. Nothing in
    /// this package varies with the value except the 0 case above.</para>
    /// <para><b>If you are calibrating a detector to sit behind this seam, match deployed
    /// ardopcf, not the thresholds in its source.</b> <c>BusyDetect3</c> means to keep a
    /// rolling average of the signal-to-baseline ratio, but <c>dblAvgStoNNarrow</c> and
    /// <c>dblAvgStoNWide</c> are locals re-zeroed on entry (BusyDetect.c:60), so the
    /// steady-state branch (BusyDetect.c:71 and :86) evaluates to
    /// <c>0.8 * 0 + 0.2 * ratio</c> and never accumulates; the file-scope
    /// <c>dblAvgStoNSlow*</c>/<c>dblAvgStoNFast*</c> that were presumably meant to hold it
    /// (BusyDetect.c:20-23) are referenced nowhere in the tree. The tested value is
    /// therefore one fifth of the true ratio, making the detector 7 dB less sensitive than
    /// its own numbers claim: at BUSYDET 5 the effective thresholds are 16.0 dB narrow and
    /// 19.4 dB wide (at 500 Hz), not the nominal 9.0 dB and 12.4 dB. The first call after
    /// a bandwidth change or a <c>ClearBusy</c> does take the full ratio
    /// (BusyDetect.c:76), but latching needs 3 consecutive trips (BusyDetect.c:139) and
    /// the following calls are back on the fifth-scale path, so that sample can never
    /// latch busy on its own and the effective figures are what every deployed station is
    /// running. Matching the effective behaviour is what keeps us interoperable; matching
    /// the nominal thresholds would make us trip where the rest of the band does not.</para>
    /// </remarks>
    public int BusyDetectLevel { get; set; } = 5;

    /// <summary>
    /// Refuse to start a session while the channel is busy (BUSYBLOCK, default false;
    /// <c>BusyBlock</c> ARQ.c:100, HostInterface.c:395).
    /// </summary>
    /// <remarks>
    /// <para><b>Inbound</b>, this is ardopcf's rule exactly: an inbound ConReq addressed
    /// to us is answered with ConRejBusy instead of ConAck when the busy history says the
    /// channel was already in use by someone other than the caller (ARQ.c:1199-1226).
    /// With BUSYBLOCK false, an inbound ConReq is accepted however busy the channel is,
    /// which is ardopcf's default.</para>
    /// <para><b>Outbound</b>, this is a deliberate divergence. ardopcf never blocks its
    /// own ARQCALL on the busy state: the host command sets <c>NeedConReq</c>
    /// (HostInterface.c:330) and <c>SendARQConnectRequest</c> (ARQ.c:2432) transmits
    /// without consulting the detector, leaving the decision to the host that was told
    /// BUSY TRUE. Here, BUSYBLOCK additionally blocks an outgoing call while the channel
    /// is busy, so a host that cannot make that decision itself still behaves on a shared
    /// channel. It remains off by default, and is doubly opt-in: it does nothing unless
    /// <see cref="ChannelBusy"/> is also wired.</para>
    /// </remarks>
    public bool BusyBlock { get; set; }
}
