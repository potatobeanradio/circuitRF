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
/// On Windows, all formats — including CF_ENHMETAFILE (vector EMF for Word/PowerPoint) — are written
/// in one P/Invoke session via WindowsClipboard, bypassing Avalonia. macOS/Linux use Avalonia's
/// DataTransfer (PDF/SVG native UTIs + PNG + text).
/// </summary>
public static class SchematicClipboard
{

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
        double gridSize = 100.0,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null,
        IntPtr                              ownerHwnd = default)
    {
        if (components.Count == 0 && wires.Count == 0 && canvasObjects.Count == 0) return;

        string json = SchematicPersistence.SerializeSelection(components, wires, canvasObjects, gridSize);

        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var renderTheme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);
        const bool excludeGrid = true;

        // Rich formats are best-effort; JSON text is always present as the fallback.
        byte[]?                          pdf = null;
        (string Svg, float W, float H)?  svg = null;
        Bitmap?                          bmp = null;
        try
        {
            pdf = TryRenderToPdf(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
            svg = TryRenderToSvg(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
            bmp = TryRenderToAvaloniaImage(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
        }
        catch { /* best-effort */ }

        // ── Windows: bypass Avalonia, write all formats (incl. CF_ENHMETAFILE) in ONE P/Invoke
        //    session. Avalonia's SetDataAsync calls EmptyClipboard and keeps clipboard ownership,
        //    so a second session to add EMF would fail. See WindowsClipboard.cs for the full why. ──
        if (OperatingSystem.IsWindows())
        {
            // circuitRF sizes the SVG per-selection (no fixed page). The EMF frame matches the SVG's
            // own dimensions, scaled so the longest side is a sane on-paste size — vector stays crisp.
            float pageW = 0f, pageH = 0f;
            if (svg is { } s)
            {
                const float maxSide = 720f;   // ≈10in at 72pt/in — Word/PowerPoint-friendly default
                float scale = MathF.Min(1f, maxSide / MathF.Max(s.W, s.H));
                pageW = s.W * scale;
                pageH = s.H * scale;
            }
            WindowsClipboard.SetClipboard(ownerHwnd, pdf, svg?.Svg, json, bmp, pageW, pageH);
            return;
        }

        // ── macOS / Linux: Avalonia cross-platform clipboard (native PDF/SVG UTIs + PNG + text). ──
        var item = new DataTransferItem();
        if (pdf is not null)
            item.Set(ClipboardFormats.PdfNativeMacFormat, pdf);
        if (svg is { } sv)
            item.Set(ClipboardFormats.SvgNativeFormat, Encoding.UTF8.GetBytes(sv.Svg));
        if (bmp is not null)
            item.Set(DataFormat.Bitmap, bmp);
        item.Set(DataFormat.Text, json);

        var transfer = new DataTransfer();
        transfer.Add(item);
        try { await clipboard.SetDataAsync(transfer); }
        catch { await clipboard.SetTextAsync(json); }
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
    /// Includes canvas objects (bitmaps) so the exported image contains them; also unions
    /// their rects into the bbox so bitmap-only selections produce a correctly-sized page.
    /// </summary>
    private static (SchematicModel Rm, SchematicSpatialIndex Idx,
                    double WorldW, double WorldH, double BbMinX, double BbMinY)?
        BuildSelectionModel(
            IReadOnlyList<EditableComponent>    components,
            IReadOnlyList<EditableWire>         wires,
            IReadOnlyList<EditableCanvasObject> canvasObjects,
            IReadOnlyList<EditableNetLabel>?    netLabels = null,
            string?                             schematicDirectory = null)
    {
        var tmp = new SchematicEditModel { GridSize = 100, SchematicDirectory = schematicDirectory };
        foreach (var c in components)      tmp.Components.Add(c);
        foreach (var w in wires)           tmp.Wires.Add(w);
        foreach (var obj in canvasObjects) tmp.CanvasObjects.Add(obj);
        if (netLabels is not null)
            foreach (var nl in netLabels)  tmp.NetLabels.Add(nl);
        var (rm, idx) = tmp.BuildRenderModel();

        // Start with the render model's bbox (derived from components + wires).
        // For bitmap-only selections the render model uses a dummy fallback extent,
        // so we recompute from scratch when there are no comps/wires.
        bool hasCompWire = components.Count > 0 || wires.Count > 0;
        double bbMinX, bbMinY, bbMaxX, bbMaxY;
        if (hasCompWire)
        {
            bbMinX = rm.BbMinX; bbMinY = rm.BbMinY;
            bbMaxX = rm.BbMaxX; bbMaxY = rm.BbMaxY;
        }
        else
        {
            bbMinX = bbMinY = double.MaxValue;
            bbMaxX = bbMaxY = double.MinValue;
        }

        // Union every bitmap rect so their positions are included in the page bounds.
        foreach (var bm in rm.Bitmaps)
        {
            if (bm.X             < bbMinX) bbMinX = bm.X;
            if (bm.Y             < bbMinY) bbMinY = bm.Y;
            if (bm.X + bm.Width  > bbMaxX) bbMaxX = bm.X + bm.Width;
            if (bm.Y + bm.Height > bbMaxY) bbMaxY = bm.Y + bm.Height;
        }

        // Union net-label text extents so long labels near the selection edge aren't clipped.
        foreach (var nl in rm.NetLabels)
        {
            double left  = nl.X;
            double right = nl.X + Math.Max(1, nl.Name.Length) * 40.0;
            double top   = nl.Y - 55.0;
            double bot   = nl.Y + 20.0;
            if (left  < bbMinX) bbMinX = left;
            if (top   < bbMinY) bbMinY = top;
            if (right > bbMaxX) bbMaxX = right;
            if (bot   > bbMaxY) bbMaxY = bot;
        }

        if (bbMinX == double.MaxValue) return null; // nothing to render
        double worldW = bbMaxX - bbMinX;
        double worldH = bbMaxY - bbMinY;
        if (worldW < 1 || worldH < 1) return null;
        return (rm, idx, worldW, worldH, bbMinX, bbMinY);
    }

    /// <summary>
    /// Renders selection to a PDF byte array using SkiaSharp's PDF document canvas.
    /// The PDF page is sized to the schematic bounding box (with padding) — no fixed paper size.
    /// Note: PDF viewers may render a transparent background as white regardless of the flag.
    /// </summary>
    private static byte[]? TryRenderToPdf(
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        SchematicRenderTheme                theme,
        bool                                useTransparentBackground,
        bool                                excludeGrid,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null)
    {
        try
        {
            var m = BuildSelectionModel(components, wires, canvasObjects, netLabels, schematicDirectory);
            if (m is null) return null;
            var (rm, idx, worldW, worldH, bbMinX, bbMinY) = m.Value;

            const double pad = 0.15;
            double zoom = Math.Min(720.0 / (worldW * (1 + 2 * pad)), 540.0 / (worldH * (1 + 2 * pad)));
            float pxW = Math.Clamp((float)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 720);
            float pxH = Math.Clamp((float)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 540);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
            using var stream = new SKDynamicMemoryWStream();
            using var doc    = SKDocument.CreatePdf(stream, metadata);
            var canvas = doc.BeginPage(pxW, pxH);
            SchematicRenderer.Draw(canvas, ((int)pxW, (int)pxH), rm, idx, panX, panY, zoom,
                theme,
                useTransparentBackground: useTransparentBackground,
                excludeGrid: excludeGrid);
            doc.EndPage();
            doc.Close();
            return stream.DetachAsData().ToArray();
        }
        catch { return null; }
    }

    /// <summary>Renders selection to an SVG string using SkiaSharp's SVG canvas.</summary>
    private static (string Svg, float W, float H)? TryRenderToSvg(
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        SchematicRenderTheme                theme,
        bool                                useTransparentBackground,
        bool                                excludeGrid,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null)
    {
        try
        {
            var m = BuildSelectionModel(components, wires, canvasObjects, netLabels, schematicDirectory);
            if (m is null) return null;
            var (rm, idx, worldW, worldH, bbMinX, bbMinY) = m.Value;

            const double pad = 0.15;
            double zoom = Math.Min(800.0 / (worldW * (1 + 2 * pad)), 800.0 / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 2400);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 2400);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            using var stream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, pxW, pxH), stream))
                SchematicRenderer.Draw(canvas, (pxW, pxH), rm, idx, panX, panY, zoom,
                    theme,
                    useTransparentBackground: useTransparentBackground,
                    excludeGrid: excludeGrid);
            return (Encoding.UTF8.GetString(stream.DetachAsData().ToArray()), (float)pxW, (float)pxH);
        }
        catch { return null; }
    }

    /// <summary>
    /// Renders selection to an Avalonia Bitmap (PNG-backed) for the DataFormat.Bitmap slot.
    /// This is the universal raster fallback understood by Keynote, Pages, Word, etc.
    /// PNG preserves alpha; apps that don't support transparency may render the background as black.
    /// </summary>
    private static Bitmap? TryRenderToAvaloniaImage(
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        SchematicRenderTheme                theme,
        bool                                useTransparentBackground,
        bool                                excludeGrid,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null)
    {
        try
        {
            var m = BuildSelectionModel(components, wires, canvasObjects, netLabels, schematicDirectory);
            if (m is null) return null;
            var (rm, idx, worldW, worldH, bbMinX, bbMinY) = m.Value;

            const double pad   = 0.15;
            const int    maxPx = 1200;
            double zoom = Math.Min(maxPx / (worldW * (1 + 2 * pad)), maxPx / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, maxPx);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, maxPx);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            using var skBmp  = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(skBmp);
            SchematicRenderer.Draw(canvas, (pxW, pxH), rm, idx, panX, panY, zoom,
                theme,
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

/// <summary>
/// Shared clipboard format identifiers used by both SchematicClipboard and SymbolClipboard.
/// Centralised here so the UTI strings are not duplicated.
/// </summary>
internal static class ClipboardFormats
{
    // "com.adobe.pdf" is the macOS UTI recognised by Keynote, Preview, Pages, etc.
    // On Windows, Office apps don't recognise that UTI, so use the IANA MIME type instead.
    internal static readonly DataFormat<byte[]> PdfNativeMacFormat =
        DataFormat.CreateBytesPlatformFormat("com.adobe.pdf");
    internal static readonly DataFormat<byte[]> PdfNativeWinFormat =
        DataFormat.CreateBytesPlatformFormat("application/pdf");

    // "public.svg-image" is the macOS/Linux UTI for Illustrator, Inkscape, Keynote (Catalina+).
    internal static readonly DataFormat<byte[]> SvgNativeFormat =
        DataFormat.CreateBytesPlatformFormat("public.svg-image");
}
