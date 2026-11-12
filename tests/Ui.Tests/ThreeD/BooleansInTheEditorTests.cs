// ================================================================
//  BooleansInTheEditorTests.cs — the gate for brief-em3d-66: the Boolean panel, the tree, the inspector, entering a
//  boolean and the refusals of a face edit on its result. Counters and document bytes only; nothing is looked at. What
//  needs OpenCASCADE is a [KernelFact] and skips, naming what to build, where the worker is not built.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Tests.ThreeD.Occ;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class BooleansInTheEditorTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-bool66-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly List<GeometryKernel> _kernels = [];

    public BooleansInTheEditorTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        foreach (var k in _kernels) k.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. legality: each refused operand's own sentence; the kernel's, read from the capability ───────

    [Fact]
    public void Gate1_RefusedOperandsSayWhy_TwoBoxesAreEnabled_AndWithoutTheKernelTheCapabilitysSentence()
    {
        string ws = Workspace();
        WriteC3d(ws, "die", new C3dDocument { Objects = [Box("chip", "Gold", 0, 0, 0, 20, 20, 5)] });
        string path = WriteC3d(ws, "pkg", new C3dDocument
        {
            SnapDbu = Um,
            Objects =
            [
                Box("a", "Gold", 0, 0, 0, 40, 40, 10), Box("b", "Gold", 20, 20, 0, 40, 40, 10),
                new C3dSheet { Name = "s", Material = "Gold", Plane = C3dPlane.XY, Offset = 30 * Um,
                               Rect = new C3dRect { Min = new C3dPoint2(0, 0), Size = new C3dPoint2(10 * Um, 10 * Um) } },
            ],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../die", Placement = new C3dPlacement { Origin = new C3dPoint3(100 * Um, 0, 0) } }],
        });
        var fake = new FakeKernel();
        var available = Kernel(fake.Create());
        var vm = Open(path, available);

        SelectInOrder(vm, "s", "a");
        Assert.All(Operations(vm), i => Assert.Equal((false, C3dBooleans.MixedRefused("s", "a")), (i.Enabled, i.Tip)));
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id),
                                .. vm.Viewer.Scene.Objects.Where(o => vm.InstanceOf(o) == "U1").Select(o => Scene3DItem.OfObject(o.Id))]);
        Assert.All(Operations(vm), i => Assert.Equal((false, C3dBooleans.InstanceRefused), (i.Enabled, i.Tip)));
        SelectInOrder(vm, "a");
        Assert.All(Operations(vm), i => Assert.Equal((false, C3dBooleans.SelectTwo), (i.Enabled, i.Tip)));
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(vm.SceneObject("a")!.Id, 0)]);
        Assert.Equal(C3dBooleans.ObjectModeOnly, vm.BooleanRefusal());
        // A polyline is never drawn and a wire needs pads: the one rule the menu reads, stated for them directly.
        Assert.Contains("Extrude it first", C3dBooleans.SelectionRefusal([new C3dPolyline { Name = "p" }, Box("a", null, 0, 0, 0, 1, 1, 1)]));
        Assert.Contains("stop being a wire", C3dBooleans.SelectionRefusal([new C3dWire { Name = "w" }, Box("a", null, 0, 0, 0, 1, 1, 1)]));

        SelectInOrder(vm, "a", "b");
        Assert.All(Operations(vm), i => Assert.True(i.Enabled, i.Tip));

        // Without the kernel: the capability's own sentence, not one of the editor's.
        var absent = Kernel(Absent());
        var none = Open(path, absent);
        SelectInOrder(none, "a", "b");
        string expected = GeometryKernel.NeedsKernel("Boolean", absent.Known!);
        Assert.All(Operations(none), i => Assert.Equal((false, expected), (i.Enabled, i.Tip)));
        Assert.Equal(0, absent.RequestsSent);
    }

    // ── 2. Tool and Blank: the first selected is a Tool, the last the Blank ────────────────────────

    [Fact]
    public void Gate2_TheLastSelectedIsTheBlank_SwapExchangesTwo_AndTheRadioLeavesExactlyOne()
    {
        var vm = Open(Write([Box("a", "Gold", 0, 0, 0, 40, 40, 10), Box("b", "Gold", 20, 0, 0, 40, 40, 10),
                             Box("c", "Gold", 0, 20, 0, 40, 40, 10), Box("d", "Gold", 20, 20, 0, 40, 40, 10)]), Kernel(new FakeKernel().Create()));
        foreach (string[] order in (string[][])[["b", "a"], ["c", "a", "d"], ["d", "c", "b", "a"]])
        {
            SelectInOrder(vm, order);
            vm.OpenBoolean(C3dBooleanOp.Subtract);
            Assert.True(vm.BooleanOpen, vm.StatusMessage);
            Quiet(vm);
            Assert.Equal(order, vm.BooleanRows.Select(r => r.Name));
            Assert.Equal([.. order.Select((_, i) => i == order.Length - 1)], vm.BooleanRows.Select(r => r.IsBlank));
            Assert.Equal(order.Length == 2, vm.BooleanCanSwap);
            vm.CancelBoolean();
            Settle(vm);                                               // Cancel gives the selection back once its scene is up
        }

        SelectInOrder(vm, "b", "a");
        vm.OpenBoolean(C3dBooleanOp.Unite);
        vm.SwapBoolean();
        Quiet(vm);
        Assert.Equal([true, false], vm.BooleanRows.Select(r => r.IsBlank));
        vm.CancelBoolean();
        Settle(vm);

        SelectInOrder(vm, "c", "a", "d");
        vm.OpenBoolean(C3dBooleanOp.Intersect);
        vm.BooleanRows[0].IsBlank = true;
        Quiet(vm);
        Assert.Equal([true, false, false], vm.BooleanRows.Select(r => r.IsBlank));
        vm.CancelBoolean();
        Assert.Equal(4, vm.Document.Objects.Count);                   // nothing was written
        Assert.Equal(0, vm.UndoEntries);
    }

    // ── 3. counters: the newest preview wins; OK, undo and redo ask the kernel nothing ─────────────

    [KernelFact]
    public void Gate3_TheLatestPreviewIsDrawnOnce_AndOkUndoAndRedoMakeNoWorkerCall()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        var vm = Open(Write([Box("lid", "Gold", 0, 0, 500, 4000, 3000, 250), Cyl("bore", "Copper", 2000, 1500, 400, 500, 300)]), kernel);
        SelectInOrder(vm, "bore", "lid");
        long scenes = vm.Viewer.Source.Requested;
        vm.OpenBoolean(C3dBooleanOp.Subtract);
        vm.BooleanOp = C3dBooleanOp.Intersect;                        // changed twice, quickly
        vm.BooleanOp = C3dBooleanOp.Subtract;
        WaitForPreview(vm);
        Settle(vm);
        Assert.Equal(1, vm.BooleanPreviewsDrawn);
        Assert.True(vm.BooleanRepliesDiscarded >= 1, $"{vm.BooleanRepliesDiscarded} replies discarded");
        Assert.Equal(scenes + 1, vm.Viewer.Source.Requested);        // the reply: one scene, never one per frame
        Assert.True(vm.SceneObject("lid")!.Selectable);               // the result …
        Assert.False(vm.SceneObject("bore:preview")!.Selectable);     // … and its operands, ghosts
        Assert.StartsWith("'lid' keeps its name and Gold; 'bore' is removed from it", vm.BooleanKeeps);
        Assert.True(vm.CanAcceptBoolean);

        long sent = kernel.RequestsSent;
        int entries = vm.UndoEntries;
        vm.AcceptBoolean();
        Settle(vm);
        Assert.Equal(sent, kernel.RequestsSent);                      // the commit: zero worker calls
        Assert.Equal(entries + 1, vm.UndoEntries);                    // one undo entry
        var b = Assert.IsType<C3dBoolean>(Assert.Single(vm.Document.Objects));
        Assert.Equal(("lid", "", "bore"), (b.Name, b.Blank!.Name, b.Tools.Single().Name));
        Assert.Empty(vm.Elaboration!.Refusals);

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(["lid", "bore"], vm.Document.Objects.Select(o => o.Name));
        vm.UndoRedo.Redo();
        Settle(vm);
        Assert.IsType<C3dBoolean>(Assert.Single(vm.Document.Objects));
        Assert.Equal(sent, kernel.RequestsSent);                      // undo and redo: zero
    }

    // ── brief-hzn-10: sheets in one plane ─────────────────────────────────────────────────────────

    /// <summary>R-hzn10-3 — two coplanar sheets are offered Unite with no geometry kernel at all; it previews at once, built here,
    /// and OK writes a Boolean of sheets.</summary>
    [Fact]
    public void TwoCoplanarSheets_UniteWithNoKernel_AndOkWritesABooleanOfSheets()
    {
        C3dSheet Sheet(string name, long u, long v) => new()
        {
            Name = name, Material = "Gold", Plane = C3dPlane.XY, Offset = 30 * Um,
            Rect = new C3dRect { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(40 * Um, 20 * Um) },
        };
        var absent = Kernel(Absent());
        var vm = Open(Write([Box("base", "Alumina", 0, 0, 0, 100, 60, 30), Sheet("a", 0, 0), Sheet("b", 30, 10)]), absent);
        SelectInOrder(vm, "b", "a");
        Assert.All(Operations(vm), i => Assert.True(i.Enabled, i.Tip));

        vm.OpenBoolean(C3dBooleanOp.Unite);
        Assert.Null(vm.BooleanError);
        Assert.True(vm.CanAcceptBoolean);
        vm.AcceptBoolean();
        Settle(vm);

        var b = Assert.IsType<C3dBoolean>(vm.Document.Objects.Single(o => o.Name == "a"));
        Assert.True(C3dSheetBooleans.IsSheetBoolean(b));
        Assert.Equal(["b"], b.Tools.Select(t => t.Name));
        Assert.Equal(["a"], vm.Elaboration!.Sheets.Select(s => s.Name));
        Assert.Equal(0, absent.RequestsSent);
    }

    // ── 5. disabled means dissolved ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_ADisabledBoolean_ElaboratesAsItsDissolvedDocument_WithNoKernelAtAll()
    {
        var absent = Kernel(Absent());
        var b = new C3dBoolean
        {
            Name = "lid", Op = C3dBooleanOp.Subtract, Enabled = false,
            Blank = Box("", "Gold", 0, 0, 500, 4000, 3000, 250), Tools = [Cyl("bore", "Copper", 2000, 1500, 400, 500, 300)],
            Placement = new C3dPlacement { Origin = new C3dPoint3(700 * Um, -300 * Um, 0), Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 90 }] },
        };
        string path = Write([Box("base", "Gold", -100, -100, 0, 9000, 9000, 100), b, Box("top", "Copper", 0, 0, 900, 100, 100, 100)]);
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!, ".cws");
        var doc = C3dPersistence.LoadFromFile(path);
        var dissolved = C3dPersistence.LoadFromFile(path);
        dissolved.Objects = [dissolved.Objects[0], .. C3dBooleans.Dissolve((C3dBoolean)dissolved.Objects[1]), dissolved.Objects[2]];

        var off = C3dElaborator.ElaborateOnce(doc, path, cws, kernel: absent);
        var flat = C3dElaborator.ElaborateOnce(dissolved, path, cws, kernel: absent);
        Assert.True(off.Ok, string.Join(" ", off.Refusals));
        Assert.Equal(Problem(flat), Problem(off));
        Assert.Equal(["base", "lid", "bore", "top"], off.Solids.Select(s => s.Name));
        Assert.Equal(0, absent.RequestsSent);
    }

    [KernelFact]
    public void Gate5b_EnabledOffInTheInspector_IsTheDissolvedProblem_BackOnFromTheCache_AndDissolveIsOneEntry()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 500, 4000, 3000, 250), Cyl("bore", "Copper", 2000, 1500, 400, 500, 300))]), kernel);
        SelectInOrder(vm, "lid");
        Assert.True(vm.Properties.IsBoolean);
        int entries = vm.UndoEntries;
        vm.Properties.BooleanEnabled = false;
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        var dissolved = C3dPersistence.Deserialize(C3dPersistence.Serialize(vm.Document));
        dissolved.Objects = C3dBooleans.Dissolve((C3dBoolean)dissolved.Objects[0]);
        var flat = C3dElaborator.ElaborateOnce(dissolved, vm.FilePath, null, kernel: kernel);
        Assert.Equal(Problem(flat), Problem(vm.Elaboration!));

        long sent = kernel.RequestsSent, trees = vm.KernelTreesBuilt;
        SelectInOrder(vm, "lid");
        vm.Properties.BooleanEnabled = true;
        Settle(vm);
        Assert.Equal(sent, kernel.RequestsSent);                      // back on: the cached result
        Assert.Equal(trees, vm.KernelTreesBuilt);

        SelectInOrder(vm, "lid");
        entries = vm.UndoEntries;
        vm.RunModify("BooleanDissolve");
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(sent, kernel.RequestsSent);
        Assert.Equal(["lid", "bore"], vm.Document.Objects.Select(o => o.Name));
        Assert.Equal("Gold", vm.Document.Objects[0].Material);
    }

    // ── 6. references on the Blank survive ─────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate6_APortAFaceBoundaryAndAWireOnTheBlank_AllStillLandAfterSubtract()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        string path = Write(
        [
            Box("lid", "Copper", 0, 0, 500, 4000, 3000, 250), Cyl("bore", "Copper", 2000, 1500, 400, 500, 300),
            Box("pad", "Copper", 4300, 1000, 500, 800, 1000, 250),
            Box("sub", "Alumina", 0, 4000, 0, 4000, 3000, 500), Box("cavity", "Alumina", 1000, 5000, 200, 1000, 1000, 400),
            new C3dWire
            {
                Name = "w1", Material = "Gold", DiameterUm = 25,
                Points = [new(1000 * Um, 500 * Um, 750 * Um), new(2800 * Um, 800 * Um, 1100 * Um), new(4700 * Um, 1500 * Um, 750 * Um)],
            },
        ], doc =>
        {
            doc.FaceBoundaries = [new C3dFaceBoundary { Object = "sub", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec }];
            doc.Ports = [new C3dPort { Number = 1, Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XY, Offset = 750 * Um,
                                       Rect = new C3dRect { Min = new C3dPoint2(4000 * Um, 1200 * Um), Size = new C3dPoint2(300 * Um, 600 * Um) } }];
        });
        var vm = Open(path, kernel);
        var before = References(vm);

        foreach (var (tool, blank) in new[] { ("bore", "lid"), ("cavity", "sub") })
        {
            SelectInOrder(vm, tool, blank);
            vm.OpenBoolean(C3dBooleanOp.Subtract);
            WaitForPreview(vm);
            Assert.Null(vm.BooleanError);
            vm.AcceptBoolean();
            Settle(vm);
        }
        Assert.Equal(2, vm.Document.Objects.OfType<C3dBoolean>().Count());
        var e = vm.Elaboration!;
        Assert.Empty(e.Refusals);
        Assert.Empty(e.WireRefusals);
        Assert.Equal(before, References(vm));
    }

    // ── 7. an operand's drag: the kernel asked nothing until the release, once then ─────────────────

    [KernelFact]
    public void Gate7_DraggingAnEnteredOperand_Fifty_MovesAskTheKernelNothing_AndTheReleaseReEvaluatesOnce()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 0, 100, 60, 20), Cyl("bore", "Copper", 50, 30, -10, 40, 10))]), kernel);
        Assert.Null(vm.EnterBoolean(0));
        Settle(vm);
        Assert.True(vm.IsInBoolean);
        Assert.False(vm.SceneObject("lid")!.Selectable);              // the result, a ghost
        var bore = vm.SceneObject("lid:bore")!;
        Assert.True(bore.Selectable);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(bore.Id)]);

        long sent = kernel.RequestsSent, trees = vm.KernelTreesBuilt;
        int entries = vm.UndoEntries;
        long previews = vm.PreviewUpdates;
        vm.StartMove(C3dMoveLock.AxisX);
        ClickAt(vm, 50, 30, 10);                                     // the base
        for (int i = 0; i < 50; i++) HoverVm(vm.Viewer, 150 + i, 150);
        Assert.True(vm.PreviewUpdates - previews >= 10, "the preview did not follow the cursor");
        Assert.Equal(sent, kernel.RequestsSent);                      // 50 moves: zero worker calls
        Assert.Equal(trees, vm.KernelTreesBuilt);
        vm.OpenField(null);
        vm.FieldText = "20";
        vm.FieldEnter();                                              // the release
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(trees + 1, vm.KernelTreesBuilt);                 // the boolean re-evaluated once
        var b = Assert.IsType<C3dBoolean>(Assert.Single(vm.Document.Objects));
        Assert.Equal(new C3dPoint3(20 * Um, 0, 0), b.Tools[0].Placement.Origin);
        Assert.Equal("bore", b.Tools[0].Name);
    }

    // ── 8. D13: a result's faces are read, never edited; an entered operand's are ───────────────────

    [KernelFact]
    public void Gate8_FaceAndVertexEditsOnAResultAreRefusedWithTheSentence_AndRunOnAnEnteredOperand()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 0, 100, 60, 20), Cyl("bore", "Copper", 50, 30, -10, 40, 10))]), kernel);
        string refusal = C3dBooleans.ResultNotEditable("lid", vm.Document.Objects[0]);
        SelectFace(vm, "lid", "zmax");
        foreach (Action start in new Action[] { vm.StartPushPull, vm.StartFaceMove, vm.StartExtrudeFace, vm.StartAlignToFace })
        {
            vm.StatusMessage = "";
            start();
            Assert.Null(vm.Tool);
            Assert.Equal(refusal, vm.StatusMessage);
        }
        Assert.All(vm.DrawMenuItems().Where(i => i.Header is "Move Along Normal" or "Move" or "Extrude to New Solid" or "Align to Face…"),
                   i => Assert.Equal((false, refusal), (i.Enabled, i.Tip)));
        var lid = vm.SceneObject("lid")!;
        var corner = vm.Viewer.Scene.FeaturesOf(lid.Id).Vertex(0);
        vm.Viewer.SelectMode = Scene3DSelectMode.Vertex;
        vm.Viewer.SetSelection([Scene3DItem.OfVertex(lid.Id, vm.Viewer.Scene.ToLocal(corner.X, corner.Y, corner.Z))]);
        vm.StartVertexMove();
        Assert.Equal(refusal, vm.StatusMessage);

        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        Assert.Null(vm.EnterBoolean(0));
        Settle(vm);
        SelectFace(vm, "lid:Blank", "zmax");
        vm.StartPushPull();
        Assert.IsType<PushPullTool>(vm.Tool);
    }

    /// <summary>An entered operand has no Appearance group of its own; the Inspector says where its look comes from instead of
    /// showing nothing (its material's, or the top-level operation's), and the top-level object still has the group.</summary>
    [KernelFact]
    public void AnEnteredOperand_SaysWhereItsLookComesFrom_InPlaceOfAnAppearanceGroup()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 0, 100, 60, 20), Cyl("bore", "Copper", 50, 30, -10, 40, 10))]), kernel);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("lid")!.Id)]);
        Assert.True(vm.Properties.HasAppearance);
        Assert.False(vm.Properties.HasOperandLookNote);
        Assert.Null(vm.EnterBoolean(0));
        Settle(vm);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("lid:bore")!.Id)]);
        Assert.False(vm.Properties.HasAppearance);
        Assert.Equal(C3dPropertiesViewModel.OperandLookText("lid", "Copper"), vm.Properties.OperandLookNote);
    }

    // ── 9. a refusal is not a rollback ─────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate9_AnInspectorEditThatFails_IsKept_FlaggedOnItsNode_AndOneUndoRestoresTheWorkingState()
    {
        var kernel = Kernel(KernelForTests.New());
        kernel.Probe();
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 0, 100, 60, 20), Box("far", "Gold", 500, 0, 0, 20, 20, 20))]), kernel);
        Assert.Empty(vm.Elaboration!.Refusals);
        SelectInOrder(vm, "lid");
        int entries = vm.UndoEntries;
        vm.Properties.BooleanOperation = C3dBooleanOp.Intersect;
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(C3dBooleanOp.Intersect, ((C3dBoolean)vm.Document.Objects[0]).Op);     // kept
        Assert.Equal("'lid' and 'far' share nothing: the intersection is empty.", TreeRow(vm, "lid").Refusal);   // flagged, in its own words
        Assert.Contains(vm.Elaboration!.Refusals, r => r.Contains("'lid'", StringComparison.Ordinal));
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(C3dBooleanOp.Subtract, ((C3dBoolean)vm.Document.Objects[0]).Op);
        Assert.Null(TreeRow(vm, "lid").Refusal);
    }

    // ── 10. the tree ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_ABooleanIsANodeUnderItsBlanksMaterial_OrUnderBooleans_AndNoFilterTakesAnOperandFromIt()
    {
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 0, 100, 60, 20), Cyl("bore", "Copper", 50, 30, -10, 40, 10)),
                             Box("other", "Copper", 200, 0, 0, 10, 10, 10)]), Kernel(Absent()), expectScene: false);
        var gold = vm.Tree.Single(g => g.Header == "Gold");
        var lid = Assert.Single(gold.Items);
        Assert.Equal("lid", lid.Name);
        Assert.Equal([("Box", "Blank"), ("bore", "Tool")], lid.Children.Select(c => (c.Name, c.Detail!.Split(' ')[0])));
        Assert.Equal(["other"], vm.Tree.Single(g => g.Header == "Copper").Items.Select(i => i.Name));    // bore is listed once

        vm.MaterialFilters.Single(f => f.Name == "Copper").IsChecked = false;
        Assert.DoesNotContain(vm.Tree, g => g.Header == "Copper");
        Assert.Contains(vm.Tree.Single(g => g.Header == "Gold").Items.Single().Children, c => c.Name == "bore");

        vm.ShowAllTreeRowsCommand.Execute(null);
        vm.TreeGrouping = C3dTreeGrouping.Primitive;
        var booleans = vm.Tree.Single(g => g.Role == C3dTreeGroupRole.Booleans);
        Assert.Equal(("Booleans", "lid"), (booleans.Header, booleans.Items.Single().Name));
        // 3D editor round 5: the air box is drawn around what did elaborate, and heads Boxes — not an operand either.
        Assert.Equal(["other"], vm.Tree.Single(g => g.Header == "Boxes").Items.Where(i => !i.IsAirBox).Select(i => i.Name));
    }

    // ── 3D editor bugs round 5 ─────────────────────────────────────────────────────────────────────

    /// <summary>Two boxes of one height united are ONE solid: its top is one face, not the operands' three pieces, and no
    /// edge is drawn where they met — the worker merges a result's same-domain faces.</summary>
    [KernelFact]
    public void Round5_AUnionOfTwoBoxesOfOneHeight_HasOneTopFace_AndNoSeamEdges()
    {
        var kernel = Kernel(KernelForTests.New());
        var u = new C3dBoolean { Name = "u", Op = C3dBooleanOp.Unite, Blank = Box("", "Gold", 0, 0, 0, 40, 40, 10), Tools = [Box("b", "Gold", 20, 20, 0, 40, 40, 10)] };
        var vm = Open(Write([u]), kernel);
        var o = vm.SceneObject("u")!;
        // The Blank's names win a merged face; the Tool keeps only the four walls that are its alone.
        Assert.Equal(["b:xmax", "b:xmin", "b:ymax", "b:ymin", "xmax", "xmin", "ymax", "ymin", "zmax", "zmin"], o.FaceNames.Order(StringComparer.Ordinal));
        // An L-shaped octagon's prism: 8 vertical edges, 8 around the top, 8 around the bottom.
        var edges = vm.Viewer.Scene.EdgeBatches.Single(e => e.ObjectId == o.Id);
        Assert.Equal(24, edges.VertexCount / 2);
    }

    /// <summary>A click on an operand's tree row enters its boolean; a click away — on nothing, or on another row — leaves
    /// it, so the result is drawn again in place of its Tools.</summary>
    [KernelFact]
    public void Round5_AnOperandRowEntersItsBoolean_AndAClickAwayOrAnotherRowLeavesIt()
    {
        var kernel = Kernel(KernelForTests.New());
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 0, 100, 60, 20), Cyl("bore", "Copper", 50, 30, -10, 40, 10))]), kernel);
        C3dTreeItem Bore() => TreeRow(vm, "lid").Children.Single(c => c.Name == "bore");

        vm.SelectedTreeItem = Bore();
        Settle(vm);
        Assert.True(vm.IsInBoolean);
        Assert.NotNull(vm.SceneObject("lid:bore"));                   // the Tool drawn as an object of its own

        HoverVm(vm.Viewer, 2, 2);                                     // nothing under the cursor
        vm.Viewer.Click(false);
        Settle(vm);
        Assert.False(vm.IsInBoolean);
        Assert.Null(vm.SceneObject("lid:bore"));
        Assert.True(vm.SceneObject("lid")!.Selectable);               // the result, no longer a ghost
        Assert.Empty(vm.Viewer.View.Selection);

        vm.SelectedTreeItem = Bore();
        Settle(vm);
        Assert.True(vm.IsInBoolean);
        vm.SelectedTreeItem = TreeRow(vm, "lid");
        Settle(vm);
        Assert.False(vm.IsInBoolean);
        Assert.Equal(["lid"], vm.Viewer.SelectedObjects().Select(x => x.Name));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private GeometryKernel Kernel(GeometryKernel k)
    {
        _kernels.Add(k);
        if (k.Known is null) k.Probe();
        return k;
    }

    private static GeometryKernel Absent() => new(new GeometryKernelOptions
    {
        Locate = () => new GeometryKernelLocation(null, null, "not found", GeometryKernelAbsence.NotBuilt,
                                                  "The worker has not been built here.", "Build it, then check again."),
        DiskCache = false,
    });

    private static IReadOnlyList<Viewer3DMenuItem> Operations(C3dEditorViewModel vm)
        => [.. vm.DrawMenuItems().Single(i => i.Header == "Boolean").Children!.Take(3)];

    /// <summary>What a run would be given, as text: each solid's name, material, role, order and geometry.</summary>
    private static string Problem(C3dElaboration e)
        => string.Join("\n", e.Solids.Select(s => $"{s.Name}|{s.Material}|{s.Role}|{s.Order}|{s.Primitive}"));

    /// <summary>The port's two conductors, the boundary's landing and the wire's pads, by name.</summary>
    private static string References(C3dEditorViewModel vm)
    {
        var e = vm.Elaboration!;
        var port = Assert.Single(C3dPorts.Resolve(vm.Document, new C3dPortContext(e, vm.Document.DbuPerMicron, null, [])));
        Assert.True(port.Refusal is null, port.Refusal);
        Assert.Null(C3dProblemAssembly.FaceBoundaries(vm.Document, e, [.. e.Materials], EmSetup.DefaultOperatingTempC, out var landed));
        return $"port {port.Resolved!.PositiveObject}/{port.Resolved.NegativeObject}; boundary " +
               string.Join(",", landed.Select(b => b.Object + "." + b.Face)) + $"; wire refusals {e.WireRefusals.Count}";
    }

    private static C3dTreeItem TreeRow(C3dEditorViewModel vm, string name)
        => vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == name);

    private static void WaitForPreview(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => !vm.BooleanBusy && (vm.BooleanPreviewsDrawn > 0 || vm.BooleanError is not null), TimeSpan.FromSeconds(60)),
                       "the preview never arrived");

    /// <summary>No preview outstanding, and the scene its reply asked for adopted. The app posts every reply to the UI
    /// thread; a test's inline post runs it on the pool, so a test waits for it before acting on the panel again.</summary>
    private static void Quiet(C3dEditorViewModel vm)
    {
        Assert.True(SpinWait.SpinUntil(() => !vm.BooleanBusy, TimeSpan.FromSeconds(60)), "the preview never arrived");
        Settle(vm);
    }

    private static void SelectInOrder(C3dEditorViewModel vm, params string[] names)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection(names.Select(n => Scene3DItem.OfObject(vm.SceneObject(n)!.Id)));
    }

    private static void SelectFace(C3dEditorViewModel vm, string obj, string face)
    {
        var o = vm.SceneObject(obj)!;
        int f = Enumerable.Range(0, o.FaceNames.Count).First(i => o.FaceNames[i] == face);
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(o.Id, f)]);
    }

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static C3dCylinder Cyl(string name, string? material, long x, long y, long z, long length, long radius)
        => new() { Name = name, Material = material, Base = new C3dPoint3(x * Um, y * Um, z * Um), Length = length * Um, Radius = radius * Um };

    private static C3dBoolean Subtract(string name, C3dObject blank, params C3dObject[] tools)
        => new() { Name = name, Op = C3dBooleanOp.Subtract, Blank = blank, Tools = [.. tools] };

    private string Write(List<C3dObject> objects, Action<C3dDocument>? more = null)
    {
        var doc = new C3dDocument { SnapDbu = Um, Objects = objects };
        more?.Invoke(doc);
        return WriteC3d(Workspace(), "cell", doc);
    }

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }, new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = "Alumina", Epsr = 9.8 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private C3dEditorViewModel Open(string c3d, GeometryKernel kernel, bool expectScene = true)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a(), kernel: kernel);
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        if (expectScene) Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(150 * UmM, 90 * UmM, 60 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        HoverVm(v, sx, sy);
        v.Click(false);
    }
}
