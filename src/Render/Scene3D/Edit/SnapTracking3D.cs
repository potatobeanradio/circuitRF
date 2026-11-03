// Snap tracking — a point snap hovered for a moment becomes a REFERENCE, and while a tool is armed the cursor then
// locks onto the world-axis lines through it: a sheet drawn from a trace's corner straight down to a ground plane, or
// across to a far edge, without any vertex there to snap to.
//
// ACQUIRING AND FORGETTING are SnapReferences' (shared with the layout editor): a point snap (vertex, midpoint, face
// centre, centre) held for its dwell becomes a reference, a second dwell lets it go, and one unused for a while is
// forgotten. The editor also clears them when the tool finishes or is put away.
//
// TRACKING. When the raw snap is not a point (an edge, the grid, nothing), each reference offers the three world-axis
// lines through it. One within the snap radius of the cursor ON SCREEN is the line the cursor is on, and the tracked
// point is on that line:
//   - near an edge, the edge snap's own coordinate along the line — the projection of the snap onto it, exactly as a
//     tool's Along reads a geometry snap — so a line down from a corner stops on the ground plane's edge;
//   - otherwise the point of the line nearest the cursor's ray, its step from the reference on the grid.
// Two lines from two references that cross, both near the cursor, give their crossing: x of one corner, y of another.
// A raw point snap always wins: the cursor on a vertex means that vertex.
//
// Pure: the caller hands in the time, the camera and the cursor, so every rule is tested without a window.

using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>One acquired reference: a world point (metres) and the object it came from.</summary>
public readonly record struct Snap3DReference(Point3 World, uint Object);

/// <summary>A tracking line in force: reference <see cref="Reference"/>'s line along world axis <see cref="Axis"/> (0 x, 1 y, 2 z).</summary>
public readonly record struct Snap3DTrackLine(int Reference, int Axis);

/// <summary>What the tracker needs of the view for one query: the scene (for its local frame), the camera, the pane's size and
/// the cursor in DIPs, the cursor's ray (world metres), the snap radius in pixels, and the grid.</summary>
public readonly record struct Snap3DTrackView(Scene3DModel Scene, Camera3D Camera, float Width, float Height, float CursorX, float CursorY,
                                              Point3 RayOrigin, Point3 RayDirection, float RadiusPixels, Snap3DGrid Grid);

public sealed class SnapTracker3D
{
    private readonly SnapReferences<Snap3DReference> _set = new((a, b) => Same(a.World, b.World));

    /// <summary>The references, oldest first.</summary>
    public IReadOnlyList<SnapReferences<Snap3DReference>.Entry> References => _set.Items;

    /// <summary>The line(s) the last <see cref="Apply"/> tracked along — none, one, or two when it took their crossing.</summary>
    public Snap3DTrackLine? LineA { get; private set; }
    public Snap3DTrackLine? LineB { get; private set; }

    /// <summary>Whether the caller should keep ticking: a dwell is pending, or a reference may yet be forgotten.</summary>
    public bool NeedsTick => _set.NeedsTick;

    /// <summary>Whether a kind is a point a reference can be acquired from (and that beats tracking).</summary>
    public static bool IsPoint(Snap3DKind k) => k is Snap3DKind.Vertex or Snap3DKind.Midpoint or Snap3DKind.FaceCentre or Snap3DKind.Centre;

    /// <summary>Every reference dropped. True when there were any.</summary>
    public bool Clear()
    {
        LineA = LineB = null;
        return _set.Clear();
    }

    /// <summary>The raw snap under the cursor now: starts, keeps or ends a dwell.</summary>
    public void Observe(in Snap3DResult raw, long nowMs)
        => _set.Observe(IsPoint(raw.Kind) ? new Snap3DReference(raw.World, raw.Object) : null, nowMs);

    /// <summary>The clock advanced: a dwell completed, or a reference was forgotten. True when the references changed.</summary>
    public bool Tick(long nowMs) => _set.Tick(nowMs);

    /// <summary>
    /// The snap in force: <paramref name="raw"/> itself when it is a point or no line is near the cursor, else the tracked
    /// point (<see cref="Snap3DKind.Track"/>, from the reference's object). Sets <see cref="LineA"/>/<see cref="LineB"/>.
    /// </summary>
    public Snap3DResult Apply(in Snap3DResult raw, in Snap3DTrackView v, long nowMs)
    {
        LineA = LineB = null;
        var refs = _set.Items;
        if (refs.Count == 0 || IsPoint(raw.Kind)) return raw;
        var cam = v.Camera;
        float r2 = v.RadiusPixels * v.RadiusPixels;
        // A length to project the line's direction with: long enough to measure on screen, short enough to stay in front.
        double probe = Math.Max(1e-9, cam.Distance * 0.05);
        Span<(int Ref, int Axis, float D2)> near = stackalloc (int, int, float)[SnapReferences<Snap3DReference>.Max * 3];
        int n = 0;
        for (int i = 0; i < refs.Count; i++)
        {
            var p = refs[i].Point.World;
            var (x0, y0, vis0) = cam.Project(v.Scene.ToLocal(p.X, p.Y, p.Z), v.Width, v.Height);
            if (!vis0) continue;
            for (int a = 0; a < 3; a++)
            {
                var q = With(p, a, Get(p, a) + probe);
                var (x1, y1, vis1) = cam.Project(v.Scene.ToLocal(q.X, q.Y, q.Z), v.Width, v.Height);
                if (!vis1) continue;
                double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
                if (len2 < 16) continue;                                   // seen end-on: the line is a point on screen
                double cross = (v.CursorX - x0) * dy - (v.CursorY - y0) * dx;
                float d2 = (float)(cross * cross / len2);
                if (d2 <= r2) near[n++] = (i, a, d2);
            }
        }
        if (n == 0) return raw;

        // Two references' lines that cross, both near: their crossing.
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (near[i].Ref != near[j].Ref && near[i].Axis != near[j].Axis
                    && Crossing(refs[near[i].Ref].Point.World, near[i].Axis, refs[near[j].Ref].Point.World, near[j].Axis) is { } x)
                {
                    LineA = new Snap3DTrackLine(near[i].Ref, near[i].Axis);
                    LineB = new Snap3DTrackLine(near[j].Ref, near[j].Axis);
                    _set.Use(near[i].Ref, nowMs);
                    _set.Use(near[j].Ref, nowMs);
                    return Result(x, refs[near[i].Ref].Point.Object, v);
                }

        int best = 0;
        for (int i = 1; i < n; i++) if (near[i].D2 < near[best].D2) best = i;
        var (ri, axis, _) = near[best];
        var at = refs[ri].Point.World;
        Point3 point;
        if (raw.Kind == Snap3DKind.Edge) point = With(at, axis, Get(raw.World, axis));
        else if (AlongRay(at, axis, v.RayOrigin, v.RayDirection) is { } t) point = With(at, axis, Get(at, axis) + OnGrid(t, v.Grid));
        else return raw;
        LineA = new Snap3DTrackLine(ri, axis);
        _set.Use(ri, nowMs);
        return Result(point, refs[ri].Point.Object, v);
    }

    private static Snap3DResult Result(Point3 p, uint obj, in Snap3DTrackView v)
    {
        var (sx, sy, _) = v.Camera.Project(v.Scene.ToLocal(p.X, p.Y, p.Z), v.Width, v.Height);
        float dx = sx - v.CursorX, dy = sy - v.CursorY;
        return new Snap3DResult(Snap3DKind.Track, p, obj, -1, -1, sx, sy, 0, MathF.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>Where the line through <paramref name="a"/> along <paramref name="axisA"/> crosses the one through
    /// <paramref name="b"/> along <paramref name="axisB"/> (different axes): they meet only when the third coordinate agrees.</summary>
    internal static Point3? Crossing(Point3 a, int axisA, Point3 b, int axisB)
    {
        int c = 3 - axisA - axisB;
        double ca = Get(a, c), cb = Get(b, c);
        if (Math.Abs(ca - cb) > 1e-12 + 1e-9 * Math.Max(Math.Abs(ca), Math.Abs(cb))) return null;
        // On A's line every coordinate but axisA is A's; on B's every one but axisB is B's.
        var p = With(a, axisA, Get(b, axisA));
        return p;
    }

    /// <summary>The parameter (metres along +axis from <paramref name="p"/>) of the line's point nearest the ray; null when
    /// the ray runs along the line.</summary>
    internal static double? AlongRay(Point3 p, int axis, Point3 o, Point3 d)
    {
        // Line L(t) = p + t·e, ray R(s) = o + s·d. Minimise |L − R|².
        double b = Get(d, axis), c = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
        var w = new Point3(p.X - o.X, p.Y - o.Y, p.Z - o.Z);
        double dd = Get(w, axis), e = d.X * w.X + d.Y * w.Y + d.Z * w.Z;
        double denom = c - b * b;                                 // e·e = 1
        if (denom < 1e-12 * c) return null;
        return (b * e - c * dd) / denom;
    }

    /// <summary>A step along a line, on the grid when it is on (metres).</summary>
    private static double OnGrid(double t, Snap3DGrid g)
    {
        if (!g.IsOn) return t;
        double pitch = g.PitchDbu * 1e-6 / g.DbuPerMicron;
        return Math.Round(t / pitch, MidpointRounding.AwayFromZero) * pitch;
    }

    private static bool Same(Point3 a, Point3 b)
    {
        double tol = 1e-12 + 1e-10 * Math.Max(Math.Abs(a.X), Math.Max(Math.Abs(a.Y), Math.Abs(a.Z)));
        return Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Z - b.Z) <= tol;
    }

    internal static double Get(Point3 p, int a) => a switch { 0 => p.X, 1 => p.Y, _ => p.Z };

    internal static Point3 With(Point3 p, int a, double v) => a switch
    {
        0 => p with { X = v },
        1 => p with { Y = v },
        _ => p with { Z = v },
    };
}
