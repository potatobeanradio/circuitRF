using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

/// <summary>Gates 7, 9, 10, 11 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): Gerber's
/// unpaired-drill-circle report, Excellon's shared tool table, and §4.3's GDSII/DXF via export
/// (barrel on <see cref="ViaShape.Layer"/>, pad on <see cref="ViaShape.LandingLayer"/>, skip+report
/// for an unmapped/missing layer, and R-via-10's fabrication note).</summary>
public class ViaInterchangeExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("via-interchange-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    private static readonly LayerKey DrillKey = new(1, 0);
    private static readonly LayerKey CopperKey = new(2, 0);

    private static Technology TechWithDrillLayer() => new()
    {
        Name = "T",
        Layers =
        [
            new LayerDef { Key = DrillKey, Name = "Drill", Color = new Rgba(0, 0, 0), Interchange = new InterchangeMapping(null, null, "DRILL", "TXT", "Drill,PTH") },
            new LayerDef { Key = CopperKey, Name = "Copper", Color = new Rgba(0xC0, 0x80, 0x20), Interchange = new InterchangeMapping(null, null, "COPPER", "GTL", "Copper,L1,Top") },
        ],
        Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [DrillKey] }] },
    };

    // ── Gate 7: Gerber — a bare Circle on a drill layer still drills, and is reported ─────────────

    [Fact]
    public void Gerber_BareCircleOnDrillLayer_EmitsDrillHit_AndIsReported()
    {
        var tech = TechWithDrillLayer();
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new CircleShape { Layer = DrillKey, Cx = 100_000, Cy = 200_000, R = 75_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, 1000, view, null);
        Assert.Equal(1, plan.UnpairedDrillCircles);
        Assert.False(plan.HasNothingToReport);

        var result = GerberExport.Write(Path.Combine(_dir, "out"), "TOP", plan);
        var drillFile = result.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal));
        Assert.Contains("X0.100000Y0.200000", File.ReadAllText(drillFile));
        Assert.Equal(1, result.DrillHitsWritten);
    }

    [Fact]
    public void Gerber_NoCirclesOnDrillLayer_ReportAbsent()
    {
        var tech = TechWithDrillLayer();
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new RectShape { Layer = CopperKey, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, 1000, view, null);
        Assert.Equal(0, plan.UnpairedDrillCircles);
        Assert.True(plan.HasNothingToReport);
    }

    // ── Gate 9: Excellon shares one tool table across Via hits and unpaired-circle hits ────────────

    [Fact]
    public void Excellon_ViaAndUnpairedCircle_SameDiameter_ShareOneTool()
    {
        var tech = TechWithDrillLayer();
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new ViaShape { Layer = DrillKey, LandingLayer = CopperKey, X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 });
            v.Shapes.Add(new CircleShape { Layer = DrillKey, Cx = 1_000_000, Cy = 0, R = 150_000 }); // diameter 300_000 — same tool
        });
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, 1000, view, null);
        var result = GerberExport.Write(Path.Combine(_dir, "out"), "TOP", plan);

        Assert.Equal(1, result.DrillToolsDefined); // deduped: both hits are 300,000 DBU diameter
        Assert.Equal(2, result.DrillHitsWritten);
    }

    // ── Gate 10: GDSII — barrel on Layer, pad on LandingLayer ───────────────────────────────────────

    [Fact]
    public void Gdsii_Via_EmitsBarrelOnLayer_AndPadOnLandingLayer()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new ViaShape { Layer = DrillKey, LandingLayer = CopperKey, X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.True(plan.CanWrite);
        Assert.Equal(0, plan.ViaPadsSkipped);

        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, plan);

        using var stream = File.OpenRead(outPath);
        var reader = GdsiiReader.Open(stream);
        var top = reader.ReadStructures().Single(s => s.Name == "TOP");

        // Two flattened-circle boundaries: barrel on DrillKey (radius 150_000), pad on CopperKey (radius 250_000).
        Assert.Equal(2, top.Shapes.Count);
        Assert.Contains(top.Shapes, s => s.Layer == DrillKey);
        Assert.Contains(top.Shapes, s => s.Layer == CopperKey);
    }

    [Fact]
    public void Gdsii_Via_NoLandingLayer_ExportsBarrelOnly_PadSkippedAndReported()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new ViaShape { Layer = DrillKey, LandingLayer = null, X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.Equal(1, plan.ViaPadsSkipped);
        Assert.False(plan.HasNothingToReport);

        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, plan);
        using var stream = File.OpenRead(outPath);
        var reader = GdsiiReader.Open(stream);
        var top = reader.ReadStructures().Single(s => s.Name == "TOP");
        var shape = Assert.Single(top.Shapes);
        Assert.Equal(DrillKey, shape.Layer); // barrel only
    }

    [Fact]
    public void Gdsii_NoVias_HasNothingToReport_True()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new RectShape { Layer = CopperKey, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.False(plan.HasVias);
        Assert.True(plan.HasNothingToReport);
    }

    // ── Gate 10: DXF — exact CIRCLE per part, unmapped layer skipped+reported ───────────────────────

    [Fact]
    public void Dxf_Via_EmitsExactCircle_BarrelAndPad_OnMappedLayers()
    {
        var tech = TechWithDrillLayer();
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new ViaShape { Layer = DrillKey, LandingLayer = CopperKey, X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = DxfExport.Analyze(cellDir, tech, 1000, view);
        var options = new DxfExportOptions(false, false, DxfViewMode.FitToExtents, new LayoutViewport(0, 0, 1, 100, 100), 1.0);
        var summary = DxfExport.Preview(plan, options);

        Assert.Equal(0, summary.ViaPartsSkipped);
        Assert.True(plan.HasVias);
    }

    [Fact]
    public void Dxf_Via_UnmappedLandingLayer_SkippedAndReported()
    {
        var tech = TechWithDrillLayer(); // only DrillKey/CopperKey are known
        var unknownCopper = new LayerKey(99, 0);
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new ViaShape { Layer = DrillKey, LandingLayer = unknownCopper, X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = DxfExport.Analyze(cellDir, tech, 1000, view);
        var options = new DxfExportOptions(false, false, DxfViewMode.FitToExtents, new LayoutViewport(0, 0, 1, 100, 100), 1.0);
        var summary = DxfExport.Preview(plan, options);

        Assert.Equal(1, summary.ViaPartsSkipped); // pad's layer isn't known to this technology
        Assert.Contains(summary.Diagnostics, d => d.Contains("99", StringComparison.Ordinal));
    }

    [Fact]
    public void Dxf_NoVias_HasVias_False()
    {
        var tech = TechWithDrillLayer();
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new RectShape { Layer = CopperKey, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = DxfExport.Analyze(cellDir, tech, 1000, view);
        Assert.False(plan.HasVias);
    }
}
