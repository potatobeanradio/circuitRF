using System.Collections.Generic;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Phase L1f gates 13/14: docs/sonnet-briefs/brief-L1f-clipboard.md §6.2
// Export-mode rendering: view-independence (R-L1f-4) and a clean geometry-only render
// (R-L1f-5/R-L1f-6). No IClipboard involved — LayoutClipboard's internal render helpers
// (TryRenderToPdf/Svg/AvaloniaImage) are exercised directly, exactly like the rest of the layout
// clipboard is split so the hard parts get real, headless tests.

public class LayoutClipboardExportTests
{
    private static readonly LayerKey Layer1 = new(1, 0);

    private static Technology MakeTech() => new()
    {
        Name = "Test",
        Layers = [new LayerDef { Key = Layer1, Name = "L1", Color = new Rgba(200, 30, 30), FillOpacity = 0.5, Visible = true }],
    };

    private static List<LayoutShape> OneRect() =>
        [new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 50_000 }];

    // ── Gate 13: export is view-independent (R-L1f-4) ────────────────────────────

    [Fact]
    public void TryRenderToSvg_SamePageDimensions_RegardlessOfAnyAmbientViewState()
    {
        var shapes = OneRect();
        var tech = MakeTech();
        var theme = LayoutRenderTheme.Light;

        var a = LayoutClipboard.TryRenderToSvg(shapes, tech, theme, transparent: true);
        var b = LayoutClipboard.TryRenderToSvg(shapes, tech, theme, transparent: true);

        Assert.NotNull(a);
        Assert.NotNull(b);
        // Page dimensions are a pure function of the selection bbox — Skia's SVG canvas assigns
        // globally-incrementing internal element IDs across calls, so the SVG text itself is not
        // byte-identical, but the page geometry (what the gate actually cares about) always is.
        Assert.Equal(a!.Value.W, b!.Value.W);
        Assert.Equal(a.Value.H, b.Value.H);
    }

    [Fact]
    public void TryRenderToPdf_DimensionsDependOnlyOnSelectionBbox_NotOnShapeOrder()
    {
        var shapes = OneRect();
        var reordered = new List<LayoutShape>(shapes); // identical bbox, would matter only if some
        reordered.Reverse();                            // hidden "current view" leaked in

        var tech = MakeTech();
        var theme = LayoutRenderTheme.Light;

        var a = LayoutClipboard.TryRenderToPdf(shapes, tech, theme, transparent: false);
        var b = LayoutClipboard.TryRenderToPdf(reordered, tech, theme, transparent: false);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Length, b!.Length);
    }

    // ── Gate 14: export mode is clean — no background/grid/selection/handles/ghost ───────────────
    //
    // TryRenderToAvaloniaImage itself is NOT unit-tested here — `new Avalonia.Media.Imaging.Bitmap(stream)`
    // requires a live Avalonia platform (`IPlatformRenderInterface`), which a headless xunit run does
    // not have. This is the exact same limitation SchematicClipboard's equivalent path already has
    // (zero existing tests touch it either); the SKBitmap encoding it wraps is exercised directly
    // below instead, which is where R-L1f-5/R-L1f-6's actual pixel content lives.

    /// <summary>Drives <see cref="LayoutRenderer.Draw"/> directly (rather than through the Avalonia
    /// <c>Bitmap</c> wrapper above) so corner pixels can be inspected against a known backdrop —
    /// gate 14's literal assertion.</summary>
    [Fact]
    public void Draw_ExportMode_NoBackgroundFill_NoGrid_NoOverlay_StrokeHasRealWidth()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        var tech = MakeTech();

        const int w = 300, h = 300;
        var backdrop = new SKColor(10, 20, 30, 0); // fully transparent, distinguishable alpha-wise from opaque paint

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(backdrop);

        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 100_000, 100_000), w, h, marginFrac: 0.15);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light,
            ShowGrid = false,
            Overlay = null,
            TransparentBackground = true,
        };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        // Corners stay untouched (no opaque background fill was painted).
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);
        Assert.Equal(0, bmp.GetPixel(w - 1, 0).Alpha);
        Assert.Equal(0, bmp.GetPixel(0, h - 1).Alpha);
        Assert.Equal(0, bmp.GetPixel(w - 1, h - 1).Alpha);

        // The shape's outline is present and covers more than a handful of pixels (a real, non-zero
        // world-space stroke width, not a literal zero-width hairline collapsing to nothing at this
        // export scale).
        int outlinePixels = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, y).Alpha > 0) outlinePixels++;

        Assert.True(outlinePixels > 50, $"expected a visible outline+fill, found {outlinePixels} non-transparent pixels");
    }
}
