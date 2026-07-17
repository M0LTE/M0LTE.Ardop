namespace M0LTE.Ardop;

/// <summary>
/// The session identity the frame-type decoder works under — the inputs to ardopcf's
/// <c>MinimalDistanceFrameType</c> (SoundInput.c:2137, git a7c9228, MIT, © 2014-2024
/// Rick Muething, John Wiseman, Peter LaRue): which candidate set to search, and which
/// session IDs the second type byte may be XORed with. The ARQ engine keeps this in
/// step via <see cref="Arq.ArdopArqEngine.SyncRxScope"/>; the defaults reproduce the
/// unconnected/FEC behaviour.
/// </summary>
public sealed class ArdopRxScope
{
    /// <summary>Search only the frames an ISS can legally receive — ACKs, NAKs, BREAK,
    /// DISC, END, ConRej and ConReq/ConAck (<c>bytValidFrameTypesISS</c>,
    /// ARDOPC.c:248). Set while the protocol state is ISS.</summary>
    public bool UseIssCandidates { get; set; }

    /// <summary>A connect handshake is pending (<c>blnPending</c>).</summary>
    public bool Pending { get; set; }

    /// <summary>The pending session ID (<c>bytPendingSessionID</c>).</summary>
    public byte PendingSessionId { get; set; }

    /// <summary>An ARQ session is connected (<c>blnARQConnected</c>).</summary>
    public bool Connected { get; set; }

    /// <summary>The connected session ID (<c>bytSessionID</c>).</summary>
    public byte SessionId { get; set; } = 0xFF;

    /// <summary>The previous session's ID, accepting DISC replays whose END was missed
    /// (<c>bytLastARQSessionID</c>; protocol rule 1.6). ardopcf's initial value is 0.</summary>
    public byte LastSessionId { get; set; }
}
