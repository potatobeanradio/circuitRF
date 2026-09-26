using System.Numerics;

namespace Viewer3dSpike.Scene;

/// <summary>What the input loop hands the frame loop each frame: a small value, never geometry.</summary>
public struct FrameInput
{
    public Camera Camera;
    public int Width, Height;          // target size in device pixels
    public float PickX, PickY;         // cursor in device pixels (top-left origin); negative = none
    public bool Orbiting;              // a camera gesture moved the camera since the last frame
    public uint Selected;
}

/// <summary>
/// Per-object back-to-front ordering of the translucent ranges (brief 27 §2.4: sorted per object,
/// not per triangle). Preallocated; <see cref="Sort"/> allocates nothing.
/// </summary>
public sealed class TranslucentSorter(DrawRange[] ranges)
{
    readonly float[] _keys = new float[ranges.Length];
    public readonly int[] Order = new int[ranges.Length];

    public void Sort(in Camera cam)
    {
        var eye = cam.Eye;
        for (int i = 0; i < ranges.Length; i++)
        {
            Order[i] = i;
            _keys[i] = -Vector3.DistanceSquared(ranges[i].Centroid, eye);   // farthest first
        }
        Array.Sort(_keys, Order);
    }
}
