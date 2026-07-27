// Framework-free grid-decimation and ruler-tick-step math (docs/design/layout-view.md — L1a brief,
// R-L1a-3 and the ruler spec). No SKPath / Avalonia types.

namespace CircuitRF.Ui.Layout;

public static class LayoutGridMath
{
    private static readonly double[] NiceMultipliers = [1.0, 2.0, 5.0, 10.0];

    /// <summary>Smallest value of the form {1,2,5}&#215;10^k that is &gt;= <paramref name="x"/>.
    /// The shared "nice number" sequence used for both grid decimation and ruler tick spacing.
    /// <paramref name="x"/> must be positive.</summary>
    public static double CeilingNiceStep(double x)
    {
        if (x <= 0 || double.IsNaN(x) || double.IsInfinity(x)) return 0;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(x)));
        foreach (var m in NiceMultipliers)
        {
            double candidate = m * mag;
            if (candidate >= x - x * 1e-9) return candidate;
        }
        return 10 * mag;
    }

    /// <summary>
    /// Decimates <paramref name="snapDbu"/> (the layout's snap-grid pitch) so the on-screen dot
    /// spacing never falls below <paramref name="minPixelSpacing"/> — R-L1a-3. Returns null when no
    /// grid should be drawn at all (non-positive pitch/zoom, or the pixel spacing cannot be made to
    /// clear the threshold).
    /// </summary>
    public static long? ComputeGridPitch(long snapDbu, double zoomPxPerDbu, double minPixelSpacing = 8.0)
    {
        if (snapDbu <= 0 || zoomPxPerDbu <= 0 || double.IsNaN(zoomPxPerDbu) || double.IsInfinity(zoomPxPerDbu))
            return null;

        double px = snapDbu * zoomPxPerDbu;
        if (px >= minPixelSpacing)
            return snapDbu;

        double neededMultiplier = minPixelSpacing / px;
        double multiplier = CeilingNiceStep(neededMultiplier);
        if (multiplier <= 0) return null;

        double newPitch = snapDbu * multiplier;
        double newPx = newPitch * zoomPxPerDbu;
        if (newPx < minPixelSpacing || newPitch > long.MaxValue / 4.0)
            return null;

        return (long)Math.Round(newPitch);
    }

    /// <summary>The major-grid pitch, drawn every 5 minor steps (docs/design/layout-view.md L1a
    /// brief §3, "every 5 or 10 minor steps").</summary>
    public const int MajorGridStepCount = 5;

    /// <summary>
    /// Chooses a ruler tick step, in the given <paramref name="displayUnit"/>, small enough that
    /// consecutive labels never collide (at least <paramref name="minLabelPixelSpacing"/> px apart)
    /// but no smaller than necessary. Returns the step already converted to DBU (exact, via
    /// <see cref="LayoutUnits.ToDbu"/>) so ruler tick positions land on exact multiples.
    /// </summary>
    public static long ComputeRulerTickStepDbu(
        double zoomPxPerDbu, LayoutUnit displayUnit, int dbuPerMicron, double minLabelPixelSpacing = 60.0)
    {
        long oneUnitDbu = LayoutUnits.ToDbu(1m, displayUnit, dbuPerMicron);
        if (oneUnitDbu <= 0) oneUnitDbu = 1;

        double onePx = oneUnitDbu * zoomPxPerDbu;
        double stepUnits = onePx > 0 ? CeilingNiceStep(minLabelPixelSpacing / onePx) : 1.0;
        if (onePx >= minLabelPixelSpacing) stepUnits = 1.0; // already comfortably spaced — no need to widen

        long stepDbu = LayoutUnits.ToDbu((decimal)stepUnits, displayUnit, dbuPerMicron);
        return stepDbu <= 0 ? oneUnitDbu : stepDbu;
    }
}
