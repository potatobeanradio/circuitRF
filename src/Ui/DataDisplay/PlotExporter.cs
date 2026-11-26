// ================================================================
//  PlotExporter.cs  —  PDF / SVG export for a single plot
//
//  Paper: 8.5" × 11" landscape = 792 × 612 pts at 72 pts/in.
//  Margins: 0.5" (36 pts) each side → usable area 720 × 540 pts.
//
//  ── The composition moved out in RND-4 ───────────────────────────
//
//  The bounding-box fit and the three encoders are
//  CircuitRF.Render.DataDisplay's PlotComposer and PlotDocumentWriter
//  now (brief-render-4-data-display.md R-rnd4-2), so `circuitrf render`
//  writes the same bytes rather than a picture that resembles them. The
//  arithmetic is unchanged and its description lives with it.
//
//  What is left here is what needs a WINDOW: the save dialog, the
//  clipboard's four representations and their Windows bypass, the
//  tab-delimited text a Table plot can also be written as, and
//  `Place(container)` — the one method that turns a live
//  PlotContainerViewModel into the six numbers the composition lays out.
//  That method is the boundary; nothing view-model-shaped crosses it.
// ================================================================

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using CircuitRF.Ui.Diagnostics;
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

        internal const float PageW  = 792f;   // 11" × 72
        internal const float PageH  = 612f;   // 8.5" × 72
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

            // ONE composition, shared with Copy and with `circuitrf render` (RND-4 R-rnd4-2).
            // This method used to carry its own single-container copy of the bounding-box fit; for
            // one container it computed exactly what PlotComposer computes for a list of one, and
            // `showFilePrefix` above is the same expression PlotComposer.Place evaluates — so
            // nothing about the written file changes, and there is no longer a second place for it
            // to drift from.
            //
            // A null container is still a first-class case (Export from a plot with no container
            // provider): it becomes one PlacedPlot filling the usable area exactly, which is the
            // same page position the old fallback branch wrote — S resolves to 1 and both pads to
            // zero, so plotX == Margin as before.
            var page = PagePlacement.Letter;
            PlacedPlot placed = container is not null
                ? Place(container)
                : new PlacedPlot
                  {
                      Plot         = plot,
                      ViewLeft     = 0,
                      ViewTop      = 0,
                      ViewWidth    = page.UsableWidth,
                      ViewHeight   = page.UsableHeight,
                      LogicalWidth = page.UsableWidth,
                      ShowFilePrefix = showFilePrefix,
                  };

            void Render(SKCanvas canvas)
                => PlotComposer.Render(canvas, [placed], theme, AppSettings.Current, page);

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
        /// <remarks><paramref name="partsOnly"/>: marker tables, marker boxes and floating legends selected without their
        /// plot — the picture carries them alone. The JSON does not: none of them can be pasted without its plot.</remarks>
        internal static async Task CopyContainersToClipboardAsync(
            Control                               anchor,
            IReadOnlyList<PlotContainerViewModel> containers,
            RenderTheme                           theme,
            AnnotationPage? annotations = null,
            IReadOnlyList<CanvasBoxViewModel>? partsOnly = null)
        {
            var     picture = Compose(containers, annotations, partsOnly);
            byte[]  pdf     = BuildPdfBytes(picture, theme);
            string  svg     = BuildSvgString(picture, theme);
            string  json    = BuildContainersJson(containers, annotations?.Items, annotations?.Controls);
            Bitmap? bitmap  = BuildBitmap(picture, theme);
            await SetClipboardDataAsync(anchor, pdf, svg, json, bitmap, picture.Page.Width, picture.Page.Height);
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
            await CopyContainersToClipboardAsync(anchor, new[] { container }, theme);
        }

        /// <summary>The clipboard picture's margin, in points, around a page cropped to what was copied.</summary>
        internal const float ClipboardMargin = 8f;

        /// <summary>What a copy draws, and the page it draws it on.</summary>
        internal sealed record ClipboardPicture(IReadOnlyList<PlacedPlot> Plots, AnnotationPage? Annotations, PagePlacement Page);

        /// <summary>
        /// The copy's placements, on a page CROPPED to them (<see cref="PlotComposer.CroppedPage"/>): the scale a letter page
        /// would draw them at, with <see cref="ClipboardMargin"/> around them. A picture pasted into a slide on a whole letter
        /// page arrives inside a box of empty space that has to be cropped away by hand. Export to a file keeps the page.
        /// </summary>
        internal static ClipboardPicture Compose(IReadOnlyList<PlotContainerViewModel> containers,
            AnnotationPage? annotations = null, IReadOnlyList<CanvasBoxViewModel>? partsOnly = null)
        {
            var letter = OnLetterPage(containers, annotations, partsOnly);
            var page = PlotComposer.CroppedPage(letter.Plots, PagePlacement.Letter, ClipboardMargin, annotations?.Items,
                                                annotations?.Zoom ?? 1, annotations?.OffsetX ?? 0, annotations?.OffsetY ?? 0,
                                                annotations?.Controls);
            return letter with { Page = page };
        }

        /// <summary>The same placements on the letter page Export writes.</summary>
        private static ClipboardPicture OnLetterPage(IReadOnlyList<PlotContainerViewModel> containers,
            AnnotationPage? annotations, IReadOnlyList<CanvasBoxViewModel>? partsOnly)
            => new(containers.Select(Place)
                             .Concat((partsOnly ?? []).GroupBy(b => b.Container).Select(g => PlaceParts(g.Key, g.ToList())))
                             .ToList(),
                   annotations, PagePlacement.Letter);

        private static void Draw(SKCanvas canvas, ClipboardPicture picture, RenderTheme theme)
            => PlotComposer.Render(canvas, picture.Plots, theme, AppSettings.Current, picture.Page, picture.Annotations?.Items,
                                   picture.Annotations?.Zoom ?? 1, picture.Annotations?.OffsetX ?? 0,
                                   picture.Annotations?.OffsetY ?? 0, picture.Annotations?.Controls);

        private static byte[] BuildPdfBytes(ClipboardPicture picture, RenderTheme theme)
            => PlotDocumentWriter.BuildPdfBytes(canvas => Draw(canvas, picture, theme), picture.Page);

        private static string BuildSvgString(ClipboardPicture picture, RenderTheme theme)
            => PlotDocumentWriter.BuildSvgString(canvas => Draw(canvas, picture, theme), picture.Page);

        /// <summary>Renders all plots in <paramref name="containers"/> to a PDF byte array, on the letter page Export
        /// writes.</summary>
        internal static byte[] BuildPdfBytesForContainers(
            IReadOnlyList<PlotContainerViewModel> containers, RenderTheme theme, AnnotationPage? annotations = null,
            IReadOnlyList<CanvasBoxViewModel>? partsOnly = null)
            => BuildPdfBytes(OnLetterPage(containers, annotations, partsOnly), theme);

        /// <summary>Renders all plots in <paramref name="containers"/> to an SVG string, on the letter page Export
        /// writes.</summary>
        internal static string BuildSvgStringForContainers(
            IReadOnlyList<PlotContainerViewModel> containers, RenderTheme theme, AnnotationPage? annotations = null,
            IReadOnlyList<CanvasBoxViewModel>? partsOnly = null)
            => BuildSvgString(OnLetterPage(containers, annotations, partsOnly), theme);

        /// <summary>
        /// Renders a copy to a high-resolution raster bitmap (2× page size) suitable
        /// for pasting into apps such as Keynote, Pages, and Word that recognise
        /// <see cref="DataFormat.Bitmap"/> but not the circuitRF application-scoped PDF format.
        /// Returns null if rendering fails.
        /// </summary>
        private static Bitmap? BuildBitmap(ClipboardPicture picture, RenderTheme theme)
        {
            const float Scale = 2.0f;
            int w = (int)Math.Ceiling(picture.Page.Width  * Scale);
            int h = (int)Math.Ceiling(picture.Page.Height * Scale);

            using var skBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas   = new SKCanvas(skBitmap);
            canvas.Scale(Scale, Scale);
            // A clipboard bitmap is a DOCUMENT, not a frame — see PlotDocumentScope. This is the
            // one plot raster this repo builds outside PlotDocumentWriter, so it enters the scope
            // itself rather than drawing a 3D surface to a different standard than Export does.
            using (PlotDocumentScope.Enter())
                Draw(canvas, picture, theme);

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
        /// <remarks>
        /// <b>Every trace's source is written as the FILE it reads, not as the reference it is saved
        /// under.</b> A saved trace that follows the display's selected source carries the "selected"
        /// sentinel, and one under the results folder carries a name relative to it; both mean
        /// something different on the display that pastes them — another tab with another selection,
        /// or another workspace with another results folder — so the plots came across empty. The
        /// paste turns each path back into the reference that display would save
        /// (<see cref="DataDisplayViewModel.PasteFromConfigAsync"/>).
        /// </remarks>
        internal static string BuildContainersJson(IReadOnlyList<PlotContainerViewModel> containers,
            IReadOnlyList<CanvasAnnotation>? annotations = null, IReadOnlyList<DisplayControl>? controls = null)
        {
            var config = new DataDisplayConfig { ZoomLevel = 1.0,
                Annotations = annotations is { Count: > 0 } ? annotations.ToList() : null,
                Controls    = controls is { Count: > 0 } ? controls.Select(c => c.Clone()).ToList() : null };
            foreach (var c in containers)
            {
                var pc = DataDisplayViewModel.BuildPlotContainerConfig(c, "", c.Library);
                if (c.Library is { } lib)
                    foreach (var tc in pc.Traces)
                    {
                        if (tc.SourcePath is { Length: > 0 } sp && lib.ResolveAbs(sp) is { } abs)
                            tc.SourcePath = abs;
                        if (tc.XSourcePath is { Length: > 0 } xp && lib.ResolveAbs(xp) is { } xabs)
                            tc.XSourcePath = xabs;
                    }
                config.Plots.Add(pc);
            }
            return JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        }

        // ---- In-memory PDF / SVG builders ---------------------------

        // Both writers moved to CircuitRF.Render.DataDisplay.PlotDocumentWriter in RND-4 so
        // `circuitrf render` emits the same bytes rather than its own. These forward; the page size
        // is this exporter's own Letter landscape, unchanged. harmonicaRF and wBond call them too.
        internal static byte[] BuildPdfBytes(Action<SKCanvas> render)
            => PlotDocumentWriter.BuildPdfBytes(render, PagePlacement.Letter);

        internal static string BuildSvgString(Action<SKCanvas> render)
            => PlotDocumentWriter.BuildSvgString(render, PagePlacement.Letter);

        // ---- Multi-plot renderer ------------------------------------


        internal static async Task ExportDocumentAsync(Control anchor, DisplayWindowViewModel window, string name)
        {
            var display = window.DataDisplay;
            var top = TopLevel.GetTopLevel(anchor);
            if (top is null || display is null) return;
            var pages = window.Tabs.Select(tab => (
                Plots: tab.DataDisplay.Plots.Select(Place).ToList(),
                Annotations: new AnnotationPage(tab.DataDisplay.Annotations.ToArray(), tab.DataDisplay.ZoomLevel,
                    tab.DataDisplay.ViewOffsetX, tab.DataDisplay.ViewOffsetY,
                    tab.DataDisplay.Controls.Select(c => c.Model.Clone()).ToArray()))).ToList();
            var activePage = Math.Max(0, window.Tabs.IndexOf(window.ActiveTab!));
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Data Display", SuggestedFileName = name + ".pdf", DefaultExtension = "pdf",
                FileTypeChoices = [new FilePickerFileType("PDF Document") { Patterns = ["*.pdf"] },
                    new FilePickerFileType("SVG Image") { Patterns = ["*.svg"] },
                    new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }],
            });
            if (file is null) return;
            string path = file.Path.LocalPath;
            Action<SKCanvas> DrawPage(int index) => canvas => PlotComposer.Render(canvas,
                pages[index].Plots, display.Theme, AppSettings.Current, PagePlacement.Letter,
                pages[index].Annotations.Items, pages[index].Annotations.Zoom,
                pages[index].Annotations.OffsetX, pages[index].Annotations.OffsetY, pages[index].Annotations.Controls);
            var draw = DrawPage(activePage);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            byte[] bytes = extension switch
            {
                ".pdf" when pages.Count > 1 => PlotDocumentWriter.BuildPdfBytes(
                    Enumerable.Range(0, pages.Count).Select(DrawPage).ToList(), PagePlacement.Letter),
                ".pdf" => PlotDocumentWriter.BuildPdfBytes(draw, PagePlacement.Letter, PlotDocumentWriter.PdfTitleFor(path)),
                ".svg" => System.Text.Encoding.UTF8.GetBytes(PlotDocumentWriter.BuildSvgString(draw, PagePlacement.Letter)),
                ".png" => PlotDocumentWriter.BuildPngBytes(draw, PagePlacement.Letter, 2) ?? [],
                _ => throw new InvalidOperationException("Choose PDF, SVG or PNG for a Data Display export."),
            };
            CircuitRF.Design.Cells.AtomicFile.WriteAllBytes(path, bytes);
        }

        /// <summary>
        /// A live container as the six numbers <see cref="PlotComposer"/> lays out — RND-4's
        /// R-rnd4-2 boundary in one method. Everything the composition needs is READ here, on the
        /// UI thread, and nothing view-model-shaped crosses.
        /// </summary>
        /// <summary>Some of a plot's floating parts on their own — its legend, marker table or marker boxes — for a copy
        /// of them selected without their plot.</summary>
        internal static PlacedPlot PlaceParts(PlotContainerViewModel c, IReadOnlyCollection<CanvasBoxViewModel> parts)
        {
            var placed = Place(c);
            var markers = parts.OfType<MarkerInfoBoxViewModel>().Select(b => b.Marker).ToHashSet();
            return new PlacedPlot
            {
                Plot = placed.Plot, ViewLeft = placed.ViewLeft, ViewTop = placed.ViewTop, ViewWidth = placed.ViewWidth,
                ViewHeight = placed.ViewHeight, LogicalWidth = placed.LogicalWidth,
                Legend      = c.GetLegendBox() is { } legend && parts.Contains(legend) ? placed.Legend : null,
                MarkerTable = c.GetMarkerTable() is { } table && parts.Contains(table) ? placed.MarkerTable : null,
                MarkerBoxes = placed.MarkerBoxes.Where(b => markers.Contains(b.Marker)).ToList(),
                ShowFilePrefix = placed.ShowFilePrefix, AlwaysShowSource = placed.AlwaysShowSource,
                AliasFor = placed.AliasFor, PartsOnly = true,
            };
        }

        internal static PlacedPlot Place(PlotContainerViewModel c)
        {
            var    lib            = c.Library;
            bool   showFilePrefix = AppSettings.Current.EffectiveShowFilePrefix(
                                        (lib?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);

            return new PlacedPlot
            {
                Plot                = c.PlotVM.Plot,
                ViewLeft            = c.ViewLeft,
                ViewTop             = c.ViewTop,
                ViewWidth           = c.ViewWidth,
                ViewHeight          = c.ViewHeight,
                LogicalWidth        = c.Width,
                LabelStripViewWidth = c.LabelStripViewWidth,
                // AutoLabel carried through: it is what the on-screen strip is SHOWING, and an
                // export that dropped it fell back to Trace.Description — a different string.
                LeftLabelStrips     = c.LeftLabelStrips
                                       .Select(s => new PlacedLabelStrip(s.Trace, s.CustomLabel, s.ShowFilePrefix, s.AutoLabel))
                                       .ToList(),
                RightLabelStrips    = c.RightLabelStrips
                                       .Select(s => new PlacedLabelStrip(s.Trace, s.CustomLabel, s.ShowFilePrefix, s.AutoLabel))
                                       .ToList(),
                Legend              = c.GetLegendBox() is { } lb
                                       ? new PlacedLegend(lb.ViewLeft, lb.ViewTop, lb.BoxWidth, lb.BoxHeight, lb.Lw)
                                       : null,
                MarkerTable         = c.GetMarkerTable() is { } table
                                       ? new PlacedMarkerTable(table.ViewLeft, table.ViewTop,
                                           MarkerTableRenderer.MeasureAll(table.Rows).W * table.BoxWidth / MarkerTableRenderer.Measure(table.Rows).W,
                                           MarkerTableRenderer.MeasureAll(table.Rows).H * table.BoxWidth / MarkerTableRenderer.Measure(table.Rows).W)
                                       : null,
                MarkerBoxes         = c.GetMarkerInfoBoxes()
                                       .Where(b => b.IsBoxVisible)
                                       .Select(b => new PlacedMarkerBox(
                                           b.Marker, b.Trace, b.FreqUnit,
                                           b.ViewLeft, b.ViewTop, b.BoxWidth, b.BoxHeight,
                                           b.PlotTraces))
                                       .ToList(),
                ShowFilePrefix      = showFilePrefix,
                AlwaysShowSource    = AppSettings.Current.EffectiveShowFilePrefix(
                                          lib?.HasMultipleSources ?? false),
                AliasFor            = lib is { } l ? t => l.AliasFor(t.EffectiveSourcePath) : null,
                // The host's own transient chrome — the Smith Chart's grippers, arrowheads and load
                // labels. Carried so a COPY is a picture of what was on screen; without it an
                // overlay a PlotControl draws on every frame is silently absent from every export.
                Overlay             = c.Overlay is { } ov ? ov.Draw : null,
            };
        }

        // ---- Clipboard write ----------------------------------------

        /// <summary>
        /// Places one picture on the clipboard in every format a receiving application might want,
        /// richest first. <b>Internal so harmonicaRF's own Copy Plot reaches it</b> (§7.8's "Not
        /// reinvented"): its panels are Skia, not <c>PlotContainerViewModel</c>s, so it renders its
        /// own page and hands the bytes to THIS — the clipboard plumbing, the Windows bypass and the
        /// text fallback are the part worth sharing, and a second copy of them would be a second set
        /// of platform bugs.
        /// </summary>
        /// <param name="pageW">The composed page's width, for the Windows bypass — the one path that
        /// has to tell the receiving application how big the picture is. It defaults to this exporter's
        /// own letter landscape, which is what every plot composes onto; the stackup cross-section
        /// composes onto a page sized to the drawing (<c>StackupGraphicExport.PageFor</c>) and passes
        /// its own, or the picture would be pasted inside a letter-sized empty box.</param>
        /// <param name="pageH">The composed page's height — see <paramref name="pageW"/>.</param>
        internal static async Task SetClipboardDataAsync(
            Control anchor, byte[] pdf, string svg, string json, Bitmap? bitmap = null,
            float pageW = PageW, float pageH = PageH)
        {
            if (OperatingSystem.IsWindows())
            {
                var topLevel = TopLevel.GetTopLevel(anchor);
                IntPtr hwnd  = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                WindowsClipboard.SetClipboard(hwnd, pdf, svg, json, bitmap, pageW, pageH);
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
        /// fallback, and the DataDisplay config JSON in <see cref="ClipboardPayload"/> for the Paste command —
        /// never as plain text, which a chat client would take in preference to every picture here.
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
            ClipboardPayload.Set(item, json);   // circuitRF's own; never plain text (ClipboardPayload)

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
                sb.Append(trace.HasLabel ? trace.Label : showFilePrefix ? trace.Description : trace.ShortDescription);
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
        //
        // Both moved to PlotDocumentWriter in RND-4, along with the axis-label-strip drawing that
        // used to sit at the bottom of this file — `circuitrf render` writes through the same three
        // and so writes the same bytes (§5.1).

        private static void WritePdf(string path, Action<SKCanvas> render)
            => PlotDocumentWriter.WritePdf(path, render, PagePlacement.Letter);

        private static void WriteSvg(string path, Action<SKCanvas> render)
            => PlotDocumentWriter.WriteSvg(path, render, PagePlacement.Letter);
    }
}
