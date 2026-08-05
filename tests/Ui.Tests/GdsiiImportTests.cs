using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates 9, 10, 11 (brief-L4a-gdsii-interchange.md): unit mismatch warns + offers refinement, import
/// creates real cell folders through the normal <see cref="CellFolder"/> machinery, and a crafted
/// cyclic GDSII imports without throwing or overflowing.
/// </summary>
public class GdsiiImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gdsii-import-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static MemoryStream BuildGdsii(IReadOnlyList<InterchangeStructure> structures, GdsiiUnits units)
    {
        var ms = new MemoryStream();
        GdsiiWriter.Write(ms, structures, units, null);
        ms.Position = 0;
        return ms;
    }

    // ── Gate 10: import creates real cells through the normal CellFolder machinery ───────────────

    [Fact]
    public void Import_MultiStructureLibrary_CreatesRealCellFoldersWithLayoutViews()
    {
        var child = new InterchangeStructure(
            "CHILD", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], []);
        var top = new InterchangeStructure(
            "TOP", [], [new LayoutInstance { CellRef = "CHILD", X = 500, Y = 500, Mag = 1.0 }]);

        using var stream = BuildGdsii([child, top], new GdsiiUnits(1e-6, 1e-9));
        var result = GdsiiImport.Import(stream, _dir, destTech: null, destDbuPerMicron: 1000, preferSourceResolution: false);

        Assert.False(result.Cancelled);
        Assert.Equal(2, result.CreatedCellDirs.Count);

        var childDir = Path.Combine(_dir, "CHILD");
        var topDir = Path.Combine(_dir, "TOP");
        Assert.True(Directory.Exists(childDir));
        Assert.True(Directory.Exists(topDir));

        var childCcell = CellPersistence.LoadFromFile(Path.Combine(childDir, CellFolder.CcellFileName));
        Assert.Equal("CHILD.clay", childCcell.PrimaryLayout);

        var topView = LayoutPersistence.LoadFromFile(
            Path.Combine(CellFolder.SubFolderPath(topDir, ViewType.Layout), "TOP.clay"));
        var inst = Assert.Single(topView.Instances);

        // The CellRef GdsiiImport computed must actually resolve back to CHILD's real cell folder.
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var resolved = Path.GetFullPath(Path.Combine(topLayoutDir, inst.CellRef));
        Assert.Equal(Path.GetFullPath(childDir), resolved);

        var childView = LayoutPersistence.LoadFromFile(
            Path.Combine(CellFolder.SubFolderPath(childDir, ViewType.Layout), "CHILD.clay"));
        Assert.Single(childView.Shapes);
    }

    [Fact]
    public void Import_StructureNameNotValidFilesystemName_MangledDeterministically_Reported()
    {
        var s = new InterchangeStructure("BAD?NAME", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }], []);
        using var stream = BuildGdsii([s], new GdsiiUnits(1e-6, 1e-9));
        var result = GdsiiImport.Import(stream, _dir, null, 1000, false);

        Assert.Single(result.CellNameByStructureName);
        var cellName = result.CellNameByStructureName["BAD?NAME"];
        Assert.True(Directory.Exists(Path.Combine(_dir, cellName)));
        Assert.Contains(result.Messages, m => m.Contains("BAD?NAME") && m.Contains(cellName));
    }

    // ── item 7/R-fix-6: top-level cell identification ───────────────────────────────────────────

    [Fact]
    public void Import_ChildReferencedByTop_TopIsTheOnlyTopLevelCell()
    {
        var child = new InterchangeStructure(
            "CHILD", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], []);
        var top = new InterchangeStructure(
            "TOP", [], [new LayoutInstance { CellRef = "CHILD", X = 0, Y = 0, Mag = 1.0 }]);

        using var stream = BuildGdsii([child, top], new GdsiiUnits(1e-6, 1e-9));
        var result = GdsiiImport.Import(stream, _dir, null, 1000, false);

        var topLevel = Assert.Single(result.TopLevelCellDirs);
        Assert.Equal("TOP", Path.GetFileName(topLevel));
    }

    [Fact]
    public void Import_NoHierarchy_EveryStructureIsItsOwnTopLevelCell()
    {
        var a = new InterchangeStructure("A", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }], []);
        var b = new InterchangeStructure("B", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }], []);

        using var stream = BuildGdsii([a, b], new GdsiiUnits(1e-6, 1e-9));
        var result = GdsiiImport.Import(stream, _dir, null, 1000, false);

        Assert.Equal(2, result.TopLevelCellDirs.Count);
    }

    [Fact]
    public void Import_MutualCycle_NoDistinctTopLevelCell_EmptyNotThrows()
    {
        var a = new InterchangeStructure("A", [], [new LayoutInstance { CellRef = "B", X = 0, Y = 0, Mag = 1.0 }]);
        var b = new InterchangeStructure("B", [], [new LayoutInstance { CellRef = "A", X = 0, Y = 0, Mag = 1.0 }]);
        using var stream = BuildGdsii([a, b], new GdsiiUnits(1e-6, 1e-9));

        var result = GdsiiImport.Import(stream, _dir, null, 1000, false);

        Assert.Empty(result.TopLevelCellDirs);
    }

    // ── item 7/R-fix-6: the completion-message helpers, tested directly (WorkspaceViewModel itself
    // cannot be constructed headlessly — see src/Ui/CLAUDE.md) ─────────────────────────────────────

    [Fact]
    public void FormatTruncatedNameList_ThreeOrFewer_ListsAllVerbatim()
    {
        Assert.Equal("\"TOP\", \"VIA_ARRAY\"",
            CircuitRF.Ui.ViewModels.WorkspaceViewModel.FormatTruncatedNameList(["TOP", "VIA_ARRAY"]));
    }

    [Fact]
    public void FormatTruncatedNameList_MoreThanThree_TruncatesWithCount()
    {
        var names = new List<string?> { "TOP", "VIA_ARRAY", "PAD", "M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9" };
        var text = CircuitRF.Ui.ViewModels.WorkspaceViewModel.FormatTruncatedNameList(names);
        Assert.Equal("\"TOP\", \"VIA_ARRAY\", \"PAD\", … (9 more)", text);
    }

    [Fact]
    public void DescribeTopLevelCells_Single_NamesIt()
    {
        var text = CircuitRF.Ui.ViewModels.WorkspaceViewModel.DescribeTopLevelCells(["/ws/TOP"]);
        Assert.Equal("Top-level cell: \"TOP\".", text);
    }

    [Fact]
    public void DescribeTopLevelCells_None_ExplainsAmbiguity_NeverGuesses()
    {
        var text = CircuitRF.Ui.ViewModels.WorkspaceViewModel.DescribeTopLevelCells([]);
        Assert.Equal("No distinct top-level cell — every structure is referenced by another.", text);
    }

    [Fact]
    public void DescribeTopLevelCells_Multiple_ListsThem()
    {
        var text = CircuitRF.Ui.ViewModels.WorkspaceViewModel.DescribeTopLevelCells(["/ws/A", "/ws/B"]);
        Assert.Equal("Top-level cells: \"A\", \"B\".", text);
    }

    [Fact]
    public void DescribeTopLevelCells_ADeviceLibrarysManyTops_AreTruncated_NotAllListed()
    {
        // C1: many tops is what a device LIBRARY looks like — every primitive is its own top, and
        // only the via arrays and corner pieces are referenced by anything (a real one measured 46 of
        // 56). Listing all of them buries the counts earlier in the same message.
        var dirs = Enumerable.Range(0, 46).Select(i => $"/ws/dev{i}").ToArray();

        var text = CircuitRF.Ui.ViewModels.WorkspaceViewModel.DescribeTopLevelCells(dirs);

        Assert.Equal("Top-level cells: \"dev0\", \"dev1\", \"dev2\", … (43 more).", text);
    }

    // ── Gate 9: unit mismatch ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Import_SourceFinerThanDestination_WarnsWithAffectedCoordinateCount()
    {
        // Source DBU = 1e-9 m (1000 DBU/µm); destination requested at 100 DBU/µm (coarser, 10x) —
        // a coordinate of 5 source-DBU does not divide evenly by the 1/10 ratio.
        var shape = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1005, Y2 = 500 };
        using var stream = BuildGdsii([new InterchangeStructure("TOP", [shape], [])], new GdsiiUnits(1e-6, 1e-9));

        var result = GdsiiImport.Import(stream, _dir, null, destDbuPerMicron: 100, preferSourceResolution: false);

        Assert.Contains(result.Messages, m => m.Contains("coordinate(s) will round"));
    }

    [Fact]
    public void Import_SourceCoarserThanDestination_Silent_NoRoundingWarning()
    {
        // Destination finer (refinement direction) — always lossless, per §2.2.
        var shape = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 };
        using var stream = BuildGdsii([new InterchangeStructure("TOP", [shape], [])], new GdsiiUnits(1e-6, 1e-8)); // 100 DBU/µm source

        var result = GdsiiImport.Import(stream, _dir, null, destDbuPerMicron: 1000, preferSourceResolution: false);

        Assert.DoesNotContain(result.Messages, m => m.Contains("will round"));
    }

    [Fact]
    public void Import_PreferSourceResolution_CreatesLayoutAtSourceResolution_LosslessNoWarning()
    {
        var shape = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1005, Y2 = 500 };
        using var stream = BuildGdsii([new InterchangeStructure("TOP", [shape], [])], new GdsiiUnits(1e-6, 1e-9));

        var result = GdsiiImport.Import(stream, _dir, null, destDbuPerMicron: 100, preferSourceResolution: true);

        Assert.DoesNotContain(result.Messages, m => m.Contains("will round"));
        var view = LayoutPersistence.LoadFromFile(
            Path.Combine(CellFolder.SubFolderPath(Path.Combine(_dir, "TOP"), ViewType.Layout), "TOP.clay"));
        Assert.Equal(1000, view.DbuPerMicron); // matches the source's own 1000 DBU/µm exactly
        var poly = Assert.IsType<PolygonShape>(Assert.Single(view.Shapes)); // GDSII has no Rect primitive
        Assert.Equal(1005, poly.Xy[2]); // exact — no rounding at all
    }

    // ── Gate 11: cycle safety ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Import_MutualCycle_DoesNotThrow_AndResolvedCellRefsFormACycle()
    {
        var a = new InterchangeStructure("A", [], [new LayoutInstance { CellRef = "B", X = 0, Y = 0, Mag = 1.0 }]);
        var b = new InterchangeStructure("B", [], [new LayoutInstance { CellRef = "A", X = 0, Y = 0, Mag = 1.0 }]);
        using var stream = BuildGdsii([a, b], new GdsiiUnits(1e-6, 1e-9));

        GdsiiImport.ImportResult result = null!;
        var ex = Record.Exception(() => result = GdsiiImport.Import(stream, _dir, null, 1000, false));
        Assert.Null(ex);
        Assert.False(result.Cancelled);

        var aDir = Path.Combine(_dir, "A");
        var bDir = Path.Combine(_dir, "B");
        var aView = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(aDir, ViewType.Layout), "A.clay"));
        var bView = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(bDir, ViewType.Layout), "B.clay"));

        // Both CellRefs must resolve back to the correct SIBLING cell folder — a genuine cycle, not a
        // silently-broken reference — and walking it must not throw or hang (relying on the existing
        // CellHierarchy.ResolveForWalk visiting-set + MaxDepth guard, never a second cycle detector).
        var aInst = Assert.Single(aView.Instances);
        var bInst = Assert.Single(bView.Instances);
        Assert.Equal(Path.GetFullPath(bDir), Path.GetFullPath(Path.Combine(CellFolder.SubFolderPath(aDir, ViewType.Layout), aInst.CellRef)));
        Assert.Equal(Path.GetFullPath(aDir), Path.GetFullPath(Path.Combine(CellFolder.SubFolderPath(bDir, ViewType.Layout), bInst.CellRef)));

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(aDir) };
        var walkEx = Record.Exception(() => CellHierarchy.ResolveForWalk(aInst, CellFolder.SubFolderPath(aDir, ViewType.Layout), visiting, 1));
        Assert.Null(walkEx);
    }


    // ── C1 ↔ C2: imported artwork arrives connectable ───────────────────────────

    /// <summary>
    /// GDSII carries no pin record, so an imported cell's connectivity has to be RECOVERED from the
    /// drawing. Import now runs the same inference C2 built and writes the result to the cell's own
    /// pin list — the join that turns imported device artwork from inert geometry into something a
    /// design can connect to.
    /// </summary>
    [Fact]
    public void Import_RecoversPinsFromArtwork_AndWritesThemToTheCellsPinList()
    {
        // A technology that says which purpose means "pin" — inference reads this, and with no
        // technology nothing is a pin (guessing from the layer number would make every shape one).
        var tech = new Technology
        {
            Name = "T",
            Layers =
            {
                new LayerDef { Key = new LayerKey(1, 0), Name = "M1.drawing", Purpose = "drawing" },
                new LayerDef { Key = new LayerKey(1, 2), Name = "M1.pin",     Purpose = "pin" },
            },
        };

        // Two pin boxes on the pin-purpose layer, at opposite ends of a wide cell, each with its own
        // label inside it — the shape real vendor artwork takes.
        var cell = new InterchangeStructure("DEV",
        [
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0,    Y1 = 0,   X2 = 2000, Y2 = 400 },
            new RectShape { Layer = new LayerKey(1, 2), X1 = 0,    Y1 = 150, X2 = 200,  Y2 = 250 },
            new RectShape { Layer = new LayerKey(1, 2), X1 = 1800, Y1 = 150, X2 = 2000, Y2 = 250 },
            new LabelShape { Layer = new LayerKey(1, 2), X = 100,  Y = 200, Text = "A", Height = 100 },
            new LabelShape { Layer = new LayerKey(1, 2), X = 1900, Y = 200, Text = "B", Height = 100 },
        ], []);

        using var stream = BuildGdsii([cell], new GdsiiUnits(1e-6, 1e-9));
        var result = GdsiiImport.Import(stream, _dir, tech, destDbuPerMicron: 1000, preferSourceResolution: false);

        Assert.False(result.Cancelled);
        string cellDir = Assert.Single(result.CreatedCellDirs);
        var view = LayoutPersistence.LoadFromFile(
            Directory.GetFiles(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "*.clay").Single());

        Assert.Equal(2, view.Pins.Count);

        // Both terminals were named — the cell labels systematically, so the assignment is accepted.
        Assert.Equal(["A", "B"], view.Pins.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        // And each carries the two things a port label alone could never have: a connecting width and
        // an outward direction, facing opposite ways out of a cell five times wider than it is tall.
        Assert.All(view.Pins, p => Assert.True(p.WidthDbu > 0));
        var facing = view.Pins.OrderBy(p => p.X).Select(p => p.OutwardDeg).ToArray();
        Assert.NotEqual(facing[0], facing[1]);

        Assert.Contains(result.Messages, m => m.Contains("Recovered 2 pin(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// Artwork with nothing pin-shaped in it imports with no pins and says nothing about them — a
    /// cell that declares no connection points is the ordinary case for plain drawing, not a defect.
    /// </summary>
    [Fact]
    public void Import_ArtworkWithNoPinLayer_ImportsWithNoPins_AndReportsNothingAboutThem()
    {
        var tech = new Technology
        {
            Name = "T",
            Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "M1.drawing", Purpose = "drawing" } },
        };
        var cell = new InterchangeStructure("PLAIN",
            [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], []);

        using var stream = BuildGdsii([cell], new GdsiiUnits(1e-6, 1e-9));
        var result = GdsiiImport.Import(stream, _dir, tech, destDbuPerMicron: 1000, preferSourceResolution: false);

        string cellDir = Assert.Single(result.CreatedCellDirs);
        var view = LayoutPersistence.LoadFromFile(
            Directory.GetFiles(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "*.clay").Single());

        Assert.Empty(view.Pins);
        Assert.DoesNotContain(result.Messages, m => m.Contains("pin(s)", StringComparison.Ordinal));
    }
}
