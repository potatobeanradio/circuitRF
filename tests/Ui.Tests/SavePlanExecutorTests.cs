using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer-2 gate tests: SavePlanExecutor.ExecuteFileOps creates the correct on-disk
/// structure and transitions scratch documents to materialized.
/// All tests are framework-free (no Avalonia).
/// </summary>
public class SavePlanExecutorTests
{
    private static SchematicDocument MakeScratch(string title)
    {
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model);
        return new SchematicDocument(title, vm);
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "SavePlanExecTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── (a) No workspace + 1 scratch → creates all files, doc materializes ───

    [Fact]
    public void NoWorkspace_OneScratch_CreatesAllFilesAndMaterializesDoc()
    {
        var parentDir = MakeTempDir();
        var doc       = MakeScratch("Untitled-Schematic-1");

        Assert.True(doc.IsScratch);
        Assert.False(doc.IsDirty); // scratch starts clean — dirty only after first edit

        var builder = new SavePlanBuilder(null, parentDir, new[] { doc });
        var plan    = builder.Build();
        var written = SavePlanExecutor.ExecuteFileOps(plan, existingWorkspaceDir: null);

        // Workspace folder + .cws
        var wsDir   = Path.Combine(parentDir, plan.WorkspaceStep!.Name);
        var cwsPath = Path.Combine(wsDir, ".cws");
        Assert.True(File.Exists(cwsPath), $".cws should exist at {cwsPath}");

        // Cell folder + .ccell
        var cellDir   = Path.Combine(wsDir, "Untitled-Schematic-1");
        var ccellPath = Path.Combine(cellDir, ".ccell");
        Assert.True(Directory.Exists(cellDir),  $"Cell dir should exist: {cellDir}");
        Assert.True(File.Exists(ccellPath),      $".ccell should exist: {ccellPath}");

        // Schematic file
        var cschPath = Path.Combine(cellDir, "schematic", "Untitled-Schematic-1.csch");
        Assert.True(File.Exists(cschPath), $".csch should exist: {cschPath}");

        // All three in written list
        Assert.Contains(written, p => p.EndsWith(".cws",   StringComparison.OrdinalIgnoreCase));
        Assert.Contains(written, p => p.EndsWith(".ccell",  StringComparison.OrdinalIgnoreCase));
        Assert.Contains(written, p => p.EndsWith(".csch",   StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, written.Count);

        // Scratch → materialized transition
        Assert.Equal(cschPath, doc.FilePath);
        Assert.False(doc.IsDirty,   "Doc should be clean after materialization.");
        Assert.False(doc.IsScratch, "Doc should not be scratch after materialization.");
    }

    // ── (b) PrimarySchematic set in .ccell ────────────────────────────────────

    [Fact]
    public void ExecuteFileOps_SetsPrimarySchematicInCcell()
    {
        var parentDir = MakeTempDir();
        var doc       = MakeScratch("MyLNA");
        var builder   = new SavePlanBuilder(null, parentDir, new[] { doc });
        var plan      = builder.Build();
        SavePlanExecutor.ExecuteFileOps(plan, existingWorkspaceDir: null);

        var wsDir      = Path.Combine(parentDir, plan.WorkspaceStep!.Name);
        var ccellPath  = Path.Combine(wsDir, "MyLNA", ".ccell");
        var ccell      = CellPersistence.LoadFromFile(ccellPath);

        Assert.Equal("MyLNA.csch", ccell.PrimarySchematic);
    }

    // ── (c) Existing workspace → no .cws created ────────────────────────────

    [Fact]
    public void ExistingWorkspace_NoWorkspaceStep_NoCwsCreated()
    {
        var wsDir       = MakeTempDir();
        var cwsPath     = Path.Combine(wsDir, ".cws");
        WorkspacePersistence.SaveToFile(cwsPath, new CwsFile());

        var doc     = MakeScratch("Amp");
        var builder = new SavePlanBuilder(cwsPath, wsDir, new[] { doc });
        var plan    = builder.Build();

        Assert.Null(plan.WorkspaceStep);

        var writtenBefore = File.GetLastWriteTimeUtc(cwsPath);
        var written       = SavePlanExecutor.ExecuteFileOps(plan, existingWorkspaceDir: wsDir);
        var writtenAfter  = File.GetLastWriteTimeUtc(cwsPath);

        // .cws must NOT have been re-written by the executor.
        Assert.Equal(writtenBefore, writtenAfter);

        // Cell and schematic files created.
        Assert.Contains(written, p => p.EndsWith(".ccell", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(written, p => p.EndsWith(".csch",  StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, written.Count);
    }

    // ── (d) AllInOneCell — two schematics in one cell ─────────────────────────

    [Fact]
    public void AllInOneCell_TwoScratch_BothSchemasInOneCell()
    {
        var parentDir = MakeTempDir();
        var doc1      = MakeScratch("Untitled-Schematic-1");
        var doc2      = MakeScratch("Untitled-Schematic-2");
        var builder   = new SavePlanBuilder(null, parentDir, new[] { doc1, doc2 });
        var plan      = builder.Build(SaveMode.AllInOneCell);
        var written   = SavePlanExecutor.ExecuteFileOps(plan, existingWorkspaceDir: null);

        var wsDir    = Path.Combine(parentDir, plan.WorkspaceStep!.Name);
        var cellDir  = Path.Combine(wsDir, "Untitled-Schematic-1");
        var cschDir  = Path.Combine(cellDir, "schematic");

        Assert.True(File.Exists(Path.Combine(cschDir, "Untitled-Schematic-1.csch")));
        Assert.True(File.Exists(Path.Combine(cschDir, "Untitled-Schematic-2.csch")));

        // PrimarySchematic is the first one.
        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, ".ccell"));
        Assert.Equal("Untitled-Schematic-1.csch", ccell.PrimarySchematic);

        // Both docs materialized.
        Assert.False(doc1.IsScratch);
        Assert.False(doc2.IsScratch);
        Assert.False(doc1.IsDirty);
        Assert.False(doc2.IsDirty);
    }

    // ── (e) Re-saving a materialized doc uses Materialize idempotently ────────

    [Fact]
    public void Materialize_CalledTwiceWithSamePath_DocRemainsClean()
    {
        var doc = MakeScratch("Foo");
        var fakePath = Path.Combine(Path.GetTempPath(), "foo.csch");

        doc.Materialize(fakePath);
        Assert.Equal(fakePath, doc.FilePath);
        Assert.False(doc.IsDirty);

        // Calling Materialize again with the same path is idempotent.
        doc.Materialize(fakePath);
        Assert.Equal(fakePath, doc.FilePath);
        Assert.False(doc.IsDirty);
    }
}
