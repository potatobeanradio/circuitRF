using System.Text.RegularExpressions;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-0 R-sdl0-1 and R-sdl0-2: Update Layout from Schematic's planner moved to <c>src/Design</c>, and the
/// placement record it writes.
///
/// <para>The equivalence theory is the gate R-sdl0-1c asks for: for each fixture shape the existing
/// <c>SchematicToLayout*</c> tests use — a first run, a re-run after a hand move with one part added, a layout
/// edit overwritten, a rename, a rotation carried, a part placed over drawn artwork — the planner's plan applied
/// to a COPY of the layout writes the same <c>.clay</c> bytes as the GUI's command chain applied to the layout,
/// and planning leaves the layout it read byte-for-byte as it was.</para>
/// </summary>
public sealed class SchematicLayoutPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sdl0-" + Guid.NewGuid().ToString("N")[..12]);

    public SchematicLayoutPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private sealed record Fixture(SchematicEditModel Model, LayoutView Target, string SchematicDir, string LayoutDir,
                                  Technology? Tech, string? TechId);

    private Fixture Cell(string name, bool withTechnology)
    {
        Technology? tech = null;
        string? techId = null;
        if (withTechnology)
        {
            tech = StarterTechnologies.MmicGaAs();
            Directory.CreateDirectory(Path.Combine(_root, "tech"));
            TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);
            WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });
            techId = "tech/t.ctech";
        }
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        return new Fixture(new SchematicEditModel { SchematicDirectory = CellFolder.SubFolderPath(cellDir, ViewType.Schematic) },
                           new LayoutView(), CellFolder.SubFolderPath(cellDir, ViewType.Schematic),
                           CellFolder.SubFolderPath(cellDir, ViewType.Layout), tech, techId);
    }

    private static EditableComponent Mlin(string name, double wMm = 2.9, double lMm = 10)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Mlin };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "W" ? wMm.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           : dp.Name == "L" ? lMm.ToString(System.Globalization.CultureInfo.InvariantCulture) : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    private static EditableComponent Term(string name, int num)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Term, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Name == "Num" ? num.ToString() : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    private SchematicToLayoutGenerator.GenerationResult Run(Fixture f, LayoutView target) =>
        SchematicToLayoutGenerator.Run(f.Model, target, f.SchematicDir, _root, f.LayoutDir, f.Tech, f.TechId, null);

    /// <summary>The fixture as it stands just before the run the theory compares.</summary>
    private Fixture Scenario(string name)
    {
        switch (name)
        {
            case "first-run":
            {
                var f = Cell("Amp1", withTechnology: true);
                f.Model.Components.Add(Mlin("ML1"));
                f.Model.Components.Add(Mlin("ML2", wMm: 1.5));
                f.Model.Components.Add(Term("T1", 1));
                return f;
            }
            case "rerun-moved-plus-one":
            {
                var f = Cell("Amp2", withTechnology: true);
                f.Model.Components.Add(Mlin("ML1"));
                Run(f, f.Target).Command!.Execute();
                f.Target.Instances[0].X = 12_345_000;
                f.Target.Instances[0].Y = 67_000;
                f.Model.Components.Add(Mlin("ML2", wMm: 1.5));
                return f;
            }
            case "layout-edit-overwritten":
            {
                var f = Cell("Amp3", withTechnology: false);
                f.Model.Components.Add(Mlin("MLIN1", wMm: 10));
                Run(f, f.Target).Command!.Execute();
                var origin = CellLayoutResolver.Resolve(f.Target.Instances[0].CellRef, f.LayoutDir).View!.PCellOrigin!;
                var edited = new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 20e-3 };
                string dir = GeneratedCellStore.GetOrCreate(_root, "MLIN", edited, null, null, PCellLayerSelection.Default);
                f.Target.Instances[0].CellRef = Path.GetRelativePath(f.LayoutDir, dir);
                f.Model.Components.Add(Mlin("MLIN2", wMm: 5));
                return f;
            }
            case "rename":
            {
                var f = Cell("Amp4", withTechnology: false);
                f.Model.Components.Add(Mlin("ML1", wMm: 2.9));
                f.Model.Components.Add(Mlin("ML2", wMm: 5.0));
                Run(f, f.Target).Command!.Execute();
                f.Model.Components[0].InstanceName = "ML7";
                return f;
            }
            case "rotation-carried":
            {
                var f = Cell("Amp5", withTechnology: false);
                f.Model.Components.Add(Mlin("ML1"));
                Run(f, f.Target).Command!.Execute();
                f.Model.Components[0].Rotation = SymbolRotation.R90;
                return f;
            }
            case "over-drawn-artwork":
            {
                var f = Cell("Amp6", withTechnology: false);
                f.Model.Components.Add(Mlin("TL1"));
                var probe = new LayoutView();
                Run(f, probe).Command!.Execute();
                var inst = Assert.Single(probe.Instances);
                var layer = CellLayoutResolver.Resolve(inst.CellRef, f.LayoutDir).View!.Shapes.First(s => s is not LabelShape).Layer;
                var bb = CellHierarchy.InstanceBbox(inst, f.LayoutDir);
                f.Target.Shapes.Add(new RectShape { Layer = layer, X1 = bb.MinX, Y1 = bb.MinY, X2 = bb.MaxX, Y2 = bb.MaxY });
                return f;
            }
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [InlineData("first-run")]
    [InlineData("rerun-moved-plus-one")]
    [InlineData("layout-edit-overwritten")]
    [InlineData("rename")]
    [InlineData("rotation-carried")]
    [InlineData("over-drawn-artwork")]
    public void PlanAppliedToACopy_WritesTheSameClayAsTheCommandChain_AndPlanningChangesNothing(string scenario)
    {
        var f = Scenario(scenario);
        string before = LayoutPersistence.Serialize(f.Target);

        var viaCommand = LayoutPersistence.Deserialize(before);
        var result = Run(f, viaCommand);
        result.Command?.Execute();

        var planned = LayoutPersistence.Deserialize(before);
        var plan = SchematicLayoutPlanner.Plan(f.Model, planned, f.SchematicDir, _root, f.LayoutDir, f.Tech, f.TechId, null);
        Assert.Equal(before, LayoutPersistence.Serialize(planned));       // pure: the layout it read is untouched

        var copy = LayoutPersistence.Deserialize(before);
        plan.ApplyTo(copy);

        Assert.Equal(LayoutPersistence.Serialize(viaCommand), LayoutPersistence.Serialize(copy));
        Assert.Equal(result.Command is null, plan.ChangesNothing);
        Assert.Equal(result.Lines, plan.Lines);
    }

    /// <summary>R-sdl0-1d: the GUI wrapper keeps no placement arithmetic — it translates a plan into commands.</summary>
    [Fact]
    public void TheGuiWrapper_HoldsNoPlacementArithmetic()
    {
        string code = Regex.Replace(Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Layout", "SchematicToLayoutGenerator.cs")),
            @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

        Assert.Contains("SchematicLayoutPlanner.Plan(", code, StringComparison.Ordinal);
        foreach (string forbidden in new[]
                 {
                     "PlaceNewInstances", "PlaceAtArtwork", "InstanceBbox", ".Compose(", "CarryRotation", "TurnInPlace",
                     "RotationDegrees =", "MirrorX =", ".X =", ".Y =", ".X +=", ".Y +=", "CellLayoutResolver",
                     "GeneratedCellStore", "NetExtractor", "GroundArtwork.Plan(", "PinAlignment",
                 })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
    }

    /// <summary>R-sdl0-2a/b/c: an add writes a record; it persists, in key order; a layout with no record
    /// writes no key for it.</summary>
    [Fact]
    public void PlacementRecord_IsWrittenForAnAdd_AndRoundTrips()
    {
        var f = Cell("Rec", withTechnology: false);
        Assert.DoesNotContain("GeneratedPlacements", LayoutPersistence.Serialize(f.Target), StringComparison.Ordinal);

        f.Model.Components.Add(Mlin("ML2"));
        f.Model.Components.Add(Mlin("ML1", wMm: 1.5));
        Run(f, f.Target).Command!.Execute();

        Assert.Equal(["ML1", "ML2"], f.Target.GeneratedPlacements.Keys.Order(StringComparer.Ordinal));
        foreach (var inst in f.Target.Instances)
            Assert.Equal(GeneratedPlacement.Of(inst), f.Target.GeneratedPlacements[inst.SchematicId!]);

        string json = LayoutPersistence.Serialize(f.Target);
        string table = json[json.IndexOf("\"GeneratedPlacements\"", StringComparison.Ordinal)..];
        Assert.True(table.IndexOf("\"ML1\"", StringComparison.Ordinal) < table.IndexOf("\"ML2\"", StringComparison.Ordinal));
        var back = LayoutPersistence.Deserialize(json);
        Assert.Equal(f.Target.GeneratedPlacements, back.GeneratedPlacements);
        Assert.Equal(json, LayoutPersistence.Serialize(back));
    }

    /// <summary>R-sdl0-2d: one Undo of the run takes its records back with its placements — an add's record goes,
    /// and a turn's record returns to what the add wrote.</summary>
    [Fact]
    public void PlacementRecord_IsUndoneWithTheRun()
    {
        var f = Cell("Undo", withTechnology: false);
        f.Model.Components.Add(Mlin("ML1"));
        var first = Run(f, f.Target);
        first.Command!.Execute();
        var placed = f.Target.GeneratedPlacements["ML1"];

        f.Model.Components[0].Rotation = SymbolRotation.R90;
        var turn = Run(f, f.Target);
        turn.Command!.Execute();
        Assert.Equal(GeneratedPlacement.Of(f.Target.Instances[0]), f.Target.GeneratedPlacements["ML1"]);
        Assert.NotEqual(placed, f.Target.GeneratedPlacements["ML1"]);

        turn.Command.Undo();
        Assert.Equal(placed, f.Target.GeneratedPlacements["ML1"]);
        first.Command.Undo();
        Assert.Empty(f.Target.GeneratedPlacements);
    }

    /// <summary>R-sdl0-3e, the artwork side: a PCell is handed the same width for <c>w50 mm</c> as for <c>w50</c>
    /// when <c>w50</c> carries its own unit — what the elaborator does too (<c>UnitAfterUnitBearingVariableTests</c>).</summary>
    [Fact]
    public void AUnitAfterAUnitBearingVariable_GivesTheGeneratorTheSameWidth()
    {
        var scope = new CircuitRF.Core.Expressions.Scope("global");
        scope.Bind("w50", "280.152", "um");
        var evaluator = new CircuitRF.Core.Expressions.Evaluator();

        Assert.True(SchematicLayoutPlanner.TryResolveSiValue("w50", "mm", scope, evaluator, out double withUnit, out _));
        Assert.True(SchematicLayoutPlanner.TryResolveSiValue("w50", null, scope, evaluator, out double without, out _));
        Assert.Equal(280.152e-6, withUnit, 1e-15);
        Assert.Equal(without, withUnit);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
