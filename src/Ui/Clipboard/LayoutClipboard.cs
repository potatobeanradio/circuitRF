using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.Diagnostics;
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
    ///
    /// <para><b>DRC violation markers ride along the same way</b> (owner request, mirrors the mesh
    /// exactly): shown in the graphic (bitmap/PDF/SVG) only when the panel's own markers toggle is on,
    /// and deliberately NOT in the JSON payload — a violation marker is a check result, not geometry,
    /// so pasting into another layout must never carry one.</para>
    ///
    /// <para><b>And railRF's board map on the same terms again</b> (brief-railrf-9-copy.md R-rail9-3,
    /// railrf.md §11.7). These four parameters are the EXPLICIT OVERLAY LIST that decides what is in
    /// the picture, and the failure mode they share is worth stating once: <i>an overlay nobody added
    /// to the list is silently absent from the copy</i> — the picture is still produced, it still
    /// looks correct, and the one thing the user copied it for is missing. That is why each of them
    /// is gated against the real rendered bytes rather than against the wiring.</para>
    /// </summary>
    public static async Task CopyAsync(
        IClipboard clipboard,
        LayoutFragment.Payload payload,
        Technology? tech,
        IntPtr ownerHwnd = default,
        string baseDir = "",
        Engine.Mom.PlanarMeshReport? planarMesh = null,
        Engine.Mom.PlanarCurrentDensityMap? currentDensity = null,
        IReadOnlyList<DrcMarker>? drcMarkers = null,
        RailMapScene? railMap = null,
        RailMapTheme? railTheme = null)
    {
        // §9B.9: RULERS COUNT AS CONTENT. Owner report, 2026-08-27 — pasting a ruler produced some
        // other geometry instead of it. A ruler-only copy fell out of this guard and returned
        // without touching the system clipboard AT ALL, so the previous copy was still sitting there
        // and Ctrl+V pasted that. The copy did not fail loudly; it did not happen.
        if (payload.Shapes.Count == 0 && payload.Instances.Count == 0 && payload.Rulers.Count == 0) return;

        string json = LayoutFragment.Serialize(payload);

        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var renderTheme = LayoutRenderTheme.FromTheme(ThemeService.Active, variant);

        // Rich formats are best-effort; JSON text is always present as the fallback.
        byte[]?                         pdf = null;
        (string Svg, float W, float H)? svg = null;
        Bitmap?                         bmp = null;
        try
        {
            var ctx = new ExportContext(payload, tech, renderTheme, transparent, baseDir, planarMesh,
                                        currentDensity, drcMarkers, railMap, railTheme);
            pdf = TryRenderToPdf(ctx);
            svg = TryRenderToSvg(ctx);
            bmp = TryRenderToAvaloniaImage(ctx);
        }
        catch { /* best-effort */ }

        // ── Windows: bypass Avalonia, write all formats (incl. CF_ENHMETAFILE) in ONE P/Invoke
        //    session — see WindowsClipboard.cs's header comment for the full why. ──
        if (OperatingSystem.IsWindows())
        {
            var (pageW, pageH) = svg is { } s ? ClipboardPageSize(s.W, s.H) : (0f, 0f);
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
        Engine.Mom.PlanarCurrentDensityMap? CurrentDensity,
        IReadOnlyList<DrcMarker>? DrcMarkers = null,
        RailMapScene? RailMap = null,
        RailMapTheme? RailTheme = null);

    /// <summary>
    /// The page a clipboard picture is offered to Windows at, from the SVG flavour's own pixel size.
    /// </summary>
    /// <remarks>
    /// One copy of the arithmetic, because two copy paths that sized their pages differently would be
    /// a difference nobody chose: the cross-platform transfer carries no dimensions at all, so the
    /// Windows bypass is the only place a receiving application is TOLD how big the picture is, and it
    /// is told the same thing whichever of this application's copies produced it.
    /// </remarks>
    internal static (float W, float H) ClipboardPageSize(float svgW, float svgH)
    {
        const float maxSide = 720f;   // ≈10in at 72pt/in — Word/PowerPoint-friendly default
        float scale = MathF.Min(1f, maxSide / MathF.Max(svgW, svgH));
        return (svgW * scale, svgH * scale);
    }

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
    /// <param name="pageW">The flavour's own page width in device pixels — the SMALLER the page, the
    /// larger a <c>Fixed</c> ruler's world text, so each renderer passes its own rather than sharing
    /// one guess (see the two-pass note below).</param>
    /// <param name="pageH">That flavour's page height.</param>
    private static (double WorldW, double WorldH, double BbMinX, double BbMinY)? ComputeSelectionBounds(
        ExportContext ctx, double pageW = DefaultBoundsPageW, double pageH = DefaultBoundsPageH)
    {
        var bbox = Bbox.Empty;
        var conductorAt = LayoutPortDirection.LookupFor(ctx.Payload.Shapes);
        var visible = LayerVisibilityOf(ctx.Tech);

        foreach (var s in ctx.Payload.Shapes)
        {
            if (!visible(s.Layer)) continue;

            bbox = bbox.Union(LayoutGeometry.BboxOf(s));

            if (s is not LabelShape label) continue;

            // centred: label.IsPort — a port's name is PAINTED centred on its anchor (2026-08-25),
            // and this pass is about what will be painted. Measuring it left-anchored puts the box
            // half a text-width off, which is how ports got cropped off the page before.
            if (LayoutRenderer.MeasureLabelWorldBbox(label, label.IsPort) is { } textBb)
                bbox = bbox.Union(textBb);

            if (LayoutPortDirection.Resolve(conductorAt, label) is { } hint)
            {
                // The marker spans the conductor width across the direction and reaches into the
                // metal along it; take the whole square about the plane, which bounds both.
                long r = Math.Max(hint.WidthDbu, label.Height);
                // The mark's OWN centre: an interior port draws its ring at the label, not a bar at
                // the conductor end (LayoutPortDirection.PortHint.Interior), and a box round the end
                // would bound empty metal while leaving the ring outside the page.
                long mx = hint.Interior ? label.X : hint.PlaneX;
                long my = hint.Interior ? label.Y : hint.PlaneY;
                bbox = bbox.Union(new Bbox(mx - r, my - r, mx + r, my + r));
            }
        }

        foreach (var inst in ctx.Payload.Instances)
        {
            var ib = CellHierarchy.InstanceBbox(inst, ctx.BaseDir, visible);
            if (!ib.IsEmpty) bbox = bbox.Union(ib);
        }

        // ── R-rul-16: rulers, in EXACTLY TWO PASSES ───────────────────────────────────────────────
        //
        // A Fixed-mode ruler's readout is n screen POINTS, so its WORLD extent depends on the export
        // scale — which depends on these bounds, which depend on that extent. That is circular, and
        // this method's own doc comment above records the family of bug it belongs to: measuring a
        // label's STORED bbox (a point) instead of its painted glyphs is what cropped the owner's
        // ports off the page.
        //
        // Pass 1 unions the line endpoints and every SCALED ruler's measured text — neither depends
        // on the scale. Pass 2 measures the FIXED text at the scale pass 1 chose, and unions that.
        // The iteration is MONOTONE (a larger bbox means a smaller scale means smaller world-space
        // fixed text), so a second pass cannot make it worse and the residual is absorbed by the page
        // margin every renderer already applies. Do not iterate to a fixed point; do not skip pass 2.
        // R-rul-6: the SOURCE document's own display unit, carried in the payload — never a
        // hard-coded one here.
        var unit = ctx.Payload.DisplayUnit;
        int dbuPerMicron = ctx.Payload.DbuPerMicron > 0 ? ctx.Payload.DbuPerMicron : LayoutUnits.DefaultDbuPerMicron;

        foreach (var r in ctx.Payload.Rulers)
        {
            bbox = bbox.Union(new Bbox(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2),
                                       Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2)));
            if (r.SizeMode == RulerSizeMode.Scaled)
                bbox = bbox.Union(LayoutRenderer.MeasureRulerWorldBbox(r, unit, dbuPerMicron, 0));
        }

        // ── R-rail9-2: railRF's overlay is part of the PAINTED extent, and goes into THIS pass ──
        //
        // The same requirement as brief 8's R-rail8-7 (Zoom to Fit frames the union) seen from the
        // other side, and it is why the legend's placement is a correctness question rather than a
        // cosmetic one: a drop map is co-extensive with the copper, so this looks harmless until the
        // legend, a source marker or a flagged-via callout sits outside the copper's own bbox and the
        // page is sized without it. RailMapScene.Bounds is the scene's own union of everything it
        // paints, computed from UNCULLED content, so it is the same answer ContentBounds() gives the
        // canvas — one rule, not two.
        //
        // Unioned BEFORE the empty check, deliberately: a rail map over an empty selection is still a
        // picture, and returning null there would be R-rail9-5's "writes nothing" failure arriving by
        // a different route.
        if (ctx.RailMap is { } railMap && !railMap.Bounds.IsEmpty)
            bbox = bbox.Union(railMap.Bounds);

        if (bbox.IsEmpty) return null;

        // A legitimately ONE-DIMENSIONAL selection is not an empty one. A purely horizontal or
        // vertical ruler — which is most rulers — has zero extent on one axis, and the degenerate
        // check at the bottom of this method used to refuse the whole graphic export for it: no PDF,
        // no SVG, no bitmap, and nothing said. Give the flat axis a nominal extent so pass 2 has a
        // scale to work with; the real text measurement then dominates it. (The same refusal applied
        // to any 1-D selection and predates rulers — they are just what finally produced one.)
        bbox = InflateDegenerateAxes(bbox);

        if (ctx.Payload.Rulers.Any(r => r.SizeMode == RulerSizeMode.Fixed))
        {
            double pass1Zoom = ZoomForBounds(bbox, pageW, pageH);
            if (pass1Zoom > 0)
                foreach (var r in ctx.Payload.Rulers)
                    if (r.SizeMode == RulerSizeMode.Fixed)
                        bbox = bbox.Union(LayoutRenderer.MeasureRulerWorldBbox(r, unit, dbuPerMicron, pass1Zoom));
        }

        double worldW = bbox.MaxX - bbox.MinX;
        double worldH = bbox.MaxY - bbox.MinY;
        if (worldW < 1 || worldH < 1) return null;
        return (worldW, worldH, bbox.MinX, bbox.MinY);
    }

    /// <summary>
    /// Which layers the page is allowed to be sized by — <c>LayerDef.Visible</c>, the same flag
    /// <c>LayoutRenderer.Draw</c> gates each layer on.
    ///
    /// <para>Owner report, 2026-09-04: two shapes far apart on two layers, one layer hidden, pasted
    /// into Keynote as a mostly-empty page with the visible shape too small to read. The page was
    /// sized from the hidden shape as well, which nothing then painted — the export measured the
    /// SELECTION where the renderer draws the VISIBLE selection, and the two disagreed by however far
    /// apart the layers' geometry happened to be. The copy itself is unchanged: the JSON payload still
    /// carries every selected shape, so a paste back into a layout is unaffected — this decides only
    /// how big the picture is.</para>
    ///
    /// <para>With no technology (or on a layer the technology does not define) the renderer falls back
    /// to <c>FallbackPalette</c> and paints, so the answer is "visible" — the filter can only ever
    /// remove a layer that was explicitly turned off.</para>
    /// </summary>
    private static Func<LayerKey, bool> LayerVisibilityOf(Technology? tech)
    {
        if (tech is null) return static _ => true;

        var map = new Dictionary<LayerKey, bool>();
        foreach (var l in tech.Layers) map[l.Key] = l.Visible;
        return key => !map.TryGetValue(key, out bool v) || v;
    }

    /// <summary>Widens an axis with no extent to an eighth of the other, so a flat selection still
    /// gets a page. Never touches an axis that already has extent.</summary>
    private static Bbox InflateDegenerateAxes(Bbox bb)
    {
        long w = bb.MaxX - bb.MinX, h = bb.MaxY - bb.MinY;
        if (w >= 1 && h >= 1) return bb;

        long span = Math.Max(Math.Max(w, h), 1);
        long padX = w >= 1 ? 0 : Math.Max(1, span / 8);
        long padY = h >= 1 ? 0 : Math.Max(1, span / 8);
        return new Bbox(bb.MinX - padX, bb.MinY - padY, bb.MaxX + padX, bb.MaxY + padY);
    }

    /// <summary>The page margin every export flavour applies, as a fraction of the world extent.</summary>
    private const double ExportPad = 0.15;

    /// <summary>The most conservative page the three flavours use — the PDF's. Only reached by a
    /// caller that does not state its own (the test seam), and conservative in the safe direction: a
    /// smaller page means a smaller scale means LARGER world-space fixed text, so the bounds it
    /// produces contain what any larger page would paint.</summary>
    private const double DefaultBoundsPageW = 720.0;
    private const double DefaultBoundsPageH = 540.0;

    /// <summary>The one place the three renderers' shared zoom rule lives, so pass 1 of
    /// <see cref="ComputeSelectionBounds"/> cannot compute a different scale from the one that is
    /// actually used to draw.</summary>
    private static double ZoomForBounds(Bbox bbox, double pageW, double pageH)
    {
        double w = bbox.MaxX - bbox.MinX, h = bbox.MaxY - bbox.MinY;
        if (w < 1 || h < 1) return 0;
        return Math.Min(pageW / (w * (1 + 2 * ExportPad)), pageH / (h * (1 + 2 * ExportPad)));
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
        // R-rul-6: the readout renders in the SOURCE document's display unit, so a ruler copied out
        // of a mil-unit board says "mil" in the slide too.
        view.DisplayUnit = ctx.Payload.DisplayUnit;
        foreach (var s in ctx.Payload.Shapes) view.Shapes.Add(s);
        foreach (var i in ctx.Payload.Instances) view.Instances.Add(i);
        // §9B.9: one line, and the ruler then appears in the PDF, SVG and bitmap flavours with no
        // export-specific rendering code at all. Rulers are DOCUMENT CONTENT, so they survive the
        // export's suppression of grid/ghost/selection (which is overlay state); selection handles do
        // not, which is correct.
        foreach (var r in ctx.Payload.Rulers) view.Rulers.Add(r);
        return view;
    }

    /// <summary>Export-mode render options (R-L1f-5): no grid, no overlay (which alone already
    /// suppresses the ghost/selection outlines/handles/marquee), transparent background — plus the
    /// EM mesh when one is showing, the DRC markers when the caller supplied any (owner request:
    /// mirrors the mesh's own "rides along in the graphic, never in the JSON payload" contract), and
    /// the base directory instances resolve against.</summary>
    private static LayoutRenderOptions ExportOptions(ExportContext ctx) => new()
    {
        Theme = ctx.Theme,
        ShowGrid = false,
        Overlay = null,
        // ── AN EXPORT CARRIES THE STORED GEOMETRY, NOT THE SCREEN'S VIEW OF IT ─────────────────
        //
        // The level-of-detail tiers are keyed to DEVICE pixels, which a PDF or SVG page does not have
        // and a pasted bitmap may be rescaled away from — so what is STORED is what is drawn, exactly
        // as `circuitrf render --detail full` does. On a real board this is a visible difference, not
        // a theoretical one.
        //
        // ALL SEVEN, and that is a correction rather than belt-and-braces (2026-09-18,
        // brief-railrf-9-copy.md's own detail gate). Only DetailPixelThreshold was set here, and it
        // turns off the VERTEX-DECIMATION tier plus the two that document themselves as implied by it
        // — not the LOD, merge, stroke-elision, hairline-fill or coarse-coverage tiers, which read
        // their own knobs and were silently running at their interactive defaults. Measured on 240
        // stored 40 µm rects at page scale: 2 drawn elements, because every one of them was sub-pixel
        // and collapsed into a per-layer batched fill. That is the one direction where the mistake
        // produces a plausible picture — a picture of LESS geometry than the document holds, pasted
        // into a document where nobody can check it. `render --detail full` sets all seven for this
        // reason and says so; this is the same list, for the same reason.
        DetailPixelThreshold          = -1,
        LodPixelThreshold             = -1,
        MergeShapeCountThreshold      = -1,
        OutlineVertexBudget           = -1,
        InstanceRasterMaxDevicePixels = -1,
        StrokeElisionPixelThreshold   = -1,
        HairlineFillPixelThreshold    = -1,
        CoarseCoverageThreshold       = -1,
        TransparentBackground = ctx.Transparent,
        BaseDir = ctx.BaseDir,
        ShowPlanarMesh = ctx.PlanarMesh is not null,
        PlanarMesh = ctx.PlanarMesh,
        PlanarCurrentDensity = ctx.CurrentDensity,
        ShowDrcMarkers = ctx.DrcMarkers is { Count: > 0 },
        DrcMarkers = ctx.DrcMarkers,
        RailMap = ctx.RailMap,
        RailTheme = ctx.RailTheme,
    };

    /// <summary>Test seam: build the same context <see cref="CopyAsync"/> builds, so a gate can drive
    /// the real export path rather than a simplified stand-in.</summary>
    internal static ExportContext MakeExportContext(
        LayoutFragment.Payload payload, Technology? tech, LayoutRenderTheme theme, bool transparent,
        string baseDir = "", Engine.Mom.PlanarMeshReport? planarMesh = null,
        Engine.Mom.PlanarCurrentDensityMap? currentDensity = null,
        IReadOnlyList<DrcMarker>? drcMarkers = null,
        RailMapScene? railMap = null, RailMapTheme? railTheme = null)
        => new(payload, tech, theme, transparent, baseDir, planarMesh, currentDensity, drcMarkers,
               railMap, railTheme);

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
            var b = ComputeSelectionBounds(ctx, 720.0, 540.0);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad = ExportPad;
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
            var b = ComputeSelectionBounds(ctx, 800.0, 800.0);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad = ExportPad;
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
            // Skia writes each text run's per-glyph x/y list with a trailing separator, which Firefox
            // reads as invalid and drops - putting every run a line above its baseline, where the
            // clip eats it. Correct in Illustrator, Inkscape, Chrome and Safari; unreadable in
            // Firefox. See SvgFontNormalizer.RepairPositionLists.
            return (SvgFontNormalizer.RepairPositionLists(Encoding.UTF8.GetString(stream.DetachAsData().ToArray())),
                    (float)pxW, (float)pxH);
        }
        catch { return null; }
    }

    internal static Bitmap? TryRenderToAvaloniaImage(ExportContext ctx)
    {
        var tech = ctx.Tech;
        try
        {
            var b = ComputeSelectionBounds(ctx, 1200.0, 1200.0);
            if (b is null) return null;
            var (worldW, worldH, bbMinX, bbMinY) = b.Value;

            const double pad   = ExportPad;
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
