// brief-em3d-28 R-em3d28-3a — the mesh Gmsh made, as lines a 3D view draws over the solids:
//   * the BOUNDARY triangles' edges, one colour per physical group (each shared edge once);
//   * the TETRAHEDRA the clip plane cuts, as their section edges — where the plane crosses a face of a
//     tetrahedron, one segment. Never the volume: a million tetrahedra drawn whole are noise.
// Both are built off the drawing path (the section again each time the plane settles) and uploaded as
// an overlay slot; the frame loop only draws them.

using System.Numerics;

namespace CircuitRF.Render.Scene3D;

public static class MeshOverlay
{
    /// <summary>Colours of the physical groups' wireframes, cycled by group order.</summary>
    private static readonly (byte R, byte G, byte B)[] GroupColours =
        [(40, 40, 45), (200, 60, 60), (40, 110, 200), (40, 150, 70), (170, 90, 200), (200, 130, 30), (30, 150, 160)];

    /// <summary>A scene-local position of mesh node <paramref name="i"/>. The mesh is in the problem's
    /// units scaled by <paramref name="toMetres"/> (Gmsh meshes in the .geo's units).</summary>
    private static Vector3 Node(MshMesh m, int i, Scene3DModel scene, double toMetres)
        => scene.ToLocal(m.Nodes[3 * i] * toMetres, m.Nodes[3 * i + 1] * toMetres, m.Nodes[3 * i + 2] * toMetres);

    /// <summary>The boundary triangles' edges, each once, coloured by physical group.</summary>
    public static Scene3DVertex[] BoundaryWireframe(MshMesh mesh, Scene3DModel scene, double toMetres, bool dark = false)
    {
        var groups = mesh.TrianglePhysical.Distinct().Order().ToList();
        var colour = new Dictionary<int, uint>();
        for (int g = 0; g < groups.Count; g++)
        {
            var c = GroupColours[g % GroupColours.Length];
            if (dark && g == 0) c = (220, 220, 225);
            colour[groups[g]] = Scene3DVertex.Pack(c.R, c.G, c.B, 255);
        }
        var seen = new HashSet<long>();
        var outv = new List<Scene3DVertex>(mesh.TriangleCount * 3);
        var t = mesh.Triangles;
        for (int k = 0; k < mesh.TriangleCount; k++)
        {
            uint rgba = colour[mesh.TrianglePhysical[k]];
            for (int e = 0; e < 3; e++)
            {
                int a = t[3 * k + e], b = t[3 * k + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(key)) continue;
                var pa = Node(mesh, a, scene, toMetres); var pb = Node(mesh, b, scene, toMetres);
                outv.Add(new Scene3DVertex(pa.X, pa.Y, pa.Z, 0, rgba));
                outv.Add(new Scene3DVertex(pb.X, pb.Y, pb.Z, 0, rgba));
            }
        }
        return [.. outv];
    }

    /// <summary>
    /// The section of every tetrahedron the plane <paramref name="clip"/> cuts: for each face of a cut
    /// tetrahedron that the plane crosses, the segment between the two crossed edges. A node exactly on
    /// the plane counts as on its kept side, so a face lying in the plane contributes nothing.
    /// </summary>
    public static Scene3DVertex[] Section(MshMesh mesh, Scene3DModel scene, double toMetres, in ClipPlane3D clip,
                                          uint rgba, CancellationToken ct = default)
    {
        var e = clip.Equation;
        var n = new Vector3(e.X, e.Y, e.Z);
        var outv = new List<Scene3DVertex>();
        Span<Vector3> p = stackalloc Vector3[4];
        Span<float> d = stackalloc float[4];
        ReadOnlySpan<int> faces = [0, 1, 2, 0, 1, 3, 0, 2, 3, 1, 2, 3];
        var t = mesh.Tets;
        for (int k = 0; k < mesh.TetCount; k++)
        {
            if ((k & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int pos = 0;
            for (int j = 0; j < 4; j++)
            {
                p[j] = Node(mesh, t[4 * k + j], scene, toMetres);
                d[j] = Vector3.Dot(n, p[j]) + e.W;
                if (d[j] > 0) pos++;
            }
            if (pos == 0 || pos == 4) continue;
            for (int f = 0; f < 4; f++)
            {
                int a = faces[3 * f], b = faces[3 * f + 1], c = faces[3 * f + 2];
                Vector3 h0 = default, h1 = default;
                int h = 0;
                for (int q = 0; q < 3 && h < 2; q++)
                {
                    int i = q == 0 ? a : q == 1 ? b : a, j = q == 0 ? b : c;
                    if ((d[i] > 0) == (d[j] > 0)) continue;
                    var at = p[i] + (p[j] - p[i]) * (d[i] / (d[i] - d[j]));
                    if (h++ == 0) h0 = at; else h1 = at;
                }
                if (h == 2)
                {
                    outv.Add(new Scene3DVertex(h0.X, h0.Y, h0.Z, 0, rgba));
                    outv.Add(new Scene3DVertex(h1.X, h1.Y, h1.Z, 0, rgba));
                }
            }
        }
        return [.. outv];
    }
}
