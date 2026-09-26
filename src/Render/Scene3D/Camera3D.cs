// brief-em3d-28 R-em3d28-1a / R-em3d28-4a — the 3D view's camera: orbit, pan, zoom to cursor, fit,
// the standard views, perspective and orthographic. Below the firewall: no GPU API, no Avalonia.
//
// Z IS UP, as in Em3dProblem. The camera orbits a TARGET: yaw turns about +z, pitch tilts toward it,
// and pitch reaches ±90° exactly so Top and Bottom are true plan views. The view basis is built from
// yaw alone for "right" — never from cross(forward, z), which is undefined looking straight down — so
// a plan view has x to the right and y up on screen, like the layout editor.
//
// ONE MATRIX. The whole camera reaches the GPU as one view-projection matrix per frame (em-3d.md §8.2
// point 1): orbiting uploads no buffer. Every method writes into caller storage and nothing here
// allocates, so the frame loop stays at zero managed allocation in the Debug build the owner runs.
//
// ORTHOGRAPHIC KEEPS THE SCALE. Its half-height is the perspective frustum's half-height AT THE
// TARGET, so toggling projection leaves whatever is at the target the same size on screen.

using System.Numerics;

namespace CircuitRF.Render.Scene3D;

/// <summary>Perspective or orthographic projection.</summary>
public enum Projection3D { Perspective, Orthographic }

/// <summary>The standard views on the toolbar and keys. Front looks along +y (from −y), Right along
/// −x (from +x), Top down −z; Iso looks from (+x, −y, +z).</summary>
public enum StandardView3D { Iso, Top, Bottom, Front, Back, Left, Right }

/// <summary>An orbit camera, z up, in scene-local coordinates (metres from the scene's origin).</summary>
public struct Camera3D
{
    /// <summary>Radians of orbit per pixel of drag.</summary>
    public const float OrbitRadiansPerPixel = 0.008f;

    /// <summary>Distance factor per wheel notch (a notch in is ×0.88, out ×1/0.88).</summary>
    public const float ZoomPerNotch = 0.88f;

    /// <summary>The perspective field of view, vertical, radians.</summary>
    public const float DefaultFovY = 0.7f;

    /// <summary>A fit leaves this fraction of the viewport as margin around the bounds.</summary>
    public const float FitMargin = 1.15f;

    public Vector3 Target;
    public float Yaw, Pitch, Distance, FovY;
    public Projection3D Projection;

    /// <summary>The radius of the scene's bounding sphere and its centre — what near and far are
    /// derived from, so a zoomed-in camera does not clip the scene.</summary>
    public float SceneRadius;
    public Vector3 SceneCentre;

    /// <summary>A camera framing <paramref name="min"/>..<paramref name="max"/> from the iso view.</summary>
    public static Camera3D Fit(Vector3 min, Vector3 max, float aspect, Projection3D projection = Projection3D.Perspective)
    {
        var c = new Camera3D { FovY = DefaultFovY, Projection = projection };
        c.SetStandardView(StandardView3D.Iso);
        c.FitBounds(min, max, aspect);
        return c;
    }

    /// <summary>The unit vector from the target toward the eye.</summary>
    public readonly Vector3 Back
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            return new Vector3(cp * MathF.Cos(Yaw), cp * MathF.Sin(Yaw), MathF.Sin(Pitch));
        }
    }

    public readonly Vector3 Eye => Target + Distance * Back;
    public readonly Vector3 Forward => -Back;

    /// <summary>Screen right, in the world. Defined from yaw alone so it exists in a plan view.</summary>
    public readonly Vector3 Right => new(-MathF.Sin(Yaw), MathF.Cos(Yaw), 0);

    /// <summary>Screen up, in the world.</summary>
    public readonly Vector3 Up => Vector3.Cross(Right, Forward);

    public void SetStandardView(StandardView3D view)
    {
        const float half = MathF.PI / 2;
        (Yaw, Pitch) = view switch
        {
            StandardView3D.Top    => (-half, half),
            StandardView3D.Bottom => (-half, -half),
            StandardView3D.Front  => (-half, 0f),
            StandardView3D.Back   => (half, 0f),
            StandardView3D.Right  => (0f, 0f),
            StandardView3D.Left   => (MathF.PI, 0f),
            _                     => (-MathF.PI / 4, MathF.Asin(1 / MathF.Sqrt(3))),
        };
    }

    public void Orbit(float dxPixels, float dyPixels)
    {
        // Wrapped: the yaw is persisted, and an unbounded one loses float precision turn by turn.
        Yaw = MathF.IEEERemainder(Yaw - dxPixels * OrbitRadiansPerPixel, 2 * MathF.PI);
        Pitch = Math.Clamp(Pitch + dyPixels * OrbitRadiansPerPixel, -MathF.PI / 2, MathF.PI / 2);
    }

    /// <summary>World units per pixel at the target's depth — what a pan moves by and what the scale
    /// bar reads.</summary>
    public readonly float WorldPerPixel(float viewportHeight)
        => 2f * Distance * MathF.Tan(FovY * 0.5f) / MathF.Max(1f, viewportHeight);

    public void Pan(float dxPixels, float dyPixels, float viewportHeight)
    {
        float k = WorldPerPixel(viewportHeight);
        Target += (-dxPixels * Right + dyPixels * Up) * k;
    }

    /// <summary>
    /// Zoom by <paramref name="notches"/> (positive = in) toward the point under the cursor: that point,
    /// on the plane through the target facing the camera, stays under the cursor.
    /// </summary>
    public void ZoomAt(float notches, float px, float py, float width, float height)
    {
        // Zoom multiplies the distance, so a zero (or lost) distance could never grow back: start it
        // from the scene's own size instead.
        if (!(Distance > 0) || !float.IsFinite(Distance)) Distance = MathF.Max(SceneRadius, 1e-6f);
        float f = MathF.Pow(ZoomPerNotch, notches);
        var p = PointOnFocalPlane(px, py, width, height);
        Target = p + (Target - p) * f;
        Distance *= f;
    }

    /// <summary>The point under pixel (<paramref name="px"/>, <paramref name="py"/>) — top-left origin —
    /// on the plane through the target facing the camera.</summary>
    public readonly Vector3 PointOnFocalPlane(float px, float py, float width, float height)
    {
        float k = WorldPerPixel(height);
        float dx = px - width * 0.5f, dy = py - height * 0.5f;
        return Target + Right * (dx * k) - Up * (dy * k);
    }

    /// <summary>Frames <paramref name="min"/>..<paramref name="max"/> from the current direction.</summary>
    public void FitBounds(Vector3 min, Vector3 max, float aspect)
    {
        SceneCentre = (min + max) * 0.5f;
        SceneRadius = MathF.Max(1e-12f, (max - min).Length() * 0.5f);
        Target = SceneCentre;
        float halfFov = FovY * 0.5f;
        float fit = SceneRadius * FitMargin / MathF.Tan(halfFov);
        if (aspect < 1) fit /= MathF.Max(0.05f, aspect);
        Distance = fit;
    }

    /// <summary>Near and far clip distances along the view direction, bracketing the scene's sphere.</summary>
    public readonly (float Near, float Far) DepthRange()
    {
        float along = Vector3.Dot(SceneCentre - Eye, Forward);
        float r = MathF.Max(SceneRadius, 1e-9f) * 1.05f;
        float far = MathF.Max(along + r, Distance * 0.01f + r);
        if (Projection == Projection3D.Orthographic) return (along - r, far);
        float near = MathF.Max(along - r, far * 1e-4f);
        return (near, far);
    }

    /// <summary>The view matrix (row-vector convention, as System.Numerics).</summary>
    public readonly Matrix4x4 View()
    {
        Vector3 r = Right, u = Up, f = Forward, e = Eye;
        // Camera looks down its −z; x = right, y = up.
        return new Matrix4x4(
            r.X, u.X, -f.X, 0,
            r.Y, u.Y, -f.Y, 0,
            r.Z, u.Z, -f.Z, 0,
            -Vector3.Dot(r, e), -Vector3.Dot(u, e), Vector3.Dot(f, e), 1);
    }

    /// <summary>The projection matrix, depth in [0, 1] (Metal, D3D11 and Vulkan all use it).</summary>
    public readonly Matrix4x4 ProjectionMatrix(float width, float height)
    {
        float aspect = MathF.Max(1, width) / MathF.Max(1, height);
        // A default-constructed camera has a zero field of view, which System.Numerics refuses with an
        // exception; that must never reach a frame (it once did, for a setup that refused and so was
        // never fitted). A degenerate depth range is widened the same way.
        float fov = FovY > 0 && FovY < MathF.PI ? FovY : DefaultFovY;
        var (near, far) = DepthRange();
        if (!(far > near)) far = near + MathF.Max(1e-6f, MathF.Abs(near));
        if (Projection == Projection3D.Orthographic)
        {
            float h = MathF.Max(1e-12f, Distance * MathF.Tan(fov * 0.5f));
            return Matrix4x4.CreateOrthographicOffCenter(-h * aspect, h * aspect, -h, h, near, far);
        }
        if (!(near > 0)) near = MathF.Max(1e-9f, far * 1e-4f);
        return Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
    }

    /// <summary>View × projection, optionally narrowed so pixel (<paramref name="pickX"/>,
    /// <paramref name="pickY"/>) fills the viewport — the 1 × 1 ID pass.</summary>
    public readonly Matrix4x4 ViewProjectionMatrix(float width, float height, float pickX = -1, float pickY = -1)
    {
        var vp = View() * ProjectionMatrix(width, height);
        if (pickX >= 0)
        {
            float nx = 2f * (pickX + 0.5f) / width - 1f, ny = 1f - 2f * (pickY + 0.5f) / height;
            // x' = W (x − nx·w), y' = H (y − ny·w): the pixel's footprint scaled to the whole clip square.
            var pk = new Matrix4x4(width, 0, 0, 0, 0, height, 0, 0, 0, 0, 1, 0, -width * nx, -height * ny, 0, 1);
            vp *= pk;
        }
        return vp;
    }

    /// <summary>
    /// The view-projection into <paramref name="dst"/> (16 floats), laid out for the WGSL source's
    /// <c>mat4x4f</c> — column-major, clip = M · p. <paramref name="flipY"/> negates clip y, for an API
    /// whose framebuffer y runs down (Vulkan).
    /// </summary>
    public readonly void WriteViewProjection(Span<float> dst, float width, float height, bool flipY = false,
                                             float pickX = -1, float pickY = -1)
    {
        var m = ViewProjectionMatrix(width, height, pickX, pickY);
        float s = flipY ? -1 : 1;
        // Row-vector M stored row-major is the column-vector Mᵀ stored column-major.
        dst[0] = m.M11; dst[1] = m.M12 * s; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22 * s; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32 * s; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42 * s; dst[14] = m.M43; dst[15] = m.M44;
    }

    /// <summary>The ray through pixel centre (<paramref name="px"/>, <paramref name="py"/>).</summary>
    public readonly (Vector3 Origin, Vector3 Direction) Ray(float px, float py, float width, float height)
    {
        var inv = Invert(ViewProjectionMatrix(width, height));
        float nx = 2f * (px + 0.5f) / width - 1f, ny = 1f - 2f * (py + 0.5f) / height;
        var a = Vector4.Transform(new Vector4(nx, ny, 0, 1), inv);
        var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (pa, Vector3.Normalize(pb - pa));
    }

    /// <summary>Where <paramref name="world"/> lands on screen, pixels from the top-left, and whether
    /// it is in front of the camera.</summary>
    public readonly (float X, float Y, bool Visible) Project(Vector3 world, float width, float height)
    {
        var c = Vector4.Transform(new Vector4(world, 1), ViewProjectionMatrix(width, height));
        if (c.W <= 0) return (0, 0, false);
        float nx = c.X / c.W, ny = c.Y / c.W;
        return ((nx + 1) * 0.5f * width, (1 - ny) * 0.5f * height, true);
    }

    private static Matrix4x4 Invert(Matrix4x4 m)
        => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;
}
