using System.Linq;
using System.Text.Json;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

/// <summary>End-to-end orchestrator gates from brief-L4c-gerber-export.md: real cell folders through
/// <see cref="GerberExport.Analyze"/>/<see cref="GerberExport.Write"/>, one file per layer plus
/// Excellon plus .gbrjob.</summary>
[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public class GerberExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gerber-export-test-").FullName;

    public GerberExportTests() => LayoutTextOutline.TestOverrideTypeface = SkiaSharp.SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        Directory.Delete(_dir, recursive: true);
    }

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        populate(view);
        var layoutPath = Path.Combine(layoutDir, $"{name}.clay");
        LayoutPersistence.SaveToFile(layoutPath, view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    private static Technology TechWithOneCopperLayer(LayerKey key) => new()
    {
        Name = "Test",
        Layers =
        [
            new LayerDef
            {
                Key = key, Name = "Top Copper", Color = new Rgba(0xC8, 0x7A, 0x3E),
                Interchange = new InterchangeMapping(null, null, null, "GTL", "Copper,L1,Top"),
            },
        ],
    };

    // ── Gate 11: silent clean export ──────────────────────────────────────────────────────────────

    [Fact]
    public void PlainRectangle_DefaultResolution_HasNothingToReport()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, null, 1000, view, null);

        Assert.True(plan.CanWrite);
        Assert.True(plan.HasNothingToReport);
    }

    [Fact]
    public void CurvedShape_HasSomethingToReport_IsFalse_WhenCubicPresent()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 300, C1Y = 200, C2X = 700, C2Y = 200 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, null, 1000, view, null);

        Assert.False(plan.HasNothingToReport);
        Assert.True(plan.CubicEdgesFlattened > 0);
    }

    // ── Gate 8: labels become geometry; port labels omitted ──────────────────────────────────────

    [Fact]
    public void Analyze_ConvertsNonPortLabel_ToPolygonGeometry_OmitsPortLabel()
    {
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new LabelShape { Layer = new LayerKey(5, 0), Text = "R1", Height = 5000, X = 0, Y = 0, IsPort = false });
            v.Shapes.Add(new LabelShape { Layer = new LayerKey(5, 0), Text = "port", Height = 5000, X = 100, Y = 100, IsPort = true });
        });
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, null, 1000, view, null);

        Assert.Equal(1, plan.LabelsConvertedToGeometry);
        Assert.Equal(1, plan.PortLabelsOmitted);
        Assert.DoesNotContain(plan.Shapes, s => s is LabelShape);
        Assert.Contains(plan.Shapes, s => s is PolygonShape);
    }

    // ── Gate 9: Excellon — a Via produces both a copper flash and a drill hit ────────────────────

    [Fact]
    public void Write_ViaShape_ProducesCopperFlashAndDrillHit()
    {
        var key = new LayerKey(1, 0);
        var tech = TechWithOneCopperLayer(key);
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new ViaShape { Layer = key, X = 100_000, Y = 200_000, PadSize = 400_000, DrillSize = 200_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, 1000, view, null);
        Assert.True(plan.CanWrite);

        var outDir = Path.Combine(_dir, "out1");
        var result = GerberExport.Write(outDir, "TOP", plan);

        var gerberFile = result.FilesWritten.Single(f => f.EndsWith(".GTL", StringComparison.Ordinal));
        var gerberText = File.ReadAllText(gerberFile);
        Assert.Contains("X100000Y200000D03*", gerberText); // pad flash

        var drillFile = result.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal));
        var drillText = File.ReadAllText(drillFile);
        Assert.Contains("X0.100000Y0.200000", drillText); // drill hit
        Assert.Equal(1, result.DrillToolsDefined);
        Assert.Equal(1, result.DrillHitsWritten);
    }

    // ── Gate 10: X2 FileFunction per file + .gbrjob lists the set ────────────────────────────────

    [Fact]
    public void Write_FileFunctionMatchesCtechMapping_JobFileListsEveryFile()
    {
        var key = new LayerKey(1, 0);
        var tech = TechWithOneCopperLayer(key);
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, 1000, view, null);
        var outDir = Path.Combine(_dir, "out2");
        var result = GerberExport.Write(outDir, "TOP", plan);

        var gerberFile = result.FilesWritten.Single(f => f.EndsWith(".GTL", StringComparison.Ordinal));
        Assert.Contains("%TF.FileFunction,Copper,L1,Top*%", File.ReadAllText(gerberFile));

        var jobFile = result.FilesWritten.Single(f => f.EndsWith(".gbrjob", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(File.ReadAllText(jobFile));
        var entries = doc.RootElement.GetProperty("FilesAttributes").EnumerateArray().ToList();
        Assert.Contains(entries, e => e.GetProperty("Path").GetString() == "TOP.GTL" &&
                                       e.GetProperty("FileFunction").GetString() == "Copper,L1,Top");
    }

    // ── Gate 7 (end-to-end): 5x5 array flattens; hierarchy report is non-zero ────────────────────

    [Fact]
    public void Write_FiveByFiveArray_FlattensIntoTwentyFiveFootprints_OneLayerFile()
    {
        var key = new LayerKey(1, 0);
        var tech = TechWithOneCopperLayer(key);
        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0,
            Rows = 5, Cols = 5, PitchX = 1000, PitchY = 1000,
        });

        var plan = GerberExport.Analyze(topDir, tech, 1000, topView, null);
        Assert.True(plan.CanWrite);
        Assert.Equal(1, plan.TopLevelInstancesFlattened);
        Assert.Equal(25, plan.ShapesContributedByFlatten);

        var outDir = Path.Combine(_dir, "out3");
        var result = GerberExport.Write(outDir, "TOP", plan);
        var gerberFile = result.FilesWritten.Single(f => f.EndsWith(".GTL", StringComparison.Ordinal));
        var text = File.ReadAllText(gerberFile);

        // 25 flattened rectangle footprints -> 25 region starts.
        Assert.Equal(25, Count(text, "G36*"));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
