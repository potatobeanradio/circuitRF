using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the Project Tree UX brief (Items 5, 6, 7).
/// Tests the file-system-level behavior of duplicate/rename logic and FileInfoInspectorViewModel
/// without requiring a running UI.
/// </summary>
public class ProjectTreeUxTests : IDisposable
{
    private readonly string _ws;

    public ProjectTreeUxTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), $"crf_ptux_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string CreateCell(string name) => CellFolder.CreateCellFolder(_ws, name);

    private static void AddSchematicFile(string cellDir, string fileName, bool makePrimary = false)
    {
        var subDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        var path   = Path.Combine(subDir, fileName);
        var model  = new SchematicEditModel();
        SchematicPersistence.SaveToFile(path, model);

        if (makePrimary)
        {
            var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell     = File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : new CcellFile();
            ccell.PrimarySchematic = fileName;
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
    }

    private static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // Mirrors the duplicate-cell primary rename logic in WorkspaceViewModel.DuplicateCellAsync.
    private static void DuplicateCellPrimaries(string newCellDir, string newCellName)
    {
        var ccellPath = Path.Combine(newCellDir, CellFolder.CcellFileName);
        var ccell     = File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : new CcellFile();
        bool ccellDirty = false;

        foreach (var viewType in new[] { ViewType.Schematic, ViewType.Symbol })
        {
            var res = CellFolder.ResolvePrimary(newCellDir, viewType);
            if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent)) continue;
            if (res.ResolvedName is null) continue;

            var subDir     = CellFolder.SubFolderPath(newCellDir, viewType);
            var ext        = CellFolder.ViewExtension(viewType);
            var targetName = newCellName + ext;
            var targetPath = Path.Combine(subDir, targetName);
            var srcPath    = Path.Combine(subDir, res.ResolvedName);

            // Skip if a different non-primary file already has the target name.
            if (File.Exists(targetPath)
                && !string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
                File.Move(srcPath, targetPath);

            if (viewType == ViewType.Schematic) { ccell.PrimarySchematic = targetName; ccellDirty = true; }
            else if (viewType == ViewType.Symbol) { ccell.PrimarySymbol = targetName; ccellDirty = true; }
        }

        if (ccellDirty) CellPersistence.SaveToFile(ccellPath, ccell);
    }

    // ── Item 6 tests ──────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_SoleFilePrimary_RenamedToNewCellName()
    {
        // foo has sole schematic foo.csch (SoleFile branch) → duplicate to bar → bar.csch + .ccell.PrimarySchematic = "bar.csch"
        var fooDir = CreateCell("foo");
        AddSchematicFile(fooDir, "foo.csch");    // sole file — no ccell primary entry needed

        var barDir = Path.Combine(_ws, "bar");
        CopyDirectoryRecursive(fooDir, barDir);
        DuplicateCellPrimaries(barDir, "bar");

        var barSchDir = CellFolder.SubFolderPath(barDir, ViewType.Schematic);
        Assert.True(File.Exists(Path.Combine(barSchDir, "bar.csch")), "bar.csch should exist");
        Assert.False(File.Exists(Path.Combine(barSchDir, "foo.csch")), "foo.csch should be renamed away");

        var ccell = CellPersistence.LoadFromFile(Path.Combine(barDir, CellFolder.CcellFileName));
        Assert.Equal("bar.csch", ccell.PrimarySchematic);
    }

    [Fact]
    public void Duplicate_NonPrimaryFilesKeepNames()
    {
        // foo has foo.csch (primary via .ccell) and extra.csch (non-primary)
        var fooDir = CreateCell("foo");
        AddSchematicFile(fooDir, "foo.csch", makePrimary: true);
        AddSchematicFile(fooDir, "extra.csch");

        var barDir = Path.Combine(_ws, "bar");
        CopyDirectoryRecursive(fooDir, barDir);
        DuplicateCellPrimaries(barDir, "bar");

        var barSchDir = CellFolder.SubFolderPath(barDir, ViewType.Schematic);
        Assert.True(File.Exists(Path.Combine(barSchDir, "bar.csch")),   "primary renamed to bar.csch");
        Assert.True(File.Exists(Path.Combine(barSchDir, "extra.csch")), "extra.csch kept");
        Assert.False(File.Exists(Path.Combine(barSchDir, "foo.csch")),  "foo.csch should be gone");
    }

    [Fact]
    public void Duplicate_PrimarySkippedWhenTargetNameCollidesWithNonPrimary()
    {
        // foo has foo.csch (primary) and extra.csch; the copy bar gets bar.csch pre-existing as a non-primary
        var fooDir = CreateCell("foo");
        AddSchematicFile(fooDir, "foo.csch", makePrimary: true);
        AddSchematicFile(fooDir, "extra.csch");

        var barDir    = Path.Combine(_ws, "bar");
        CopyDirectoryRecursive(fooDir, barDir);

        // Simulate a pre-existing "bar.csch" (not the primary) in the copied folder.
        // We rename extra.csch → bar.csch to represent the collision.
        var barSchDir = CellFolder.SubFolderPath(barDir, ViewType.Schematic);
        File.Move(Path.Combine(barSchDir, "extra.csch"), Path.Combine(barSchDir, "bar.csch"));

        DuplicateCellPrimaries(barDir, "bar");

        // Primary rename must be skipped; foo.csch must still be the primary filename on disk.
        Assert.True(File.Exists(Path.Combine(barSchDir, "foo.csch")), "foo.csch (primary) should remain because bar.csch already existed");
        Assert.True(File.Exists(Path.Combine(barSchDir, "bar.csch")), "bar.csch (collider) should remain");

        // .ccell should NOT have been updated to "bar.csch" (the skip path doesn't write .ccell for that view).
        var ccell = CellPersistence.LoadFromFile(Path.Combine(barDir, CellFolder.CcellFileName));
        Assert.Equal("foo.csch", ccell.PrimarySchematic);
    }

    // ── Item 5 tests (FileInfoInspectorViewModel) ─────────────────────────────

    [Fact]
    public void FileInfoVm_ReadsFilename()
    {
        var filePath = Path.Combine(_ws, "sample.npy");
        File.WriteAllBytes(filePath, new byte[512]);

        var vm = new FileInfoInspectorViewModel(filePath);
        Assert.Equal("sample.npy", vm.Name);
    }

    [Fact]
    public void FileInfoVm_FormatsSizeInKb()
    {
        var filePath = Path.Combine(_ws, "data.npy");
        File.WriteAllBytes(filePath, new byte[2048]);   // 2 KB

        var vm = new FileInfoInspectorViewModel(filePath);
        Assert.Contains("KB", vm.SizeText);
    }

    [Fact]
    public void FileInfoVm_MissingFileReturnsDash()
    {
        var vm = new FileInfoInspectorViewModel(Path.Combine(_ws, "nonexistent.txt"));
        Assert.Equal("—", vm.SizeText);
        Assert.Equal("—", vm.ModifiedText);
    }
}
