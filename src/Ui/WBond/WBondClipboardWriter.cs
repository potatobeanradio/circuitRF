using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.WBond;
using SkiaSharp;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The wBond editor's ordinary Copy, writing every format at once — <b>a transcription of
/// <see cref="LayoutClipboard.CopyAsync"/>, deliberately, not a second design</b>.
///
/// <h3>The bug this exists to fix</h3>
/// <para>Owner, 2026-08-16: <i>"Copy/Paste from wBond into PowerPoint/Keynote is not working."</i>
/// It never could: wBond's Copy called <c>clipboard.SetTextAsync(json)</c> and nothing else, so the
/// only thing on the pasteboard was a marker-prefixed JSON string. PowerPoint and Keynote have no
/// idea what that is, so they either paste the raw JSON as text or refuse. The Layout Editor solved
/// this across Windows, macOS and Linux over several rounds; the instruction here was to COPY that
/// implementation rather than re-derive it, so the structure below is line-for-line the same:</para>
/// <list type="number">
///   <item>page framed from what is actually PAINTED, never from the current pan/zoom;</item>
///   <item>PDF, SVG and a PNG raster rendered best-effort — a failure costs the picture, never the
///     JSON, which is what makes paste-back-into-wBond survive anything;</item>
///   <item><b>Windows bypasses Avalonia entirely</b> and writes every format (CF_ENHMETAFILE first,
///     which is what Word and PowerPoint take) in ONE P/Invoke session — see
///     <see cref="WindowsClipboard"/>'s header for why a second session fails;</item>
///   <item>macOS/Linux use one <see cref="DataTransfer"/> carrying the native
///     <c>com.adobe.pdf</c> / <c>public.svg-image</c> UTIs, the bitmap and the text, with a
///     text-only fallback if the platform refuses the multi-format write.</item>
/// </list>
///
/// <h3>What goes in the picture</h3>
/// <para>The SELECTION — the same wires and geometry the JSON carries — so what lands in the slide
/// is what the user had highlighted. With nothing selected there is nothing to copy at all, and
/// this is never reached.</para>
/// </summary>
internal static class WBondClipboardWriter
{
    /// <summary>Fraction of the content size left as margin, the same figure the layout export uses.</summary>
    private const double Pad = 0.15;

    /// <summary>
    /// Puts <paramref name="json"/> on the clipboard together with a PDF, an SVG and a PNG of
    /// <paramref name="wires"/> drawn over <paramref name="layout"/>.
    /// </summary>
    /// <returns>True when something was written.</returns>
    internal static async Task<bool> CopyAsync(
        Avalonia.Controls.Control anchor,
        IClipboard clipboard,
        string json,
        WBondDesign wires,
        LayoutView? layout,
        Technology? technology,
        string? instanceBaseDir,
        WBondRenderTheme wireTheme,
        LayoutRenderTheme layoutTheme,
        WireThicknessMode thickness,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(wires);

        if (string.IsNullOrEmpty(json)) return false;

        // Rich formats are best-effort; the JSON text is always present as the fallback — the same
        // contract LayoutClipboard states, and the reason a rendering failure can never cost a paste.
        byte[]? pdf = null;
        (string Svg, float W, float H)? svg = null;
        Bitmap? bmp = null;
        try
        {
            var ctx = new ExportContext(wires, layout, technology, instanceBaseDir,
                                        wireTheme, layoutTheme, thickness, dbuPerMicron);
            pdf = TryRenderToPdf(ctx);
            svg = TryRenderToSvg(ctx);
            bmp = TryRenderToBitmap(ctx);
        }
        catch { /* best-effort */ }

        // ── Windows: bypass Avalonia, one P/Invoke session, EMF first. ──
        if (OperatingSystem.IsWindows())
        {
            float pageW = 0f, pageH = 0f;
            if (svg is { } s)
            {
                const float maxSide = 720f;   // ≈10in at 72pt/in — Word/PowerPoint-friendly
                float scale = MathF.Min(1f, maxSide / MathF.Max(s.W, s.H));
                pageW = s.W * scale;
                pageH = s.H * scale;
            }

            IntPtr hwnd = Avalonia.Controls.TopLevel.GetTopLevel(anchor)?.TryGetPlatformHandle()?.Handle
                          ?? IntPtr.Zero;
            WindowsClipboard.SetClipboard(hwnd, pdf, svg?.Svg, json, bmp, pageW, pageH);
            return true;
        }

        // ── macOS / Linux: one DataTransfer, native UTIs first, text last. ──
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

        return true;
    }

    /// <summary>
    /// A design holding only the wires the selection touches, with their arrays and profiles — the
    /// picture must show what was copied, not the whole board.
    ///
    /// <para>The <see cref="Wire"/> objects are shared, not cloned: this design is rendered and
    /// dropped within the call, and nothing here mutates a point.</para>
    /// </summary>
    internal static WBondDesign SelectionDesign(WBondDesign design, WireSelection? selection)
    {
        ArgumentNullException.ThrowIfNull(design);

        var touched = selection?.TouchedWires();
        if (touched is null || touched.Count == 0) return design;

        var copy = new WBondDesign();
        foreach (var profile in design.Profiles) copy.Profiles.Add(profile);

        int flat = -1;
        foreach (var array in design.Arrays)
        {
            WireArray? mirror = null;
            foreach (var wire in array.Wires)
            {
                flat++;
                if (!touched.Contains(flat)) continue;

                mirror ??= new WireArray { Name = array.Name, Profile = array.Profile };
                mirror.Wires.Add(wire);
            }
            if (mirror is not null) copy.Arrays.Add(mirror);
        }

        return copy.Arrays.Count > 0 ? copy : design;
    }

    /// <summary>A throwaway <see cref="LayoutView"/> over the copied shapes and instances, so
    /// <see cref="LayoutRenderer.Draw"/> is reused verbatim — the same trick
    /// <c>LayoutClipboard.BuildTransientView</c> uses.</summary>
    internal static LayoutView? TransientLayout(LayoutFragment.Payload? payload)
    {
        if (payload is null) return null;
        if (payload.Shapes.Count == 0 && payload.Instances.Count == 0) return null;

        var view = new LayoutView();
        if (payload.DbuPerMicron > 0) view.DbuPerMicron = payload.DbuPerMicron;
        foreach (var s in payload.Shapes) view.Shapes.Add(s);
        foreach (var i in payload.Instances) view.Instances.Add(i);
        return view;
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private sealed record ExportContext(
        WBondDesign Design,
        LayoutView? Layout,
        Technology? Tech,
        string? BaseDir,
        WBondRenderTheme WireTheme,
        LayoutRenderTheme LayoutTheme,
        WireThicknessMode Thickness,
        int DbuPerMicron);

    /// <summary>
    /// Bounds of what will actually be PAINTED, in layout database units — the wires and the
    /// geometry together. Null when there is nothing to frame.
    /// </summary>
    private static (double W, double H, double MinX, double MinY)? ContentBounds(ExportContext ctx)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var wire in ctx.Design.AllWires())
        {
            foreach (var p in wire.Points)
            {
                double x = WBondSnap.ToDbu(p.X, ctx.DbuPerMicron);
                double y = WBondSnap.ToDbu(p.Y, ctx.DbuPerMicron);
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (ctx.Layout is { } layout)
        {
            foreach (var shape in layout.Shapes)
            {
                var bb = LayoutGeometry.BboxOf(shape);
                if (bb.MinX < minX) minX = bb.MinX;
                if (bb.MinY < minY) minY = bb.MinY;
                if (bb.MaxX > maxX) maxX = bb.MaxX;
                if (bb.MaxY > maxY) maxY = bb.MaxY;
            }

            foreach (var inst in layout.Instances)
            {
                var ib = CellHierarchy.InstanceBbox(inst, ctx.BaseDir ?? "");
                if (ib.IsEmpty) continue;
                if (ib.MinX < minX) minX = ib.MinX;
                if (ib.MinY < minY) minY = ib.MinY;
                if (ib.MaxX > maxX) maxX = ib.MaxX;
                if (ib.MaxY > maxY) maxY = ib.MaxY;
            }
        }

        if (minX > maxX || minY > maxY) return null;

        // A single straight wire has zero extent across its own axis; a page of zero height cannot be
        // rendered, so both spans get a floor rather than the framing being refused.
        double w = Math.Max(maxX - minX, 1.0);
        double h = Math.Max(maxY - minY, 1.0);
        return (w, h, minX, minY);
    }

    /// <summary>
    /// A glyph margin in PIXELS, kept clear on every side.
    ///
    /// <para><b>The content bounds are of the wire POINTS, and a point is not what is drawn.</b> Each
    /// one gets a filled dot of <c>theme.DotRadiusPx</c> and the segments through it a stroke of
    /// <c>theme.LineWidthPx</c> — both in screen pixels, neither of which any world-space bbox knows
    /// about. So an extreme point sits exactly on the frame edge and half its dot falls off the page,
    /// which is the owner's "the points are clipped at the edges of the bounding box".</para>
    ///
    /// <para>A pixel margin rather than a larger world pad, because the failure is in pixels: the
    /// proportional <see cref="Pad"/> shrinks to nothing along a degenerate axis (a straight
    /// north/south wire has ZERO extent in x) while the dot stays 3 px wide whatever the zoom.</para>
    /// </summary>
    private const float GlyphMarginPx = 12f;

    /// <summary>Page size and viewport for a given maximum side, shared by all three renderers so
    /// they cannot frame the same content differently.</summary>
    private static (LayoutViewport Vp, float W, float H)? Frame(ExportContext ctx, double maxW, double maxH)
    {
        if (ContentBounds(ctx) is not { } b) return null;

        // The zoom has to fit the content into the page LESS the glyph margin, or reserving the
        // margin afterwards would just push the content back off the other edge.
        double usableW = Math.Max(maxW - 2 * GlyphMarginPx, 1.0);
        double usableH = Math.Max(maxH - 2 * GlyphMarginPx, 1.0);

        double zoom = Math.Min(usableW / (b.W * (1 + 2 * Pad)), usableH / (b.H * (1 + 2 * Pad)));

        float pxW = Math.Clamp((float)Math.Ceiling(b.W * zoom * (1 + 2 * Pad)) + 2 * GlyphMarginPx, 80, (float)maxW);
        float pxH = Math.Clamp((float)Math.Ceiling(b.H * zoom * (1 + 2 * Pad)) + 2 * GlyphMarginPx, 80, (float)maxH);

        // ── CENTRED on the content, not offset from its corner by a pad fraction ────────────────
        //
        // Deriving the pan from MinX − W·Pad is only equivalent to centring while the page is
        // exactly the padded content size — and it is not, twice over: the two axes share one zoom,
        // and each page dimension is CLAMPED to an 80 px floor. A straight north/south wire has
        // W = 1 DBU, so its page is clamped up to 80 px wide while the pan still says "start 0.15
        // DBU left of the wire" — putting the wire on the left EDGE of an 80 px page with its dots
        // hanging off it. Centring makes both the shared zoom and the clamp harmless.
        double panX = (b.MinX + b.W / 2.0) - pxW / (2.0 * zoom);
        double panY = (b.MinY + b.H / 2.0) - pxH / (2.0 * zoom);

        return (new LayoutViewport(panX, panY, zoom, pxW, pxH), pxW, pxH);
    }

    private static void Compose(SKCanvas canvas, ExportContext ctx, LayoutViewport vp)
    {
        if (ctx.Layout is not null)
        {
            LayoutRenderer.Draw(canvas, ctx.Layout, ctx.Tech, vp, new LayoutRenderOptions
            {
                Theme = ctx.LayoutTheme,
                ShowGrid = false,               // an export is artwork, not an editing surface
                Overlay = null,                 // no selection outlines, no handles, no ghosts
                TransparentBackground = true,   // the destination document supplies the background
                BaseDir = ctx.BaseDir ?? "",
            });
        }

        WBondRenderer.Draw(canvas, ctx.Design, vp, ctx.WireTheme, selection: null,
                           thickness: ctx.Thickness, dbuPerMicron: ctx.DbuPerMicron);
    }

    private static byte[]? TryRenderToPdf(ExportContext ctx)
    {
        try
        {
            if (Frame(ctx, 720.0, 540.0) is not { } f) return null;

            var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
            using var stream = new SKDynamicMemoryWStream();
            using var doc = SKDocument.CreatePdf(stream, metadata);
            var canvas = doc.BeginPage(f.W, f.H);
            Compose(canvas, ctx, f.Vp);
            doc.EndPage();
            doc.Close();
            return stream.DetachAsData().ToArray();
        }
        catch { return null; }
    }

    private static (string Svg, float W, float H)? TryRenderToSvg(ExportContext ctx)
    {
        try
        {
            if (Frame(ctx, 800.0, 800.0) is not { } f) return null;

            using var stream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, f.W, f.H), stream))
                Compose(canvas, ctx, f.Vp);

            return (Encoding.UTF8.GetString(stream.DetachAsData().ToArray()), f.W, f.H);
        }
        catch { return null; }
    }

    private static Bitmap? TryRenderToBitmap(ExportContext ctx)
    {
        try
        {
            if (Frame(ctx, 1200.0, 1200.0) is not { } f) return null;

            int pxW = (int)Math.Ceiling(f.W), pxH = (int)Math.Ceiling(f.H);

            using var skBmp = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
            skBmp.Erase(SKColors.Transparent);   // LayoutRenderer never Clears — see its header
            using var canvas = new SKCanvas(skBmp);
            Compose(canvas, ctx, f.Vp);

            using var data = skBmp.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) return null;

            using var ms = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    /// <summary>Test seam: the page framing is the part the cropped-content reports are always about,
    /// and it is worth asserting directly rather than inferring from rendered bytes.</summary>
    internal static (double W, double H, double MinX, double MinY)? ContentBoundsForTests(
        WBondDesign design, LayoutView? layout, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
        => ContentBounds(Context(design, layout, dbuPerMicron));

    /// <summary>Test seam over <see cref="Frame"/> — the bitmap's own framing, at its own page size,
    /// so "is the extreme point far enough from the edge for its dot to fit" is asked of the real
    /// arithmetic rather than of a rendered PNG.</summary>
    internal static (LayoutViewport Vp, float W, float H)? BitmapFrameForTests(
        WBondDesign design, LayoutView? layout, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
        => Frame(Context(design, layout, dbuPerMicron), 1200.0, 1200.0);

    /// <summary>The margin a test may assume is kept clear on every side.</summary>
    internal static float GlyphMarginForTests => GlyphMarginPx;

    private static ExportContext Context(WBondDesign design, LayoutView? layout, int dbuPerMicron)
        => new(design, layout, null, null,
               WBondRenderTheme.Fallback, LayoutRenderTheme.Dark,
               WireThicknessMode.ConstantPixels, dbuPerMicron);
}
