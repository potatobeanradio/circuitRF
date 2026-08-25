// Shape promotion for a NON-AXIS-ALIGNED transform (brief-L3d-arbitrary-angle-instances.md R-L3d-6).
//
// THE BUG THIS EXISTS TO MAKE UNREPRESENTABLE. RectShape, RoundedRectShape and BitmapShape are stored
// as two corners (or a corner plus a size) and are therefore axis-aligned BY TYPE.
// LayoutCoordinateWalk maps those corner fields through the caller's transform, which is exactly right
// for a scale, a mirror or a 90-degree rotation — and silently wrong for any other angle: pushing a
// rect's (X1,Y1) and (X2,Y2) through 37 degrees and re-normalizing yields the AXIS-ALIGNED BOUNDING BOX
// of the rotated rect. That is plausible-looking output, on the right layer, at roughly the right
// place, with no error anywhere — the worst failure class this codebase keeps re-learning. So the walk
// now REFUSES those branches under a rotating transform (LayoutCoordinateTransform.RotatesAxes) and
// this file supplies the shape that can carry the rotation instead.
//
// The pattern — "promote to the more general representation first" — and the reference-equality
// signal for "nothing was promoted" are both taken verbatim from LayoutArcPromotion, which does the
// same job for a non-uniform scale (R-L1h-7). Two shapes deliberately have no promotion:
//   * CircleShape and ViaShape do not need one — a circle is rotation-invariant, and a via's pad is
//     drawn as a circle (LayoutRenderer's via branch), so both transform correctly as they are.
//   * BitmapShape cannot HAVE one — it is a min-corner-plus-size rect with no vertex list to rotate,
//     and a rotated reference image is not a thing the model can express. Callers skip it and say so;
//     LayoutCoordinateWalk's own bitmap branch has anticipated exactly this since L3c.

namespace CircuitRF.Ui.Layout;

public static class LayoutRotationPromotion
{
    /// <summary>tan(90 deg / 4) — the bulge of one counter-clockwise quarter-circle corner, in the
    /// bulge = tan(sweep/4) convention <see cref="LayoutArc"/> defines.</summary>
    private const double QuarterTurnBulge = 0.41421356237309503;

    /// <summary>True when <paramref name="shape"/>'s TYPE presumes axis alignment and a rotating
    /// transform therefore needs <see cref="Promote"/> first.</summary>
    public static bool NeedsPromotion(LayoutShape shape) => shape is RectShape or RoundedRectShape;

    /// <summary>True when <paramref name="shape"/> cannot be rotated at all and the caller must skip
    /// it and report it — see this file's header for why a bitmap is the only such shape.</summary>
    public static bool CannotRotate(LayoutShape shape) => shape is BitmapShape;

    /// <summary>
    /// Returns an equivalent shape whose representation survives a rotation: a <see cref="RectShape"/>
    /// becomes a four-vertex <see cref="PolygonShape"/>, a <see cref="RoundedRectShape"/> becomes a
    /// <see cref="CurveShape"/> of four lines and four quarter-circle arc edges (or a plain polygon
    /// when its corner radius clamps to zero). Anything else is returned AS-IS — the same instance, so
    /// callers can use reference equality to tell whether a promotion actually happened, exactly as
    /// <see cref="LayoutArcPromotion.PromoteArcsToCubics"/> does.
    /// </summary>
    public static LayoutShape Promote(LayoutShape shape) => shape switch
    {
        RectShape r         => RectToPolygon(r),
        RoundedRectShape rr => RoundedRectToCurve(rr),
        _                   => shape,
    };

    private static PolygonShape RectToPolygon(RectShape r)
    {
        long x1 = Math.Min(r.X1, r.X2), x2 = Math.Max(r.X1, r.X2);
        long y1 = Math.Min(r.Y1, r.Y2), y2 = Math.Max(r.Y1, r.Y2);
        // Counter-clockwise from the minimum corner, matching LayoutFlattener's own rectangle ring.
        return new PolygonShape { Layer = r.Layer, Net = r.Net, Xy = [x1, y1, x2, y1, x2, y2, x1, y2] };
    }

    /// <summary>
    /// Eight vertices and eight edges, counter-clockwise from the bottom edge's start — the SAME
    /// traversal and the SAME corner-radius clamp as <c>LayoutFlattener.FlattenRoundedRect</c>, so a
    /// promoted rounded rect and a flattened one describe the same outline rather than two outlines
    /// that merely look alike. Every corner is one counter-clockwise quarter turn, hence a positive
    /// <see cref="QuarterTurnBulge"/> on each (positive bulge sweeps in the direction of increasing
    /// angle — <see cref="LayoutArc"/>'s stated convention).
    /// </summary>
    private static LayoutShape RoundedRectToCurve(RoundedRectShape rr)
    {
        long x1 = Math.Min(rr.X1, rr.X2), x2 = Math.Max(rr.X1, rr.X2);
        long y1 = Math.Min(rr.Y1, rr.Y2), y2 = Math.Max(rr.Y1, rr.Y2);
        long cr = Math.Max(0, Math.Min(rr.CornerRadius, Math.Min(x2 - x1, y2 - y1) / 2));

        if (cr <= 0)
            return new PolygonShape { Layer = rr.Layer, Net = rr.Net, Xy = [x1, y1, x2, y1, x2, y2, x1, y2] };

        var line = () => new LayoutEdge { Kind = EdgeKind.Line };
        var arc  = () => new LayoutEdge { Kind = EdgeKind.Arc, Bulge = QuarterTurnBulge };

        return new CurveShape
        {
            Layer = rr.Layer,
            Net   = rr.Net,
            Xy =
            [
                x1 + cr, y1,   x2 - cr, y1,     // bottom edge, then the (x2,y1) corner
                x2,      y1 + cr, x2,   y2 - cr, // right edge,  then the (x2,y2) corner
                x2 - cr, y2,   x1 + cr, y2,     // top edge,    then the (x1,y2) corner
                x1,      y2 - cr, x1,   y1 + cr, // left edge,   then the (x1,y1) corner closes it
            ],
            Edges = [line(), arc(), line(), arc(), line(), arc(), line(), arc()],
            FlattenTolDbu = rr.FlattenTolDbu,
        };
    }
}
