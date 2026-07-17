namespace M0LTE.Ardop;

/// <summary>
/// The ARDOP frame-type space: named codes, classification helpers, the frame-type
/// parity symbol and the valid-type candidate list used by the minimal-distance
/// frame-type decoder. Ported from ardopcf (git a7c9228, MIT, © 2014-2024 Rick Muething,
/// John Wiseman, Peter LaRue): ARDOPC.h:330-367, <c>strFrameType</c> ARDOPC.c:321,
/// <c>IsShortControlFrame</c>/<c>IsDataFrame</c> SoundInput.c:340-365,
/// <c>ComputeTypeParity</c> ARDOPC.c:1640, <c>bytValidFrameTypesALL</c> ARDOPC.c:226.
/// Matches ARDOP spec App. B. See PROVENANCE.md and docs/ardop-design.md §3.3.
/// </summary>
public static class ArdopFrameType
{
    /// <summary>DataNAK range start — the 5 LSBs carry quality (Q = 38 + 2·bits).</summary>
    public const byte DataNakMin = 0x00;

    /// <summary>DataNAK range end.</summary>
    public const byte DataNakMax = 0x1F;

    /// <summary>BREAK (0x23).</summary>
    public const byte Break = 0x23;

    /// <summary>IDLE (0x24).</summary>
    public const byte Idle = 0x24;

    /// <summary>DISC (0x29).</summary>
    public const byte Disc = 0x29;

    /// <summary>END (0x2C).</summary>
    public const byte End = 0x2C;

    /// <summary>ConRejBusy (0x2D).</summary>
    public const byte ConRejBusy = 0x2D;

    /// <summary>ConRejBW (0x2E).</summary>
    public const byte ConRejBw = 0x2E;

    /// <summary>ID frame (0x30): callsign + grid square, 12 bytes + RS 4.</summary>
    public const byte IdFrame = 0x30;

    /// <summary>ConReq200M (0x31); the ConReq range runs 0x31-0x38.</summary>
    public const byte ConReq200M = 0x31;

    /// <summary>ConReq500M (0x32).</summary>
    public const byte ConReq500M = 0x32;

    /// <summary>ConReq1000M (0x33).</summary>
    public const byte ConReq1000M = 0x33;

    /// <summary>ConReq2000M (0x34).</summary>
    public const byte ConReq2000M = 0x34;

    /// <summary>ConReq200F (0x35).</summary>
    public const byte ConReq200F = 0x35;

    /// <summary>ConReq500F (0x36).</summary>
    public const byte ConReq500F = 0x36;

    /// <summary>ConReq1000F (0x37).</summary>
    public const byte ConReq1000F = 0x37;

    /// <summary>ConReq2000F (0x38).</summary>
    public const byte ConReq2000F = 0x38;

    /// <summary>First ConReq code.</summary>
    public const byte ConReqMin = 0x31;

    /// <summary>Last ConReq code.</summary>
    public const byte ConReqMax = 0x38;

    /// <summary>ConAck200 (0x39); the ConAck range runs 0x39-0x3C.</summary>
    public const byte ConAck200 = 0x39;

    /// <summary>ConAck500 (0x3A).</summary>
    public const byte ConAck500 = 0x3A;

    /// <summary>ConAck1000 (0x3B).</summary>
    public const byte ConAck1000 = 0x3B;

    /// <summary>ConAck2000 (0x3C).</summary>
    public const byte ConAck2000 = 0x3C;

    /// <summary>PingAck (0x3D): S:N + quality, 3 bytes ×3 redundancy, no RS.</summary>
    public const byte PingAck = 0x3D;

    /// <summary>Ping (0x3E): caller + target callsigns, 12 bytes + RS 4.</summary>
    public const byte Ping = 0x3E;

    /// <summary>DataACK range start — the 5 LSBs carry quality, as DataNAK.</summary>
    public const byte DataAckMin = 0xE0;

    /// <summary>The frame-type name table (<c>strFrameType</c>, ARDOPC.c:321).
    /// NAK/ACK ranges collapse to "DataNAK"/"DataACK".</summary>
    public static string Name(byte type)
    {
        if (type <= DataNakMax)
        {
            return "DataNAK";
        }

        if (type >= DataAckMin)
        {
            return "DataACK";
        }

        return type switch
        {
            Break => "BREAK",
            Idle => "IDLE",
            Disc => "DISC",
            End => "END",
            ConRejBusy => "ConRejBusy",
            ConRejBw => "ConRejBW",
            IdFrame => "IDFrame",
            ConReq200M => "ConReq200M",
            ConReq500M => "ConReq500M",
            ConReq1000M => "ConReq1000M",
            ConReq2000M => "ConReq2000M",
            ConReq200F => "ConReq200F",
            ConReq500F => "ConReq500F",
            ConReq1000F => "ConReq1000F",
            ConReq2000F => "ConReq2000F",
            ConAck200 => "ConAck200",
            ConAck500 => "ConAck500",
            ConAck1000 => "ConAck1000",
            ConAck2000 => "ConAck2000",
            PingAck => "PingAck",
            Ping => "Ping",
            0x40 => "4PSK.200.100.E",
            0x41 => "4PSK.200.100.O",
            0x42 => "4PSK.200.100S.E",
            0x43 => "4PSK.200.100S.O",
            0x44 => "8PSK.200.100.E",
            0x45 => "8PSK.200.100.O",
            0x46 => "16QAM.200.100.E",
            0x47 => "16QAM.200.100.O",
            0x48 => "4FSK.200.50S.E",
            0x49 => "4FSK.200.50S.O",
            0x4A => "4FSK.500.100.E",
            0x4B => "4FSK.500.100.O",
            0x4C => "4FSK.500.100S.E",
            0x4D => "4FSK.500.100S.O",
            0x50 => "4PSK.500.100.E",
            0x51 => "4PSK.500.100.O",
            0x52 => "8PSK.500.100.E",
            0x53 => "8PSK.500.100.O",
            0x54 => "16QAM.500.100.E",
            0x55 => "16QAM.500.100.O",
            0x60 => "4PSK.1000.100.E",
            0x61 => "4PSK.1000.100.O",
            0x62 => "8PSK.1000.100.E",
            0x63 => "8PSK.1000.100.O",
            0x64 => "16QAM.1000.100.E",
            0x65 => "16QAM.1000.100.O",
            0x70 => "4PSK.2000.100.E",
            0x71 => "4PSK.2000.100.O",
            0x72 => "8PSK.2000.100.E",
            0x73 => "8PSK.2000.100.O",
            0x74 => "16QAM.2000.100.E",
            0x75 => "16QAM.2000.100.O",
            0x7A => "4FSK.2000.600.E",
            0x7B => "4FSK.2000.600.O",
            0x7C => "4FSK.2000.600S.E",
            0x7D => "4FSK.2000.600S.O",
            _ => "",
        };
    }

    /// <summary>True for the payload-free control frames: NAK, BREAK, IDLE, DISC, END,
    /// ConRejBusy, ConRejBW, ACK (<c>IsShortControlFrame</c>, SoundInput.c:340).</summary>
    public static bool IsShortControl(byte type)
    {
        if (type <= DataNakMax)
        {
            return true;
        }

        if (type is Break or Idle or Disc or End or ConRejBusy or ConRejBw)
        {
            return true;
        }

        return type >= DataAckMin;
    }

    /// <summary>True for data frames — the even/odd paired types whose names end
    /// ".E"/".O" (<c>IsDataFrame</c>, SoundInput.c:353).</summary>
    public static bool IsData(byte type)
    {
        string name = Name(type);
        return name.EndsWith(".E", StringComparison.Ordinal)
            || name.EndsWith(".O", StringComparison.Ordinal);
    }

    /// <summary>
    /// The 2-bit parity symbol appended after each frame-type byte on the wire: the
    /// XOR-fold of the four dibits of the <i>plain</i> frame type, seeded with 1
    /// (<c>ComputeTypeParity</c>, ARDOPC.c:1640). Both parity symbols — including the
    /// one after the session-XORed byte — are computed from the plain type.
    /// </summary>
    public static byte TypeParity(byte frameType)
    {
        byte mask = 0xC0;
        byte paritySum = 1;

        for (int k = 0; k < 4; k++)
        {
            byte sym = (byte)((mask & frameType) >> (2 * (3 - k)));
            paritySum ^= sym;
            mask >>= 2;
        }

        return (byte)(paritySum & 0x3);
    }

    /// <summary>The candidate set the minimal-distance frame-type decoder searches when
    /// unconnected or monitoring (<c>bytValidFrameTypesALL</c>, ARDOPC.c:226): all NAKs,
    /// the control/special frames, all data frames and all ACKs.</summary>
    public static ReadOnlySpan<byte> ValidTypesAll => ValidTypesAllArray;

    /// <summary>The candidate set searched while the protocol state is ISS
    /// (<c>bytValidFrameTypesISS</c>, ARDOPC.c:248): the frames an ISS can legally
    /// receive — NAKs, BREAK, DISC, END, ConRej, ConReq/ConAck and ACKs, but no IDLE,
    /// ID, Ping or data frames.</summary>
    public static ReadOnlySpan<byte> ValidTypesIss => ValidTypesIssArray;

    private static readonly byte[] ValidTypesAllArray = BuildValidTypesAll();

    private static readonly byte[] ValidTypesIssArray = BuildValidTypesIss();

    private static byte[] BuildValidTypesIss()
    {
        var types = new List<byte>();
        for (byte t = DataNakMin; t <= DataNakMax; t++)
        {
            types.Add(t);
        }

        types.AddRange([Break, Disc, End, ConRejBusy, ConRejBw]);
        for (byte t = ConReqMin; t <= ConReqMax; t++)
        {
            types.Add(t);
        }

        types.AddRange([ConAck200, ConAck500, ConAck1000, ConAck2000]);
        for (int t = DataAckMin; t <= 0xFF; t++)
        {
            types.Add((byte)t);
        }

        return [.. types];
    }

    private static byte[] BuildValidTypesAll()
    {
        var types = new List<byte>();
        for (byte t = DataNakMin; t <= DataNakMax; t++)
        {
            types.Add(t);
        }

        types.AddRange([Break, Idle, Disc, End, ConRejBusy, ConRejBw, IdFrame]);
        for (byte t = ConReqMin; t <= ConReqMax; t++)
        {
            types.Add(t);
        }

        types.AddRange([ConAck200, ConAck500, ConAck1000, ConAck2000, PingAck, Ping]);
        for (byte t = 0x40; t <= 0x7D; t++)
        {
            if (IsData(t))
            {
                types.Add(t);
            }
        }

        for (int t = DataAckMin; t <= 0xFF; t++)
        {
            types.Add((byte)t);
        }

        return [.. types];
    }
}
