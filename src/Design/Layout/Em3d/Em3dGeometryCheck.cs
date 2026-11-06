// brief-dfr35-1 R-dfr35-1-7 — what is known to stop Gmsh, found in the outlines a run will mesh, before Gmsh runs.
//
// WHY. "Overlapping facets" has had two different causes on imported boards: a 0.1 µm jog at a board edge and a zero-width spike
// in a pour. Neither is visible to a dry run, and each cost a mesh run that names only surface tags. This looks at the outlines
// left AFTER Robust meshing, so what it reports is what Robust meshing could not take.
//
// WHAT STOPS GMSH, MEASURED. Two faces SHARING AN EDGE at under its 0.1° facet angle, and nothing else tried: 0.09° fails and 0.2°
// meshes; the same 0.05° wedge with a 1 nm gap, so that the faces share no edge, meshes. Parallel-sided slits and slivers from
// 0.002 µm to 0.2 µm wide mesh, as do parallel jogs of 0.1 µm and 0.4 µm against a board edge, so they are not reported.
//
// WHAT IT REPORTS.
//   - Reversal: an outline that turns back on itself, its legs under the facet angle and within the tolerance at the shorter
//     leg's end. The zero-width needle of a merged pour.
//   - Sliver: a wedge-shaped slit or sliver, the same corner opening wider than the tolerance; or two stretches of one object's
//     outline lying exactly on each other, away from any corner, a zero-width cut such as a keyhole.
//   - NearFaces: stretches of two outlines that start from one point, share a height, and part at under the facet angle.
//   - PointTouch: a corner of one object's outline lying on another stretch of that outline with nothing beside it, such as a hole
//     tangent to its pour's edge. OpenCASCADE does not split a face's outer wire against its own holes, so the 1D mesh crosses there.
//   - ShortEdge: an edge far below the element asked for where it meets another object. No board tried with one stopped Gmsh, so
//     this is a note and not a warning: it costs elements (Em3dSizeEstimate counts them), and it is where a flat element has come from.
//
// WHAT IT COSTS. Each edge goes in a uniform grid and is compared with the edges sharing a cell, so a board's cost goes with its
// edge count; nothing here allocates per pair unless the pair already touches. Measured with a scratch harness on
// a 3,211-shape board: see src/Design/RESOLVED.md.
//
// A WARNING, NEVER A REFUSAL: a mesh may still succeed, and a mesh failing is what Gmsh is there to say.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>Which of the check's classes a finding is.</summary>
public enum Em3dGeometryKind { Reversal, Sliver, NearFaces, PointTouch, ShortEdge }

/// <summary>One place: the object and layer it is on, the other object where there is one, in layout micrometres.</summary>
public sealed record Em3dGeometryFinding(Em3dGeometryKind Kind, string Object, string Layer, string? Other, double XUm, double YUm, string Sentence);

public static class Em3dGeometryCheck
{
    /// <summary>What separates a needle from a wedge, metres, and how near an edge counts as meeting another object: Robust meshing's cap.</summary>
    public const double ToleranceM = Em3dOutlineCleanup.MaxToleranceM;

    /// <summary>Two points this close, metres, are the same point: half the layout's smallest step. A gap of 1 nm is two faces, and meshes.</summary>
    private const double SameM = Em3dOutlineCleanup.SameM;

    /// <summary>Gmsh's own default for two facets lying on each other (<c>Mesh.AngleToleranceFacetOverlap</c>), degrees.</summary>
    public const double FacetAngleDegrees = 0.1;

    /// <summary>An edge this many times below the local element is "far below" it.</summary>
    public const double ShortEdgeRatio = 50;

    /// <summary>Findings said in full; the rest are counted (see <see cref="Sentences"/>).</summary>
    public const int MaxSaid = 12;

    private readonly record struct Edge(int Ring, int Index, double Ax, double Ay, double Bx, double By)
    {
        public double Length => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (By - Ay) * (By - Ay));
    }

    private sealed record Ring(string Object, double Z0, double Z1, bool Flat, int Owner, IReadOnlyList<Point2> Points);

    /// <summary>The problem's findings, in the order of the outlines. <paramref name="localEdgeM"/> is the smallest element the
    /// script asks for on metal; zero or less leaves out the short-edge class.</summary>
    public static IReadOnlyList<Em3dGeometryFinding> Check(Em3dProblem problem, double localEdgeM, double toleranceM = ToleranceM)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var rings = new List<Ring>();
        int owner = 0;
        foreach (var s in problem.Solids)
        {
            switch (s.Primitive)
            {
                case Em3dExtrudedPolygon e:
                    rings.Add(new(s.Name, e.ZBottom, e.ZTop, false, owner, e.Outline));
                    foreach (var h in e.Holes) rings.Add(new(s.Name, e.ZBottom, e.ZTop, false, owner, h));
                    break;
                case Em3dBox b:
                    rings.Add(new(s.Name, b.Min.Z, b.Max.Z, false, owner,
                                  [new Point2(b.Min.X, b.Min.Y), new Point2(b.Max.X, b.Min.Y), new Point2(b.Max.X, b.Max.Y), new Point2(b.Min.X, b.Max.Y)]));
                    break;
            }
            owner++;
        }
        foreach (var sh in problem.Sheets)
        {
            if (sh.Frame is not null) { owner++; continue; }    // in a plane of its own, so its (x, y) are not the layout's
            rings.Add(new(sh.Name, sh.Z, sh.Z, true, owner, sh.Outline));
            foreach (var h in sh.Holes) rings.Add(new(sh.Name, sh.Z, sh.Z, true, owner, h));
            owner++;
        }

        double sinFacet = Math.Sin(FacetAngleDegrees * Math.PI / 180);
        var found = new List<Em3dGeometryFinding>();
        var said = new HashSet<(Em3dGeometryKind, string, long, long)>();
        void Add(Em3dGeometryKind kind, Ring on, string? other, double x, double y, string what)
        {
            // One place, however many edges of it are involved.
            if (!said.Add((kind, on.Object, (long)Math.Round(x * 1e5), (long)Math.Round(y * 1e5)))) return;
            string layer = LayerOf(on.Object);
            found.Add(new(kind, on.Object, layer, other, x * 1e6, y * 1e6,
                          $"'{on.Object}' on '{layer}': {what} at {At(x, y)}."));
        }

        // ── one ring at a time: where it turns straight back ─────────────────────────────────
        var edges = new List<Edge>();
        for (int r = 0; r < rings.Count; r++)
        {
            var p = rings[r].Points;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                var (a, b, c) = (p[(i + n - 1) % n], p[i], p[(i + 1) % n]);
                double ux = b.X - a.X, uy = b.Y - a.Y, vx = c.X - b.X, vy = c.Y - b.Y;
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu > SameM && lv > SameM && ux * vx + uy * vy < 0 && Math.Abs(ux * vy - uy * vx) / (lu * lv) < sinFacet)
                {
                    double shorter = Math.Min(lu, lv), mouth = Math.Abs(ux * vy - uy * vx) / Math.Max(lu, lv);
                    if (mouth < toleranceM)
                        Add(Em3dGeometryKind.Reversal, rings[r], null, b.X, b.Y, $"its outline turns back on itself, a needle {Length(shorter)} long,");
                    else
                        Add(Em3dGeometryKind.Sliver, rings[r], null, b.X, b.Y,
                            $"a wedge-shaped slit or sliver {Length(shorter)} long comes to a point under {Degrees(FacetAngleDegrees)},");
                }
                if (lv > SameM) edges.Add(new(r, i, b.X, b.Y, c.X, c.Y));
            }
        }
        if (edges.Count == 0) return found;

        // ── every edge in a grid ─────────────────────────────────────────────────────────────
        double x0 = edges.Min(e => Math.Min(e.Ax, e.Bx)), y0 = edges.Min(e => Math.Min(e.Ay, e.By));
        double span = Math.Max(edges.Max(e => Math.Max(e.Ax, e.Bx)) - x0, edges.Max(e => Math.Max(e.Ay, e.By)) - y0);
        double cell = Math.Max(span / Math.Clamp(Math.Sqrt(edges.Count) * 2, 16, 2048), 4 * toleranceM);
        var cells = new Dictionary<(long, long), List<int>>();
        (long, long) Cell(double x, double y) => ((long)Math.Floor((x - x0) / cell), (long)Math.Floor((y - y0) / cell));
        for (int k = 0; k < edges.Count; k++)
        {
            // In pieces no longer than half a cell, so a long diagonal fills the cells along it and not its whole box.
            var e = edges[k];
            int pieces = (int)Math.Ceiling(e.Length / (cell / 2)) + 1;
            for (int m = 0; m < pieces; m++)
            {
                double t0 = (double)m / pieces, t1 = (double)(m + 1) / pieces;
                double xa = e.Ax + t0 * (e.Bx - e.Ax), ya = e.Ay + t0 * (e.By - e.Ay), xb = e.Ax + t1 * (e.Bx - e.Ax), yb = e.Ay + t1 * (e.By - e.Ay);
                var (i0, j0) = Cell(Math.Min(xa, xb) - toleranceM, Math.Min(ya, yb) - toleranceM);
                var (i1, j1) = Cell(Math.Max(xa, xb) + toleranceM, Math.Max(ya, yb) + toleranceM);
                for (long i = i0; i <= i1; i++)
                    for (long j = j0; j <= j1; j++)
                    {
                        if (!cells.TryGetValue((i, j), out var list)) cells[(i, j)] = list = [];
                        if (list.Count == 0 || list[^1] != k) list.Add(k);
                    }
            }
        }

        bool ShareHeight(Ring a, Ring b)
        {
            double overlap = Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0);
            return a.Flat || b.Flat ? overlap >= -SameM : overlap > SameM;
        }

        // Corner j of edge e's ring on edge other, with nothing beside it.
        void Touch(Edge e, Edge other, int j)
        {
            var p = rings[e.Ring].Points;
            int n = p.Count;
            var (a, b, c) = (p[(j + n - 1) % n], p[j % n], p[(j + 1) % n]);
            if (Em3dOutlineCleanup.TouchesAtPoint((a.X, a.Y), (b.X, b.Y), (c.X, c.Y), (other.Ax, other.Ay), (other.Bx, other.By)))
                Add(Em3dGeometryKind.PointTouch, rings[e.Ring], null, b.X, b.Y, "its outline touches another stretch of itself at one point,");
        }

        // ── pairs sharing a cell ─────────────────────────────────────────────────────────────
        var seen = new HashSet<(int, int)>();
        foreach (var list in cells.Values)
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    int ka = Math.Min(list[a], list[b]), kb = Math.Max(list[a], list[b]);
                    var (ea, eb) = (edges[ka], edges[kb]);
                    if (Math.Max(ea.Ax, ea.Bx) + toleranceM < Math.Min(eb.Ax, eb.Bx) || Math.Max(eb.Ax, eb.Bx) + toleranceM < Math.Min(ea.Ax, ea.Bx) ||
                        Math.Max(ea.Ay, ea.By) + toleranceM < Math.Min(eb.Ay, eb.By) || Math.Max(eb.Ay, eb.By) + toleranceM < Math.Min(ea.Ay, ea.By))
                        continue;
                    var (ra, rb) = (rings[ea.Ring], rings[eb.Ring]);
                    bool same = ra.Owner == rb.Owner;
                    if (!same && !ShareHeight(ra, rb)) continue;
                    if (ea.Ring == eb.Ring && Adjacent(ea.Index, eb.Index, ra.Points.Count)) continue;
                    if (same)
                    {
                        Touch(ea, eb, ea.Index);
                        Touch(ea, eb, ea.Index + 1);
                        Touch(eb, ea, eb.Index);
                        Touch(eb, ea, eb.Index + 1);
                    }
                    if (Near(ea, eb) is not { } near || near.Sine > sinFacet || near.Smallest > SameM || !seen.Add((ka, kb))) continue;
                    double mx = (near.Xa + near.Xb) / 2, my = (near.Ya + near.Yb) / 2;
                    if (near.Largest <= SameM)
                    {
                        // Exactly on each other: the fragment merges two objects' faces, and within one object it is a cut of no width.
                        if (same && near.Overlap > 2 * toleranceM)
                            Add(Em3dGeometryKind.Sliver, ra, null, mx, my, $"its outline lies on itself for {Length(near.Overlap)}, a cut of no width,");
                    }
                    else if (near.Overlap > 2 * toleranceM)
                        Add(Em3dGeometryKind.NearFaces, ra, same ? null : rb.Object, mx, my,
                            $"its side and {(same ? "another of its own" : $"'{rb.Object}''s")} start from one point and part at {Degrees(Math.Asin(near.Sine) * 180 / Math.PI)}, {Length(near.Largest)} apart over {Length(near.Overlap)},");
                }

        // ── a short edge where it meets another object ───────────────────────────────────────
        if (localEdgeM > 0)
        {
            double shortM = localEdgeM / ShortEdgeRatio;
            for (int k = 0; k < edges.Count; k++)
            {
                var e = edges[k];
                if (e.Length >= shortM) continue;
                var ring = rings[e.Ring];
                if (!cells.TryGetValue(Cell(e.Ax, e.Ay), out var list)) continue;
                foreach (int o in list)
                {
                    var other = edges[o];
                    var oring = rings[other.Ring];
                    if (oring.Owner == ring.Owner || !ShareHeight(ring, oring)) continue;
                    if (Distance(e.Ax, e.Ay, other) > toleranceM && Distance(e.Bx, e.By, other) > toleranceM) continue;
                    Add(Em3dGeometryKind.ShortEdge, ring, oring.Object, (e.Ax + e.Bx) / 2, (e.Ay + e.By) / 2,
                        $"an edge {Length(e.Length)} long meets '{oring.Object}', against an element of {Length(localEdgeM)} there,");
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>The check at the element a run of <paramref name="setup"/> asks for on metal.</summary>
    public static IReadOnlyList<Em3dGeometryFinding> ForRun(Em3dProblem problem, EmSetup setup)
    {
        double edge = setup.IsThermal ? 0 : CircuitRF.Design.Em3d.GmshGeoWriter.SmallestRequestedSizeM(problem, PalaceSettings.Resolve(setup.Palace));
        return Check(problem, double.IsFinite(edge) ? edge : 0);
    }

    /// <summary>The findings that can stop the mesher, as sentences for a run's warnings: a fixed number in full, the rest counted.</summary>
    public static IReadOnlyList<string> Sentences(IReadOnlyList<Em3dGeometryFinding> findings)
        => Said("Geometry that can stop the mesher", [.. findings.Where(f => f.Kind != Em3dGeometryKind.ShortEdge)]);

    /// <summary>The short edges, as sentences for a run's notes: no board tried with one stopped Gmsh.</summary>
    public static IReadOnlyList<string> NoteSentences(IReadOnlyList<Em3dGeometryFinding> findings)
        => Said("An edge far below the element, which adds elements there", [.. findings.Where(f => f.Kind == Em3dGeometryKind.ShortEdge)]);

    private static IReadOnlyList<string> Said(string head, IReadOnlyList<Em3dGeometryFinding> findings)
    {
        if (findings.Count == 0) return [];
        var lines = findings.Take(MaxSaid).Select(f => $"{head}: {f.Sentence}").ToList();
        if (findings.Count > MaxSaid) lines.Add($"{head}: {findings.Count - MaxSaid} more place(s) like these.");
        return lines;
    }

    /// <summary>The layer in a solid's name, which the generator writes as its layer first ("Top Copper/GND/5").</summary>
    private static string LayerOf(string name) => name.IndexOf('/') is > 0 and var i ? name[..i] : name;

    private static bool Adjacent(int i, int j, int n) => Math.Abs(i - j) == 1 || Math.Abs(i - j) == n - 1;

    private readonly record struct Nearness(double Sine, double Overlap, double Smallest, double Largest, double Xa, double Ya, double Xb, double Yb);

    /// <summary>How two edges lie against each other over the stretch where they overlap, measured along the longer; null when
    /// they do not overlap or are not close to parallel.</summary>
    private static Nearness? Near(Edge p, Edge q)
    {
        if (q.Length > p.Length) (p, q) = (q, p);
        double lp = p.Length, ux = (p.Bx - p.Ax) / lp, uy = (p.By - p.Ay) / lp;
        double lq = q.Length, vx = (q.Bx - q.Ax) / lq, vy = (q.By - q.Ay) / lq;
        double sine = Math.Abs(ux * vy - uy * vx);
        // Wider than the facet angle, which the caller applies; this only keeps the arithmetic off crossing edges.
        if (sine > 0.01) return null;
        double ta = (q.Ax - p.Ax) * ux + (q.Ay - p.Ay) * uy, tb = (q.Bx - p.Ax) * ux + (q.By - p.Ay) * uy;
        double lo = Math.Max(0, Math.Min(ta, tb)), hi = Math.Min(lp, Math.Max(ta, tb));
        if (hi - lo <= 0 || Math.Abs(tb - ta) <= 0) return null;
        double At(double t)
        {
            double s = (t - ta) / (tb - ta);
            double x = q.Ax + s * (q.Bx - q.Ax), y = q.Ay + s * (q.By - q.Ay);
            return Math.Abs((x - p.Ax) * uy - (y - p.Ay) * ux);
        }
        double d0 = At(lo), d1 = At(hi);
        return new(sine, hi - lo, Math.Min(d0, d1), Math.Max(d0, d1),
                   p.Ax + lo * ux, p.Ay + lo * uy, p.Ax + hi * ux, p.Ay + hi * uy);
    }

    private static double Distance(double x, double y, Edge e)
    {
        double ux = e.Bx - e.Ax, uy = e.By - e.Ay, l2 = ux * ux + uy * uy;
        double t = l2 > 0 ? Math.Clamp(((x - e.Ax) * ux + (y - e.Ay) * uy) / l2, 0, 1) : 0;
        double dx = x - (e.Ax + t * ux), dy = y - (e.Ay + t * uy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string Degrees(double d) => d.ToString("0.####", CultureInfo.InvariantCulture) + "°";

    private static string At(double x, double y)
        => string.Create(CultureInfo.InvariantCulture, $"({x * 1e6:#,0.###}, {y * 1e6:#,0.###}) µm");

    private static string Length(double m) => string.Create(CultureInfo.InvariantCulture, $"{m * 1e6:0.###} µm");
}
