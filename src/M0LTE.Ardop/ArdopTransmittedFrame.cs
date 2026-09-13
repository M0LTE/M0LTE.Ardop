namespace M0LTE.Ardop;

/// <summary>One frame this station transmitted, as it went out.</summary>
/// <remarks>
/// The transmit counterpart of <see cref="ArdopDecodedFrame"/>, and deliberately the same
/// shape where the two have anything in common, so a monitor can list what went out beside
/// what came in under one frame-type spelling. What it does not carry is a measurement:
/// quality, S:N and leader length are things a receiver measures, and stating them about our
/// own transmission would be inventing a measurement of ourselves.
/// </remarks>
public sealed record ArdopTransmittedFrame
{
    /// <summary>The frame-type code.</summary>
    public required byte Type { get; init; }

    /// <summary>The frame-type name (<see cref="ArdopFrameType.Name"/>), the same spelling a
    /// receiving station's <see cref="ArdopDecodedFrame.Name"/> carries for this frame.</summary>
    public string Name => ArdopFrameType.Name(Type);

    /// <summary>Payload bytes: the net payload for data frames (ARQ and FEC alike); empty for
    /// control frames, which carry none.</summary>
    public required byte[] Data { get; init; }

    /// <summary>This station for ConReq/Ping/ID frames, which carry the callsigns in clear;
    /// null on every other frame type, which carries no callsign at all.</summary>
    public string? Caller { get; init; }

    /// <summary>The station called, for ConReq/Ping.</summary>
    public string? Target { get; init; }

    /// <summary>Grid square for ID frames.</summary>
    public string? GridSquare { get; init; }
}
