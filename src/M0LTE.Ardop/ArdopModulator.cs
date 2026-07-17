namespace M0LTE.Ardop;

/// <summary>
/// ARDOP transmitter: two-tone leader + sync, the 10-symbol 50 Bd frame type with
/// parity, template-driven data symbols — 4FSK at 50/100/600 Bd and differential
/// 4PSK/8PSK/16QAM on 1/2/4/8 parallel 100 Bd carriers — and the 1500 Hz trailer, all
/// run through ardopcf's frequency-sampling TX filter. Ported from ardopcf
/// (git a7c9228, MIT, © 2014-2024 Rick Muething, John Wiseman, Peter LaRue):
/// <c>GetTwoToneLeaderWithSync</c> / <c>SendLeaderAndSYNC</c> Modulate.c:39-118,
/// <c>Mod4FSKDataAndPlay</c> :121, <c>Mod4FSK600BdDataAndPlay</c> :262,
/// <c>SoftClip</c> :337, <c>Calc1CarPSKSymbols</c> :361, <c>PlayPSKSymbols</c> :413,
/// <c>ModPSKDataAndPlay</c> :471, <c>AddTrailer</c> :595, and the
/// <c>initFilter</c>/<c>SampleSink</c> comb-resonator filter :676-905. Output is 12 kHz
/// 16-bit samples, sample-comparable with ardopcf's <c>--writetxwav</c> audio.
/// See PROVENANCE.md and docs/ardop-design.md §3.1-3.2.
/// </summary>
/// <remarks>
/// <para>
/// Sign conventions carried from the reference: leader symbols alternate polarity with
/// the final (sync) symbol repeating the polarity the <i>next</i> symbol would have
/// had; frame-type and 50/100 Bd data symbols flip polarity every symbol so there is
/// no phase discontinuity at symbol boundaries; 600 Bd symbols play their templates
/// unflipped. The filter drops its first 60 output samples (half the 120-tap comb),
/// exactly as <c>SampleSink</c> does.
/// </para>
/// <para>
/// The PSK/QAM path sums independent per-carrier tone templates in the time domain
/// (parallel tones, not IFFT OFDM): each carrier starts with one full-scale phase-0
/// reference symbol, then differential phase steps (SymSet 2 for 4PSK so the full
/// 8-phase circle is used, 1 for 8PSK/16QAM) with 16QAM's amplitude bit halving the
/// template via an arithmetic shift. The per-carrier-count scaling factors and the
/// soft clip above ±30000 are ardopcf's empirically-chosen crest-factor controls
/// (Modulate.c:493-526) — kept verbatim.
/// </para>
/// </remarks>
public sealed class ArdopModulator
{
    /// <summary>ARDOP's native sample rate.</summary>
    public const int SampleRate = 12000;

    private readonly int _driveLevel;

    /// <summary>Creates a modulator. <paramref name="driveLevel"/> scales input samples
    /// by n/100 before the TX filter (ardopcf's DRIVELEVEL, default 100).</summary>
    public ArdopModulator(int driveLevel = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(driveLevel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driveLevel, 100);
        _driveLevel = driveLevel;
    }

    /// <summary>
    /// Modulates an encoded frame (from <see cref="ArdopFrameCodec"/>) to 12 kHz
    /// samples. <paramref name="leaderLengthMs"/> is the two-tone leader length
    /// including the sync symbol (ardopcf default 240, negotiable 100-1000 in 20 ms
    /// steps); <paramref name="trailerLengthMs"/> adds trailer tone beyond the one
    /// mandatory symbol (default 20).
    /// </summary>
    public short[] Modulate(ReadOnlySpan<byte> encodedFrame, int leaderLengthMs = 240, int trailerLengthMs = 20)
    {
        if (encodedFrame.Length < 2)
        {
            throw new ArgumentException("encoded frame must be at least the 2 type bytes", nameof(encodedFrame));
        }

        var info = ArdopFrameInfo.Get(encodedFrame[0]);
        if (info.Modulation != ArdopModulation.Fsk4)
        {
            return ModulatePsk(info, encodedFrame, leaderLengthMs, trailerLengthMs);
        }

        // Mod4FSKDataAndPlay: 50 Bd → 200 Hz filter; 100 Bd → 500 Hz;
        // Mod4FSK600BdDataAndPlay: 600 Bd → 2000 Hz. All centred on 1500 Hz.
        int filterWidth = info.Baud switch { 50 => 200, 100 => 500, _ => 2000 };
        var filter = new TxFilter(filterWidth, 1500, _driveLevel);

        SendLeaderAndSync(filter, encodedFrame, leaderLengthMs);

        // Data symbols.
        short[][] templates = info.Baud switch
        {
            50 => ArdopTxTemplates.Fsk50Bd,
            100 => ArdopTxTemplates.Fsk100Bd,
            _ => ArdopTxTemplates.Fsk600Bd,
        };
        for (int m = 2; m < encodedFrame.Length; m++)
        {
            byte mask = 0xC0;
            for (int k = 0; k < 4; k++)
            {
                byte symbol = (byte)((mask & encodedFrame[m]) >> (2 * (3 - k)));
                short[] template = templates[symbol];
                if (info.Baud == 600)
                {
                    // 600 Bd symbols are played unflipped (Mod4FSK600BdDataAndPlay).
                    foreach (short sample in template)
                    {
                        filter.Sink(sample);
                    }
                }
                else
                {
                    bool positive = (k & 1) == 0;
                    foreach (short sample in template)
                    {
                        filter.Sink(positive ? sample : (short)-sample);
                    }
                }

                mask >>= 2;
            }
        }

        AddTrailer(filter, trailerLengthMs);
        return filter.Drain();
    }

    /// <summary>The 5-second two-tone (1450/1550 Hz) drive-level test burst
    /// (<c>Send5SecTwoTone</c>, ardopcf ARDOPC.c:1630: 250 leader symbols through the
    /// 200 Hz TX filter, including the closing phase-reversal sync symbol).</summary>
    public short[] TwoToneTest()
    {
        var filter = new TxFilter(200, 1500, _driveLevel);
        const int LeaderSymbols = 250;
        int sign = (LeaderSymbols & 1) == 1 ? -1 : 1;
        for (int i = 0; i < LeaderSymbols; i++)
        {
            int symbolSign = i == LeaderSymbols - 1 ? -sign : sign;
            foreach (short sample in ArdopTxTemplates.Leader50Bd)
            {
                filter.Sink((short)(symbolSign * sample));
            }

            sign = -sign;
        }

        return filter.Drain();
    }

    // GetTwoToneLeaderWithSync + SendLeaderAndSYNC (Modulate.c:39-118): the two-tone
    // leader with the phase-reversal sync symbol, then the frame type as 10 × 50 Bd
    // 4FSK symbols — 4 data dibits + 1 parity symbol per byte, both parity symbols
    // computed from the plain type byte.
    private static void SendLeaderAndSync(TxFilter filter, ReadOnlySpan<byte> encodedFrame, int leaderLengthMs)
    {
        int leaderSymbols = leaderLengthMs / 20;
        int sign = (leaderSymbols & 1) == 1 ? -1 : 1;
        for (int i = 0; i < leaderSymbols; i++)
        {
            int symbolSign = i == leaderSymbols - 1 ? -sign : sign;
            foreach (short sample in ArdopTxTemplates.Leader50Bd)
            {
                filter.Sink((short)(symbolSign * sample));
            }

            sign = -sign;
        }

        for (int j = 0; j < 2; j++)
        {
            byte mask = 0xC0;
            for (int k = 0; k < 5; k++)
            {
                byte symbol = k < 4
                    ? (byte)((mask & encodedFrame[j]) >> (2 * (3 - k)))
                    : ArdopFrameType.TypeParity(encodedFrame[0]);
                short[] template = ArdopTxTemplates.Fsk50Bd[symbol];
                bool positive = ((5 * j + k) & 1) == 0;
                foreach (short sample in template)
                {
                    filter.Sink(positive ? sample : (short)-sample);
                }

                mask >>= 2;
            }
        }
    }

    // AddTrailer (Modulate.c:595): 1 + trailerMs/10 symbols of the 1500 Hz phase-0
    // template.
    private static void AddTrailer(TxFilter filter, int trailerLengthMs)
    {
        int trailerSymbols = 1 + trailerLengthMs / 10;
        for (int i = 0; i < trailerSymbols; i++)
        {
            foreach (short sample in ArdopTxTemplates.Trailer1500Hz)
            {
                filter.Sink(sample);
            }
        }
    }

    // ModPSKDataAndPlay (Modulate.c:471): differential PSK/16QAM on 1/2/4/8 parallel
    // 100 Bd carriers.
    private short[] ModulatePsk(ArdopFrameInfo info, ReadOnlySpan<byte> encodedFrame, int leaderLengthMs, int trailerLengthMs)
    {
        // Per-carrier-count crest-factor scaling (Modulate.c:493-526).
        double carScalingFactor = info.CarrierCount switch
        {
            1 => 1.2,
            2 => info.Modulation == ArdopModulation.Qam16 ? 0.67 : 0.65,
            4 => 0.4,
            _ => info.Modulation == ArdopModulation.Qam16 ? 0.27 : 0.25,
        };

        int filterWidth = info.CarrierCount switch { 1 => 200, 2 => 500, 4 => 1000, _ => 2000 };
        var filter = new TxFilter(filterWidth, 1500, _driveLevel);

        SendLeaderAndSync(filter, encodedFrame, leaderLengthMs);

        // One full-scale phase-0 reference symbol per carrier, then the differential
        // data symbols.
        int bytesPerCarrier = info.DataLength + info.RsLength + 3;
        int expected = 2 + bytesPerCarrier * info.CarrierCount;
        if (encodedFrame.Length != expected)
        {
            throw new ArgumentException(
                $"{info.Name}: encoded frame must be {expected} bytes, got {encodedFrame.Length}", nameof(encodedFrame));
        }

        var reference = new byte[info.CarrierCount][];
        var symbols = new byte[info.CarrierCount][];
        for (int car = 0; car < info.CarrierCount; car++)
        {
            reference[car] = [0];
            symbols[car] = CalcCarrierPskSymbols(
                info.Modulation, encodedFrame.Slice(2 + car * bytesPerCarrier, bytesPerCarrier));
        }

        PlayPskSymbols(filter, reference, info.CarrierCount, 1, carScalingFactor);
        PlayPskSymbols(filter, symbols, info.CarrierCount, symbols[0].Length, carScalingFactor);

        AddTrailer(filter, trailerLengthMs);
        return filter.Drain();
    }

    // Calc1CarPSKSymbols (Modulate.c:361): each output value's low 3 bits are the
    // absolute phase index (0-7 = 0°-315°, accumulated differentially from the phase-0
    // reference; SymSet 2 for 4PSK), bit 3 the 16QAM half-magnitude flag (absolute,
    // from the raw symbol — not accumulated).
    private static byte[] CalcCarrierPskSymbols(ArdopModulation modulation, ReadOnlySpan<byte> source)
    {
        int bitsPerSymbol = modulation switch
        {
            ArdopModulation.Psk4 => 2,
            ArdopModulation.Psk8 => 3,
            _ => 4,
        };
        int symSet = modulation == ArdopModulation.Psk4 ? 2 : 1;

        var symbols = new byte[source.Length * 8 / bitsPerSymbol];
        ushort dataBuf = 0;
        int bitsBuffered = 0;
        int sourcePtr = 0;
        for (int symNum = 0; symNum < symbols.Length; symNum++)
        {
            if (bitsBuffered < bitsPerSymbol)
            {
                dataBuf += (ushort)(source[sourcePtr++] << (8 - bitsBuffered));
                bitsBuffered += 8;
            }

            byte rawSym = (byte)(dataBuf >> (16 - bitsPerSymbol));
            dataBuf <<= bitsPerSymbol;
            bitsBuffered -= bitsPerSymbol;

            byte prior = symNum == 0 ? (byte)0 : symbols[symNum - 1];
            symbols[symNum] = (byte)(((prior + rawSym * symSet) & 7) + (rawSym & 0x08));
        }

        return symbols;
    }

    // PlayPSKSymbols (Modulate.c:413): sum the per-carrier templates in the time
    // domain, scale, soft-clip, sink. Phases 4-7 are the negatives of 0-3; the QAM
    // half-magnitude bit becomes a per-sample arithmetic right shift. Kept int-exact
    // with the reference (including the int truncation of the scaled sum).
    private static void PlayPskSymbols(
        TxFilter filter, byte[][] symbols, int numCars, int symbolCount, double carScalingFactor)
    {
        // Carrier 4 (1500 Hz) is single-carrier only; multi-carrier modes straddle it
        // (2 → 1400/1600, 4 → 1200-1800, 8 → 800-2200 skipping 1500).
        int carStartIndex = numCars switch { 1 => 4, 2 => 3, 4 => 2, _ => 0 };

        for (int m = 0; m < symbolCount; m++)
        {
            for (int n = 0; n < 120; n++)
            {
                int sample = 0;
                int carIndex = carStartIndex;
                for (int i = 0; i < numCars; i++)
                {
                    byte symbol = symbols[i][m];
                    int phase = symbol & 0x07;
                    int shift = symbol >> 3;
                    if (phase < 4)
                    {
                        sample += ArdopTxTemplates.Psk100Bd[carIndex][phase][n] >> shift;
                    }
                    else
                    {
                        sample -= ArdopTxTemplates.Psk100Bd[carIndex][phase - 4][n] >> shift;
                    }

                    carIndex++;
                    if (carIndex == 4)
                    {
                        carIndex++; // multi-carrier modes skip 1500 Hz
                    }
                }

                sample = (int)(sample * carScalingFactor);
                filter.Sink((short)SoftClip(sample));
            }
        }
    }

    // SoftClip (Modulate.c:337): compress the summed waveform above ±30000. The
    // arithmetic stays in double until the final int truncation, as in the reference.
    private static int SoftClip(int input)
    {
        if (input > 30000)
        {
            return (int)Math.Min(32700.0, 30000 + 20 * Math.Sqrt(input - 30000));
        }

        if (input < -30000)
        {
            return (int)Math.Max(-32700.0, -30000 - 20 * Math.Sqrt(-(input + 30000)));
        }

        return input;
    }

    /// <summary>
    /// ardopcf's TX frequency-sampling filter (<c>initFilter</c>/<c>SampleSink</c>,
    /// Modulate.c:676-905): a 120-sample comb feeding a bank of 100 Hz resonators
    /// around 1500 Hz — 3 sections for the 200 Hz width, 7 for 500, 11 for 1000,
    /// 21 for 2000 — with the reference's exact transition coefficients, float
    /// arithmetic and int truncations preserved.
    /// </summary>
    private sealed class TxFilter
    {
        private const int N = 120;             // intN — comb length, 12000/100
        private const float R = 0.9995f;       // dblR — pole radius

        private readonly int _width;
        private readonly int _first;
        private readonly int _last;
        private readonly int _driveLevel;
        private readonly float _rn;
        private readonly float _r2;
        private readonly float[] _coef = new float[32];
        private readonly float[] _zout0 = new float[32];
        private readonly float[] _zout1 = new float[32];
        private readonly float[] _zout2 = new float[32];
        private readonly short[] _last120 = new short[121];
        private readonly List<short> _output = [];

        private float _zin1;
        private float _zin2;
        private int _sampleNo;
        private int _get;
        private int _put = 120;

        public TxFilter(int width, int centreHz, int driveLevel)
        {
            _width = width;
            _driveLevel = driveLevel;
            int centreSlot = centreHz / 100;
            (_first, _last) = width switch
            {
                200 => (centreSlot - 1, centreSlot + 1),
                500 => (centreSlot - 3, centreSlot + 3),
                1000 => (centreSlot - 5, centreSlot + 5),
                2000 => (centreSlot - 10, centreSlot + 10),
                _ => throw new ArgumentException($"unsupported filter width {width}", nameof(width)),
            };

            _rn = MathF.Pow(R, N);
            _r2 = MathF.Pow(R, 2);
            for (int i = _first; i <= _last; i++)
            {
                _coef[i] = 2 * R * MathF.Cos(2 * MathF.PI * i / N);
            }
        }

        public void Sink(short sample)
        {
            const int FilterDelay = N / 2;

            sample = (short)(sample * _driveLevel / 100);

            float zin = _sampleNo < N
                ? sample
                : sample - _rn * _last120[_get];
            if (++_get == 121)
            {
                _get = 0;
            }

            // The comb.
            float zcomb = zin - _zin2 * _r2;
            _zin2 = _zin1;
            _zin1 = zin;

            // The resonators. The (int) truncations on the inner sections of the wider
            // filters are ardopcf's — kept for sample-exactness, not taste.
            float filtered = 0;
            for (int j = _first; j <= _last; j++)
            {
                _zout0[j] = zcomb + _coef[j] * _zout1[j] - _r2 * _zout2[j];
                _zout2[j] = _zout1[j];
                _zout1[j] = _zout0[j];

                if (_sampleNo < FilterDelay)
                {
                    continue;
                }

                switch (_width)
                {
                    case 200:
                        if (j == _first || j == _last)
                        {
                            filtered += 0.7389f * _zout0[j];
                        }
                        else
                        {
                            filtered -= _zout0[j];
                        }

                        break;

                    case 500:
                        if (j == _first || j == _last)
                        {
                            filtered += 0.10601f * _zout0[j];
                        }
                        else if (j == _first + 1 || j == _last - 1)
                        {
                            filtered -= 0.59383f * _zout0[j];
                        }
                        else if ((j & 1) == 0)
                        {
                            filtered += (int)_zout0[j];
                        }
                        else
                        {
                            filtered -= (int)_zout0[j];
                        }

                        break;

                    case 1000:
                        if (j == _first || j == _last)
                        {
                            filtered += 0.377f * _zout0[j];
                        }
                        else if ((j & 1) == 0)
                        {
                            filtered += (int)_zout0[j];
                        }
                        else
                        {
                            filtered -= (int)_zout0[j];
                        }

                        break;

                    default: // 2000
                        if (j == _first || j == _last)
                        {
                            filtered += 0.371f * _zout0[j];
                        }
                        else if ((j & 1) == 0)
                        {
                            filtered += (int)_zout0[j];
                        }
                        else
                        {
                            filtered -= (int)_zout0[j];
                        }

                        break;
                }
            }

            if (_sampleNo >= FilterDelay)
            {
                filtered *= 0.00833333333f; // 1/120 — rescale for filter gain
                if (filtered > 32700)
                {
                    filtered = 32700;
                }
                else if (filtered < -32700)
                {
                    filtered = -32700;
                }

                _output.Add((short)filtered);
            }

            _last120[_put++] = sample;
            if (_put == 121)
            {
                _put = 0;
            }

            _sampleNo++;
        }

        public short[] Drain() => [.. _output];
    }
}
