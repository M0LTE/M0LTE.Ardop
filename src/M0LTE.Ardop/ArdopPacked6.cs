namespace M0LTE.Ardop;

/// <summary>
/// DEC SIXBIT packing of 8-character text fields into 6 bytes, as used by the ARDOP
/// IDFrame, ConReq and Ping payloads (callsign+SSID and grid square). Ported from
/// ardopcf's <c>Packed6.c</c> (git a7c9228, MIT, © 2014-2024 Rick Muething, John
/// Wiseman, Peter LaRue). The packable alphabet is ASCII 32 (space) — 63 (underscore);
/// lowercase letters fold to uppercase; anything else packs as space and reports
/// failure. See docs/ardop-design.md §3.3 (IDFrame row).
/// </summary>
public static class ArdopPacked6
{
    /// <summary>Packed size in bytes.</summary>
    public const int Size = 6;

    /// <summary>Maximum text length.</summary>
    public const int MaxChars = 8;

    /// <summary>
    /// Packs <paramref name="text"/> (right-padded with spaces to 8 characters) into
    /// <paramref name="packed"/> (6 bytes). Returns false if any character was outside
    /// the SIXBIT alphabet (it packs as space) or the text was truncated.
    /// </summary>
    public static bool Pack(string text, Span<byte> packed)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (packed.Length != Size)
        {
            throw new ArgumentException($"packed must be exactly {Size} bytes", nameof(packed));
        }

        bool ok = text.Length <= MaxChars;
        Span<char> work = stackalloc char[MaxChars];
        work.Fill(' ');
        for (int i = 0; i < Math.Min(text.Length, MaxChars); i++)
        {
            work[i] = text[i];
        }

        ok &= CompressFour(work[..4], packed[..3]);
        ok &= CompressFour(work[4..], packed[3..]);
        return ok;
    }

    /// <summary>Unpacks 6 bytes to the 8-character text (space-padded; always
    /// succeeds — every 6-bit code maps to a character).</summary>
    public static string Unpack(ReadOnlySpan<byte> packed)
    {
        if (packed.Length != Size)
        {
            throw new ArgumentException($"packed must be exactly {Size} bytes", nameof(packed));
        }

        Span<char> text = stackalloc char[MaxChars];
        DecompressThree(packed[..3], text[..4]);
        DecompressThree(packed[3..], text[4..]);
        return new string(text);
    }

    private static bool CompressFour(ReadOnlySpan<char> chars, Span<byte> compressed)
    {
        bool ok = true;
        uint pack = 0;

        for (int i = 0; i < 4; i++)
        {
            int b = chars[i];
            if (b is >= ' ' and <= '_')
            {
                b -= ' ';
            }
            else if (b is >= 'a' and <= 'z')
            {
                b = b - ('a' - 'A') - ' ';
            }
            else
            {
                b = 0;
                ok = false;
            }

            pack = (pack << 6) | (uint)(b & 0x3F);
        }

        for (int i = 0; i < 3; i++)
        {
            compressed[2 - i] = (byte)(pack & 0xFF);
            pack >>= 8;
        }

        return ok;
    }

    private static void DecompressThree(ReadOnlySpan<byte> compressed, Span<char> chars)
    {
        uint unpack = 0;
        for (int i = 0; i < 3; i++)
        {
            unpack = (unpack << 8) | compressed[i];
        }

        for (int i = 0; i < 4; i++)
        {
            chars[3 - i] = (char)((unpack & 0x3F) + ' ');
            unpack >>= 6;
        }
    }
}
