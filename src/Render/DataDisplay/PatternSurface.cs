// ================================================================
//  PatternSurface.cs  —  ANT-10: the camera, the named views and the
//  (theta, phi) grid a 3D pattern surface is drawn from
//
//  §1: "a surface r(theta, phi) = the pattern in dB above a floor, drawn
//  over the upper hemisphere, coloured by the same value, rotatable."
//  This file is everything about that which is ARITHMETIC — no Skia, no
//  canvas, no theme — so the gates of §6 can assert the GEOMETRY (peak
//  direction, null directions, symmetry, depth order) rather than pixels.
//
//  ORTHOGRAPHIC, and §3 says why: perspective is not measurably clearer
//  for a pattern and is easier to get wrong. An orthographic projection
//  also has the property the depth sort depends on — depth is a linear
//  functional of position, so a facet's centroid depth IS the mean of its
//  vertices' and there is no near-plane to divide by.
//
//  THE RADIUS IS ANT-7's. PolarPatternScale.Radius maps dB to [0, 1]
//  against the plot's own floor and reference, clamping below the floor
//  rather than dropping the sample — so a null in the antenna and a
//  clipped value look the same in 3D as they do on the polar cut, which
//  is the point of not having a second scale.
// ================================================================

using System;
using System.Collections.Generic;

namespace CircuitRF.Render.DataDisplay;

/// <summary>
/// The views §3 asks for by name, because "which way am I looking" is the question a 3D picture
/// always raises and a button answers it better than a gesture.
///
/// <para><b>The two principal planes are named by their own φ, not "E-plane" and "H-plane".</b>
/// Which cut is the E-plane is a property of the ANTENNA's polarization, and the cube does not say
/// — ANT-6 computes polarization separately and can disagree with a guess made from the axis names.
/// A view button that named the wrong plane would be a caption that is confidently wrong, which is
/// worse than one that is merely literal.</para>
/// </summary>
public enum SurfaceStandardView
{
    /// <summary>The default, and what Reset returns to. Off both principal planes so the surface
    /// reads as a solid rather than as a silhouette.</summary>
    Isometric,

    /// <summary>Looking straight down the +z axis at the zenith — the pattern in plan.</summary>
    Broadside,

    /// <summary>The φ = 0° / 180° plane lying IN the screen, so that cut is read directly.</summary>
    PhiZeroPlane,

    /// <summary>The φ = 90° / 270° plane lying in the screen.</summary>
    PhiNinetyPlane,
}

/// <summary>
/// An orthographic camera on the pattern scene: where the eye is (two angles) and how much of the
/// canvas the unit sphere fills.
///
/// <para><b>Two angles and a scale, and deliberately not a matrix.</b> A rotation the user can only
/// reach through drag/zoom/reset has exactly two degrees of freedom, so storing nine numbers would
/// be storing six that can never be independently set — and a `.cdd` round trip has to reproduce the
/// view exactly, which two angles do by construction and an orthonormalised matrix does only if
/// nothing ever renormalises it.</para>
/// </summary>
public readonly record struct PatternCamera
{
    /// <summary>Rotation of the eye about the +z axis, degrees. Wrapped to [0, 360).</summary>
    public double AzimuthDeg { get; init; }

    /// <summary>Elevation of the eye above the ground plane, degrees. Clamped to [−90, +90];
    /// <b>negative is allowed and is informative</b> — from below, a hemisphere shows nothing but
    /// the ground disc, which is §4's statement made by the view itself.</summary>
    public double ElevationDeg { get; init; }

    /// <summary>Canvas fill factor; 1 frames the unit sphere with room for the axes.
    /// Clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>].</summary>
    public double Zoom { get; init; }

    public const double MinZoom = 0.25;
    public const double MaxZoom = 8.0;

    public static PatternCamera Default => For(SurfaceStandardView.Isometric);

    /// <summary>The named views of §3. Azimuth and elevation only — a standard view never changes
    /// the zoom, because a user who has zoomed in to look at a lobe is asking to keep looking at it
    /// from a stated direction, not to start over.</summary>
    public static PatternCamera For(SurfaceStandardView view, double zoom = 1.0) => view switch
    {
        SurfaceStandardView.Broadside      => New(  0, 90, zoom),
        SurfaceStandardView.PhiZeroPlane   => New( 90,  0, zoom),
        SurfaceStandardView.PhiNinetyPlane => New(  0,  0, zoom),
        _                                  => New( 35, 25, zoom),
    };

    public static PatternCamera New(double azDeg, double elDeg, double zoom = 1.0) => new()
    {
        AzimuthDeg   = Wrap360(azDeg),
        ElevationDeg = double.IsFinite(elDeg) ? Math.Clamp(elDeg, -90, 90) : 0,
        Zoom         = double.IsFinite(zoom)  ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1.0,
    };

    /// <summary>A drag. <b>Dragging right turns the scene left</b> — the object follows the pointer,
    /// which is what every direct-manipulation 3D view does and is the opposite of moving a camera.</summary>
    public PatternCamera RotatedBy(double dAzDeg, double dElDeg) =>
        New(AzimuthDeg + dAzDeg, ElevationDeg + dElDeg, Zoom);

    public PatternCamera ZoomedBy(double factor) =>
        !double.IsFinite(factor) || factor <= 0 ? this : New(AzimuthDeg, ElevationDeg, Zoom * factor);

    public PatternCamera WithZoom(double zoom) => New(AzimuthDeg, ElevationDeg, zoom);

    /// <summary>True when this camera IS one of the named views, to within a tick of rounding —
    /// what a view button reads to show itself as the active one.</summary>
    public bool Is(SurfaceStandardView view)
    {
        var v = For(view, Zoom);
        return Math.Abs(Wrap180(v.AzimuthDeg - AzimuthDeg)) < 1e-6
            && Math.Abs(v.ElevationDeg - ElevationDeg) < 1e-6;
    }

    // ---- The basis -------------------------------------------------------

    /// <summary>The unit vector from the origin toward the eye.</summary>
    public (double X, double Y, double Z) Eye
    {
        get
        {
            double a = AzimuthDeg * Math.PI / 180.0, e = ElevationDeg * Math.PI / 180.0;
            return (Math.Cos(e) * Math.Cos(a), Math.Cos(e) * Math.Sin(a), Math.Sin(e));
        }
    }

    /// <summary>Screen +x in world coordinates. Independent of elevation, which is what keeps the
    /// basis well conditioned at the poles — a broadside view is an ordinary case here, not a
    /// gimbal lock to be special-cased.</summary>
    public (double X, double Y, double Z) Right
    {
        get
        {
            double a = AzimuthDeg * Math.PI / 180.0;
            return (-Math.Sin(a), Math.Cos(a), 0.0);
        }
    }

    /// <summary>Screen +y in world coordinates, <c>Eye × Right</c>. Points toward +z whenever the
    /// eye is above the horizon.</summary>
    public (double X, double Y, double Z) Up
    {
        get
        {
            double a = AzimuthDeg * Math.PI / 180.0, e = ElevationDeg * Math.PI / 180.0;
            double se = Math.Sin(e), ce = Math.Cos(e);
            return (-se * Math.Cos(a), -se * Math.Sin(a), ce);
        }
    }

    /// <summary>
    /// World → scene. <b>Y is UP in the result</b>, as every world window in this code base is; the
    /// renderer flips it once on the way to the canvas. <see cref="Zoom"/> is already applied.
    /// </summary>
    /// <returns>Depth increases TOWARD the eye, so a painter's algorithm draws in ascending depth.</returns>
    public (double X, double Y, double Depth) Project(double x, double y, double z)
    {
        var r = Right; var u = Up; var e = Eye;
        return (Zoom * (x * r.X + y * r.Y + z * r.Z),
                Zoom * (x * u.X + y * u.Y + z * u.Z),
                        x * e.X + y * e.Y + z * e.Z);
    }

    /// <summary>The unit direction of one (θ, φ) in degrees — the ray the pattern's radius is
    /// measured along, on the layout's own axes (θ from +z, φ from +x toward +y).</summary>
    public static (double X, double Y, double Z) Direction(double thetaDeg, double phiDeg)
    {
        double t = thetaDeg * Math.PI / 180.0, p = phiDeg * Math.PI / 180.0;
        double st = Math.Sin(t);
        return (st * Math.Cos(p), st * Math.Sin(p), Math.Cos(t));
    }

    private static double Wrap360(double d)
    {
        if (!double.IsFinite(d)) return 0;
        d %= 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    private static double Wrap180(double d)
    {
        d = Wrap360(d);
        return d > 180 ? d - 360 : d;
    }
}

/// <summary>
/// One trace's pattern over two free angle axes — what <see cref="SurfaceResolve"/> pulls out of a
/// rank-2 cube slice, in the cube's own units, before any scale is applied.
///
/// <para><b>It is not a family of curves.</b> The family mechanism would express the same grid
/// (θ as X, φ iterated) and was rejected for one reason: <c>Trace.MaxFamilyCurves</c> caps a family
/// at 101 curves, and ANT-11's 0…180° θ axis at 1° is 181. A surface silently missing everything
/// past θ = 101° is exactly the plausible-and-wrong picture this series keeps refusing to draw.</para>
/// </summary>
public sealed class PatternSurfaceGrid
{
    public required IReadOnlyList<double> ThetaDeg { get; init; }
    public required IReadOnlyList<double> PhiDeg   { get; init; }

    /// <summary>Row-major <c>[theta, phi]</c>, in the trace's own displayed dB quantity.
    /// Non-finite entries are legal and are dropped by the mesh, exactly as a rect trace drops them.</summary>
    public required double[] Db { get; init; }

    /// <summary>The cube axis the θ values came from — <c>"theta"</c> or <c>"el"</c>. Carried so the
    /// hemisphere note of §4 is built from the AXIS and never written as a constant.</summary>
    public required string ThetaAxisName { get; init; }
    public required string PhiAxisName   { get; init; }

    public int ThetaCount => ThetaDeg.Count;
    public int PhiCount   => PhiDeg.Count;

    public double At(int it, int ip) => Db[it * PhiCount + ip];

    /// <summary>
    /// Whether the φ axis goes all the way round, so the last column joins the first. Detected from
    /// the axis rather than assumed: a grid sampled 0…350° in 10° steps closes, and one sampled
    /// 0…360° already carries the duplicate column and must NOT be closed again — a doubled seam
    /// draws a dark stripe through the lobe.
    /// </summary>
    public bool PhiWraps
    {
        get
        {
            int n = PhiCount;
            if (n < 3) return false;
            double span = PhiDeg[n - 1] - PhiDeg[0];
            if (Math.Abs(span) >= 360.0 - 1e-6) return false;      // endpoint already duplicated
            double step = span / (n - 1);
            return Math.Abs(Math.Abs(span) + Math.Abs(step) - 360.0) < Math.Abs(step) * 0.5 + 1e-9;
        }
    }

    /// <summary>The largest finite dB on the grid, and where it is. NaN when the grid is empty.</summary>
    public (double Db, double ThetaDeg, double PhiDeg) Peak()
    {
        double best = double.NegativeInfinity; int bt = -1, bp = -1;
        for (int it = 0; it < ThetaCount; it++)
            for (int ip = 0; ip < PhiCount; ip++)
            {
                double v = At(it, ip);
                if (double.IsFinite(v) && v > best) { best = v; bt = it; bp = ip; }
            }
        return bt < 0
            ? (double.NaN, double.NaN, double.NaN)
            : (best, ThetaDeg[bt], PhiDeg[bp]);
    }
}

/// <summary>
/// One drawn triangle of the surface, in SCENE coordinates (y up, zoom applied) with the depth and
/// the value that decide its order and its colour.
/// </summary>
/// <remarks>
/// <b>Triangles, not quads.</b> A (θ, φ) cell of a star-shaped surface is only near-planar, and a
/// non-planar quad filled as one path folds visibly wherever the pattern turns quickly — which is at
/// a null, the one feature the picture exists to show. Two triangles cannot fold, and they sort
/// independently, which is what makes §6's "a lobe behind the origin" case come out right.
/// </remarks>
public readonly record struct PatternFacet(
    double X0, double Y0, double X1, double Y1, double X2, double Y2,
    double Depth, double Db);

public static class PatternMesh
{
    /// <summary>
    /// The whole surface, <b>sorted far-to-near</b> and ready to fill in order.
    /// </summary>
    /// <param name="grid">The pattern, in dB.</param>
    /// <param name="scale">ANT-7's radial scale — the same floor and reference the polar cut uses.</param>
    /// <param name="camera">Where the eye is.</param>
    /// <param name="stride">Take every n-th sample on BOTH axes. 1 is every sample; §6's
    /// interaction decimation raises it and the endpoints are always kept, so a decimated surface
    /// has the same silhouette and the same peak direction as the full one.</param>
    public static PatternFacet[] Build(PatternSurfaceGrid grid, PolarPatternScale scale,
                                       PatternCamera camera, int stride = 1)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(scale);

        int[] ti = Sample(grid.ThetaCount, stride);
        int[] pi = Sample(grid.PhiCount,   stride);
        if (ti.Length < 2 || pi.Length < 1) return [];

        bool wrap = grid.PhiWraps;
        int  pn   = pi.Length;
        int  pCells = wrap ? pn : pn - 1;
        if (pCells < 1) return [];

        // Project every sampled vertex once. A (θ, φ) vertex is shared by up to four cells and
        // projecting it per cell would be four times the trigonometry for the same answer.
        var vx = new double[ti.Length * pn];
        var vy = new double[ti.Length * pn];
        var vd = new double[ti.Length * pn];
        var vv = new double[ti.Length * pn];
        for (int a = 0; a < ti.Length; a++)
            for (int b = 0; b < pn; b++)
            {
                double db = grid.At(ti[a], pi[b]);
                double r  = scale.Radius(db);
                int k = a * pn + b;
                if (!double.IsFinite(r)) { vv[k] = double.NaN; continue; }
                var (dx, dy, dz) = PatternCamera.Direction(grid.ThetaDeg[ti[a]], grid.PhiDeg[pi[b]]);
                var (sx, sy, sd) = camera.Project(r * dx, r * dy, r * dz);
                vx[k] = sx; vy[k] = sy; vd[k] = sd; vv[k] = db;
            }

        var facets = new List<PatternFacet>((ti.Length - 1) * pCells * 2);
        for (int a = 0; a + 1 < ti.Length; a++)
            for (int b = 0; b < pCells; b++)
            {
                int b1 = (b + 1) % pn;
                int k00 = a * pn + b,  k01 = a * pn + b1;
                int k10 = (a + 1) * pn + b, k11 = (a + 1) * pn + b1;
                Add(facets, vx, vy, vd, vv, k00, k01, k11);
                Add(facets, vx, vy, vd, vv, k00, k11, k10);
            }

        var result = facets.ToArray();
        // Ascending depth: the farthest facet is painted first and everything nearer covers it.
        var keys = new double[result.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = result[i].Depth;
        Array.Sort(keys, result);
        return result;
    }

    private static void Add(List<PatternFacet> into,
                            double[] vx, double[] vy, double[] vd, double[] vv,
                            int a, int b, int c)
    {
        if (!double.IsFinite(vv[a]) || !double.IsFinite(vv[b]) || !double.IsFinite(vv[c])) return;

        // A degenerate triangle is the θ = 0 pole, where every φ collapses to one point. It draws
        // nothing, so it is dropped here rather than sorted and filled n times.
        double ax = vx[a], ay = vy[a], bx = vx[b], by = vy[b], cx = vx[c], cy = vy[c];
        double area2 = Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay));
        if (area2 < 1e-14) return;

        into.Add(new PatternFacet(ax, ay, bx, by, cx, cy,
                                  (vd[a] + vd[b] + vd[c]) / 3.0,
                                  (vv[a] + vv[b] + vv[c]) / 3.0));
    }

    /// <summary>
    /// Indices taken every <paramref name="stride"/> samples, <b>always including the last one</b>.
    /// Dropping the final θ row would open the surface at the horizon and dropping the final φ
    /// column would leave a slit — both read as rendering faults rather than as decimation.
    /// </summary>
    internal static int[] Sample(int count, int stride)
    {
        if (count <= 0) return [];
        if (stride < 1) stride = 1;
        if (count <= 2 || stride == 1)
        {
            var all = new int[count];
            for (int i = 0; i < count; i++) all[i] = i;
            return all;
        }
        var list = new List<int>(count / stride + 2);
        for (int i = 0; i < count - 1; i += stride) list.Add(i);
        if (list[^1] != count - 1) list.Add(count - 1);
        return list.ToArray();
    }
}
