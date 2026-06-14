using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Clipboard;

/// <summary>
/// System-clipboard helper for symbol editor selections.
/// Primary format: JSON text (cross-platform, round-trips perfectly).
/// Rich formats: PDF vector + SVG (macOS/Linux) + PNG raster — mirrors SchematicClipboard.
/// Rendering calls SchematicRenderer.DrawSymbol directly (same as PaletteGlyphControl),
/// bypassing the editor-overlay infrastructure not needed for a clipboard image.
/// </summary>
public static class SymbolClipboard
{
    // Prefix guard — any text that doesn't contain this marker is silently ignored on paste.
    private const string Marker = "circuitrf/symbol-clipboard-v1";

    private sealed class Payload
    {
        public string? Marker     { get; set; }
        public double  GridSize   { get; set; } = 100.0;
        public List<SymbolPrimitive> Primitives { get; set; } = [];
        public List<CsymPin>         Pins       { get; set; } = [];
    }

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Copies the given selection to the system clipboard.
    /// Places JSON text + PDF + SVG (non-Windows) + PNG on the clipboard simultaneously so
    /// receiving apps can pick the richest representation they understand.
    /// No-op if both lists are empty.
    /// </summary>
    public static async Task CopyAsync(
        IClipboard clipboard,
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double gridSize = 100.0)
    {
        if (primitives.Count == 0 && pins.Count == 0) return;

        // Build JSON payload (primary format — round-trips perfectly; paste depends on it).
        var payload = new Payload
        {
            Marker     = Marker,
            GridSize   = gridSize,
            Primitives = [..primitives],
            Pins       = pins.Select(p => new CsymPin
            {
                LocalX    = p.LocalX,
                LocalY    = p.LocalY,
                PortIndex = p.PortIndex,
                Name      = p.Name,
            }).ToList(),
        };

        string json;
        try   { json = JsonSerializer.Serialize(payload, _opts); }
        catch { return; }

        // Resolve render policy once for this copy operation.
        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var renderTheme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);

        // Page bounds from primitives AND pins, so pins (which can sit outside the primitive bbox,
        // e.g. on stubs) aren't clipped. pinMargin covers the pin dot; the 15% render pad in each
        // helper absorbs the port label. Handles primitive-free (pins-only) selections too.
        double bbMinX = double.MaxValue, bbMinY = double.MaxValue,
               bbMaxX = double.MinValue, bbMaxY = double.MinValue;
        if (primitives.Count > 0)
        {
            var (p0, q0, p1, q1) = SymbolGeometry.ComputeBb(primitives);
            bbMinX = p0; bbMinY = q0; bbMaxX = p1; bbMaxY = q1;
        }
        const double pinMargin = 12.0;
        foreach (var pin in pins)
        {
            bbMinX = Math.Min(bbMinX, pin.LocalX - pinMargin);
            bbMinY = Math.Min(bbMinY, pin.LocalY - pinMargin);
            bbMaxX = Math.Max(bbMaxX, pin.LocalX + pinMargin);
            bbMaxY = Math.Max(bbMaxY, pin.LocalY + pinMargin);
        }
        bool hasBounds = bbMinX != double.MaxValue;
        double worldW = hasBounds ? bbMaxX - bbMinX : 0;
        double worldH = hasBounds ? bbMaxY - bbMinY : 0;

        var item = new DataTransferItem();

        try
        {
            if (worldW >= 1 && worldH >= 1)
            {
                byte[]? pdf = TryRenderToPdf(primitives, pins, bbMinX, bbMinY, worldW, worldH, renderTheme, transparent);
                if (pdf is not null)
                    item.Set(OperatingSystem.IsWindows() ? ClipboardFormats.PdfNativeWinFormat : ClipboardFormats.PdfNativeMacFormat, pdf);

                string? svg = TryRenderToSvg(primitives, pins, bbMinX, bbMinY, worldW, worldH, renderTheme, transparent);
                if (svg is not null && !OperatingSystem.IsWindows())
                    item.Set(ClipboardFormats.SvgNativeFormat, Encoding.UTF8.GetBytes(svg));

                Bitmap? bmp = TryRenderToAvaloniaImage(primitives, pins, bbMinX, bbMinY, worldW, worldH, renderTheme, transparent);
                if (bmp is not null)
                    item.Set(DataFormat.Bitmap, bmp);
            }
        }
        catch { /* rich formats are best-effort; JSON is always present */ }

        item.Set(DataFormat.Text, json);            // JSON — primary / always

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
    /// Returns null if the clipboard contains no recognized symbol JSON.
    /// Primitives are translated by (offsetX, offsetY); pins are P-snapped after offset.
    /// </summary>
    public static async Task<(List<SymbolPrimitive> Prims, List<SymbolPin> Pins, double GridSize)?> PasteAsync(
        IClipboard clipboard,
        double offsetX = 100.0, double offsetY = 100.0)
    {
        string? json;
        try   { json = await clipboard.TryGetTextAsync(); }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(json)) return null;

        Payload? payload;
        try   { payload = JsonSerializer.Deserialize<Payload>(json, _opts); }
        catch { return null; }

        if (payload?.Marker != Marker) return null;
        if (payload.Primitives.Count == 0 && payload.Pins.Count == 0) return null;

        // Offset each primitive in-place — fresh objects from deserialization, safe to mutate.
        foreach (var p in payload.Primitives)
            SymbolGeometry.TranslateBy(p, offsetX, offsetY);

        // Offset and P-snap pins (pins always land on the connection grid P=100).
        static double PSnap(double v) => Math.Round(v / 100.0) * 100.0;
        var pins = payload.Pins
            .Select(p => new SymbolPin(PSnap(p.LocalX + offsetX), PSnap(p.LocalY + offsetY), p.PortIndex, p.Name))
            .ToList();

        return (payload.Primitives, pins, payload.GridSize);
    }

    // ── Private rendering helpers ─────────────────────────────────────────────
    // Use SchematicRenderer.DrawSymbol directly — the same proven path as PaletteGlyphControl.
    // No grid, no selection overlay, no pin markers: just the symbol geometry.

    private static void RenderSymbol(
        SKCanvas                       canvas,
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double panX, double panY, double zoom,
        SchematicRenderTheme           theme,
        bool                           useTransparentBackground)
    {
        canvas.Clear(useTransparentBackground ? SKColors.Transparent : theme.Background);
        SchematicRenderer.DrawSymbol(
            canvas, primitives,
            compX: 0, compY: 0,
            rotation: SymbolRotation.R0, mirrorX: false,
            panX: panX, panY: panY, zoom: zoom,
            theme: theme);
        // Pins: same dot + port-label rendering and scale as the editor, so exports match screen.
        SymbolEditorRenderer.DrawPinMarkersPlain(canvas, pins, panX, panY, zoom, theme);
    }

    private static byte[]? TryRenderToPdf(
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double bbMinX, double bbMinY, double worldW, double worldH,
        SchematicRenderTheme           theme,
        bool                           useTransparentBackground)
    {
        try
        {
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
            RenderSymbol(canvas, primitives, pins, panX, panY, zoom, theme, useTransparentBackground);
            doc.EndPage();
            doc.Close();
            return stream.DetachAsData().ToArray();
        }
        catch { return null; }
    }

    private static string? TryRenderToSvg(
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double bbMinX, double bbMinY, double worldW, double worldH,
        SchematicRenderTheme           theme,
        bool                           useTransparentBackground)
    {
        try
        {
            const double pad = 0.15;
            double zoom = Math.Min(800.0 / (worldW * (1 + 2 * pad)), 800.0 / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 2400);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 2400);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            using var stream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, pxW, pxH), stream))
                RenderSymbol(canvas, primitives, pins, panX, panY, zoom, theme, useTransparentBackground);
            return Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
        }
        catch { return null; }
    }

    private static Bitmap? TryRenderToAvaloniaImage(
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double bbMinX, double bbMinY, double worldW, double worldH,
        SchematicRenderTheme           theme,
        bool                           useTransparentBackground)
    {
        try
        {
            const double pad   = 0.15;
            const int    maxPx = 1200;
            double zoom = Math.Min(maxPx / (worldW * (1 + 2 * pad)), maxPx / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, maxPx);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, maxPx);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            using var skBmp  = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(skBmp);
            RenderSymbol(canvas, primitives, pins, panX, panY, zoom, theme, useTransparentBackground);

            using var skData = skBmp.Encode(SKEncodedImageFormat.Png, 100);
            if (skData is null) return null;
            using var ms = new MemoryStream(skData.ToArray());
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
