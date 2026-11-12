using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-3 R-hzn3-1 — a Hertzon project in the Project Tree: found inside the workspace or listed as a Known File, it
/// expands to its designs in file order, and a file that fails the version gate shows one row carrying the sentence.
/// </summary>
public sealed class ProjectTreeHznTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzntree_" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectTreeHznTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    private static ProjectTreeNodeViewModel Find(ProjectTreeNodeViewModel from, Func<ProjectTreeNodeViewModel, bool> match)
    {
        if (match(from)) return from;
        foreach (var child in from.Children)
            if (FindOrNull(child, match) is { } hit) return hit;
        throw new InvalidOperationException("no such row");
    }

    private static ProjectTreeNodeViewModel? FindOrNull(ProjectTreeNodeViewModel from, Func<ProjectTreeNodeViewModel, bool> match)
        => match(from) ? from : from.Children.Select(c => FindOrNull(c, match)).FirstOrDefault(r => r is not null);

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        return ws;
    }

    [Fact]
    public void AProjectInAUserFolder_ExpandsToItsDesigns_InFileOrder_AndIsNotReadUntilThen()
    {
        string ws = Workspace();
        Directory.CreateDirectory(Path.Combine(ws, "hertzon"));
        File.Copy(Fixture("circuit-and-3d.hzn"), Path.Combine(ws, "hertzon", "p.hzn"));

        var tree = new ProjectTreeNodeViewModel(WorkspaceScanner.Scan(ws), new ProjectTreeFilterState());
        var project = Find(tree, n => n.Kind == NodeKind.HznFile);

        Assert.Equal("p.hzn", project.Name);
        Assert.Equal("CircuitBeside3D", project.TooltipPath);                       // the project's own name
        Assert.Equal(NodeKind.NotReadYet, Assert.Single(project.Children).Kind);    // nothing read yet

        project.IsExpanded = true;

        Assert.Equal(["Matching", "Stub"], project.Children.Select(c => c.Name));
        Assert.Equal([NodeKind.HznDesignCircuit, NodeKind.HznDesign3D], project.Children.Select(c => c.Kind));
        Assert.Equal(project.Children, project.FilteredChildren);
    }

    [Fact]
    public void AProjectAddedAsAKnownFile_ExpandsLikeOne()
    {
        string ws = Workspace();
        string outside = Path.Combine(_root, "elsewhere", "two.hzn");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.Copy(Fixture("two-designs.hzn"), outside);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { KnownFiles = [outside] });

        var tree = new ProjectTreeNodeViewModel(WorkspaceScanner.Scan(ws), new ProjectTreeFilterState());
        var known = Find(tree, n => n.Kind == NodeKind.KnownFile);
        Assert.True(known.IsHznProject);
        Assert.Equal("Remove from Known Files", known.RemoveKnownFileHeader);

        known.IsExpanded = true;

        Assert.Equal(["First", "Second"], known.Children.Select(c => c.Name));
        Assert.All(known.Children, c => Assert.Equal(NodeKind.HznDesign3D, c.Kind));
    }

    [Fact]
    public void AVersionThreeProject_ShowsOneRefusalRow_AndNothingElse()
    {
        string ws = Workspace();
        string path = Path.Combine(ws, "newer.hzn");
        File.WriteAllText(path, """{ "format": "hertzon-project", "version": 3, "name": "N", "designs": [ { "name": "D" } ] }""");

        var tree = new ProjectTreeNodeViewModel(WorkspaceScanner.Scan(ws), new ProjectTreeFilterState());
        var project = Find(tree, n => n.Kind == NodeKind.HznFile);
        project.IsExpanded = true;

        var row = Assert.Single(project.Children);
        Assert.Equal(NodeKind.HznRefusal, row.Kind);
        Assert.Contains("format version 3", row.Name);
        Assert.Contains("newer.hzn", row.Name);
        Assert.DoesNotContain(ws, row.Name);                    // the file is named, not its folder
        Assert.True(row.IsWarning);
    }
}
