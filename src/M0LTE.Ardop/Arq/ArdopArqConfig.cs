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
}
