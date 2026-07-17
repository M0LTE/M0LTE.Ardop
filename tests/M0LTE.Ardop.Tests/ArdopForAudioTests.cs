using M0LTE.Ardop.Host;
using M0LTE.Radio.Audio;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Covers the <see cref="ArdopHostServer.ForAudio"/> convenience — the seam that binds an
/// ARDOP TNC to the M0LTE.Radio.Audio abstractions (the replacement for the old
/// SoundModemChannel factory). The rest of the ARDOP engine is exercised by the loopback,
/// oracle and reference-vector suites.
/// </summary>
public class ArdopForAudioTests
{
    private sealed class ClosedInput : IAudioInput
    {
        public int SampleRate => ArdopModulator.SampleRate;

        // Immediately closing, so the background receive pump exits at once.
        public int Read(Span<float> destination) => 0;
    }

    private sealed class CaptureOutput : IAudioOutput
    {
        public List<float> Written { get; } = [];
        public int Drains { get; private set; }
        public int SampleRate => ArdopModulator.SampleRate;
        public void Write(ReadOnlySpan<float> samples) => Written.AddRange(samples.ToArray());
        public void Drain() => Drains++;
    }

    private sealed class RecordingPtt : IPttControl
    {
        public List<string> Events { get; } = [];
        public void Key() => Events.Add("key");
        public void Unkey() => Events.Add("unkey");
    }

    [Fact]
    public async Task ForAudio_transmitter_brackets_the_burst_with_ptt_and_drains_the_output()
    {
        var output = new CaptureOutput();
        var ptt = new RecordingPtt();
        await using ArdopHostServer server = ArdopHostServer.ForAudio(new ClosedInput(), output, ptt);

        // Hand a canned s16 burst to the transmit seam, as the TNC would.
        short[] burst = [0, 16384, -16384, 32767, -32768];
        await server.Tnc.Transmitter!(burst);

        ptt.Events.Should().Equal("key", "unkey");         // keyed for the burst, then released
        output.Drains.Should().Be(1);                       // drained before unkey
        output.Written.Should().HaveCount(burst.Length);    // converted s16 -> normalised float
        output.Written[3].Should().BeApproximately(32767f / 32768f, 1e-6f);
    }

    [Fact]
    public void ForAudio_rejects_audio_that_is_not_ardops_native_rate()
    {
        var wrong = new WrongRateIo();
        Action act = () => ArdopHostServer.ForAudio(wrong, wrong, new NullPtt());
        act.Should().Throw<ArgumentException>();
    }

    private sealed class WrongRateIo : IAudioInput, IAudioOutput
    {
        public int SampleRate => 48_000;
        public int Read(Span<float> destination) => 0;
        public void Write(ReadOnlySpan<float> samples) { }
        public void Drain() { }
    }
}
