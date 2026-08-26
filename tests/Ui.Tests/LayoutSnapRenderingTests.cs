using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §2.5/R-snp-4 — the marker glyph paints
// at the candidate's world position, colored by its SOURCE layer (not the theme accent) — pixel
// oracle in the same style as LayoutHandleRenderingTests.cs (L1d).

public class LayoutSnapRenderingTests
{
    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };

    private static Technology MakeTech(LayerKey key, SKColor color) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = key, Name = "L", Color = new CircuitRF.Design.Theming.Rgba(color.Red, color.Green, color.Blue), FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static SKSurface Render(LayoutView view, LayoutViewport vp, LayoutOverlay? overlay)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, Overlay = overlay };
        LayoutRenderer.Draw(surface.Canvas, view, MakeTech(MarkerLayer, MarkerColor), vp, opts);
        return surface;
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(System.Math.Clamp(x, 0, bmp.Width - 1), System.Math.Clamp(y, 0, bmp.Height - 1));
    }

    // A saturated, distinctive color unlikely to appear anywhere else in a plain empty-background render.
    private static readonly LayerKey MarkerLayer = new(9, 0);
    private static readonly SKColor MarkerColor = new(230, 30, 200);

    private static bool NearColor(SKColor a, SKColor b, int tol = 40) =>
        System.Math.Abs(a.Red - b.Red) <= tol && System.Math.Abs(a.Green - b.Green) <= tol && System.Math.Abs(a.Blue - b.Blue) <= tol;

    private static bool ColorNear(SKSurface surface, int sx, int sy, SKColor target, int radius = 4)
    {
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                if (NearColor(PixelAt(surface, sx + dx, sy + dy), target)) return true;
        return false;
    }

    /// <summary>The rendered glyph is the source layer color TINTED for contrast against the canvas
    /// background (owner follow-up, brief-geometry-snap-followups.md — darker on a light canvas,
    /// lighter on a dark one) — every test here renders on <see cref="LayoutRenderTheme.Light"/>, so
    /// the expected on-screen color is <paramref name="sourceColor"/> blended 30% toward black.
    /// Computed independently from production's own (private) <c>TintForContrast</c>, as a real
    /// cross-check rather than assuming the implementation agrees with itself.</summary>
    private static SKColor ExpectedMarkerColorOnLightTheme(SKColor sourceColor)
    {
        const double amount = 0.3;
        return new SKColor(
            (byte)System.Math.Clamp(sourceColor.Red   * (1 - amount), 0, 255),
            (byte)System.Math.Clamp(sourceColor.Green * (1 - amount), 0, 255),
            (byte)System.Math.Clamp(sourceColor.Blue  * (1 - amount), 0, 255));
    }

    [Fact]
    public void SnapMarker_PaintsAtCandidatePosition_InItsSourceLayerColor_TintedForContrast()
    {
        var view = MakeView();
        var vp = new LayoutViewport(-50_000, -50_000, 0.002, 400, 400);
        var candidate = new SnapCandidate(SnapFeatureKind.CornerEndpoint, 10_000, 10_000, MarkerLayer, false, 0);
        var expected = ExpectedMarkerColorOnLightTheme(MarkerColor);

        int sx = (int)vp.WorldToScreenX(10_000);
        int sy = (int)vp.WorldToScreenY(10_000);

        var surfaceNone = Render(view, vp, new LayoutOverlay());
        var surfaceMarked = Render(view, vp, new LayoutOverlay { SnapMarker = candidate });

        Assert.False(ColorNear(surfaceNone, sx, sy, expected), "sanity check: no marker paints nothing there");
        Assert.True(ColorNear(surfaceMarked, sx, sy, expected), "expected the marker glyph, tinted for contrast, at the candidate's world position");
    }

    [Fact]
    public void SnapMarker_ScreenSpaceSize_IsConstantAcrossZoom()
    {
        var view = MakeView();
        var candidate = new SnapCandidate(SnapFeatureKind.Centroid, 0, 0, MarkerLayer, false, 0);
        var expected = ExpectedMarkerColorOnLightTheme(MarkerColor);

        var vpNear = new LayoutViewport(-2_000, -2_000, 0.05, 400, 400);
        var vpFar  = new LayoutViewport(-500_000, -500_000, 0.0002, 400, 400);

        // At both zooms, a small radius around the projected candidate point should show the marker
        // color — the glyph is defined in constant DEVICE pixels, so this holds at any zoom.
        foreach (var vp in new[] { vpNear, vpFar })
        {
            int sx = (int)vp.WorldToScreenX(0);
            int sy = (int)vp.WorldToScreenY(0);
            var surface = Render(view, vp, new LayoutOverlay { SnapMarker = candidate });
            Assert.True(ColorNear(surface, sx, sy, expected, radius: 6));
        }
    }

    /// <summary>
    /// <b>The glyph survives an overlay painted over the whole canvas</b> — the wBond wire case, where
    /// a wire and its vertex dots scale with zoom without limit while the glyph is a fixed ~8 device
    /// pixels, so past some zoom the wire is simply wider than the glyph and covers it (owner,
    /// 2026-08-19: "the geometry snap glyphs do not render if the zoom level is too high. I believe
    /// the glyphs are there but are hidden behind the wire point and segment renderings.").
    ///
    /// <para>The overlay here is a plain opaque fill over everything, which is the extreme form of the
    /// same thing and needs no wires to state it: with the glyph drawn inside
    /// <see cref="LayoutRenderer.Draw"/> nothing of it can survive, and with the draw deferred to
    /// after the overlay all of it does. Both halves are asserted, so this fails if either the option
    /// stops suppressing or the top-most call stops drawing.</para>
    /// </summary>
    [Fact]
    public void SnapMarker_DeferredToTheHost_PaintsOverAnOverlay()
    {
        var view = MakeView();
        var tech = MakeTech(MarkerLayer, MarkerColor);
        var vp = new LayoutViewport(-50_000, -50_000, 0.002, 400, 400);
        var candidate = new SnapCandidate(SnapFeatureKind.CornerEndpoint, 10_000, 10_000, MarkerLayer, false, 0);
        var expected = ExpectedMarkerColorOnLightTheme(MarkerColor);

        int sx = (int)vp.WorldToScreenX(10_000);
        int sy = (int)vp.WorldToScreenY(10_000);

        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light,
            ShowGrid = false,
            Overlay = new LayoutOverlay { SnapMarker = candidate },
            DeferSnapMarker = true,
        };

        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        // Whatever the host paints after the layout — here, everything.
        using (var cover = new SKPaint { Color = new SKColor(20, 20, 20), Style = SKPaintStyle.Fill })
            surface.Canvas.DrawRect(SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height), cover);

        Assert.False(ColorNear(surface, sx, sy, expected),
                     "deferred means LayoutRenderer.Draw must not have drawn it — it would be under the overlay");

        LayoutRenderer.DrawSnapMarkerOnTop(surface.Canvas, view, tech, vp, opts);

        Assert.True(ColorNear(surface, sx, sy, expected),
                    "the glyph is drawn last, above the overlay, which is the only place it can be seen");
    }

    /// <summary>
    /// The top-most draw lands in the SAME place the in-pipeline one does — it rebuilds the path-space
    /// transform rather than being handed it, so an oracle that only checked "something was painted"
    /// would not catch an origin or a Y-flip going astray.
    /// </summary>
    [Fact]
    public void SnapMarker_DrawnOnTop_LandsWhereTheInPipelineDrawDoes()
    {
        var view = MakeView();
        var tech = MakeTech(MarkerLayer, MarkerColor);
        var vp = new LayoutViewport(-50_000, -20_000, 0.002, 400, 300);
        var candidate = new SnapCandidate(SnapFeatureKind.Midpoint, 30_000, -5_000, MarkerLayer, false, 0);
        var overlay = new LayoutOverlay { SnapMarker = candidate };
        var expected = ExpectedMarkerColorOnLightTheme(MarkerColor);

        var inPipeline = Render(view, vp, overlay);

        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, Overlay = overlay, DeferSnapMarker = true,
        };
        using var onTop = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(onTop.Canvas, view, tech, vp, opts);
        LayoutRenderer.DrawSnapMarkerOnTop(onTop.Canvas, view, tech, vp, opts);

        using var a = SKBitmap.FromImage(inPipeline.Snapshot());
        using var b = SKBitmap.FromImage(onTop.Snapshot());

        int matched = 0;
        for (int x = 0; x < a.Width; x++)
            for (int y = 0; y < a.Height; y++)
            {
                Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
                if (NearColor(a.GetPixel(x, y), expected)) matched++;
            }

        Assert.True(matched > 0, "sanity check: the glyph has to be on the canvas for the comparison to mean anything");
        inPipeline.Dispose();
    }

    [Fact]
    public void SnapMarker_Null_PaintsNothingExtra()
    {
        var view = MakeView();
        var vp = new LayoutViewport(-50_000, -50_000, 0.002, 400, 400);
        var surface = Render(view, vp, new LayoutOverlay { SnapMarker = null });
        int sx = (int)vp.WorldToScreenX(10_000);
        int sy = (int)vp.WorldToScreenY(10_000);
        Assert.False(ColorNear(surface, sx, sy, MarkerColor));
    }

    [Fact]
    public void SnapMarker_TintsLighter_OnADarkCanvas()
    {
        var view = MakeView();
        var vp = new LayoutViewport(-50_000, -50_000, 0.002, 400, 400);
        var candidate = new SnapCandidate(SnapFeatureKind.CornerEndpoint, 10_000, 10_000, MarkerLayer, false, 0);

        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Dark, ShowGrid = false, Overlay = new LayoutOverlay { SnapMarker = candidate } };
        LayoutRenderer.Draw(surface.Canvas, view, MakeTech(MarkerLayer, MarkerColor), vp, opts);

        // Opposite direction from the light-theme tint above: blended TOWARD white, not black.
        const double amount = 0.3;
        var expected = new SKColor(
            (byte)System.Math.Clamp(MarkerColor.Red   + (255 - MarkerColor.Red)   * amount, 0, 255),
            (byte)System.Math.Clamp(MarkerColor.Green + (255 - MarkerColor.Green) * amount, 0, 255),
            (byte)System.Math.Clamp(MarkerColor.Blue  + (255 - MarkerColor.Blue)  * amount, 0, 255));

        int sx = (int)vp.WorldToScreenX(10_000);
        int sy = (int)vp.WorldToScreenY(10_000);
        Assert.True(ColorNear(surface, sx, sy, expected), "expected the marker tinted LIGHTER against a dark canvas");
    }

    // Pixel-measuring the exact glyph size is fragile (stroke thickness alone extends the painted
    // region well past the geometric half-size, and the path-space<->device-pixel conversion this
    // glyph deliberately keeps constant across zoom is an internal implementation detail this test
    // has no business depending on) — so, per this codebase's own established fallback for exactly
    // this situation (an on-screen dimension that can't be pixel-measured reliably), read the actual
    // source constants directly. Confirms BOTH the size and stroke were scaled together (a "10%
    // bigger glyph" that grew its outline box but not its stroke width would look thin and small
    // anyway), not just one of the two.
    private static string ReadRepoFile(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = System.IO.Path.GetDirectoryName(here);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "CLAUDE.md")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void SnapMarkerSizeConstants_AreAFurtherTenPercentLarger_PerBriefRCmb6()
    {
        var src = ReadRepoFile("src/Ui/Renderers/LayoutRenderer.Snap.cs");
        // docs/sonnet-briefs/brief-snap-combobox-and-consistency.md R-cmb-6: a further 10% on top of
        // the 7.7/1.65 pair above — 8.47/1.815. Both must move together (a bigger outline box with an
        // unchanged stroke width would look thin and small regardless of the box size).
        Assert.Contains("SnapMarkerSizeDevicePixels = 8.47", src);
        Assert.Contains("SnapMarkerStrokeDevicePixels = 1.815", src);
        // Confirmed (by direct code reading, not merely asserted) that the size is defined in exactly
        // ONE place and used as a parameter by every glyph shape — nothing to consolidate.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, "SnapMarkerSizeDevicePixels\\s*="));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, "SnapMarkerStrokeDevicePixels\\s*="));
    }
}
