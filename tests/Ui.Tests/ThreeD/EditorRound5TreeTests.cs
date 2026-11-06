// ================================================================
//  EditorRound5TreeTests.cs — the 3D editor's fifth round of owner feedback, the object tree: the air box's hidden state is
//  saved with the document; the header's Show all / Hide all; several rows selected at once (in the order they were
//  selected, which is the canvas's selection and a boolean's Tool/Blank order), with the canvas's own menu; and a canvas
//  multi-selection highlights every row it stands for. Pixels were not seen: these read the view model.
// ================================================================

using Avalonia.Controls;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound5TreeTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r5tree-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public EditorRound5TreeTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ADoubleClickedRow_BringsPropertiesForward_OnlyWhenNothingIsBeneathIt()
    {
        var vm = Open(Doc(), out _);
        int requests = 0;
        vm.PropertiesRequested += _ => requests++;
        var row = Row(vm, "a");

        Assert.True(vm.ShowPropertiesOfRow(row));
        Assert.Equal(1, requests);

        row.Children.Add(Row(vm, "b"));                                    // now it opens: the double-click is its expander's
        Assert.False(vm.ShowPropertiesOfRow(row));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void TheAirBoxsTick_IsSavedWithTheDocument_AndUndoable()
    {
        var vm = Open(Doc(), out string path);
        Row(vm, C3dEditorViewModel.AirBoxName).IsVisible = false;
        Assert.True(vm.Document.AirBoxHidden);
        Assert.Null(vm.Save());

        var reopened = Reopen(path);
        Assert.False(reopened.AirBoxShown);
        Assert.False(Row(reopened, C3dEditorViewModel.AirBoxName).IsVisible);
        Assert.False(reopened.Viewer.ShowBoundaryFaces);

        vm.UndoRedo.Undo();                                                // the hide was one undo entry
        Assert.True(vm.AirBoxShown);
        Assert.True(Row(vm, C3dEditorViewModel.AirBoxName).IsVisible);
    }

    [Fact]
    public void HideAll_HidesEveryListedRow_AirBoxToo_AsOneUndoEntry_AndShowAllBringsThemBack()
    {
        var vm = Open(Doc(), out _);
        int entries = vm.UndoEntries;
        vm.HideAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.False(vm.AirBoxShown);
        Assert.All(vm.Tree.SelectMany(g => g.Items), i => Assert.False(i.IsVisible));
        Assert.Equal(entries + 1, vm.UndoEntries);

        vm.ShowAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
        Assert.True(vm.AirBoxShown);

        vm.UndoRedo.Undo();                                                // Show all undone: all hidden again, box too
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.False(vm.AirBoxShown);
    }

    [Fact]
    public void AVisibilityTick_RequestsNoScene_AndThePaneFollowsTheDocument()
    {
        // Hidden is drawing state: a hidden object is in the scene already, so a tick (and its undo) sets the pane's flags and
        // re-elaborates nothing. Through the ordinary edit path, a 1,500-object flattened board waited up to 0.7 s per tick.
        var vm = Open(Doc(), out _);
        long requested = vm.Viewer.Source.Requested;
        bool Drawn(string name) => vm.Viewer.View.IsVisible(vm.SceneObject(name)!.Id);

        vm.HideAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.Objects, o => Assert.False(Drawn(o.Name)));
        Assert.False(vm.Viewer.ShowBoundaryFaces);
        Row(vm, "b").IsVisible = true;
        Assert.True(Drawn("b"));
        Assert.False(Drawn("a"));

        vm.UndoRedo.Undo();                                                // b's tick
        Assert.False(Drawn("b"));
        Assert.False(Row(vm, "b").IsVisible);
        vm.UndoRedo.Undo();                                                // Hide all, box too
        Assert.All(vm.Document.Objects, o => Assert.True(Drawn(o.Name)));
        Assert.True(vm.Viewer.ShowBoundaryFaces);
        Assert.Equal(requested, vm.Viewer.Source.Requested);
    }

    [Fact]
    public void SeveralRows_SelectEveryObjectInTheOrderSelected_AndTheMenuIsTheCanvassWithItsBoolean()
    {
        var vm = Open(Doc(), out _);
        vm.TreeSelectionChanged([Row(vm, "b")]);
        vm.TreeSelectionChanged([Row(vm, "a"), Row(vm, "b")]);            // Shift/Ctrl/Cmd-click adds a second row

        Assert.Equal(["b", "a"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.Equal(["b", "a"], vm.Viewer.SelectedObjects().Select(o => o.Name));   // first-selected is the Tool (D4)
        Assert.Contains(vm.Viewer.ContextMenuItems(), m => m.Header == "Boolean");

        vm.TreeSelectionChanged([Row(vm, "a")]);                          // and removes one again
        Assert.Equal(["a"], vm.Viewer.SelectedObjects().Select(o => o.Name));
        Assert.Equal("a", vm.SelectedTreeItem?.Name);
    }

    /// <summary>A plain click replaces the selection. The TreeView does it as Clear-then-Add, and the Clear's Reset reports
    /// no removed rows — so the handler must hand over the tree's whole selection, never the event's deltas.</summary>
    [Fact]
    public void APlainClick_ReplacesTheSelection_ThoughTheTreesClearNamesNoRemovedRows()
    {
        var vm = Open(Doc(), out _);
        var tree = new TreeView { SelectionMode = SelectionMode.Multiple, ItemsSource = vm.Tree.SelectMany(g => g.Items).ToList() };
        List<(int Removed, int Added)> events = [];
        tree.SelectionChanged += (_, e) =>
        {
            events.Add((e.RemovedItems.Count, e.AddedItems.Count));
            vm.TreeSelectionChanged([.. tree.SelectedItems.OfType<C3dTreeItem>()]);
        };
        tree.SelectedItems.Add(Row(vm, "a"));
        tree.SelectedItems.Add(Row(vm, "b"));
        Assert.Equal(["a", "b"], vm.SelectedTreeItems.Select(r => r.Name));

        events.Clear();
        tree.SelectedItems.Clear();                                        // TreeView.SelectSingleItem, as a plain click runs it
        tree.SelectedItems.Add(Row(vm, "c"));
        Assert.All(events, e => Assert.Equal(0, e.Removed));              // the Reset: no row ever said to have been removed
        Assert.Equal(["c"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.Equal(["c"], vm.Viewer.SelectedObjects().Select(o => o.Name));
    }

    [Fact]
    public void ACanvasMultiSelection_SelectsEveryRow_AndARebuildKeepsThem()
    {
        var vm = Open(Doc(), out _);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id), Scene3DItem.OfObject(vm.SceneObject("c")!.Id)]);
        Assert.Equal(["a", "c"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.Equal("a", vm.SelectedTreeItem?.Name);

        vm.TreeGrouping = C3dTreeGrouping.Primitive;                      // a rebuild of the tree
        Assert.Equal(["a", "c"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.All(vm.SelectedTreeItems, r => Assert.Contains(r, vm.Tree.SelectMany(g => g.Items)));
    }

    /// <summary>A section header's one switch: ticked while every row under it is shown, the dash while some are, clear while
    /// none are; a click on a mixed section shows all of it first, a click on a shown one hides it, one undo entry each, and
    /// no other section is touched.</summary>
    [Fact]
    public void ASectionsSwitch_ShowsOrHidesThatSectionOnly_AndReadsMixedInBetween()
    {
        var vm = Open(Doc(), out _);
        C3dTreeGroup Copper() => vm.Tree.Single(g => g.Items.Any(i => i.Name == "a"));
        Assert.True(Copper().HasVisibilitySwitch);
        Assert.True(Copper().AllVisible);

        Row(vm, "b").IsVisible = false;
        Assert.Null(Copper().AllVisible);

        int entries = vm.UndoEntries;
        Copper().AllVisible = false;                                       // whatever the box toggled to: a mixed section is shown
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
        Assert.True(Copper().AllVisible);

        Copper().AllVisible = false;
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.False(Copper().AllVisible);
        Assert.True(vm.AirBoxShown);                                       // another section's row is left as it was
        Assert.Equal(entries + 2, vm.UndoEntries);
    }

    /// <summary>A drawn sheet made a lumped port: the sheet itself is not one of the conductors the port meets (it was refused as
    /// touching itself), the port takes the sheet's rectangle exactly and the sheet and its material go — one undo entry, and
    /// undo brings the sheet back. A sheet selected in Object mode offers the same Make Port.</summary>
    [Fact]
    public void MakingASheetALumpedPort_ReplacesTheSheet_WithItsOwnRectangle_AsOneUndoEntry()
    {
        C3dBox Slab(string name, long z) => new() { Name = name, Material = "Copper", Min = new C3dPoint3(0, 0, z * Um), Size = new C3dPoint3(200 * Um, 200 * Um, 100 * Um) };
        var gap = new C3dSheet
        {
            Name = "gap", Material = "Copper", Plane = C3dPlane.XZ, Offset = 100 * Um,
            Rect = new C3dRect { Min = new C3dPoint2(50 * Um, 100 * Um), Size = new C3dPoint2(100 * Um, 100 * Um) },
        };
        var vm = Open(new C3dDocument { Objects = [Slab("low", 0), Slab("high", 200), gap] }, out _);
        var sheet = vm.Viewer.Scene.Objects.Single(o => o.Name == "gap");

        vm.Viewer.SelectMode = CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([new CircuitRF.Render.Scene3D.Edit.Scene3DItem(sheet.Id, -1, default)]);
        var lumped = vm.DrawMenuItems().Single(i => i.Header == "Make Port").Children!.Single(i => i.Header == "Lumped");
        Assert.True(lumped.Enabled, lumped.Tip);
        Assert.Contains("replaces 'gap'", lumped.Tip);

        int entries = vm.UndoEntries;
        Assert.Null(vm.MakePortFromFace(sheet.Id, 0, CircuitRF.Engine.Em3d.Em3dPortKind.Lumped));
        Assert.DoesNotContain(vm.Document.Objects, o => o.Name == "gap");
        var port = Assert.Single(vm.Document.Ports);
        Assert.Equal((C3dPlane.XZ, 100 * Um), (port.Plane, port.Offset));
        Assert.Equal((50 * Um, 100 * Um, 100 * Um, 100 * Um), (port.Rect.Min.U, port.Rect.Min.V, port.Rect.Size.U, port.Rect.Size.V));
        Assert.Equal(entries + 1, vm.UndoEntries);

        vm.UndoRedo.Undo();
        Assert.Contains(vm.Document.Objects, o => o.Name == "gap");
        Assert.Empty(vm.Document.Ports);
    }

    /// <summary>A drawn polygon sheet that is a rectangle at 30°, its corners on the grid, across the gap between two pads turned
    /// alike: Make Port ▸ Lumped makes a port turned 30° that resolves between the pads. A sheet whose width changes along it is
    /// refused, saying so.</summary>
    [Fact]
    public void MakingATurnedSheetALumpedPort_TurnsThePort_AndATaperIsRefused()
    {
        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
        List<C3dPoint2> Turned(double cx, double cy, params (double A, double B)[] q)
            => [.. q.Select(p => new C3dPoint2((long)Math.Round((cx + p.A * c - p.B * s) * Um), (long)Math.Round((cy + p.A * s + p.B * c) * Um)))];
        (double, double)[] Box(double a, double b) => [(-a, -b), (a, -b), (a, b), (-a, b)];
        C3dPrism Pad(string name, double along) => new()
        {
            Name = name, Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Height = 35 * Um,
            Outline = Turned(1500 + along * c, 1500 + along * s, Box(400, 500)),
        };
        C3dSheet Sheet(string name, double cy, params (double, double)[] q)
            => new() { Name = name, Material = "Copper", Plane = C3dPlane.XY, Offset = 17500, Outline = Turned(1500, cy, q) };
        var vm = Open(new C3dDocument { Objects = [Pad("padA", -600), Pad("padB", 600), Sheet("gap", 1500, Box(200, 400)),
                                                   Sheet("taper", 4500, (-200, -400), (200, -300), (200, 300), (-200, 400))] }, out string path);

        Assert.Null(vm.MakePortFromFace(vm.Viewer.Scene.Objects.Single(o => o.Name == "gap").Id, 0, CircuitRF.Engine.Em3d.Em3dPortKind.Lumped));
        var port = Assert.Single(vm.Document.Ports);
        Assert.Equal(30, port.Angle, 4);
        Assert.InRange(port.Rect.Size.U, 400 * Um - 2, 400 * Um + 2);
        Assert.InRange(port.Rect.Size.V, 800 * Um - 2, 800 * Um + 2);
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var e = new C3dElaborator().Elaborate(vm.Document, path, cws);
        var resolved = C3dPorts.Resolve(port, vm.Document.DbuPerMicron, C3dProblemAssembly.PortContext(null, vm.Document, e, null));
        Assert.True(resolved.Resolved is not null, resolved.Refusal);

        string? taper = vm.MakePortFromFace(vm.Viewer.Scene.Objects.Single(o => o.Name == "taper").Id, 0, CircuitRF.Engine.Em3d.Em3dPortKind.Lumped);
        Assert.Contains("width runs from", taper);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dTreeItem Row(C3dEditorViewModel vm, string name) => vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == name);

    /// <summary>Fit frames what is shown: the air box while it is drawn; with it and the far box hidden, the two boxes left.</summary>
    [Fact]
    public void Fit_FramesOnlyTheVisibleObjects_TheAirBoxIncludedWhileShown()
    {
        var vm = Open(Doc(), out _);
        vm.Viewer.Resized(400, 300);
        Assert.True(vm.AirBoxShown);
        vm.Viewer.FitCommand.Execute(null);
        float withBox = vm.Viewer.View.Camera.Distance;
        vm.AirBoxShown = false;
        Settle(vm);
        vm.Viewer.FitCommand.Execute(null);
        float withoutBox = vm.Viewer.View.Camera.Distance;
        Assert.True(withoutBox < withBox, $"{withoutBox} vs {withBox}");  // the padded box is larger than the content

        vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "c").IsVisible = false;
        Settle(vm);
        vm.Viewer.FitCommand.Execute(null);
        var target = vm.Viewer.Scene.ToWorld(vm.Viewer.View.Camera.Target);
        Assert.Equal(75e-6, target.X, 1e-7);                                 // a (0..100 µm) and b (50..150 µm)
        // a and b are 150 µm across against the 500 µm row: under a third of the width, ~0.55 of the height that binds in iso
        Assert.True(vm.Viewer.View.Camera.Distance < withoutBox / 1.5);
    }

    /// <summary>General designer feedback round 14 — View ▸ Zoom to Fit (and Ctrl/Cmd+0, which the window binds to it) on an
    /// active 3D view is that view's Fit; it answered "no document is focused".</summary>
    [Fact]
    public void ViewZoomToFit_OnAnActive3DView_FitsIt()
    {
        var vm = Open(Doc(), out _);
        var doc = new C3dEditorDocument(vm);
        var ws = new CircuitRF.Ui.ViewModels.WorkspaceViewModel();
        var dock = ws.Factory.DocumentDock!;
        dock.VisibleDockables!.Add(doc);
        dock.ActiveDockable = doc;                                         // the document the shell's commands act on
        int fits = vm.Viewer.Fits;
        ws.ZoomToFitCommand.Execute(null);
        Assert.Equal(fits + 1, vm.Viewer.Fits);
        Invoke(ws, "ClosedC3dEditor", doc);
        _open.Remove(vm);
    }

    /// <summary>The Analyses panel shows the active .c3d's EM setups — not the last schematic's analyses — and lets go of them
    /// when the .c3d closes.</summary>
    [Fact]
    public void TheAnalysesPanel_ShowsTheActiveC3dsSetups_AndLetsGoWhenItCloses()
    {
        var vm = Open(Doc(), out _);
        var doc = new C3dEditorDocument(vm);
        var ws = new CircuitRF.Ui.ViewModels.WorkspaceViewModel();
        var tool = ws.Factory.AnalysesTool!;
        Assert.True(tool.ShowsSchematic);

        Invoke(ws, "ActivateDocument", doc, false);
        Assert.Same(vm, tool.C3dEditor);
        Assert.False(tool.ShowsSchematic);

        Invoke(ws, "ClosedC3dEditor", doc);
        Assert.Null(tool.C3dEditor);
        Assert.True(tool.ShowsSchematic);
        _open.Remove(vm);                                                  // ClosedC3dEditor disposed it
    }

    private static void Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                 .Invoke(target, args);

    private static C3dDocument Doc()
    {
        C3dBox Box(string name, long x) => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };
        return new C3dDocument { Objects = [Box("a", 0), Box("b", 50), Box("c", 400)] };
    }

    private C3dEditorViewModel Open(C3dDocument doc, out string path)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return Reopen(path);
    }

    private C3dEditorViewModel Reopen(string path)
    {
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
