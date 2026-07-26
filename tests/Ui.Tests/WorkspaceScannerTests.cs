using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
//  L1 gate — tree-node model
//  Constructs small trees by hand and reads back kind / name / paths / flags.
// ═══════════════════════════════════════════════════════════════════════════════

public class ProjectTreeNodeTests
{
    [Fact]
    public void Node_StoresKindNamePaths()
    {
        var n = new ProjectTreeNode(NodeKind.Cell, "AmpStage", "/ws/AmpStage", "AmpStage");
        Assert.Equal(NodeKind.Cell, n.Kind);
        Assert.Equal("AmpStage", n.Name);
        Assert.Equal("/ws/AmpStage", n.AbsolutePath);
        Assert.Equal("AmpStage", n.RelativePath);
    }

    [Fact]
    public void Node_DefaultFlags_AreFalse()
    {
        var n = new ProjectTreeNode(NodeKind.ViewFile, "amp.csym", "/ws/AmpStage/symbol/amp.csym", "AmpStage/symbol/amp.csym");
        Assert.False(n.IsPrimary);
        Assert.False(n.IsTestBench);
        Assert.Null(n.WarningReason);
    }

    [Fact]
    public void Node_OptionalFlags_Stored()
    {
        var n = new ProjectTreeNode(NodeKind.Cell, "TB", "/ws/TB", "TB",
            isTestBench: true, warningReason: "Primary symbol reference broken: amp.csym not found.");
        Assert.True(n.IsTestBench);
        Assert.Equal("Primary symbol reference broken: amp.csym not found.", n.WarningReason);
    }

    [Fact]
    public void Node_IsPrimary_Stored()
    {
        var n = new ProjectTreeNode(NodeKind.ViewFile, "amp.csym", "/ws/AmpStage/symbol/amp.csym", "AmpStage/symbol/amp.csym", isPrimary: true);
        Assert.True(n.IsPrimary);
    }

    [Fact]
    public void Node_StartsWithNoChildren()
    {
        var n = new ProjectTreeNode(NodeKind.Workspace, "ws", "/ws", "");
        Assert.Empty(n.Children);
    }

    [Fact]
    public void Node_AddChild_AppearsInChildren()
    {
        var parent = new ProjectTreeNode(NodeKind.Workspace, "ws", "/ws", "");
        var child  = new ProjectTreeNode(NodeKind.Cell, "AmpStage", "/ws/AmpStage", "AmpStage");
        parent.AddChild(child);
        Assert.Single(parent.Children);
        Assert.Same(child, parent.Children[0]);
    }

    [Fact]
    public void Node_AddChild_PreservesInsertionOrder()
    {
        var parent = new ProjectTreeNode(NodeKind.Workspace, "ws", "/ws", "");
        var a = new ProjectTreeNode(NodeKind.Cell, "A", "/ws/A", "A");
        var b = new ProjectTreeNode(NodeKind.Cell, "B", "/ws/B", "B");
        var c = new ProjectTreeNode(NodeKind.Cell, "C", "/ws/C", "C");
        parent.AddChild(a);
        parent.AddChild(b);
        parent.AddChild(c);
        Assert.Equal(new[] { "A", "B", "C" }, parent.Children.Select(n => n.Name));
    }

    [Fact]
    public void Node_BuildSmallTree_CanNavigate()
    {
        var ws = new ProjectTreeNode(NodeKind.Workspace, "MyWorkspace", "/ws", "");
        var cell = new ProjectTreeNode(NodeKind.Cell, "AmpStage", "/ws/AmpStage", "AmpStage", isTestBench: true);
        var symFolder = new ProjectTreeNode(NodeKind.CellViewFolder, "symbol", "/ws/AmpStage/symbol", "AmpStage/symbol");
        var symFile = new ProjectTreeNode(NodeKind.ViewFile, "amp.csym", "/ws/AmpStage/symbol/amp.csym", "AmpStage/symbol/amp.csym", isPrimary: true);

        symFolder.AddChild(symFile);
        cell.AddChild(symFolder);
        ws.AddChild(cell);

        Assert.Equal(NodeKind.Workspace, ws.Kind);
        Assert.Single(ws.Children);
        var c = ws.Children[0];
        Assert.Equal(NodeKind.Cell, c.Kind);
        Assert.True(c.IsTestBench);
        Assert.Single(c.Children);
        var sf = c.Children[0];
        Assert.Equal(NodeKind.CellViewFolder, sf.Kind);
        Assert.Single(sf.Children);
        var vf = sf.Children[0];
        Assert.Equal(NodeKind.ViewFile, vf.Kind);
        Assert.True(vf.IsPrimary);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  L2 gate — workspace scanner
//  Hand-built temp workspace tree → assert correct node structure.
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkspaceScannerTests : IDisposable
{
    private readonly string _root;

    public WorkspaceScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WSScan_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string MakeCell(string name, bool isTestBench = false, string? primarySymbol = null, string? primarySchematic = null)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        if (isTestBench || primarySymbol is not null || primarySchematic is not null)
        {
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.IsTestBench = isTestBench;
            if (primarySymbol is not null)    ccell.PrimarySymbol    = primarySymbol;
            if (primarySchematic is not null) ccell.PrimarySchematic = primarySchematic;
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        return cellDir;
    }

    private static string AddView(string cellDir, ViewType vt, string filename)
    {
        string sub = CellFolder.SubFolderPath(cellDir, vt);
        string path = Path.Combine(sub, filename);
        File.WriteAllText(path, "");
        return path;
    }

    private void WriteCws(CwsFile cws)
        => WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), cws);

    private static ProjectTreeNode FindKind(ProjectTreeNode parent, NodeKind kind)
        => parent.Children.First(n => n.Kind == kind);

    private static ProjectTreeNode? TryFindName(ProjectTreeNode parent, string name)
        => parent.Children.FirstOrDefault(n => n.Name == name);

    // ── Workspace root ────────────────────────────────────────────────────────

    [Fact]
    public void Scan_RootNode_IsWorkspace_WithFolderName()
    {
        var tree = WorkspaceScanner.Scan(_root);
        Assert.Equal(NodeKind.Workspace, tree.Kind);
        Assert.Equal(Path.GetFileName(_root), tree.Name);
        Assert.Equal(_root, tree.AbsolutePath);
        Assert.Equal("", tree.RelativePath);
    }

    [Fact]
    public void Scan_EmptyWorkspace_NoChildren()
    {
        var tree = WorkspaceScanner.Scan(_root);
        Assert.Empty(tree.Children);
    }

    // ── Cell detection ────────────────────────────────────────────────────────

    [Fact]
    public void Scan_CellFolder_ProducesCell()
    {
        MakeCell("AmpStage");
        var tree = WorkspaceScanner.Scan(_root);
        var cell = Assert.Single(tree.Children);
        Assert.Equal(NodeKind.Cell, cell.Kind);
        Assert.Equal("AmpStage", cell.Name);
    }

    [Fact]
    public void Scan_FolderWithoutCcell_ProducesUserFolder()
    {
        string dir = Path.Combine(_root, "displays");
        Directory.CreateDirectory(dir);
        var tree = WorkspaceScanner.Scan(_root);
        var uf = Assert.Single(tree.Children);
        Assert.Equal(NodeKind.UserFolder, uf.Kind);
        Assert.Equal("displays", uf.Name);
    }

    // ── Cell – empty sub-folders produce no CellViewFolder ────────────────────

    [Fact]
    public void Scan_Cell_EmptyViewSubFolders_ProduceNoCellViewFolder()
    {
        MakeCell("Empty");
        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "Empty");
        // All three sub-folders exist but are empty → no CellViewFolder children
        Assert.Empty(cell.Children);
    }

    // ── Cell – sole view file → primary ───────────────────────────────────────

    [Fact]
    public void Scan_Cell_SoleSymbol_MarkedPrimary()
    {
        string cellDir = MakeCell("AmpStage");
        AddView(cellDir, ViewType.Symbol, "amp.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "AmpStage");
        var symFolder = cell.Children.Single(n => n.Kind == NodeKind.CellViewFolder);
        Assert.Equal("symbol", symFolder.Name);
        var viewFile = Assert.Single(symFolder.Children);
        Assert.Equal("amp.csym", viewFile.Name);
        Assert.True(viewFile.IsPrimary);
    }

    // ── Cell – .clay is scanned like any other view file (L0b: no scanner change needed) ─

    [Fact]
    public void Scan_Cell_SoleLayout_MarkedPrimary()
    {
        string cellDir = MakeCell("AmpStage");
        AddView(cellDir, ViewType.Layout, "amp.clay");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "AmpStage");
        var layoutFolder = cell.Children.Single(n => n.Kind == NodeKind.CellViewFolder);
        Assert.Equal("layout", layoutFolder.Name);
        var viewFile = Assert.Single(layoutFolder.Children);
        Assert.Equal("amp.clay", viewFile.Name);
        Assert.True(viewFile.IsPrimary);
    }

    // ── Cell – multiple views, named primary ──────────────────────────────────

    [Fact]
    public void Scan_Cell_MultipleSymbols_NamedPrimary_Resolved()
    {
        string cellDir = MakeCell("Multi", primarySymbol: "b.csym");
        AddView(cellDir, ViewType.Symbol, "a.csym");
        AddView(cellDir, ViewType.Symbol, "b.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "Multi");
        var symFolder = cell.Children.Single(n => n.Kind == NodeKind.CellViewFolder);
        var files = symFolder.Children.ToList();
        Assert.Equal(2, files.Count);

        var a = files.Single(f => f.Name == "a.csym");
        var b = files.Single(f => f.Name == "b.csym");
        Assert.False(a.IsPrimary);
        Assert.True(b.IsPrimary);
    }

    // ── Cell – multiple views, no primary named ───────────────────────────────

    [Fact]
    public void Scan_Cell_MultipleSymbols_NoPrimaryNamed_NoneMarked()
    {
        string cellDir = MakeCell("NoPrimary");
        AddView(cellDir, ViewType.Symbol, "a.csym");
        AddView(cellDir, ViewType.Symbol, "b.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "NoPrimary");
        var symFolder = cell.Children.Single(n => n.Kind == NodeKind.CellViewFolder);
        Assert.All(symFolder.Children, f => Assert.False(f.IsPrimary));
        Assert.Null(cell.WarningReason); // NoPrimary is not a warning
    }

    // ── Cell – MissingNamedPrimary → WarningReason ────────────────────────────

    [Fact]
    public void Scan_Cell_MissingNamedPrimary_SetsWarningReason()
    {
        string cellDir = MakeCell("Broken", primarySymbol: "missing.csym");
        AddView(cellDir, ViewType.Symbol, "actual.csym");
        AddView(cellDir, ViewType.Symbol, "other.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "Broken");
        Assert.NotNull(cell.WarningReason);
        Assert.Contains("missing.csym", cell.WarningReason);
        Assert.Contains("Primary symbol reference broken", cell.WarningReason);
    }

    [Fact]
    public void Scan_Cell_MissingNamedPrimary_NoneOfTheFilesMarkedPrimary()
    {
        string cellDir = MakeCell("Broken", primarySymbol: "gone.csym");
        AddView(cellDir, ViewType.Symbol, "actual.csym");
        AddView(cellDir, ViewType.Symbol, "other.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "Broken");
        var symFolder = cell.Children.Single(n => n.Kind == NodeKind.CellViewFolder);
        Assert.All(symFolder.Children, f => Assert.False(f.IsPrimary));
    }

    [Fact]
    public void Scan_Cell_NoContradiction_NoWarning()
    {
        string cellDir = MakeCell("Clean");
        AddView(cellDir, ViewType.Symbol, "sym.csym");
        AddView(cellDir, ViewType.Schematic, "sch.csch");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "Clean");
        Assert.Null(cell.WarningReason);
    }

    // ── IsTestBench ───────────────────────────────────────────────────────────

    [Fact]
    public void Scan_Cell_IsTestBench_FlagPropagated()
    {
        MakeCell("TB", isTestBench: true);
        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "TB");
        Assert.True(cell.IsTestBench);
    }

    [Fact]
    public void Scan_Cell_NotTestBench_FlagFalse()
    {
        MakeCell("NormalCell");
        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "NormalCell");
        Assert.False(cell.IsTestBench);
    }

    // ── User folder with .cdd and .ccolor ─────────────────────────────────────

    [Fact]
    public void Scan_UserFolder_FilesClassifiedByExtension()
    {
        string uf = Path.Combine(_root, "data");
        Directory.CreateDirectory(uf);
        File.WriteAllText(Path.Combine(uf, "sweep.cdd"), "");
        File.WriteAllText(Path.Combine(uf, "midnight.ccolor"), "");
        File.WriteAllText(Path.Combine(uf, "pcb-2layer.ctech"), "");
        File.WriteAllText(Path.Combine(uf, "notes.txt"), "");

        var tree = WorkspaceScanner.Scan(_root);
        var folder = tree.Children.Single(n => n.Name == "data");
        Assert.Equal(NodeKind.UserFolder, folder.Kind);

        var cdd    = folder.Children.Single(n => n.Name == "sweep.cdd");
        var ccolor = folder.Children.Single(n => n.Name == "midnight.ccolor");
        var ctech  = folder.Children.Single(n => n.Name == "pcb-2layer.ctech");
        var txt    = folder.Children.Single(n => n.Name == "notes.txt");

        Assert.Equal(NodeKind.DataDisplayFile, cdd.Kind);
        Assert.Equal(NodeKind.ColorThemeFile, ccolor.Kind);
        Assert.Equal(NodeKind.TechFile, ctech.Kind);
        Assert.Equal(NodeKind.OtherFile, txt.Kind);
    }

    // ── L0c: tech/ folder appears as a UserFolder; .ctech files classified ───

    [Fact]
    public void Scan_TechFolder_AppearsAsUserFolder_CtechFilesClassified()
    {
        string techDir = Path.Combine(_root, "tech");
        Directory.CreateDirectory(techDir);
        File.WriteAllText(Path.Combine(techDir, "pcb-2layer.ctech"), "");

        var tree = WorkspaceScanner.Scan(_root);
        var folder = tree.Children.Single(n => n.Name == "tech");
        Assert.Equal(NodeKind.UserFolder, folder.Kind);

        var ctech = folder.Children.Single(n => n.Name == "pcb-2layer.ctech");
        Assert.Equal(NodeKind.TechFile, ctech.Kind);
    }

    // ── L0c: .cws DefaultTechRef + CwsTreeViewState.TechFiles round-trip ─────

    [Fact]
    public void CwsFile_DefaultTechRef_RoundTrips()
    {
        var cws = new CwsFile { DefaultTechRef = "tech/pcb-2layer.ctech" };
        WriteCws(cws);

        var reloaded = WorkspacePersistence.LoadFromFile(Path.Combine(_root, ".cws"));
        Assert.Equal("tech/pcb-2layer.ctech", reloaded.DefaultTechRef);
    }

    [Fact]
    public void CwsFile_DefaultTechRef_AbsentOnOlderFile_LoadsAsNull()
    {
        // An older .cws written without DefaultTechRef must still load cleanly (alpha policy:
        // an absent field means "no default", not a load failure).
        WriteCws(new CwsFile());

        var reloaded = WorkspacePersistence.LoadFromFile(Path.Combine(_root, ".cws"));
        Assert.Null(reloaded.DefaultTechRef);
    }

    [Fact]
    public void CwsTreeViewState_TechFiles_DefaultsTrue_AndRoundTrips()
    {
        var cws = new CwsFile { TreeViewState = new CwsTreeViewState { TechFiles = false } };
        WriteCws(cws);

        var reloaded = WorkspacePersistence.LoadFromFile(Path.Combine(_root, ".cws"));
        Assert.False(reloaded.TreeViewState!.TechFiles);
        Assert.True(new CwsTreeViewState().TechFiles);
    }

    // ── Hidden files (.DS_Store / .source) ───────────────────────────────────

    [Fact]
    public void Scan_HiddenFiles_NotVisibleAtRootOrUserFolder()
    {
        // Workspace root: a.csch, .DS_Store, x.source, notes.txt
        string cellDir = MakeCell("MyCell");
        AddView(cellDir, ViewType.Schematic, "a.csch");
        File.WriteAllText(Path.Combine(_root, ".DS_Store"), "");
        File.WriteAllText(Path.Combine(_root, "x.source"), "");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "");

        // User folder: same hidden files inside
        string uf = Path.Combine(_root, "docs");
        Directory.CreateDirectory(uf);
        File.WriteAllText(Path.Combine(uf, ".DS_Store"), "");
        File.WriteAllText(Path.Combine(uf, "readme.source"), "");
        File.WriteAllText(Path.Combine(uf, "info.txt"), "");

        var tree = WorkspaceScanner.Scan(_root);

        // Root loose files: only notes.txt (not .DS_Store or x.source)
        var rootFiles = tree.Children.Where(n => n.Kind == NodeKind.OtherFile).Select(n => n.Name).ToList();
        Assert.Contains("notes.txt", rootFiles);
        Assert.DoesNotContain(".DS_Store", rootFiles);
        Assert.DoesNotContain("x.source", rootFiles);

        // User folder: only info.txt
        var folder = tree.Children.Single(n => n.Name == "docs");
        var folderFiles = folder.Children.Select(n => n.Name).ToList();
        Assert.Contains("info.txt", folderFiles);
        Assert.DoesNotContain(".DS_Store", folderFiles);
        Assert.DoesNotContain("readme.source", folderFiles);
    }

    [Fact]
    public void Scan_HiddenFileInKnownFiles_StillVisible()
    {
        // .DS_Store listed explicitly as a Known File → opt-in still works
        string dsStorePath = Path.Combine(_root, ".DS_Store");
        File.WriteAllText(dsStorePath, "");

        WriteCws(new CwsFile { KnownFiles = [".DS_Store"] });

        var tree = WorkspaceScanner.Scan(_root);

        // Loose root files: .DS_Store suppressed
        var rootFiles = tree.Children.Where(n => n.Kind == NodeKind.OtherFile).Select(n => n.Name).ToList();
        Assert.DoesNotContain(".DS_Store", rootFiles);

        // Known Files group: .DS_Store present
        var kfGroup = tree.Children.Single(n => n.Kind == NodeKind.KnownFilesGroup);
        Assert.Contains(".DS_Store", kfGroup.Children.Select(n => n.Name));
    }

    // ── Relative paths ────────────────────────────────────────────────────────

    [Fact]
    public void Scan_RelativePaths_RelativeToWorkspaceRoot()
    {
        string cellDir = MakeCell("AmpStage");
        AddView(cellDir, ViewType.Symbol, "amp.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "AmpStage");
        Assert.Equal("AmpStage", cell.RelativePath);

        var symFolder = cell.Children.Single();
        Assert.Equal(Path.Combine("AmpStage", "symbol"), symFolder.RelativePath);

        var viewFile = symFolder.Children.Single();
        Assert.Equal(Path.Combine("AmpStage", "symbol", "amp.csym"), viewFile.RelativePath);
    }

    // ── Stable ordering ───────────────────────────────────────────────────────

    [Fact]
    public void Scan_OrderingIsAlphabetical()
    {
        MakeCell("Zebra");
        MakeCell("Alpha");
        MakeCell("Mango");

        var tree = WorkspaceScanner.Scan(_root);
        var names = tree.Children.Select(n => n.Name).ToList();
        Assert.Equal(new[] { "Alpha", "Mango", "Zebra" }, names);
    }

    [Fact]
    public void Scan_ViewFiles_OrderedAlphabetically()
    {
        string cellDir = MakeCell("MultiSym");
        AddView(cellDir, ViewType.Symbol, "z_sym.csym");
        AddView(cellDir, ViewType.Symbol, "a_sym.csym");
        AddView(cellDir, ViewType.Symbol, "m_sym.csym");

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "MultiSym");
        var symFolder = cell.Children.Single();
        var names = symFolder.Children.Select(n => n.Name).ToList();
        Assert.Equal(new[] { "a_sym.csym", "m_sym.csym", "z_sym.csym" }, names);
    }

    // ── Multiple cells: schematic + symbol + empty layout ─────────────────────

    [Fact]
    public void Scan_Cell_MultipleViewTypes_CorrectCellViewFolders()
    {
        string cellDir = MakeCell("Full");
        AddView(cellDir, ViewType.Schematic, "full.csch");
        AddView(cellDir, ViewType.Symbol, "full.csym");
        // layout/ stays empty → no CellViewFolder

        var tree = WorkspaceScanner.Scan(_root);
        var cell = tree.Children.Single(n => n.Name == "Full");
        Assert.Equal(2, cell.Children.Count);

        var schFolder = cell.Children.Single(n => n.Name == "schematic");
        var symFolder = cell.Children.Single(n => n.Name == "symbol");
        Assert.Equal(NodeKind.CellViewFolder, schFolder.Kind);
        Assert.Equal(NodeKind.CellViewFolder, symFolder.Kind);
        // No layout node
        Assert.DoesNotContain(cell.Children, n => n.Name == "layout");
    }

    // ── Missing .cws → scan still works ──────────────────────────────────────

    [Fact]
    public void Scan_MissingCws_ScanStillProducesCells()
    {
        MakeCell("AmpStage");
        // No .cws written
        var tree = WorkspaceScanner.Scan(_root);
        Assert.Single(tree.Children);
        Assert.Equal(NodeKind.Cell, tree.Children[0].Kind);
    }

    // ── LibrariesGroup ────────────────────────────────────────────────────────

    [Fact]
    public void Scan_ResolvableLibrary_ProducesLibraryGroupWithCells()
    {
        // Build a sibling library folder
        string libDir = Path.Combine(Path.GetTempPath(), "TestLib_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(libDir);
            CellFolder.CreateCellFolder(libDir, "Resistor");
            CellFolder.CreateCellFolder(libDir, "Capacitor");

            WriteCws(new CwsFile { LibraryRefs = [libDir] });

            var tree = WorkspaceScanner.Scan(_root);
            var libGroup = FindKind(tree, NodeKind.LibrariesGroup);
            Assert.Equal("Libraries", libGroup.Name);

            var lib = Assert.Single(libGroup.Children);
            Assert.Equal(NodeKind.Library, lib.Kind);
            Assert.Null(lib.WarningReason);
            Assert.Equal(2, lib.Children.Count);
            Assert.All(lib.Children, c => Assert.Equal(NodeKind.Cell, c.Kind));
        }
        finally { Directory.Delete(libDir, recursive: true); }
    }

    [Fact]
    public void Scan_BrokenLibraryRef_ProducesWarningNode()
    {
        string badPath = Path.Combine(_root, "nonexistent_lib");
        WriteCws(new CwsFile { LibraryRefs = [badPath] });

        var tree = WorkspaceScanner.Scan(_root);
        var libGroup = FindKind(tree, NodeKind.LibrariesGroup);
        var lib = Assert.Single(libGroup.Children);
        Assert.Equal(NodeKind.Library, lib.Kind);
        Assert.NotNull(lib.WarningReason);
        Assert.Contains("unresolved", lib.WarningReason);
    }

    [Fact]
    public void Scan_MixedLibraryRefs_OneGoodOneBroken()
    {
        string libDir = Path.Combine(Path.GetTempPath(), "GoodLib_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(libDir);
            CellFolder.CreateCellFolder(libDir, "Inductor");

            string badPath = Path.Combine(_root, "gone_lib");
            WriteCws(new CwsFile { LibraryRefs = [libDir, badPath] });

            var tree = WorkspaceScanner.Scan(_root);
            var libGroup = FindKind(tree, NodeKind.LibrariesGroup);
            Assert.Equal(2, libGroup.Children.Count);

            var goodLib = libGroup.Children.Single(n => n.WarningReason is null);
            var badLib  = libGroup.Children.Single(n => n.WarningReason is not null);
            Assert.Equal(NodeKind.Library, goodLib.Kind);
            Assert.Equal(NodeKind.Library, badLib.Kind);
        }
        finally { Directory.Delete(libDir, recursive: true); }
    }

    // ── KnownFilesGroup ───────────────────────────────────────────────────────

    [Fact]
    public void Scan_ResolvableKnownFile_ProducesNodeWithNoWarning()
    {
        string kfPath = Path.Combine(_root, "notes.txt");
        File.WriteAllText(kfPath, "");
        WriteCws(new CwsFile { KnownFiles = [kfPath] });

        var tree = WorkspaceScanner.Scan(_root);
        var kfGroup = FindKind(tree, NodeKind.KnownFilesGroup);
        Assert.Equal("Known Files", kfGroup.Name);
        var kf = Assert.Single(kfGroup.Children);
        Assert.Equal(NodeKind.KnownFile, kf.Kind);
        Assert.Null(kf.WarningReason);
    }

    [Fact]
    public void Scan_BrokenKnownFile_ProducesWarningNode()
    {
        string badPath = Path.Combine(_root, "missing_file.cdd");
        WriteCws(new CwsFile { KnownFiles = [badPath] });

        var tree = WorkspaceScanner.Scan(_root);
        var kfGroup = FindKind(tree, NodeKind.KnownFilesGroup);
        var kf = Assert.Single(kfGroup.Children);
        Assert.Equal(NodeKind.KnownFile, kf.Kind);
        Assert.NotNull(kf.WarningReason);
        Assert.Contains("not found", kf.WarningReason);
    }

    // ── No LibrariesGroup / KnownFilesGroup when .cws is empty ───────────────

    [Fact]
    public void Scan_EmptyCws_NoGroupNodes()
    {
        WriteCws(new CwsFile());
        var tree = WorkspaceScanner.Scan(_root);
        Assert.DoesNotContain(tree.Children, n => n.Kind == NodeKind.LibrariesGroup);
        Assert.DoesNotContain(tree.Children, n => n.Kind == NodeKind.KnownFilesGroup);
    }

    // ── Full L2 gate scenario ─────────────────────────────────────────────────

    [Fact]
    public void Scan_FullL2Scenario()
    {
        // Cells
        string ampDir = MakeCell("AmpStage");
        AddView(ampDir, ViewType.Schematic, "amp.csch");
        AddView(ampDir, ViewType.Symbol, "amp.csym");
        // layout stays empty

        string tbDir = MakeCell("TopLevel", isTestBench: true);
        AddView(tbDir, ViewType.Schematic, "tb.csch");

        string multiDir = MakeCell("MultiSym", primarySymbol: "b.csym");
        AddView(multiDir, ViewType.Symbol, "a.csym");
        AddView(multiDir, ViewType.Symbol, "b.csym");

        string brokenDir = MakeCell("BrokenPrimary", primarySymbol: "gone.csym");
        AddView(brokenDir, ViewType.Symbol, "actual.csym");
        AddView(brokenDir, ViewType.Symbol, "other.csym");

        // User folder
        string ufDir = Path.Combine(_root, "assets");
        Directory.CreateDirectory(ufDir);
        File.WriteAllText(Path.Combine(ufDir, "sweep.cdd"), "");
        File.WriteAllText(Path.Combine(ufDir, "dark.ccolor"), "");

        // Library
        string libDir = Path.Combine(Path.GetTempPath(), "L2Lib_" + Guid.NewGuid().ToString("N")[..8]);
        string badLibPath = Path.Combine(_root, "no_such_lib");
        string goodKfPath = Path.Combine(ufDir, "sweep.cdd");
        string badKfPath  = Path.Combine(_root, "missing.cdd");
        try
        {
            Directory.CreateDirectory(libDir);
            CellFolder.CreateCellFolder(libDir, "Resistor");

            WriteCws(new CwsFile
            {
                LibraryRefs = [libDir, badLibPath],
                KnownFiles  = [goodKfPath, badKfPath],
            });

            var tree = WorkspaceScanner.Scan(_root);

            // Workspace root
            Assert.Equal(NodeKind.Workspace, tree.Kind);

            // Cell children (alphabetical: AmpStage, BrokenPrimary, MultiSym, TopLevel)
            // plus UserFolder (assets), then LibrariesGroup, KnownFilesGroup
            var cells = tree.Children.Where(n => n.Kind == NodeKind.Cell).ToList();
            Assert.Equal(4, cells.Count);
            Assert.Equal(new[] { "AmpStage", "BrokenPrimary", "MultiSym", "TopLevel" },
                cells.Select(c => c.Name));

            // AmpStage: schematic + symbol (sole → primary each), no layout folder
            var amp = cells.Single(c => c.Name == "AmpStage");
            Assert.Null(amp.WarningReason);
            var ampSchFolder = amp.Children.Single(n => n.Name == "schematic");
            var ampSymFolder = amp.Children.Single(n => n.Name == "symbol");
            Assert.True(ampSchFolder.Children.Single().IsPrimary);
            Assert.True(ampSymFolder.Children.Single().IsPrimary);
            Assert.DoesNotContain(amp.Children, n => n.Name == "layout");

            // TopLevel: IsTestBench
            var tb = cells.Single(c => c.Name == "TopLevel");
            Assert.True(tb.IsTestBench);
            Assert.True(tb.Children.Single().Children.Single().IsPrimary);

            // MultiSym: b.csym is primary, a.csym is not
            var multi = cells.Single(c => c.Name == "MultiSym");
            var multiSym = multi.Children.Single();
            var a = multiSym.Children.Single(f => f.Name == "a.csym");
            var b = multiSym.Children.Single(f => f.Name == "b.csym");
            Assert.False(a.IsPrimary);
            Assert.True(b.IsPrimary);

            // BrokenPrimary: warning for MissingNamedPrimary
            var broken = cells.Single(c => c.Name == "BrokenPrimary");
            Assert.NotNull(broken.WarningReason);
            Assert.Contains("gone.csym", broken.WarningReason);

            // assets user folder
            var uf = tree.Children.Single(n => n.Kind == NodeKind.UserFolder);
            Assert.Equal("assets", uf.Name);
            Assert.Contains(uf.Children, f => f.Kind == NodeKind.DataDisplayFile && f.Name == "sweep.cdd");
            Assert.Contains(uf.Children, f => f.Kind == NodeKind.ColorThemeFile && f.Name == "dark.ccolor");

            // Libraries group
            var libGroup = FindKind(tree, NodeKind.LibrariesGroup);
            Assert.Equal(2, libGroup.Children.Count);
            var goodLib = libGroup.Children.Single(n => n.WarningReason is null);
            var badLib  = libGroup.Children.Single(n => n.WarningReason is not null);
            Assert.Single(goodLib.Children); // Resistor
            Assert.Contains("unresolved", badLib.WarningReason);

            // Known Files group
            var kfGroup = FindKind(tree, NodeKind.KnownFilesGroup);
            Assert.Equal(2, kfGroup.Children.Count);
            var goodKf = kfGroup.Children.Single(n => n.Name == "sweep.cdd");
            var badKf  = kfGroup.Children.Single(n => n.Name == "missing.cdd");
            Assert.Null(goodKf.WarningReason);
            Assert.NotNull(badKf.WarningReason);
        }
        finally { Directory.Delete(libDir, recursive: true); }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  L3 gate — refresh contract
//  Re-running Scan after filesystem changes reflects the new state.
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkspaceModelRefreshTests : IDisposable
{
    private readonly string _root;

    public WorkspaceModelRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WSRefresh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WorkspaceModel_StartsWithScan()
    {
        var model = new WorkspaceModel(_root);
        Assert.Equal(NodeKind.Workspace, model.RootNode.Kind);
    }

    [Fact]
    public void Rescan_AfterAddingCell_ReflectsNewCell()
    {
        var model = new WorkspaceModel(_root);
        Assert.Empty(model.RootNode.Children);

        CellFolder.CreateCellFolder(_root, "NewCell");
        model.Rescan();

        Assert.Single(model.RootNode.Children);
        Assert.Equal(NodeKind.Cell, model.RootNode.Children[0].Kind);
        Assert.Equal("NewCell", model.RootNode.Children[0].Name);
    }

    [Fact]
    public void Rescan_AfterDeletingCell_CellDisappears()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "TempCell");
        var model = new WorkspaceModel(_root);
        Assert.Single(model.RootNode.Children);

        Directory.Delete(cellDir, recursive: true);
        model.Rescan();

        Assert.Empty(model.RootNode.Children);
    }

    [Fact]
    public void Rescan_ContradictionAppearsWhenPrimaryFileDeleted()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Cell");
        string symDir  = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        // Three symbols; a.csym is named primary.
        // Deleting a.csym leaves b + c — two files, named primary absent → MissingNamedPrimary.
        // (If only one file remained, SoleFile would fire and there would be no warning.)
        string sym1 = Path.Combine(symDir, "a.csym");
        File.WriteAllText(sym1, "");
        File.WriteAllText(Path.Combine(symDir, "b.csym"), "");
        File.WriteAllText(Path.Combine(symDir, "c.csym"), "");
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimarySymbol = "a.csym";
        CellPersistence.SaveToFile(ccellPath, ccell);

        var model = new WorkspaceModel(_root);
        Assert.Null(model.RootNode.Children.Single().WarningReason); // NamedPresent → clean

        File.Delete(sym1); // ← breaks the named primary
        model.Rescan();

        var cell = model.RootNode.Children.Single();
        Assert.NotNull(cell.WarningReason);
        Assert.Contains("a.csym", cell.WarningReason);
    }

    [Fact]
    public void Rescan_ContradictionDisappearsWhenPrimaryFileRestored()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Cell");
        string symDir  = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        // Two files exist (b + c), .ccell names a.csym (absent) → MissingNamedPrimary.
        // Creating a.csym resolves to NamedPresent → no warning.
        string sym1 = Path.Combine(symDir, "a.csym");
        File.WriteAllText(Path.Combine(symDir, "b.csym"), "");
        File.WriteAllText(Path.Combine(symDir, "c.csym"), "");
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimarySymbol = "a.csym";
        CellPersistence.SaveToFile(ccellPath, ccell);

        var model = new WorkspaceModel(_root);
        Assert.NotNull(model.RootNode.Children.Single().WarningReason); // contradiction present

        File.WriteAllText(sym1, ""); // ← restore the named primary
        model.Rescan();

        Assert.Null(model.RootNode.Children.Single().WarningReason);
    }

    [Fact]
    public void Rescan_ReturnsNewRootNode_NotSameReference()
    {
        var model = new WorkspaceModel(_root);
        var firstRoot = model.RootNode;
        model.Rescan();
        // Each Rescan() builds a fresh tree
        Assert.NotSame(firstRoot, model.RootNode);
    }

    [Fact]
    public void Rescan_IdempotentOnUnchangedTree_SameStructure()
    {
        CellFolder.CreateCellFolder(_root, "Alpha");
        var model = new WorkspaceModel(_root);
        var first = model.RootNode.Children.Select(n => n.Name).ToList();
        model.Rescan();
        var second = model.RootNode.Children.Select(n => n.Name).ToList();
        Assert.Equal(first, second);
    }
}
