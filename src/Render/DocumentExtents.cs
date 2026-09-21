using SkiaSharp;

using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Render;

/// <summary>
/// A world-space rectangle in a document's OWN coordinate space — integer DBU for a layout,
/// dimensionless design units for a schematic or a symbol.
/// </summary>
public readonly record struct WorldRect(double X0, double Y0, double X1, double Y1)
{
    public double W => X1 - X0;
    public double H => Y1 - Y0;
}

/// <summary>
/// <b>How big is this document?</b> — asked once, answered once (RND-3 R-rnd3-9).
///
/// <para><b>Why this is a shared function and not two.</b> <c>circuitrf render --fit</c> frames a page
/// from this box and <c>circuitrf explain --extents</c> REPORTS it, and a caller uses the second to
/// compute a window for the first. If the two could disagree the reported number would be worse than
/// useless — so there is one measurement here, below the UI firewall, called by both, and nothing
/// re-derives it.</para>
///
/// <para><b>Two boxes, not one, and the distinction is the whole design.</b> Some of what a frame
/// paints is measured in PIXELS at render time and therefore has no world extent until a zoom is
/// chosen: a symbol pin's name (drawn at a size with a floor — see
/// <c>ComponentPreviewRaster.RasterSymbol</c>) and a <see cref="RulerSizeMode.Fixed"/> ruler's readout
/// (n screen points, so its world width depends on the scale, which depends on the bounds). So:</para>
/// <list type="bullet">
/// <item><see cref="LayoutBox"/> / <see cref="SymbolBox"/> — the GEOMETRY, zoom-independent. This is
/// what a zoom-independent verb reports, and it is the number that is stable across page sizes.</item>
/// <item><see cref="LayoutFitBox"/> / <see cref="SymbolFitBox"/> — that box plus the room those
/// render-time marks need at the page being drawn. This is what a fit is solved against, so nothing
/// measured in pixels falls off the edge.</item>
/// </list>
///
/// <para>On a document carrying neither kind of mark — which is most of them, and every fixture the
/// gates compare — the two are the same box.</para>
/// </summary>
public static class DocumentExtents
{
    /// <summary><c>LayoutViewport.ZoomToFit</c>'s own default, which is the margin the application's
    /// Zoom to Fit uses. Shared with the schematic and symbol halves so a fitted picture of one
    /// document kind is framed like a fitted picture of another.</summary>
    public const double DefaultMargin = 0.10;

    // ── layout ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A layout's geometry extents, in its own DBU: every shape on a VISIBLE layer, the world box of
    /// every label's painted text, the conductor-direction hint a port label draws, every instance
    /// placement's recursive box, and every ruler whose extent does not depend on the scale.
    ///
    /// <para><b>Labels, port hints and instances are unioned in explicitly</b> because none of them is
    /// in the primitive bounding box: a label's glyphs extend past its anchor, a port label draws a
    /// direction hint at the conductor it names, and an instance's content lives in another file. Each
    /// is the same union <c>LayoutClipboard.ComputeSelectionBounds</c> makes, and its header records
    /// why.</para>
    ///
    /// <para><b>Visibility is <see cref="LayerDef.Visible"/></b>, the same flag
    /// <c>LayoutRenderer.Draw</c> gates each layer on. Sizing a page from geometry nothing then paints
    /// produces a mostly-empty picture with the visible content too small to read.</para>
    /// </summary>
    public static Bbox LayoutBox(LayoutView view, Technology? tech, string baseDir)
    {
        var bbox = Bbox.Empty;
        var conductorAt = LayoutPortDirection.LookupFor(view.Shapes);
        var visible = LayerVisibility(tech);

        foreach (var s in view.Shapes)
        {
            if (!visible(s.Layer)) continue;
            bbox = bbox.Union(LayoutGeometry.BboxOf(s));

            if (s is not LabelShape label) continue;
            if (LayoutRenderer.MeasureLabelWorldBbox(label, label.IsPort) is { } textBb)
                bbox = bbox.Union(textBb);
            if (LayoutPortDirection.Resolve(conductorAt, label) is { } hint)
            {
                long rad = Math.Max(hint.WidthDbu, label.Height);
                // The mark's OWN centre: an interior port draws its ring at the label, not a bar at
                // the conductor end (LayoutPortDirection.PortHint.Interior), and a box round the end
                // would bound empty metal while leaving the ring outside the page.
                long mx = hint.Interior ? label.X : hint.PlaneX;
                long my = hint.Interior ? label.Y : hint.PlaneY;
                bbox = bbox.Union(new Bbox(mx - rad, my - rad, mx + rad, my + rad));
            }
        }

        foreach (var inst in view.Instances)
        {
            var ib = CellHierarchy.InstanceBbox(inst, baseDir, visible);
            if (!ib.IsEmpty) bbox = bbox.Union(ib);
        }

        // brief-footprint-4b R-fp4b-5d — a placement's reference designator is drawn OUTSIDE its
        // body (above it, by construction), so a fit computed without it crops the designators off
        // the top row of a board. It is derived per frame rather than stored, which is exactly why
        // it has to be asked for here rather than arriving with view.Shapes: out of the same one
        // function the renderer and every export use, never a second estimate of where it lands.
        foreach (var label in FootprintLabel.ShapesFor(view, baseDir, tech, null))
        {
            if (!visible(label.Layer)) continue;
            if (LayoutRenderer.MeasureLabelWorldBbox(label) is { } db) bbox = bbox.Union(db);
        }

        foreach (var ruler in view.Rulers)
        {
            bbox = bbox.Union(new Bbox(Math.Min(ruler.X1, ruler.X2), Math.Min(ruler.Y1, ruler.Y2),
                                       Math.Max(ruler.X1, ruler.X2), Math.Max(ruler.Y1, ruler.Y2)));
            if (ruler.SizeMode == RulerSizeMode.Scaled)
                bbox = bbox.Union(LayoutRenderer.MeasureRulerWorldBbox(
                    ruler, view.DisplayUnit, view.DbuPerMicron, 0));
        }

        if (bbox.IsEmpty) return bbox;

        // A legitimately ONE-DIMENSIONAL document is not an empty one — a purely horizontal ruler, or a
        // single zero-height trace, has no extent on one axis and would otherwise be refused outright.
        return InflateDegenerateAxes(bbox);
    }

    /// <summary>
    /// <see cref="LayoutBox"/> plus the room a <see cref="RulerSizeMode.Fixed"/> ruler's readout needs
    /// at this page size — the box a FIT is solved against.
    ///
    /// <para><b>Exactly two passes</b>, for the reason <c>LayoutRulerRenderer</c>'s own method records:
    /// a Fixed-mode ruler's readout is n screen POINTS, so its world extent depends on the scale, which
    /// depends on these bounds. Pass one is the geometry; pass two measures the Fixed text at the scale
    /// pass one chose. The iteration is monotone, so a second pass cannot make it worse — do not
    /// iterate to a fixed point, and do not skip pass two.</para>
    ///
    /// <para><b>Identical to <see cref="LayoutBox"/> on a document with no Fixed ruler in it</b>, which
    /// is what makes the two verbs' answers comparable on ordinary documents.</para>
    /// </summary>
    public static Bbox LayoutFitBox(
        LayoutView view, Technology? tech, string baseDir, double pxW, double pxH,
        double margin = DefaultMargin)
    {
        var bbox = LayoutBox(view, tech, baseDir);
        if (bbox.IsEmpty) return bbox;
        if (!view.Rulers.Any(r => r.SizeMode == RulerSizeMode.Fixed)) return bbox;

        var pass1 = LayoutViewport.ZoomToFit(bbox, pxW, pxH, marginFrac: margin);
        if (pass1.Zoom <= 0) return bbox;

        foreach (var ruler in view.Rulers)
            if (ruler.SizeMode == RulerSizeMode.Fixed)
                bbox = bbox.Union(LayoutRenderer.MeasureRulerWorldBbox(
                    ruler, view.DisplayUnit, view.DbuPerMicron, pass1.Zoom));

        return bbox;
    }

    /// <summary>
    /// <see cref="LayoutBox"/> restricted to one layer at a time, for the layers that have geometry.
    ///
    /// <para><b>Only the document's OWN shapes</b>, and that is a deliberate limit rather than an
    /// oversight: an instance's content is measured through <see cref="CellHierarchy.InstanceBbox"/>,
    /// which returns ONE box for the whole placement and cannot be split by layer without a second
    /// walk that would be free to disagree with the first. A per-layer box is a "where is my metal"
    /// question about the artwork in this file, and answering it with a number that silently folds in
    /// a sub-cell's box would be worse than not answering it.</para>
    /// </summary>
    public static IReadOnlyList<(LayerKey Key, Bbox Box)> LayoutBoxPerLayer(LayoutView view, Technology? tech)
    {
        var visible = LayerVisibility(tech);
        var conductorAt = LayoutPortDirection.LookupFor(view.Shapes);
        var boxes = new Dictionary<LayerKey, Bbox>();

        foreach (var s in view.Shapes)
        {
            if (!visible(s.Layer)) continue;

            var bb = LayoutGeometry.BboxOf(s);
            if (s is LabelShape label)
            {
                if (LayoutRenderer.MeasureLabelWorldBbox(label, label.IsPort) is { } textBb)
                    bb = bb.Union(textBb);
                if (LayoutPortDirection.Resolve(conductorAt, label) is { } hint)
                {
                    long rad = Math.Max(hint.WidthDbu, label.Height);
                    // The mark's OWN centre: an interior port draws its ring at the label, not a bar at
                // the conductor end (LayoutPortDirection.PortHint.Interior), and a box round the end
                // would bound empty metal while leaving the ring outside the page.
                    long mx = hint.Interior ? label.X : hint.PlaneX;
                    long my = hint.Interior ? label.Y : hint.PlaneY;
                    bb = bb.Union(new Bbox(mx - rad, my - rad, mx + rad, my + rad));
                }
            }

            boxes[s.Layer] = boxes.TryGetValue(s.Layer, out var have) ? have.Union(bb) : bb;
        }

        return [.. boxes.Where(kv => !kv.Value.IsEmpty)
                        .OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype)
                        .Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>Which layers a page may be sized by. Null technology means every layer, which is what
    /// the fallback palette renders (nothing is hidden when nothing declares it hidden).</summary>
    public static Func<LayerKey, bool> LayerVisibility(Technology? tech)
    {
        if (tech is null) return static _ => true;
        var map = new Dictionary<LayerKey, bool>();
        foreach (var l in tech.Layers) map[l.Key] = l.Visible;
        return key => !map.TryGetValue(key, out bool v) || v;
    }

    private static Bbox InflateDegenerateAxes(Bbox bb)
    {
        long w = bb.MaxX - bb.MinX, h = bb.MaxY - bb.MinY;
        if (w >= 1 && h >= 1) return bb;

        long span = Math.Max(Math.Max(w, h), 1);
        long padX = w >= 1 ? 0 : Math.Max(1, span / 8);
        long padY = h >= 1 ? 0 : Math.Max(1, span / 8);
        return new Bbox(bb.MinX - padX, bb.MinY - padY, bb.MaxX + padX, bb.MaxY + padY);
    }

    // ── schematic ────────────────────────────────────────────────────────────

    /// <summary>
    /// What the schematic renderer will paint. The render model's own bbox covers components and
    /// wires; bitmaps and net labels are unioned separately because neither contributes to it — a
    /// bitmap-only selection otherwise sizes to a dummy extent, and a long net label near the edge is
    /// clipped. <c>SchematicClipboard.BuildSelectionModel</c> is where both were learned.
    ///
    /// <para>Null when there is nothing to draw, or when what there is has no extent on an axis — which
    /// is a different fact from a box at the origin and must not be reported as one.</para>
    /// </summary>
    public static WorldRect? SchematicBox(SchematicEditModel model, SchematicModel rm)
    {
        bool hasCompWire = model.Components.Count > 0 || model.Wires.Count > 0;
        double x0, y0, x1, y1;
        if (hasCompWire) { x0 = rm.BbMinX; y0 = rm.BbMinY; x1 = rm.BbMaxX; y1 = rm.BbMaxY; }
        else             { x0 = y0 = double.MaxValue; x1 = y1 = double.MinValue; }

        foreach (var bm in rm.Bitmaps)
        {
            x0 = Math.Min(x0, bm.X);              y0 = Math.Min(y0, bm.Y);
            x1 = Math.Max(x1, bm.X + bm.Width);   y1 = Math.Max(y1, bm.Y + bm.Height);
        }

        foreach (var nl in rm.NetLabels)
        {
            x0 = Math.Min(x0, nl.X);
            y0 = Math.Min(y0, nl.Y - 55.0);
            x1 = Math.Max(x1, nl.X + Math.Max(1, nl.Name.Length) * 40.0);
            y1 = Math.Max(y1, nl.Y + 20.0);
        }

        if (x0 == double.MaxValue) return null;
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return new WorldRect(x0, y0, x1, y1);
    }

    // ── symbol ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A symbol's GEOMETRY box: its primitives, unioned with its pin ANCHORS. Zoom-independent, and
    /// therefore the answer a zoom-independent caller gets.
    ///
    /// <para><b>The pin NAMES are deliberately absent.</b> They are drawn in pixels at a size with a
    /// floor, so they have no world extent until a zoom is chosen — see <see cref="SymbolFitBox"/>,
    /// which is where a fit puts them.</para>
    ///
    /// <para>A symbol with no extent on an axis is inflated rather than refused: a single horizontal
    /// line is a legitimate symbol.</para>
    /// </summary>
    public static WorldRect? SymbolBox(Design.Symbol.Symbol symbol)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        if (symbol.Primitives.Count > 0)
        {
            var (p0, q0, p1, q1) = SymbolGeometry.ComputeBb(symbol.Primitives);
            minX = p0; minY = q0; maxX = p1; maxY = q1;
        }
        foreach (var pin in symbol.Pins)
        {
            minX = Math.Min(minX, pin.LocalX); minY = Math.Min(minY, pin.LocalY);
            maxX = Math.Max(maxX, pin.LocalX); maxY = Math.Max(maxY, pin.LocalY);
        }
        if (minX == double.MaxValue) return null;
        if (maxX - minX < 1e-9) { minX -= 50; maxX += 50; }
        if (maxY - minY < 1e-9) { minY -= 50; maxY += 50; }
        return new WorldRect(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// <see cref="SymbolBox"/> with room for the pin NAMES at this page size — the box a fit is SOLVED
    /// against rather than computed from: each pass measures the marks at the current zoom, widens the
    /// box by what they need, and re-fits at the smaller zoom that results. It converges downward in
    /// two or three passes and is capped.
    ///
    /// <para><b>The two errors this pass has already made once</b>, both recorded on
    /// <c>ComponentPreviewRaster.RasterSymbol</c> and both available to make again here:</para>
    /// <list type="number">
    /// <item><b>Reserving room on the wrong side.</b> A name drawn on the LEFT with room reserved on
    /// the right is the off-centre error made twice — room where nothing is drawn AND none where
    /// something is. The side comes from the pin's own <c>NameAlign</c>, measured, never assumed.</item>
    /// <item><b>Reserving the widest label at the rightmost pin.</b> Wrong the moment the longest name
    /// is not the rightmost pin, which is the ordinary case — a THERMAL pad among pads called 1..8.
    /// Each pin is measured AT ITS OWN POSITION.</item>
    /// </list>
    /// </summary>
    public static WorldRect SymbolFitBox(
        Design.Symbol.Symbol symbol, WorldRect body, int pxW, int pxH, double margin = DefaultMargin)
    {
        double x0 = body.X0, y0 = body.Y0, x1 = body.X1, y1 = body.Y1;
        double zoom = FitZoom(x1 - x0, y1 - y0, pxW, pxH, margin);
        if (zoom <= 0 || symbol.Pins.Count == 0) return body;

        for (int pass = 0; pass < 3; pass++)
        {
            float fontSize = (float)Math.Max(8.0, zoom * 12.0);
            float dot      = (float)Math.Max(3.0, zoom * 5.0);
            using var font = new SKFont(SkiaFonts.PlexBold, fontSize);

            double a0 = x0, b0 = y0, a1 = x1, b1 = y1;
            foreach (var pin in symbol.Pins)
            {
                string label = pin.Name is { Length: > 0 } n ? n : $"P{pin.PortIndex + 1}";
                double text = font.MeasureText(label);
                var (left, right, up, down) = pin.NameAlign switch
                {
                    SymbolPinNameAlign.Right  => (dot + 2 + text, (double)dot, (double)dot, (double)(fontSize * 0.85)),
                    SymbolPinNameAlign.Center => (text / 2, text / 2, (double)dot, (double)(fontSize * 0.85)),
                    SymbolPinNameAlign.Top    => ((double)(fontSize * 0.85), (double)(fontSize * 0.85), (double)dot, dot + 2 + text),
                    SymbolPinNameAlign.Bottom => ((double)(fontSize * 0.85), (double)(fontSize * 0.85), dot + 2 + text, (double)dot),
                    _                         => ((double)dot, dot + 2 + text, (double)dot, (double)(fontSize * 0.85)),
                };
                a0 = Math.Min(a0, pin.LocalX - left  / zoom);
                b0 = Math.Min(b0, pin.LocalY - up    / zoom);
                a1 = Math.Max(a1, pin.LocalX + right / zoom);
                b1 = Math.Max(b1, pin.LocalY + down  / zoom);
            }

            double next = FitZoom(a1 - a0, b1 - b0, pxW, pxH, margin);
            x0 = a0; y0 = b0; x1 = a1; y1 = b1;
            if (next <= 0) break;
            if (Math.Abs(next - zoom) / zoom < 0.01) break;
            zoom = next;
        }
        return new WorldRect(x0, y0, x1, y1);
    }

    /// <summary>The screen-sense fit zoom: output units per world unit, with the margin taken off both
    /// sides. Shared so a schematic, a symbol and a layout are framed alike.</summary>
    public static double FitZoom(double w, double h, int pxW, int pxH, double margin)
        => Math.Min(pxW / Math.Max(w, 1e-9), pxH / Math.Max(h, 1e-9)) * (1.0 - 2.0 * margin);
}
