using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;


/// <summary>
/// Separable Skia renderer for the schematic canvas.
/// Accepts a Skia canvas + viewport parameters + model; draws everything.
/// No Avalonia types — keeps renderer re-skinnable.
/// </summary>
public static class SchematicRenderer
{
    private const double LodThreshold        = 6.0;
    // Labels/port markers fade out below this on-screen component width.
    //
    // Lowered TWICE, both times to the same owner ask ("keep the text rendering for 2 more zoom out
    // clicks of the scroll wheel before the text is no longer rendered"): 22.0 to 16.64 on
    // 2026-06-24, and 16.64 to 12.583 on 2026-08-20. Each step divides by 1.15**2 — the canvas's own
    // ZoomFactor squared — so "two scroll-wheel clicks" is arithmetic here rather than a number
    // somebody liked. It moves the Match Designer's network pane with it: that pane draws through
    // this renderer, which is the whole reason it was built on SchematicModel.
    private const double SimplifiedThreshold = 12.583;
    private const double MinGridSpacingPx    = 4.0;

    /// <summary>
    /// The nominal on-screen component width at one zoom level — the quantity both LOD thresholds
    /// above are compared against.
    /// </summary>
    public static double ComponentPixelWidth(double zoom) => zoom * 300.0;

    /// <summary>
    /// True when component LABELS are actually drawn at <paramref name="zoom"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so a hit-test cannot offer a click target for text the renderer is not drawing. The
    /// Match Designer's network pane opens an inline editor on a label double-click, and without this
    /// a click on where a faded-out label WOULD be would open an editor over nothing.
    /// </remarks>
    public static bool LabelsVisibleAt(double zoom) => ComponentPixelWidth(zoom) >= SimplifiedThreshold;

    private const double CoarseGridRatio     = 10.0;

    // Single switch point for symbol stroke joins (schematic + symbol editor).
    // Change to Miter/Bevel here to restyle all symbol corners at once.
    public  const SKStrokeJoin SymbolStrokeJoinStyle = SKStrokeJoin.Round;
    public  const SKStrokeCap  SymbolStrokeCapStyle  = SKStrokeCap.Round;

    private const float DotHalfSize = 5f;
    private const float PortBoxHalf = 8f;   // world units; chosen so zoom=1 → 8px (matches prior clamped appearance)
    private const float ConnDotHalf = 4f;

    // Label layout aliases — authoritative values live on SchematicComponent; these keep the
    // renderer readable without repeating the magic numbers.
    private const double LabelBaseOffsetX = SchematicComponent.LabelBaseOffsetX;
    private const double LabelBaseY       = SchematicComponent.LabelBaseY;
    private const double LabelWorldHeight = SchematicComponent.LabelWorldHeight;
    private const double LabelWorldStep   = SchematicComponent.LabelWorldStep;

    // ── Bitmap image cache ─────────────────────────────────────────────────────
    // Extracted to BitmapCache (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md §2,
    // R-bmp-1) — the ONE decode cache shared with LayoutRenderer. These two forwarders exist so no
    // existing caller (SchematicViewModel, SymbolEditorViewModel) needed to change.

    public static void InvalidateBitmapCache(string? path) => BitmapCache.Invalidate(path);

    public static (int Width, int Height)? TryGetBitmapPixelSize(string path) => BitmapCache.TryGetPixelSize(path);

    public static void Draw(
        SKCanvas canvas,
        (double W, double H) canvasSize,
        SchematicModel? model,
        SchematicSpatialIndex? index,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        SchematicOverlay? overlay = null,
        bool useTransparentBackground = false,
        bool excludeGrid = false)
    {
        canvas.Clear(useTransparentBackground ? SKColors.Transparent : theme.Background);

        if (model is null)
            return;

        if (!excludeGrid)
            DrawGrid(canvas, canvasSize, model.GridSize, panX, panY, zoom, theme);

        double vpMinX = panX;
        double vpMinY = panY;
        double vpMaxX = panX + canvasSize.W / zoom;
        double vpMaxY = panY + canvasSize.H / zoom;

        var visComps = new HashSet<int>();
        var visWires = new HashSet<int>();

        if (index is not null)
            index.QueryViewport(vpMinX, vpMinY, vpMaxX, vpMaxY, visComps, visWires);
        else
        {
            for (int i = 0; i < model.Components.Count; i++) visComps.Add(i);
            for (int i = 0; i < model.Wires.Count;      i++) visWires.Add(i);
        }

        double compPixW   = zoom * 300.0;
        bool isLod        = compPixW < LodThreshold;
        bool isSimplified = compPixW < SimplifiedThreshold;

        using var wirePaint       = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                   StrokeWidth = (float)Math.Max(1.0, zoom * 4),    Color = theme.Wire,
                                                   StrokeJoin  = SKStrokeJoin.Miter };
        using var bodyPaint       = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                   StrokeWidth = (float)Math.Max(1.0, zoom * 3),    Color = theme.SymbolLine,
                                                   StrokeJoin  = SymbolStrokeJoinStyle, StrokeCap = SymbolStrokeCapStyle };
        using var plusPaint       = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                   StrokeWidth = (float)Math.Max(1.0, zoom * 3),    Color = theme.SymbolPlus,
                                                   StrokeJoin  = SymbolStrokeJoinStyle, StrokeCap = SymbolStrokeCapStyle };
        using var lodPaint        = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.LodRect };
        using var dotPaint        = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.ConnectionDot };
        using var unconnPaint     = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                   StrokeWidth = (float)Math.Max(1.0, zoom * 2),    Color = theme.Warning };
        using var connPinPaint    = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.ConnectedPin };
        using var warnFillPaint   = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.Warning.WithAlpha(60) };
        using var warnStrokePaint = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                   StrokeWidth = (float)Math.Max(1.5, zoom * 2.5),  Color = theme.Warning };
        using var textFont        = new SKFont(SkiaFonts.PlexRegular, Math.Max(4f, (float)(zoom * LabelWorldHeight)));
        using var compNamePaint   = new SKPaint { IsAntialias = true,  Color = theme.ComponentNameText };
        using var instNamePaint   = new SKPaint { IsAntialias = true,  Color = theme.InstanceNameText };
        using var paramNamePaint  = new SKPaint { IsAntialias = true,  Color = theme.ParameterNameText };
        using var netLabelFont    = new SKFont(SkiaFonts.PlexItalic,  Math.Max(4f, (float)(zoom * 65.0)));
        using var netLabelPaint   = new SKPaint { IsAntialias = true,  Color = theme.NetLabelText };

        // ── Bitmaps (canvas objects, drawn behind wires and components) ──────────
        if (!isLod && model.Bitmaps.Count > 0)
            DrawBitmaps(canvas, model.Bitmaps, overlay?.CanvasObjectDragPositions, panX, panY, zoom, theme);

        // ── Wires ─────────────────────────────────────────────────────────────
        float unconnEndHalf = (float)Math.Max(3.0, zoom * PortBoxHalf);
        var wireDragPts = overlay?.WireDragPoints;

        // Reused path object — Rewind() resets it without reallocation.
        using var wirePath = new SKPath();

        foreach (int wi in visWires)
        {
            var w = model.Wires[wi];

            // During drag, use overridden point list if available; skip BB cull for moved wires.
            IReadOnlyList<(double X, double Y)> pts;
            if (wireDragPts is not null && wireDragPts.TryGetValue(w.Id, out var overridePts))
                pts = overridePts;
            else
            {
                if (!BbIntersects(w.BbMinX, w.BbMinY, w.BbMaxX, w.BbMaxY, vpMinX, vpMinY, vpMaxX, vpMaxY))
                    continue;
                pts = w.Points;
            }

            // Draw as a single connected path so miter joins close the corners cleanly.
            if (pts.Count >= 2)
            {
                wirePath.Rewind();
                var (ax0, ay0) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                wirePath.MoveTo(ax0, ay0);
                for (int pi = 1; pi < pts.Count; pi++)
                {
                    var (ax, ay) = ToPixel(pts[pi].X, pts[pi].Y, panX, panY, zoom);
                    wirePath.LineTo(ax, ay);
                }
                canvas.DrawPath(wirePath, wirePaint);
            }

            // Unconnected endpoint squares — use model connectivity (deferred to drag-end).
            // Guard on pts.Count >= 2 symmetrically for both endpoints.
            if (!isSimplified && !isLod && pts.Count >= 2)
            {
                if (!w.StartConnected)
                {
                    var (ex, ey) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                    canvas.DrawRect(SKRect.Create(ex - unconnEndHalf, ey - unconnEndHalf,
                        unconnEndHalf * 2, unconnEndHalf * 2), unconnPaint);
                }
                if (!w.EndConnected)
                {
                    var (ex, ey) = ToPixel(pts[^1].X, pts[^1].Y, panX, panY, zoom);
                    canvas.DrawRect(SKRect.Create(ex - unconnEndHalf, ey - unconnEndHalf,
                        unconnEndHalf * 2, unconnEndHalf * 2), unconnPaint);
                }
            }
        }

        // ── Pin-on-pin separation preview wires (synthetic — not yet in model) ─
        var popPreviews = overlay?.PinOnPinPreviewWires;
        if (popPreviews is not null)
        {
            foreach (var pts in popPreviews)
            {
                if (pts.Count < 2) continue;
                wirePath.Rewind();
                var (px0, py0) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                wirePath.MoveTo(px0, py0);
                for (int pi = 1; pi < pts.Count; pi++)
                {
                    var (px, py) = ToPixel(pts[pi].X, pts[pi].Y, panX, panY, zoom);
                    wirePath.LineTo(px, py);
                }
                canvas.DrawPath(wirePath, wirePaint);
            }
        }

        // ── Components ────────────────────────────────────────────────────────
        var compDragPos = overlay?.ComponentDragPositions;

        // Live dot key set for real-time port-connection suppression.
        // Uses overlay dots when dragging (live-recomputed geometry) so a port whose world
        // position coincides with a junction dot during a drag renders as connected (no red box),
        // even though the stale render model may still show it as Unconnected.
        // Uses model dots at rest (they agree with the model port states, so no visual change).
        var liveDotSrc = overlay?.ConnectionDotsOverride ?? model.ConnectionDots;
        HashSet<(long, long)>? liveDotKeys = null;
        if (liveDotSrc.Count > 0)
        {
            double gs = model.GridSize;
            liveDotKeys = new HashSet<(long, long)>(liveDotSrc.Count);
            foreach (var d in liveDotSrc)
                liveDotKeys.Add(((long)Math.Round(d.X / gs), (long)Math.Round(d.Y / gs)));
        }

        foreach (int ci in visComps)
        {
            var c = model.Components[ci];

            // During drag: use override position for moved components.
            double cx = c.X, cy = c.Y;
            if (compDragPos is not null && compDragPos.TryGetValue(c.Id, out var dragPos))
            {
                (cx, cy) = dragPos;
                // Approximate BB at new position; skip culling for moved items in the viewport.
                const double hb = 400.0;
                if (!BbIntersects(cx - hb, cy - hb, cx + hb, cy + hb, vpMinX, vpMinY, vpMaxX, vpMaxY))
                    continue;
            }
            else
            {
                if (!BbIntersects(c.FullBbMinX, c.FullBbMinY, c.FullBbMaxX, c.FullBbMaxY,
                                  vpMinX, vpMinY, vpMaxX, vpMaxY))
                    continue;
            }

            var (cpx, cpy) = ToPixel(cx, cy, panX, panY, zoom);

            // LOD substitutes a filled rectangle for a symbol too small to read. Both the DECISION and
            // the rectangle come from this component's OWN glyph box, never from the nominal built-in
            // size the zoom thresholds are expressed in.
            //
            // **An imported kit's symbol is routinely ten times a built-in's size** — one measured
            // real part is 3,275 x 3,375 world units against a built-in's ~300 x 400 — so a rectangle
            // sized from the nominal width replaced a part that was still 65 pixels across with a
            // 4-pixel speck, and the part read as having stopped rendering at exactly the zoom where
            // everything around it was still perfectly legible. Owner-reported.
            if (isLod)
            {
                double gwPx = Math.Max(0.0, c.GlyphBbMaxX - c.GlyphBbMinX) * zoom;
                double ghPx = Math.Max(0.0, c.GlyphBbMaxY - c.GlyphBbMinY) * zoom;

                // Only genuinely unreadable artwork is stood in for. A symbol still large on screen
                // is drawn — the cost of a handful of those is nothing next to a part disappearing.
                if (Math.Max(gwPx, ghPx) < LodThreshold)
                {
                    // Centred on the glyph, not on the component origin: a kit symbol's artwork is
                    // often nowhere near its origin, so the two are not interchangeable.
                    double dx = cx - c.X, dy = cy - c.Y;
                    var (gx0, gy0) = ToPixel(c.GlyphBbMinX + dx, c.GlyphBbMinY + dy, panX, panY, zoom);
                    float hw = (float)Math.Max(1.0, gwPx * 0.35);
                    float hh = (float)Math.Max(1.0, ghPx * 0.35);
                    float mx = (float)(gx0 + gwPx * 0.5), my = (float)(gy0 + ghPx * 0.5);
                    canvas.DrawRect(SKRect.Create(mx - hw, my - hh, hw * 2, hh * 2), lodPaint);
                    continue;
                }
            }

            // Body: dispatch on CellRefState for cell-reference components; built-in path unchanged.
            if (c.CellRefState is CellSymbolState.Resolved && c.CellRefPrimitives is not null)
            {
                DrawSymbol(canvas, c.CellRefPrimitives,
                    cx, cy, c.Rotation, c.MirrorX, panX, panY, zoom, theme,
                    applyForceReadable: true);
            }
            else if (c.CellRefState is CellSymbolState.NotFound)
            {
                DrawCellRefNotFoundGlyph(canvas, cx, cy, panX, panY, zoom,
                    warnStrokePaint, warnFillPaint, instNamePaint);
            }
            else if (c.CellRefState is CellSymbolState.PrimaryMissing)
            {
                DrawCellRefPrimaryMissingGlyph(canvas, cx, cy, panX, panY, zoom, bodyPaint);
            }
            else if (c.InstanceSymbol is not null)
            {
                DrawSymbol(canvas, c.InstanceSymbol.Primitives,
                    cx, cy, c.Rotation, c.MirrorX, panX, panY, zoom, theme,
                    applyForceReadable: true);
            }
            else
            {
                // Built-in component: existing path.
                // Body + polarity marks: DrawSymbol dispatches per-primitive to the right paint.
                // Plus-role primitives (e.g. VoltageSource +/−) are inside the same primitive list,
                // so the separate ForSymbolPlusSegments path is gone.
                DrawSymbol(canvas, BuiltInSymbols.Primitives(c.Symbol, PortCountOf(c)).Primitives,
                    cx, cy, c.Rotation, c.MirrorX, panX, panY, zoom, theme,
                    applyForceReadable: true);
                DrawVariadicPortLeads(canvas, c, cx, cy, panX, panY, zoom, bodyPaint);
            }

            // MW2 R-mw2-13 — a cell from ANOTHER workspace names its source beside the glyph, so a
            // user can see without clicking that this part of the schematic is not theirs. Marked
            // whether or not it resolves: a broken external reference most needs to say which
            // project it was supposed to come from, which "Not Found" alone never does.
            if (c.ExternalAlias is { Length: > 0 } alias && !isLod)
                DrawExternalAliasTag(canvas, alias, c, cx, cy, panX, panY, zoom, textFont, instNamePaint);

            // SL3 R-sl3-9 — the referenced cell's published interface changed since this instance was
            // placed. CHROME ONLY, R36 without exception: the glyph above is the librarian's new
            // symbol and it is the truth (R-sl3-1) — what is in doubt is whether the WIRES still mean
            // what they did, and that is exactly what a surround around the instance says.
            if (c.InterfaceChanged && !isLod)
                DrawInterfaceChangedMark(canvas, c, panX, panY, zoom, textFont, warnStrokePaint);

            // DisableState overlay (drawn on top of body)
            if (c.DisableState != DisableState.None && !isLod)
                DrawDisableOverlay(canvas, c, cx, cy, panX, panY, zoom, warnStrokePaint, warnFillPaint);

            if (!isSimplified)
            {
                DrawPortMarkers(canvas, c, cx, cy, panX, panY, zoom, unconnPaint, connPinPaint, liveDotKeys, model.GridSize);
                (double DX, double DY)? lblDrag = null;
                if (overlay?.LabelDragOffsets is { } ldo && ldo.TryGetValue(c.Id, out var ld))
                    lblDrag = ld;
                DrawLabels(canvas, c, cx, cy, panX, panY, zoom, textFont,
                    compNamePaint, instNamePaint, paramNamePaint, lblDrag);
            }
        }

        // ── Connection dots ───────────────────────────────────────────────────
        if (!isLod)
        {
            // During a drag the overlay carries live dots recomputed from the moving geometry;
            // use them so junction dots follow instead of lagging at their pre-drag positions.
            var dotsToDraw = overlay?.ConnectionDotsOverride ?? model.ConnectionDots;
            float dotHalf = (float)Math.Max(2.0, zoom * DotHalfSize);
            foreach (var dot in dotsToDraw)
            {
                if (dot.X < vpMinX - 20 || dot.X > vpMaxX + 20 ||
                    dot.Y < vpMinY - 20 || dot.Y > vpMaxY + 20) continue;
                var (dx, dy) = ToPixel(dot.X, dot.Y, panX, panY, zoom);
                canvas.DrawRect(SKRect.Create(dx - dotHalf, dy - dotHalf, dotHalf * 2, dotHalf * 2), dotPaint);
            }
        }

        // ── Net labels ────────────────────────────────────────────────────────
        if (!isLod && !isSimplified && netLabelFont.Size >= 4f)
        {
            foreach (var lbl in model.NetLabels)
            {
                // Live drag: track the wire via the overlay override; else use the committed position.
                double lwx = lbl.X, lwy = lbl.Y;
                if (overlay?.NetLabelDragPositions is { } nlp && nlp.TryGetValue(lbl.Id, out var op))
                    (lwx, lwy) = op;
                if (lwx < vpMinX - 200 || lwx > vpMaxX + 200 ||
                    lwy < vpMinY - 50  || lwy > vpMaxY + 50) continue;
                var (lx, ly) = ToPixel(lwx, lwy, panX, panY, zoom);
                canvas.DrawText(lbl.Name, lx, ly, SKTextAlign.Left, netLabelFont, netLabelPaint);
            }
        }

        // ── 6d overlay ────────────────────────────────────────────────────────
        if (overlay is not null)
            DrawOverlay(canvas, canvasSize, model, overlay, panX, panY, zoom, theme, isLod);
    }

    // ── DrawSymbol — generic primitive-list renderer ──────────────────────────

    /// <summary>
    /// Draws a symbol's primitive list through the component's LocalToPixel transform.
    /// When <paramref name="overridePaint"/> is non-null (ghost preview):
    ///   • only SymbolLine-role primitives are drawn, using overridePaint.
    ///   • SymbolPlus-role and all other roles are skipped (parity with the prior
    ///     ghost path that only called SchematicSymbols.For, not ForSymbolPlusSegments).
    /// Text and Bitmap are stubbed (no-op) — see TODO below.
    /// </summary>
    internal static void DrawSymbol(
        SKCanvas canvas,
        IReadOnlyList<SymbolPrimitive> primitives,
        double compX, double compY,
        SymbolRotation rotation, bool mirrorX,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        SKPaint? overridePaint = null,
        bool applyForceReadable = false)      // true only for schematic component instances
    {
        // Rotation in degrees for angle-bearing primitives (Arc, Sine).
        double rotDeg = rotation switch
        {
            SymbolRotation.R90  =>  90.0,
            SymbolRotation.R180 => 180.0,
            SymbolRotation.R270 => 270.0,
            _                   =>   0.0,
        };

        (float X, float Y) LP(double lx, double ly) =>
            LocalToPixel(lx, ly, compX, compY, rotation, mirrorX, panX, panY, zoom);

        foreach (var prim in primitives)
        {
            // Bitmap — skip in ghost mode; otherwise load and draw with component transform.
            if (prim is BitmapPrimitive bmp)
            {
                if (overridePaint is not null) continue;

                var (px0, py0) = LP(bmp.X,              bmp.Y);
                var (px1, py1) = LP(bmp.X + bmp.W,      bmp.Y);
                var (px2, py2) = LP(bmp.X,              bmp.Y + bmp.H);

                float pixW = (float)Math.Sqrt((px1 - px0) * (px1 - px0) + (py1 - py0) * (py1 - py0));
                float pixH = (float)Math.Sqrt((px2 - px0) * (px2 - px0) + (py2 - py0) * (py2 - py0));
                if (pixW < 2 || pixH < 2) continue;

                var skBmp = BitmapCache.Load(bmp.ImagePathRef);
                if (skBmp is null)
                {
                    // Broken link: dashed quad outline + X diagonals
                    var (px3, py3) = LP(bmp.X + bmp.W, bmp.Y + bmp.H);
                    using var dashEffect = SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0);
                    using var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = theme.Warning.WithAlpha(180), PathEffect = dashEffect };
                    using var outline = new SKPath();
                    outline.MoveTo(px0, py0); outline.LineTo(px1, py1);
                    outline.LineTo(px3, py3); outline.LineTo(px2, py2);
                    outline.Close();
                    canvas.DrawPath(outline, bp);
                    bp.PathEffect = null;
                    bp.StrokeWidth = 1f;
                    canvas.DrawLine(px0, py0, px3, py3, bp);
                    canvas.DrawLine(px1, py1, px2, py2, bp);
                }
                else
                {
                    byte alpha = (byte)Math.Clamp(bmp.Opacity * 255, 0, 255);
                    using var bmpPaint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(alpha) };
                    // Affine matrix: maps bitmap pixel coords → screen pixel coords.
                    // (0,0)→(px0,py0),  (srcW,0)→(px1,py1),  (0,srcH)→(px2,py2)
                    float srcW = skBmp.Width, srcH = skBmp.Height;
                    var mat = new SKMatrix(
                        scaleX: (px1 - px0) / srcW,  skewX:  (px2 - px0) / srcH,  transX: px0,
                        skewY:  (py1 - py0) / srcW,  scaleY: (py2 - py0) / srcH,  transY: py0,
                        persp0: 0, persp1: 0, persp2: 1);
                    int save = canvas.Save();
                    canvas.Concat(mat);
                    canvas.DrawBitmap(skBmp, 0, 0, bmpPaint);
                    canvas.RestoreToCount(save);
                }
                continue;
            }

            // TextPrimitive — drawn centered at its box center, rotated in place.
            if (prim is TextPrimitive txt)
            {
                // SchematicRenderTheme has no SymbolText color yet; SymbolText maps to SymbolLine.
                SKColor textColor = overridePaint?.Color ?? txt.ColorRole switch
                {
                    SymbolColorRole.SymbolPlus => theme.SymbolPlus,
                    _                          => theme.SymbolLine,
                };
                SKTypeface typeface = txt.FontStyle switch
                {
                    SymbolFontStyle.Bold      => SkiaFonts.PlexBold,
                    SymbolFontStyle.Italic    => SkiaFonts.PlexItalic,
                    SymbolFontStyle.Condensed => SkiaFonts.PlexLight,
                    _                         => SkiaFonts.PlexRegular,
                };
                float fontSize = Math.Max(1f, (float)(txt.FontSize * zoom));
                using var font   = new SKFont(typeface, fontSize);
                using var tPaint = new SKPaint { IsAntialias = true, Color = textColor };

                // Net glyph angle = component rotation + the text's own rotation (CW, screen Y-down).
                double textRotDeg = txt.Rotation switch
                {
                    SymbolRotation.R90  =>  90.0,
                    SymbolRotation.R180 => 180.0,
                    SymbolRotation.R270 => 270.0,
                    _                   =>   0.0,
                };
                double netDeg = rotDeg + textRotDeg;

                // Actual font metrics (px at this zoom) so the (Align,VAlign) corner lands EXACTLY on the
                // primitive's anchor, instead of the estimated box width that drifts with string length.
                font.GetFontMetrics(out var fm);
                float ascPx  = -fm.Ascent;                     // distance above baseline (px)
                float descPx =  fm.Descent;                    // distance below baseline (px)
                float boxHpx =  ascPx + descPx;
                float awPx   =  font.MeasureText(txt.Content);  // real advance width (px)

                // Anchor offset from the box centre, unrotated text frame, WORLD units.
                double ox = txt.Align switch
                {
                    SymbolTextAlign.Center => 0.0,
                    SymbolTextAlign.Right  => +awPx * 0.5 / zoom,
                    _                      => -awPx * 0.5 / zoom,   // Left
                };
                double oy = txt.VAlign switch
                {
                    SymbolTextVAlign.Top    => -boxHpx * 0.5 / zoom,
                    SymbolTextVAlign.Middle =>  0.0,
                    SymbolTextVAlign.Bottom => +boxHpx * 0.5 / zoom,
                    _                       => (-boxHpx * 0.5 + ascPx) / zoom,   // Baseline (legacy)
                };

                // Local box centre = Anchor − Rot(textRotation, offset); LP then applies the component
                // rotation/mirror/pan/zoom, so mirrored/rotated instances stay correct.
                var (orx, ory) = txt.Rotation switch
                {
                    SymbolRotation.R90  => (-oy,  ox),
                    SymbolRotation.R180 => (-ox, -oy),
                    SymbolRotation.R270 => ( oy, -ox),
                    _                   => ( ox,  oy),
                };
                var (cxp, cyp) = LP(txt.AnchorX - orx, txt.AnchorY - ory);

                // Readability auto-flip — schematic instances only, opt-in per text. Flip 180° about the
                // box centre (centred draw keeps it in place). Default ForceReadable=false ⇒ rigid.
                if (applyForceReadable && txt.ForceReadable)
                {
                    double n = ((netDeg % 360.0) + 360.0) % 360.0;
                    if (n > 90.0 && n <= 270.0) netDeg += 180.0;
                }

                float baselineDy = (ascPx - descPx) * 0.5f;   // baseline offset from box centre (px)

                int save = canvas.Save();
                canvas.Translate(cxp, cyp);
                canvas.RotateDegrees((float)netDeg);
                canvas.DrawText(txt.Content, 0f, baselineDy, SKTextAlign.Center, font, tPaint);
                canvas.RestoreToCount(save);
                continue;
            }

            // Determine role+tier of this vector primitive.
            (SymbolColorRole role, SymbolStrokeTier tier, bool filled) info = prim switch
            {
                LinePrimitive        l  => (l.ColorRole,  l.StrokeTier,  false),
                PolylinePrimitive    pl => (pl.ColorRole, pl.StrokeTier, false),
                RectPrimitive        r  => (r.ColorRole,  r.StrokeTier,  r.Filled),
                RoundedRectPrimitive rr => (rr.ColorRole, rr.StrokeTier, rr.Filled),
                CirclePrimitive      c  => (c.ColorRole,  c.StrokeTier,  c.Filled),
                EllipsePrimitive     e  => (e.ColorRole,  e.StrokeTier,  e.Filled),
                ArcPrimitive         a  => (a.ColorRole,  a.StrokeTier,  false),
                PolygonPrimitive     pg => (pg.ColorRole, pg.StrokeTier, pg.Filled),
                QuadCurvePrimitive   qc => (qc.ColorRole, qc.StrokeTier, false),
                CubicCurvePrimitive  cc => (cc.ColorRole, cc.StrokeTier, false),
                SinePrimitive              s  => (s.ColorRole,  s.StrokeTier,  false),
                ExponentialTaperPrimitive  et => (et.ColorRole, et.StrokeTier, et.Filled),
                _                             => (SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, false),
            };

            // Ghost mode: skip everything except SymbolLine (preserves parity with the
            // prior path that only drew SchematicSymbols.For segments, not plus marks).
            if (overridePaint is not null && info.role != SymbolColorRole.SymbolLine) continue;

            // Resolve paint.
            SKColor color = overridePaint?.Color ?? info.role switch
            {
                SymbolColorRole.SymbolPlus => theme.SymbolPlus,
                _                          => theme.SymbolLine,
            };
            float sw = overridePaint is not null
                ? overridePaint.StrokeWidth
                : (float)Math.Max(1.0, zoom * (info.tier == SymbolStrokeTier.Thick ? 5.0
                                             : info.tier == SymbolStrokeTier.Normal ? 3.0 : 1.5));

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style       = info.filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                Color       = color,
                StrokeWidth = sw,
                StrokeJoin  = SymbolStrokeJoinStyle,
                StrokeCap   = SymbolStrokeCapStyle,
            };
            if (overridePaint?.PathEffect is { } pe) paint.PathEffect = pe;

            switch (prim)
            {
                case LinePrimitive l:
                {
                    var (ax, ay) = LP(l.X1, l.Y1);
                    var (bx, by) = LP(l.X2, l.Y2);
                    canvas.DrawLine(ax, ay, bx, by, paint);
                    break;
                }

                case PolylinePrimitive pl:
                {
                    if (pl.Points.Count < 2) break;
                    using var path = new SKPath();
                    var (px0, py0) = LP(pl.Points[0][0], pl.Points[0][1]);
                    path.MoveTo(px0, py0);
                    for (int i = 1; i < pl.Points.Count; i++)
                    {
                        var (px, py) = LP(pl.Points[i][0], pl.Points[i][1]);
                        path.LineTo(px, py);
                    }
                    canvas.DrawPath(path, paint);
                    break;
                }

                case RectPrimitive r:
                {
                    // Transform all 4 corners and draw as a closed polygon path.
                    double hw = r.W * 0.5, hh = r.H * 0.5;
                    DrawQuadPath(canvas, paint,
                        LP(r.Cx - hw, r.Cy - hh), LP(r.Cx + hw, r.Cy - hh),
                        LP(r.Cx + hw, r.Cy + hh), LP(r.Cx - hw, r.Cy + hh));
                    break;
                }

                case RoundedRectPrimitive rr:
                {
                    // Approximated as a plain rect when rotated; only R0 gets corner rounding.
                    double hw = rr.W * 0.5, hh = rr.H * 0.5;
                    if (rotation == SymbolRotation.R0 && !mirrorX)
                    {
                        var (x0, y0) = LP(rr.Cx - hw, rr.Cy - hh);
                        var (x1, y1) = LP(rr.Cx + hw, rr.Cy + hh);
                        float rx = (float)(rr.Radius * zoom);
                        canvas.DrawRoundRect(new SKRoundRect(SKRect.Create(x0, y0, x1 - x0, y1 - y0), rx), paint);
                    }
                    else
                    {
                        DrawQuadPath(canvas, paint,
                            LP(rr.Cx - hw, rr.Cy - hh), LP(rr.Cx + hw, rr.Cy - hh),
                            LP(rr.Cx + hw, rr.Cy + hh), LP(rr.Cx - hw, rr.Cy + hh));
                    }
                    break;
                }

                case CirclePrimitive c:
                {
                    var (pcx, pcy) = LP(c.Cx, c.Cy);
                    float pr = (float)(c.R * zoom);
                    if (info.filled)
                        canvas.DrawCircle(pcx, pcy, pr, paint);
                    else
                        canvas.DrawCircle(pcx, pcy, pr, paint);
                    break;
                }

                case EllipsePrimitive e:
                {
                    var (pcx, pcy) = LP(e.Cx, e.Cy);
                    float prx = (float)(e.Rx * zoom);
                    float pry = (float)(e.Ry * zoom);
                    canvas.DrawOval(pcx, pcy, prx, pry, paint);
                    break;
                }

                case ArcPrimitive a:
                {
                    var (pcx, pcy) = LP(a.Cx, a.Cy);
                    float pr = (float)(a.R * zoom);
                    var oval = SKRect.Create(pcx - pr, pcy - pr, pr * 2, pr * 2);
                    double startWorld = mirrorX
                        ? (180.0 - a.StartDeg + rotDeg)
                        : (a.StartDeg + rotDeg);
                    double sweepWorld = mirrorX ? -a.SweepDeg : a.SweepDeg;
                    canvas.DrawArc(oval, (float)startWorld, (float)sweepWorld, useCenter: false, paint);
                    break;
                }

                case PolygonPrimitive pg:
                {
                    if (pg.Points.Count < 2) break;
                    using var path = new SKPath();
                    var (px0, py0) = LP(pg.Points[0][0], pg.Points[0][1]);
                    path.MoveTo(px0, py0);
                    for (int i = 1; i < pg.Points.Count; i++)
                    {
                        var (px, py) = LP(pg.Points[i][0], pg.Points[i][1]);
                        path.LineTo(px, py);
                    }
                    path.Close();
                    canvas.DrawPath(path, paint);
                    break;
                }

                case QuadCurvePrimitive qc:
                {
                    var (px0, py0) = LP(qc.P0X,   qc.P0Y);
                    var (pcx, pcy) = LP(qc.CtrlX, qc.CtrlY);
                    var (px2, py2) = LP(qc.P2X,   qc.P2Y);
                    using var path = new SKPath();
                    path.MoveTo(px0, py0);
                    path.QuadTo(pcx, pcy, px2, py2);
                    canvas.DrawPath(path, paint);
                    break;
                }

                case CubicCurvePrimitive cc:
                {
                    var (px0, py0) = LP(cc.P0X, cc.P0Y);
                    var (pc1x, pc1y) = LP(cc.C1X, cc.C1Y);
                    var (pc2x, pc2y) = LP(cc.C2X, cc.C2Y);
                    var (px3, py3) = LP(cc.P3X, cc.P3Y);
                    using var path = new SKPath();
                    path.MoveTo(px0, py0);
                    path.CubicTo(pc1x, pc1y, pc2x, pc2y, px3, py3);
                    canvas.DrawPath(path, paint);
                    break;
                }

                case SinePrimitive s:
                {
                    int N = Math.Max((int)Math.Ceiling(s.Cycles * Math.Max(s.PtsPerCycle, 1)), 2);
                    using var path = new SKPath();
                    for (int k = 0; k <= N; k++)
                    {
                        double t = (double)k / N;
                        double lx, ly;
                        if (s.Axis == SineAxis.Horizontal)
                        {
                            lx = s.Cx + (t - 0.5) * s.Length;
                            ly = s.Cy + s.Amp * Math.Sin(2 * Math.PI * s.Cycles * t);
                        }
                        else
                        {
                            ly = s.Cy + (t - 0.5) * s.Length;
                            lx = s.Cx + s.Amp * Math.Sin(2 * Math.PI * s.Cycles * t);
                        }
                        var (px, py) = LP(lx, ly);
                        if (k == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                    }
                    canvas.DrawPath(path, paint);
                    break;
                }

                case ExponentialTaperPrimitive et:
                {
                    if (et.L <= 0) break;
                    int N = Math.Max(et.NumPts, 2);
                    double wRatio = (et.W1 > 0 && et.W2 > 0) ? et.W2 / et.W1 : 1.0;
                    using var path = new SKPath();
                    // Top outline (t: 0→1)
                    for (int k = 0; k <= N; k++)
                    {
                        double t = (double)k / N;
                        double w = et.W1 * Math.Pow(wRatio, t);
                        double lx, ly;
                        if (et.Axis == SineAxis.Horizontal)
                        { lx = et.Cx + (t - 0.5) * et.L;  ly = et.Cy - w * 0.5; }
                        else
                        { ly = et.Cy + (t - 0.5) * et.L;  lx = et.Cx - w * 0.5; }
                        var (px, py) = LP(lx, ly);
                        if (k == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                    }
                    // Bottom outline (t: 1→0)
                    for (int k = N; k >= 0; k--)
                    {
                        double t = (double)k / N;
                        double w = et.W1 * Math.Pow(wRatio, t);
                        double lx, ly;
                        if (et.Axis == SineAxis.Horizontal)
                        { lx = et.Cx + (t - 0.5) * et.L;  ly = et.Cy + w * 0.5; }
                        else
                        { ly = et.Cy + (t - 0.5) * et.L;  lx = et.Cx + w * 0.5; }
                        var (px, py) = LP(lx, ly);
                        path.LineTo(px, py);
                    }
                    path.Close();
                    canvas.DrawPath(path, paint);
                    break;
                }

            }
        }
    }

    private static void DrawQuadPath(
        SKCanvas canvas, SKPaint paint,
        (float X, float Y) a, (float X, float Y) b,
        (float X, float Y) c, (float X, float Y) d)
    {
        using var path = new SKPath();
        path.MoveTo(a.X, a.Y);
        path.LineTo(b.X, b.Y);
        path.LineTo(c.X, c.Y);
        path.LineTo(d.X, d.Y);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// How many PORTS a placed component has, given how many PINS it draws — the number
    /// <see cref="BuiltInSymbols.Primitives(SymbolKind, int)"/> wants, and not the same thing as
    /// the pin count for every kind.
    ///
    /// <para>SDD and ZPort expose each port as a differential ± PAIR, so their pin count is 2N.
    /// A compact model's terminals are not ports (a four-terminal MOSFET is not a four-port), so
    /// <see cref="SymbolKind.VerilogA"/> is 1:1. Halving it — which this call site used to do for
    /// every kind — produced the whole reported VerilogA defect at once: a body sized for half the
    /// terminals, leads drawn only for the pins that half-sized symbol happens to have (so the rest
    /// have none), and, for a one-terminal model, <c>1/2 == 0</c> falling through to the two-port
    /// default and drawing a second lead to a pin that does not exist.</para>
    ///
    /// <para>Every non-variadic kind ignores the argument entirely, and SnP/Tuner never reach here
    /// (they carry a per-instance <c>InstanceSymbol</c>), so the pin count is the right answer for
    /// everything outside the one SDD/ZPort case.</para>
    /// </summary>
    private static int PortCountOf(SchematicComponent c) => PortCountOf(c.Symbol, c.Ports.Count);

    /// <summary>The rule above, reachable by pin count alone so it can be tested directly.</summary>
    internal static int PortCountOf(SymbolKind kind, int pinCount) =>
        kind is SymbolKind.Sdd or SymbolKind.ZPort ? pinCount / 2 : pinCount;

    // ── Variadic port lead stubs (ZPort, Sdd) ────────────────────────────────

    // Draws stub lines from each port tip to the component body edge.
    // SDD/ZPort body edges are at ±90; port lead stubs run from ±90 to the pin tip at ±200.
    // Ports at LocalX<0 are left ports; ports at LocalX>0 are right ports.
    private static void DrawVariadicPortLeads(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,
        double panX, double panY, double zoom,
        SKPaint paint)
    {
        if (c.Symbol is not (SymbolKind.ZPort or SymbolKind.Sdd)) return;
        DrawVariadicPortLeads(canvas, c.Symbol,
            c.Ports.Select(p => ((double)p.LocalX, (double)p.LocalY)).ToList(),
            compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom, paint);
    }

    /// <summary>
    /// The same stubs, reachable from a pin list alone — which is what the documentation artwork
    /// generator has (<see cref="Diagnostics.SymbolArtworkGenerator"/>). The doc figures must show
    /// exactly what the schematic shows, so both call this rather than each drawing its own leads.
    /// </summary>
    internal static void DrawVariadicPortLeads(
        SKCanvas canvas, SymbolKind kind, IReadOnlyList<(double X, double Y)> pins,
        double compX, double compY, SymbolRotation rotation, bool mirrorX,
        double panX, double panY, double zoom, SKPaint paint)
    {
        if (kind is not (SymbolKind.ZPort or SymbolKind.Sdd)) return;
        const float bodyEdge = 90f;
        foreach (var (lx, ly) in pins)
        {
            double innerX = lx < 0 ? -bodyEdge : bodyEdge;
            var (ax, ay) = LocalToPixel(lx,     ly, compX, compY, rotation, mirrorX, panX, panY, zoom);
            var (bx, by) = LocalToPixel(innerX, ly, compX, compY, rotation, mirrorX, panX, panY, zoom);
            canvas.DrawLine(ax, ay, bx, by, paint);
        }
    }

    // ── Port markers ─────────────────────────────────────────────────────────

    private static void DrawPortMarkers(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,          // explicit world position (may differ during drag)
        double panX, double panY, double zoom,
        SKPaint unconnPaint, SKPaint connPaint,
        HashSet<(long, long)>? liveDotKeys = null, double gridSize = 100.0)
    {
        float boxHalf  = (float)Math.Max(3.0, zoom * PortBoxHalf);
        float connHalf = (float)Math.Max(2.0, zoom * ConnDotHalf);

        foreach (var port in c.Ports)
        {
            var (px, py) = LocalToPixel(port.LocalX, port.LocalY, compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom);

            bool isConnected = port.State == PortConnectionState.Connected;
            // Live override: if a junction dot exists at this port's current world position
            // (e.g. pin-on-pin or pin-on-wire during a drag), treat the port as connected so
            // no red unconnected box appears where a dot is already drawn.
            if (!isConnected && liveDotKeys is not null)
            {
                var (wx, wy) = SchematicGeometry.LocalToWorld(port.LocalX, port.LocalY, compX, compY, c.Rotation, c.MirrorX);
                isConnected = liveDotKeys.Contains(
                    ((long)Math.Round(wx / gridSize), (long)Math.Round(wy / gridSize)));
            }

            if (!isConnected)
                canvas.DrawRect(SKRect.Create(px - boxHalf, py - boxHalf, boxHalf * 2, boxHalf * 2), unconnPaint);
            else
                canvas.DrawRect(SKRect.Create(px - connHalf, py - connHalf, connHalf * 2, connHalf * 2), connPaint);
        }
    }

    /// <summary>
    /// The UNCONNECTED port marker for one pin, at the size and shape <see cref="DrawPortMarkers"/>
    /// draws. Used by the documentation artwork generator, whose figures show a component as the
    /// user meets it in the palette — before anything is wired to it, so every pin is unconnected.
    /// </summary>
    internal static void DrawUnconnectedPortMarker(
        SKCanvas canvas, double localX, double localY,
        double compX, double compY, SymbolRotation rotation, bool mirrorX,
        double panX, double panY, double zoom, SKPaint unconnPaint)
    {
        float boxHalf = (float)Math.Max(3.0, zoom * PortBoxHalf);
        var (px, py) = LocalToPixel(localX, localY, compX, compY, rotation, mirrorX, panX, panY, zoom);
        canvas.DrawRect(SKRect.Create(px - boxHalf, py - boxHalf, boxHalf * 2, boxHalf * 2), unconnPaint);
    }

    /// <summary>Half-width, in WORLD units, of an unconnected port marker — for bounding-box fits.</summary>
    internal static float PortMarkerWorldHalf => PortBoxHalf;

    // ── Labels (left-aligned; order: type, name, params) ─────────────────────
    // Label index 0 = component/type name  → ComponentNameText
    // Label index 1 = instance name        → InstanceNameText
    // Label index 2+ = parameters          → ParameterNameText

    private static void DrawLabels(
        SKCanvas canvas, SchematicComponent c,
        double cx, double cy,
        double panX, double panY, double zoom,
        SKFont font,
        SKPaint compNamePaint, SKPaint instNamePaint, SKPaint paramNamePaint,
        (double DX, double DY)? dragDelta = null)
    {
        // All anchors are computed via the canonical helper so the renderer and hit-test
        // can never drift — SchematicComponent.LabelRowGeometry is the single source of truth.
        int portCount = c.Ports.Count / 2;
        for (int i = 0; i < c.Labels.Count; i++)
        {
            string label = c.Labels[i];
            if (string.IsNullOrEmpty(label)) continue;
            var (oDx, oDy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, i);
            if (dragDelta is { } dd) { oDx += dd.DX; oDy += dd.DY; }
            var (worldX, worldY, _, _) = SchematicComponent.LabelRowGeometry(cx, cy, i, oDx, oDy, c.Symbol, portCount, c.GlyphBbMaxY - c.Y);
            var (lx, ly) = ToPixel(worldX, worldY, panX, panY, zoom);
            var paint = i == 0 ? compNamePaint : (i == 1 ? instNamePaint : paramNamePaint);
            canvas.DrawText(label, lx, ly, SKTextAlign.Left, font, paint);
        }
    }

    // ── DisableState overlay ──────────────────────────────────────────────────

    private static void DrawDisableOverlay(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,          // explicit world position (may differ during drag)
        double panX, double panY, double zoom,
        SKPaint stroke, SKPaint hatchFill)
    {
        // Offset the baked glyph BB by the drag delta
        double dx = compX - c.X, dy = compY - c.Y;
        float pad = (float)(zoom * 18);
        var (ax, ay) = ToPixel(c.GlyphBbMinX + dx, c.GlyphBbMinY + dy, panX, panY, zoom);
        var (bx, by) = ToPixel(c.GlyphBbMaxX + dx, c.GlyphBbMaxY + dy, panX, panY, zoom);
        var rect = new SKRect(ax - pad, ay - pad, bx + pad, by + pad);

        canvas.DrawRect(rect, stroke);

        if (c.DisableState == DisableState.Open)
        {
            // X from corner to corner
            canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, stroke);
            canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, stroke);
        }
        else if (c.DisableState == DisableState.Short)
        {
            // Diagonal hatch fill
            canvas.Save();
            canvas.ClipRect(rect);
            float step = Math.Max(6f, (float)(zoom * 25));
            float h    = rect.Height;
            for (float d = rect.Left - h; d < rect.Right + h; d += step)
                canvas.DrawLine(d, rect.Top, d + h, rect.Bottom, hatchFill);
            canvas.Restore();
        }
    }

    // ── Cell-reference glyphs ─────────────────────────────────────────────────

    /// <summary>
    /// Draws the "Not Found" warning glyph for a cell-ref component whose cell folder
    /// does not resolve.  Warning box (fill + stroke) with centred "Not Found" label.
    /// </summary>
    /// <summary>
    /// The <c>[alias]</c> tag on an external cell instance (MW2 R-mw2-13). Drawn above the glyph's
    /// own bounding box, in the instance-name paint, so it reads as part of the instance's chrome
    /// rather than as one of its parameter labels — and so it never overlaps the label stack, which
    /// grows downward from the glyph's bottom.
    /// </summary>
    private static void DrawExternalAliasTag(
        SKCanvas canvas, string alias, SchematicComponent c,
        double cx, double cy, double panX, double panY, double zoom,
        SKFont font, SKPaint paint)
    {
        _ = cx; _ = cy;
        float x = (float)((c.GlyphBbMinX + panX) * zoom);
        float y = (float)((c.GlyphBbMinY + panY) * zoom) - 3f;
        canvas.DrawText($"[{alias}]", x, y, SKTextAlign.Left, font, paint);
    }

    /// <summary>
    /// The mark on an instance whose cell's interface changed since it was placed (SL3 R-sl3-9): a
    /// dashed surround around the glyph's own bounding box, plus a short tag, both in the warning
    /// paint. Drawn OUTSIDE the glyph BB so it can never be confused for part of the symbol, and
    /// below-right so it does not collide with the <c>[alias]</c> tag above-left — an external cell
    /// whose interface changed carries both, which is the ordinary case for a shared library.
    /// </summary>
    private static void DrawInterfaceChangedMark(
        SKCanvas canvas, SchematicComponent c,
        double panX, double panY, double zoom, SKFont font, SKPaint warnPaint)
    {
        float pad  = (float)(zoom * 40);
        float left = (float)((c.GlyphBbMinX + panX) * zoom) - pad;
        float top  = (float)((c.GlyphBbMinY + panY) * zoom) - pad;
        float right  = (float)((c.GlyphBbMaxX + panX) * zoom) + pad;
        float bottom = (float)((c.GlyphBbMaxY + panY) * zoom) + pad;

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = warnPaint.StrokeWidth,
            Color       = warnPaint.Color,
            PathEffect  = SKPathEffect.CreateDash([(float)(zoom * 30), (float)(zoom * 20)], 0),
        };
        canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), stroke);

        using var textPaint = new SKPaint { IsAntialias = true, Color = warnPaint.Color };
        canvas.DrawText("interface changed", right, bottom + font.Size, SKTextAlign.Right, font, textPaint);
    }

    private static void DrawCellRefNotFoundGlyph(
        SKCanvas canvas,
        double cx, double cy,
        double panX, double panY, double zoom,
        SKPaint strokePaint, SKPaint fillPaint, SKPaint textPaint)
    {
        float hw = (float)(zoom * 160);
        float hh = (float)(zoom * 60);
        var (px, py) = ToPixel(cx, cy, panX, panY, zoom);
        var rect = SKRect.Create(px - hw, py - hh, hw * 2, hh * 2);
        canvas.DrawRect(rect, fillPaint);
        canvas.DrawRect(rect, strokePaint);

        float fontSize = (float)Math.Max(6.0, zoom * 40);
        using var font  = new SKFont(SkiaFonts.PlexRegular, fontSize);
        canvas.DrawText("Not Found", px, py + font.Size * 0.4f, SKTextAlign.Center, font, textPaint);
    }

    /// <summary>
    /// Draws a plain-rectangle stand-in for a cell-ref component whose primary symbol
    /// is missing (PrimaryMissing state — cell resolves but no usable .csym).
    /// </summary>
    private static void DrawCellRefPrimaryMissingGlyph(
        SKCanvas canvas,
        double cx, double cy,
        double panX, double panY, double zoom,
        SKPaint strokePaint)
    {
        float hw = (float)(zoom * 160);
        float hh = (float)(zoom * 60);
        var (px, py) = ToPixel(cx, cy, panX, panY, zoom);
        canvas.DrawRect(SKRect.Create(px - hw, py - hh, hw * 2, hh * 2), strokePaint);
    }

    // ── Overlay (selection, wire preview, ghost, rubber-band) ────────────────

    private static void DrawOverlay(
        SKCanvas canvas,
        (double W, double H) canvasSize,
        SchematicModel model,
        SchematicOverlay overlay,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        bool isLod)
    {
        if (!isLod && (overlay.SelectedComponentIds.Count > 0 || overlay.SelectedWireIds.Count > 0))
        {
            using var selStroke = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 3),
                Color       = theme.SelectionBox,
                PathEffect  = SKPathEffect.CreateDash([6f, 4f], 0f),
            };
            using var selFill = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Fill,
                Color       = theme.SelectionFill,
            };

            foreach (var comp in model.Components)
            {
                if (!overlay.SelectedComponentIds.Contains(comp.Id)) continue;
                // Glyph BB from render model; shift by drag delta when a drag is active.
                double bbMinX = comp.GlyphBbMinX, bbMinY = comp.GlyphBbMinY;
                double bbMaxX = comp.GlyphBbMaxX, bbMaxY = comp.GlyphBbMaxY;
                if (overlay.ComponentDragPositions is not null &&
                    overlay.ComponentDragPositions.TryGetValue(comp.Id, out var dragPos))
                {
                    double ddx = dragPos.X - comp.X, ddy = dragPos.Y - comp.Y;
                    bbMinX += ddx; bbMinY += ddy;
                    bbMaxX += ddx; bbMaxY += ddy;
                }
                // 2px fixed pad gives a thin outline exactly around the hit boundary.
                const float pad = 2f;
                var (ax, ay) = ToPixel(bbMinX, bbMinY, panX, panY, zoom);
                var (bx, by) = ToPixel(bbMaxX, bbMaxY, panX, panY, zoom);
                var rect = SKRect.Create(ax - pad, ay - pad, bx - ax + pad * 2, by - ay + pad * 2);
                canvas.DrawRect(rect, selFill);
                canvas.DrawRect(rect, selStroke);
            }

            using var wSel = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(6.0, zoom * 14),
                Color       = theme.SelectionBox.WithAlpha(120),
                StrokeJoin  = SKStrokeJoin.Miter,
            };
            foreach (var wire in model.Wires)
            {
                if (!overlay.SelectedWireIds.Contains(wire.Id)) continue;
                // Use drag-override points while a drag is active so the highlight tracks movement.
                var pts = (overlay.WireDragPoints is not null &&
                           overlay.WireDragPoints.TryGetValue(wire.Id, out var dp))
                    ? dp : wire.Points;
                if (pts.Count < 2) continue;
                using var selPath = new SKPath();
                var (sx0, sy0) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                selPath.MoveTo(sx0, sy0);
                for (int pi = 1; pi < pts.Count; pi++)
                {
                    var (sx, sy) = ToPixel(pts[pi].X, pts[pi].Y, panX, panY, zoom);
                    selPath.LineTo(sx, sy);
                }
                canvas.DrawPath(selPath, wSel);
            }
        }

        // Selected wire segments (per-segment clicks — highlight each selected segment)
        var selSegs = overlay.SelectedWireSegments;
        if (selSegs.Count > 0 && !isLod)
        {
            using var segSelPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(6.0, zoom * 14),
                Color       = theme.SelectionBox.WithAlpha(120),
                StrokeJoin  = SKStrokeJoin.Miter,
            };
            foreach (var segSel in selSegs)
            {
                SchematicWire? segWire = null;
                foreach (var w in model.Wires)
                    if (w.Id == segSel.WireId) { segWire = w; break; }
                if (segWire is null) continue;

                var segPts = (overlay.WireDragPoints is not null &&
                              overlay.WireDragPoints.TryGetValue(segSel.WireId, out var sdp))
                    ? sdp : segWire.Points;
                if (segSel.SegmentIndex >= segPts.Count - 1) continue;

                var (sax, say) = ToPixel(segPts[segSel.SegmentIndex].X,     segPts[segSel.SegmentIndex].Y,     panX, panY, zoom);
                var (sbx, sby) = ToPixel(segPts[segSel.SegmentIndex + 1].X, segPts[segSel.SegmentIndex + 1].Y, panX, panY, zoom);
                canvas.DrawLine(sax, say, sbx, sby, segSelPaint);
            }
        }

        // Wire preview
        if (overlay.WirePreview is { Count: >= 2 } pts2)
        {
            using var previewPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 4),
                Color       = theme.WireRouting,
                PathEffect  = SKPathEffect.CreateDash([8f, 4f], 0f),
            };
            for (int i = 0; i < pts2.Count - 1; i++)
            {
                var (ax, ay) = ToPixel(pts2[i].X,     pts2[i].Y,     panX, panY, zoom);
                var (bx, by) = ToPixel(pts2[i + 1].X, pts2[i + 1].Y, panX, panY, zoom);
                canvas.DrawLine(ax, ay, bx, by, previewPaint);
            }
        }

        // Placement ghost — uses DrawSymbol with overridePaint so it reads the single
        // geometry source (BuiltInSymbols.Primitives) and draws only SymbolLine-role
        // primitives (matching the prior behaviour that called SchematicSymbols.For only).
        //
        // R-dup-1 gave this exactly one more caller rather than a second ghost style: the copy an Alt
        // drag is about to make is drawn by the same code, in the same paint, because it is the same
        // statement — "this is not in the model yet". A duplicate that looked different from a
        // placement would be a second visual language for one idea.
        if (!isLod && (overlay.Ghost is not null || overlay.DuplicateGhosts is { Count: > 0 }))
        {
            using var ghostPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 3),
                Color       = theme.GhostBody,
                PathEffect  = SKPathEffect.CreateDash([6f, 3f], 0f),
            };
            float ghostBoxHalf = (float)Math.Max(3.0, zoom * PortBoxHalf);
            using var ghostPortPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 2),
                Color       = theme.GhostBody,
            };

            void DrawOneGhost(PlacementGhost g)
            {
                var ghostPrimitives = g.ResolvedPrimitives
                    ?? BuiltInSymbols.Primitives(g.Symbol, g.PortCount).Primitives;
                DrawSymbol(canvas, ghostPrimitives,
                    g.X, g.Y, g.Rotation, g.MirrorX, panX, panY, zoom,
                    theme, ghostPaint);

                // Ghost port markers — same geometry source as DrawPortMarkers, ghost color (solid; no dash)
                if (g.ResolvedPins is { } resolvedPins)
                {
                    foreach (var pin in resolvedPins)
                    {
                        var (px, py) = LocalToPixel(pin.LocalX, pin.LocalY, g.X, g.Y, g.Rotation, g.MirrorX, panX, panY, zoom);
                        canvas.DrawRect(SKRect.Create(px - ghostBoxHalf, py - ghostBoxHalf, ghostBoxHalf * 2, ghostBoxHalf * 2), ghostPortPaint);
                    }
                }
                else
                {
                    foreach (var (_, lx, ly) in SymbolPortDefs.For(g.Symbol, g.PortCount))
                    {
                        var (px, py) = LocalToPixel(lx, ly, g.X, g.Y, g.Rotation, g.MirrorX, panX, panY, zoom);
                        canvas.DrawRect(SKRect.Create(px - ghostBoxHalf, py - ghostBoxHalf, ghostBoxHalf * 2, ghostBoxHalf * 2), ghostPortPaint);
                    }
                }
            }

            if (overlay.Ghost is { } ghost) DrawOneGhost(ghost);
            if (overlay.DuplicateGhosts is { } dupGhosts)
                foreach (var g in dupGhosts) DrawOneGhost(g);

            // The wire and canvas-object halves of a duplicate. Plain strokes in the same paint: a
            // wire has no symbol to resolve, and a bitmap ghost that painted its own image would be
            // indistinguishable from the copy it is promising to make.
            if (overlay.DuplicateGhostWires is { } dupWires)
            {
                foreach (var pts in dupWires)
                {
                    if (pts.Count < 2) continue;
                    using var path = new SKPath();
                    var (w0x, w0y) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                    path.MoveTo(w0x, w0y);
                    for (int i = 1; i < pts.Count; i++)
                    {
                        var (wx, wy) = ToPixel(pts[i].X, pts[i].Y, panX, panY, zoom);
                        path.LineTo(wx, wy);
                    }
                    canvas.DrawPath(path, ghostPaint);
                }
            }

            if (overlay.DuplicateGhostRects is { } dupRects)
            {
                foreach (var (rx, ry, rw, rh) in dupRects)
                {
                    var (x0, y0) = ToPixel(rx, ry, panX, panY, zoom);
                    var (x1, y1) = ToPixel(rx + rw, ry + rh, panX, panY, zoom);
                    canvas.DrawRect(new SKRect(x0, y0, x1, y1), ghostPaint);
                }
            }
        }

        // Canvas-object selection boxes and resize gripper.
        if (!isLod && overlay.SelectedCanvasObjIds.Count > 0)
        {
            using var bmSelDash = SKPathEffect.CreateDash([6f, 4f], 0f);
            using var bmSelStroke = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 3),
                Color       = theme.SelectionBox,
                PathEffect  = bmSelDash,
            };
            using var bmSelFill = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Fill,
                Color       = theme.SelectionFill,
            };

            foreach (var bm in model.Bitmaps)
            {
                if (!overlay.SelectedCanvasObjIds.Contains(bm.Id)) continue;
                double wx, wy, ww, wh;
                if (overlay.CanvasObjectDragPositions is not null &&
                    overlay.CanvasObjectDragPositions.TryGetValue(bm.Id, out var ov))
                    (wx, wy, ww, wh) = (ov.X, ov.Y, ov.W, ov.H);
                else
                    (wx, wy, ww, wh) = (bm.X, bm.Y, bm.Width, bm.Height);

                const float bmPad = 2f;
                var (ax, ay) = ToPixel(wx,      wy,      panX, panY, zoom);
                var (bx, by) = ToPixel(wx + ww, wy + wh, panX, panY, zoom);
                var rect = SKRect.Create(ax - bmPad, ay - bmPad, bx - ax + bmPad * 2, by - ay + bmPad * 2);
                canvas.DrawRect(rect, bmSelFill);
                canvas.DrawRect(rect, bmSelStroke);
            }
        }

        // Resize gripper handle — small filled accent square at bottom-right of single selected bitmap.
        if (!isLod && overlay.CanvasObjectGripperPos is { } grip)
        {
            var (ghx, ghy) = ToPixel(grip.X, grip.Y, panX, panY, zoom);
            float gs = (float)Math.Max(4f, zoom * 6.0);
            using var gripFill = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Fill,
                Color       = theme.SelectionBox,
            };
            using var gripStroke = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,   Color = theme.Background,
            };
            var gripRect = SKRect.Create(ghx - gs * 0.5f, ghy - gs * 0.5f, gs, gs);
            canvas.DrawRect(gripRect, gripFill);
            canvas.DrawRect(gripRect, gripStroke);
        }

        // Rubber-band — solid outline for window (L→R), dashed for crossing (R→L)
        if (overlay.RubberBand is { } rb)
        {
            using var rbFill   = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.RubberBandFill };
            using var rbStroke = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = theme.RubberBandStroke };
            if (overlay.RubberBandCrossing)
                rbStroke.PathEffect = SKPathEffect.CreateDash([6f, 4f], 0f);
            var (ax, ay) = ToPixel(rb.X,         rb.Y,         panX, panY, zoom);
            var (bx, by) = ToPixel(rb.X + rb.W,  rb.Y + rb.H,  panX, panY, zoom);
            var rect = SKRect.Create(ax, ay, bx - ax, by - ay);
            canvas.DrawRect(rect, rbFill);
            canvas.DrawRect(rect, rbStroke);
        }
    }

    // ── Grid ─────────────────────────────────────────────────────────────────

    private static void DrawGrid(
        SKCanvas canvas, (double W, double H) size,
        double gridWorld, double panX, double panY, double zoom,
        SchematicRenderTheme theme)
    {
        double finePx = zoom * gridWorld;
        if (finePx < MinGridSpacingPx) return;

        double spacingWorld;
        byte gridAlpha;
        if (finePx < MinGridSpacingPx * 3)
        {
            spacingWorld = gridWorld * CoarseGridRatio;
            gridAlpha    = 45;
        }
        else
        {
            spacingWorld = gridWorld;
            gridAlpha    = 35;
        }

        double spacingPx = zoom * spacingWorld;
        if (spacingPx < MinGridSpacingPx) return;

        using var paint = new SKPaint
        {
            IsAntialias = false, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f, Color = theme.Grid.WithAlpha(gridAlpha),
        };

        double firstWorldX = Math.Ceiling(panX / spacingWorld) * spacingWorld;
        double startPx     = (firstWorldX - panX) * zoom;
        int    vCount      = (int)Math.Ceiling((size.W - startPx) / spacingPx) + 2;
        for (int i = 0; i < vCount && i < 600; i++)
        {
            float px = (float)(startPx + i * spacingPx);
            if (px < -1 || px > size.W + 1) continue;
            canvas.DrawLine(px, 0f, px, (float)size.H, paint);
        }

        double firstWorldY = Math.Ceiling(panY / spacingWorld) * spacingWorld;
        double startPy     = (firstWorldY - panY) * zoom;
        int    hCount      = (int)Math.Ceiling((size.H - startPy) / spacingPx) + 2;
        for (int j = 0; j < hCount && j < 600; j++)
        {
            float py = (float)(startPy + j * spacingPx);
            if (py < -1 || py > size.H + 1) continue;
            canvas.DrawLine(0f, py, (float)size.W, py, paint);
        }
    }

    // ── Canvas-object rendering ───────────────────────────────────────────────

    private static void DrawBitmaps(
        SKCanvas canvas,
        IReadOnlyList<SchematicBitmap> bitmaps,
        IReadOnlyDictionary<string, (double X, double Y, double W, double H)>? overrides,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme)
    {
        foreach (var bm in bitmaps)
        {
            double wx, wy, ww, wh;
            if (overrides is not null && overrides.TryGetValue(bm.Id, out var ov))
                (wx, wy, ww, wh) = (ov.X, ov.Y, ov.W, ov.H);
            else
                (wx, wy, ww, wh) = (bm.X, bm.Y, bm.Width, bm.Height);

            var (px0, py0) = ToPixel(wx,        wy,        panX, panY, zoom);
            var (px1, py1) = ToPixel(wx + ww,   wy + wh,   panX, panY, zoom);
            float pixW = px1 - px0;
            float pixH = py1 - py0;
            if (pixW < 2 || pixH < 2) continue;

            var skBmp = BitmapCache.Load(bm.ImagePath);
            if (skBmp is null)
            {
                BitmapCache.DrawBrokenPlaceholder(canvas, px0, py0, pixW, pixH, theme.Warning);
            }
            else
            {
                byte alpha = (byte)Math.Clamp(bm.Opacity * 255, 0, 255);
                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(alpha) };
                canvas.DrawBitmap(skBmp, new SKRect(px0, py0, px1, py1), paint);
            }
        }
    }

    // ── Transform helpers ─────────────────────────────────────────────────────

    private static (float X, float Y) ToPixel(double wx, double wy, double panX, double panY, double zoom)
        => ((float)((wx - panX) * zoom), (float)((wy - panY) * zoom));

    // internal: PaletteGlyphControl reuses this directly so a palette tile's port leads land at
    // exactly the same pixel positions the real schematic renderer (DrawVariadicPortLeads) would
    // produce — one conversion, not a second hand-copied one.
    internal static (float X, float Y) LocalToPixel(
        float lx, float ly,
        double compX, double compY,
        SymbolRotation rot, bool mirrorX,
        double panX, double panY, double zoom)
    {
        float mlx = mirrorX ? -lx : lx;
        (double rx, double ry) = rot switch
        {
            SymbolRotation.R90  => (-(double)ly,  (double)mlx),
            SymbolRotation.R180 => (-(double)mlx, -(double)ly),
            SymbolRotation.R270 => ((double)ly,   -(double)mlx),
            _                   => ((double)mlx,  (double)ly),
        };
        return ((float)((compX + rx - panX) * zoom),
                (float)((compY + ry - panY) * zoom));
    }

    // Double-coordinate overload — used by DrawSymbol for double-precision local coords.
    private static (float X, float Y) LocalToPixel(
        double lx, double ly,
        double compX, double compY,
        SymbolRotation rot, bool mirrorX,
        double panX, double panY, double zoom)
    {
        double mlx = mirrorX ? -lx : lx;
        (double rx, double ry) = rot switch
        {
            SymbolRotation.R90  => (-ly,  mlx),
            SymbolRotation.R180 => (-mlx, -ly),
            SymbolRotation.R270 => ( ly,  -mlx),
            _                   => ( mlx,  ly),
        };
        return ((float)((compX + rx - panX) * zoom),
                (float)((compY + ry - panY) * zoom));
    }

    private static bool BbIntersects(
        double minX, double minY, double maxX, double maxY,
        double vpMinX, double vpMinY, double vpMaxX, double vpMaxY)
        => maxX >= vpMinX && minX <= vpMaxX && maxY >= vpMinY && minY <= vpMaxY;
}
