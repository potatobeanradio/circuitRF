// brief-em3d-43 — the 3D editor: a .c3d document, and the 3D pane drawing its elaboration.
//
// WHAT THE EDITOR SHOWS IS WHAT THE SOLVER GETS (overview §0). The scene is built from C3dElaborator's
// output — the same elaboration C3dProblemAssembly hands a solver — through Scene3DBuilder, under brief
// 28's generation numbers, never from a second picture of the document. An edit changes the document,
// which is elaborated again; the elaborator's per-object cache makes that ONE object's work, the builder's
// tessellation cache makes it one object's tessellation, and the session's patch makes it one object's
// upload (R-em3d43-1b, gate 6). The document has no setup yet (brief 49), so no air box is drawn.
//
// THE PANE IS THE VIEWER'S (R-em3d43-1a): Viewer3DViewModel with this class as its IViewer3DEditHost. The
// build runs on the thread pool from a SNAPSHOT (the document's own text) taken on the UI thread, so the
// document is never read while it is being edited; the elaborator is serialised by a lock, since two
// generations may overlap for a moment and its caches are not thread-safe.
//
// UNDO (R-em3d43-1c): one entry per user action, storing only what it changed (C3dEdit). A GESTURE — a
// drag, from brief 46 on — edits the document as it goes and commits ONE entry on release (BeginGesture).
// The display unit is a document PREFERENCE, not geometry (owner decision D4): changing it dirties the
// document and is saved, but adds no undo entry, elaborates nothing and moves nothing (gate 10).

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>The UI thread's snapshot for one scene build: the document as its file would say it.</summary>
/// <para>brief-em3d-49 — <paramref name="SetupJson"/> is the active setup's full .cem spelling: its air box is drawn and the
/// ports are resolved against it.</para>
/// <para>brief-em3d-51 — <paramref name="Cell"/> is the cell as a gesture's preview would leave it (a drag writing a
/// parameter's default); null reads the cell from disk.</para>
/// <para>brief-em3d-66 — <paramref name="Ghosts"/>: the objects a boolean's preview or an entered boolean draws as ghosts.</para>
/// <para>brief-em3d-90 — <paramref name="HiddenTints"/>: the active setup's thermal boundaries whose rows are unticked, by row
/// name (<c>thermal:die/zmin</c>) — data for the build thread, which never reads the editor's own sets.</para>
public sealed record C3dSceneInputs(string DocumentText, string Path, string? WorkspaceCws, ColorTheme Theme, ColorVariant Variant,
                                    string? SetupJson = null, C3dCell? Cell = null,
                                    IReadOnlyDictionary<string, Scene3DGhost>? Ghosts = null,
                                    IReadOnlySet<string>? HiddenTints = null);

public sealed partial class C3dEditorViewModel : ObservableObject, IViewer3DEditHost, IDisposable
{
    private readonly C3dElaborator _elaborator;
    private readonly object _elaborating = new();
    private readonly Scene3DTessellationCache _tessellations = new();
    private readonly ConcurrentDictionary<long, C3dElaboration> _elaborations = new();
    private readonly ConcurrentDictionary<long, (string Own, string Stamp)> _readStamps = new();
    private (string Own, string Stamp)? _adoptedReadStamp;
    private readonly Func<string?> _workspaceCws;
    private (double X, double Y, double Z)? _origin;
    private bool _preferenceDirty;
    private bool _loading;
    private string? _savedStamp;

    public string FilePath { get; private set; }

    /// <summary>3D editor bugs round 2 — a SCRATCH design (Settings ▸ On Launch ▸ New 3D Design): <see cref="FilePath"/> is
    /// where it WOULD be — its relative references and its technology resolve from there — but nothing has been written.
    /// Its first save is a Save As, and Simulate asks for that Save As before it runs.</summary>
    public bool IsScratch { get; private set; }
    public C3dDocument Document { get; private set; }
    /// <summary>The ACTIVE frame's history (brief-em3d-48: each pushed-in child has its own, as a layout frame does).</summary>
    public UndoRedoStack UndoRedo { get; private set; } = new();

    /// <summary>The pane's view model — the read-only viewer's own class.</summary>
    public Viewer3DViewModel Viewer { get; }

    public ObservableCollection<C3dTreeGroup> Tree { get; } = [];
    public C3dPropertiesViewModel Properties { get; }

    /// <summary>The elaboration the current scene was built from (UI thread).</summary>
    public C3dElaboration? Elaboration { get; private set; }

    /// <summary>Undo entries pushed — gate 10 reads that a unit change adds none.</summary>
    public int UndoEntries { get; private set; }

    /// <summary>Tessellations the scene builder had to make for this document — gate 6's counter.</summary>
    public long TessellationMisses => _tessellations.Misses;

    /// <summary>The generation whose scene has been adopted and applied (tree, Hidden, Properties).</summary>
    public long AdoptedGeneration => Interlocked.Read(ref _adoptedGeneration);
    private long _adoptedGeneration;

    /// <summary>brief-em3d-108 — how many times the document has been elaborated (each scene built through the elaborator, however
    /// many of its objects were cache hits). A display-only reload adds none.</summary>
    public long Elaborations => Interlocked.Read(ref _elaborations_);
    private long _elaborations_;

    /// <summary>Objects the elaborator lowered because its cache missed — gate 6's counter.</summary>
    public long ObjectsElaborated { get { lock (_elaborating) return _elaborator.ObjectsElaborated; } }

    /// <summary>brief-em3d-66 — kernel trees the elaborator handed the kernel because its cache missed: an operand's drag
    /// re-evaluates its boolean once, on the release.</summary>
    public long KernelTreesBuilt { get { lock (_elaborating) return _elaborator.KernelTreesBuilt; } }

    /// <summary>brief-em3d-46 gate 2 — placed cells read and built because the child cache missed: moving, rotating or
    /// arraying an instance must leave it where it was.</summary>
    public long ChildrenElaborated { get { lock (_elaborating) return _elaborator.ChildrenElaborated; } }

    public bool IsDirty => UndoRedo.IsModified || _preferenceDirty || OtherFramesDirty;

    /// <summary>Raised when the file changed on disk while the document is dirty — the shell asks.</summary>
    public event Action? ExternalChangeWhileDirty;

    /// <summary>Raised when the Properties Inspector should come forward (true: for a rename). The shell handles it.</summary>
    public event Action<bool>? PropertiesRequested;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private LayoutUnit _displayUnit;
    [ObservableProperty] private bool _showTree = true;

    public static IReadOnlyList<LayoutUnit> AllUnits => Layout.LayoutEditorViewModel.AllUnits;

    private readonly Action<Action> _post;

    public C3dEditorViewModel(string path, C3dDocument document, Func<Viewer3DBackend> backend, Func<string?> workspaceCws,
                              Action<Action> post, TechnologyCache? technologies = null, bool scratch = false,
                              CircuitRF.Design.ThreeD.Occ.GeometryKernel? kernel = null)
    {
        _kernel = kernel;
        FilePath = Path.GetFullPath(path);
        IsScratch = scratch;
        Document = document;
        _workspaceCws = workspaceCws;
        _post = post;
        _elaborator = new C3dElaborator(technologies, kernel);
        _technologies = technologies;
        _savedStamp = Stamp(FilePath);
        Viewer = new Viewer3DViewModel(FilePath, Path.GetFileName(FilePath), Snapshot, Build, backend, () => ResultsRootProvider?.Invoke(), post)
        {
            EditHost = this,
            KeepEmptyView = () => Document.Objects.Count == 0 && Document.Instances.Count == 0,
            // brief-em3d-106 — the realistic view reads the document's Look; a .hdr is relative to this file.
            LookSource = () => (ShownLook, FilePath),
        };
        Viewer.SceneAdopted += OnSceneAdopted;
        Viewer.SelectionChanged += OnViewerSelectionChanged;
        Viewer.SelectionChanged += PickForSetup;
        WatchFieldPlots();
        // 3D menu cleanup — the menu bar's Modify items follow the selection and the select mode.
        Viewer.SelectionChanged += RaiseMenuStateChanged;
        Viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Viewer3DViewModel.SelectMode)) RaiseMenuStateChanged(); };
        // brief-em3d-75 — the probe table and the menus follow the thermal result and the sweep step.
        Viewer.ThermalResultsChanged += () => { if (ProbeTableOpen) RefreshProbeTable(); RaiseMenuStateChanged(); };
        WatchSweep();
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Viewer3DViewModel.TemperatureStep) && ProbeTableOpen) RefreshProbeTable();
            if (e.PropertyName == nameof(Viewer3DViewModel.IsRealistic)) Properties?.RealisticChanged();   // brief-em3d-108
            if (e.PropertyName is nameof(Viewer3DViewModel.FieldsAvailable) or nameof(Viewer3DViewModel.IsThermalRun))
            {
                OnPropertyChanged(nameof(ShowThermalRunTools));
                RaiseMenuStateChanged();
            }
        };
        // brief-em3d-44 R-em3d44-5 — the editor snaps; its switches are the user's, stored per user.
        var (snapOn, kinds) = Snap3DPreference.Preferred;
        Viewer.SnapKinds = kinds;
        Viewer.SnapEnabled = snapOn;
        Viewer.SnapTogglesChanged += () => Snap3DPreference.Preferred = (Viewer.SnapEnabled, Viewer.SnapKinds);
        Viewer.FrameRequested += OnViewerFrame;
        Viewer.CursorResolved += OnCursorResolvedForOperation;
        Viewer.DescribeSuffix = NotModelledSuffix;              // brief-em3d-93 — "(not modelled)" on the hover
        Viewer.MaterialHoverContext = HoverContextOf;           // brief-em3d-94 — the lines that apply to what the object is
        Viewer.DescribeGroup = HoverGroupName;                  // 3D editor groups — the group a click takes, named on the hover
        // 3D editor round 1 — the air box's tree tick is the toolbar's air-box switch. Round 3: both are AirBoxShown, the
        // user's choice, which the editor re-applies to every scene it adopts.
        ApplySnapGrid();
        ApplyThemeVariant();
        ThemeService.ThemeChanged += OnThemeChanged;
        Properties = new C3dPropertiesViewModel(this);
        Variables = new C3dVariablesViewModel(this);
        ResolveDocument();
        WatchStack(UndoRedo);
        InitFrames();
        _loading = true;
        DisplayUnit = document.DisplayUnit;
        _loading = false;
        ApplyLengthFormat();
        RebuildTree();
        SyncPlaneTexts();
    }

    /// <summary>Builds the first scene.</summary>
    public void Start() => Viewer.Regenerate();

    // ── the scene: elaboration → problem → scene ─────────────────────────────────────────────

    private object Snapshot()
    {
        // brief-em3d-66 — a boolean previewed or entered draws its own document, with ghosts; brief-em3d-67 — so does a fillet.
        var boolean = FilletScene() ?? BooleanScene();
        return new C3dSceneInputs(boolean?.Text ?? DocumentText(), FilePath, _workspaceCws(), ThemeService.Active, ThemeService.CurrentVariant,
                                  SceneSetupJson(), _namePreview?.Cell, boolean?.Ghosts, HiddenTintsOfActiveSetup());
    }

    /// <summary>
    /// The document as its file would say it — with, while a face or vertex gesture runs, the gesture's edited object
    /// standing in for the document's own (brief-em3d-47 R-em3d47-6). The document's list is swapped for a copy for the
    /// length of one serialisation; its objects are never written.
    /// </summary>
    /// <para>brief-em3d-51 — and with the VARs as the gesture's drag rule would leave them, so every object using a name
    /// the release will write previews too.</para>
    private string DocumentText()
    {
        // brief-em3d-92 — a transparency slider's drag draws a copy with the dragged value.
        if (_transparencyPreview is { } tp) return TransparencyPreviewText(tp);
        // … and so does a face image's (brief-em3d-101).
        if (_faceImageTransparencyPreview is { } fp) return FaceImageTransparencyPreviewText(fp);
        if (_facePreview is not { } p || p.Index >= Document.Objects.Count) return C3dPersistence.Serialize(Document);
        var objects = Document.Objects;
        var variables = Document.Variables;
        var shown = new List<C3dObject>(objects) { [p.Index] = p.Object };
        Document.Objects = shown;
        if (_namePreview is { } names) Document.Variables = names.Variables;
        try { return C3dPersistence.Serialize(Document); }
        finally { Document.Objects = objects; Document.Variables = variables; }
    }

    /// <summary>The origin moves only when the content has moved further than its own size from it, so an
    /// ordinary edit leaves every other object's vertex bytes as they were (gate 6).</summary>
    private static bool NearEnough((double X, double Y, double Z) o, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) e)
    {
        double size = Math.Max(Math.Max(e.X1 - e.X0, e.Y1 - e.Y0), e.Z1 - e.Z0);
        double cx = (e.X0 + e.X1) / 2, cy = (e.Y0 + e.Y1) / 2, cz = (e.Z0 + e.Z1) / 2;
        return Math.Abs(cx - o.X) <= size && Math.Abs(cy - o.Y) <= size && Math.Abs(cz - o.Z) <= size;
    }

    private Scene3DModel Build(long generation, object? state, CancellationToken ct)
    {
        // brief-em3d-108 R-em3d108-1d — a technology that changed only in how it looks: the scene again from the kept elaboration.
        if (state is C3dDisplayRebuild display) return Rebuild(generation, display);
        var inputs = (C3dSceneInputs)state!;
        var doc = C3dPersistence.Deserialize(inputs.DocumentText);
        lock (_elaborating)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _elaborations_);
            var e = _elaborator.Elaborate(doc, inputs.Path, inputs.WorkspaceCws, new C3dElaborationOptions { Cell = inputs.Cell, PadsOptional = true });
            _elaborations[generation] = e;
            _readStamps[generation] = (inputs.Path, ReadStamp(e.FilesRead, inputs.Path));
            _frameKeys[generation] = inputs.Path;
            // brief-em3d-84 — the origin rule `render --field` places its slice by (FieldPlotResolver.SceneOrigin).
            var extent = e.DisplayExtent() ?? FieldPlotResolver.EmptyExtent;
            if (_origin is not { } o || !NearEnough(o, extent))
                _origin = FieldPlotResolver.SceneOrigin(extent);
            // brief-em3d-49 — the active setup's box, ports and face boundaries, resolved as a run resolves them.
            var records = ResolveRecords(doc, e, inputs.SetupJson, inputs.HiddenTints);
            _records[generation] = records;
            ComputeFidelity(generation, doc, inputs.Path, inputs.WorkspaceCws);
            _built[generation] = new C3dDisplayRebuild(doc, inputs, e, records,
                                                       inputs.Ghosts is not { Count: > 0 } && inputs.Cell is null);
            // Pushed in, only the child is drawn — as the layout editor draws only the cell pushed into (owner, 2026-10-04).
            // brief-em3d-48 R-em3d48-4a drew the top document around it, dimmed; that read as part of the child.
            return Assemble(generation, doc, e, inputs, records);
        }
    }

    /// <summary>The scene from an elaboration: what <see cref="Build"/> does once the document has been elaborated, and all that a
    /// display-only rebuild does (brief-em3d-108). Called under the elaboration lock: it shares the tessellation cache.</summary>
    private Scene3DModel Assemble(long generation, C3dDocument doc, C3dElaboration e, C3dSceneInputs inputs, RecordsView records)
    {
        var extent = e.DisplayExtent() ?? FieldPlotResolver.EmptyExtent;
        var box = records.Box ?? C3dProblemAssembly.ExtentBox(extent);
        IReadOnlyList<Em3dSolid> solids = e.Solids;
        IReadOnlyList<Em3dSheet> sheets = e.Sheets;
        IReadOnlyList<Em3dMaterial> materials = e.Materials;
        // 3D editor bugs round 1 — what has no material is drawn as a wireframe, last (never an instance run's
        // element), so it can still be seen, picked and edited. Round 2: the solver ignores it (a warning); a material
        // the technology lacks is still a refusal that stops a run.
        var unassigned = new HashSet<string>(e.UnassignedSolids.Select(s => s.Name).Concat(e.UnassignedSheets.Select(s => s.Name)), StringComparer.Ordinal);
        if (unassigned.Count > 0)
        {
            solids = [.. solids, .. e.UnassignedSolids];
            sheets = [.. sheets, .. e.UnassignedSheets];
        }
        var instancing = InstancingFor(doc, e);
        var faceImageHosts = e.FaceImages.Select(u => u.Object).ToHashSet(StringComparer.Ordinal);
        // brief-em3d-88 — the one problem a view draws, which `render` builds a thermal section's picture from too.
        var problem = C3dProblemAssembly.ViewProblem(solids, sheets, materials, [.. records.Ports.Select(r => r.Resolved).OfType<Em3dPort>()], box,
                                                     e.CylinderSheets);
        var notes = new List<string>(e.Refusals);
        notes.AddRange(e.Warnings);
        notes.AddRange(e.Notes);
        return Scene3DBuilder.Build(problem, generation, e.Origins, e.Technology, inputs.Theme, inputs.Variant, notes,
            new Scene3DBuildOptions(name => e.Provenance.TryGetValue(name, out var p) ? p.FaceNames : null,
                                    _tessellations, DrawAirBox: records.Box is not null, Origin: _origin,
                                    FeatureShare: name => e.Provenance.TryGetValue(name, out var p) ? ShareOf(p) : null,
                                    Instancing: name => unassigned.Contains(name) || e.Images.ContainsKey(name) || faceImageHosts.Contains(name) ? null : instancing(name),
                                    Wireframe: unassigned.Count > 0 ? unassigned.Contains : null,
                                    EditorBoundaries: true,
                                    Ghost: inputs.Ghosts is { Count: > 0 } ghosts ? n => ghosts.TryGetValue(n, out var g) ? g : Scene3DGhost.None : null,
                                    OwnFrame: name => OwnFrameOf(doc, e, name),
                                    HideOutermostDielectric: false,
                                    Transparency: Scene3DTransparency.Of(e.Provenance),
                                    // brief-em3d-105 — each object's look over its material's (the realistic view's slots).
                                    Appearance: CircuitRF.Design.ThreeD.Appearance.AppearanceOverride.Of(e.Provenance),
                                    // brief-em3d-101 — an image sheet is drawn with its picture (C3dElaboration.Images).
                                    Images: e.Images.Count == 0 ? null : name => e.Images.GetValueOrDefault(name),
                                    FaceImages: e.FaceImages.Count == 0 ? null : e.FaceImages,
                                    FaceTints: [.. records.Boundaries.Where(b => b.Refusal is null)
                                                       .Select(b => new Scene3DFaceTint(b.Boundary.Object + "/" + b.Boundary.Face, b.Boundary.Kind, b.Pieces)),
                                                .. records.ThermalTints]));
    }

    /// <summary>
    /// brief-em3d-67 R-em3d67-2b — an object's map from world metres into its own frame, where the runs one pair of faces
    /// bounds are numbered: the inverse of its placement (and, inside an instance, of the element's transform). Null — the
    /// identity — for an object the document does not place.
    /// </summary>
    private static Func<Point3, Point3>? OwnFrameOf(C3dDocument doc, C3dElaboration e, string name)
    {
        if (!e.Provenance.TryGetValue(name, out var p)) return null;
        var t = C3dTransform.Identity;
        // A managed object's own placement (a disabled chain's target carries it; the features' are the identity).
        if (p.InstancePath.Length == 0 && doc.Objects.FirstOrDefault(o => o.Name == name) is { } obj
            && (C3dFillets.DisabledCore(obj) ?? obj) is { Placement.IsDefault: false } placed && !C3dOperands.IsKernel(placed))
            t = C3dLowering.InMetres(placed.Placement.ToTransform(), doc.DbuPerMicron);
        if (p.Element is { } w) t = t.Then(w);
        if (t == C3dTransform.Identity) return null;
        var i = t.Inverse();
        return q => new Point3(i.M00 * q.X + i.M01 * q.Y + i.M02 * q.Z + i.Tx, i.M10 * q.X + i.M11 * q.Y + i.M12 * q.Z + i.Ty,
                               i.M20 * q.X + i.M21 * q.Y + i.M22 * q.Z + i.Tz);
    }

    /// <summary>
    /// brief-em3d-44 R-em3d44-3b — objects of one child document under element transforms with the same
    /// rotation are one mesh moved by a translation, so they share one feature table: the key is (file,
    /// object, rotation), the translation is the element's. The document's own objects have their own.
    /// </summary>
    private static Scene3DFeatureShare? ShareOf(C3dProvenance p)
    {
        if (p.Element is not { } w) return null;
        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{p.DocumentPath}|{p.ObjectName}|{w.M00:R},{w.M01:R},{w.M02:R},{w.M10:R},{w.M11:R},{w.M12:R},{w.M20:R},{w.M21:R},{w.M22:R}");
        return new Scene3DFeatureShare(key, w.Tx, w.Ty, w.Tz);
    }

    private HashSet<string> _postedRefusals = new(StringComparer.Ordinal);

    /// <summary>
    /// Issue #33 — what kept part of the view from being drawn goes to the Messages panel, in full: the canvas label is
    /// only the short form ("U1: cannot be drawn in 3D"), and it does not say why. Each refusal is posted once while it
    /// stands; one that clears and comes back is posted again.
    /// </summary>
    private void PostNewRefusals(C3dElaboration e)
    {
        var now = new HashSet<string>(e.Refusals.Where(r => !e.NotModelledRefusals.Contains(r)), StringComparer.Ordinal);
        foreach (string r in now)
            if (!_postedRefusals.Contains(r)) PostMessage?.Invoke(r, true);
        _postedRefusals = now;
    }

    private void OnSceneAdopted()
    {
        long gen = Viewer.Scene.Generation;
        CarryCameraAcrossOrigin(gen);
        if (_elaborations.TryGetValue(gen, out var e)) { Elaboration = e; PostNewRefusals(e); }
        if (_readStamps.TryGetValue(gen, out var read)) _adoptedReadStamp = read;
        foreach (long old in _readStamps.Keys.Where(k => k <= gen).ToList()) _readStamps.TryRemove(old, out _);
        AdoptBuilt(gen);                           // brief-em3d-108 — what a display-only reload re-assembles
        AdoptRecords(gen);
        ApplySnapGrid();
        ApplySnapExclusion();
        foreach (long old in _elaborations.Keys.Where(k => k <= gen).ToList()) _elaborations.TryRemove(old, out _);
        ApplyHiddenFlags();
        ApplyHiddenPorts();
        RefreshTreeVisibility();
        RebuildInstanceChildren();
        RefreshWireFlags();
        RefreshKernelFlags();
        RememberInstanceBounds();
        ApplyVisiblePlots();                       // brief-em3d-83 — its plane and faces, on this scene
        RefreshPlacementDimension();               // brief-sar-12 — the placed device's separation, measured again
        if (_fitOnAdopt && Viewer.Scene.Objects.Length > 0)
        {
            _fitOnAdopt = false;
            Viewer.FitCommand.Execute(null);
        }
        // 3D editor round 1 — a node selected while the scene did not hold its object (a box given its first material)
        // becomes the scene's selection the moment the scene does.
        if (Viewer.Selection.Count == 0 && SelectedTreeItem is { ObjectIndex: >= 0 and var pi } && pi < Document.Objects.Count &&
            SceneObjectsFor(Document.Objects[pi]).FirstOrDefault() is { } now)
        {
            _syncingTree = true;
            try { Viewer.SetSelection([Scene3DItem.OfObject(now.Id)]); }
            finally { _syncingTree = false; }
        }
        Properties.Reload();
        OnPropertyChanged(nameof(Materials));
        OnPropertyChanged(nameof(MaterialChoices));
        SyncCurrentMaterial();
        RefreshGridText();
        ReleaseHeldPreview(gen);
        ReselectFace();
        ReselectVertex();
        FilletSceneAdopted(gen);
        // Published LAST: a reader that waits for this generation (a test's settle) must find the adoption finished — the
        // held preview released and the face reselected — not half done.
        Interlocked.Exchange(ref _adoptedGeneration, gen);
        RaiseMenuStateChanged();
    }

    /// <summary>
    /// 3D editor bugs round 2 — the one line drawn on the viewport, now that the status pane under it is hidden: the last
    /// message (a refusal, what a gesture did) while there is one, else what the armed tool wants next, else — pushed
    /// into a placed cell — which frame the coordinates are in. The full text is the pane's (CRF_3D_STATUS_PANE=1).
    /// </summary>
    public string ViewportLine
        => StatusMessage is { Length: > 0 } m ? m : ToolPrompt is { Length: > 0 } p ? p : BooleanBreadcrumb is { Length: > 0 } b ? b : FrameText;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(StatusMessage) or nameof(ToolPrompt) or nameof(FrameText))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ViewportLine)));
    }

    /// <summary>3D editor bugs round 2 — the status text under the viewport is hidden; it is kept, whole, for debugging, and
    /// shown when the environment sets <c>CRF_3D_STATUS_PANE=1</c>.</summary>
    public static bool ShowStatusPane { get; } = Environment.GetEnvironmentVariable("CRF_3D_STATUS_PANE") == "1";

    private readonly ConcurrentDictionary<long, string> _frameKeys = new();
    private (string Frame, (double X, double Y, double Z) Origin)? _adoptedFrame;

    /// <summary>
    /// 3D editor bugs round 2 — the camera lives in scene-LOCAL coordinates, and Build re-bases the origin when the design
    /// has moved far from the old one (NearEnough), which one object dragged a few of its own sizes does. The camera used to
    /// keep its local numbers while the world moved under them, so every object, the grid and the axes jumped at once: the
    /// viewport seemed to move with the drag. Within one frame the target is carried to the same WORLD point. Across a
    /// push or a pop it is not — a push fits, and a pop restores the parent's own camera.
    /// </summary>
    private void CarryCameraAcrossOrigin(long gen)
    {
        var scene = Viewer.Scene;
        if (!_frameKeys.TryGetValue(gen, out var frame)) return;
        foreach (long old in _frameKeys.Keys.Where(k => k < gen).ToList()) _frameKeys.TryRemove(old, out _);
        if (_adoptedFrame is { } prev && prev.Frame == frame && prev.Origin != scene.Origin)
            Viewer.View.Camera.Target += CameraShift(prev.Origin, scene.Origin);
        _adoptedFrame = (frame, scene.Origin);
    }

    /// <summary>What a scene-local point gains when the origin moves from <paramref name="from"/> to <paramref name="to"/>
    /// with the world point held still.</summary>
    internal static Vector3 CameraShift((double X, double Y, double Z) from, (double X, double Y, double Z) to)
        => new((float)(from.X - to.X), (float)(from.Y - to.Y), (float)(from.Z - to.Z));

    /// <summary>A document object's <c>Hidden</c> is document state (brief 41 §2a): the pane follows it.</summary>
    private void ApplyHiddenFlags() => ApplyHiddenFlags(Document.Objects);

    /// <summary>The pane follows <paramref name="objects"/>' <c>Hidden</c>, as one batch.</summary>
    private void ApplyHiddenFlags(IEnumerable<C3dObject> objects)
    {
        var changes = objects.SelectMany(o => SceneObjectsFor(o, balls: true).Select(s => (s.Id, !o.Hidden))).ToList();
        // brief-em3d-101 Phase B — a hidden object hides its face images; a hidden face image is not drawn at all.
        foreach (var o in objects.Where(o => o.FaceImages is { Count: > 0 }))
            foreach (var fi in o.FaceImages!)
                if (SceneObject(CircuitRF.Design.ThreeD.C3dImages.FacePrefix + o.Name + "/" + fi.Face) is { } s) changes.Add((s.Id, !o.Hidden));
        // brief-em3d-66 — an entered operand's own Hidden.
        if (EnteredTop is >= 0 and var top)
            foreach (var op in _enteredOperands)
                if (SceneObject(op.SceneName) is { } s && C3dBooleans.At(Document.Objects[top], op.Path) is { } local)
                    changes.Add((s.Id, !local.Hidden));
        Viewer.SetVisibleEverywhere(changes);
    }

    /// <summary>The scene object a document object became, by its name, or null (a polyline, a refusal).</summary>
    public Scene3DObject? SceneObject(string name)
    {
        // Indexed per adopted scene (each adoption is a new Scene3DModel): a linear scan here made every per-object pass
        // over a 1,500-object document quadratic. The first object of a name wins, as FirstOrDefault did.
        var scene = Viewer.Scene;
        if (_sceneNames is not { } c || !ReferenceEquals(c.Scene, scene))
        {
            var byName = new Dictionary<string, Scene3DObject>(scene.Objects.Length, StringComparer.Ordinal);
            foreach (var o in scene.Objects) byName.TryAdd(o.Name, o);
            _sceneNames = c = (scene, byName);
        }
        return c.ByName.GetValueOrDefault(name);
    }

    private (Scene3DModel Scene, Dictionary<string, Scene3DObject> ByName)? _sceneNames;

    /// <summary>3D editor round 4 — the scene objects a document object became: itself, or every element of a wire array
    /// (<c>w1[k]</c>) — and, with <paramref name="balls"/>, each wire's balls.</summary>
    public IEnumerable<Scene3DObject> SceneObjectsFor(C3dObject o, bool balls = false)
    {
        if (o is not C3dWire w) return SceneObject(o.Name) is { } s ? [s] : [];
        var names = C3dWires.ElementNames(w);
        if (balls) names = names.SelectMany(n => new[] { n, n + "/ball/start", n + "/ball/end" });
        return names.Select(SceneObject).OfType<Scene3DObject>().ToList();
    }

    /// <summary>brief-em3d-66 — the index a scene object is edited through: the document's own object's, or an entered
    /// boolean's operand's (<see cref="OperandIndexOf"/>); −1 for neither.</summary>
    public int EditableIndex(Scene3DObject o) => DocumentIndex(o) is >= 0 and var i ? i : OperandIndexOf(o);

    /// <summary>The document index of a scene object that is the document's own, or −1.</summary>
    public int DocumentIndex(Scene3DObject o)
    {
        if (InstanceOf(o) is not null) return -1;
        int i = Document.Objects.FindIndex(d => d.Name == o.Name);
        // brief-em3d-50 — a wire's ball is a solid of its own, and belongs to the wire it was made for.
        if (i < 0 && Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true && p.ObjectName != o.Name)
            i = Document.Objects.FindIndex(d => d.Name == p.ObjectName);
        // brief-sar-3 — a phantom's layer is its phantom's solid.
        if (i < 0) i = PhantomIndexOf(o);
        return i;
    }

    // ── IViewer3DEditHost ────────────────────────────────────────────────────────────────────

    public string KindOf(Scene3DObject o)
    {
        if (InstanceOf(o) is { } inst) return $"Part of instance {inst}";
        // brief-em3d-66 — an entered boolean's operand says what it is in its boolean.
        if (OperandIndexOf(o) is >= 0 and var oi && ObjectAt(oi) is { } operand)
            return $"{C3dObject.KindOf(operand)} ({(C3dBooleans.LastStep(_enteredOperands[oi - OperandBase].Path) < 0 ? "Blank" : "Tool")})";
        return Document.Objects.FirstOrDefault(d => d.Name == o.Name) is { } obj ? C3dObject.KindOf(obj) : o.Kind.ToString();
    }

    public string? InstanceOf(Scene3DObject o)
        => Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true && p.InstancePath.Length > 0 ? p.InstancePath : null;

    public IReadOnlyList<string> Materials
        => Elaboration?.Technology?.ResolvedMaterials.Select(m => m.Name).ToList() ?? (IReadOnlyList<string>)[];

    public bool SetHidden(IReadOnlyList<Scene3DObject> objects, bool hidden, string description)
    {
        var indices = objects.Select(EditableIndex).Where(i => i >= 0).Distinct().ToList();
        if (indices.Count == 0) return false;
        ChangeHidden(description, indices, _ => hidden);
        // An instance's contents are not the document's to hide: the view hides them for this session.
        foreach (var o in objects.Where(o => EditableIndex(o) < 0)) Viewer.SetVisibleEverywhere(o.Id, !hidden);
        if (hidden) Viewer.SetSelection([]);
        return true;
    }

    public bool ShowAll(IReadOnlyList<Scene3DObject>? keep)
    {
        var keepNames = keep?.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var indices = Enumerable.Range(0, Document.Objects.Count)
            .Where(i => Document.Objects[i].Hidden != (keepNames is not null && !keepNames.Contains(Document.Objects[i].Name)))
            .ToList();
        if (indices.Count > 0)
            ChangeHidden(keep is null ? "Show all" : "Isolate", indices,
                         o => keepNames is not null && !keepNames.Contains(o.Name));
        foreach (var s in Viewer.Scene.Objects.Where(s => s.Pickable && DocumentIndex(s) < 0))
            Viewer.SetVisibleEverywhere(s.Id, keepNames is null || keepNames.Contains(s.Name));
        if (keepNames is null && _hiddenPorts.Count > 0)
        {
            _hiddenPorts.Clear();       // Show All shows a refused port's outline too, and its row's tick follows
            foreach (var row in AllTreeItems().Where(t => t.Kind is "Port" or TerminalKind)) row.Sync(true);
            Viewer.RequestFrame();
        }
        return true;
    }

    public void SetMaterial(IReadOnlyList<Scene3DObject> objects, string material)
    {
        var indices = objects.Select(EditableIndex).Where(i => i >= 0).Distinct().ToList();
        if (indices.Count == 0) return;
        ChangeObjects(indices.Count == 1 ? $"Material of {ObjectLabel(indices[0])}" : $"Material of {indices.Count} objects",
                      indices, o => SetMaterialOf(o, material));
    }

    public bool DeleteSelection()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object) return false;
        // brief-em3d-49 — a selected port is a document record: deleted as one.
        if (SelectedPorts() is { Count: > 0 } ports) { DeletePorts(ports); return true; }
        // brief-em3d-90 — so is a selected boundary's tint: the boundary goes, the face stays
        if (SelectedTints() is { Count: > 0 } tints) { DeleteTints(tints); return true; }
        // brief-em3d-101 R-em3d101-9b — a selected face image is a record: removed as one, its face stays
        if (SelectedFaceImages() is { Count: > 0 } faceImages) { RemoveFaceImages(faceImages); return true; }
        // … and so are their tree rows when the images are hidden: a hidden one is not in the scene, so only its row is selected.
        if (SelectedTreeItems.Count > 0 && SelectedTreeItems.All(r => r.Kind == FaceImageKind && r.FaceImageHost >= 0 && r.FaceImageFace is not null))
        {
            RemoveFaceImages([.. SelectedTreeItems.Select(r => (r.FaceImageHost, r.FaceImageFace!)).Distinct()]);
            return true;
        }
        var objects = Viewer.SelectedObjects();
        // brief-em3d-66 R-em3d66-6 — an entered operand: a Tool is removed from its boolean; the Blank is refused.
        if (objects.Select(OperandIndexOf).Where(i => i >= 0).Distinct().ToList() is { Count: > 0 } operands) return DeleteOperands(operands);
        // A group selected whole is deleted whole, its instances with its objects (C3dGroups).
        if (SelectedUnits().Where(u => u.IsGroup).ToList() is { Count: > 0 } groups && groups.SelectMany(u => C3dGroups.MembersOf(Document, u.GroupPath!)).Any(m => m.Instance))
        {
            DeleteMembers(SelectedMembers(), groups.Count == 1 ? $"Delete {C3dGroups.NameOf(groups[0].GroupPath!)}" : $"Delete {groups.Count} groups");
            return true;
        }
        var indices = objects.Select(DocumentIndex).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        if (indices.Count == 0)
        {
            if (objects.Count > 0) StatusMessage = "Nothing deletable is selected: an instance's contents belong to its own cell.";
            return objects.Count > 0;
        }
        DeleteObjects(indices);
        return true;
    }

    /// <summary>Deletes the document objects at <paramref name="indices"/>: one undo entry. The one delete, for the scene's
    /// selection and for the tree's node alike (a node the scene does not draw can be deleted too).</summary>
    public void DeleteObjects(IReadOnlyList<int> indices)
    {
        var sorted = indices.Where(i => i >= 0 && i < Document.Objects.Count).Distinct().OrderBy(i => i).ToList();
        if (sorted.Count == 0) return;
        var slots = sorted.Select(i => new C3dEditSlot(false, i, C3dPersistence.SerializeObject(Document.Objects[i]), null)).ToList();
        Viewer.SetSelection([]);
        Push(new C3dEdit(sorted.Count == 1 ? $"Delete {Document.Objects[sorted[0]].Name}" : $"Delete {sorted.Count} objects",
                         slots, ApplySlots));
    }

    /// <summary>3D editor round 1 — the selection's fields are in the application's Properties Inspector (the one a
    /// schematic and a layout use): the shell brings it forward, opening it if it was closed; a rename also puts the
    /// caret in its Name field.</summary>
    public void ShowProperties(bool rename)
    {
        PropertiesRequested?.Invoke(rename);
        if (rename) Properties.RequestRename();
    }

    /// <summary>The toolbar's Properties button.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowPropertiesPanel() => ShowProperties(rename: false);

    // ── edits ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One undoable edit of the objects at <paramref name="indices"/>: each is copied, the copy is
    /// changed, and only the ones whose file spelling changed are in the entry. Nothing changed, no entry.</summary>
    public void ChangeObjects(string description, IReadOnlyList<int> indices, Action<C3dObject> mutate)
    {
        var slots = new List<C3dEditSlot>();
        // brief-em3d-66 — an entered operand is changed in its world form and written back into its top-level object.
        var operands = new List<(int, C3dObject)>();
        foreach (int i in indices.Where(IsOperandIndex).Distinct())
            if (ObjectAt(i) is { } w) { mutate(w); operands.Add((i, w)); }
        slots.AddRange(ReplacementSlots(operands));
        foreach (int i in indices.Where(i => !IsOperandIndex(i)).Distinct().OrderBy(i => i))
        {
            string before = C3dPersistence.SerializeObject(Document.Objects[i]);
            var copy = C3dPersistence.DeserializeObject(before);
            mutate(copy);
            string after = C3dPersistence.SerializeObject(copy);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        if (slots.Count > 0) Push(new C3dEdit(description, slots, ApplySlots));
    }

    /// <summary>
    /// One undoable edit of the objects' <c>Hidden</c> alone: <paramref name="hidden"/> says each one's new value. Hidden is
    /// drawing state that no elaboration reads — a hidden object is in the scene, and the view's own flag decides whether it
    /// is drawn — so this entry re-elaborates nothing and regenerates no scene (<see cref="ApplyHiddenSlots"/>). Through
    /// <see cref="ChangeObjects"/>, a tick on a 1,500-object flattened board waited 0.2-0.7 s for a scene it already had. An
    /// entered operand goes that way still: its Hidden is written back into its boolean.
    /// </summary>
    public void ChangeHidden(string description, IReadOnlyList<int> indices, Func<C3dObject, bool> hidden)
    {
        if (indices.Any(IsOperandIndex))
        {
            ChangeObjects(description, indices, o => o.Hidden = hidden(o));
            return;
        }
        var slots = new List<C3dEditSlot>();
        foreach (int i in indices.Distinct().OrderBy(i => i))
        {
            var o = Document.Objects[i];
            bool h = hidden(o);
            if (o.Hidden == h) continue;
            string before = C3dPersistence.SerializeObject(o);
            var copy = C3dPersistence.DeserializeObject(before);
            copy.Hidden = h;
            slots.Add(new C3dEditSlot(false, i, before, C3dPersistence.SerializeObject(copy)));
        }
        if (slots.Count > 0) Push(new C3dEdit(description, slots, ApplyHiddenSlots));
    }

    /// <summary>A rename: validated here, one entry, and the pane keeps the object selected under its new name.</summary>
    public string? Rename(int index, string name)
    {
        name = name.Trim();
        if (IsOperandIndex(index)) return RenameOperand(index, name);
        var obj = Document.Objects[index];
        if (name == obj.Name) return null;
        if (name.Length == 0) return "A name cannot be empty.";
        if (string.Equals(name, "airbox", StringComparison.OrdinalIgnoreCase)) return "'airbox' is reserved: the air box's faces are named after it.";
        if (name.Contains('/')) return "A name cannot hold '/': an instance's contents are named '<instance>/<object>'.";
        if (Document.Objects.Any(o => o != obj && o.Name == name) || Document.Instances.Any(i => i.Name == name) || NestedNames().Contains(name)
            || Document.LumpedElements.Any(l => l.Name == name))
            return $"'{name}' is already the name of something in this 3D view.";
        Viewer.ExpectRename(obj.Name, name);
        bool entered = _entered is { } e0 && e0.Top == obj.Name;
        ChangeObjects($"Rename {obj.Name} to {name}", [index], o => o.Name = name);
        if (entered && _entered is { } e1) { _entered = (name, e1.Path); RefreshEntered(); Viewer.Regenerate(); }
        return null;
    }

    /// <summary>brief-em3d-66 — every Tool's name inside a boolean, at any depth: unique across the document with the
    /// top-level names (R-em3d64-1d).</summary>
    private HashSet<string> NestedNames()
        => [.. Document.Objects.SelectMany(o => C3dOperands.SelfAndDescendants(o).Skip(1)).Select(o => o.Name).Where(n => n.Length > 0)];

    /// <summary>A rename of an entered operand: a Tool takes the name; a Blank has none of its own.</summary>
    private string? RenameOperand(int index, string name)
    {
        int top = TopOf(index, out string path);
        if (top < 0 || C3dBooleans.At(Document.Objects[top], path) is not { } local) return null;
        if (C3dBooleans.LastStep(path) < 0) return name == ObjectLabel(index) ? null : "A Blank takes its boolean's name: rename the boolean instead.";
        if (name == local.Name) return null;
        if (name.Length == 0) return "A name cannot be empty.";
        if (name.Contains('/') || name.Contains(':') || name.Contains('|')) return "A name cannot hold '/', ':' or '|'.";
        if (Document.Objects.Any(o => o.Name == name) || Document.Instances.Any(i => i.Name == name) || NestedNames().Contains(name))
            return $"'{name}' is already the name of something in this 3D view.";
        ChangeObjects($"Rename {local.Name} to {name}", [index], o => o.Name = name);
        return null;
    }

    /// <para>brief-em3d-51 — every entry replacing objects passes the drag rule first (<see cref="ThroughNames"/>): it may
    /// become an entry that writes the names the edit moved, or be refused. Inside a group (<see cref="BeginGroup"/>) the
    /// entry is applied and kept for the group's one entry.</para>
    /// <returns>False when the drag rule refused it (the status line says why, and the document is as it was).</returns>
    private bool Push(IUiCommand edit)
    {
        if (edit is C3dEdit ce)
        {
            if (ThroughNames(ce) is not { } through) return false;
            edit = through;
        }
        CanReplaceWithNumber = false;
        StatusMessage = "";
        if (_group is { } group)
        {
            edit.Execute();
            group.Add(edit);
            return true;
        }
        UndoRedo.Execute(edit);
        UndoEntries++;
        return true;
    }

    /// <summary>Times the document's objects were written — brief-em3d-47 gate 7 reads that a drag writes none.</summary>
    public int DocumentWrites { get; private set; }

    /// <summary>The one place the document's objects change, for every entry, forward and back.</summary>
    private void ApplySlots(IReadOnlyList<C3dEditSlot> slots, bool forward)
    {
        DocumentWrites++;
        C3dEdit.Apply(Document, slots, forward);
        DocumentChanged();
    }

    /// <summary><see cref="ChangeHidden"/>'s entry, forward and back: the document is written as any entry writes it, and then
    /// only what reads Hidden follows — the pane's flags, the tree's ticks, the Inspector (it holds the replaced objects) and
    /// the dirty and stale lines. Nothing is elaborated: a later edit elaborates the document as it stands.</summary>
    private void ApplyHiddenSlots(IReadOnlyList<C3dEditSlot> slots, bool forward)
    {
        DocumentWrites++;
        C3dEdit.Apply(Document, slots, forward);
        _targetsCache = null;
        ApplyHiddenFlags(slots.Select(s => Document.Objects[s.Index]));
        RefreshTreeVisibility();
        Properties.Reload();
        OnPropertyChanged(nameof(IsDirty));
        RefreshFieldsStale();
    }

    /// <summary>The document changed: elaborate again (the caches make it the changed objects' work), and
    /// bring the tree and the panel up to date.</summary>
    private void DocumentChanged()
    {
        // Targets() caches document INDICES keyed on the selection and the adopted scene, and neither changes until the
        // new scene is adopted: an undo that shrinks the object list (a Flatten's) left them pointing past its end.
        _targetsCache = null;
        // brief-em3d-66 — the panel previews the document as it was when it opened: any other change closes it; an entered
        // boolean follows the document (and is left when it is gone).
        CancelBoolean();
        RefreshEntered();
        ResolveDocument();
        Viewer.Regenerate();
        Viewer.LookChanged();          // brief-em3d-106 — an undo, a reload or another frame's document may carry another Look
        RebuildTree();
        Properties.Reload();
        OnPropertyChanged(nameof(IsDirty));
        RefreshFieldsStale();
    }

    /// <summary>
    /// R-em3d43-1c — a gesture (a drag) changes the objects at <paramref name="indices"/> as it goes and is
    /// ONE undo entry, pushed by <see cref="C3dGesture.Commit"/>. Each <see cref="C3dGesture.Update"/> re-elaborates
    /// the changed objects only, so a drag previews through the same path an edit takes.
    /// </summary>
    /// <para>brief-em3d-44 R-snpf-4: what the gesture moves never attracts the snap, however the gesture started —
    /// the objects themselves unless <paramref name="excludeObjects"/> is false (a face or vertex drag, which
    /// names what it moves with <see cref="C3dGesture.ExcludeFace"/> / <see cref="C3dGesture.ExcludeVertex"/>).</para>
    public C3dGesture BeginGesture(string description, IReadOnlyList<int> indices, bool excludeObjects = true)
    {
        var g = new C3dGesture(this, description, indices);
        if (excludeObjects)
        {
            foreach (int i in indices) _snapExcludedObjects.Add(Document.Objects[i].Name);
            ApplySnapExclusion();
        }
        return g;
    }

    public sealed class C3dGesture
    {
        private readonly C3dEditorViewModel _owner;
        private readonly string _description;
        private readonly List<(int Index, string Before)> _before;
        private bool _done;

        internal C3dGesture(C3dEditorViewModel owner, string description, IReadOnlyList<int> indices)
        {
            _owner = owner;
            _description = description;
            _before = [.. indices.Distinct().OrderBy(i => i).Select(i => (i, C3dPersistence.SerializeObject(owner.Document.Objects[i])))];
        }

        /// <summary>
        /// A face drag: face <paramref name="face"/> of <paramref name="objectName"/> never attracts the snap while
        /// the gesture lasts — its corners, edges and centre wherever the scene now has them, and its corners
        /// where they are NOW (the old position, until the scene has caught up with the drag).
        /// </summary>
        public void ExcludeFace(string objectName, int face)
        {
            _owner._snapExcludedFaces.Add((objectName, face));
            if (_owner.SceneObject(objectName) is { } o && _owner.Viewer.Scene.FeaturesOf(o.Id) is { Table: { } t } fr && face >= 0 && face < t.FaceCount)
                for (int k = t.FaceVertexStart[face]; k < t.FaceVertexStart[face + 1]; k++)
                    _owner._snapExcludedPoints.Add(fr.Vertex(t.FaceVertices[k]));
            _owner.ApplySnapExclusion();
        }

        /// <summary>A vertex drag: the corner at <paramref name="world"/> (metres) never attracts the snap.</summary>
        public void ExcludeVertex(Point3 world)
        {
            _owner._snapExcludedPoints.Add(world);
            _owner.ApplySnapExclusion();
        }

        /// <summary>One step of the gesture: the objects change in the document, no entry is pushed.</summary>
        public void Update(Action<C3dObject> mutate)
        {
            if (_done) throw new InvalidOperationException("This gesture has ended.");
            _owner.DocumentWrites++;
            foreach (var (i, _) in _before) mutate(_owner.Document.Objects[i]);
            _owner.DocumentChanged();
        }

        /// <summary>The release: one entry holding each object's first before and last after.</summary>
        public void Commit()
        {
            if (_done) return;
            _done = true;
            _owner.ClearSnapExclusion();
            var slots = _before.Select(b => new C3dEditSlot(false, b.Index, b.Before, C3dPersistence.SerializeObject(_owner.Document.Objects[b.Index])))
                               .Where(s => s.Before != s.After).ToList();
            if (slots.Count > 0) _owner.Push(new C3dEdit(_description, slots, _owner.ApplySlots, alreadyApplied: true));
        }

        /// <summary>Esc: every object back as it was, no entry.</summary>
        public void Cancel()
        {
            if (_done) return;
            _done = true;
            _owner.ClearSnapExclusion();
            foreach (var (i, before) in _before) _owner.Document.Objects[i] = C3dPersistence.DeserializeObject(before);
            _owner.DocumentChanged();
        }
    }

    // ── snapping (brief-em3d-44) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d44-3c / brief-em3d-45 R-em3d45-2d — the grid the snap uses: the drawing plane, at the document's snap
    /// step (its technology's default when the document states none) — not necessarily the drawn minor spacing.
    /// The pane's drawn grid follows the same plane.
    /// </summary>
    private void ApplySnapGrid()
    {
        Viewer.SnapGrid = new Snap3DGrid(_plane.Plane, _plane.OffsetDbu, SnapPitch, Document.DbuPerMicron);
        ApplyDrawingGrid();
        RefreshSnapDistance();
    }

    /// <summary>
    /// R-em3d44-4 — a snapped point as a DBU point of this document, and whether it IS one exactly: the object's
    /// placements up to this document are all integral at this document's DBU (the elaboration says so), and
    /// the point converts to integers. Otherwise the point is metres and <see cref="C3dSnapPoint.Dbu"/> is it
    /// rounded — which a tool uses only at the moment it commits.
    /// </summary>
    public C3dSnapPoint ToDocumentPoint(in Snap3DResult snap)
    {
        bool chain = snap.Kind == Snap3DKind.Grid
                     || (Viewer.Scene.Object(snap.Object) is { } o &&
                         Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true && p!.Exact);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        (long D, bool Whole) Of(double m)
        {
            double d = m / per, r = Math.Round(d);
            return ((long)r, Math.Abs(d - r) <= 1e-6 + 1e-12 * Math.Abs(d));
        }
        var (x, wx) = Of(snap.World.X);
        var (y, wy) = Of(snap.World.Y);
        var (z, wz) = Of(snap.World.Z);
        return new C3dSnapPoint(new C3dPoint3(x, y, z), snap.World, chain && wx && wy && wz);
    }

    /// <summary>R-em3d44-5 — <c>(x, y, z) µm</c> in the display unit, with <c>≈</c> when not exact.</summary>
    public string? SnapPointText(Snap3DResult snap)
    {
        var p = ToDocumentPoint(snap);
        string F(long dbu) => LayoutUnits.Format(dbu, Document.DisplayUnit, Document.DbuPerMicron);
        return (p.Exact ? "" : "≈ ") + $"({F(p.Dbu.X)}, {F(p.Dbu.Y)}, {F(p.Dbu.Z)}) {LayoutUnits.Suffix(Document.DisplayUnit)}";
    }

    // R-snpf-4 — what a gesture moves, by NAME, so it survives every regeneration the gesture causes.
    private readonly HashSet<string> _snapExcludedObjects = new(StringComparer.Ordinal);
    private readonly List<(string Object, int Face)> _snapExcludedFaces = [];
    private readonly List<Point3> _snapExcludedPoints = [];

    /// <summary>The pane's exclusion, from the names, for the scene it now shows.</summary>
    private void ApplySnapExclusion()
    {
        if (_snapExcludedObjects.Count == 0 && _snapExcludedFaces.Count == 0 && _snapExcludedPoints.Count == 0)
        {
            Viewer.SnapExclusion = null;
            return;
        }
        var ex = new Snap3DExclusion();
        foreach (string n in _snapExcludedObjects) if (SceneObject(n) is { } o) ex.Objects.Add(o.Id);
        foreach (var (n, f) in _snapExcludedFaces) if (SceneObject(n) is { } o) ex.Faces.Add((o.Id, f));
        ex.Points.AddRange(_snapExcludedPoints);
        Viewer.SnapExclusion = ex;
    }

    private void ClearSnapExclusion()
    {
        _snapExcludedObjects.Clear();
        _snapExcludedFaces.Clear();
        _snapExcludedPoints.Clear();
        Viewer.SnapExclusion = null;
    }

    // ── the display unit (owner decision D4: a preference, not geometry) ────────────────────

    partial void OnDisplayUnitChanged(LayoutUnit value)
    {
        if (_loading) return;
        Document.DisplayUnit = value;
        _preferenceDirty = true;
        OnPropertyChanged(nameof(IsDirty));
        ApplyLengthFormat();
        Properties.Reload();
        ApplyDrawingGrid();
        SyncPlaneTexts();
        RefreshSnapDistance();
        // 3D editor round 5 — a fillet's or chamfer's row spells its size in the display unit (C3dFillets.RowLabel).
        RebuildTree();
    }

    private void ApplyLengthFormat()
    {
        var f = EmLengthFormat.For(Document.DisplayUnit, Document.DbuPerMicron);
        Viewer.SetLengthFormat(m => f(m));
        Viewer.SetMeasureUnits(Document.DisplayUnit, Document.DbuPerMicron);
    }

    /// <summary>A length in DBU, spelled in the display unit with its suffix.</summary>
    public string Length(long dbu)
        => $"{LayoutUnits.Format(dbu, Document.DisplayUnit, Document.DbuPerMicron)} {LayoutUnits.Suffix(Document.DisplayUnit)}";

    // ── save, reload, external change (R-em3d43-1c / -1d) ────────────────────────────────────

    /// <summary>Writes the document — and, pushed in, every other frame with unsaved edits (brief-em3d-48 R-em3d48-4c:
    /// the save the schematic and layout hierarchies have); null on success, else why not.</summary>
    public string? Save()
    {
        if (IsScratch) return "This 3D design has not been saved yet: Save As chooses where it goes.";
        try { C3dPersistence.SaveToFile(FilePath, Document); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        _savedStamp = Stamp(FilePath);
        UndoRedo.MarkSaved();
        _preferenceDirty = false;
        foreach (var f in _frames.Take(Math.Max(0, _frames.Count - 1)).Where(f => f.UndoRedo.IsModified || f.PreferenceDirty))
        {
            try { C3dPersistence.SaveToFile(f.FilePath, f.Document); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{Path.GetFileName(f.FilePath)}: {ex.Message}"; }
            f.SavedStamp = Stamp(f.FilePath);
            f.UndoRedo.MarkSaved();
            f.PreferenceDirty = false;
        }
        // brief-em3d-68 R-em3d68-4d — a copy this session wrote and nothing now names goes with the save.
        RemoveUnnamedStepCopies();
        OnPropertyChanged(nameof(IsDirty));
        return null;
    }

    /// <summary>Writes the document to <paramref name="path"/> and follows it there; null on success.</summary>
    public string? SaveAs(string path)
    {
        if (CanPopOut) return "Pop out to the top document first: Save As writes the tab's own file.";
        string was = FilePath;
        bool wasScratch = IsScratch;
        // brief-em3d-101 R-em3d101-1b — an image's path is stored for THIS file's folder: written again for the new one, so it
        // still resolves (and put back if the save fails).
        var images = C3dImages.AllImages(Document.Objects).Select(img => (Image: img, Old: img.Path)).ToList();
        foreach (var (img, old) in images)
            if (C3dImages.Resolve(was, old) is { } abs) img.Path = C3dImages.Store(path, abs);
        FilePath = Path.GetFullPath(path);
        IsScratch = false;
        if (Save() is { } why)
        {
            FilePath = was; IsScratch = wasScratch;
            foreach (var (img, old) in images) img.Path = old;
            return why;
        }
        RebaseHistoryImages(was, FilePath);
        if (wasScratch) OnPropertyChanged(nameof(IsScratch));
        Viewer.Regenerate();         // the document's own path is where its relative references resolve from
        return null;
    }

    /// <summary>brief-em3d-101 — the image paths the undo history holds, spelled again for the document's new file (as Save As
    /// spelled the document's own), so undoing or redoing past the save never puts back a path written for the old folder. Text
    /// that carries no image is left byte for byte as it was.</summary>
    private void RebaseHistoryImages(string from, string to)
    {
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(from)), Path.GetDirectoryName(to), StringComparison.Ordinal)) return;
        string ObjectText(string text)
        {
            if (!text.Contains("\"Path\"", StringComparison.Ordinal)) return text;
            var o = C3dPersistence.DeserializeObject(text);
            if (!C3dImages.AllImages([o]).Any()) return text;
            C3dImages.Rebase([o], from, to);
            return C3dPersistence.SerializeObject(o);
        }
        string DocumentText(string text)
        {
            if (!text.Contains("\"Path\"", StringComparison.Ordinal)) return text;
            var d = C3dPersistence.Deserialize(text);
            if (!C3dImages.AllImages(d.Objects).Any()) return text;
            C3dImages.Rebase(d.Objects, from, to);
            return C3dPersistence.Serialize(d);
        }
        foreach (var entry in UndoRedo.Entries.OfType<IC3dObjectTextEntry>()) entry.RewriteObjects(ObjectText, DocumentText);
    }

    /// <summary>The file changed on disk. Our own save is ignored; a clean document reloads without asking; a
    /// dirty one raises <see cref="ExternalChangeWhileDirty"/> and the shell asks, as for a layout.</summary>
    public void OnFileChangedOnDisk()
    {
        if (CanPopOut) { Viewer.Invalidate(); return; }       // pushed in: the top file is context, redrawn
        string? stamp = Stamp(FilePath);
        if (stamp is null || stamp == _savedStamp) return;
        if (IsDirty) { ExternalChangeWhileDirty?.Invoke(); return; }
        Reload();
    }

    /// <summary>A placed cell's .c3d or .clay changed: elaborate again. The elaborator's child cache is keyed
    /// by the file's stamp, so only that instance is rebuilt, and nothing is asked.</summary>
    public void OnChildChanged()
    {
        Viewer.Invalidate();
        ScheduleSolveStatus();      // brief-em3d-98 R-em3d98-6 — a placed layout, technology or library saved: re-check
    }

    /// <summary>
    /// Designer round 32 — elaborate again when a file the shown scene was built from (a placed .clay, its technology, a
    /// material library) has changed since. The workspace's file watcher is what normally says so, but a watcher can drop
    /// events (a synced cloud folder, a run writing hundreds of field files under the same tree), and the scene then stayed
    /// on the old layout until the .c3d was closed and opened again. Called when the tab is activated and when the watcher
    /// reports an error; costs one stamp per file read.
    /// </summary>
    public void RecheckInputs()
    {
        if (Elaboration is not { } e || _adoptedReadStamp is not { } then) return;
        if (ReadStamp(e.FilesRead, then.Own) != then.Stamp) OnChildChanged();
    }

    /// <summary>The stamps of the files an elaboration read, the document's own file left out (its changes reach it as
    /// <see cref="OnFileChangedOnDisk"/>, and its text comes from the editor, not the disk).</summary>
    private static string ReadStamp(IReadOnlyList<string> files, string own)
        => string.Join("|", files.Where(f => !string.Equals(f, Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
                                 .Select(f => f + "=" + (Stamp(f) ?? "-")));

    /// <summary>Reads the file again, dropping the history (it described a document that is gone).</summary>
    public string? Reload()
    {
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(FilePath); }
        catch (Exception ex) { return ex.Message; }
        Document = doc;
        _savedStamp = Stamp(FilePath);
        UndoRedo.Reset();
        InitFrames();
        _preferenceDirty = false;
        _loading = true;
        DisplayUnit = doc.DisplayUnit;
        _loading = false;
        ApplyLengthFormat();
        Viewer.SetSelection([]);
        SetTool(null);
        LeaveBooleanFully();                // brief-em3d-66 — the document it was entered in is gone
        ApplySnapGrid();
        SyncPlaneTexts();
        DocumentChanged();
        return null;
    }

    private static string? Stamp(string path)
    {
        try
        {
            var f = new FileInfo(path);
            return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : null;
        }
        catch (Exception) { return null; }
    }

    // ── the tree (R-em3d43-6a) ───────────────────────────────────────────────────────────────

    private bool _syncingTree;

    [ObservableProperty] private C3dTreeItem? _selectedTreeItem;

    private static readonly (Type Type, string Header)[] Groups =
    [
        (typeof(C3dBox), "Boxes"), (typeof(C3dPrism), "Prisms"), (typeof(C3dCylinder), "Cylinders"), (typeof(C3dSphere), "Spheres"),
        // brief-sar-3 — an ellipsoid beside the sphere; a phantom, one row with its layers beneath it.
        (typeof(C3dEllipsoid), "Ellipsoids"), (typeof(C3dPhantom), "Phantoms"),
        // brief-sar-11 — imported anatomy: a voxel body is one row (its tissues are its label table), a mesh one per surface.
        (typeof(C3dVoxelBody), "Voxel bodies"), (typeof(C3dMesh), "Meshes"),
        (typeof(C3dPolyhedron), "Polyhedra"), (typeof(C3dSheet), "Sheets"), (typeof(C3dPolyline), "Polylines"),
        (typeof(C3dWire), "Wires"),
        // brief-em3d-64 — drawn and listed; brief 66 gives an operation its own node with its operands beneath it.
        (typeof(C3dBoolean), "Booleans"), (typeof(C3dFillet), "Fillets"), (typeof(C3dChamfer), "Chamfers"), (typeof(C3dStep), "Step parts"),
    ];

    /// <summary>Objects in construction order, grouped by material or by kind (3D editor round 2) and filtered; then
    /// instances, each expandable to its cell's objects (read-only).</summary>
    private void RebuildTree()
    {
        // 3D editor round 5 — every selected row, in its order, not only SelectedTreeItem.
        var keep = _selectedTreeItems.ToList();
        _syncingTree = true;
        try
        {
            DetachExpansion(Tree);
            Tree.Clear();
            RebuildTreeFilters();
            if (GroupsSection() is { } groups) Tree.Add(groups);
            foreach (var g in IsViewOnly ? ViewObjectGroups() : ObjectGroups()) Tree.Add(g);
            if (InstanceGroup() is { } instances) Tree.Add(instances);
            RebuildInstanceChildren();
            RefreshWireFlags();
            RefreshKernelFlags();
            RebuildRecordsTree();
            List<C3dTreeItem> rows = [.. keep.Select(RebuiltRow).OfType<C3dTreeItem>().Distinct()];
            // A group's row that is gone (ungrouped, or its grouping undone): the rows of what the view has selected.
            if (rows.Count < keep.Count && keep.Any(k => k.IsGroup))
                rows = [.. Viewer.SelectedObjects().Select(RowOf).OfType<C3dTreeItem>().Distinct()];
            SetTreeRows(CollapseToGroups(rows));
        }
        finally { _syncingTree = false; }
    }

    /// <summary>The rebuilt tree's row that stands where <paramref name="old"/> stood, or null.</summary>
    private C3dTreeItem? RebuiltRow(C3dTreeItem old)
        => old.FeaturePath is { } feature ? AllTreeItems().FirstOrDefault(t => t.FeaturePath == feature && t.TopName == old.TopName)
         : old.IsGroup ? AllTreeItems().FirstOrDefault(t => t.IsGroup && t.GroupPath == old.GroupPath)
         : AllTreeItems().FirstOrDefault(t => t.Name == old.Name && !t.IsGroup && t.IsAirBox == old.IsAirBox && t.OperandPath == old.OperandPath &&
                                              (t.Kind == FieldPlotKind) == (old.Kind == FieldPlotKind));

    // 3D editor round 1 — each node's expansion, by key, across rebuilds: written as the user opens and closes nodes,
    // read when a rebuilt node takes its place. A group starts expanded; everything else closed.
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.Ordinal);

    /// <summary>Stops nodes about to be thrown away from writing their expansion: a container torn down with its node
    /// may push "closed" back through the binding, which is not the user closing it.</summary>
    private static void DetachExpansion(IEnumerable<C3dTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            n.Memory = null;
            if (n is C3dTreeGroup g) DetachExpansion(g.Items);
            if (n is C3dTreeItem i) DetachExpansion(i.Children);
        }
    }

    /// <summary>Gives every node of the tree the expansion its namesake had, and lets it record its changes.</summary>
    private void RestoreExpansion()
    {
        foreach (var g in Tree)
        {
            Restore(g, open: true);
            foreach (var i in g.Items) RestoreItem(i);
        }

        // brief-em3d-66 — a boolean's operands are nodes inside its node, at any depth.
        void RestoreItem(C3dTreeItem i)
        {
            Restore(i, open: false);
            foreach (var c in i.Children) RestoreItem(c);
        }

        void Restore(C3dTreeNode n, bool open)
        {
            if (n.Memory is not null) return;
            n.IsExpanded = _expansion.TryGetValue(n.ExpansionKey, out bool o) ? o : open;
            n.Memory = _expansion;
        }
    }

    private IEnumerable<C3dTreeItem> AllTreeItems()
        => Tree.SelectMany(g => g.Items).SelectMany(Walk);

    /// <summary>A node and every node beneath it — a boolean's operands at any depth.</summary>
    private static IEnumerable<C3dTreeItem> Walk(C3dTreeItem i) => i.Children.SelectMany(Walk).Prepend(i);

    /// <summary>Each instance's children, from the elaboration: what the placed cell contributed.</summary>
    private void RebuildInstanceChildren()
    {
        if (Elaboration is not { } e) return;
        // An instance in a group is a row under the group's (C3dGroups), not in the Instances section.
        foreach (var inst in AllTreeItems().Where(t => t.InstanceIndex >= 0 && !t.IsReadOnly).ToList())
        {
            inst.Children.Clear();
            foreach (var (name, p) in e.Provenance.Where(kv => kv.Value.InstancePath == inst.Name ||
                                                               kv.Value.InstancePath.StartsWith(inst.Name + "/", StringComparison.Ordinal) ||
                                                               kv.Value.InstancePath.StartsWith(inst.Name + "[", StringComparison.Ordinal)))
                inst.Children.Add(new C3dTreeItem(this, name, "Part", Path.GetFileName(p.DocumentPath), -1, -1,
                                                  SceneObject(name) is { } s && Viewer.View.IsVisible(s.Id))
                                  { IsReadOnly = true, IsModelled = !CircuitRF.Design.ThreeD.C3dModelled.IsOff(e, name) });
        }
    }

    private void RefreshTreeVisibility()
    {
        foreach (var item in AllTreeItems())
            if (item.IsGroup) item.Sync(GroupVisible(item.GroupPath!));
            else if (item.OperandPath is { } op && item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count)
                item.Sync(C3dBooleans.At(Document.Objects[item.ObjectIndex], op)?.Hidden != true);
            else if (item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count) item.Sync(!Document.Objects[item.ObjectIndex].Hidden);
            else if (SceneObject(item.Name) is { } s) item.Sync(Viewer.View.IsVisible(s.Id));
    }

    /// <summary>A tree checkbox: brief-em3d-90 — the one visibility function (SetRowsVisible), for every kind of row. A group's
    /// tick is its members'; a feature row's switch is Enabled, not visibility.</summary>
    internal void TreeVisibilityChanged(C3dTreeItem item, bool visible)
    {
        if (_syncingTree || item.IsFeature) return;
        SetRowsVisible([item], visible, $"{(visible ? "Show" : "Hide")} {(item.IsGroup ? C3dGroups.NameOf(item.GroupPath!) : item.Name)}");
    }

    /// <summary>
    /// A tree click selects in the scene (Object mode) and the Properties Inspector follows. 3D editor round 1: a node
    /// whose object the scene does not hold — refused by elaboration, say a box with no material yet — is still the
    /// selection: the Inspector shows its document fields, which is where the missing material is given. The air box's
    /// node selects its six faces, and shows them.
    /// </summary>
    partial void OnSelectedTreeItemChanged(C3dTreeItem? value)
    {
        RaiseMenuStateChanged();
        // brief-em3d-96 — a drawn plot selected is the focused one: the Inspector shows it, and it is drawn last.
        if (value is { Kind: FieldPlotKind }) Viewer.FocusPlot(value.Name);
        // brief-dfr31-3 — a plot's plane is drawn while its row is selected, and its armed "Through…" lasts only as long.
        if (_plotThrough is not null && !(value is { Kind: FieldPlotKind } row && row.Name == _plotThrough)) CancelPlotThrough();
        Viewer.RequestFrame();
        if (!_settingTreeRows && !(value is null ? _selectedTreeItems.Count == 0 : _selectedTreeItems is [var only] && only == value))
        {
            _selectedTreeItems = value is null ? [] : [value];
            OnPropertyChanged(nameof(SelectedTreeItems));
        }
        if (_syncingTree || value is null) return;
        // brief-em3d-66 R-em3d66-5a — an operand's row enters its boolean and selects it there, once that scene is up.
        if (value.OperandPath is { } operandPath && value.ObjectIndex >= 0 && value.ObjectIndex < Document.Objects.Count)
        {
            SelectOperand(value.ObjectIndex, operandPath);
            Properties.Reload();
            return;
        }
        // 3D editor bugs round 5 — any other row leaves an entered boolean: the operand row put the editor there with one
        // click, so one click elsewhere takes it out, and the result is drawn again. Its object is selected once that
        // scene is up (this scene holds it as a ghost, or not at all).
        if (_entered is not null)
        {
            LeaveBooleanFully();
            _selectAfterAdopt = value.ObjectIndex >= 0 && value.ObjectIndex < Document.Objects.Count ? [Document.Objects[value.ObjectIndex].Name]
                              : value.InstanceIndex >= 0 ? [value.Name]
                              : value.IsGroup ? [.. C3dGroups.MembersOf(Document, value.GroupPath!).Select(MemberName)] : null;
            Viewer.Regenerate();
        }
        _syncingTree = true;
        try
        {
            List<Scene3DItem> ids = [.. SceneObjectsOfRow(value).Select(o => Scene3DItem.OfObject(o.Id))];
            if (Viewer.SelectMode != Scene3DSelectMode.Object) Viewer.SelectMode = Scene3DSelectMode.Object;
            Viewer.SetSelection(ids);
        }
        finally { _syncingTree = false; }
        // A row the scene holds nothing of (a lumped element) leaves the scene's selection as it was, so nothing else redraws
        // the overlay that shows it selected.
        Viewer.RequestFrame();
        Properties.Reload();
    }

    /// <summary>A click on nothing, or Esc, with nothing selected in the scene: the selected rows — a lumped element's, a
    /// probe's, which stand for nothing there — are let go as any selection is.</summary>
    public void SelectionClearedWhenEmpty()
    {
        if (_syncingTree || _selectedTreeItems.Count == 0 && SelectedTreeItem is null) return;
        _syncingTree = true;
        try { SetTreeRows([]); }
        finally { _syncingTree = false; }
        Viewer.RequestFrame();
        Properties.Reload();
    }

    /// <summary>The scene objects selecting a tree row selects: the air box's faces (Round 3: selecting it never switches it
    /// on — a click on its tick selects the row too, and turned a box the user had just hidden straight back on), an
    /// instance's parts, a document object's — a feature row's too, since it rounds that object — or the named object.</summary>
    private IEnumerable<Scene3DObject> SceneObjectsOfRow(C3dTreeItem row)
        => row.Kind is FieldPlotKind or SymmetryPlaneKind ? []            // brief-em3d-83/90 — records, in no scene (a plane is overlay)
            // brief-em3d-90 R-em3d90-3 — a thermal boundary's row selects its tint (none while it is hidden: the row still selects)
            : row.Kind == ThermalBoundaryKindName ? new[] { SceneObject(Scene3DBuilder.FaceTintPrefix + row.Name) }.OfType<Scene3DObject>()
            : row.IsAirBox ? AirBoxFaceObjects()
            : row.IsGroup ? SceneObjectsOfGroup(row.GroupPath!)
            : row.InstanceIndex >= 0 ? row.Children.Select(c => c.Name).Select(SceneObject).OfType<Scene3DObject>()
            : row.ObjectIndex >= 0 && row.ObjectIndex < Document.Objects.Count ? SceneObjectsFor(Document.Objects[row.ObjectIndex])
            : new[] { SceneObject(row.Name) }.OfType<Scene3DObject>();

    /// <summary>3D editor round 5 — the tree's row still selects exactly the view's selection: a regenerated scene
    /// re-selecting the same objects under new ids (after any edit) leaves the row alone. Without this a fillet's row gave
    /// way to its object's the moment an Inspector edit of it regenerated the scene.</summary>
    /// <para>brief-em3d-90 — and a record row the scene holds nothing of (a symmetry plane, a hidden boundary) stays selected
    /// when the view's selection empties, so its Inspector page stays up to edit it.</para>
    /// <para>brief-em3d-91 — several rows still select it when the view holds exactly what they select together (as
    /// TreeSelectionChanged selected it): a hidden thermal boundary's tint leaves the build, and without this its row — and a
    /// probe's, which the scene never held — gave way to the rows of what the view still had, so the second H found nothing
    /// hidden to show.</para>
    private bool TreeRowStillSelected()
    {
        if (SelectedTreeItem is not { OperandPath: null } row) return false;
        var selected = Viewer.SelectedObjects().Select(o => o.Id).ToHashSet();
        if (_selectedTreeItems.Count > 1)
            return selected.Count > 0 && _selectedTreeItems.SelectMany(SceneObjectsOfSelectedRow).Select(o => o.Id).ToHashSet().SetEquals(selected);
        var ofRow = SceneObjectsOfRow(row).Select(o => o.Id).ToHashSet();
        // A row with nothing of its own in the scene stays selected when the scene selects nothing — a symmetry plane, a thermal
        // boundary, and a face image that is hidden (brief-em3d-101: hiding one from the Inspector kept its section up).
        if (selected.Count == 0) return ofRow.Count == 0 && row.Kind is SymmetryPlaneKind or ThermalBoundaryKindName or FaceImageKind;
        return ofRow.SetEquals(selected);
    }

    private void OnViewerSelectionChanged()
    {
        FilletSelectionChanged();
        OnPropertyChanged(nameof(CanPushIn));
        if (!_syncingTree && !TreeRowStillSelected())
        {
            _syncingTree = true;
            try
            {
                // 3D editor round 5 — every selected object's row is selected in the tree, not only the first's.
                var rows = Viewer.SelectedObjects().Select(o => IsViewOnly ? ViewTreeItemOf(o) : RowOf(o)).OfType<C3dTreeItem>().Distinct().ToList();
                // A group selected whole is its own row (C3dGroups).
                SetTreeRows(CollapseToGroups(rows));
            }
            finally { _syncingTree = false; }
            if (SelectedTreeItem is { } shown) TreeRevealRequested?.Invoke(shown);
        }
        // After the tree: the Inspector falls back to the tree's node when the scene holds nothing selected.
        Properties.Reload();
    }

    /// <summary>The tree row a scene object is listed under, or null.</summary>
    private C3dTreeItem? RowOf(Scene3DObject o)
        => BoxFaceOf(o) is not null ? AllTreeItems().FirstOrDefault(t => t.IsAirBox)
           // brief-em3d-90 R-em3d90-3 — a tint is its boundary's row: a thermal one's is named without the scene's prefix
           : o.Tint ? AllTreeItems().FirstOrDefault(t => t.Kind == ThermalBoundaryKindName && Scene3DBuilder.FaceTintPrefix + t.Name == o.Name)
                      ?? AllTreeItems().FirstOrDefault(t => t.Kind is EmBoundaryKind or FaceImageKind && t.Name == o.Name)
           // brief-em3d-66 — an entered operand is its row under its boolean.
           : OperandIndexOf(o) is >= 0 and var oi && TopOf(oi, out string op) is >= 0 and var top
             ? AllTreeItems().FirstOrDefault(t => t.ObjectIndex == top && t.OperandPath == op)
           : AllTreeItems().FirstOrDefault(t => t.Name == o.Name && !t.IsAirBox && !t.IsGroup)
             // 3D editor round 4 — an element of a wire array (w1[2]) is its wire's row.
             ?? (DocumentIndex(o) is >= 0 and var di ? AllTreeItems().FirstOrDefault(t => t.ObjectIndex == di && !t.IsAirBox && t.OperandPath is null && !t.IsFeature) : null)
             ?? (InstanceOf(o) is { } inst ? AllTreeItems().FirstOrDefault(t => t.InstanceIndex >= 0 && t.Name == inst.Split('/', '[')[0]) : null);

    // ── 3D editor round 5: the tree's multiple selection ─────────────────────────────────────

    private IReadOnlyList<C3dTreeItem> _selectedTreeItems = [];
    private bool _settingTreeRows;

    /// <summary>
    /// 3D editor round 5 — every row the tree has selected, in the order they were selected: a click selects one, Shift a
    /// range, Ctrl/Cmd adds or removes one, exactly as the canvas's Shift-click does. The order is the canvas's selection
    /// order too, so a boolean opened on it takes its Tool and Blank from it (D4). <see cref="SelectedTreeItem"/> is the
    /// first of them. The view mirrors this list; it never binds the tree's own selection.
    /// </summary>
    public IReadOnlyList<C3dTreeItem> SelectedTreeItems => _selectedTreeItems;

    /// <summary>The rows selected, <see cref="SelectedTreeItem"/> the first — set without selecting in the scene.</summary>
    private void SetTreeRows(IReadOnlyList<C3dTreeItem> rows)
    {
        _settingTreeRows = true;
        try { SelectedTreeItem = rows.Count > 0 ? rows[0] : null; }
        finally { _settingTreeRows = false; }
        if (rows.SequenceEqual(_selectedTreeItems)) return;
        _selectedTreeItems = rows;
        OnPropertyChanged(nameof(SelectedTreeItems));
    }

    /// <summary>
    /// The tree's own selection changed, and <paramref name="current"/> is what it now holds. One row selects as a click
    /// on it always has; several select every object they stand for in the scene, in the order the rows were selected —
    /// rows already selected keep their place, new ones follow — and the canvas's menu is then theirs.
    /// <para>The tree's WHOLE selection, not its event's added/removed lists: a plain click clears the TreeView's
    /// selection with a Reset, which names no removed rows, so a delta-kept list grew with every click.</para>
    /// </summary>
    public void TreeSelectionChanged(IReadOnlyCollection<C3dTreeItem> current)
    {
        var rows = _selectedTreeItems.Where(current.Contains).ToList();
        foreach (var a in current) if (!rows.Contains(a)) rows.Add(a);
        if (rows.Count <= 1)
        {
            var one = rows.FirstOrDefault();
            if (ReferenceEquals(one, SelectedTreeItem)) OnSelectedTreeItemChanged(one);   // the same row, re-applied: the scene follows
            else SelectedTreeItem = one;
            return;
        }
        _syncingTree = true;
        try
        {
            SetTreeRows(rows);
            if (Viewer.SelectMode != Scene3DSelectMode.Object) Viewer.SelectMode = Scene3DSelectMode.Object;
            Viewer.SetSelection(rows.SelectMany(SceneObjectsOfSelectedRow).Select(s => Scene3DItem.OfObject(s.Id)));
        }
        finally { _syncingTree = false; }
        Viewer.RequestFrame();
        Properties.Reload();
    }

    /// <summary>What one of several selected rows selects in the view: its objects; an operand's or a feature's row, nothing.</summary>
    private IReadOnlyList<Scene3DObject> SceneObjectsOfSelectedRow(C3dTreeItem r) => r.OperandPath is null && !r.IsFeature ? SceneObjectsOfNode(r) : [];

    /// <summary>3D editor bugs round 5 — a click in the scene selected this node: the view brings it into sight (the read-only
    /// viewer's reveal, which the editor lacked).</summary>
    public event Action<C3dTreeItem>? TreeRevealRequested;

    /// <summary>The document object the tree has selected that the scene does not hold (elaboration refused it), or −1.</summary>
    public int TreeOnlyObjectIndex()
        => SelectedTreeItem is { ObjectIndex: >= 0 and var i } && i < Document.Objects.Count && !SceneObjectsFor(Document.Objects[i]).Any() ? i : -1;

    /// <summary>A camera move changes the drawn grid spacing: the status line follows (cheap arithmetic, no geometry).</summary>
    private void OnViewerFrame()
    {
        if (ShowDrawingGrid) RefreshGridText();
    }

    public void Dispose()
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        Viewer.Dispose();
    }
}

/// <summary>brief-em3d-44 R-em3d44-4 — a snapped point in the document: <paramref name="Dbu"/> (rounded when
/// not <paramref name="Exact"/>) and the metres it came from.</summary>
public readonly record struct C3dSnapPoint(C3dPoint3 Dbu, Point3 Metres, bool Exact);

/// <summary>3D editor round 1 — what every node of the editor's tree has: whether it is expanded. The tree is rebuilt on
/// every document change, so the editor remembers the state across rebuilds (a visibility tick used to collapse the
/// ticked object's group).</summary>
public abstract partial class C3dTreeNode : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The key the editor remembers this node's expansion under.</summary>
    public abstract string ExpansionKey { get; }

    /// <summary>Where a change of <see cref="IsExpanded"/> is recorded; null while the node is not (or no longer) shown.</summary>
    internal Dictionary<string, bool>? Memory { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (Memory is { } m) m[ExpansionKey] = value;
    }
}

/// <summary>A group of the editor's tree. Code finds a group by its <see cref="Role"/>: by material, a header is a
/// material's name, and any name is possible.</summary>
public sealed class C3dTreeGroup(string header, IEnumerable<C3dTreeItem> items, C3dTreeGroupRole role = C3dTreeGroupRole.Objects) : C3dTreeNode
{
    public string Header { get; } = header;
    public C3dTreeGroupRole Role { get; } = role;
    public ObservableCollection<C3dTreeItem> Items { get; } = [.. items];

    /// <summary>brief-em3d-83 — the header's <c>+</c> (the Field Plots group's New Field Plot…), or null for none.</summary>
    public System.Windows.Input.ICommand? AddCommand { get; init; }
    public bool CanAdd => AddCommand is not null;
    public string? AddTip { get; init; }

    /// <summary>The header's tooltip, or null.</summary>
    public string? HeaderTip { get; init; }

    /// <summary>brief-em3d-94 — the material a By-material header names (never <c>No material</c>), whose menu offers Edit
    /// Material…; null for every other group.</summary>
    public string? MaterialName { get; init; }
    public override string ExpansionKey => "group:" + Role + ":" + Header;
}

/// <summary>One node of the editor's tree: a document object, an instance, or (read-only) an instance's part.</summary>
public sealed partial class C3dTreeItem(C3dEditorViewModel owner, string name, string kind, string? detail,
                                        int objectIndex, int instanceIndex, bool visible) : C3dTreeNode
{
    /// <summary>The kind of the air box's node (3D editor round 1): selectable, never deletable.</summary>
    public const string AirBoxKind = "Air box";

    public override string ExpansionKey => "item:" + Kind + ":" + (GroupPath ?? Name) + (OperandPath is { } p ? "@" + TopName + "/" + p : "")
                                           + (FeaturePath is { } fp ? "@" + TopName + "#" + fp : "");

    /// <summary>The active setup's air box.</summary>
    public bool IsAirBox => Kind == AirBoxKind;

    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string? Detail { get; } = detail;
    public int ObjectIndex { get; } = objectIndex;
    public int InstanceIndex { get; } = instanceIndex;

    /// <summary>The tick's tooltip: in a setup's view (3D editor round 5) a tick is the view's alone, never saved.</summary>
    public string VisibleTip => owner.IsViewOnly || IsViewState
        ? "Visible — in this view only; nothing is saved"
        : "Visible — a drawn object's visibility is saved with the document and undoable";

    /// <summary>brief-em3d-90 — a row whose tick is the view's alone (a thermal place, a thermal boundary, a symmetry plane, an
    /// instance's part, an EM face boundary's tint): nothing is saved, and nothing is undoable.</summary>
    private bool IsViewState => ObjectIndex < 0 && !IsAirBox && !IsGroup && Kind != C3dEditorViewModel.FieldPlotKind && Kind != C3dEditorViewModel.FaceImageKind;

    /// <summary>brief-em3d-101 Phase B — a face image's row: the object it is on (document index) and the face; −1 and null otherwise.</summary>
    public int FaceImageHost { get; init; } = -1;
    public string? FaceImageFace { get; init; }

    /// <summary>What the row reads as when that is not its <see cref="Name"/> (a face image's row: "Image on zmax").</summary>
    public string? DisplayName { get; init; }
    public string Label => DisplayName ?? Name;

    /// <summary>brief-em3d-90 — the name's tooltip for a record row (a symmetry plane says how it is selected), or null.</summary>
    public string? RowTip { get; init; }

    /// <summary>brief-em3d-46 R-em3d46-4d — a document object's place in construction order (1-based), which decides
    /// which solid wins an overlap; null for an instance and its parts. 3D editor round 3: the row's tooltip, no longer a
    /// "#n" in front of every name — the tree lists in construction order within a group anyway.</summary>
    public string? OrderTip => !IsModelled ? CircuitRF.Design.ThreeD.C3dModelled.Tip : RowTip ?? (ObjectIndex >= 0 && OperandPath is null && FeaturePath is null
        ? $"Construction order {ObjectIndex + 1}: a later object wins where solids overlap (3D ▸ Modify ▸ Order)"
        : null);

    /// <summary>brief-em3d-66 R-em3d66-3c — an operand's path under its top-level object (<see cref="ObjectIndex"/>):
    /// <c>Blank.</c>, <c>Tools[1].</c>, <c>Tools[0].Blank.</c>; null for a top-level row.</summary>
    public string? OperandPath { get; init; }

    /// <summary>brief-em3d-67 R-em3d67-6a — a fillet's or chamfer's row: the feature's path under its top-level object
    /// (<see cref="ObjectIndex"/>) — "" for the outermost, "Target." for the one inside it; null for any other row.</summary>
    public string? FeaturePath { get; init; }

    /// <summary>A feature row has no visibility of its own: its switch is Enabled, in the inspector.</summary>
    public bool IsFeature => FeaturePath is not null;

    /// <summary>A group's row (C3dGroups): the group's path; null for any other row.</summary>
    public string? GroupPath { get; init; }

    /// <summary>The row of a group of the document's objects — not a section of the tree (<see cref="C3dTreeGroup"/>).</summary>
    public bool IsGroup => GroupPath is not null;

    /// <summary>The top-level object's name, for an operand's row: what its expansion is remembered under.</summary>
    public string TopName { get; init; } = "";

    /// <summary>brief-em3d-66 R-em3d66-3a — an operation's icon (the toolbar's), or null.</summary>
    public Material.Icons.MaterialIconKind? Icon { get; init; }
    public bool HasIcon => Icon is not null;
    public Material.Icons.MaterialIconKind IconKind => Icon ?? default;

    /// <summary>A disabled operation's icon is dimmed (R-em3d66-4a).</summary>
    public double IconOpacity { get; init; } = 1;

    /// <summary>brief-em3d-93 R-em3d93-3 — false for a row that is not modelled (an object, an instance, a part, a port), and for a
    /// group whose every member is not: its name is greyed and its tooltip says why.</summary>
    public bool IsModelled { get; init; } = true;

    /// <summary>The name's opacity: greyed, as the row's detail is, when <see cref="IsModelled"/> is false.</summary>
    public double NameOpacity => IsModelled ? 1 : 0.45;
    public bool IsReadOnly { get; init; }
    public ObservableCollection<C3dTreeItem> Children { get; } = [];

    [ObservableProperty] private bool _isVisible = visible;

    /// <summary>brief-em3d-50 R-em3d50-4 — why a drawn wire does not elaborate (an end on no pad), or null: the tree flags it.</summary>
    [ObservableProperty] private string? _refusal;

    partial void OnIsVisibleChanged(bool value) => owner.TreeVisibilityChanged(this, value);

    /// <summary>Set without calling back — the document or the view changed it.</summary>
    internal void Sync(bool visible)
    {
#pragma warning disable MVVMTK0034
        if (_isVisible == visible) return;
        _isVisible = visible;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsVisible));
    }
}
