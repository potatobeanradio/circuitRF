using System;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;

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
    /// <param name="wires">
    /// The wires to snap to as well as the layout's own geometry (owner, 2026-08-16: "snap wire points
    /// does not snap to wire vertices or segments"). Null snaps to layout geometry only, which is what
    /// this did before it existed.
    /// </param>
    /// <param name="excludeWire">
    /// Which wires the current gesture is MOVING, and must therefore not snap to — a dragged wire's
    /// own vertices are at distance zero from themselves, so without this the drag would pin itself in
    /// place. See <see cref="WireSnap"/>.
    /// </param>
    public static Result Snap(LayoutView? view, Technology? tech, string? baseDir,
                              long xNm, long yNm, long toleranceNm,
                              bool includeIntersections = false,
                              WBondDesign? wires = null,
                              Func<int, bool>? excludeWire = null)
    {
        // The WIRE half needs no layout at all — a design with no reference geometry still has wires
        // to land on, and that is exactly §10's third entry point.
        var wireHit = WireSnap.Nearest(wires, xNm, yNm, toleranceNm, excludeWire);

        if (view is null || toleranceNm <= 0) return FromWire(wireHit, xNm, yNm);

        int dbuPerMicron = view.DbuPerMicron;
        long tolDbu = ToDbu(toleranceNm, dbuPerMicron);
        if (tolDbu <= 0) return FromWire(wireHit, xNm, yNm);

        var counters = new SnapQueryCounters();
        var candidates = LayoutSnapQuery.FindCandidates(
            view, tech, baseDir ?? string.Empty,
            ToDbu(xNm, dbuPerMicron), ToDbu(yNm, dbuPerMicron), tolDbu,
            includeIntersections, excludeShapeIndices: null, excludeInstanceIndices: null,
            ref counters);

        // FindCandidates already sorts by priority then distance (R-snp-5), so the first is the one
        // the layout editor's own marker would show. Taking any other would make the wBond overlay
        // disagree with the marker under the same cursor.
        if (candidates.Count == 0) return FromWire(wireHit, xNm, yNm);

        var best = candidates[0];
        var geometry = new Result(true, ToNm(best.X, dbuPerMicron), ToNm(best.Y, dbuPerMicron), best.Kind);

        return Better(geometry, wireHit, xNm, yNm);
    }

    /// <summary>
    /// Which of the two answers wins — <b>by the same (priority, then distance) rule the layout engine
    /// sorts its own candidates with</b>, so a wire competes with a pad corner on exactly the terms two
    /// pad corners compete with each other.
    ///
    /// <para>A wire VERTEX is ranked <see cref="SnapFeatureKind.CornerEndpoint"/> and a point along a
    /// wire <see cref="SnapFeatureKind.Nearest"/>: a vertex is an intentional feature (it is where a
    /// bond lands and where the loop bends), a point mid-segment is the same "somewhere on this edge"
    /// answer nearest-on-edge already means. Mapping them into the existing vocabulary rather than
    /// inventing wire-specific kinds is also what lets the snap MARKER draw with no new case.</para>
    /// </summary>
    private static Result Better(Result geometry, WireSnapResult wire, long xNm, long yNm)
    {
        if (!wire.Found) return geometry;

        var wireResult = FromWire(wire, xNm, yNm);
        if (!geometry.Snapped) return wireResult;

        if (wireResult.Kind != geometry.Kind)
            return wireResult.Kind < geometry.Kind ? wireResult : geometry;

        // Same rank: nearest to the cursor wins.
        double gx = geometry.XNm - (double)xNm, gy = geometry.YNm - (double)yNm;
        return wire.DistanceNm * wire.DistanceNm < gx * gx + gy * gy ? wireResult : geometry;
    }

    private static Result FromWire(WireSnapResult wire, long xNm, long yNm) =>
        wire.Found
            ? new Result(true, wire.XNm, wire.YNm,
                         wire.Kind == WireSnapKind.Vertex
                             ? SnapFeatureKind.CornerEndpoint
                             : SnapFeatureKind.Nearest)
            : Result.Miss(xNm, yNm);
}
