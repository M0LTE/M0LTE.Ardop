using M0LTE.Ardop;
using M0LTE.Ardop.Arq;
using Xunit.Abstractions;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Hermetic full-audio ARQ sessions: two complete stations (engine + modulator +
/// demodulator) cross-connected by a virtual full-duplex audio cable, the whole
/// exchange running on the sample-count clock — every leader, ACK and repeat is real
/// modulated 12 kHz audio, decoded by the real demodulator, just faster than real time
/// (docs/ardop-design.md §6.2 rung 3's offline precursor). Both ends run FSKONLY, the
/// Phase B configuration.
/// </summary>
public class ArdopArqSessionTests(ITestOutputHelper output)
{
    private const int BlockSamples = 240;  // 20 ms — the live-loop block size

    /// <summary>Two stations wired back-to-back with one block of propagation delay
    /// per direction.</summary>
    private sealed class VirtualAir
    {
        public ArdopArqStation A { get; }

        public ArdopArqStation B { get; }

        public List<string> NotesA { get; } = [];

        public List<string> NotesB { get; } = [];

        public List<byte> ReceivedAtA { get; } = [];

        public List<byte> ReceivedAtB { get; } = [];

        private short[] _aToB = new short[BlockSamples];
        private short[] _bToA = new short[BlockSamples];

        public VirtualAir(Action<ArdopArqConfig>? tuneA = null, Action<ArdopArqConfig>? tuneB = null)
        {
            var configA = new ArdopArqConfig
            {
                MyCall = Station("M0AAA"),
                GridSquare = "IO81VK",
                ArqBandwidth = ArdopBandwidth.B500Max,
                FskOnly = true,
            };
            var configB = new ArdopArqConfig
            {
                MyCall = Station("G8BBB"),
                GridSquare = "IO92XX",
                ArqBandwidth = ArdopBandwidth.B500Max,
                FskOnly = true,
            };
            tuneA?.Invoke(configA);
            tuneB?.Invoke(configB);

            A = new ArdopArqStation(configA, randomSeed: 11);
            B = new ArdopArqStation(configB, randomSeed: 22);
            A.Engine.HostNotification += NotesA.Add;
            B.Engine.HostNotification += NotesB.Add;
            A.Engine.DataReceived += data => ReceivedAtA.AddRange(data);
            B.Engine.DataReceived += data => ReceivedAtB.AddRange(data);
        }

        public long NowMs => A.NowMs;

        /// <summary>Steps both stations until <paramref name="condition"/> holds;
        /// false on timeout.</summary>
        public bool RunUntil(Func<bool> condition, int timeoutMs)
        {
            int blocks = timeoutMs / 20;
            for (int i = 0; i < blocks; i++)
            {
                if (condition())
                {
                    return true;
                }

                short[] fromA = A.Step(_bToA);
                short[] fromB = B.Step(_aToB);
                _aToB = fromA;
                _bToA = fromB;
            }

            return condition();
        }

        public void Run(int durationMs) => RunUntil(() => false, durationMs);
    }

    private static ArdopStationId Station(string call)
    {
        ArdopStationId.TryParse(call, out var id).Should().BeTrue();
        return id;
    }

    private static byte[] Payload(int length, int seed)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static VirtualAir Connect(Action<ArdopArqConfig>? tuneA = null, Action<ArdopArqConfig>? tuneB = null)
    {
        var air = new VirtualAir(tuneA, tuneB);
        air.A.Engine.ConnectRequest(Station("G8BBB"), air.A.NowMs).Should().BeTrue();
        air.RunUntil(() => air.A.Engine.IsConnected && air.B.Engine.IsConnected, 30000)
            .Should().BeTrue("the ConReq/ConAck/ConAck/ACK handshake must complete");
        return air;
    }

    // Obliterates a slice of a frame's data section so RS+CRC fails but the leader,
    // sync and frame type survive (the ArdopFecModeTests idiom).
    private static short[] ChopDataSection(short[] audio, int leaderMs, int fraction, int ofFractions)
    {
        var mangled = (short[])audio.Clone();
        int dataStart = leaderMs * 12 + 2400;  // leader+sync then 10 type symbols
        int dataLength = audio.Length - dataStart - 480;
        int chop = dataLength / ofFractions;
        Array.Clear(mangled, dataStart + fraction * chop, chop);
        return mangled;
    }

    // ------------------------------------------------------------------ connect

    [Fact]
    public void Session_Connects_And_Tears_Down_Cleanly()
    {
        var air = Connect();

        air.A.Engine.SessionBandwidthHz.Should().Be(500);
        air.B.Engine.SessionBandwidthHz.Should().Be(500);
        air.A.Engine.SessionId.Should().Be(ArdopCrc.SessionId("M0AAA", "G8BBB"));
        air.B.Engine.SessionId.Should().Be(air.A.Engine.SessionId);
        air.NotesA.Should().Contain("CONNECTED G8BBB 500");
        air.NotesB.Should().Contain("CONNECTED M0AAA 500");

        // Orderly teardown from the caller: DISC → END, both land in DISC
        // (rules 1.6-1.7; the END/replay quirk makes this take one DISC repeat).
        air.A.Engine.Disconnect(air.A.NowMs);
        air.RunUntil(
            () => air.A.Engine.State == ArdopProtocolState.Disc
                && air.B.Engine.State == ArdopProtocolState.Disc
                && !air.A.IsTransmitting && !air.B.IsTransmitting,
            60000).Should().BeTrue();

        air.NotesB.Should().Contain("DISCONNECTED");
        air.NotesA.Should().Contain(n => n.StartsWith("DISCONNECTED") || n.Contains("END NOT RECEIVED"));
    }

    [Fact]
    public void Session_Connects_With_Roles_Reversed()
    {
        // B calls A — exercises each engine in the opposite role.
        var air = new VirtualAir();
        air.B.Engine.ConnectRequest(Station("M0AAA"), air.B.NowMs).Should().BeTrue();

        air.RunUntil(() => air.A.Engine.IsConnected && air.B.Engine.IsConnected, 30000)
            .Should().BeTrue();
        air.B.Engine.State.Should().BeOneOf(ArdopProtocolState.Iss, ArdopProtocolState.Idle);
        air.A.Engine.State.Should().Be(ArdopProtocolState.Irs);
    }

    [Fact]
    public void Bandwidth_Negotiation_Lands_On_The_Callee_Maximum()
    {
        var air = new VirtualAir(tuneA: c => c.ArqBandwidth = ArdopBandwidth.B2000Max);
        air.A.Engine.ConnectRequest(Station("G8BBB"), air.A.NowMs).Should().BeTrue();

        air.RunUntil(() => air.A.Engine.IsConnected && air.B.Engine.IsConnected, 30000)
            .Should().BeTrue();

        air.A.Engine.SessionBandwidthHz.Should().Be(500, "2000MAX caller × 500MAX callee → 500");
        air.B.Engine.SessionBandwidthHz.Should().Be(500);
    }

    [Fact]
    public void Forced_Bandwidth_Mismatch_Is_Rejected_On_Air()
    {
        var air = new VirtualAir(
            tuneA: c => c.ArqBandwidth = ArdopBandwidth.B2000Forced,
            tuneB: c => c.ArqBandwidth = ArdopBandwidth.B500Forced);
        air.A.Engine.ConnectRequest(Station("G8BBB"), air.A.NowMs).Should().BeTrue();

        air.RunUntil(() => air.NotesA.Any(n => n.StartsWith("REJECTEDBW")), 30000)
            .Should().BeTrue("the callee must answer ConRejBW");

        air.RunUntil(() => air.A.Engine.State == ArdopProtocolState.Disc, 10000).Should().BeTrue();
        air.A.Engine.IsConnected.Should().BeFalse();
        air.B.Engine.IsConnected.Should().BeFalse();
    }

    // ------------------------------------------------------------ data transfer

    [Fact]
    public void Data_Flows_Iss_To_Irs_Exactly_Once_With_Acks_Inside_The_Repeat_Window()
    {
        var air = Connect();
        byte[] payload = Payload(200, seed: 42);

        // ACK-timing instrumentation: data-frame TX end (ISS clock) → ACK decode.
        var ackDelaysMs = new List<long>();
        long dataEndMs = -1;
        air.A.FrameTransmitted += (request, endMs) =>
        {
            if (ArdopFrameType.IsData(request.Type))
            {
                dataEndMs = endMs;
            }
        };
        air.A.FrameDecoded += (frame, nowMs) =>
        {
            if (frame.Type >= ArdopFrameType.DataAckMin && dataEndMs >= 0)
            {
                ackDelaysMs.Add(nowMs - dataEndMs);
                dataEndMs = -1;
            }
        };

        air.A.Engine.EnqueueData(payload);
        air.RunUntil(() => air.ReceivedAtB.Count == payload.Length, 120000)
            .Should().BeTrue("all 200 bytes must arrive");
        air.ReceivedAtB.Should().Equal(payload);

        // Idle afterwards: 200 bytes = 13 × 16-byte 4FSK.200.50S frames, no more.
        air.RunUntil(() => air.A.Engine.State == ArdopProtocolState.Idle, 30000).Should().BeTrue();
        air.ReceivedAtB.Should().HaveCount(payload.Length, "no duplicates after IDLE");

        // A clean channel needs no repeats and no NAKs — every ACK beat the repeat
        // window (that is what zero repeats *means*).
        air.A.Engine.Stats.RepeatsSent.Should().Be(0);
        air.B.Engine.Stats.NaksSent.Should().Be(0);
        air.A.Engine.Stats.DataAcksReceived.Should().Be(13);

        ackDelaysMs.Should().HaveCount(13);
        output.WriteLine(
            $"ACK delays after data-frame end: min={ackDelaysMs.Min()} ms, " +
            $"max={ackDelaysMs.Max()} ms over {ackDelaysMs.Count} frames " +
            $"(repeat window floor 1500 ms + measured remote leader)");
        ackDelaysMs.Max().Should().BeLessThan(1500,
            "the ACK must land inside the ISS's tightest repeat window");
    }

    [Fact]
    public void A_Corrupted_Frame_Is_Nakked_And_Recovered_By_Repeat()
    {
        var air = Connect();
        byte[] payload = Payload(48, seed: 7);

        // Corrupt the first transmission of the first data frame only.
        int dataFramesSent = 0;
        air.A.TransmitFilter = (request, audio) =>
        {
            if (!ArdopFrameType.IsData(request.Type))
            {
                return audio;
            }

            dataFramesSent++;
            return dataFramesSent == 1
                ? ChopDataSection(audio, request.LeaderLengthMs, 0, 2)
                : audio;
        };

        air.A.Engine.EnqueueData(payload);
        air.RunUntil(() => air.ReceivedAtB.Count == payload.Length, 120000).Should().BeTrue();

        air.ReceivedAtB.Should().Equal(payload);
        air.B.Engine.Stats.NaksSent.Should().BeGreaterThanOrEqualTo(1, "the chopped copy fails RS+CRC");
        air.A.Engine.Stats.NaksReceived.Should().BeGreaterThanOrEqualTo(1);
        air.A.Engine.Stats.RepeatsSent.Should().BeGreaterThanOrEqualTo(1,
            "with a single-rung ladder the NAK cannot shift, so the repeat timer re-sends");
        output.WriteLine(
            $"NAKs sent by IRS: {air.B.Engine.Stats.NaksSent}; repeats by ISS: {air.A.Engine.Stats.RepeatsSent}");
    }

    [Fact]
    public void Memory_Arq_Assembles_A_Frame_No_Single_Copy_Of_Which_Decoded()
    {
        var air = Connect();
        byte[] payload = Payload(16, seed: 5);

        // Every copy of the (single) data frame is damaged, each in a different third
        // of its data section: no copy decodes alone; the IRS's tone-magnitude
        // averaging across repeats assembles it (SaveFSKSamples, Memory ARQ).
        int copies = 0;
        air.A.TransmitFilter = (request, audio) =>
        {
            if (!ArdopFrameType.IsData(request.Type))
            {
                return audio;
            }

            int copy = copies++;
            return copy < 3
                ? ChopDataSection(audio, request.LeaderLengthMs, copy % 3, 3)
                : audio;  // safety valve — not expected to be reached
        };

        air.A.Engine.EnqueueData(payload);
        air.RunUntil(() => air.ReceivedAtB.Count == payload.Length, 120000).Should().BeTrue();

        air.ReceivedAtB.Should().Equal(payload);
        copies.Should().BeGreaterThanOrEqualTo(2, "recovery must have needed repeats");
        copies.Should().BeLessThanOrEqualTo(3, "averaging should decode by the third damaged copy");
        air.B.Engine.Stats.NaksSent.Should().BeGreaterThanOrEqualTo(1);
        output.WriteLine($"Damaged copies transmitted: {copies}; NAKs: {air.B.Engine.Stats.NaksSent}");
    }

    [Fact]
    public void Gearshift_Shifts_Down_Under_Damage_And_Back_Up_When_Clean()
    {
        // 1000 Hz FSKONLY gives the two-rung ladder {4FSK.500.100S, 4FSK.500.100};
        // fastStart lands on the top rung.
        var air = Connect(
            tuneA: c => c.ArqBandwidth = ArdopBandwidth.B1000Max,
            tuneB: c => c.ArqBandwidth = ArdopBandwidth.B1000Max);
        byte[] payload = Payload(400, seed: 13);

        // Damage every 4FSK.500.100 (0x4A/0x4B) transmission until the ISS has
        // shifted down; the robust rung and the recovered channel then shift back up.
        air.A.TransmitFilter = (request, audio) =>
            request.Type is 0x4A or 0x4B && air.A.Engine.Gearshift.ShiftDowns == 0
                ? ChopDataSection(audio, request.LeaderLengthMs, 0, 2)
                : audio;

        air.A.Engine.EnqueueData(payload);
        air.RunUntil(() => air.ReceivedAtB.Count == payload.Length, 240000)
            .Should().BeTrue("the transfer must complete despite the damage");

        air.ReceivedAtB.Should().Equal(payload);
        air.A.Engine.Gearshift.ShiftDowns.Should().BeGreaterThanOrEqualTo(1,
            "the damaged top rung must be abandoned");
        air.A.Engine.Gearshift.ShiftUps.Should().BeGreaterThanOrEqualTo(1,
            "sustained clean quality must climb back");
        output.WriteLine(
            $"Shift downs: {air.A.Engine.Gearshift.ShiftDowns}, shift ups: {air.A.Engine.Gearshift.ShiftUps}, " +
            $"NAKs: {air.B.Engine.Stats.NaksSent}, repeats: {air.A.Engine.Stats.RepeatsSent}, " +
            $"duration: {air.NowMs / 1000.0:F1} s virtual");
    }

    [Theory]
    [InlineData(ArdopBandwidth.B500Max, 0x54, 1536)]   // ladder {48,42,40,50,52,54} → 16QAM.500.100
    [InlineData(ArdopBandwidth.B1000Max, 0x64, 4096)]  // ladder {4C,4A,50,60,62,64} → 16QAM.1000.100
    [InlineData(ArdopBandwidth.B2000Max, 0x70, 6144)]  // ladder {4C,4A,50,60,70,72,74} → 4PSK.2000.100
    public void Full_Ladder_Climbs_The_Psk_Qam_Rungs_On_A_Clean_Cable(ArdopBandwidth bandwidth, byte topRung, int payloadLength)
    {
        // Phase C exit shape (hermetic leg): both ends unrestricted, the gearshift
        // must climb the FSK→PSK→QAM ladder to the highest reachable rung on clean
        // audio and deliver byte-exact. At 500/1000 Hz that is the 16QAM top rung.
        // At 2000 Hz the reachable top is 4PSK.2000.100 — ardopcf-parity, not a gap:
        // the 4PSK.2000→8PSK.2000 shift threshold is 85 (GetShiftUpThresholds,
        // ARQ.c:682) while a clean 4PSK constellation measures quality 84 in
        // ardopcf's own decoder (UpdatePhaseConstellation's floor for the mode), so
        // neither implementation climbs that rung on reported quality alone.
        var air = Connect(
            tuneA: c => { c.ArqBandwidth = bandwidth; c.FskOnly = false; },
            tuneB: c => { c.ArqBandwidth = bandwidth; c.FskOnly = false; });
        byte[] payload = Payload(payloadLength, seed: 21);

        var dataTypesOnAir = new List<byte>();
        air.A.FrameTransmitted += (request, _) =>
        {
            if (ArdopFrameType.IsData(request.Type))
            {
                dataTypesOnAir.Add(request.Type);
            }
        };

        air.A.Engine.EnqueueData(payload);
        air.RunUntil(() => air.ReceivedAtB.Count == payload.Length, 600000)
            .Should().BeTrue("the whole payload must arrive");

        air.ReceivedAtB.Should().Equal(payload);
        dataTypesOnAir.Should().Contain(t => (t & 0xFE) == topRung,
            "a clean cable must climb to {0}", ArdopFrameType.Name(topRung));
        air.B.Engine.Stats.NaksSent.Should().Be(0, "nothing should fail on a clean cable");
        air.A.Engine.Stats.RepeatsSent.Should().Be(0);

        output.WriteLine(
            $"data frames on air: {string.Join(", ", dataTypesOnAir.Select(ArdopFrameType.Name))}; " +
            $"shift ups: {air.A.Engine.Gearshift.ShiftUps}, duration: {air.NowMs / 1000.0:F1} s virtual");
    }

    // --------------------------------------------------------------- turnover

    [Fact]
    public void AutoBreak_Turns_The_Link_Over_And_Data_Flows_Back()
    {
        var air = Connect();
        byte[] forward = Payload(32, seed: 1);
        byte[] backward = Payload(24, seed: 2);

        air.A.Engine.EnqueueData(forward);
        air.RunUntil(() => air.ReceivedAtB.Count == forward.Length, 120000).Should().BeTrue();

        // The IRS gets data to send; AUTOBREAK takes the link on the next IDLE
        // (rule 3.3), the ACK completes the swap (rule 3.5), and data flows back.
        air.B.Engine.EnqueueData(backward);
        air.RunUntil(() => air.ReceivedAtA.Count == backward.Length, 120000)
            .Should().BeTrue("the reversed link must deliver B's data to A");

        air.ReceivedAtA.Should().Equal(backward);
        air.ReceivedAtB.Should().Equal(forward);
        air.A.Engine.Stats.LinkTurnovers.Should().BeGreaterThanOrEqualTo(1);
        air.B.Engine.Stats.LinkTurnovers.Should().BeGreaterThanOrEqualTo(1);
        air.B.Engine.State.Should().BeOneOf(ArdopProtocolState.Iss, ArdopProtocolState.Idle);
        air.A.Engine.State.Should().Be(ArdopProtocolState.Irs);
        output.WriteLine(
            $"Turnovers A={air.A.Engine.Stats.LinkTurnovers} B={air.B.Engine.Stats.LinkTurnovers}; " +
            $"BREAKs on air handled at {air.NowMs / 1000.0:F1} s virtual");
    }

    // ----------------------------------------------------------------- timeout

    [Fact]
    public void A_Dead_Channel_Times_Out_Both_Ends_To_Disc()
    {
        var air = Connect(
            tuneA: c => c.ArqTimeoutSeconds = 30,
            tuneB: c => c.ArqTimeoutSeconds = 30);

        // Cut the cable both ways: every frame goes out as silence.
        static short[] Mute(ArdopTxRequest request, short[] audio) => new short[audio.Length];
        air.A.TransmitFilter = Mute;
        air.B.TransmitFilter = Mute;

        air.RunUntil(
            () => air.A.Engine.State == ArdopProtocolState.Disc
                && air.B.Engine.State == ArdopProtocolState.Disc,
            90000).Should().BeTrue("both ends must reach DISC by the session timeout (rule 1.8)");

        air.NotesA.Should().Contain(n => n.Contains("Timeout"));
        air.NotesB.Should().Contain(n => n.Contains("Timeout"));
        output.WriteLine($"Both ends DISC at {air.NowMs / 1000.0:F1} s virtual (timeout 30 s)");
    }

    // -------------------------------------------------------------------- PING

    [Fact]
    public void Ping_Gets_A_PingAck_With_Measured_Sn_And_Quality()
    {
        var air = new VirtualAir();
        air.A.Engine.Ping(Station("G8BBB"), repeats: 5, air.A.NowMs).Should().BeTrue();

        air.RunUntil(() => air.NotesA.Any(n => n.StartsWith("PINGACK")), 30000)
            .Should().BeTrue("the PingAck must come back and stop the repeats");

        air.NotesB.Should().Contain(n => n.StartsWith("PING M0AAA>G8BBB"));
        air.NotesB.Should().Contain("PINGREPLY");
        string ack = air.NotesA.First(n => n.StartsWith("PINGACK"));
        output.WriteLine($"{ack} (clean virtual cable)");
        int sn = int.Parse(ack.Split(' ')[1]);
        sn.Should().BeGreaterThan(10, "a clean cable should report high S:N");
    }
}
