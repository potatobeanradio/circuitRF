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

    // ── Tooltips ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The Known Files GROUP is synthesised by the scanner with an empty relative path, and the
    /// tree's one shared tooltip renders TooltipPath — so hovering it showed an empty tooltip
    /// (owner-reported). It describes what the folder holds instead.
    /// </summary>
    [Fact]
    public void KnownFilesGroup_TooltipExplainsTheFolder_RatherThanBeingBlank()
    {
        var node = new ProjectTreeNode(NodeKind.KnownFilesGroup, "Known Files", _root, "");
        var vm   = new ProjectTreeNodeViewModel(node, AllOn());

        Assert.Equal("External files that are known to this workspace are listed here.", vm.TooltipPath);
    }

    /// <summary>A Known File DIRECTORY still shows its absolute path — the group is the only case
    /// that changed.</summary>
    [Fact]
    public void KnownFileDirectory_TooltipStillShowsTheAbsolutePath()
    {
        string abs = Path.Combine(_root, "elsewhere");
        var node = new ProjectTreeNode(NodeKind.KnownFile, "elsewhere", abs, "../elsewhere",
                                       isDirectory: true);
        var vm   = new ProjectTreeNodeViewModel(node, AllOn());

        Assert.Equal(abs, vm.TooltipPath);
    }

    /// <summary>Every other node still shows its relative path.</summary>
    [Fact]
    public void OrdinaryNode_TooltipStillShowsTheRelativePath()
    {
        var node = new ProjectTreeNode(NodeKind.Cell, "Amp", Path.Combine(_root, "Amp"), "Amp");
        var vm   = new ProjectTreeNodeViewModel(node, AllOn());

        Assert.Equal("Amp", vm.TooltipPath);
    }

    // ── Context-menu visibility helpers ───────────────────────────────────────

    private static ProjectTreeNodeViewModel MakeVm(NodeKind kind, string absPath)
    {
        var node = new ProjectTreeNode(kind, Path.GetFileName(absPath), absPath, absPath);
        return new ProjectTreeNodeViewModel(node, AllOn());
    }

    [Theory]
    [InlineData(NodeKind.DataDisplayFile, true)]
    [InlineData(NodeKind.OtherFile,       false)]
    [InlineData(NodeKind.ViewFile,        false)]
    [InlineData(NodeKind.Cell,            false)]
    [InlineData(NodeKind.UserFolder,      false)]
    public void IsDataDisplayFile_CorrectForKind(NodeKind kind, bool expected)
    {
        var vm = MakeVm(kind, Path.Combine(_root, "test.cdd"));
        Assert.Equal(expected, vm.IsDataDisplayFile);
    }

    [Theory]
    [InlineData(NodeKind.OtherFile,       "/tmp/x/foo.npy",    true)]
    [InlineData(NodeKind.UserFolder,      "/tmp/x/results",    true)]
    [InlineData(NodeKind.ViewFile,        "/tmp/x/Amp.csch",   true)]
    [InlineData(NodeKind.ViewFile,        "/tmp/x/Amp.csym",   true)]
    [InlineData(NodeKind.ViewFile,        "/tmp/x/Amp.clay",   true)]
    [InlineData(NodeKind.DataDisplayFile, "/tmp/x/disp.cdd",   false)]
    [InlineData(NodeKind.Cell,            "/tmp/x/Amp",        false)]
    [InlineData(NodeKind.KnownFile,       "/tmp/x/data.snp",   false)]
    [InlineData(NodeKind.ColorThemeFile,  "/tmp/x/dark.ccolor",false)]
    public void IsRemovableFile_CorrectForKindAndExtension(NodeKind kind, string absPath, bool expected)
    {
        var vm = MakeVm(kind, absPath);
        Assert.Equal(expected, vm.IsRemovableFile);
    }

    [Theory]
    [InlineData(NodeKind.DataDisplayFile, "/tmp/x/disp.cdd",  true)]
    [InlineData(NodeKind.ViewFile,        "/tmp/x/Amp.csch",  true)]
    [InlineData(NodeKind.ViewFile,        "/tmp/x/Amp.csym",  true)]
    [InlineData(NodeKind.ViewFile,        "/tmp/x/Amp.clay",  true)]
    [InlineData(NodeKind.OtherFile,       "/tmp/x/foo.npy",   false)]
    [InlineData(NodeKind.Cell,            "/tmp/x/Amp",       false)]
    [InlineData(NodeKind.TechFile,        "/tmp/x/pcb.ctech", true)]  // L0d: the .ctech editor
    public void IsOpenableFile_CorrectForKindAndExtension(NodeKind kind, string absPath, bool expected)
    {
        var vm = MakeVm(kind, absPath);
        Assert.Equal(expected, vm.IsOpenableFile);
    }

    // ── L0c: .ctech node ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NodeKind.TechFile,        true)]
    [InlineData(NodeKind.ColorThemeFile,  false)]
    [InlineData(NodeKind.DataDisplayFile, false)]
    [InlineData(NodeKind.OtherFile,       false)]
    public void IsTechFile_CorrectForKind(NodeKind kind, bool expected)
    {
        var vm = MakeVm(kind, Path.Combine(_root, "pcb.ctech"));
        Assert.Equal(expected, vm.IsTechFile);
    }

    [Fact]
    public void TechFile_IconKind_IsLayersOutline()
    {
        var vm = MakeVm(NodeKind.TechFile, Path.Combine(_root, "pcb.ctech"));
        Assert.Equal(Material.Icons.MaterialIconKind.LayersOutline, vm.IconKind);
    }

    [Fact]
    public void TechFile_CanReveal_IsTrue()
    {
        var vm = MakeVm(NodeKind.TechFile, Path.Combine(_root, "pcb.ctech"));
        Assert.True(vm.CanReveal);
    }

    [Fact]
    public void TechFile_NoActions_IsWorkspaceDefaultTechIsFalse_CommandsDisabled()
    {
        var vm = MakeVm(NodeKind.TechFile, Path.Combine(_root, "pcb.ctech"));
        Assert.False(vm.IsWorkspaceDefaultTech);
        Assert.False(vm.SetAsWorkspaceDefaultCommand.CanExecute(null));
        Assert.False(vm.ReloadTechnologyCommand.CanExecute(null));
    }

    [Fact]
    public void Filter_TechFilesOff_HidesTechFileNodes()
    {
        var techDir = Path.Combine(_root, "tech");
        Directory.CreateDirectory(techDir);
        File.WriteAllText(Path.Combine(techDir, "pcb-2layer.ctech"), "");

        var filter = AllOn();
        var root   = WorkspaceScanner.Scan(_root);
        var vm     = new ProjectTreeNodeViewModel(root, filter);
        var folder = vm.FilteredChildren.Single(c => c.Name == "tech");

        Assert.Contains(folder.FilteredChildren, c => c.Kind == NodeKind.TechFile);

        filter.TechFiles = false;
        Assert.DoesNotContain(folder.FilteredChildren, c => c.Kind == NodeKind.TechFile);
    }
}

// ── L0b gate 6: "Open Layout" enabled when a primary layout resolves ───────────

public class ProjectTreeNodeViewModelLayoutTests : IDisposable
{
    private readonly string _root;

    public ProjectTreeNodeViewModelLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ProjectTreeFilterState AllOn() => new();

    [Fact]
    public void Cell_NoLayout_CanOpenLayoutFalse()
    {
        CellFolder.CreateCellFolder(_root, "NoLayout");
        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn());

        var cellVm = vm.Children.Single(c => c.Name == "NoLayout");
        Assert.False(cellVm.CanOpenLayout);
    }

    [Fact]
    public void Cell_SoleLayout_CanOpenLayoutTrue()
    {
        var cellDir   = CellFolder.CreateCellFolder(_root, "HasLayout");
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        File.WriteAllText(Path.Combine(layoutDir, "amp.clay"), "{}");

        var root = WorkspaceScanner.Scan(_root);
        var vm   = new ProjectTreeNodeViewModel(root, AllOn());

        var cellVm = vm.Children.Single(c => c.Name == "HasLayout");
        Assert.True(cellVm.CanOpenLayout);
    }
}
