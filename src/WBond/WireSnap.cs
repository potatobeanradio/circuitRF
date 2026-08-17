namespace CircuitRF.WBond;

/// <summary>What kind of wire feature a snap landed on.</summary>
public enum WireSnapKind
{
    /// <summary>Nothing was within tolerance.</summary>
    None,

    /// <summary>A wire POINT — a foot or an interior vertex of the loop.</summary>
    Vertex,

    /// <summary>The nearest point on a wire SEGMENT, between two of its vertices.</summary>
    Segment,
}

/// <summary>Where a wire snap landed. <see cref="Kind"/> is <see cref="WireSnapKind.None"/> on a miss.</summary>
/// <param name="Wire">Flat wire index (<c>WBondDesign.AllWires</c> order), or −1 on a miss.</param>
/// <param name="Point">The vertex index, or for a segment its FIRST vertex. −1 on a miss.</param>
public readonly record struct WireSnapResult(
    WireSnapKind Kind, long XNm, long YNm, double DistanceNm, int Wire, int Point)
{
    public static WireSnapResult Miss => new(WireSnapKind.None, 0, 0, double.PositiveInfinity, -1, -1);

    public bool Found => Kind != WireSnapKind.None;
}

/// <summary>
/// Snapping a point to the WIRES themselves — their vertices and their segments — in the layout
/// (X-Y) plane.
///
/// <h3>Why this exists beside the layout's own snap engine</h3>
/// <para>A wire is not a layout shape (WB23: the wires are an overlay, nothing enters <c>.clay</c>),
/// so <c>LayoutSnapQuery</c> — which walks shapes, instances and their cached feature indices —
/// cannot see one and never will. Landing a wire foot exactly on another wire's foot, or on a point
/// along another wire, is nevertheless an ordinary thing to want: an array fanned out from a common
/// pad, a stitch onto an existing bond. So the wires get their own tiny query and the two answers are
/// merged by the caller, rather than teaching the layout index about a geometry it does not own.</para>
///
/// <h3>Both editors, one implementation</h3>
/// <para>The wBond editor snaps WIRE points to wires (through <c>WBondSnap</c>), and the layout editor
/// snaps SHAPE geometry to wires (through <c>LayoutEditorViewModel</c>'s own snap recompute, whose
/// <c>WireDesign</c> is the same design object). Two copies of "how close is that segment" would be
/// two chances to disagree about where a snap marker is drawn versus where the geometry lands.</para>
///
/// <h3>What is excluded, and why it must be</h3>
/// <para>A drag snaps to everything EXCEPT what it is dragging (<paramref name="excludeWire"/>). Left
/// in, a dragged wire's own vertices are always at distance zero from themselves, so every frame
/// would snap the point back onto where it already is and the wire could not be moved at all — the
/// same reason the layout's own query takes an exclusion set (R-snpf-4/5).</para>
///
/// <para><b>XY only.</b> This is the layout view's plane; z is carried by the caller unchanged. A
/// wire's loop height is what the profile view edits, and pulling a foot's z onto another wire's
/// apex because they happen to cross in plan would be nonsense.</para>
/// </summary>
public static class WireSnap
{
    /// <summary>
    /// The highest-priority wire feature within <paramref name="toleranceNm"/> of
    /// (<paramref name="xNm"/>, <paramref name="yNm"/>), or <see cref="WireSnapResult.Miss"/>.
    ///
    /// <para>A VERTEX outranks a segment at any distance inside the tolerance, mirroring the layout
    /// engine's own priority order (corner/endpoint above nearest-on-edge, R-snp-5): a user reaching
    /// near the end of a wire means its end, not the line an eyelash away from it.</para>
    /// </summary>
    /// <param name="excludeWire">
    /// Given a flat wire index, returns true to leave that wire out. Null includes every wire.
    /// </param>
    public static WireSnapResult Nearest(WBondDesign? design, long xNm, long yNm, long toleranceNm,
                                         Func<int, bool>? excludeWire = null)
    {
        if (design is null || toleranceNm <= 0) return WireSnapResult.Miss;

        double tol = toleranceNm;
        double tolSq = tol * tol;

        var bestVertex = WireSnapResult.Miss;
        var bestSegment = WireSnapResult.Miss;

        int index = -1;
        foreach (var wire in design.AllWires())
        {
            index++;
            if (excludeWire is not null && excludeWire(index)) continue;
            if (wire.Points.Count == 0) continue;

            for (int i = 0; i < wire.Points.Count; i++)
            {
                var p = wire.Points[i];
                double dx = p.X - xNm, dy = p.Y - yNm;
                double dsq = dx * dx + dy * dy;

                if (dsq <= tolSq && dsq < bestVertex.DistanceNm * bestVertex.DistanceNm)
                    bestVertex = new WireSnapResult(WireSnapKind.Vertex, p.X, p.Y, Math.Sqrt(dsq), index, i);
            }

            for (int i = 1; i < wire.Points.Count; i++)
            {
                var a = wire.Points[i - 1];
                var b = wire.Points[i];

                var (px, py) = NearestOnSegment(a, b, xNm, yNm);
                double dx = px - xNm, dy = py - yNm;
                double dsq = dx * dx + dy * dy;

                if (dsq <= tolSq && dsq < bestSegment.DistanceNm * bestSegment.DistanceNm)
                    bestSegment = new WireSnapResult(WireSnapKind.Segment,
                                                     (long)Math.Round(px), (long)Math.Round(py),
                                                     Math.Sqrt(dsq), index, i - 1);
            }
        }

        return bestVertex.Found ? bestVertex : bestSegment;
    }

    /// <summary>
    /// The point on segment a→b (in XY) closest to (<paramref name="xNm"/>, <paramref name="yNm"/>),
    /// clamped to the segment's own ends so a projection that falls beyond them lands on the end
    /// rather than off in space.
    /// </summary>
    private static (double X, double Y) NearestOnSegment(Point3 a, Point3 b, long xNm, long yNm)
    {
        double ex = b.X - (double)a.X, ey = b.Y - (double)a.Y;
        double lengthSquared = ex * ex + ey * ey;

        // Degenerate in plan (a purely vertical stub): the segment IS its own endpoint here.
        if (lengthSquared <= 0.0) return (a.X, a.Y);

        double t = ((xNm - (double)a.X) * ex + (yNm - (double)a.Y) * ey) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);

        return (a.X + ex * t, a.Y + ey * t);
    }
}
