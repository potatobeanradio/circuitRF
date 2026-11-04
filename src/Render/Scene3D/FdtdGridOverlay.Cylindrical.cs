// brief-em3d-120 R-em3d120-5 — a CYLINDRICAL FDTD grid, drawn as the Cartesian one is (FdtdGridOverlay.cs): on the clip plane
// and on conductor surfaces, never through the volume. Its grid surfaces are the ρ cylinders, the α half-planes (spokes from
// the axis) and the planes normal to the axis, so:
//   * a clip plane NORMAL to the axis shows each ρ line as a circle (through the α nodes, so its chords are the grid's own) and
//     each α line as a spoke from ρ_min out to the grid's edge;
//   * a clip plane ALONG the axis shows each ρ circle where it cuts the plane, as lines along the axis, and each axial line
//     across the plane's chord of the cylinder;
//   * a conductor's surface shows where each of the three families crosses its triangles.

using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

public static partial class FdtdGridOverlay
{
    /// <summary>Segments per full circle at least, when a ρ circle is drawn through fewer α lines than this.</summary>
    private const int MinCircleSegments = 96;

    private static FdtdGridDrawing BuildCylindrical(FdtdGridResult grid, FdtdCylinder cyl, Scene3DModel scene, in ClipPlane3D clip,
                                                    bool dark, CancellationToken ct)
    {
        uint planeInk = dark ? Scene3DVertex.Pack(170, 175, 185, 255) : Scene3DVertex.Pack(110, 115, 125, 255);
        uint metalInk = dark ? Scene3DVertex.Pack(255, 120, 60, 255) : Scene3DVertex.Pack(215, 60, 20, 255);
        var outv = new List<Scene3DVertex>();
        var rho = grid.X.Lines;
        var alpha = grid.Y.Lines;
        var z = grid.Z.Lines;
        int a = cyl.A, u = cyl.U, v = cyl.V;
        double ou = Get(cyl.Origin, u), ov = Get(cyl.Origin, v);
        double rOuter = rho[^1];

        // A world point from (ρ, α, axial).
        double[] World(double r, double t, double along)
        {
            var w = new double[3];
            w[u] = ou + r * Math.Cos(t);
            w[v] = ov + r * Math.Sin(t);
            w[a] = along;
            return w;
        }
        void Line(double[] p0, double[] p1, uint ink)
        {
            var q0 = scene.ToLocal(p0[0], p0[1], p0[2]); var q1 = scene.ToLocal(p1[0], p1[1], p1[2]);
            outv.Add(new Scene3DVertex(q0.X, q0.Y, q0.Z, 0, ink));
            outv.Add(new Scene3DVertex(q1.X, q1.Y, q1.Z, 0, ink));
        }

        // ── on the clip plane ────────────────────────────────────────────────────────────────
        if (clip.Enabled && clip.IsAxis)
        {
            int n = clip.Axis switch { ClipAxis3D.X => 0, ClipAxis3D.Y => 1, _ => 2 };
            double at = clip.Offset + (n == 0 ? scene.Origin.X : n == 1 ? scene.Origin.Y : scene.Origin.Z);
            if (n == a)
            {
                int steps = Math.Max(1, (int)Math.Ceiling(MinCircleSegments / (double)(alpha.Count - 1)));
                foreach (double r in rho)
                    for (int k = 0; k + 1 < alpha.Count; k++)
                        for (int s = 0; s < steps; s++)
                        {
                            double t0 = alpha[k] + (alpha[k + 1] - alpha[k]) * s / steps, t1 = alpha[k] + (alpha[k + 1] - alpha[k]) * (s + 1) / steps;
                            Line(World(r, t0, at), World(r, t1, at), planeInk);
                        }
                for (int k = 0; k + 1 < alpha.Count; k++) Line(World(rho[0], alpha[k], at), World(rOuter, alpha[k], at), planeInk);
            }
            else
            {
                // The plane w[n] = at, along the axis: its distance from the axis and the direction across it within the plane.
                int across = n == u ? v : u;
                double d = at - (n == u ? ou : ov), oAcross = across == u ? ou : ov;
                if (Math.Abs(d) < rOuter)
                {
                    foreach (double r in rho)
                    {
                        if (r <= Math.Abs(d)) continue;
                        double h = Math.Sqrt(r * r - d * d);
                        foreach (double sgn in new[] { -1.0, 1.0 })
                        {
                            var p0 = new double[3]; var p1 = new double[3];
                            p0[n] = p1[n] = at; p0[across] = p1[across] = oAcross + sgn * h;
                            p0[a] = z[0]; p1[a] = z[^1];
                            Line(p0, p1, planeInk);
                        }
                    }
                    double chord = Math.Sqrt(rOuter * rOuter - d * d);
                    foreach (double zz in z)
                    {
                        var p0 = new double[3]; var p1 = new double[3];
                        p0[n] = p1[n] = at; p0[a] = p1[a] = zz;
                        p0[across] = oAcross - chord; p1[across] = oAcross + chord;
                        Line(p0, p1, planeInk);
                    }
                }
            }
        }

        // ── on conductor surfaces: contours of the axial coordinate, of ρ, and of each α half-plane ─────────────
        var origin = scene.Origin;
        foreach (var batch in scene.Batches)
        {
            ct.ThrowIfCancellationRequested();
            var o = scene.Objects[batch.ObjectId - 1];
            if (o.Kind is not (Scene3DKind.Conductor or Scene3DKind.Via or Scene3DKind.Wire or Scene3DKind.Sheet)) continue;
            for (int i = batch.FirstIndex; i < batch.FirstIndex + batch.IndexCount; i += 3)
            {
                var p0 = P(scene, scene.Indices[i]); var p1 = P(scene, scene.Indices[i + 1]); var p2 = P(scene, scene.Indices[i + 2]);
                double[] W(Vector3 p) => [p.X + origin.X, p.Y + origin.Y, p.Z + origin.Z];
                double[] w0 = W(p0), w1 = W(p1), w2 = W(p2);
                Contour(p0, p1, p2, w0[a], w1[a], w2[a], z, null);
                double R(double[] w) => Math.Sqrt((w[u] - ou) * (w[u] - ou) + (w[v] - ov) * (w[v] - ov));
                Contour(p0, p1, p2, R(w0), R(w1), R(w2), rho, null);
                for (int k = 0; k + 1 < alpha.Count; k++)
                {
                    double c = Math.Cos(alpha[k]), s = Math.Sin(alpha[k]);
                    double F(double[] w) => -s * (w[u] - ou) + c * (w[v] - ov);
                    double Ahead(Vector3 q) { var w = W(q); return c * (w[u] - ou) + s * (w[v] - ov); }
                    Contour(p0, p1, p2, F(w0), F(w1), F(w2), [0.0], Ahead);
                }
            }
        }

        void Contour(Vector3 p0, Vector3 p1, Vector3 p2, double d0, double d1, double d2, IReadOnlyList<double> levels, Func<Vector3, double>? keep)
        {
            double lo = Math.Min(d0, Math.Min(d1, d2)), hi = Math.Max(d0, Math.Max(d1, d2));
            if (hi <= lo) return;
            for (int k = LowerBound(levels, lo); k < levels.Count && levels[k] <= hi; k++)
            {
                double g = levels[k];
                Vector3 h0 = default, h1 = default;
                int h = 0;
                Edge(p0, d0, p1, d1); Edge(p1, d1, p2, d2); Edge(p0, d0, p2, d2);
                if (h == 2 && h0 != h1 && (keep is null || keep((h0 + h1) * 0.5f) > 0))
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

        // ── the wave ports' feed planes, as on a Cartesian grid ──────────────────────────────
        var feedLabels = new List<FdtdFeedLabel>();
        uint sourceInk = dark ? Scene3DVertex.Pack(225, 120, 255, 255) : Scene3DVertex.Pack(150, 40, 190, 255);
        uint referenceInk = dark ? Scene3DVertex.Pack(110, 230, 120, 255) : Scene3DVertex.Pack(20, 140, 40, 255);
        string an = FdtdGrid.AxisName(cyl.Axis);
        foreach (var feed in grid.WavePorts.Feeds)
        {
            string names = string.Join(", ", feed.Ports.Select(n => $"'{n}'"));
            double rMax = feed.Terminals.Max(t => t.OuterRadiusM);
            foreach (var (at, ink, what) in new[] { (feed.SourceAtM, sourceInk, "openEMS source"), (feed.ReferenceAtM, referenceInk, "reference plane") })
            {
                for (int k = 0; k < MinCircleSegments; k++)
                    Line(World(rMax, 2 * Math.PI * k / MinCircleSegments, at), World(rMax, 2 * Math.PI * (k + 1) / MinCircleSegments, at), ink);
                var c = World(rMax, 0, at);
                feedLabels.Add(new FdtdFeedLabel($"{what} of {names} at {an} = {FdtdGrid.FormatLength(at)}", scene.ToLocal(c[0], c[1], c[2])));
            }
        }

        // ── the smallest cell on each of ρ, α (as an arc) and the axis ───────────────────────
        var labels = new List<FdtdCellLabel>();
        var mid = (scene.ContentMin + scene.ContentMax) * 0.5f;
        var axialAt = mid;
        float local = (float)(grid.Z.SmallestCellAtM - (a == 0 ? scene.Origin.X : a == 1 ? scene.Origin.Y : scene.Origin.Z));
        axialAt = a switch { 0 => axialAt with { X = local }, 1 => axialAt with { Y = local }, _ => axialAt with { Z = local } };
        var rhoAt = World(grid.X.SmallestCellAtM, 0, (z[0] + z[^1]) / 2);
        var arcAt = World(cyl.SmallestArcAtM, 0, (z[0] + z[^1]) / 2);
        labels.Add(new FdtdCellLabel(FdtdAxis.Rho, grid.X.SmallestCellM,
                                     $"smallest Δρ {FdtdGrid.FormatLength(grid.X.SmallestCellM)} at ρ = {FdtdGrid.FormatLength(grid.X.SmallestCellAtM)}",
                                     scene.ToLocal(rhoAt[0], rhoAt[1], rhoAt[2])));
        labels.Add(new FdtdCellLabel(FdtdAxis.Alpha, cyl.SmallestArcM,
                                     $"smallest arc ρ·Δα {FdtdGrid.FormatLength(cyl.SmallestArcM)} at ρ = {FdtdGrid.FormatLength(cyl.SmallestArcAtM)}, " +
                                     $"{cyl.AzimuthCells} α cells",
                                     scene.ToLocal(arcAt[0], arcAt[1], arcAt[2])));
        labels.Add(new FdtdCellLabel(cyl.Axis, grid.Z.SmallestCellM,
                                     $"smallest Δ{an} {FdtdGrid.FormatLength(grid.Z.SmallestCellM)} at {an} = {FdtdGrid.FormatLength(grid.Z.SmallestCellAtM)}",
                                     axialAt));
        return new FdtdGridDrawing([.. outv], labels) { FeedLabels = feedLabels };
    }

    private static double Get(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };
}
