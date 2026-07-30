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
