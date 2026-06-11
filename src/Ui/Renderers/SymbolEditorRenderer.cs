using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Static Skia renderer for the Symbol Editor canvas.
/// No Avalonia types — re-skinnable.
/// Renders the symbol via the shared SchematicRenderer.DrawSymbol, then overlays
/// the fine-grid, selection bboxes (offset by LiveDragOffset), rubber-band rect,
/// and pin markers with port labels + an unmapped-port info panel (4c).
/// </summary>
internal static class SymbolEditorRenderer
{
    // Fine authoring grid: p = P/20 = 5 local units. Coarse = P = 100.
    private const double FineGrid   =   5.0;
    private const double CoarseGrid = 100.0;

    private const double MinGridPx  =   4.0;

    public static void Draw(
        SKCanvas canvas,
        (double W, double H) size,
        Symbol? symbol,
        SymbolEditorOverlay overlay,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme)
    {
        canvas.Clear(theme.Background);

        DrawGrid(canvas, size, panX, panY, zoom, theme);

        if (symbol is null) return;

        // Draw the symbol at local origin, no rotation, using the editor pan/zoom.
        SchematicRenderer.DrawSymbol(
            canvas, symbol.Primitives,
            compX: 0, compY: 0,
            rotation: SymbolRotation.R0, mirrorX: false,
            panX: panX, panY: panY, zoom: zoom,
            theme: theme);

        // Draw pin markers (small filled circles at each pin location, with port labels).
        DrawPinMarkers(canvas, symbol.Pins, overlay, panX, panY, zoom, theme);

        // Draw in-progress primitive as a ghost/preview (dashed, semi-transparent).
        if (overlay.InProgressPrimitive is { } inProg)
        {
            using var ghostPaint = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                Color       = theme.GhostBody,
                StrokeWidth = (float)Math.Max(1.5, zoom * 3.0),
                PathEffect  = SKPathEffect.CreateDash([8f, 4f], 0f),
            };
            SchematicRenderer.DrawSymbol(
                canvas, [inProg],
                compX: 0, compY: 0,
                rotation: SymbolRotation.R0, mirrorX: false,
                panX: panX, panY: panY, zoom: zoom,
                theme: theme,
                overridePaint: ghostPaint);
        }

        // Draw selection overlay.
        DrawSelectionOverlay(canvas, symbol, overlay, panX, panY, zoom, theme);

        // Draw unmapped port info panel in bottom-left corner (non-blocking, informational).
        if (overlay.UnmappedPortIndices.Count > 0)
            DrawUnmappedPortPanel(canvas, size, overlay.UnmappedPortIndices, theme);
    }

    // ── Grid ──────────────────────────────────────────────────────────────────

    private static void DrawGrid(
        SKCanvas canvas, (double W, double H) size,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme)
    {
        double finePx   = zoom * FineGrid;
        double coarsePx = zoom * CoarseGrid;

        double spacingWorld;
        byte alpha;
        if (coarsePx < MinGridPx)
            return;
        if (finePx >= MinGridPx)
        {
            spacingWorld = FineGrid;
            alpha        = 30;
        }
        else
        {
            spacingWorld = CoarseGrid;
            alpha        = 45;
        }

        double spacingPx = zoom * spacingWorld;
        using var paint = new SKPaint
        {
            IsAntialias = false, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,    Color = theme.Grid.WithAlpha(alpha),
        };

        double firstX  = Math.Ceiling(panX / spacingWorld) * spacingWorld;
        double startPx = (firstX - panX) * zoom;
        int vCount = (int)Math.Ceiling((size.W - startPx) / spacingPx) + 2;
        for (int i = 0; i < vCount && i < 600; i++)
        {
            float px = (float)(startPx + i * spacingPx);
            if (px < -1 || px > size.W + 1) continue;
            canvas.DrawLine(px, 0f, px, (float)size.H, paint);
        }

        double firstY  = Math.Ceiling(panY / spacingWorld) * spacingWorld;
        double startPy = (firstY - panY) * zoom;
        int hCount = (int)Math.Ceiling((size.H - startPy) / spacingPx) + 2;
        for (int j = 0; j < hCount && j < 600; j++)
        {
            float py = (float)(startPy + j * spacingPx);
            if (py < -1 || py > size.H + 1) continue;
            canvas.DrawLine(0f, py, (float)size.W, py, paint);
        }

        // Coarse grid second pass when fine grid is shown.
        if (spacingWorld == FineGrid && coarsePx >= MinGridPx)
        {
            using var coarsePaint = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,    Color = theme.Grid.WithAlpha(55),
            };
            double cStartPx = (Math.Ceiling(panX / CoarseGrid) * CoarseGrid - panX) * zoom;
            int    cVCount  = (int)Math.Ceiling((size.W - cStartPx) / coarsePx) + 2;
            for (int i = 0; i < cVCount && i < 100; i++)
            {
                float px = (float)(cStartPx + i * coarsePx);
                if (px < -1 || px > size.W + 1) continue;
                canvas.DrawLine(px, 0f, px, (float)size.H, coarsePaint);
            }
            double cStartPy = (Math.Ceiling(panY / CoarseGrid) * CoarseGrid - panY) * zoom;
            int    cHCount  = (int)Math.Ceiling((size.H - cStartPy) / coarsePx) + 2;
            for (int j = 0; j < cHCount && j < 100; j++)
            {
                float py = (float)(cStartPy + j * coarsePx);
                if (py < -1 || py > size.H + 1) continue;
                canvas.DrawLine(0f, py, (float)size.W, py, coarsePaint);
            }
        }
    }

    // ── Pin markers ───────────────────────────────────────────────────────────

    private static void DrawPinMarkers(
        SKCanvas canvas, IReadOnlyList<SymbolPin> pins,
        SymbolEditorOverlay overlay,
        double panX, double panY, double zoom, SchematicRenderTheme theme)
    {
        if (pins.Count == 0) return;

        float r        = (float)Math.Max(3.0, zoom * 5.0);
        float strokeW  = (float)Math.Max(1.0, zoom * 1.5);
        float fontSize = (float)Math.Max(8.0, zoom * 12.0);

        var selPins        = overlay.SelectedPinIndices;
        var (pinDx, pinDy) = overlay.PinLiveDragOffset;

        using var fillNormal   = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                               Color = theme.ConnectedPin };
        using var fillSelected = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                               Color = theme.SelectionBox };
        using var strokeNormal = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                               StrokeWidth = strokeW, Color = theme.Wire };
        using var strokeSel    = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                               StrokeWidth = strokeW * 1.5f, Color = theme.SelectionBox };
        using var ringFill     = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                               Color = theme.SelectionBox.WithAlpha(30) };
        using var ringStroke   = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                               StrokeWidth = 2f, Color = theme.SelectionBox };
        using var textPaint    = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                               Color = theme.ComponentNameText };
        using var labelFont    = new SKFont(SkiaFonts.PlexBold, fontSize);

        for (int i = 0; i < pins.Count; i++)
        {
            var pin  = pins[i];
            bool sel = selPins.Contains(i);
            double drawX = pin.LocalX + (sel ? pinDx : 0);
            double drawY = pin.LocalY + (sel ? pinDy : 0);

            float sx = (float)((drawX - panX) * zoom);
            float sy = (float)((drawY - panY) * zoom);

            // Selection ring — drawn behind the pin dot so the dot sits inside the halo.
            if (sel)
            {
                canvas.DrawCircle(sx, sy, r + 6f, ringFill);
                canvas.DrawCircle(sx, sy, r + 6f, ringStroke);
            }

            canvas.DrawCircle(sx, sy, r, sel ? fillSelected : fillNormal);
            canvas.DrawCircle(sx, sy, r, sel ? strokeSel    : strokeNormal);

            // Port label: pin name if set, else "P<portIndex+1>".
            string lbl = pin.Name is { Length: > 0 } n ? n : $"P{pin.PortIndex + 1}";
            canvas.DrawText(lbl, sx + r + 2, sy + fontSize * 0.35f,
                            SKTextAlign.Left, labelFont, textPaint);
        }

        // Ghost circle at drag destination for each selected pin being dragged.
        if ((pinDx != 0 || pinDy != 0) && selPins.Count > 0)
        {
            using var ghostPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeW, Color = theme.GhostBody,
                PathEffect  = SKPathEffect.CreateDash([4f, 3f], 0f),
            };
            foreach (int si in selPins)
            {
                if (si < 0 || si >= pins.Count) continue;
                var p  = pins[si];
                float gx = (float)((p.LocalX + pinDx - panX) * zoom);
                float gy = (float)((p.LocalY + pinDy - panY) * zoom);
                canvas.DrawCircle(gx, gy, r, ghostPaint);
            }
        }
    }

    // ── Unmapped port info panel ──────────────────────────────────────────────

    private static void DrawUnmappedPortPanel(
        SKCanvas canvas, (double W, double H) size,
        IReadOnlyList<int> unmapped, SchematicRenderTheme theme)
    {
        const float PadX = 8f, PadY = 6f, LineH = 16f, TextSz = 11f;

        using var bg = new SKPaint
        {
            IsAntialias = false, Style = SKPaintStyle.Fill,
            Color       = new SKColor(0xFF, 0xFF, 0xCC, 180),
        };
        using var border = new SKPaint
        {
            IsAntialias = false, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,   Color = new SKColor(0xCC, 0xCC, 0x60, 200),
        };
        using var headerPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                              Color = new SKColor(0x60, 0x40, 0x00, 230) };
        using var textPaint   = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                              Color = new SKColor(0x50, 0x50, 0x00, 230) };
        using var headerFont  = new SKFont(SkiaFonts.PlexBold,    TextSz);
        using var bodyFont    = new SKFont(SkiaFonts.PlexRegular,  TextSz);

        float panelH = PadY * 2 + LineH * (unmapped.Count + 1);
        float panelW = 180f;
        float panelX = PadX;
        float panelY = (float)size.H - panelH - PadY;

        var rect = SKRect.Create(panelX, panelY, panelW, panelH);
        canvas.DrawRect(rect, bg);
        canvas.DrawRect(rect, border);

        float ty = panelY + PadY + TextSz;
        canvas.DrawText("Unmapped ports (open):", panelX + PadX, ty,
                        SKTextAlign.Left, headerFont, headerPaint);
        ty += LineH;

        foreach (int pi in unmapped)
        {
            canvas.DrawText($"  Port {pi + 1} → open circuit", panelX + PadX, ty,
                            SKTextAlign.Left, bodyFont, textPaint);
            ty += LineH;
        }
    }

    // ── Selection overlay ──────────────────────────────────────────────────────

    private static void DrawSelectionOverlay(
        SKCanvas canvas, Symbol symbol, SymbolEditorOverlay overlay,
        double panX, double panY, double zoom, SchematicRenderTheme theme)
    {
        var selected = overlay.SelectedIndices;
        var (dx, dy) = overlay.LiveDragOffset;

        if (selected.Count > 0)
        {
            // Wire-style re-stroke: thick semi-transparent accent painted over the primitive's
            // own geometry — mirrors SchematicRenderer wire-selection paint.
            using var selPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(6.0, zoom * 8.0),
                Color       = theme.SelectionBox.WithAlpha(120),
                StrokeJoin  = SKStrokeJoin.Round,
                StrokeCap   = SKStrokeCap.Round,
            };
            // Box highlight kept for Text primitives and for the fill background.
            using var boxStroke = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 1.5),
                Color       = theme.SelectionBox,
                PathEffect  = SKPathEffect.CreateDash([4f, 4f], 0f),
            };
            using var boxFill = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Fill,
                Color       = theme.SelectionFill,
            };

            foreach (int idx in selected)
            {
                if (idx < 0 || idx >= symbol.Primitives.Count) continue;
                var prim = symbol.Primitives[idx];

                if (prim is TextPrimitive)
                {
                    // Text: corrected bbox box highlight (Layer 7).
                    var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(prim);
                    bx0 += dx; by0 += dy; bx1 += dx; by1 += dy;
                    float sx0 = (float)((bx0 - panX) * zoom);
                    float sy0 = (float)((by0 - panY) * zoom);
                    float sx1 = (float)((bx1 - panX) * zoom);
                    float sy1 = (float)((by1 - panY) * zoom);
                    float margin = (float)Math.Max(2.0, zoom * 2.0);
                    var rect = SKRect.Create(sx0 - margin, sy0 - margin,
                                             sx1 - sx0 + 2 * margin, sy1 - sy0 + 2 * margin);
                    canvas.DrawRect(rect, boxFill);
                    canvas.DrawRect(rect, boxStroke);
                }
                else
                {
                    // All other primitives: re-stroke actual geometry with selection accent.
                    // Shift panX/panY so the overlay tracks the live-drag offset.
                    SchematicRenderer.DrawSymbol(
                        canvas, [prim],
                        compX: 0, compY: 0,
                        rotation: SymbolRotation.R0, mirrorX: false,
                        panX: panX - dx, panY: panY - dy, zoom: zoom,
                        theme: theme,
                        overridePaint: selPaint);
                }
            }
        }

        // Resize gripper handle — small filled accent square at bottom-right.
        if (overlay.ResizeHandle.HasValue)
        {
            var (hx, hy) = overlay.ResizeHandle.Value;
            float ghx = (float)((hx - panX) * zoom);
            float ghy = (float)((hy - panY) * zoom);
            float gs  = (float)Math.Max(4f, zoom * 6.0);
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

        if (overlay.RubberBand.HasValue)
        {
            var (rx0, ry0, rx1, ry1) = overlay.RubberBand.Value;
            float sx0 = (float)((rx0 - panX) * zoom);
            float sy0 = (float)((ry0 - panY) * zoom);
            float sx1 = (float)((rx1 - panX) * zoom);
            float sy1 = (float)((ry1 - panY) * zoom);
            var rbRect = SKRect.Create(sx0, sy0, sx1 - sx0, sy1 - sy0);

            using var rbFill = new SKPaint
            { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.RubberBandFill };
            using var rbStroke = new SKPaint
            { IsAntialias = true,  Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
              Color = theme.RubberBandStroke };

            canvas.DrawRect(rbRect, rbFill);
            canvas.DrawRect(rbRect, rbStroke);
        }
    }
}
