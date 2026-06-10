using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Tests;

// ── Layer 1 gate: ProjectTreeNodeViewModel maps a scanned ProjectTreeNode tree ─────

public class ProjectTreeNodeViewModelTests : IDisposable
{
    private readonly string _root;

    public ProjectTreeNodeViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ProjectTreeFilterState AllOn() => new();

    // ── Kind and flags preserved ───────────────────────────────────────────────

    [Fact]
    public void WorkspaceRoot_KindPreserved()
    {
        var node = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(node, AllOn());
        Assert.Equal(NodeKind.Workspace, vm.Kind);
    }

    [Fact]
    public void WorkspaceRoot_IsExpandedByDefault()
    {
        var node = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(node, AllOn());
        Assert.True(vm.IsExpanded);
    }

    [Fact]
    public void Cell_KindPreserved()
    {
        var cellDir = Path.Combine(_root, "Amp");
        Directory.CreateDirectory(cellDir);
        File.WriteAllText(Path.Combine(cellDir, ".ccell"),
            """{"format_version":1,"name":"Amp"}""");

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn());

        var cellVm = vm.Children.Single(c => c.Kind == NodeKind.Cell);
        Assert.Equal("Amp", cellVm.Name);
        Assert.Equal(NodeKind.Cell, cellVm.Kind);
    }

    [Fact]
    public void TestBench_FlagPreserved()
    {
        var tbDir = Path.Combine(_root, "TB_PA");
        Directory.CreateDirectory(tbDir);
        File.WriteAllText(Path.Combine(tbDir, ".ccell"),
            """{"format_version":1,"name":"TB_PA","isTestBench":true}""");

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn());

        var tbVm = vm.Children.Single(c => c.Kind == NodeKind.Cell);
        Assert.True(tbVm.IsTestBench);
    }

    [Fact]
    public void Primary_ViewFile_FlagPreserved()
    {
        var cellDir  = Path.Combine(_root, "Amp");
        var schmDir  = Path.Combine(cellDir, "schematic");
        Directory.CreateDirectory(schmDir);
        File.WriteAllText(Path.Combine(cellDir, ".ccell"),
            """{"format_version":1,"name":"Amp"}""");
        File.WriteAllText(Path.Combine(schmDir, "Amp.csch"), "{}");

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn());

        var cellVm = vm.Children.Single(c => c.Kind == NodeKind.Cell);
        var folderVm = cellVm.Children.Single(c => c.Kind == NodeKind.CellViewFolder);
        var fileVm   = folderVm.Children.Single();

        Assert.True(fileVm.IsPrimary);
        Assert.True(fileVm.IsBold);
    }

    [Fact]
    public void WarningReason_Preserved()
    {
        // MissingNamedPrimary requires ≥2 view files where the named primary is absent.
        CellFolder.CreateCellFolder(_root, "Broken");
        var cellDir   = Path.Combine(_root, "Broken");
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell     = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimarySymbol = "missing.csym";
        CellPersistence.SaveToFile(ccellPath, ccell);
        // Add two .csym files — neither is missing.csym → MissingNamedPrimary warning.
        var symDir = Path.Combine(cellDir, CellFolder.SubFolderName(ViewType.Symbol));
        File.WriteAllText(Path.Combine(symDir, "actual.csym"),  "{}");
        File.WriteAllText(Path.Combine(symDir, "another.csym"), "{}");

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn());

        var cellVm = vm.Children.Single(c => c.Name == "Broken");
        Assert.True(cellVm.IsWarning);
        Assert.NotNull(cellVm.WarningReason);
        Assert.True(cellVm.IsItalic);
    }

    // ── Filter behavior ────────────────────────────────────────────────────────

    [Fact]
    public void Filter_CellsOff_HidesCellNodes()
    {
        var cellDir = Path.Combine(_root, "Amp");
        Directory.CreateDirectory(cellDir);
        File.WriteAllText(Path.Combine(cellDir, ".ccell"),
            """{"format_version":1,"name":"Amp"}""");

        var filter = AllOn();
        var root   = WorkspaceScanner.Scan(_root);
        var vm     = new ProjectTreeNodeViewModel(root, filter);

        // All on — cell visible.
        Assert.Contains(vm.FilteredChildren, c => c.Kind == NodeKind.Cell);

        // Cells off — cell hidden.
        filter.Cells = false;
        Assert.DoesNotContain(vm.FilteredChildren, c => c.Kind == NodeKind.Cell);
    }

    [Fact]
    public void Filter_TestBenchOn_CellsOff_ShowsOnlyTestBenches()
    {
        var cellDir = Path.Combine(_root, "RegularAmp");
        var tbDir   = Path.Combine(_root, "TB_PA");
        Directory.CreateDirectory(cellDir);
        Directory.CreateDirectory(tbDir);
        File.WriteAllText(Path.Combine(cellDir, ".ccell"),
            """{"format_version":1,"name":"RegularAmp"}""");
        File.WriteAllText(Path.Combine(tbDir, ".ccell"),
            """{"format_version":1,"name":"TB_PA","isTestBench":true}""");

        var filter = AllOn();
        filter.Cells = false;
        // TestBenches still on — TB cell should appear.

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, filter);

        var cells = vm.FilteredChildren.Where(c => c.Kind == NodeKind.Cell).ToList();
        Assert.Single(cells);
        Assert.True(cells[0].IsTestBench);
    }

    [Fact]
    public void Filter_AllOff_EmptyFilteredChildren()
    {
        var cellDir = Path.Combine(_root, "Amp");
        Directory.CreateDirectory(cellDir);
        File.WriteAllText(Path.Combine(cellDir, ".ccell"),
            """{"format_version":1,"name":"Amp"}""");

        var filter = AllOn();
        var root   = WorkspaceScanner.Scan(_root);
        var vm     = new ProjectTreeNodeViewModel(root, filter);

        filter.SetAll(false);
        Assert.Empty(vm.FilteredChildren);
    }

    [Fact]
    public void ExpandedPaths_RestoredOnRebuild()
    {
        var cellDir = Path.Combine(_root, "Amp");
        Directory.CreateDirectory(cellDir);
        File.WriteAllText(Path.Combine(cellDir, ".ccell"),
            """{"format_version":1,"name":"Amp"}""");

        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            cellDir,
        };

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn(), expandedPaths);

        var cellVm = vm.Children.Single(c => c.Kind == NodeKind.Cell);
        Assert.True(cellVm.IsExpanded);
    }
}
