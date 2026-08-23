// ================================================================
//  MarkerGlyphContourTests.cs
//  Gate tests for brief-dd-loadpull-contour-ux-round8 §1 — the contour Mode-1 (interpolated)
//  marker glyph: harmonicaRF-matched size, name-length branch (inside vs above), and the
//  derived Bone-tinted fill colour's luminance floor.
//
//  SkiaFonts.PlexBold cannot load headlessly (src/Ui/CLAUDE.md) — TestOverrideTypeface substitutes
//  SKTypeface.Default, which is typeface-independent for the pixel-presence checks used here.
// ================================================================

using System;
using System.IO;
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

    // The glyph radius is the MXP/MXE glyph's own — 3.5 x AxesRenderer.LineWidth — and is
    // canvas-proportional with NO floor, so the marker keeps the same relative size at every zoom.
    //
    // REVISED 2026-08-18 (owner: "the marker render size changes relative size to MXP/MXE glyphs
    // depending on data display zoom level"). It used to be harmonicaRF's r = max(6, min(W,H)*0.020),
    // transcribed for visual parity with that tool: 14% larger than the MXP disc at a big plot, and
    // the max(6, ...) floor meant it stopped shrinking below roughly 300x300 while MXP/MXE kept going,
    // reaching 1.71x at 100x100 — which is the ratio drift being reported. See
    // ContourRenderer.OptimumMarkerRadius; DataDisplayRound12Tests holds the equality itself.
    [Theory]
    [InlineData(1000.0, 1000.0, 17.5)]   // 3.5 * (1000/200)
    [InlineData(100.0, 100.0, 1.75)]     // proportional all the way down — no floor
    public void ContourMode1Marker_HitRadius_MatchesTheOptimumGlyphRule(double w, double h, double expectedR)
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

    // ── The two-character name fits inside the ring (2026-08-23) ────────────────
    //
    //  Measured against the SHIPPED typeface, loaded straight off disk rather than through the
    //  class's SKTypeface.Default substitution: whether "m1" overflows is a property of the face,
    //  and the substitute's does not overflow where IBM Plex Bold's does. The static font seam is
    //  deliberately left alone — other test classes read it concurrently.

    private static float PlexBoldHalfDiagonalPerEm(string name)
    {
        string path = Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Fonts",
            "IBM_Plex_Sans", "static", "IBMPlexSans-Bold.ttf");
        using var face = SKTypeface.FromFile(path);
        Assert.NotNull(face);

        const float measureSize = 100f;
        using var font = new SKFont(face, measureSize);
        font.MeasureText(name, out SKRect ink);
        return MathF.Sqrt(ink.Width * ink.Width + ink.Height * ink.Height) / 2f / measureSize;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// A two-character name drawn at the MXP/MXE letter size does not fit inside the disc — that is
    /// the reported defect, asserted so the fit below is measured against a real overflow and not a
    /// hypothetical one. That size is sized for ONE letter.
    /// </summary>
    [Theory]
    [InlineData(200.0)]
    [InlineData(400.0)]
    [InlineData(1000.0)]
    public void TwoCharacterName_AtTheOptimumLetterSize_OverflowsTheRing(double size)
    {
        float ts    = ContourRenderer.OptimumMarkerFontSize((size, size));
        float clear = ContourRenderer.OptimumMarkerRadius((size, size))
                    - ContourRenderer.OptimumMarkerRingWidth((size, size)) / 2f;

        Assert.True(PlexBoldHalfDiagonalPerEm("m1") * ts > clear,
            "the unfitted two-character name reaches past the ring's inner edge");
    }

    /// <summary>The fitted size clears the ring with margin, at every canvas size.</summary>
    [Theory]
    [InlineData(200.0)]
    [InlineData(400.0)]
    [InlineData(1000.0)]
    public void FittedName_ClearsTheRingWithMargin(double size)
    {
        float r     = ContourRenderer.OptimumMarkerRadius((size, size));
        float rw    = ContourRenderer.OptimumMarkerRingWidth((size, size));
        float ts    = ContourRenderer.OptimumMarkerFontSize((size, size));
        float clear = r - rw / 2f;
        float perEm = PlexBoldHalfDiagonalPerEm("m1");

        float fitted = MarkerRenderer.FitNameInsideDisc(perEm, ts, r, rw);

        Assert.True(fitted < ts, "a two-character name must be shrunk");
        Assert.True(perEm * fitted <= clear * 0.95f,
            "the fitted name must leave a visible margin, not merely touch the ring");
    }

    /// <summary>
    /// A ONE-character name is left at exactly the MXP/MXE letter size — the fit buys the margin
    /// only where it is needed, so the three glyphs stay one family where they always were.
    /// </summary>
    [Theory]
    [InlineData("m")]
    [InlineData("P")]
    [InlineData("E")]
    public void SingleCharacterName_IsNotShrunk(string name)
    {
        var size = (1000.0, 1000.0);
        float r  = ContourRenderer.OptimumMarkerRadius(size);
        float rw = ContourRenderer.OptimumMarkerRingWidth(size);
        float ts = ContourRenderer.OptimumMarkerFontSize(size);

        Assert.Equal(ts, MarkerRenderer.FitNameInsideDisc(PlexBoldHalfDiagonalPerEm(name), ts, r, rw), 5);
    }

    /// <summary>
    /// The DISC is untouched — this change is the name's size only. Both halves of that claim are
    /// asserted: the radius still equals the MXP/MXE radius, and the fit can never enlarge a name.
    /// </summary>
    [Fact]
    public void TheDiscIsUnchangedAndTheFitNeverGrowsTheName()
    {
        var size = (1000.0, 1000.0);
        Assert.Equal(ContourRenderer.OptimumMarkerRadius(size),
                     MarkerRenderer.ContourMarkerRadiusForTests(size), 5);

        float r  = ContourRenderer.OptimumMarkerRadius(size);
        float rw = ContourRenderer.OptimumMarkerRingWidth(size);
        float ts = ContourRenderer.OptimumMarkerFontSize(size);

        foreach (var name in new[] { "m", "P", "E", "m1", "m9" })
            Assert.True(MarkerRenderer.FitNameInsideDisc(PlexBoldHalfDiagonalPerEm(name), ts, r, rw) <= ts);
    }

    /// <summary>
    /// The shrink is a fixed RATIO of the letter size, so the marker keeps scaling with the canvas
    /// exactly as the MXP/MXE glyphs do — the proportionality an earlier round established is not
    /// spent to buy the margin. Measuring the fit at the DRAW size instead breaks this: text metrics
    /// are not linear in font size at these sizes, which is why the renderer measures once at a
    /// stable reference size.
    /// </summary>
    [Fact]
    public void TheFittedNameStaysProportionalToTheCanvas()
    {
        float perEm = PlexBoldHalfDiagonalPerEm("m1");

        float Ratio(double size)
        {
            float r  = ContourRenderer.OptimumMarkerRadius((size, size));
            float rw = ContourRenderer.OptimumMarkerRingWidth((size, size));
            float ts = ContourRenderer.OptimumMarkerFontSize((size, size));
            return MarkerRenderer.FitNameInsideDisc(perEm, ts, r, rw) / ts;
        }

        Assert.Equal(Ratio(200.0), Ratio(1600.0), 4);
    }
}
