using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Smoke coverage for docs/sonnet-briefs/brief-L5-schematic-to-layout.md §2/§9's core generator —
/// placement, PCell-cell creation/reuse, idempotent re-run preserving manual arrangement (gate 7),
/// and silence-when-nothing-changed (gate 15's "no message" half, R-L5-14).
/// </summary>
public class SchematicToLayoutGeneratorTests : IDisposable
{
    private readonly string _root;

    public SchematicToLayoutGeneratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-s2l-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private (string SchematicDir, string LayoutDir, Technology Tech) MakeCell(string cellName)
    {
        var tech = StarterTechnologies.MmicGaAs();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });

        string cellDir = Path.Combine(_root, cellName);
        CellFolder.CreateCellFolder(_root, cellName);
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout) is { } _ // ensure folder exists
            ? CellFolder.SubFolderPath(cellDir, ViewType.Schematic)
            : throw new InvalidOperationException();
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        return (schematicDir, layoutDir, tech);
    }

    private static EditableComponent MakeMlin(string instanceName, double wMm = 2.9, double lMm = 10)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
        {
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "W" ? wMm.ToString() : dp.Name == "L" ? lMm.ToString() : dp.Expression,
                Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
                Dimension = dp.Dimension,
            });
        }
        return comp;
    }

    private static EditableComponent MakeTerm(string instanceName, int num)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Term, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Term, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "Num" ? num.ToString() : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Fact]
    public void FirstRun_PlacesMlinInstance_PointingAtGeneratedPCellCell()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Amp1");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1"));
        model.Components.Add(MakeTerm("T1", 1));
        model.Components.Add(MakeTerm("T2", 2));

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(
            model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", cellResolver: null);

        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.Single(target.Instances);
        var inst = target.Instances[0];
        Assert.Equal("ML1", inst.SchematicId);

        var res = CellLayoutResolver.Resolve(inst.CellRef, layoutDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.NotNull(res.View!.PCellOrigin);
        Assert.Equal("MLIN", res.View.PCellOrigin!.GeneratorId);
        Assert.Equal(1, result.AddedCount);

        // Term (no PCell, no CellRef) has no layout view — reported, not placed.
        Assert.Contains(result.NoLayoutWarnings, w => w.Contains("T1"));
        Assert.Contains(result.NoLayoutWarnings, w => w.Contains("T2"));
    }

    [Fact]
    public void SecondRun_Unchanged_ProducesNoCommand()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Amp2");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1"));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);
        r1.Command!.Execute();

        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);

        Assert.True(r2.NothingChanged);
        Assert.Null(r2.Command);
    }

    [Fact]
    public void Rerun_AfterManualMoveAndOneNewComponent_PreservesMovedPosition_AddsExactlyOne()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Amp3");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1"));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);
        r1.Command!.Execute();
        Assert.Single(target.Instances);

        // User hand-places it.
        target.Instances[0].X = 12_345_000;
        target.Instances[0].Y = 67_000;

        // Add one more component to the schematic.
        model.Components.Add(MakeMlin("ML2", wMm: 1.5));

        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);
        Assert.NotNull(r2.Command);
        r2.Command!.Execute();

        Assert.Equal(2, target.Instances.Count);
        var ml1 = target.Instances.First(i => i.SchematicId == "ML1");
        Assert.Equal(12_345_000, ml1.X);
        Assert.Equal(67_000, ml1.Y);
        Assert.Equal(1, r2.AddedCount);
        Assert.Equal(0, r2.UpdatedCount);
    }

    [Fact]
    public void SameParameters_TwoInstances_ShareOneGeneratedCell()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Amp4");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1", wMm: 2.9));
        model.Components.Add(MakeMlin("ML2", wMm: 2.9));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);
        r1.Command!.Execute();

        Assert.Equal(2, target.Instances.Count);
        var ml1 = target.Instances.First(i => i.SchematicId == "ML1");
        var ml2 = target.Instances.First(i => i.SchematicId == "ML2");

        var abs1 = Path.GetFullPath(Path.Combine(layoutDir, ml1.CellRef));
        var abs2 = Path.GetFullPath(Path.Combine(layoutDir, ml2.CellRef));
        Assert.Equal(abs1, abs2, ignoreCase: true);
    }

    [Fact]
    public void DifferentParameters_TwoInstances_GetDifferentGeneratedCells()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Amp5");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1", wMm: 2.9));
        model.Components.Add(MakeMlin("ML2", wMm: 5.0));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);
        r1.Command!.Execute();

        var ml1 = target.Instances.First(i => i.SchematicId == "ML1");
        var ml2 = target.Instances.First(i => i.SchematicId == "ML2");
        var abs1 = Path.GetFullPath(Path.Combine(layoutDir, ml1.CellRef));
        var abs2 = Path.GetFullPath(Path.Combine(layoutDir, ml2.CellRef));
        Assert.NotEqual(abs1, abs2, StringComparer.OrdinalIgnoreCase);
    }
}
