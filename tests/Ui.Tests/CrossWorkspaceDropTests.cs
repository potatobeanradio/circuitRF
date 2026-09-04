using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  MW3 — workspace-to-workspace drag and drop
//  (docs/sonnet-briefs/brief-multi-workspace-3-workspace-dnd.md §6).
//
//  Two real workspaces on disk in every fixture, for MW2's reason: the feature IS the
//  boundary between them, and an in-memory double would agree with itself about a rule
//  the filesystem is the authority on.
//
//  The dialog itself is not asserted here — it is a Window, and what it decides
//  (copy-vs-reference, the sub-cell mode) arrives at this code as an argument. What IS
//  asserted is every rule the dialog reads FROM: which drops are even offered, what a
//  copy would collide with, which kits are missing, and what the copied files say
//  afterwards.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CrossWorkspaceDropTests : IDisposable
{
    private readonly string _root;
    private readonly string _wsA;
    private readonly string _wsB;

    public CrossWorkspaceDropTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_mw3_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string WriteTechnology(string root, string fileName, Technology? tech = null)
    {
        string path = Path.Combine(root, fileName);
        TechPersistence.SaveToFile(path, tech ?? StarterTechnologies.Pcb2Layer());
        return path;
    }

    private static Symbol TwoPinSymbol() => new(
        primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
        pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
        portCount:  2);

    private static PdkKitPart MakePart(string id) =>
        new(id, TwoPinSymbol(), new CcellFile { NumPorts = 2 }, IconPath: null);

    /// <summary>A cell with a primary symbol, a primary schematic and a primary layout carrying one
    /// rectangle — enough for the symbol, the geometry and the references to all be asserted.</summary>
    private static string CreateCell(string workspaceRoot, string name)
    {
        string cellDir = CellFolder.CreateCellFolder(workspaceRoot, name);

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"), TwoPinSymbol());

        var schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        SchematicPersistence.SaveToFile(Path.Combine(schDir, name + ".csch"), new SchematicEditModel());

        var layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 2000 });
        LayoutPersistence.SaveToFile(Path.Combine(layDir, name + ".clay"), view);

        return cellDir;
    }

    /// <summary>Adds one component to a cell's primary schematic, and one instance to its primary
    /// layout, both carrying <paramref name="cellRef"/> — so a rewrite has to reach both view kinds.</summary>
    private static void AddChildRef(string cellDir, string cellRef)
    {
        string name = Path.GetFileName(cellDir);

        string schPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name + ".csch");
        var (model, _, _) = SchematicPersistence.LoadFromFile(schPath);
        model.Components.Add(new EditableComponent
        { InstanceName = "X" + (model.Components.Count + 1), Symbol = SymbolKind.Generic, CellRef = cellRef });
        SchematicPersistence.SaveToFile(schPath, model);

        string layPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay");
        var view = LayoutPersistence.LoadFromFile(layPath);
        view.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(layPath, view);
    }

    /// <summary>A shares its technology with B; A holds Amp, which places Bias.</summary>
    private void BuildSharedTechFixture()
    {
        string tech = WriteTechnology(_root, "shared.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, tech));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, tech));

        CreateCell(_wsA, "Bias");
        string amp = CreateCell(_wsA, "Amp");
        AddChildRef(amp, Path.GetRelativePath(
            CellFolder.SubFolderPath(amp, ViewType.Schematic), Path.Combine(_wsA, "Bias")));

        WorkspaceRootFinder.InvalidateCache();
    }

    private static IEnumerable<string> CellRefsIn(string cellDir)
    {
        foreach (var (viewType, pattern, key) in new[]
        {
            (ViewType.Schematic, "*.csch", "Components"),
            (ViewType.Layout,    "*.clay", "Instances"),
        })
        {
            string sub = CellFolder.SubFolderPath(cellDir, viewType);
            if (!Directory.Exists(sub)) continue;
            foreach (var file in Directory.EnumerateFiles(sub, pattern))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
                if (node?[key]?.AsArray() is not { } array) continue;
                foreach (var item in array)
                    if (item?["CellRef"]?.GetValue<string?>() is { Length: > 0 } r)
                        yield return r;
            }
        }
    }

    // ── §6.1 — the payload crosses, and a same-workspace drag still does nothing ──

    [Fact]
    public void CellPayload_FromAnotherWorkspace_ResolvesToThatWorkspacesCellFolder()
    {
        BuildSharedTechFixture();
        string amp = Path.Combine(_wsA, "Amp");

        string wire = new CellDragPayload(amp).Serialize();
        var intent = TreeDrop.ForPayload(wire, _wsB);

        Assert.Equal(TreeDropAction.Cell, intent.Action);
        Assert.Equal(Path.GetFullPath(amp), intent.Path);
        Assert.True(File.Exists(Path.Combine(intent.Path, CellFolder.CcellFileName)));
    }

    [Fact]
    public void R_mw3_4_SameWorkspaceDrag_IsRefused()
    {
        BuildSharedTechFixture();
        CreateCell(_wsB, "Board");

        // R-mw3-4 said this drop did NOTHING, and MW3 deliberately left it inert because the
        // reference repointing a move needs did not exist yet. TM1 built that, so the same payload
        // now means Move — never the copy-or-reference question, which is what R-mw3-4 was actually
        // protecting against and is still asserted below.
        var intent = TreeDrop.ForPayload(
            new CellDragPayload(Path.Combine(_wsB, "Board")).Serialize(), _wsB);

        Assert.Equal(TreeDropAction.Move, intent.Action);
        Assert.NotEqual(TreeDropAction.Cell, intent.Action);
    }

    [Fact]
    public void ForeignTextAndFilePayloads_AreClassifiedByTheirOwnPrefix()
    {
        BuildSharedTechFixture();
        string loose = Path.Combine(_wsA, "sweep.s2p");
        File.WriteAllText(loose, "! touchstone\n");

        Assert.Equal(TreeDropAction.None,
            TreeDrop.ForPayload("just some dragged text", _wsB).Action);

        Assert.Equal(TreeDropAction.File,
            TreeDrop.ForPayload(new WorkspaceFileDragPayload(loose).Serialize(), _wsB).Action);

        // The Data Display's own payload, dropped on a tree instead: still a file.
        Assert.Equal(TreeDropAction.File,
            TreeDrop.ForPayload(new NpyFileDragPayload(loose).Serialize(), _wsB).Action);

        // …and dropped on its OWN tree it is a TM1 move rather than MW3's copy — see
        // R_mw3_4_SameWorkspaceDrag_IsRefused for why that supersedes only half of R-mw3-4.
        Assert.Equal(TreeDropAction.Move,
            TreeDrop.ForPayload(new NpyFileDragPayload(loose).Serialize(), _wsA).Action);
    }

    // ── §6.2 — copy with sub-cells ────────────────────────────────────────────

    [Fact]
    public void R_mw3_7_CopyWithSubCells_ResolvesEntirelyWithinTheReceivingWorkspace()
    {
        BuildSharedTechFixture();

        var plan = CrossWorkspaceCellCopy.Plan(
            Path.Combine(_wsA, "Amp"), _wsB, _wsB, SubCellMode.Copy);

        Assert.Equal(2, plan.Folders.Count);                       // Amp and the Bias it places
        Assert.Contains(plan.Folders, f => Path.GetFileName(f.DestDir) == "Bias");
        Assert.False(plan.NeedsSourceAlias);                       // nothing is left behind in A

        CrossWorkspaceCellCopy.Execute(plan);
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();

        string copied = Path.Combine(_wsB, "Amp");
        Assert.True(Directory.Exists(Path.Combine(_wsB, "Bias")));

        // Not one reference in the copy points into A, and every one of them resolves inside B.
        foreach (var cellRef in CellRefsIn(copied))
        {
            Assert.False(ExternalCellRef.IsExternalRef(cellRef));
            string resolved = ExternalCellRef.ResolveCellDir(
                cellRef, CellFolder.SubFolderPath(copied, ViewType.Schematic))
                ?? ExternalCellRef.ResolveCellDir(cellRef, CellFolder.SubFolderPath(copied, ViewType.Layout))!;
            Assert.False(WorkspaceRootFinder.IsOutside(resolved, _wsB));
        }

        // …and it renders: the sub-cell's own geometry is reachable from the copy.
        var layout = CellLayoutResolver.Resolve(
            CellRefsIn(copied).First(), CellFolder.SubFolderPath(copied, ViewType.Layout));
        Assert.Equal(CellLayoutState.Resolved, layout.State);
        Assert.Single(layout.View!.Shapes);
    }

    // ── §6.3 — copy keeping sub-cells referenced ──────────────────────────────

    [Fact]
    public void R_mw3_7_KeepReferenced_WritesTheAliasFormAndResolvesBackIntoTheSourceWorkspace()
    {
        BuildSharedTechFixture();

        // The alias must exist BEFORE the rewrite — MakeCellRef reads the table, and without it the
        // rewrite would silently emit the raw ../.. form R-mw2-5 forbids producing.
        WriteCws(_wsB, c =>
        {
            c.DefaultTechRef = Path.GetRelativePath(_wsB, Path.Combine(_root, "shared.ctech"));
            c.ReferencedWorkspaces =
                [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) }];
        });

        var plan = CrossWorkspaceCellCopy.Plan(
            Path.Combine(_wsA, "Amp"), _wsB, _wsB, SubCellMode.KeepReferenced);

        Assert.Single(plan.Folders);          // the top cell only
        Assert.True(plan.NeedsSourceAlias);   // …because it places one

        CrossWorkspaceCellCopy.Execute(plan);
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();

        string copied = Path.Combine(_wsB, "Amp");
        var refs = CellRefsIn(copied).ToList();
        Assert.NotEmpty(refs);

        foreach (var cellRef in refs)
        {
            Assert.True(ExternalCellRef.IsExternalRef(cellRef), cellRef);
            Assert.Equal(ExternalCellRef.RefFor("A", "Bias"), cellRef);
        }

        // And it renders — resolving back into A, which is the whole point of the mode.
        var layout = CellLayoutResolver.Resolve(
            refs[0], CellFolder.SubFolderPath(copied, ViewType.Layout));
        Assert.Equal(CellLayoutState.Resolved, layout.State);
        Assert.Single(layout.View!.Shapes);
    }

    [Fact]
    public void R_mw3_7_DifferingTechnologies_RefuseTheReferencedMode()
    {
        // The negative §6.3 asks for: "keep them referenced" creates external references and is
        // therefore subject to every MW2 rule, R-mw2-7 included. The gate the drop asks is the CELL
        // one — the drop names a cell, so its layout's own technology is the whole answer and the two
        // workspaces' defaults have nothing to add.
        string techA = WriteTechnology(_wsA, "processA.ctech", StarterTechnologies.MmicGaAs());
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));
        string amp = CreateCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        var check = ExternalWorkspaceGate.CheckCellTechnology(null, _wsB, amp, new TechnologyCache());
        Assert.False(check.Permitted);
        Assert.Contains("processA.ctech", check.Refusal);
        Assert.Contains("processB.ctech", check.Refusal);
    }

    [Fact]
    public void R_mw3_7_TwoCopiesOfOneTechnology_DoNotRefuseTheReferencedMode()
    {
        // Two projects on one process, each keeping its own copy of the .ctech beside it — the
        // ordinary shape of two boards for one fab, and what the path comparison refused while
        // printing two identical file names.
        string techA = WriteTechnology(_wsA, "pcb-2layer.ctech");
        string techB = WriteTechnology(_wsB, "pcb-2layer.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));
        string amp = CreateCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        Assert.True(ExternalWorkspaceGate
            .CheckCellTechnology(null, _wsB, amp, new TechnologyCache()).Permitted);
    }

    // ── §6.4 — the kit trap ───────────────────────────────────────────────────

    [Fact]
    public void R_mw3_8_CopyingAKitBearingCell_ReportsTheKitTheDestinationLacks()
    {
        BuildSharedTechFixture();
        AddChildRef(Path.Combine(_wsA, "Amp"), PdkKitRegistry.RefFor("KitOne", "P1"));

        // A has it mounted; B has not imported it. A pdk:// reference is not a path and is not
        // rewritten, so the copy resolves in B only if B mounts the same kit.
        PdkKitRegistry.SetKit(_wsA, "KitOne", [MakePart("P1")]);

        var plan = CrossWorkspaceCellCopy.Plan(
            Path.Combine(_wsA, "Amp"), _wsB, _wsB, SubCellMode.Copy);

        Assert.Equal(["KitOne"], plan.UnimportedKits);

        // "Copy anyway" — the parts report NotFound rather than throwing, which is the existing
        // reported and repairable state.
        CrossWorkspaceCellCopy.Execute(plan);
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();

        string copied = Path.Combine(_wsB, "Amp");
        Assert.Contains(PdkKitRegistry.RefFor("KitOne", "P1"), CellRefsIn(copied));

        var res = CellSymbolResolver.Resolve(
            PdkKitRegistry.RefFor("KitOne", "P1"), CellFolder.SubFolderPath(copied, ViewType.Schematic));
        Assert.Equal(CellSymbolState.NotFound, res.State);
    }

    [Fact]
    public void R_mw3_8_NoWarning_WhenTheDestinationHasTheSameKit()
    {
        BuildSharedTechFixture();
        AddChildRef(Path.Combine(_wsA, "Amp"), PdkKitRegistry.RefFor("KitOne", "P1"));
        PdkKitRegistry.SetKit(_wsA, "KitOne", [MakePart("P1")]);
        PdkKitRegistry.SetKit(_wsB, "KitOne", [MakePart("P1")]);

        var plan = CrossWorkspaceCellCopy.Plan(
            Path.Combine(_wsA, "Amp"), _wsB, _wsB, SubCellMode.Copy);

        Assert.Empty(plan.UnimportedKits);
    }

    // ── §6.5 — name collisions are reported, never auto-suffixed ──────────────

    [Fact]
    public void R_mw3_9_ACollisionIsReported_AndNothingIsAutoSuffixed()
    {
        BuildSharedTechFixture();
        CreateCell(_wsB, "Amp");            // B already has a cell of that name

        var plan = CrossWorkspaceCellCopy.Plan(
            Path.Combine(_wsA, "Amp"), _wsB, _wsB, SubCellMode.Copy);

        Assert.Contains(Path.Combine(_wsB, "Amp"), plan.Collisions);
        Assert.DoesNotContain(plan.Folders, f => Path.GetFileName(f.DestDir) == "Amp_2");

        // Renaming the top is the caller's answer to that, and the plan follows it.
        var renamed = plan.Folders.Select(f =>
            f.IsTop ? f with { DestDir = Path.Combine(_wsB, "AmpFromA") } : f).ToList();
        var repointed = plan with { Folders = renamed, DestCellDir = Path.Combine(_wsB, "AmpFromA") };

        CrossWorkspaceCellCopy.Execute(repointed);
        Assert.True(Directory.Exists(Path.Combine(_wsB, "AmpFromA")));
        Assert.False(Directory.Exists(Path.Combine(_wsB, "Amp_2")));
    }

    // ── §6.6 — a .cws opens, and copies nothing ───────────────────────────────

    [Fact]
    public void R_mw3_12_ADroppedCwsOpensAWorkspace_AndIsNeverCopied()
    {
        BuildSharedTechFixture();
        string cws = Path.Combine(_wsA, ".cws");

        var intent = TreeDrop.ForDroppedPath(cws);

        Assert.Equal(TreeDropAction.OpenWorkspace, intent.Action);
        Assert.Equal(cws, intent.Path);

        // Nothing about that classification produces a copy: it never reaches AddKnownFile, and
        // there is no branch that writes into the receiving workspace.
        Assert.False(File.Exists(Path.Combine(_wsB, ".cws.1")));
        Assert.Empty(Directory.EnumerateDirectories(_wsB));

        // Everything else in an OS file list is still a bookmark, exactly as before MW3.
        string loose = Path.Combine(_wsA, "sweep.s2p");
        File.WriteAllText(loose, "! touchstone\n");
        Assert.Equal(TreeDropAction.KnownFile, TreeDrop.ForDroppedPath(loose).Action);
    }

    // ── §6.7 — the alias is created once and reused ───────────────────────────

    [Fact]
    public void R_mw3_7_TheAliasIsCreatedOnceAndReused_ForASecondCellOfTheSameWorkspace()
    {
        BuildSharedTechFixture();
        CreateCell(_wsA, "Filter");

        Assert.True(ViewModels.WorkspaceViewModel.AddReferencedWorkspace(
            _wsB, "A", Path.Combine(_wsA, ".cws"), out string? error), error);

        // The second cell finds the reference already there — MW2 §2's "one alias per workspace",
        // which is what keeps a rename repair from having to guess which of two names to fix.
        Assert.Equal("A", ViewModels.WorkspaceViewModel.ExistingAliasFor(_wsB, _wsA));

        Assert.True(ViewModels.WorkspaceViewModel.AddReferencedWorkspace(
            _wsB, "A", Path.Combine(_wsA, ".cws"), out error), error);

        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(_wsB, ".cws"));
        Assert.Single(cws.ReferencedWorkspaces!);

        // …and a second alias NAME for the same workspace is refused rather than added.
        Assert.False(ViewModels.WorkspaceViewModel.AddReferencedWorkspace(
            _wsB, "AlsoA", Path.Combine(_wsA, ".cws"), out error));
        Assert.Contains("already referenced", error);
        Assert.Single(WorkspacePersistence.LoadFromFile(Path.Combine(_wsB, ".cws")).ReferencedWorkspaces!);
    }
}
