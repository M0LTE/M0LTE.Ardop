using M0LTE.Ardop;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Rung 2 of the validation ladder (docs/ardop-design.md §6.2): connectionless FEC-mode
/// exchanges over the audio channel — frame sequencing with repeats and even/odd
/// toggling, receive-side dedup, Memory-ARQ recovery of a frame no single copy of which
/// decodes, and the ERR delivery path for data the sender abandoned.
/// </summary>
public class ArdopFecModeTests
{
    private static (ArdopDemodulator Demod, ArdopFecReceiver Receiver, List<(ArdopFecTag Tag, byte[] Data)> Received) Rx()
    {
        var demod = new ArdopDemodulator();
        var receiver = new ArdopFecReceiver(demod);
        var received = new List<(ArdopFecTag, byte[])>();
        receiver.DataReceived += (tag, data) => received.Add((tag, data));
        return (demod, receiver, received);
    }

    private static void Play(ArdopDemodulator demod, IEnumerable<byte[]> frames)
    {
        var modulator = new ArdopModulator();
        demod.ProcessSamples(new short[2400]);
        foreach (byte[] encoded in frames)
        {
            demod.ProcessSamples(modulator.Modulate(encoded));
            demod.ProcessSamples(new short[4800]); // inter-frame gap
        }

        demod.ProcessSamples(new short[4800]);
    }

    private static byte[] Payload(int length, int seed = 7)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    [Fact]
    public void Sender_Splits_Toggles_And_Repeats()
    {
        byte[] data = Payload(150);
        var frames = ArdopFecSender.BuildFrames(0x4A, data, repeats: 2);

        // 150 bytes over 64-byte frames = 3 distinct frames, each sent 3 times.
        frames.Should().HaveCount(9);
        frames.Select(f => f[0]).Should().Equal(0x4A, 0x4A, 0x4A, 0x4B, 0x4B, 0x4B, 0x4A, 0x4A, 0x4A);
        frames[0][1].Should().Be(0x4A ^ 0xFF, "FEC frames always use session ID 0xFF");
    }

    [Fact]
    public void Fec_Transmission_With_Repeats_Delivers_Each_Frame_Exactly_Once()
    {
        byte[] data = Payload(150);
        var frames = ArdopFecSender.BuildFrames(0x4A, data, repeats: 2);
        var (demod, _, received) = Rx();

        Play(demod, frames);

        received.Should().HaveCount(3, "three distinct frames, repeats deduplicated");
        received.Should().OnlyContain(r => r.Tag == ArdopFecTag.Fec);
        received.SelectMany(r => r.Data).Should().Equal(data);
    }

    [Fact]
    public void Consecutive_Frames_With_Identical_Data_Still_Deliver_Separately()
    {
        // The even/odd toggle is what distinguishes "new frame, same bytes" from a
        // repeat — dedup is (type, payload CRC), so the toggled type re-delivers.
        byte[] chunk = Payload(32, seed: 3);
        byte[] data = [.. chunk, .. chunk];
        var frames = ArdopFecSender.BuildFrames(0x4C, data, repeats: 1);
        var (demod, _, received) = Rx();

        Play(demod, frames);

        received.Should().HaveCount(2);
        received[0].Data.Should().Equal(chunk);
        received[1].Data.Should().Equal(chunk);
    }

    [Fact]
    public void Memory_Arq_Recovers_A_Frame_No_Single_Copy_Decodes()
    {
        // Three copies of one frame, each with a different burst of obliterated audio
        // inside the data section — every copy fails RS+CRC alone, but the tone-
        // magnitude averaging across copies recovers the frame (SaveFSKSamples,
        // SoundInput.c:4975).
        byte[] data = Payload(64, seed: 9);
        byte[] encoded = ArdopFrameCodec.EncodeDataFrame(0x4A, data, 0xFF);
        short[] clean = new ArdopModulator().Modulate(encoded);

        // Frame layout: 240 ms leader (2880) + 10 type symbols (2400); the 83-byte
        // block spans 83 × 480 samples beyond that. Chop a different third of the
        // data section per copy.
        int dataStart = 2880 + 2400;
        int dataLength = 83 * 480;
        var copies = new List<short[]>();
        for (int copy = 0; copy < 3; copy++)
        {
            var mangled = (short[])clean.Clone();
            int chopStart = dataStart + copy * (dataLength / 3);
            Array.Clear(mangled, chopStart, dataLength / 3);
            copies.Add(mangled);
        }

        // Each copy alone must fail (that is what makes this Memory ARQ, not FEC).
        foreach (short[] copy in copies)
        {
            var (aloneDemod, _, aloneReceived) = Rx();
            aloneDemod.ProcessSamples(new short[2400]);
            aloneDemod.ProcessSamples(copy);
            aloneDemod.ProcessSamples(new short[4800]);
            aloneReceived.Where(r => r.Tag == ArdopFecTag.Fec).Should().BeEmpty(
                "no single damaged copy should decode by itself");
        }

        // Together, averaged, they decode.
        var (demod, _, received) = Rx();
        demod.ProcessSamples(new short[2400]);
        foreach (short[] copy in copies)
        {
            demod.ProcessSamples(copy);
            demod.ProcessSamples(new short[4800]);
        }

        received.Where(r => r.Tag == ArdopFecTag.Fec).Should().ContainSingle()
            .Which.Data.Should().Equal(data);
    }

    [Fact]
    public void Failed_Data_Is_Delivered_As_Err_When_The_Sender_Moves_On()
    {
        // A damaged frame the sender never repeats, followed by a good frame of a
        // different type: the failed bytes must reach the host tagged ERR before the
        // good data (PassFECErrDataToHost, FEC.c:335).
        byte[] lostData = Payload(64, seed: 11);
        short[] damaged = new ArdopModulator().Modulate(
            ArdopFrameCodec.EncodeDataFrame(0x4A, lostData, 0xFF));
        Array.Clear(damaged, 2880 + 2400, 83 * 480 / 2); // obliterate half the data section

        byte[] goodData = Payload(32, seed: 12);
        short[] good = new ArdopModulator().Modulate(
            ArdopFrameCodec.EncodeDataFrame(0x4C, goodData, 0xFF));

        var (demod, _, received) = Rx();
        demod.ProcessSamples(new short[2400]);
        demod.ProcessSamples(damaged);
        demod.ProcessSamples(new short[4800]);
        demod.ProcessSamples(good);
        demod.ProcessSamples(new short[4800]);

        received.Should().HaveCount(2);
        received[0].Tag.Should().Be(ArdopFecTag.Err);
        received[0].Data.Should().HaveCount(64, "ERR passes the raw payload field");
        received[1].Tag.Should().Be(ArdopFecTag.Fec);
        received[1].Data.Should().Equal(goodData);
    }

    [Fact]
    public void Id_Frames_Are_Delivered_With_The_Idf_Tag()
    {
        ArdopStationId.TryParse("M7TFF-3", out var station).Should().BeTrue();
        short[] audio = new ArdopModulator().Modulate(ArdopFrameCodec.EncodeIdFrame(station, "IO81VK"));

        var (demod, _, received) = Rx();
        demod.ProcessSamples(new short[2400]);
        demod.ProcessSamples(audio);
        demod.ProcessSamples(new short[4800]);

        received.Should().ContainSingle();
        received[0].Tag.Should().Be(ArdopFecTag.Idf);
        System.Text.Encoding.ASCII.GetString(received[0].Data).Should().Be("ID:M7TFF-3 [IO81VK]:");
    }
}
