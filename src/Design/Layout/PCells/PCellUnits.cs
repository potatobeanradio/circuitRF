namespace CircuitRF.Design.Layout.PCells;

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

    /// <summary>
    /// The inverse: a DBU count back to SI metres, for a generator that has worked something out in
    /// DBU and needs to state it as a PARAMETER value — a <see cref="PCellHandle"/>'s
    /// <c>Min</c>/<c>Max</c> bound, which <c>PCellHandleSolver</c> compares against the parameter
    /// itself and which is therefore in the parameter's own units (SI metres in-process).
    ///
    /// <para>Deriving the bound in DBU and converting DOWN, rather than computing it in metres
    /// alongside, is what makes it agree with the generator's own geometry to the last DBU: both come
    /// from the same integer. A bound computed independently in metres can land a DBU off after
    /// <see cref="MetresToDbu"/> rounds it, and a grip that stops one DBU short of where the geometry
    /// stops changing is a grip that visibly refuses to reach its own limit.</para>
    /// </summary>
    public static double DbuToMetres(long dbu, int dbuPerMicron)
        => (double)((decimal)dbu / (dbuPerMicron * 1_000_000m));
}
