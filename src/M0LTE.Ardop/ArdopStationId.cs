namespace M0LTE.Ardop;

/// <summary>
/// An ARDOP station identity — a 2-7 character callsign plus optional SSID — and its
/// 6-byte SIXBIT wire form (callsign left-justified in 7 characters, the 8th character
/// carrying the packed SSID). Ported from ardopcf's <c>StationId.c</c> (git a7c9228,
/// MIT, © 2014-2024 Rick Muething, John Wiseman, Peter LaRue).
/// </summary>
/// <remarks>
/// SSID wire mapping (<c>stationid_ssid_pack</c>, StationId.c:189): numeric SSIDs 0-9
/// pack as '0'-'9', letters A-Z as 'A'-'Z', and numeric 10-15 as ':'-'?'. SSID 0 is
/// the implicit default and renders without a "-0" suffix.
/// </remarks>
public sealed class ArdopStationId
{
    private ArdopStationId(string call, char ssidByte)
    {
        Call = call;
        SsidChar = ssidByte;
    }

    /// <summary>The bare callsign (no SSID), uppercase, 2-7 characters.</summary>
    public string Call { get; }

    /// <summary>The packed single-character SSID ('0'-'9', ':'-'?', 'A'-'Z').</summary>
    public char SsidChar { get; }

    /// <summary>Canonical display form: <c>CALL</c> or <c>CALL-SSID</c>.</summary>
    public override string ToString()
    {
        if (SsidChar == '0')
        {
            return Call;
        }

        return SsidChar is >= '0' and <= '?'
            ? $"{Call}-{SsidChar - '0'}"
            : $"{Call}-{SsidChar}";
    }

    /// <summary>Parses <c>CALL</c> or <c>CALL-SSID</c> (SSID 0-15 or A-Z).
    /// Returns false for invalid callsigns or SSIDs.</summary>
    public static bool TryParse(string text, out ArdopStationId stationId)
    {
        stationId = null!;
        ArgumentNullException.ThrowIfNull(text);

        string call;
        string ssid;
        int dash = text.IndexOf('-');
        if (dash < 0)
        {
            call = text;
            ssid = "0";
        }
        else
        {
            call = text[..dash];
            ssid = text[(dash + 1)..];
        }

        call = call.ToUpperInvariant();
        if (call.Length is < 2 or > 7 || call.Contains(' '))
        {
            return false;
        }

        char ssidChar;
        ssid = ssid.ToUpperInvariant();
        if (ssid.Length == 1 && ssid[0] is >= 'A' and <= 'Z')
        {
            ssidChar = ssid[0];
        }
        else if (int.TryParse(ssid, out int n) && n is >= 0 and <= 15)
        {
            ssidChar = n <= 9 ? (char)('0' + n) : (char)(':' + (n - 10));
        }
        else
        {
            return false;
        }

        // Round-trip through the SIXBIT alphabet to reject unpackable characters.
        Span<byte> probe = stackalloc byte[ArdopPacked6.Size];
        if (!ArdopPacked6.Pack(call.PadRight(7) + ssidChar, probe))
        {
            return false;
        }

        stationId = new ArdopStationId(call, ssidChar);
        return true;
    }

    /// <summary>Writes the 6-byte wire form (<c>stationid_to_buffer</c>).</summary>
    public void ToBytes(Span<byte> destination)
    {
        ArdopPacked6.Pack(Call.PadRight(7) + SsidChar, destination);
    }

    /// <summary>Reads the 6-byte wire form back to a station ID
    /// (<c>stationid_from_bytes</c>); false if the unpacked callsign or SSID is
    /// invalid.</summary>
    public static bool TryFromBytes(ReadOnlySpan<byte> wire, out ArdopStationId stationId)
    {
        stationId = null!;
        string text = ArdopPacked6.Unpack(wire);
        string call = text[..7].TrimEnd(' ');
        char ssidChar = text[7];

        if (call.Length is < 2 or > 7 || call.Contains(' '))
        {
            return false;
        }

        if (ssidChar is not ((>= '0' and <= '?') or (>= 'A' and <= 'Z')))
        {
            return false;
        }

        stationId = new ArdopStationId(call, ssidChar);
        return true;
    }
}
