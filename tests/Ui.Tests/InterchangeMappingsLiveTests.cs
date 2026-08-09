using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner question: the Technology Editor's Interchange tab said the DXF and Gerber fields were
/// "scaffolding for later phases". They are not — every one of the four is read by a real
/// export/import path, and has been since L4b/L4c landed. What was actually missing is what happens
/// when TWO layers claim one name, which is silent and destructive rather than merely untidy.
/// </summary>
[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class InterchangeMappingsLiveTests
{
    // ── The fields are live, in both directions ───────────────────────────────

    [Fact]
    public void DxfExport_NamesALayerByItsDxfAlias_NotItsOwnLayerName()
    {
        var tech = TechWithOneLayer(new InterchangeMapping(null, null, "TOP_COPPER", null, null));
        var view = OneRectOnLayerOne();

        string dxf = WriteDxf(view, tech);

        Assert.Contains("TOP_COPPER", dxf);
        Assert.DoesNotContain("Signal", dxf);   // the layer's own Name must not win over the alias
    }

    [Fact]
    public void GerberExport_NamesTheFileByItsSuffix_AndWritesTheX2FileFunction()
    {
        var tech = TechWithOneLayer(new InterchangeMapping(null, null, null, "GTL", "Copper,L1,Top"));
        var dir  = NewTempDir();

        var plan = AnalyzeGerber(dir, tech);
        var result = GerberExport.Write(dir, "board", plan);

        string gtl = Assert.Single(result.FilesWritten, f => f.EndsWith("board.GTL", System.StringComparison.Ordinal));
        Assert.Contains("Copper,L1,Top", File.ReadAllText(gtl));

        string job = Assert.Single(result.FilesWritten, f => f.EndsWith(".gbrjob", System.StringComparison.Ordinal));
        Assert.Contains("board.GTL", File.ReadAllText(job));
    }

    [Fact]
    public void ABlankSuffix_FallsBackToASyntheticOne_RatherThanFailing()
    {
        var tech = TechWithOneLayer(interchange: null);
        var dir  = NewTempDir();

        var result = GerberExport.Write(dir, "board", AnalyzeGerber(dir, tech));

        Assert.Contains(result.FilesWritten, f => Path.GetFileName(f) == "board.G1_0");
    }

    // ── Duplicate aliases are a real defect, and are now reported ─────────────

    [Fact]
    public void TwoLayersSharingAGerberSuffix_AreReported()
    {
        var tech = TwoLayers(
            new InterchangeMapping(null, null, null, "GTL", null),
            new InterchangeMapping(null, null, null, "gtl", null));   // case-insensitive on purpose

        Assert.Contains(TechValidation.Validate(tech),
            p => p.Contains("Gerber suffix", System.StringComparison.Ordinal)
              && p.Contains("GTL", System.StringComparison.OrdinalIgnoreCase)
              && p.Contains("Signal") && p.Contains("Ground"));
    }

    [Fact]
    public void TwoLayersSharingADxfName_AreReported()
    {
        var tech = TwoLayers(
            new InterchangeMapping(null, null, "METAL", null, null),
            new InterchangeMapping(null, null, "METAL", null, null));

        Assert.Contains(TechValidation.Validate(tech),
            p => p.Contains("DXF layer name", System.StringComparison.Ordinal));
    }

    [Fact]
    public void TwoLayersSharingAGdsiiAlias_AreReported()
    {
        var tech = TwoLayers(
            new InterchangeMapping(42, 0, null, null, null),
            new InterchangeMapping(42, 0, null, null, null));

        Assert.Contains(TechValidation.Validate(tech), p => p.Contains("GDSII alias"));
    }

    [Fact]
    public void AHalfStatedGdsiiAlias_IsNotACollision()
    {
        // Only one half stated means the other falls back to the layer's own key, which the layer
        // table's own duplicate-key check already covers. Flagging it here would be a false alarm.
        var tech = TwoLayers(
            new InterchangeMapping(42, null, null, null, null),
            new InterchangeMapping(42, null, null, null, null));

        Assert.DoesNotContain(TechValidation.Validate(tech), p => p.Contains("GDSII alias"));
    }

    [Fact]
    public void BlankAliasesAreNeverACollision()
    {
        var tech = TwoLayers(interchangeA: null, interchangeB: null);

        Assert.DoesNotContain(TechValidation.Validate(tech),
            p => p.Contains("Gerber suffix") || p.Contains("DXF layer name") || p.Contains("GDSII alias"));
    }

    // ── …and a colliding suffix no longer destroys one layer's copper ─────────

    [Fact]
    public void TwoLayersSharingASuffix_BothFilesAreWritten_NeitherOverwritesTheOther()
    {
        var tech = TwoLayers(
            new InterchangeMapping(null, null, null, "GTL", null),
            new InterchangeMapping(null, null, null, "GTL", null));

        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0,      Y1 = 0, X2 = 1_000_000, Y2 = 500_000 });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 2_000_000, Y1 = 0, X2 = 3_000_000, Y2 = 500_000 });

        var dir  = NewTempDir();
        var plan = GerberExport.Analyze(NewTempDir(), tech, 1000, view, resolveTechAt: null);
        var result = GerberExport.Write(dir, "board", plan);

        var gerbers = result.FilesWritten
            .Where(f => Path.GetFileName(f).StartsWith("board.GTL", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, gerbers.Count);
        Assert.Equal(2, gerbers.Select(Path.GetFileName).Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        foreach (var f in gerbers)
            Assert.Contains("D01", File.ReadAllText(f));   // each carries real geometry, not an empty shell
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Technology TechWithOneLayer(InterchangeMapping? interchange)
    {
        var tech = StarterTechnologies.Empty();
        tech.Layers.Add(new LayerDef
        {
            Key = new LayerKey(1, 0), Name = "Signal",
            Color = new Rgba(200, 60, 60, 255), Interchange = interchange,
        });
        return tech;
    }

    private static Technology TwoLayers(InterchangeMapping? interchangeA, InterchangeMapping? interchangeB)
    {
        var tech = TechWithOneLayer(interchangeA);
        tech.Layers.Add(new LayerDef
        {
            Key = new LayerKey(2, 0), Name = "Ground",
            Color = new Rgba(60, 60, 200, 255), Interchange = interchangeB,
        });
        return tech;
    }

    private static LayoutView OneRectOnLayerOne()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 500_000 });
        return view;
    }

    private static GerberExport.ExportPlan AnalyzeGerber(string dir, Technology tech)
        => GerberExport.Analyze(dir, tech, 1000, OneRectOnLayerOne(), resolveTechAt: null);

    private static string WriteDxf(LayoutView view, Technology tech)
    {
        var plan = DxfExport.Analyze(NewTempDir(), tech, 1000, view);
        var options = new DxfExportOptions(
            FlattenSplinesToPolyline: false, PathAsOutlinePolygon: false,
            ViewMode: DxfViewMode.FitToExtents,
            MatchViewport: new LayoutViewport(0, 0, 1, 100, 100), CanvasAspect: 1.0);
        string path = Path.Combine(NewTempDir(), "out.dxf");
        DxfExport.Write(path, plan, options);
        return File.ReadAllText(path);
    }

    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-interchange-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }
}
