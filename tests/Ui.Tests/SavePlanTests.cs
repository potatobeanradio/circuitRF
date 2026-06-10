using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer-1 gate tests: SavePlanBuilder computes correct plans for EachOwnCell and
/// AllInOneCell modes, workspace-present short-circuits the workspace step, and
/// name overrides are respected.  All tests are framework-free (no Avalonia).
/// </summary>
public class SavePlanTests
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
            "SavePlanTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── (a) No workspace + 2 scratch schematics, EachOwnCell ─────────────────

    [Fact]
    public void NoWorkspace_TwoScratch_EachOwnCell_PlanHasWorkspaceAndTwoCellsAndTwoSaves()
    {
        var parentDir = MakeTempDir();
        var docs = new[]
        {
            MakeScratch("Untitled-Schematic-1"),
            MakeScratch("Untitled-Schematic-2"),
        };
        var builder = new SavePlanBuilder(null, parentDir, docs);
        var plan    = builder.Build(SaveMode.EachOwnCell);

        // Workspace step present and seeded from parent dir.
        Assert.NotNull(plan.WorkspaceStep);
        Assert.StartsWith("Untitled-Workspace-", plan.WorkspaceStep.Name);
        Assert.Equal(parentDir, plan.WorkspaceStep.ParentDir);

        // Two cell steps, named from doc names, no TestBench (no analyses yet).
        Assert.Equal(2, plan.CellSteps.Count);
        Assert.Equal("Untitled-Schematic-1", plan.CellSteps[0].Name);
        Assert.Equal("Untitled-Schematic-2", plan.CellSteps[1].Name);
        Assert.False(plan.CellSteps[0].IsTestBench);
        Assert.False(plan.CellSteps[1].IsTestBench);

        // Two save steps, each in their respective cell, each primary.
        Assert.Equal(2, plan.SaveSteps.Count);
        Assert.Equal("Untitled-Schematic-1", plan.SaveSteps[0].TargetCellName);
        Assert.Equal("Untitled-Schematic-2", plan.SaveSteps[1].TargetCellName);
        Assert.Equal("Untitled-Schematic-1.csch", plan.SaveSteps[0].FileName);
        Assert.Equal("Untitled-Schematic-2.csch", plan.SaveSteps[1].FileName);
        Assert.True(plan.SaveSteps[0].IsPrimary);
        Assert.True(plan.SaveSteps[1].IsPrimary);
    }

    // ── (b) AllInOneCell → 1 cell, first primary ──────────────────────────────

    [Fact]
    public void NoWorkspace_TwoScratch_AllInOneCell_PlanHasOneCellAndTwoSaves()
    {
        var parentDir = MakeTempDir();
        var docs = new[]
        {
            MakeScratch("Untitled-Schematic-1"),
            MakeScratch("Untitled-Schematic-2"),
        };
        var builder = new SavePlanBuilder(null, parentDir, docs);
        var plan    = builder.Build(SaveMode.AllInOneCell);

        Assert.NotNull(plan.WorkspaceStep);

        // Exactly one cell step, seeded from first doc name.
        Assert.Single(plan.CellSteps);
        Assert.Equal("Untitled-Schematic-1", plan.CellSteps[0].Name);
        Assert.False(plan.CellSteps[0].IsTestBench);

        // Two save steps, both targeting the same cell.
        Assert.Equal(2, plan.SaveSteps.Count);
        Assert.Equal("Untitled-Schematic-1", plan.SaveSteps[0].TargetCellName);
        Assert.Equal("Untitled-Schematic-1", plan.SaveSteps[1].TargetCellName);

        // First is primary; second is not.
        Assert.True(plan.SaveSteps[0].IsPrimary);
        Assert.False(plan.SaveSteps[1].IsPrimary);

        // First uses shared cell name as filename; second uses its own doc name.
        Assert.Equal("Untitled-Schematic-1.csch", plan.SaveSteps[0].FileName);
        Assert.Equal("Untitled-Schematic-2.csch", plan.SaveSteps[1].FileName);
    }

    // ── (c) Workspace already loaded → no workspace step ─────────────────────

    [Fact]
    public void WorkspaceLoaded_NoWorkspaceStep()
    {
        var dir     = MakeTempDir();
        var cwsPath = Path.Combine(dir, ".cws");
        File.WriteAllText(cwsPath, "{}");
        var docs    = new[] { MakeScratch("Untitled-Schematic-1") };
        var builder = new SavePlanBuilder(cwsPath, dir, docs);
        var plan    = builder.Build();

        Assert.Null(plan.WorkspaceStep);
        Assert.Single(plan.CellSteps);
        Assert.Single(plan.SaveSteps);
    }

    // ── (d) AllInOneCell with custom shared name ──────────────────────────────

    [Fact]
    public void AllInOneCell_WithCustomName_UsesProvidedName()
    {
        var parentDir = MakeTempDir();
        var docs = new[]
        {
            MakeScratch("Untitled-Schematic-1"),
            MakeScratch("Untitled-Schematic-2"),
        };
        var builder = new SavePlanBuilder(null, parentDir, docs);
        var plan    = builder.Build(SaveMode.AllInOneCell, allInOneCellName: "MyCell");

        Assert.Equal("MyCell", plan.CellSteps[0].Name);
        Assert.Equal("MyCell", plan.SaveSteps[0].TargetCellName);
        Assert.Equal("MyCell.csch", plan.SaveSteps[0].FileName);
        // Second schematic still uses its own doc name for the filename.
        Assert.Equal("Untitled-Schematic-2.csch", plan.SaveSteps[1].FileName);
    }

    // ── (e) EachOwnCell with cell name override ───────────────────────────────

    [Fact]
    public void EachOwnCell_WithCellNameOverride_RespectsOverride()
    {
        var parentDir = MakeTempDir();
        var docs = new[]
        {
            MakeScratch("Untitled-Schematic-1"),
            MakeScratch("Untitled-Schematic-2"),
        };
        var overrides = new Dictionary<string, string>
        {
            ["Untitled-Schematic-1"] = "LNA",
        };
        var builder = new SavePlanBuilder(null, parentDir, docs);
        var plan    = builder.Build(SaveMode.EachOwnCell, cellNameOverrides: overrides);

        Assert.Equal("LNA", plan.CellSteps[0].Name);
        Assert.Equal("LNA", plan.SaveSteps[0].TargetCellName);
        Assert.Equal("LNA.csch", plan.SaveSteps[0].FileName);
        Assert.Equal("Untitled-Schematic-2", plan.CellSteps[1].Name);
    }

    // ── (f) Default workspace name increments when folder exists ─────────────

    [Fact]
    public void DefaultWorkspaceName_IncrementsWhenFolderExists()
    {
        var parentDir = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(parentDir, "Untitled-Workspace-1"));

        var builder = new SavePlanBuilder(null, parentDir, Array.Empty<SchematicDocument>());
        var name    = builder.DefaultWorkspaceName();

        Assert.Equal("Untitled-Workspace-2", name);
    }

    // ── (g) Workspace step name comes from override ───────────────────────────

    [Fact]
    public void WorkspaceNameOverride_IsUsedInPlan()
    {
        var parentDir = MakeTempDir();
        var builder   = new SavePlanBuilder(null, parentDir, Array.Empty<SchematicDocument>());
        var plan      = builder.Build(workspaceNameOverride: "MyWorkspace");

        Assert.NotNull(plan.WorkspaceStep);
        Assert.Equal("MyWorkspace", plan.WorkspaceStep.Name);
    }
}
