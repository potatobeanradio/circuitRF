// brief-em3d-28 R-em3d28-4d — one clip plane, on an axis or perpendicular to the view.
//
// The plane keeps the half-space n·p + d ≤ 0 and discards the rest in the fragment shader (no geometry
// is cut). A solid the plane cuts shows its own inside: its BACK faces, which are drawn flat in the
// solid's colour whenever clipping is on — so the cut reads as capped in the cut solid's colour with
// no cap geometry to build or upload while the plane is dragged. That works because every solid the
// tessellation produces is closed and wound outward.

using System.Numerics;

namespace CircuitRF.Render.Scene3D;

/// <summary>Which way the clip plane faces.</summary>
public enum ClipAxis3D { X, Y, Z, View }

/// <summary>The clip plane's state: off, or an axis and a position along it.</summary>
public struct ClipPlane3D
{
    public bool Enabled;
    public ClipAxis3D Axis;
    /// <summary>Where the plane is along its normal, scene-local metres.</summary>
    public float Offset;
    /// <summary>For <see cref="ClipAxis3D.View"/>: the normal, captured when the axis was chosen, so
    /// orbiting afterwards does not swing the cut.</summary>
    public Vector3 ViewNormal;
    /// <summary>Keep the other side.</summary>
    public bool Flip;

    public readonly Vector3 Normal
    {
        get
        {
            var n = Axis switch
            {
                ClipAxis3D.X => Vector3.UnitX,
                ClipAxis3D.Y => Vector3.UnitY,
                ClipAxis3D.Z => Vector3.UnitZ,
                _            => ViewNormal == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(ViewNormal),
            };
            return Flip ? -n : n;
        }
    }

    /// <summary>(nx, ny, nz, d) with the kept side n·p + d ≤ 0 — the shader's <c>clip</c> vector.</summary>
    public readonly Vector4 Equation
    {
        get
        {
            var n = Normal;
            float at = Flip ? -Offset : Offset;
            return new Vector4(n, -at);
        }
    }

    /// <summary>Whether <paramref name="p"/> is on the kept side (always, when off).</summary>
    public readonly bool Keeps(Vector3 p)
    {
        if (!Enabled) return true;
        var e = Equation;
        return Vector3.Dot(new Vector3(e.X, e.Y, e.Z), p) + e.W <= 0;
    }

    /// <summary>The range the plane can be dragged over for <paramref name="min"/>..<paramref name="max"/>.</summary>
    public readonly (float Lo, float Hi) Range(Vector3 min, Vector3 max)
    {
        // Along the UNFLIPPED normal, the direction Offset is measured in (Equation negates both).
        var n = Axis switch
        {
            ClipAxis3D.X => Vector3.UnitX, ClipAxis3D.Y => Vector3.UnitY, ClipAxis3D.Z => Vector3.UnitZ,
            _ => ViewNormal == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(ViewNormal),
        };
        float lo = float.MaxValue, hi = float.MinValue;
        for (int k = 0; k < 8; k++)
        {
            var c = new Vector3((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z);
            float d = Vector3.Dot(n, c);
            lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
        }
        return (lo, hi);
    }
}
