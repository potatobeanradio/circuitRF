// ================================================================
//  ProjectTreeSelectionTests.cs — several Workspace-panel rows at once, and Delete/Backspace
//  removing them (owner, 2026-10-07: removing a cell or folder needed the context menu; the
//  panel could select only one row; and a key that deletes must never reach a cell while the
//  user is working in an editor).
//
//  What is held here is the RULES (ProjectTreeSelection) and the two safety properties that do
//  not need a display: a rebuilt tree selects nothing, and every tree removal's confirmation
//  answers Enter with Cancel.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Input;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ProjectTreeSelectionTests : IDisposable
{
    private readonly string _root;

    public ProjectTreeSelectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Cell(string relativePath)
    {
        var dir = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".ccell"),
            $$"""{"format_version":1,"name":"{{Path.GetFileName(dir)}}"}""");
    }

    private void Document(string name) => File.WriteAllText(Path.Combine(_root, name), "{}");

    private ProjectTreeTool OpenTool()
    {
        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_root);
        return tool;
    }

    private static ProjectTreeNodeViewModel Row(ProjectTreeTool tool, string name)
    {
        static ProjectTreeNodeViewModel? Find(System.Collections.Generic.IEnumerable<ProjectTreeNodeViewModel> rows, string name)
        {
            foreach (var row in rows)
            {
                if (row.Name == name) return row;
                if (Find(row.Children, name) is { } hit) return hit;
            }
            return null;
        }
        var found = Find(tool.TopLevelItems!, name);
        Assert.True(found is not null, $"No row named '{name}'.");
        return found!;
    }

    [Theory]
    [InlineData(Key.Delete, KeyModifiers.None,    true)]
    [InlineData(Key.Back,   KeyModifiers.None,    true)]    // a Mac keyboard's "delete"
    [InlineData(Key.Back,   KeyModifiers.Meta,    true)]    // ⌘⌫, the Finder's Move to Trash
    [InlineData(Key.Back,   KeyModifiers.Control, false)]
    [InlineData(Key.Delete, KeyModifiers.Shift,   false)]
    [InlineData(Key.D,      KeyModifiers.None,    false)]
    public void TheDeleteGesture_IsDeleteOrBackspace_BareOrWithCommand(Key key, KeyModifiers modifiers, bool expected)
        => Assert.Equal(expected, ProjectTreeSelection.IsDeleteGesture(key, modifiers));

    [Fact]
    public void ARowInsideAnotherSelectedRow_IsFoldedIntoIt()
    {
        Cell("boards/amp");
        Cell("mixer");
        var tool = OpenTool();
        var boards = Row(tool, "boards");
        var amp    = Row(tool, "amp");
        var mixer  = Row(tool, "mixer");

        var folded = ProjectTreeSelection.TopMost([amp, boards, mixer, mixer]);

        Assert.Equal(["boards", "mixer"], folded.Select(n => n.Name).OrderBy(n => n));
    }

    [Fact]
    public void DeleteOnOneRow_RunsThatRowsOwnRemove()
    {
        Cell("amp");
        Document("sweep.cdd");
        Document("process.ctech");
        var tool = OpenTool();

        Assert.Same(Row(tool, "amp").RemoveCellCommand,
                    ProjectTreeSelection.SingleRemoveCommand(Row(tool, "amp")));
        Assert.Same(Row(tool, "sweep.cdd").RemoveFileCommand,
                    ProjectTreeSelection.SingleRemoveCommand(Row(tool, "sweep.cdd")));
        Assert.Same(Row(tool, "process.ctech").RemoveTechnologyCommand,
                    ProjectTreeSelection.SingleRemoveCommand(Row(tool, "process.ctech")));
    }

    [Fact]
    public void CellsFoldersAndDocuments_GoTogether_ATechnologyIsRefusedByName()
    {
        Cell("amp");
        Cell("boards/pa");
        Document("sweep.cdd");
        Document("process.ctech");
        var tool = OpenTool();
        var together = new[] { Row(tool, "amp"), Row(tool, "boards"), Row(tool, "sweep.cdd") };

        Assert.Null(ProjectTreeSelection.BulkRemoveRefusal(together));

        var refusal = ProjectTreeSelection.BulkRemoveRefusal([.. together, Row(tool, "process.ctech")]);
        Assert.NotNull(refusal);
        Assert.Contains("process.ctech", refusal);
    }

    // The tree rebuilds every row on a refresh. A selection left pointing at the discarded rows is
    // invisible — and Delete would act on it.
    [Fact]
    public void ARebuiltTree_HasNothingSelected()
    {
        Cell("amp");
        var tool = OpenTool();
        tool.SelectedNodes.Add(Row(tool, "amp"));
        Assert.Single(tool.Selection);

        Cell("mixer");    // a real change on disk, so the refresh rebuilds rather than skipping
        tool.Refresh();

        Assert.Empty(tool.SelectedNodes);
        Assert.Empty(tool.Selection);
    }

    // Delete can start a removal now, so a confirmation that Enter accepts would turn a stray
    // Delete-then-Enter into a cell in the Trash.
    [Fact]
    public void EveryTreeRemovalConfirmation_AnswersEnterWithCancel()
    {
        string src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        var removals = Regex.Matches(src, @"new Views\.Dialogs\.SaveChangesDialog\((?:[^;])*?\);", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(d => Regex.IsMatch(d, @"title:\s*(""Remove|dialogTitle)"))
            .ToList();

        Assert.True(removals.Count >= 6, $"Expected the tree's removal dialogs, found {removals.Count}.");
        Assert.All(removals, d => Assert.Matches(@"destructive:\s*true", d));
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── Reveal in Workspace Tree (a document tab's menu) ──────────────────────────────────────

    [Fact]
    public void Reveal_ExpandsEveryAncestor_AndSelectsTheDocumentsRow()
    {
        Cell("Lib/Sub/Amp");
        string view = Path.Combine(_root, "Lib", "Sub", "Amp", "schematic");
        Directory.CreateDirectory(view);
        string file = Path.Combine(view, "Amp.csch");
        File.WriteAllText(file, "{}");
        var tool = OpenTool();

        ProjectTreeNodeViewModel? revealed = null;
        tool.RevealRequested += n => revealed = n;
        Assert.True(tool.Reveal(file));

        Assert.NotNull(revealed);
        Assert.Same(revealed, tool.SelectedItem);
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(revealed!.AbsolutePath), ignoreCase: true);
        for (var a = revealed.Parent; a?.Parent is not null; a = a.Parent)
            Assert.True(a.IsExpanded, $"{a.Name} was left collapsed.");
    }

    [Fact]
    public void Reveal_ClearsAFilterThatHidesTheRow()
    {
        Cell("Amp");
        var tool = OpenTool();
        tool.FilterState.Cells = false;
        Assert.False(tool.FilterState.AllOn);

        Assert.True(tool.Reveal(Path.Combine(_root, "Amp")));
        Assert.True(tool.FilterState.AllOn);
        Assert.Equal("Amp", tool.SelectedItem!.Name);
    }

    [Fact]
    public void TheFiltersAllSwitch_TurnsEveryCategoryOffAndOn_AndReadsFalseWhenAnyIsOff()
    {
        var f = new ProjectTreeFilterState();
        Assert.True(f.AllOn);
        f.AllOn = false;
        Assert.False(f.Cells || f.Libraries || f.TestBenches || f.DataDisplays || f.ColorThemes || f.TechFiles
                     || f.KnownFiles || f.WorkspaceFileSystem || f.ReferencedCells || f.ReferencedWorkspaces);
        f.AllOn = true;
        Assert.True(f.IsAllOn);

        int raised = 0;
        f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProjectTreeFilterState.AllOn)) raised++; };
        f.TechFiles = false;
        Assert.False(f.AllOn);
        Assert.Equal(1, raised);
    }
}

