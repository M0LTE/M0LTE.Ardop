using System.Diagnostics;
using M0LTE.Ardop;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Rung 1, transmit leg: our modulated audio must decode in ardopcf's own receiver
/// (<c>--decodewav</c>), for every Phase A frame type. Requires an ardopcf binary
/// (git a7c9228 — see samples/ardop/PROVENANCE.md) named by the <c>ARDOPCF</c>
/// environment variable; skipped otherwise, so CI stays hermetic. Run results as of
/// 2026-07-16 on the dev box: 33/33 frame types decoded by ardopcf, payload-exact for
/// all ten data-frame cases.
/// </summary>
public class ArdopOracleTxTests
{
    private static string? ArdopcfBinary()
    {
        string? path = Environment.GetEnvironmentVariable("ARDOPCF");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static string DecodeWithArdopcf(string binary, short[] audio)
    {
        string wav = Path.Combine(Path.GetTempPath(), $"pdn-ardop-tx-{Guid.NewGuid():N}.wav");
        try
        {
            var floats = new float[audio.Length];
            for (int i = 0; i < audio.Length; i++)
            {
                floats[i] = audio[i] / 32767f;
            }

            WavFile.WriteMono(wav, floats, ArdopModulator.SampleRate);

            var psi = new ProcessStartInfo(binary, $"--nologfile --decodewav {wav}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30000).Should().BeTrue("ardopcf should finish decoding");
            return output;
        }
        finally
        {
            File.Delete(wav);
        }
    }

    private static byte[] Payload(int length, int seed)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    [SkippableTheory]
    [InlineData(0x48, 16, "4FSK.200.50S.E")]
    [InlineData(0x49, 9, "4FSK.200.50S.O")]
    [InlineData(0x4A, 64, "4FSK.500.100.E")]
    [InlineData(0x4B, 40, "4FSK.500.100.O")]
    [InlineData(0x4C, 32, "4FSK.500.100S.E")]
    [InlineData(0x4D, 20, "4FSK.500.100S.O")]
    [InlineData(0x7A, 600, "4FSK.2000.600.E")]
    [InlineData(0x7B, 321, "4FSK.2000.600.O")]
    [InlineData(0x7C, 200, "4FSK.2000.600S.E")]
    [InlineData(0x7D, 77, "4FSK.2000.600S.O")]
    [InlineData(0x40, 64, "4PSK.200.100.E")]
    [InlineData(0x41, 40, "4PSK.200.100.O")]
    [InlineData(0x42, 16, "4PSK.200.100S.E")]
    [InlineData(0x43, 9, "4PSK.200.100S.O")]
    [InlineData(0x44, 108, "8PSK.200.100.E")]
    [InlineData(0x45, 60, "8PSK.200.100.O")]
    [InlineData(0x46, 128, "16QAM.200.100.E")]
    [InlineData(0x47, 77, "16QAM.200.100.O")]
    [InlineData(0x50, 128, "4PSK.500.100.E")]
    [InlineData(0x51, 80, "4PSK.500.100.O")]
    [InlineData(0x52, 216, "8PSK.500.100.E")]
    [InlineData(0x53, 130, "8PSK.500.100.O")]
    [InlineData(0x54, 256, "16QAM.500.100.E")]
    [InlineData(0x55, 150, "16QAM.500.100.O")]
    [InlineData(0x60, 256, "4PSK.1000.100.E")]
    [InlineData(0x61, 129, "4PSK.1000.100.O")]
    [InlineData(0x62, 432, "8PSK.1000.100.E")]
    [InlineData(0x63, 216, "8PSK.1000.100.O")]
    [InlineData(0x64, 512, "16QAM.1000.100.E")]
    [InlineData(0x65, 300, "16QAM.1000.100.O")]
    [InlineData(0x70, 512, "4PSK.2000.100.E")]
    [InlineData(0x71, 257, "4PSK.2000.100.O")]
    [InlineData(0x72, 864, "8PSK.2000.100.E")]
    [InlineData(0x73, 500, "8PSK.2000.100.O")]
    [InlineData(0x74, 1024, "16QAM.2000.100.E")]
    [InlineData(0x75, 600, "16QAM.2000.100.O")]
    public void Ardopcf_Decodes_Our_Data_Frames_Payload_Exact(byte type, int payloadLength, string name)
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        byte[] payload = Payload(payloadLength, seed: type * 1000 + payloadLength);
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeDataFrame(type, payload, 0xFF));

        string output = DecodeWithArdopcf(binary!, audio);

        output.Should().Contain($"{name} frame received OK.  frameLen = {payloadLength}");

        // The hex dump follows "N bytes of data as hex values:".
        string hex = string.Join(" ", payload.Select(b => b.ToString("X2")));
        string normalised = string.Join(" ",
            output.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        normalised.Should().Contain(hex, "ardopcf must recover our payload byte-exact");
    }

    [SkippableTheory]
    [InlineData(ArdopFrameType.Break, "BREAK")]
    [InlineData(ArdopFrameType.Idle, "IDLE")]
    [InlineData(ArdopFrameType.Disc, "DISC")]
    [InlineData(ArdopFrameType.End, "END")]
    [InlineData(ArdopFrameType.ConRejBusy, "ConRejBusy")]
    [InlineData(ArdopFrameType.ConRejBw, "ConRejBW")]
    public void Ardopcf_Decodes_Our_Short_Control_Frames(byte type, string name)
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeControl(type, 0xFF));

        string output = DecodeWithArdopcf(binary!, audio);

        output.Should().Contain($"[Frame Type Decode OK  ] H{type:X2}:{name}");
    }

    [SkippableTheory]
    [InlineData(60)]
    [InlineData(80)]
    public void Ardopcf_Decodes_Our_Ack_And_Nak_With_Quality(int quality)
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        var modulator = new ArdopModulator();

        string nak = DecodeWithArdopcf(binary!, modulator.Modulate(ArdopFrameCodec.EncodeDataNak(quality, 0xFF)));
        nak.Should().Contain($"indicates decode quality ({quality}/100)");

        string ack = DecodeWithArdopcf(binary!, modulator.Modulate(ArdopFrameCodec.EncodeDataAck(quality, 0xFF)));
        ack.Should().Contain($"indicates decode quality ({quality}/100)");
    }

    [SkippableTheory]
    [InlineData(ArdopFrameType.ConAck200, "ConAck200", 320)]
    [InlineData(ArdopFrameType.ConAck500, "ConAck500", 240)]
    [InlineData(ArdopFrameType.ConAck1000, "ConAck1000", 500)]
    [InlineData(ArdopFrameType.ConAck2000, "ConAck2000", 2000)]
    public void Ardopcf_Decodes_Our_Con_Acks(byte type, string name, int leaderMs)
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeConAck(type, leaderMs, 0xFF));

        string output = DecodeWithArdopcf(binary!, audio);

        output.Should().Contain($"Frame: {name} Decode PASS");
        output.Should().Contain(
            $"length (in tens of ms) of the received leader repeated 3 times: "
            + $"{leaderMs / 10} {leaderMs / 10} {leaderMs / 10}");
    }

    [SkippableFact]
    public void Ardopcf_Decodes_Our_Ping_Ack()
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodePingAck(snDb: 15, quality: 90));

        string output = DecodeWithArdopcf(binary!, audio);

        output.Should().Contain("PingAck data is S:N=15 and Quality=90");
    }

    [SkippableFact]
    public void Ardopcf_Decodes_Our_Id_Frame()
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        ArdopStationId.TryParse("M7TFF-3", out var station).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeIdFrame(station, "IO81VK"));

        string output = DecodeWithArdopcf(binary!, audio);

        // ardopcf renders the grid in Maidenhead case convention (IO81vk).
        output.ToUpperInvariant().Should().Contain("IDFRAME: M7TFF-3 [IO81VK]");
    }

    [SkippableTheory]
    [InlineData(ArdopFrameType.ConReq200M, "ConReq200M")]
    [InlineData(ArdopFrameType.ConReq500M, "ConReq500M")]
    [InlineData(ArdopFrameType.ConReq1000M, "ConReq1000M")]
    [InlineData(ArdopFrameType.ConReq2000M, "ConReq2000M")]
    [InlineData(ArdopFrameType.ConReq200F, "ConReq200F")]
    [InlineData(ArdopFrameType.ConReq500F, "ConReq500F")]
    [InlineData(ArdopFrameType.ConReq1000F, "ConReq1000F")]
    [InlineData(ArdopFrameType.ConReq2000F, "ConReq2000F")]
    public void Ardopcf_Decodes_Our_Con_Reqs(byte type, string name)
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        ArdopStationId.TryParse("M7TFF", out var caller).Should().BeTrue();
        ArdopStationId.TryParse("GB7RDG-15", out var target).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeConReq(type, caller, target));

        string output = DecodeWithArdopcf(binary!, audio);

        output.Should().Contain($"Frame: {name} Decode PASS");
    }

    [SkippableFact]
    public void Ardopcf_Decodes_Our_Ping_With_Callsigns()
    {
        string? binary = ArdopcfBinary();
        Skip.If(binary is null, "set ARDOPCF=/path/to/ardopcf to run the reverse oracle leg");

        ArdopStationId.TryParse("M7TFF", out var caller).Should().BeTrue();
        ArdopStationId.TryParse("GB7RDG", out var target).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodePing(caller, target));

        string output = DecodeWithArdopcf(binary!, audio);

        output.Should().Contain("'M7TFF GB7RDG'");
    }
}
