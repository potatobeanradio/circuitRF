using System;
using System.IO;
using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Clipboard;

/// <summary>
/// System-clipboard helper for schematic objects.
/// Primary format: JSON text (cross-platform, roundtrips perfectly).
/// Rich formats: SVG vector (macOS/Linux cross-app) + PNG raster bitmap (universal fallback).
/// On Windows, CF_ENHMETAFILE (EMF) is omitted here — it requires System.Drawing.Imaging and
/// Svg.NET (see splotRF's WindowsClipboard.cs for that pattern; add those packages to unlock it).
/// </summary>
public static class SchematicClipboard
{
    // "com.adobe.pdf" is the macOS UTI recognised by Keynote, Preview, Pages, etc.
    // On Windows, Office apps don't recognise that UTI, so use the IANA MIME type instead.
    private static readonly DataFormat<byte[]> PdfNativeMacFormat =
        DataFormat.CreateBytesPlatformFormat("com.adobe.pdf");
    private static readonly DataFormat<byte[]> PdfNativeWinFormat =
        DataFormat.CreateBytesPlatformFormat("application/pdf");

    // "public.svg-image" is the macOS/Linux UTI for Illustrator, Inkscape, Keynote (Catalina+).
    private static readonly DataFormat<byte[]> SvgNativeFormat =
        DataFormat.CreateBytesPlatformFormat("public.svg-image");

    /// <summary>
    /// Copies the given selection to the system clipboard.
    /// Places JSON text + SVG vector + PNG raster on the clipboard simultaneously so
    /// receiving apps can pick the richest representation they understand.
    /// Color variant and background transparency are read from <see cref="ClipboardRenderPolicy"/>.
    /// </summary>
    public static async Task CopyAsync(
        IClipboard clipboard,
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        double gridSize = 100.0)
    {
        if (components.Count == 0 && wires.Count == 0 && canvasObjects.Count == 0) return;

        string json = SchematicPersistence.SerializeSelection(components, wires, canvasObjects, gridSize);

        // Resolve render policy once for this copy operation.
        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var renderTheme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);
        const bool excludeGrid = true;

        var item = new DataTransferItem();

        try
        {
            // PDF: platform-native format first — richest vector representation.
            // com.adobe.pdf UTI (macOS): Keynote, Preview, Pages, etc.
            // application/pdf (Windows): recognised by some viewers; EMF would be the true
            //   Windows vector format (see splotRF WindowsClipboard.cs — future work).
            byte[]? pdf = TryRenderToPdf(components, wires, renderTheme, transparent, excludeGrid);
            if (pdf is not null)
                item.Set(OperatingSystem.IsWindows() ? PdfNativeWinFormat : PdfNativeMacFormat, pdf);

            // SVG vector: public.svg-image UTI (macOS/Linux) — Illustrator, Inkscape, etc.
            string? svg = TryRenderToSvg(components, wires, renderTheme, transparent, excludeGrid);
            if (svg is not null && !OperatingSystem.IsWindows())
                item.Set(SvgNativeFormat, Encoding.UTF8.GetBytes(svg));

            // PNG bitmap: universal raster fallback (Keynote, Pages, Word, etc.).
            Bitmap? bmp = TryRenderToAvaloniaImage(components, wires, renderTheme, transparent, excludeGrid);
            if (bmp is not null)
                item.Set(DataFormat.Bitmap, bmp);
        }
        catch { /* rich formats are best-effort; JSON is always present */ }

        item.Set(DataFormat.Text, json);                               // JSON — primary / always

        var transfer = new DataTransfer();
        transfer.Add(item);

        try
        {
            await clipboard.SetDataAsync(transfer);
        }
        catch
        {
            // Fall back to plain-text clipboard if DataTransfer is not supported.
            await clipboard.SetTextAsync(json);
        }
    }

    /// <summary>
    /// Tries to paste from the system clipboard.
    /// Returns null if the clipboard has no recognized schematic JSON.
    /// Offsets pasted items to avoid exact overlap.
    /// <c>SourceGridSize</c> in the result is the P that was active when the content was copied —
    /// pass it to <c>SchematicPasteCommand</c> so cross-grid snapping can be applied (§5).
    /// </summary>
    public static async Task<(List<EditableComponent> Comps, List<EditableWire> Wires,
        List<EditableCanvasObject> CanvasObjs, double SourceGridSize)?> PasteAsync(
        IClipboard clipboard,
        double offsetX = 100, double offsetY = 100)
    {
        string? json = null;
        try { json = await clipboard.TryGetTextAsync(); }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var (comps, wires, cobjs, srcGrid) = SchematicPersistence.DeserializeSelection(json);

            var newComps = new List<EditableComponent>(comps.Count);
            foreach (var c in comps)
            {
                var nc = c.Clone();
                nc.X += offsetX;
                nc.Y += offsetY;
                newComps.Add(nc);
            }

            var newWires = new List<EditableWire>(wires.Count);
            foreach (var w in wires)
            {
                var nw = w.Clone();
                for (int i = 0; i < nw.Points.Count; i++)
                {
                    var pt = nw.Points[i];
                    nw.Points[i] = (pt.X + offsetX, pt.Y + offsetY);
                }
                newWires.Add(nw);
            }

            var newCobjs = new List<EditableCanvasObject>(cobjs.Count);
            foreach (var o in cobjs)
            {
                var no = o.Clone();
                no.X += offsetX;
                no.Y += offsetY;
                newCobjs.Add(no);
            }

            return (newComps, newWires, newCobjs, srcGrid);
        }
        catch { return null; }
    }

    // ── Private rendering helpers ─────────────────────────────────────────────

    /// <summary>
    /// Builds a transient render model from the selection, computing bounding box.
    /// Returns null if the selection has no visible geometry.
    /// </summary>
    private static (SchematicModel Rm, SchematicSpatialIndex Idx, double WorldW, double WorldH)?
        BuildSelectionModel(
            IReadOnlyList<EditableComponent> components,
            IReadOnlyList<EditableWire>      wires)
    {
        var tmp = new SchematicEditModel { GridSize = 100 };
        foreach (var c in components) tmp.Components.Add(c);
        foreach (var w in wires)      tmp.Wires.Add(w);
        var (rm, idx) = tmp.BuildRenderModel();
        double worldW = rm.BbMaxX - rm.BbMinX;
        double worldH = rm.BbMaxY - rm.BbMinY;
        if (worldW < 1 || worldH < 1) return null;
        return (rm, idx, worldW, worldH);
    }

    /// <summary>
    /// Renders selection to a PDF byte array using SkiaSharp's PDF document canvas.
    /// The PDF page is sized to the schematic bounding box (with padding) — no fixed paper size.
    /// Note: PDF viewers may render a transparent background as white regardless of the flag.
    /// </summary>
    private static byte[]? TryRenderToPdf(
        IReadOnlyList<EditableComponent> components,
        IReadOnlyList<EditableWire>      wires,
        SchematicRenderTheme             theme,
        bool                             useTransparentBackground,
        bool                             excludeGrid)
    {
        try
        {
            var m = BuildSelectionModel(components, wires);
            if (m is null) return null;
            var (rm, idx, worldW, worldH) = m.Value;

            const double pad = 0.15;
            double zoom = Math.Min(720.0 / (worldW * (1 + 2 * pad)), 540.0 / (worldH * (1 + 2 * pad)));
            float pxW = Math.Clamp((float)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 720);
            float pxH = Math.Clamp((float)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 540);
            double panX = rm.BbMinX - worldW * pad;
            double panY = rm.BbMinY - worldH * pad;

            var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
            using var stream = new SKDynamicMemoryWStream();
            using var doc    = SKDocument.CreatePdf(stream, metadata);
            var canvas = doc.BeginPage(pxW, pxH);
            SchematicRenderer.Draw(canvas, ((int)pxW, (int)pxH), rm, idx, panX, panY, zoom,
                theme, showFps: false,
                useTransparentBackground: useTransparentBackground,
                excludeGrid: excludeGrid);
            doc.EndPage();
            doc.Close();
            return stream.DetachAsData().ToArray();
        }
        catch { return null; }
    }

    /// <summary>Renders selection to an SVG string using SkiaSharp's SVG canvas.</summary>
    private static string? TryRenderToSvg(
        IReadOnlyList<EditableComponent> components,
        IReadOnlyList<EditableWire>      wires,
        SchematicRenderTheme             theme,
        bool                             useTransparentBackground,
        bool                             excludeGrid)
    {
        try
        {
            var m = BuildSelectionModel(components, wires);
            if (m is null) return null;
            var (rm, idx, worldW, worldH) = m.Value;

            const double pad = 0.15;
            double zoom = Math.Min(800.0 / (worldW * (1 + 2 * pad)), 800.0 / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 2400);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 2400);
            double panX = rm.BbMinX - worldW * pad;
            double panY = rm.BbMinY - worldH * pad;

            using var stream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, pxW, pxH), stream))
                SchematicRenderer.Draw(canvas, (pxW, pxH), rm, idx, panX, panY, zoom,
                    theme, showFps: false,
                    useTransparentBackground: useTransparentBackground,
                    excludeGrid: excludeGrid);
            return Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
        }
        catch { return null; }
    }

    /// <summary>
    /// Renders selection to an Avalonia Bitmap (PNG-backed) for the DataFormat.Bitmap slot.
    /// This is the universal raster fallback understood by Keynote, Pages, Word, etc.
    /// PNG preserves alpha; apps that don't support transparency may render the background as black.
    /// </summary>
    private static Bitmap? TryRenderToAvaloniaImage(
        IReadOnlyList<EditableComponent> components,
        IReadOnlyList<EditableWire>      wires,
        SchematicRenderTheme             theme,
        bool                             useTransparentBackground,
        bool                             excludeGrid)
    {
        try
        {
            var m = BuildSelectionModel(components, wires);
            if (m is null) return null;
            var (rm, idx, worldW, worldH) = m.Value;

            const double pad   = 0.15;
            const int    maxPx = 1200;
            double zoom = Math.Min(maxPx / (worldW * (1 + 2 * pad)), maxPx / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, maxPx);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, maxPx);
            double panX = rm.BbMinX - worldW * pad;
            double panY = rm.BbMinY - worldH * pad;

            using var skBmp  = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(skBmp);
            SchematicRenderer.Draw(canvas, (pxW, pxH), rm, idx, panX, panY, zoom,
                theme, showFps: false,
                useTransparentBackground: useTransparentBackground,
                excludeGrid: excludeGrid);

            using var skData = skBmp.Encode(SKEncodedImageFormat.Png, 100);
            if (skData is null) return null;
            using var ms = new MemoryStream(skData.ToArray());
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
