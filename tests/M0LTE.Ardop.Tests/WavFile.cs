using System.Buffers.Binary;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Minimal RIFF/WAVE reader and writer for 16-bit PCM — the offline harness for feeding
/// recorded audio through the modems and for writing test corpora. Not a general WAV
/// library: 16-bit PCM only, unknown chunks are skipped on read.
/// </summary>
internal static class WavFile
{
    /// <summary>Reads a 16-bit PCM WAV file as normalised floats (−1..1). Multi-channel
    /// files yield the requested channel (default: the first / left).</summary>
    public static (float[] Samples, int SampleRate) ReadMono(string path, int channel = 0)
    {
        byte[] data = File.ReadAllBytes(path);
        var span = data.AsSpan();
        if (data.Length < 44 || !span[..4].SequenceEqual("RIFF"u8) || !span[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        float[]? samples = null;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = span.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            pos += 8;
            if (pos + size > data.Length)
            {
                break; // truncated chunk — take what we have
            }

            if (id.SequenceEqual("fmt "u8))
            {
                var fmt = span.Slice(pos, size);
                int format = BinaryPrimitives.ReadInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..]);
                if (format != 1 || bitsPerSample != 16)
                {
                    throw new InvalidDataException(
                        $"only 16-bit PCM is supported (format {format}, {bitsPerSample} bits)");
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("data chunk before fmt chunk");
                }

                if (channel >= channels)
                {
                    throw new ArgumentOutOfRangeException(nameof(channel), $"file has {channels} channel(s)");
                }

                int frameBytes = channels * 2;
                int frameCount = size / frameBytes;
                samples = new float[frameCount];
                for (int i = 0; i < frameCount; i++)
                {
                    short value = BinaryPrimitives.ReadInt16LittleEndian(
                        span.Slice(pos + i * frameBytes + channel * 2, 2));
                    samples[i] = value / 32768f;
                }
            }

            pos += size + (size & 1); // chunks are word-aligned
        }

        if (samples is null || sampleRate == 0)
        {
            throw new InvalidDataException("missing fmt or data chunk");
        }

        return (samples, sampleRate);
    }

    /// <summary>Writes mono 16-bit PCM. Samples are clipped to −1..1.</summary>
    public static void WriteMono(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        int dataBytes = samples.Length * 2;
        var buffer = new byte[44 + dataBytes];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);  // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);  // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (int i = 0; i < samples.Length; i++)
        {
            float clipped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(
                span[(44 + i * 2)..], (short)MathF.Round(clipped * 32767f));
        }

        File.WriteAllBytes(path, buffer);
    }
}
