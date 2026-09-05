using System;
using System.IO;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Open Referenced Workspace in New Window</b> on a cell that belongs to ANOTHER workspace (owner,
/// 2026-09-03) — directly above the reveal item, which answers the same "where does this come from?"
/// question by leaving the app.
///
/// <para>The part worth pinning is what the item is gated on: the walk-up to the cell's own ancestor
/// <c>.cws</c>, not the node's position under a Referenced Workspace sub-tree. A library cell that
/// happens to live inside someone else's workspace belongs to that workspace just as much, and a cell
/// reached through a reference but sitting inside THIS workspace does not.</para>
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public class ReferencedWorkspaceContextMenuTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _mine;
    private readonly string _other;

    public ReferencedWorkspaceContextMenuTests()
    {
        _tmp   = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        _mine  = Path.Combine(_tmp, "Mine");
        _other = Path.Combine(_tmp, "Other");
        Directory.CreateDirectory(_mine);
        Directory.CreateDirectory(_other);
        File.WriteAllText(Path.Combine(_mine,  ".cws"), "{}");
        File.WriteAllText(Path.Combine(_other, ".cws"), "{}");
        // A .cws appearing changes which workspace a path belongs to, and the walk-up is memoised.
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        WorkspaceRootFinder.InvalidateCache();
    }

    private string MakeCell(string workspaceRoot, string name)
    {
        string dir = Path.Combine(workspaceRoot, "cells", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".ccell"), "{}");
        return dir;
    }

    private static ProjectTreeNodeViewModel CellVm(string cellDir, WorkspaceViewModel actions) =>
        new(new ProjectTreeNode(NodeKind.Cell, Path.GetFileName(cellDir), cellDir, "", isDirectory: true),
            new ProjectTreeFilterState(), expandedPaths: null, actions: actions);

    private WorkspaceViewModel OpenOnMine()
    {
        var vm = new WorkspaceViewModel { CurrentWorkspacePath = Path.Combine(_mine, ".cws") };
        return vm;
    }

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>A cell living in another workspace names that workspace's <c>.cws</c>.</summary>
    [Fact]
    public void ACellInAnotherWorkspace_NamesThatWorkspace()
    {
        var vm   = OpenOnMine();
        var cell = CellVm(MakeCell(_other, "Amp"), vm);

        Assert.Equal(Path.Combine(_other, ".cws"), vm.ForeignWorkspaceCwsFor(cell));
        Assert.True(cell.CanOpenReferencedWorkspace);
        Assert.True(cell.OpenReferencedWorkspaceCommand.CanExecute(null));
    }

    /// <summary>A cell in the OPEN workspace has nothing to open — the item stays hidden.</summary>
    [Fact]
    public void ACellInTheOpenWorkspace_OffersNothing()
    {
        var vm   = OpenOnMine();
        var cell = CellVm(MakeCell(_mine, "Amp"), vm);

        Assert.Null(vm.ForeignWorkspaceCwsFor(cell));
        Assert.False(cell.CanOpenReferencedWorkspace);
        Assert.False(cell.OpenReferencedWorkspaceCommand.CanExecute(null));
    }

    /// <summary>
    /// A cell in a plain library folder — no ancestor <c>.cws</c> anywhere above it — offers nothing
    /// either. There is no workspace to open, and inventing one from the folder would be a guess.
    /// </summary>
    [Fact]
    public void ACellWithNoAncestorWorkspace_OffersNothing()
    {
        string loose = Path.Combine(_tmp, "LooseLibrary");
        Directory.CreateDirectory(loose);

        var vm   = OpenOnMine();
        var cell = CellVm(MakeCell(loose, "Amp"), vm);

        Assert.Null(vm.ForeignWorkspaceCwsFor(cell));
        Assert.False(cell.CanOpenReferencedWorkspace);
    }

    /// <summary>
    /// The Referenced Workspaces ROW offers it too (owner, 2026-09-03) — its own path IS the other
    /// workspace's root, so the same walk-up answers it, and that row is where a user looking for the
    /// whole project reaches first.
    /// </summary>
    [Fact]
    public void TheReferencedWorkspaceRowOffersItToo()
    {
        var vm  = OpenOnMine();
        var row = new ProjectTreeNodeViewModel(
            new ProjectTreeNode(NodeKind.ReferencedWorkspace, "Other", _other, "", isDirectory: true),
            new ProjectTreeFilterState(), expandedPaths: null, actions: vm);

        Assert.True(row.CanOpenReferencedWorkspace);
        Assert.Equal(Path.Combine(_other, ".cws"), vm.ForeignWorkspaceCwsFor(row));
    }

    /// <summary>
    /// An UNRESOLVED reference does not: its node carries the reason instead of a real path, and the
    /// walk-up from a path that is not there can land on an ancestor workspace that has nothing to do
    /// with the reference.
    /// </summary>
    [Fact]
    public void AnUnresolvedReferenceRowOffersNothing()
    {
        var vm  = OpenOnMine();
        var row = new ProjectTreeNodeViewModel(
            new ProjectTreeNode(NodeKind.ReferencedWorkspace, "Gone",
                                Path.Combine(_mine, "gone", ".cws"), "",
                                warningReason: "Referenced workspace unresolved"),
            new ProjectTreeFilterState(), expandedPaths: null, actions: vm);

        Assert.False(row.CanOpenReferencedWorkspace);
        // …but it can still be removed. An entry pointing at a workspace that moved away is exactly
        // the one a user comes here to be rid of.
        Assert.True(row.IsReferencedWorkspace);
        Assert.True(row.RemoveWorkspaceReferenceCommand.CanExecute(null));
    }

    /// <summary>
    /// Not on an ordinary FILE inside the other workspace. It is foreign by the same walk-up, but the
    /// item is about the cell — or the reference — the user right-clicked; offering it on every
    /// foreign node would put it on rows whose menu is about the file itself.
    /// </summary>
    [Fact]
    public void AForeignFileNodeOffersNothing()
    {
        var vm      = OpenOnMine();
        string cell = MakeCell(_other, "Amp");
        string file = Path.Combine(cell, "schematic", "Amp.csch");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");

        var viewFile = new ProjectTreeNodeViewModel(
            new ProjectTreeNode(NodeKind.ViewFile, "Amp.csch", file, ""),
            new ProjectTreeFilterState(), expandedPaths: null, actions: vm);

        Assert.False(viewFile.CanOpenReferencedWorkspace);
        Assert.False(viewFile.RemoveWorkspaceReferenceCommand.CanExecute(null));
    }

    // ── The menu ──────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>It sits directly above the reveal item on the tree's node menu, and is bound to the
    /// node's own command and visibility flag.</summary>
    [Fact]
    public void TheItemSitsDirectlyAboveReveal_AndIsBoundToTheNodesOwnCommand()
    {
        var xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml"));

        // The TREE's menu, not the recent list's: the last reveal item in the file is the node one.
        int reveal = xaml.LastIndexOf("Header=\"{Binding RevealLabel}\"", StringComparison.Ordinal);
        int open   = xaml.LastIndexOf("Header=\"Open Referenced Workspace in New Window\"",
                                      StringComparison.Ordinal);

        Assert.True(open >= 0, "the node menu should offer Open Referenced Workspace in New Window");
        Assert.True(open < reveal, "it goes directly above the reveal item");

        var item = xaml[open..xaml.IndexOf('>', open)];
        Assert.Contains("Command=\"{Binding OpenReferencedWorkspaceCommand}\"", item, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanOpenReferencedWorkspace}\"", item, StringComparison.Ordinal);

        // Nothing between the two but the comment that explains the pairing.
        var between = xaml[open..reveal];
        Assert.DoesNotContain("<MenuItem Header=", between, StringComparison.Ordinal);
    }

    // ── Removing the reference (owner, 2026-09-03) ────────────────────────────

    /// <summary>
    /// The count the confirmation is built on: cells here that place a cell through the alias. Counted
    /// by the reference's SPELLING, so a reference whose target has already moved away still counts —
    /// that is the entry a removal is most often aimed at.
    /// </summary>
    [Fact]
    public void TheAliasCount_CountsCellsThatNameTheAlias()
    {
        string user = MakeCell(_mine, "Board");
        string sch  = Path.Combine(user, "schematic");
        Directory.CreateDirectory(sch);
        File.WriteAllText(Path.Combine(sch, "Board.csch"),
            """{"Components":[{"CellRef":"ws://Other/cells/Amp"},{"CellRef":"cells/Local"}]}""");

        // A second cell that references nothing external — not counted.
        string plain = MakeCell(_mine, "Plain");
        Directory.CreateDirectory(Path.Combine(plain, "schematic"));
        File.WriteAllText(Path.Combine(plain, "schematic", "Plain.csch"),
            """{"Components":[{"CellRef":"cells/Local"}]}""");

        Assert.Equal(1, CellUsageScanner.CountCellsUsingWorkspaceAlias(_mine, "Other"));
        Assert.Equal(0, CellUsageScanner.CountCellsUsingWorkspaceAlias(_mine, "Elsewhere"));

        // The target having moved away changes nothing — the alias is what stops resolving.
        Directory.Delete(_other, recursive: true);
        Assert.Equal(1, CellUsageScanner.CountCellsUsingWorkspaceAlias(_mine, "Other"));
    }

    /// <summary>
    /// Reference-only, like the Known File item it is modelled on: the entry leaves the <c>.cws</c>
    /// and nothing on either disk is touched. Source-scanned, because the removal ends in a modal
    /// dialog that cannot be shown headlessly.
    /// </summary>
    [Fact]
    public void RemovingTheReference_EditsTheCwsAndDeletesNothing()
    {
        var ws = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        int at = ws.IndexOf("public async Task RemoveWorkspaceReferenceAsync(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = ws[at..ws.IndexOf("/// <inheritdoc/>", at, StringComparison.Ordinal)];

        Assert.Contains("cws.ReferencedWorkspaces?.RemoveAll(", body, StringComparison.Ordinal);
        Assert.Contains("WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath, cws);", body,
                        StringComparison.Ordinal);
        // The alias table a ws:// reference resolves through is memoised, and this rewrite changes it —
        // and every open document that placed a cell through the alias is now showing a state its
        // render model still holds. Both live in RefreshAfterReferenceChange, which is the one place
        // a reference change says what has to happen next (see CellReferenceRelinkTests).
        Assert.Contains("RefreshAfterReferenceChange();", body, StringComparison.Ordinal);
        // Nothing on disk, in either workspace.
        Assert.DoesNotContain("Directory.Delete", body, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Trash", body, StringComparison.Ordinal);
        // Confirmed first, and the count is in hand before the prompt is built.
        Assert.Contains("CountCellsUsingWorkspaceAlias", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("CountCellsUsingWorkspaceAlias", StringComparison.Ordinal)
                    < body.IndexOf("SaveChangesDialog", StringComparison.Ordinal));
    }

    /// <summary>The item is on the Referenced Workspace row, and only there.</summary>
    [Fact]
    public void TheRemoveItem_IsOnTheReferencedWorkspaceRow()
    {
        var xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml"));

        int at = xaml.IndexOf("Header=\"Remove Workspace Reference\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "the node menu should offer Remove Workspace Reference");

        var item = xaml[at..xaml.IndexOf('>', at)];
        Assert.Contains("Command=\"{Binding RemoveWorkspaceReferenceCommand}\"", item, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsReferencedWorkspace}\"", item, StringComparison.Ordinal);
    }
}
