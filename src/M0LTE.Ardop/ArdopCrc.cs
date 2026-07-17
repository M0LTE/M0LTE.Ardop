using System.Text;

namespace M0LTE.Ardop;

/// <summary>
/// The ARDOP CRCs, ported verbatim from ardopcf (git a7c9228, MIT, © 2014-2024
/// Rick Muething, John Wiseman, Peter LaRue): the 16-bit frame CRC
/// (<c>GenCRC16</c>, ARDOPC.c:1673) and the 8-bit session-ID CRC (<c>GenCRC8</c>,
/// ARQ.c:200). See PROVENANCE.md and docs/ardop-design.md §3.1.
/// </summary>
/// <remarks>
/// <para>
/// The spec (App. B) describes the frame CRC as "x^16 + x^12 + x^5 + 1", but the shipped
/// formulation is <b>not</b> table-standard CRC-16/CCITT-FALSE: the data bit is shifted
/// into the LSB <i>before</i> the polynomial XOR and the polynomial constant is
/// <c>0x8810</c>, not <c>0x1021</c>. Code wins over spec here (docs/ardop-design.md §9.3) —
/// this is the operative wire format of every deployed ARDOP station. Neither
/// <c>Fec/Crc16X25</c> nor <c>Fec/FreedvCrc16</c> matches it.
/// </para>
/// <para>
/// On the wire the CRC's low byte is XORed with the frame type
/// (<c>GenCRC16FrameType</c>, ARDOPC.c:1722), binding each carrier block to the frame
/// type that carried it.
/// </para>
/// </remarks>
public static class ArdopCrc
{
    /// <summary>Computes the 16-bit ARDOP frame CRC over <paramref name="data"/>
    /// (poly constant 0x8810, init 0xFFFF, MSB-first, LSB data injection).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int register = 0xFFFF;
        const int Poly = 0x8810;

        foreach (byte value in data)
        {
            int mask = 0x80;
            for (int i = 0; i < 8; i++)
            {
                int bit = value & mask;
                mask >>= 1;

                // Shift left bringing the data bit onto the LSB, then divide by the
                // polynomial if the register's MSB was set — the LSB-injection order
                // that makes this variant nonstandard.
                if ((register & 0x8000) != 0)
                {
                    register = 0xFFFF & ((register << 1) + (bit != 0 ? 1 : 0));
                    register ^= Poly;
                }
                else
                {
                    register = 0xFFFF & ((register << 1) + (bit != 0 ? 1 : 0));
                }
            }
        }

        return (ushort)register;
    }

    /// <summary>Appends the frame CRC to <paramref name="buffer"/>: the two bytes at
    /// [<paramref name="length"/>] and [<paramref name="length"/> + 1] become the CRC's
    /// high byte and its low byte XORed with <paramref name="frameType"/>
    /// (<c>GenCRC16FrameType</c>, ARDOPC.c:1722).</summary>
    public static void AppendCrc16(Span<byte> buffer, int length, byte frameType)
    {
        ushort crc = Crc16(buffer[..length]);
        buffer[length] = (byte)(crc >> 8);
        buffer[length + 1] = (byte)((crc & 0xFF) ^ frameType);
    }

    /// <summary>Checks the frame CRC stored at [<paramref name="length"/>] and
    /// [<paramref name="length"/> + 1] of <paramref name="data"/>
    /// (<c>CheckCRC16FrameType</c>, ARDOPC.c:1737).</summary>
    public static bool CheckCrc16(ReadOnlySpan<byte> data, int length, byte frameType)
    {
        ushort crc = Crc16(data[..length]);
        return (crc >> 8) == data[length] && ((crc & 0xFF) ^ frameType) == data[length + 1];
    }

    /// <summary>Computes the 8-bit session-ID CRC (poly constant 0xC6, init 0xFF,
    /// MSB-first with LSB data injection; <c>GenCRC8</c>, ARQ.c:200).</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        const int Poly = 0xC6;
        int register = 0xFF;

        foreach (byte value in data)
        {
            int val = value;
            for (int i = 7; i >= 0; i--)
            {
                bool bit = (val & 0x80) != 0;
                val <<= 1;

                if ((register & 0x80) == 0x80)
                {
                    register = 0xFF & (2 * register + (bit ? 1 : 0));
                    register ^= Poly;
                }
                else
                {
                    register = 0xFF & (2 * register + (bit ? 1 : 0));
                }
            }
        }

        return (byte)register;
    }

    /// <summary>
    /// Derives the ARQ session ID from the caller and target callsign strings
    /// (their canonical <c>CALL</c> / <c>CALL-SSID</c> forms concatenated, CRC-8;
    /// <c>GenerateSessionID</c>, ARQ.c:507). A result of 0xFF is remapped to 0 —
    /// 0xFF is reserved for unconnected/FEC frames.
    /// </summary>
    public static byte SessionId(string callerCallsign, string targetCallsign)
    {
        byte id = Crc8(Encoding.ASCII.GetBytes(callerCallsign + targetCallsign));
        return id == 0xFF ? (byte)0 : id;
    }
}
