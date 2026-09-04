using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.WBond;
using SkiaSharp;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Copies a wBond design to the system clipboard as a GRAPHIC — PDF, SVG and bitmap at once
/// (wbond.md §6.7, "to other applications").
///
/// <para><b>Nothing here is new plumbing.</b> The page is composed from the two renderers that
/// already draw this design on screen — <see cref="LayoutRenderer"/> for the reference geometry and
/// <see cref="WBondRenderer"/> for the wires — and the clipboard write is
/// <see cref="PlotExporter.SetClipboardDataAsync"/>, the same multi-format path (with its Windows
/// bypass and text fallback) the schematic, layout and data display already use. §6.7 says outright
/// that this is tricky and already solved, and that wBond reuses it rather than re-solving it.</para>
///
/// <para>The wires are drawn through the SAME <see cref="WBondRenderer.Draw"/> the canvas calls, so a
/// copied graphic cannot disagree with what is on screen about thickness, colour or the nm→DBU
/// bridge.</para>
/// </summary>
internal static class WBondGraphicExport
{
    /// <summary>Fraction of the page left as margin on every side.</summary>
    private const float MarginFraction = 0.06f;

    /// <summary>2× the page, matching <c>PlotExporter</c>'s own bitmap scale.</summary>
    private const float BitmapScale = 2.0f;

    /// <summary>
    /// Renders the design onto <paramref name="canvas"/>, framed to the page.
    ///
    /// <para>Framing comes from the WIRES plus the reference layout's own extent, so a design whose
    /// wires sit in one corner of a large board still exports both — the alternative (frame the wires
    /// alone) silently crops away the geometry a reader needs to make sense of them.</para>
    /// </summary>
    internal static void Render(
        SKCanvas canvas,
        WBondDesign design,
        LayoutView? layout,
        Technology? technology,
        string? instanceBaseDir,
        WBondRenderTheme wireTheme,
        LayoutRenderTheme layoutTheme,
        WireThicknessMode thickness,
        float pageWidth,
        float pageHeight,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(design);

        var vp = FitViewport(design, layout, pageWidth, pageHeight, dbuPerMicron);

        var layoutOpts = new LayoutRenderOptions
        {
            Theme = layoutTheme,
            ShowGrid = false,             // an export is artwork, not an editing surface
            Overlay = null,               // no selection, no handles, no ghosts
            DetailPixelThreshold = -1,    // exact stored geometry — see LayoutClipboard.ExportOptions
            TransparentBackground = true, // the destination document supplies the background
            BaseDir = instanceBaseDir,
        };

        if (layout is not null)
            LayoutRenderer.Draw(canvas, layout, technology, vp, layoutOpts with { DeferRulers = true });

        WBondRenderer.Draw(canvas, design, vp, wireTheme, selection: null,
                           thickness: thickness, dbuPerMicron: dbuPerMicron);

            // §9B.9 exports rulers on the presentation path, and the wires below are painted after
            // this call — so without the deferral the annotation lands UNDER the wire it measures,
            // exactly as it did on the interactive canvas. See LayoutRenderOptions.DeferRulers.
        LayoutRenderer.DrawRulersOnTop(canvas, layout, vp, layoutOpts);
    }

    /// <summary>
    /// Frames the wires and the reference layout together, centred on the page with a margin.
    /// Falls back to a small, sane span when there is nothing to measure, so an empty design exports
    /// a blank page rather than dividing by zero.
    /// </summary>
    internal static LayoutViewport FitViewport(
        WBondDesign design, LayoutView? layout, float pageWidth, float pageHeight, int dbuPerMicron)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var wire in design.AllWires())
        {
            foreach (var p in wire.Points)
            {
                double x = WBondSnap.ToDbu(p.X, dbuPerMicron);
                double y = WBondSnap.ToDbu(p.Y, dbuPerMicron);
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (layout is not null)
        {
            foreach (var shape in layout.Shapes)
            {
                var bb = LayoutGeometry.BboxOf(shape);
                if (bb.MinX < minX) minX = bb.MinX;
                if (bb.MinY < minY) minY = bb.MinY;
                if (bb.MaxX > maxX) maxX = bb.MaxX;
                if (bb.MaxY > maxY) maxY = bb.MaxY;
            }
        }

        if (minX > maxX || minY > maxY)
        {
            // Nothing to frame — a blank page at an arbitrary but valid scale.
            return new LayoutViewport(0, 0, 1.0, pageWidth, pageHeight);
        }

        double w = Math.Max(maxX - minX, 1.0);
        double h = Math.Max(maxY - minY, 1.0);

        float usableW = pageWidth * (1f - 2f * MarginFraction);
        float usableH = pageHeight * (1f - 2f * MarginFraction);

        double zoom = Math.Min(usableW / w, usableH / h);

        // Centre: the world point at the page centre must be the design's own centre.
        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;
        double panX = cx - pageWidth * 0.5 / zoom;
        double panY = cy - pageHeight * 0.5 / zoom;

        return new LayoutViewport(panX, panY, zoom, pageWidth, pageHeight);
    }

    /// <summary>
    /// Renders the design and places PDF, SVG and a 2× bitmap on the clipboard together, richest
    /// first — the receiving application picks whichever it understands.
    /// </summary>
    internal static async Task CopyToClipboardAsync(
        Control anchor,
        WBondDesign design,
        LayoutView? layout,
        Technology? technology,
        string? instanceBaseDir,
        WBondRenderTheme wireTheme,
        LayoutRenderTheme layoutTheme,
        WireThicknessMode thickness,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(design);

        void Compose(SKCanvas c) => Render(
            c, design, layout, technology, instanceBaseDir,
            wireTheme, layoutTheme, thickness,
            PlotExporter.PageW, PlotExporter.PageH, dbuPerMicron);

        byte[] pdf = PlotExporter.BuildPdfBytes(Compose);
        string svg = PlotExporter.BuildSvgString(Compose);
        var bitmap = BuildBitmap(Compose);

        await PlotExporter.SetClipboardDataAsync(anchor, pdf, svg, json: string.Empty, bitmap);
    }

    /// <summary>
    /// A 2× raster for applications that recognise <c>DataFormat.Bitmap</c> but not the
    /// application-scoped PDF format. Returns null if rendering fails — the PDF and SVG still go.
    /// </summary>
    private static Avalonia.Media.Imaging.Bitmap? BuildBitmap(Action<SKCanvas> compose)
    {
        try
        {
            int w = (int)(PlotExporter.PageW * BitmapScale);
            int h = (int)(PlotExporter.PageH * BitmapScale);

            using var skBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(skBitmap);
            canvas.Scale(BitmapScale, BitmapScale);
            compose(canvas);

            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) return null;

            using var ms = new System.IO.MemoryStream(data.ToArray());
            return new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch
        {
            return null;   // best effort — never let a raster failure cost the vector formats
        }
    }
}
