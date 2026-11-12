// 3D editor bugs round 5 — ONE 3D VIEW. A .cem's Show 3D View is the 3D editor's own view, with nothing to edit.
//
// The read-only viewer and the editor had drifted: two toolbars, two trees, two status panes, and every improvement
// to the editor's chrome had to be made twice or was not made to the viewer at all. The viewer's VIEW MODEL was never
// the problem — the editor already wraps one (Viewer3DViewModel, R-em3d43-1a). So a setup's view is now this class
// built around the setup's own Viewer3DViewModel, and C3dEditorView draws it.
//
// NOTHING HERE CAN EDIT, BY CONSTRUCTION RATHER THAN BY GUARD:
//   * the pane's edit host stays null, so no tool arms, no gizmo shows, no key deletes and no drop places anything —
//     the viewer's own rule since brief 43;
//   * the document is an EMPTY C3dDocument that is never saved: every document path (undo, Hidden, the records tree)
//     finds nothing to act on;
//   * the dock document is still Viewer3DDocument — not IUndoableDocument, no Save route, never dirty — so the shell's
//     Undo, Save, Simulate and 3D ▸ Draw/Modify never resolve to it.
// The editing chrome is hidden (IsEditable). What the scene draws comes from the setup's generator, exactly as before
// (Snapshot3DInputs → Viewer3DViewModel.Build); the tree lists the SCENE's objects, and a tick is a view-held
// visibility, as the viewer's always was.

using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>3D editor bugs round 5 — this is a setup's 3D view (Show 3D View): the editor's view with nothing to edit.</summary>
    public bool IsViewOnly { get; }

    /// <summary>The editing chrome is shown: the drawing tools, the setups, Simulate, the unit and the snap step.</summary>
    public bool IsEditable => !IsViewOnly;

    /// <summary>The status pane under the viewport: kept for debugging in the editor (<see cref="ShowStatusPane"/>), and
    /// always shown for a setup's view, whose refusals and notes ("This setup would not run: …") are the point.</summary>
    public bool ShowsStatusPane => ShowStatusPane || IsViewOnly;

    /// <summary>
    /// 3D editor bugs round 5 — the editor's view of a scene it does not own: a .cem's Show 3D View. <paramref name="viewer"/>
    /// builds its own scene (the setup through its generator) and keeps no edit host; this class supplies the tree and
    /// the chrome. Nothing is written, ever.
    /// </summary>
    public C3dEditorViewModel(Viewer3DViewModel viewer)
    {
        IsViewOnly = true;
        FilePath = Path.GetFullPath(viewer.CemPath);
        Source = new C3dFileSource(FilePath);
        Document = new C3dDocument();
        _workspaceCws = () => null;
        _post = a => a();
        _elaborator = new C3dElaborator();
        Viewer = viewer;
        Viewer.SceneAdopted += OnViewSceneAdopted;
        Viewer.SelectionChanged += OnViewerSelectionChanged;
        Viewer.VisibilityChanged += OnViewVisibilityChanged;
        WatchFieldPlots();
        Properties = new C3dPropertiesViewModel(this);
        ResolveDocument();
        InitFrames();
        RebuildTree();
    }

    /// <summary>Tree row → scene object id, and back, for the scene the tree was last built from.</summary>
    private readonly Dictionary<uint, C3dTreeItem> _viewItems = [];
    private readonly Dictionary<C3dTreeItem, uint> _viewIds = [];

    private void OnViewSceneAdopted()
    {
        RebuildTree();
        ApplyVisiblePlots();                       // brief-em3d-83 — the session's plot, on this scene
        Interlocked.Exchange(ref _adoptedGeneration, Viewer.Scene.Generation);
    }

    /// <summary>A visibility the view changed (a toolbar kind switch, Hide, Isolate, Show All): the row's tick follows.</summary>
    private void OnViewVisibilityChanged(uint id, bool visible)
    {
        if (_viewItems.TryGetValue(id, out var item)) item.Sync(visible);
    }

    /// <summary>A row's tick: the view's visibility of its object, never the setup's.</summary>
    private void ViewTreeVisibilityChanged(C3dTreeItem item, bool visible)
    {
        if (_viewIds.TryGetValue(item, out uint id)) Viewer.SetVisibleEverywhere(id, visible);
    }

    /// <summary>The row a scene object is listed under, or null (filtered out).</summary>
    private C3dTreeItem? ViewTreeItemOf(Scene3DObject o) => _viewItems.GetValueOrDefault(o.Id);

    /// <summary>Kinds that are a setup's records — the ports and the air box — and are never filtered, as in the editor.</summary>
    private static bool IsViewRecord(Scene3DObject o) => o.Kind is Scene3DKind.Port or Scene3DKind.Boundary;

    private static string ViewTypeHeaderOf(Scene3DObject o)
        => Viewer3DViewModel.TreeGroups.FirstOrDefault(g => g.Kinds.Contains(o.Kind)).Header ?? o.Kind.ToString();

    private static string ViewMaterialHeaderOf(Scene3DObject o) => o.Material is { Length: > 0 } m ? m : NoMaterialHeader;

    private bool PassesViewTreeFilter(Scene3DObject o)
        => IsViewRecord(o) || (!_hiddenTypes.Contains(ViewTypeHeaderOf(o)) && !_hiddenMaterials.Contains(ViewMaterialHeaderOf(o)));

    /// <summary>The scene's objects, grouped as the editor groups its document's — by material or by type — and filtered.</summary>
    private IEnumerable<C3dTreeGroup> ViewObjectGroups()
    {
        _viewItems.Clear();
        _viewIds.Clear();
        var scene = Viewer.Scene;
        var listed = scene.Objects.Where(o => Viewer3DViewModel.TreeGroups.Any(g => g.Kinds.Contains(o.Kind)) && PassesViewTreeFilter(o)).ToList();
        bool byMaterial = TreeGrouping == C3dTreeGrouping.Material;
        C3dTreeItem Item(Scene3DObject o, bool detail)
        {
            var item = new C3dTreeItem(this, o.Name, o.Kind.ToString(), detail ? o.Material : null, -1, -1, Viewer.View.IsVisible(o.Id)) { IsReadOnly = true };
            _viewItems[o.Id] = item;
            _viewIds[item] = o.Id;
            return item;
        }

        if (!byMaterial)
        {
            foreach (var (header, kinds) in Viewer3DViewModel.TreeGroups)
            {
                var items = listed.Where(o => kinds.Contains(o.Kind)).Select(o => Item(o, detail: true)).ToList();
                if (items.Count > 0) yield return new C3dTreeGroup(header, items);
            }
            yield break;
        }

        var solids = listed.Where(o => !IsViewRecord(o)).ToList();
        var bare = solids.Where(o => ViewMaterialHeaderOf(o) == NoMaterialHeader).Select(o => Item(o, detail: false)).ToList();
        if (bare.Count > 0) yield return new C3dTreeGroup(NoMaterialHeader, bare);
        foreach (var g in solids.Where(o => ViewMaterialHeaderOf(o) != NoMaterialHeader)
                                .GroupBy(o => o.Material!, StringComparer.Ordinal)
                                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            yield return new C3dTreeGroup(g.Key, [.. g.Select(o => Item(o, detail: false))]);
        foreach (var (header, kinds) in Viewer3DViewModel.TreeGroups.Where(g => g.Kinds.Any(k => k is Scene3DKind.Port or Scene3DKind.Boundary)))
        {
            var items = listed.Where(o => kinds.Contains(o.Kind)).Select(o => Item(o, detail: false)).ToList();
            if (items.Count > 0) yield return new C3dTreeGroup(header, items);
        }
    }

    /// <summary>The filter's rows for the scene: the types and materials it holds (records are never filtered).</summary>
    private (List<string> Types, List<string> Materials) ViewFilterNames()
    {
        var solids = Viewer.Scene.Objects.Where(o => !IsViewRecord(o) && Viewer3DViewModel.TreeGroups.Any(g => g.Kinds.Contains(o.Kind))).ToList();
        var types = Viewer3DViewModel.TreeGroups.Select(g => g.Header).Where(h => solids.Any(o => ViewTypeHeaderOf(o) == h)).ToList();
        var materials = solids.Select(ViewMaterialHeaderOf).Distinct(StringComparer.Ordinal)
                              .OrderBy(m => m == NoMaterialHeader ? 0 : 1).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        return (types, materials);
    }

    /// <summary>The tree's menu in a setup's view: what the view itself can do — hide, isolate, show — and nothing else.</summary>
    private IReadOnlyList<Viewer3DMenuItem> ViewTreeMenuItems(C3dTreeItem item)
    {
        var items = new List<Viewer3DMenuItem> { new(item.Name, Enabled: false), Viewer3DMenuItem.Separator };
        IReadOnlyList<Scene3DObject> scene = _viewIds.TryGetValue(item, out uint id) && Viewer.Scene.Object(id) is { } o ? [o] : [];
        if (scene.Count > 0)
        {
            items.Add(new Viewer3DMenuItem(item.IsVisible ? "Hide" : "Show", () => Viewer.SetVisibleEverywhere(id, !item.IsVisible)));
            items.Add(new Viewer3DMenuItem("Isolate", () => Viewer.Isolate(scene)));
        }
        items.Add(new Viewer3DMenuItem("Show All", Viewer.ShowAll));
        return items;
    }
}
