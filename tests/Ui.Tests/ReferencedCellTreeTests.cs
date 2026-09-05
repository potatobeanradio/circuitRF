// ================================================================
//  ReferencedCellTreeTests.cs — owner, 2026-09-04:
//
//    Referencing a CELL from another workspace pulled that whole workspace into the Project Tree,
//    so taking one cell from a colleague's project listed every cell they had. A reference to one
//    cell must list one cell — at the root, with a network-file glyph — and the "Referenced
//    Workspaces" heading is gone with it: a row's own icon already says it is a reference.
//
//  What is gated here:
//    1. the .cws records the CELL, and the alias it resolves through is marked CellsOnly,
//    2. the tree draws ONE row for it and none of the other workspace's other cells,
//    3. a whole referenced workspace is a ROOT row, not a group child,
//    4. each of the two new filter toggles hides its own branch — cells and all,
//    5. removing the reference deletes the listing, keeps the other workspace, and takes the
//       now-unused alias with it,
//    6. ws:// addressing is untouched: the CellsOnly flag is a rendering decision only.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Material.Icons;
using Xunit;

namespace CircuitRF.Ui.Tests;

// Both process-global invalidators live in this class's fixture — CellSymbolResolver.InvalidateAll
// and the walk-up memo (which also drops CellStat's and WorkspaceWritability's), so it runs in the
// collection that keeps those out of each other's way. See CellStatGlobalsCollection.
[Collection(CellStatGlobalsCollection.Name)]
public class ReferencedCellTreeTests : IDisposable
{
    private readonly string _stem;
    private readonly string _mine;
    private readonly string _theirs;

    public ReferencedCellTreeTests()
    {
        _stem   = Path.Combine(Path.GetTempPath(), "RefCell_" + Guid.NewGuid().ToString("N")[..8]);
        _mine   = Path.Combine(_stem, "mine");
        _theirs = Path.Combine(_stem, "theirs");
        MakeWorkspace(_mine);
        MakeWorkspace(_theirs);
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_stem, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void MakeWorkspace(string dir, CwsFile? cws = null)
    {
        Directory.CreateDirectory(dir);
        WorkspacePersistence.SaveToFile(Path.Combine(dir, ".cws"), cws ?? new CwsFile());
    }

    private static string MakeCell(string root, string relPath)
    {
        string parent = Path.Combine(
            root, Path.GetDirectoryName(relPath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
        Directory.CreateDirectory(parent);
        string name    = Path.GetFileName(relPath);
        string cellDir = CellFolder.CreateCellFolder(parent, name);

        // A view file, so the cell has a view SUB-FOLDER in the tree — the scanner renders none for
        // an empty one, and the folder rows are what the icon rule below is about.
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol(
                primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
                pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
                portCount:  2));
        return cellDir;
    }

    private CwsFile MyCws() => WorkspacePersistence.LoadFromFile(Path.Combine(_mine, ".cws"));

    private void WriteMyCws(CwsFile cws)
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_mine, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
    }

    /// <summary>The state the reference gesture leaves behind: one alias, marked CellsOnly, and one
    /// cell listed through it.</summary>
    private void ReferenceOneCell(string alias, string cellRelPath)
    {
        var cws = MyCws();
        cws.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = alias, Path = Path.Combine(_theirs, ".cws"), CellsOnly = true }];
        cws.ReferencedCells = [ExternalCellRef.RefFor(alias, cellRelPath)];
        WriteMyCws(cws);
    }

    private static ProjectTreeNodeViewModel Vm(ProjectTreeNode root, ProjectTreeFilterState filter)
        => new(root, filter);

    private static bool Shows(ProjectTreeNodeViewModel root, string name)
        => root.FilteredChildren.Any(c => c.Name == name);

    // ── 1. What the .cws records ──────────────────────────────────────────────

    [Fact]
    public void ReferencingACell_RecordsTheCell_AndACellsOnlyAlias()
    {
        MakeCell(_theirs, "Amp");
        MakeCell(_theirs, "Mixer");

        Assert.True(WorkspaceViewModel.AddReferencedWorkspace(
            _mine, "theirs", Path.Combine(_theirs, ".cws"), out string? err, cellsOnly: true), err);
        Assert.True(WorkspaceViewModel.AddReferencedCell(
            _mine, ExternalCellRef.RefFor("theirs", "Amp"), out err, out bool already), err);
        Assert.False(already);

        var cws = MyCws();
        Assert.True(Assert.Single(cws.ReferencedWorkspaces!).CellsOnly);
        Assert.Equal("ws://theirs/Amp", Assert.Single(cws.ReferencedCells!));

        // Idempotent: the same cell dragged across twice is not two rows and not an error.
        Assert.True(WorkspaceViewModel.AddReferencedCell(
            _mine, ExternalCellRef.RefFor("theirs", "Amp"), out err, out already), err);
        Assert.True(already);
        Assert.Single(MyCws().ReferencedCells!);
    }

    // ── 2. One cell in, one row out ───────────────────────────────────────────

    [Fact]
    public void AReferencedCell_IsOneRootRow_AndBringsNoOtherCellWithIt()
    {
        MakeCell(_theirs, "Amp");
        MakeCell(_theirs, "Mixer");
        MakeCell(_theirs, "passives/R0402");
        ReferenceOneCell("theirs", "Amp");

        var tree = WorkspaceScanner.Scan(_mine);

        var row = Assert.Single(tree.Children.Where(c => c.IsReferencedCell));
        Assert.Equal("Amp", row.Name);
        Assert.Equal(NodeKind.Cell, row.Kind);
        Assert.Null(row.WarningReason);

        // Nothing else of theirs came along — not the workspace row, not its other cells.
        Assert.Empty(tree.Children.Where(c => c.Kind == NodeKind.ReferencedWorkspace));
        Assert.Empty(tree.Children.Where(c => c.Name is "Mixer" or "passives"));

        // The same network glyph the referenced WORKSPACE row carries: both rows say "not mine".
        var rowVm = Vm(tree, new ProjectTreeFilterState()).Children.Single(c => c.IsReferencedCell);
        Assert.Equal(MaterialIconKind.FolderNetworkOutline, rowVm.IconKind);

        // …and so does every folder inside it, opened up.
        var viewFolder = rowVm.Children.Single(c => c.Kind == NodeKind.CellViewFolder);
        Assert.Equal(MaterialIconKind.FolderNetworkOutline, viewFolder.IconKind);
        // The view FILES are unchanged — a file is a file wherever it lives.
        Assert.Equal(MaterialIconKind.FileOutline,
                     viewFolder.Children.Single(c => c.Kind == NodeKind.ViewFile).IconKind);
    }

    /// <summary>
    /// The marking stops at a referenced CELL. A cell reached through a referenced WORKSPACE is
    /// browsed like any other cell — the branch it hangs under already says where it came from, and
    /// marking every folder of a 200-cell library would say it two hundred times.
    /// </summary>
    [Fact]
    public void AReferencedWorkspacesOwnCellFolders_KeepThePlainFolderGlyph()
    {
        MakeCell(_theirs, "Amp");
        var cws = MyCws();
        cws.ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "theirs", Path = Path.Combine(_theirs, ".cws") }];
        WriteMyCws(cws);

        var root = Vm(WorkspaceScanner.Scan(_mine), new ProjectTreeFilterState());
        var ws   = root.Children.Single(c => c.Kind == NodeKind.ReferencedWorkspace);
        var cell = ws.Children.Single(c => c.Kind == NodeKind.Cell);

        Assert.Equal(MaterialIconKind.IntegratedCircuitChip, cell.IconKind);
        Assert.Equal(MaterialIconKind.FolderOutline,
                     cell.Children.Single(c => c.Kind == NodeKind.CellViewFolder).IconKind);
    }

    [Fact]
    public void AnUnresolvableReference_IsARowCarryingItsReason_NotAnAbsentRow()
    {
        // The alias names a workspace that is not there any more.
        var cws = MyCws();
        cws.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "gone", Path = Path.Combine(_stem, "gone", ".cws"), CellsOnly = true }];
        cws.ReferencedCells = ["ws://gone/Amp"];
        WriteMyCws(cws);

        var row = Assert.Single(WorkspaceScanner.Scan(_mine).Children.Where(c => c.IsReferencedCell));
        Assert.Equal("Amp", row.Name);
        Assert.NotNull(row.WarningReason);
    }

    // ── 3. A referenced WORKSPACE is a root row, with no heading above it ──────

    [Fact]
    public void AReferencedWorkspace_IsARootRow_WithNoGroupHeading()
    {
        MakeCell(_theirs, "Amp");
        var cws = MyCws();
        cws.ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "theirs", Path = Path.Combine(_theirs, ".cws") }];
        WriteMyCws(cws);

        var tree = WorkspaceScanner.Scan(_mine);
        var ws   = Assert.Single(tree.Children.Where(c => c.Kind == NodeKind.ReferencedWorkspace));
        Assert.Equal("theirs", ws.Name);
        Assert.Equal("Amp", Assert.Single(ws.Children).Name);
        Assert.DoesNotContain(tree.Children, c => c.Name == "Referenced Workspaces");
    }

    // ── 4. Each toggle owns its branch, top to bottom ──────────────────────────

    [Fact]
    public void TheReferencedCellsToggle_HidesTheRowAndItsViews()
    {
        MakeCell(_mine,   "MyCell");
        MakeCell(_theirs, "Amp");
        ReferenceOneCell("theirs", "Amp");

        var filter = new ProjectTreeFilterState();
        var root   = Vm(WorkspaceScanner.Scan(_mine), filter);

        Assert.True(Shows(root, "Amp"));

        filter.ReferencedCells = false;
        Assert.False(Shows(root, "Amp"));
        Assert.True(Shows(root, "MyCell"));      // the workspace's own cells are untouched
    }

    [Fact]
    public void TheReferencedWorkspacesToggle_HidesTheBranchEvenWhileCellsAreOn()
    {
        MakeCell(_mine,   "MyCell");
        MakeCell(_theirs, "Amp");
        var cws = MyCws();
        cws.ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "theirs", Path = Path.Combine(_theirs, ".cws") }];
        WriteMyCws(cws);

        var filter = new ProjectTreeFilterState();
        var root   = Vm(WorkspaceScanner.Scan(_mine), filter);
        Assert.True(Shows(root, "theirs"));

        // The branch's own cells rode the Cells toggle before this, and ancestor-preservation then
        // kept the branch visible no matter what its own checkbox said.
        filter.ReferencedWorkspaces = false;
        Assert.False(Shows(root, "theirs"));
        Assert.True(Shows(root, "MyCell"));
    }

    [Fact]
    public void TurningCellsOff_LeavesAReferencedCellAlone()
    {
        MakeCell(_mine,   "MyCell");
        MakeCell(_theirs, "Amp");
        ReferenceOneCell("theirs", "Amp");

        var filter = new ProjectTreeFilterState { Cells = false };
        var root   = Vm(WorkspaceScanner.Scan(_mine), filter);

        Assert.False(Shows(root, "MyCell"));
        Assert.True(Shows(root, "Amp"));
    }

    // ── 5. Removing the reference ─────────────────────────────────────────────

    [Fact]
    public void RemovingTheReference_DropsTheListing_AndTheAliasNothingElseNeeds()
    {
        MakeCell(_theirs, "Amp");
        ReferenceOneCell("theirs", "Amp");

        Assert.True(WorkspaceViewModel.RemoveReferencedCell(
            _mine, "ws://theirs/Amp", out string? err, out string? removedAlias), err);

        Assert.Equal("theirs", removedAlias);
        var cws = MyCws();
        Assert.Empty(cws.ReferencedCells ?? []);
        Assert.Empty(cws.ReferencedWorkspaces ?? []);
        Assert.True(Directory.Exists(Path.Combine(_theirs, "Amp")), "nothing is deleted on disk");
    }

    [Fact]
    public void RemovingOneOfTwoCells_KeepsTheAliasTheOtherStillNeeds()
    {
        MakeCell(_theirs, "Amp");
        MakeCell(_theirs, "Mixer");

        var cws = MyCws();
        cws.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "theirs", Path = Path.Combine(_theirs, ".cws"), CellsOnly = true }];
        cws.ReferencedCells = ["ws://theirs/Amp", "ws://theirs/Mixer"];
        WriteMyCws(cws);

        Assert.True(WorkspaceViewModel.RemoveReferencedCell(
            _mine, "ws://theirs/Amp", out string? err, out string? removedAlias), err);

        Assert.Null(removedAlias);
        Assert.Equal("ws://theirs/Mixer", Assert.Single(MyCws().ReferencedCells!));
        Assert.Single(MyCws().ReferencedWorkspaces!);
    }

    // ── 6. Addressing is untouched, and the whole-workspace gesture promotes ───

    [Fact]
    public void ACellsOnlyAlias_StillResolvesEveryWsReferenceThroughIt()
    {
        string theirCell = MakeCell(_theirs, "Amp");
        MakeCell(_theirs, "Mixer");
        ReferenceOneCell("theirs", "Amp");

        // Not only the listed cell: the flag is about what the TREE shows, never about addressing —
        // a document may legitimately place any cell of that workspace through the alias.
        Assert.Equal(Path.GetFullPath(theirCell),
            Path.GetFullPath(ExternalCellRef.ResolveCellDir("ws://theirs/Amp", _mine)!));
        Assert.Equal(Path.GetFullPath(Path.Combine(_theirs, "Mixer")),
            Path.GetFullPath(ExternalCellRef.ResolveCellDir("ws://theirs/Mixer", _mine)!));
    }

    [Fact]
    public void ReferencingTheWholeWorkspace_PromotesTheExistingAlias_RatherThanAddingASecond()
    {
        MakeCell(_theirs, "Amp");
        MakeCell(_theirs, "Mixer");
        ReferenceOneCell("theirs", "Amp");

        Assert.Equal("theirs", WorkspaceViewModel.ExistingAliasFor(_mine, _theirs));
        Assert.True(WorkspaceViewModel.ShowReferencedWorkspace(_mine, "theirs", out string? err), err);
        WorkspaceRootFinder.InvalidateCache();

        var entry = Assert.Single(MyCws().ReferencedWorkspaces!);
        Assert.False(entry.CellsOnly);

        // One alias, one workspace row — and the cell listing stands on its own beside it.
        var tree = WorkspaceScanner.Scan(_mine);
        Assert.Single(tree.Children.Where(c => c.Kind == NodeKind.ReferencedWorkspace));
        Assert.Single(tree.Children.Where(c => c.IsReferencedCell));
    }

    // ── 7. The context menu, as the .axaml actually spells it ─────────────────
    //
    //  WorkspaceWindow and this view are real Avalonia controls and cannot be constructed in this
    //  headless suite, so menu STRUCTURE is checked by parsing the real .axaml as XML — the
    //  established precedent here (FileMenuRestructureTests says the same). Visibility is evaluated
    //  against a REAL node view-model through the same property each binding names, so a renamed or
    //  mistyped property fails this test rather than silently hiding an item in the app.

    private static string RepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return Path.Combine(dir!, relativePath);
    }

    private sealed record MenuEntry(bool IsSeparator, string Label, string? VisibilityBinding);

    /// <summary>The node context menu's direct children, in source order.</summary>
    private static List<MenuEntry> NodeContextMenu()
    {
        var doc  = XDocument.Load(RepoFile(Path.Combine("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml")));
        var menu = doc.Descendants()
            .Single(e => e.Name.LocalName == "ContextMenu"
                      && (string?)e.Attribute("Opening") == "OnNodeContextMenuOpening");

        var entries = new List<MenuEntry>();
        foreach (var child in menu.Elements())
        {
            string name = child.Name.LocalName;
            if (name is not ("Separator" or "MenuItem")) continue;
            entries.Add(new MenuEntry(
                name == "Separator",
                (string?)child.Attribute("Header") ?? "",
                (string?)child.Attribute("IsVisible")));
        }
        Assert.NotEmpty(entries);
        return entries;
    }

    /// <summary>Resolves one <c>{Binding Prop}</c> / <c>{Binding !Prop}</c> against a real node VM.
    /// An item with no IsVisible is always shown.</summary>
    private static bool Visible(MenuEntry entry, ProjectTreeNodeViewModel vm)
    {
        if (entry.VisibilityBinding is not { } b) return true;

        string expr = b.Trim();
        Assert.StartsWith("{Binding ", expr, StringComparison.Ordinal);
        expr = expr["{Binding ".Length..].TrimEnd('}').Trim();

        bool negate = expr.StartsWith('!');
        if (negate) expr = expr[1..].Trim();

        var prop = typeof(ProjectTreeNodeViewModel).GetProperty(expr);
        Assert.True(prop is not null, $"ProjectTreeNodeViewModel has no '{expr}' (bound by '{entry.Label}').");
        bool value = (bool)prop!.GetValue(vm)!;
        return negate ? !value : value;
    }

    private static void AssertNoDoubleSeparators(ProjectTreeNodeViewModel vm, string what)
    {
        var shown = NodeContextMenu().Where(e => Visible(e, vm)).ToList();

        for (int i = 1; i < shown.Count; i++)
            Assert.False(shown[i].IsSeparator && shown[i - 1].IsSeparator,
                $"{what}: two adjacent separators (before '{(i + 1 < shown.Count ? shown[i + 1].Label : "end")}').");

        if (shown.Count > 0)
        {
            Assert.False(shown[0].IsSeparator,  $"{what}: the menu opens with a separator.");
            Assert.False(shown[^1].IsSeparator, $"{what}: the menu ends with a separator.");
        }
    }

    private ProjectTreeNodeViewModel RootVm()
    {
        MakeCell(_mine, "MyCell");
        MakeCell(_mine, "sub/Nested");          // gives the fixture a plain user folder too
        MakeCell(_theirs, "Amp");
        MakeCell(_theirs, "Mixer");

        var cws = MyCws();
        cws.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "theirs", Path = Path.Combine(_theirs, ".cws") }];
        cws.ReferencedCells = [ExternalCellRef.RefFor("theirs", "Mixer")];
        WriteMyCws(cws);

        return Vm(WorkspaceScanner.Scan(_mine), new ProjectTreeFilterState());
    }

    [Fact]
    public void TheReferencedCellMenu_HasNoTwoAdjacentSeparators()
    {
        var root = RootVm();
        var cell = root.Children.Single(c => c.IsReferencedCell);

        // Everything the "creation actions" separator introduces — New Schematic/Symbol/Layout,
        // Duplicate, Rename — is hidden on someone else's cell, so the separator rode IsCell and sat
        // straight against the next one.
        AssertNoDoubleSeparators(cell, "referenced cell");
    }

    [Fact]
    public void TheOtherNodeMenus_HaveNoTwoAdjacentSeparators()
    {
        var root = RootVm();

        AssertNoDoubleSeparators(root.Children.Single(c => c.Name == "MyCell"), "own cell");
        AssertNoDoubleSeparators(root.Children.Single(c => c.Kind == NodeKind.ReferencedWorkspace),
                                 "referenced workspace");
        AssertNoDoubleSeparators(root.Children.Single(c => c.Kind == NodeKind.UserFolder), "user folder");

        // The workspace ROOT is deliberately absent: the TreeView binds to its children and the panel
        // header names the workspace, so that node is never rendered and its menu never opens.
    }

    // ── 8. Reveal reaches the other workspace's folder ────────────────────────

    [Fact]
    public void AReferencedWorkspaceRow_OffersReveal()
    {
        var root = RootVm();
        var ws   = root.Children.Single(c => c.Kind == NodeKind.ReferencedWorkspace);

        Assert.True(ws.CanReveal, "the row names a real folder on disk — Reveal is the way to it");
        Assert.Equal(Path.GetFullPath(_theirs), Path.GetFullPath(ws.AbsolutePath));

        // And the menu really binds that item, rather than the property merely being true.
        var reveal = NodeContextMenu().Single(e => e.Label == "{Binding RevealLabel}");
        Assert.True(Visible(reveal, ws));
    }
}
