using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  brief-foreign-documents.md — gate tests for the parts of the brief that are
//  headlessly testable. WorkspaceViewModel itself cannot be constructed headlessly
//  (per this project's own "Testing without the Avalonia runtime" convention), so
//  gates that live entirely inside it (R-fgn-2's ResetToBlankShell split, the R-fgn-4
//  dialog flow, Save-All/quit sweeps) are exercised either via a Simulate* helper that
//  mirrors the production seam exactly (the same pattern LayoutHierarchySaveTests/
//  HierarchySaveTests already use), or via the framework-free primitives the real
//  WorkspaceViewModel code delegates to (WorkspaceRootFinder, TechnologyResolver,
//  CellUsageScanner, LayoutEditorViewModel's computed IsForeign/SourceWorkspaceName
//  properties, LayoutDocument's title/marking).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class WorkspaceRootFinderTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceRootFinderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_wsroot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FindAncestorCws_DirectChild_FindsIt()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".cws"), "{}");
        var sub = Path.Combine(_tempDir, "cellA", "layout");
        Directory.CreateDirectory(sub);

        var found = WorkspaceRootFinder.FindAncestorCws(sub);

        Assert.Equal(Path.Combine(_tempDir, ".cws"), found);
    }

    [Fact]
    public void FindAncestorCws_NearestWins_NotAnOuterOne()
    {
        // Two nested workspaces — the walk must stop at the NEAREST ancestor .cws, not an outer one.
        File.WriteAllText(Path.Combine(_tempDir, ".cws"), "{}");
        var innerRoot = Path.Combine(_tempDir, "innerWorkspace");
        Directory.CreateDirectory(innerRoot);
        File.WriteAllText(Path.Combine(innerRoot, ".cws"), "{}");
        var sub = Path.Combine(innerRoot, "cellA", "layout");
        Directory.CreateDirectory(sub);

        var found = WorkspaceRootFinder.FindAncestorCws(sub);

        Assert.Equal(Path.Combine(innerRoot, ".cws"), found);
    }

    [Fact]
    public void FindAncestorCws_NoAncestor_ReturnsNull()
    {
        var sub = Path.Combine(_tempDir, "loose", "cellA");
        Directory.CreateDirectory(sub);

        Assert.Null(WorkspaceRootFinder.FindAncestorCws(sub));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindAncestorCws_NullOrEmpty_ReturnsNull(string? startDir)
    {
        Assert.Null(WorkspaceRootFinder.FindAncestorCws(startDir));
    }

    [Fact]
    public void IsOutside_PathInsideRoot_False()
    {
        var root = Path.Combine(_tempDir, "ws");
        Directory.CreateDirectory(root);
        var inside = Path.Combine(root, "child.clay");

        Assert.False(WorkspaceRootFinder.IsOutside(inside, root));
    }

    [Fact]
    public void IsOutside_PathOutsideRoot_True()
    {
        var root = Path.Combine(_tempDir, "ws");
        Directory.CreateDirectory(root);
        var outsideDir = Path.Combine(_tempDir, "elsewhere");
        Directory.CreateDirectory(outsideDir);
        var outside = Path.Combine(outsideDir, "foreign.clay");

        Assert.True(WorkspaceRootFinder.IsOutside(outside, root));
    }
}

/// <summary>
/// R-fgn-3: TechRef=null resolves against the document's OWN ancestor workspace (never the currently
/// open one), and does so LIVE — re-derived on every call, never snapshotted. Exercises
/// <see cref="LayoutEditorViewModel"/>'s computed IsForeign/WorkspaceTechDir/SourceWorkspaceName
/// directly (constructible headlessly), plus a Simulate* helper mirroring
/// WorkspaceViewModel.ResolveTechFor's core resolution logic exactly (minus message-posting and the
/// R-fgn-4 session-override/prompt machinery, which need Messages/a dialog host).
/// </summary>
public sealed class ForeignDocumentTechResolutionTests : IDisposable
{
    private readonly string _tempDir;

    public ForeignDocumentTechResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_fgntech_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static LayoutView MakeModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>Mirrors WorkspaceViewModel.ResolveTechFor's resolution-order logic exactly (R-fgn-3),
    /// without the session-override dictionary, message posting, or the R-fgn-4 prompt trigger — those
    /// need WorkspaceViewModel's own Messages sink / a dialog host and are not this gate's concern.</summary>
    private static TechResolution SimulateResolveTechFor(
        string? techRef, string? clayPath, string? currentWorkspacePath, TechnologyCache cache)
    {
        string? normalizedClayPath = clayPath is null ? null : Path.GetFullPath(clayPath);

        string? ownCwsPath = normalizedClayPath is not null
            ? WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(normalizedClayPath))
            : currentWorkspacePath;

        string? workspaceDir = ownCwsPath is null ? null : Path.GetDirectoryName(ownCwsPath);

        string? defaultTechRef = null;
        if (ownCwsPath is not null)
        {
            try { defaultTechRef = WorkspacePersistence.LoadFromFile(ownCwsPath).DefaultTechRef; }
            catch { /* corrupt .cws -> no default */ }
        }

        string? clayDir = normalizedClayPath is null ? null : Path.GetDirectoryName(normalizedClayPath);
        return TechnologyResolver.Resolve(techRef, clayDir, workspaceDir, defaultTechRef, cache);
    }

    private string MakeWorkspace(string name, Technology tech)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(root);
        var techDir = Path.Combine(root, "tech");
        Directory.CreateDirectory(techDir);
        var techPath = Path.Combine(techDir, "x.ctech");
        TechPersistence.SaveToFile(techPath, tech);

        var cwsPath = Path.Combine(root, ".cws");
        WorkspacePersistence.SaveToFile(cwsPath, new CwsFile
        {
            FormatVersion = WorkspacePersistence.CurrentFormatVersion,
            DefaultTechRef = "tech/x.ctech",
        });
        return root;
    }

    // ── Gate 4 — the headline test ────────────────────────────────────────────────

    [Fact]
    public void Gate4_ForeignDocument_ResolvesAgainstOwnWorkspace_NotTheCurrentlyOpenOne()
    {
        // Workspace A owns the document under test; workspace B is the CURRENTLY open one.
        // Both PCB2Layer and MmicGaAs share the (1,0)-(8,0) key range but disagree on what (7,0) means
        // (Drill vs Substrate) — exactly the collision this brief exists to prevent silently.
        var wsA = MakeWorkspace("workspaceA", StarterTechnologies.Pcb2Layer());
        var wsB = MakeWorkspace("workspaceB", StarterTechnologies.MmicGaAs());

        var docAPath = Path.Combine(wsA, "child.clay");
        var cache = new TechnologyCache();

        var resolution = SimulateResolveTechFor(
            techRef: null, clayPath: docAPath, currentWorkspacePath: Path.Combine(wsB, ".cws"), cache);

        Assert.NotNull(resolution.Tech);
        var layer70 = resolution.Tech!.Layers.Single(l => l.Key == new LayerKey(7, 0));
        Assert.Equal("Drill", layer70.Name);      // A's (PCB) name, never MMIC's "Substrate"
        Assert.NotEqual("Substrate", layer70.Name);
    }

    // ── Gate 5 — live, not frozen ──────────────────────────────────────────────────

    [Fact]
    public void Gate5_LiveNotFrozen_EditingTheCtechAfterFirstResolve_IsPickedUpOnNextResolve()
    {
        var wsA = MakeWorkspace("workspaceA", StarterTechnologies.Pcb2Layer());
        var docPath = Path.Combine(wsA, "child.clay");
        var techPath = Path.Combine(wsA, "tech", "x.ctech");
        var cache = new TechnologyCache();

        var first = SimulateResolveTechFor(null, docPath, currentWorkspacePath: null, cache);
        Assert.NotNull(first.Tech);
        Assert.Equal("Drill", first.Tech!.Layers.Single(l => l.Key == new LayerKey(7, 0)).Name);

        // Edit the .ctech on disk (rename the layer) and invalidate the cache the same way a live
        // .ctech-editor save / Reload Technology would — ResolveTechFor is never snapshotted, so a
        // fresh call must see the edit.
        var edited = StarterTechnologies.Pcb2Layer();
        edited.Layers.Single(l => l.Key == new LayerKey(7, 0)).Name = "Renamed Drill";
        TechPersistence.SaveToFile(techPath, edited);
        cache.Invalidate(techPath);

        var second = SimulateResolveTechFor(null, docPath, currentWorkspacePath: null, cache);
        Assert.Equal("Renamed Drill", second.Tech!.Layers.Single(l => l.Key == new LayerKey(7, 0)).Name);
    }

    // ── Gate 6 — no ancestor workspace resolves to None, never a silent fallback ──

    [Fact]
    public void Gate6_NoAncestorWorkspace_NoTechRef_ResolvesToNone_NotSilentFallback()
    {
        var loose = Path.Combine(_tempDir, "loose");
        Directory.CreateDirectory(loose);
        var docPath = Path.Combine(loose, "orphan.clay");
        var cache = new TechnologyCache();

        var resolution = SimulateResolveTechFor(null, docPath, currentWorkspacePath: null, cache);

        Assert.Null(resolution.Tech);
        Assert.Equal(TechResolutionSource.None, resolution.Source);
        // This is exactly the condition WorkspaceViewModel.ResolveTechFor checks before triggering the
        // R-fgn-4 prompt (ownCwsPath is null && techRef is null && resolution.Source == None) — pinned
        // here directly since the dialog machinery itself needs a live window host.
    }

    // ── IsForeign / SourceWorkspaceName / WorkspaceTechDir — computed, live properties ──

    [Fact]
    public void IsForeign_ScratchDocument_NeverForeign()
    {
        var vm = new LayoutEditorViewModel(MakeModel()); // no CurrentLayoutPath
        Assert.False(vm.IsForeign);
        Assert.Null(vm.SourceWorkspaceName);
    }

    [Fact]
    public void IsForeign_MaterializedInAncestorWorkspace_MatchingCurrentProvider_NotForeign()
    {
        var wsA = MakeWorkspace("workspaceA", StarterTechnologies.Pcb2Layer());
        var docPath = Path.Combine(wsA, "child.clay");
        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => wsA,
        };

        Assert.False(vm.IsForeign);
        Assert.Equal("workspaceA", vm.SourceWorkspaceName);
        Assert.Equal(Path.Combine(wsA, ".cws"), vm.SourceWorkspaceCwsPath);
    }

    [Fact]
    public void IsForeign_MaterializedInDifferentWorkspaceThanCurrentlyOpen_IsForeign()
    {
        var wsA = MakeWorkspace("workspaceA", StarterTechnologies.Pcb2Layer());
        var wsB = MakeWorkspace("workspaceB", StarterTechnologies.MmicGaAs());
        var docPath = Path.Combine(wsA, "child.clay");
        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => wsB,
        };

        Assert.True(vm.IsForeign);
        Assert.Equal("workspaceA", vm.SourceWorkspaceName);
    }

    // ── Gate 8 — push-in/hierarchy navigation resolves against the document's OWN workspace ──

    [Fact]
    public void Gate8_PushInto_SubCellSession_StillResolvesAgainstParentsOwnWorkspace_NotTheCurrentlyOpenOne()
    {
        var wsA = MakeWorkspace("workspaceA", StarterTechnologies.Pcb2Layer());
        var wsB = MakeWorkspace("workspaceB", StarterTechnologies.MmicGaAs());

        var parentPath = Path.Combine(wsA, "parent.clay");
        var childPath  = Path.Combine(wsA, "child.clay");

        // Mirrors WorkspaceViewModel.WireRetargetSeam: EVERY session VM (base or pushed-in) gets the
        // SAME CurrentWorkspaceRootDirProvider (reads whichever workspace is CURRENTLY open — here, B)
        // — the only thing that differs per-VM is its own CurrentLayoutPath, which is what the
        // ancestor-.cws walk actually keys on.
        var parentVm = new LayoutEditorViewModel(MakeModel(), parentPath) { CurrentWorkspaceRootDirProvider = () => wsB };
        var childVm  = new LayoutEditorViewModel(MakeModel(), childPath)  { CurrentWorkspaceRootDirProvider = () => wsB };

        var doc = new LayoutDocument("parent.clay", parentVm, parentPath);
        Assert.True(doc.IsForeign); // foreign to B even at the base level

        doc.PushIn(childVm, "X1");

        Assert.Same(childVm, doc.ActiveViewModel);
        Assert.True(doc.IsForeign); // still foreign once pushed in
        Assert.Equal("workspaceA", doc.SourceWorkspaceName); // the SUB-CELL's own workspace, not B
    }

    [Fact]
    public void IsForeign_NoAncestorWorkspaceAtAll_AlwaysForeign_SourceNameNull()
    {
        var loose = Path.Combine(_tempDir, "loose");
        Directory.CreateDirectory(loose);
        var docPath = Path.Combine(loose, "orphan.clay");
        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => null,
        };

        Assert.True(vm.IsForeign);
        Assert.Null(vm.SourceWorkspaceName);
        Assert.Null(vm.SourceWorkspaceCwsPath);
    }

    // ── Gate 12 — Save As adopts, purely as a consequence of live path re-evaluation ──

    [Fact]
    public void Gate12_SaveAsIntoCurrentWorkspace_AdoptsAutomatically_NoExplicitCall()
    {
        var wsA = MakeWorkspace("workspaceA", StarterTechnologies.Pcb2Layer());
        var wsB = MakeWorkspace("workspaceB", StarterTechnologies.MmicGaAs());
        var docPathInA = Path.Combine(wsA, "child.clay");

        var vm = new LayoutEditorViewModel(MakeModel(), docPathInA)
        {
            CurrentWorkspaceRootDirProvider = () => wsB,
        };
        Assert.True(vm.IsForeign); // foreign to B while it lives in A

        // Simulate "Save As" landing the document inside B — nothing but CurrentLayoutPath changes.
        vm.CurrentLayoutPath = Path.Combine(wsB, "adopted.clay");

        Assert.False(vm.IsForeign);
        Assert.Equal("workspaceB", vm.SourceWorkspaceName);
    }
}

/// <summary>§4 marking — title bar suffix. The edge band / tab tint are AXAML-level and cannot be
/// pixel-verified headlessly (matching every prior Layout Editor phase's own note on this); the title
/// suffix is plain string logic on <see cref="LayoutDocument"/> and is fully testable.</summary>
public sealed class ForeignDocumentMarkingTests : IDisposable
{
    private readonly string _tempDir;

    public ForeignDocumentMarkingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_fgnmark_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static LayoutView MakeModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    [Fact]
    public void TitleBar_ForeignDocument_NamesSourceWorkspace_NoAsterisk()
    {
        var wsA = Path.Combine(_tempDir, "workspaceA");
        Directory.CreateDirectory(wsA);
        File.WriteAllText(Path.Combine(wsA, ".cws"), "{}");
        var docPath = Path.Combine(wsA, "amp.clay");

        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => null, // some OTHER workspace is open (or none)
        };
        var doc = new LayoutDocument("amp.clay", vm, docPath);

        Assert.Contains("[workspaceA]", doc.Title);
        Assert.DoesNotContain("*", doc.Title);
    }

    [Fact]
    public void TitleBar_ForeignAndDirty_ShowsBulletAndSuffix_NeverAsterisk()
    {
        var wsA = Path.Combine(_tempDir, "workspaceA");
        Directory.CreateDirectory(wsA);
        File.WriteAllText(Path.Combine(wsA, ".cws"), "{}");
        var docPath = Path.Combine(wsA, "amp.clay");

        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => null,
        };
        var doc = new LayoutDocument("amp.clay", vm, docPath);

        // Draw something to make the session dirty (any undoable edit will do).
        vm.Execute(new AddShapeCommand(vm.Model,
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));

        Assert.Contains("•", doc.Title);
        Assert.Contains("[workspaceA]", doc.Title);
        Assert.DoesNotContain("*", doc.Title);
    }

    [Fact]
    public void TitleBar_WorkspaceBoundDocument_NoSuffixAtAll()
    {
        var ws = Path.Combine(_tempDir, "ws");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, ".cws"), "{}");
        var docPath = Path.Combine(ws, "amp.clay");

        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => ws, // this IS the currently open workspace
        };
        var doc = new LayoutDocument("amp.clay", vm, docPath);

        Assert.Equal("amp.clay", doc.Title);
        Assert.DoesNotContain("[", doc.Title);
    }

    [Fact]
    public void RefreshForeignMarking_RecomputesTitleAfterCurrentWorkspaceChanges()
    {
        var wsA = Path.Combine(_tempDir, "workspaceA");
        Directory.CreateDirectory(wsA);
        File.WriteAllText(Path.Combine(wsA, ".cws"), "{}");
        var docPath = Path.Combine(wsA, "amp.clay");

        string? currentWs = wsA;
        var vm = new LayoutEditorViewModel(MakeModel(), docPath)
        {
            CurrentWorkspaceRootDirProvider = () => currentWs,
        };
        var doc = new LayoutDocument("amp.clay", vm, docPath);
        Assert.Equal("amp.clay", doc.Title); // workspace-bound, no suffix

        // Simulate a workspace switch to some other workspace, then the refresh call
        // WorkspaceViewModel.OnCurrentWorkspacePathChanged makes on every open LayoutDocument.
        currentWs = null;
        doc.RefreshForeignMarking();

        Assert.Contains("[workspaceA]", doc.Title);
    }
}

/// <summary>R-fgn-6/gate 10: CellUsageScanner (and therefore Remove/Rename Cell, which route through
/// it) never reaches outside the workspace root it is given — confirmed directly by using two entirely
/// separate temp roots and asserting the "foreign" one is untouched, even when it uses the SAME cell
/// name as the target (the exact name-collision case gate 10 calls out).</summary>
public sealed class ForeignDocumentIsolationTests : IDisposable
{
    private readonly string _tempDir;

    public ForeignDocumentIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_fgniso_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteLayoutReferencing(string clayPath, string cellRefRelative)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(clayPath)!);
        var json = $$"""
        {
          "FormatVersion": 1,
          "DbuPerMicron": 1000,
          "Shapes": [],
          "Instances": [ { "CellRef": "{{cellRefRelative}}", "X": 0, "Y": 0 } ]
        }
        """;
        File.WriteAllText(clayPath, json);
    }

    [Fact]
    public void CountReferencingCells_NeverCountsACellOutsideTheGivenWorkspaceRoot_EvenOnNameCollision()
    {
        // Current workspace: has a "Target" cell, referenced from "Referencer".
        var currentWs = Path.Combine(_tempDir, "currentWorkspace");
        var targetDir = Path.Combine(currentWs, "Target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, CellFolder.CcellFileName), "{}");

        var referencerDir = Path.Combine(currentWs, "Referencer");
        Directory.CreateDirectory(referencerDir);
        File.WriteAllText(Path.Combine(referencerDir, CellFolder.CcellFileName), "{}");
        WriteLayoutReferencing(
            Path.Combine(referencerDir, "layout", "cell.clay"), "../../Target");

        // A completely separate "foreign" workspace, with a cell of the SAME NAME ("Target") that is
        // also referenced from a cell inside it — this must never be counted against the CURRENT
        // workspace's Target, and Remove-Cell in the current workspace must never touch it.
        var foreignWs = Path.Combine(_tempDir, "foreignWorkspace");
        var foreignTargetDir = Path.Combine(foreignWs, "Target");
        Directory.CreateDirectory(foreignTargetDir);
        File.WriteAllText(Path.Combine(foreignTargetDir, CellFolder.CcellFileName), "{}");
        var foreignReferencerDir = Path.Combine(foreignWs, "Referencer");
        Directory.CreateDirectory(foreignReferencerDir);
        File.WriteAllText(Path.Combine(foreignReferencerDir, CellFolder.CcellFileName), "{}");
        WriteLayoutReferencing(
            Path.Combine(foreignReferencerDir, "layout", "cell.clay"), "../../Target");

        var count = CellUsageScanner.CountReferencingCells(currentWs, targetDir);

        Assert.Equal(1, count); // only the in-workspace Referencer counts, never the foreign one

        // Rename must likewise never touch the foreign workspace's own identically-named cell/files.
        var beforeForeignClay = File.ReadAllText(Path.Combine(foreignReferencerDir, "layout", "cell.clay"));
        CellUsageScanner.RewriteCellReferences(currentWs, "Target", "Renamed", out _);
        var afterForeignClay = File.ReadAllText(Path.Combine(foreignReferencerDir, "layout", "cell.clay"));

        Assert.Equal(beforeForeignClay, afterForeignClay); // untouched
        Assert.Contains("Renamed",
            File.ReadAllText(Path.Combine(referencerDir, "layout", "cell.clay"))); // in-workspace one WAS rewritten
    }
}

/// <summary>§4 item 3 — the tab header tint. <see cref="ForeignDocumentTintConverter"/> is
/// plain-data (Avalonia.Media types only, no window/runtime needed) and is fully unit-testable; the
/// App.axaml HeaderTemplate override itself cannot be pixel-verified headlessly (matching every prior
/// phase's own note on this), so a source-scan pins the wiring directly, mirroring
/// LayoutContextMenuStackingTests.cs's own "cannot construct a Control headlessly" fallback.</summary>
public sealed class ForeignDocumentTabTintTests
{
    [Fact]
    public void Converter_Foreign_ReturnsTintedBrush_NotTransparent()
    {
        var result = ForeignDocumentTintConverter.Instance.Convert(
            true, typeof(Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);

        var brush = Assert.IsType<Avalonia.Media.SolidColorBrush>(result);
        Assert.NotEqual(0, brush.Color.A); // not fully transparent
        Assert.NotEqual(Avalonia.Media.Colors.Red, brush.Color); // R-fgn-7: never red (that means error)
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Converter_NotForeignOrUnbound_ReturnsTransparent(object? value)
    {
        var result = ForeignDocumentTintConverter.Instance.Convert(
            value, typeof(Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Same(Avalonia.Media.Brushes.Transparent, result);
    }

    [Fact]
    public void AppAxaml_DocumentControlHeaderTemplate_BindsIsForeignThroughTheTintConverter()
    {
        // App.axaml + the two shared style/resource files: the application-scope styles moved into
        // Styles/CircuitRfStyles.axaml at H8 so both Applications include one copy (R-h8-6). The
        // wiring being pinned here is unchanged; where it is declared is not the claim.
        var appAxamlPath = FindAppAxaml();
        var dir = Path.GetDirectoryName(appAxamlPath)!;
        var xml = File.ReadAllText(appAxamlPath)
                + File.ReadAllText(Path.Combine(dir, "Styles", "CircuitRfStyles.axaml"))
                + File.ReadAllText(Path.Combine(dir, "Styles", "CircuitRfResources.axaml"));

        Assert.Contains("dockCtrl|DocumentControl", xml);
        Assert.Contains("HeaderTemplate", xml);
        Assert.Contains("IsForeign", xml);
        Assert.Contains("ForeignDocumentTintConverter", xml);
    }

    private static string FindAppAxaml()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Ui", "App.axaml");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate src/Ui/App.axaml from test output directory.");
    }
}
