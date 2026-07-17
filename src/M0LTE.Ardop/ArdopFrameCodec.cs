using M0LTE.Fec;

namespace M0LTE.Ardop;

/// <summary>
/// Byte-level codec for the ARDOP 4FSK frame family: builds the "encoded bytes" a
/// modulator plays ([frame type, type ⊕ session ID, per-carrier blocks…]) and decodes
/// the raw demodulated blocks back to payloads. Ported from ardopcf (git a7c9228, MIT,
/// © 2014-2024 Rick Muething, John Wiseman, Peter LaRue): <c>EncodeFSKData</c>
/// ARDOPC.c:1199, <c>EncodeARQConRequest/EncodePing/Encode4FSKIDFrame</c> :1337-1462,
/// <c>Encode4FSKControl/EncodeConACKwTiming/EncodePingAck/EncodeDATAACK/EncodeDATANAK</c>
/// :1464-1570, and the decode counterparts in SoundInput.c. Matches ARDOP spec App. B.
/// See PROVENANCE.md and docs/ardop-design.md §3.1/§3.3.
/// </summary>
/// <remarks>
/// <para>
/// Reed-Solomon parity reuses <see cref="ReedSolomon"/> in its FX.25 configuration —
/// GF(2^8) poly 0x11D, generator roots α¹…α^parity, exactly ardopcf's
/// <c>lib/rockliff/rrs.c</c> field and generator — but the Rockliff <i>wire layout</i>
/// is the byte reversal of the FX.25 one: <c>rs_append</c> places the first wire byte
/// at the <i>lowest</i> codeword degree and clocks the LFSR through the shortened
/// code's zero padding after the data (rrs.c:530,229). Equivalently: parity =
/// reverse(FX.25-encode(reverse(data) zero-padded to k)), and a wire block corrects as
/// the FX.25 decode of its byte reversal. <see cref="RsAppend"/>/<see cref="RsCorrect"/>
/// implement that mapping, validated byte-exact against rrs.c vectors in
/// <c>samples/ardop/ardop-reference-vectors.txt</c>.
/// </para>
/// <para>
/// Each data-frame carrier block is <c>[1 count byte][data, zero-padded][CRC16]
/// [RS parity]</c>; the CRC covers count + data only and its low byte is XORed with
/// the frame type. The 12-byte ID/ConReq/Ping payloads carry RS parity but no count
/// or CRC.
/// </para>
/// </remarks>
public static class ArdopFrameCodec
{
    private static readonly ReedSolomon Rs4 = new(4, firstConsecutiveRoot: 1);
    private static readonly ReedSolomon Rs8 = new(8, firstConsecutiveRoot: 1);
    private static readonly ReedSolomon Rs16 = new(16, firstConsecutiveRoot: 1);
    private static readonly ReedSolomon Rs50 = new(50, firstConsecutiveRoot: 1);

    private static ReedSolomon Rs(int parity) => parity switch
    {
        4 => Rs4,
        8 => Rs8,
        16 => Rs16,
        50 => Rs50,
        _ => new ReedSolomon(parity, firstConsecutiveRoot: 1),
    };

    /// <summary>Computes Rockliff-layout RS parity over
    /// <c>block[..dataLength]</c> into <c>block[dataLength..]</c>
    /// (<c>rs_append</c>, rrs.c:530 — see the class remarks for the mapping).</summary>
    internal static void RsAppend(Span<byte> block, int dataLength, int rsLength)
    {
        // Reversed data, zero-padded to the full k = 255 - rsLength message: the
        // trailing zeros reproduce rs_append clocking the LFSR through the pad.
        var message = new byte[255 - rsLength];
        for (int i = 0; i < dataLength; i++)
        {
            message[i] = block[dataLength - 1 - i];
        }

        Span<byte> parity = stackalloc byte[rsLength];
        Rs(rsLength).Encode(message, parity);
        for (int j = 0; j < rsLength; j++)
        {
            block[dataLength + j] = parity[rsLength - 1 - j];
        }
    }

    /// <summary>Corrects a Rockliff-layout wire block (data + parity) in place;
    /// returns corrections made, or -1 if uncorrectable (<c>rs_correct</c>,
    /// rrs.c:570). Errors the reversal maps outside the shortened codeword —
    /// Rockliff's "non-zero padding" rejection — decode as failures.</summary>
    internal static int RsCorrect(Span<byte> block, int rsLength)
    {
        var reversed = new byte[block.Length];
        for (int i = 0; i < block.Length; i++)
        {
            reversed[i] = block[block.Length - 1 - i];
        }

        int corrections = Rs(rsLength).Decode(reversed);
        if (corrections > 0)
        {
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = reversed[block.Length - 1 - i];
            }
        }

        return corrections;
    }

    /// <summary>
    /// Encodes a data frame of any modulation family (<c>EncodeFSKData</c>,
    /// ARDOPC.c:1199; <c>EncodePSKData</c>, :1106 — identical per-block layout).
    /// <paramref name="data"/> may be shorter than the frame capacity (the count byte
    /// carries the true length; unused bytes are zero-filled) but not longer. One block
    /// per carrier for the PSK/16QAM multi-carrier modes; the 600 Bd long frame
    /// (0x7A/0x7B) is built as three sequential 200-data + 50-RS blocks.
    /// </summary>
    public static byte[] EncodeDataFrame(byte type, ReadOnlySpan<byte> data, byte sessionId)
    {
        var info = ArdopFrameInfo.Get(type);
        if (!ArdopFrameType.IsData(type) || info.DataLength == 0)
        {
            throw new ArgumentException($"0x{type:X2} ({info.Name}) is not a data frame", nameof(type));
        }

        int capacity = info.DataLength * info.CarrierCount;
        if (data.Length == 0 || data.Length > capacity)
        {
            throw new ArgumentException(
                $"payload must be 1..{capacity} bytes for {info.Name}", nameof(data));
        }

        // The 600 Bd long frame splits its 600-byte field into three RS blocks; every
        // other data frame is one block per carrier.
        (int blocks, int blockDataLen, int blockRsLen) = type is 0x7A or 0x7B
            ? (3, info.DataLength / 3, info.RsLength / 3)
            : (info.CarrierCount, info.DataLength, info.RsLength);

        var encoded = new byte[2 + blocks * (blockDataLen + 3 + blockRsLen)];
        encoded[0] = type;
        encoded[1] = (byte)(type ^ sessionId);

        int sourcePtr = 0;
        int writePtr = 2;
        for (int i = 0; i < blocks; i++)
        {
            var block = encoded.AsSpan(writePtr, blockDataLen + 3 + blockRsLen);
            int carDataCount = Math.Min(data.Length - sourcePtr, blockDataLen);
            block[0] = (byte)carDataCount; // may be 0 when the payload ran out
            if (carDataCount > 0)
            {
                data.Slice(sourcePtr, carDataCount).CopyTo(block[1..]);
                sourcePtr += carDataCount;
            }

            ArdopCrc.AppendCrc16(block, blockDataLen + 1, type);
            RsAppend(block, blockDataLen + 3, blockRsLen);
            writePtr += block.Length;
        }

        return encoded;
    }

    /// <summary>Encodes a payload-free control frame — BREAK, IDLE, DISC, END,
    /// ConRejBusy, ConRejBW (<c>Encode4FSKControl</c>, ARDOPC.c:1464).</summary>
    public static byte[] EncodeControl(byte type, byte sessionId) =>
        [type, (byte)(type ^ sessionId)];

    /// <summary>Encodes a DataACK carrying 5-bit scaled quality
    /// (<c>EncodeDATAACK</c>, ARDOPC.c:1532).</summary>
    public static byte[] EncodeDataAck(int quality, byte sessionId)
    {
        byte type = (byte)(ArdopFrameType.DataAckMin + ScaleQuality(quality));
        return [type, (byte)(type ^ sessionId)];
    }

    /// <summary>Encodes a DataNAK carrying 5-bit scaled quality
    /// (<c>EncodeDATANAK</c>, ARDOPC.c:1551).</summary>
    public static byte[] EncodeDataNak(int quality, byte sessionId)
    {
        byte type = (byte)ScaleQuality(quality);
        return [type, (byte)(type ^ sessionId)];
    }

    /// <summary>The quality carried by a received ACK/NAK type code: 38-100 in steps
    /// of 2 (<c>DecodeACKNAK</c>, SoundInput.c:3340).</summary>
    public static int AckNakQuality(byte type) => 38 + 2 * (type & 0x1F);

    /// <summary>Encodes a ConAck with the measured received-leader length in ms,
    /// stored as one byte of 10 ms units repeated three times
    /// (<c>EncodeConACKwTiming</c>, ARDOPC.c:1481).</summary>
    public static byte[] EncodeConAck(byte type, int receivedLeaderMs, byte sessionId)
    {
        if (type is < ArdopFrameType.ConAck200 or > ArdopFrameType.ConAck2000)
        {
            throw new ArgumentException($"0x{type:X2} is not a ConAck frame type", nameof(type));
        }

        byte timing = receivedLeaderMs is > 2550 or < 0
            ? (byte)0
            : (byte)Math.Min(255, receivedLeaderMs / 10);
        return [type, (byte)(type ^ sessionId), timing, timing, timing];
    }

    /// <summary>Decodes a ConAck payload (3 timing bytes) by 2-of-3 majority; null when
    /// all three disagree (<c>Decode4FSKConACK</c>, SoundInput.c:3059).</summary>
    public static int? DecodeConAck(ReadOnlySpan<byte> payload)
    {
        if (payload[0] == payload[1] || payload[0] == payload[2])
        {
            return 10 * payload[0];
        }

        return payload[1] == payload[2] ? 10 * payload[1] : null;
    }

    /// <summary>Encodes a PingAck carrying S:N (−10…+21 dB, 5 bits) and quality
    /// (30-100, 3 bits), the byte repeated three times
    /// (<c>EncodePingAck</c>, ARDOPC.c:1510). Session ID is always 0xFF.</summary>
    public static byte[] EncodePingAck(int snDb, int quality)
    {
        byte value = snDb >= 21
            ? (byte)0xF8
            : (byte)(((snDb + 10) & 0x1F) << 3);
        value += (byte)(Math.Max(0, (quality - 30) / 10) & 7);
        return [ArdopFrameType.PingAck, ArdopFrameType.PingAck ^ 0xFF, value, value, value];
    }

    /// <summary>Decodes a PingAck payload by 2-of-3 majority to (S:N dB, quality);
    /// null when all three copies disagree
    /// (<c>Decode4FSKPingACK</c>, SoundInput.c:3096).</summary>
    public static (int SnDb, int Quality)? DecodePingAck(ReadOnlySpan<byte> payload)
    {
        int ack;
        if (payload[0] == payload[1] || payload[0] == payload[2])
        {
            ack = payload[0];
        }
        else if (payload[1] == payload[2])
        {
            ack = payload[1];
        }
        else
        {
            return null;
        }

        return (((ack & 0xF8) >> 3) - 10, (ack & 7) * 10 + 30);
    }

    /// <summary>Encodes an ID frame: callsign + grid square as two Packed6 fields with
    /// 4 RS bytes; session ID is always 0xFF
    /// (<c>Encode4FSKIDFrame</c>, ARDOPC.c:1440).</summary>
    public static byte[] EncodeIdFrame(ArdopStationId callsign, string gridSquare)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(gridSquare);

        var encoded = new byte[18];
        encoded[0] = ArdopFrameType.IdFrame;
        encoded[1] = ArdopFrameType.IdFrame ^ 0xFF;
        callsign.ToBytes(encoded.AsSpan(2, 6));
        ArdopPacked6.Pack(gridSquare, encoded.AsSpan(8, 6));
        RsAppend(encoded.AsSpan(2, 16), 12, 4);
        return encoded;
    }

    /// <summary>Encodes a connect request: caller + target callsigns with 4 RS bytes;
    /// session ID is always 0xFF (<c>EncodeARQConRequest</c>, ARDOPC.c:1337).</summary>
    public static byte[] EncodeConReq(byte type, ArdopStationId caller, ArdopStationId target)
    {
        if (type is < ArdopFrameType.ConReqMin or > ArdopFrameType.ConReqMax)
        {
            throw new ArgumentException($"0x{type:X2} is not a ConReq frame type", nameof(type));
        }

        return EncodeTwoStationFrame(type, caller, target);
    }

    /// <summary>Encodes a Ping: caller + target callsigns with 4 RS bytes; session ID
    /// is always 0xFF (<c>EncodePing</c>, ARDOPC.c:1395).</summary>
    public static byte[] EncodePing(ArdopStationId caller, ArdopStationId target) =>
        EncodeTwoStationFrame(ArdopFrameType.Ping, caller, target);

    private static byte[] EncodeTwoStationFrame(byte type, ArdopStationId caller, ArdopStationId target)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);

        var encoded = new byte[18];
        encoded[0] = type;
        encoded[1] = (byte)(type ^ 0xFF);
        caller.ToBytes(encoded.AsSpan(2, 6));
        target.ToBytes(encoded.AsSpan(8, 6));
        RsAppend(encoded.AsSpan(2, 16), 12, 4);
        return encoded;
    }

    /// <summary>
    /// RS-corrects a raw 16-byte ID/ConReq/Ping block in place and unpacks its two
    /// Packed6 fields. For ID frames the second field is the grid square (returned via
    /// <paramref name="second"/> as text with <paramref name="secondStation"/> null);
    /// for ConReq/Ping it is the target station. Returns false when RS correction
    /// fails or a callsign is invalid (<c>Decode4FSKConReq/Decode4FSKID</c>,
    /// SoundInput.c:2857,3131).
    /// </summary>
    public static bool TryDecodeStationBlock(
        Span<byte> rawBlock,
        bool secondFieldIsGrid,
        out ArdopStationId first,
        out ArdopStationId? secondStation,
        out string second)
    {
        first = null!;
        secondStation = null;
        second = "";

        if (rawBlock.Length != 16)
        {
            throw new ArgumentException("ID/ConReq/Ping blocks are 12 data + 4 RS bytes", nameof(rawBlock));
        }

        // Without a CRC, RS syndrome success is the only integrity check these frames
        // carry; callsign validity is the backstop (as in ardopcf).
        bool ok = RsCorrect(rawBlock, 4) >= 0;
        ok &= ArdopStationId.TryFromBytes(rawBlock[..6], out first);

        if (secondFieldIsGrid)
        {
            second = ArdopPacked6.Unpack(rawBlock[6..12]).TrimEnd(' ');
        }
        else
        {
            ok &= ArdopStationId.TryFromBytes(rawBlock[6..12], out var target);
            secondStation = target;
            second = ok ? target.ToString() : "";
        }

        return ok;
    }

    /// <summary>
    /// RS-corrects one raw data-frame carrier block in place and validates count + CRC
    /// (the decode half of <c>CorrectRawDataWithRS</c>, SoundInput.c:692). On success
    /// <paramref name="payloadLength"/> is the count byte's value and the payload sits
    /// at <c>rawBlock[1..1+payloadLength]</c>.
    /// </summary>
    public static bool TryCorrectDataBlock(Span<byte> rawBlock, int dataLength, int rsLength, byte frameType, out int payloadLength) =>
        TryCorrectDataBlock(rawBlock, dataLength, rsLength, frameType, out payloadLength, out _);

    /// <summary>
    /// As <see cref="TryCorrectDataBlock(Span{byte}, int, int, byte, out int)"/>, also
    /// reporting the number of RS byte corrections made on success
    /// (<c>totalRSErrors</c> accounting — feeds the received-quality floor,
    /// SoundInput.c:3801).
    /// </summary>
    public static bool TryCorrectDataBlock(
        Span<byte> rawBlock, int dataLength, int rsLength, byte frameType, out int payloadLength, out int rsCorrections)
    {
        payloadLength = 0;
        rsCorrections = 0;
        if (rawBlock.Length != dataLength + 3 + rsLength)
        {
            throw new ArgumentException("block must be count + data + CRC16 + RS", nameof(rawBlock));
        }

        // Always run RS before the CRC check: ardopcf saw a corrupted block pass the
        // CRC yet be repairable by RS, so the CRC verdict is only trusted on the
        // RS-corrected bytes (comment at SoundInput.c:724).
        int corrections = RsCorrect(rawBlock, rsLength);
        if (corrections < 0)
        {
            return false;
        }

        if (rawBlock[0] > dataLength || !ArdopCrc.CheckCrc16(rawBlock, dataLength + 1, frameType))
        {
            return false;
        }

        payloadLength = rawBlock[0];
        rsCorrections = corrections;
        return true;
    }

    private static int ScaleQuality(int quality)
    {
        // 5-bit field where 0 represents Q <= 38 (ARDOPC.c:1540).
        quality = Math.Min(quality, 100);
        return Math.Max(0, quality / 2 - 19);
    }
}
