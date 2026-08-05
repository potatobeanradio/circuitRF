using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §1, gates 2/3/4: a freshly placed MKlopf with
/// UNTOUCHED default parameters (Z1/Z2 in "Ω") resolves and places successfully; the alternate
/// W1/W2 and F3db entry routes resolve too, converting to the canonical Z1/Z2/L the generator
/// actually reads; a genuinely unresolvable parameter reports the real reason.
/// </summary>
public sealed class MklopfParameterResolutionTests : IDisposable
{
    private readonly string _root;

    public MklopfParameterResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mklopf-resolve-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (string SchematicDir, string LayoutDir) MakeCell(string name)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        return (CellFolder.SubFolderPath(cellDir, ViewType.Schematic), CellFolder.SubFolderPath(cellDir, ViewType.Layout));
    }

    /// <summary>Mirrors SchematicViewModel.CommitPlacement's exact seeding — Expression = dp.Expression
    /// verbatim, Unit = dp.Unit verbatim (the editor GLYPH, "Ω", not the ASCII engine spelling) — so
    /// this test exercises literally what a freshly-placed component looks like, not an idealized one.</summary>
    private static EditableComponent PlaceFresh(string instanceName, CircuitRF.Ui.Schematic.SymbolKind kind, int portCount = 0)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, portCount))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Theory]
    [InlineData(CircuitRF.Ui.Schematic.SymbolKind.Mklopf)]
    [InlineData(CircuitRF.Ui.Schematic.SymbolKind.Mlin)]
    [InlineData(CircuitRF.Ui.Schematic.SymbolKind.MBend)]
    [InlineData(CircuitRF.Ui.Schematic.SymbolKind.MTee)]
    [InlineData(CircuitRF.Ui.Schematic.SymbolKind.MCross)]
    [InlineData(CircuitRF.Ui.Schematic.SymbolKind.Mtaper)]
    public void FreshlyPlaced_UntouchedDefaults_ResolvesAndPlaces_Gate2(CircuitRF.Ui.Schematic.SymbolKind kind)
    {
        var (schematicDir, layoutDir) = MakeCell("Cell_" + kind);
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(PlaceFresh("X1", kind));

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        Assert.Empty(result.NoLayoutWarnings);
        Assert.NotNull(result.Command);
        Assert.Equal(1, result.AddedCount);
    }

    [Fact]
    public void Mklopf_WidthEntryMode_Resolves_ConvertsToCanonicalImpedances_Gate3()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var comp = PlaceFresh("MKF1", CircuitRF.Ui.Schematic.SymbolKind.Mklopf);

        // Mirror ParameterEditorViewModel.ToggleMklopfImpedanceEntry: remove Z1/Z2, add W1/W2.
        comp.Parameters.RemoveAll(p => p.Name is "Z1" or "Z2");
        comp.Parameters.Add(new EditableParameter { Name = "W1", Expression = "2.0", Unit = "mm", ShowOnSchematic = true });
        comp.Parameters.Add(new EditableParameter { Name = "W2", Expression = "1.0", Unit = "mm", ShowOnSchematic = true });
        model.Components.Add(comp);

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        Assert.Empty(result.NoLayoutWarnings);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        var origin = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir).View!.PCellOrigin!;
        Assert.True(origin.Parameters.ContainsKey("Z1"));
        Assert.True(origin.Parameters.ContainsKey("Z2"));
        Assert.False(origin.Parameters.ContainsKey("W1"));
        // Narrower width (W2 < W1) means higher impedance (Z2 > Z1) — sanity check the conversion
        // actually ran the right direction, not just "some number."
        Assert.True(origin.Parameters.Real("Z2") > origin.Parameters.Real("Z1"));
    }

    [Fact]
    public void Mklopf_F3dbEntryMode_Resolves_ConvertsToCanonicalLength_Gate3()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp2");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var comp = PlaceFresh("MKF1", CircuitRF.Ui.Schematic.SymbolKind.Mklopf);

        comp.Parameters.RemoveAll(p => p.Name == "L");
        comp.Parameters.Add(new EditableParameter { Name = "F3db", Expression = "2", Unit = "GHz", ShowOnSchematic = true });
        model.Components.Add(comp);

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        Assert.Empty(result.NoLayoutWarnings);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        var origin = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir).View!.PCellOrigin!;
        Assert.True(origin.Parameters.ContainsKey("L"));
        Assert.False(origin.Parameters.ContainsKey("F3db"));
        Assert.True(origin.Parameters.Real("L") > 0);
    }

    [Fact]
    public void GenuinelyUnresolvableParameter_ReportsRealReason_NotGenericMessage_Gate4()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp3");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var comp = PlaceFresh("ML1", CircuitRF.Ui.Schematic.SymbolKind.Mlin);
        comp.Parameters.First(p => p.Name == "W").Expression = "NotANumber + )";
        model.Components.Add(comp);

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        Assert.Single(result.NoLayoutWarnings);
        string warning = result.NoLayoutWarnings[0];
        Assert.DoesNotContain("could not be resolved\"", warning); // not the old bare-genericmessage form
        Assert.Contains("W", warning);
    }
}
