using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using M0LTE.Ardop.Host;

namespace M0LTE.Ardop.Tests;

/// <summary>
/// The TCP transport of the host interface: CR-terminated command framing on the
/// command socket, [2-byte big-endian length] blocks on the data socket with the
/// 3-character TNC→host type tags, connection replacement, and the host-link
/// failsafe hook — the byte formats of ardopcf's <c>TCPHostInterface.c</c>.
/// </summary>
public class ArdopHostServerTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public ArdopHostServer Server { get; }

        public TcpClient Command { get; }

        public TcpClient Data { get; }

        private readonly List<byte> _commandBytes = [];
        private readonly List<byte> _dataBytes = [];

        public Rig()
        {
            var tnc = new ArdopHostTnc(version: "pdn-soundmodem_test")
            {
                Transmitter = _ => Task.CompletedTask,
            };
            Server = new ArdopHostServer(tnc, commandPort: 0, ownsTnc: true);
            Server.Start();
            Command = new TcpClient("127.0.0.1", Server.LocalCommandPort);
            Data = new TcpClient("127.0.0.1", Server.LocalDataPort);
            Server.WaitForConnectionsAsync().GetAwaiter().GetResult();
            _ = Pump(Command, _commandBytes);
            _ = Pump(Data, _dataBytes);
        }

        private static async Task Pump(TcpClient client, List<byte> sink)
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int got = await client.GetStream().ReadAsync(buffer);
                    if (got == 0)
                    {
                        return;
                    }

                    lock (sink)
                    {
                        sink.AddRange(buffer.AsSpan(0, got));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void SendCommand(string text) =>
            Command.GetStream().Write(Encoding.ASCII.GetBytes(text));

        public void SendData(byte[] payload)
        {
            var framed = new byte[payload.Length + 2];
            framed[0] = (byte)(payload.Length >> 8);
            framed[1] = (byte)payload.Length;
            payload.CopyTo(framed, 2);
            Data.GetStream().Write(framed);
        }

        /// <summary>Reply lines received so far (CR-separated).</summary>
        public List<string> CommandLines()
        {
            lock (_commandBytes)
            {
                string text = Encoding.ASCII.GetString([.. _commandBytes]);
                return [.. text.Split('\r', StringSplitOptions.RemoveEmptyEntries)];
            }
        }

        public bool WaitForLine(string line, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (CommandLines().Contains(line))
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return CommandLines().Contains(line);
        }

        public byte[] DataBytes()
        {
            lock (_dataBytes)
            {
                return [.. _dataBytes];
            }
        }

        public async ValueTask DisposeAsync()
        {
            Command.Dispose();
            Data.Dispose();
            await Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Commands_Are_Cr_Terminated_And_Replies_Come_Back_Cr_Terminated()
    {
        await using var rig = new Rig();
        rig.SendCommand("INITIALIZE\r");
        rig.WaitForLine("INITIALIZE", 5000).Should().BeTrue();
        rig.CommandLines().Should().Equal("BUFFER 0", "INITIALIZE");

        // Two commands in one segment, and one split across two segments.
        rig.SendCommand("STATE\rMYCALL M0A");
        rig.WaitForLine("STATE DISC", 5000).Should().BeTrue();
        rig.SendCommand("AA\r");
        rig.WaitForLine("MYCALL now M0AAA", 5000).Should().BeTrue();
    }

    [Fact]
    public async Task Rdy_Is_Swallowed_Without_Reply()
    {
        await using var rig = new Rig();
        rig.SendCommand("RDY\rVERSION\r");
        rig.WaitForLine("VERSION pdn-soundmodem_test", 5000).Should().BeTrue();
        rig.CommandLines().Should().Equal("VERSION pdn-soundmodem_test");
    }

    [Fact]
    public async Task Host_Data_Blocks_Reach_The_Buffer_And_Buffer_Notifications_Flow()
    {
        await using var rig = new Rig();
        rig.SendCommand("PROTOCOLMODE ARQ\r");
        rig.WaitForLine("PROTOCOLMODE now ARQ", 5000).Should().BeTrue();

        rig.SendData(new byte[70]);
        rig.WaitForLine("BUFFER 70", 5000).Should().BeTrue("loading data must notify BUFFER");

        // Two blocks in one write.
        rig.SendData(new byte[10]);
        rig.SendData(new byte[5]);
        rig.WaitForLine("BUFFER 85", 5000).Should().BeTrue();
    }

    [Fact]
    public async Task Tnc_To_Host_Data_Is_Length_Prefixed_And_Tagged()
    {
        await using var rig = new Rig();
        rig.SendCommand("PROTOCOLMODE FEC\r");
        rig.WaitForLine("PROTOCOLMODE now FEC", 5000).Should().BeTrue();

        // A real FEC burst from a second TNC decoded by this one, delivered on the
        // data socket as [len]["FEC"][payload].
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] encoded = M0LTE.Ardop.ArdopFrameCodec.EncodeDataFrame(0x4A, payload, 0xFF);
        short[] burst = new M0LTE.Ardop.ArdopModulator().Modulate(encoded);
        var floats = new float[burst.Length + 4800];
        for (int i = 0; i < burst.Length; i++)
        {
            floats[i] = burst[i] / 32768f;
        }

        rig.Server.Tnc.ProcessReceive(floats);

        var sw = Stopwatch.StartNew();
        while (rig.DataBytes().Length < payload.Length + 5 && sw.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(20);
        }

        byte[] framed = rig.DataBytes();
        framed.Length.Should().Be(payload.Length + 5);
        ((framed[0] << 8) | framed[1]).Should().Be(payload.Length + 3, "the length includes the 3-byte tag");
        Encoding.ASCII.GetString(framed, 2, 3).Should().Be("FEC");
        framed[5..].Should().Equal(payload);
    }

    [Fact]
    public async Task A_New_Command_Connection_Replaces_The_Old_One()
    {
        await using var rig = new Rig();
        rig.SendCommand("STATE\r");
        rig.WaitForLine("STATE DISC", 5000).Should().BeTrue();

        using var second = new TcpClient("127.0.0.1", rig.Server.LocalCommandPort);
        second.GetStream().Write(Encoding.ASCII.GetBytes("VERSION\r"));
        var buffer = new byte[256];
        second.ReceiveTimeout = 5000;
        int got = second.GetStream().Read(buffer);
        Encoding.ASCII.GetString(buffer, 0, got).Should().Be("VERSION pdn-soundmodem_test\r");
    }
}
