// ================================================================
//  EmSetupTreeDirtyTests.cs — owner-reported, 2026-08-21: "a dirty .cem does not show as dirty in
//  the Project tree /em folder."
//
//  WorkspaceViewModel.HookEmSetupDirty was a copy of HookTechFileDirty and called the .ctech setter,
//  ProjectTreeTool.SetTechFileDirty — whose `node is { Kind: NodeKind.TechFile }` guard threw the push
//  away in silence, because a .cem node is NodeKind.EmSetupFile. Nothing errored; the mark simply
//  never appeared. There is one setter for every file kind now (SetFileDirty), so the wrong one is no
//  longer reachable — these tests hold that shut from both ends.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmSetupTreeDirtyTests : IDisposable
{
    private readonly string _wsDir;

    public EmSetupTreeDirtyTests()
    {
        _wsDir = Path.Combine(Path.GetTempPath(), "crftest_emtree_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_wsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wsDir, recursive: true); }
        catch { /* best effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Writes <c>&lt;workspace&gt;/em/&lt;stem&gt;.cem</c> — the folder both EM doors use
    /// (File ▸ New ▸ EM Setup… and the Layout Editor's EM button) — and returns its path.</summary>
    private string MakeCem(string stem)
    {
        var dir = Path.Combine(_wsDir, "em");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, stem + EmSetupPersistence.Extension);
        EmSetupPersistence.SaveToFile(path, new EmSetup { Name = stem });
        return Path.GetFullPath(path);
    }

    private string MakeFile(string relPath, string content)
    {
        var path = Path.Combine(_wsDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    private ProjectTreeTool ScannedTree()
    {
        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_wsDir);
        return tool;
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

    /// <summary>One source file with its comments stripped — the comments here name the very setter
    /// the scans below assert is gone.</summary>
    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"///[^\n]*", "", RegexOptions.None);
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

    // ── 1 — the node that could not be marked ─────────────────────────────────

    [Fact]
    public void ACemNodeInTheEmFolder_CanBeMarkedDirty()
    {
        string cem = MakeCem("bend");
        var tool = ScannedTree();

        var node = Find(tool.RootItems[0], cem);
        Assert.NotNull(node);
        Assert.Equal(NodeKind.EmSetupFile, node!.Kind);   // NOT TechFile — the whole bug in one line
        Assert.False(node.IsDirty);

        tool.SetFileDirty(cem, true);
        Assert.True(node.IsDirty);

        tool.SetFileDirty(cem, false);
        Assert.False(node.IsDirty);
    }

    /// <summary>
    /// One setter, every file kind — the property that makes a mis-aimed push impossible rather than
    /// silent. A per-kind setter is what let the .cem push be dropped for free.
    /// </summary>
    [Fact]
    public void OneSetterMarksEveryEditableFileKind()
    {
        string cem   = MakeCem("bend");
        string ctech = MakeFile(Path.Combine("tech", "board.ctech"), "{}");
        string cdd   = MakeFile(Path.Combine("results", "run1.cdd"), "{}");
        string data  = MakeFile(Path.Combine("results", "run1.npy"), "not a document");

        var tool = ScannedTree();

        foreach (var path in new[] { cem, ctech, cdd })
        {
            tool.SetFileDirty(path, true);
            Assert.True(Find(tool.RootItems[0], path)!.IsDirty, path + " should be markable");
        }

        // A file with no editor behind it is left alone — nothing can make it dirty.
        tool.SetFileDirty(data, true);
        Assert.False(Find(tool.RootItems[0], data)!.IsDirty);
    }

    /// <summary>A relative path reaches the same node: every node stores an absolute path, and the
    /// setter normalises rather than failing to match.</summary>
    [Fact]
    public void ARelativePathReachesTheSameNode()
    {
        string cem = MakeCem("bend");
        var tool = ScannedTree();

        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), cem);
        tool.SetFileDirty(relative, true);

        Assert.True(Find(tool.RootItems[0], cem)!.IsDirty);
    }

    // ── 2 — the state the push carries ────────────────────────────────────────

    /// <summary>
    /// The contract <c>HookEmSetupDirty</c> rides on, end to end at the model level: an edit dirties
    /// the editor, the document mirrors it, and a save clears both — so the value pushed at each
    /// notification is the one the tree should be showing.
    /// </summary>
    [Fact]
    public void EditingACem_DirtiesTheDocument_AndSavingClearsIt()
    {
        string cem = MakeCem("bend");
        var vm  = new EmSetupEditorViewModel(cem, EmSetupPersistence.LoadFromFile(cem));
        var doc = new EmSetupDocument(Path.GetFileName(cem), vm, cem);

        var tool = ScannedTree();
        var node = Find(tool.RootItems[0], cem)!;

        // Exactly what HookEmSetupDirty subscribes (asserted to be the real wiring below).
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EmSetupEditorViewModel.IsDirty))
                tool.SetFileDirty(doc.FilePath, vm.IsDirty);
        };

        vm.MinCellsAcrossWidthText = "11";
        vm.CommitMeshField(nameof(EmMeshSettings.MinCellsAcrossWidth));

        Assert.True(vm.IsDirty);
        Assert.True(doc.IsDirty);
        Assert.True(node.IsDirty);

        vm.SaveCommand.Execute(null);

        Assert.False(vm.IsDirty);
        Assert.False(doc.IsDirty);
        Assert.False(node.IsDirty);
    }

    // ── 3 — the wiring, asserted where it lives ───────────────────────────────

    [Fact]
    public void TheEmSetupHook_PushesThroughTheOneSetter()
    {
        string hook = Between(
            Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"),
            "private void HookEmSetupDirty(EmSetupDocument doc)");

        Assert.Contains("nameof(EmSetupEditorViewModel.IsDirty)", hook, StringComparison.Ordinal);
        Assert.Contains("SetFileDirty(doc.FilePath, doc.ViewModel.IsDirty)", hook, StringComparison.Ordinal);
    }

    /// <summary>
    /// The kind-specific setters must not come back: they are what made a mis-aimed push a silent
    /// no-op, and a fourth file kind would repeat the report verbatim.
    /// </summary>
    [Fact]
    public void NoKindSpecificFileSetterSurvives()
    {
        foreach (var parts in new[]
                 {
                     new[] { "src", "Ui", "ViewModels", "Dock", "ProjectTreeTool.cs" },
                     new[] { "src", "Ui", "ViewModels", "WorkspaceViewModel.cs" },
                 })
        {
            string src = Src(parts);
            Assert.DoesNotContain("SetTechFileDirty", src, StringComparison.Ordinal);
            Assert.DoesNotContain("SetDataDisplayDirty", src, StringComparison.Ordinal);
            Assert.DoesNotContain("SetEmSetupDirty", src, StringComparison.Ordinal);
        }
    }
}
