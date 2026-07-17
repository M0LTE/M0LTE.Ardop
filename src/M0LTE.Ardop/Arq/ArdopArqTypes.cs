namespace M0LTE.Ardop.Arq;

/// <summary>
/// ARDOP protocol states (<c>_ARDOPState</c>, ardopcf ARDOPC.h:271, git a7c9228, MIT,
/// © 2014-2024 Rick Muething, John Wiseman, Peter LaRue; ARDOP spec Fig. D-2). The FEC
/// states are carried by <see cref="ArdopFecSender"/>/<see cref="ArdopFecReceiver"/>;
/// the ARQ engine occupies the five ARQ states.
/// </summary>
public enum ArdopProtocolState
{
    /// <summary>Not connected, listening (DISC).</summary>
    Disc,

    /// <summary>Information Sending Station.</summary>
    Iss,

    /// <summary>Information Receiving Station.</summary>
    Irs,

    /// <summary>ISS ran out of data; sending IDLE chirps awaiting BREAK or new data.</summary>
    Idle,

    /// <summary>IRS repeating BREAK to take the link (spec rule 3.3).</summary>
    IrsToIss,
}

/// <summary>ARQ sub-states (<c>_ARQSubStates</c>, ARDOPC.h:290).</summary>
public enum ArdopArqSubState
{
    /// <summary>No ARQ activity.</summary>
    None,

    /// <summary>ISS repeating ConReq awaiting ConAck (rule 1.1).</summary>
    IssConReq,

    /// <summary>ISS sent its ConAck reply, awaiting the IRS's first ACK (rule 1.4).</summary>
    IssConAck,

    /// <summary>ISS exchanging data frames.</summary>
    IssData,

    /// <summary>ISS sending ID (unused by ardopcf; carried for parity).</summary>
    IssId,

    /// <summary>IRS sent ConAck, awaiting the ISS's confirming ConAck (rule 1.2).</summary>
    IrsConAck,

    /// <summary>IRS receiving data.</summary>
    IrsData,

    /// <summary>IRS BREAK pending (unused by ardopcf; carried for parity).</summary>
    IrsBreak,

    /// <summary>New IRS after a role swap; completes the changeover on the first data
    /// frame received (rule 3.5).</summary>
    IrsFromIss,

    /// <summary>Session ended from the ARQ path.</summary>
    DiscArqEnd,
}

/// <summary>
/// The ARQBW / CALLBW setting space (<c>_ARQBandwidth</c>, ARDOPC.h:305;
/// <c>ARQBandwidths</c> ARQ.c:70): each of the four bandwidth classes as either
/// FORCED (only that class acceptable) or MAX (negotiate downward).
/// </summary>
public enum ArdopBandwidth
{
    /// <summary>200 Hz, forced.</summary>
    B200Forced,

    /// <summary>500 Hz, forced.</summary>
    B500Forced,

    /// <summary>1000 Hz, forced.</summary>
    B1000Forced,

    /// <summary>2000 Hz, forced.</summary>
    B2000Forced,

    /// <summary>200 Hz maximum.</summary>
    B200Max,

    /// <summary>500 Hz maximum.</summary>
    B500Max,

    /// <summary>1000 Hz maximum.</summary>
    B1000Max,

    /// <summary>2000 Hz maximum.</summary>
    B2000Max,

    /// <summary>No per-call override (CALLBW UNDEFINED).</summary>
    Undefined,
}

/// <summary>Helpers over <see cref="ArdopBandwidth"/>.</summary>
public static class ArdopBandwidthExtensions
{
    /// <summary>The bandwidth in Hz (0 for <see cref="ArdopBandwidth.Undefined"/>).</summary>
    public static int Hertz(this ArdopBandwidth bandwidth) => bandwidth switch
    {
        ArdopBandwidth.B200Forced or ArdopBandwidth.B200Max => 200,
        ArdopBandwidth.B500Forced or ArdopBandwidth.B500Max => 500,
        ArdopBandwidth.B1000Forced or ArdopBandwidth.B1000Max => 1000,
        ArdopBandwidth.B2000Forced or ArdopBandwidth.B2000Max => 2000,
        _ => 0,
    };

    /// <summary>True for the FORCED variants.</summary>
    public static bool IsForced(this ArdopBandwidth bandwidth) =>
        bandwidth is ArdopBandwidth.B200Forced or ArdopBandwidth.B500Forced
            or ArdopBandwidth.B1000Forced or ArdopBandwidth.B2000Forced;

    /// <summary>The ConReq frame type a caller sends for this setting
    /// (<c>EncodeARQConRequest</c>, ARDOPC.c:1337: MAX → the M codes 0x31-0x34,
    /// FORCED → the F codes 0x35-0x38).</summary>
    public static byte ConReqFrameType(this ArdopBandwidth bandwidth) => bandwidth switch
    {
        ArdopBandwidth.B200Max => ArdopFrameType.ConReq200M,
        ArdopBandwidth.B500Max => ArdopFrameType.ConReq500M,
        ArdopBandwidth.B1000Max => ArdopFrameType.ConReq1000M,
        ArdopBandwidth.B2000Max => ArdopFrameType.ConReq2000M,
        ArdopBandwidth.B200Forced => ArdopFrameType.ConReq200F,
        ArdopBandwidth.B500Forced => ArdopFrameType.ConReq500F,
        ArdopBandwidth.B1000Forced => ArdopFrameType.ConReq1000F,
        ArdopBandwidth.B2000Forced => ArdopFrameType.ConReq2000F,
        _ => throw new ArgumentException("UNDEFINED has no ConReq frame", nameof(bandwidth)),
    };

    /// <summary>Parses the host-command form ("500MAX", "2000FORCED", "UNDEFINED").</summary>
    public static bool TryParse(string text, out ArdopBandwidth bandwidth)
    {
        bandwidth = text.ToUpperInvariant() switch
        {
            "200FORCED" => ArdopBandwidth.B200Forced,
            "500FORCED" => ArdopBandwidth.B500Forced,
            "1000FORCED" => ArdopBandwidth.B1000Forced,
            "2000FORCED" => ArdopBandwidth.B2000Forced,
            "200MAX" => ArdopBandwidth.B200Max,
            "500MAX" => ArdopBandwidth.B500Max,
            "1000MAX" => ArdopBandwidth.B1000Max,
            "2000MAX" => ArdopBandwidth.B2000Max,
            "UNDEFINED" => ArdopBandwidth.Undefined,
            _ => (ArdopBandwidth)(-1),
        };
        return bandwidth >= ArdopBandwidth.B200Forced && bandwidth <= ArdopBandwidth.Undefined;
    }
}
