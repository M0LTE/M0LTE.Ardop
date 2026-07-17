namespace M0LTE.Ardop;

/// <summary>Delivery tag for FEC-mode data passed to the host, mirroring the 3-byte
/// prefixes of ardopcf's data socket.</summary>
public enum ArdopFecTag
{
    /// <summary>Correctly decoded FEC data.</summary>
    Fec,

    /// <summary>Failed data the sender has moved past — delivered errored, flagged.</summary>
    Err,

    /// <summary>An ID frame (caller + grid square as text).</summary>
    Idf,
}

/// <summary>
/// Connectionless FEC-mode receive semantics over <see cref="ArdopDemodulator"/>,
/// ported from ardopcf's <c>ProcessRcvdFECDataFrame</c>/<c>PassFECErrDataToHost</c>
/// (FEC.c:332-393, git a7c9228, MIT, © 2014-2024 Rick Muething, John Wiseman,
/// Peter LaRue): the first good copy of each frame passes to the host once (repeats
/// deduplicated by frame type + payload CRC); a failed frame's data is held back and
/// only delivered tagged <see cref="ArdopFecTag.Err"/> after the sender has moved on to
/// a different frame type (or the held data goes stale). Memory-ARQ state resets after
/// every good decode so a following same-typed frame with new data is never mistaken
/// for a repeat.
/// </summary>
public sealed class ArdopFecReceiver
{
    private readonly ArdopDemodulator _demodulator;

    private int _lastFrameTypeToHost = -1;
    private ushort _crcLastPassedToHost;
    private byte[] _failedData = [];
    private int _lastFailedFrameType = -1;

    /// <summary>Creates a receiver bound to <paramref name="demodulator"/>. When
    /// <paramref name="attach"/> is true (default) it subscribes to the demodulator's
    /// decoded frames directly; pass false when a host interface routes frames by
    /// protocol mode and feeds <see cref="ProcessFrame"/> itself (staleness delivery
    /// of held ERR data stays subscribed either way).</summary>
    public ArdopFecReceiver(ArdopDemodulator demodulator, bool attach = true)
    {
        ArgumentNullException.ThrowIfNull(demodulator);
        _demodulator = demodulator;
        if (attach)
        {
            demodulator.FrameDecoded += ProcessFrame;
        }

        demodulator.MemoryArqStale += PassFailedDataToHost;
    }

    /// <summary>Raised for each chunk of data FEC reception delivers to the host:
    /// good data, errored data the sender abandoned, and ID frame text.</summary>
    public event Action<ArdopFecTag, byte[]>? DataReceived;

    /// <summary>Runs one decoded frame through the FEC receive rules — the entry point
    /// for hosts that construct with <c>attach: false</c> and route frames per
    /// protocol mode.</summary>
    public void ProcessFrame(ArdopDecodedFrame frame)
    {
        if (frame.Type == ArdopFrameType.IdFrame)
        {
            if (frame.Ok)
            {
                DataReceived?.Invoke(
                    ArdopFecTag.Idf,
                    System.Text.Encoding.ASCII.GetBytes($"ID:{frame.Caller} [{frame.GridSquare}]:"));
            }

            return;
        }

        if (!ArdopFrameType.IsData(frame.Type))
        {
            return; // control frames carry no FEC data
        }

        if (frame.Ok)
        {
            // Reset Memory ARQ so the next same-typed frame is decoded fresh.
            _demodulator.ResetMemoryArq();

            ushort crc = ArdopCrc.Crc16(frame.Data);
            if (frame.Type == _lastFrameTypeToHost && crc == _crcLastPassedToHost)
            {
                return; // repeat of data already delivered
            }

            if (_lastFailedFrameType != frame.Type)
            {
                PassFailedDataToHost();
            }

            DataReceived?.Invoke(ArdopFecTag.Fec, frame.Data);
            _crcLastPassedToHost = crc;
            _lastFrameTypeToHost = frame.Type;
            _failedData = [];
            _lastFailedFrameType = -1;
        }
        else
        {
            // Hold the failed data: a later repeat may still recover it via Memory
            // ARQ. Deliver the previous failure first if this is a different type.
            if (_lastFailedFrameType != frame.Type)
            {
                PassFailedDataToHost();
            }

            _failedData = frame.Data;
            _lastFailedFrameType = frame.Type;
        }
    }

    private void PassFailedDataToHost()
    {
        if (_failedData.Length > 0)
        {
            DataReceived?.Invoke(ArdopFecTag.Err, _failedData);
            _failedData = [];
            _lastFailedFrameType = -1;
        }
    }
}
