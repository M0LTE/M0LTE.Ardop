using M0LTE.Ardop;
using M0LTE.Ardop.Arq;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// Pure-logic ARQ engine tests: the engine driven with synthetic decoded frames and a
/// hand-cranked clock, no audio. Protocol rules cited are ARDOP spec App. D; behaviour
/// is ardopcf's (ARQ.c) where the two differ. Full-audio sessions are in
/// <see cref="ArdopArqSessionTests"/>.
/// </summary>
public class ArdopArqEngineTests
{
    /// <summary>Drives one engine, auto-completing transmissions with a nominal
    /// on-air duration so repeat timers arm.</summary>
    private sealed class Harness
    {
        public const int FrameAirMs = 500;

        public ArdopArqConfig Config { get; }

        public ArdopArqEngine Engine { get; }

        public List<ArdopTxRequest> Sent { get; } = [];

        public List<string> Notes { get; } = [];

        public List<byte[]> Delivered { get; } = [];

        public long NowMs { get; private set; } = 1000;

        private int _inFlight;

        public Harness(Action<ArdopArqConfig>? configure = null)
        {
            Config = new ArdopArqConfig { MyCall = Station("M0ME"), GridSquare = "IO81VK" };
            configure?.Invoke(Config);
            Engine = new ArdopArqEngine(Config, randomSeed: 7);
            Engine.TransmitRequested += request =>
            {
                Sent.Add(request);
                _inFlight++;
            };
            Engine.HostNotification += Notes.Add;
            Engine.DataReceived += Delivered.Add;
        }

        public static ArdopStationId Station(string call)
        {
            ArdopStationId.TryParse(call, out var id).Should().BeTrue();
            return id;
        }

        /// <summary>Completes all in-flight transmissions, advancing the clock by a
        /// nominal air time each.</summary>
        public void FlushTx()
        {
            while (_inFlight > 0)
            {
                NowMs += FrameAirMs;
                Engine.TransmitCompleted(NowMs);
                _inFlight--;
            }
        }

        public void Advance(long ms, long pollStepMs = 100)
        {
            long end = NowMs + ms;
            while (NowMs < end)
            {
                NowMs = Math.Min(end, NowMs + pollStepMs);
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

    private static ArdopDecodedFrame DataFrame(byte type, byte[] data, bool ok = true, int quality = 80) => new()
    {
        Type = type,
        Ok = ok,
        Data = data,
        Quality = quality,
        LeaderReceivedMs = 240,
        RemoteLeaderMeasureMs = 500,
    };

    private static ArdopDecodedFrame ConReq(byte type, string caller = "G8XYZ", string target = "M0ME") => new()
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

    // Puts a harness into a connected IRS session (caller G8XYZ → us).
    private static byte ConnectAsIrs(Harness h, byte conReq = ArdopFrameType.ConReq500M, byte conAck = ArdopFrameType.ConAck500)
    {
        h.Receive(ConReq(conReq));
        h.LastSent.Type.Should().Be(conAck);
        h.Receive(Frame(conAck));
        h.Engine.IsConnected.Should().BeTrue();
        return h.Engine.SessionId;
    }

    // Puts a harness into a connected ISS session (us → G8XYZ).
    private static void ConnectAsIss(Harness h)
    {
        h.Engine.ConnectRequest(Harness.Station("G8XYZ"), h.NowMs).Should().BeTrue();
        h.FlushTx();
        h.Receive(Frame(ArdopFrameType.ConAck500));
        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);
        h.Receive(Frame(0xF0));  // the IRS's confirming ACK
        h.Engine.IsConnected.Should().BeTrue();
    }

    // ------------------------------------------------------ connect negotiation

    [Theory]
    // IRSNegotiateBW (ARQ.c:2318): MAX settings answer at min(request, setting)…
    [InlineData(ArdopBandwidth.B2000Max, ArdopFrameType.ConReq500M, ArdopFrameType.ConAck500)]
    [InlineData(ArdopBandwidth.B2000Max, ArdopFrameType.ConReq2000M, ArdopFrameType.ConAck2000)]
    [InlineData(ArdopBandwidth.B500Max, ArdopFrameType.ConReq2000M, ArdopFrameType.ConAck500)]
    [InlineData(ArdopBandwidth.B500Max, ArdopFrameType.ConReq200M, ArdopFrameType.ConAck200)]
    [InlineData(ArdopBandwidth.B1000Max, ArdopFrameType.ConReq2000M, ArdopFrameType.ConAck1000)]
    // …a 200MAX callee even accepts any MAX request, negotiating down to 200
    // (the B200MAX branch spans ConReq200M..ConReq200F, ARQ.c:2363)…
    [InlineData(ArdopBandwidth.B200Max, ArdopFrameType.ConReq500M, ArdopFrameType.ConAck200)]
    // …forced requests only match at or below the setting…
    [InlineData(ArdopBandwidth.B2000Max, ArdopFrameType.ConReq500F, ArdopFrameType.ConAck500)]
    [InlineData(ArdopBandwidth.B500Max, ArdopFrameType.ConReq500F, ArdopFrameType.ConAck500)]
    // …and forced settings accept only their own class (or a wider MAX request).
    [InlineData(ArdopBandwidth.B500Forced, ArdopFrameType.ConReq2000M, ArdopFrameType.ConAck500)]
    [InlineData(ArdopBandwidth.B500Forced, ArdopFrameType.ConReq500F, ArdopFrameType.ConAck500)]
    public void Bandwidth_Negotiation_Answers_The_Reference_ConAck(
        ArdopBandwidth setting, byte conReqType, byte expectedConAck)
    {
        var h = new Harness(c => c.ArqBandwidth = setting);
        h.Receive(ConReq(conReqType));
        h.LastSent.Type.Should().Be(expectedConAck);
        h.Engine.State.Should().Be(ArdopProtocolState.Irs);
        h.Engine.IsPending.Should().BeTrue();
    }

    [Theory]
    // Incompatible pairs → ConRejBW, stay DISC (rule 1.3).
    [InlineData(ArdopBandwidth.B2000Forced, ArdopFrameType.ConReq500M)]
    [InlineData(ArdopBandwidth.B500Forced, ArdopFrameType.ConReq200F)]
    [InlineData(ArdopBandwidth.B500Max, ArdopFrameType.ConReq2000F)]
    public void Incompatible_Bandwidth_Is_Rejected(ArdopBandwidth setting, byte conReqType)
    {
        var h = new Harness(c => c.ArqBandwidth = setting);
        h.Receive(ConReq(conReqType));
        h.LastSent.Type.Should().Be(ArdopFrameType.ConRejBw);
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain(n => n.StartsWith("REJECTEDBW"));
    }

    [Fact]
    public void ConReq_To_An_Aux_Call_Is_Answered_With_Its_Session_Id()
    {
        var h = new Harness(c => c.AuxCalls.Add(Harness.Station("M0ME-2")));
        h.Receive(ConReq(ArdopFrameType.ConReq500M, target: "M0ME-2"));

        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);
        h.Engine.PendingSessionId.Should().Be(ArdopCrc.SessionId("G8XYZ", "M0ME-2"));
    }

    [Fact]
    public void ConReq_For_Someone_Else_Is_Ignored_With_CancelPending()
    {
        var h = new Harness();
        h.Receive(ConReq(ArdopFrameType.ConReq500M, target: "G0OTHER"));

        h.Sent.Should().BeEmpty();
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain("CANCELPENDING");
    }

    [Fact]
    public void ConReq_Is_Ignored_When_Not_Listening()
    {
        var h = new Harness(c => c.Listen = false);
        h.Receive(ConReq(ArdopFrameType.ConReq500M));
        h.Sent.Should().BeEmpty();
    }

    [Fact]
    public void Caller_Repeats_ConReq_Then_Fails_After_The_Budget()
    {
        var h = new Harness(c => c.ConReqRepeats = 3);
        h.Engine.ConnectRequest(Harness.Station("G8XYZ"), h.NowMs).Should().BeTrue();
        h.FlushTx();
        h.Engine.SessionId.Should().Be(ArdopCrc.SessionId("M0ME", "G8XYZ"));

        h.Advance(20000);

        // ardopcf counts the initial transmission in the budget: with
        // ARQConReqRepeats = 3 the ConReq goes out 3 times in total (the counter
        // starts at 1, ARQ.c:2467, and gives up when it exceeds the budget, :404).
        h.Sent.Should().HaveCount(3);
        h.Sent.Should().OnlyContain(r => r.Type == ArdopFrameType.ConReq2000M);
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain(n => n.Contains("CONNECT TO G8XYZ FAILED"));
    }

    [Fact]
    public void Callee_Pending_Times_Out_After_Ten_Seconds()
    {
        var h = new Harness();
        h.Receive(ConReq(ArdopFrameType.ConReq500M));
        h.Engine.State.Should().Be(ArdopProtocolState.Irs);

        h.Advance(11000);

        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Engine.IsPending.Should().BeFalse();
        h.Notes.Should().Contain("DISCONNECTED");
    }

    [Fact]
    public void Full_Callee_Handshake_Connects_And_Acks()
    {
        var h = new Harness();
        h.Receive(ConReq(ArdopFrameType.ConReq500M));
        h.LastSent.Type.Should().Be(ArdopFrameType.ConAck500);

        // The ISS's confirming ConAck completes the exchange (rule 1.4).
        h.Receive(Frame(ArdopFrameType.ConAck500));

        h.Engine.IsConnected.Should().BeTrue();
        h.Engine.SessionBandwidthHz.Should().Be(500);
        h.Engine.SessionId.Should().Be(ArdopCrc.SessionId("G8XYZ", "M0ME"));
        h.LastSent.Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);
        h.Notes.Should().Contain($"CONNECTED G8XYZ 500");
    }

    [Fact]
    public void Full_Caller_Handshake_Connects_On_First_Ack()
    {
        var h = new Harness();
        ConnectAsIss(h);

        h.Engine.SessionBandwidthHz.Should().Be(500);
        h.Notes.Should().Contain($"CONNECTED G8XYZ 500");
        // No data queued: the new ISS goes straight to IDLE chirps (rule 2.5).
        h.Engine.State.Should().Be(ArdopProtocolState.Idle);
        h.LastSent.Type.Should().Be(ArdopFrameType.Idle);
    }

    // ------------------------------------------------------------ data exchange

    [Fact]
    public void Iss_Sends_Data_And_Advances_On_Ack()
    {
        var h = new Harness(c => c.FskOnly = true);  // 500 Hz FSK ladder = {0x48}
        ConnectAsIss(h);

        byte[] payload = new byte[40];
        new Random(3).NextBytes(payload);
        h.Engine.EnqueueData(payload);

        // From IDLE, an ACK for the idle chirp starts the data (ARQ.c:1768).
        h.Receive(Frame(0xF0));
        h.LastSent.Type.Should().Be(0x48, "FSKONLY 500 Hz sends 4FSK.200.50S; toggle starts even");
        h.LastSent.EncodedFrame[2].Should().Be(16, "first frame carries a full 16-byte block");

        h.Receive(Frame(0xF0));  // ACK → dequeue + next frame (odd toggle)
        h.LastSent.Type.Should().Be(0x49);
        h.Engine.OutboundCount.Should().Be(24);

        h.Receive(Frame(0xF0));
        h.LastSent.Type.Should().Be(0x48);

        h.Receive(Frame(0xF0));  // 40 bytes = 16+16+8 — all sent; back to IDLE
        h.LastSent.Type.Should().Be(ArdopFrameType.Idle);
        h.Engine.State.Should().Be(ArdopProtocolState.Idle);
        h.Engine.OutboundCount.Should().Be(0);
    }

    [Fact]
    public void Missed_Ack_Causes_A_Repeat_Not_A_Toggle()
    {
        var h = new Harness(c => c.FskOnly = true);
        ConnectAsIss(h);
        h.Engine.EnqueueData(new byte[16]);
        h.Receive(Frame(0xF0));
        h.LastSent.Type.Should().Be(0x48);
        int sentBefore = h.Sent.Count;

        // No reply: the repeat timer re-sends the same even frame (rule 2.4).
        h.Advance(4000);

        h.Sent.Count.Should().BeGreaterThan(sentBefore);
        h.LastSent.Type.Should().Be(0x48);
        h.LastSent.IsRepeat.Should().BeTrue();
        h.Engine.Stats.RepeatsSent.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Irs_Delivers_Data_Exactly_Once_And_Reacks_Repeats()
    {
        var h = new Harness();
        ConnectAsIrs(h);

        byte[] data = [1, 2, 3, 4, 5];
        h.Receive(DataFrame(0x48, data));
        h.Delivered.Should().ContainSingle().Which.Should().Equal(data);
        h.LastSent.Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);

        // Repeat of the same even frame (our ACK was missed): re-ACK, no re-delivery
        // (rule 2.3).
        h.Receive(DataFrame(0x48, data));
        h.Delivered.Should().HaveCount(1);
        h.LastSent.Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);

        // The odd toggle is new data.
        byte[] next = [6, 7];
        h.Receive(DataFrame(0x49, next));
        h.Delivered.Should().HaveCount(2);
        h.Delivered[1].Should().Equal(next);
    }

    [Fact]
    public void Irs_Naks_A_Failed_Data_Frame_With_Its_Quality()
    {
        var h = new Harness();
        ConnectAsIrs(h);

        h.Receive(DataFrame(0x48, [], ok: false, quality: 62));

        h.Delivered.Should().BeEmpty();
        h.LastSent.Type.Should().BeLessThanOrEqualTo(ArdopFrameType.DataNakMax);
        ArdopFrameCodec.AckNakQuality(h.LastSent.Type).Should().Be(62);
        h.Engine.Stats.NaksSent.Should().Be(1);
    }

    [Fact]
    public void Irs_Reacks_A_Failed_Frame_It_Already_Acked()
    {
        var h = new Harness();
        ConnectAsIrs(h);
        h.Receive(DataFrame(0x48, [9, 9]));
        h.Delivered.Should().HaveCount(1);

        // Channel degraded: the same frame type now fails to decode, but we ACKed it
        // before — re-ACK so the ISS moves on (ARQ.c:1696).
        h.Receive(DataFrame(0x48, [], ok: false, quality: 50));

        h.LastSent.Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);
        h.Delivered.Should().HaveCount(1);
    }

    // ------------------------------------------------------------ IDLE and BREAK

    [Fact]
    public void Irs_With_Data_Breaks_On_Idle_And_Becomes_Iss()
    {
        var h = new Harness(c => c.FskOnly = true);
        ConnectAsIrs(h);
        h.Engine.EnqueueData([1, 2, 3]);

        // AUTOBREAK: IDLE received with data queued → BREAK, IRStoISS (rule 3.3).
        h.Receive(Frame(ArdopFrameType.Idle));
        h.LastSent.Type.Should().Be(ArdopFrameType.Break);
        h.Engine.State.Should().Be(ArdopProtocolState.IrsToIss);

        // The ISS's ACK completes the turnover; we send our data (rule 3.5).
        h.Receive(Frame(0xF0));
        h.Engine.State.Should().Be(ArdopProtocolState.Iss);
        h.LastSent.Type.Should().BeOneOf(0x48, 0x49);
        h.Engine.Stats.LinkTurnovers.Should().Be(1);
    }

    [Fact]
    public void Irs_Without_Data_Acks_Idle()
    {
        var h = new Harness();
        ConnectAsIrs(h);
        h.Receive(Frame(ArdopFrameType.Idle));
        h.LastSent.Type.Should().BeInRange(ArdopFrameType.DataAckMin, 0xFF);
        h.Engine.State.Should().Be(ArdopProtocolState.Irs);
    }

    [Fact]
    public void Iss_Receiving_Break_Purges_And_Becomes_Irs()
    {
        var h = new Harness(c => c.FskOnly = true);
        ConnectAsIss(h);
        h.Engine.EnqueueData(new byte[100]);
        h.Receive(Frame(0xF0));  // start sending

        h.Receive(Frame(ArdopFrameType.Break));

        h.Engine.State.Should().Be(ArdopProtocolState.Irs);
        h.LastSent.Type.Should().Be(0xFF, "BREAK is ACKed with quality 100");
        h.Engine.OutboundCount.Should().Be(0, "pending data is purged on BREAK (rule 3.6)");
        h.Engine.Stats.LinkTurnovers.Should().Be(1);
    }

    [Fact]
    public void IrsToIss_Answers_Data_With_Break_Until_Acked()
    {
        var h = new Harness(c => c.FskOnly = true);
        ConnectAsIrs(h);
        h.Engine.EnqueueData([1]);
        h.Receive(Frame(ArdopFrameType.Idle));
        h.Engine.State.Should().Be(ArdopProtocolState.IrsToIss);

        // A data frame arriving mid-changeover is answered with BREAK, not ACK/NAK
        // (rule 3.3; SoundInput.c:1257).
        h.Receive(DataFrame(0x48, [5, 5]));
        h.LastSent.Type.Should().Be(ArdopFrameType.Break);
        h.Delivered.Should().BeEmpty();
        h.Engine.State.Should().Be(ArdopProtocolState.IrsToIss);
    }

    [Fact]
    public void Break_Command_Interrupts_Data_Reception()
    {
        var h = new Harness(c => { c.FskOnly = true; c.AutoBreak = false; });
        ConnectAsIrs(h);
        h.Receive(DataFrame(0x48, [1]));  // first frame delivered + ACKed
        h.Engine.BreakRequested(h.NowMs);

        // Rule 3.4: BREAK goes out on the next not-yet-ACKed data frame.
        h.Receive(DataFrame(0x49, [2]));

        h.LastSent.Type.Should().Be(ArdopFrameType.Break);
        h.Engine.State.Should().Be(ArdopProtocolState.IrsToIss);
        h.Delivered.Should().HaveCount(1, "the interrupted frame is not delivered");
    }

    // --------------------------------------------------------------- teardown

    [Fact]
    public void Disconnect_Sends_Disc_And_Ends_On_End()
    {
        var h = new Harness();
        ConnectAsIss(h);
        byte session = h.Engine.SessionId;

        h.Engine.Disconnect(h.NowMs);
        // The host DISCONNECT is consumed at the next idle-repeat poll (ardopcf's
        // CheckForDisconnect runs from GetNextARQFrame at the repeat timer).
        h.Advance(4000);
        h.LastSent.Type.Should().Be(ArdopFrameType.Disc);
        h.LastSent.EncodedFrame[1].Should().Be((byte)(ArdopFrameType.Disc ^ session));

        h.Receive(Frame(ArdopFrameType.End));
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain("DISCONNECTED");
        // END → immediate ID frame (rule 1.7).
        h.LastSent.Type.Should().Be(ArdopFrameType.IdFrame);
    }

    [Fact]
    public void Disc_Repeats_Five_Times_Then_Gives_Up()
    {
        var h = new Harness();
        ConnectAsIss(h);
        h.Engine.Disconnect(h.NowMs);
        h.Advance(4000);
        int discsBefore = h.Sent.Count(r => r.Type == ArdopFrameType.Disc);
        discsBefore.Should().Be(1);

        h.Advance(30000);

        // The repeat counter starts at 1 and gives up above 5: five DISCs in total
        // (ARQ.c:371-387).
        h.Sent.Count(r => r.Type == ArdopFrameType.Disc).Should().Be(5);
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain(n => n.Contains("END NOT RECEIVED"));
    }

    [Fact]
    public void Irs_Receiving_Disc_Replies_End_And_Disconnects()
    {
        var h = new Harness();
        byte session = ConnectAsIrs(h);

        h.Receive(Frame(ArdopFrameType.Disc));

        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain("DISCONNECTED");
        h.LastSent.Type.Should().Be(ArdopFrameType.End);
        // ardopcf quirk ported faithfully: this END is encoded after the session-ID
        // reset, so it carries 0xFF, not the session ID (see engine remarks).
        h.LastSent.EncodedFrame[1].Should().Be((byte)(ArdopFrameType.End ^ 0xFF));
        h.Engine.LastSessionId.Should().Be(session);
    }

    [Fact]
    public void Disc_In_Disc_State_Replays_End_With_The_Last_Session_Id()
    {
        var h = new Harness();
        byte session = ConnectAsIrs(h);
        h.Receive(Frame(ArdopFrameType.Disc));
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        int sentBefore = h.Sent.Count;

        // The peer missed our END and repeats DISC: answer END from DISC state with
        // the previous session's ID (rule 1.6; ARQ.c:1138).
        h.Receive(Frame(ArdopFrameType.Disc));

        h.Sent.Count.Should().Be(sentBefore + 1);
        h.LastSent.Type.Should().Be(ArdopFrameType.End);
        h.LastSent.EncodedFrame[1].Should().Be((byte)(ArdopFrameType.End ^ session));
    }

    [Fact]
    public void Session_Times_Out_To_Id_Plus_Disc()
    {
        var h = new Harness(c => c.ArqTimeoutSeconds = 30);
        ConnectAsIrs(h);

        // Nothing decodable arrives for the timeout period (rule 1.8).
        h.Advance(32000);

        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Notes.Should().Contain("DISCONNECTED");
        var tail = h.Sent.TakeLast(2).ToList();
        tail[0].Type.Should().Be(ArdopFrameType.IdFrame, "timeout sends ID first (rule 4.0)");
        tail[1].Type.Should().Be(ArdopFrameType.Disc);
    }

    [Fact]
    public void Abort_Drops_Straight_To_Disc()
    {
        var h = new Harness();
        ConnectAsIrs(h);
        h.Engine.Abort(h.NowMs);
        h.Engine.State.Should().Be(ArdopProtocolState.Disc);
        h.Engine.IsConnected.Should().BeFalse();
    }

    // ------------------------------------------------------------------- ID

    [Fact]
    public void Iss_Inserts_An_Id_Frame_After_Nine_Minutes()
    {
        var h = new Harness(c => { c.FskOnly = true; c.ArqTimeoutSeconds = 240; });
        ConnectAsIss(h);
        h.Engine.EnqueueData(new byte[64]);
        h.Receive(Frame(0xF0));  // start data
        h.Sent.Count(r => r.Type == ArdopFrameType.IdFrame).Should().Be(0);

        // Age the session past the 9-minute ID point without letting the ARQ timeout
        // fire: keep ACKing data. (Clock jumps are fine — the engine only compares.)
        for (int i = 0; i < 5; i++)
        {
            h.Advance(115000, pollStepMs: 115000);
            h.Engine.EnqueueData(new byte[16]);
            h.Receive(Frame(0xF0));
        }

        h.Sent.Count(r => r.Type == ArdopFrameType.IdFrame).Should().BeGreaterThan(0,
            "an ID frame is inserted at least every 10 minutes of transmission (rule 4.0)");
    }

    // ------------------------------------------------------------------ PING

    [Fact]
    public void Ping_Is_Answered_With_PingAck_When_Enabled()
    {
        var h = new Harness();
        h.Receive(new ArdopDecodedFrame
        {
            Type = ArdopFrameType.Ping,
            Ok = true,
            Data = [],
            Quality = 88,
            Caller = "G8XYZ",
            Target = "M0ME",
            SnDb = 15,
        });

        h.LastSent.Type.Should().Be(ArdopFrameType.PingAck);
        h.Notes.Should().Contain("PINGREPLY");
        var decoded = ArdopFrameCodec.DecodePingAck(h.LastSent.EncodedFrame.AsSpan(2, 3));
        decoded.Should().NotBeNull();
        decoded!.Value.SnDb.Should().Be(15);
        decoded.Value.Quality.Should().Be(80, "quality is quantized to 10s (30-100)");
    }

    [Fact]
    public void Ping_Repeats_Stop_On_PingAck()
    {
        var h = new Harness();
        h.Engine.Ping(Harness.Station("G8XYZ"), repeats: 5, h.NowMs).Should().BeTrue();
        h.FlushTx();
        h.Advance(2500);
        h.Sent.Count(r => r.Type == ArdopFrameType.Ping).Should().BeGreaterThanOrEqualTo(2);

        h.Receive(new ArdopDecodedFrame
        {
            Type = ArdopFrameType.PingAck,
            Ok = true,
            Data = [],
            Quality = 80,
            PingAckSnDb = 10,
            PingAckQuality = 80,
        });
        int pingsAtAck = h.Sent.Count(r => r.Type == ArdopFrameType.Ping);

        h.Advance(10000);

        h.Sent.Count(r => r.Type == ArdopFrameType.Ping).Should().Be(pingsAtAck);
        h.Notes.Should().Contain("PINGACK 10 80");
    }

    // -------------------------------------------------------------- gearshift

    [Fact]
    public void Gearshift_Shifts_Down_After_Naks_And_Up_On_Quality()
    {
        var shifter = new ArdopGearshift();
        // 1000 Hz FSK ladder {0x4C, 0x4A}; fastStart lands on 0x4A.
        shifter.Initialize(1000, fskOnly: true, fastStart: true);
        shifter.CurrentFrameType.Should().Be(0x4A);

        // 0x4A has never worked: one NAK reverts immediately (DownNAKS = 1).
        shifter.RecordNak(50, bytesRemaining: 500).Should().BeTrue();
        shifter.NextTypeToSend(0).Should().Be(0x4D, "toggle against last-acked parity 0");
        shifter.CurrentFrameType.Should().Be(0x4C);
        shifter.ShiftDowns.Should().Be(1);

        // Two high-quality ACKs with data remaining beyond one frame → shift up.
        shifter.RecordAck(90, bytesRemaining: 500);
        shifter.PendingShift.Should().Be(0, "one ACK is not enough");
        shifter.RecordAck(90, bytesRemaining: 500);
        shifter.PendingShift.Should().Be(1, "avg 90 > threshold 80 with 2 ACKs");
        shifter.NextTypeToSend(0).Should().Be(0x4B);
        shifter.CurrentFrameType.Should().Be(0x4A);
        shifter.ShiftUps.Should().Be(1);
    }

    [Fact]
    public void Gearshift_Does_Not_Shift_Up_When_Remaining_Data_Fits()
    {
        var shifter = new ArdopGearshift();
        shifter.Initialize(1000, fskOnly: true, fastStart: false);
        shifter.CurrentFrameType.Should().Be(0x4C);

        shifter.RecordAck(95, bytesRemaining: 20);
        shifter.RecordAck(95, bytesRemaining: 20);

        shifter.PendingShift.Should().Be(0, "20 bytes fit one 32-byte 4FSK.500.100S frame");
    }

    [Fact]
    public void Gearshift_Worked_Mode_Needs_Two_Naks_To_Shift_Down()
    {
        var shifter = new ArdopGearshift();
        shifter.Initialize(1000, fskOnly: true, fastStart: true);
        shifter.RecordAck(70, 500);  // mode has now worked

        shifter.RecordNak(50, 500).Should().BeFalse("one NAK on a worked mode holds");
        shifter.RecordNak(50, 500).Should().BeTrue("the second NAK shifts down");
    }
}
