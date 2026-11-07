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
// Read only (R-em3d28-5): nothing here writes the .cem. The .c3d editor (brief-em3d-43, src/Ui/ThreeD) is
// this same view model built with a scene builder of its own and an IViewer3DEditHost — one pane, two uses.

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

    private readonly Func<object?> _prepare;
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
    private (MeshStamp?, object?, bool)? _meshLoading;
    private (MshMesh?, object?, bool) _meshDrawn;
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

    /// <summary>brief-em3d-116 R-em3d116-5 — the wave-port feeds' source and reference planes, named where they are drawn.</summary>
    public IReadOnlyList<FdtdFeedLabel> GridFeedLabels { get; private set; } = [];

    /// <summary>Formats a length, metres, in the layout's display unit.</summary>
    public Func<double, string> FormatLength { get; private set; } = m => (m * 1e6).ToString("G4", CultureInfo.InvariantCulture) + " µm";

    /// <summary>The pane asks for a frame when anything it draws changed.</summary>
    public event Action? FrameRequested;

    /// <summary>brief-em3d-45 — the editor changed something the pane draws (the grid, the drawing's overlay).</summary>
    public void RequestFrame() => FrameRequested?.Invoke();

    /// <summary>The tree should scroll to this item (a click in the view selected it).</summary>
    public event Action<Viewer3DTreeItem>? RevealRequested;

    public ObservableCollection<Viewer3DTreeGroup> Tree { get; } = [];

    public Viewer3DViewModel(string cemPath, Func<Viewer3DInputs> prepare, Func<Viewer3DBackend> backend,
                             Func<string?> resultsRoot, Action<Action> post)
        : this(cemPath, Path.GetFileNameWithoutExtension(cemPath) + " — 3D", () => prepare(), Build, backend, resultsRoot, post)
    {
    }

    /// <summary>brief-em3d-43 — a pane whose scene comes from <paramref name="build"/>, handed the snapshot
    /// <paramref name="snapshot"/> takes on the UI thread for each generation (the .c3d editor's elaboration).</summary>
    public Viewer3DViewModel(string path, string title, Func<object?> snapshot,
                             Func<long, object?, CancellationToken, Scene3DModel> build, Func<Viewer3DBackend> backend,
                             Func<string?> resultsRoot, Action<Action> post)
    {
        CemPath = path;
        Title = title;
        _layers = [_own];                          // brief-em3d-96 — with no plot, the view's own field layer
        _focused = _own;
        _prepare = snapshot;
        _resultsRoot = resultsRoot;
        _post = post;
        Session = new Viewer3DSession(backend);
        Source = new Scene3DSource(build);
        Source.SceneReady += s => _post(() => Adopt(s));
        Source.BuildFailed += (gen, ex) => _post(() => BuildFailed(gen, ex));
    }

    /// <summary>Messages for the Messages panel — (text, is a warning). The shell posts them; a test reads them.</summary>
    public Action<string, bool>? PostMessage { get; set; }

    private string? _lastFailure;

    /// <summary>
    /// UI thread: a build threw, or the newest one ended with no scene (R-dfr31-5-3). One Messages line per failure — a
    /// burst of the same one is said once — and the building state ends when it was the newest generation, whatever else
    /// follows: a lost GPU context once left "Building the 3D view…" up until the app was restarted.
    /// </summary>
    private void BuildFailed(long gen, Exception ex)
    {
        if (_disposed) return;
        string text = $"The 3D view could not build its scene: {ex.GetBaseException().Message}";
        if (text != _lastFailure) PostMessage?.Invoke(text, true);
        _lastFailure = text;
        if (gen >= Source.Requested) IsRegenerating = false;
    }

    /// <summary>R-dfr31-5-3 — how long a first scene may build, with nothing drawn, before the bar offers Retry.</summary>
    public static TimeSpan FirstSceneStallAfter { get; set; } = TimeSpan.FromSeconds(30);

    private System.Threading.Timer? _stallTimer;

    /// <summary>
    /// R-dfr31-5-3 — the first scene has been building for <see cref="FirstSceneStallAfter"/> with nothing drawn: the bar
    /// says the view did not finish and offers Retry, rather than building for ever.
    /// </summary>
    [ObservableProperty] private bool _isFirstSceneStalled;

    partial void OnIsFirstSceneStalledChanged(bool value) => OnPropertyChanged(nameof(IsBuildingFirstScene));

    /// <summary>Why the pane showing this view cannot draw, or null while it can — the pane sets it.</summary>
    public string? PaneFault { get; set; }

    /// <summary>brief-dfr34-5 R-dfr34-5-2 — asks the pane to rebuild its surface and device (<see cref="Viewer3DPane.Reconnect"/>).
    /// Raised on the UI thread.</summary>
    public event Action? ReconnectRequested;

    /// <summary>The stalled bar's Retry: builds the first scene again from a fresh snapshot, and reconnects a pane that has
    /// a GPU fault (R-dfr34-5-2), so closing and reopening is never the only way back.</summary>
    [RelayCommand]
    private void RetryFirstScene()
    {
        IsFirstSceneStalled = false;
        _lastFailure = null;
        if (PaneFault is not null) ReconnectRequested?.Invoke();
        Regenerate();
    }

    /// <summary>The fault text's Reconnect (R-dfr34-5-2).</summary>
    [RelayCommand]
    private void ReconnectPane() => ReconnectRequested?.Invoke();

    private void ArmStallTimer()
    {
        _stallTimer?.Dispose();
        _stallTimer = null;
        if (_disposed || !IsRegenerating || Scene.Objects.Length > 0) return;
        _stallTimer = new System.Threading.Timer(_ => _post(() =>
        {
            if (!_disposed && IsRegenerating && Scene.Objects.Length == 0) IsFirstSceneStalled = true;
        }), null, FirstSceneStallAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised on the UI thread after a new scene has been adopted.</summary>
    public event Action? SceneAdopted;

    /// <summary>brief-em3d-43 — the editor's lengths follow its document's display unit.</summary>
    public void SetLengthFormat(Func<double, string> format)
    {
        FormatLength = format;
        RefreshLengthTexts();
    }

    // ── regeneration ────────────────────────────────────────────────────────────────────────

    /// <summary>Asks for a new scene now, from a snapshot taken on this (the UI) thread.</summary>
    public void Regenerate()
    {
        if (_disposed) return;
        var inputs = _prepare();
        // Set before the request: a build that ends at once, with a post that runs inline, clears it before Request returns.
        IsRegenerating = true;
        long gen = Source.Request(inputs);
        if (inputs is Viewer3DInputs vi) _inputs[gen] = vi;
        ArmStallTimer();
    }

    /// <summary>brief-em3d-108 — asks for a new scene from <paramref name="state"/> rather than a fresh snapshot: the 3D editor's
    /// display-only rebuild, which hands its build the elaboration it already has.</summary>
    public void RegenerateWith(object state)
    {
        if (_disposed) return;
        IsRegenerating = true;
        Source.Request(state);
        ArmStallTimer();
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
        // brief-dfr26-1 R-dfr26-1-5 — ports that refuse do not blank the picture of a 3D setup either: the geometry is
        // drawn without them and the refusal is the banner. A solve still refuses (EmRunService never asks for this).
        // brief-lcp-5 R-lcp5-4 — the problem a run of the setup makes, by the one door: a setup naming component ports is built in
        // a 3D view of the layout, and draws their sheets and landings as a .c3d does.
        var g = CircuitRF.Design.ThreeD.Em3dSetupProblem.Of(inputs.Setup, source, tech, portsOptional: true);
        ct.ThrowIfCancellationRequested();
        var notes = new List<string>();
        if (g.PortRefusal is { } portRefusal) notes.Add(WouldNotRunNote + portRefusal);
        if (g.Problem is null && inputs.Setup.HasWavePorts3D)
        {
            // A view exists to show what is about to be solved, and a refused wave port is the moment
            // a user most needs to SEE the layout: so the geometry is drawn with every wave port as a
            // lumped one, and the refusal is stated first, verbatim, so nobody mistakes the picture for
            // a problem that would run.
            var preview = inputs.Setup.Clone();
            preview.Ports3D = [.. preview.Ports3D.Select(p => p with { Kind = Em3dPortKind.Lumped })];
            var lumped = CircuitRF.Design.ThreeD.Em3dSetupProblem.Of(preview, source, tech);
            if (lumped.Problem is not null)
            {
                notes.Add(WouldNotRunNote + g.Refusal);
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
    /// port lumped would be built — the air box at the generator's default. Nothing about a 3D solve is asked
    /// of it, so the generator's notes on one are not repeated; the note says what the picture is.
    /// <para>The planar solve treats every dielectric as laterally INFINITE, so there is no slab shape to show;
    /// the picture draws each one to <see cref="SlabLateralBound"/>'s shape (the board outline, else the closed
    /// hull of the copper above and below it) — the shape a 3D solver would use for the same layout — and shows
    /// it, rather than an air-box-wide slab hidden so the traces can be seen.</para>
    /// </summary>
    /// <summary>The one note a planar setup's 3D picture carries.</summary>
    public const string PlanarPreviewNote =
        "Planar setup, shown in 3D. The planar solve treats every dielectric as laterally infinite; each is drawn " +
        "here to the board outline, or to the outline of the copper above and below it, for viewing only, and one " +
        "with neither is not drawn. A planar solve has no air box, so none is shown.";

    /// <summary>What a 3D setup's picture says first when it is drawn but a solve would refuse; the refusal follows.</summary>
    public const string WouldNotRunNote = "This setup would not run: ";

    /// <summary>What a planar setup's picture says first when its ports could not be built; the refusal follows.</summary>
    public const string PortsNotDrawnNote = "Shown without ports: ";

    /// <summary>What a planar setup's picture says first when it was drawn with wires on no pad, or with no artwork at
    /// all; the refusal a solve would stop on follows.</summary>
    public const string NotSolvableNote = "Shown for a look, but would not run: ";

    private static Scene3DModel BuildPlanar(long gen, Viewer3DInputs inputs, EmLayoutSource source,
                                            Technology tech, CancellationToken ct)
    {
        var preview = inputs.Setup.Clone();
        preview.Solver3D = Em3dSolvers.Default;
        preview.Problem3D = Em3dProblemType.Driven;
        preview.Ports3D = [];
        // A planar setup's component ports are a strip and a delta gap in its own solve, not a 3D sheet: its look in 3D is the
        // layout's (brief-lcp-5).
        preview.ComponentPorts = [];
        // Ports that would refuse a solve do not blank the picture: it exists for a look, and a layout with no port
        // labels yet, or one whose ports cannot be built in 3D, is exactly when the geometry is worth seeing. Nor do
        // wires with no pad under them — a wBond pushed to a fresh layout has wires and no artwork yet.
        var g = Em3dGenerator.Generate(preview, source, tech, displaySlabs: true, portsOptional: true, padsOptional: true);
        ct.ThrowIfCancellationRequested();
        if (g.Problem is null) return Scene3DModel.Empty(gen, [g.Refusal ?? "the layout could not be built in 3D."]);
        List<string> notes = [];
        if (g.PadRefusal is { } pads) notes.Add(NotSolvableNote + pads);
        if (g.PortRefusal is { } ports) notes.Add(PortsNotDrawnNote + ports);
        notes.Add(PlanarPreviewNote);
        // No air box and no air solid: the generator makes both for a 3D solve, and a planar one has neither.
        var problem = g.Problem with { Solids = [.. g.Problem.Solids.Where(x => x.Name != Em3dGenerator.AirSolidName)] };
        return Scene3DBuilder.Build(problem, gen, g.Origins, tech, inputs.Theme, inputs.Variant,
                                    notes, new Scene3DBuildOptions(DrawAirBox: false, HideOutermostDielectric: false));
    }

    /// <summary>
    /// 3D editor bugs round 2 — an EMPTY first scene counts as framed when this says so: the camera it is shown with is the
    /// one the user draws in. The editor says so for a document with nothing in it (not for one whose first build merely
    /// failed, which still wants a fit when its content arrives). Without it the first scene with an object in it — the first box drawn into a new
    /// design — was fitted, and the view jumped the moment that box appeared. A viewer of a setup keeps waiting for
    /// content to fit, since there an empty scene is a refusal, not a canvas.
    /// </summary>
    public Func<bool>? KeepEmptyView { get; init; }

    /// <summary>UI thread: the newest scene arrived. Keeps the user's toggles, fits the first one.</summary>
    internal void Adopt(Scene3DModel scene)
    {
        if (_disposed || scene.Generation < Scene.Generation) return;
        // R-dfr31-5-3 — the building state ends on every exit: a throw while adopting (a lost GPU context reached from a
        // handler) left it set, and the bar up, for good.
        try { AdoptCore(scene); }
        catch (Exception ex) { BuildFailed(scene.Generation, ex); }
        finally
        {
            IsRegenerating = scene.Generation < Source.Requested;
            if (Scene.Objects.Length > 0 || !IsRegenerating) IsFirstSceneStalled = false;
            if (!IsRegenerating) { _stallTimer?.Dispose(); _stallTimer = null; }
            OnPropertyChanged(nameof(IsBuildingFirstScene));
        }
    }

    private void AdoptCore(Scene3DModel scene)
    {
        if (_inputs.TryGetValue(scene.Generation, out var inputs))
        {
            _lastSetup = inputs.Setup;
            _lastSource = inputs.Source;
        }
        foreach (long old in _inputs.Keys.Where(k => k <= scene.Generation).ToList()) _inputs.Remove(old);
        View.Adopt(scene, _objectNames);
        _objectNames = [.. scene.Objects.Select(o => o.Name)];
        var previous = Scene;
        // brief-em3d-108 — a dialog's or a slider's appearance preview stands over every scene adopted while it lasts
        Scene = PreviewOver(scene);
        HitCycle.SceneChanged(scene.Generation);
        IsRegenerating = scene.Generation < Source.Requested;
        OnPropertyChanged(nameof(IsBuildingFirstScene));
        // An empty scene's first note is its refusal, which Status already says. A drawn scene's refusal — the setup
        // would not run, or its ports are not drawn — is ALSO the banner over the viewport: one line there, so it does not
        // cover the scene (designer round 33), and kept in the notes, where the whole sentence can be read.
        Banner = scene.Objects.Length > 0 && scene.Notes.Count > 0 &&
                 (scene.Notes[0].StartsWith(WouldNotRunNote, StringComparison.Ordinal) ||
                  scene.Notes[0].StartsWith(PortsNotDrawnNote, StringComparison.Ordinal))
            ? scene.Notes[0] : "";
        Notes = string.Join("\n", scene.Objects.Length == 0 ? scene.Notes.Skip(1) : scene.Notes);
        if (_lastSource is { } src)
        {
            var unit = src.View.DisplayUnit;
            int dbu = src.DbuPerMicron;
            var f = EmLengthFormat.For(unit, dbu);
            FormatLength = m => f(m);
            SetMeasureUnits(unit, dbu);
        }
        if (!_fitted && (scene.Objects.Length > 0 || KeepEmptyView?.Invoke() == true))
        {
            if (scene.Objects.Length > 0) View.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, _aspect, View.Camera.Projection);
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
        RemapSelection(previous, scene);
        RefreshSolverOverlays();
        OnPropertyChanged(nameof(Status));
        RealisticSceneAdopted();
        SceneAdopted?.Invoke();
        FrameRequested?.Invoke();
    }

    // ── the object tree (R-em3d28-4c) ───────────────────────────────────────────────────────

    private readonly Dictionary<uint, Viewer3DTreeItem> _items = [];

    /// <summary>The tree's groups by kind — the 3D editor's tree, hosting this view read-only, lists by the same ones.</summary>
    internal static readonly (string Header, Scene3DKind[] Kinds)[] TreeGroups =
    [
        ("Conductors", [Scene3DKind.Conductor, Scene3DKind.Via, Scene3DKind.Sheet]),
        ("Wires", [Scene3DKind.Wire]),
        ("Dielectrics", [Scene3DKind.Dielectric]),
        ("Bodies", [Scene3DKind.Body]),
        ("Air", [Scene3DKind.Air]),
        ("Ports", [Scene3DKind.Port]),
        ("Boundary", [Scene3DKind.Boundary]),
    ];

    private void RebuildTree()
    {
        Tree.Clear();
        _items.Clear();
        foreach (var (header, kinds) in TreeGroups)
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

    /// <summary>3D editor bugs round 5 — an object's view-held visibility changed (its id, and whether it is now shown):
    /// the 3D editor's tree, hosting this view read-only, follows it.</summary>
    public event Action<uint, bool>? VisibilityChanged;

    internal void SetVisible(uint id, bool visible)
    {
        if (id < 1 || id > View.Visible.Length || View.Visible[id - 1] == visible) return;
        View.Visible[id - 1] = visible;
        SyncKindToggles();
        VisibilityChanged?.Invoke(id, visible);
        FrameRequested?.Invoke();
    }

    private void SetKindVisible(Func<Scene3DObject, bool> which, bool visible)
    {
        foreach (var o in Scene.Objects.Where(which))
        {
            View.Visible[o.Id - 1] = visible;
            if (_items.TryGetValue(o.Id, out var it)) it.Sync(visible);
            VisibilityChanged?.Invoke(o.Id, visible);
        }
        FrameRequested?.Invoke();
    }

    private bool _syncingKinds;

    private void SyncKindToggles()
    {
        _syncingKinds = true;
        ShowDielectrics = Scene.Objects.Any(o => o.Kind == Scene3DKind.Dielectric && View.IsVisible(o.Id));
        ShowAir = Scene.Objects.Any(o => o.Kind == Scene3DKind.Air && View.IsVisible(o.Id));
        ShowBoundaryFaces = Scene.Objects.Any(o => IsAirBoxPart(o) && View.IsVisible(o.Id));
        _syncingKinds = false;
    }

    [ObservableProperty] private bool _showDielectrics;
    [ObservableProperty] private bool _showAir;
    [ObservableProperty] private bool _showBoundaryFaces;

    partial void OnShowDielectricsChanged(bool value) { if (!_syncingKinds) SetKindVisible(o => o.Kind == Scene3DKind.Dielectric, value); }
    partial void OnShowAirChanged(bool value) { if (!_syncingKinds) SetKindVisible(o => o.Kind == Scene3DKind.Air, value); }
    partial void OnShowBoundaryFacesChanged(bool value) { if (!_syncingKinds) SetKindVisible(IsAirBoxPart, value); }

    /// <summary>An air-box face — not the box's edges, and not a face boundary's tint (brief-em3d-49).</summary>
    private static bool IsBoxFace(Scene3DObject o)
        => o.Kind == Scene3DKind.Boundary && o.Name != "airbox" && !o.Name.StartsWith(Scene3DBuilder.FaceTintPrefix, StringComparison.Ordinal);

    /// <summary>3D editor round 3 — what the air-box switch shows and hides: its faces AND its edges. The edges were left
    /// out, so switching the box off still drew its outline, and the switch then read "on" again from that outline.
    /// A face boundary's tint is not the box's and stays drawn.</summary>
    private static bool IsAirBoxPart(Scene3DObject o)
        => o.Kind == Scene3DKind.Boundary && !o.Name.StartsWith(Scene3DBuilder.FaceTintPrefix, StringComparison.Ordinal);

    [ObservableProperty] private Viewer3DTreeItem? _selectedItem;

    partial void OnSelectedItemChanged(Viewer3DTreeItem? value)
    {
        // brief-em3d-43 — the tree selects an OBJECT, so it selects in Object mode.
        if (!_selectingFromTree)
        {
            _selectingFromTree = true;
            try
            {
                if (value is not null && SelectMode != Render.Scene3D.Edit.Scene3DSelectMode.Object)
                    SelectMode = Render.Scene3D.Edit.Scene3DSelectMode.Object;
                SetSelection(value is null ? [] : [Render.Scene3D.Edit.Scene3DItem.OfObject(value.Id)]);
            }
            finally { _selectingFromTree = false; }
        }
        // brief-em3d-29 — a selected solid is where a volume field's surface is drawn.
        if (ShowField) ScheduleFieldGeometry(l => l.OnSurfaces && l.Quantity is { OnBoundary: false });
        FrameRequested?.Invoke();
    }

    // ── status and hover ────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRegenerating;

    partial void OnIsRegeneratingChanged(bool value)
    {
        if (!value) { IsFirstSceneStalled = false; _stallTimer?.Dispose(); _stallTimer = null; }
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsBuildingFirstScene));
    }

    /// <summary>
    /// Designer feedback 02 — true while a view that has drawn nothing yet is building its first scene. The status pane
    /// that says "Generating the 3D problem…" is hidden by default, so a board that takes seconds to build opened on an
    /// empty grid and read as a view that had failed; the viewport says it on the canvas instead.
    /// </summary>
    public bool IsBuildingFirstScene => IsRegenerating && Scene.Objects.Length == 0 && !IsFirstSceneStalled;
    [ObservableProperty] private string _notes = "";
    /// <summary>brief-dfr26-1 R-dfr26-1-5 — why the drawn scene would not solve, shown over the viewport; empty otherwise.</summary>
    [ObservableProperty] private string _banner = "";
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _meshText = "";

    // ── brief-em3d-48 R-em3d48-3c: the triangle budget, and saying when it bites ──────────────────

    /// <summary>The frame's triangle budget (the user's, from 3D ▸ Triangle Budget…).</summary>
    public long TriangleBudget { get; set; } = Lod3DPreference.Budget;

    /// <summary>Empty, or how many array elements the last frame drew as boxes and why — the one place a picture shows
    /// less than the solver gets, so it is said on screen.</summary>
    [ObservableProperty] private string _lodText = "";

    /// <summary>Frame loop → UI: what the planned frame left out. Allocates only when the count changes.</summary>
    public void FramePlanned(Scene3DFramePlan plan)
    {
        int boxed = plan.LodBoxedElements;
        if (boxed == _lastBoxed && _lastBudget == plan.TriangleBudget) return;
        _lastBoxed = boxed;
        _lastBudget = plan.TriangleBudget;
        LodText = boxed == 0 ? "" : LodMessage(boxed, plan.ElementCount, plan.TriangleBudget);
    }

    private int _lastBoxed;
    private long _lastBudget;

    /// <summary>The status line's sentence for <paramref name="boxed"/> of <paramref name="elements"/> drawn as boxes.</summary>
    public static string LodMessage(int boxed, int elements, long budget)
        => $"{boxed:N0} of {elements:N0} array elements drawn as boxes: over the {budget:N0}-triangle budget " +
           "(3D ▸ Triangle Budget…). Nearer ones are drawn in full; the solver gets every one.";

    public string Status => Scene.Objects.Length == 0
        ? (Scene.Notes.Count > 0 ? "Nothing to show: " + Scene.Notes[0] : "Generating the 3D problem…")
        : $"{Scene.Objects.Length} objects, {Scene.TriangleCount:N0} triangles, {Scene.Batches.Length} draws (generation {Scene.Generation})"
          + (IsRegenerating ? " — regenerating…" : "");

    /// <summary>Frame loop → UI: the ID pass's answer arrived. Updates the tooltip and the dimension
    /// under the cursor. Touches no geometry.</summary>
    internal void OnPicked(uint id, Vector3 point, bool hit)
    {
        // brief-em3d-29 R-em3d29-3e — the field under the cursor, read from the field data.
        string field = FieldValueUnderCursor(id, point, hit);
        if (View.Hovered != id || field != _hoverField)
        {
            if (View.Hovered != id) FrameRequested?.Invoke();
            View.Hovered = id;
            _hoverField = field;
            string about = Describe(Scene.Object(id));
            HoverText = field.Length == 0 ? about : about.Length == 0 ? field : about + "\n" + field;
        }
        if (hit)
        {
            var (x, y, z) = Scene.ToWorld(point);
            _lastCursorWorld = (x, y, z);
            CursorText = $"x {FormatLength(x)}   y {FormatLength(y)}   z {FormatLength(z)}";
        }
        else { CursorText = ""; _lastCursorWorld = null; }
    }

    /// <summary>brief-em3d-93 — what the owner adds to an object's hover line (the 3D editor's "(not modelled)"), or null.</summary>
    public Func<Scene3DObject, string?>? DescribeSuffix { get; set; }

    /// <summary>3D editor groups — the group a click on this object takes whole, named first on its hover line; or null.</summary>
    public Func<Scene3DObject, string?>? DescribeGroup { get; set; }

    /// <summary>brief-em3d-94 — what the owner knows of an object's material that the scene does not (its record, the active
    /// thermal setup, why it has no values), or null: the 3D editor's.</summary>
    public Func<Scene3DObject, MaterialHoverContext?>? MaterialHoverContext { get; set; }

    /// <summary>The tooltip: name, then the material lines that apply to what the object is (MaterialHover), σ at the setup's
    /// operating temperature; a face's boundary kind.</summary>
    internal string Describe(Scene3DObject? o)
    {
        if (o is null) return "";
        // brief-em3d-101 R-em3d101-3d — an image sheet names its file: "image1 — die.png (not modelled)".
        var t = (DescribeGroup?.Invoke(o) is { } group ? $"{group} (group) — " : "")
                + o.Kind switch { Scene3DKind.Port => $"Port {o.PortNumber}  {o.Name}", _ => o.Name }
                + (o.ImageName is { } file ? " — " + file : "") + DescribeSuffix?.Invoke(o);
        // An image sheet with no material is a reference picture: no material line to give.
        var lines = o.ImageName is not null && o.MaterialValues is null ? []
                  : MaterialHover.Lines(o, Scene.Problem?.OperatingTempC, MaterialHoverContext?.Invoke(o));
        if (lines.Count > 0) t += "\n" + string.Join("\n", lines);
        else if (o.Boundary is { } b) t += $"\n{b}";
        return t;
    }

    // ── input (the UI thread's whole job) ───────────────────────────────────────────────────

    private float _aspect = 1.6f;
    private string _hoverField = "";

    /// <summary>The view's width over its height — what a glTF camera of this view frames at (brief-em3d-111).</summary>
    public float Aspect => _aspect;
    private (double X, double Y, double Z)? _lastCursorWorld;

    /// <summary>brief-em3d-47 — the surface point under the cursor, world metres, or null off every surface.</summary>
    public (double X, double Y, double Z)? CursorWorld => _lastCursorWorld;

    public void Resized(float width, float height)
    {
        // a pane collapsing to no width would set an aspect of 0, which a later Fit frames against
        if (width > 0 && height > 0) { _aspect = width / height; _viewW = width; _viewH = height; }
    }

    public void Hover(float x, float y)
    {
        if (CameraGesture) return;
        View.CursorX = x; View.CursorY = y;
        HitCycle.CursorMoved(x, y);
        HoverGizmo(x, y);
        FrameRequested?.Invoke();
    }

    public void Leave()
    {
        View.CursorX = View.CursorY = -1;
        HoveredItem = null;
        View.HoveredFace = -1;
        if (View.Hovered != 0) { View.Hovered = 0; HoverText = ""; }
        CursorText = "";
        ClearSnap();
        FrameRequested?.Invoke();
    }

    // ── 3D round 3: a Project Tree item dragged onto the pane ────────────────────────────────
    // A drag delivers no pointer moves, so the drag-over IS the hover: the cursor, Ctrl/Cmd (the placement's
    // bottom-centre handle) and the snap follow it exactly as they follow the mouse, and the drop takes the point a
    // click there would take. The read-only viewer has no edit host and accepts nothing.

    /// <summary>A drag carrying <paramref name="text"/> is over the pane at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool TreeDragOver(float x, float y, string text, bool command)
    {
        if (EditHost is not { } host) return false;
        SetCommandHeld(command);
        Hover(x, y);
        return host.TreeDragOver(text);
    }

    /// <summary>The drag was dropped at (<paramref name="x"/>, <paramref name="y"/>): true when it was placed or refused as ours.</summary>
    public bool TreeDrop(float x, float y, string text, bool command)
    {
        if (EditHost is not { } host) return false;
        SetCommandHeld(command);
        Hover(x, y);
        return host.TreeDrop(text);
    }

    /// <summary>brief-em3d-101 — files dragged over the pane at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool FileDragOver(float x, float y, IReadOnlyList<string> paths)
    {
        if (EditHost is not { } host) return false;
        Hover(x, y);
        return host.FileDragOver(paths);
    }

    /// <summary>brief-em3d-101 — files dropped at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool FileDrop(float x, float y, IReadOnlyList<string> paths, bool shift)
    {
        if (EditHost is not { } host) return false;
        Hover(x, y);
        return host.FileDrop(paths, x, y, shift);
    }

    /// <summary>The drag left the pane.</summary>
    public void TreeDragLeave()
    {
        EditHost?.TreeDragLeave();
        Leave();
    }

    /// <summary>
    /// 3D editor bugs round 2 — an orbit or pan drag is under way. While it is, the view has no cursor: nothing is hovered,
    /// highlighted or snapped, because the cursor is steering the camera and is not pointing at anything. A pick read back
    /// from a frame planned before the drag began is dropped (<see cref="OnPicked(uint, uint, Vector3, bool, Scene3DIdPatch?)"/>).
    /// </summary>
    public bool CameraGesture { get; private set; }

    public void SetCameraGesture(bool on)
    {
        if (CameraGesture == on) return;
        CameraGesture = on;
        if (on) Leave();
    }

    public void Orbit(float dx, float dy) { View.Camera.Orbit(dx, dy); View.Orbiting = true; FrameRequested?.Invoke(); }

    public void Pan(float dx, float dy, float height) { View.Camera.Pan(dx, dy, height); View.Orbiting = true; FrameRequested?.Invoke(); }

    public void Zoom(float notches, float x, float y, float w, float h)
    {
        // 3D editor bugs round 3 — a perspective zoom in approaches the drawing plane under the cursor rather than dollying
        // through it (the grid vanished below a ~10 µm scale): PlaneGrid.SeatTargetOnPlane.
        if (notches > 0 && View.DrawingGrid is { Visible: true } g)
            CircuitRF.Render.Scene3D.Edit.PlaneGrid.SeatTargetOnPlane(ref View.Camera, Scene, g, x, y, w, h);
        View.Camera.ZoomAt(notches, x, y, w, h);
        View.Orbiting = true;
        FrameRequested?.Invoke();
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
        CountFit();
        // 3D editor round 5 — only what is shown: a hidden object is not framed, the air box is while it is drawn.
        var (min, max) = Scene.VisibleContent(View.Visible);
        // General designer feedback round 14 — tight to what is seen from HERE, the orientation kept (Camera3D.FrameBounds).
        View.Camera.FrameBounds(min, max, _aspect);
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

    /// <summary>3D editor bugs round 6 — the scale bar at the bottom right (the "scale legend"), on screen and in every picture
    /// made of the view (Copy, Export Picture…, Copy as Vector, Export as Vector…), as the axis indicator is.</summary>
    [ObservableProperty] private bool _showScaleLegend = true;
    partial void OnShowScaleLegendChanged(bool value) => FrameRequested?.Invoke();

    [ObservableProperty] private bool _showTree = true;

    // Clip plane (R-em3d28-4d).
    [ObservableProperty] private bool _clipEnabled;
    [ObservableProperty] private ClipAxis3D _clipAxis = ClipAxis3D.Z;
    /// <summary>0..1 across the scene's extent along the plane's normal.</summary>
    [ObservableProperty] private double _clipPosition = 0.5;
    [ObservableProperty] private bool _clipFlip;

    public IReadOnlyList<ClipAxis3D> ClipAxes { get; } = [ClipAxis3D.X, ClipAxis3D.Y, ClipAxis3D.Z, ClipAxis3D.View, ClipAxis3D.Section];

    /// <summary>brief-dfr34-3 R-dfr34-3-3 — the normal a <see cref="ClipAxis3D.Section"/> plane was given (Section Through…), kept
    /// while another axis is chosen so choosing Section again cuts there again. Zero until one is given: Section then cuts on the
    /// vertical plane facing the view.</summary>
    private Vector3 _sectionNormal;

    partial void OnClipEnabledChanged(bool value) => ApplyClip();
    partial void OnClipAxisChanged(ClipAxis3D value) => ApplyClip();
    partial void OnClipPositionChanged(double value) => ApplyClip();
    partial void OnClipFlipChanged(bool value) => ApplyClip();

    /// <summary>brief-dfr36-1 — the plane the toolbar's clip controls state, which a field's slice and the FDTD grid are cut on. What
    /// the view actually cuts solids on is <c>View.Clip</c>: this, unless it is off and the mesh's plane is showing.</summary>
    public ClipPlane3D ToolbarClip { get; private set; } = new() { Axis = ClipAxis3D.Z };

    /// <summary>What cuts the solids: the clip plane when it is on, else the mesh's own plane when it is showing, since a plane inside
    /// opaque solids could not otherwise be seen. A field plot on its own plane is drawn unclipped either way.</summary>
    private void CutView() => View.Clip = !ClipEnabled && ShowMesh && MeshPlaneEnabled ? MeshPlaneAt(_drawnMeshPlanePosition) : ToolbarClip;

    private void ApplyClip()
    {
        var c = ToolbarClip;
        c.Enabled = ClipEnabled;
        if (ClipAxis == ClipAxis3D.View && (c.Axis != ClipAxis3D.View || c.ViewNormal == Vector3.Zero))
            c.ViewNormal = View.Camera.Forward;
        if (ClipAxis == ClipAxis3D.Section)
        {
            if (_sectionNormal == Vector3.Zero)
            {
                var f = View.Camera.Forward;
                _sectionNormal = Math.Abs(f.X) + Math.Abs(f.Y) > 1e-6f ? Vector3.Normalize(new Vector3(f.X, f.Y, 0)) : Vector3.UnitX;
            }
            c.ViewNormal = _sectionNormal;
        }
        c.Axis = ClipAxis;
        c.Flip = ClipFlip;
        var (lo, hi) = c.Range(Scene.BoundsMin, Scene.BoundsMax);
        c.Offset = (float)(lo + (hi - lo) * ClipPosition);
        ToolbarClip = c;
        CutView();
        FrameRequested?.Invoke();
        ScheduleClipOverlays();
        // brief-em3d-96 — a ClipPlane plot cuts on its own plane: the view's section never rebuilds it.
        ScheduleFieldGeometry(l => !l.IsClipPlanePlot);
    }

    /// <summary>brief-dfr34-3 — the toolbar's clip plane as it stands: what Section Through… changes and its undo puts back.</summary>
    public readonly record struct ClipState(bool Enabled, ClipAxis3D Axis, double Position, bool Flip, Vector3 SectionNormal);

    public ClipState CurrentClip => new(ClipEnabled, ClipAxis, ClipPosition, ClipFlip, _sectionNormal);

    /// <summary>Puts the clip plane back as <paramref name="state"/> held it.</summary>
    public void RestoreClip(ClipState state)
    {
        _sectionNormal = state.SectionNormal;
        // each setter re-applies; the last leaves the plane exactly as stated
        ClipAxis = state.Axis;
        ClipFlip = state.Flip;
        ClipPosition = state.Position;
        ClipEnabled = state.Enabled;
        ApplyClip();
    }

    /// <summary>
    /// brief-dfr34-3 R-dfr34-3-3 — the clip plane on the plane <paramref name="normal"/>·p = <paramref name="offset"/> (scene-local
    /// metres, the normal's own length one), keeping the side the normal points away from when <paramref name="flip"/>: Section
    /// Through… keeps the side away from the camera. The toolbar's axis reads Section.
    /// </summary>
    public void SetSectionClip(Vector3 normal, float offset, bool flip)
    {
        _sectionNormal = Vector3.Normalize(normal);
        var probe = new ClipPlane3D { Axis = ClipAxis3D.Section, ViewNormal = _sectionNormal };
        var (lo, hi) = probe.Range(Scene.BoundsMin, Scene.BoundsMax);
        double position = hi > lo ? Math.Clamp((offset - lo) / (hi - lo), 0, 1) : 0.5;
        RestoreClip(new ClipState(true, ClipAxis3D.Section, position, flip, _sectionNormal));
    }

    /// <summary>brief-dfr34-3 — the camera as it stands, and put back: Section Through… turns it, and its undo turns it back.</summary>
    public Camera3D CurrentCamera => View.Camera;

    public void RestoreCamera(Camera3D camera)
    {
        View.Camera = camera;
        IsPerspective = camera.Projection == Projection3D.Perspective;
        FrameRequested?.Invoke();
    }

    /// <summary>
    /// brief-dfr34-3 R-dfr34-3-2 — the camera turned to look straight at a vertical plane from the side <paramref name="toward"/>
    /// (a horizontal unit vector, scene-local) points to: level, orthographic, framed on what is shown.
    /// </summary>
    public void LookAlong(Vector3 toward)
    {
        View.Camera.Yaw = MathF.Atan2(toward.Y, toward.X);
        View.Camera.Pitch = 0;
        IsPerspective = false;
        Fit();
    }

    // Overlays (R-em3d28-3).
    [ObservableProperty] private bool _meshAvailable;

    /// <summary>Opened from a Palace run directory: turn the mesh on as soon as the setup's scene lands.</summary>
    public bool ShowMeshWhenAvailable { get; set; }
    [ObservableProperty] private bool _gridAvailable;
    [ObservableProperty] private bool _showMesh;
    [ObservableProperty] private bool _showGrid;

    /// <summary>brief-dfr36-1 D1 — any mesh on disk for this setup: a finished run's, a stopped run's or a mesh-only pass's. The mesh is
    /// looked at to judge it before solving, so it needs no fields.</summary>
    public bool CanShowMesh => MeshAvailable;

    public string MeshTip => CanShowMesh ? $"Show the mesh Gmsh made, {_meshOrigin}: its boundary triangles, or the tetrahedra on the mesh plane beside this."
                           : _lastSetup is { IsThermal: false, Is3D: false } ? "The planar mesh is shown in the layout view"
                           : _lastSetup is { } setup && Em3dRunService.MeshButtonRefusal(setup) is { } refusal ? refusal
                           : "No mesh yet: Mesh makes one without solving, and Simulate makes one first";

    /// <summary>Who made the mesh on disk, in the tooltip's words.</summary>
    private string _meshOrigin = "";

    private static string MeshOriginOf(string meshPath)
    {
        var madeUtc = File.GetLastWriteTimeUtc(meshPath);
        string when = madeUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        if (C3dRunStatus.Read(Path.GetDirectoryName(meshPath)!) is not { } run || (run.Finished is { } end && madeUtc > end))
            return $"at {when} by a mesh-only pass";
        string started = run.Started.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        if (madeUtc < run.Started) return $"at {when} and reused by the run at {started}";
        return run.Partial ? $"at {when} by a run that did not finish" : $"at {when} by the run at {started}";
    }

    // ── brief-dfr36-1 — the mesh's own plane ─────────────────────────────────────────────────
    //
    // The tetrahedra are cut here, apart from the toolbar's clip plane and from any field plot's plane, so moving one never moves
    // another. The Lines pipeline does not read the view's clip plane, so that plane never removes the section; opaque solids in front
    // of it do hide it, which is why this plane cuts the solids itself while the clip plane is off (CutView).

    [ObservableProperty] private bool _meshPlaneEnabled;
    [ObservableProperty] private ClipAxis3D _meshPlaneAxis = ClipAxis3D.Z;
    /// <summary>0..1 across the scene's extent along the axis.</summary>
    [ObservableProperty] private double _meshPlanePosition = 0.5;
    /// <summary>Which side of the plane is kept, when it is the mesh's plane that cuts the solids.</summary>
    [ObservableProperty] private bool _meshPlaneFlip;

    public IReadOnlyList<ClipAxis3D> MeshPlaneAxes { get; } = [ClipAxis3D.X, ClipAxis3D.Y, ClipAxis3D.Z];

    partial void OnMeshPlaneEnabledChanged(bool value)
    {
        // The plane draws the mesh's tetrahedra, so turning it on with the mesh off would show nothing at all.
        if (value && CanShowMesh && !ShowMesh) ShowMesh = true;
        ApplyMeshPlane();
    }
    partial void OnMeshPlaneAxisChanged(ClipAxis3D value) => ApplyMeshPlane();
    partial void OnMeshPlanePositionChanged(double value)
    {
        if (!MeshPlaneTooSlowToFollow) { ApplyMeshPlane(); return; }
        OnPropertyChanged(nameof(MeshPlaneText));
        _meshPlaneQuiet?.Dispose();
        _meshPlaneQuiet = new System.Threading.Timer(_ => _post(ApplyMeshPlane), null, MeshPlaneQuietMs, Timeout.Infinite);
    }
    partial void OnMeshPlaneFlipChanged(bool value) => ApplyMeshPlane();

    /// <summary>On a plane, the mesh is that plane's tetrahedra alone: the boundary wireframe is not clipped, so the half the plane cuts
    /// away would be drawn in front of the section.</summary>
    private void ShowMeshDrawing()
    {
        View.ShowMesh = ShowMesh && !MeshPlaneEnabled;
        View.ShowMeshSection = ShowMesh && MeshPlaneEnabled;
    }

    private void ApplyMeshPlane()
    {
        if (_disposed) return;
        _meshPlaneQuiet?.Dispose();
        _meshPlaneQuiet = null;
        _drawnMeshPlanePosition = MeshPlanePosition;
        ShowMeshDrawing();
        CutView();
        OnPropertyChanged(nameof(MeshPlaneText));
        FrameRequested?.Invoke();
        ScheduleClipOverlays();
    }

    /// <summary>The mesh's plane where it now stands, scene-local.</summary>
    public ClipPlane3D MeshPlane => MeshPlaneAt(MeshPlanePosition);

    private ClipPlane3D MeshPlaneAt(double position)
    {
        var plane = new ClipPlane3D { Enabled = true, Axis = MeshPlaneAxis, Flip = MeshPlaneFlip };
        var (lo, hi) = plane.Range(Scene.BoundsMin, Scene.BoundsMax);
        plane.Offset = (float)(lo + (hi - lo) * position);
        return plane;
    }

    /// <summary>A plane that takes longer than this to reach the screen, its cut and a frame together, stops following its slider: it
    /// holds while the slider moves and lands when the slider rests. 500,000 tetrahedra cut in about 50 ms on a Debug build.</summary>
    public const double MeshPlaneLiveLimitMs = 150;

    /// <summary>How long the slider must rest before a plane too slow to follow lands on it.</summary>
    public int MeshPlaneQuietMs { get; set; } = 150;

    /// <summary>Where the plane is drawn: the slider's position, except while a plane too slow to follow waits for the slider to rest.</summary>
    private double _drawnMeshPlanePosition = 0.5;
    private System.Threading.Timer? _meshPlaneQuiet;
    private double _meshCutMs, _frameMs;

    private bool MeshPlaneTooSlowToFollow => ShowMesh && MeshPlaneEnabled && _meshCutMs + _frameMs > MeshPlaneLiveLimitMs;

    /// <summary>How long a frame took from being planned to being presented, as the pane measures it.</summary>
    public void FrameTook(double milliseconds) => _frameMs = Smoothed(_frameMs, milliseconds);

    /// <summary>How long a cut of the mesh took.</summary>
    internal void MeshCutTook(double milliseconds) => _meshCutMs = Smoothed(_meshCutMs, milliseconds);

    /// <summary>A running average, so one stall does not end a drag's following, and a measurement made while holding brings it back.</summary>
    private static double Smoothed(double average, double sample) => average == 0 ? sample : (average + sample) / 2;

    /// <summary>Where the plane is, in world millimetres.</summary>
    public string MeshPlaneText
    {
        get
        {
            var plane = MeshPlane;
            var at = Scene.ToWorld(plane.Unflipped * plane.Offset);
            double metres = MeshPlaneAxis switch { ClipAxis3D.X => at.X, ClipAxis3D.Y => at.Y, _ => at.Z };
            return string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{MeshPlaneAxis} = {metres * 1e3:0.###} mm");
        }
    }

    // ── brief-dfr36-1 R-dfr36-1-3 — is the mesh shown still the model's? ──────────────────────

    /// <summary>Set when the mesh shown is no longer what a run would use, or when only a run can tell.</summary>
    [ObservableProperty] private string? _meshStaleText;

    /// <summary>Asked on the UI thread for the question to put off it: whether a run would reuse the mesh, or null when there is
    /// nothing to ask. The editor's assembles the document; the default reads the problem the scene was built from.</summary>
    public Func<Func<Em3dMeshCurrency>?>? MeshCurrencySource { get; set; }

    private CancellationTokenSource? _meshCurrencyCts;

    /// <summary>The model or the setup may have changed: asked again shortly, and only while the mesh is on screen.</summary>
    public void ScheduleMeshCurrency()
    {
        _meshCurrencyCts?.Cancel();
        if (!ShowMesh || !MeshAvailable || (MeshCurrencySource ?? SceneMeshCurrency)() is not { } ask) { MeshStaleText = null; return; }
        var cts = _meshCurrencyCts = new CancellationTokenSource();
        string made = _meshOrigin;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MeshCurrencyQuietMs, cts.Token).ConfigureAwait(false);
                var currency = ask();
                _post(() => { if (!cts.IsCancellationRequested && !_disposed) MeshStaleText = MeshStaleSentence(currency, made); });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // An answer that could not be reached is not "current".
                _post(() => { if (!cts.IsCancellationRequested && !_disposed) MeshStaleText = MeshStaleSentence(Em3dMeshCurrency.Unknown, made); });
            }
        });
    }

    /// <summary>How long the model must be quiet before the mesh is compared with it; zero under a test.</summary>
    public int MeshCurrencyQuietMs { get; set; } = 400;

    private Func<Em3dMeshCurrency>? SceneMeshCurrency()
    {
        if (_lastSetup is not { } setup || Scene.Problem is not { } problem || _resultsRoot() is not { } root) return null;
        return () => Em3dRunService.MeshCurrency(problem, setup, root);
    }

    internal static string? MeshStaleSentence(Em3dMeshCurrency currency, string made) => currency switch
    {
        Em3dMeshCurrency.Stale => $"The mesh was made {made}; the model or the setup has changed since. It is drawn as it was made, and a run meshes again.",
        Em3dMeshCurrency.Unknown => $"The mesh was made {made}. Whether it is still current is decided when a run starts.",
        _ => null,
    };
    public string GridTip => GridAvailable ? "Show the FDTD grid on the clip plane and where it meets the metal"
                                           : "The FDTD grid is shown for an openEMS setup";

    partial void OnMeshAvailableChanged(bool value) { OnPropertyChanged(nameof(CanShowMesh)); OnPropertyChanged(nameof(MeshTip)); }
    partial void OnGridAvailableChanged(bool value) => OnPropertyChanged(nameof(GridTip));

    partial void OnShowMeshChanged(bool value)
    {
        ShowMeshDrawing();
        CutView();
        if (value) LoadMesh();
        ScheduleMeshCurrency();
        FrameRequested?.Invoke();
    }

    partial void OnShowGridChanged(bool value) { View.ShowGrid = value; ScheduleClipOverlays(); FrameRequested?.Invoke(); }

    /// <summary>The Gmsh mesh beside this setup's Palace run, if there is one. It exists only after
    /// Gmsh has run (R-em3d28-3b).</summary>
    public string? MeshPath()
    {
        if (_lastSetup is not { } setup || _resultsRoot() is not { } root) return null;
        // brief-em3d-75 — a thermal run keeps its Gmsh mesh in its own directory.
        if (setup.IsThermal)
        {
            string tm = Path.Combine(CircuitRF.Design.Thermal.ThermalRunService.RunDirectory(root, setup), GmshGeoWriter.MeshFile);
            return File.Exists(tm) ? tm : null;
        }
        if (Em3dSolvers.FirstWith(setup.Solver3D, c => c.GmshMesh) is not { } meshed) return null;
        string p = Path.Combine(Em3dRunService.RunDirectory(root, setup, meshed.Id), GmshGeoWriter.MeshFile);
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// brief-em3d-49 R-em3d49-5b — the 3D editor's pane has no <c>.cem</c> of its own: the editor names the setup whose run
    /// the fields (and the mesh) are read from — the active one, as its run names it — and they are re-read from that
    /// run's own directory. Null takes them away.
    /// </summary>
    public void SetRunSetup(EmSetup? runSetup)
    {
        _lastSetup = runSetup;
        RefreshSolverOverlays();
    }

    /// <summary>Re-reads what the setup's solver produced: the mesh file (a run may have made one) and
    /// the FDTD grid.</summary>
    public void RefreshSolverOverlays()
    {
        _meshOrigin = MeshPath() is { } onDisk ? MeshOriginOf(onDisk) : "";
        MeshAvailable = MeshPath() is not null;
        OnPropertyChanged(nameof(MeshTip));
        if (MeshAvailable && ShowMeshWhenAvailable) { ShowMeshWhenAvailable = false; ShowMesh = true; }
        if (!MeshAvailable && ShowMesh) ShowMesh = false;
        if (_meshStamp is not null && (MeshPath() is not { } now || MeshStamp.Of(now) != _meshStamp))
        {
            _mesh = null; _meshStamp = null; _meshDrawn = default;
            MeshText = "";
        }
        if (ShowMesh) LoadMesh();
        ScheduleMeshCurrency();

        // brief-emi-1 — the grid shown is circuitRF's own FDTD grid, which an FDTD solver's run is placed on.
        GridAvailable = _lastSetup is { } gridSetup && Em3dSolvers.Of(gridSetup.Solver3D).Any(d => d.Method == Em3dSolverFit.Fdtd)
                        && Scene.Problem is not null;
        if (!GridAvailable && ShowGrid) ShowGrid = false;
        ScheduleClipOverlays();
        RefreshFields();
    }

    /// <summary>The mesh is in GmshGeoWriter's units (Palace's L0).</summary>
    public const double MeshToMetres = GmshGeoWriter.LengthUnitM;

    /// <summary>Stands in for building the mesh's lines under a test.</summary>
    internal Func<MshMesh, Scene3DModel, bool, Scene3DVertex[]>? MeshWireframeBuilder { get; set; }

    private void LoadMesh()
    {
        if (MeshPath() is not { } path) return;
        var scene = Scene;
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        var stamp = MeshStamp.Of(path);
        // Read already: the lines drawn are still right unless the scene or the theme moved, and a new set is built off this
        // thread like the first. On a large mesh building them takes long enough to be felt, and turning the mesh back on
        // needs none of it. The old lines stay on screen until the new ones arrive.
        var known = _mesh is not null && stamp is not null && _meshStamp == stamp ? _mesh : null;
        (MshMesh?, object?, bool) drawing = (known, scene.Geometry, dark);
        if (known is not null && drawing == _meshDrawn)
        {
            ScheduleClipOverlays();
            return;
        }
        (MeshStamp?, object?, bool) request = (stamp, scene.Geometry, dark);
        if (stamp is not null && _meshLoading == request) return;    // already being built
        _meshLoading = request;
        if (known is null) MeshText = "Reading the mesh…";
        Task.Run(() =>
        {
            try
            {
                var m = known ?? MshReader.Read(path);
                var lines = MeshWireframeBuilder?.Invoke(m, scene, dark)
                            ?? Render.Scene3D.MeshOverlay.BoundaryWireframe(m, scene, MeshToMetres, dark);
                _post(() =>
                {
                    // A newer request (a scene or theme that moved meanwhile) has this mesh's lines coming.
                    if (_meshLoading != request) return;
                    _meshLoading = null;
                    if (_disposed) return;
                    _mesh = m; _meshStamp = stamp;
                    _meshDrawn = (m, scene.Geometry, dark);
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
                    if (_meshLoading != request) return;
                    _meshLoading = null;
                    MeshText = "The mesh could not be read: " + ex.Message;
                });
            }
        });
    }

    /// <summary>The clip-dependent overlays — the mesh's section and the grid — rebuilt off the UI
    /// thread when the plane or the scene moves; a newer request cancels an older one.</summary>
    private (MshMesh?, Vector4, object?, bool) _sectionKey;

    private void ScheduleClipOverlays()
    {
        _overlayCts?.Cancel();
        var cts = _overlayCts = new CancellationTokenSource();
        CutView();                                 // the scene's bounds, which the mesh's plane is placed in, may have moved
        ScheduleSection();
        var scene = Scene;
        var clip = ToolbarClip;
        bool wantGrid = ShowGrid && GridAvailable && scene.Problem is not null;
        var setup = _lastSetup;
        var grid = _grid is { } cached && ReferenceEquals(cached.Scene.Geometry, scene.Geometry) ? cached.Grid : null;
        if (!wantGrid)
        {
            GridOverlay = Scene3DOverlay.None; GridLabels = []; GridFeedLabels = []; OnPropertyChanged(nameof(GridLabels));
            FrameRequested?.Invoke();
            return;
        }
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        Task.Run(() =>
        {
            try
            {
                if (grid is null)
                {
                    grid = FdtdGrid.Build(scene.Problem!, CemOpenEms.ResolveGrid(setup?.OpenEms, CemPath));   // brief-sar-3 — a relative GridLines.File
                    var built = grid;
                    _post(() => { if (ReferenceEquals(Scene.Geometry, scene.Geometry)) _grid = (scene, built); });
                }
                var drawing = FdtdGridOverlay.Build(grid, scene, clip, dark, cts.Token);
                if (cts.IsCancellationRequested) return;
                _post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    GridOverlay = new Scene3DOverlay(drawing.Lines, ++_overlayVersion);
                    GridLabels = drawing.Labels;
                    GridFeedLabels = drawing.FeedLabels;
                    OnPropertyChanged(nameof(GridLabels));
                    FrameRequested?.Invoke();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _post(() => Notes = "The grid could not be drawn: " + ex.Message); }
        });
    }

    // A slider moves more often than a large mesh can be cut, so a new plane does not cancel the cut under way, which would leave nothing
    // drawn until the slider rested. The cut finishes and shows, and the next one takes the plane as it then stands. Only a new mesh,
    // scene or theme cancels one, since its lines would be of no use.
    private CancellationTokenSource? _sectionCts;
    private (MshMesh?, object?, bool) _sectionRun;

    /// <summary>Stands in for the cut under a test.</summary>
    internal Func<MshMesh, Scene3DModel, ClipPlane3D, uint, CancellationToken, Scene3DVertex[]>? MeshSectionCutter { get; set; }

    private void ScheduleSection()
    {
        if (!ShowMesh || !MeshPlaneEnabled || _mesh is not { } mesh)
        {
            _sectionCts?.Cancel();
            _sectionCts = null;
            SectionOverlay = Scene3DOverlay.None;
            _sectionKey = default;
            return;
        }
        var scene = Scene;
        var plane = MeshPlaneAt(_drawnMeshPlanePosition);
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        // Only the mesh's own plane, the mesh, the scene or the theme rebuild it: dragging the clip plane must not re-cut a large mesh.
        (MshMesh?, Vector4, object?, bool) key = (mesh, plane.Equation, scene.Geometry, dark);
        if (key == _sectionKey) return;
        (MshMesh?, object?, bool) run = (mesh, scene.Geometry, dark);
        if (_sectionCts is not null)
        {
            if (run == _sectionRun) return;
            _sectionCts.Cancel();
        }
        var cts = _sectionCts = new CancellationTokenSource();
        _sectionRun = run;
        uint colour = dark ? Scene3DVertex.Pack(255, 210, 90, 255) : Scene3DVertex.Pack(170, 90, 0, 255);
        var cut = MeshSectionCutter ?? ((m, s, p, c, t) => Render.Scene3D.MeshOverlay.Section(m, s, MeshToMetres, p, c, t));
        Task.Run(() =>
        {
            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                var lines = cut(mesh, scene, plane, colour, cts.Token);
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _post(() =>
                {
                    if (!ReferenceEquals(cts, _sectionCts) || _disposed) return;
                    _sectionCts = null;
                    MeshCutTook(ms);
                    SectionOverlay = new Scene3DOverlay(lines, ++_overlayVersion);
                    _sectionKey = key;
                    FrameRequested?.Invoke();
                    ScheduleSection();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _post(() =>
                {
                    if (!ReferenceEquals(cts, _sectionCts)) return;
                    _sectionCts = null;
                    Notes = "The mesh section could not be drawn: " + ex.Message;
                });
            }
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
        _stallTimer?.Dispose();
        _overlayCts?.Cancel();
        _sectionCts?.Cancel();
        _meshPlaneQuiet?.Dispose();
        _animation?.Dispose();
        foreach (var layer in _layers) layer.Dispose();
        _own.Dispose();
        Source.Dispose();
        Session.Dispose();
    }
}
