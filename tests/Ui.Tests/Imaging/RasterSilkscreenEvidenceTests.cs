// brief-img-4-layout-image-to-schematic.md §4 — silkscreen read from a picture's pixels (R-im4-4), and the source scan
// that holds the recognition shut against pictures (R-im4-1).
//
// "C12" is drawn in the built-in stroke font — the font AS-10's matcher holds templates of — beside an 0603 land pattern,
// at 20 µm/px. At a 3 px stroke the skeleton IM-3 keeps reads as the designator and AS-4 gives it to the pair beside it;
// at 1 px the strokes are reported too thin, and nothing is read.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class RasterSilkscreenEvidenceTests
{
    private const double UmPerPx = 20;
    private static readonly SKColor Copper = new(0xc8, 0x75, 0x33), Legend = new(0x20, 0x30, 0x90);

    /// <summary>An 0603 pair at (200, 200) px along x, and "C12" above it, 40 px high, drawn with a
    /// <paramref name="strokePx"/> pen.</summary>
    private static RasterImage Board(float strokePx) => Pictures.Draw(500, 360, c =>
    {
        var r = LandPatternMatch.References.All.First(r => r.Case.Code == "0603" && r.Density == DensityLevel.Nominal);
        float along = (float)(r.AlongUm / UmPerPx), across = (float)(r.AcrossUm / UmPerPx), gap = (float)(r.GapUm / UmPerPx);
        using var cu = Pictures.Fill(Copper);
        c.DrawRect(SKRect.Create(200, 200, along, across), cu);
        c.DrawRect(SKRect.Create(200 + along + gap, 200, along, across), cu);

        using var pen = Pictures.Stroke(Legend, strokePx);
        pen.StrokeCap = SKStrokeCap.Round;
        pen.StrokeJoin = SKStrokeJoin.Round;
        const float capPx = 40, x0 = 200, baseline = 180;
        foreach (var stroke in GlyphTemplates.BuiltIn.Draw("C12", capPx))
        {
            using var path = new SKPath();
            path.MoveTo(x0 + (float)stroke[0], baseline - (float)stroke[1]);
            for (int i = 2; i + 1 < stroke.Length; i += 2) path.LineTo(x0 + (float)stroke[i], baseline - (float)stroke[i + 1]);
            c.DrawPath(path, pen);
        }
    });

    private static (ImageTraceResult Trace, RecognitionInput Input) Read(float strokePx)
    {
        // The legend's colour mapped to the silkscreen, as the user maps it: one short word is no rows of text, so Auto
        // does not call it silkscreen on its own.
        var tech = TracePictures.Tech();
        var input = new ImageTraceInput
        {
            Source = TracePictures.Source(Board(strokePx)), Technology = tech, Scale = ImageScaleStatement.Stated(UmPerPx * 1e-6),
        };
        var auto = ImageTrace.Trace(input);
        int legend = Pictures.ClusterOf(auto.Clusters!, (Legend.Red << 16) | (Legend.Green << 8) | Legend.Blue);
        var trace = ImageTrace.Trace(input with { LayerMap = auto.LayerMap!.With(legend, ImageLayerRole.Silkscreen, "Silk Top") });
        Assert.True(trace.Ok, trace.Refusal);
        return (trace, RecognitionInput.FromTrace(trace, tech));
    }

    [Fact]
    public void AThreePixelDesignator_ReadsAndNamesThePairBesideIt()
    {
        var (_, input) = Read(3);
        var silk = Assert.IsType<RasterSilkscreenEvidence>(Assert.Single(input.EvidenceSources));
        Assert.False(silk.TooThin);
        Assert.Contains(silk.Lines, l => l.Refdes == "C12");

        var parts = ArtworkRecognition.Recognize(input).Parts;
        var row = Assert.Single(parts.Rows);
        Assert.Equal("C12", row.Refdes);
        Assert.Equal(PartEvidenceSource.Silkscreen, row.Evidence[PartField.Refdes]);
        Assert.Equal("0603", row.Case?.Code);
    }

    [Fact]
    public void AOnePixelDesignator_IsReportedTooThin_AndNamesNothing()
    {
        var (_, input) = Read(1);
        var silk = Assert.IsType<RasterSilkscreenEvidence>(Assert.Single(input.EvidenceSources));
        Assert.True(silk.TooThin);
        Assert.Empty(silk.Lines);
        var report = new RecognitionReport();
        silk.Report(report);
        Assert.Equal(RecognitionFindingClass.ImageSilkscreenTooThin, Assert.Single(report.Findings).Class);

        var row = Assert.Single(ArtworkRecognition.Recognize(input).Parts.Rows);
        Assert.Equal(PartEvidenceSource.Generated, row.Evidence[PartField.Refdes]);
    }

    /// <summary>R-im4-1: the recognition gains no image branch. Comment-stripped, no file under
    /// <c>src/Design/Layout/Recognition/</c> names <c>ImageSource</c> but the picture code itself, the raster evidence
    /// source, and the file holding <c>FromTrace</c>.</summary>
    [Fact]
    public void NoArtworkStage_NamesThePicture()
    {
        string root = Path.Combine(RepoRoot(), "src", "Design", "Layout", "Recognition");
        string fromTrace = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                                    .Single(f => Strip(File.ReadAllText(f)).Contains("static RecognitionInput FromTrace(", StringComparison.Ordinal));
        var naming = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Path.Combine(root, "Image") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => f != Path.Combine(root, "Silkscreen", "RasterSilkscreenEvidence.cs") && f != fromTrace)
            .Where(f => Regex.IsMatch(Strip(File.ReadAllText(f)), @"\bImageSource\b"))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.Empty(naming);
    }

    private static string Strip(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
