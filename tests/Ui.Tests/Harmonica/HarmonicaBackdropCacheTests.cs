// ================================================================
//  HarmonicaBackdropCacheTests.cs — brief-harmonicarf-r4 §4
//
//  The two cached raster layers behind a Smith panel (chrome + frozen contours + optimum; the
//  grid-point dots) must be: (1) pixel-identical to the uncached draw for a static scene, and
//  (2) invalidated by each individual key field, one test per field rather than one test that
//  changes everything at once (§4.5's own gate).
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using RfCore.Loadpull;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class HarmonicaBackdropCacheTests : IDisposable
{
    private const int W = 420, H = 420;

    public HarmonicaBackdropCacheTests() => SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
    public void Dispose() => SkiaFonts.TestOverrideTypeface = null;

    private static IReadOnlyList<IsoPolyline> Contours(double level = 3.0) =>
    [
        new IsoPolyline(level,
            [(-0.3, -0.2), (0.1, 0.3), (0.4, -0.1), (-0.3, -0.2)],
            Closed: true),
    ];

    private static SmithPanelData Fixture() => new()
    {
        Title = "P-3dB Power (dBm)",
        Subtitle = "Fundamental Load Plane, Z0=50Ω",
        Contours = Contours(),
        Levels = [1.0, 2.0, 3.0],
        GridPoints =
        [
            new HarmonicaGridPoint(new Complex(-0.4, 0.1), IsHole: false),
            new HarmonicaGridPoint(new Complex(0.35, -0.2), IsHole: true),
        ],
        Optimum = new SmithPanelData.SmithOptimum(new Complex(0.1, 0.05), 37.2, Solved: null, Published: null),
    };

    private static SKBitmap Render(SmithPanelData d, HarmonicaBackdropCache? cache, double deviceScale = 1.0)
    {
        var theme = HarmonicaRenderTheme.Dark;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), d, theme, darkMode: true,
                                              showGridPoints: true, topmostMarker: null,
                                              cache: cache, deviceScale: deviceScale);
        return SKBitmap.FromImage(surface.Snapshot());
    }

    private static bool BitmapsIdentical(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
        for (int x = 0; x < a.Width; x++)
            if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    /// <summary>§4.5's own gate names 1x AND 2x explicitly, and the real Avalonia canvas this actually
    /// runs on is never phase-aligned to the panel's own ChartBox origin the way <see cref="Render"/>'s
    /// bare surface-at-(0,0) is — a real panel sits at some outer position within a larger canvas, and
    /// HiDPI means that position is scaled too. Renders with an outer 2x scale AND a fractional outer
    /// translate (mimicking a real panel's position within the app's own canvas) to prove the
    /// cache/uncached match holds under the general case, not merely the test harness's simplest one —
    /// this is what actually exercises <c>DrawSmithPanelCached</c>'s <c>canvas.TotalMatrix</c>-based
    /// phase alignment rather than the degenerate identity-matrix case <see cref="Render"/> alone
    /// would.</summary>
    [Fact]
    public void CacheOnVsOff_ArePixelIdentical_At2xWithAnOuterFractionalTransform()
    {
        var d = Fixture();
        const float outerScale = 2.0f, outerTx = 17.3f, outerTy = 8.7f;

        SKBitmap RenderWith(HarmonicaBackdropCache? cache)
        {
            var theme = HarmonicaRenderTheme.Dark;
            using var surface = SKSurface.Create(new SKImageInfo(
                (int)((W + 60) * outerScale), (int)((H + 60) * outerScale)));
            surface.Canvas.Clear(theme.Background);
            surface.Canvas.Save();
            surface.Canvas.Scale(outerScale);
            surface.Canvas.Translate(outerTx, outerTy);
            HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), d, theme, darkMode: true,
                                                  showGridPoints: true, topmostMarker: null,
                                                  cache: cache, deviceScale: outerScale);
            surface.Canvas.Restore();
            return SKBitmap.FromImage(surface.Snapshot());
        }

        using var uncached = RenderWith(null);
        using var cache = new HarmonicaBackdropCache();
        using var cached = RenderWith(cache);

        Assert.True(BitmapsIdentical(uncached, cached),
            "a cached frame must be pixel-identical to the uncached frame at 2x with a fractional outer transform");
    }

    // ══ §4.5's correctness gate — cache on vs off, pixel-identical for a static scene ══════════

    [Fact]
    public void CacheOnVsOff_ArePixelIdentical_ForAStaticScene()
    {
        var d = Fixture();

        using var uncached = Render(d, cache: null);
        using var cache = new HarmonicaBackdropCache();
        using var cached = Render(d, cache);

        Assert.True(BitmapsIdentical(uncached, cached),
            "a cached frame must be pixel-identical to the uncached frame for a static scene");
    }

    [Fact]
    public void CacheOnVsOff_ArePixelIdentical_WithAReachableRegionShowing()
    {
        // The reachable region's own z-order moved (§4's own note in DrawSmithPanelCached) — this is
        // what proves that move keeps cached and uncached frames identical even then, not just in the
        // common case where Reachable is null.
        var d = Fixture() with
        {
            Reachable = new ReachableRegion(
                Boundary: [new Complex(-0.5, -0.3), new Complex(0.5, -0.3),
                           new Complex(0.5, 0.3), new Complex(-0.5, 0.3)],
                Interior: [], Solves: 4),
        };

        using var uncached = Render(d, cache: null);
        using var cache = new HarmonicaBackdropCache();
        using var cached = Render(d, cache);

        Assert.True(BitmapsIdentical(uncached, cached),
            "a cached frame with a reachable region showing must still be pixel-identical to uncached");
    }

    [Fact]
    public void RenderingTwice_WithTheSameData_ReusesBothLayers()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();

        using (Render(d, cache)) { }
        using (Render(d, cache)) { }

        Assert.Equal(1, cache.LayerARebuilds);
        Assert.Equal(1, cache.LayerBRebuilds);
    }

    // ══ §4.5 — one test per invalidation-key field ══════════════════════════════════════════

    [Fact]
    public void ChangingContours_RebuildsLayerA_NotLayerB()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        using (Render(d, cache)) { }

        var changed = d with { Contours = Contours(level: 7.0) };
        using (Render(changed, cache)) { }

        Assert.Equal(2, cache.LayerARebuilds);
        Assert.Equal(1, cache.LayerBRebuilds);
    }

    [Fact]
    public void ChangingLevels_RebuildsLayerA()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        using (Render(d, cache)) { }

        var changed = d with { Levels = [1.0, 2.0, 3.0, 4.0] };
        using (Render(changed, cache)) { }

        Assert.Equal(2, cache.LayerARebuilds);
    }

    [Fact]
    public void ChangingOptimum_RebuildsLayerA()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        using (Render(d, cache)) { }

        var changed = d with
        {
            Optimum = new SmithPanelData.SmithOptimum(new Complex(-0.2, 0.2), 40.0, null, null),
        };
        using (Render(changed, cache)) { }

        Assert.Equal(2, cache.LayerARebuilds);
    }

    [Fact]
    public void ChangingTitleOrSubtitle_RebuildsLayerA()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        using (Render(d, cache)) { }

        using (Render(d with { Title = "different title" }, cache)) { }
        Assert.Equal(2, cache.LayerARebuilds);

        using (Render(d with { Subtitle = "different subtitle" }, cache)) { }
        Assert.Equal(3, cache.LayerARebuilds);
    }

    [Fact]
    public void ChangingGridPoints_RebuildsLayerB_NotLayerA()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        using (Render(d, cache)) { }

        var changed = d with
        {
            GridPoints = [new HarmonicaGridPoint(new Complex(0.0, 0.0), IsHole: false)],
        };
        using (Render(changed, cache)) { }

        Assert.Equal(1, cache.LayerARebuilds);
        Assert.Equal(2, cache.LayerBRebuilds);
    }

    [Fact]
    public void ChangingPanelRect_RebuildsBothLayers()
    {
        var d = Fixture();
        var theme = HarmonicaRenderTheme.Dark;
        using var cache = new HarmonicaBackdropCache();

        using (var s1 = SKSurface.Create(new SKImageInfo(W, H)))
        {
            s1.Canvas.Clear(theme.Background);
            HarmonicaPanelRenderer.DrawSmithPanel(s1.Canvas, (W, H), d, theme, darkMode: true,
                                                  showGridPoints: true, cache: cache);
        }
        using (var s2 = SKSurface.Create(new SKImageInfo(W + 40, H + 20)))
        {
            s2.Canvas.Clear(theme.Background);
            HarmonicaPanelRenderer.DrawSmithPanel(s2.Canvas, (W + 40, H + 20), d, theme, darkMode: true,
                                                  showGridPoints: true, cache: cache);
        }

        Assert.Equal(2, cache.LayerARebuilds);
        Assert.Equal(2, cache.LayerBRebuilds);
    }

    [Fact]
    public void ChangingDevicePixelScale_RebuildsBothLayers()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();

        using (Render(d, cache, deviceScale: 1.0)) { }
        using (Render(d, cache, deviceScale: 2.0)) { }

        Assert.Equal(2, cache.LayerARebuilds);
        Assert.Equal(2, cache.LayerBRebuilds);
    }

    [Fact]
    public void ChangingTheme_RebuildsBothLayers()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();

        using (var s1 = SKSurface.Create(new SKImageInfo(W, H)))
        {
            HarmonicaPanelRenderer.DrawSmithPanel(s1.Canvas, (W, H), d, HarmonicaRenderTheme.Dark,
                                                  darkMode: true, showGridPoints: true, cache: cache);
        }
        using (var s2 = SKSurface.Create(new SKImageInfo(W, H)))
        {
            HarmonicaPanelRenderer.DrawSmithPanel(s2.Canvas, (W, H), d, HarmonicaRenderTheme.Light,
                                                  darkMode: false, showGridPoints: true, cache: cache);
        }

        Assert.Equal(2, cache.LayerARebuilds);
        Assert.Equal(2, cache.LayerBRebuilds);
    }

    [Fact]
    public void ChangingShowGridPoints_ToggleDoesNotRebuildLayerA()
    {
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        var theme = HarmonicaRenderTheme.Dark;

        using (var s1 = SKSurface.Create(new SKImageInfo(W, H)))
            HarmonicaPanelRenderer.DrawSmithPanel(s1.Canvas, (W, H), d, theme, darkMode: true,
                                                  showGridPoints: true, cache: cache);
        using (var s2 = SKSurface.Create(new SKImageInfo(W, H)))
            HarmonicaPanelRenderer.DrawSmithPanel(s2.Canvas, (W, H), d, theme, darkMode: true,
                                                  showGridPoints: false, cache: cache);

        // Layer A is untouched by the grid-point visibility toggle; Layer B is simply not built at
        // all when grid points are off (nothing to draw), so its OWN rebuild count stays at 1.
        Assert.Equal(1, cache.LayerARebuilds);
        Assert.Equal(1, cache.LayerBRebuilds);
    }

    [Fact]
    public void ChangingShowIsoLineLabels_RebuildsLayerA()
    {
        var d = Fixture();
        var theme = HarmonicaRenderTheme.Dark;
        using var cache = new HarmonicaBackdropCache();

        using (var s1 = SKSurface.Create(new SKImageInfo(W, H)))
            HarmonicaPanelRenderer.DrawSmithPanel(s1.Canvas, (W, H), d, theme, darkMode: true,
                                                  showGridPoints: true, cache: cache, showIsoLineLabels: false);
        using (var s2 = SKSurface.Create(new SKImageInfo(W, H)))
            HarmonicaPanelRenderer.DrawSmithPanel(s2.Canvas, (W, H), d, theme, darkMode: true,
                                                  showGridPoints: true, cache: cache, showIsoLineLabels: true);

        Assert.Equal(2, cache.LayerARebuilds);
    }

    // ══ carry-forward (R-h9r2-1) reuses the SAME list reference — the cache must recognise that ═

    [Fact]
    public void TheSameListReferenceCarriedForwardAcrossFrames_IsRecognisedAsUnchanged()
    {
        // Mirrors HarmonicaSolver.CarryForwardContourLayer: a grid-less/dragging frame's SmithPanelData
        // reuses the PREDECESSOR's Contours/Levels/GridPoints/Optimum list instances exactly, never a
        // copy. If the cache required value-identical-but-different-instance lists to hit, a drag
        // would rebuild every frame despite R-h9r2-1's own freeze — this is the regression that would
        // silently defeat the whole point of caching mid-drag.
        var d = Fixture();
        using var cache = new HarmonicaBackdropCache();
        using (Render(d, cache)) { }

        // A NEW SmithPanelData record, but Contours/Levels/GridPoints/Optimum are the SAME references
        // — only Title changed (unrelated to grid points), simulating a frame where the grid layer
        // truly did not move.
        var carried = d with { Title = d.Title };
        using (Render(carried, cache)) { }

        Assert.Equal(1, cache.LayerARebuilds);
        Assert.Equal(1, cache.LayerBRebuilds);
    }
}
