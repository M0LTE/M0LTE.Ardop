namespace M0LTE.Ardop;

/// <summary>
/// Connectionless FEC-mode transmit sequencing (<c>StartFEC</c>/<c>GetNextFECFrame</c>,
/// ardopcf FEC.c:45-330, git a7c9228, MIT, © 2014-2024 Rick Muething, John Wiseman,
/// Peter LaRue; ARDOP spec App. D FEC rules): the payload is cut into frames of the
/// chosen mode's capacity, each frame is transmitted 1 + <c>repeats</c> times, and each
/// <i>new</i> frame toggles the even/odd frame type so receivers can distinguish new
/// data from repeats. FEC frames always use session ID 0xFF.
/// </summary>
public static class ArdopFecSender
{
    /// <summary>
    /// Builds the encoded frames for one FEC transmission in transmit order (feed each
    /// to <see cref="ArdopModulator.Modulate"/>). <paramref name="evenFrameType"/> is
    /// the mode's even type code (e.g. 0x4A for 4FSK.500.100);
    /// <paramref name="repeats"/> is the FECREPEATS count 0-5.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildFrames(byte evenFrameType, ReadOnlySpan<byte> data, int repeats)
    {
        if ((evenFrameType & 1) != 0 || !ArdopFrameType.IsData(evenFrameType))
        {
            throw new ArgumentException(
                $"0x{evenFrameType:X2} is not an even data frame type", nameof(evenFrameType));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(repeats, 5);
        if (data.IsEmpty)
        {
            throw new ArgumentException("no data to send", nameof(data));
        }

        var info = ArdopFrameInfo.Get(evenFrameType);
        int capacity = info.DataLength * info.CarrierCount;

        var frames = new List<byte[]>();
        byte type = evenFrameType;
        int offset = 0;
        while (offset < data.Length)
        {
            int take = Math.Min(capacity, data.Length - offset);
            byte[] encoded = ArdopFrameCodec.EncodeDataFrame(type, data.Slice(offset, take), 0xFF);
            for (int i = 0; i <= repeats; i++)
            {
                frames.Add(encoded);
            }

            offset += take;
            type ^= 1; // next new frame toggles even/odd
        }

        return frames;
    }
}
