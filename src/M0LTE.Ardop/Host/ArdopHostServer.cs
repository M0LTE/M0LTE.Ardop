using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.Radio.Audio;

namespace M0LTE.Ardop.Host;

/// <summary>
/// The ardopcf-compatible TCP host interface (<c>TCPHostInterface.c</c>, git a7c9228,
/// MIT, © 2014-2024 Rick Muething, John Wiseman, Peter LaRue): a command socket
/// (default 8515) carrying CR-terminated ASCII commands, replies and asynchronous
/// notifications, and a data socket (always command port + 1) carrying
/// <c>[2-byte big-endian length][payload]</c> blocks — TNC→host payloads prefixed
/// with a 3-character type tag (ARQ/FEC/ERR/IDF) inside the length. Drop-in for
/// hosts that speak ardopcf's TCP interface (Pat, Winlink Express, ARIM, gARIM,
/// hamChat). Protocol logic lives in <see cref="ArdopHostTnc"/>.
/// </summary>
/// <remarks>
/// One host at a time per socket, as in ardopcf: a new connection replaces the
/// previous one (ours closes the old socket where ardopcf leaks it). A dropped
/// command or data connection triggers the host-link failsafe
/// (<see cref="ArdopHostTnc.HostLinkLost"/> — abort TX, revert to receive, spec
/// §8.1.2.1.4). The server also runs the TNC's 20 ms protocol-timer poll.
/// </remarks>
public sealed class ArdopHostServer : IAsyncDisposable
{
    private readonly ArdopHostTnc _tnc;
    private readonly bool _ownsTnc;
    private readonly TcpListener _commandListener;
    private readonly TcpListener _dataListener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _commandWriteLock = new();
    private readonly object _dataWriteLock = new();
    private readonly List<Task> _loops = [];
    private TcpClient? _commandClient;
    private TcpClient? _dataClient;
    private Thread? _receivePump;
    private readonly TaskCompletionSource _commandAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dataAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a host server over an existing TNC.</summary>
    /// <param name="tnc">The virtual TNC (its <see cref="ArdopHostTnc.Transmitter"/>
    /// and receive feed must be bound by the caller).</param>
    /// <param name="commandPort">Command socket port; the data socket listens on
    /// <paramref name="commandPort"/> + 1 (0 = ephemeral pair, see
    /// <see cref="LocalCommandPort"/>/<see cref="LocalDataPort"/>).</param>
    /// <param name="bind">Bind address; loopback by default.</param>
    /// <param name="ownsTnc">Dispose the TNC with the server.</param>
    public ArdopHostServer(ArdopHostTnc tnc, int commandPort = 8515, IPAddress? bind = null, bool ownsTnc = false)
    {
        ArgumentNullException.ThrowIfNull(tnc);
        _tnc = tnc;
        _ownsTnc = ownsTnc;
        IPAddress address = bind ?? IPAddress.Loopback;
        _commandListener = new TcpListener(address, commandPort);
        _dataListener = new TcpListener(address, commandPort == 0 ? 0 : commandPort + 1);
        // SO_REUSEADDR as in ardopcf (OpenSocket4, TCPHostInterface.c:505): a
        // restarted TNC must be able to rebind its well-known ports immediately.
        _commandListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _dataListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        tnc.CommandToHost += SendCommandLine;
        tnc.DataToHost += SendTaggedData;
    }

    /// <summary>
    /// Creates the TNC + server pair driven by an audio device: receive audio is pumped in
    /// from <paramref name="input"/>, and each transmit burst keys <paramref name="ptt"/>,
    /// plays through <paramref name="output"/>, drains, then unkeys. All three must run at
    /// ARDOP's native 12 kHz (<see cref="ArdopModulator.SampleRate"/>). ARDOP runs its own
    /// channel discipline (ARQ timing, leader budgets), so give it a dedicated device.
    /// </summary>
    /// <remarks>
    /// The receive pump is a background thread that blocks in <see cref="IAudioInput.Read"/>;
    /// dispose the server and then close <paramref name="input"/> to stop it promptly. Each
    /// burst transmits on a thread-pool thread (the <see cref="IAudioOutput.Write"/>/
    /// <see cref="IAudioOutput.Drain"/> block until the burst has left the device).
    /// </remarks>
    public static ArdopHostServer ForAudio(
        IAudioInput input,
        IAudioOutput output,
        IPttControl ptt,
        int commandPort = 8515,
        IPAddress? bind = null,
        string audioDevice = "default")
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ptt);
        if (input.SampleRate != ArdopModulator.SampleRate || output.SampleRate != ArdopModulator.SampleRate)
        {
            throw new ArgumentException(
                $"ARDOP needs {ArdopModulator.SampleRate} Hz audio "
                + $"(input {input.SampleRate}, output {output.SampleRate})");
        }

        var tnc = new ArdopHostTnc(captureDevice: audioDevice, playbackDevice: audioDevice)
        {
            Transmitter = audio => Task.Run(() =>
            {
                var floats = new float[audio.Length];
                for (int i = 0; i < audio.Length; i++)
                {
                    floats[i] = audio[i] / 32768f;
                }

                ptt.Key();
                try
                {
                    output.Write(floats);
                    output.Drain();
                }
                finally
                {
                    ptt.Unkey();
                }
            }),
        };

        var server = new ArdopHostServer(tnc, commandPort, bind, ownsTnc: true);
        server.StartReceivePump(input);
        return server;
    }

    private void StartReceivePump(IAudioInput input)
    {
        _receivePump = new Thread(() =>
        {
            var buffer = new float[ArdopModulator.SampleRate / 10]; // ~100 ms blocks
            while (!_stopping.IsCancellationRequested)
            {
                int got = input.Read(buffer);
                if (got <= 0)
                {
                    break; // source closing
                }

                _tnc.ProcessReceive(buffer.AsSpan(0, got));
            }
        })
        {
            IsBackground = true,
            Name = "ardop-rx-pump",
        };
        _receivePump.Start();
    }

    /// <summary>The TNC behind this server.</summary>
    public ArdopHostTnc Tnc => _tnc;

    /// <summary>The bound command port (useful when constructed with port 0).</summary>
    public int LocalCommandPort => ((IPEndPoint)_commandListener.LocalEndpoint).Port;

    /// <summary>The bound data port.</summary>
    public int LocalDataPort => ((IPEndPoint)_dataListener.LocalEndpoint).Port;

    /// <summary>Starts listening and the protocol-timer poll.</summary>
    public void Start()
    {
        _commandListener.Start();
        _dataListener.Start();
        _loops.Add(AcceptLoopAsync(_commandListener, isData: false));
        _loops.Add(AcceptLoopAsync(_dataListener, isData: true));
        _loops.Add(PollLoopAsync());
    }

    /// <summary>Completes when both a command and a data client have connected.</summary>
    public Task WaitForConnectionsAsync(CancellationToken ct = default) =>
        Task.WhenAll(_commandAccepted.Task.WaitAsync(ct), _dataAccepted.Task.WaitAsync(ct));

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                _tnc.Poll();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool isData)
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                client.NoDelay = true;
                TcpClient? previous;
                if (isData)
                {
                    lock (_dataWriteLock)
                    {
                        previous = _dataClient;
                        _dataClient = client;
                    }
                }
                else
                {
                    lock (_commandWriteLock)
                    {
                        previous = _commandClient;
                        _commandClient = client;
                    }
                }

                previous?.Dispose();
                if (isData) _dataAccepted.TrySetResult(); else _commandAccepted.TrySetResult();
                _ = isData ? ServeDataAsync(client) : ServeCommandAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ProcessReceivedControl (TCPHostInterface.c:285): CR-terminated commands; any
    // number per segment; a command split across segments is reassembled.
    private async Task ServeCommandAsync(TcpClient client)
    {
        var pending = new List<byte>();
        var buffer = new byte[4096];
        try
        {
            NetworkStream stream = client.GetStream();
            while (!_stopping.IsCancellationRequested)
            {
                int got = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    break;
                }

                for (int i = 0; i < got; i++)
                {
                    if (buffer[i] == (byte)'\r')
                    {
                        string line = Encoding.UTF8.GetString([.. pending]);
                        pending.Clear();
                        if (line.Length > 0)
                        {
                            _tnc.ProcessCommand(line);
                        }
                    }
                    else
                    {
                        pending.Add(buffer[i]);
                    }
                }
            }
        }
        catch (Exception)
        {
            // A host error only costs that host its connection.
        }
        finally
        {
            OnClientGone(client, isData: false);
        }
    }

    // ProcessReceivedData (TCPHostInterface.c:396): [2-byte BE length][payload].
    private async Task ServeDataAsync(TcpClient client)
    {
        var pending = new List<byte>();
        var buffer = new byte[4096];
        try
        {
            NetworkStream stream = client.GetStream();
            while (!_stopping.IsCancellationRequested)
            {
                int got = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    break;
                }

                pending.AddRange(buffer.AsSpan(0, got));
                while (pending.Count >= 2)
                {
                    int length = (pending[0] << 8) + pending[1];
                    if (pending.Count < length + 2)
                    {
                        break;
                    }

                    byte[] block = [.. pending[2..(length + 2)]];
                    pending.RemoveRange(0, length + 2);
                    _tnc.AcceptHostData(block);
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            OnClientGone(client, isData: true);
        }
    }

    private void OnClientGone(TcpClient client, bool isData)
    {
        bool wasCurrent;
        if (isData)
        {
            lock (_dataWriteLock)
            {
                wasCurrent = ReferenceEquals(_dataClient, client);
                if (wasCurrent)
                {
                    _dataClient = null;
                }
            }
        }
        else
        {
            lock (_commandWriteLock)
            {
                wasCurrent = ReferenceEquals(_commandClient, client);
                if (wasCurrent)
                {
                    _commandClient = null;
                }
            }
        }

        client.Dispose();
        if (wasCurrent && !_stopping.IsCancellationRequested)
        {
            // The host-socket failsafe (LostHost, ARDOPCommon.c:424).
            _tnc.HostLinkLost();
        }
    }

    // TCPSendCommandToHost (TCPHostInterface.c:118): "<line><CR>".
    private void SendCommandLine(string line)
    {
        lock (_commandWriteLock)
        {
            if (_commandClient is not { } client)
            {
                return;
            }

            try
            {
                client.GetStream().Write(Encoding.UTF8.GetBytes(line + "\r"));
            }
            catch (Exception)
            {
                // Broken pipe: the read loop cleans up.
            }
        }
    }

    // TCPAddTagToDataAndSendToHost (TCPHostInterface.c:220):
    // [2-byte BE length = payload + 3][3-char tag][payload].
    private void SendTaggedData(string tag, byte[] payload)
    {
        var framed = new byte[payload.Length + 5];
        int length = payload.Length + 3;
        framed[0] = (byte)(length >> 8);
        framed[1] = (byte)length;
        Encoding.ASCII.GetBytes(tag, framed.AsSpan(2, 3));
        payload.CopyTo(framed, 5);
        lock (_dataWriteLock)
        {
            if (_dataClient is not { } client)
            {
                return;
            }

            try
            {
                client.GetStream().Write(framed);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _commandListener.Stop();
        _dataListener.Stop();
        _commandClient?.Dispose();
        _dataClient?.Dispose();
        foreach (Task loop in _loops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        // The receive pump may still be blocked in a Read; it's a background thread and
        // checks _stopping after each block, so a bounded join is enough (closing the input
        // unblocks it immediately).
        _receivePump?.Join(TimeSpan.FromSeconds(1));

        if (_ownsTnc)
        {
            await _tnc.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }
}
