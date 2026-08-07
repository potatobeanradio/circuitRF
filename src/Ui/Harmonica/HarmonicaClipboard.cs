using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// §7.6's <i>Copy Plot</i>, for a harmonicaRF panel.
///
/// <para><b>R-h8-11 — the exporter is NOT reinvented.</b> The `.cdd` path copies a
/// <c>PlotContainerViewModel</c>, and harmonicaRF has none: its panels are one Skia surface drawn by
/// <c>HarmonicaPanelRenderer</c>. What is genuinely shared is the part that is hard and platform-
/// specific — putting one picture on the clipboard as PDF, SVG, a raster and text at once, with the
/// Windows bypass and the text fallback — so this renders the page and hands the bytes to
/// <see cref="PlotExporter.SetClipboardDataAsync"/>. The page size is <c>PlotExporter</c>'s own, so a
/// harmonicaRF panel pastes at the same size as a Data Display plot.</para>
///
/// <para><b>Which panel: the one under the pointer.</b> Stated because R-h8-11 asks. The whole canvas
/// was the alternative and is worse for the thing people actually do — a Smith chart or a loadline
/// goes into a report on its own, and a copy of all five panels at page size renders each of them too
/// small to read. The pointer already resolves to a panel for Edit Display's own delete gesture
/// (<c>HarmonicaEditTarget</c>), so "under the pointer" needs no new hit test and cannot disagree with
/// the one the user already sees respond. With the pointer outside every panel it falls back to the
/// whole canvas, which is the only other answer that is never wrong.</para>
/// </summary>
public static class HarmonicaClipboard
{
    /// <summary>The JSON a Data Display would carry. harmonicaRF has no plot config to serialise, so
    /// the text flavour is the readouts — which is what a paste into a text field should give.</summary>
    private static string TextFor(HarmonicaViewModel vm)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (label, value, _) in vm.Frame.Readouts)
            sb.Append(label).Append('\t').AppendLine(value);
        return sb.ToString();
    }

    /// <summary>
    /// Copies one panel (or the whole canvas when <paramref name="panelId"/> is null) to the
    /// clipboard. Returns the panel's own display name, for the status strip.
    /// </summary>
    public static async Task<string> CopyAsync(Control anchor, HarmonicaViewModel vm, string? panelId)
    {
        var snap = HarmonicaCanvasRenderer.Snapshot.Of(vm);

        void Render(SKCanvas canvas)
        {
            HarmonicaCanvasRenderer.FillBackground(canvas, PlotExporter.PageW, PlotExporter.PageH,
                                                   snap.Theme);
            if (panelId is null)
                HarmonicaCanvasRenderer.DrawAll(canvas, PlotExporter.PageW, PlotExporter.PageH, snap);
            else
                HarmonicaCanvasRenderer.DrawPanel(canvas, PlotExporter.PageW, PlotExporter.PageH,
                                                  panelId, snap);
        }

        byte[] pdf = PlotExporter.BuildPdfBytes(Render);
        string svg = PlotExporter.BuildSvgString(Render);
        Bitmap? bmp = BuildBitmap(Render);

        await PlotExporter.SetClipboardDataAsync(anchor, pdf, svg, TextFor(vm), bmp);

        return panelId is null ? "the whole canvas"
                               : HarmonicaCanvasRenderer.DisplayName(panelId, snap);
    }

    /// <summary>A 2× raster, for applications that take a bitmap and nothing richer. Null on failure —
    /// the other three formats still go, so a raster nobody could encode is not fatal.</summary>
    private static Bitmap? BuildBitmap(Action<SKCanvas> render)
    {
        try
        {
            const float scale = 2.0f;
            int w = (int)(PlotExporter.PageW * scale);
            int h = (int)(PlotExporter.PageH * scale);

            using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale(scale, scale);
            render(canvas);

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) return null;
            using var ms = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
