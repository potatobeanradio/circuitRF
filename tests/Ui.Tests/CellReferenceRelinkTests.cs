// ================================================================
//  CellReferenceRelinkTests.cs — owner, 2026-09-04:
//
//    "If I place a referenced cell into a schematic, then remove the cell reference, the instance
//     becomes Not Found. How can I relink it back?" — and, separately: adding the reference back
//     left the glyph reading Not Found "until I dragged the instance".
//
//  Two things, and they are independent:
//    1. Re-reference Cell… — search the likely places first, ask only when that fails or is
//       ambiguous, and rewrite the document only when the reference itself has to change.
//    2. The stale render. A schematic's render model carries each component's resolution STATE,
//       computed when the model is built — so dropping the resolver caches fixes what the next
//       resolution answers and nothing on screen. The drag was doing the real work: it was an edit,
//       and an edit rebuilds the model. Every gesture that changes this workspace's references now
//       goes through one method that rebuilds.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

// Both process-global invalidators live in this class's fixture — CellSymbolResolver.InvalidateAll
// and the walk-up memo (which also drops CellStat's and WorkspaceWritability's), so it runs in the
// collection that keeps those out of each other's way. See CellStatGlobalsCollection.
[Collection(CellStatGlobalsCollection.Name)]
public sealed class CellReferenceRelinkTests : IDisposable
{
    private readonly string _stem;
    private readonly string _mine;
    private readonly string _theirs;

    public CellReferenceRelinkTests()
    {
        _stem   = Path.Combine(Path.GetTempPath(), "Relink_" + Guid.NewGuid().ToString("N")[..8]);
        _mine   = Path.Combine(_stem, "mine");
        _theirs = Path.Combine(_stem, "theirs");
        MakeWorkspace(_mine);
        MakeWorkspace(_theirs);
        CellSymbolResolver.InvalidateAll();
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_stem, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static void MakeWorkspace(string dir)
    {
        Directory.CreateDirectory(dir);
        WorkspacePersistence.SaveToFile(Path.Combine(dir, ".cws"), new CwsFile());
    }

    /// <summary>A cell with a one-pin primary symbol — enough to resolve.</summary>
    private static string MakeCell(string root, string relPath)
    {
        string parent = Path.Combine(
            root, Path.GetDirectoryName(relPath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
        Directory.CreateDirectory(parent);
        string name    = Path.GetFileName(relPath);
        string cellDir = CellFolder.CreateCellFolder(parent, name);

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol(
                primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
                pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
                portCount:  2));
        return cellDir;
    }

    private void ReferenceCell(string alias, string cellRelPath)
    {
        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(_mine, ".cws"));
        cws.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = alias, Path = Path.Combine(_theirs, ".cws"), CellsOnly = true }];
        cws.ReferencedCells = [ExternalCellRef.RefFor(alias, cellRelPath)];
        WorkspacePersistence.SaveToFile(Path.Combine(_mine, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
    }

    private void DropTheReference()
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_mine, ".cws"), new CwsFile());
        WorkspaceRootFinder.InvalidateCache();
    }

    // ── 1. The search ─────────────────────────────────────────────────────────

    [Fact]
    public void AReferenceThatResolves_IsReportedAsSuchAndNothingIsGuessed()
    {
        MakeCell(_theirs, "Amp");
        ReferenceCell("theirs", "Amp");

        var found = CellReferenceRepair.Find("ws://theirs/Amp", _mine, [_mine, _theirs]);
        Assert.Equal(CellRefFoundBy.AlreadyResolves, found.FoundBy);
    }

    [Fact]
    public void AfterTheReferenceIsRemoved_TheCellIsFoundAtTheSamePath()
    {
        MakeCell(_theirs, "cells/Amp");
        ReferenceCell("theirs", "cells/Amp");
        DropTheReference();

        // Nothing resolves any more — that is the user's "Not Found".
        Assert.Null(ExternalCellRef.ResolveCellDir("ws://theirs/cells/Amp", _mine));

        var found = CellReferenceRepair.Find("ws://theirs/cells/Amp", _mine, [_mine, _theirs]);
        Assert.Equal(CellRefFoundBy.SamePath, found.FoundBy);
        Assert.Equal(Path.GetFullPath(Path.Combine(_theirs, "cells", "Amp")),
                     Path.GetFullPath(found.CellDir!));
    }

    [Fact]
    public void AMovedCell_IsFoundByItsNameWhenExactlyOneCandidateHasIt()
    {
        MakeCell(_theirs, "archive/2026/Amp");        // not where the reference says
        var found = CellReferenceRepair.Find("ws://theirs/cells/Amp", _mine, [_mine, _theirs]);

        Assert.Equal(CellRefFoundBy.UniqueName, found.FoundBy);
        Assert.EndsWith("Amp", found.CellDir!, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoCellsOfTheSameName_AreNotGuessedBetween()
    {
        MakeCell(_theirs, "a/Amp");
        MakeCell(_theirs, "b/Amp");

        // Guessing wrong here is worse than asking: the picker is the answer, not a coin toss.
        var found = CellReferenceRepair.Find("ws://theirs/cells/Amp", _mine, [_mine, _theirs]);
        Assert.Equal(CellRefFoundBy.NotFound, found.FoundBy);
        Assert.Null(found.CellDir);
    }

    [Fact]
    public void APlainRelativeReference_IsSearchedTheSameWay()
    {
        MakeCell(_mine, "parts/Amp");
        string schDir = Path.Combine(_mine, "Board", "schematic");
        Directory.CreateDirectory(schDir);

        var found = CellReferenceRepair.Find("../../gone/Amp", schDir, [_mine]);
        Assert.Equal(CellRefFoundBy.UniqueName, found.FoundBy);
        Assert.Equal(Path.GetFullPath(Path.Combine(_mine, "parts", "Amp")),
                     Path.GetFullPath(found.CellDir!));
    }

    [Theory]
    [InlineData("pdk://SomeKit/nfet")]
    [InlineData("wbond://Board")]
    [InlineData("")]
    public void AReferenceThatNamesNoFolder_IsNotRepairableByPointingAtOne(string cellRef)
    {
        // A kit part and a wBond also read NotFound; "locate the cell folder" is not their repair.
        Assert.False(CellReferenceRepair.IsRepairable(cellRef));
        Assert.Equal(CellRefFoundBy.NotFound,
                     CellReferenceRepair.Find(cellRef, _mine, [_mine, _theirs]).FoundBy);
    }

    [Fact]
    public void AnOrdinaryCellReference_IsRepairable()
    {
        Assert.True(CellReferenceRepair.IsRepairable("ws://theirs/Amp"));
        Assert.True(CellReferenceRepair.IsRepairable("../Amp"));
    }

    // ── 2. The rewrite ────────────────────────────────────────────────────────

    [Fact]
    public void Relinking_RewritesEveryInstanceOfThatReference_AsOneUndoEntry()
    {
        var model = new SchematicEditModel { SchematicDirectory = _mine };
        model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "ws://gone/Amp" });
        model.Components.Add(new EditableComponent { InstanceName = "X2", Symbol = SymbolKind.Generic, CellRef = "ws://gone/Amp" });
        model.Components.Add(new EditableComponent { InstanceName = "X3", Symbol = SymbolKind.Generic, CellRef = "ws://gone/Mixer" });

        var vm = new SchematicViewModel(model);
        Assert.Equal(2, vm.RelinkCellReferences("ws://gone/Amp", "ws://theirs/Amp"));

        Assert.Equal("ws://theirs/Amp", model.Components[0].CellRef);
        Assert.Equal("ws://theirs/Amp", model.Components[1].CellRef);
        Assert.Equal("ws://gone/Mixer", model.Components[2].CellRef);   // a different cell is not touched

        // One gesture, one undo — repairing half a reference is not a state anyone asked for.
        vm.UndoRedo.Undo();
        Assert.Equal("ws://gone/Amp", model.Components[0].CellRef);
        Assert.Equal("ws://gone/Amp", model.Components[1].CellRef);
    }

    [Fact]
    public void RelinkingToTheSameReference_ChangesNothing()
    {
        var model = new SchematicEditModel { SchematicDirectory = _mine };
        model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "ws://theirs/Amp" });
        var vm = new SchematicViewModel(model);

        // The alias was restored under its old name, so the document is already right — and must not
        // get an undo entry for a rewrite that rewrites nothing.
        Assert.Equal(0, vm.RelinkCellReferences("ws://theirs/Amp", "ws://theirs/Amp"));
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── 2b. The same gesture in the LAYOUT editor ─────────────────────────────

    [Fact]
    public void LayoutRelinking_RewritesEveryInstanceOfThatReference_AsOneUndoEntry()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "ws://gone/Amp",   X = 0,     Y = 0, Mag = 1.0 });
        view.Instances.Add(new LayoutInstance { CellRef = "ws://gone/Amp",   X = 10000, Y = 0, Mag = 1.0 });
        view.Instances.Add(new LayoutInstance { CellRef = "ws://gone/Mixer", X = 20000, Y = 0, Mag = 1.0 });

        var vm = new LayoutEditorViewModel(view, Path.Combine(_mine, "Board", "layout", "Board.clay"));
        Assert.Equal(2, vm.RelinkCellReferences("ws://gone/Amp", "ws://theirs/Amp"));

        Assert.Equal("ws://theirs/Amp", view.Instances[0].CellRef);
        Assert.Equal("ws://theirs/Amp", view.Instances[1].CellRef);
        Assert.Equal("ws://gone/Mixer", view.Instances[2].CellRef);

        vm.UndoRedo.Undo();
        Assert.Equal("ws://gone/Amp", view.Instances[0].CellRef);
        Assert.Equal("ws://gone/Amp", view.Instances[1].CellRef);
    }

    /// <summary>
    /// The item is offered on a BROKEN instance under the click, and on nothing else — the same rule
    /// the schematic's menu follows, asked of the layout's own hit-test.
    /// </summary>
    [Fact]
    public void TheLayoutMenuItem_FindsABrokenInstanceUnderTheClick_AndNotAResolvedOne()
    {
        string amp     = MakeCell(_theirs, "Amp");
        string boardDir = MakeCell(_mine, "Board");
        string layDir   = CellFolder.SubFolderPath(boardDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);

        // Two instances side by side: one that resolves (a relative reference to a cell of this
        // workspace) and one that does not (an alias nothing declares).
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance
        { CellRef = Path.GetRelativePath(layDir, amp).Replace('\\', '/'), X = 0, Y = 0, Mag = 1.0 });
        view.Instances.Add(new LayoutInstance { CellRef = "ws://gone/Amp", X = 500_000, Y = 0, Mag = 1.0 });

        // The resolving one needs a layout view of its own, or it is broken for a different reason.
        var ampLay = CellFolder.SubFolderPath(amp, ViewType.Layout);
        Directory.CreateDirectory(ampLay);
        var ampView = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        ampView.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 2000 });
        LayoutPersistence.SaveToFile(Path.Combine(ampLay, "Amp.clay"), ampView);

        string clay = Path.Combine(layDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        CellLayoutResolver.InvalidateAll();

        var vm = new LayoutEditorViewModel(view, clay);

        var onBroken = vm.FindBrokenInstanceForContextMenu(500_000, 0, tolDbu: 50_000);
        Assert.NotNull(onBroken);
        Assert.Equal("ws://gone/Amp", onBroken!.Value.Instance.CellRef);

        Assert.Null(vm.FindBrokenInstanceForContextMenu(0, 0, tolDbu: 1000));      // the one that resolves
        Assert.Null(vm.FindBrokenInstanceForContextMenu(9_000_000, 9_000_000, 1000)); // empty canvas
    }

    [Fact]
    public void TheLayoutViewOffersTheItemThroughTheHierarchyHost()
    {
        // The canvas builds the layout menu, but this item is workspace-level and is contributed by
        // the view — the same route Push Into Cell takes. A source scan, because the view is a real
        // Avalonia control this headless suite cannot construct.
        string view = StripComments(ReadRepoFile(
            Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs")));

        Assert.Contains("Re-reference Cell", view, StringComparison.Ordinal);
        Assert.Contains("FindBrokenInstanceForContextMenu(", view, StringComparison.Ordinal);
        Assert.Contains("host.ReReferenceInstanceCellAsync(doc, hit.Instance)", view, StringComparison.Ordinal);
    }

    // ── 2c. Removing a reference REALLY removes it ────────────────────────────

    /// <summary>
    /// Owner, 2026-09-04: "I added a layout instance reference, then removed my reference from the
    /// Project tree, but the layout instance still resolves… I even quit the app and restarted."
    ///
    /// <para>The alias behind the reference used to be kept alive by the very instances the removal
    /// dialog had just warned would stop resolving — so the app did the opposite of what it promised,
    /// and the outcome depended on whether a document happened to be SAVED (an unsaved one counts
    /// zero, which is why the schematic broke and the layout did not).</para>
    /// </summary>
    [Fact]
    public void RemovingTheReference_BreaksTheInstancesThatPlacedIt_RatherThanKeepingTheAliasAlive()
    {
        MakeCell(_theirs, "Amp");
        ReferenceCell("theirs", "Amp");

        // A saved layout in this workspace that places the referenced cell — what kept the alias.
        string board = MakeCell(_mine, "Board");
        string layDir = CellFolder.SubFolderPath(board, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "ws://theirs/Amp", X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(layDir, "Board.clay"), view);

        Assert.Equal(1, CellUsageScanner.CountCellsUsingWorkspaceAlias(_mine, "theirs"));
        Assert.NotNull(ExternalCellRef.ResolveCellDir("ws://theirs/Amp", layDir));

        Assert.True(WorkspaceViewModel.RemoveReferencedCell(
            _mine, "ws://theirs/Amp", out string? err, out string? removedAlias), err);

        Assert.Equal("theirs", removedAlias);
        Assert.Empty(WorkspacePersistence.LoadFromFile(Path.Combine(_mine, ".cws")).ReferencedWorkspaces ?? []);
        // The instance no longer resolves — and it survives a restart, because it is the .cws that
        // says so and nothing is cached across one.
        Assert.Null(ExternalCellRef.ResolveCellDir("ws://theirs/Amp", layDir));
        Assert.True(Directory.Exists(Path.Combine(_theirs, "Amp")), "nothing is deleted on disk");
    }

    // ── 3. The stale render, and what actually clears it ──────────────────────

    [Fact]
    public void RestoringTheReference_ShowsThroughOnlyAfterTheRenderModelIsRebuilt()
    {
        MakeCell(_theirs, "Amp");
        string schDir = Path.Combine(_mine, "Board", "schematic");
        Directory.CreateDirectory(schDir);

        var model = new SchematicEditModel { SchematicDirectory = schDir };
        model.Components.Add(new EditableComponent
        { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "ws://theirs/Amp" });
        var vm = new SchematicViewModel(model);

        // No reference declared: this is the "Not Found" glyph the owner saw.
        Assert.Equal(CellSymbolState.NotFound, Assert.Single(vm.RenderModel!.Components).CellRefState);

        ReferenceCell("theirs", "Amp");
        CellSymbolResolver.InvalidateAll();

        // Dropping the caches fixes what the NEXT resolution answers…
        Assert.Equal(CellSymbolState.Resolved,
                     CellSymbolResolver.Resolve("ws://theirs/Amp", schDir).State);
        // …and changes nothing on screen, because the state lives in the built render model.
        Assert.Equal(CellSymbolState.NotFound, Assert.Single(vm.RenderModel!.Components).CellRefState);

        vm.TriggerRebuild();
        Assert.Equal(CellSymbolState.Resolved, Assert.Single(vm.RenderModel!.Components).CellRefState);
    }

    // ── 4. Every reference gesture goes through the one refresh ───────────────

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    /// <summary>Comments explaining why something is NOT done read identically to doing it.</summary>
    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    [Fact]
    public void TheRefreshRebuildsOpenDocuments_AndEveryReferenceGestureUsesIt()
    {
        var relink = StripComments(ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.CellRelink.cs")));

        int at = relink.IndexOf("private void RefreshAfterReferenceChange()", StringComparison.Ordinal);
        Assert.True(at >= 0, "RefreshAfterReferenceChange is the one place this is written down.");
        string body = relink[at..];

        // The caches answer the next question; the rebuild is what the user sees.
        Assert.Contains("CellSymbolResolver.InvalidateAll();", body, StringComparison.Ordinal);
        Assert.Contains("WorkspaceRootFinder.InvalidateCache();", body, StringComparison.Ordinal);
        Assert.Contains("RebuildOpenSchematics();", body, StringComparison.Ordinal);
        Assert.Contains("RepaintOpenLayouts();", body, StringComparison.Ordinal);

        // Adding, promoting, removing — a gesture that changes a reference and does not refresh is
        // the bug this test exists for, in both directions. Asked per METHOD, because a tree-only
        // Refresh is still right for a gesture that changes no reference (dropping a loose file in).
        foreach (var (file, method) in new[]
        {
            ("WorkspaceViewModel.ExternalRefs.cs", "private async Task ReferenceWorkspace("),
            ("WorkspaceViewModel.CellDrop.cs",     "private void ReferenceExternalCell("),
            ("WorkspaceViewModel.CellDrop.cs",     "private async Task CopyExternalCellAsync("),
            ("WorkspaceViewModel.cs",              "public async Task RemoveWorkspaceReferenceAsync("),
            ("WorkspaceViewModel.cs",              "public async Task RemoveCellReferenceAsync("),
        })
        {
            string src   = StripComments(ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", file)));
            int    start = src.IndexOf(method, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{file}: {method}");

            string tail = src[start..Math.Min(src.Length, start + 5000)];
            Assert.Contains("RefreshAfterReferenceChange();", tail, StringComparison.Ordinal);
        }
    }
}
