namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// R-pc-6: PCell length parameters are SI metres; the generator converts to DBU at its own
/// boundary with ONE documented rounding rule — round-half-away-from-zero — so two generators can
/// never disagree about where a 2.9 mm edge lands. This is the one helper every generator in
/// <c>src/Ui/Layout/PCells/</c> routes through; it does not reimplement the conversion, it
/// delegates to <see cref="LayoutUnits.ToDbu"/> (already round-half-away-from-zero, computed in
/// <c>decimal</c>, exact for mm/mil/inch) so there is exactly one rounding rule in the whole
/// codebase, not two that could drift.
/// </summary>
public static class PCellUnits
{
    /// <summary>Converts an SI-metres value (a resolved cell parameter, e.g. W = 0.0029) to DBU
    /// at the given resolution.</summary>
    public static long MetresToDbu(double meters, int dbuPerMicron)
        => LayoutUnits.ToDbu((decimal)meters * 1000m, LayoutUnit.Mm, dbuPerMicron);
}
