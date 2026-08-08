using System;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Snaps a wire point to the real layout's geometry (wbond.md §6.6).
///
/// <h3>All four snap kinds, unchanged — no second snap engine</h3>
/// <para>Corner/endpoint, midpoint, centroid and intersection already exist in
/// <see cref="LayoutSnapQuery"/>, already handle nested instances by transforming the CURSOR rather
/// than the geometry (R-snp-13), and are already bounded by the L2b spatial index. wBond adds the
/// unit bridge below and nothing else. Reimplementing the query here would be a second set of rules
/// that could disagree with what the layout editor's own snap markers show the user.</para>
///
/// <h3>The unit bridge is the part that can be silently wrong</h3>
/// <para>A wire point is stored in <b>nanometres</b> (<c>Point3</c>); a layout coordinate is stored in
/// the layout's own <b>database units</b>, whose size is set by <see cref="LayoutView.DbuPerMicron"/>.
/// At the default 1,000 DBU/µm the two coincide exactly, which is precisely why a missing conversion
/// would pass every test written on a default layout and land wires ten times out of place on a
/// 100 DBU/µm one. <see cref="ToDbu"/>/<see cref="ToNm"/> are the one crossing point.</para>
/// </summary>
public static class WBondSnap
{
    /// <summary>What a snap attempt produced.</summary>
    /// <param name="Snapped">False when nothing was within tolerance — the caller keeps the raw point.</param>
    /// <param name="XNm">The snapped x in nanometres (the raw x when <paramref name="Snapped"/> is false).</param>
    /// <param name="YNm">The snapped y in nanometres.</param>
    /// <param name="Kind">Which feature won, for the marker the canvas draws.</param>
    public readonly record struct Result(bool Snapped, long XNm, long YNm, SnapFeatureKind Kind)
    {
        public static Result Miss(long xNm, long yNm) => new(false, xNm, yNm, SnapFeatureKind.Nearest);
    }

    /// <summary>Nanometres to a layout's own DBU. 1 µm = 1,000 nm = <c>DbuPerMicron</c> DBU.</summary>
    public static long ToDbu(long nm, int dbuPerMicron) =>
        dbuPerMicron <= 0 ? nm : (long)Math.Round(nm * (double)dbuPerMicron / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>A layout's own DBU back to nanometres.</summary>
    public static long ToNm(long dbu, int dbuPerMicron) =>
        dbuPerMicron <= 0 ? dbu : (long)Math.Round(dbu * 1000.0 / dbuPerMicron, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Finds the highest-priority snap feature within <paramref name="toleranceNm"/> of a wire point.
    ///
    /// <para>Returns the raw point unchanged when there is no layout, when snapping is off, or when
    /// nothing is in range — snapping never refuses a placement, it only improves one.</para>
    /// </summary>
    /// <param name="view">The reference layout, or null when the editor has no layout context (§10 entry 3).</param>
    /// <param name="includeIntersections">
    /// Off by default, matching the layout editor: intersections are computed live over the near-shape
    /// set rather than indexed (R-snp-12), so they cost something on every query.
    /// </param>
    public static Result Snap(LayoutView? view, Technology? tech, string? baseDir,
                              long xNm, long yNm, long toleranceNm,
                              bool includeIntersections = false)
    {
        if (view is null || toleranceNm <= 0) return Result.Miss(xNm, yNm);

        int dbuPerMicron = view.DbuPerMicron;
        long tolDbu = ToDbu(toleranceNm, dbuPerMicron);
        if (tolDbu <= 0) return Result.Miss(xNm, yNm);

        var counters = new SnapQueryCounters();
        var candidates = LayoutSnapQuery.FindCandidates(
            view, tech, baseDir ?? string.Empty,
            ToDbu(xNm, dbuPerMicron), ToDbu(yNm, dbuPerMicron), tolDbu,
            includeIntersections, excludeShapeIndices: null, excludeInstanceIndices: null,
            ref counters);

        // FindCandidates already sorts by priority then distance (R-snp-5), so the first is the one
        // the layout editor's own marker would show. Taking any other would make the wBond overlay
        // disagree with the marker under the same cursor.
        if (candidates.Count == 0) return Result.Miss(xNm, yNm);

        var best = candidates[0];
        return new Result(true, ToNm(best.X, dbuPerMicron), ToNm(best.Y, dbuPerMicron), best.Kind);
    }
}
