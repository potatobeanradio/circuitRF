using System.Numerics;

namespace Viewer3dSpike.Scene;

public enum ObjectKind { Metal, Dielectric, Air }

public sealed class SceneObject
{
    public required int Local;                  // index within one replica
    public required string Name;
    public required ObjectKind Kind;
    public bool Translucent => Kind != ObjectKind.Metal;
}

/// <summary>A draw range: one object in one replica. Id 0 is the background.</summary>
public struct DrawRange
{
    public uint Id;
    public int FirstIndex, IndexCount;
    public Vector3 Centroid;
}

/// <summary>
/// The spike's scene: F0 case A's boundary surfaces, one object per Gmsh surface entity (plus the
/// air/substrate interface recovered from the tetrahedra), replicated in a grid until the triangle
/// count reaches the target. ONE vertex buffer and ONE index buffer for everything. Opaque objects
/// come first and are drawn as one range; each translucent object is its own range so it can be
/// sorted per object (brief 27 §2.4).
/// Vertex layout (16 bytes): float x, y, z; uint objectId.
/// </summary>
public sealed class SceneModel
{
    public const int VertexStride = 16;
    public byte[] Vertices = [];               // packed VertexStride-byte vertices
    public uint[] Indices = [];
    public int VertexCount, TriangleCount, Replicas, ObjectsPerReplica;
    public SceneObject[] Objects = [];
    public int OpaqueIndexCount;               // [0, OpaqueIndexCount) is every opaque object, all replicas
    public DrawRange[] Opaque = [];            // per object, for bookkeeping only (drawn as one range)
    public DrawRange[] Translucent = [];
    public Vector3 BoundsMin, BoundsMax;
    public float[] Colors = [];                // RGBA per local object

    public static SceneModel FromMsh(MshMesh mesh, int targetTriangles)
    {
        // ---- group one replica's triangles into objects ----
        var groups = new List<(SceneObject Obj, List<(int, int, int)> Tris)>();
        var byEntity = new Dictionary<(int, int), int>();
        var metalFaces = new HashSet<(int, int, int)>();
        foreach (var t in mesh.Triangles)
        {
            if (!byEntity.TryGetValue((t.Physical, t.Entity), out int g))
            {
                g = groups.Count;
                byEntity[(t.Physical, t.Entity)] = g;
                string pname = mesh.PhysicalNames.GetValueOrDefault(t.Physical, $"phys{t.Physical}");
                groups.Add((new SceneObject { Local = g, Name = $"{pname}#{t.Entity}", Kind = ObjectKind.Metal }, []));
            }
            groups[g].Tris.Add((t.A, t.B, t.C));
            metalFaces.Add(Sorted(t.A, t.B, t.C));
        }
        // outer_pec faces: the floor is the ground (metal); the other walls are drawn as the air or
        // dielectric they enclose, so they are translucent. Classified by the z-range of the face.
        float zSub = 100.5f;
        foreach (var (obj, tris) in groups)
        {
            if (!obj.Name.StartsWith("outer_pec")) continue;
            float zmin = float.MaxValue, zmax = float.MinValue;
            foreach (var (a, b, c) in tris)
                foreach (int n in (int[])[a, b, c]) { float z = mesh.Nodes[3 * n + 2]; zmin = Math.Min(zmin, z); zmax = Math.Max(zmax, z); }
            obj.Kind = zmax < 0.5f ? ObjectKind.Metal : zmax <= zSub ? ObjectKind.Dielectric : ObjectKind.Air;
        }
        // The air/substrate interface: faces shared by an air tet and a substrate tet, minus the
        // metal already drawn there (the pads). This is the "dielectric over metal" layer.
        int physAir = mesh.PhysicalNames.FirstOrDefault(p => p.Value == "air").Key;
        int physSub = mesh.PhysicalNames.FirstOrDefault(p => p.Value == "substrate").Key;
        if (mesh.Tets.Count > 0)
        {
            var owner = new Dictionary<(int, int, int), int>();
            var iface = new List<(int, int, int)>();
            foreach (var t in mesh.Tets)
            {
                ReadOnlySpan<(int, int, int)> faces = [(t.A, t.B, t.C), (t.A, t.B, t.D), (t.A, t.C, t.D), (t.B, t.C, t.D)];
                foreach (var f in faces)
                {
                    var k = Sorted(f.Item1, f.Item2, f.Item3);
                    if (owner.Remove(k, out int other))
                    {
                        if (other != t.Physical && (other == physAir || other == physSub) && (t.Physical == physAir || t.Physical == physSub)
                            && !metalFaces.Contains(k))
                            iface.Add(f);
                    }
                    else owner[k] = t.Physical;
                }
            }
            if (iface.Count > 0)
                groups.Add((new SceneObject { Local = groups.Count, Name = "substrate_top", Kind = ObjectKind.Dielectric }, iface));
        }

        // ---- per-object local vertex lists ----
        var objVerts = new List<(int[] NodeOf, uint[] Idx)>();
        foreach (var (_, tris) in groups)
        {
            var map = new Dictionary<int, int>();
            var nodes = new List<int>();
            var idx = new uint[tris.Count * 3];
            int w = 0;
            foreach (var (a, b, c) in tris)
                foreach (int n in (int[])[a, b, c])
                {
                    if (!map.TryGetValue(n, out int v)) { v = nodes.Count; map[n] = v; nodes.Add(n); }
                    idx[w++] = (uint)v;
                }
            objVerts.Add((nodes.ToArray(), idx));
        }

        int trisPerReplica = groups.Sum(g => g.Tris.Count);
        int replicas = Math.Max(1, (int)Math.Ceiling(targetTriangles / (double)trisPerReplica));
        int nx = (int)Math.Ceiling(Math.Sqrt(replicas * 1.5)), ny = (int)Math.Ceiling(replicas / (double)nx);

        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int i = 0; i < mesh.Nodes.Length; i += 3)
        {
            var p = new Vector3(mesh.Nodes[i], mesh.Nodes[i + 1], mesh.Nodes[i + 2]);
            mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
        }
        var size = mx - mn;
        float sx = size.X * 1.08f, sy = size.Y * 1.08f;

        var s = new SceneModel
        {
            Objects = groups.Select(g => g.Obj).ToArray(),
            ObjectsPerReplica = groups.Count,
            Replicas = replicas,
            TriangleCount = trisPerReplica * replicas,
        };
        int vertsPerReplica = objVerts.Sum(o => o.NodeOf.Length);
        s.VertexCount = vertsPerReplica * replicas;
        s.Vertices = new byte[s.VertexCount * VertexStride];
        s.Indices = new uint[s.TriangleCount * 3];

        // vertex base of (replica, object)
        var vbase = new int[replicas, groups.Count];
        int vw = 0;
        var vspan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(s.Vertices.AsSpan());
        var vuint = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(s.Vertices.AsSpan());
        for (int r = 0; r < replicas; r++)
        {
            var off = new Vector3((r % nx) * sx, (r / nx) * sy, 0) - new Vector3(mn.X, mn.Y, 0);
            for (int o = 0; o < groups.Count; o++)
            {
                vbase[r, o] = vw;
                uint id = (uint)(r * groups.Count + o + 1);
                foreach (int n in objVerts[o].NodeOf)
                {
                    int f = vw * 4;
                    vspan[f] = mesh.Nodes[3 * n] + off.X;
                    vspan[f + 1] = mesh.Nodes[3 * n + 1] + off.Y;
                    vspan[f + 2] = mesh.Nodes[3 * n + 2] + off.Z;
                    vuint[f + 3] = id;
                    vw++;
                }
            }
        }
        s.BoundsMin = new Vector3(0, 0, mn.Z);
        s.BoundsMax = new Vector3((nx - 1) * sx + size.X, (ny - 1) * sy + size.Y, mx.Z);

        // index buffer: opaque first (one range), then each translucent object
        int iw = 0;
        var opaque = new List<DrawRange>(); var trans = new List<DrawRange>();
        foreach (bool pass in (bool[])[false, true])
        {
            for (int r = 0; r < replicas; r++)
                for (int o = 0; o < groups.Count; o++)
                {
                    if (s.Objects[o].Translucent != pass) continue;
                    int first = iw;
                    var c = Vector3.Zero;
                    foreach (uint li in objVerts[o].Idx)
                    {
                        uint gi = (uint)(vbase[r, o] + li);
                        s.Indices[iw++] = gi;
                        c += new Vector3(vspan[(int)gi * 4], vspan[(int)gi * 4 + 1], vspan[(int)gi * 4 + 2]);
                    }
                    var dr = new DrawRange { Id = (uint)(r * groups.Count + o + 1), FirstIndex = first, IndexCount = iw - first, Centroid = c / Math.Max(1, iw - first) };
                    (pass ? trans : opaque).Add(dr);
                }
            if (!pass) s.OpaqueIndexCount = iw;
        }
        s.Opaque = opaque.ToArray();
        s.Translucent = trans.ToArray();

        s.Colors = new float[groups.Count * 4];
        for (int o = 0; o < groups.Count; o++)
        {
            var obj = s.Objects[o];
            (float R, float G, float B, float A) col = obj.Kind switch
            {
                ObjectKind.Air => (0.55f, 0.75f, 1.0f, 0.10f),
                ObjectKind.Dielectric => (0.35f, 0.75f, 0.35f, 0.30f),
                _ when obj.Name.StartsWith("wire") => (0.95f, 0.78f, 0.25f, 1f),
                _ when obj.Name.StartsWith("port") => (0.90f, 0.30f, 0.30f, 1f),
                _ when obj.Name.StartsWith("pads") => (0.85f, 0.65f, 0.35f, 1f),
                _ => (0.60f, 0.60f, 0.62f, 1f),
            };
            s.Colors[4 * o] = col.R; s.Colors[4 * o + 1] = col.G; s.Colors[4 * o + 2] = col.B; s.Colors[4 * o + 3] = col.A;
        }
        return s;
    }

    /// <summary>For hosts with no mesh file: a procedurally generated stand-in with the same shape
    /// of data (opaque + translucent objects). Used only when the .msh is absent, and says so.</summary>
    public static SceneModel Synthetic(int targetTriangles)
    {
        var m = new MshMesh { PhysicalNames = { [1] = "air", [3] = "outer_pec", [7] = "wire" } };
        var nodes = new List<float>();
        int Node(float x, float y, float z) { nodes.Add(x); nodes.Add(y); nodes.Add(z); return nodes.Count / 3 - 1; }
        // a tessellated tube (the "wire") and a box (the air), fine enough to be ~100k triangles
        int seg = 64, rings = 700;
        int b0 = 0;
        for (int i = 0; i <= rings; i++)
            for (int j = 0; j < seg; j++)
            {
                float t = i / (float)rings, a = j * MathF.Tau / seg;
                float x = -475 + 950 * t, zc = 112 + 150 * MathF.Sin(MathF.PI * t);
                Node(x, 12.7f * MathF.Cos(a), zc + 12.7f * MathF.Sin(a));
            }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < seg; j++)
            {
                int a = b0 + i * seg + j, b = b0 + i * seg + (j + 1) % seg, c = a + seg, d = b + seg;
                m.Triangles.Add((a, b, d, 7, 1)); m.Triangles.Add((a, d, c, 7, 1));
            }
        int[] q = [Node(-1500, -1000, 0), Node(1500, -1000, 0), Node(1500, 1000, 0), Node(-1500, 1000, 0),
                   Node(-1500, -1000, 1100), Node(1500, -1000, 1100), Node(1500, 1000, 1100), Node(-1500, 1000, 1100)];
        m.Triangles.Add((q[0], q[1], q[2], 3, 2)); m.Triangles.Add((q[0], q[2], q[3], 3, 2));
        int[][] walls = [[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [4, 5, 6, 7]];
        int e = 3;
        foreach (var w in walls) { m.Triangles.Add((q[w[0]], q[w[1]], q[w[2]], 3, e)); m.Triangles.Add((q[w[0]], q[w[2]], q[w[3]], 3, e)); e++; }
        m.Nodes = nodes.ToArray();
        return FromMsh(m, targetTriangles);
    }

    static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    public string Describe() =>
        $"{TriangleCount:N0} triangles, {VertexCount:N0} vertices, {Replicas} replicas x {ObjectsPerReplica} objects " +
        $"({Objects.Count(o => !o.Translucent)} opaque, {Objects.Count(o => o.Translucent)} translucent per replica); " +
        $"VB {Vertices.Length / 1048576.0:F1} MiB, IB {Indices.Length * 4 / 1048576.0:F1} MiB";
}
