using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Clipboard;

/// <summary>
/// System-clipboard helper for layout shapes (docs/sonnet-briefs/brief-L1f-clipboard.md §6). Mirrors
/// <see cref="SchematicClipboard"/> end to end — JSON text (guarded by
/// <see cref="LayoutFragment.Marker"/>) is the primary format; PDF/SVG/PNG ride alongside so a
/// layout selection pastes into PowerPoint, Word, Pages and Keynote as a proper vector graphic. This
/// file contains ONLY serialization, <see cref="IClipboard"/> traffic, and the rich-format renders —
/// every "what does the paste mean" decision (rescale, layer reconciliation, placement) lives in
/// <see cref="LayoutFragment"/>, which this file calls but never duplicates.
/// </summary>
public static class LayoutClipboard
{
    /// <summary>
    /// Copies an already-built fragment (<see cref="LayoutFragment.Build"/>, via
    /// <c>LayoutEditorViewModel.BuildCopyPayload</c> — carries shapes AND instances together since
    /// brief-L3a-followups.md §2/R-fix-2 made a mixed selection normal) to the system clipboard.
    /// Places JSON text + PDF + SVG + PNG simultaneously so receiving apps can pick the richest
    /// representation they understand. Color variant and background transparency are read from
    /// <see cref="ClipboardRenderPolicy"/>, exactly like every other copy path in this app.
    ///
    /// <b>The rich (PDF/SVG/PNG) preview renders <paramref name="payload"/>'s SHAPES only</b> — an
    /// instance's geometry lives in a resolved sub-cell this file has no compiled-rendering access to
    /// (that machinery is <c>LayoutRenderer.Instances.cs</c>'s, wired for the live canvas, not this
    /// export path), so an instance-only copy simply carries no rich graphic (an empty selection
    /// bbox), same as any other best-effort render failure — the JSON text is always present and is
    /// what circuitRF's own paste path actually reads. A named, narrow scope limitation, not a defect.
    /// </summary>
    public static async Task CopyAsync(
        IClipboard clipboard,
        LayoutFragment.Payload payload,
        Technology? tech,
        IntPtr ownerHwnd = default)
    {
        if (payload.Shapes.Count == 0 && payload.Instances.Count == 0) return;

        string json = LayoutFragment.Serialize(payload);

        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var renderTheme = LayoutRenderTheme.FromTheme(ThemeService.Active, variant);

        // Rich formats are best-effort; JSON text is always present as the fallback.
        byte[]?                         pdf = null;
        (string Svg, float W, float H)? svg = null;
        Bitmap?                         bmp = null;
        try
        {
            pdf = TryRenderToPdf(payload.Shapes, tech, renderTheme, transparent);
            svg = TryRenderToSvg(payload.Shapes, tech, renderTheme, transparent);
            bmp = TryRenderToAvaloniaImage(payload.Shapes, tech, renderTheme, transparent);
        }
        catch { /* best-effort */ }

        // ── Windows: bypass Avalonia, write all formats (incl. CF_ENHMETAFILE) in ONE P/Invoke
        //    session — see WindowsClipboard.cs's header comment for the full why. ──
        if (OperatingSystem.IsWindows())
        {
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
    /// Tries to read a layout fragment from the system clipboard. Returns null when the clipboard
    /// holds no text, or text without <see cref="LayoutFragment.Marker"/> — arbitrary text, a
    /// symbol-clipboard payload, or truncated JSON are all a clean no-op (never an exception, never
    /// a partial model change).
    /// </summary>
    public static async Task<LayoutFragment.Payload?> PasteAsync(IClipboard clipboard)
    {
        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { return null; }

        // "Paste whatever is on the clipboard": a copy made in the wBond editor from a MIXED
        // selection arrives wrapped, and the layout half has to come back out of it here or a paste
        // into this editor silently does nothing. The Layout Editor cannot hold wires, so the wire
        // half is simply not asked for — each editor takes the part it understands.
        var (_, layoutJson) = WBondMixedClipboard.Unwrap(text);

        return LayoutFragment.TryDeserialize(layoutJson, out var payload) ? payload : null;
    }

    // ── Private rendering helpers ────────────────────────────────────────────

    /// <summary>Bounds of the shapes being exported (R-L1f-4: renders the SELECTION, not the current
    /// view) — independent of whatever the user's on-screen pan/zoom happen to be.</summary>
    private static (double WorldW, double WorldH, double BbMinX, double BbMinY)? ComputeSelectionBounds(
        IReadOnlyList<LayoutShape> shapes)
    {
        var bbox = Bbox.Empty;
        foreach (var s in shapes) bbox = bbox.Union(LayoutGeometry.BboxOf(s));
        if (bbox.IsEmpty) return null;

        double worldW = bbox.MaxX - bbox.MinX;
        double worldH = bbox.MaxY - bbox.MinY;
        if (worldW < 1 || worldH < 1) return null;
        return (worldW, worldH, bbox.MinX, bbox.MinY);
    }

    /// <summary>A transient, throwaway <see cref="LayoutView"/> wrapping just the exported shapes —
    /// <see cref="LayoutRenderer.Draw"/> renders straight off a <see cref="LayoutView"/>, so this is
    /// the cheapest way to reuse it verbatim rather than adding a shapes-only overload.</summary>
    private static LayoutView BuildTransientView(IReadOnlyList<LayoutShape> shapes)
    {
        var view = new LayoutView();
        foreach (var s in shapes) view.Shapes.Add(s);
        return view;
    }

    /// <summary>Export-mode render options (R-L1f-5): no grid, no overlay (which alone already
    /// suppresses the ghost/selection outlines/handles/marquee), transparent background.</summary>
    private static LayoutRenderOptions ExportOptions(LayoutRenderTheme theme, bool transparentBackground) => new()
    {
        Theme = theme,
        ShowGrid = false,
        Overlay = null,
        TransparentBackground = transparentBackground,
    };

    internal static byte[]? TryRenderToPdf(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
    {
        try
        {
            var b = ComputeSelectionBounds(shapes);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad = 0.15;
            double zoom = Math.Min(720.0 / (worldW * (1 + 2 * pad)), 540.0 / (worldH * (1 + 2 * pad)));
            float pxW = Math.Clamp((float)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 720);
            float pxH = Math.Clamp((float)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 540);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var view = BuildTransientView(shapes);
            var vp = new LayoutViewport(panX, panY, zoom, pxW, pxH);
            var opts = ExportOptions(theme, transparent);

            var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
            using var stream = new SKDynamicMemoryWStream();
            using var doc    = SKDocument.CreatePdf(stream, metadata);
            var canvas = doc.BeginPage(pxW, pxH);
            LayoutRenderer.Draw(canvas, view, tech, vp, opts);
            doc.EndPage();
            doc.Close();
            return stream.DetachAsData().ToArray();
        }
        catch { return null; }
    }

    internal static (string Svg, float W, float H)? TryRenderToSvg(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
    {
        try
        {
            var b = ComputeSelectionBounds(shapes);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad = 0.15;
            double zoom = Math.Min(800.0 / (worldW * (1 + 2 * pad)), 800.0 / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 2400);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 2400);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var view = BuildTransientView(shapes);
            var vp = new LayoutViewport(panX, panY, zoom, pxW, pxH);
            var opts = ExportOptions(theme, transparent);

            using var stream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, pxW, pxH), stream))
                LayoutRenderer.Draw(canvas, view, tech, vp, opts);
            return (Encoding.UTF8.GetString(stream.DetachAsData().ToArray()), (float)pxW, (float)pxH);
        }
        catch { return null; }
    }

    internal static Bitmap? TryRenderToAvaloniaImage(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
    {
        try
        {
            var b = ComputeSelectionBounds(shapes);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad   = 0.15;
            const int    maxPx = 1200;
            double zoom = Math.Min(maxPx / (worldW * (1 + 2 * pad)), maxPx / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, maxPx);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, maxPx);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var view = BuildTransientView(shapes);
            var vp = new LayoutViewport(panX, panY, zoom, pxW, pxH);
            var opts = ExportOptions(theme, transparent);

            using var skBmp  = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
            skBmp.Erase(SKColors.Transparent);   // LayoutRenderer never Clears (see its header comment) —
                                                  // the destination must arrive already zero-initialized.
            using var canvas = new SKCanvas(skBmp);
            LayoutRenderer.Draw(canvas, view, tech, vp, opts);

            using var skData = skBmp.Encode(SKEncodedImageFormat.Png, 100);
            if (skData is null) return null;
            using var ms = new MemoryStream(skData.ToArray());
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
