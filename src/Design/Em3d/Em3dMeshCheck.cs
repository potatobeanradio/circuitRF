// brief-emb-2 R-emb2-4 — the mesh Gmsh made, checked before Palace reads it.
//
// WHY. On an imported two-layer board a through-hole pad meshed into 167 zero-volume tetrahedra, and Palace stopped on
// "Chebyshev/Jacobi smoother … requires a strictly positive diagonal": no cause, no place. A lumped port sheet that was not
// embedded conformally left surface triangles that are no face of any tetrahedron, and MFEM aborted in STable3D. Both are
// visible in the mesh itself, in one pass, so the run refuses with a sentence per place instead of handing the solver a mesh
// it cannot use.
//
// WHAT IS COUNTED. A tetrahedron whose corners span (almost) no volume — 6V under ZeroVolumeRelative of its longest edge
// cubed — or whose volume is negative (Gmsh orients every tetrahedron positively, checked on its own output); and, for a
// second-order mesh, one whose Jacobian is not positive at a corner or at its centroid (a curved element folded over).
// A surface triangle that is no face of any tetrahedron is an orphan. Each bad element is named by the surfaces its corners
// lie on (a conductor's, a sheet's, a port's), which is the object it bounds, else by its own volume's group; bad elements
// close together are said once, with where they are.
//
// The physical tag of every element IS its group's Palace attribute (GmshGeoWriter writes one physical group per
// Em3dGroup, numbered by attribute), so the groups handed in name everything; the entity table is not needed here.

using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>What the post-mesh check found: the counts, and one sentence per place (empty when the mesh is usable).</summary>
public sealed record Em3dMeshCheckResult(int Tetrahedra, int ZeroVolume, int Inverted, int OrphanTriangles, IReadOnlyList<string> Sentences)
{
    public bool Ok => Sentences.Count == 0;

    /// <summary>The refusal a run stops on, or null when the mesh is usable.</summary>
    public string? Refusal => Ok ? null : "The mesh has elements the solver cannot use. " + string.Join(" ", Sentences);
}

public static class Em3dMeshCheck
{
    /// <summary>The diagnostic source a run's refusal is forwarded under.</summary>
    public const string Source = "palace-mesh-check";

    /// <summary>A tetrahedron is zero-volume when 6V is under this fraction of its longest edge cubed (a regular one is 0.71).</summary>
    public const double ZeroVolumeRelative = 1e-8;

    /// <summary>What an element-level sentence advises.</summary>
    public const string ElementRemedy = "set the via entry's Fill to Solid, enable robust meshing, or leave the part out of the model.";

    /// <summary>What an orphan-triangle sentence advises.</summary>
    public const string OrphanRemedy =
        "enable robust meshing, or move it so that it lies on the metal's face or clear of it.";

    /// <summary>Bad elements clustered pairwise at most; past this the rest are counted, not placed.</summary>
    private const int MaxPlaced = 4000;

    /// <summary>
    /// The mesh at <paramref name="mshPath"/> (MSH 2.2, as Palace reads it) against the <paramref name="groups"/> its script
    /// made. Null when there is no such file or it cannot be read: the check then says nothing and the solver reads it.
    /// </summary>
    /// <param name="lengthUnitM">The mesh's length unit, metres (the Palace script's is a micrometre).</param>
    public static Em3dMeshCheckResult? Check(string mshPath, IReadOnlyList<Em3dGroup> groups, double lengthUnitM = GmshGeoWriter.LengthUnitM)
    {
        if (!File.Exists(mshPath)) return null;
        MshMesh mesh;
        try { mesh = MshReader.Read(mshPath); }
        catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
        return Check(mesh, groups, lengthUnitM);
    }

    /// <summary>The check on a mesh already read.</summary>
    public static Em3dMeshCheckResult Check(MshMesh mesh, IReadOnlyList<Em3dGroup> groups, double lengthUnitM = GmshGeoWriter.LengthUnitM)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var byAttr = new Dictionary<int, Em3dGroup>();
        foreach (var g in groups) byAttr.TryAdd(g.Attribute, g);
        string Name(int tag) => byAttr.TryGetValue(tag, out var g) ? g.Name : mesh.GroupName(tag);
        bool Bounds(int tag) => byAttr.TryGetValue(tag, out var g)
            ? g.Kind is Em3dGroupKind.Conductor or Em3dGroupKind.Sheet or Em3dGroupKind.Port or Em3dGroupKind.FaceBoundary
            : true;
        double mm = lengthUnitM * 1e3;
        var x = mesh.Nodes;
        var sentences = new List<string>();

        // ── elements ──────────────────────────────────────────────────────────────────────────
        var bad = new List<(int Tet, bool Inverted, double Cx, double Cy, double Size)>();
        int zero = 0, inverted = 0;
        bool high = mesh.Order == 2 && mesh.TetsHigh.Length == 10 * mesh.TetCount;
        Span<double> p = stackalloc double[30];
        for (int t = 0; t < mesh.TetCount; t++)
        {
            int a = mesh.Tets[4 * t], b = mesh.Tets[4 * t + 1], c = mesh.Tets[4 * t + 2], d = mesh.Tets[4 * t + 3];
            double l2 = 0;
            int[] corner = [a, b, c, d];
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    l2 = Math.Max(l2, Sq(x[3 * corner[i]] - x[3 * corner[j]]) + Sq(x[3 * corner[i] + 1] - x[3 * corner[j] + 1]) +
                                      Sq(x[3 * corner[i] + 2] - x[3 * corner[j] + 2]));
            double l3 = l2 * Math.Sqrt(l2), floor = ZeroVolumeRelative * l3;
            double det = Det(x, a, b, c, d);
            bool isZero = l3 == 0 || Math.Abs(det) <= floor, isInverted = !isZero && det < 0;
            if (!isZero && !isInverted && high)
            {
                for (int k = 0; k < 10; k++)
                {
                    int n = mesh.TetsHigh[10 * t + k];
                    p[3 * k] = x[3 * n]; p[3 * k + 1] = x[3 * n + 1]; p[3 * k + 2] = x[3 * n + 2];
                }
                isInverted = !CurvedPositive(p, floor);
            }
            if (!isZero && !isInverted) continue;
            if (isZero) zero++; else inverted++;
            bad.Add((t, isInverted, (x[3 * a] + x[3 * b] + x[3 * c] + x[3 * d]) / 4, (x[3 * a + 1] + x[3 * b + 1] + x[3 * c + 1] + x[3 * d + 1]) / 4,
                     Math.Sqrt(l2)));
        }
        if (bad.Count > 0)
        {
            // The surfaces each bad element's corners lie on: the object it bounds.
            var corners = new Dictionary<int, SortedSet<int>>();
            foreach (var e in bad)
                for (int k = 0; k < 4; k++) corners.TryAdd(mesh.Tets[4 * e.Tet + k], []);
            for (int s = 0; s < mesh.TriangleCount; s++)
            {
                int tag = mesh.TrianglePhysical[s];
                if (!Bounds(tag)) continue;
                for (int k = 0; k < 3; k++)
                    if (corners.TryGetValue(mesh.Triangles[3 * s + k], out var on)) on.Add(tag);
            }
            var placed = bad.Take(MaxPlaced).ToList();
            var root = Enumerable.Range(0, placed.Count).ToArray();
            int Find(int i) { while (root[i] != i) i = root[i] = root[root[i]]; return i; }
            for (int i = 0; i < placed.Count; i++)
                for (int j = i + 1; j < placed.Count; j++)
                {
                    double reach = 2 * Math.Max(placed[i].Size, placed[j].Size);
                    if (Sq(placed[i].Cx - placed[j].Cx) + Sq(placed[i].Cy - placed[j].Cy) <= reach * reach) root[Find(i)] = Find(j);
                }
            foreach (var cluster in Enumerable.Range(0, placed.Count).GroupBy(Find).OrderBy(g => g.Min()))
            {
                var members = cluster.Select(i => placed[i]).ToList();
                var where = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var e in members)
                {
                    var surfaces = Enumerable.Range(0, 4).SelectMany(k => corners[mesh.Tets[4 * e.Tet + k]]).ToList();
                    if (surfaces.Count == 0) where.Add(Name(mesh.TetPhysical[e.Tet]));
                    else foreach (int s in surfaces) where.Add(Name(s));
                }
                int z = members.Count(e => !e.Inverted), v = members.Count - z;
                string what = z > 0 && v > 0 ? $"{z} zero-volume and {v} inverted elements"
                            : z > 0 ? (z == 1 ? "1 zero-volume element" : $"{z} zero-volume elements")
                                    : (v == 1 ? "1 inverted element" : $"{v} inverted elements");
                sentences.Add($"{what} {(members.Count == 1 ? "lies" : "lie")} in {List(where)} around " +
                              $"({Mm(members.Average(e => e.Cx) * mm)}, {Mm(members.Average(e => e.Cy) * mm)}) mm; {ElementRemedy}");
            }
            if (bad.Count > placed.Count)
                sentences.Add($"{bad.Count - placed.Count} more such elements lie elsewhere in the mesh.");
        }

        // ── surface triangles that are no element's face ─────────────────────────────────────
        var index = new Dictionary<(int, int, int), int>(mesh.TriangleCount);
        var next = new int[mesh.TriangleCount];
        for (int s = 0; s < mesh.TriangleCount; s++)
        {
            var key = Key(mesh.Triangles[3 * s], mesh.Triangles[3 * s + 1], mesh.Triangles[3 * s + 2]);
            next[s] = index.TryGetValue(key, out int head) ? head : -1;
            index[key] = s;
        }
        var faceOf = new bool[mesh.TriangleCount];
        for (int t = 0; t < mesh.TetCount && index.Count > 0; t++)
        {
            int a = mesh.Tets[4 * t], b = mesh.Tets[4 * t + 1], c = mesh.Tets[4 * t + 2], d = mesh.Tets[4 * t + 3];
            foreach (var key in new[] { Key(a, b, c), Key(a, b, d), Key(a, c, d), Key(b, c, d) })
                if (index.Remove(key, out int s))
                    for (; s >= 0; s = next[s]) faceOf[s] = true;
        }
        int orphans = 0;
        var orphanGroups = new SortedDictionary<int, (int Count, double X, double Y)>();
        for (int s = 0; s < mesh.TriangleCount; s++)
        {
            if (faceOf[s]) continue;
            orphans++;
            int tag = mesh.TrianglePhysical[s];
            double cx = 0, cy = 0;
            for (int k = 0; k < 3; k++) { cx += x[3 * mesh.Triangles[3 * s + k]] / 3; cy += x[3 * mesh.Triangles[3 * s + k] + 1] / 3; }
            var o = orphanGroups.GetValueOrDefault(tag);
            orphanGroups[tag] = (o.Count + 1, o.X + cx, o.Y + cy);
        }
        foreach (var (tag, o) in orphanGroups)
            sentences.Add($"{o.Count} surface triangle{(o.Count == 1 ? "" : "s")} of {Name(tag)} near ({Mm(o.X / o.Count * mm)}, " +
                          $"{Mm(o.Y / o.Count * mm)}) mm {(o.Count == 1 ? "is" : "are")} a face of no element: that surface is not " +
                          $"embedded in the volume mesh, which the solver cannot read; {OrphanRemedy}");

        return new Em3dMeshCheckResult(mesh.TetCount, zero, inverted, orphans, sentences);
    }

    /// <summary>Six times the signed volume of the corner tetrahedron.</summary>
    private static double Det(double[] x, int a, int b, int c, int d)
    {
        double ux = x[3 * b] - x[3 * a], uy = x[3 * b + 1] - x[3 * a + 1], uz = x[3 * b + 2] - x[3 * a + 2];
        double vx = x[3 * c] - x[3 * a], vy = x[3 * c + 1] - x[3 * a + 1], vz = x[3 * c + 2] - x[3 * a + 2];
        double wx = x[3 * d] - x[3 * a], wy = x[3 * d + 1] - x[3 * a + 1], wz = x[3 * d + 2] - x[3 * a + 2];
        return ux * (vy * wz - vz * wy) - uy * (vx * wz - vz * wx) + uz * (vx * wy - vy * wx);
    }

    /// <summary>The mid-edge nodes of a ten-node tetrahedron, in Gmsh's order (as ThermalMesh reads them).</summary>
    private static readonly (int I, int J)[] Edges = [(0, 1), (1, 2), (0, 2), (0, 3), (2, 3), (1, 3)];

    /// <summary>The barycentric points the quadratic Jacobian is sampled at: the four corners and the centroid.</summary>
    private static readonly double[][] Samples = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1], [0.25, 0.25, 0.25, 0.25]];

    /// <summary>Whether a ten-node tetrahedron's Jacobian is above <paramref name="floor"/> at every sample point.</summary>
    private static bool CurvedPositive(ReadOnlySpan<double> p, double floor)
    {
        Span<double> g = stackalloc double[40];        // dN_a/dL_m, node a, barycentric m
        Span<double> jac = stackalloc double[9];
        foreach (var l in Samples)
        {
            g.Clear();
            jac.Clear();
            for (int k = 0; k < 4; k++) g[4 * k + k] = 4 * l[k] - 1;
            for (int e = 0; e < 6; e++)
            {
                var (i, j) = Edges[e];
                g[4 * (4 + e) + i] = 4 * l[j];
                g[4 * (4 + e) + j] = 4 * l[i];
            }
            // J[r, d] = Σ_a x_a[r] (dN_a/dL_d − dN_a/dL_0), d = 1..3.
            for (int a = 0; a < 10; a++)
                for (int d = 1; d <= 3; d++)
                {
                    double w = g[4 * a + d] - g[4 * a];
                    for (int r = 0; r < 3; r++) jac[3 * r + d - 1] += p[3 * a + r] * w;
                }
            double det = jac[0] * (jac[4] * jac[8] - jac[5] * jac[7]) - jac[1] * (jac[3] * jac[8] - jac[5] * jac[6]) +
                         jac[2] * (jac[3] * jac[7] - jac[4] * jac[6]);
            if (det <= floor) return false;
        }
        return true;
    }

    private static (int, int, int) Key(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static string List(IReadOnlyCollection<string> names)
    {
        var n = names.Take(4).ToList();
        if (names.Count > 4) return string.Join(", ", n) + $" and {names.Count - 4} other object(s)";
        return n.Count == 1 ? n[0] : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];
    }

    private static string Mm(double v) => (Math.Abs(v) < 0.005 ? 0 : v).ToString("0.00", CultureInfo.InvariantCulture);

    private static double Sq(double v) => v * v;
}
