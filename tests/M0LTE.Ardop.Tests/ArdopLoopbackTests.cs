using M0LTE.Ardop;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Our ARDOP TX played straight into our RX — the internal-consistency leg. Payload
/// exactness here proves the codec, modulator and demodulator agree with each other;
/// agreement with ardopcf is proved separately by the oracle fixtures
/// (<see cref="ArdopOracleRxTests"/>) and the <c>--decodewav</c> reverse leg.
/// </summary>
public class ArdopLoopbackTests
{
    private static List<ArdopDecodedFrame> Decode(short[] audio, ArdopDemodulator? demod = null)
    {
        demod ??= new ArdopDemodulator();
        var frames = new List<ArdopDecodedFrame>();
        demod.FrameDecoded += frames.Add;

        // Lead-in/out silence: the demodulator needs audio before and after the frame,
        // as a sound card would deliver.
        demod.ProcessSamples(new short[2400]);
        demod.ProcessSamples(audio);
        demod.ProcessSamples(new short[4800]);
        return frames;
    }

    private static byte[] Payload(int length, int seed = 42)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    [Theory]
    [InlineData(0x48, 16)]  // 4FSK.200.50S.E — full frame
    [InlineData(0x49, 7)]   // 4FSK.200.50S.O — partial frame
    [InlineData(0x4A, 64)]  // 4FSK.500.100.E
    [InlineData(0x4B, 33)]  // 4FSK.500.100.O
    [InlineData(0x4C, 32)]  // 4FSK.500.100S.E
    [InlineData(0x4D, 1)]   // 4FSK.500.100S.O — minimum payload
    [InlineData(0x7A, 600)] // 4FSK.2000.600.E — three sequential RS blocks
    [InlineData(0x7B, 450)] // 4FSK.2000.600.O — last block partial
    [InlineData(0x7C, 200)] // 4FSK.2000.600S.E
    [InlineData(0x7D, 128)] // 4FSK.2000.600S.O
    [InlineData(0x40, 64)]   // 4PSK.200.100.E
    [InlineData(0x41, 33)]   // 4PSK.200.100.O — partial frame
    [InlineData(0x42, 16)]   // 4PSK.200.100S.E
    [InlineData(0x43, 1)]    // 4PSK.200.100S.O — minimum payload
    [InlineData(0x44, 108)]  // 8PSK.200.100.E
    [InlineData(0x45, 55)]   // 8PSK.200.100.O
    [InlineData(0x46, 128)]  // 16QAM.200.100.E
    [InlineData(0x47, 64)]   // 16QAM.200.100.O
    [InlineData(0x50, 128)]  // 4PSK.500.100.E — 2 carriers
    [InlineData(0x51, 65)]   // 4PSK.500.100.O — second carrier partial
    [InlineData(0x52, 216)]  // 8PSK.500.100.E
    [InlineData(0x53, 100)]  // 8PSK.500.100.O
    [InlineData(0x54, 256)]  // 16QAM.500.100.E
    [InlineData(0x55, 129)]  // 16QAM.500.100.O
    [InlineData(0x60, 256)]  // 4PSK.1000.100.E — 4 carriers
    [InlineData(0x61, 65)]   // 4PSK.1000.100.O — trailing carriers empty
    [InlineData(0x62, 432)]  // 8PSK.1000.100.E
    [InlineData(0x63, 217)]  // 8PSK.1000.100.O
    [InlineData(0x64, 512)]  // 16QAM.1000.100.E
    [InlineData(0x65, 300)]  // 16QAM.1000.100.O
    [InlineData(0x70, 512)]  // 4PSK.2000.100.E — 8 carriers
    [InlineData(0x71, 300)]  // 4PSK.2000.100.O
    [InlineData(0x72, 864)]  // 8PSK.2000.100.E
    [InlineData(0x73, 500)]  // 8PSK.2000.100.O
    [InlineData(0x74, 1024)] // 16QAM.2000.100.E
    [InlineData(0x75, 700)]  // 16QAM.2000.100.O
    public void Data_Frames_Round_Trip_Payload_Exact(byte type, int payloadLength)
    {
        byte[] payload = Payload(payloadLength);
        byte[] encoded = ArdopFrameCodec.EncodeDataFrame(type, payload, 0xFF);
        short[] audio = new ArdopModulator().Modulate(encoded);

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(type);
        frames[0].Ok.Should().BeTrue();
        frames[0].Data.Should().Equal(payload);
    }

    [Theory]
    [InlineData(ArdopFrameType.Break)]
    [InlineData(ArdopFrameType.Idle)]
    [InlineData(ArdopFrameType.Disc)]
    [InlineData(ArdopFrameType.End)]
    [InlineData(ArdopFrameType.ConRejBusy)]
    [InlineData(ArdopFrameType.ConRejBw)]
    public void Short_Control_Frames_Round_Trip(byte type)
    {
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeControl(type, 0xFF));

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(type);
        frames[0].Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(74)]
    [InlineData(38)]
    public void Ack_And_Nak_Round_Trip_Quality(int quality)
    {
        var modulator = new ArdopModulator();

        var ackFrames = Decode(modulator.Modulate(ArdopFrameCodec.EncodeDataAck(quality, 0xFF)));
        ackFrames.Should().ContainSingle();
        ackFrames[0].Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);
        ArdopFrameCodec.AckNakQuality(ackFrames[0].Type).Should().Be(quality);

        var nakFrames = Decode(modulator.Modulate(ArdopFrameCodec.EncodeDataNak(quality, 0xFF)));
        nakFrames.Should().ContainSingle();
        nakFrames[0].Type.Should().BeLessThanOrEqualTo(ArdopFrameType.DataNakMax);
        ArdopFrameCodec.AckNakQuality(nakFrames[0].Type).Should().Be(quality);
    }

    [Fact]
    public void Id_Frame_Round_Trips_Callsign_And_Grid()
    {
        ArdopStationId.TryParse("M7TFF-3", out var station).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeIdFrame(station, "IO81vk"));

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(ArdopFrameType.IdFrame);
        frames[0].Ok.Should().BeTrue();
        frames[0].Caller.Should().Be("M7TFF-3");
        frames[0].GridSquare.Should().Be("IO81VK"); // SIXBIT folds to uppercase
    }

    [Theory]
    [InlineData(ArdopFrameType.ConReq500M)]
    [InlineData(ArdopFrameType.ConReq2000F)]
    public void Con_Req_Round_Trips_Callsigns(byte type)
    {
        ArdopStationId.TryParse("M7TFF", out var caller).Should().BeTrue();
        ArdopStationId.TryParse("GB7RDG-15", out var target).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeConReq(type, caller, target));

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(type);
        frames[0].Ok.Should().BeTrue();
        frames[0].Caller.Should().Be("M7TFF");
        frames[0].Target.Should().Be("GB7RDG-15");
    }

    [Fact]
    public void Ping_Round_Trips_Callsigns()
    {
        ArdopStationId.TryParse("M7TFF", out var caller).Should().BeTrue();
        ArdopStationId.TryParse("GB7RDG", out var target).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodePing(caller, target));

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(ArdopFrameType.Ping);
        frames[0].Ok.Should().BeTrue();
        frames[0].Caller.Should().Be("M7TFF");
        frames[0].Target.Should().Be("GB7RDG");
    }

    [Theory]
    [InlineData(ArdopFrameType.ConAck500, 320)]
    [InlineData(ArdopFrameType.ConAck2000, 2000)]
    public void Con_Ack_Round_Trips_Leader_Timing(byte type, int leaderMs)
    {
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeConAck(type, leaderMs, 0xFF));

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(type);
        frames[0].Ok.Should().BeTrue();
        frames[0].ConAckLeaderMs.Should().Be(leaderMs);
    }

    [Fact]
    public void Ping_Ack_Round_Trips_Sn_And_Quality()
    {
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodePingAck(snDb: 12, quality: 80));

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Type.Should().Be(ArdopFrameType.PingAck);
        frames[0].Ok.Should().BeTrue();
        frames[0].PingAckSnDb.Should().Be(12);
        frames[0].PingAckQuality.Should().Be(80);
    }

    [Theory]
    [InlineData(120)] // ardopcf's host-settable minimum (spec App. B says 100, but the
                      // LEADER command clamps to 120-2500 — HostInterface.c:857)
    [InlineData(500)]
    [InlineData(1000)]
    public void Nonstandard_Leader_Lengths_Still_Decode(int leaderMs)
    {
        byte[] payload = Payload(32);
        byte[] encoded = ArdopFrameCodec.EncodeDataFrame(0x4C, payload, 0xFF);
        short[] audio = new ArdopModulator().Modulate(encoded, leaderLengthMs: leaderMs);

        var frames = Decode(audio);

        frames.Should().ContainSingle();
        frames[0].Ok.Should().BeTrue();
        frames[0].Data.Should().Equal(payload);
    }

    [Theory]
    [InlineData(0x4A, 64)]   // 4FSK.500.100
    [InlineData(0x40, 64)]   // 4PSK.200.100
    [InlineData(0x44, 108)]  // 8PSK.200.100
    [InlineData(0x46, 128)]  // 16QAM.200.100
    [InlineData(0x60, 256)]  // 4PSK.1000.100 — 4 carriers
    [InlineData(0x74, 1024)] // 16QAM.2000.100 — 8 carriers
    public void Frequency_Offset_Within_Tuning_Range_Is_Captured(byte type, int payloadLength)
    {
        byte[] payload = Payload(payloadLength);
        byte[] encoded = ArdopFrameCodec.EncodeDataFrame(type, payload, 0xFF);
        short[] audio = new ArdopModulator().Modulate(encoded);

        foreach (double offsetHz in new[] { -80.0, +80.0 })
        {
            short[] shifted = FrequencyShift(audio, offsetHz);
            var frames = Decode(shifted);

            frames.Should().ContainSingle("offset {0} Hz should be captured", offsetHz);
            frames[0].Ok.Should().BeTrue();
            frames[0].Data.Should().Equal(payload);
        }
    }

    [Theory]
    [InlineData(0x72, 864, 3, 1500)]  // 8PSK.2000.100 — 8 carriers, SavePSKSamples path
    [InlineData(0x54, 256, 2, 4200)]  // 16QAM.500.100 — 2 carriers, SaveQAMSamples path
    public void Memory_Arq_Recovers_A_Psk_Frame_No_Single_Copy_Decodes(byte type, int payloadLength, int seed, int noiseRms)
    {
        // Two repeats of the same frame, each with enough independent Gaussian noise
        // that the first doesn't decode alone; the weighted-angle phase averaging
        // (SavePSKSamples — plus magnitude averaging for 16QAM, SaveQAMSamples)
        // recovers the frame from the pair. Noise levels calibrated per mode: deep
        // enough to kill single-copy decode for this seed with margin either side,
        // shallow enough that the average comes back. (The single-carrier PSK modes
        // are too robust to fail before leader acquisition does — a multi-carrier
        // mode is the honest exercise of this path.)
        byte[] payload = Payload(payloadLength, seed);
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeDataFrame(type, payload, 0xFF));

        var random = new Random(seed);
        short[] Noisy()
        {
            var noisy = (short[])audio.Clone();
            for (int i = 0; i < noisy.Length; i++)
            {
                double gauss = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()))
                    * Math.Cos(2 * Math.PI * random.NextDouble());
                noisy[i] = (short)Math.Clamp(noisy[i] + gauss * noiseRms, short.MinValue, short.MaxValue);
            }

            return noisy;
        }

        var demod = new ArdopDemodulator();
        var frames = new List<ArdopDecodedFrame>();
        demod.FrameDecoded += frames.Add;

        demod.ProcessSamples(new short[2400]);
        demod.ProcessSamples(Noisy());
        demod.ProcessSamples(new short[4800]);
        frames.Should().ContainSingle();
        frames[0].Ok.Should().BeFalse("the first copy must fail alone for this test to bite");
        frames[0].Data.Should().HaveCount(payloadLength,
            "a failed frame still passes its raw payload fields (the FEC ERR path)");

        demod.ProcessSamples(Noisy());
        demod.ProcessSamples(new short[4800]);
        frames.Should().HaveCount(2);
        frames[1].Ok.Should().BeTrue("Memory-ARQ phase averaging must assemble the pair");
        frames[1].Data.Should().Equal(payload);
    }

    /// <summary>Frequency-shifts audio via the analytic signal (FFT → zero negative
    /// frequencies → complex mix → real part) — a clean SSB shift for test purposes.</summary>
    internal static short[] FrequencyShift(short[] audio, double offsetHz)
    {
        int n = 1;
        while (n < audio.Length)
        {
            n <<= 1;
        }

        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < audio.Length; i++)
        {
            re[i] = audio[i];
        }

        Fft.Forward(re, im);

        // Analytic signal: double positive bins, zero negative bins.
        for (int i = 1; i < n / 2; i++)
        {
            re[i] *= 2;
            im[i] *= 2;
        }

        for (int i = n / 2 + 1; i < n; i++)
        {
            re[i] = 0;
            im[i] = 0;
        }

        // Inverse FFT via the conjugation identity (Fft exposes Forward only).
        for (int i = 0; i < n; i++)
        {
            im[i] = -im[i];
        }

        Fft.Forward(re, im);
        for (int i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] = -im[i] / n;
        }

        var shifted = new short[audio.Length];
        double phaseInc = 2 * Math.PI * offsetHz / ArdopModulator.SampleRate;
        for (int i = 0; i < audio.Length; i++)
        {
            double phase = phaseInc * i;
            double value = re[i] * Math.Cos(phase) - im[i] * Math.Sin(phase);
            shifted[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        return shifted;
    }
}
