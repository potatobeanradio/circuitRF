// ================================================================
//  SarEditorTests.cs — brief-sar-3's three editor gestures, headless: the Ellipsoid tool makes an ellipsoid; Add Phantom
//  inserts one phantom in one undo entry and Flatten replaces it with its layers; the Lumped Element tool adds a record
//  that undo takes away, selects from the tree and lets go as any selection does, edits its topology from a choice and
//  goes into Copy as Vector. What they write is the Design layer's, gated in tests/Ui.Tests/Em3d/SarPhantomsLumpedGridTests.cs.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class SarEditorTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar3-editor-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public SarEditorTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>A click for the centre and one 30 µm away make one ellipsoid with three equal semi-axes, in one undo entry.</summary>
    [Fact]
    public void TheEllipsoidTool_MakesAnEllipsoid()
    {
        var vm = Open();
        int before = vm.UndoEntries;
        vm.Arm(C3dToolKind.Ellipsoid);
        Assert.IsType<EllipsoidTool>(vm.Tool);
        ClickAt(vm, 20, 10, 0);
        ClickAt(vm, 50, 10, 0);
        var e = Assert.IsType<C3dEllipsoid>(vm.Document.Objects[^1]);
        Assert.Equal("ellipsoid1", e.Name);
        Assert.True(e.SemiAxes.X > 0 && e.SemiAxes.X == e.SemiAxes.Y && e.SemiAxes.Y == e.SemiAxes.Z, e.SemiAxes.ToString());
        Assert.Equal(before + 1, vm.UndoEntries);
    }

    /// <summary>Add Phantom is one undoable phantom; Flatten turns it into its three layers, in one more entry, and undo brings
    /// the phantom back.</summary>
    [Fact]
    public void AddPhantom_IsOneEntry_AndFlattenReplacesItWithItsLayers()
    {
        var vm = Open();
        int before = vm.UndoEntries;
        vm.AddPhantom(C3dPhantomShape.Ellipsoid3);
        var p = Assert.IsType<C3dPhantom>(vm.Document.Objects[^1]);
        Assert.Equal(("phantom1", 3), (p.Name, p.Layers.Count));
        Assert.Equal(before + 1, vm.UndoEntries);

        Assert.Null(vm.FlattenPhantom(vm.Document.Objects.Count - 1));
        Assert.DoesNotContain(vm.Document.Objects, o => o is C3dPhantom);
        Assert.Equal(["phantom1_skin", "phantom1_skull", "phantom1_brain"], vm.Document.Objects.OfType<C3dEllipsoid>().Select(o => o.Name));
        Assert.Equal(before + 2, vm.UndoEntries);

        vm.UndoRedo.Undo();
        Assert.Single(vm.Document.Objects.OfType<C3dPhantom>());
    }

    /// <summary>A layer's New Material… is the command it is everywhere else: it opens the picker on that layer, the layer keeps
    /// what it states until the picker answers, and the answer is the layer's, one undo entry.</summary>
    [Fact]
    public void APhantomLayersNewMaterial_OpensThePicker_ForThatLayer()
    {
        var vm = Open();
        vm.AddPhantom(C3dPhantomShape.Ellipsoid3);
        Settle(vm);
        vm.TreeSelectionChanged([vm.Tree.SelectMany(g => g.Items).First(r => r.Name == "phantom1")]);
        var layer = vm.Properties.PhantomLayers[0];
        string? was = layer.Material;
        (IReadOnlyList<int> Indices, bool StartNew, C3dMaterialPart? Part)? asked = null;
        vm.MaterialPickerRequested += (i, n, part) => asked = (i, n, part);

        layer.Material = C3dEditorViewModel.NewMaterialItem;
        Assert.NotNull(asked);
        Assert.True(asked.Value.StartNew);
        var target = Assert.IsType<C3dMaterialPart>(asked.Value.Part);
        Assert.Equal(was, target.Current);
        Assert.Equal(was, layer.Material);
        var phantom = (C3dPhantom)vm.Document.Objects[^1];
        Assert.Equal(was, phantom.Layers[0].Material);

        Settle(vm);
        int before = vm.UndoEntries;
        vm.ApplyPickedMaterial(target, "Gold");
        Assert.Equal("Gold", ((C3dPhantom)vm.Document.Objects[^1]).Layers[0].Material);
        Assert.Equal(before + 1, vm.UndoEntries);
    }

    /// <summary>A layer's pick commits after the pick, never inside it: the commit rebuilds the Inspector's rows, and doing that
    /// inside the ComboBox's own selection change crashed Avalonia's selection model (an index out of range). New Material… the
    /// same, and the row is put back then too.</summary>
    [Fact]
    public void APhantomLayerPick_CommitsAfterThePick()
    {
        var queued = new Queue<Action>();
        var committed = new List<string?>();
        int pickers = 0;
        var row = new C3dPhantomLayerRow("skin", "Skin (dry)", committed.Add, queued.Enqueue, () => pickers++);

        row.Material = "Muscle";
        Assert.Empty(committed);
        queued.Dequeue()();
        Assert.Equal(["Muscle"], committed);

        row.Material = C3dEditorViewModel.NewMaterialItem;
        Assert.Equal((0, C3dEditorViewModel.NewMaterialItem), (pickers, row.Material));
        queued.Dequeue()();
        Assert.Equal((1, "Muscle"), (pickers, row.Material));
        Assert.Equal(["Muscle"], committed);
    }

    /// <summary>Two corners make one lumped element record, C1 at 1 pF; undo removes it.</summary>
    [Fact]
    public void TheLumpedElementTool_AddsARecord_ThatUndoRemoves()
    {
        var vm = Open();
        vm.Arm(C3dToolKind.LumpedElement);
        Assert.IsType<LumpedElementTool>(vm.Tool);
        ClickAt(vm, 20, 10, 0);
        ClickAt(vm, 40, 30, 0);
        var le = Assert.Single(vm.Document.LumpedElements);
        Assert.Equal(("C1", "1", "pF"), (le.Name, le.C, le.CUnit));
        Assert.DoesNotContain(vm.Document.Objects, o => o.Name == "C1");

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.LumpedElements);
    }

    /// <summary>A lumped element's row stands for nothing in the scene. Selected from the tree with nothing selected, it is drawn
    /// as the selection; Esc lets it go, and so does a click on nothing.</summary>
    [Fact]
    public void ALumpedElementRow_HighlightsFromNothing_AndEscOrAnEmptyClickLetsItGo()
    {
        var vm = WithLumpedElement();
        foreach (var letGo in new Action[]
                 {
                     () => vm.Viewer.HandleKey(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None, gestureInProgress: false),
                     () => { vm.Viewer.Hover(1, 1); vm.Viewer.OnPicked(0u, Vector3.Zero, false); vm.Viewer.Click(false); },
                 })
        {
            Assert.Empty(vm.Viewer.Selection);
            int frames = 0;
            vm.Viewer.FrameRequested += Count;
            vm.TreeSelectionChanged([LumpedRow(vm)]);
            vm.Viewer.FrameRequested -= Count;
            Assert.True(frames > 0, "selecting the row asked for no frame, so its highlight is never drawn");
            var overlay = new Viewer3DDrawOverlay();
            vm.FillDrawOverlay(overlay);
            Assert.NotEmpty(overlay.Selected);
            Assert.Empty(overlay.LumpedElements);

            letGo();
            Assert.Null(vm.SelectedTreeItem);
            Assert.Empty(vm.SelectedTreeItems);
            overlay = new Viewer3DDrawOverlay();
            vm.FillDrawOverlay(overlay);
            Assert.Empty(overlay.Selected);
            Assert.NotEmpty(overlay.LumpedElements);
            void Count() => frames++;
        }
    }

    /// <summary>The Inspector offers the topology, the units and Model as choices, and a pick commits as typing did.</summary>
    [Fact]
    public void TheInspector_OffersTheTopologyAsAChoice()
    {
        var vm = WithLumpedElement();
        // The scene's rebuild after the placement rewrites the Inspector's rows; read them once it has landed, or a full
        // run's load lets it land mid-read (a "Collection was modified" from the Single below).
        Settle(vm);
        vm.TreeSelectionChanged([LumpedRow(vm)]);
        Settle(vm);
        var fields = vm.Properties.ThermalFields;
        Assert.Equal(["Parallel", "Series"], fields.Single(f => f.Key == "Topology").Choices);
        Assert.Equal(C3dEditorViewModel.LumpedUnits["C"], fields.Single(f => f.Key == "CUnit").Choices);
        Assert.Null(fields.Single(f => f.Key == "C").Choices);

        var topology = fields.Single(f => f.Key == "Topology");
        topology.Text = "Series";
        vm.Properties.CommitThermalField(topology);
        Assert.Equal(CircuitRF.Engine.Em3d.Em3dRlcTopology.Series, Assert.Single(vm.Document.LumpedElements).Topology);
    }

    /// <summary>Copy as Vector carries the lumped element as the view draws it: its outline and glyph, and its label.</summary>
    [Fact]
    public void CopyAsVector_CarriesTheLumpedElement()
    {
        var vm = WithLumpedElement();
        var picture = CircuitRF.Ui.Views.Viewer3D.Viewer3DVectorExport.Picture(vm.Viewer, W, H, out string? why);
        Assert.True(picture is not null, why);
        Assert.Equal(8, picture.Scene.Marks.Count);      // the sheet's four sides, two plates and two leads
        Assert.Equal("C1: C 1 pF", Assert.Single(picture.Scene.MarkLabels).Text);
    }

    private C3dEditorViewModel WithLumpedElement()
    {
        var vm = Open();
        vm.Arm(C3dToolKind.LumpedElement);
        ClickAt(vm, 20, 10, 0);
        ClickAt(vm, 40, 30, 0);
        vm.Disarm();
        Assert.Single(vm.Document.LumpedElements);
        return vm;
    }

    private static C3dTreeItem LumpedRow(C3dEditorViewModel vm)
        => vm.Tree.Single(g => g.Role == C3dTreeGroupRole.LumpedElements).Items.Single();

    private C3dEditorViewModel Open()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "pad", Material = "Gold", Min = new C3dPoint3(100 * Um, 0, 0), Size = new C3dPoint3(40 * Um, 40 * Um, 20 * Um) }],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(150 * UmM, 80 * UmM, 80 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)), "the scene never settled");

    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        v.Hover(sx, sy);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, sx, sy, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
        v.Click(false);
    }
}
