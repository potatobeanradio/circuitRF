// brief-em3d-28 R-em3d28-1a / gate 9 — picking-ray math, for the CPU fallback and for tests.
//
// The viewer picks on the GPU (R-em3d28-4b): the ID pass draws every visible object's ID through the
// camera's pick-narrowed matrix into a 1 × 1 target and reads it back. Two CPU paths live here:
//   * Pick — a ray through the pixel, intersected with every visible triangle (the fallback, and the
//     answer the GPU's must agree with);
//   * IdAtPixel — the ID pass itself, in software: every triangle through Camera3D's PICK matrix,
//     covered-or-not at the 1 × 1 target's centre, nearest depth wins. It is what the GPU computes,
//     so gate 9 asserts the two agree.
// Neither runs per frame; both allocate nothing per triangle. Both see the same set as the GPU: visible
// PICKABLE objects (everything but the air and the box's faces, which enclose everything else and
// would otherwise be all a cursor ever found), nearest surface first.

using System.Numerics;

namespace CircuitRF.Render.Scene3D;

public static class Scene3DPicking
{
    /// <summary>The nearest visible object the ray through pixel (<paramref name="px"/>,
    /// <paramref name="py"/>) hits on the kept side of <paramref name="clip"/>, and where; 0 for none.</summary>
    public static (uint Id, Vector3 Point) Pick(Scene3DModel scene, in Camera3D camera, float px, float py,
                                                 float width, float height, ReadOnlySpan<bool> visible,
                                                 in ClipPlane3D clip = default)
    {
        var (o, d) = camera.Ray(px, py, width, height);
        float best = float.MaxValue;
        uint id = 0;
        var verts = scene.Vertices;
        foreach (var b in scene.Batches)
        {
            if (!Visible(visible, b.ObjectId) || !scene.Objects[b.ObjectId - 1].Pickable) continue;
            for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i += 3)
            {
                var v0 = P(verts[scene.Indices[i]]); var v1 = P(verts[scene.Indices[i + 1]]); var v2 = P(verts[scene.Indices[i + 2]]);
                if (Intersect(o, d, v0, v1, v2, out float t) && t < best && clip.Keeps(o + d * t))
                {
                    best = t;
                    id = b.ObjectId;
                }
            }
        }
        return (id, id == 0 ? Vector3.Zero : o + d * best);
    }

    /// <summary>
    /// The ID the GPU's 1 × 1 ID pass writes for pixel (<paramref name="px"/>, <paramref name="py"/>),
    /// computed in software through the very matrix the renderer is handed
    /// (<see cref="Camera3D.ViewProjectionMatrix"/> with the pick pixel): the target's single sample is
    /// at clip (0, 0), and the nearest covering fragment in [0, 1] depth wins.
    /// </summary>
    public static uint IdAtPixel(Scene3DModel scene, in Camera3D camera, float px, float py, float width,
                                 float height, ReadOnlySpan<bool> visible)
    {
        var m = camera.ViewProjectionMatrix(width, height, px, py);
        float bestDepth = float.MaxValue;
        uint id = 0;
        var verts = scene.Vertices;
        foreach (var b in scene.Batches)
        {
            if (!Visible(visible, b.ObjectId) || !scene.Objects[b.ObjectId - 1].Pickable) continue;
            for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i += 3)
            {
                var c0 = Vector4.Transform(new Vector4(P(verts[scene.Indices[i]]), 1), m);
                var c1 = Vector4.Transform(new Vector4(P(verts[scene.Indices[i + 1]]), 1), m);
                var c2 = Vector4.Transform(new Vector4(P(verts[scene.Indices[i + 2]]), 1), m);
                if (c0.W <= 0 || c1.W <= 0 || c2.W <= 0) continue;
                var n0 = new Vector3(c0.X, c0.Y, c0.Z) / c0.W;
                var n1 = new Vector3(c1.X, c1.Y, c1.Z) / c1.W;
                var n2 = new Vector3(c2.X, c2.Y, c2.Z) / c2.W;
                // Barycentrics of the sample point (0, 0) in the projected triangle.
                float area = Edge(n0, n1, n2.X, n2.Y);
                if (area == 0) continue;
                float w0 = Edge(n1, n2, 0, 0) / area, w1 = Edge(n2, n0, 0, 0) / area, w2 = Edge(n0, n1, 0, 0) / area;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * n0.Z + w1 * n1.Z + w2 * n2.Z;
                if (z < 0 || z > 1 || z >= bestDepth) continue;
                bestDepth = z;
                id = b.ObjectId;
            }
        }
        return id;
    }

    private static bool Visible(ReadOnlySpan<bool> visible, uint id)
        => visible.IsEmpty || (id - 1 < (uint)visible.Length && visible[(int)id - 1]);

    private static Vector3 P(in Scene3DVertex v) => new(v.X, v.Y, v.Z);

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    /// <summary>Möller–Trumbore, two-sided.</summary>
    public static bool Intersect(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0;
        var e1 = v1 - v0; var e2 = v2 - v0;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-30f) return false;
        float inv = 1 / det;
        var s = o - v0;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0;
    }
}
