// brief-em3d-28 R-em3d28-2d / R-em3d28-3 / R-em3d28-4 — the 3D view's state: what scene it shows,
// which overlays are on, what the toolbar's buttons do, and the object tree.
//
// THE THREE LOOPS (em-3d.md §8.4), and which of them this class is in:
//   * the INPUT loop is here, on the UI thread — pointer and key events become small changes to the
//     Viewer3DViewState (a camera, a cursor, a flag) and a request for a frame. It computes no geometry;
//     gate 5 counts that a thousand hovers cause no tessellation and no problem generation.
//   * the FRAME loop is the pane's render thread (Viewer3DPane), which reads that state;
//   * the KERNEL loop is Scene3DSource: a change to the .cem, the layout, the technology or the .wBond
//     regenerates the problem in the background, and the view keeps drawing the last finished scene.
//
// Read only (R-em3d28-5): nothing here writes the .cem. F4 is the editor.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>A snapshot the UI thread takes for one regeneration: the setup (a copy), its resolved
/// layout, and how to paint. The build never reads live state through anything else.</summary>
public sealed record Viewer3DInputs(EmSetup Setup, EmLayoutSource? Source, string? Refusal,
                                    ColorTheme Theme, ColorVariant Variant);

/// <summary>One object in the tree, with its visibility toggle.</summary>
public sealed partial class Viewer3DTreeItem(Viewer3DViewModel owner, Scene3DObject obj) : ObservableObject
{
    public uint Id { get; } = obj.Id;
    public string Name { get; } = obj.Name;
    public Scene3DKind Kind { get; } = obj.Kind;
    public string? Detail { get; } = obj.Material;

    [ObservableProperty] private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value) => owner.SetVisible(Id, value);

    /// <summary>Set without calling back — the owner changed it.</summary>
    internal void Sync(bool visible)
    {
#pragma warning disable MVVMTK0034
        if (_isVisible == visible) return;
        _isVisible = visible;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsVisible));
    }
}

/// <summary>A group of the tree: solids by kind, then ports, then the boundary.</summary>
public sealed class Viewer3DTreeGroup(string header, IEnumerable<Viewer3DTreeItem> items)
{
    public string Header { get; } = header;
    public ObservableCollection<Viewer3DTreeItem> Items { get; } = [.. items];
}

public sealed partial class Viewer3DViewModel : ObservableObject, IDisposable
{
    /// <summary>A regeneration waits this long after the last change, so a burst of edits is one build.</summary>
    public const int RegenerateDebounceMs = 250;

    private readonly Func<Viewer3DInputs> _prepare;
    private readonly Func<string?> _resultsRoot;
    private readonly Action<Action> _post;
    private System.Threading.Timer? _debounce;
    private IReadOnlyList<string>? _objectNames;
    private bool _fitted;
    private CancellationTokenSource? _overlayCts;
    private MshMesh? _mesh;
    /// <summary>The file _mesh was read from, AS IT WAS: a re-run rewrites the same path, so the path
    /// alone would keep showing the old mesh and its old count.</summary>
    private MeshStamp? _meshStamp;
    private MeshStamp? _meshLoading;
    /// <summary>The FDTD grid and the scene it was built for. Written by whichever overlay task builds
    /// it first, cancelled or not — a clip-plane drag cancels every task before its successor, and a
    /// cache filled only on completion would rebuild the grid on every tick of the drag.</summary>
    private (Scene3DModel Scene, FdtdGridResult Grid)? _grid;
    private long _overlayVersion;
    private bool _disposed;

    private sealed record MeshStamp(string Path, DateTime WrittenUtc, long Length)
    {
        public static MeshStamp? Of(string path)
        {
            try { var f = new FileInfo(path); return f.Exists ? new MeshStamp(path, f.LastWriteTimeUtc, f.Length) : null; }
            catch (Exception) { return null; }
        }
    }

    public string CemPath { get; }
    public string Title { get; }
    public Viewer3DViewState View { get; } = new();
    public Viewer3DSession Session { get; }
    public Scene3DSource Source { get; }

    /// <summary>The scene the frame loop draws — the last FINISHED generation.</summary>
    public Scene3DModel Scene { get; private set; } = Scene3DModel.Empty();

    public Scene3DOverlay MeshOverlay { get; private set; } = Scene3DOverlay.None;
    public Scene3DOverlay SectionOverlay { get; private set; } = Scene3DOverlay.None;
    public Scene3DOverlay GridOverlay { get; private set; } = Scene3DOverlay.None;
    public IReadOnlyList<FdtdCellLabel> GridLabels { get; private set; } = [];

    /// <summary>Formats a length, metres, in the layout's display unit.</summary>
    public Func<double, string> FormatLength { get; private set; } = m => (m * 1e6).ToString("G4", CultureInfo.InvariantCulture) + " µm";

    /// <summary>The pane asks for a frame when anything it draws changed.</summary>
    public event Action? FrameRequested;

    /// <summary>The tree should scroll to this item (a click in the view selected it).</summary>
    public event Action<Viewer3DTreeItem>? RevealRequested;

    public ObservableCollection<Viewer3DTreeGroup> Tree { get; } = [];

    public Viewer3DViewModel(string cemPath, Func<Viewer3DInputs> prepare, Func<Viewer3DBackend> backend,
                             Func<string?> resultsRoot, Action<Action> post)
    {
        CemPath = cemPath;
        Title = Path.GetFileNameWithoutExtension(cemPath) + " — 3D";
        _prepare = prepare;
        _resultsRoot = resultsRoot;
        _post = post;
        Session = new Viewer3DSession(backend);
        Source = new Scene3DSource(Build);
        Source.SceneReady += s => _post(() => Adopt(s));
    }

    // ── regeneration ────────────────────────────────────────────────────────────────────────

    /// <summary>Asks for a new scene now, from a snapshot taken on this (the UI) thread.</summary>
    public void Regenerate()
    {
        if (_disposed) return;
        var inputs = _prepare();
        long gen = Source.Request(inputs);
        _inputs[gen] = inputs;
        IsRegenerating = true;
    }

    /// <summary>A .cem, .clay, .ctech or .wBond changed: regenerate after the burst settles.</summary>
    public void Invalidate()
    {
        if (_disposed) return;
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => _post(Regenerate), null, RegenerateDebounceMs, Timeout.Infinite);
    }

    /// <summary>The snapshot each generation was built from — UI thread only.</summary>
    private readonly Dictionary<long, Viewer3DInputs> _inputs = [];
    private EmSetup? _lastSetup;
    private EmLayoutSource? _lastSource;

    private static Scene3DModel Build(long gen, object? state, CancellationToken ct)
    {
        var inputs = (Viewer3DInputs)state!;
        if (inputs.Refusal is not null || inputs.Source is not { } source) return Scene3DModel.Empty(gen, [inputs.Refusal ?? "no layout"]);
        if (source.Technology is not { } tech) return Scene3DModel.Empty(gen, [EmDiagnostics.NoTechnology(inputs.Setup.LayoutRef).Render()]);
        if (!inputs.Setup.Is3D) return BuildPlanar(gen, inputs, source, tech, ct);
        var g = Em3dGenerator.Generate(inputs.Setup, source, tech);
        ct.ThrowIfCancellationRequested();
        var notes = new List<string>();
        if (g.Problem is null && inputs.Setup.HasWavePorts3D)
        {
            // A view exists to show what is about to be solved, and a refused wave port is the moment
            // a user most needs to SEE the layout: so the geometry is drawn with every wave port as a
            // lumped one, and the refusal is stated first, verbatim, so nobody mistakes the picture for
            // a problem that would run.
            var preview = inputs.Setup.Clone();
            preview.Ports3D = [.. preview.Ports3D.Select(p => p with { Kind = Em3dPortKind.Lumped })];
            var lumped = Em3dGenerator.Generate(preview, source, tech);
            if (lumped.Problem is not null)
            {
                notes.Add("This setup would not run: " + g.Refusal);
                notes.Add("Shown for inspection with its wave ports drawn as lumped ports.");
                g = lumped;
            }
        }
        if (g.Problem is null) return Scene3DModel.Empty(gen, [g.Refusal ?? "the 3D problem could not be built."]);
        notes.AddRange(g.Warnings);
        notes.AddRange(g.Notes);
        return Scene3DBuilder.Build(g.Problem, gen, g.Origins, tech, inputs.Theme, inputs.Variant, notes);
    }

    /// <summary>
    /// A PLANAR setup, for a look in 3D: its layout through the stackup, as a driven 3D problem with every
    /// port lumped would be built — the air box at the generator's default and the dielectrics finite.
    /// Nothing about a 3D solve is asked of it, so the generator's notes on one are not repeated; the
    /// single note says what the picture is.
    /// </summary>
    private static Scene3DModel BuildPlanar(long gen, Viewer3DInputs inputs, EmLayoutSource source,
                                            Technology tech, CancellationToken ct)
    {
        var preview = inputs.Setup.Clone();
        preview.Solver3D = Em3dSolver.Palace;
        preview.Problem3D = Em3dProblemType.Driven;
        preview.Ports3D = [];
        var g = Em3dGenerator.Generate(preview, source, tech);
        ct.ThrowIfCancellationRequested();
        if (g.Problem is null) return Scene3DModel.Empty(gen, [g.Refusal ?? "the layout could not be built in 3D."]);
        return Scene3DBuilder.Build(g.Problem, gen, g.Origins, tech, inputs.Theme, inputs.Variant,
                                    ["Planar setup, shown in 3D."]);
    }

    /// <summary>UI thread: the newest scene arrived. Keeps the user's toggles, fits the first one.</summary>
    internal void Adopt(Scene3DModel scene)
    {
        if (_disposed || scene.Generation < Scene.Generation) return;
        if (_inputs.TryGetValue(scene.Generation, out var inputs))
        {
            _lastSetup = inputs.Setup;
            _lastSource = inputs.Source;
        }
        foreach (long old in _inputs.Keys.Where(k => k <= scene.Generation).ToList()) _inputs.Remove(old);
        View.Adopt(scene, _objectNames);
        _objectNames = [.. scene.Objects.Select(o => o.Name)];
        Scene = scene;
        IsRegenerating = scene.Generation < Source.Requested;
        // An empty scene's first note is its refusal, which Status already says.
        Notes = string.Join("\n", scene.Objects.Length == 0 ? scene.Notes.Skip(1) : scene.Notes);
        if (_lastSource is { } src)
        {
            var unit = src.View.DisplayUnit;
            int dbu = src.DbuPerMicron;
            var f = EmLengthFormat.For(unit, dbu);
            FormatLength = m => f(m);
        }
        if (!_fitted && scene.Objects.Length > 0)
        {
            View.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, _aspect, View.Camera.Projection);
            if (_pendingCamera is { } c) { ApplyCamera(c); _pendingCamera = null; }
            View.Camera.SceneCentre = (scene.BoundsMin + scene.BoundsMax) * 0.5f;
            View.Camera.SceneRadius = (scene.BoundsMax - scene.BoundsMin).Length() * 0.5f;
            _fitted = true;
        }
        else
        {
            View.Camera.SceneCentre = (scene.BoundsMin + scene.BoundsMax) * 0.5f;
            View.Camera.SceneRadius = (scene.BoundsMax - scene.BoundsMin).Length() * 0.5f;
        }
        RebuildTree();
        RefreshSolverOverlays();
        OnPropertyChanged(nameof(Status));
        FrameRequested?.Invoke();
    }

    // ── the object tree (R-em3d28-4c) ───────────────────────────────────────────────────────

    private readonly Dictionary<uint, Viewer3DTreeItem> _items = [];

    private void RebuildTree()
    {
        Tree.Clear();
        _items.Clear();
        (string Header, Scene3DKind[] Kinds)[] groups =
        [
            ("Conductors", [Scene3DKind.Conductor, Scene3DKind.Via, Scene3DKind.Sheet]),
            ("Wires", [Scene3DKind.Wire]),
            ("Dielectrics", [Scene3DKind.Dielectric]),
            ("Bodies", [Scene3DKind.Body]),
            ("Air", [Scene3DKind.Air]),
            ("Ports", [Scene3DKind.Port]),
            ("Boundary", [Scene3DKind.Boundary]),
        ];
        foreach (var (header, kinds) in groups)
        {
            var items = Scene.Objects.Where(o => kinds.Contains(o.Kind)).Select(o =>
            {
                var it = new Viewer3DTreeItem(this, o);
                it.Sync(View.IsVisible(o.Id));
                _items[o.Id] = it;
                return it;
            }).ToList();
            if (items.Count > 0) Tree.Add(new Viewer3DTreeGroup(header, items));
        }
        SyncKindToggles();
    }

    internal void SetVisible(uint id, bool visible)
    {
        if (id < 1 || id > View.Visible.Length || View.Visible[id - 1] == visible) return;
        View.Visible[id - 1] = visible;
        SyncKindToggles();
        FrameRequested?.Invoke();
    }

    private void SetKindVisible(Func<Scene3DObject, bool> which, bool visible)
    {
        foreach (var o in Scene.Objects.Where(which))
        {
            View.Visible[o.Id - 1] = visible;
            if (_items.TryGetValue(o.Id, out var it)) it.Sync(visible);
        }
        FrameRequested?.Invoke();
    }

    private bool _syncingKinds;

    private void SyncKindToggles()
    {
        _syncingKinds = true;
        ShowDielectrics = Scene.Objects.Any(o => o.Kind == Scene3DKind.Dielectric && View.IsVisible(o.Id));
        ShowAir = Scene.Objects.Any(o => o.Kind == Scene3DKind.Air && View.IsVisible(o.Id));
        ShowBoundaryFaces = Scene.Objects.Any(o => o.Kind == Scene3DKind.Boundary && o.Name != "airbox" && View.IsVisible(o.Id));
        _syncingKinds = false;
    }

    [ObservableProperty] private bool _showDielectrics;
    [ObservableProperty] private bool _showAir;
    [ObservableProperty] private bool _showBoundaryFaces;

    partial void OnShowDielectricsChanged(bool value) { if (!_syncingKinds) SetKindVisible(o => o.Kind == Scene3DKind.Dielectric, value); }
    partial void OnShowAirChanged(bool value) { if (!_syncingKinds) SetKindVisible(o => o.Kind == Scene3DKind.Air, value); }
    partial void OnShowBoundaryFacesChanged(bool value) { if (!_syncingKinds) SetKindVisible(o => o.Kind == Scene3DKind.Boundary && o.Name != "airbox", value); }

    [ObservableProperty] private Viewer3DTreeItem? _selectedItem;

    partial void OnSelectedItemChanged(Viewer3DTreeItem? value)
    {
        View.Selected = value?.Id ?? 0;
        FrameRequested?.Invoke();
    }

    // ── status and hover ────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRegenerating;

    partial void OnIsRegeneratingChanged(bool value) => OnPropertyChanged(nameof(Status));
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _meshText = "";

    public string Status => Scene.Objects.Length == 0
        ? (Scene.Notes.Count > 0 ? "Nothing to show: " + Scene.Notes[0] : "Generating the 3D problem…")
        : $"{Scene.Objects.Length} objects, {Scene.TriangleCount:N0} triangles, {Scene.Batches.Length} draws (generation {Scene.Generation})"
          + (IsRegenerating ? " — regenerating…" : "");

    /// <summary>Frame loop → UI: the ID pass's answer arrived. Updates the tooltip and the dimension
    /// under the cursor. Touches no geometry.</summary>
    internal void OnPicked(uint id, Vector3 point, bool hit)
    {
        if (View.Hovered != id)
        {
            View.Hovered = id;
            HoverText = Describe(Scene.Object(id));
            FrameRequested?.Invoke();
        }
        if (hit)
        {
            var (x, y, z) = Scene.ToWorld(point);
            CursorText = $"x {FormatLength(x)}   y {FormatLength(y)}   z {FormatLength(z)}";
        }
        else CursorText = "";
    }

    /// <summary>The tooltip: name, material, and the values at the setup's operating temperature.</summary>
    internal string Describe(Scene3DObject? o)
    {
        if (o is null) return "";
        var t = o.Kind switch { Scene3DKind.Port => $"Port {o.PortNumber}  {o.Name}", _ => o.Name };
        if (o.MaterialValues is { } m)
        {
            string at = Scene.Problem is { } p ? $" at {p.OperatingTempC.ToString("G4", CultureInfo.InvariantCulture)} °C" : "";
            t += $"\n{m.Name}: εr {m.Epsr.ToString("G4", CultureInfo.InvariantCulture)}, tanδ {m.TanD.ToString("G3", CultureInfo.InvariantCulture)}, " +
                 $"σ {m.SigmaSm.ToString("G3", CultureInfo.InvariantCulture)} S/m{at}";
        }
        else if (o.Boundary is { } b) t += $"\n{b}";
        return t;
    }

    // ── input (the UI thread's whole job) ───────────────────────────────────────────────────

    private float _aspect = 1.6f;

    public void Resized(float width, float height) { if (height > 0) _aspect = width / height; }

    public void Hover(float x, float y)
    {
        View.CursorX = x; View.CursorY = y;
        FrameRequested?.Invoke();
    }

    public void Leave()
    {
        View.CursorX = View.CursorY = -1;
        if (View.Hovered != 0) { View.Hovered = 0; HoverText = ""; }
        CursorText = "";
        FrameRequested?.Invoke();
    }

    public void Orbit(float dx, float dy) { View.Camera.Orbit(dx, dy); View.Orbiting = true; FrameRequested?.Invoke(); }

    public void Pan(float dx, float dy, float height) { View.Camera.Pan(dx, dy, height); View.Orbiting = true; FrameRequested?.Invoke(); }

    public void Zoom(float notches, float x, float y, float w, float h)
    {
        View.Camera.ZoomAt(notches, x, y, w, h);
        View.Orbiting = true;
        FrameRequested?.Invoke();
    }

    /// <summary>A click selects what is under the cursor, and the tree scrolls to it.</summary>
    public void Click()
    {
        uint id = View.Hovered;
        var item = id != 0 && _items.TryGetValue(id, out var it) ? it : null;
        SelectedItem = item;
        if (item is not null) RevealRequested?.Invoke(item);
    }

    // ── the toolbar ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isPerspective = true;

    partial void OnIsPerspectiveChanged(bool value)
    {
        View.Camera.Projection = value ? Projection3D.Perspective : Projection3D.Orthographic;
        OnPropertyChanged(nameof(IsOrthographic));
        FrameRequested?.Invoke();
    }

    public bool IsOrthographic { get => !IsPerspective; set => IsPerspective = !value; }

    [RelayCommand] private void Fit()
    {
        View.Camera.FitBounds(Scene.ContentMin, Scene.ContentMax, _aspect);
        View.Camera.SceneCentre = (Scene.BoundsMin + Scene.BoundsMax) * 0.5f;
        View.Camera.SceneRadius = (Scene.BoundsMax - Scene.BoundsMin).Length() * 0.5f;
        FrameRequested?.Invoke();
    }

    /// <summary>A standard view. The six plan views and the iso view are orthographic — an isometric
    /// view is by definition — and Perspective turns perspective back on from wherever the camera is.</summary>
    [RelayCommand] private void StandardView(StandardView3D view)
    {
        View.Camera.SetStandardView(view);
        IsPerspective = false;
        Fit();
    }

    [RelayCommand] private void Perspective() => IsPerspective = true;
    [RelayCommand] private void Orthographic() => IsPerspective = false;

    [ObservableProperty] private bool _showAxisIndicator = true;
    partial void OnShowAxisIndicatorChanged(bool value) { View.ShowAxisIndicator = value; FrameRequested?.Invoke(); }

    [ObservableProperty] private bool _showTree = true;

    // Clip plane (R-em3d28-4d).
    [ObservableProperty] private bool _clipEnabled;
    [ObservableProperty] private ClipAxis3D _clipAxis = ClipAxis3D.Z;
    /// <summary>0..1 across the scene's extent along the plane's normal.</summary>
    [ObservableProperty] private double _clipPosition = 0.5;
    [ObservableProperty] private bool _clipFlip;

    public IReadOnlyList<ClipAxis3D> ClipAxes { get; } = [ClipAxis3D.X, ClipAxis3D.Y, ClipAxis3D.Z, ClipAxis3D.View];

    partial void OnClipEnabledChanged(bool value) => ApplyClip();
    partial void OnClipAxisChanged(ClipAxis3D value) => ApplyClip();
    partial void OnClipPositionChanged(double value) => ApplyClip();
    partial void OnClipFlipChanged(bool value) => ApplyClip();

    private void ApplyClip()
    {
        var c = View.Clip;
        c.Enabled = ClipEnabled;
        if (ClipAxis == ClipAxis3D.View && (c.Axis != ClipAxis3D.View || c.ViewNormal == Vector3.Zero))
            c.ViewNormal = View.Camera.Forward;
        c.Axis = ClipAxis;
        c.Flip = ClipFlip;
        var (lo, hi) = c.Range(Scene.BoundsMin, Scene.BoundsMax);
        c.Offset = (float)(lo + (hi - lo) * ClipPosition);
        View.Clip = c;
        View.ShowMeshSection = ShowMesh && ClipEnabled;
        FrameRequested?.Invoke();
        ScheduleClipOverlays();
    }

    // Overlays (R-em3d28-3).
    [ObservableProperty] private bool _meshAvailable;

    /// <summary>Opened from a Palace run directory: turn the mesh on as soon as the setup's scene lands.</summary>
    public bool ShowMeshWhenAvailable { get; set; }
    [ObservableProperty] private bool _gridAvailable;
    [ObservableProperty] private bool _showMesh;
    [ObservableProperty] private bool _showGrid;

    public string MeshTip => MeshAvailable ? "Show the mesh Gmsh made (boundary triangles, and the tetrahedra the clip plane cuts)"
                           : _lastSetup is { Is3D: false } ? "The planar mesh is shown in the layout view"
                           : "Simulate to mesh";
    public string GridTip => GridAvailable ? "Show the FDTD grid on the clip plane and where it meets the metal"
                                           : "The FDTD grid is shown for an openEMS setup";

    partial void OnMeshAvailableChanged(bool value) => OnPropertyChanged(nameof(MeshTip));
    partial void OnGridAvailableChanged(bool value) => OnPropertyChanged(nameof(GridTip));

    partial void OnShowMeshChanged(bool value)
    {
        View.ShowMesh = value;
        View.ShowMeshSection = value && ClipEnabled;
        if (value) LoadMesh();
        FrameRequested?.Invoke();
    }

    partial void OnShowGridChanged(bool value) { View.ShowGrid = value; ScheduleClipOverlays(); FrameRequested?.Invoke(); }

    /// <summary>The Gmsh mesh beside this setup's Palace run, if there is one. It exists only after
    /// Gmsh has run (R-em3d28-3b).</summary>
    public string? MeshPath()
    {
        if (_lastSetup is not { } setup || _resultsRoot() is not { } root) return null;
        if (setup.Solver3D is not (Em3dSolver.Palace or Em3dSolver.Both)) return null;
        string p = Path.Combine(Em3dRunService.RunDirectory(root, setup, Em3dSolver.Palace), GmshGeoWriter.MeshFile);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Re-reads what the setup's solver produced: the mesh file (a run may have made one) and
    /// the FDTD grid.</summary>
    public void RefreshSolverOverlays()
    {
        MeshAvailable = MeshPath() is not null;
        OnPropertyChanged(nameof(MeshTip));
        if (MeshAvailable && ShowMeshWhenAvailable) { ShowMeshWhenAvailable = false; ShowMesh = true; }
        if (!MeshAvailable && ShowMesh) ShowMesh = false;
        if (_meshStamp is not null && (MeshPath() is not { } now || MeshStamp.Of(now) != _meshStamp))
        {
            _mesh = null; _meshStamp = null;
            MeshText = "";
        }
        if (ShowMesh) LoadMesh();

        GridAvailable = _lastSetup?.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both && Scene.Problem is not null;
        if (!GridAvailable && ShowGrid) ShowGrid = false;
        ScheduleClipOverlays();
    }

    /// <summary>The mesh is in GmshGeoWriter's units (Palace's L0).</summary>
    public const double MeshToMetres = GmshGeoWriter.LengthUnitM;

    private void LoadMesh()
    {
        if (MeshPath() is not { } path) return;
        var scene = Scene;
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        var stamp = MeshStamp.Of(path);
        if (_mesh is not null && stamp is not null && _meshStamp == stamp)
        {
            MeshOverlay = new Scene3DOverlay(Render.Scene3D.MeshOverlay.BoundaryWireframe(_mesh, scene, MeshToMetres, dark), ++_overlayVersion);
            ScheduleClipOverlays();
            return;
        }
        if (stamp is not null && _meshLoading == stamp) return;      // already being read
        _meshLoading = stamp;
        MeshText = "Reading the mesh…";
        Task.Run(() =>
        {
            try
            {
                var m = MshReader.Read(path);
                var lines = Render.Scene3D.MeshOverlay.BoundaryWireframe(m, scene, MeshToMetres, dark);
                _post(() =>
                {
                    if (_meshLoading == stamp) _meshLoading = null;
                    if (_disposed) return;
                    _mesh = m; _meshStamp = stamp;
                    MeshOverlay = new Scene3DOverlay(lines, ++_overlayVersion);
                    MeshText = $"Mesh: {m.TetCount:N0} tetrahedra, {m.TriangleCount:N0} boundary triangles, {m.NodeCount:N0} nodes";
                    ScheduleClipOverlays();
                    FrameRequested?.Invoke();
                });
            }
            catch (Exception ex)
            {
                _post(() =>
                {
                    if (_meshLoading == stamp) _meshLoading = null;
                    MeshText = "The mesh could not be read: " + ex.Message;
                });
            }
        });
    }

    /// <summary>The clip-dependent overlays — the mesh's section and the grid — rebuilt off the UI
    /// thread when the plane or the scene moves; a newer request cancels an older one.</summary>
    private void ScheduleClipOverlays()
    {
        _overlayCts?.Cancel();
        var cts = _overlayCts = new CancellationTokenSource();
        var scene = Scene;
        var clip = View.Clip;
        var mesh = _mesh;
        bool wantSection = ShowMesh && ClipEnabled && mesh is not null;
        bool wantGrid = ShowGrid && GridAvailable && scene.Problem is not null;
        var setup = _lastSetup;
        var grid = _grid is { } cached && ReferenceEquals(cached.Scene, scene) ? cached.Grid : null;
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        if (!wantSection) SectionOverlay = Scene3DOverlay.None;
        if (!wantGrid) { GridOverlay = Scene3DOverlay.None; GridLabels = []; OnPropertyChanged(nameof(GridLabels)); }
        if (!wantSection && !wantGrid) { FrameRequested?.Invoke(); return; }
        Task.Run(() =>
        {
            try
            {
                Scene3DVertex[]? section = null;
                if (wantSection)
                    section = Render.Scene3D.MeshOverlay.Section(mesh!, scene, MeshToMetres, clip,
                        dark ? Scene3DVertex.Pack(255, 210, 90, 255) : Scene3DVertex.Pack(170, 90, 0, 255), cts.Token);
                FdtdGridDrawing? drawing = null;
                if (wantGrid)
                {
                    if (grid is null)
                    {
                        grid = FdtdGrid.Build(scene.Problem!, CemOpenEms.ResolveGrid(setup?.OpenEms));
                        var built = grid;
                        _post(() => { if (ReferenceEquals(Scene, scene)) _grid = (scene, built); });
                    }
                    drawing = FdtdGridOverlay.Build(grid, scene, clip, dark, cts.Token);
                }
                if (cts.IsCancellationRequested) return;
                _post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    if (section is not null) SectionOverlay = new Scene3DOverlay(section, ++_overlayVersion);
                    if (drawing is not null)
                    {
                        GridOverlay = new Scene3DOverlay(drawing.Lines, ++_overlayVersion);
                        GridLabels = drawing.Labels;
                        OnPropertyChanged(nameof(GridLabels));
                    }
                    FrameRequested?.Invoke();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _post(() => Notes = "The grid or mesh section could not be drawn: " + ex.Message); }
        });
    }

    // ── camera persistence (R-em3d28-5: the workspace's window state, not the .cem) ─────────

    private Design.Workspace.CwsCamera3D? _pendingCamera;

    /// <summary>A camera stored for this setup, applied when the first scene lands.</summary>
    public void RestoreCamera(Design.Workspace.CwsCamera3D? stored)
    {
        if (stored is null) return;
        if (_fitted) ApplyCamera(stored); else _pendingCamera = stored;
    }

    private void ApplyCamera(Design.Workspace.CwsCamera3D c)
    {
        // A stored camera is only as good as the session that wrote it. One written by a view that
        // never had a scene held a zero distance — and zoom MULTIPLIES the distance, so the restored
        // view could never zoom (owner report, 2026-09-25). Anything not finite and positive keeps
        // the fit instead.
        static bool Ok(double v) => double.IsFinite(v);
        if (!(c.Distance > 0) || !Ok(c.Distance) || !Ok(c.Yaw) || !Ok(c.Pitch) ||
            !Ok(c.TargetX) || !Ok(c.TargetY) || !Ok(c.TargetZ))
            return;
        View.Camera.Target = new Vector3((float)c.TargetX, (float)c.TargetY, (float)c.TargetZ);
        View.Camera.Yaw = (float)Math.IEEERemainder(c.Yaw, 2 * Math.PI);
        View.Camera.Pitch = (float)Math.Clamp(c.Pitch, -Math.PI / 2, Math.PI / 2);     // as Orbit keeps it
        View.Camera.Distance = (float)c.Distance;
        IsPerspective = !c.Orthographic;
        View.Camera.Projection = c.Orthographic ? Projection3D.Orthographic : Projection3D.Perspective;
    }

    /// <summary>The camera as the workspace stores it — or null while no scene has ever been framed,
    /// when the camera is only the placeholder and is not the user's.</summary>
    public Design.Workspace.CwsCamera3D? CameraToPersist()
    {
        if (!_fitted) return _pendingCamera;
        var c = View.Camera;
        // JSON cannot write a NaN or an infinity (the whole .cwsuser save would throw), and a camera
        // that is not finite is not one worth restoring anyway.
        if (!float.IsFinite(c.Target.X) || !float.IsFinite(c.Target.Y) || !float.IsFinite(c.Target.Z) ||
            !float.IsFinite(c.Yaw) || !float.IsFinite(c.Pitch) || !(c.Distance > 0) || !float.IsFinite(c.Distance))
            return null;
        return new Design.Workspace.CwsCamera3D
        {
            TargetX = c.Target.X, TargetY = c.Target.Y, TargetZ = c.Target.Z,
            Yaw = c.Yaw, Pitch = c.Pitch, Distance = c.Distance,
            Orthographic = c.Projection == Projection3D.Orthographic,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Dispose();
        _overlayCts?.Cancel();
        Source.Dispose();
        Session.Dispose();
    }
}
