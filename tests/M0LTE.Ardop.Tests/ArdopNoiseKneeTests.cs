using System.Diagnostics;
using M0LTE.Ardop;
using Xunit.Abstractions;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Noise-knee characterisation, ours vs ardopcf (docs/ardop-design.md §7.1 —
/// "INPUTNOISE SNR sweeps against ardopcf's own decode rate on identical WAVs"): for
/// each PSK/16QAM mode class the same noisy audio — an ardopcf oracle fixture plus
/// seeded Gaussian noise — is decoded by our demodulator and by <c>ardopcf
/// --decodewav</c>, one file per ardopcf invocation (a shared instance would
/// Memory-ARQ-average the repeats and inflate its single-copy rate). The 50%-decode
/// knee of our receiver must sit within 1 dB of ardopcf's. Requires <c>ARDOPCF</c>;
/// skipped otherwise so CI stays hermetic.
/// </summary>
/// <remarks>
/// SNR here is full-band: 20·log10(RMS of the fixture waveform / RMS of the added
/// noise) at 12 kHz — a label for the shared axis, not a claim about a 3 kHz
/// channel. Run results as of 2026-07-16 on the dev box: decode counts were
/// <b>identical, trial-for-trial,</b> between the two receivers at every swept
/// (mode, SNR) point — knee delta 0 dB for all six classes (4PSK/8PSK/16QAM ×
/// 200/1000 Hz, plus 16QAM.2000).
/// </remarks>
public class ArdopNoiseKneeTests(ITestOutputHelper output)
{
    private const int Trials = 6;

    private static string? ArdopcfBinary()
    {
        string? path = Environment.GetEnvironmentVariable("ARDOPCF");
        return path is not null && File.Exists(path) ? path : null;
    }

    [SkippableTheory]
    [InlineData("txframe_4PSK.200.100.E.wav", "4PSK.200.100.E", 4, -2)]
    [InlineData("txframe_8PSK.200.100.E.wav", "8PSK.200.100.E", 6, -2)]
    [InlineData("txframe_16QAM.200.100.E.wav", "16QAM.200.100.E", 8, 0)]
    [InlineData("txframe_4PSK.1000.100.E.wav", "4PSK.1000.100.E", 8, 0)]
    [InlineData("txframe_8PSK.1000.100.E.wav", "8PSK.1000.100.E", 16, 8)]
    [InlineData("txframe_16QAM.1000.100.E.wav", "16QAM.1000.100.E", 18, 10)]
    [InlineData("txframe_16QAM.2000.100.E.wav", "16QAM.2000.100.E", 20, 12)]
    public void Our_Decode_Knee_Is_Within_1dB_Of_Ardopcf(string wavName, string name, int snrHigh, int snrLow)
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the noise-knee sweep");

        string samplesDir = ArdopReferenceVectorTests.SamplesDir();
        string manifestLine = File.ReadAllLines(Path.Combine(samplesDir, "txframe-manifest.txt"))
            .Single(line => line.StartsWith(wavName + " ", StringComparison.Ordinal));
        byte[] expected = Convert.FromHexString(manifestLine.Split(' ')[3]);

        (float[] audio, int rate) = WavFile.ReadMono(Path.Combine(samplesDir, wavName));
        rate.Should().Be(12000);
        var clean = new short[audio.Length];
        double sumSquares = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            clean[i] = (short)Math.Clamp(audio[i] * 32768f, short.MinValue, short.MaxValue);
            sumSquares += (double)clean[i] * clean[i];
        }

        double signalRms = Math.Sqrt(sumSquares / clean.Length);

        string tempDir = Path.Combine(Path.GetTempPath(), $"pdn-ardop-knee-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var rows = new List<(int SnrDb, int Ours, int Ardopcf)>();
            for (int snr = snrHigh; snr >= snrLow; snr -= 2)
            {
                double noiseRms = signalRms / Math.Pow(10, snr / 20.0);
                int oursOk = 0, ardopcfOk = 0;
                for (int trial = 0; trial < Trials; trial++)
                {
                    short[] noisy = NoisyTrial(clean, noiseRms, seed: snr * 100 + trial);

                    if (DecodesInOurs(noisy, wavName, expected))
                    {
                        oursOk++;
                    }

                    if (DecodesInArdopcf(binary!, tempDir, noisy, name))
                    {
                        ardopcfOk++;
                    }
                }

                rows.Add((snr, oursOk, ardopcfOk));
            }

            output.WriteLine($"{name}  (signal RMS {signalRms:F0}, {Trials} trials/point, full-band SNR)");
            output.WriteLine("  SNR dB | ours | ardopcf");
            foreach (var (snr, ours, cf) in rows)
            {
                output.WriteLine($"  {snr,6} | {ours}/{Trials}  | {cf}/{Trials}");
            }

            int ourKnee = Knee(rows, r => r.Ours);
            int cfKnee = Knee(rows, r => r.Ardopcf);
            output.WriteLine($"  knee: ours {Render(ourKnee, snrLow)}, ardopcf {Render(cfKnee, snrLow)}");

            cfKnee.Should().BeLessThan(int.MaxValue,
                "the sweep window must reach ardopcf's knee — widen it if this fires");
            ourKnee.Should().BeLessThanOrEqualTo(cfKnee + 1,
                "our 50% decode knee must be within 1 dB of ardopcf's (or better)");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string Render(int knee, int snrLow) =>
        knee == int.MaxValue ? "above window" : knee <= snrLow ? $"<= {knee} dB" : $"{knee} dB";

    // The knee: the lowest swept SNR at which this receiver still decodes at least
    // half the trials, with every higher swept point also at least half (so a lucky
    // deep-noise decode doesn't count).
    private static int Knee(List<(int SnrDb, int Ours, int Ardopcf)> rows, Func<(int SnrDb, int Ours, int Ardopcf), int> select)
    {
        int knee = int.MaxValue;
        foreach (var row in rows)  // rows run high SNR → low
        {
            if (2 * select(row) >= Trials)
            {
                knee = row.SnrDb;
            }
            else
            {
                break;
            }
        }

        return knee;
    }

    private static short[] NoisyTrial(short[] clean, double noiseRms, int seed)
    {
        var random = new Random(seed);
        var noisy = new short[2400 + clean.Length + 4800];
        for (int i = 0; i < noisy.Length; i++)
        {
            double gauss = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()))
                * Math.Cos(2 * Math.PI * random.NextDouble());
            int signal = i >= 2400 && i < 2400 + clean.Length ? clean[i - 2400] : 0;
            noisy[i] = (short)Math.Clamp(signal + gauss * noiseRms, short.MinValue, short.MaxValue);
        }

        return noisy;
    }

    private static bool DecodesInOurs(short[] noisy, string wavName, byte[] expected)
    {
        var demodulator = new ArdopDemodulator();
        var frames = new List<ArdopDecodedFrame>();
        demodulator.FrameDecoded += frames.Add;
        demodulator.ProcessSamples(noisy);
        demodulator.ProcessSamples(new short[4800]);
        return frames.Count == 1 && frames[0].Ok && frames[0].Data.AsSpan().SequenceEqual(expected);
    }

    private static bool DecodesInArdopcf(string binary, string tempDir, short[] noisy, string name)
    {
        var floats = new float[noisy.Length];
        for (int i = 0; i < noisy.Length; i++)
        {
            floats[i] = noisy[i] / 32768f;
        }

        string wav = Path.Combine(tempDir, "trial.wav");
        WavFile.WriteMono(wav, floats, 12000);

        var psi = new ProcessStartInfo(binary, $"--nologfile --decodewav {wav}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        string result = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60000).Should().BeTrue("ardopcf must finish decoding");
        return result.Contains($"{name} frame received OK", StringComparison.Ordinal);
    }
}
