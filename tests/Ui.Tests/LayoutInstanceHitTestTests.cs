using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a gate 10 — R-L3a-5: clicking sub-cell geometry selects the instance; clicking empty
//  space inside the instance's bbox does not.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstanceHitTestTests : IDisposable
{
    private readonly string _workspaceDir;

    public LayoutInstanceHitTestTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstHitTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    /// <summary>A cell whose layout is a small rect near one CORNER of a much larger nominal bbox —
    /// so "inside the bbox but not on the geometry" is a real, testable region.</summary>
    private string CreateSparseCell(string name)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    [Fact]
    public void HitInstanceStack_ClickOnSubCellGeometry_SelectsInstance()
    {
        CreateSparseCell("Sparse");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "Sparse", X = 10_000, Y = 10_000, Mag = 1.0 });

        // Rect occupies local [0,100] -> world [10000,10100].
        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 10_050, 10_050, tolDbu: 5);
        Assert.Equal([0], hits);
    }

    [Fact]
    public void HitInstanceStack_ClickOnEmptySpaceInsideOverallExtent_DoesNotSelect()
    {
        CreateSparseCell("Sparse");
        var view = new LayoutView { DbuPerMicron = 1000 };
        // Array spreads the small rect far apart — the CENTER of the overall array bbox is empty space.
        var inst = new LayoutInstance { CellRef = "Sparse", X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 3, PitchX = 100_000 };
        view.Instances.Add(inst);

        // Rects land at local [0,100] shifted by col*PitchX: col0=[0,100], col1=[100000,100100],
        // col2=[200000,200100] — 50000 sits squarely between col0 and col1, far from every rect.
        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 50_000, 50, tolDbu: 5);
        Assert.Empty(hits);
    }

    [Fact]
    public void HitInstanceStack_ClickOnSecondArrayCell_Selects()
    {
        CreateSparseCell("Sparse");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "Sparse", X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 3, PitchX = 100_000 });

        // Second array cell's rect: local [0,100] shifted by PitchX*1 = 100000.
        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 100_050, 50, tolDbu: 5);
        Assert.Equal([0], hits);
    }

    [Fact]
    public void HitInstanceStack_BrokenReference_WholeBboxIsClickTarget()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "GhostCell", X = 5_000, Y = 5_000, Mag = 1.0 });

        // Anywhere inside the placeholder half-extent counts (no real geometry to be more precise about).
        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 5_000, 5_000, tolDbu: 5);
        Assert.Equal([0], hits);
    }

    [Fact]
    public void HitInstanceStack_Rotated90_HitTestsCorrectFootprint()
    {
        CreateSparseCell("Sparse"); // local rect [0,0]-[100,100]
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "Sparse", X = 0, Y = 0, Rot = LayoutRotation.R90, Mag = 1.0 });

        // R90 of local (50,50) -> world (-50, 50) (matches LayoutInstanceTransformTests' table).
        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, -50, 50, tolDbu: 5);
        Assert.Equal([0], hits);

        var miss = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 50, -50, tolDbu: 5);
        Assert.Empty(miss);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Owner report, 2026-09-04: with many instances of a complex design placed in one .clay,
    //  clicking the layout canvas — or starting a marquee, whose press runs the same pick stack —
    //  took many seconds. Measured on the reported file (a 20x20 array of a 3,284-shape board):
    //  1,063 ms for ONE click anywhere inside the array's extent, and a MISS paid the full bill.
    //
    //  TWO multiplications, both removed, both gated below rather than by a stopwatch (a wall-clock
    //  assertion would measure the machine):
    //    * every array cell was descended into, unconditionally — Rows*Cols per instance, per level;
    //    * inside each one, the sub-cell's WHOLE shape list ran the exact per-shape test, which
    //      flattens polygons and curves, instead of the spatial index's candidates.
    //  Together: 400 x 3,284 flatten-and-test operations to answer one click.
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A click descends into the array cells it could actually be in — not all of them.
    /// This is the structural property; the second and third assertions are what make it a gate
    /// rather than a restatement, since "correct answer" alone was already true before.</summary>
    [Fact]
    public void HitInstanceStack_LargeArray_DescendsIntoOnlyTheCellsUnderTheClick()
    {
        CreateSparseCell("Sparse"); // local rect [0,0]-[100,100]
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = "Sparse", X = 0, Y = 0, Mag = 1.0,
            Rows = 20, Cols = 20, PitchX = 100_000, PitchY = 100_000,
        });

        // On the geometry of one cell deep inside the array (row 13, col 7).
        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir,
            7 * 100_000 + 50, 13 * 100_000 + 50, tolDbu: 5, out var onGeometry);
        Assert.Equal([0], hits);
        Assert.Equal(1, onGeometry.ArrayCellsDescended);

        // Blank space between cells: the answer is "no hit", and it must cost nothing to reach — the
        // 400-cell walk used to pay its whole cost precisely on the miss.
        var miss = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir,
            7 * 100_000 + 50_000, 13 * 100_000 + 50_000, tolDbu: 5, out var blank);
        Assert.Empty(miss);
        Assert.Equal(0, blank.ArrayCellsDescended);

        // And the array's SIZE is not what the click costs: 60x60 is nine times the placements and
        // the same one descent.
        view.Instances[0].Rows = 60;
        view.Instances[0].Cols = 60;
        view.NotifyChanged(LayoutChangeInfo.Full);
        LayoutHitTest.HitInstanceStack(view, null, _workspaceDir,
            7 * 100_000 + 50, 13 * 100_000 + 50, tolDbu: 5, out var bigger);
        Assert.Equal(1, bigger.ArrayCellsDescended);
    }

    /// <summary>Reaching a shape THROUGH an instance costs what reaching it directly on the canvas
    /// costs: the sub-cell's own spatial index decides which shapes run the exact test, so a cell's
    /// shape COUNT is not what a click into it costs.</summary>
    [Fact]
    public void HitInstanceStack_DenseCell_TestsIndexCandidatesRatherThanEveryShape()
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, "Dense");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var cell = new LayoutView { DbuPerMicron = 1000 };
        for (int i = 0; i < 500; i++)                       // a row of well-separated rects
            cell.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = i * 1_000, Y1 = 0, X2 = i * 1_000 + 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), cell);

        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "Dense", X = 0, Y = 0, Mag = 1.0 });

        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 250_050, 50, tolDbu: 5, out var counters);
        Assert.Equal([0], hits);
        Assert.True(counters.ShapeTestsRun < 10,
            $"the index should hand the exact test a handful of candidates, not the cell's 500 shapes (got {counters.ShapeTestsRun})");
    }

    [Fact]
    public void HitInstanceStack_TopmostIsLastInList()
    {
        CreateSparseCell("Sparse");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "Sparse", X = 0, Y = 0, Mag = 1.0 });
        view.Instances.Add(new LayoutInstance { CellRef = "Sparse", X = 0, Y = 0, Mag = 1.0 }); // same footprint, drawn later

        var hits = LayoutHitTest.HitInstanceStack(view, null, _workspaceDir, 50, 50, tolDbu: 5);
        Assert.Equal([1, 0], hits);
    }
}
