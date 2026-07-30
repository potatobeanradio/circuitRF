using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a — CellHierarchy: bbox computation (incl. arrays and recursion) and R-L3a-2's three
//  cycle-rejection enforcement points (edit time here; load/render time in
//  LayoutInstanceRendererTests.cs, since that needs the compiled-geometry path).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CellHierarchyTests : IDisposable
{
    private readonly string _workspaceDir;

    public CellHierarchyTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfHierarchyTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private string CreateCellWithRect(string name, long x1, long y1, long x2, long y2)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    private string CreateEmptyCellWithInstance(string name, string instanceCellRef, long instX, long instY, LayoutRotation rot = LayoutRotation.R0)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = instanceCellRef, X = instX, Y = instY, Rot = rot, Mag = 1.0, Rows = 1, Cols = 1 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    // ── Bbox ──────────────────────────────────────────────────────────────────

    [Fact]
    public void InstanceBbox_SimpleResolvedCell_MatchesShapeBboxTranslated()
    {
        CreateCellWithRect("Via", 0, 0, 1000, 2000);
        var inst = new LayoutInstance { CellRef = "Via", X = 5000, Y = 6000, Mag = 1.0 };

        var bbox = CellHierarchy.InstanceBbox(inst, _workspaceDir);

        Assert.Equal(new Bbox(5000, 6000, 6000, 8000), bbox);
    }

    [Fact]
    public void InstanceBbox_Array_ExpandsAcrossRowsAndCols()
    {
        CreateCellWithRect("Via", 0, 0, 100, 100);
        var inst = new LayoutInstance { CellRef = "Via", X = 0, Y = 0, Mag = 1.0, Rows = 3, Cols = 5, PitchX = 500, PitchY = 700 };

        var bbox = CellHierarchy.InstanceBbox(inst, _workspaceDir);

        // Base cell [0,100]; array extends +4*500 in X (5 cols) and +2*700 in Y (3 rows).
        Assert.Equal(new Bbox(0, 0, 100 + 4 * 500, 100 + 2 * 700), bbox);
    }

    [Fact]
    public void InstanceBbox_Rotated90_SwapsWidthAndHeight()
    {
        CreateCellWithRect("Rect", 0, 0, 1000, 200); // wide, short
        var inst = new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Rot = LayoutRotation.R90, Mag = 1.0 };

        var bbox = CellHierarchy.InstanceBbox(inst, _workspaceDir);
        Assert.Equal(200, bbox.MaxX - bbox.MinX);
        Assert.Equal(1000, bbox.MaxY - bbox.MinY);
    }

    [Fact]
    public void InstanceBbox_Magnified2x_DoublesExtent()
    {
        CreateCellWithRect("Rect", 0, 0, 1000, 500);
        var inst = new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Mag = 2.0 };

        var bbox = CellHierarchy.InstanceBbox(inst, _workspaceDir);
        Assert.Equal(2000, bbox.MaxX - bbox.MinX);
        Assert.Equal(1000, bbox.MaxY - bbox.MinY);
    }

    [Fact]
    public void InstanceBbox_NestedInstance_RecursesOneLevel()
    {
        CreateCellWithRect("Leaf", 0, 0, 100, 100);
        CreateEmptyCellWithInstance("Middle", "../../Leaf", 0, 0);
        var inst = new LayoutInstance { CellRef = "Middle", X = 1000, Y = 2000, Mag = 1.0 };

        var bbox = CellHierarchy.InstanceBbox(inst, _workspaceDir);

        Assert.Equal(new Bbox(1000, 2000, 1100, 2100), bbox);
    }

    [Fact]
    public void InstanceBbox_UnresolvedCell_ReturnsPlaceholderBbox_NotEmpty()
    {
        var inst = new LayoutInstance { CellRef = "GhostCell", X = 500, Y = 500, Mag = 1.0 };
        var bbox = CellHierarchy.InstanceBbox(inst, _workspaceDir);
        Assert.False(bbox.IsEmpty);
        Assert.True(bbox.Contains(500, 500));
    }

    // ── Depth cap (R-L3a-2, render/load-time backstop) ───────────────────────────

    [Fact]
    public void InstanceBbox_ChainDeeperThanMaxDepth_TerminatesWithoutOverflow()
    {
        // Build a straight chain of 40 cells, each instancing the next — deeper than
        // CellHierarchy.MaxDepth (32). Must terminate (no StackOverflowException) and produce SOME
        // bbox (the depth-exceeded backstop treats it as broken past the cap, not as nothing at all).
        const int chainLength = 40;
        string PrevRef(int i) => $"../../Cell{i - 1}";

        CreateCellWithRect("Cell0", 0, 0, 10, 10);
        for (int i = 1; i < chainLength; i++)
            CreateEmptyCellWithInstance($"Cell{i}", PrevRef(i), 0, 0);

        var rootInst = new LayoutInstance { CellRef = $"Cell{chainLength - 1}", X = 0, Y = 0, Mag = 1.0 };

        var exception = Record.Exception(() => CellHierarchy.InstanceBbox(rootInst, _workspaceDir));
        Assert.Null(exception);
    }

    // ── Edit-time cycle rejection (R-L3a-2) ──────────────────────────────────────

    /// <summary>A resolvable-but-empty cell — WouldCreateCycle needs the CANDIDATE to actually
    /// resolve (a bare CellFolder.CreateCellFolder with no .clay at all leaves it PrimaryMissing,
    /// "nothing real to cycle through," which is a different, also-tested case below).</summary>
    private string CreateEmptyResolvableCell(string name)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), new LayoutView { DbuPerMicron = 1000 });
        return cellDir;
    }

    [Fact]
    public void WouldCreateCycle_DirectSelfReference_ReturnsTrue()
    {
        var cellDir = CreateEmptyResolvableCell("Self");
        Assert.True(CellHierarchy.WouldCreateCycle(cellDir, "Self", _workspaceDir));
    }

    [Fact]
    public void WouldCreateCycle_TransitiveCycle_AtoBtoA_ReturnsTrue()
    {
        // A already references B (A -> B). Adding B -> A would close the cycle.
        var cellA = CreateEmptyResolvableCell("A");
        CreateEmptyCellWithInstance("B", "../../A", 0, 0);

        // WouldCreateCycle(currentLayoutAbsDir = B's cell dir, candidateCellRef = "../A" from B's own base).
        string bBaseDir = CellFolder.SubFolderPath(Path.Combine(_workspaceDir, "B"), ViewType.Layout);
        bool wouldCycle = CellHierarchy.WouldCreateCycle(cellA, "../../A", bBaseDir);
        Assert.True(wouldCycle);
    }

    [Fact]
    public void WouldCreateCycle_NoRelationship_ReturnsFalse()
    {
        var cellA = CreateEmptyResolvableCell("A");
        CreateEmptyResolvableCell("B"); // resolvable, but has no instances at all — cannot reach A
        Assert.False(CellHierarchy.WouldCreateCycle(cellA, "B", _workspaceDir));
    }

    [Fact]
    public void WouldCreateCycle_UnresolvableCandidate_ReturnsFalse_NothingToCycleThrough()
    {
        var cellA = CellFolder.CreateCellFolder(_workspaceDir, "A");
        Assert.False(CellHierarchy.WouldCreateCycle(cellA, "NoSuchCell", _workspaceDir));
    }

    [Fact]
    public void WouldCreateCycle_NullCurrentDir_ReturnsFalse_ScratchDocumentCannotCycle()
    {
        Assert.False(CellHierarchy.WouldCreateCycle(null, "AnythingAtAll", _workspaceDir));
    }
}
