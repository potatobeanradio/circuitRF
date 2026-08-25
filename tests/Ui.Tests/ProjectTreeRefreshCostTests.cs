// ================================================================
//  ProjectTreeRefreshCostTests.cs — owner, 2026-08-25:
//    "when I open a large workspace, I can see the workspace flash a little bit."
//    "is opening a workspace off the UI thread? It needs to be for large workspaces."
//
//  ProjectTreeView re-scans on the workspace window's Activated — which fires on open, on every
//  alt-tab back, and on every dialog close. Every one of those threw the entire VM tree away and
//  assigned a NEW collection to TopLevelItems, so the TreeView tore down and rebuilt every realized
//  container (and Avalonia's TreeView does not virtualize, so that is every row).
//
//  COUNTERS, not wall-clock: what is asserted here is that a rescan finding nothing changed does no
//  work at all, and that one finding a real change still does.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class ProjectTreeRefreshCostTests : IDisposable
{
    private readonly string _root;

    public ProjectTreeRefreshCostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Cell(string relativePath)
    {
        var dir = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".ccell"),
            $$"""{"format_version":1,"name":"{{Path.GetFileName(dir)}}"}""");
        return dir;
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private ProjectTreeTool OpenTool()
    {
        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_root);
        return tool;
    }

    // ── The flash ─────────────────────────────────────────────────────────────

    // The tell for "the whole tree was thrown away": TopLevelItems becomes a DIFFERENT collection
    // instance, which is what makes the TreeView rebuild every container.
    [Fact]
    public void ARefreshThatFindsNothingChanged_DoesNotRebuildTheTree()
    {
        Cell("MyAmp");
        Cell("Mixer");

        var tool = OpenTool();
        var before = tool.TopLevelItems;
        var beforeNodes = tool.TopLevelItems!.ToList();

        tool.Refresh();

        Assert.Same(before, tool.TopLevelItems);                    // same collection…
        Assert.Equal(beforeNodes, tool.TopLevelItems!.ToList());    // …same node VMs
    }

    [Fact]
    public void ARefreshThatFindsARealChange_DoesRebuildTheTree()
    {
        Cell("MyAmp");

        var tool = OpenTool();
        var before = tool.TopLevelItems;
        Assert.Single(tool.TopLevelItems!);

        Cell("Mixer");                                              // something appeared on disk
        tool.Refresh();

        Assert.NotSame(before, tool.TopLevelItems);
        Assert.Equal(2, tool.TopLevelItems!.Count);
    }

    // Expansion survives a rebuild too (CollectExpandedPaths reinstates it), so this is a guard that
    // the skip did not BREAK it — not evidence for the skip. The state a rebuild genuinely loses is
    // the selection, below.
    [Fact]
    public void ARefreshThatFindsNothingChanged_KeepsExpansionState()
    {
        Cell("myboard/R0402");

        var tool = OpenTool();
        var folder = tool.TopLevelItems!.Single();
        folder.IsExpanded = true;

        tool.Refresh();

        Assert.True(tool.TopLevelItems!.Single().IsExpanded);
    }

    // A rebuild replaces every node VM, so the selected one is no longer in the tree and the selection
    // is dropped — on every window activation, which is every alt-tab and every dialog close.
    [Fact]
    public void ARefreshThatFindsNothingChanged_KeepsTheSelection()
    {
        Cell("MyAmp");
        var tool = OpenTool();
        tool.SelectedItem = tool.TopLevelItems!.Single();

        tool.Refresh();

        Assert.NotNull(tool.SelectedItem);
        Assert.Equal("MyAmp", tool.SelectedItem!.Name);
        Assert.Same(tool.TopLevelItems!.Single(), tool.SelectedItem);
    }

    // A rename is the case a shape-only signature would miss.
    [Fact]
    public void ARenameOnDisk_CountsAsAChange()
    {
        Cell("MyAmp");
        var tool = OpenTool();
        var before = tool.TopLevelItems;

        Directory.Move(Path.Combine(_root, "MyAmp"), Path.Combine(_root, "MyAmp2"));
        tool.Refresh();

        Assert.NotSame(before, tool.TopLevelItems);
        Assert.Equal("MyAmp2", tool.TopLevelItems!.Single().Name);
    }

    // Two cells swapping places must not hash the same as the original order.
    [Fact]
    public void TheSignatureIsOrderSensitive()
    {
        Cell("Bravo");
        var tool = OpenTool();
        var before = tool.TopLevelItems;

        Cell("Alpha");          // sorts BEFORE Bravo, so the child list reorders
        tool.Refresh();

        Assert.NotSame(before, tool.TopLevelItems);
        Assert.Equal(["Alpha", "Bravo"], tool.TopLevelItems!.Select(n => n.Name));
    }

    // ── Off the UI thread ─────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_ProducesTheSameTreeAsTheSynchronousOne()
    {
        Cell("MyAmp");
        var tool = OpenTool();

        Cell("Mixer");
        await tool.RefreshAsync();

        Assert.Equal(["Mixer", "MyAmp"], tool.TopLevelItems!.Select(n => n.Name));
    }

    [Fact]
    public async Task RefreshAsync_OnAnUnchangedWorkspace_AlsoRebuildsNothing()
    {
        Cell("MyAmp");
        var tool = OpenTool();
        var before = tool.TopLevelItems;

        await tool.RefreshAsync();

        Assert.Same(before, tool.TopLevelItems);
    }

    [Fact]
    public async Task RefreshAsync_OnAClosedWorkspace_IsANoOp()
    {
        var tool = new ProjectTreeTool();
        await tool.RefreshAsync();          // never opened — must not throw
        Assert.Null(tool.TopLevelItems);
    }

    // The scan walks a live filesystem unattended; a folder that vanishes mid-walk must not surface as
    // an error box on a window activation.
    [Fact]
    public async Task RefreshAsync_SurvivesTheWorkspaceFolderDisappearing()
    {
        Cell("MyAmp");
        var tool = OpenTool();

        Directory.Delete(_root, recursive: true);
        var ex = await Record.ExceptionAsync(() => tool.RefreshAsync());

        Assert.Null(ex);
    }

    // The on-focus path is the frequent one — open, every alt-tab back, every dialog close — and
    // nothing waits on its result, so its filesystem walk belongs off the UI thread.
    [Fact]
    public void TheOnFocusRescanUsesTheAsyncPath()
    {
        var cs = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");
        Assert.Contains("tool.RefreshAsync()", cs);
    }
}
