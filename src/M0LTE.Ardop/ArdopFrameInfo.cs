namespace M0LTE.Ardop;

/// <summary>Modulation family of an ARDOP frame type.</summary>
public enum ArdopModulation
{
    /// <summary>4-tone FSK (all control frames; the 50/100/600 Bd data modes).</summary>
    Fsk4,

    /// <summary>Differential 4PSK, 100 Bd.</summary>
    Psk4,

    /// <summary>Differential 8PSK, 100 Bd.</summary>
    Psk8,

    /// <summary>16QAM (8 differential phases × 2 amplitudes), 100 Bd.</summary>
    Qam16,
}

/// <summary>
/// Frame geometry for one ARDOP frame type, transcribed from ardopcf's
/// <c>FrameInfo</c> (ARDOPC.c:770-1077, git a7c9228, MIT, © 2014-2024 Rick Muething,
/// John Wiseman, Peter LaRue), matching ARDOP spec App. B. Transcribed from the code,
/// not the spec PDF's table images (docs/ardop-design.md §9.3).
/// </summary>
/// <param name="Type">The frame-type code.</param>
/// <param name="Name">The frame name (<see cref="ArdopFrameType.Name"/>).</param>
/// <param name="IsOdd">The even/odd duplicate-detection toggle (bit 0 of the type).</param>
/// <param name="CarrierCount">Simultaneous carriers (1 for all 4FSK modes).</param>
/// <param name="Modulation">Modulation family.</param>
/// <param name="Baud">Symbol rate: 50, 100 or 600.</param>
/// <param name="DataLength">Payload bytes per carrier (total across the three
/// sequential blocks for the 600 Bd long frame).</param>
/// <param name="RsLength">Reed-Solomon parity bytes per carrier (total across blocks
/// for the 600 Bd long frame).</param>
/// <param name="QualityThreshold">Minimum quality threshold ardopcf associates with
/// the type (carried for parity; not consulted by the Phase A decoder).</param>
public sealed record ArdopFrameInfo(
    byte Type,
    string Name,
    bool IsOdd,
    int CarrierCount,
    ArdopModulation Modulation,
    int Baud,
    int DataLength,
    int RsLength,
    byte QualityThreshold)
{
    /// <summary>Looks up the geometry for <paramref name="type"/>; false for codes with
    /// no assigned frame (mirrors <c>FrameInfo</c> returning FALSE).</summary>
    public static bool TryGet(byte type, out ArdopFrameInfo info)
    {
        info = Lookup(type)!;
        return info is not null;
    }

    /// <summary>Looks up the geometry for <paramref name="type"/>, throwing for
    /// unassigned codes.</summary>
    public static ArdopFrameInfo Get(byte type) =>
        Lookup(type) ?? throw new ArgumentException($"no ARDOP frame type 0x{type:X2}", nameof(type));

    private static ArdopFrameInfo? Lookup(byte type)
    {
        bool odd = (type & 1) != 0;
        string name = ArdopFrameType.Name(type);

        // 1-carrier 4FSK control frames: NAK (0x00-0x1F) and ACK (0xE0-0xFF).
        // (ardopcf's switch has a dead DataACKmin case with threshold 60; the range
        // branch with threshold 40 is what actually executes for the whole ACK range.)
        if (type <= ArdopFrameType.DataNakMax || type >= ArdopFrameType.DataAckMin)
        {
            return new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 50, 0, 0, 40);
        }

        return type switch
        {
            // Short control frames.
            ArdopFrameType.Break or ArdopFrameType.Idle or ArdopFrameType.Disc =>
                new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 50, 0, 0, 60),
            ArdopFrameType.End or ArdopFrameType.ConRejBusy or ArdopFrameType.ConRejBw =>
                new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 50, 0, 0, 60),

            // ID, connect requests and Ping: 12 payload bytes + 4 RS, no count/CRC.
            ArdopFrameType.IdFrame or (>= ArdopFrameType.ConReqMin and <= ArdopFrameType.ConReqMax)
                or ArdopFrameType.Ping =>
                new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 50, 12, 4, 50),

            // Connect ACKs and PingAck: 3 payload bytes (×3 redundancy), no RS.
            >= ArdopFrameType.ConAck200 and <= ArdopFrameType.PingAck =>
                new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 50, 3, 0, 50),

            // 200 Hz class data.
            0x40 or 0x41 => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Psk4, 100, 64, 32, 30),
            0x42 or 0x43 => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Psk4, 100, 16, 8, 30),
            0x44 or 0x45 => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Psk8, 100, 108, 36, 30),
            0x46 or 0x47 => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Qam16, 100, 128, 64, 30),
            0x48 or 0x49 => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 50, 16, 4, 30),

            // 500 Hz class data.
            0x4A or 0x4B => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 100, 64, 16, 30),
            0x4C or 0x4D => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 100, 32, 8, 30),
            0x50 or 0x51 => new ArdopFrameInfo(type, name, odd, 2, ArdopModulation.Psk4, 100, 64, 32, 50),
            0x52 or 0x53 => new ArdopFrameInfo(type, name, odd, 2, ArdopModulation.Psk8, 100, 108, 36, 50),
            0x54 or 0x55 => new ArdopFrameInfo(type, name, odd, 2, ArdopModulation.Qam16, 100, 128, 64, 50),

            // 1000 Hz class data.
            0x60 or 0x61 => new ArdopFrameInfo(type, name, odd, 4, ArdopModulation.Psk4, 100, 64, 32, 50),
            0x62 or 0x63 => new ArdopFrameInfo(type, name, odd, 4, ArdopModulation.Psk8, 100, 108, 36, 50),
            0x64 or 0x65 => new ArdopFrameInfo(type, name, odd, 4, ArdopModulation.Qam16, 100, 128, 64, 50),

            // 2000 Hz class data.
            0x70 or 0x71 => new ArdopFrameInfo(type, name, odd, 8, ArdopModulation.Psk4, 100, 64, 32, 50),
            0x72 or 0x73 => new ArdopFrameInfo(type, name, odd, 8, ArdopModulation.Psk8, 100, 108, 36, 50),
            0x74 or 0x75 => new ArdopFrameInfo(type, name, odd, 8, ArdopModulation.Qam16, 100, 128, 64, 50),

            // 600 Bd 4FSK (FM-only 2000 Hz modes). The long frame's 600/150 bytes are
            // carried as three sequential 200+50 blocks (EncodeFSKData, ARDOPC.c:1294).
            0x7A or 0x7B => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 600, 600, 150, 30),
            0x7C or 0x7D => new ArdopFrameInfo(type, name, odd, 1, ArdopModulation.Fsk4, 600, 200, 50, 30),

            _ => null,
        };
    }
}
