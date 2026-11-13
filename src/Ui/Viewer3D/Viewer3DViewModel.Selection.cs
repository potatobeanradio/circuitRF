// brief-em3d-43 R-em3d43-2 / -3 / -4 / -5 — selection in EVERY 3D pane: the three modes, hover, click and
// Shift-click, B through what is behind, the keys, and the context menu's frame.
//
// ONE PANE, TWO USES (R-em3d43-1a). The read-only viewer a .cem opens and the .c3d editor are the same
// view model and the same pane; the editor is this plus a document, reached through IViewer3DEditHost.
// With no host, selecting is for measuring and reading: the status line names the face's area and normal,
// and every command that would write something is absent from the menu, not disabled.
//
// ALL OF IT IS INPUT-LOOP WORK (em-3d.md §8.4): a hover sets two uniforms, a click writes a selection list
// the shader reads, and nothing here tessellates, elaborates or uploads (gate 5). The one CPU geometry query
// is B's ray (RayHits), on the key press, never on a hover, over a hierarchy built once per scene; and
// Vertex mode's hover reads the vertices of the ONE face under the cursor (Scene3DFaces).
//
// FACE BOUNDARIES ARE IN THE CYCLE (brief-em3d-90). A boundary's tint — an EM face boundary's, or a thermal setup's — is
// not in the ID pass (the snap reads that pass, and must land on the face beneath), so hover asks the CPU about the few
// tints there are (TintUnderCursor), and RayHits lists them in Object and Face mode. At the face a tint lies on, the tint
// ranks FIRST, by kind: a click selects the boundary, B steps to the solid (or the face) under it, Shift+B steps back.
//
// SELECTION SURVIVES A REGENERATION BY NAME. An edit rebuilds the scene and may renumber its objects (a
// delete shifts every later ID), so the selection is carried across by object name and face index, and
// what no longer exists drops out of it.

using System.Globalization;
using System.Numerics;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>What the editor adds to a 3D pane (null in the read-only viewer).</summary>
public interface IViewer3DEditHost
{
    /// <summary>The document kind of a scene object — <c>Box</c>, <c>Prism</c> … — or the instance it came
    /// from; null when the object is not the document's (never, in practice).</summary>
    string KindOf(Scene3DObject o);

    /// <summary>The instance path of an object inside an instance, or null for the document's own.</summary>
    string? InstanceOf(Scene3DObject o);

    /// <summary>The materials a drawn object may be given (the document's technology's).</summary>
    IReadOnlyList<string> Materials { get; }

    /// <summary>Hides <paramref name="objects"/> (<paramref name="hidden"/> true) or shows them, as ONE undoable
    /// edit of the document's <c>Hidden</c> flags. False when none of them is the document's to hide.</summary>
    bool SetHidden(IReadOnlyList<Scene3DObject> objects, bool hidden, string description);

    /// <summary>Shows every document object, and hides all but <paramref name="keep"/> when it is given.</summary>
    bool ShowAll(IReadOnlyList<Scene3DObject>? keep);

    void SetMaterial(IReadOnlyList<Scene3DObject> objects, string material);

    /// <summary>brief-em3d-53 — Assign Material…: the picker over the technology's materials, for these objects.</summary>
    void AssignMaterial(IReadOnlyList<Scene3DObject> objects) { }

    /// <summary>brief-em3d-53 — Material ▸ New Material…: the picker on a new row, for these objects.</summary>
    void NewMaterial(IReadOnlyList<Scene3DObject> objects) { }

    /// <summary>Deletes the selection's document objects: one undo entry. False when nothing was deletable.</summary>
    bool DeleteSelection();

    /// <summary>The Properties panel, shown (and its Name field focused when <paramref name="rename"/>).</summary>
    void ShowProperties(bool rename);

    /// <summary>brief-em3d-44 R-em3d44-4 / -5 — a snapped point as the status line shows it, in the document's
    /// display unit with <c>≈</c> when it is not an exact DBU point; null leaves it to the pane.</summary>
    string? SnapPointText(Snap3DResult snap) => null;

    /// <summary>brief-em3d-45 R-em3d45-1b / -3 — a click the editor's drawing takes: a tool is armed, or it is a
    /// drawing-plane gesture (Ctrl/Cmd-click a face, Ctrl/Cmd+Shift-click a snapped point). False leaves it to
    /// selection. <paramref name="clickCount"/> 2 is the second click of a double-click.</summary>
    bool DrawClick(KeyModifiers modifiers, int clickCount) => false;

    /// <summary>3D editor groups — what a click on <paramref name="item"/> selects: the item, or (Object mode, in the editor)
    /// every member of the top-most group it is in, the item first.</summary>
    IReadOnlyList<Scene3DItem> PickGroup(Scene3DItem item) => [item];

    /// <summary>brief-em3d-45 — a key the drawing takes before the pane's own keys: Shift+A, and while a tool is armed
    /// its Esc, Enter, Tab and the digits that open the typed field.</summary>
    bool DrawKey(Key key, KeyModifiers modifiers) => false;

    /// <summary>brief-em3d-45 — what the drawing adds to the 2D overlay this frame: the rubber band, the document's
    /// construction polylines, a refused outline's crossing.</summary>
    void FillDrawOverlay(Viewer3DDrawOverlay overlay) { }

    /// <summary>brief-em3d-45 — the drawing's entries for the context menu (Extrude, Drawing Plane from Face).</summary>
    IEnumerable<Viewer3DMenuItem> DrawMenuItems() => [];

    /// <summary>brief-hzn-12 — the menu of a scattering setup's incident wave, when its arrows are what is selected.</summary>
    IReadOnlyList<Viewer3DMenuItem> ExcitationMenuItems() => [];

    /// <summary>brief-em3d-46 R-em3d46-5 — where the move gizmo sits (world metres), or null for none: it is offered
    /// in Object mode with something movable selected and no gesture in progress.</summary>
    CircuitRF.Engine.Em3d.Point3? GizmoPivot => null;

    /// <summary>A press on gizmo handle <paramref name="handle"/>: starts the constrained move. False when it did not.</summary>
    bool GizmoDrag(GizmoHandle handle) => false;

    /// <summary>The gizmo drag's release: the move commits.</summary>
    void GizmoRelease() { }

    /// <summary>The gizmo drag was lost: the move is cancelled.</summary>
    void GizmoCancel() { }

    /// <summary>Vertex mode — a plain press on <paramref name="vertex"/> (the hovered one) that the editor can drag: it is
    /// selected and its move starts. False leaves the press to the pane (a click, an orbit).</summary>
    bool PointDrag(Scene3DItem vertex) => false;

    /// <summary>The point drag's release: <paramref name="moved"/> past the click slop commits where the cursor is; a press
    /// that stayed put was a click, which selected the vertex and moves nothing.</summary>
    void PointDragRelease(bool moved) { }

    /// <summary>The point drag was lost: the move is cancelled.</summary>
    void PointDragCancel() { }

    /// <summary>brief-em3d-46 R-em3d46-6a — the point a measurement click takes: the snap, as the document's exact
    /// point where it is one; else the drawing plane under the cursor. Null leaves it to the pane.</summary>
    Viewer3DMeasurePoint? MeasurePoint() => null;

    /// <summary>A measurement started: the editor disarms its tool (one gesture at a time).</summary>
    void MeasureStarted() { }

    /// <summary>3D round 1 — a drawing tool or an operation is armed: a plain left drag must not orbit (only
    /// Ctrl/Cmd + drag does), so a press that wobbles past the click slop still places its point.</summary>
    bool DrawArmed => false;

    /// <summary>3D round 3 — a Project Tree drag is over the pane carrying <paramref name="text"/>: true when a drop
    /// would place something (the placement's outline then follows the cursor). A refusal is said on the status line.</summary>
    bool TreeDragOver(string text) => false;

    /// <summary>3D round 3 — the drag was dropped at the cursor: true when it was ours.</summary>
    bool TreeDrop(string text) => false;

    /// <summary>3D round 3 — the drag left the pane without a drop: a placement it armed is disarmed.</summary>
    void TreeDragLeave() { }

    /// <summary>brief-em3d-101 R-em3d101-2c — files dragged over the pane: true when a drop would place them (image files only:
    /// anything else is refused, so a stray file is never half-imported).</summary>
    bool FileDragOver(IReadOnlyList<string> paths) => false;

    /// <summary>brief-em3d-101 — files dropped at (<paramref name="x"/>, <paramref name="y"/>); <paramref name="shift"/> held (owner
    /// decision D6: onto a flat face, map the image onto it). True when they were ours.</summary>
    bool FileDrop(IReadOnlyList<string> paths, float x, float y, bool shift) => false;

    /// <summary>brief-em3d-67 R-em3d67-5a — Edge mode's entries for the context menu (Fillet…, Chamfer…).</summary>
    IEnumerable<Viewer3DMenuItem> EdgeMenuItems() => [];

    /// <summary>brief-em3d-67 — a status sentence (the tangent chain's "stopped at a branch").</summary>
    void Say(string text) { }

    /// <summary>brief-em3d-91 — H: hides the selection when all of it is shown, else shows all of it. False when nothing is
    /// selected.</summary>
    bool HideOrShowSelection() => false;

    /// <summary>brief-em3d-91 — the context menu's Hide / Show Selection (with H shown), or null for none.</summary>
    Viewer3DMenuItem? HideShowSelectionItem() => null;

    /// <summary>brief-em3d-95 — Copy Objects: the selection's rows as a 3D copy, to paste into this or another 3D view. The
    /// canvas's own Copy stays the picture copy.</summary>
    Viewer3DMenuItem? CopyObjectsItem() => null;

    /// <summary>A click on nothing, or Esc, cleared a selection the scene already held nothing of: the editor drops a selected
    /// row that stands for nothing in the scene (a lumped element, a probe), which no selection change would otherwise reach.</summary>
    void SelectionClearedWhenEmpty() { }

    /// <summary>What the view draws over the scene that Copy as Vector carries too (the lumped elements), in world metres; null
    /// for none.</summary>
    CircuitRF.Render.Em3dPictureMarks? PictureMarks() => null;
}

/// <summary>brief-em3d-45 — the drawing's 2D chrome for one frame, in world metres: the overlay projects it.</summary>
public sealed class Viewer3DDrawOverlay
{
    /// <summary>The tool's rubber band.</summary>
    public List<DrawSegment> Rubber { get; } = [];
    /// <summary>The document's construction polylines (never in the solved problem, so never in the scene).</summary>
    public List<DrawSegment> Construction { get; } = [];
    /// <summary>The polylines selected in the tree.</summary>
    public List<DrawSegment> Selected { get; } = [];
    /// <summary>The two edges a refused outline crosses at.</summary>
    public List<DrawSegment> Crossing { get; } = [];
    /// <summary>The points the gesture has fixed so far.</summary>
    public List<CircuitRF.Engine.Em3d.Point3> Fixed { get; } = [];
    /// <summary>brief-em3d-46 — an operation's pivot, drawn as a small cross.</summary>
    public List<CircuitRF.Engine.Em3d.Point3> Pivots { get; } = [];
    /// <summary>brief-em3d-48 R-em3d48-6b — an instance whose cell resolves to nothing: a dashed box.</summary>
    public List<DrawSegment> Missing { get; } = [];
    /// <summary>brief-em3d-48 — chrome text at a world point: a missing cell's name, an external instance's
    /// <c>[alias]</c> tag. Marking is chrome, never geometry (layout-view §7.1).</summary>
    public List<(CircuitRF.Engine.Em3d.Point3 At, string Text)> Labels { get; } = [];
    /// <summary>brief-em3d-75 R-em3d75-1b — heat sources: outline and hatch, in a warm colour.</summary>
    public List<DrawSegment> HeatSources { get; } = [];
    /// <summary>Probes' lines and spot rings.</summary>
    public List<DrawSegment> Probes { get; } = [];
    /// <summary>A probe's marker, at the point it reads (a line's two ends).</summary>
    public List<CircuitRF.Engine.Em3d.Point3> ProbeMarks { get; } = [];
    /// <summary>Mesh regions: dashed wireframe boxes.</summary>
    public List<DrawSegment> MeshRegions { get; } = [];
    /// <summary>brief-em3d-90 — declared thermal symmetry planes: outline and hatch, in the air box's symmetry colour.</summary>
    public List<DrawSegment> SymmetryPlanes { get; } = [];
    /// <summary>brief-sar-3 R-sar3-3 — lumped elements: the gap's outline and a capacitor glyph across it.</summary>
    public List<DrawSegment> LumpedElements { get; } = [];
    /// <summary>The face a tool will land on (the Wire tool's pad top), each as its rings — the outline, then any holes —
    /// filled in the hover's colour.</summary>
    public List<IReadOnlyList<IReadOnlyList<CircuitRF.Engine.Em3d.Point3>>> TargetFaces { get; } = [];
    /// <summary>brief-dfr31-3 R-dfr31-3-1 — a clip-plane field plot's plane: its four corners across the air box, and the label
    /// naming its axis and offset.</summary>
    public List<(CircuitRF.Engine.Em3d.Point3[] Corners, string Label)> FieldPlanes { get; } = [];
    /// <summary>brief-dfr34-1 R-dfr34-1-2 — port sheets drawn over the geometry, not depth-tested, while ports are being worked
    /// on: each one's four corners, and whether it is selected.</summary>
    public List<(CircuitRF.Engine.Em3d.Point3[] Corners, bool Selected)> Ports { get; } = [];
    /// <summary>brief-cxp-2 — coaxial ports' rings drawn the same way: each one's outer and inner circle, and whether it is
    /// selected.</summary>
    public List<(CircuitRF.Engine.Em3d.Point3[] Outer, CircuitRF.Engine.Em3d.Point3[] Inner, bool Selected)> PortRings { get; } = [];

    public void Clear()
    {
        TargetFaces.Clear();
        Ports.Clear();
        PortRings.Clear();
        FieldPlanes.Clear();
        Rubber.Clear(); Construction.Clear(); Selected.Clear(); Crossing.Clear(); Fixed.Clear(); Pivots.Clear(); Missing.Clear(); Labels.Clear();
        HeatSources.Clear(); Probes.Clear(); ProbeMarks.Clear(); MeshRegions.Clear(); SymmetryPlanes.Clear(); LumpedElements.Clear();
    }
}

/// <summary>One context-menu entry. <see cref="Run"/> null and no children is a heading or a disabled item.
/// <see cref="Gesture"/> is its shortcut, shown in the menu's own shortcut column (display only: the pane's keys do the
/// work) — never spelled into <see cref="Header"/>.</summary>
public sealed record Viewer3DMenuItem(string Header, Action? Run = null, bool Enabled = true, string? Tip = null,
                                      IReadOnlyList<Viewer3DMenuItem>? Children = null, KeyGesture? Gesture = null)
{
    public static readonly Viewer3DMenuItem Separator = new("-");
    public bool IsSeparator => Header == "-";

    /// <summary>A bare key, e.g. G.</summary>
    public static KeyGesture Plain(Key key, KeyModifiers modifiers = KeyModifiers.None) => new(key, modifiers);

    /// <summary>The platform's command key with <paramref name="key"/>: Cmd on macOS, Ctrl elsewhere.</summary>
    public static KeyGesture Command(Key key, KeyModifiers more = KeyModifiers.None)
        => new(key, (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | more);
}

public sealed partial class Viewer3DViewModel
{
    /// <summary>B's list, cached per cursor, mode and scene (R-em3d43-4b).</summary>
    public Scene3DHitCycle HitCycle { get; } = new();

    /// <summary>The editor, or null in the read-only viewer.</summary>
    public IViewer3DEditHost? EditHost { get; set; }

    /// <summary>What a click would select now, or null.</summary>
    public Scene3DItem? HoveredItem
    {
        get => _hoveredItem;
        private set
        {
            _hoveredItem = value;
            // 3D editor groups — a click on a group's member takes the whole group, so the hover tints the whole group.
            View.HoverGroup = value is { } h && EditHost?.PickGroup(h) is { Count: > 1 } g
                ? [.. g.Where(i => i != h).Select(i => i.Object).Distinct()] : [];
        }
    }
    private Scene3DItem? _hoveredItem;

    /// <summary>brief-em3d-45 — the (object, face) the ID pass last found under the cursor, in any mode; (0, −1) for none.</summary>
    public (uint Object, int Face) LastPick { get; private set; } = (0, -1);

    /// <summary>brief-em3d-45 — the ray through the cursor, world metres (a unit direction), or null off the view.</summary>
    public (CircuitRF.Engine.Em3d.Point3 Origin, CircuitRF.Engine.Em3d.Point3 Direction)? CursorRay()
    {
        if (View.CursorX < 0 || View.CursorY < 0) return null;
        // The snap query casts its ray through the cursor itself (Ray adds half a pixel): the same ray here.
        return RayAt(View.CursorX - 0.5f, View.CursorY - 0.5f);
    }

    /// <summary>brief-em3d-101 — the ray through pixel (<paramref name="px"/>, <paramref name="py"/>) (Camera3D.Ray's convention),
    /// world metres, starting where the view does — the cursor's ray, for a point that is not the cursor (a drop, the view's
    /// centre).</summary>
    public (CircuitRF.Engine.Em3d.Point3 Origin, CircuitRF.Engine.Em3d.Point3 Direction) RayAt(float px, float py)
    {
        var cam = View.Camera;
        var (o, d) = cam.Ray(px, py, _viewW, _viewH);
        // 3D editor bugs round 2 — Ray starts at the NEAR CLIP PLANE, which brackets the scene's sphere; a drawing plane
        // nearer the camera than that (the lower half of a new, empty design's orthographic view) was "behind the camera"
        // and refused the click. The ray starts where the view does: at the eye in perspective, and in orthographic —
        // where every ray is a whole line and the grid is drawn along all of it — far enough back to precede any plane the
        // view can show short of edge-on. Moving the origin along the ray's own direction, in double, moves no hit point.
        double back = Vector3.Dot(o - cam.Eye, d);
        if (cam.Projection == Projection3D.Orthographic)
        {
            double half = cam.Distance * Math.Tan((cam.FovY > 0 ? cam.FovY : Camera3D.DefaultFovY) * 0.5) * Math.Max(1.0, _viewW / Math.Max(1f, _viewH));
            back += 2 * (cam.Distance + cam.SceneRadius) + 2 * half / DrawingPlane.EdgeOnCosine;
        }
        var (x, y, z) = Scene.ToWorld(o);
        return (new CircuitRF.Engine.Em3d.Point3(x - d.X * back, y - d.Y * back, z - d.Z * back), new CircuitRF.Engine.Em3d.Point3(d.X, d.Y, d.Z));
    }

    /// <summary>3D editor bugs round 2 — the nearest visible object under the cursor and where the cursor's ray meets it
    /// (world metres), found on the CPU from the scene itself: exact at the moment of a click, never a pick read back from an
    /// earlier frame. (0, null) off the view or over nothing.</summary>
    public (uint Object, (double X, double Y, double Z)? World) PickUnderCursor()
    {
        if (View.CursorX < 0 || View.CursorY < 0) return (0, null);
        var (id, point) = Scene3DPicking.Pick(Scene, View.Camera, View.CursorX - 0.5f, View.CursorY - 0.5f, _viewW, _viewH, View.Visible, View.Clip);
        return id == 0 ? (0, null) : (id, Scene.ToWorld(point));
    }

    /// <summary>The pane's size, DIPs, as the last resize said (<see cref="DefaultViewSize"/> before any).</summary>
    public (float Width, float Height) ViewSize => (_viewW, _viewH);

    /// <summary>The size a pane is taken to have before it is laid out.</summary>
    public static readonly (float Width, float Height) DefaultViewSize = (800, 500);

    /// <summary>Vertex mode's candidate under the cursor (scene-local), for the overlay's dot.</summary>
    public Vector3? HoveredVertex => HoveredItem is { Face: < 0, Object: > 0 } h && SelectMode == Scene3DSelectMode.Vertex ? h.Point : null;

    /// <summary>The selection, in the order it was made.</summary>
    public IReadOnlyList<Scene3DItem> Selection => View.Selection;

    /// <summary>Raised whenever <see cref="Selection"/> changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>How many times Fit ran — gate 8 reads it, since there is no pixel to look at.</summary>
    public int Fits { get; private set; }

    [ObservableProperty] private Scene3DSelectMode _selectMode;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private string _cycleText = "";

    partial void OnSelectModeChanged(Scene3DSelectMode value)
    {
        View.Mode = value;
        HitCycle.ModeChanged(value);
        // A selection means something only in the mode it was made in.
        HoveredItem = null;
        View.HoveredFace = -1;
        SetSelection([]);
        CycleText = "";
        OnPropertyChanged(nameof(IsObjectMode));
        OnPropertyChanged(nameof(IsFaceMode));
        OnPropertyChanged(nameof(IsVertexMode));
        OnPropertyChanged(nameof(IsEdgeMode));
        OnPropertyChanged(nameof(HoveredVertex));
        FrameRequested?.Invoke();
    }

    /// <summary>The toolbar's three exclusive toggles. Unchecking the checked one does nothing: a mode is
    /// always on.</summary>
    public bool IsObjectMode { get => SelectMode == Scene3DSelectMode.Object; set { if (value) SelectMode = Scene3DSelectMode.Object; else OnPropertyChanged(); } }
    public bool IsFaceMode   { get => SelectMode == Scene3DSelectMode.Face;   set { if (value) SelectMode = Scene3DSelectMode.Face;   else OnPropertyChanged(); } }
    public bool IsVertexMode { get => SelectMode == Scene3DSelectMode.Vertex; set { if (value) SelectMode = Scene3DSelectMode.Vertex; else OnPropertyChanged(); } }
    public bool IsEdgeMode   { get => SelectMode == Scene3DSelectMode.Edge;   set { if (value) SelectMode = Scene3DSelectMode.Edge;   else OnPropertyChanged(); } }

    // ── the selection ───────────────────────────────────────────────────────────────────────

    private bool _selectingFromTree;

    /// <summary>Replaces the selection. Duplicates are dropped; order is kept.</summary>
    public void SetSelection(IEnumerable<Scene3DItem> items)
    {
        var list = new List<Scene3DItem>();
        foreach (var i in items) if (i.Object != 0 && !list.Contains(i)) list.Add(i);
        if (list.SequenceEqual(View.Selection)) { RefreshSelectionText(); return; }
        View.Selection = [.. list];
        RefreshSelectionText();
        if (!_selectingFromTree)
        {
            _selectingFromTree = true;
            try { SelectedItem = list.Count > 0 && _items.TryGetValue(list[0].Object, out var it) ? it : null; }
            finally { _selectingFromTree = false; }
        }
        SelectionChanged?.Invoke();
        FrameRequested?.Invoke();
    }

    /// <summary>The user cleared the selection (a click on nothing, Esc). When the scene held nothing selected already no change
    /// is raised, so the editor is told directly: a selected row that stands for nothing in the scene is let go too.</summary>
    private void ClearSelection()
    {
        bool wasEmpty = View.Selection.Length == 0;
        SetSelection([]);
        if (wasEmpty) EditHost?.SelectionClearedWhenEmpty();
    }

    /// <summary>The selection's objects, each once, in selection order.</summary>
    public IReadOnlyList<Scene3DObject> SelectedObjects()
        => [.. View.Selection.Select(i => Scene.Object(i.Object)).OfType<Scene3DObject>().Distinct()];

    /// <summary>brief-dfr34-4 R-dfr34-4-3 — every object the line of sight through the cursor crosses, nearest first, each
    /// once, hidden and clipped-away objects left out: what an armed terminal Pick walks to reach the first conductor
    /// behind the dielectric a click on a board lands on.</summary>
    public IReadOnlyList<string> ObjectsUnderCursor()
    {
        if (View.CursorX < 0 || View.CursorY < 0 || Scene.Objects.Length == 0) return [];
        var q = new Scene3DRayQuery(View.Camera, View.CursorX, View.CursorY, _viewW, _viewH);
        var scene = Scene;
        return [.. RayHits.Collect(scene, q, Scene3DSelectMode.Object, View.Visible, View.Clip)
                          .Select(h => scene.Object(h.Item.Object)?.Name).OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A click: select what is under the cursor (Shift adds or removes it). A click on nothing
    /// clears, unless Shift is held.</summary>
    public void Click(bool shift, KeyModifiers modifiers = KeyModifiers.None, int clickCount = 1)
    {
        if (clickCount == 1) SelectionBeforeClick = View.Selection;
        bool cycling = HitCycle.Active;
        HitCycle.Reset();
        CycleText = "";
        // brief-em3d-45 — the drawing has the click first: an armed tool places a point, a plane gesture moves the plane.
        // brief-em3d-46 — a measurement takes the click before anything, and selects nothing.
        if (MeasureActive) { MeasureClick(); return; }
        if (EditHost?.DrawClick(modifiers | (shift ? KeyModifiers.Shift : KeyModifiers.None), clickCount) == true) return;
        var item = HoveredItem;
        // brief-dfr34-1 R-dfr34-1-2 — a port ranks first, so a second press at the same point on the port already selected
        // steps to what is underneath it (B's list along the line of sight), as the layout editor's repeated click does.
        var at = (View.CursorX, View.CursorY);
        bool again = clickCount == 1 && !shift && _lastPress is { } lp &&
                     MathF.Abs(lp.X - at.CursorX) <= Scene3DHitCycle.ResetPixels && MathF.Abs(lp.Y - at.CursorY) <= Scene3DHitCycle.ResetPixels;
        if (clickCount == 1) _lastPress = (at.CursorX, at.CursorY);
        // A third press goes on stepping (the list from the last press is kept), wrapping back round to the port.
        if (again && item is { } ip && Scene.Object(ip.Object)?.Kind == Scene3DKind.Port && View.Selection is [var only] &&
            (only == ip || cycling))
        {
            if (cycling) HitCycle.Resume();
            Cycle(+1);
            return;
        }
        if (item is not { } it)
        {
            if (!shift) ClearSelection();
            return;
        }
        // brief-em3d-67 R-em3d67-4 — a double-click on an edge takes its tangent chain (Shift adds it).
        if (it.IsEdge && clickCount == 2)
        {
            SelectTangentChain(it, add: shift);
            return;
        }
        // 3D editor groups — a click on any of a group takes all of it, and Shift adds or removes all of it.
        var picked = EditHost?.PickGroup(it) ?? [it];
        if (shift)
            SetSelection(View.Selection.Contains(it) ? View.Selection.Where(s => !picked.Contains(s)) : [.. View.Selection, .. picked]);
        else SetSelection(picked);
        if (_items.TryGetValue(it.Object, out var treeItem)) RevealRequested?.Invoke(treeItem);
    }

    /// <summary>brief-dfr34-1 — where the last single press was, for a second press at the same point.</summary>
    private (float X, float Y)? _lastPress;

    /// <summary>3D editor groups — the selection as it was before the first click of the latest click or double-click: what a
    /// double-click's second click compares with, since its first has already changed the selection.</summary>
    public Scene3DItem[] SelectionBeforeClick { get; private set; } = [];

    /// <summary>3D editor groups — <paramref name="item"/> alone, a group's member though it is, revealed in the tree.</summary>
    public void SelectOnly(Scene3DItem item)
    {
        SetSelection([item]);
        if (_items.TryGetValue(item.Object, out var treeItem)) RevealRequested?.Invoke(treeItem);
    }

    /// <summary>3D editor round 5 — the selectable wire nearest the cursor on screen, within the snap radius, when nothing in
    /// front of it is under the cursor: <paramref name="under"/> (the ID pass's object, 0 for none) is met along the
    /// cursor's ray before the wire's nearest point only when it hides it.</summary>
    private uint? WireNearCursor(uint under)
    {
        if (View.CursorX < 0 || View.CursorY < 0) return null;
        if (Scene3DPicking.NearestOnScreen(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, PickVisible,
                                           Scene3DSnap.RadiusPixels, o => o.Kind == Scene3DKind.Wire && o.Selectable, View.Clip)
            is not { } near) return null;
        if (under == 0) return near.Id;
        float px = View.CursorX - 0.5f, py = View.CursorY - 0.5f;
        if (Scene3DPicking.RayDistance(Scene, View.Camera, px, py, _viewW, _viewH, under) is not { } front) return near.Id;
        var (o, d) = View.Camera.Ray(px, py, _viewW, _viewH);
        return Vector3.Dot(near.Point - o, d) <= front ? near.Id : null;
    }

    /// <summary>B (<paramref name="direction"/> +1) or Shift+B (−1): the next thing behind, or in front, along
    /// the line of sight through the cursor (R-em3d43-4). True when the key did something. brief-em3d-90 — a face
    /// boundary's tint is in the list, just in front of the face it lies on, so B steps from a boundary to its solid.</summary>
    public bool Cycle(int direction)
    {
        if (View.CursorX < 0 || Scene.Objects.Length == 0) return false;
        Scene3DItem? current = View.Selection.Length == 1 ? View.Selection[0] : null;
        var q = new Scene3DRayQuery(View.Camera, View.CursorX, View.CursorY, _viewW, _viewH);
        var scene = Scene;
        var mode = SelectMode;
        var visible = View.Visible;
        var clip = View.Clip;
        var hit = HitCycle.Step(direction, View.CursorX, View.CursorY, mode, scene.Generation, current,
                                () => HitsAlongRay(scene, q, mode, visible, clip));
        if (hit is not { } h)
        {
            CycleText = "Nothing under the cursor to step through.";
            return true;
        }
        SetSelection([h.Item]);
        // R-em3d43-4c: without a readout, cycling feels like a glitch.
        CycleText = $"{Name(h.Item)} · {HitCycle.Index + 1} of {HitCycle.Count}";
        return true;
    }

    /// <summary>Everything a click could select along a ray, nearest first, what is hidden behind the front included.</summary>
    private static List<Scene3DHit> HitsAlongRay(Scene3DModel scene, in Scene3DRayQuery q, Scene3DSelectMode mode, ReadOnlySpan<bool> visible, in ClipPlane3D clip)
    {
        var hits = RayHits.Collect(scene, q, mode, visible, clip);
        // brief-em3d-67 — B steps through what a click could select.
        if (mode == Scene3DSelectMode.Edge) hits.RemoveAll(x => scene.Object(x.Item.Object)?.Selectable != true);
        // brief-em3d-49 — the air-box faces after every solid face: lowest priority.
        if (mode == Scene3DSelectMode.Face)
            foreach (var (id, depth, point) in Scene3DPicking.PickLastHits(scene, q.Camera, q.Px, q.Py, q.Width, q.Height, visible))
                hits.Add(new Scene3DHit(Scene3DItem.OfFace(id, 0), depth, point));
        return hits;
    }

    private readonly Dictionary<string, string> _renames = new(StringComparer.Ordinal);

    /// <summary>The editor is renaming <paramref name="from"/> to <paramref name="to"/>: the next scene's
    /// object of that name is the same object, and stays selected.</summary>
    public void ExpectRename(string from, string to) => _renames[from] = to;

    /// <summary>The selection's scene objects were renumbered by a regeneration: carry it across by name.</summary>
    private void RemapSelection(Scene3DModel from, Scene3DModel to)
    {
        var renames = new Dictionary<string, string>(_renames, StringComparer.Ordinal);
        _renames.Clear();
        if (View.Selection.Length == 0 && HoveredItem is null) return;
        var byName = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var o in to.Objects) byName.TryAdd(o.Name, o.Id);
        Scene3DItem? Map(Scene3DItem i)
            => from.Object(i.Object) is { } o && byName.TryGetValue(renames.GetValueOrDefault(o.Name, o.Name), out uint id)
               ? i with { Object = id } : null;
        // brief-em3d-67 R-em3d67-3b — an edge by its NAME: its index is this scene's, rebuilt per elaboration.
        Scene3DItem? MapEdge(Scene3DItem i)
        {
            if (Map(i) is not { } m) return null;
            if (!i.IsEdge) return m;
            var was = from.FeaturesOf(i.Object).Table?.Named;
            var now = to.FeaturesOf(m.Object).Table?.Named;
            if (was is null || now is null || i.Edge >= was.Edges.Length) return null;
            int e = now.IndexOf(was.Edges[i.Edge].Name);
            return e < 0 ? null : m with { Edge = e };
        }
        var mapped = View.Selection.Select(MapEdge).OfType<Scene3DItem>().ToList();
        HoveredItem = HoveredItem is { } h ? MapEdge(h) : null;
        View.Selection = [];          // SetSelection compares against it; force the notifications
        SetSelection(mapped);
    }

    // ── hover (the ID pass's answer) ────────────────────────────────────────────────────────

    /// <summary>
    /// Frame loop → UI: the ID pass's (object, face) under the cursor. Sets what a click would select —
    /// in Vertex mode the nearest corner of that face on screen, within the snap radius (R-em3d43-3b) —
    /// and the tooltip. Touches no geometry beyond that one face's corners — and then resolves the snap from
    /// the frame's pick <paramref name="patch"/> (brief-em3d-44; null renders one on the CPU).
    /// </summary>
    internal void OnPicked(uint id, uint face, Vector3 point, bool hit, Scene3DIdPatch? patch = null)
    {
        if (CameraGesture) return;
        int f = face == Scene3DVertex.NoFace ? -1 : (int)face;
        Scene3DItem? item = null;
        // brief-em3d-48 R-em3d48-4a — the dimmed parent around a pushed-in child is under the cursor for the snap and for
        // a drawing-plane pick, never for hover or selection.
        bool selectable = Scene.Object(id)?.Selectable == true;
        // brief-em3d-49 R-em3d49-3b — in Face mode, where nothing pickable is under the cursor, the nearest air-box face
        // the editor offers (picked last: a solid face in front always wins).
        if (id == 0 && SelectMode == Scene3DSelectMode.Face && PickLastUnderCursor() is { } last)
        {
            (id, f, selectable) = (last, 0, true);
        }
        // brief-em3d-67 R-em3d67-3a — Edge mode: the snap query's edge tier over the frame's patch, not the ID pass's object.
        if (SelectMode == Scene3DSelectMode.Edge)
        {
            var (edgePatch, radius) = EdgePatch(patch);
            patch ??= edgePatch;
            if (SnapQuery.NearestEdge(Scene, edgePatch, radius, PickVisible, View.Clip, o => Scene.Object(o)?.Selectable == true) is { } eh)
                item = Scene3DItem.OfEdge(eh.Object, eh.Edge);
        }
        // brief-dfr34-1 R-dfr34-1-2 — Object mode: a port ranks first, as in the layout editor. Its sheet is thin and sits inside
        // metal and dielectric, so the ID pass's front object is almost never it: a port within a few pixels of the cursor on
        // screen is what a click selects, wherever it is in depth, and a second press at the same point steps behind it.
        else if (SelectMode == Scene3DSelectMode.Object && PortNearCursor() is { } port)
            item = Scene3DItem.OfObject(port);
        // 3D editor round 5 — Object mode: a bond wire within a few pixels of the cursor is what a click selects, unless
        // what IS under the cursor stands in front of it. Only the hover's item: the pick itself (the snap, a tool's pad)
        // stays the pixel's.
        else if (SelectMode == Scene3DSelectMode.Object && Scene.Object(id)?.Kind != Scene3DKind.Wire && WireNearCursor(id) is { } wire)
            item = Scene3DItem.OfObject(wire);
        else if (id != 0 && selectable)
            item = SelectMode switch
            {
                Scene3DSelectMode.Face => f >= 0 ? Scene3DItem.OfFace(id, f) : null,
                Scene3DSelectMode.Vertex => Scene3DFaces.NearestVertexOnScreen(Scene, id, f, View.Camera, View.CursorX, View.CursorY,
                                                                              _viewW, _viewH, Scene3DSnap.RadiusPixels) is { } v
                                            ? Scene3DItem.OfVertex(id, v) : null,
                _ => Scene3DItem.OfObject(id),
            };
        // brief-em3d-90 R-em3d90-3 — a face boundary's tint over the face under the cursor is what a click selects: the
        // boundary wins over the face it lies on (B then steps to the face or the solid). LastPick stays the ID pass's face,
        // so a tool drawing on a tinted face still finds the face.
        if (SelectMode is Scene3DSelectMode.Object or Scene3DSelectMode.Face && (item is null || item.Value.Object == id)
            && TintUnderCursor(id) is { } tint)
            item = SelectMode == Scene3DSelectMode.Face ? Scene3DItem.OfFace(tint, 0) : Scene3DItem.OfObject(tint);
        LastPick = (id, f);
        int hoveredFace = SelectMode == Scene3DSelectMode.Face ? f : -1;
        if (item != HoveredItem || View.HoveredFace != hoveredFace)
        {
            HoveredItem = item;
            View.HoveredFace = hoveredFace;
            OnPropertyChanged(nameof(HoveredVertex));
            FrameRequested?.Invoke();
        }
        OnPicked(selectable ? id : 0, point, hit);
        ResolveSnap(patch);
        // brief-em3d-46 — an operation's preview and a measurement's rubber band follow the cursor from here.
        MeasureFollow();
        CursorResolved?.Invoke();
    }

    /// <summary>brief-dfr34-1 R-dfr34-1-2 — the visible, selectable port sheet nearest the cursor on screen within the snap
    /// radius (a sheet seen edge-on is a line, and is still found), or null. Depth is not asked: a port ranks first.</summary>
    private uint? PortNearCursor()
    {
        if (View.CursorX < 0 || View.CursorY < 0 || !Scene.Objects.Any(o => o.Kind == Scene3DKind.Port)) return null;
        return Scene3DPicking.NearestOnScreen(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, PickVisible,
                                              Scene3DSnap.RadiusPixels, o => o.Kind == Scene3DKind.Port && o.Selectable, View.Clip)
            is { } near ? near.Id : null;
    }

    /// <summary>
    /// brief-em3d-90 R-em3d90-3 — the visible tint under the cursor that stands at or in front of <paramref name="under"/> (the
    /// ID pass's object, 0 for none), or null. At the face it lies on, the tint wins by KIND (Scene3DPicking.TintTie), not by
    /// the lift that keeps it from z-fighting; a solid genuinely in front of a tinted face still wins.
    /// </summary>
    private uint? TintUnderCursor(uint under)
    {
        if (View.CursorX < 0 || !Scene.HasTints) return null;
        var hits = Scene3DPicking.TintHits(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, View.Visible, View.Clip);
        if (hits.Count == 0) return null;
        if (under == 0) return hits[0].Id;
        var front = Scene3DPicking.RayDistance(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, under);
        return front is not { } d || hits[0].Depth <= d + Scene3DPicking.TintTie(Scene) ? hits[0].Id : null;
    }

    /// <summary>brief-em3d-49 — the nearest pick-last object (an air-box face) under the cursor, or null.</summary>
    private uint? PickLastUnderCursor()
    {
        if (View.CursorX < 0 || !Scene.Objects.Any(o => o.PickLast)) return null;
        var hits = Scene3DPicking.PickLastHits(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, View.Visible);
        return hits.Count > 0 ? hits[0].Id : null;
    }

    // ── keys (owner decision D3) ────────────────────────────────────────────────────────────

    /// <summary>3D round 1 — a plain left drag orbits only when nothing is waiting for a click: while a tool, an operation
    /// or Measure is armed, Ctrl/Cmd + drag orbits and a plain press is always that gesture's click.</summary>
    public bool OrbitNeedsCommand => MeasureActive || EditHost?.DrawArmed == true;

    /// <summary>
    /// R-em3d43-2a — every 3D pane's keys. Mode keys act only with no modifier and no gesture in progress
    /// (<paramref name="gestureInProgress"/>), so Cmd+V is still paste. Esc with a gesture is the pane's to
    /// cancel (false here); with none it clears the selection. Delete and Backspace delete — in the editor.
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers modifiers, bool gestureInProgress)
    {
        // brief-em3d-45 — the drawing's keys come first: while a tool gesture is in progress a digit opens the typed
        // field instead of choosing a standard view, and Esc cancels the gesture rather than the selection.
        if (!gestureInProgress && EditHost?.DrawKey(key, modifiers) == true) return true;
        bool plain = modifiers == KeyModifiers.None;
        if (key == Key.B && (plain || modifiers == KeyModifiers.Shift)) return Cycle(plain ? +1 : -1);
        // 3D menu cleanup — Ctrl/Cmd+A on the pane is 3D ▸ Select All Objects (the menu carries no key equivalent of its own:
        // on macOS one would take Cmd+A from every text box while a 3D view is active).
        if (key == Key.A && (modifiers == KeyModifiers.Control || modifiers == KeyModifiers.Meta) && !gestureInProgress)
        {
            SelectAllObjects();
            return true;
        }
        // Ctrl/Cmd+E fits the selection, Hertzon's key, in either interaction scheme since it takes nothing from circuitRF's.
        // (Hertzon's Ctrl+D Fit All is not taken: Ctrl/Cmd+D is Duplicate here, and Home and Ctrl/Cmd+0 already fit.)
        if (key == Key.E && (modifiers == KeyModifiers.Control || modifiers == KeyModifiers.Meta) && !gestureInProgress)
            return FitSelection();
        if (CameraKey(key, modifiers)) return true;
        if (!plain) return false;
        StandardView3D? standard = key switch
        {
            Key.D1 or Key.NumPad1 => StandardView3D.Iso, Key.D2 or Key.NumPad2 => StandardView3D.Top,
            Key.D3 or Key.NumPad3 => StandardView3D.Front, Key.D4 or Key.NumPad4 => StandardView3D.Right,
            Key.D5 or Key.NumPad5 => StandardView3D.Back, Key.D6 or Key.NumPad6 => StandardView3D.Left,
            Key.D7 or Key.NumPad7 => StandardView3D.Bottom, _ => null,
        };
        if (standard is { } v) { StandardViewCommand.Execute(v); return true; }
        switch (key)
        {
            case Key.O when !gestureInProgress: SelectMode = Scene3DSelectMode.Object; return true;
            case Key.F when !gestureInProgress: SelectMode = Scene3DSelectMode.Face; return true;
            case Key.V when !gestureInProgress: SelectMode = Scene3DSelectMode.Vertex; return true;
            // brief-em3d-67 R-em3d67-1a (D6) — E arms Edge mode in every 3D pane, beside O, F and V.
            case Key.E when !gestureInProgress: SelectMode = Scene3DSelectMode.Edge; return true;
            case Key.Home: FitCommand.Execute(null); return true;
            // 3D round 1 — the layout editor's snap keys: S or F3 geometry snap, F9 the grid.
            case Key.S or Key.F3: ToggleGeometrySnap(); return true;
            case Key.F9: ToggleGridSnap(); return true;
            case Key.M when !gestureInProgress: ToggleMeasure(); return true;
            case Key.P: IsPerspective = !IsPerspective; return true;
            // The realistic view's key: L (R is Rotate). The Windows/Linux menu shows it; the macOS menu cannot carry a bare letter
            // without taking it from every text box, so its tooltip names it, as Hide / Show Selection's does.
            case Key.L: IsRealistic = !IsRealistic; return true;
            case Key.C: ClipEnabled = !ClipEnabled; return true;
            case Key.A: ShowAxisIndicator = !ShowAxisIndicator; return true;
            case Key.Escape:
                if (gestureInProgress) return false;
                // 3D round 1 — Esc unwinds one step at a time: a measurement's points go first (Measure stays armed),
                // then Measure itself, and only then the selection.
                if (MeasureActive && MeasureP1 is not null) { ClearMeasurement(); return true; }
                if (MeasureActive || MeasureP1 is not null) { EndMeasure(); return true; }
                // Then out of Face, Vertex or Edge mode, back to Object (which drops the selection made in that mode).
                if (SelectMode != Scene3DSelectMode.Object) { SelectMode = Scene3DSelectMode.Object; return true; }
                HitCycle.Reset();
                CycleText = "";
                ClearSelection();
                return true;
            case Key.Delete or Key.Back:
                return EditHost?.DeleteSelection() ?? false;
            // brief-em3d-91 R-em3d91-2 — H hides or shows the selection, in the editor (the read-only viewer has no host).
            case Key.H when !gestureInProgress:
                return EditHost?.HideOrShowSelection() ?? false;
        }
        return false;
    }

    /// <summary>Degrees the view turns per Shift+arrow.</summary>
    public const float OrbitStepDegrees = 15f;

    /// <summary>
    /// General designer feedback round 14 — the camera from the keyboard, for a trackpad with no wheel and no middle button.
    /// Ctrl/Cmd + and − zoom one wheel notch about the view's centre, Ctrl/Cmd 0 is Fit (as View ▸ Zoom to Fit is everywhere
    /// else); a bare arrow pans by <see cref="CanvasArrowPan"/>'s screen step, as on every 2D canvas, and Shift+arrow orbits
    /// by <see cref="OrbitStepDegrees"/> — the 2D canvases' Shift is a coarse pan, but a 3D view has a third motion to give
    /// it. Nothing in the 3D views nudges a selection with an arrow, so the arrows are free whatever is selected.
    /// </summary>
    private bool CameraKey(Key key, KeyModifiers modifiers)
    {
        bool command = modifiers is KeyModifiers.Control or KeyModifiers.Meta;
        if (command && key is Key.OemPlus or Key.Add) { ZoomStep(+1); return true; }
        if (command && key is Key.OemMinus or Key.Subtract) { ZoomStep(-1); return true; }
        if (command && key is Key.D0 or Key.NumPad0) { FitCommand.Execute(null); return true; }
        if (modifiers == KeyModifiers.Shift && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            // As the pan: the CAMERA travels the arrow's way round the target (Up rises over the top), the opposite of a drag.
            float px = OrbitStepDegrees * MathF.PI / 180f / Camera3D.OrbitRadiansPerPixel;
            var (dx, dy) = key switch { Key.Left => (px, 0f), Key.Right => (-px, 0f), Key.Up => (0f, px), _ => (0f, -px) };
            Orbit(dx, dy);
            return true;
        }
        if (CanvasArrowPan.ScreenStep(key, modifiers) is not { } step) return false;
        // The VIEW moves with the arrow, so what is drawn moves against it — the opposite of a drag, which carries the content.
        Pan(-(float)step.Dx, -(float)step.Dy, _viewH);
        return true;
    }

    /// <summary>One wheel notch in (positive) or out about the centre of the view.</summary>
    public void ZoomStep(float notches) => Zoom(notches, _viewW * 0.5f, _viewH * 0.5f, _viewW, _viewH);

    // ── words ───────────────────────────────────────────────────────────────────────────────

    /// <summary>An object as the status line names it: <c>Box "lid"</c>, or its scene name in the viewer.</summary>
    public string ObjectName(Scene3DObject o)
        => EditHost?.KindOf(o) is { } kind ? $"{kind} \"{o.Name}\"" : $"\"{o.Name}\"";

    /// <summary><c>Face top · Box "lid"</c>, <c>Vertex (x, y, z) · Box "lid"</c> or <c>Box "lid"</c>.</summary>
    public string Name(Scene3DItem item)
    {
        if (Scene.Object(item.Object) is not { } o) return "";
        if (o.Tint) return Scene3DBuilder.TintLabel(o.Name);          // brief-em3d-90 — a boundary, whatever the mode
        string owner = ObjectName(o);
        if (item.Face >= 0) return $"Face {o.FaceName(item.Face)} · {owner}";
        if (item.IsEdge) return EdgeOf(item) is { } e ? $"Edge {e.Edge.Name} · {owner}" : owner;
        if (SelectMode == Scene3DSelectMode.Vertex) return $"Vertex {Point(item.Point)} · {owner}";
        return owner;
    }

    private string Point(Vector3 local)
    {
        var (x, y, z) = Scene.ToWorld(local);
        return $"({FormatLength(x)}, {FormatLength(y)}, {FormatLength(z)})";
    }

    /// <summary>R-em3d43-2c / -6b — what is selected, in the document's display unit: a face's name, area
    /// and normal; a vertex's coordinates; an object's material.</summary>
    private void RefreshSelectionText()
    {
        var sel = View.Selection;
        if (sel.Length == 0) { SelectionText = ""; return; }
        string more = sel.Length > 1 ? $"  (+{sel.Length - 1} more selected)" : "";
        if (sel.Length > Scene3DFramePlan.SelectionLimit)
            more += $" — the first {Scene3DFramePlan.SelectionLimit} are drawn highlighted";
        var first = sel[0];
        string text = Name(first);
        if (Scene.Object(first.Object) is { } o)
        {
            if (first.Face >= 0)
            {
                var (area, normal) = Scene3DFaces.AreaAndNormal(Scene, o.Id, first.Face);
                text += $" · area {FormatArea(area)} · normal " +
                        (normal is { } n ? $"({Num(n.X)}, {Num(n.Y)}, {Num(n.Z)})" : "varies (a curved face)");
            }
            if (first.IsEdge && EdgeOf(first) is { } e) text += $" · {e.Edge.Kind.ToString().ToLowerInvariant()} · length {FormatLength(e.Edge.Length)}";
            if (o.Material is { } m) text += $" · {m}";
        }
        // brief-em3d-67 R-em3d67-3f — several edges: their count and total length.
        if (sel.Length > 1 && sel.All(i => i.IsEdge))
            text = $"{sel.Length} edges · total length {FormatLength(sel.Sum(i => EdgeOf(i)?.Edge.Length ?? 0))}";
        SelectionText = text + (sel.All(i => i.IsEdge) ? "" : more);
    }

    private static string Num(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>An area in the display unit, squared: through the length formatter, so it follows the unit.</summary>
    private string FormatArea(double m2)
    {
        // The formatter spells a LENGTH; an area is its value in unit² — found by formatting 1 m and reading
        // the unit's scale off it would be fragile, so the scale comes from a length of one unit instead.
        string one = FormatLength(1);
        int space = one.LastIndexOf(' ');
        string unit = space > 0 ? one[(space + 1)..] : "m";
        double perMetre = space > 0 && double.TryParse(one[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1;
        return (m2 * perMetre * perMetre).ToString("G5", CultureInfo.InvariantCulture) + " " + unit + "²";
    }

    /// <summary>Every text that prints a length again, after the display unit changed.</summary>
    public void RefreshLengthTexts()
    {
        RefreshSelectionText();
        if (_lastCursorWorld is { } w) CursorText = $"x {FormatLength(w.X)}   y {FormatLength(w.Y)}   z {FormatLength(w.Z)}";
        CycleText = "";
        OnPropertyChanged(nameof(Status));
    }

    // ── the context menu (R-em3d43-4e: it acts on the current item) ─────────────────────────

    /// <summary>
    /// A right-click: selects what is under the cursor first when it is not selected — unless the selection
    /// came from B at this cursor, which the menu then acts on even though the cursor is over the front face, or the
    /// cursor is on the selection at all, however far back. Returns the menu for the selection.
    /// </summary>
    public IReadOnlyList<Viewer3DMenuItem> OpenContextMenu()
    {
        if (!HitCycle.Active && HoveredItem is { } h && !View.Selection.Contains(h) && !SelectionUnderCursor())
            SetSelection(EditHost?.PickGroup(h) ?? [h]);
        return ContextMenuItems();
    }

    /// <summary>
    /// Whether anything selected lies along the cursor's ray. The hovered item alone cannot say: the ID pass keeps the front
    /// fragment, while a selected face is drawn over everything (<see cref="Scene3DPipeline.OnTop"/>) and a selected object's
    /// outline likewise, so what looks selected under the cursor can stand behind what is hovered there.
    /// </summary>
    private bool SelectionUnderCursor()
    {
        if (View.Selection.Length == 0 || View.CursorX < 0) return false;
        var q = new Scene3DRayQuery(View.Camera, View.CursorX, View.CursorY, _viewW, _viewH);
        var selected = View.Selection;
        return HitsAlongRay(Scene, q, SelectMode, View.Visible, View.Clip).Any(x => selected.Contains(x.Item));
    }

    /// <summary>The menu for the current selection, per mode. Brief 43 builds the frame and the operations
    /// that need no geometry; briefs 46, 47 and 49 add theirs.</summary>
    public IReadOnlyList<Viewer3DMenuItem> ContextMenuItems()
    {
        var items = new List<Viewer3DMenuItem>();
        var objects = SelectedObjects();
        var host = EditHost;
        if (View.Selection.Length > 0)
        {
            string title = View.Selection.Length == 1 ? Name(View.Selection[0]) : $"{View.Selection.Length} selected";
            items.Add(new Viewer3DMenuItem(title, Enabled: false));
            items.Add(Viewer3DMenuItem.Separator);
        }
        // brief-em3d-90 — a boundary's tint is a record, not an object: its menu is the editor's alone (no Rename, Material,
        // Isolate), and the face under it is one B away.
        if (host is not null && objects.Count > 0 && objects.All(o => o.Tint))
        {
            items.AddRange(host.DrawMenuItems());
            if (host.HideShowSelectionItem() is { } tintHideShow) items.Add(tintHideShow);
            if (host.CopyObjectsItem() is { } tintCopy) items.Add(tintCopy);
            return items;
        }
        // brief-hzn-12 — the incident wave's arrows: the wave's own menu (it is the setup's, not geometry: no Rename or Material)
        if (host is not null && objects.Count > 0 && objects.All(o => o.Kind == Scene3DKind.Excitation))
        {
            items.AddRange(host.ExcitationMenuItems());
            return items;
        }
        bool any = objects.Count > 0;
        bool inInstance = host is not null && objects.Any(o => host.InstanceOf(o) is not null);
        bool own = host is not null && any && !inInstance;

        if (SelectMode != Scene3DSelectMode.Object && any)
        {
            if (inInstance)
            {
                // Measuring from a die's pad is the point; editing the child from here is refused by
                // construction — the menu does not offer it (R-em3d43-3c).
                if (SelectMode == Scene3DSelectMode.Face)
                    items.Add(new Viewer3DMenuItem("Copy Face as Sheet", Enabled: false, Tip: "Comes with face editing (brief 47)."));
                items.Add(new Viewer3DMenuItem("Select Owning Instance", SelectOwningInstance));
                items.Add(new Viewer3DMenuItem("Push into Cell to Edit", Enabled: false, Tip: "Comes with hierarchy editing (brief 48)."));
            }
            else items.Add(new Viewer3DMenuItem("Select Owning Object", SelectOwningObject));
            items.Add(Viewer3DMenuItem.Separator);
        }

        if (SelectMode == Scene3DSelectMode.Object && own && host is not null)
        {
            items.Add(new Viewer3DMenuItem("Rename…", () => host.ShowProperties(rename: true), Enabled: objects.Count == 1));
            var mats = host.Materials;
            // brief-em3d-53 R-em3d53-5 — ends in New Material…, so a technology with no materials is not a dead end.
            items.Add(new Viewer3DMenuItem("Material",
                Children: [.. mats.Select(m => new Viewer3DMenuItem(m, () => host.SetMaterial(objects, m))),
                           .. mats.Count > 0 ? [Viewer3DMenuItem.Separator] : Array.Empty<Viewer3DMenuItem>(),
                           new Viewer3DMenuItem("New Material…", () => host.NewMaterial(objects),
                               Tip: "Make a material in the technology or one of its libraries, and give it to the selection.")]));
            items.Add(new Viewer3DMenuItem("Assign Material…", () => host.AssignMaterial(objects),
                Tip: "Choose the material from the technology's materials — each with its role and source — or make a new one."));
        }
        if (any)
        {
            items.Add(new Viewer3DMenuItem("Hide", () => Hide(objects)));
            items.Add(new Viewer3DMenuItem("Isolate", () => Isolate(objects)));
        }
        // brief-em3d-91 — the editor's H, on whatever is selected (a record selected in the tree too).
        if (host?.HideShowSelectionItem() is { } hideShow) items.Add(hideShow);
        // brief-em3d-95 — the selection's objects as a 3D copy (the pane's Copy is the picture)
        if (any && host?.CopyObjectsItem() is { } copyObjects) items.Add(copyObjects);
        items.Add(new Viewer3DMenuItem("Show All", ShowAll));
        // brief-em3d-46 R-em3d46-6 — in the read-only viewer as in the editor, on anything or nothing.
        items.Add(new Viewer3DMenuItem(MeasureActive ? "End Measure" : "Measure", ToggleMeasure, Gesture: Viewer3DMenuItem.Plain(Key.M)));
        if (SelectMode == Scene3DSelectMode.Object && own && host is not null)
            items.Add(new Viewer3DMenuItem("Delete", () => host.DeleteSelection()));
        // brief-em3d-67 R-em3d67-4 / -5a — on an edge: its tangent chain, and the editor's Fillet… and Chamfer….
        if (SelectMode == Scene3DSelectMode.Edge && View.Selection.Length > 0 && View.Selection[^1].IsEdge)
        {
            var last = View.Selection[^1];
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Select Tangent Chain", () => SelectTangentChain(last, add: false),
                Tip: "Every edge that continues this one smoothly (tangents within 1°). Or double-click the edge; Shift adds."));
            if (host?.EdgeMenuItems().ToList() is { Count: > 0 } edgeItems) items.AddRange(edgeItems);
        }
        if (host?.DrawMenuItems().ToList() is { Count: > 0 } draw)
        {
            items.Add(Viewer3DMenuItem.Separator);
            items.AddRange(draw);
        }
        if (host is not null && any)
        {
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Properties", () => host.ShowProperties(rename: false)));
        }
        return items;
    }

    public void SelectOwningObject()
    {
        var ids = View.Selection.Select(i => i.Object).Distinct().ToList();
        SelectMode = Scene3DSelectMode.Object;
        SetSelection(ids.Select(Scene3DItem.OfObject));
    }

    public void SelectOwningInstance()
    {
        if (EditHost is not { } host) return;
        var paths = SelectedObjects().Select(host.InstanceOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        SelectMode = Scene3DSelectMode.Object;
        SetSelection(Scene.Objects.Where(o => host.InstanceOf(o) is { } p && paths.Contains(p)).Select(o => Scene3DItem.OfObject(o.Id)));
    }

    /// <summary>Hide: the editor writes the document's <c>Hidden</c> (undoable); the viewer only draws less.</summary>
    public void Hide(IReadOnlyList<Scene3DObject> objects)
    {
        if (EditHost?.SetHidden(objects, true, objects.Count == 1 ? $"Hide {objects[0].Name}" : $"Hide {objects.Count} objects") == true) return;
        foreach (var o in objects) SetVisibleEverywhere(o.Id, false);
        SetSelection([]);
    }

    public void Isolate(IReadOnlyList<Scene3DObject> objects)
    {
        if (EditHost?.ShowAll(objects) == true) return;
        var keep = objects.Select(o => o.Id).ToHashSet();
        foreach (var o in Scene.Objects.Where(o => o.Pickable)) SetVisibleEverywhere(o.Id, keep.Contains(o.Id));
    }

    /// <summary>
    /// 3D menu cleanup — 3D ▸ Select All Objects, and Ctrl/Cmd+A on the pane: Object mode, then every object that is SHOWN —
    /// solids, sheets, wires and instances' parts. A hidden object is left out (Select All is what a marquee over the whole
    /// view would take); ports, the air box's faces and air are not objects to act on, so they are left out too.
    /// </summary>
    public void SelectAllObjects()
    {
        SelectMode = Scene3DSelectMode.Object;
        HitCycle.Reset();
        CycleText = "";
        SetSelection(Scene.Objects
            .Where(o => o.Pickable && !o.PickLast && o.Kind is not (Scene3DKind.Port or Scene3DKind.Boundary or Scene3DKind.Air)
                        && View.IsVisible(o.Id))
            .Select(o => Scene3DItem.OfObject(o.Id)));
    }

    public void ShowAll()
    {
        if (EditHost?.ShowAll(null) == true) return;
        foreach (var o in Scene.Objects.Where(o => o.Pickable)) SetVisibleEverywhere(o.Id, true);
    }

    /// <summary>A visibility the view holds (not the document): the tree's checkbox follows.</summary>
    internal void SetVisibleEverywhere(uint id, bool visible)
    {
        SetVisible(id, visible);
        if (_items.TryGetValue(id, out var it)) it.Sync(visible);
    }

    /// <summary>Several visibilities at once: the kind switches are re-read and a frame requested ONCE. One at a time, each
    /// re-read scanned the whole scene, so hiding a 1,500-object board was a few million scans.</summary>
    internal void SetVisibleEverywhere(IEnumerable<(uint Id, bool Visible)> changes)
    {
        bool any = false;
        foreach (var (id, visible) in changes)
        {
            if (id < 1 || id > View.Visible.Length) continue;
            if (View.Visible[id - 1] != visible)
            {
                View.Visible[id - 1] = visible;
                VisibilityChanged?.Invoke(id, visible);
                any = true;
            }
            if (_items.TryGetValue(id, out var it)) it.Sync(visible);
        }
        if (!any) return;
        SyncKindToggles();
        FrameRequested?.Invoke();
    }

    // ── edges (brief-em3d-67) ───────────────────────────────────────────────────────────────

    /// <summary>An edge item's edge and the translation from its table to where its object is (an instance's offset).</summary>
    public (Scene3DEdge Edge, CircuitRF.Engine.Em3d.Point3 Offset)? EdgeOf(Scene3DItem item)
    {
        if (!item.IsEdge) return null;
        var fr = Scene.FeaturesOf(item.Object);
        if (fr.Table?.Named is not { } named || item.Edge >= named.Edges.Length) return null;
        return (named.Edges[item.Edge], new CircuitRF.Engine.Em3d.Point3(fr.Dx, fr.Dy, fr.Dz));
    }

    /// <summary>An edge item's run in world metres, for the overlay; null for anything else.</summary>
    public IReadOnlyList<CircuitRF.Engine.Em3d.Point3>? EdgePoints(Scene3DItem item)
        => EdgeOf(item) is { } e ? [.. e.Edge.Points.Select(p => new CircuitRF.Engine.Em3d.Point3(p.X + e.Offset.X, p.Y + e.Offset.Y, p.Z + e.Offset.Z))] : null;

    /// <summary>The selected edges' names, by object name, in selection order — what the editor's model holds.</summary>
    public IReadOnlyList<(string Object, string Edge)> SelectedEdgeNames()
        => [.. View.Selection.Where(i => i.IsEdge).Select(i => (Scene.Object(i.Object)?.Name ?? "", EdgeOf(i)?.Edge.Name ?? "")).Where(t => t.Item2.Length > 0)];

    /// <summary>The edge of scene object <paramref name="objectName"/> called <paramref name="edge"/>, or null.</summary>
    public Scene3DItem? EdgeItem(string objectName, string edge)
    {
        foreach (var o in Scene.Objects)
            if (o.Name == objectName && Scene.FeaturesOf(o.Id).Table?.Named is { } named && named.IndexOf(edge) is >= 0 and var e)
                return Scene3DItem.OfEdge(o.Id, e);
        return null;
    }

    /// <summary>The chain the last Select Tangent Chain took, for the tests.</summary>
    public Scene3DChain? LastChain { get; private set; }

    /// <summary>
    /// brief-em3d-67 R-em3d67-4 — every edge of <paramref name="edge"/>'s object reachable through vertices where the
    /// tangents differ by less than a degree; <paramref name="add"/> adds them to the selection. Stopping at a branch is
    /// said on the status line.
    /// </summary>
    public void SelectTangentChain(Scene3DItem edge, bool add)
    {
        if (Scene.FeaturesOf(edge.Object).Table?.Named is not { } named || !edge.IsEdge) return;
        var chain = Scene3DEdgeChain.Walk(named, edge.Edge);
        LastChain = chain;
        var items = chain.Edges.Select(e => Scene3DItem.OfEdge(edge.Object, e)).ToList();
        SetSelection(add ? [.. View.Selection, .. items] : items);
        string text = chain.StoppedAtBranch ? $"Stopped at a branch: {chain.BranchCount} edges" : $"Tangent chain: {items.Count} edge{(items.Count == 1 ? "" : "s")}";
        if (EditHost is { } h) h.Say(text); else CycleText = text;
    }

    /// <summary>Called from Fit: counted for gate 8.</summary>
    private void CountFit() => Fits++;
}
