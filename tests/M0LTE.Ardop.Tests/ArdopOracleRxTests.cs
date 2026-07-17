using M0LTE.Ardop;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Rung 1, receive leg: ardopcf's transmit audio must decode payload-exact in our
/// demodulator, for every Phase A frame type. The fixtures in <c>samples/ardop/</c>
/// were produced by ardopcf's null-device TX path (<c>TXFRAME</c> + <c>--writetxwav</c>,
/// git a7c9228; see that folder's PROVENANCE.md); <c>txframe-manifest.txt</c> carries
/// the expected type/payload per file. Clean, frequency-offset and noise-degraded
/// variants (docs/ardop-design.md §6.2, rung 1) — the offset and noise are applied here
/// deterministically rather than checked in as extra WAVs.
/// </summary>
public class ArdopOracleRxTests
{
    private sealed record Fixture(string Wav, byte Type, byte SessionId, string PayloadOrExtra);

    private static readonly Dictionary<string, Fixture> Manifest = LoadManifest();

    private static Dictionary<string, Fixture> LoadManifest()
    {
        string path = Path.Combine(ArdopReferenceVectorTests.SamplesDir(), "txframe-manifest.txt");
        var manifest = new Dictionary<string, Fixture>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(' ');
            manifest[parts[0]] = new Fixture(
                parts[0], Convert.ToByte(parts[1], 16), Convert.ToByte(parts[2], 16), parts[3]);
        }

        return manifest;
    }

    private static List<ArdopDecodedFrame> DecodeWav(string wavName, Func<short[], short[]>? mutate = null)
    {
        (float[] audio, int rate) = WavFile.ReadMono(
            Path.Combine(ArdopReferenceVectorTests.SamplesDir(), wavName));
        rate.Should().Be(12000);

        var samples = new short[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            samples[i] = (short)Math.Clamp(audio[i] * 32768f, short.MinValue, short.MaxValue);
        }

        if (mutate is not null)
        {
            samples = mutate(samples);
        }

        var demodulator = new ArdopDemodulator();
        var frames = new List<ArdopDecodedFrame>();
        demodulator.FrameDecoded += frames.Add;
        demodulator.ProcessSamples(new short[2400]);
        demodulator.ProcessSamples(samples);
        demodulator.ProcessSamples(new short[4800]);
        return frames;
    }

    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string name in LoadManifest().Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_Ardopcf_Fixture_Decodes_With_The_Expected_Type_And_Payload(string wavName)
    {
        var fixture = Manifest[wavName];
        var frames = DecodeWav(wavName);

        frames.Should().ContainSingle("ardopcf's own audio must decode");
        var frame = frames[0];
        frame.Type.Should().Be(fixture.Type);
        frame.Ok.Should().BeTrue();
        AssertExpectations(frame, fixture);
    }

    private static void AssertExpectations(ArdopDecodedFrame frame, Fixture fixture)
    {
        if (fixture.PayloadOrExtra == "-")
        {
            frame.Data.Should().BeEmpty();
            return;
        }

        if (fixture.PayloadOrExtra.StartsWith("timing=", StringComparison.Ordinal))
        {
            frame.ConAckLeaderMs.Should().Be(int.Parse(fixture.PayloadOrExtra[7..]));
            return;
        }

        if (fixture.PayloadOrExtra.StartsWith("sn=", StringComparison.Ordinal))
        {
            string[] kv = fixture.PayloadOrExtra.Split(',');
            frame.PingAckSnDb.Should().Be(int.Parse(kv[0][3..]));
            frame.PingAckQuality.Should().Be(int.Parse(kv[1]["quality=".Length..]));
            return;
        }

        if (fixture.PayloadOrExtra.StartsWith("caller=", StringComparison.Ordinal))
        {
            var kv = fixture.PayloadOrExtra.Split(',')
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => pair[0], pair => pair[1]);
            frame.Caller.Should().Be(kv["caller"]);
            if (kv.TryGetValue("target", out string? target))
            {
                frame.Target.Should().Be(target);
            }

            if (kv.TryGetValue("grid", out string? grid))
            {
                frame.GridSquare.Should().Be(grid.ToUpperInvariant());
            }

            return;
        }

        frame.Data.Should().Equal(Convert.FromHexString(fixture.PayloadOrExtra));
    }

    [Theory]
    [InlineData("txframe_4FSK.200.50S.E.wav", -80.0)]
    [InlineData("txframe_4FSK.200.50S.E.wav", +80.0)]
    [InlineData("txframe_4FSK.500.100.E.wav", -80.0)]
    [InlineData("txframe_4FSK.500.100.E.wav", +80.0)]
    [InlineData("txframe_4FSK.500.100S.O.wav", -40.0)]
    [InlineData("txframe_IDFrame.wav", +40.0)]
    [InlineData("txframe_4PSK.200.100.E.wav", -80.0)]
    [InlineData("txframe_4PSK.200.100.E.wav", +80.0)]
    [InlineData("txframe_4PSK.200.100S.O.wav", -40.0)]
    [InlineData("txframe_8PSK.200.100.E.wav", -80.0)]
    [InlineData("txframe_8PSK.200.100.E.wav", +80.0)]
    [InlineData("txframe_16QAM.200.100.E.wav", -80.0)]
    [InlineData("txframe_16QAM.200.100.E.wav", +80.0)]
    [InlineData("txframe_16QAM.200.100.O.wav", +40.0)]
    [InlineData("txframe_4PSK.500.100.E.wav", -80.0)]
    [InlineData("txframe_8PSK.500.100.E.wav", +80.0)]
    [InlineData("txframe_16QAM.500.100.E.wav", -40.0)]
    [InlineData("txframe_16QAM.500.100.E.wav", +40.0)]
    [InlineData("txframe_4PSK.1000.100.E.wav", -80.0)]
    [InlineData("txframe_8PSK.1000.100.E.wav", +80.0)]
    [InlineData("txframe_16QAM.1000.100.E.wav", -40.0)]
    [InlineData("txframe_4PSK.2000.100.E.wav", +80.0)]
    [InlineData("txframe_8PSK.2000.100.E.wav", -40.0)]
    [InlineData("txframe_16QAM.2000.100.E.wav", +40.0)]
    [InlineData("txframe_16QAM.2000.100.E.wav", -40.0)]
    public void Ardopcf_Audio_Decodes_With_Frequency_Offset(string wavName, double offsetHz)
    {
        var fixture = Manifest[wavName];
        var frames = DecodeWav(wavName, samples => ArdopLoopbackTests.FrequencyShift(samples, offsetHz));

        frames.Should().ContainSingle("the leader search captures ±100 Hz (spec §4.1 asks ±200)");
        frames[0].Type.Should().Be(fixture.Type);
        frames[0].Ok.Should().BeTrue();
        AssertExpectations(frames[0], fixture);
    }

    [Theory]
    [InlineData("txframe_4FSK.200.50S.E.wav", 1500, 11)]
    [InlineData("txframe_4FSK.500.100.E.wav", 1500, 13)]
    [InlineData("txframe_4FSK.500.100S.E.wav", 2000, 17)]
    [InlineData("txframe_BREAK.wav", 2000, 19)]
    [InlineData("txframe_4PSK.200.100.E.wav", 1500, 23)]
    [InlineData("txframe_8PSK.200.100.E.wav", 1500, 29)]
    [InlineData("txframe_16QAM.200.100.E.wav", 1200, 31)]
    [InlineData("txframe_4PSK.500.100.E.wav", 1000, 37)]
    [InlineData("txframe_8PSK.500.100.E.wav", 1000, 41)]
    [InlineData("txframe_16QAM.500.100.E.wav", 800, 43)]
    [InlineData("txframe_4PSK.1000.100.E.wav", 800, 47)]
    [InlineData("txframe_8PSK.1000.100.E.wav", 700, 53)]
    [InlineData("txframe_16QAM.1000.100.E.wav", 600, 59)]
    [InlineData("txframe_4PSK.2000.100.E.wav", 600, 61)]
    [InlineData("txframe_8PSK.2000.100.E.wav", 500, 67)]
    [InlineData("txframe_16QAM.2000.100.E.wav", 400, 71)]
    public void Ardopcf_Audio_Decodes_Through_Additive_Noise(string wavName, int noiseRms, int seed)
    {
        // ardopcf's own INPUTNOISE degradation is Gaussian noise added to input audio;
        // this is the same operation applied deterministically. The TX peak is ~26000,
        // so RMS 1500-2000 is roughly 14-17 dB SNR on the strongest tone — comfortably
        // decodable, still a real perturbation of every DSP stage. The PSK/QAM rows
        // scale the noise down with carrier count (per-carrier power falls as the
        // carrier-count scaling factors shrink) and with constellation density.
        var fixture = Manifest[wavName];
        var random = new Random(seed);
        var frames = DecodeWav(wavName, samples =>
        {
            for (int i = 0; i < samples.Length; i++)
            {
                double gauss = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()))
                    * Math.Cos(2 * Math.PI * random.NextDouble());
                samples[i] = (short)Math.Clamp(samples[i] + gauss * noiseRms, short.MinValue, short.MaxValue);
            }

            return samples;
        });

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(fixture.Type);
        frames[0].Ok.Should().BeTrue();
        AssertExpectations(frames[0], fixture);
    }
}
