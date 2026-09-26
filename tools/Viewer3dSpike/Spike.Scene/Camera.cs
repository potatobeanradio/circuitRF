using System.Numerics;

namespace Viewer3dSpike.Scene;

/// <summary>
/// Orbit camera, Z up. The whole camera is one view-projection matrix (brief 27 §2.2): moving it
/// uploads no buffer — the matrix goes to the GPU as a uniform / inline bytes each frame.
/// Every method writes into caller storage; nothing allocates.
/// </summary>
public struct Camera
{
    public Vector3 Target;
    public float Yaw, Pitch, Distance, FovY;
    public float Near, Far;

    public static Camera Frame(Vector3 min, Vector3 max)
    {
        var c = (min + max) * 0.5f;
        float r = (max - min).Length() * 0.5f;
        return new Camera { Target = c, Yaw = -0.9f, Pitch = 0.55f, Distance = r * 1.9f, FovY = 0.8f, Near = r * 0.01f, Far = r * 8f };
    }

    public readonly Vector3 Eye
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            return Target + Distance * new Vector3(cp * MathF.Cos(Yaw), cp * MathF.Sin(Yaw), MathF.Sin(Pitch));
        }
    }

    public readonly Vector3 Forward => Vector3.Normalize(Target - Eye);

    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.008f;
        Pitch = Math.Clamp(Pitch + dy * 0.008f, -1.55f, 1.55f);
    }

    public void Pan(float dx, float dy, float viewportHeight)
    {
        var f = Forward;
        var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var up = Vector3.Cross(right, f);
        float k = 2f * Distance * MathF.Tan(FovY * 0.5f) / MathF.Max(1f, viewportHeight);
        Target += (-dx * right + dy * up) * k;
    }

    public void Zoom(float wheel) => Distance *= MathF.Pow(0.88f, wheel);

    /// <summary>Column-major view-projection into <paramref name="dst"/> (16 floats), for GLSL/MSL/WGSL.
    /// <paramref name="zeroToOne"/>: depth range [0,1] (Metal, WebGPU) instead of GL's [-1,1].
    /// A non-null pick pixel narrows the projection so that one pixel fills the viewport — the ID
    /// pass then renders into a 1x1 target.</summary>
    public readonly void ViewProjection(Span<float> dst, float width, float height, bool zeroToOne, float pickX = -1, float pickY = -1)
    {
        var view = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitZ);
        float near = MathF.Max(Near, Distance * 0.002f), far = Far + Distance;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(FovY, MathF.Max(1, width) / MathF.Max(1, height), near, far);
        // System.Numerics is row-vector with z in [0,1]; GL wants [-1,1]
        if (!zeroToOne)
        {
            proj.M33 = -(far + near) / (far - near);
            proj.M43 = -2f * far * near / (far - near);
        }
        var vp = view * proj;                    // row-vector: clip = p * view * proj
        if (pickX >= 0)
        {
            float nx = 2f * (pickX + 0.5f) / width - 1f, ny = 1f - 2f * (pickY + 0.5f) / height;
            // x' = W (x - nx w), y' = H (y - ny w), on the column that produces each clip component
            var pk = new Matrix4x4(width, 0, 0, 0, 0, height, 0, 0, 0, 0, 1, 0, -width * nx, -height * ny, 0, 1);
            vp = vp * pk;
        }
        // row-vector matrix M stored row-major == column-vector M^T stored column-major
        dst[0] = vp.M11; dst[1] = vp.M12; dst[2] = vp.M13; dst[3] = vp.M14;
        dst[4] = vp.M21; dst[5] = vp.M22; dst[6] = vp.M23; dst[7] = vp.M24;
        dst[8] = vp.M31; dst[9] = vp.M32; dst[10] = vp.M33; dst[11] = vp.M34;
        dst[12] = vp.M41; dst[13] = vp.M42; dst[14] = vp.M43; dst[15] = vp.M44;
    }
}
