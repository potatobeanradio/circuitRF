// ================================================================
//  PlotExporter.cs  —  PDF / SVG export for a single plot
//
//  Paper: 8.5" × 11" landscape = 792 × 612 pts at 72 pts/in.
//  Margins: 0.5" (36 pts) each side → usable area 720 × 540 pts.
//
//  Layout strategy — bounding-box fit:
//    All positioned objects (plot canvas, axis label strips, marker
//    info boxes) share the DataDisplay screen-pixel coordinate space
//    via their View* properties.  The export:
//      1. Collects all objects' screen-space rectangles.
//      2. Computes their union (bounding box).
//      3. Derives a single uniform scale S = min(usableW, usableH)
//         relative to the bounding-box dimensions, then centres the
//         result on the page.
//      4. Positions every object with the same linear mapping:
//           page_coord = Margin + pad + (screen_coord − bndOrigin) × S
//
//    This guarantees that the relative position of any info box to
//    the plot axes is identical in the export and on screen, regardless
//    of zoom level, aspect ratio, or whether the box was dragged
//    outside the plot area.
//
//  Rendering order:
//    1. Background fill.
//    2. Axis label strips (Smith/Polar only).
//    3. PlotRenderer.Draw — grid, traces, axis labels, marker symbols.
//    4. Marker info boxes.
// ================================================================

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.DataDisplay
{
    public static class PlotExporter
    {
        // ---- Paper constants (points at 72 DPI, landscape) -----------------

        private const float PageW  = 792f;   // 11" × 72
        private const float PageH  = 612f;   // 8.5" × 72
        private const float Margin = 36f;    // 0.5" × 72

        // ---- Public entry point --------------------------------------------

        /// <summary>
        /// Shows a save file dialog anchored to <paramref name="anchor"/>, then
        /// exports the plot to the chosen PDF or SVG path.
        /// The entire composition (plot, label strips, marker info boxes) is
        /// scaled uniformly to fill the usable page area, preserving the
        /// relative position of every object exactly as seen on screen.
        /// </summary>
        public static async Task ExportAsync(
            Control                 anchor,
            Plot                    plot,
            RenderTheme             theme,
            bool                    showFilePrefix,
            PlotContainerViewModel? container)
        {
            var topLevel = TopLevel.GetTopLevel(anchor);
            if (topLevel is null) return;

            // ---- File picker --------------------------------------------

            var fileTypeChoices = new List<FilePickerFileType>
            {
                new FilePickerFileType("PDF Document")
                {
                    Patterns  = ["*.pdf"],
                    MimeTypes = ["application/pdf"]
                },
                new FilePickerFileType("SVG Image")
                {
                    Patterns  = ["*.svg"],
                    MimeTypes = ["image/svg+xml"]
                },
            };

            // Tab-delimited text export is only meaningful for Table plots.
            if (plot.PlotType == PlotType.Table)
            {
                fileTypeChoices.Add(new FilePickerFileType("Tab-Delimited Text")
                {
                    Patterns  = ["*.txt"],
                    MimeTypes = ["text/plain"]
                });
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title             = "Export Plot",
                    SuggestedFileName = string.IsNullOrWhiteSpace(plot.Title) ? "plot" : plot.Title,
                    FileTypeChoices   = fileTypeChoices,
                });

            if (file is null) return;   // user cancelled

            string path   = file.Path.LocalPath;
            bool   isPdf  = path.EndsWith(".pdf",  StringComparison.OrdinalIgnoreCase);
            bool   isTsv  = path.EndsWith(".txt",  StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".tsv",  StringComparison.OrdinalIgnoreCase);

            // ---- Usable area -------------------------------------------

            float usableW = PageW - 2f * Margin;   // 720 pts
            float usableH = PageH - 2f * Margin;   // 540 pts

            // Pre-gather info boxes once (they live on the UI thread).
            IReadOnlyList<MarkerInfoBoxViewModel> markerBoxes =
                container?.GetMarkerInfoBoxes() ?? Array.Empty<MarkerInfoBoxViewModel>();

            float plotX, plotY, plotW, plotH, stripW, exportScale;

            if (container is not null)
            {
                int    nLeft  = container.LeftLabelStrips.Count;
                int    nRight = container.RightLabelStrips.Count;
                double sw     = container.LabelStripViewWidth;

                double bndL = container.ViewLeft  - nLeft  * sw;
                double bndT = container.ViewTop;
                double bndR = container.ViewLeft  + container.ViewWidth + nRight * sw;
                double bndB = container.ViewTop   + container.ViewHeight;

                foreach (var boxVm in markerBoxes)
                {
                    bndL = Math.Min(bndL, boxVm.ViewLeft);
                    bndT = Math.Min(bndT, boxVm.ViewTop);
                    bndR = Math.Max(bndR, boxVm.ViewLeft + boxVm.BoxWidth);
                    bndB = Math.Max(bndB, boxVm.ViewTop  + boxVm.BoxHeight);
                }

                double bndW = bndR - bndL;
                double bndH = bndB - bndT;

                exportScale = (bndW > 0 && bndH > 0)
                    ? (float)Math.Min(usableW / bndW, usableH / bndH)
                    : 1f;

                float padX = (usableW - (float)bndW * exportScale) / 2f;
                float padY = (usableH - (float)bndH * exportScale) / 2f;

                plotX  = Margin + padX + (float)(container.ViewLeft - bndL) * exportScale;
                plotY  = Margin + padY + (float)(container.ViewTop  - bndT) * exportScale;
                plotW  = (float)container.ViewWidth  * exportScale;
                plotH  = (float)container.ViewHeight * exportScale;
                stripW = (float)sw * exportScale;
            }
            else
            {
                exportScale = 1f;
                stripW      = 0f;
                plotX = Margin;
                plotY = Margin;
                plotW = usableW;
                plotH = usableH;
            }

            bool hasStrips = (container?.LeftLabelStrips.Count ?? 0) +
                             (container?.RightLabelStrips.Count ?? 0) > 0;

            // ---- Draw callback -----------------------------------------

            void Render(SKCanvas canvas)
            {
                var appSettings    = AppSettingsViewModel.Instance;
                var effectiveTheme = appSettings.GetExportRenderTheme(theme);
                theme = effectiveTheme;

                canvas.Clear(appSettings.ExportTransparentBackground
                    ? SKColors.Transparent
                    : theme.BackgroundColor);

                if (container is not null && hasStrips)
                {
                    var tf      = PlotRenderer.BuildTransforms(plot, (plotW, plotH));
                    float vpTop    = (float)(tf.Viewport.Y * plotH);
                    float vpBottom = (float)((tf.Viewport.Y + tf.Viewport.Height) * plotH);
                    float chartH   = vpBottom - vpTop;
                    float chartY   = plotY + vpTop;

                    int nLeft  = container.LeftLabelStrips.Count;
                    int nRight = container.RightLabelStrips.Count;

                    for (int i = 0; i < nLeft; i++)
                    {
                        var strip = container.LeftLabelStrips[i];
                        float sx  = plotX - (i + 1) * stripW;
                        DrawAxisLabelStrip(canvas, sx, chartY, stripW, chartH,
                            strip.Trace, false, theme, strip.CustomLabel, strip.ShowFilePrefix);
                    }

                    for (int i = 0; i < nRight; i++)
                    {
                        var strip = container.RightLabelStrips[i];
                        float sx  = plotX + plotW + i * stripW;
                        DrawAxisLabelStrip(canvas, sx, chartY, stripW, chartH,
                            strip.Trace, true, theme, strip.CustomLabel, strip.ShowFilePrefix);
                    }
                }

                float tableZoom = (plot.PlotType == PlotType.Table && container is not null)
                    ? plotW / (float)container.Width
                    : 1f;

                canvas.Save();
                canvas.Translate(plotX, plotY);
                PlotRenderer.Draw(canvas, (plotW, plotH), plot, PlotDetail.Full, theme, showFilePrefix,
                    zoomLevel: tableZoom,
                    aliasFor: container?.Library is { } exLib ? t => exLib.AliasFor(t.EffectiveSourcePath) : null,
                    alwaysShowSource: AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                        container?.Library?.HasMultipleSources ?? false));
                canvas.Restore();

                if (container is not null && markerBoxes.Count > 0)
                {
                    foreach (var boxVm in markerBoxes)
                    {
                        float bx = plotX + (float)(boxVm.ViewLeft - container.ViewLeft) * exportScale;
                        float by = plotY + (float)(boxVm.ViewTop  - container.ViewTop)  * exportScale;
                        float bw = (float)boxVm.BoxWidth  * exportScale;
                        float bh = (float)boxVm.BoxHeight * exportScale;

                        canvas.Save();
                        canvas.Translate(bx, by);
                        MarkerRenderer.DrawInfoBox(
                            canvas, (bw, bh),
                            boxVm.Marker, boxVm.Trace, boxVm.FreqUnit,
                            theme, showFilePrefix,
                            transparentBackground: appSettings.MarkerBoxTransparentBackground,
                            otherTraces: boxVm.Marker.IsMulti ? boxVm.OtherTraces : null);
                        canvas.Restore();
                    }
                }
            }

            // ---- Write file --------------------------------------------

            try
            {
                if (isTsv)
                    WriteTabText(path, plot, showFilePrefix);
                else if (isPdf)
                    WritePdf(path, Render);
                else
                    WriteSvg(path, Render);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlotExporter] Export failed: {ex}");
            }
        }

        // ================================================================
        //  Clipboard entry points
        // ================================================================

        /// <summary>
        /// Renders <paramref name="containers"/> to PDF, SVG, raster bitmap, and JSON, then
        /// writes all formats to the system clipboard so the receiving application (Keynote,
        /// Pages, Word, PowerPoint) can paste the richest representation it understands.
        /// Plain-text JSON is always present as the circuitRF Paste fallback.
        /// </summary>
        internal static async Task CopyContainersToClipboardAsync(
            Control                               anchor,
            IReadOnlyList<PlotContainerViewModel> containers,
            RenderTheme                           theme)
        {
            byte[]  pdf    = BuildPdfBytesForContainers(containers, theme);
            string  svg    = BuildSvgStringForContainers(containers, theme);
            string  json   = BuildContainersJson(containers);
            Bitmap? bitmap = BuildBitmapForContainers(containers, theme);
            await SetClipboardDataAsync(anchor, pdf, svg, json, bitmap);
        }

        /// <summary>
        /// Renders the plot (label strips + markers) to PDF bytes, SVG text, and a
        /// JSON DataDisplay config string, then places all three formats on the
        /// system clipboard so the receiving application can pick the richest one.
        /// </summary>
        public static async Task CopyPlotToClipboardAsync(
            Control                 anchor,
            Plot                    plot,
            RenderTheme             theme,
            bool                    showFilePrefix,
            PlotContainerViewModel? container)
        {
            if (container is null) return;

            var    containers = (IReadOnlyList<PlotContainerViewModel>)new[] { container };
            byte[] pdf    = BuildPdfBytesForContainers(containers, theme);
            string svg    = BuildSvgStringForContainers(containers, theme);
            string json   = BuildContainersJson(containers);
            Bitmap? bitmap = BuildBitmapForContainers(containers, theme);

            await SetClipboardDataAsync(anchor, pdf, svg, json, bitmap);
        }

        /// <summary>Renders all plots in <paramref name="containers"/> to a PDF byte array.</summary>
        internal static byte[] BuildPdfBytesForContainers(
            IReadOnlyList<PlotContainerViewModel> containers, RenderTheme theme)
            => BuildPdfBytes(canvas => RenderContainersToCanvas(canvas, containers, theme));

        /// <summary>Renders all plots in <paramref name="containers"/> to an SVG string.</summary>
        internal static string BuildSvgStringForContainers(
            IReadOnlyList<PlotContainerViewModel> containers, RenderTheme theme)
            => BuildSvgString(canvas => RenderContainersToCanvas(canvas, containers, theme));

        /// <summary>
        /// Renders all plots to a high-resolution raster bitmap (2× page size) suitable
        /// for pasting into apps such as Keynote, Pages, and Word that recognise
        /// <see cref="DataFormat.Bitmap"/> but not the circuitRF application-scoped PDF format.
        /// Returns null if rendering fails.
        /// </summary>
        internal static Bitmap? BuildBitmapForContainers(
            IReadOnlyList<PlotContainerViewModel> containers, RenderTheme theme)
        {
            const float Scale = 2.0f;
            int w = (int)(PageW * Scale);
            int h = (int)(PageH * Scale);

            using var skBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas   = new SKCanvas(skBitmap);
            canvas.Scale(Scale, Scale);
            RenderContainersToCanvas(canvas, containers, theme);

            using var skData = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (skData is null) return null;
            using var ms = new MemoryStream(skData.ToArray());
            return new Bitmap(ms);
        }

        /// <summary>
        /// Builds a JSON DataDisplayConfig string from the given containers.
        /// Source paths are kept absolute (no config-dir relativization) so the JSON
        /// is portable as clipboard text without knowing a base directory.
        /// </summary>
        internal static string BuildContainersJson(IReadOnlyList<PlotContainerViewModel> containers)
        {
            var config = new DataDisplayConfig { ZoomLevel = 1.0 };
            foreach (var c in containers)
                config.Plots.Add(DataDisplayViewModel.BuildPlotContainerConfig(c, ""));
            return JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        }

        // ---- In-memory PDF / SVG builders ---------------------------

        private static byte[] BuildPdfBytes(Action<SKCanvas> render)
        {
            var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
            using var skStream = new SKDynamicMemoryWStream();
            using var doc      = SKDocument.CreatePdf(skStream, metadata);
            var canvas = doc.BeginPage(PageW, PageH);
            render(canvas);
            doc.EndPage();
            doc.Close();
            return skStream.DetachAsData().ToArray();
        }

        private static string BuildSvgString(Action<SKCanvas> render)
        {
            using var skStream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, PageW, PageH), skStream))
                render(canvas);
            return Encoding.UTF8.GetString(skStream.DetachAsData().ToArray());
        }

        // ---- Multi-plot renderer ------------------------------------

        private static void RenderContainersToCanvas(
            SKCanvas                              canvas,
            IReadOnlyList<PlotContainerViewModel> containers,
            RenderTheme                           theme)
        {
            float usableW = PageW - 2f * Margin;
            float usableH = PageH - 2f * Margin;

            var appSettings = AppSettingsViewModel.Instance;
            theme = appSettings.GetExportRenderTheme(theme);

            canvas.Clear(appSettings.ExportTransparentBackground
                ? SKColors.Transparent
                : theme.BackgroundColor);
            if (containers.Count == 0) return;

            // ---- Bounding box in DataDisplay screen-pixel space --------

            double bndL = double.PositiveInfinity;
            double bndT = double.PositiveInfinity;
            double bndR = double.NegativeInfinity;
            double bndB = double.NegativeInfinity;

            foreach (var c in containers)
            {
                double sw     = c.LabelStripViewWidth;
                int    nLeft  = c.LeftLabelStrips.Count;
                int    nRight = c.RightLabelStrips.Count;

                bndL = Math.Min(bndL, c.ViewLeft - nLeft  * sw);
                bndT = Math.Min(bndT, c.ViewTop);
                bndR = Math.Max(bndR, c.ViewLeft + c.ViewWidth + nRight * sw);
                bndB = Math.Max(bndB, c.ViewTop  + c.ViewHeight);

                foreach (var boxVm in c.GetMarkerInfoBoxes())
                {
                    bndL = Math.Min(bndL, boxVm.ViewLeft);
                    bndT = Math.Min(bndT, boxVm.ViewTop);
                    bndR = Math.Max(bndR, boxVm.ViewLeft + boxVm.BoxWidth);
                    bndB = Math.Max(bndB, boxVm.ViewTop  + boxVm.BoxHeight);
                }
            }

            if (double.IsInfinity(bndL)) return;

            double bndW = bndR - bndL;
            double bndH = bndB - bndT;

            float S    = (bndW > 0 && bndH > 0)
                ? (float)Math.Min(usableW / bndW, usableH / bndH)
                : 1f;
            float padX = (usableW - (float)bndW * S) / 2f;
            float padY = (usableH - (float)bndH * S) / 2f;

            // ---- Draw each container -----------------------------------

            foreach (var c in containers)
            {
                var  plot           = c.PlotVM.Plot;
                var  markerBoxes    = c.GetMarkerInfoBoxes();
                bool showFilePrefix = appSettings.EffectiveShowFilePrefix(
                    (c.Library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);

                double sw     = c.LabelStripViewWidth;
                int    nLeft  = c.LeftLabelStrips.Count;
                int    nRight = c.RightLabelStrips.Count;

                float plotX  = Margin + padX + (float)(c.ViewLeft - bndL) * S;
                float plotY  = Margin + padY + (float)(c.ViewTop  - bndT) * S;
                float plotW  = (float)c.ViewWidth  * S;
                float plotH  = (float)c.ViewHeight * S;
                float stripW = (float)sw * S;

                // Label strips (Smith / Polar only)
                if (nLeft + nRight > 0)
                {
                    var   tf       = PlotRenderer.BuildTransforms(plot, (plotW, plotH));
                    float vpTop    = (float)(tf.Viewport.Y * plotH);
                    float vpBottom = (float)((tf.Viewport.Y + tf.Viewport.Height) * plotH);
                    float chartH   = vpBottom - vpTop;
                    float chartY   = plotY + vpTop;

                    for (int i = 0; i < nLeft; i++)
                    {
                        var s = c.LeftLabelStrips[i];
                        DrawAxisLabelStrip(canvas, plotX - (i + 1) * stripW, chartY,
                            stripW, chartH, s.Trace, false, theme, s.CustomLabel, s.ShowFilePrefix);
                    }
                    for (int i = 0; i < nRight; i++)
                    {
                        var s = c.RightLabelStrips[i];
                        DrawAxisLabelStrip(canvas, plotX + plotW + i * stripW, chartY,
                            stripW, chartH, s.Trace, true, theme, s.CustomLabel, s.ShowFilePrefix);
                    }
                }

                // Main plot content
                float cTableZoom = (plot.PlotType == PlotType.Table)
                    ? plotW / (float)c.Width
                    : 1f;

                canvas.Save();
                canvas.Translate(plotX, plotY);
                PlotRenderer.Draw(canvas, (plotW, plotH), plot, PlotDetail.Full, theme, showFilePrefix,
                    zoomLevel: cTableZoom,
                    aliasFor: c.Library is { } cLib ? t => cLib.AliasFor(t.EffectiveSourcePath) : null,
                    alwaysShowSource: AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                        c.Library?.HasMultipleSources ?? false));
                canvas.Restore();

                // Marker info boxes
                foreach (var boxVm in markerBoxes)
                {
                    float bx = Margin + padX + (float)(boxVm.ViewLeft - bndL) * S;
                    float by = Margin + padY + (float)(boxVm.ViewTop  - bndT) * S;
                    float bw = (float)boxVm.BoxWidth  * S;
                    float bh = (float)boxVm.BoxHeight * S;

                    canvas.Save();
                    canvas.Translate(bx, by);
                    MarkerRenderer.DrawInfoBox(
                        canvas, (bw, bh),
                        boxVm.Marker, boxVm.Trace, boxVm.FreqUnit,
                        theme, showFilePrefix,
                        transparentBackground: appSettings.MarkerBoxTransparentBackground,
                        otherTraces: boxVm.Marker.IsMulti ? boxVm.OtherTraces : null);
                    canvas.Restore();
                }
            }
        }

        // ---- Clipboard write ----------------------------------------

        private static async Task SetClipboardDataAsync(
            Control anchor, byte[] pdf, string svg, string json, Bitmap? bitmap = null)
        {
            if (OperatingSystem.IsWindows())
            {
                var topLevel = TopLevel.GetTopLevel(anchor);
                IntPtr hwnd  = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                WindowsClipboard.SetClipboard(hwnd, pdf, svg, json, bitmap, PageW, PageH);
                return;
            }

            var clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
            if (clipboard is null) return;
            try
            {
                var transfer = BuildClipboardTransferCore(pdf, svg, json, bitmap);
                await clipboard.SetDataAsync(transfer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlotExporter] Clipboard write failed: {ex.Message}");
                try { await clipboard.SetTextAsync(json); } catch { }
            }
        }

        /// <summary>
        /// Builds a <see cref="DataTransfer"/> holding multiple clipboard representations:
        /// PDF and SVG under their native macOS UTIs (com.adobe.pdf / public.svg-image) for
        /// cross-app paste (Keynote, Preview, Illustrator), app-scoped PDF/SVG for
        /// circuitRF-to-circuitRF fidelity, an optional raster bitmap (public.png) as a universal
        /// fallback, and the DataDisplay config JSON as plain text for the Paste command.
        /// </summary>
        internal static DataTransfer BuildClipboardTransferCore(
            byte[] pdf, string svg, string json, Bitmap? bitmap = null)
        {
            var item = new DataTransferItem();

            item.Set(OperatingSystem.IsWindows() ? PdfNativeWinFormat : PdfNativeMacFormat, pdf);
            if (!OperatingSystem.IsWindows())
                item.Set(SvgNativeFormat, Encoding.UTF8.GetBytes(svg));
            if (bitmap is not null)
                item.Set(DataFormat.Bitmap, bitmap);
            item.Set(PdfFormat, pdf);
            item.Set(SvgFormat, svg);
            item.Set(DataFormat.Text, json);

            var transfer = new DataTransfer();
            transfer.Add(item);
            return transfer;
        }

        // Application-scoped formats.
        internal static readonly DataFormat<byte[]> PdfFormat =
            DataFormat.CreateBytesApplicationFormat("circuitRF.pdf");
        internal static readonly DataFormat<string> SvgFormat =
            DataFormat.CreateStringApplicationFormat("circuitRF.svg");

        private static readonly DataFormat<byte[]> PdfNativeMacFormat =
            DataFormat.CreateBytesPlatformFormat("com.adobe.pdf");
        private static readonly DataFormat<byte[]> PdfNativeWinFormat =
            DataFormat.CreateBytesPlatformFormat("application/pdf");
        private static readonly DataFormat<byte[]> SvgNativeFormat =
            DataFormat.CreateBytesPlatformFormat("public.svg-image");

        // ---- Tab-delimited text writer (Table plots only) ---------------

        private static void WriteTabText(string path, Plot plot, bool showFilePrefix)
        {
            var    sb        = new StringBuilder();
            double freqScale = plot.FreqUnits.Scale();
            string freqFmt   = $"{plot.FormatString}{plot.MaximumFractionDigits}";

            sb.Append($"Freq ({plot.FreqUnits.Description()})");
            foreach (var trace in plot.Traces)
            {
                sb.Append('\t');
                sb.Append(showFilePrefix ? trace.Description : trace.ShortDescription);
            }
            sb.AppendLine();

            foreach (double freq in TableRenderer.GetSortedFrequencies(plot))
            {
                sb.Append((freq * freqScale).ToString(freqFmt));
                foreach (var trace in plot.Traces)
                {
                    sb.Append('\t');
                    sb.Append(TableRenderer.FormatTraceCell(trace, freq));
                }
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        // ---- PDF / SVG writers -----------------------------------------

        private static void WritePdf(string path, Action<SKCanvas> render)
        {
            var metadata = new SKDocumentPdfMetadata
            {
                Title   = Path.GetFileNameWithoutExtension(path),
                Creator = "circuitRF",
            };

            using var skStream = new SKFileWStream(path);
            using var doc      = SKDocument.CreatePdf(skStream, metadata);
            var canvas = doc.BeginPage(PageW, PageH);
            render(canvas);
            doc.EndPage();
            doc.Close();
        }

        private static void WriteSvg(string path, Action<SKCanvas> render)
        {
            using var skStream = new SKFileWStream(path);
            using var canvas   = SKSvgCanvas.Create(new SKRect(0, 0, PageW, PageH), skStream);
            render(canvas);
        }

        // ---- Axis label strip (mirrors AxisLabelControl.LabelDrawOperation.Render) ----

        private static void DrawAxisLabelStrip(
            SKCanvas    canvas,
            float       x,
            float       y,
            float       w,
            float       h,
            Trace       trace,
            bool        isRight,
            RenderTheme theme,
            string?     customLabel,
            bool        showFilePrefix)
        {
            float cap        = w * 0.85f;
            float fontSizePx = MathF.Min(MathF.Max(h * 0.04f, MathF.Min(6f, cap)), cap);

            bool    useCustom   = !string.IsNullOrEmpty(customLabel);
            string  displayText = useCustom ? customLabel!
                                : (showFilePrefix ? trace.Description : trace.ShortDescription);
            SKColor textColor   = useCustom ? theme.TextColor
                                : RenderTheme.ToSKColor(trace.Properties.LineColor);

            using var font  = new SKFont(SkiaFonts.PlexRegular, fontSizePx);
            using var paint = new SKPaint { Color = textColor, IsAntialias = true };

            string text   = displayText;
            float  maxLen = h - 12f;
            while (text.Length > 1 && font.MeasureText(text) > maxLen)
                text = text[..^1];
            if (text.Length < displayText.Length)
                text = text.TrimEnd() + "…";

            float tw = font.MeasureText(text);
            float cx = x + w / 2f;
            float cy = y + h / 2f;

            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.RotateDegrees(isRight ? 90f : -90f);
            canvas.DrawText(text, -tw / 2f, font.Size * 0.35f, SKTextAlign.Left, font, paint);
            canvas.Restore();
        }
    }
}
