using CircuitRF.Ui.Layout;
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

    [Fact]
    public void LayoutEditOverwritten_ReportsFromLayoutValueToSchematicValue_AtWarning_Gate13()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("MLIN3", wMm: 10)); // schematic says W = 10 mm

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        r1.Command!.Execute();

        // Simulate a layout-side parameter edit: repoint the instance's CellRef at a cell generated
        // for W = 20 mm (the exact mechanism EditInstancePCellParameters uses).
        var origin20 = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir).View!.PCellOrigin!;
        var params20 = new Dictionary<string, PCellValue>(origin20.Parameters) { ["W"] = 20 * 1e-3 }; // 20 mm in SI metres
        string cell20 = CircuitRF.Ui.Layout.PCells.GeneratedCellStore.GetOrCreate(
            _root, "MLIN", params20, null, null, CircuitRF.Ui.Layout.PCells.PCellLayerSelection.Default);
        target.Instances[0].CellRef = Path.GetRelativePath(layoutDir, cell20);

        // Re-run: schematic still says 10 mm (unchanged) — the layout's 20 mm edit is overwritten.
        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        Assert.NotNull(r2.Command);
        r2.Command!.Execute();

        var wLine = r2.Lines.First(l => l.Text.Contains("W changed"));
        Assert.Equal(SchematicToLayoutGenerator.ReportSeverity.Warning, wLine.Severity);
        Assert.Contains("20 mm", wLine.Text);
        Assert.Contains("10 mm", wLine.Text);
        // "from 20 mm ... to 10 mm" — the LAYOUT's current (about-to-be-discarded) value comes first.
        Assert.True(wLine.Text.IndexOf("20 mm", StringComparison.Ordinal) < wLine.Text.IndexOf("to 10 mm", StringComparison.Ordinal));

        var res = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir);
        Assert.Equal(10 * 1e-3, res.View!.PCellOrigin!.Parameters.Real("W"), 6);
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
        Assert.Equal(SchematicToLayoutGenerator.ReportSeverity.Info, wLine.Severity);
    }
}
