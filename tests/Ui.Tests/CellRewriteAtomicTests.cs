using System.IO;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A document a cell rename or move rewrites is whole afterwards, the old text or the new. Each test puts a folder where
/// the write's temp file goes, so a write through the temp file fails before the document is touched and an in-place
/// write would go straight through.
/// </summary>
public class CellRewriteAtomicTests : IDisposable
{
    private readonly string _ws;

    public CellRewriteAtomicTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), $"crf_atomic_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ws);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile());
        WorkspaceRootFinder.InvalidateCache();
        ExternalCellRef.InvalidateCache();
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        ExternalCellRef.InvalidateCache();
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    private string Cell(string name) => CellFolder.CreateCellFolder(_ws, name);

    private static string Document(string cellDir, ViewType view, string extension, string body)
    {
        string dir = CellFolder.SubFolderPath(cellDir, view);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Path.GetFileName(cellDir) + extension);
        File.WriteAllText(path, body);
        return path;
    }

    private static string Referrer(string cellDir, string extension) => extension switch
    {
        ".csch" => Document(cellDir, ViewType.Schematic, extension, """{"Components":[{"CellRef":"../../oldCell"}]}"""),
        ".clay" => Document(cellDir, ViewType.Layout, extension, """{"Instances":[{"CellRef":"../../oldCell"}]}"""),
        _ => Document(cellDir, ViewType.ThreeD, extension, """{"Instances":[{"CellRef":"../../oldCell"}]}"""),
    };

    private static void BlockTemp(string path) => Directory.CreateDirectory(path + ".tmp");

    [Theory]
    [InlineData(".csch")]
    [InlineData(".clay")]
    [InlineData(".c3d")]
    public void ReferenceRewrite_ThatCannotWrite_LeavesTheDocumentWhole(string extension)
    {
        string oldCell = Cell("oldCell");
        string path = Referrer(Cell("cellA"), extension);
        string before = File.ReadAllText(path);
        BlockTemp(path);

        var rewritten = CellUsageScanner.RewriteCellReferences(_ws, oldCell, "newCell", out var failed);

        Assert.Empty(rewritten);
        Assert.Contains(failed, f => f.StartsWith(path, StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void WBondLinkRewrite_ThatCannotWrite_LeavesTheSchematicWhole()
    {
        string cell = Cell("PowerAmp");
        string layoutDir = CellFolder.SubFolderPath(cell, ViewType.Layout);
        string path = Document(cell, ViewType.Schematic, ".csch",
            $$"""{"Components":[{"Symbol":"WBond","Parameters":[{"Name":"{{WBondPlacement.FileParameter}}","Expression":"../layout/Amp.wBond"}]}]}""");
        string before = File.ReadAllText(path);
        BlockTemp(path);

        var rewritten = CellUsageScanner.RewriteWBondLinks(_ws, layoutDir, "Amp", "PowerAmp", "Amp", "PowerAmp", out var failed);

        Assert.Empty(rewritten);
        Assert.Contains(failed, f => f.StartsWith(path, StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void MoveRewrite_ThatCannotWrite_LeavesTheDocumentWhole()
    {
        string target = Cell("oldCell");
        string path = Referrer(Cell("cellA"), ".csch");
        string before = File.ReadAllText(path);
        BlockTemp(path);
        string moved = Path.Combine(_ws, "sub", "oldCell");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);

        var capture = WorkspaceMove.Capture([_ws]);
        Directory.Move(target, moved);
        WorkspaceRootFinder.InvalidateCache();
        ExternalCellRef.InvalidateCache();
        var result = WorkspaceMove.Apply(capture, target, moved);

        Assert.DoesNotContain(path, result.RewrittenFiles);
        Assert.Contains(result.Failures, f => f.StartsWith(path, StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(path));
    }

    /// <summary>A rename would replace a read-only file on macOS and Linux, where an in-place write was refused.</summary>
    [Fact]
    public void ReferenceRewrite_RefusesAReadOnlyDocument()
    {
        string oldCell = Cell("oldCell");
        string path = Referrer(Cell("cellA"), ".csch");
        string before = File.ReadAllText(path);
        new FileInfo(path).IsReadOnly = true;
        try
        {
            var rewritten = CellUsageScanner.RewriteCellReferences(_ws, oldCell, "newCell", out var failed);

            Assert.Empty(rewritten);
            Assert.Contains(failed, f => f.StartsWith(path, StringComparison.Ordinal));
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { new FileInfo(path).IsReadOnly = false; }
    }

    /// <summary>A save of each view a cell holds goes through the temp file too.</summary>
    [Theory]
    [InlineData(".csch")]
    [InlineData(".clay")]
    [InlineData(".csym")]
    [InlineData(".c3d")]
    public void SaveThatCannotWrite_LeavesTheDocumentWhole(string extension)
    {
        string path = Path.Combine(_ws, "doc" + extension);
        File.WriteAllText(path, "the old text");
        BlockTemp(path);

        Action save = extension switch
        {
            ".csch" => () => SchematicPersistence.SaveToFile(path, new SchematicEditModel()),
            ".clay" => () => LayoutPersistence.SaveToFile(path, new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 }),
            ".csym" => () => SymbolPersistence.SaveToFile(path, new Symbol([], [], 10)),
            _ => () => C3dPersistence.SaveToFile(path, new C3dDocument { Objects = [] }),
        };

        Assert.ThrowsAny<Exception>(save);
        Assert.Equal("the old text", File.ReadAllText(path));
    }

    /// <summary>Copying a cell and choosing a version in a clash have no temp-file test of their own, so they are held here.</summary>
    [Theory]
    [InlineData("src/Ui/Schematic/CellUsageScanner.cs")]
    [InlineData("src/Ui/Schematic/WorkspaceMove.cs")]
    [InlineData("src/Design/Workspace/CrossWorkspaceCellCopy.cs")]
    [InlineData("src/Design/Revision/DocumentClash.cs")]
    public void DocumentRewriters_HaveNoInPlaceWrite(string relativePath)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "circuitRF.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        Assert.DoesNotContain("File.WriteAll", File.ReadAllText(Path.Combine(root!, relativePath)));
    }
}
