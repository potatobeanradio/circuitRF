// Settings ▸ General ▸ 3D View ▸ Use Hertzon interactivity — the selection RECTANGLE (a plain left drag in that scheme), the
// right-drag zoom, and Fit Selection (Ctrl/Cmd+E, in either scheme: it takes nothing from circuitRF's own). The rectangle takes what lies WHOLLY inside it, as Hertzon's does: in Object mode the
// objects Select All would take (shown, not ports, boundaries or air), each group only when every shown member is inside; in
// Face, Edge and Vertex mode those parts of the shown, selectable objects. A point the section plane cuts away is not
// tested, and an object it cuts away entirely is not taken.
//
// Nothing here draws: the rectangle is two corners the overlay strokes each frame, in the pane's DIPs.

using System.Numerics;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The selection rectangle being dragged, in the pane's DIPs (two opposite corners), or null.</summary>
    public (float X0, float Y0, float X1, float Y1)? Marquee { get; private set; }

    /// <summary>The rectangle follows the pointer: nothing is hovered while it does.</summary>
    public void SetMarquee(float x0, float y0, float x1, float y1)
    {
        Marquee = (x0, y0, x1, y1);
        SetCameraGesture(true);
        FrameRequested?.Invoke();
    }

    /// <summary>The rectangle goes, selecting nothing (Esc, a lost capture).</summary>
    public void ClearMarquee()
    {
        if (Marquee is null) return;
        Marquee = null;
        FrameRequested?.Invoke();
    }

    /// <summary>
    /// The rectangle was released: everything of the select mode wholly inside it becomes the selection, or with
    /// <paramref name="add"/> joins it. Returns how many items it took.
    /// </summary>
    public int BoxSelect(float x0, float y0, float x1, float y1, bool add)
    {
        Marquee = null;
        HitCycle.Reset();
        CycleText = "";
        var inside = ItemsInside(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1));
        if (add) SetSelection([.. View.Selection, .. inside]);
        else if (inside.Count == 0) ClearSelection();
        else SetSelection(inside);
        if (inside.Count > 0 && _items.TryGetValue(inside[0].Object, out var row)) RevealRequested?.Invoke(row);
        FrameRequested?.Invoke();
        return inside.Count;
    }

    /// <summary>What the rectangle (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>, <paramref name="y1"/>),
    /// DIPs with x0 ≤ x1 and y0 ≤ y1, holds wholly, in the current select mode.</summary>
    public List<Scene3DItem> ItemsInside(float x0, float y0, float x1, float y1)
    {
        var scene = Scene;
        var cam = View.Camera;
        var clip = View.Clip;
        float w = _viewW, h = _viewH;
        var visible = PickVisible;
        bool Shown(uint id) => id >= 1 && id <= visible.Length && visible[id - 1];

        // One point: null when the section plane removes it (it is not tested), else whether it projects inside.
        bool? In(Vector3 local)
        {
            if (!clip.Keeps(local)) return null;
            var (x, y, front) = cam.Project(local, w, h);
            return front && x >= x0 && x <= x1 && y >= y0 && y <= y1;
        }

        // A set of points is inside when every one kept is, and at least one is kept.
        bool AllIn(IEnumerable<Vector3> points)
        {
            bool any = false;
            foreach (var p in points)
            {
                if (In(p) is not { } at) continue;
                if (!at) return false;
                any = true;
            }
            return any;
        }

        var batches = new Dictionary<uint, List<Scene3DBatch>>();
        foreach (var b in scene.Batches)
        {
            if (!batches.TryGetValue(b.ObjectId, out var list)) batches[b.ObjectId] = list = [];
            list.Add(b);
        }

        // An object's drawn points: its triangles' corners, or its bounds' eight corners when it draws none of its own (a wire,
        // an array element drawn from its prototype).
        IEnumerable<Vector3> PointsOf(Scene3DObject o)
        {
            if (batches.TryGetValue(o.Id, out var own))
            {
                foreach (var b in own)
                    for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i++)
                    {
                        var v = scene.Vertices[scene.Indices[i]];
                        yield return new Vector3(v.X, v.Y, v.Z) + b.Offset;
                    }
                yield break;
            }
            var (mn, mx) = (o.Min, o.Max);
            for (int k = 0; k < 8; k++)
                yield return new Vector3((k & 1) == 0 ? mn.X : mx.X, (k & 2) == 0 ? mn.Y : mx.Y, (k & 4) == 0 ? mn.Z : mx.Z);
        }

        var result = new List<Scene3DItem>();
        switch (SelectMode)
        {
            case Scene3DSelectMode.Object:
            {
                var candidates = scene.Objects.Where(o => o.Selectable && !o.PickLast && Shown(o.Id)
                                                          && o.Kind is not (Scene3DKind.Port or Scene3DKind.Boundary or Scene3DKind.Air)).ToList();
                var inside = candidates.Where(o => AllIn(PointsOf(o))).Select(o => o.Id).ToHashSet();
                foreach (var id in candidates.Select(o => o.Id).Where(inside.Contains))
                {
                    var item = Scene3DItem.OfObject(id);
                    // A group is one unit: taken only when every shown member is inside.
                    var group = EditHost?.PickGroup(item) ?? [item];
                    if (group.All(g => !Shown(g.Object) || inside.Contains(g.Object))) result.Add(item);
                }
                break;
            }
            case Scene3DSelectMode.Face:
                foreach (var o in scene.Objects.Where(o => o.Selectable && !o.PickLast && Shown(o.Id)))
                {
                    if (!batches.TryGetValue(o.Id, out var own)) continue;
                    var faces = new Dictionary<int, List<Vector3>>();
                    foreach (var b in own)
                        for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i++)
                        {
                            var v = scene.Vertices[scene.Indices[i]];
                            if (v.Face == Scene3DVertex.NoFace) continue;
                            if (!faces.TryGetValue((int)v.Face, out var pts)) faces[(int)v.Face] = pts = [];
                            pts.Add(new Vector3(v.X, v.Y, v.Z) + b.Offset);
                        }
                    foreach (var (face, pts) in faces.OrderBy(f => f.Key))
                        if (AllIn(pts)) result.Add(Scene3DItem.OfFace(o.Id, face));
                }
                break;
            case Scene3DSelectMode.Edge:
                foreach (var o in scene.Objects.Where(o => o.Selectable && !o.PickLast && Shown(o.Id)))
                {
                    var fr = scene.FeaturesOf(o.Id);
                    if (fr.Table?.Named is not { } named) continue;
                    for (int e = 0; e < named.Edges.Length; e++)
                        if (AllIn(named.Edges[e].Points.Select(p => scene.ToLocal(p.X + fr.Dx, p.Y + fr.Dy, p.Z + fr.Dz))))
                            result.Add(Scene3DItem.OfEdge(o.Id, e));
                }
                break;
            case Scene3DSelectMode.Vertex:
                foreach (var o in scene.Objects.Where(o => o.Selectable && !o.PickLast && Shown(o.Id)))
                {
                    var fr = scene.FeaturesOf(o.Id);
                    if (fr.Table is not { } table) continue;
                    for (int v = 0; v < table.Vertices.Length; v++)
                    {
                        var p = fr.Vertex(v);
                        var local = scene.ToLocal(p.X, p.Y, p.Z);
                        if (In(local) == true) result.Add(Scene3DItem.OfVertex(o.Id, local));
                    }
                }
                break;
        }
        return result;
    }

    /// <summary>A zoom drag (Hertzon's right drag, Shift + middle, Alt + Shift + left) of <paramref name="dyPixels"/> DIPs
    /// down: up zooms in, about the view's centre. A drag over half the view's height zooms by about 2.6×.</summary>
    public void ZoomDrag(float dyPixels)
    {
        if (_viewH <= 0 || dyPixels == 0) return;
        float factor = MathF.Pow(1.1f, -20f * dyPixels / _viewH);        // the zoom wanted: > 1 in
        float notches = MathF.Log(factor) / MathF.Log(1f / Camera3D.ZoomPerNotch);
        Zoom(notches, _viewW * 0.5f, _viewH * 0.5f, _viewW, _viewH);
    }

    /// <summary>Ctrl/Cmd+E (Hertzon's key, in either scheme): frame the selected objects (the whole of each), the orientation kept;
    /// with nothing selected, everything shown, as Home does — a key that did nothing on an empty selection read as broken.</summary>
    public bool FitSelection()
    {
        var objects = View.Selection.Select(i => Scene.Object(i.Object)).OfType<Scene3DObject>().Distinct().ToList();
        if (objects.Count == 0)
        {
            FitCommand.Execute(null);
            return true;
        }
        var min = objects.Aggregate(new Vector3(float.MaxValue), (m, o) => Vector3.Min(m, o.Min));
        var max = objects.Aggregate(new Vector3(float.MinValue), (m, o) => Vector3.Max(m, o.Max));
        View.Camera.FrameBounds(min, max, _aspect);
        // FrameBounds takes the box it frames as the scene's sphere, and the depth range brackets that sphere: left so, everything
        // beyond the selection was clipped away once the view zoomed out. The whole scene's, as Fit restores it.
        View.Camera.SceneCentre = (Scene.BoundsMin + Scene.BoundsMax) * 0.5f;
        View.Camera.SceneRadius = (Scene.BoundsMax - Scene.BoundsMin).Length() * 0.5f;
        FrameRequested?.Invoke();
        return true;
    }
}
