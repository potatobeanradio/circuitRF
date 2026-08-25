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
/// A coordinate transform: <see cref="Point"/> maps a whole (x,y) pair together (so it CAN express a
/// 90°-rotation, which mixes X and Y — brief-L3c-flatten-and-group.md R-L3c-2 generalized this from
/// axis-independent scale-only to full affine specifically so Flatten Hierarchy could become the
/// FOURTH consumer of this one walk instead of writing a parallel traversal that risks forgetting hole
/// rings, exactly as R-L1h-6's own history already did once). <see cref="Magnitude"/> is for scalar
/// lengths with no single axis of their own (a circle's radius, a rounded-rect's corner radius, a
/// path's width, a via's pad/drill, a label's height, a flatten tolerance) — under a NON-uniform
/// transform there is no exact answer for what these become (a corner radius scaled 2×1 would need to
/// become elliptical, which the model cannot represent outside the Circle→Curve promotion R-L1h-7
/// already carves out for arcs specifically), so callers scaling non-uniformly should supply the
/// isotropic-equivalent factor (e.g. <c>sqrt(fx*fy)</c>) rather than leave these fields untouched.
/// <b>Arc bulge is never part of this transform</b> (see <see cref="LayoutCoordinateWalk.Transform"/>'s
/// own doc comment) — a ROTATION-only transform leaves bulge unchanged, but a MIRROR flips its sign;
/// that is Flatten's own concern (<c>LayoutFlatten.FlipBulgeSigns</c>), applied as a small separate
/// pass, never folded into this walk (the other three callers here — DBU resolution change, paste
/// rescale, Scale — must NEVER touch bulge, mirror or not).
/// </summary>
public readonly record struct LayoutCoordinateTransform(
    Func<long, long, (long X, long Y)> Point,
    Func<long, long> Magnitude,
    bool RotatesAxes = false)
{
    /// <summary><b>Set by a caller whose <see cref="Point"/> does not map the X and Y axes onto axes —
    /// i.e. a rotation that is not a multiple of 90 degrees</b> (brief-L3d-arbitrary-angle-instances.md
    /// R-L3d-7). Shapes stored as two corners are axis-aligned BY TYPE, so under such a transform
    /// <see cref="LayoutCoordinateWalk.Transform"/> REFUSES them rather than quietly emitting the
    /// bounding box of the rotated shape; <see cref="LayoutRotationPromotion"/> is what the caller runs
    /// first to get a shape that can carry the rotation. False for every axis-preserving caller — DBU
    /// resolution change, paste rescale, Scale, and a flatten at a cardinal angle — all of which are
    /// unaffected by any of this.</summary>
    public bool RotatesAxes { get; init; } = RotatesAxes;

    /// <summary>A single scalar ratio applied identically to every axis and every magnitude — what
    /// DBU resolution change and paste rescale always use (both are inherently uniform; there is no
    /// such thing as a non-uniform DBU resolution or a non-uniform cross-workspace rescale).</summary>
    public static LayoutCoordinateTransform Uniform(Func<long, long> f) => new((x, y) => (f(x), f(y)), f);

    /// <summary>Two independent per-axis scalar functions plus a magnitude function — what Scale (L1h)
    /// uses for a possibly-non-uniform factor. Cannot express rotation (X and Y never mix); use this
    /// only when the transform is genuinely axis-aligned.</summary>
    public static LayoutCoordinateTransform AxisIndependent(Func<long, long> fx, Func<long, long> fy, Func<long, long> fMagnitude) =>
        new((x, y) => (fx(x), fy(y)), fMagnitude);
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
        if (t.RotatesAxes && (LayoutRotationPromotion.NeedsPromotion(shape) || LayoutRotationPromotion.CannotRotate(shape)))
            throw new InvalidOperationException(
                $"{shape.GetType().Name} is axis-aligned by type and cannot be walked through a rotating " +
                "transform — run LayoutRotationPromotion.Promote first (a bitmap has no promotion and must " +
                "be skipped). See LayoutRotationPromotion's header (R-L3d-7).");

        switch (shape)
        {
            case RectShape r:
                (r.X1, r.Y1) = t.Point(r.X1, r.Y1);
                (r.X2, r.Y2) = t.Point(r.X2, r.Y2);
                break;
            case PolygonShape p:
                TransformArray(p.Xy, t);
                TransformHoles(p.Holes, t);
                break;
            case RoundedRectShape rr:
                (rr.X1, rr.Y1) = t.Point(rr.X1, rr.Y1);
                (rr.X2, rr.Y2) = t.Point(rr.X2, rr.Y2);
                rr.CornerRadius = t.Magnitude(rr.CornerRadius);
                if (rr.FlattenTolDbu is { } rrTol) rr.FlattenTolDbu = t.Magnitude(rrTol);
                break;
            case CircleShape c:
                (c.Cx, c.Cy) = t.Point(c.Cx, c.Cy);
                c.R = t.Magnitude(c.R);
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
                (via.X, via.Y) = t.Point(via.X, via.Y);
                via.PadSize = t.Magnitude(via.PadSize); via.DrillSize = t.Magnitude(via.DrillSize);
                break;
            case LabelShape label:
                (label.X, label.Y) = t.Point(label.X, label.Y);
                label.Height = t.Magnitude(label.Height);
                break;
            case BitmapShape bmp:
            {
                // W/H are derived from the transformed OPPOSITE corner, mirroring how Rect/RoundedRect
                // implicitly handle non-uniform scale via their own X2/Y2 — a min-corner+size shape has
                // no second corner field to transform directly, so this reconstructs the same effect.
                // A rotating transform would invalidate a min-corner+size shape's own axis-aligned
                // assumption — which is no longer left to callers to remember: the RotatesAxes guard at
                // the top of this method refuses a BitmapShape outright (R-L3d-7), since unlike a rect
                // there is no promotion that would let one rotate.
                var (x2, y2) = t.Point(bmp.X + bmp.W, bmp.Y + bmp.H);
                (bmp.X, bmp.Y) = t.Point(bmp.X, bmp.Y);
                bmp.W = x2 - bmp.X;
                bmp.H = y2 - bmp.Y;
                break;
            }
        }
    }

    private static void TransformArray(long[] xy, LayoutCoordinateTransform t)
    {
        for (int i = 0; i + 1 < xy.Length; i += 2)
            (xy[i], xy[i + 1]) = t.Point(xy[i], xy[i + 1]);
    }

    /// <summary>Cubic control points are coordinates and easy to miss (they are NOT in the Xy vertex
    /// list). Bulge (Arc edges) is dimensionless and deliberately excluded — see the type doc.</summary>
    private static void TransformCubicControlPoints(List<LayoutEdge>? edges, LayoutCoordinateTransform t)
    {
        if (edges is null) return;
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) continue;
            (e.C1X, e.C1Y) = t.Point(e.C1X, e.C1Y);
            (e.C2X, e.C2Y) = t.Point(e.C2X, e.C2Y);
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
