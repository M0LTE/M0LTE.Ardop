namespace M0LTE.Ardop.Arq;

/// <summary>
/// One frame the ARQ engine wants on the air. The driver modulates
/// <see cref="EncodedFrame"/> (via <see cref="ArdopModulator"/>) with
/// <see cref="LeaderLengthMs"/>, starts playout no earlier than
/// <see cref="NotBeforeMs"/> on the engine's clock, and calls
/// <see cref="ArdopArqEngine.TransmitCompleted"/> when the last sample has played.
/// Requests are strictly ordered — play them in the sequence they were raised.
/// </summary>
public sealed record ArdopTxRequest
{
    /// <summary>The frame-type code (equals <c>EncodedFrame[0]</c>).</summary>
    public required byte Type { get; init; }

    /// <summary>The encoded frame bytes for the modulator.</summary>
    public required byte[] EncodedFrame { get; init; }

    /// <summary>Two-tone leader length in ms.</summary>
    public required int LeaderLengthMs { get; init; }

    /// <summary>Earliest engine-clock time playout may start: RX→TX turnaround
    /// enforcement (250 ms + EXTRADELAY after decode, ARQ.c:1127). Zero = immediately.</summary>
    public long NotBeforeMs { get; init; }

    /// <summary>True when this is a repeat of the previous transmission
    /// (<c>RemodulateLastFrame</c>).</summary>
    public bool IsRepeat { get; init; }
}
