using System;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Zoomed far enough out, a symbol is stood in for by a filled rectangle. <b>How much of the canvas
/// that leaves occupied has to follow the symbol, not a fixed nominal size.</b>
///
/// <para><b>Owner report: an imported kit's part stopped rendering when zoomed out, while everything
/// around it stayed perfectly legible.</b> Both the decision to substitute and the rectangle drawn
/// came from a fixed nominal 300 x 100 world units — right for a built-in, an order of magnitude
/// wrong for a kit symbol. One measured real part is 3,275 x 3,375 units, so at the zoom where the
/// substitution switched on it was still 65 pixels across and was replaced by a 4-pixel speck.
/// Nothing errored; the part simply looked absent.</para>
///
/// <para>The oracle is the extent of the PAINTED PIXELS off a real render, not the renderer's own
/// arithmetic — a test that recomputed the rectangle would agree with whatever rectangle the
/// renderer chose, including the wrong one.</para>
/// </summary>
public sealed class SchematicLodGlyphSizeTests
{
    private const int CanvasPx = 400;

    /// <summary>Below <c>SchematicRenderer</c>'s LOD threshold (nominal width 300 x zoom &lt; 6).</summary>
    private const double LodZoom = 0.015;

    /// <summary>A component whose glyph spans <paramref name="span"/> world units square, centred on
    /// the origin, drawn as one rectangle outline — the simplest thing with a real extent.</summary>
    private static SchematicComponent ComponentSpanning(double span)
    {
        double half = span / 2;
        return new SchematicComponent
        {
            Id = "X1", InstanceName = "X1", Symbol = SymbolKind.Generic,
            X = 0, Y = 0,
            CellRefState = CellSymbolState.Resolved,
            CellRefPrimitives = [new RectPrimitive { Cx = 0, Cy = 0, W = span, H = span }],
            BbMinX = -half, BbMinY = -half, BbMaxX = half, BbMaxY = half,
            GlyphBbMinX = -half, GlyphBbMinY = -half, GlyphBbMaxX = half, GlyphBbMaxY = half,
            FullBbMinX = -half, FullBbMinY = -half, FullBbMaxX = half, FullBbMaxY = half,
        };
    }

    /// <summary>How wide, in pixels, the not-background region is — what a user reads as "how big
    /// the thing on screen is". Zero when nothing was painted at all.</summary>
    private static int PaintedWidthPx(double span, double zoom)
    {
        var theme = SchematicRenderTheme.Light;
        var model = new SchematicModel { Components = [ComponentSpanning(span)] };

        using var surface = SKSurface.Create(new SKImageInfo(CanvasPx, CanvasPx));
        // Centred on the component, and the grid excluded so the measurement is the symbol alone.
        SchematicRenderer.Draw(
            surface.Canvas, (CanvasPx, CanvasPx), model, index: null,
            panX: -CanvasPx / (2 * zoom), panY: -CanvasPx / (2 * zoom), zoom: zoom,
            theme: theme, overlay: null, useTransparentBackground: false, excludeGrid: true);

        using var image = surface.Snapshot();
        using var bmp   = SKBitmap.FromImage(image);

        int minX = int.MaxValue, maxX = int.MinValue;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width;  x++)
            if (bmp.GetPixel(x, y) != theme.Background)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
            }

        return maxX < minX ? 0 : maxX - minX + 1;
    }

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A kit-sized symbol occupies the screen space its own artwork covers. Under the old rule both
    /// sizes below produced the SAME few pixels, which is the whole defect: the part was there and
    /// looked as though it was not.
    /// </summary>
    [Fact]
    public void ALargeSymbolOccupiesItsOwnScreenSizeRatherThanTheBuiltInNominalOne()
    {
        const double span = 3000;                       // 45 px across at this zoom
        int width = PaintedWidthPx(span, LodZoom);

        // Deliberately loose on the low side (a stroke's own width and anti-aliasing both count),
        // and stated as a fraction of what the symbol actually spans rather than as a pixel number.
        Assert.True(width >= span * LodZoom * 0.8,
            $"a symbol spanning {span * LodZoom:F0} px on screen was drawn {width} px wide.");

        // And it is genuinely bigger than a built-in's, which is the comparison the defect failed.
        Assert.True(width > PaintedWidthPx(span: 300, zoom: LodZoom) * 4);
    }

    /// <summary>The built-in case is unchanged: artwork genuinely too small to read is still stood in
    /// for by a solid mark rather than drawn, which is what the substitution is for.</summary>
    [Fact]
    public void ASymbolTooSmallToReadIsStillReplacedBySolidMark()
    {
        // 300 units at this zoom is 4.5 px — under the threshold.
        int width = PaintedWidthPx(span: 300, zoom: LodZoom);
        Assert.InRange(width, 1, 8);
    }

    /// <summary>Nothing disappears entirely across the size range a kit spans. The failure being
    /// fixed is a part that renders nothing, so the floor is worth stating separately from the size.
    ///
    /// <para>Bounded above by what the canvas can hold: an outline wider than the viewport has its
    /// edges off-screen and paints nothing, which is ordinary and is not this test's subject.</para>
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    [InlineData(3000)]
    [InlineData(10000)]
    public void ASymbolIsNeverPaintedAsNothing(double span)
        => Assert.True(PaintedWidthPx(span, LodZoom) > 0);
}
