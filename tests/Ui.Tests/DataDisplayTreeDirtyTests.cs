// ================================================================
//  DataDisplayTreeDirtyTests.cs — owner-reported, 2026-08-21: "after I saved a .cdd file to my
//  results directory, the project tree view still indicated it was dirty in the tree."
//
//  A .cdd node's dirty mark had NO push site: it was written only by ProjectTreeTool.RebuildVmTree's
//  RestoreDirtyFlags pass, which runs on a workspace-window Activated rescan. Saving raises no
//  Activated, so the mark a rescan had put there stayed until some later, unrelated focus change.
//
//  The tree-tool half is testable for real (it needs only a scanned folder); the WorkspaceViewModel
//  half needs an Avalonia application and a dock factory, so it is asserted against the source it is
//  written in, naming the mechanism rather than scanning for a word.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayTreeDirtyTests : IDisposable
{
    private readonly string _tempDir;

    public DataDisplayTreeDirtyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_ddtree_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Writes <c>&lt;temp&gt;/results/&lt;name&gt;.cdd</c> and returns its absolute path.</summary>
    private string MakeCdd(string name)
    {
        var dir = Path.Combine(_tempDir, "results");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".cdd");
        File.WriteAllText(path, "{}");
        return Path.GetFullPath(path);
    }

    private static ProjectTreeNodeViewModel? Find(ProjectTreeNodeViewModel node, string absPath)
    {
        if (string.Equals(node.AbsolutePath, absPath, StringComparison.OrdinalIgnoreCase)) return node;
        return node.Children.Select(c => Find(c, absPath)).FirstOrDefault(f => f is not null);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One source file with its comments stripped — every scan below is about what the code
    /// does, and the comments here quote the very report the fix answers.</summary>
    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);
        return raw;
    }

    private static string Between(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source any more");
        int open = src.IndexOf('{', i);
        int depth = 0;
        for (int k = open; k < src.Length; k++)
        {
            if (src[k] == '{') depth++;
            else if (src[k] == '}' && --depth == 0) return src[open..(k + 1)];
        }
        Assert.Fail($"'{signature}' has no closing brace");
        return "";
    }

    // ── 1 — the push site the .cdd node never had ─────────────────────────────

    [Fact]
    public void SetFileDirty_MarksAndClearsTheCddNode()
    {
        string cdd = MakeCdd("run1");

        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_tempDir);

        var node = Find(tool.RootItems[0], cdd);
        Assert.NotNull(node);
        Assert.Equal(NodeKind.DataDisplayFile, node!.Kind);
        Assert.False(node.IsDirty);

        tool.SetFileDirty(cdd, true);
        Assert.True(node.IsDirty);

        // The clear is the half the report is about: a save must be able to take the mark back off.
        tool.SetFileDirty(cdd, false);
        Assert.False(node.IsDirty);
    }

    /// <summary>A path that is not an openable file is a no-op — a plain data file in the same folder
    /// has no editor and so can never be dirty.</summary>
    [Fact]
    public void SetFileDirty_IgnoresAFileNothingCanEdit()
    {
        string cdd = MakeCdd("run1");
        string other = Path.Combine(_tempDir, "results", "notes.txt");
        File.WriteAllText(other, "x");

        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_tempDir);

        tool.SetFileDirty(Path.GetFullPath(other), true);
        var otherNode = Find(tool.RootItems[0], Path.GetFullPath(other));
        Assert.NotNull(otherNode);
        Assert.False(otherNode!.IsDirty);

        // And the real .cdd node is untouched by that call.
        Assert.False(Find(tool.RootItems[0], cdd)!.IsDirty);
    }

    // ── 2 — the event the push rides on carries the CLEAR a save performs ─────

    /// <summary>
    /// The push is driven by <c>DisplayWindowViewModel.DirtyChanged</c> and reads
    /// <c>HasUnsavedChanges()</c> at that moment. This is what makes a save clear the tree: the save
    /// captures its baseline FIRST and then raises the event, so the handler sees "clean".
    /// </summary>
    [Fact]
    public async Task SavingRaisesDirtyChanged_WithHasUnsavedChangesAlreadyFalse()
    {
        var docVm = new DataDisplayDocumentViewModel();
        string path = Path.Combine(_tempDir, "display.cdd");

        await docVm.Window.SaveAllAsync(path);        // establish a clean baseline
        docVm.Window.ActiveTab!.DataDisplay.AddPlot(PlotType.Smith, FreqUnit.GHz);
        Assert.True(docVm.Window.HasUnsavedChanges());

        bool? seenByHandler = null;
        docVm.Window.DirtyChanged += (_, _) => seenByHandler = docVm.Window.HasUnsavedChanges();

        await docVm.Window.SaveAllAsync(path);

        Assert.True(seenByHandler.HasValue, "a save must raise DirtyChanged — the tree push rides on it");
        Assert.False(seenByHandler!.Value);
    }

    // ── 3 — the wiring, asserted where it lives ───────────────────────────────

    /// <summary>
    /// Every path that creates a <c>DataDisplayDocument</c> must wire the push, or the document it
    /// creates is the one whose node goes stale. There are three: New Data Display, open-or-activate
    /// a .cdd, and the post-run auto-open.
    /// </summary>
    [Fact]
    public void EveryDataDisplayDocument_IsWiredToTheTreeMark()
    {
        string ws = Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");

        Assert.Equal(3, Regex.Matches(ws, @"WireDataDisplayTreeDirty\((?:doc|newDoc)\);").Count);

        string wire = Between(ws, "private void WireDataDisplayTreeDirty(DataDisplayDocument doc)");
        Assert.Contains("DirtyChanged", wire, StringComparison.Ordinal);
        Assert.Contains("SetFileDirty(", wire, StringComparison.Ordinal);
        // The authoritative predicate — the same one IsNodeDirty, the close prompt and the Window
        // menu use. DataDisplayDocumentViewModel.IsDirty can lag a live edit; this must not.
        Assert.Contains("HasUnsavedChanges()", wire, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scratch display saved through the picker writes a file the last scan never saw, so the node
    /// has to be BUILT before it can be marked — the rebuild asks IsNodeDirty and arrives clean.
    /// </summary>
    [Fact]
    public void SavingAScratchDisplay_RefreshesTheTree()
    {
        string save = Between(
            Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"),
            "private async Task<bool> SaveDataDisplayDoc(DataDisplayDocument dd, Window owner)");

        int materialize = save.IndexOf("dd.Materialize(picked);", StringComparison.Ordinal);
        int refresh     = save.IndexOf("ProjectTreeTool?.Refresh();", StringComparison.Ordinal);
        Assert.True(materialize > 0, "the scratch branch must still materialize the document");
        Assert.True(refresh > materialize,
            "the tree refresh must follow the materialize, so the rebuilt node sees the saved FilePath");
    }

    /// <summary>
    /// A closed document can hold no unsaved work, so the mark it pushed must come off — a "Don't
    /// Save" close leaves it standing otherwise, which is the same staleness in a different door.
    /// All three whose dirty answer comes from the OPEN documents alone (.cdd, .ctech, .cem), and
    /// none of the three whose dirty session OUTLIVES the tab in the session registry.
    /// </summary>
    [Fact]
    public void ClosingAFileBackedDocument_ClearsItsMark()
    {
        string closed = Regex.Replace(
            Between(Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"),
                    "private void OnDockableClosed(IDockable dockable)"),
            @"\s+", " ");

        Assert.Contains("SetFileDirty(closedFilePath, false)", closed, StringComparison.Ordinal);
        Assert.Contains("DataDisplayDocument d => d.FilePath", closed, StringComparison.Ordinal);
        Assert.Contains("TechDocument d => d.FilePath", closed, StringComparison.Ordinal);
        Assert.Contains("EmSetupDocument d => d.FilePath", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("SchematicDocument d => d.FilePath", closed, StringComparison.Ordinal);
    }
}
