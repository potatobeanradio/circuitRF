// ================================================================
//  EdgeMovesTests.cs — Expand Edges and Contract Edges on a drawn sheet and a lumped port: an edge stops at the next boundary it
//  meets, a rectangle never collapses, and a rough sheet reaches a port the resolver accepts.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class EdgeMovesTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-edges-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();     // the scene builds on another thread

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static C3dBox Block(string name, long x, long y, long z, long dx, long dy, long dz, string material = "Copper")
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private static C3dSheet Sheet(string name, long u, long v, long du, long dv, long at = 100, C3dPlane plane = C3dPlane.XZ)
        => new() { Name = name, Plane = plane, Offset = at * Um, Rect = new C3dRect { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) } };

    /// <summary>Copper from z 0 to 100 and from 200 to 300, the whole of y 0 to 200 and x 0 to 200.</summary>
    private static C3dBox[] Plates() => [Block("low", 0, 0, 0, 200, 200, 100), Block("high", 0, 0, 200, 200, 200, 100)];

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6]);
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fr4", Epsr = 4.4 }],
        });
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new PatchRecordingBackend(), () => cws, _posted.Enqueue);
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

    private Viewer3DMenuItem Item(C3dEditorViewModel vm, string name, string menu, string child)
    {
        Settle(vm);
        var o = vm.Viewer.Scene.Objects.Single(x => x.Name == name);
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([new Scene3DItem(o.Id, -1, default)]);
        return vm.DrawMenuItems().Single(i => i.Header == menu).Children!.Single(i => i.Header == child);
    }

    private static C3dRect Rect(C3dEditorViewModel vm, string name) => ((C3dSheet)vm.Document.Objects.Single(o => o.Name == name)).Rect!;

    /// <summary>The whole reason for the feature: a sheet reaching through the top plate and floating above the bottom one is
    /// refused as a port saying how far it misses; two moves make it one, each its own undo entry.</summary>
    [Fact]
    public void ARoughSheet_BecomesALumpedPort_AndTheRefusalSaysHowFarItMissed()
    {
        var vm = Open(new C3dDocument { Objects = [.. Plates(), Sheet("gap", 50, 120, 100, 180)] });     // z 120 to 300

        var make = Item(vm, "gap", "Make Port", "Lumped");
        Assert.False(make.Enabled);
        Assert.Contains("both its left and right edges", make.Tip);
        Assert.Contains("Its bottom edge misses 'low' by 20 µm.", make.Tip);

        int entries = vm.UndoEntries;
        var contract = Item(vm, "gap", "Contract Edges", "+Z Only");
        Assert.Contains("'high'", contract.Tip);
        contract.Run!();
        Assert.Equal((120 * Um, 80 * Um), (Rect(vm, "gap").Min.V, Rect(vm, "gap").Size.V));

        Item(vm, "gap", "Expand Edges", "-Z Only").Run!();
        Assert.Equal((100 * Um, 100 * Um), (Rect(vm, "gap").Min.V, Rect(vm, "gap").Size.V));
        Assert.Equal(entries + 2, vm.UndoEntries);

        make = Item(vm, "gap", "Make Port", "Lumped");
        Assert.True(make.Enabled, make.Tip);

        vm.UndoRedo.Undo();
        Assert.Equal((120 * Um, 80 * Um), (Rect(vm, "gap").Min.V, Rect(vm, "gap").Size.V));
    }

    /// <summary>Touching is where an edge stays, a repeated Expand changes nothing, and with nothing beyond it the item is off. A side
    /// edge grazing the plates at its corners and a wall flush with the side are not in its way.</summary>
    [Fact]
    public void Expand_StopsAtWhatItTouches_AndIgnoresWhatOnlyGrazesItsEnds()
    {
        var vm = Open(new C3dDocument { Objects = [.. Plates(), Block("post", 170, 90, 100, 10, 20, 100), Sheet("gap", 50, 100, 100, 100)] });

        var again = Item(vm, "gap", "Expand Edges", "Z");
        Assert.False(again.Enabled);
        Assert.Contains("already touches 'low'", again.Tip);
        Assert.Contains("already touches 'high'", again.Tip);

        var right = Item(vm, "gap", "Expand Edges", "+X Only");
        Assert.Contains("'post'", right.Tip);                // past the plates its ends graze, to the post at x = 170
        right.Run!();
        Assert.Equal((50 * Um, 120 * Um), (Rect(vm, "gap").Min.U, Rect(vm, "gap").Size.U));

        var left = Item(vm, "gap", "Expand Edges", "-X Only");
        Assert.False(left.Enabled);
        Assert.Contains("has nothing beyond it", left.Tip);
    }

    /// <summary>Two triangles of a wall cut into two collinear pieces; the join between them is not a boundary, or the edge stops half
    /// way up the wall. Past a wall inside its own span, it stops where the block really ends.</summary>
    [Fact]
    public void ACutWallsTriangleJoin_IsNotAStop()
    {
        var vm = Open(new C3dDocument { Objects = [Block("block", 100, 0, 0, 100, 200, 100), Sheet("gap", 50, 10, 100, 20)] });
        Item(vm, "gap", "Expand Edges", "+Z Only").Run!();
        Assert.Equal(100 * Um, Rect(vm, "gap").Min.V + Rect(vm, "gap").Size.V);
    }

    /// <summary>A multilayer stackup is dielectric on dielectric: the boundary between two layers stops nothing, or an edge would never
    /// reach its copper. A dielectric met from open space does stop it.</summary>
    [Fact]
    public void ABoundaryBetweenDielectrics_StopsNothing_AndOneMetFromAirDoes()
    {
        var vm = Open(new C3dDocument
        {
            Objects = [Block("floor", 0, 0, 0, 200, 200, 10), Block("core", 0, 0, 10, 200, 200, 50, "Fr4"), Block("prepreg", 0, 0, 60, 200, 200, 50, "Fr4"),
                       Sheet("gap", 50, 80, 100, 10), Sheet("over", 50, 150, 100, 10, 150)],
        });
        var down = Item(vm, "gap", "Expand Edges", "-Z Only");
        Assert.Contains("'floor'", down.Tip);
        down.Run!();
        Assert.Equal(10 * Um, Rect(vm, "gap").Min.V);

        Item(vm, "over", "Expand Edges", "-Z Only").Run!();
        Assert.Equal(110 * Um, Rect(vm, "over").Min.V);
    }

    /// <summary>Contract never collapses: with no boundary inside, the item is off; with one boundary both edges would stop at, the nearer
    /// goes and the other stays.</summary>
    [Fact]
    public void Contract_NeverCollapses_TheNearerEdgeTakesASharedBoundary()
    {
        var vm = Open(new C3dDocument { Objects = [.. Plates(), Sheet("gap", 50, 110, 100, 100), Sheet("free", 300, 300, 50, 50)] });
        var free = Item(vm, "free", "Contract Edges", "All");
        Assert.False(free.Enabled);
        Assert.Contains("has nothing to stop at before the opposite edge", free.Tip);

        Item(vm, "gap", "Contract Edges", "Z").Run!();                  // z 110 to 210: both stop at 200, the top is nearer, the bottom stays
        Assert.Equal((110 * Um, 90 * Um), (Rect(vm, "gap").Min.V, Rect(vm, "gap").Size.V));
    }

    /// <summary>A round via is met at its true tangent, not at a facet's, and a sheet crossing the plane stops an edge as a solid does.</summary>
    [Fact]
    public void AnEdgeMeetsACylindersTangent_AndASheetCrossingThePlane()
    {
        var via = new C3dCylinder { Name = "via", Material = "Copper", Axis = C3dAxis.Z, Base = new C3dPoint3(100 * Um, 200 * Um, 0), Radius = 50 * Um, Length = 100 * Um };
        var vm = Open(new C3dDocument { Objects = [via, Sheet("flat", 50, 0, 100, 50, 50, C3dPlane.XY), Sheet("wall", 0, 0, 40, 100, 80), Sheet("up", 10, 10, 20, 20, 30, C3dPlane.XY)] });

        Item(vm, "flat", "Expand Edges", "+Y Only").Run!();
        Assert.Equal(150 * Um, Rect(vm, "flat").Min.V + Rect(vm, "flat").Size.V);

        var r = Item(vm, "up", "Expand Edges", "+Y Only");
        Assert.Contains("'wall'", r.Tip);
        r.Run!();
        Assert.Equal(80 * Um, Rect(vm, "up").Min.V + Rect(vm, "up").Size.V);
    }

    /// <summary>On a lumped port too: a refused one is reached from its row, becomes one the resolver accepts, and its status says so. A
    /// turned port's edges lie along no axis, so its items are off and say why.</summary>
    [Fact]
    public void AnUnconnectedLumpedPort_IsFixedInPlace_AndATurnedOneIsRefused()
    {
        var port = new C3dPort { Number = 1, Name = "P1", Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XZ, Offset = 100 * Um, Rect = new C3dRect { Min = new C3dPoint2(50 * Um, 120 * Um), Size = new C3dPoint2(100 * Um, 80 * Um) } };
        var vm = Open(new C3dDocument { Objects = [.. Plates()], Ports = [port] });
        var live = vm.Document.Ports[0];
        Settle(vm);
        // Refused, it has no sheet in the view: its row is the way to it.
        Assert.DoesNotContain(vm.Viewer.Scene.Objects, x => vm.PortOf(x) == live);
        var row = vm.Tree.SelectMany(g => g.Items).Single(i => i.Kind == "Port");
        vm.TreeMenuItems(row).Single(i => i.Header == "Expand Edges").Children!.Single(i => i.Header == "-Z Only").Run!();
        Assert.Equal((100 * Um, 100 * Um), (live.Rect.Min.V, live.Rect.Size.V));
        Assert.Contains("'low'", vm.StatusMessage);
        Assert.Contains("'high'", vm.StatusMessage);
        Assert.Null(C3dPorts.Resolve(live, vm.Document.DbuPerMicron, new C3dPortContext(vm.Elaboration!, vm.Document.DbuPerMicron, null, [])).Refusal);

        // Resolved, it has one, and the view's menu offers the same.
        Settle(vm);
        var o = vm.Viewer.Scene.Objects.Single(x => vm.PortOf(x) == live);
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([new Scene3DItem(o.Id, -1, default)]);
        Assert.Contains(vm.DrawMenuItems(), i => i.Header == "Contract Edges");

        live.Angle = 30;
        var off = vm.DrawMenuItems().Single(i => i.Header == "Expand Edges");
        Assert.False(off.Enabled);
        Assert.Contains("turned", off.Tip);

    }

    private C3dEditorViewModel Bound(string expression, params C3dVariable[] variables)
    {
        var sheet = Sheet("gap", 50, 120, 100, 80);
        sheet.Rect!.Exprs = new() { ["Min"] = [new C3dExpr("50", "Um"), new C3dExpr(expression, "Um")] };
        return Open(new C3dDocument { Objects = [.. Plates(), sheet], Variables = [.. variables] });
    }

    /// <summary>An expression of numbers alone holds no link to lose: it becomes the number, the other component keeps its own, and undo
    /// brings it back, all as one entry.</summary>
    [Fact]
    public void AnEdgeHoldingArithmetic_BecomesTheNumber_AsOneUndoEntry()
    {
        var vm = Bound("100+20");
        int entries = vm.UndoEntries;
        Item(vm, "gap", "Expand Edges", "-Z Only").Run!();
        var min = C3dBindings.SpecOf(typeof(C3dRect), nameof(C3dRect.Min))!;
        Assert.Equal((100 * Um, 100 * Um), (Rect(vm, "gap").Min.V, Rect(vm, "gap").Size.V));
        Assert.Null(C3dBindings.GetExpr(Rect(vm, "gap"), min, 1));
        Assert.NotNull(C3dBindings.GetExpr(Rect(vm, "gap"), min, 0));
        Assert.Equal(entries + 1, vm.UndoEntries);

        vm.UndoRedo.Undo();
        Assert.Equal("100+20", C3dBindings.GetExpr(Rect(vm, "gap"), min, 1)!.Expr);
    }

    /// <summary>An edge holding a variable takes the drag rule: the variable is written and stays bound. One no value can follow is refused
    /// with Replace with Number on offer, nothing moves, and the status says so rather than that it moved.</summary>
    [Fact]
    public void AnEdgeHoldingAVariable_TakesTheDragRule()
    {
        var vm = Bound("z", new C3dVariable { Name = "z", Expression = "120", Unit = "Um" });
        Item(vm, "gap", "Expand Edges", "-Z Only").Run!();
        Settle(vm);
        var min = C3dBindings.SpecOf(typeof(C3dRect), nameof(C3dRect.Min))!;
        Assert.Equal("z", C3dBindings.GetExpr(Rect(vm, "gap"), min, 1)!.Expr);
        Assert.Equal(100 * Um, Rect(vm, "gap").Min.V);
        Assert.Equal("100", vm.Document.Variables.Single(v => v.Name == "z").Expression);

        var stuck = Bound("a*a+20", new C3dVariable { Name = "a", Expression = "10" });
        Item(stuck, "gap", "Expand Edges", "-Z Only").Run!();
        Assert.Equal(120 * Um, Rect(stuck, "gap").Min.V);
        Assert.True(stuck.CanReplaceWithNumber);
        Assert.DoesNotContain("moves", stuck.StatusMessage);
    }
}
