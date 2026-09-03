using CircuitRF.Ui.Archive;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  MW2 — referencing a cell in another workspace
//  (docs/sonnet-briefs/brief-multi-workspace-2-external-cell-refs.md §9).
//
//  Every test here builds TWO real workspaces on disk, because the whole feature is
//  about what happens across the boundary between them: an in-memory double would
//  agree with itself about a resolution rule the filesystem is the authority on.
//
//  The alias table is memoised per workspace root (it is asked per cell instance per
//  render), so every fixture drops that memo before it asserts — a test that wrote a
//  .cws after some earlier test had already asked about that path would otherwise
//  read the stale answer.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class ExternalCellReferenceTests : IDisposable
{
    private readonly string _root;
    private readonly string _wsA;
    private readonly string _wsB;

    public ExternalCellReferenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_mw2_" + Guid.NewGuid().ToString("N")[..8]);
        _wsA  = Path.Combine(_root, "workspaceA");
        _wsB  = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(_wsA);
        Directory.CreateDirectory(_wsB);
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        PdkKitRegistry.ClearWorkspace(_wsA);
        PdkKitRegistry.ClearWorkspace(_wsB);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static void WriteCws(string root, Action<CwsFile>? edit = null)
    {
        var cws = new CwsFile();
        edit?.Invoke(cws);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
    }

    /// <summary>Writes one <c>.ctech</c> at <paramref name="root"/> and makes it that workspace's
    /// default — the technology §3's gate compares.</summary>
    private static string WriteTechnology(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        TechPersistence.SaveToFile(path, StarterTechnologies.Pcb2Layer());
        return path;
    }

    /// <summary>A cell with a two-pin primary symbol, a declared parameter, a primary schematic and a
    /// primary layout carrying one rectangle — enough for the symbol, the pins, the published
    /// interface, push-in and the rendered geometry to all be asserted on one fixture.</summary>
    private static string CreateCell(string workspaceRoot, string name, string? techRelFrom = null)
    {
        string cellDir = CellFolder.CreateCellFolder(workspaceRoot, name);

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"), TwoPinSymbol());

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = 2;
        ccell.Parameters.Add(new CcellParameter { Name = "W", DefaultExpression = "10u", ShowOnSchematic = true });
        CellPersistence.SaveToFile(ccellPath, ccell);

        var schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        SchematicPersistence.SaveToFile(Path.Combine(schDir, name + ".csch"), new SchematicEditModel());

        var layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        if (techRelFrom is not null) view.TechRef = Path.GetRelativePath(layDir, techRelFrom);
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 2000 });
        LayoutPersistence.SaveToFile(Path.Combine(layDir, name + ".clay"), view);

        return cellDir;
    }

    private static void SaveSchematicWithRef(string cellDir, string cellRef)
    {
        var schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = cellRef });
        SchematicPersistence.SaveToFile(Path.Combine(schDir, Path.GetFileName(cellDir) + ".csch"), model);
    }

    private static void SaveLayoutWithRef(string cellDir, string cellRef)
    {
        var layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(layDir, Path.GetFileName(cellDir) + ".clay"), view);
    }

    /// <summary>A and B, sharing ONE technology file (§3's precondition), with B referencing A's
    /// "Amp" through the alias "A". Returns B's referring cell folder.</summary>
    private string BuildSharedTechFixture(string alias = "A")
    {
        string tech = WriteTechnology(_root, "shared.ctech");

        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, tech));
        WriteCws(_wsB, c =>
        {
            c.DefaultTechRef = Path.GetRelativePath(_wsB, tech);
            c.ReferencedWorkspaces =
                [new CwsWorkspaceRef { Alias = alias, Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) }];
        });

        CreateCell(_wsA, "Amp");
        string top = CreateCell(_wsB, "Board");
        SaveSchematicWithRef(top, ExternalCellRef.RefFor(alias, "Amp"));
        SaveLayoutWithRef(top, ExternalCellRef.RefFor(alias, "Amp"));
        WorkspaceRootFinder.InvalidateCache();
        return top;
    }

    // ── §9.1 — the reference resolves, all four ways ──────────────────────────

    [Fact]
    public void ExternalReference_ResolvesSymbolPinsInterfaceAndPushIn()
    {
        string top = BuildSharedTechFixture();
        string schDir = CellFolder.SubFolderPath(top, ViewType.Schematic);
        string cellRef = ExternalCellRef.RefFor("A", "Amp");

        var symbol = CellSymbolResolver.Resolve(cellRef, schDir);
        Assert.Equal(CellSymbolState.Resolved, symbol.State);
        Assert.Equal(2, symbol.Symbol!.Pins.Count);

        var ccell = CellSymbolResolver.ResolveCcell(cellRef, schDir);
        Assert.NotNull(ccell);
        Assert.Contains(ccell!.Parameters, p => p.Name == "W");

        // Push-in: the same resolution the elaborator's descent uses, since both go through
        // HierarchyResolver.
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(schDir, "Board.csch"));
        model.SchematicDirectory = schDir;
        Assert.True(HierarchyResolver.CanPushInto(model.Components[0], model, out string? why), why);

        // And the layout half, which is what the renderer walks.
        string layDir = CellFolder.SubFolderPath(top, ViewType.Layout);
        var layout = CellLayoutResolver.Resolve(cellRef, layDir);
        Assert.Equal(CellLayoutState.Resolved, layout.State);
        Assert.Single(layout.View!.Shapes);
    }

    [Fact]
    public void ExternalReference_RendersThroughTheSharedLayerTable()
    {
        BuildSharedTechFixture();

        // §3's whole premise: a layout's hierarchy is compiled against ONE technology, so what the
        // external cell's shapes are drawn with is the HOST's table. The gate is that both sides
        // resolve to the same .ctech file, which is what makes that safe.
        var cache = new TechnologyCache();
        var (a, _) = TechnologyResolver.ResolveForDocument(
            null, Path.Combine(_wsA, "Amp", "layout", "Amp.clay"), null, cache);
        var (b, _) = TechnologyResolver.ResolveForDocument(
            null, Path.Combine(_wsB, "Board", "layout", "Board.clay"), null, cache);

        Assert.NotNull(a.ResolvedPath);
        Assert.Equal(Path.GetFullPath(a.ResolvedPath!), Path.GetFullPath(b.ResolvedPath!));
    }

    // ── §9.2 — the technology refusal ─────────────────────────────────────────

    [Fact]
    public void DifferentTechnologies_RefuseTheReference_NamingBoth()
    {
        string techA = WriteTechnology(_wsA, "processA.ctech");
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));

        var check = ExternalWorkspaceGate.CheckWorkspaceTechnology(_wsB, _wsA, new TechnologyCache());

        Assert.False(check.Permitted);
        Assert.Contains("processA.ctech", check.Refusal);
        Assert.Contains("processB.ctech", check.Refusal);
        Assert.Contains("workspaceA", check.Refusal);
        Assert.Contains("workspaceB", check.Refusal);
    }

    [Fact]
    public void CheckCellTechnology_ComparesTheHostsRESOLVEDTechnology_NotItsWorkspaceDefault()
    {
        // A .clay may deviate from its workspace default by carrying its own TechRef, so the
        // placement gate asks the renderer's own answer rather than re-deriving one.
        string shared = WriteTechnology(_root, "shared.ctech");
        string other  = WriteTechnology(_root, "other.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, shared));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, shared));
        CreateCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        Assert.True(ExternalWorkspaceGate
            .CheckCellTechnology(shared, _wsB, Path.Combine(_wsA, "Amp")).Permitted);

        var refused = ExternalWorkspaceGate.CheckCellTechnology(other, _wsB, Path.Combine(_wsA, "Amp"));
        Assert.False(refused.Permitted);
        Assert.Contains("other.ctech", refused.Refusal);
        Assert.Contains("shared.ctech", refused.Refusal);
    }

    [Fact]
    public void MakeCellRef_PrefersTheDEEPESTReferencedWorkspace_WhenTwoNest()
    {
        // A delivery folder that is itself a workspace, holding a project workspace inside it. The
        // cell belongs to the inner one, which is the same "nearest ancestor" rule the .cws walk-up
        // uses — and the answer must not depend on the alias table's iteration order.
        string outerRoot = Path.Combine(_root, "delivery");
        string innerRoot = Path.Combine(outerRoot, "project");
        Directory.CreateDirectory(innerRoot);
        WriteCws(outerRoot);
        WriteCws(innerRoot);
        CreateCell(innerRoot, "Amp");

        WriteCws(_wsB, c => c.ReferencedWorkspaces =
        [
            new CwsWorkspaceRef { Alias = "Outer", Path = Path.GetRelativePath(_wsB, Path.Combine(outerRoot, ".cws")) },
            new CwsWorkspaceRef { Alias = "Inner", Path = Path.GetRelativePath(_wsB, Path.Combine(innerRoot, ".cws")) },
        ]);
        string board = CreateCell(_wsB, "Board");
        WorkspaceRootFinder.InvalidateCache();

        string made = ExternalCellRef.MakeCellRef(
            CellFolder.SubFolderPath(board, ViewType.Schematic), Path.Combine(innerRoot, "Amp"));

        Assert.Equal(ExternalCellRef.RefFor("Inner", "Amp"), made);
    }

    [Fact]
    public void SameTechnology_PermitsTheReference()
    {
        string tech = WriteTechnology(_root, "shared.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, tech));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, tech));

        Assert.True(ExternalWorkspaceGate.CheckWorkspaceTechnology(_wsB, _wsA, new TechnologyCache()).Permitted);
    }

    // ── §9.3 — the three kit rules ────────────────────────────────────────────

    [Fact]
    public void R_mw2_8_KitPartResolves_WhenItsOwnWorkspaceHasItMounted()
    {
        BuildSharedTechFixture();
        string ampSch = Path.Combine(_wsA, "Amp", "schematic");
        SaveSchematicWithRef(Path.Combine(_wsA, "Amp"), PdkKitRegistry.RefFor("KitOne", "P1"));

        // A mounts the kit in ITS OWN scope; B never does. The reference still resolves, because a
        // pdk:// reference resolves against the referencing document's own parent workspace (R-mw1-5).
        PdkKitRegistry.SetKit(_wsA, "KitOne", [MakePart("P1")]);

        var res = CellSymbolResolver.Resolve(PdkKitRegistry.RefFor("KitOne", "P1"), ampSch);
        Assert.Equal(CellSymbolState.Resolved, res.State);

        Assert.Equal(ExternalCellState.Resolved,
            ExternalCellStatusResolver.Classify(
                ExternalCellRef.RefFor("A", "Amp"),
                Path.Combine(_wsB, "Board", "schematic")).State);
    }

    [Fact]
    public void R_mw2_9_KitPartIsUnresolved_WhenItsWorkspaceIsNotOpen()
    {
        BuildSharedTechFixture();
        SaveSchematicWithRef(Path.Combine(_wsA, "Amp"), PdkKitRegistry.RefFor("KitOne", "P1"));
        // Nothing is mounted anywhere: workspace A is not open.

        var status = ExternalCellStatusResolver.Classify(
            ExternalCellRef.RefFor("A", "Amp"), Path.Combine(_wsB, "Board", "schematic"));

        Assert.Equal(ExternalCellState.WorkspaceNotOpen, status.State);
        Assert.Contains("KitOne", status.Explanation);
        Assert.Contains("workspaceA", status.Repair);
    }

    [Fact]
    public void R_mw2_10_KitPartInAnUnownedCell_IsNotFoundAndDoesNotThrow()
    {
        // A cell folder with NO ancestor .cws at all, holding a kit part. There is no workspace to
        // resolve the kit against and no guess worth making — the existing NotFound placeholder.
        string loose = Path.Combine(_root, "loose");
        Directory.CreateDirectory(loose);
        string cellDir = CellFolder.CreateCellFolder(loose, "Orphan");
        SaveSchematicWithRef(cellDir, PdkKitRegistry.RefFor("KitOne", "P1"));
        WorkspaceRootFinder.InvalidateCache();

        var res = CellSymbolResolver.Resolve(
            PdkKitRegistry.RefFor("KitOne", "P1"),
            CellFolder.SubFolderPath(cellDir, ViewType.Schematic));

        Assert.Equal(CellSymbolState.NotFound, res.State);
        Assert.Null(WorkspaceRootFinder.WorkspaceDirOf(cellDir));
    }

    private static Symbol TwoPinSymbol() => new(
        primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
        pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
        portCount:  2);

    private static PdkKitPart MakePart(string id) =>
        new(id, TwoPinSymbol(), new CcellFile { NumPorts = 2 }, IconPath: null);

    // ── §9.4 — the pre-existing rewriter defect (R-mw2-15) ────────────────────

    [Fact]
    public void RewriteCellReferences_DoesNotRepointASameNamedExternalCell()
    {
        // B holds its own "Amp" AND references A's "Amp". Renaming B's Amp must leave the external
        // reference alone: it names a different cell, in a workspace the rename had no business
        // touching. Against the pre-fix last-path-segment rule this failed — "ws://A/Amp" ends in
        // "Amp" too, and the rewrite produced a reference to a cell that does not exist.
        string tech = WriteTechnology(_root, "shared.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, tech));
        WriteCws(_wsB, c =>
        {
            c.DefaultTechRef = Path.GetRelativePath(_wsB, tech);
            c.ReferencedWorkspaces =
                [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) }];
        });

        CreateCell(_wsA, "Amp");
        string ownAmp = CreateCell(_wsB, "Amp");
        string board  = CreateCell(_wsB, "Board");
        WorkspaceRootFinder.InvalidateCache();

        var schDir = CellFolder.SubFolderPath(board, ViewType.Schematic);
        var model  = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../Amp" });
        model.Components.Add(new EditableComponent
        { InstanceName = "X2", Symbol = SymbolKind.Generic, CellRef = ExternalCellRef.RefFor("A", "Amp") });
        SchematicPersistence.SaveToFile(Path.Combine(schDir, "Board.csch"), model);

        // The rename itself: the folder moves first, exactly as RenameCell does.
        string renamed = Path.Combine(_wsB, "Preamp");
        Directory.Move(ownAmp, renamed);
        CellUsageScanner.RewriteCellReferences(_wsB, ownAmp, "Preamp", out var failed);

        Assert.Empty(failed);
        var (after, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(schDir, "Board.csch"));
        Assert.Equal("../../Preamp", after.Components[0].CellRef);
        Assert.Equal(ExternalCellRef.RefFor("A", "Amp"), after.Components[1].CellRef);
    }

    [Fact]
    public void RewriteCellReferences_DoesRepointAnExternalReferenceThatActuallyNamesTheRenamedCell()
    {
        string top = BuildSharedTechFixture();
        string ampDir = Path.Combine(_wsA, "Amp");

        Directory.Move(ampDir, Path.Combine(_wsA, "Preamp"));
        CellUsageScanner.RewriteCellReferences(_wsA, ampDir, "Preamp", out var failed, [_wsB]);

        Assert.Empty(failed);
        var (after, _, _) = SchematicPersistence.LoadFromFile(
            Path.Combine(CellFolder.SubFolderPath(top, ViewType.Schematic), "Board.csch"));
        Assert.Equal(ExternalCellRef.RefFor("A", "Preamp"), after.Components[0].CellRef);
    }

    // ── §9.5 — the counter sees an open external referrer (R-mw2-14) ──────────

    [Fact]
    public void CountReferencingCells_SeesAnExternalReferrerInAnotherOpenWorkspace()
    {
        BuildSharedTechFixture();
        string ampDir = Path.Combine(_wsA, "Amp");

        var alone = CellUsageScanner.CountReferencingCells(_wsA, ampDir);
        Assert.Equal(0, alone.Count);               // A's own workspace holds no referrer

        var across = CellUsageScanner.CountReferencingCells(_wsA, ampDir, [_wsB]);
        Assert.Equal(1, across.Count);
        Assert.Single(across.OtherWorkspaceRoots);
        Assert.Equal(_wsB, across.OtherWorkspaceRoots[0]);
    }

    // ── §9.6 — the archive round trip (R-mw2-16) ──────────────────────────────

    [Fact]
    public void Archive_CarriesTheReferencedCell_AndTheLayoutStillRendersWithoutWorkspaceA()
    {
        BuildSharedTechFixture();

        var plan = WorkspaceArchiveScanner.Scan(_wsB);
        var row  = Assert.Single(plan.ReferencedWorkspaces);
        Assert.Equal("A", row.DisplayName);
        Assert.True(row.Selected, "a referenced cell is the user's own design and travels by default");
        Assert.Contains(row.Members, m => m.RelativePath.EndsWith("Amp.clay", StringComparison.Ordinal));

        string zip = Path.Combine(_root, "B.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        // Extract somewhere else, with workspace A gone entirely — the recipient's machine.
        string dest = Path.Combine(_root, "unpacked");
        Directory.CreateDirectory(dest);
        var extracted = WorkspaceArchiveExtractor.Extract(zip, dest);
        Assert.NotNull(extracted.CwsPath);
        Directory.Delete(_wsA, recursive: true);
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();

        string newLayDir = Path.Combine(extracted.WorkspaceDir, "Board", "layout");
        var res = CellLayoutResolver.Resolve(ExternalCellRef.RefFor("A", "Amp"), newLayDir);

        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.Single(res.View!.Shapes);
    }

    // ── R-mw2-5 — a raw ../.. form still resolves and is never produced ───────

    [Fact]
    public void MakeCellRef_UsesTheAlias_ForACellInAReferencedWorkspace_AndAPathOtherwise()
    {
        BuildSharedTechFixture();
        string schDir = CellFolder.SubFolderPath(Path.Combine(_wsB, "Board"), ViewType.Schematic);

        Assert.Equal(ExternalCellRef.RefFor("A", "Amp"),
                     ExternalCellRef.MakeCellRef(schDir, Path.Combine(_wsA, "Amp")));

        string sibling = CreateCell(_wsB, "Filter");
        Assert.Equal("../../Filter", ExternalCellRef.MakeCellRef(schDir, sibling).Replace('\\', '/'));
    }

    [Fact]
    public void ARawRelativeReferenceIntoAnotherWorkspaceStillResolves()
    {
        // R-mw2-5: it resolves today by accident and goes on resolving — removing that would break
        // the LIBRARY case, which legitimately points outside the workspace.
        BuildSharedTechFixture();
        string schDir = CellFolder.SubFolderPath(Path.Combine(_wsB, "Board"), ViewType.Schematic);
        string raw = Path.GetRelativePath(schDir, Path.Combine(_wsA, "Amp")).Replace('\\', '/');

        Assert.Equal(CellSymbolState.Resolved, CellSymbolResolver.Resolve(raw, schDir).State);
    }

    // ── A broken alias explains itself rather than reading as a typo ──────────

    [Fact]
    public void AnUndeclaredAlias_IsBroken_AndSaysSo()
    {
        BuildSharedTechFixture();
        string schDir = CellFolder.SubFolderPath(Path.Combine(_wsB, "Board"), ViewType.Schematic);

        var status = ExternalCellStatusResolver.Classify(ExternalCellRef.RefFor("Nope", "Amp"), schDir);

        Assert.Equal(ExternalCellState.Broken, status.State);
        Assert.Contains("Nope", status.Explanation);
        Assert.Equal(CellSymbolState.NotFound,
                     CellSymbolResolver.Resolve(ExternalCellRef.RefFor("Nope", "Amp"), schDir).State);
    }

    [Fact]
    public void RelocatingTheOtherProject_IsOneCwsEdit()
    {
        // R-mw2-2's first reason, asserted rather than asserted-in-prose: the documents are untouched.
        string top = BuildSharedTechFixture();
        string moved = Path.Combine(_root, "moved-A");
        Directory.Move(_wsA, moved);
        WriteCws(_wsB, c =>
        {
            c.DefaultTechRef = Path.GetRelativePath(_wsB, Path.Combine(_root, "shared.ctech"));
            c.ReferencedWorkspaces =
                [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(moved, ".cws")) }];
        });
        CellSymbolResolver.InvalidateAll();

        var res = CellSymbolResolver.Resolve(
            ExternalCellRef.RefFor("A", "Amp"), CellFolder.SubFolderPath(top, ViewType.Schematic));

        Assert.Equal(CellSymbolState.Resolved, res.State);
    }
}
