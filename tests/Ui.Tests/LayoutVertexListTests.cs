using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

// Phase L1j — docs/sonnet-briefs/brief-L1j-properties-inspector.md §3, gates 8-12: the properties
// panel's editable vertex list — visibility, editing (one ReplaceShapeCommand per commit), holes, and
// the two silent virtualization traps (R-L1j-5/6) a 20,000-vertex polygon would otherwise expose.

public class LayoutVertexListTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(LayoutView model)
    {
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 8: visibility ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShownOnlyForASingle_Polygon_Curve_OrPath_HiddenForEverythingElse()
    {
        var model = FreshModel();
        var poly  = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 1000, 1000] };
        var curve = new CurveShape  { Layer = new LayerKey(1, 0), Xy = [5000, 0, 6000, 0, 5500, 1000] };
        var path  = new PathShape   { Layer = new LayerKey(1, 0), Xy = [10_000, 0, 11_000, 0], Width = 100 };
        var rect  = new RectShape   { Layer = new LayerKey(1, 0), X1 = 20_000, Y1 = 0, X2 = 21_000, Y2 = 1000 };
        var circle = new CircleShape { Layer = new LayerKey(1, 0), Cx = 30_000, Cy = 0, R = 500 };
        var label = new LabelShape  { Layer = new LayerKey(1, 0), X = 40_000, Y = 0, Text = "L", Height = 200 };
        model.Shapes.AddRange([poly, curve, path, rect, circle, label]);
        var (vm, props) = Setup(model);

        Click(vm, 300, 200);       Assert.True(props.ShowVertexList);  // Polygon
        Click(vm, 5500, 300);      Assert.True(props.ShowVertexList);  // Curve
        Click(vm, 10_500, 0);      Assert.True(props.ShowVertexList);  // Path
        Click(vm, 20_500, 500);    Assert.False(props.ShowVertexList); // Rect
        Click(vm, 30_000, 0);      Assert.False(props.ShowVertexList); // Circle
        Click(vm, 40_000, 100);    Assert.False(props.ShowVertexList); // Label

        vm.SelectAllCommand.Execute(null);
        Assert.False(props.ShowVertexList); // multi-selection
    }

    // ── Gate 9: vertex editing ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EditingARowsX_CommitsOneReplaceShapeCommand_UndoRestoresAtOriginalIndex_ListReflectsIt()
    {
        var model = FreshModel();
        var filler = new RectShape { Layer = new LayerKey(1, 0), X1 = 50_000, Y1 = 0, X2 = 51_000, Y2 = 1000 };
        model.Shapes.Add(filler); // index 0
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 1000, 1000] }; // index 1
        model.Shapes.Add(poly);
        var (vm, props) = Setup(model);

        Click(vm, 900, 200); // clearly inside the triangle both before and after the vertex-0 edit below
        Assert.Equal(1, Assert.Single(vm.SelectedIndices));
        Assert.True(props.ShowVertexList);

        // Row 0 = "Outer (3)" header, row 1 = vertex 0.
        var row = (VertexRowViewModel)props.VertexRows![1];
        Assert.Equal(0, row.VertexIndex);
        row.CommitX("500nm");

        Assert.False(row.HasError);
        Assert.Same(filler, model.Shapes[0]); // filler unaffected, still at index 0
        var updated = (PolygonShape)model.Shapes[1];
        Assert.NotSame(poly, updated); // ReplaceShapeCommand swapped in a new instance
        Assert.Equal(500, updated.Xy[0]);
        Assert.Equal(1, vm.UndoRedo.CanUndo ? 1 : 0);

        vm.UndoRedo.Undo();
        Assert.Same(poly, model.Shapes[1]); // restored — the EXACT original instance, at its original index
        Assert.Equal(0, poly.Xy[0]);

        // Redo, then confirm the list reflects the change again.
        vm.UndoRedo.Redo();
        Click(vm, 900, 200); // re-select — clearly inside the triangle both before and after the edit
        var refreshedRow = (VertexRowViewModel)props.VertexRows![1];
        Assert.Equal("0.5", refreshedRow.XText); // 500 dbu @ 1000 dbu/um = 0.5 um
    }

    [Fact]
    public void VertexRowInvalidCommit_ShowsErrorAndKeepsText_NoCommandPushed()
    {
        var model = FreshModel();
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 1000, 1000] };
        model.Shapes.Add(poly);
        var (vm, props) = Setup(model);

        Click(vm, 900, 200); // clearly inside the triangle both before and after the vertex-0 edit below
        var row = (VertexRowViewModel)props.VertexRows![1];
        row.CommitX("garbage");

        Assert.True(row.HasError);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Same(poly, model.Shapes[0]); // untouched
    }

    // ── Gate 10: holes ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HolesAreListed_BothRingGroupsShownWithCorrectCounts_EditingAHoleVertexWorks()
    {
        var model = FreshModel();
        var poly = new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000], // outer, 4 vertices
            Holes = [[2000, 2000, 4000, 2000, 4000, 4000]],        // 1 hole, 3 vertices
        };
        model.Shapes.Add(poly);
        var (vm, props) = Setup(model);

        Click(vm, 500, 500); // inside outer, outside hole
        Assert.True(props.ShowVertexList);

        // Row layout: 0=Outer(4) header, 1-4=outer vertices, 5=Hole 1(3) header, 6-8=hole vertices.
        Assert.Equal(9, props.VertexRows!.Count);
        Assert.IsType<RingHeaderRow>(props.VertexRows[0]);
        Assert.Equal("Outer (4)", ((RingHeaderRow)props.VertexRows[0]).Text);
        Assert.IsType<RingHeaderRow>(props.VertexRows[5]);
        Assert.Equal("Hole 1 (3)", ((RingHeaderRow)props.VertexRows[5]).Text);

        var holeRow0 = (VertexRowViewModel)props.VertexRows[6];
        Assert.Equal(0, holeRow0.Ring); // hole index 0 (outer is Ring = -1)
        Assert.Equal(0, holeRow0.VertexIndex);
        Assert.Equal("Line", holeRow0.EdgeText); // §3.1a — holes are always plain polygons

        holeRow0.CommitY("2500nm");
        Assert.False(holeRow0.HasError);

        var updated = (PolygonShape)model.Shapes[0];
        Assert.Equal(2500, updated.Holes![0][1]); // hole vertex 0's Y
        Assert.Equal(2000, updated.Holes[0][0]);  // X untouched
    }

    // ── Gate 11: virtualization holds (R-L1j-5/R-L1j-6) ────────────────────────────────────────

    [Fact]
    public void A20000VertexPolygon_MaterializesOnlyAccessedRows_OpensNearInstantly()
    {
        var model = FreshModel();
        const int vertexCount = 20_000;
        var xy = new long[vertexCount * 2];
        for (int i = 0; i < vertexCount; i++) { xy[2 * i] = i; xy[2 * i + 1] = i % 2; }
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = xy };
        model.Shapes.Add(poly);
        var (vm, props) = Setup(model);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.SelectAllCommand.Execute(null); // the only shape — selects it
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"selecting a 20,000-vertex polygon took {sw.ElapsedMilliseconds}ms — should be near-instant " +
            "if the row sequence isn't eagerly materialized");

        Assert.True(props.ShowVertexList);
        Assert.Equal(vertexCount + 1, props.VertexRows!.Count); // 1 header + 20,000 vertices
        Assert.Equal(0, props.VertexRows.MaterializedCount); // R-L1j-6: nothing built until accessed

        // Simulate a virtualizing panel realizing ~30 on-screen rows.
        for (int i = 0; i < 30; i++) _ = props.VertexRows[i];
        Assert.Equal(30, props.VertexRows.MaterializedCount); // stays in the tens, never the thousands
    }

    // ── Gate 12: a drag does not rebuild the list ──────────────────────────────────────────────

    [Fact]
    public void CanvasVertexDrag_NeverRebuildsTheRowsCollection_RealizedRowUpdatesLive()
    {
        var model = FreshModel();
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000] };
        model.Shapes.Add(poly);
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        Assert.True(props.ShowVertexList);
        var before = props.VertexRows;
        var row0 = (VertexRowViewModel)before![1]; // realize the row for vertex 0
        Assert.Equal("0", row0.XText);
        Assert.Equal("0", row0.YText);

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40); // grab vertex 0 (at the origin)
        vm.OnPointerMoved(3000, 4000, true, KeyModifiers.None, 40); // mid-drag, no release yet

        Assert.Same(before, props.VertexRows);   // R-L1j-6: same collection instance
        Assert.Same(row0, props.VertexRows![1]); // same row instance
        Assert.Equal("3", row0.XText);           // live, mid-drag
        Assert.Equal("4", row0.YText);

        vm.OnPointerReleased(3000, 4000, KeyModifiers.None);
        Assert.Same(before, props.VertexRows); // still unchanged after commit — vertex count didn't change
    }
}
