namespace M0LTE.Ardop.Arq;

/// <summary>
/// The ARQ mode-shift machine, a behavioural port of ardopcf's <c>Gearshift_9</c>
/// (ARQ.c:717), <c>ComputeQualityAvg</c> (:793) and the mode-pointer half of
/// <c>GetNextFrameData</c> (:967) — git a7c9228, MIT, © 2014-2024 Rick Muething,
/// John Wiseman, Peter LaRue. Ported verbatim rather than re-derived: interop lives in
/// both stations converging on the same rungs (docs/ardop-design.md §4.6).
/// </summary>
/// <remarks>
/// Shift decisions ride on the 5-bit quality echoed in every ACK/NAK: shift down after
/// 2 NAKs at the current rung (1 if the rung has never worked); shift up when the
/// exponentially-averaged quality exceeds the rung's threshold with at least 2
/// consecutive ACKs, unless the remaining data already fits in one current-mode frame,
/// or the next rung failed straight away before and fewer than 5 consecutive ACKs have
/// accumulated since.
/// </remarks>
public sealed class ArdopGearshift
{
    private byte[] _ladder = [];
    private byte[] _thresholds = [];
    private int _pointer;
    private readonly int[] _modeHasWorked = new int[16];
    private readonly int[] _modeHasBeenTried = new int[16];
    private readonly int[] _modeNaks = new int[16];
    private int _ackCounter;
    private int _nakCounter;

    /// <summary>The rung the shifter currently points at (<c>bytCurrentFrameType</c>;
    /// always the even type code of the pair).</summary>
    public byte CurrentFrameType { get; private set; }

    /// <summary>The pending shift decision: -1 shift down, +1 shift up, 0 stay
    /// (<c>intShiftUpDn</c>). Consumed by <see cref="NextTypeToSend"/>.</summary>
    public int PendingShift { get; private set; }

    /// <summary>The exponentially-averaged reported quality (<c>intAvgQuality</c>;
    /// 0 = uninitialized, practical range 50-96).</summary>
    public int AverageQuality { get; private set; }

    /// <summary>Cumulative shift-up count (session stats).</summary>
    public int ShiftUps { get; private set; }

    /// <summary>Cumulative shift-down count (session stats).</summary>
    public int ShiftDowns { get; private set; }

    /// <summary>True once <see cref="Initialize"/> has run for the session.</summary>
    public bool IsInitialized => _ladder.Length > 0;

    /// <summary>Sets up the ladder for a session bandwidth (the initialize branch of
    /// <c>GetNextFrameData</c>, ARQ.c:982; <c>fastStart</c> starts midway).</summary>
    public void Initialize(int sessionBandwidthHz, bool fskOnly, bool fastStart, bool fmModes = false)
    {
        _ladder = ArdopDataLadder.Modes(sessionBandwidthHz, fskOnly, fmModes).ToArray();
        _thresholds = ArdopDataLadder.ShiftUpThresholds(sessionBandwidthHz, fmModes).ToArray();
        _pointer = fastStart ? _ladder.Length / 2 : 0;
        CurrentFrameType = _ladder[_pointer];
        PendingShift = 0;
        AverageQuality = 0;
        _ackCounter = 0;
        _nakCounter = 0;
        Array.Clear(_modeHasWorked);
        Array.Clear(_modeHasBeenTried);
        Array.Clear(_modeNaks);
    }

    /// <summary>Resets all per-session state (<c>InitializeConnection</c>'s gearshift
    /// portion, ARQ.c:1084).</summary>
    public void Reset()
    {
        _ladder = [];
        _thresholds = [];
        _pointer = 0;
        CurrentFrameType = 0;
        PendingShift = 0;
        AverageQuality = 0;
        _ackCounter = 0;
        _nakCounter = 0;
        ShiftUps = 0;
        ShiftDowns = 0;
        Array.Clear(_modeHasWorked);
        Array.Clear(_modeHasBeenTried);
        Array.Clear(_modeNaks);
    }

    /// <summary>ACK received for the last data frame: fold its quality into the average
    /// and run the shifter (ISS ACK path, ARQ.c:2126-2142 ordering).</summary>
    public void RecordAck(int reportedQuality, int bytesRemaining)
    {
        _ackCounter++;
        ComputeQualityAvg(reportedQuality);
        Gearshift9(bytesRemaining);
        _nakCounter = 0;
    }

    /// <summary>NAK received for the last data frame; returns true when a shift is now
    /// pending, in which case the caller retriggers its timeout and resends immediately
    /// (ISS NAK path, ARQ.c:2203-2227 ordering).</summary>
    public bool RecordNak(int reportedQuality, int bytesRemaining)
    {
        _nakCounter++;
        ComputeQualityAvg(reportedQuality);
        Gearshift9(bytesRemaining);
        bool shifted = PendingShift != 0;
        if (shifted)
        {
            _nakCounter = 0;
        }

        _ackCounter = 0;
        return shifted;
    }

    /// <summary>
    /// Applies any pending shift and yields the frame type to transmit, toggling
    /// even/odd against the last ACKed data frame so repeats are distinguishable from
    /// new data (the non-initialize half of <c>GetNextFrameData</c>, ARQ.c:1001-1042;
    /// spec rule 2.3).
    /// </summary>
    public byte NextTypeToSend(byte lastAckedDataFrameType)
    {
        if (PendingShift < 0 && _pointer > 0)
        {
            _pointer = Math.Max(0, _pointer + PendingShift);
            CurrentFrameType = _ladder[_pointer];
        }
        else if (PendingShift > 0 && _pointer < _ladder.Length - 1)
        {
            // (ardopcf clamps to Length here, but Gearshift_9 only ever requests +1
            // below the top rung, so Length - 1 is the reachable bound.)
            _pointer = Math.Min(_ladder.Length - 1, _pointer + PendingShift);
            CurrentFrameType = _ladder[_pointer];
        }

        PendingShift = 0;

        return (CurrentFrameType & 1) == (lastAckedDataFrameType & 1)
            ? (byte)(CurrentFrameType ^ 1)
            : CurrentFrameType;
    }

    // ComputeQualityAvg (ARQ.c:793): alpha-0.5 exponential averager, first sample
    // initializes.
    private void ComputeQualityAvg(int reportedQuality)
    {
        const float Alpha = 0.5f;
        AverageQuality = AverageQuality == 0
            ? reportedQuality
            : (int)(AverageQuality * (1 - Alpha) + Alpha * reportedQuality + 0.5f);
    }

    // Gearshift_9 (ARQ.c:717).
    private void Gearshift9(int bytesRemaining)
    {
        int downNaks = 2;  // Normal (changed from 3 Nov 17)

        if (_modeHasWorked[_pointer] == 0)
        {
            downNaks = 1;  // this mode has never worked — revert immediately
        }

        if (_ackCounter > 0)
        {
            _modeHasWorked[_pointer]++;
        }
        else if (_nakCounter > 0)
        {
            _modeNaks[_pointer]++;
        }

        if (_pointer > 0 && _nakCounter >= downNaks)
        {
            PendingShift = -1;
            AverageQuality = 0;  // first received quality becomes the new average
            _nakCounter = 0;
            _ackCounter = 0;
            ShiftDowns++;
        }
        else if (AverageQuality > _thresholds[_pointer]
            && _pointer < _ladder.Length - 1
            && _ackCounter >= 2)
        {
            // Don't shift if the remaining data fits in one current-mode frame.
            if (bytesRemaining <= ArdopDataLadder.FrameCapacity(_ladder[_pointer]))
            {
                PendingShift = 0;
                return;
            }

            // If the next mode was tried and immediately failed, don't retry until at
            // least 5 successive ACKs.
            if (_modeHasBeenTried[_pointer + 1] != 0
                && _modeHasWorked[_pointer + 1] == 0
                && _ackCounter < 5)
            {
                PendingShift = 0;
                return;
            }

            PendingShift = 1;
            _modeHasBeenTried[_pointer + 1] = 1;
            AverageQuality = 0;
            _nakCounter = 0;
            _ackCounter = 0;
            ShiftUps++;
        }
    }
}
