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
    /// <b>The rich (PDF/SVG/PNG) preview renders the SHAPES <i>and</i> the INSTANCES</b>, and sizes
    /// its page from what is actually PAINTED rather than from raw geometry bounds. Owner report,
    /// 2026-08-09: <i>"pasting the selected geometry with ports has a glitch — I only see pieces of
    /// the ports (cut off at the lower area) and I do not see my MLIN geometry."</i> Both halves had
    /// the same shape of cause:
    /// <list type="bullet">
    ///   <item>the transient view was built from <c>payload.Shapes</c> alone, so a placed PCell —
    ///   i.e. every piece of metal in a schematic-generated layout — was simply absent;</item>
    ///   <item>the page bbox unioned <c>LayoutGeometry.BboxOf</c>, and a <c>LabelShape</c>'s stored
    ///   bbox is a POINT. A selection of two ports and one instance therefore produced a page sized
    ///   to almost nothing, with the port glyphs and their text hanging off the edges.</item>
    /// </list>
    /// <paramref name="baseDir"/> is what makes the instance half possible — an instance's
    /// <c>CellRef</c> resolves relative to the directory containing the <c>.clay</c>, and this file
    /// has no other way to know it.
    ///
    /// <para><b>The EM mesh rides along when one is showing</b> (owner request) — it is part of the
    /// picture the user is looking at. It is deliberately NOT in the JSON payload: a mesh belongs to
    /// an EM setup, not to geometry, so pasting into another layout must not carry one.</para>
    /// </summary>
    public static async Task CopyAsync(
        IClipboard clipboard,
        LayoutFragment.Payload payload,
        Technology? tech,
        IntPtr ownerHwnd = default,
        string baseDir = "",
        Engine.Mom.PlanarMeshReport? planarMesh = null,
        Engine.Mom.PlanarCurrentDensityMap? currentDensity = null)
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
            var ctx = new ExportContext(payload, tech, renderTheme, transparent, baseDir, planarMesh, currentDensity);
            pdf = TryRenderToPdf(ctx);
            svg = TryRenderToSvg(ctx);
            bmp = TryRenderToAvaloniaImage(ctx);
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

    /// <summary>Everything the three renderers need, gathered once so they cannot disagree about
    /// what is in the picture or how big the page is.</summary>
    internal sealed record ExportContext(
        LayoutFragment.Payload Payload,
        Technology? Tech,
        LayoutRenderTheme Theme,
        bool Transparent,
        string BaseDir,
        Engine.Mom.PlanarMeshReport? PlanarMesh,
        Engine.Mom.PlanarCurrentDensityMap? CurrentDensity);

    /// <summary>
    /// Bounds of what will actually be PAINTED (R-L1f-4: the SELECTION, never the current view) —
    /// independent of whatever the user's on-screen pan/zoom happen to be.
    ///
    /// <para><b>Painted, not stored.</b> Three of the four contributions are not
    /// <c>LayoutGeometry.BboxOf</c>: a label's stored bbox is a POINT (its glyphs are measured via
    /// <c>LayoutRenderer.MeasureLabelWorldBbox</c>), an EM port additionally draws a width bar and an
    /// arrow at the conductor end, and an instance's extent has to be resolved through its cell. Using
    /// the stored bboxes is exactly what cropped the owner's ports off the bottom of the page.</para>
    /// </summary>
    private static (double WorldW, double WorldH, double BbMinX, double BbMinY)? ComputeSelectionBounds(ExportContext ctx)
    {
        var bbox = Bbox.Empty;
        var conductorAt = LayoutPortDirection.LookupFor(ctx.Payload.Shapes);

        foreach (var s in ctx.Payload.Shapes)
        {
            bbox = bbox.Union(LayoutGeometry.BboxOf(s));

            if (s is not LabelShape label) continue;

            if (LayoutRenderer.MeasureLabelWorldBbox(label) is { } textBb) bbox = bbox.Union(textBb);

            if (LayoutPortDirection.Resolve(conductorAt, label) is { } hint)
            {
                // The marker spans the conductor width across the direction and reaches into the
                // metal along it; take the whole square about the plane, which bounds both.
                long r = Math.Max(hint.WidthDbu, label.Height);
                bbox = bbox.Union(new Bbox(hint.PlaneX - r, hint.PlaneY - r, hint.PlaneX + r, hint.PlaneY + r));
            }
        }

        foreach (var inst in ctx.Payload.Instances)
        {
            var ib = CellHierarchy.InstanceBbox(inst, ctx.BaseDir);
            if (!ib.IsEmpty) bbox = bbox.Union(ib);
        }

        if (bbox.IsEmpty) return null;

        double worldW = bbox.MaxX - bbox.MinX;
        double worldH = bbox.MaxY - bbox.MinY;
        if (worldW < 1 || worldH < 1) return null;
        return (worldW, worldH, bbox.MinX, bbox.MinY);
    }

    /// <summary>A transient, throwaway <see cref="LayoutView"/> wrapping the exported shapes AND
    /// instances — <see cref="LayoutRenderer.Draw"/> renders straight off a <see cref="LayoutView"/>,
    /// so this is the cheapest way to reuse it verbatim rather than adding a second overload. The
    /// resolution is carried across because the mesh overlay reads <c>view.DbuPerMicron</c> to map
    /// the engine's metres onto layout coordinates.</summary>
    private static LayoutView BuildTransientView(ExportContext ctx)
    {
        var view = new LayoutView();
        if (ctx.Payload.DbuPerMicron > 0) view.DbuPerMicron = ctx.Payload.DbuPerMicron;
        foreach (var s in ctx.Payload.Shapes) view.Shapes.Add(s);
        foreach (var i in ctx.Payload.Instances) view.Instances.Add(i);
        return view;
    }

    /// <summary>Export-mode render options (R-L1f-5): no grid, no overlay (which alone already
    /// suppresses the ghost/selection outlines/handles/marquee), transparent background — plus the
    /// EM mesh when one is showing, and the base directory instances resolve against.</summary>
    private static LayoutRenderOptions ExportOptions(ExportContext ctx) => new()
    {
        Theme = ctx.Theme,
        ShowGrid = false,
        Overlay = null,
        TransparentBackground = ctx.Transparent,
        BaseDir = ctx.BaseDir,
        ShowPlanarMesh = ctx.PlanarMesh is not null,
        PlanarMesh = ctx.PlanarMesh,
        PlanarCurrentDensity = ctx.CurrentDensity,
    };

    /// <summary>Test seam: build the same context <see cref="CopyAsync"/> builds, so a gate can drive
    /// the real export path rather than a simplified stand-in.</summary>
    internal static ExportContext MakeExportContext(
        LayoutFragment.Payload payload, Technology? tech, LayoutRenderTheme theme, bool transparent,
        string baseDir = "", Engine.Mom.PlanarMeshReport? planarMesh = null,
        Engine.Mom.PlanarCurrentDensityMap? currentDensity = null)
        => new(payload, tech, theme, transparent, baseDir, planarMesh, currentDensity);

    /// <summary>Test seam over <see cref="ComputeSelectionBounds"/> — the page-framing rule is the
    /// thing the cropped-ports report was about, and it is worth asserting directly rather than
    /// inferring from rendered bytes.</summary>
    internal static (double WorldW, double WorldH, double BbMinX, double BbMinY)? SelectionBoundsForTests(
        ExportContext ctx) => ComputeSelectionBounds(ctx);

    /// <summary>Shapes-only convenience over the three renderers below — no instances, no mesh, no
    /// base directory. Retained because the export-geometry gates (R-L1f-4's "the page is a pure
    /// function of the SELECTION") are about exactly that, and threading a context through them would
    /// test the plumbing rather than the framing rule.</summary>
    private static ExportContext ShapesOnly(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
    {
        var payload = new LayoutFragment.Payload();
        payload.Shapes.AddRange(shapes);
        return new ExportContext(payload, tech, theme, transparent, "", null, null);
    }

    internal static byte[]? TryRenderToPdf(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
        => TryRenderToPdf(ShapesOnly(shapes, tech, theme, transparent));

    internal static (string Svg, float W, float H)? TryRenderToSvg(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
        => TryRenderToSvg(ShapesOnly(shapes, tech, theme, transparent));

    internal static Bitmap? TryRenderToAvaloniaImage(
        IReadOnlyList<LayoutShape> shapes, Technology? tech, LayoutRenderTheme theme, bool transparent)
        => TryRenderToAvaloniaImage(ShapesOnly(shapes, tech, theme, transparent));

    internal static byte[]? TryRenderToPdf(ExportContext ctx)
    {
        var tech = ctx.Tech;
        try
        {
            var b = ComputeSelectionBounds(ctx);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad = 0.15;
            double zoom = Math.Min(720.0 / (worldW * (1 + 2 * pad)), 540.0 / (worldH * (1 + 2 * pad)));
            float pxW = Math.Clamp((float)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 720);
            float pxH = Math.Clamp((float)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 540);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var view = BuildTransientView(ctx);
            var vp = new LayoutViewport(panX, panY, zoom, pxW, pxH);
            var opts = ExportOptions(ctx);

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

    internal static (string Svg, float W, float H)? TryRenderToSvg(ExportContext ctx)
    {
        var tech = ctx.Tech;
        try
        {
            var b = ComputeSelectionBounds(ctx);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad = 0.15;
            double zoom = Math.Min(800.0 / (worldW * (1 + 2 * pad)), 800.0 / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, 2400);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, 2400);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var view = BuildTransientView(ctx);
            var vp = new LayoutViewport(panX, panY, zoom, pxW, pxH);
            var opts = ExportOptions(ctx);

            using var stream = new SKDynamicMemoryWStream();
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, pxW, pxH), stream))
                LayoutRenderer.Draw(canvas, view, tech, vp, opts);
            return (Encoding.UTF8.GetString(stream.DetachAsData().ToArray()), (float)pxW, (float)pxH);
        }
        catch { return null; }
    }

    internal static Bitmap? TryRenderToAvaloniaImage(ExportContext ctx)
    {
        var tech = ctx.Tech;
        try
        {
            var b = ComputeSelectionBounds(ctx);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad   = 0.15;
            const int    maxPx = 1200;
            double zoom = Math.Min(maxPx / (worldW * (1 + 2 * pad)), maxPx / (worldH * (1 + 2 * pad)));
            int pxW = Math.Clamp((int)Math.Ceiling(worldW * zoom * (1 + 2 * pad)), 80, maxPx);
            int pxH = Math.Clamp((int)Math.Ceiling(worldH * zoom * (1 + 2 * pad)), 80, maxPx);
            double panX = bbMinX - worldW * pad;
            double panY = bbMinY - worldH * pad;

            var view = BuildTransientView(ctx);
            var vp = new LayoutViewport(panX, panY, zoom, pxW, pxH);
            var opts = ExportOptions(ctx);

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
