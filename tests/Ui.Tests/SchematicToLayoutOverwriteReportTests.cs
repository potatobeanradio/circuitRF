using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §2.2/R-L5-9/10/11, gate 13: a re-run's overwrite
/// report distinguishes "the schematic changed" (informational) from "the layout was edited, and is
/// about to be discarded" (warning) using the stored <c>SchematicPCellSnapshots</c> — exactly the
/// three-row table R-L5-11 specifies.
/// </summary>
public sealed class SchematicToLayoutOverwriteReportTests : IDisposable
{
    private readonly string _root;

    public SchematicToLayoutOverwriteReportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-s2l-overwrite-test-" + Guid.NewGuid().ToString("N"));
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

    private static EditableComponent MakeMlin(string instanceName, double wMm) =>
        new EditableComponentBuilder(instanceName, wMm).Build();

    // Small local builder so each test reads as "W = <value>" without repeating the DefaultParameters loop.
    private sealed class EditableComponentBuilder(string instanceName, double wMm)
    {
        public EditableComponent Build()
        {
            var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
            foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
                comp.Parameters.Add(new EditableParameter
                {
                    Name = dp.Name,
                    Expression = dp.Name == "W" ? wMm.ToString() : dp.Expression,
                    Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
                });
            return comp;
        }
    }

    /// <summary>
    /// brief-sdl-4 R-sdl4-3 (D-conflict). This test used to assert R-L5-11 — a layout edit OVERWRITTEN by the
    /// schematic's value, with a warning. SDL-4 replaced that rule: a parameter changed in the layout is the board's
    /// decision and is kept. When the schematic changed the same parameter too, the layout's value is still kept and the
    /// run says so as a warning naming both values; the method name is kept so the gate filter still finds it.
    /// </summary>
    [Fact]
    public void LayoutEditOverwritten_ReportsFromLayoutValueToSchematicValue_AtWarning_Gate13()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("MLIN3", wMm: 10)); // schematic says W = 10 mm

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        r1.Command!.Execute();

        // A layout-side parameter edit: repoint the instance's CellRef at a cell generated for W = 20 mm (the exact
        // mechanism EditInstancePCellParameters uses).
        var origin20 = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir).View!.PCellOrigin!;
        var params20 = new Dictionary<string, PCellValue>(origin20.Parameters) { ["W"] = 20 * 1e-3 }; // 20 mm in SI metres
        string cell20 = CircuitRF.Design.Layout.PCells.GeneratedCellStore.GetOrCreate(
            _root, "MLIN", params20, null, null, CircuitRF.Design.Layout.PCells.PCellLayerSelection.Default);
        target.Instances[0].CellRef = Path.GetRelativePath(layoutDir, cell20);

        // …and the schematic changes the same parameter, 10 mm to 12 mm.
        model.Components[0].Parameters.First(p => p.Name == "W").Expression = "12";

        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        r2.Command?.Execute();

        var wLine = r2.Lines.Single(l => l.Text.Contains("W was changed in both"));
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Warning, wLine.Severity);
        Assert.Contains("20 mm is kept", wLine.Text);
        Assert.Contains("from 10 mm to 12 mm", wLine.Text);

        var res = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir);
        Assert.Equal(20 * 1e-3, res.View!.PCellOrigin!.Parameters.Real("W"), 6);   // the layout's value, kept

        // The next run reads the pair as "the layout changed it": Info, every run, the layout's value still kept.
        var r3 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        var kept = r3.Lines.Single(l => l.Text.Contains("W is 20 mm in the layout"));
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Info, kept.Severity);
        Assert.Contains("the schematic says 12 mm", kept.Text);
    }

    [Fact]
    public void SchematicOnlyChange_ReportsInformational_Gate13()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp2");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("MLIN1", wMm: 2.9));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        r1.Command!.Execute();

        // Change ONLY the schematic — no layout-side edit.
        model.Components[0].Parameters.First(p => p.Name == "W").Expression = "5.0";

        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        Assert.NotNull(r2.Command);

        var wLine = r2.Lines.First(l => l.Text.Contains("W changed"));
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Info, wLine.Severity);
    }
}
