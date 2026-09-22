// ================================================================
//  MarkerGlyphTests.cs — the per-point trace symbol: shape, file, picker.
//
//  Owner request, 2026-09-16: the two shapes a trace could draw its samples with (circle, square)
//  became fourteen. Three things had to hold together for that, and each is one test here:
//
//    • the RENDERER draws a different shape for each one, and still draws the original two exactly
//      as it did — a `.cdd` saved before this existed must come back pixel for pixel;
//    • the `.cdd` carries the choice, by NAME, for every member;
//    • the trace card's picker OFFERS every member, drawn from the renderer's own outline rather
//      than from a look-alike icon.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class MarkerGlyphTests
{
    private const int    W = 120, H = 120;
    private const double MarkerSize = 5.0;

    private static readonly MarkerType[] All = Enum.GetValues<MarkerType>();

    /// <summary>One point, dead centre, drawn large — the glyph and nothing else.</summary>
    private static SKBitmap RenderOne(MarkerType shape)
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
        t.Points.Add(new Vector2(0f, 0f));
        t.Properties.LineEnabled   = false;
        t.Properties.MarkerEnabled = true;
        t.Properties.MarkerSize    = MarkerSize;
        t.Properties.MarkerType    = shape;

        // Identity scale with the one point landing on the canvas centre.
        var map = (XScale: 1.0, YScale: 1.0, XOffset: W / 2.0, YOffset: H / 2.0);
        var tf  = new TransformSet { Primary = map, Secondary = map, CanvasSize = (W, H) };

        var bmp = new SKBitmap(W, H);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        TraceRenderer.Draw(canvas, (W, H), t, tf, RenderTheme.Light);
        return bmp;
    }

    /// <summary>
    /// <b>Every shape draws, and no two draw the same picture.</b> The failure this catches is the
    /// quiet one: a switch that falls through to the circle leaves the trace card offering shapes
    /// the plot does not honour, and the plot looks perfectly fine while it happens.
    /// </summary>
    [Fact]
    public void EveryMarkerTypeDrawsItsOwnShape()
    {
        var seen = new Dictionary<string, MarkerType>();

        foreach (var shape in All)
        {
            using var bmp = RenderOne(shape);

            int ink = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (bmp.GetPixel(x, y) != SKColors.White) ink++;
            Assert.True(ink > 20, $"{shape} drew nothing");

            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bmp.Bytes));
            Assert.False(seen.TryGetValue(key, out var twin),
                         $"{shape} and {twin} draw the identical picture");
            seen[key] = shape;
        }
    }

    /// <summary>
    /// <b>Circle and Square are unchanged.</b> They are the two shapes that existed before the
    /// rest, so every `.cdd` in the wild names one of them; compared against the oval/rect calls
    /// the renderer made before the glyph table, pixel for pixel.
    /// </summary>
    [Theory]
    [InlineData(MarkerType.Circle)]
    [InlineData(MarkerType.Square)]
    public void TheTwoOriginalShapesRenderExactlyAsBefore(MarkerType shape)
    {
        using var actual = RenderOne(shape);

        // The pre-change arithmetic, kept as its own reference rather than derived from the code
        // under test: radius = min(W,H)/200 * MarkerSize, centred on the point.
        float ms = (float)(Math.Min(W, H) / 200.0) * (float)MarkerSize;
        var   c  = new SKPoint(W / 2f, H / 2f);
        var   r  = new SKRect(c.X - ms, c.Y - ms, c.X + ms, c.Y + ms);

        using var expected = new SKBitmap(W, H);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.White);
            using var fill = new SKPaint
            {
                Color       = RenderTheme.ToSKColor(new TraceProperties().MarkerColor, 1.0),
                Style       = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var stroke = new SKPaint
            {
                Color       = new SKColor(0, 0, 0, 200),
                StrokeWidth = (float)(Math.Min(W, H) / 200.0) / 2f,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            if (shape == MarkerType.Square) { canvas.DrawRect(r, fill); canvas.DrawRect(r, stroke); }
            else                            { canvas.DrawOval(r, fill); canvas.DrawOval(r, stroke); }
        }

        Assert.True(actual.Bytes.AsSpan().SequenceEqual(expected.Bytes),
                    $"{shape} no longer draws what a saved .cdd was written against");
    }

    /// <summary>
    /// <b>The `.cdd` carries every shape, by name.</b> The config's enum converter writes the
    /// member name, which is why adding members loads old files unchanged — and why renaming one
    /// would silently reset every trace that used it to Circle.
    /// </summary>
    [Fact]
    public void EveryMarkerTypeRoundTripsThroughTheConfigByName()
    {
        foreach (var shape in All)
        {
            var cfg  = new TracePropertiesConfig { MarkerType = shape };
            string json = JsonSerializer.Serialize(cfg, DataDisplayViewModel.JsonOpts);

            Assert.Contains($"\"{shape}\"", json);

            var back = JsonSerializer.Deserialize<TracePropertiesConfig>(json, DataDisplayViewModel.JsonOpts);
            Assert.NotNull(back);
            Assert.Equal(shape, back!.MarkerType);

            // And the loader's own copy, which is what a real open goes through.
            var props = new TraceProperties();
            PlotConfigLoader.ApplyProperties(back, props);
            Assert.Equal(shape, props.MarkerType);
        }
    }

    /// <summary>
    /// <b>The picker offers every shape, drawn from the renderer's own outline.</b> A shape that
    /// exists in the enum and in the file format but not in the trace card is a shape nobody can
    /// choose; a picker icon sourced from somewhere else is one that can disagree with the plot.
    /// </summary>
    [Fact]
    public void ThePickerOffersEveryShapeAndDrawsTheRenderersOutline()
    {
        var offered = PlotInspectorViewModel.SymbolModes;

        Assert.Single(offered, m => m.IsOff);
        Assert.Equal(All, offered.Where(m => !m.IsOff).Select(m => m.Shape).ToArray());
        Assert.Equal(All, PlotInspectorViewModel.AllMarkerTypes.Select(m => m.Value).ToArray());

        foreach (var item in offered.Where(m => !m.IsOff))
        {
            // The picker's icon is MarkerGlyph's own path data — the same outline the Skia path is
            // built from — rather than a look-alike icon chosen per shape. Asserted as the STRING,
            // because parsing it needs Avalonia's render interface and this test has no display.
            Assert.Equal(MarkerGlyph.SvgPath(item.Shape, MarkerTypeItem.IconRadius), item.GlyphData);
            Assert.False(string.IsNullOrWhiteSpace(item.GlyphData), $"{item.Shape} has no outline");

            // And it is well-formed path data describing the shape the plot draws: parsed here by
            // Skia, which reads the same M/L/A/Z subset Avalonia's parser does, and compared
            // against the path the renderer builds from the vertex table.
            using var parsed = SKPath.ParseSvgPathData(item.GlyphData);
            Assert.NotNull(parsed);
            using var built = MarkerGlyph.BuildPath(item.Shape, (float)MarkerTypeItem.IconRadius);
            var (a, b) = (parsed.Bounds, built.Bounds);
            Assert.True(Math.Abs(a.Width - b.Width) < 0.05 && Math.Abs(a.Height - b.Height) < 0.05,
                        $"{item.Shape}: picker {a.Width}x{a.Height} vs plot {b.Width}x{b.Height}");
        }

        // The Off row has no glyph of its own — the template draws its own struck-through icon.
        Assert.Null(offered.First(m => m.IsOff).GlyphData);
    }
}
