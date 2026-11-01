// ================================================================
//  EditorRound1TreeTests.cs — the 3D editor's first round of owner feedback, the tree and Properties half: a visibility
//  tick keeps the tree's expansion; a node whose object elaboration refused (a box with no material) is still selectable
//  and its fields are in Properties, where giving it a material draws it; the tree's menu deletes it; the air box is a
//  node under Boxes that cannot be deleted, whose padding is a share of the content's extent; the Properties Inspector
//  hosts a 3D view's selection. Pixels were not seen: these read the view models and the document.
// ================================================================

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound1TreeTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r1tree-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public EditorRound1TreeTests()
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
    public void AVisibilityTick_LeavesEveryGroupExpandedAsTheUserLeftIt()
    {
        var vm = Open(Doc());
        vm.TreeGrouping = C3dTreeGrouping.Primitive;                     // round 2: by material is the default
        var sheets = vm.Tree.Single(g => g.Header == "Sheets");
        Assert.True(sheets.IsExpanded);                                  // a group starts open
        vm.Tree.Single(g => g.Header == "Boxes").IsExpanded = false;

        sheets.Items.Single(i => i.Name == "trace").IsVisible = false;   // a document edit: the tree is rebuilt
        Settle(vm);

        Assert.True(vm.Document.Objects.Single(o => o.Name == "trace").Hidden);
        Assert.True(vm.Tree.Single(g => g.Header == "Sheets").IsExpanded);
        Assert.False(vm.Tree.Single(g => g.Header == "Boxes").IsExpanded);
    }

    [Fact]
    public void AnObjectWithNoMaterial_IsSelectableFromTheTree_AndPropertiesSaysTheSolverIgnoresIt()
    {
        var doc = Doc();
        doc.Objects.Add(new C3dBox { Name = "bare", Min = new C3dPoint3(0, 0, 200 * Um), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) });
        var vm = Open(doc);
        var drawn = Assert.IsType<CircuitRF.Render.Scene3D.Scene3DObject>(vm.SceneObject("bare"));   // drawn as a wireframe

        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "bare");
        Assert.Equal([drawn.Id], vm.Viewer.Selection.Select(s => s.Object));   // the node is the scene's selection
        Assert.True(vm.Properties.IsEditable);
        Assert.Equal("bare", vm.Properties.NameText);
        Assert.Contains(vm.Properties.Rows, r => r.Label == "Not simulated" && r.Value == C3dElaborator.NoMaterialWarning("bare"));

        vm.Properties.Material = "Fill";
        Settle(vm);
        Assert.DoesNotContain(vm.Properties.Rows, r => r.Label == "Not simulated");

        // The tree's menu deletes a node, drawn or not, as one undo entry.
        var menu = vm.TreeMenuItems(vm.SelectedTreeItem!);
        menu.Single(m => m.Header == "Delete").Run!();
        Assert.DoesNotContain(vm.Document.Objects, o => o.Name == "bare");
        vm.UndoRedo.Undo();
        Assert.Contains(vm.Document.Objects, o => o.Name == "bare");
    }

    [Fact]
    public void TheAirBox_IsANodeUnderBoxes_NotDeletable_ItsPaddingAShareOfTheExtent()
    {
        var vm = Open(Doc(new EmSetup { Name = "S1", Solver3D = Em3dSolverId.Palace }));
        vm.TreeGrouping = C3dTreeGrouping.Primitive;                     // round 3: by material it is under "Air"
        var box = vm.Tree.Single(g => g.Header == "Boxes").Items[0];
        Assert.True(box.IsAirBox);
        var menu = vm.TreeMenuItems(box);
        Assert.False(menu.Single(m => m.Header == "Delete").Enabled);
        Assert.False(menu.Single(m => m.Header == "Duplicate").Enabled);

        vm.SelectedTreeItem = box;
        Assert.True(vm.Properties.IsAirBox);
        Assert.Equal(6, vm.Properties.AirBoxFaces.Count);

        // Nothing stated: the box shows the default padding as a share of the 1000 µm x-extent, and focus passing through
        // it unchanged states nothing.
        int entries = vm.UndoEntries;
        double defaultPad = -vm.ShownAirBox!.Min.X;
        Assert.Equal((defaultPad / 1000e-6 * 100).ToString("G4", CultureInfo.InvariantCulture), vm.Properties.PadXMinPercent);
        vm.Properties.CommitAirBoxPercent("xmin");
        Settle(vm);
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Null(EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).AirBox?.XMin);

        // Each face is its own setting: the +X side alone.
        vm.Properties.PadXMaxPercent = "10";
        vm.Properties.CommitAirBoxPercent("xmax");
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        var stated = EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).AirBox!;
        Assert.Equal(new EmAirBoxFace(null, null, 10), stated.XMax);
        Assert.Null(stated.XMin);
        // The content spans x 0 … 1000 µm: a tenth of it beyond +X, the default still beyond −X.
        Assert.Equal(1100e-6, vm.ShownAirBox!.Max.X, 12);
        Assert.Equal(-defaultPad, vm.ShownAirBox.Min.X, 12);

        vm.Properties.PadXMinPercent = "10";
        vm.Properties.CommitAirBoxPercent("xmin");
        Settle(vm);
        Assert.Equal(new EmAirBoxFace(null, null, 10), EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).AirBox!.XMin);
        Assert.Equal(-100e-6, vm.ShownAirBox!.Min.X, 12);
        // A boundary change keeps the percentage.
        Assert.Null(vm.SetAirBoxBoundary("xmin", Em3dBoundaryKind.Pec));
        Assert.Equal(10, EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).AirBox!.XMin!.PaddingPercent);
    }

    /// <summary>brief-dfr32-1 R-dfr32-1-4 — each face row reads its padding as a length, in the unit it reads in, beside its
    /// share of the extent: 20 % of this board's 100 µm z extent is "20 µm, 20 %", and a default face says both as well.</summary>
    [Fact]
    public void AFacesPadding_ReadsAsALengthBesideItsPercentage()
    {
        var vm = Open(Doc(new EmSetup
        {
            Name = "S1", Solver3D = Em3dSolverId.Palace, AirBox = new EmAirBox(ZMax: new EmAirBoxFace(null, null, 20)),
        }));
        vm.TreeGrouping = C3dTreeGrouping.Primitive;
        vm.SelectedTreeItem = vm.Tree.Single(g => g.Header == "Boxes").Items[0];

        Assert.Equal("20 µm, 20 %", vm.Properties.AirBoxFaces.Single(f => f.Face == "zmax").Padding);
        double pad = -vm.ShownAirBox!.Min.X;
        Assert.StartsWith($"{CircuitRF.Render.Em3dSectionScene.FormatLength(pad)}, " +
                          $"{(pad / 1000e-6 * 100).ToString("G4", CultureInfo.InvariantCulture)} % (default: ",
                          vm.Properties.AirBoxFaces.Single(f => f.Face == "xmin").Padding);
    }

    [Fact]
    public void APaddingTypedWithADecimalComma_IsReadAsADecimalPoint()
    {
        var vm = Open(Doc(new EmSetup { Name = "S1", Solver3D = Em3dSolverId.Palace }));
        vm.RestoreActiveSetup("S1");
        Assert.Null(vm.SetAirBoxPaddingPercent("zmax", "100,8"));
        Assert.Equal(100.8, C3dSetups.Read(vm.Document).Single().Setup!.AirBox!.ZMax!.PaddingPercent);
    }

    [Fact]
    public void PaddingPercent_RoundTripsTheCem_RefusesBothSpellings_AndAFlatAxisTakesTheDefaultWithANote()
    {
        var setup = new EmSetup { Name = "S", Solver3D = Em3dSolverId.Palace, AirBox = new EmAirBox(ZMin: new EmAirBoxFace(null, null, 50), ZMax: new EmAirBoxFace(null, null, 50)) };
        Assert.Equal(setup.AirBox, EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup)).AirBox);

        var notes = new List<string>();
        var flat = Em3dGenerator.PaddedAirBox(setup, (0, 0, 1e-3, 1e-3, 0, 0), 1e9, null, [], notes, out string? refusal);
        Assert.Null(refusal);
        Assert.True(flat!.Max.Z > 0);
        Assert.Contains(notes, n => n.Contains("along z") && n.Contains("default padding"));

        setup.AirBox = new EmAirBox(XMin: new EmAirBoxFace(100, null, 10));
        Assert.Null(Em3dGenerator.PaddedAirBox(setup, (0, 0, 1e-3, 1e-3, 0, 1e-4), 1e9, null, [], [], out refusal));
        Assert.Contains("both PaddingUm and PaddingPercent", refusal);
    }

    [Fact]
    public void ThePropertiesInspector_HostsTheActive3DViewsSelection_AndTheToolbarPlaneComboSetsThePlane()
    {
        var vm = Open(Doc());
        var panel = new PropertiesTool();
        panel.SetActiveC3d(vm);
        Assert.True(panel.IsC3dActive);
        Assert.Same(vm.Properties, panel.C3dInspectorVm);
        Assert.False(panel.IsSchematicContextActive);
        panel.SetActiveLayout(null);
        Assert.False(panel.IsC3dActive);
        Assert.Null(panel.C3dInspectorVm);

        vm.PlaneKind = C3dPlane.YZ;
        Assert.True(vm.IsPlaneYZ);
        Assert.Equal(C3dPlane.YZ, vm.Plane.Plane);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dDocument Doc(params EmSetup[] setups)
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        return new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000 * Um, 1000 * Um, 100 * Um) },
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 100 * Um, Rect = R(0, 450, 1000, 100) },
                new C3dSheet { Name = "gnd", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = R(0, 0, 1000, 1000) },
            ],
            Setups = [.. setups.Select(EmSetupPersistence.ToEmbedded)],
        };
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 2 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
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
