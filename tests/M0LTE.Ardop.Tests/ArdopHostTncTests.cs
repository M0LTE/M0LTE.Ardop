using System.Text;
using M0LTE.Ardop;
using M0LTE.Ardop.Arq;
using M0LTE.Ardop.Host;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// The ardopcf host-command surface, hermetically: every reply, fault and
/// notification format is pinned to what <c>ProcessCommandFromHost</c>
/// (HostInterface.c, git a7c9228) produces — the live transcript test
/// (<see cref="ArdopHostLiveTests"/>) then diffs the same script against a running
/// ardopcf. Plus the FEC-mode send path (real modulated frames decoded back), the
/// RXO monitor (a third-party session's frames reported with the decoded session
/// ID), and a complete ARQ session driven end-to-end through two host TNCs.
/// </summary>
public class ArdopHostTncTests
{
    private sealed class Host : IAsyncDisposable
    {
        public ArdopHostTnc Tnc { get; }

        public List<string> Commands { get; } = [];

        public List<(string Tag, byte[] Data)> Data { get; } = [];

        public List<short[]> Transmitted { get; } = [];

        public Host()
        {
            Tnc = new ArdopHostTnc(
                captureDevice: "plughw:9,0", playbackDevice: "plughw:9,0",
                randomSeed: 42, version: "pdn-soundmodem_test");
            Tnc.CommandToHost += line =>
            {
                lock (Commands)
                {
                    Commands.Add(line);
                }
            };
            Tnc.DataToHost += (tag, data) =>
            {
                lock (Data)
                {
                    Data.Add((tag, data));
                }
            };
            Tnc.Transmitter = audio =>
            {
                lock (Transmitted)
                {
                    Transmitted.Add(audio);
                }

                return Task.CompletedTask;
            };
        }

        public List<string> Exchange(string command)
        {
            int before;
            lock (Commands)
            {
                before = Commands.Count;
            }

            Tnc.ProcessCommand(command);
            lock (Commands)
            {
                return Commands[before..];
            }
        }

        public bool WaitForCommand(Func<IReadOnlyList<string>, bool> predicate, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (Commands)
                {
                    if (predicate(Commands))
                    {
                        return true;
                    }
                }

                Thread.Sleep(20);
            }

            lock (Commands)
            {
                return predicate(Commands);
            }
        }

        public ValueTask DisposeAsync() => Tnc.DisposeAsync();
    }

    // ------------------------------------------------------- reply/fault formats

    [Fact]
    public async Task Initialize_Clears_Buffer_Then_Echoes()
    {
        await using var host = new Host();
        host.Exchange("INITIALIZE").Should().Equal("BUFFER 0", "INITIALIZE");
    }

    [Fact]
    public async Task Boot_Defaults_Match_Ardopcf()
    {
        await using var host = new Host();
        host.Exchange("STATE").Should().Equal("STATE DISC");
        // ARQ, not the FEC of ardopcf's variable initializer — ardopmain() sets ARQ
        // at startup (confirmed against the live transcript).
        host.Exchange("PROTOCOLMODE").Should().Equal("PROTOCOLMODE ARQ");
        host.Exchange("ARQBW").Should().Equal("ARQBW 2000MAX");
        host.Exchange("CALLBW").Should().Equal("CALLBW UNDEFINED");
        host.Exchange("ARQTIMEOUT").Should().Equal("ARQTIMEOUT 120");
        host.Exchange("AUTOBREAK").Should().Equal("AUTOBREAK TRUE");
        host.Exchange("BUFFER").Should().Equal("BUFFER 0");
        host.Exchange("BUSYBLOCK").Should().Equal("BUSYBLOCK FALSE");
        host.Exchange("BUSYDET").Should().Equal("BUSYDET 5");
        host.Exchange("CWID").Should().Equal("CWID FALSE");
        host.Exchange("DRIVELEVEL").Should().Equal("DRIVELEVEL 100");
        host.Exchange("ENABLEPINGACK").Should().Equal("ENABLEPINGACK TRUE");
        host.Exchange("EXTRADELAY").Should().Equal("EXTRADELAY 0");
        host.Exchange("FASTSTART").Should().Equal("FASTSTART TRUE");
        host.Exchange("FECID").Should().Equal("FECID FALSE");
        host.Exchange("FECMODE").Should().Equal("FECMODE 4FSK.500.100");
        host.Exchange("FECREPEATS").Should().Equal("FECREPEATS 0");
        host.Exchange("FSKONLY").Should().Equal("FSKONLY FALSE");
        host.Exchange("GRIDSQUARE").Should().Equal("GRIDSQUARE ");
        host.Exchange("LEADER").Should().Equal("LEADER 240");
        host.Exchange("LISTEN").Should().Equal("LISTEN TRUE");
        host.Exchange("MONITOR").Should().Equal("MONITOR TRUE");
        host.Exchange("MYAUX").Should().Equal("MYAUX");
        host.Exchange("MYCALL").Should().Equal("MYCALL ");
        host.Exchange("SQUELCH").Should().Equal("SQUELCH 5");
        host.Exchange("TRAILER").Should().Equal("TRAILER 20");
        host.Exchange("TUNINGRANGE").Should().Equal("TUNINGRANGE 100");
        host.Exchange("USE600MODES").Should().Equal("USE600MODES FALSE");
        host.Exchange("VERSION").Should().Equal("VERSION pdn-soundmodem_test");
    }

    [Fact]
    public async Task Set_Replies_Use_The_Now_Form()
    {
        await using var host = new Host();
        host.Exchange("MYCALL m0lte-7").Should().Equal("MYCALL now M0LTE-7");
        host.Exchange("GRIDSQUARE io91wm").Should().Equal(
            ["GRIDSQUARE now IO91wm"], "the canonical form lowercases the subsquare pair (Locator.c)");
        host.Exchange("ARQBW 500MAX").Should().Equal("ARQBW now 500MAX");
        host.Exchange("CALLBW 1000FORCED").Should().Equal("CALLBW now 1000FORCED");
        host.Exchange("ARQTIMEOUT 90").Should().Equal("ARQTIMEOUT now 90");
        host.Exchange("LISTEN false").Should().Equal("LISTEN now FALSE");
        host.Exchange("AUTOBREAK FALSE").Should().Equal("AUTOBREAK now FALSE");
        host.Exchange("BUSYDET 0").Should().Equal("BUSYDET now 0");
        host.Exchange("DRIVELEVEL 50").Should().Equal("DRIVELEVEL now 50");
        host.Exchange("EXTRADELAY 100").Should().Equal("EXTRADELAY now 100");
        host.Exchange("FECMODE 4PSK.1000.100").Should().Equal("FECMODE now 4PSK.1000.100");
        host.Exchange("FECREPEATS 2").Should().Equal("FECREPEATS now 2");
        host.Exchange("FSKONLY TRUE").Should().Equal("FSKONLY now TRUE");
        host.Exchange("LEADER 245").Should().Equal(["LEADER now 250"], "the length rounds up to 10 ms");
        host.Exchange("MYAUX g8bbb,m0aaa-2").Should().Equal("MYAUX now G8BBB,M0AAA-2");
        host.Exchange("MYAUX").Should().Equal("MYAUX G8BBB,M0AAA-2");
        host.Exchange("SQUELCH 7").Should().Equal("SQUELCH now 7");
        host.Exchange("TRAILER 35").Should().Equal("TRAILER now 40");
        host.Exchange("TUNINGRANGE 50").Should().Equal("TUNINGRANGE now 50");
        host.Exchange("CWID ONOFF").Should().Equal("CWID now ONOFF");
        host.Exchange("CWID").Should().Equal("CWID ONOFF");
    }

    [Fact]
    public async Task Faults_Match_Ardopcf_Formats()
    {
        await using var host = new Host();
        // ardopcf's misspelling is part of the wire format (HostInterface.c:1544).
        host.Exchange("NOSUCHCMD").Should().Equal("FAULT CMD NOSUCHCMD not recoginized");
        host.Exchange("ARQBW 300MAX").Should().Equal("FAULT Syntax Err: ARQBW 300MAX");
        host.Exchange("ARQBW UNDEFINED").Should().Equal(
            ["FAULT Syntax Err: ARQBW UNDEFINED"], "UNDEFINED is CALLBW-only");
        host.Exchange("ARQTIMEOUT 20").Should().Equal("FAULT Syntax Err: ARQTIMEOUT 20");
        host.Exchange("AUTOBREAK MAYBE").Should().Equal("FAULT Syntax Err: AUTOBREAK MAYBE");
        host.Exchange("BUFFER 5").Should().Equal("FAULT Syntax Err: BUFFER 5");
        host.Exchange("STATE X").Should().Equal("FAULT Syntax Err: STATE X");
        host.Exchange("FECSEND").Should().Equal("FAULT Syntax Err: FECSEND");
        host.Exchange("FECMODE 4FSK.9999.1").Should().Equal("FAULT Syntax Err: FECMODE 4FSK.9999.1");
        host.Exchange("LEADER 20").Should().Equal("FAULT Syntax Err: LEADER 20");
        host.Exchange("ARQCALL").Should().Equal("FAULT Syntax Err: ARQCALL: expected \"TARGET NATTEMPTS\"");
        host.Exchange("ARQCALL G8BBB").Should().Equal(
            "FAULT Syntax Err: ARQCALL G8BBB: expected \"TARGET NATTEMPTS\"");
        host.Exchange("ARQCALL G8BBB X").Should().Equal(
            "FAULT Syntax Err: ARQCALL G8BBB X: NATTEMPTS not valid as number");
        host.Exchange("ARQCALL G8BBB 0").Should().Equal(
            "FAULT Syntax Err: ARQCALL G8BBB 0: NATTEMPTS must be positive");
        host.Exchange("ARQCALL G8BBB 5").Should().Equal("FAULT MYCALL not set");
        host.Exchange("SENDID").Should().Equal("FAULT MYCALL not set");
        host.Exchange("TWOTONETEST").Should().Equal("FAULT MYCALL not set");
        host.Exchange("FECSEND TRUE").Should().Equal("FAULT MYCALL not set");
        host.Exchange("GRIDSQUARE ZZ99").Should().Equal(
            "FAULT Syntax Err: GRIDSQUARE ZZ99: locator has invalid field (first pair)");
        host.Exchange("GRIDSQUARE IO9").Should().Equal(
            "FAULT Syntax Err: GRIDSQUARE IO9: locator must be 2, 4, 6, or 8 characters");
        host.Exchange("RADIOFREQ").Should().Equal("FAULT RADIOFREQ command string missing");
        host.Exchange("RADIOPTTON 1122").Should().Equal("FAULT RADIOPTTON command CAT Port not defined");
    }

    [Fact]
    public async Task Arqcall_In_Fec_Mode_Faults_And_Disconnect_Is_Ignored_When_Disc()
    {
        await using var host = new Host();
        host.Exchange("MYCALL M0AAA").Should().Equal("MYCALL now M0AAA");
        host.Exchange("PROTOCOLMODE FEC").Should().Equal("PROTOCOLMODE now FEC");
        host.Exchange("ARQCALL G8BBB 5").Should().Equal("FAULT Not from mode FEC");
        host.Exchange("PROTOCOLMODE RXO").Should().Equal("PROTOCOLMODE now RXO");
        host.Exchange("ARQCALL G8BBB 5").Should().Equal("FAULT Not from mode RXO");
        // ardopcf's PING handler misses its goto in RXO (HostInterface.c:1013):
        // the echo goes out and the FAULT follows.
        host.Exchange("PING G8BBB 2").Should().Equal("PING G8BBB 2", "FAULT Not from mode RXO");
        host.Exchange("DISCONNECT").Should().Equal("DISCONNECT IGNORED");
        host.Exchange("ABORT").Should().Equal("ABORT");
    }

    [Fact]
    public async Task Arqcall_Echoes_The_Original_Casing_And_Transmits_A_ConReq()
    {
        await using var host = new Host();
        host.Exchange("PROTOCOLMODE ARQ").Should().Contain("PROTOCOLMODE now ARQ");
        host.Exchange("MYCALL M0AAA").Should().Equal("MYCALL now M0AAA");
        var replies = host.Exchange("arqcall g8bbb 5");
        replies[0].Should().Be("arqcall g8bbb 5", "the echo keeps the host's casing (cmdCopy)");
        replies.Should().Contain("NEWSTATE ISS ", "the trailing space is ardopcf's");

        host.WaitForCommand(c => c.Contains("PTT TRUE"), 2000).Should().BeTrue();
        host.WaitForCommand(c => c.Contains("PTT FALSE"), 2000).Should().BeTrue();
        lock (host.Transmitted)
        {
            host.Transmitted.Should().HaveCountGreaterThanOrEqualTo(1);
        }

        // The burst on the wire is a real ConReq500M... 2000MAX default → ConReq2000M.
        var demod = new ArdopDemodulator();
        var heard = new List<ArdopDecodedFrame>();
        demod.FrameDecoded += heard.Add;
        lock (host.Transmitted)
        {
            demod.ProcessSamples(host.Transmitted[0]);
        }

        demod.ProcessSamples(new short[4800]);
        heard.Should().ContainSingle(f => f.Ok && f.Type == ArdopFrameType.ConReq2000M
            && f.Caller == "M0AAA" && f.Target == "G8BBB");
    }

    [Fact]
    public async Task Protocolmode_Accepts_Anything_Like_Ardopcf()
    {
        await using var host = new Host();
        // The reference's validation can never trip (HostInterface.c:1065); unknown
        // values fall back to ARQ but the reply echoes what was sent.
        host.Exchange("PROTOCOLMODE BOGUS").Should().Equal("PROTOCOLMODE now BOGUS");
        host.Exchange("PROTOCOLMODE").Should().Equal("PROTOCOLMODE ARQ");
    }

    [Fact]
    public async Task Data_In_Arq_Mode_Buffers_And_Purges()
    {
        await using var host = new Host();
        host.Exchange("PROTOCOLMODE ARQ");
        host.Tnc.AcceptHostData(new byte[100]);
        host.WaitForCommand(c => c.Contains("BUFFER 100"), 1000).Should().BeTrue();
        host.Exchange("BUFFER").Should().Equal("BUFFER 100");
        host.Exchange("DATATOSEND").Should().Equal("DATATOSEND 100");
        host.Exchange("PURGEBUFFER").Should().Equal("BUFFER 0", "PURGEBUFFER");
    }

    [Fact]
    public async Task Twotonetest_Transmits_Five_Seconds_Of_Leader_Tones()
    {
        await using var host = new Host();
        host.Exchange("MYCALL M0AAA");
        host.Exchange("TWOTONETEST").Should().Equal("TWOTONETEST");
        host.WaitForCommand(c => c.Contains("PTT FALSE"), 2000).Should().BeTrue();
        lock (host.Transmitted)
        {
            host.Transmitted.Should().ContainSingle();
            // 250 leader symbols × 240 samples (fixed filter latency aside).
            host.Transmitted[0].Length.Should().BeInRange(59000, 62000);
        }
    }

    // ------------------------------------------------------------------ FEC mode

    [Fact]
    public async Task Fecsend_Transmits_Decodable_Frames_And_Counts_The_Buffer_Down()
    {
        await using var host = new Host();
        host.Exchange("MYCALL M0AAA");
        host.Exchange("PROTOCOLMODE FEC");
        host.Exchange("FECMODE 4FSK.500.100");

        byte[] payload = new byte[100];
        new Random(5).NextBytes(payload);
        host.Tnc.AcceptHostData(payload);
        host.WaitForCommand(c => c.Contains("BUFFER 100"), 1000).Should().BeTrue();

        // NEWSTATE precedes the reply, as in ardopcf (StartFEC sets FECSend state
        // before SendReplyToHost runs).
        host.Exchange("FECSEND TRUE").Should().Equal("NEWSTATE FECSEND ", "FECSEND now TRUE");
        host.WaitForCommand(c => c.Contains("NEWSTATE DISC "), 15000)
            .Should().BeTrue("the send loop must drain and return to DISC");

        lock (host.Commands)
        {
            host.Commands.Should().Contain("BUFFER 36", "the first 64-byte frame consumes the buffer head");
            host.Commands.Should().Contain("BUFFER 0");
        }

        // Both bursts decode as FEC data with session ID 0xFF, toggled even/odd.
        var rxHost = new Host();
        await using (rxHost.ConfigureAwait(false))
        {
            rxHost.Exchange("PROTOCOLMODE FEC");
            lock (host.Transmitted)
            {
                foreach (short[] burst in host.Transmitted)
                {
                    Feed(rxHost.Tnc, burst);
                    Feed(rxHost.Tnc, new short[4800]);
                }
            }

            Feed(rxHost.Tnc, new short[4800]);
            lock (rxHost.Data)
            {
                rxHost.Data.Should().HaveCount(2);
                rxHost.Data.Should().OnlyContain(d => d.Tag == "FEC");
                rxHost.Data.SelectMany(d => d.Data).Should().Equal(payload);
            }
        }
    }

    [Fact]
    public async Task Fec_Mode_Reports_Heard_ConReqs_As_Arq_Display_Text()
    {
        await using var host = new Host();
        host.Exchange("PROTOCOLMODE FEC");

        ArdopStationId.TryParse("M0AAA", out var caller).Should().BeTrue();
        ArdopStationId.TryParse("G8BBB", out var target).Should().BeTrue();
        byte[] conReq = ArdopFrameCodec.EncodeConReq(ArdopFrameType.ConReq500M, caller, target);
        Feed(host.Tnc, new ArdopModulator().Modulate(conReq));
        Feed(host.Tnc, new short[4800]);

        lock (host.Data)
        {
            host.Data.Should().ContainSingle(d => d.Tag == "ARQ");
            Encoding.ASCII.GetString(host.Data[0].Data)
                .Should().Be(" [ConReq500M: M0AAA > G8BBB]", "ProcessUnconnectedConReqFrame's display format");
        }
    }

    // ------------------------------------------------------------------ RXO mode

    [Fact]
    public async Task Rxo_Monitor_Decodes_A_Third_Party_Session_With_Its_Session_Id()
    {
        // A real two-station ARQ session (the hermetic session harness), with the
        // monitor summing both directions — a shared-channel listener that knows
        // neither callsign nor the session ID.
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
        var a = new ArdopArqStation(configA, randomSeed: 11);
        var b = new ArdopArqStation(configB, randomSeed: 22);

        byte[] payload = new byte[40];
        new Random(9).NextBytes(payload);
        a.Engine.EnqueueData(payload);
        a.Engine.ConnectRequest(Station("G8BBB"), 0).Should().BeTrue();

        var mixed = new List<float>();
        var aToB = new short[240];
        var bToA = new short[240];
        bool discRequested = false;
        bool disconnected = false;
        for (int i = 0; i < 60_000 / 20 && !disconnected; i++)
        {
            short[] fromA = a.Step(bToA);
            short[] fromB = b.Step(aToB);
            for (int s = 0; s < 240; s++)
            {
                mixed.Add((fromA[s] + fromB[s]) / 32768f);
            }

            aToB = fromA;
            bToA = fromB;
            if (a.Engine.IsConnected && a.Engine.OutboundCount == 0 && !discRequested)
            {
                a.Engine.Disconnect(a.NowMs);
                discRequested = true;
            }

            disconnected = discRequested
                && a.Engine.State == ArdopProtocolState.Disc
                && b.Engine.State == ArdopProtocolState.Disc
                && !a.IsTransmitting && !b.IsTransmitting;
        }

        disconnected.Should().BeTrue("the monitored session must complete");

        await using var monitor = new Host();
        monitor.Exchange("PROTOCOLMODE RXO").Should().Equal("PROTOCOLMODE now RXO");
        monitor.Tnc.ProcessReceive([.. mixed]);
        monitor.Tnc.ProcessReceive(new float[4800]);

        byte sessionId = ArdopCrc.SessionId("M0AAA", "G8BBB");
        List<string> status;
        lock (monitor.Commands)
        {
            status = [.. monitor.Commands.Where(c => c.StartsWith("STATUS [RXO", StringComparison.Ordinal))];
        }

        // The connect handshake, the data, its ACK and the teardown all decode; the
        // decoded session ID switches from 0xFF (ConReq) to the session's own.
        status.Should().Contain("STATUS [RXO FF] ConReq500M frame received OK.");
        status.Should().Contain($"STATUS [RXO {sessionId:X2}] ConAck500 frame received OK.");
        status.Should().Contain(s =>
            s.StartsWith($"STATUS [RXO {sessionId:X2}] 4FSK.") && s.EndsWith("frame received OK."),
            "the session's data frames decode under its session ID");
        status.Should().Contain($"STATUS [RXO {sessionId:X2}] DataACK frame received OK.");
        status.Should().Contain($"STATUS [RXO {sessionId:X2}] DISC frame received OK.");
        status.Should().Contain(s => s.Contains("END frame received OK."));
    }

    // ------------------------------------------------- full session, host to host

    [Fact]
    public async Task Two_Host_Tncs_Run_A_Complete_Arq_Session_With_Failsafe_Teardown()
    {
        // Two complete virtual TNCs joined by an instant audio cable: the Pat-shaped
        // command sequences drive a real connect, byte-exact transfer both ways, and
        // the host-link failsafe tears the session down (spec §8.1.2.1.4).
        await using var alice = new Host();
        await using var bob = new Host();
        alice.Tnc.Transmitter = audio =>
        {
            Feed(bob.Tnc, audio);
            return Task.CompletedTask;
        };
        bob.Tnc.Transmitter = audio =>
        {
            Feed(alice.Tnc, audio);
            return Task.CompletedTask;
        };

        using var polling = new CancellationTokenSource();
        Task pollLoop = Task.Run(async () =>
        {
            while (!polling.IsCancellationRequested)
            {
                alice.Tnc.Poll();
                bob.Tnc.Poll();
                await Task.Delay(10).ConfigureAwait(false);
            }
        });

        foreach (string command in (string[])
            ["INITIALIZE", "PROTOCOLMODE ARQ", "ARQTIMEOUT 90", "FSKONLY TRUE", "ARQBW 500MAX"])
        {
            alice.Tnc.ProcessCommand(command);
            bob.Tnc.ProcessCommand(command);
        }

        alice.Tnc.ProcessCommand("MYCALL M0AAA");
        alice.Tnc.ProcessCommand("LISTEN false");
        bob.Tnc.ProcessCommand("MYCALL G8BBB");
        bob.Tnc.ProcessCommand("LISTEN true");

        byte[] payload = new byte[48];
        new Random(3).NextBytes(payload);
        alice.Tnc.AcceptHostData(payload);
        alice.Tnc.ProcessCommand("ARQCALL G8BBB 5");

        bob.WaitForCommand(c => c.Contains("TARGET G8BBB"), 30000)
            .Should().BeTrue("the callee reports which of its calls was asked for, before CONNECTED");
        bob.WaitForCommand(c => c.Any(x => x.StartsWith("CONNECTED M0AAA 500", StringComparison.Ordinal)), 30000)
            .Should().BeTrue();
        alice.WaitForCommand(c => c.Any(x => x.StartsWith("CONNECTED G8BBB 500", StringComparison.Ordinal)), 30000)
            .Should().BeTrue();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 60000)
        {
            lock (bob.Data)
            {
                if (bob.Data.Where(d => d.Tag == "ARQ").SelectMany(d => d.Data).Count() >= payload.Length)
                {
                    break;
                }
            }

            Thread.Sleep(50);
        }

        lock (bob.Data)
        {
            bob.Data.Where(d => d.Tag == "ARQ").SelectMany(d => d.Data)
                .Should().Equal(payload, "the ARQ payload must arrive byte-exact");
        }

        // The failsafe: the caller's host vanishes mid-session.
        alice.Tnc.HostLinkLost();
        alice.WaitForCommand(c => c.Contains("DISCONNECTED"), 60000)
            .Should().BeTrue("losing the host must end the session (spec §8.1.2.1.4)");
        bob.WaitForCommand(c => c.Contains("DISCONNECTED"), 60000).Should().BeTrue();
        alice.Tnc.Engine.State.Should().Be(ArdopProtocolState.Disc);
        bob.Tnc.Engine.State.Should().Be(ArdopProtocolState.Disc);

        await polling.CancelAsync();
        await pollLoop;
    }

    private static void Feed(ArdopHostTnc tnc, ReadOnlySpan<short> audio)
    {
        var floats = new float[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            floats[i] = audio[i] / 32768f;
        }

        tnc.ProcessReceive(floats);
    }

    private static ArdopStationId Station(string call)
    {
        ArdopStationId.TryParse(call, out var id).Should().BeTrue();
        return id;
    }
}
