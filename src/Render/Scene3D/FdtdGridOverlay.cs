// brief-em3d-28 R-em3d28-3c — the FDTD grid circuitRF made, drawn where it can be read.
//
// FROM FdtdGrid.Build, EXACTLY: the lines are the grid's own coordinates, computed on demand from the
// setup with no run. They are drawn in two places only (§8.5):
//   * ON THE CLIP PLANE, when it lies on an axis — the two families of lines that lie in it, across
//     the grid's whole extent (PML included);
//   * ON CONDUCTOR SURFACES — where each grid plane crosses a conductor's triangles, which is where an
//     FDTD user sees whether the grid landed on the metal's edges.
// Never through the volume: a million-cell grid through the volume is noise.
//
// THE SMALLEST CELL ON EACH AXIS IS LABELLED, with where it is and what set it, because one tiny cell
// sets the time step for the whole run (em-3d.md §12).

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>A label for one axis's smallest cell: the sentence and the scene-local point it names.</summary>
public sealed record FdtdCellLabel(FdtdAxis Axis, double SmallestCellM, string Text, Vector3 At);

/// <summary>The drawn grid: line vertices, and the per-axis smallest-cell labels.</summary>
public sealed record FdtdGridDrawing(Scene3DVertex[] Lines, IReadOnlyList<FdtdCellLabel> Labels);

public static class FdtdGridOverlay
{
    public static FdtdGridDrawing Build(FdtdGridResult grid, Scene3DModel scene, in ClipPlane3D clip,
                                        bool dark = false, CancellationToken ct = default)
    {
        uint planeInk = dark ? Scene3DVertex.Pack(170, 175, 185, 255) : Scene3DVertex.Pack(110, 115, 125, 255);
        uint metalInk = dark ? Scene3DVertex.Pack(255, 120, 60, 255) : Scene3DVertex.Pack(215, 60, 20, 255);
        var outv = new List<Scene3DVertex>();

        // ── on the clip plane ────────────────────────────────────────────────────────────────
        if (clip.Enabled && clip.Axis != ClipAxis3D.View)
        {
            var a = clip.Axis switch { ClipAxis3D.X => FdtdAxis.X, ClipAxis3D.Y => FdtdAxis.Y, _ => FdtdAxis.Z };
            double at = clip.Offset + Coord(scene.Origin, a);
            var (b, c) = Others(a);
            var gb = grid.Axis(b); var gc = grid.Axis(c);
            if (gb.Lines.Count > 0 && gc.Lines.Count > 0)
            {
                foreach (double vb in gb.Lines) Segment(a, at, b, vb, c, gc.Lines[0], gc.Lines[^1], planeInk);
                foreach (double vc in gc.Lines) Segment(a, at, c, vc, b, gb.Lines[0], gb.Lines[^1], planeInk);
            }
        }

        // ── on conductor surfaces ────────────────────────────────────────────────────────────
        // Vertices are FLOAT and scene-local, so a vertex the grid put a line through exactly comes back
        // a few ulps above or below it — and which side decides whether the metal's own edge (the one
        // line §8.5 exists to show) is drawn. Coordinates within SnapUlps of a line are taken as ON it,
        // and a triangle with an EDGE on a line draws that edge.
        var far = Vector3.Max(Vector3.Abs(scene.BoundsMin), Vector3.Abs(scene.BoundsMax));
        float reach = MathF.Max(far.X, MathF.Max(far.Y, far.Z));
        double tol = SnapUlps * 1.1920929e-7 * reach;       // float's machine epsilon × the extent
        foreach (var batch in scene.Batches)
        {
            ct.ThrowIfCancellationRequested();
            var o = scene.Objects[batch.ObjectId - 1];
            if (o.Kind is not (Scene3DKind.Conductor or Scene3DKind.Via or Scene3DKind.Wire or Scene3DKind.Sheet)) continue;
            foreach (FdtdAxis axis in new[] { FdtdAxis.X, FdtdAxis.Y, FdtdAxis.Z })
            {
                var lines = grid.Axis(axis).Lines;
                double shift = Coord(scene.Origin, axis);
                for (int i = batch.FirstIndex; i < batch.FirstIndex + batch.IndexCount; i += 3)
                {
                    var p0 = P(scene, scene.Indices[i]); var p1 = P(scene, scene.Indices[i + 1]); var p2 = P(scene, scene.Indices[i + 2]);
                    double d0 = Snap(lines, C(p0, axis) + shift, tol), d1 = Snap(lines, C(p1, axis) + shift, tol),
                           d2 = Snap(lines, C(p2, axis) + shift, tol);
                    double lo = Math.Min(d0, Math.Min(d1, d2)), hi = Math.Max(d0, Math.Max(d1, d2));
                    if (hi <= lo) continue;                                  // a face lying in a grid plane
                    for (int k = LowerBound(lines, lo); k < lines.Count && lines[k] <= hi; k++)
                    {
                        double g = lines[k];
                        Vector3 h0 = default, h1 = default;
                        int h = 0;
                        if (d0 == g && d1 == g) { h0 = p0; h1 = p1; h = 2; }      // an edge ON the line
                        else if (d1 == g && d2 == g) { h0 = p1; h1 = p2; h = 2; }
                        else if (d0 == g && d2 == g) { h0 = p0; h1 = p2; h = 2; }
                        else { Edge(p0, d0, p1, d1); Edge(p1, d1, p2, d2); Edge(p0, d0, p2, d2); }
                        if (h == 2 && h0 != h1)
                        {
                            outv.Add(new Scene3DVertex(h0.X, h0.Y, h0.Z, 0, metalInk));
                            outv.Add(new Scene3DVertex(h1.X, h1.Y, h1.Z, 0, metalInk));
                        }

                        void Edge(Vector3 pa, double da, Vector3 pb, double db)
                        {
                            if (h == 2 || (da < g) == (db < g) || da == db) return;
                            var q = pa + (pb - pa) * (float)((g - da) / (db - da));
                            if (h++ == 0) h0 = q; else h1 = q;
                        }
                    }
                }
            }
        }

        // ── the smallest cell on each axis ───────────────────────────────────────────────────
        var labels = new List<FdtdCellLabel>();
        var mid = (scene.ContentMin + scene.ContentMax) * 0.5f;
        foreach (FdtdAxis axis in new[] { FdtdAxis.X, FdtdAxis.Y, FdtdAxis.Z })
        {
            var g = grid.Axis(axis);
            if (g.Lines.Count < 2) continue;
            string name = FdtdGrid.AxisName(axis);
            string by = g.SmallestCellFeatures.Count > 0 ? ", set by " + g.SmallestCellFeatures[0].Describe(axis) : "";
            string text = $"smallest Δ{name} {FdtdGrid.FormatLength(g.SmallestCellM)} at {name} = {FdtdGrid.FormatLength(g.SmallestCellAtM)}{by}";
            var at = mid;
            float local = (float)(g.SmallestCellAtM - Coord(scene.Origin, axis));
            at = axis switch { FdtdAxis.X => at with { X = local }, FdtdAxis.Y => at with { Y = local }, _ => at with { Z = local } };
            labels.Add(new FdtdCellLabel(axis, g.SmallestCellM, text, at));
        }
        return new FdtdGridDrawing([.. outv], labels);

        void Segment(FdtdAxis fixedAxis, double fixedAt, FdtdAxis lineAxis, double lineAt, FdtdAxis runAxis, double from, double to, uint rgba)
        {
            var a0 = new double[3]; var a1 = new double[3];
            a0[(int)fixedAxis] = a1[(int)fixedAxis] = fixedAt;
            a0[(int)lineAxis] = a1[(int)lineAxis] = lineAt;
            a0[(int)runAxis] = from; a1[(int)runAxis] = to;
            var q0 = scene.ToLocal(a0[0], a0[1], a0[2]); var q1 = scene.ToLocal(a1[0], a1[1], a1[2]);
            outv.Add(new Scene3DVertex(q0.X, q0.Y, q0.Z, 0, rgba));
            outv.Add(new Scene3DVertex(q1.X, q1.Y, q1.Z, 0, rgba));
        }
    }

    /// <summary>How many float ulps of the scene's extent a vertex may sit off a grid line and still be
    /// taken as on it.</summary>
    public const double SnapUlps = 4;

    private static double Snap(IReadOnlyList<double> lines, double d, double tol)
    {
        int k = LowerBound(lines, d);
        if (k < lines.Count && lines[k] - d <= tol) return lines[k];
        if (k > 0 && d - lines[k - 1] <= tol) return lines[k - 1];
        return d;
    }

    /// <summary>The smallest spacing between consecutive lines — what the label states.</summary>
    public static double MinSpacing(IReadOnlyList<double> lines)
    {
        double m = double.PositiveInfinity;
        for (int i = 1; i < lines.Count; i++) m = Math.Min(m, lines[i] - lines[i - 1]);
        return m;
    }

    private static (FdtdAxis, FdtdAxis) Others(FdtdAxis a) => a switch
    {
        FdtdAxis.X => (FdtdAxis.Y, FdtdAxis.Z), FdtdAxis.Y => (FdtdAxis.X, FdtdAxis.Z), _ => (FdtdAxis.X, FdtdAxis.Y),
    };

    private static double Coord((double X, double Y, double Z) o, FdtdAxis a) => a switch { FdtdAxis.X => o.X, FdtdAxis.Y => o.Y, _ => o.Z };
    private static double C(Vector3 p, FdtdAxis a) => a switch { FdtdAxis.X => p.X, FdtdAxis.Y => p.Y, _ => p.Z };
    private static Vector3 P(Scene3DModel s, uint i) => new(s.Vertices[i].X, s.Vertices[i].Y, s.Vertices[i].Z);

    private static int LowerBound(IReadOnlyList<double> a, double v)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    internal static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
