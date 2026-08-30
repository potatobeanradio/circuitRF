using System.IO;
using CircuitRF.Ui;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Known Files that are circuitRF documents: they open as orphan documents (double-click and an
/// "Open" context item), the three cell views additionally offer "Copy to Workspace as Cell…" behind
/// a validation gate, and a BROKEN reference reveals the nearest folder that still exists.
/// </summary>
public class KnownFileDocumentActionsTests : IDisposable
{
    private readonly string _root;

    public KnownFileDocumentActionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ProjectTreeNodeViewModel KnownFileVm(string absPath)
    {
        bool isDir = Directory.Exists(absPath);
        var node = new ProjectTreeNode(
            NodeKind.KnownFile, Path.GetFileName(absPath), absPath, absPath,
            warningReason: (isDir || File.Exists(absPath)) ? null : "Known File path not found",
            isDirectory: isDir);
        return new ProjectTreeNodeViewModel(node, new ProjectTreeFilterState());
    }

    private string WriteValidSchematic(string name)
    {
        var path = Path.Combine(_root, name);
        SchematicPersistence.SaveToFile(path, new SchematicEditModel(), cellName: "Amp");
        return path;
    }

    private string WriteValidLayout(string name)
    {
        var path = Path.Combine(_root, name);
        LayoutPersistence.SaveToFile(path, new LayoutView());
        return path;
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── The one extension→kind table ─────────────────────────────────────────

    [Theory]
    [InlineData("a.csch",   NodeKind.ViewFile)]
    [InlineData("a.csym",   NodeKind.ViewFile)]
    [InlineData("a.clay",   NodeKind.ViewFile)]
    [InlineData("a.CSCH",   NodeKind.ViewFile)]   // extension match is case-insensitive
    [InlineData("a.cdd",    NodeKind.DataDisplayFile)]
    [InlineData("a.charm",  NodeKind.HarmonicaFile)]
    [InlineData("a.wBond",  NodeKind.WBondFile)]
    [InlineData("a.ctech",  NodeKind.TechFile)]
    [InlineData("a.cem",    NodeKind.EmSetupFile)]
    [InlineData("a.ccolor", NodeKind.ColorThemeFile)]
    [InlineData("a.s2p",    NodeKind.OtherFile)]
    [InlineData("a",        NodeKind.OtherFile)]
    public void ClassifyFile_MapsExtensionToKind(string name, NodeKind expected)
        => Assert.Equal(expected, WorkspaceScanner.ClassifyFile(Path.Combine(_root, name)));

    // ── Opening a Known File as a document ───────────────────────────────────

    [Theory]
    [InlineData("doc.csch")]
    [InlineData("doc.csym")]
    [InlineData("doc.clay")]
    [InlineData("doc.cdd")]
    [InlineData("doc.charm")]
    [InlineData("doc.wBond")]
    [InlineData("doc.ctech")]
    [InlineData("doc.cem")]
    public void KnownFile_WithDocumentExtension_IsOpenable(string name)
    {
        var vm = KnownFileVm(WriteFile(name, "{}"));
        Assert.True(vm.IsOpenableKnownFile);
        Assert.True(vm.IsOpenableFile);   // the shared "Open" context item follows this one
    }

    [Theory]
    [InlineData("data.s2p")]
    [InlineData("notes.txt")]
    [InlineData("dark.ccolor")]           // a colour theme has no document editor
    public void KnownFile_WithoutDocumentExtension_IsNotOpenable(string name)
    {
        var vm = KnownFileVm(WriteFile(name, "x"));
        Assert.False(vm.IsOpenableKnownFile);
        Assert.False(vm.IsOpenableFile);
    }

    [Fact]
    public void KnownFile_Directory_IsNotOpenable_EvenWithADocumentExtension()
    {
        var dir = Path.Combine(_root, "bundle.clay");
        Directory.CreateDirectory(dir);
        Assert.False(KnownFileVm(dir).IsOpenableKnownFile);
    }

    [Fact]
    public void KnownFile_BrokenReference_IsNotOpenable()
        => Assert.False(KnownFileVm(Path.Combine(_root, "gone.csch")).IsOpenableKnownFile);

    /// <summary>
    /// The double-click and the "Open" item both run OpenNode, and OpenNode has to classify a
    /// KnownFile by extension — its own NodeKind says nothing about what the file is.
    /// </summary>
    [Fact]
    public void OpenNode_ClassifiesAKnownFileByExtension()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        int i = src.IndexOf("public void OpenNode(ProjectTreeNodeViewModel node)", StringComparison.Ordinal);
        Assert.True(i > 0, "OpenNode not found");
        var body = src[i..(i + 2000)];
        Assert.Contains("WorkspaceScanner.ClassifyFile(node.AbsolutePath)", body, StringComparison.Ordinal);
        Assert.Contains("node.IsDirectory", body, StringComparison.Ordinal);
    }

    // ── Copy to Workspace as Cell… — which nodes offer it ────────────────────

    [Theory]
    [InlineData("v.csch", true)]
    [InlineData("v.csym", true)]
    [InlineData("v.clay", true)]
    [InlineData("v.cdd",  false)]
    [InlineData("v.cem",  false)]
    [InlineData("v.s2p",  false)]
    public void IsKnownFileCopyableAsCell_OnlyTheThreeCellViews(string name, bool expected)
        => Assert.Equal(expected, KnownFileVm(WriteFile(name, "{}")).IsKnownFileCopyableAsCell);

    [Fact]
    public void IsKnownFileCopyableAsCell_FalseForABrokenReference()
        => Assert.False(KnownFileVm(Path.Combine(_root, "gone.csch")).IsKnownFileCopyableAsCell);

    [Fact]
    public void IsKnownFileCopyableAsCell_FalseForAnOrdinaryViewFileNode()
    {
        // The item is a KNOWN FILE action — a .csch already inside a cell has Make Primary instead.
        var path = WriteValidSchematic("Amp.csch");
        var node = new ProjectTreeNode(NodeKind.ViewFile, "Amp.csch", path, "Amp.csch");
        Assert.False(new ProjectTreeNodeViewModel(node, new ProjectTreeFilterState()).IsKnownFileCopyableAsCell);
    }

    // ── The validation gate itself ───────────────────────────────────────────

    [Fact]
    public void Validator_AcceptsAWellFormedSchematic()
        => Assert.Null(CellViewFileValidator.DescribeDefect(WriteValidSchematic("Amp.csch"), ViewType.Schematic));

    [Fact]
    public void Validator_AcceptsAWellFormedLayout()
        => Assert.Null(CellViewFileValidator.DescribeDefect(WriteValidLayout("Amp.clay"), ViewType.Layout));

    [Fact]
    public void Validator_RejectsAMissingFile()
    {
        var why = CellViewFileValidator.DescribeDefect(Path.Combine(_root, "gone.csch"), ViewType.Schematic);
        Assert.NotNull(why);
        Assert.Contains("no longer there", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsAnEmptyFile()
    {
        var why = CellViewFileValidator.DescribeDefect(WriteFile("Empty.csch", ""), ViewType.Schematic);
        Assert.NotNull(why);
        Assert.Contains("empty", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsNonJson()
    {
        var why = CellViewFileValidator.DescribeDefect(WriteFile("Junk.csch", "not json at all"), ViewType.Schematic);
        Assert.NotNull(why);
        Assert.Contains("JSON", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsAJsonArray()
    {
        var why = CellViewFileValidator.DescribeDefect(WriteFile("Arr.csch", "[1,2,3]"), ViewType.Schematic);
        Assert.NotNull(why);
    }

    /// <summary>
    /// The case an extension check alone cannot catch, and the reason the validator looks at the
    /// JSON keys: System.Text.Json ignores unknown members, so a layout renamed to .csch
    /// deserializes CLEANLY into an empty schematic and would have produced a silently wrong cell.
    /// </summary>
    [Fact]
    public void Validator_RejectsALayoutWearingASchematicExtension()
    {
        var clay = WriteValidLayout("Real.clay");
        var disguised = Path.Combine(_root, "Disguised.csch");
        File.Copy(clay, disguised);

        var why = CellViewFileValidator.DescribeDefect(disguised, ViewType.Schematic);
        Assert.NotNull(why);
        Assert.Contains("Components", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsASchematicWearingASymbolExtension()
    {
        var csch = WriteValidSchematic("Real.csch");
        var disguised = Path.Combine(_root, "Disguised.csym");
        File.Copy(csch, disguised);

        var why = CellViewFileValidator.DescribeDefect(disguised, ViewType.Symbol);
        Assert.NotNull(why);
        Assert.Contains("Primitives", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsAFormatVersionFromANewerBuild()
    {
        var why = CellViewFileValidator.DescribeDefect(
            WriteFile("Future.csch", """{"FormatVersion":9999,"Components":[],"Wires":[]}"""),
            ViewType.Schematic);
        Assert.NotNull(why);
    }

    [Fact]
    public void ViewTypeFor_MapsOnlyTheThreeCellViewExtensions()
    {
        Assert.Equal(ViewType.Schematic, CellViewFileValidator.ViewTypeFor("/x/a.csch"));
        Assert.Equal(ViewType.Symbol,    CellViewFileValidator.ViewTypeFor("/x/a.csym"));
        Assert.Equal(ViewType.Layout,    CellViewFileValidator.ViewTypeFor("/x/a.CLAY"));
        Assert.Null(CellViewFileValidator.ViewTypeFor("/x/a.cdd"));
        Assert.Null(CellViewFileValidator.ViewTypeFor("/x/a"));
    }

    /// <summary>The gate runs BEFORE anything is created — a cell must never be left behind for a
    /// file the editor would then refuse to open.</summary>
    [Fact]
    public void CopyAsCell_ValidatesBeforeItCreatesAnything()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        int i = src.IndexOf("public async Task CopyKnownFileToWorkspaceAsCellAsync(", StringComparison.Ordinal);
        Assert.True(i > 0, "CopyKnownFileToWorkspaceAsCellAsync not found");
        var body = src[i..src.IndexOf("public void RemoveKnownFile(", i, StringComparison.Ordinal)];

        int validate = body.IndexOf("CellViewFileValidator.DescribeDefect", StringComparison.Ordinal);
        int prompt   = body.IndexOf("new InputNameDialog", StringComparison.Ordinal);
        int create   = body.IndexOf("CellFolder.CreateCellFolder", StringComparison.Ordinal);
        Assert.True(validate > 0 && prompt > 0 && create > 0);
        Assert.True(validate < prompt, "validation must run before the user is asked to name a cell");
        Assert.True(validate < create, "validation must run before the cell folder is created");
    }

    // ── Reveal on a broken reference ─────────────────────────────────────────

    [Fact]
    public void NearestExistingDirectory_MissingFileInAnExistingFolder_ReturnsTheFolder()
    {
        var folder = Path.Combine(_root, "folder1");
        Directory.CreateDirectory(folder);
        Assert.Equal(folder, FileReveal.NearestExistingDirectory(Path.Combine(folder, "test.txt")));
    }

    [Fact]
    public void NearestExistingDirectory_WalksUpPastMissingFolders()
    {
        var folder = Path.Combine(_root, "folder1");
        Directory.CreateDirectory(folder);
        var deep = Path.Combine(folder, "gone", "alsoGone", "test.txt");
        Assert.Equal(folder, FileReveal.NearestExistingDirectory(deep));
    }

    [Fact]
    public void NearestExistingDirectory_ExistingDirectory_IsItsOwnAnswer()
        => Assert.Equal(_root, FileReveal.NearestExistingDirectory(_root));

    [Fact]
    public void NearestExistingDirectory_ExistingFile_ReturnsItsParent()
        => Assert.Equal(_root, FileReveal.NearestExistingDirectory(WriteFile("here.txt", "x")));

    [Fact]
    public void NearestExistingDirectory_NullOrBlank_IsNull()
    {
        Assert.Null(FileReveal.NearestExistingDirectory(null));
        Assert.Null(FileReveal.NearestExistingDirectory("   "));
    }

    [Fact]
    public void Reveal_FallsBackToTheNearestExistingFolder()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        int i = src.IndexOf("public void Reveal(ProjectTreeNodeViewModel node)", StringComparison.Ordinal);
        Assert.True(i > 0, "Reveal not found");
        var body = src[i..(i + 1200)];
        Assert.Contains("FileReveal.NearestExistingDirectory", body, StringComparison.Ordinal);
    }

    // ── The context menu ─────────────────────────────────────────────────────

    [Fact]
    public void ContextMenu_OffersCopyToWorkspaceAsCell()
    {
        var xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml"));
        Assert.Contains("Copy to Workspace as Cell", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding CopyToWorkspaceAsCellCommand}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding IsKnownFileCopyableAsCell}", xaml, StringComparison.Ordinal);
        // Open External… stays — a bookmarked .pdf still has nowhere else to go.
        Assert.Contains("Open External", xaml, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
