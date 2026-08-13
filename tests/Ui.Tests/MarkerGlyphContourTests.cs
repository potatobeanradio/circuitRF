// ================================================================
//  MarkerGlyphContourTests.cs
//  Gate tests for brief-dd-loadpull-contour-ux-round8 §1 — the contour Mode-1 (interpolated)
//  marker glyph: harmonicaRF-matched size, name-length branch (inside vs above), and the
//  derived Bone-tinted fill colour's luminance floor.
//
//  SkiaFonts.PlexBold cannot load headlessly (src/Ui/CLAUDE.md) — TestOverrideTypeface substitutes
//  SKTypeface.Default, which is typeface-independent for the pixel-presence checks used here.
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Renderers;
using RfCore;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class MarkerGlyphContourTests
{
    public MarkerGlyphContourTests() => SkiaFonts.TestOverrideTypeface = SKTypeface.Default;

    private static (Trace trace, TransformSet tf, RenderTheme theme) BuildFixture()
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.ContourData = new ContourData();

        var tf = new TransformSet
        {
            Primary    = (1.0, 1.0, 500.0, 500.0),
            Secondary  = (1.0, 1.0, 500.0, 500.0),
            CanvasSize = (1000.0, 1000.0),
            Viewport   = new Avalonia.Rect(0, 0, 1, 1),
        };
        return (trace, tf, RenderTheme.Light);
    }

    private static bool HasInk(SKBitmap bmp, int x0, int y0, int x1, int y1, SKColor background)
    {
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
            if (bmp.GetPixel(x, y) != background) return true;
        }
        return false;
    }

    // §1: name length <= 2 ("m1") renders inside the disc, not above it.
    [Fact]
    public void ContourMode1Marker_ShortName_DrawnInsideDisc()
    {
        var (trace, tf, theme) = BuildFixture();
        var marker = new Marker(trace, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            MarkerKind     = MarkerKind.Contour,
            ContourSnapped = false,
            Name           = "m1",
            PositionStatic = Vector2.Zero,   // → canvas (500, 500)
        };

        using var surface = SKSurface.Create(new SKImageInfo(1000, 1000));
        surface.Canvas.Clear(SKColors.White);
        MarkerRenderer.DrawSymbol(surface.Canvas, tf.CanvasSize, marker, trace, tf, theme);

        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        // Inside the disc (name baseline ≈ centre.Y + ts*0.36) must carry ink.
        Assert.True(HasInk(bmp, 480, 495, 520, 515, SKColors.White),
            "expected the short name to be drawn inside the disc");

        // Well above the glyph (where the >2-char branch would have put the name) must be
        // untouched white — nothing is drawn there for a short name.
        Assert.False(HasInk(bmp, 470, 455, 530, 470, SKColors.White),
            "no ink expected above the glyph for a short (inside-drawn) name");
    }

    // §1: name length > 2 ("peak1") keeps today's behavior — centred above the glyph.
    [Fact]
    public void ContourMode1Marker_LongName_DrawnAboveGlyph()
    {
        var (trace, tf, theme) = BuildFixture();
        var marker = new Marker(trace, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            MarkerKind     = MarkerKind.Contour,
            ContourSnapped = false,
            Name           = "peak1",
            PositionStatic = Vector2.Zero,
        };

        using var surface = SKSurface.Create(new SKImageInfo(1000, 1000));
        surface.Canvas.Clear(SKColors.White);
        MarkerRenderer.DrawSymbol(surface.Canvas, tf.CanvasSize, marker, trace, tf, theme);

        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        // Above the glyph must carry ink (the name).
        Assert.True(HasInk(bmp, 460, 455, 540, 480, SKColors.White),
            "expected a >2-char name to be drawn above the glyph");
    }

    // §1: the glyph radius matches harmonicaRF's rule — r = max(6, min(W,H)*0.020) — and is
    // canvas-proportional (independent of any zoom factor, per round-7 §2).
    [Theory]
    [InlineData(1000.0, 1000.0, 20.0)]
    [InlineData(100.0, 100.0, 6.0)]   // floor kicks in below 300x300
    public void ContourMode1Marker_HitRadius_MatchesHarmonicaRfRule(double w, double h, double expectedR)
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var marker = new Marker(trace, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            MarkerKind     = MarkerKind.Contour,
            ContourSnapped = false,
            Name           = "m1",
        };
        float hitRadius = MarkerRenderer.SymbolHitRadius(marker, (w, h));
        Assert.Equal(expectedR * 1.5f, hitRadius, precision: 3);
    }

    // §1: a snapped (Mode-2) contour marker keeps the original triangle sizing/hit-radius —
    // untouched by this brief.
    [Fact]
    public void ContourMode2Marker_HitRadius_UnaffectedByMode1Change()
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var mode1 = new Marker(trace, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            MarkerKind = MarkerKind.Contour, ContourSnapped = false, Name = "m1",
        };
        var mode2 = new Marker(trace, freq: 0, isMulti: false, isDelta: false, index: 2)
        {
            MarkerKind = MarkerKind.Contour, ContourSnapped = true, Name = "m1",
        };

        float r1 = MarkerRenderer.SymbolHitRadius(mode1, (1000.0, 1000.0));
        float r2 = MarkerRenderer.SymbolHitRadius(mode2, (1000.0, 1000.0));

        Assert.NotEqual(r1, r2);
    }

    // §1: the derived fill colour's luminance clears the floor (0.70), and is not simply white.
    [Fact]
    public void ResolveContourMarkerFill_LuminanceClearsFloor()
    {
        var c = MarkerRenderer.ResolveContourMarkerFill();
        float lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;

        Assert.True(lum >= 0.70f, $"fill luminance {lum:F3} must clear the 0.70 floor");
        Assert.NotEqual(SKColors.White, c);
    }
}
