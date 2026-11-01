// brief-em3d-44 R-em3d44-1 / -2b / -2c / -3c / -4 — the 3D snap: from a pick patch to one snapped point.
//
// NEAR AND VISIBLE, BY CONSTRUCTION. Candidates come only from the (object, face) pairs the patch holds —
// the faces within the snap radius of the cursor that won the depth test somewhere near it — so a hover
// examines a number of features set by the patch, never by the scene (gate 1). Each face's corners, edges,
// midpoints and centre are a slice of its object's feature table (Scene3DFeatureTable); an instance's
// element adds its offset to the few it examines (gate 2).
//
// VISIBLE (R-em3d44-2b). A feature is hidden when, at its pixel and at each of the eight around it, the
// patch holds something nearer than the feature (beyond a pixel's worth of depth) that is not one of the
// feature's own faces. A pixel holding one of its own faces, or nothing, shows it: an edge is shared by a
// face that faces the viewer and one that faces away, and a corner on a silhouette has background beside it.
//
// X-RAY. With the clip plane on, or with something translucent under the cursor, the objects the cursor's
// ray passes near (RayHits' hierarchy — what is along the line of sight, not the scene) that are drawn
// TRANSLUCENT contribute every feature, hidden or not: a dielectric's far corners are seen through it.
//
// PRIORITY (R-em3d44-2c, layout's order with the face centre added): vertex; then midpoint and face centre;
// then the nearest point on an edge; then the grid — which applies only when no feature is in range
// (R-snpf-3). Within a tier the nearest on screen wins, and a tie goes to the smaller depth.
//
// WHAT IS MOVED NEVER ATTRACTS ITSELF (R-snpf-4): an excluded object gives nothing; an excluded face gives
// none of its corners, edges or centre, through whichever face they are reached; an excluded POINT (a
// dragged corner's old position, still in a scene that has not caught up) gives nothing that lies on it.
//
// ZERO ALLOCATIONS per query in steady state (gate 7): every set and list is the query's own and reused.
//
// brief-em3d-67 R-em3d67-3e — A KERNEL SOLID SNAPS TO ITS CURVES, NOT ITS TRIANGLES. Its vertices are the B-rep's (a
// curved edge's polyline points move with the display deflection and are none, nor is a circle's seam); an open edge's midpoint is the one along
// the curve the worker gave, and a closed one has none; the nearest point on an edge runs along the polyline and is
// APPROXIMATE (≈) unless it lands on a vertex; and a circle's or an arc's centre is a target — how a pin is put exactly
// on a bore's axis. Every managed object snaps exactly as before: this path reads only a kernel object's edge table.
//
// brief-em3d-67 R-em3d67-3a — EDGE MODE'S HOVER IS THIS QUERY'S EDGE TIER (NearestEdge): the named edges of the (object,
// face) pairs in the patch, the nearest visible one on screen within the radius — no new GPU channel, and a count of
// edges examined that the patch bounds, never the scene.

using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>What a snap landed on.</summary>
public enum Snap3DKind { None, Vertex, Midpoint, FaceCentre, Edge, Grid, Centre }

/// <summary>The kinds a user has switched on (R-em3d44-5's toggles).</summary>
[Flags]
public enum Snap3DKinds
{
    None = 0, Vertex = 1, Midpoint = 2, Edge = 4, FaceCentre = 8, Grid = 16,
    All = Vertex | Midpoint | Edge | FaceCentre | Grid,
}

/// <summary>The drawing plane's grid (brief 45 owns the plane; the snap only reads it): integer DBU, so a
/// grid point is an exact DBU point.</summary>
public readonly record struct Snap3DGrid(C3dPlane Plane, long OffsetDbu, long PitchDbu, int DbuPerMicron)
{
    public bool IsOn => PitchDbu > 0 && DbuPerMicron > 0;
}

/// <summary>What one query is asked. Radius in the patch's own (viewport) pixels.</summary>
public readonly record struct Snap3DSettings(Snap3DKinds Kinds, float RadiusPixels, bool GeometrySuspended = false,
                                             Snap3DGrid Grid = default)
{
    public bool Wants(Snap3DKinds k) => (Kinds & k) != 0;
}

/// <summary>
/// One snapped point (R-em3d44-4): world metres, the kind, what it came from (object, face, table index)
/// and where it is on screen in DIPs. <see cref="Kind"/> None is no snap.
/// </summary>
/// brief-em3d-67 — <see cref="Approximate"/>: a point on a curved edge's polyline, within the display deflection of the
/// curve and never an exact point (the status line's <c>≈</c>).
public readonly record struct Snap3DResult(Snap3DKind Kind, Point3 World, uint Object, int Face, int Index,
                                           float ScreenX, float ScreenY, float Depth, float Distance, bool Approximate = false)
{
    public bool IsSnap => Kind != Snap3DKind.None;
}

/// <summary>Plain counters, asserted directly (gates 1 and 2).</summary>
public struct Snap3DCounters
{
    /// <summary>Distinct (object, face) pairs the patch held.</summary>
    public int FacesInPatch;
    /// <summary>Corners, midpoints, edges and centres looked at.</summary>
    public int FeaturesExamined;
    /// <summary>Instance elements whose shared table was offset — only those in the patch.</summary>
    public int ElementsTransformed;
    /// <summary>Translucent objects X-ray added from along the line of sight.</summary>
    public int XRayObjects;
    /// <summary>brief-em3d-67 — named edges Edge mode's hover looked at.</summary>
    public int EdgesExamined;
}

/// <summary>brief-em3d-67 R-em3d67-3a — Edge mode's hover: the nearest visible named edge under the cursor.</summary>
public readonly record struct Edge3DHit(uint Object, int Edge, int Face, Point3 World, float Distance, float Depth);

/// <summary>R-snpf-4 — what the gesture in progress is moving. World metres for points.</summary>
public sealed class Snap3DExclusion
{
    public HashSet<uint> Objects { get; } = [];
    public HashSet<(uint Object, int Face)> Faces { get; } = [];
    public List<Point3> Points { get; } = [];

    public bool IsEmpty => Objects.Count == 0 && Faces.Count == 0 && Points.Count == 0;

    public bool Excludes(Point3 p)
    {
        foreach (var q in Points)
        {
            double tol = 1e-12 + 1e-10 * Math.Max(Math.Abs(q.X), Math.Max(Math.Abs(q.Y), Math.Abs(q.Z)));
            if (Math.Abs(p.X - q.X) <= tol && Math.Abs(p.Y - q.Y) <= tol && Math.Abs(p.Z - q.Z) <= tol) return true;
        }
        return false;
    }
}

public sealed class SnapQuery3D
{
    private readonly HashSet<ulong> _seen = [];
    private readonly List<ulong> _pairs = [];
    private readonly HashSet<uint> _transformed = [];
    private readonly HashSet<uint> _xrayObjects = [];
    private readonly List<int> _near = [];
    private readonly Stack<int> _stack = new();

    /// <summary>Screen distances this close are equal, and the smaller depth breaks the tie.</summary>
    public const float TiePixels = 0.05f;

    /// <summary>The last query's counters.</summary>
    public Snap3DCounters Counters;

    // One query's state, so the helpers take no long parameter lists (and capture nothing).
    private Scene3DModel _scene = Scene3DModel.Empty();
    private Scene3DIdPatch _patch = null!;
    private Snap3DExclusion? _ex;
    private ClipPlane3D _clip;
    private Matrix4x4 _vp;
    private Camera3D _cam;
    private Vector3 _rayO, _rayD;
    private float _r2, _dip;
    private Snap3DSettings _s;
    private Snap3DResult _best;
    private int _bestTier;

    /// <summary>
    /// The snap for the cursor the patch was read at. <paramref name="visible"/> is the view's visibility
    /// (empty: everything), <paramref name="clip"/> its clip plane.
    /// </summary>
    public Snap3DResult Query(Scene3DModel scene, Scene3DIdPatch patch, in Snap3DSettings settings, Snap3DExclusion? exclusion,
                              ReadOnlySpan<bool> visible, in ClipPlane3D clip)
    {
        Counters = default;
        _best = default;
        _bestTier = int.MaxValue;
        if (!patch.Valid || settings.Kinds == Snap3DKinds.None) return default;
        _scene = scene; _patch = patch; _ex = exclusion is { IsEmpty: false } ? exclusion : null; _clip = clip; _s = settings;
        _cam = patch.Camera;
        _vp = _cam.ViewProjectionMatrix(patch.Width, patch.Height);
        (_rayO, _rayD) = _cam.Ray(patch.CursorX - 0.5f, patch.CursorY - 0.5f, patch.Width, patch.Height);
        _r2 = settings.RadiusPixels * settings.RadiusPixels;
        _dip = patch.PixelsPerDip;

        bool geometry = !settings.GeometrySuspended
                        && settings.Wants(Snap3DKinds.Vertex | Snap3DKinds.Midpoint | Snap3DKinds.Edge | Snap3DKinds.FaceCentre)
                        && patch.Generation == scene.Generation;
        if (geometry) Geometry(visible);
        if (!_best.IsSnap && settings.Wants(Snap3DKinds.Grid) && settings.Grid.IsOn) GridPoint(settings.Grid);
        _patch = null!;
        return _best;
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    private void Geometry(ReadOnlySpan<bool> visible)
    {
        _seen.Clear(); _pairs.Clear(); _transformed.Clear(); _xrayObjects.Clear();
        int n = _patch.Size * _patch.Size;
        bool translucentSeen = false;
        for (int k = 0; k < n; k++)
        {
            uint id = _patch.Ids[k];
            if (id == 0) continue;
            ulong key = ((ulong)id << 32) | FaceOf(id, _patch.Faces[k]);
            if (_seen.Add(key)) _pairs.Add(key);
            if (_scene.Object(id) is { Translucent: true }) translucentSeen = true;
        }
        Counters.FacesInPatch = _pairs.Count;

        // X-ray: translucent objects along the line of sight give all their faces.
        if (_clip.Enabled || translucentSeen)
        {
            RayHits.BatchesNearRay(_scene, _rayO, _rayD, _cam, _patch.Height, _s.RadiusPixels, _near, _stack);
            foreach (int b in _near)
            {
                uint id = _scene.Batches[b].ObjectId;
                var o = _scene.Objects[id - 1];
                if (!o.Translucent || !o.Pickable || !IsVisible(visible, id) || !_xrayObjects.Add(id)) continue;
                Counters.XRayObjects++;
                var fr = _scene.FeaturesOf(id);
                if (fr.Table is null) continue;
                for (int f = 0; f < fr.Table.FaceCount; f++)
                {
                    ulong key = ((ulong)id << 32) | (uint)f;
                    if (_seen.Add(key)) _pairs.Add(key);
                }
            }
        }

        foreach (ulong key in _pairs)
        {
            uint id = (uint)(key >> 32);
            int face = (int)(uint)key;
            if (_ex is not null && _ex.Objects.Contains(id)) continue;
            var fr = _scene.FeaturesOf(id);
            if (fr.Table is not { } t || face < 0 || face >= t.FaceCount) continue;
            if (fr.Shared && _transformed.Add(id)) Counters.ElementsTransformed++;
            bool xray = _xrayObjects.Contains(id);

            if (t.NamedFromKernel) KernelFeatures(id, face, fr, t, xray);
            else
            {
            if (_s.Wants(Snap3DKinds.Vertex))
                for (int k = t.FaceVertexStart[face]; k < t.FaceVertexStart[face + 1]; k++)
                {
                    int v = t.FaceVertices[k];
                    Counters.FeaturesExamined++;
                    if (ExcludedVertex(id, t, v)) continue;
                    Consider(Snap3DKind.Vertex, 1, fr.Vertex(v), id, face, v, t, Snap3DKind.Vertex, xray);
                }
            bool mid = _s.Wants(Snap3DKinds.Midpoint), edge = _s.Wants(Snap3DKinds.Edge);
            if (mid || edge)
                for (int k = t.FaceEdgeStart[face]; k < t.FaceEdgeStart[face + 1]; k++)
                {
                    int e = t.FaceEdges[k];
                    if (_ex is not null && (_ex.Faces.Contains((id, t.EdgeFace0[e])) || _ex.Faces.Contains((id, t.EdgeFace1[e])))) continue;
                    var a = fr.Vertex(t.EdgeA[e]);
                    var b = fr.Vertex(t.EdgeB[e]);
                    if (mid)
                    {
                        Counters.FeaturesExamined++;
                        var m = new Point3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
                        if (_ex is null || !_ex.Excludes(m)) Consider(Snap3DKind.Midpoint, 2, m, id, face, e, t, Snap3DKind.Edge, xray);
                    }
                    if (edge && _bestTier >= 3)
                    {
                        Counters.FeaturesExamined++;
                        if (NearestOnEdge(a, b) is { } p) Consider(Snap3DKind.Edge, 3, p, id, face, e, t, Snap3DKind.Edge, xray);
                    }
                }
            }
            if (_s.Wants(Snap3DKinds.FaceCentre) && t.HasCentre[face])
            {
                Counters.FeaturesExamined++;
                if (_ex is null || !_ex.Faces.Contains((id, face)))
                {
                    var c = fr.Centre(face);
                    if (_ex is null || !_ex.Excludes(c)) Consider(Snap3DKind.FaceCentre, 2, c, id, face, face, t, Snap3DKind.FaceCentre, xray);
                }
            }
        }
    }

    /// <summary>
    /// brief-em3d-67 R-em3d67-3e — a kernel solid's features on one face, from its named edges: the B-rep's vertices, each
    /// open edge's midpoint along the curve, a circle's or an arc's centre, and the nearest point along the polyline
    /// (approximate unless it is a vertex).
    /// </summary>
    private void KernelFeatures(uint id, int face, Scene3DFeatureRef fr, Scene3DFeatureTable t, bool xray)
    {
        var named = t.Named;
        if (face >= named.FaceCount) return;
        bool vtx = _s.Wants(Snap3DKinds.Vertex), mid = _s.Wants(Snap3DKinds.Midpoint), edge = _s.Wants(Snap3DKinds.Edge),
             centre = _s.Wants(Snap3DKinds.FaceCentre);
        for (int k = named.FaceEdgeStart[face]; k < named.FaceEdgeStart[face + 1]; k++)
        {
            int e = named.FaceEdges[k];
            var ed = named.Edges[e];
            if (_ex is not null && (_ex.Faces.Contains((id, ed.Face0)) || _ex.Faces.Contains((id, ed.Face1)))) continue;
            // A closed edge's one vertex is where the kernel happened to start the circle — no point a user aims for — and it
            // would win over the circle's centre, whose tier is lower (src/Render/RESOLVED.md). An open edge ending there offers it.
            if (vtx && !ed.Closed)
                for (int end = 0; end < 2; end++)
                {
                    Counters.FeaturesExamined++;
                    var v = Off(fr, named.Vertices[end == 0 ? ed.StartVertex : ed.EndVertex]);
                    if (_ex is null || !_ex.Excludes(v)) Consider(Snap3DKind.Vertex, 1, v, id, face, e, t, Snap3DKind.Edge, xray, ed.Face0, ed.Face1);
                }
            if (mid && ed.Mid is { } m)
            {
                Counters.FeaturesExamined++;
                var w = Off(fr, m);
                if (_ex is null || !_ex.Excludes(w)) Consider(Snap3DKind.Midpoint, 2, w, id, face, e, t, Snap3DKind.Edge, xray, ed.Face0, ed.Face1);
            }
            if (centre && ed.Centre is { } c)
            {
                Counters.FeaturesExamined++;
                var w = Off(fr, c);
                if (_ex is null || !_ex.Excludes(w)) Consider(Snap3DKind.Centre, 2, w, id, face, e, t, Snap3DKind.Edge, xray, ed.Face0, ed.Face1);
            }
            if (edge && _bestTier >= 3)
                for (int i = 1; i < ed.Points.Length; i++)
                {
                    Counters.FeaturesExamined++;
                    var a = Off(fr, ed.Points[i - 1]);
                    var b = Off(fr, ed.Points[i]);
                    if (NearestOnEdge(a, b, out float s) is not { } p) continue;
                    // On a vertex only at the run's own ends; anywhere else it is on a chord of the curve.
                    bool vertex = (i == 1 && s <= 0) || (i == ed.Points.Length - 1 && s >= 1);
                    bool approx = !vertex && ed.Kind != Scene3DEdgeKind.Line;
                    Consider(Snap3DKind.Edge, 3, p, id, face, e, t, Snap3DKind.Edge, xray, ed.Face0, ed.Face1, approx);
                }
        }
    }

    private static Point3 Off(Scene3DFeatureRef fr, Point3 p) => new(p.X + fr.Dx, p.Y + fr.Dy, p.Z + fr.Dz);

    /// <summary>
    /// brief-em3d-67 R-em3d67-3a — Edge mode's hover: of the named edges on the (object, face) pairs the patch holds, the
    /// nearest on screen within <paramref name="radiusPixels"/> that is visible there (R-em3d44-2b's test), a tie going to
    /// the nearer. <paramref name="selectable"/> says which objects a click may select. Null when there is none.
    /// </summary>
    public Edge3DHit? NearestEdge(Scene3DModel scene, Scene3DIdPatch patch, float radiusPixels, ReadOnlySpan<bool> visible,
                                  in ClipPlane3D clip, Func<uint, bool>? selectable = null)
    {
        Counters = default;
        if (!patch.Valid || patch.Generation != scene.Generation) return null;
        _scene = scene; _patch = patch; _clip = clip; _ex = null;
        _cam = patch.Camera;
        _vp = _cam.ViewProjectionMatrix(patch.Width, patch.Height);
        (_rayO, _rayD) = _cam.Ray(patch.CursorX - 0.5f, patch.CursorY - 0.5f, patch.Width, patch.Height);
        _r2 = radiusPixels * radiusPixels;
        _dip = patch.PixelsPerDip;
        _seen.Clear(); _pairs.Clear();
        int n = patch.Size * patch.Size;
        for (int k = 0; k < n; k++)
        {
            uint id = patch.Ids[k];
            if (id == 0) continue;
            ulong key = ((ulong)id << 32) | patch.Faces[k];
            if (_seen.Add(key)) _pairs.Add(key);
        }
        Counters.FacesInPatch = _pairs.Count;
        Edge3DHit? best = null;
        _edgesSeen.Clear();
        foreach (ulong key in _pairs)
        {
            uint id = (uint)(key >> 32);
            int face = (int)(uint)key;
            if (!IsVisible(visible, id) || selectable?.Invoke(id) == false) continue;
            var fr = scene.FeaturesOf(id);
            if (fr.Table is not { } t || face < 0 || face >= t.Named.FaceCount) continue;
            var named = t.Named;
            for (int k = named.FaceEdgeStart[face]; k < named.FaceEdgeStart[face + 1]; k++)
            {
                int e = named.FaceEdges[k];
                if (!_edgesSeen.Add(((ulong)id << 32) | (uint)e)) continue;
                Counters.EdgesExamined++;
                var ed = named.Edges[e];
                for (int i = 1; i < ed.Points.Length; i++)
                {
                    if (NearestOnEdge(Off(fr, ed.Points[i - 1]), Off(fr, ed.Points[i]), out _) is not { } p) continue;
                    var local = scene.ToLocal(p.X, p.Y, p.Z);
                    if (!clip.Keeps(local)) continue;
                    var c = Vector4.Transform(new Vector4(local, 1), _vp);
                    if (c.W <= 0) continue;
                    float x = (c.X / c.W + 1) * 0.5f * patch.Width, y = (1 - c.Y / c.W) * 0.5f * patch.Height;
                    float dx = x - patch.CursorX, dy = y - patch.CursorY, d2 = dx * dx + dy * dy;
                    if (d2 > _r2) continue;
                    float d = MathF.Sqrt(d2), depth = _cam.ViewDepth(local);
                    if (best is { } b && (d > b.Distance + TiePixels || (d >= b.Distance - TiePixels && depth >= b.Depth))) continue;
                    if (!VisibleOn(x, y, depth, id, ed.Face0, ed.Face1)) continue;
                    best = new Edge3DHit(id, e, face, p, d, depth);
                }
            }
        }
        _patch = null!;
        return best;
    }

    private readonly HashSet<ulong> _edgesSeen = [];

    private bool ExcludedVertex(uint id, Scene3DFeatureTable t, int v)
    {
        if (_ex is null) return false;
        foreach (var (o, f) in _ex.Faces)
            if (o == id && f >= 0 && f < t.FaceCount && t.VertexOnFace(v, f)) return true;
        return _ex.Points.Count > 0 && _ex.Excludes(_scene.FeaturesOf(id).Vertex(v));
    }

    /// <summary>
    /// A candidate: kept when it is on screen within the radius, visible (or X-rayed), and better than the best
    /// so far by tier, then distance, then depth. <paramref name="on"/> says which of the table's faces the
    /// feature lies on — a corner's, an edge's two, or a centre's one — for the visibility test.
    /// </summary>
    private void Consider(Snap3DKind kind, int tier, Point3 world, uint id, int face, int index, Scene3DFeatureTable t,
                          Snap3DKind on, bool xray, int ownA = -2, int ownB = -2, bool approximate = false)
    {
        if (tier > _bestTier) return;
        var local = _scene.ToLocal(world.X, world.Y, world.Z);
        if (!_clip.Keeps(local)) return;
        var c = Vector4.Transform(new Vector4(local, 1), _vp);
        if (c.W <= 0) return;
        float x = (c.X / c.W + 1) * 0.5f * _patch.Width, y = (1 - c.Y / c.W) * 0.5f * _patch.Height;
        float dx = x - _patch.CursorX, dy = y - _patch.CursorY, d2 = dx * dx + dy * dy;
        if (d2 > _r2) return;
        float depth = _cam.ViewDepth(local);
        float d = MathF.Sqrt(d2);
        // Two corners one behind the other land on one screen point, a rounding apart: that is a tie, and a
        // tie goes to the nearer (R-em3d44-2c).
        if (tier == _bestTier && (d > _best.Distance + TiePixels || (d >= _best.Distance - TiePixels && depth >= _best.Depth))) return;
        bool shown = (xray && _scene.Objects[id - 1].Translucent)
                     || (ownA != -2 ? VisibleOn(x, y, depth, id, ownA, ownB) : Visible(x, y, depth, id, index, t, on));
        if (!shown) return;
        _bestTier = tier;
        _best = new Snap3DResult(kind, world, id, face, index, x / _dip, y / _dip, depth, d, approximate);
    }

    /// <summary>R-em3d44-2b — at the feature's pixel or one beside it: nothing, one of its own faces, or
    /// something no nearer than it (within a pixel's worth of depth).</summary>
    private bool Visible(float x, float y, float depth, uint id, int index, Scene3DFeatureTable t, Snap3DKind on)
    {
        int px = (int)MathF.Floor(x), py = (int)MathF.Floor(y);
        float tol = 1e-4f * MathF.Abs(depth) + 1.5f * PixelWorld(depth);
        bool any = false;
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int k = _patch.IndexOf(px + i, py + j);
                if (k < 0) continue;
                any = true;
                uint pid = _patch.Ids[k];
                if (pid == 0 || depth <= _patch.Depth[k] + tol) return true;
                if (pid != id) continue;
                int f = (int)FaceOf(id, _patch.Faces[k]);
                bool own = on switch
                {
                    Snap3DKind.Vertex => f >= 0 && f < t.FaceCount && t.VertexOnFace(index, f),
                    Snap3DKind.Edge   => f == t.EdgeFace0[index] || f == t.EdgeFace1[index],
                    _                 => f == index,
                };
                if (own) return true;
            }
        return !any;
    }

    /// <summary>brief-em3d-67 — <see cref="Visible"/> for a feature on faces <paramref name="f0"/> and <paramref name="f1"/>
    /// of object <paramref name="id"/> (a named edge's two, −1 for a rim's missing one).</summary>
    private bool VisibleOn(float x, float y, float depth, uint id, int f0, int f1)
    {
        int px = (int)MathF.Floor(x), py = (int)MathF.Floor(y);
        float tol = 1e-4f * MathF.Abs(depth) + 1.5f * PixelWorld(depth);
        bool any = false;
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int k = _patch.IndexOf(px + i, py + j);
                if (k < 0) continue;
                any = true;
                uint pid = _patch.Ids[k];
                if (pid == 0 || depth <= _patch.Depth[k] + tol) return true;
                if (pid == id && ((int)_patch.Faces[k] == f0 || (f1 >= 0 && (int)_patch.Faces[k] == f1))) return true;
            }
        return !any;
    }

    /// <summary>A pixel's world size at view depth <paramref name="depth"/>.</summary>
    private float PixelWorld(float depth)
        => _cam.Projection == Projection3D.Orthographic
            ? _cam.WorldPerPixel(_patch.Height)
            : 2f * MathF.Abs(depth) * MathF.Tan(_cam.FovY * 0.5f) / MathF.Max(1f, _patch.Height);

    /// <summary>The point of segment a–b nearest the cursor's ray (world metres), or null for a degenerate edge.</summary>
    private Point3? NearestOnEdge(Point3 a, Point3 b) => NearestOnEdge(a, b, out _);

    private Point3? NearestOnEdge(Point3 a, Point3 b, out float s)
    {
        s = 0;
        // In scene-local single precision the geometry is fine for FINDING the parameter; the point returned
        // is re-made in doubles from it.
        var la = _scene.ToLocal(a.X, a.Y, a.Z);
        var lb = _scene.ToLocal(b.X, b.Y, b.Z);
        var u = lb - la;
        float uu = Vector3.Dot(u, u);
        if (uu <= 0) return null;
        var w = la - _rayO;
        float ud = Vector3.Dot(u, _rayD), dd = Vector3.Dot(_rayD, _rayD);
        float denom = uu * dd - ud * ud;
        // Parallel to the ray: every point is equally near; the nearer end is as good as any.
        s = denom <= 1e-12f * uu * dd ? 0 : Math.Clamp((ud * Vector3.Dot(_rayD, w) - dd * Vector3.Dot(u, w)) / denom, 0, 1);
        return new Point3(a.X + (b.X - a.X) * s, a.Y + (b.Y - a.Y) * s, a.Z + (b.Z - a.Z) * s);
    }

    // ── the grid (R-em3d44-3c) ──────────────────────────────────────────────────────────────

    private void GridPoint(Snap3DGrid g)
    {
        // The ray in world metres, from the scene's origin and its local direction.
        var (ox, oy, oz) = _scene.ToWorld(_rayO);
        double dx = _rayD.X, dy = _rayD.Y, dz = _rayD.Z;
        double mPerDbu = C3dLowering.Metres(1, g.DbuPerMicron);
        double offset = C3dLowering.Metres(g.OffsetDbu, g.DbuPerMicron);
        (double o, double d) = g.Plane switch { C3dPlane.YZ => (ox, dx), C3dPlane.XZ => (oy, dy), _ => (oz, dz) };
        if (Math.Abs(d) < 1e-9) return;
        double t = (offset - o) / d;
        if (t < 0) return;
        double hx = ox + dx * t, hy = oy + dy * t, hz = oz + dz * t;
        long Round(double m) => (long)Math.Round(m / mPerDbu / g.PitchDbu, MidpointRounding.AwayFromZero) * g.PitchDbu;
        long X = Round(hx), Y = Round(hy), Z = Round(hz);
        (X, Y, Z) = g.Plane switch { C3dPlane.YZ => (g.OffsetDbu, Y, Z), C3dPlane.XZ => (X, g.OffsetDbu, Z), _ => (X, Y, g.OffsetDbu) };
        var world = new Point3(C3dLowering.Metres(X, g.DbuPerMicron), C3dLowering.Metres(Y, g.DbuPerMicron), C3dLowering.Metres(Z, g.DbuPerMicron));
        var local = _scene.ToLocal(world.X, world.Y, world.Z);
        var c = Vector4.Transform(new Vector4(local, 1), _vp);
        if (c.W <= 0) return;
        float x = (c.X / c.W + 1) * 0.5f * _patch.Width, y = (1 - c.Y / c.W) * 0.5f * _patch.Height;
        float ddx = x - _patch.CursorX, ddy = y - _patch.CursorY;
        _best = new Snap3DResult(Snap3DKind.Grid, world, 0, -1, -1, x / _dip, y / _dip, _cam.ViewDepth(local), MathF.Sqrt(ddx * ddx + ddy * ddy));
    }

    /// <summary>
    /// The table face a patch pixel of object <paramref name="id"/> is on. An object drawn with no faces in the ID pass
    /// (<see cref="Scene3DVertex.NoFace"/> — a port) but given a one-face table, as a sheet's, is on that face; every other
    /// pixel's face is its own.
    /// </summary>
    private uint FaceOf(uint id, uint face)
        => face == Scene3DVertex.NoFace && _scene.FeaturesOf(id).Table is { FaceCount: 1 } ? 0u : face;

    private static bool IsVisible(ReadOnlySpan<bool> visible, uint id)
        => visible.IsEmpty || (id - 1 < (uint)visible.Length && visible[(int)id - 1]);
}
