using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report, 2026-09-17: running <b>Update Layout from Schematic</b> on the Klopfenstein Taper
/// example left the <c>.clay</c> with two tapers, one exactly over the other. That example's layout
/// is hand-drawn artwork — which an EM example has to ship, since a generated cell is rebuilt by the
/// GUI on open and a clone has none on disk — and drawn metal carries no <c>SchematicId</c>, so the
/// generator has nothing to match and places the component as new.
///
/// <para>The placement is not prevented (nothing can tell drawn metal that IS this component from
/// drawn metal that merely sits where it was put), so it is REPORTED — and reported on the mutual
/// coverage rule, which is the half that keeps the line from becoming noise on every part placed
/// over a pour. See <c>SchematicToLayoutGenerator.ReportPlacementOntoDrawnArtwork</c>.</para>
/// </summary>
public sealed class SchematicToLayoutDrawnArtworkOverlapTests : IDisposable
{
    private readonly string _root;

    public SchematicToLayoutDrawnArtworkOverlapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-s2l-overlap-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static EditableComponent Mlin(string instanceName)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    /// <summary>The cell, the schematic, and where this component's artwork lands when the layout is
    /// empty — which is what the "already drawn" shape has to be drawn over to reproduce the report.</summary>
    private (SchematicEditModel Model, string SchematicDir, string LayoutDir, Bbox Footprint, LayerKey Layer) Setup()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Amp");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir    = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(Mlin("TL1"));

        var probe = new LayoutView();
        var run = SchematicToLayoutGenerator.Run(model, probe, schematicDir, _root, layoutDir, null, null, null);
        run.Command!.Execute();

        var inst = Assert.Single(probe.Instances);
        var view = CellLayoutResolver.Resolve(inst.CellRef!, layoutDir).View!;
        var layer = view.Shapes.First(s => s is not LabelShape).Layer;

        return (model, schematicDir, layoutDir, CellHierarchy.InstanceBbox(inst, layoutDir), layer);
    }

    private static RectShape Rect(LayerKey layer, Bbox bb) =>
        new() { Layer = layer, X1 = bb.MinX, Y1 = bb.MinY, X2 = bb.MaxX, Y2 = bb.MaxY };

    [Fact]
    public void PlacedOnTopOfTheSameArtworkDrawnByHand_IsReportedAsAWarning()
    {
        var (model, schematicDir, layoutDir, footprint, layer) = Setup();

        var target = new LayoutView();
        target.Shapes.Add(Rect(layer, footprint));      // the component, already drawn

        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        Assert.Equal(1, result.AddedCount);             // still placed — reported, never refused
        Assert.Contains(result.Lines, l => l.Severity == SchematicToLayoutGenerator.ReportSeverity.Warning
                                        && l.Text.Contains("already drawn in this layout", StringComparison.Ordinal));
    }

    [Fact]
    public void PlacedOverAPourItIsMerelyInside_IsNotReported()
    {
        var (model, schematicDir, layoutDir, footprint, layer) = Setup();

        long w = footprint.MaxX - footprint.MinX, h = footprint.MaxY - footprint.MinY;
        var pour = new Bbox(footprint.MinX - 5 * w, footprint.MinY - 5 * h,
                            footprint.MaxX + 5 * w, footprint.MaxY + 5 * h);

        var target = new LayoutView();
        target.Shapes.Add(Rect(layer, pour));

        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        Assert.Equal(1, result.AddedCount);
        Assert.DoesNotContain(result.Lines, l => l.Text.Contains("already drawn in this layout", StringComparison.Ordinal));
    }
}
