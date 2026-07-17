namespace M0LTE.Ardop.Arq;

/// <summary>
/// The per-bandwidth ARQ data-mode ladders (most robust first) and their shift-up
/// quality thresholds, transcribed from ardopcf (git a7c9228, MIT, © 2014-2024 Rick
/// Muething, John Wiseman, Peter LaRue): <c>GetDataModes</c> ARQ.c:610 (tables
/// :571-608) and <c>GetShiftUpThresholds</c> ARQ.c:684 (tables :678-682). The FSKONLY
/// variants restrict each ladder to its 4FSK rungs — a host-commandable configuration,
/// not a fork (docs/ardop-design.md §8 Phase B).
/// </summary>
public static class ArdopDataLadder
{
    private static readonly byte[] Modes200 = [0x48, 0x42, 0x40, 0x44, 0x46];
    private static readonly byte[] Modes200Fsk = [0x48];
    private static readonly byte[] Modes500 = [0x48, 0x42, 0x40, 0x50, 0x52, 0x54];
    private static readonly byte[] Modes500Fsk = [0x48];
    private static readonly byte[] Modes1000 = [0x4C, 0x4A, 0x50, 0x60, 0x62, 0x64];
    private static readonly byte[] Modes1000Fsk = [0x4C, 0x4A];
    private static readonly byte[] Modes2000 = [0x4C, 0x4A, 0x50, 0x60, 0x70, 0x72, 0x74];
    private static readonly byte[] Modes2000Fsk = [0x4C, 0x4A];
    private static readonly byte[] Modes2000Fm = [0x4C, 0x4A, 0x7C, 0x7A];

    // Shift-up thresholds, one per rung (the top rung's value is never consulted).
    // Derivation methodology in the comment at ARQ.c:686-691.
    private static readonly byte[] Thresholds200 = [82, 84, 84, 85, 0];
    private static readonly byte[] Thresholds500 = [80, 84, 84, 75, 79, 0];
    private static readonly byte[] Thresholds1000 = [80, 80, 80, 80, 75, 0];
    private static readonly byte[] Thresholds2000 = [80, 80, 80, 76, 85, 75, 0];
    private static readonly byte[] Thresholds2000Fm = [60, 85, 85, 0];

    /// <summary>
    /// The data-mode ladder for a session bandwidth. <paramref name="fmModes"/> selects
    /// the FM 2000 Hz ladder (ardopcf uses it when <c>TuningRange == 0 || Use600Modes</c>).
    /// The FSKONLY 2000 Hz FM ladder equals the full FM ladder — the 600 Bd modes are
    /// 4FSK (ARQ.c:606).
    /// </summary>
    public static ReadOnlySpan<byte> Modes(int bandwidthHz, bool fskOnly, bool fmModes = false) =>
        bandwidthHz switch
        {
            200 => fskOnly ? Modes200Fsk : Modes200,
            500 => fskOnly ? Modes500Fsk : Modes500,
            1000 => fskOnly ? Modes1000Fsk : Modes1000,
            2000 when fmModes => Modes2000Fm,
            2000 => fskOnly ? Modes2000Fsk : Modes2000,
            _ => throw new ArgumentException($"no ARDOP ladder for {bandwidthHz} Hz", nameof(bandwidthHz)),
        };

    /// <summary>The shift-up quality thresholds matching the full ladder for the
    /// bandwidth. For FSKONLY ladders the same leading entries apply — ardopcf indexes
    /// the full threshold table with the FSK ladder's pointer (GetShiftUpThresholds has
    /// no FSKONLY variant).</summary>
    public static ReadOnlySpan<byte> ShiftUpThresholds(int bandwidthHz, bool fmModes = false) =>
        bandwidthHz switch
        {
            200 => Thresholds200,
            500 => Thresholds500,
            1000 => Thresholds1000,
            2000 when fmModes => Thresholds2000Fm,
            2000 => Thresholds2000,
            _ => throw new ArgumentException($"no ARDOP thresholds for {bandwidthHz} Hz", nameof(bandwidthHz)),
        };

    /// <summary>Net payload capacity in bytes of one frame of <paramref name="type"/>
    /// (<c>FrameSize</c>, ARDOPC.c:301; equals payload/carrier × carriers).</summary>
    public static int FrameCapacity(byte type)
    {
        var info = ArdopFrameInfo.Get(type);
        return info.DataLength * info.CarrierCount;
    }
}
