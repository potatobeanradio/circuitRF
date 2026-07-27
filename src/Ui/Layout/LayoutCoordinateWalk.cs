// The ONE per-shape coordinate traversal (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md
// R-L1h-6). Before this file existed, DBU resolution change (LayoutScaling.ScaleShape) and paste
// rescale (LayoutFragment.RescaleShape) were two independently hand-maintained copies of the exact
// same switch-over-shape-kind field list — already caught drifting once (this file's own history:
// both omitted CircleShape/RoundedRectShape.FlattenTolDbu identically, simply because those fields
// didn't exist yet). Scale (L1h) is a third mutator of the same field list; rather than write it a
// third time, every one of the three now calls this.

using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// A coordinate transform, split into X / Y / Magnitude because a uniform transform (DBU resolution
/// change, paste rescale, or a uniform Scale) is the degenerate case where all three coincide, while
/// a non-uniform Scale needs X and Y to differ. <see cref="Magnitude"/> is for scalar lengths with no
/// single axis of their own (a circle's radius, a rounded-rect's corner radius, a path's width, a
/// via's pad/drill, a label's height, a flatten tolerance) — under a NON-uniform transform there is no
/// exact answer for what these become (a corner radius scaled 2×1 would need to become elliptical,
/// which the model cannot represent outside the Circle→Curve promotion R-L1h-7 already carves out for
/// arcs specifically), so callers scaling non-uniformly should supply the isotropic-equivalent factor
/// (e.g. <c>sqrt(fx*fy)</c>) rather than leave these fields untouched.
/// </summary>
public readonly record struct LayoutCoordinateTransform(
    Func<long, long> X,
    Func<long, long> Y,
    Func<long, long> Magnitude)
{
    /// <summary>A single scalar ratio applied identically to every axis and every magnitude — what
    /// DBU resolution change and paste rescale always use (both are inherently uniform; there is no
    /// such thing as a non-uniform DBU resolution or a non-uniform cross-workspace rescale).</summary>
    public static LayoutCoordinateTransform Uniform(Func<long, long> f) => new(f, f, f);
}

public static class LayoutCoordinateWalk
{
    /// <summary>
    /// Applies <paramref name="t"/> to every coordinate field of <paramref name="shape"/> — outer
    /// vertices, hole rings, cubic control points, circle radius, rounded-rect corner radius, path
    /// width, via pad/drill, label position/height, and <c>FlattenTolDbu</c> (the full field list
    /// R-L1h-6 names). Mutates in place. <b>Arc bulge is a dimensionless sweep-angle descriptor, not
    /// a coordinate, and is never touched here</b> — scaling it would silently change the arc's
    /// curvature relative to its (now-transformed) chord.
    /// </summary>
    public static void Transform(LayoutShape shape, LayoutCoordinateTransform t)
    {
        switch (shape)
        {
            case RectShape r:
                r.X1 = t.X(r.X1); r.Y1 = t.Y(r.Y1); r.X2 = t.X(r.X2); r.Y2 = t.Y(r.Y2);
                break;
            case PolygonShape p:
                TransformArray(p.Xy, t);
                TransformHoles(p.Holes, t);
                break;
            case RoundedRectShape rr:
                rr.X1 = t.X(rr.X1); rr.Y1 = t.Y(rr.Y1); rr.X2 = t.X(rr.X2); rr.Y2 = t.Y(rr.Y2);
                rr.CornerRadius = t.Magnitude(rr.CornerRadius);
                if (rr.FlattenTolDbu is { } rrTol) rr.FlattenTolDbu = t.Magnitude(rrTol);
                break;
            case CircleShape c:
                c.Cx = t.X(c.Cx); c.Cy = t.Y(c.Cy); c.R = t.Magnitude(c.R);
                if (c.FlattenTolDbu is { } cTol) c.FlattenTolDbu = t.Magnitude(cTol);
                break;
            case CurveShape curve:
                TransformArray(curve.Xy, t);
                TransformCubicControlPoints(curve.Edges, t);
                TransformHoles(curve.Holes, t);
                if (curve.FlattenTolDbu is { } curveTol) curve.FlattenTolDbu = t.Magnitude(curveTol);
                break;
            case PathShape path:
                TransformArray(path.Xy, t);
                TransformCubicControlPoints(path.Edges, t);
                path.Width = t.Magnitude(path.Width);
                if (path.FlattenTolDbu is { } pathTol) path.FlattenTolDbu = t.Magnitude(pathTol);
                break;
            case ViaShape via:
                via.X = t.X(via.X); via.Y = t.Y(via.Y);
                via.PadSize = t.Magnitude(via.PadSize); via.DrillSize = t.Magnitude(via.DrillSize);
                break;
            case LabelShape label:
                label.X = t.X(label.X); label.Y = t.Y(label.Y);
                label.Height = t.Magnitude(label.Height);
                break;
            case BitmapShape bmp:
            {
                // W/H are derived from the transformed OPPOSITE corner, mirroring how Rect/RoundedRect
                // implicitly handle non-uniform scale via their own X2/Y2 — a min-corner+size shape has
                // no second corner field to transform directly, so this reconstructs the same effect.
                long x2 = t.X(bmp.X + bmp.W);
                long y2 = t.Y(bmp.Y + bmp.H);
                bmp.X = t.X(bmp.X);
                bmp.Y = t.Y(bmp.Y);
                bmp.W = x2 - bmp.X;
                bmp.H = y2 - bmp.Y;
                break;
            }
        }
    }

    private static void TransformArray(long[] xy, LayoutCoordinateTransform t)
    {
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            xy[i]     = t.X(xy[i]);
            xy[i + 1] = t.Y(xy[i + 1]);
        }
    }

    /// <summary>Cubic control points are coordinates and easy to miss (they are NOT in the Xy vertex
    /// list). Bulge (Arc edges) is dimensionless and deliberately excluded — see the type doc.</summary>
    private static void TransformCubicControlPoints(List<LayoutEdge>? edges, LayoutCoordinateTransform t)
    {
        if (edges is null) return;
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) continue;
            e.C1X = t.X(e.C1X); e.C1Y = t.Y(e.C1Y);
            e.C2X = t.X(e.C2X); e.C2Y = t.Y(e.C2Y);
        }
    }

    /// <summary>Holes (§3.1a) are absolute-coordinate rings — the same "easy to miss" list as cubic
    /// control points, and equally not part of the outer Xy vertex list.</summary>
    private static void TransformHoles(List<long[]>? holes, LayoutCoordinateTransform t)
    {
        if (holes is null) return;
        foreach (var hole in holes) TransformArray(hole, t);
    }
}
