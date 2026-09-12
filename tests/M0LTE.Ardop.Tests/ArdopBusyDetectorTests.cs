using M0LTE.Ardop;
using M0LTE.Ardop.Arq;
using M0LTE.Ardop.Host;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// The channel-busy seam (<see cref="ArdopArqConfig.ChannelBusy"/>) and everything the
/// engine drives from it: ardopcf's confirm-and-hold hysteresis (BusyDetect.c:123-152),
/// BUSY TRUE/FALSE reporting to the host (ARDOPC.c:2106-2142), BUSYDET 0 disabling
/// detection (BusyDetect.c:120), ClearBusy on LISTEN TRUE (HostInterface.c:882), and the
/// BUSYBLOCK refusals: ConRejBusy for an inbound ConReq (ARQ.c:1199-1226) and, as this
/// port's one addition, a blocked outgoing ARQCALL.
///
/// The load-bearing test here is
/// <see cref="A_busy_channel_must_never_disturb_an_in_flight_session"/>. Read its comment
/// before touching anything in this file.
///
/// No test in this file (or anywhere else in this package) may decide anything by the
/// wall clock: the engine takes its time from the caller, and these harnesses hand it a
/// counter they own.
/// </summary>
public class ArdopBusyDetectorTests
{
    /// <summary>Drives one engine on a hand-cranked clock, recording everything the
    /// engine emits with the clock reading at the moment it was emitted.</summary>
    private sealed class Harness
    {
        public const int FrameAirMs = 500;

        public ArdopArqConfig Config { get; }

        public ArdopArqEngine Engine { get; }

        /// <summary>What the host application would report through the seam.</summary>
        public bool Busy { get; set; }

        public List<string> Transcript { get; } = [];

        public List<string> Notes { get; } = [];

        public List<ArdopTxRequest> Sent { get; } = [];

        public long NowMs { get; private set; } = 1000;

        private int _inFlight;

        public Harness(bool wireSeam = true, Action<ArdopArqConfig>? configure = null)
        {
            Config = new ArdopArqConfig { MyCall = Station("M0ME"), GridSquare = "IO81VK" };
            if (wireSeam)
            {
                Config.ChannelBusy = () => Busy;
            }

            configure?.Invoke(Config);
            Engine = new ArdopArqEngine(Config, randomSeed: 7);
            Engine.TransmitRequested += request =>
            {
                Sent.Add(request);
                Transcript.Add($"{NowMs} TX {request.Type:X2}");
                _inFlight++;
            };
            Engine.HostNotification += note =>
            {
                Notes.Add(note);
                Transcript.Add($"{NowMs} {note}");
            };
        }

        public static ArdopStationId Station(string call)
        {
            ArdopStationId.TryParse(call, out var id).Should().BeTrue();
            return id;
        }

        public void FlushTx()
        {
            while (_inFlight > 0)
            {
                NowMs += FrameAirMs;
                Engine.TransmitCompleted(NowMs);
                _inFlight--;
            }
        }

        /// <summary>Advances the injected clock, polling every 100 ms, which is exactly
        /// ardopcf's busy-check interval (ARDOPC.c:2062) so each poll takes one sample.</summary>
        public void Advance(long ms)
        {
            long end = NowMs + ms;
            while (NowMs < end)
            {
                NowMs = Math.Min(end, NowMs + 100);
                Engine.Poll(NowMs);
                FlushTx();
            }
        }

        public void Receive(ArdopDecodedFrame frame)
        {
            NowMs += 10;
            Engine.FrameReceived(frame, NowMs);
            FlushTx();
        }

        public ArdopTxRequest LastSent => Sent[^1];
    }

    private static ArdopDecodedFrame Frame(byte type, bool ok = true, int quality = 80) => new()
    {
        Type = type,
        Ok = ok,
        Data = [],
        Quality = quality,
        LeaderReceivedMs = 240,
        RemoteLeaderMeasureMs = 500,
    };

    private static ArdopDecodedFrame DataFrame(byte type, byte[] data) => new()
    {
        Type = type,
        Ok = true,
        Data = data,
        Quality = 80,
        LeaderReceivedMs = 240,
        RemoteLeaderMeasureMs = 500,
    };

    private static ArdopDecodedFrame ConReq(byte type = ArdopFrameType.ConReq500M,
        string caller = "G8XYZ", string target = "M0ME") => new()
    {
        Type = type,
        Ok = true,
        Data = [],
        Quality = 85,
        Caller = caller,
        Target = target,
        LeaderReceivedMs = 240,
        RemoteLeaderMeasureMs = 500,
    };

    // Latches the detector busy: 3 consecutive busy samples are the minimum
    // (BusyDetect.c:139), a few more so dttLastTrip tracks and the 5 s hold is real
    // (BusyDetect.c:130).
    private static void LatchBusy(Harness h)
    {
        h.Busy = true;
        h.Advance(1000);
        h.Engine.IsChannelBusy.Should().BeTrue();
    }

    // ------------------------------------------------------- the safety property

    /// <summary>
    /// THE ONE THAT MATTERS. The busy state gates session initiation and nothing else.
    /// An IRS has to get its ACK out inside the ISS's repeat window; if a busy check ever
    /// held up an in-flight burst the link would break rather than the channel being
    /// protected. (pdn-soundmodem PR #171 exists because ARDOP bursts waiting on channel
    /// access deadlocked; ardopcf avoids it by only running the detector in protocol state
    /// DISC, ARDOPC.c:2054 and :2102.)
    ///
    /// The assertion is deliberately absolute rather than a spot check: the same session
    /// script is run twice on the same injected clock, once with the channel clear
    /// throughout and once with it filling up with a third party's traffic the moment the
    /// session connects, and every frame transmitted and every host notification, with the
    /// clock reading at which it happened, must match exactly. Nothing about a busy
    /// channel may show up anywhere inside a session: not a delayed burst, not a changed
    /// frame, not so much as an extra notification.
    ///
    /// If you are here because this test is in your way, it is not the test that is wrong.
    /// </summary>
    [Fact]
    public void A_busy_channel_must_never_disturb_an_in_flight_session()
    {
        List<string> clearChannel = RunSessionScript(channelGoesBusyOnceConnected: false);
        List<string> busyChannel = RunSessionScript(channelGoesBusyOnceConnected: true);

        busyChannel.Should().Equal(clearChannel);
        busyChannel.Should().NotContainMatch("*BUSY TRUE*");
    }

    private static List<string> RunSessionScript(bool channelGoesBusyOnceConnected)
    {
        var h = new Harness();

        // Answer a call and complete the handshake, channel clear in both runs.
        h.Receive(ConReq());
        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);
        h.Receive(Frame(ArdopFrameType.ConAck500));
        h.Engine.IsConnected.Should().BeTrue();

        if (channelGoesBusyOnceConnected)
        {
            h.Busy = true;
        }

        // A data exchange with idle gaps long enough for several busy samples, had any
        // been taken.
        h.Advance(1000);
        h.Receive(DataFrame(0x48, [1, 2, 3]));
        h.LastSent.Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);
        h.Advance(1000);
        h.Receive(DataFrame(0x49, [4, 5, 6]));
        h.Advance(6000);
        h.Receive(DataFrame(0x48, [7, 8, 9]));
        h.Advance(1000);

        h.Engine.IsConnected.Should().BeTrue();
        h.Engine.IsChannelBusy.Should().BeFalse("the seam is not sampled outside DISC");
        return h.Transcript;
    }

    // -------------------------------------------------------------- the default

    [Fact]
    public void The_default_seam_is_never_busy()
    {
        var h = new Harness(wireSeam: false, configure: c =>
        {
            // Both switches on, to prove it is the unwired seam that keeps everything
            // dormant and not the switches being off.
            c.BusyBlock = true;
            c.BusyDetectLevel = 10;
        });

        h.Busy = true;  // ignored: nothing is reading it
        h.Advance(30000);

        h.Engine.IsChannelBusy.Should().BeFalse();
        h.Notes.Should().NotContain("BUSY TRUE");
        h.Notes.Should().NotContain("BUSY FALSE");

        // An inbound call is answered ...
        h.Receive(ConReq());
        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);

        // ... and an outbound call goes out.
        var caller = new Harness(wireSeam: false, configure: c => c.BusyBlock = true);
        caller.Busy = true;
        caller.Advance(30000);
        caller.Engine.ConnectRequest(Harness.Station("G8XYZ"), caller.NowMs).Should().BeTrue();
    }

    // ------------------------------------------------------------- the hysteresis

    [Fact]
    public void Busy_latches_only_after_three_consecutive_samples()
    {
        var h = new Harness();

        h.Advance(500);  // seeds the detector clear
        h.Engine.IsChannelBusy.Should().BeFalse();

        h.Busy = true;
        h.Advance(100);
        h.Engine.IsChannelBusy.Should().BeFalse("one sample is not enough");
        h.Advance(100);
        h.Engine.IsChannelBusy.Should().BeFalse("two samples are not enough");
        h.Advance(100);
        h.Engine.IsChannelBusy.Should().BeTrue("three consecutive samples latch (BusyDetect.c:139)");
    }

    [Fact]
    public void A_latched_busy_holds_for_five_seconds_after_the_channel_clears()
    {
        var h = new Harness();
        LatchBusy(h);

        h.Busy = false;
        h.Advance(3000);
        h.Engine.IsChannelBusy.Should().BeTrue("the hold time is 5 s (intHoldMs, BusyDetect.c:33)");

        h.Advance(2500);
        h.Engine.IsChannelBusy.Should().BeFalse();
    }

    // --------------------------------------------------------- host reporting

    [Fact]
    public void The_host_is_told_BUSY_TRUE_and_BUSY_FALSE_on_transitions()
    {
        var h = new Harness();

        LatchBusy(h);
        h.Notes.Should().Equal("BUSY TRUE");

        // Latched, not re-reported per sample.
        h.Advance(5000);
        h.Notes.Should().Equal(new[] { "BUSY TRUE" });

        h.Busy = false;
        h.Advance(6000);
        h.Notes.Should().Equal("BUSY TRUE", "BUSY FALSE");
    }

    [Fact]
    public async Task The_command_socket_carries_BUSY_TRUE_and_BUSY_FALSE()
    {
        long now = 1000;
        List<string> commands = [];
        bool busy = false;

        await using var tnc = new ArdopHostTnc(clock: () => now, randomSeed: 42);
        tnc.CommandToHost += commands.Add;
        tnc.Config.ChannelBusy = () => busy;

        void Run(long ms)
        {
            for (long end = now + ms; now < end;)
            {
                now = Math.Min(end, now + 100);
                tnc.Poll();
            }
        }

        Run(500);
        commands.Should().BeEmpty();

        busy = true;
        Run(1000);
        commands.Should().Equal("BUSY TRUE");

        busy = false;
        Run(6000);
        commands.Should().Equal("BUSY TRUE", "BUSY FALSE");
    }

    [Fact]
    public void BUSYDET_zero_disables_detection()
    {
        var h = new Harness(configure: c => c.BusyDetectLevel = 0);

        h.Busy = true;
        h.Advance(10000);

        h.Engine.IsChannelBusy.Should().BeFalse("BUSYDET 0 forces the reading clear (BusyDetect.c:120)");
        h.Notes.Should().BeEmpty();
    }

    [Fact]
    public void BUSYDET_zero_releases_an_already_latched_busy()
    {
        var h = new Harness();
        LatchBusy(h);

        // ardopcf does not special-case this: a zero makes the raw reading false and the
        // ordinary hold and clear-count then release it (BusyDetect.c:120-121, :147).
        h.Config.BusyDetectLevel = 0;
        h.Advance(6000);

        h.Engine.IsChannelBusy.Should().BeFalse();
        h.Notes.Should().Equal("BUSY TRUE", "BUSY FALSE");
    }

    [Fact]
    public async Task LISTEN_TRUE_clears_the_busy_history()
    {
        long now = 1000;
        List<string> commands = [];

        await using var tnc = new ArdopHostTnc(clock: () => now, randomSeed: 42);
        tnc.CommandToHost += commands.Add;
        tnc.Config.ChannelBusy = () => true;

        for (int i = 0; i < 10; i++)
        {
            now += 100;
            tnc.Poll();
        }

        commands.Should().Contain("BUSY TRUE");
        tnc.Engine.IsChannelBusy.Should().BeTrue();

        // ClearBusy on LISTEN TRUE (HostInterface.c:882): a scanning station must not be
        // held off by history from the frequency it has just left.
        commands.Clear();
        tnc.ProcessCommand("LISTEN TRUE");
        tnc.Engine.IsChannelBusy.Should().BeFalse();

        now += 100;
        tnc.Poll();
        commands.Should().Equal("LISTEN now TRUE", "BUSY FALSE");
    }

    // -------------------------------------------------- inbound ConRejBusy (BUSYBLOCK)

    [Fact]
    public void An_inbound_ConReq_is_refused_with_ConRejBusy_when_the_channel_is_busy()
    {
        var h = new Harness(configure: c => c.BusyBlock = true);
        LatchBusy(h);

        // Let the busy episode age past this ConReq's own occupancy, so it cannot be
        // attributed to the caller (ARQ.c:1197).
        h.Advance(3000);
        h.Receive(ConReq());

        h.LastSent.Type.Should().Be(ArdopFrameType.ConRejBusy);
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Engine.IsPending.Should().BeFalse();
        h.Notes.Should().Contain("REJECTEDBUSY G8XYZ");
        h.Notes.Should().Contain("STATUS ARQ CONNECTION REQUEST FROM G8XYZ REJECTED, CHANNEL BUSY.");

        // ClearBusy in the refusal path (ARQ.c:1207), so the caller's own frame plus the
        // hold time do not read as a permanent busy condition.
        h.Engine.IsChannelBusy.Should().BeFalse();
    }

    [Fact]
    public void An_inbound_ConReq_is_answered_when_BUSYBLOCK_is_off_however_busy_the_channel_is()
    {
        var h = new Harness();  // BUSYBLOCK defaults false, as in ardopcf (ARQ.c:100)
        LatchBusy(h);
        h.Advance(3000);

        h.Receive(ConReq());

        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);
        h.Engine.State.Should().Be(ArdopProtocolState.Irs);
    }

    [Fact]
    public void A_caller_whose_own_signal_tripped_the_detector_is_not_refused()
    {
        // Without ardopcf's leader-trip exception (ARQ.c:1197) a detector that hears the
        // incoming ConReq, which every real one does, would refuse every caller. The busy
        // episode here begins with the call itself, so the caller is judged on the
        // previous episode, and there has not been one.
        var h = new Harness(configure: c => c.BusyBlock = true);
        h.Advance(500);  // seed the detector clear

        LatchBusy(h);
        h.Receive(ConReq());

        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);
        h.Engine.State.Should().Be(ArdopProtocolState.Irs);
    }

    [Fact]
    public void An_inbound_ConReq_is_answered_on_a_clear_channel_with_BUSYBLOCK_on()
    {
        var h = new Harness(configure: c => c.BusyBlock = true);
        h.Advance(5000);

        h.Receive(ConReq());

        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);
    }

    // --------------------------------------------------- outbound call (BUSYBLOCK)

    [Fact]
    public void An_outgoing_call_is_blocked_when_the_channel_is_busy_and_BUSYBLOCK_is_on()
    {
        var h = new Harness(configure: c => c.BusyBlock = true);
        LatchBusy(h);

        h.Engine.ConnectRequest(Harness.Station("G8XYZ"), h.NowMs).Should().BeFalse();
        h.Sent.Should().BeEmpty();
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain("STATUS ARQ CONNECT REQUEST TO G8XYZ BLOCKED, CHANNEL BUSY.");
    }

    [Fact]
    public void An_outgoing_call_goes_out_on_a_busy_channel_when_BUSYBLOCK_is_off()
    {
        // ardopcf's own behaviour: ARQCALL never consults the detector (ARDOPC.c:1914
        // into ARQ.c:2432), it is the host that was told BUSY TRUE that decides.
        var h = new Harness();
        LatchBusy(h);

        h.Engine.ConnectRequest(Harness.Station("G8XYZ"), h.NowMs).Should().BeTrue();
        h.LastSent.Type.Should().Be(ArdopFrameType.ConReq2000M);
    }

    [Fact]
    public async Task ARQCALL_faults_when_blocked_by_busy()
    {
        long now = 1000;
        List<string> commands = [];

        await using var tnc = new ArdopHostTnc(clock: () => now, randomSeed: 42);
        tnc.CommandToHost += commands.Add;
        tnc.Config.ChannelBusy = () => true;
        tnc.ProcessCommand("MYCALL M0ME");
        tnc.ProcessCommand("BUSYBLOCK TRUE");

        for (int i = 0; i < 10; i++)
        {
            now += 100;
            tnc.Poll();
        }

        commands.Clear();
        tnc.ProcessCommand("ARQCALL G8XYZ 5");
        commands.Should().Equal("FAULT Blocked by Busy");
        tnc.Engine.State.Should().Be(ArdopProtocolState.Disc);
    }
}
