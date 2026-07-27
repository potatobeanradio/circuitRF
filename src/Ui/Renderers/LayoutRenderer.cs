using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>Render-time knobs for <see cref="LayoutRenderer.Draw"/>.</summary>
public readonly struct LayoutRenderOptions
{
    public LayoutRenderTheme Theme { get; init; }
    public bool ShowGrid { get; init; }

    /// <summary>The in-progress draw ghost (L1b), or null. Drawn above every committed layer in its
    /// own layer's resolved color, with a dashed outline — never contributes to
    /// <see cref="LayoutRenderResult.UnknownLayers"/> (it is provisional, not committed geometry).</summary>
    public LayoutOverlay? Overlay { get; init; }

    public static LayoutRenderOptions Default(LayoutRenderTheme theme) => new() { Theme = theme, ShowGrid = true };
}

/// <summary>
/// Layer keys encountered during this <see cref="LayoutRenderer.Draw"/> call that a resolved
/// <see cref="Technology"/> did not define (docs/design/layout-view.md §2.4 gap-fill). Empty when
/// there is no technology at all — that is the normal "everything is fallback" case, not a warning.
/// The caller (the canvas / view model) is responsible for deduping against what has already been
/// warned about "once per layer per load" and posting to Messages — this is a pure render call and
/// never posts anything itself.
/// </summary>
public readonly record struct LayoutRenderResult(IReadOnlyList<LayerKey> UnknownLayers);

/// <summary>
/// Separable Skia renderer for the layout canvas (docs/design/layout-view.md §2.3/§3.2, L1a brief).
/// No Avalonia types. Draws 10³–10⁶ shapes; <see cref="SchematicRenderer.DrawSymbol"/> is NOT reused
/// here — see the brief's §0 for why (per-primitive path construction does not scale to layout counts).
///
/// <b>Coordinate convention (R-L1a-1/2 — read before touching this file):</b> Layout coordinates are
/// 64-bit integer DBU; <c>SKPath</c> is float32 (24-bit mantissa, ~16.7M distinct values), so feeding
/// raw DBU straight into a path quantizes badly far from the origin. Instead:
/// <list type="bullet">
/// <item>Paths are built in "path space": <c>(dbu - origin) * dbuToUm</c> (<see cref="PathSpace"/>),
/// where <c>origin</c> is a per-frame anchor near the viewport centre (quantized to a coarse step so
/// it changes rarely — see <see cref="ComputeOrigin"/>). Magnitudes are then bounded by the visible
/// extent in micrometres, not by absolute position — small at every zoom level, however far from
/// (0,0) the design sits.</item>
/// <item>Path space is built Y-DOWN (screen sense) even though the layout's own coordinate system is
/// Y-UP (physical/GDSII convention, <see cref="LayoutViewport"/>) — the flip happens once, per
/// vertex, at path-space construction (<see cref="PathSpace.Y"/>). <b>Arc parameters are always
/// derived from the ORIGINAL (Y-up, DBU) endpoints via <see cref="LayoutArc.FromBulge(long,long,long,long,double)"/>,
/// never re-derived from the already-flipped path-space floats</b> — a flip is a reflection
/// (determinant -1), which reverses an arc's sweep sense; re-deriving center/radius/angle from
/// flipped points with the same signed bulge silently fits a DIFFERENT arc (same two endpoints, same
/// sweep magnitude, wrong center) rather than the mirrored version of the original one. The fix is to
/// negate the world-computed start angle and sweep once when converting to Skia's arc-degrees
/// convention (see <see cref="AppendEdge"/>) — this is covered by a regression test
/// (<c>ClosedCurve_OfFourQuarterArcs_FillsLikeACircle</c>) precisely because the bug is silent: it
/// still draws *a* curve, just not the right one.</item>
/// <item>Pan and zoom are then just a plain positive-scale <c>SKMatrix</c> (<see cref="SKMatrix.CreateScaleTranslation"/>)
/// applied to the whole path-space geometry — panning never rebuilds a path, only changes the matrix.</item>
/// </list>
///
/// <b>The compositing contract (§2.3 R8a):</b> fills are drawn per-shape (so same-layer overlap
/// composites darker — this is the owner's decision, see the design doc); strokes are fully opaque
/// and a CONSTANT device-pixel width at any zoom (<see cref="GeometryStrokeDevicePixels"/> — a
/// scale-compensated width, <see cref="DevicePixelsToPathSpace"/>, rather than Skia's <c>StrokeWidth
/// = 0</c> hairline special case, which can only ever mean exactly 1 device pixel) and are batched
/// into one path per layer, since opaque-stroke overlap is idempotent. One <see cref="SKPaint"/> per
/// layer per role (fill/stroke), reused across every shape on that layer.
///
/// <b>Curves render natively</b> — <c>Line</c>→<c>LineTo</c>, <c>Arc</c>→<c>ArcTo</c>, <c>Cubic</c>→
/// <c>CubicTo</c>, <c>Circle</c>→<c>AddCircle</c>, <c>RoundedRect</c>→<c>AddRoundRect</c>. No
/// flattener is written in this phase — Skia tessellates adaptively at the current transform, which
/// already is §3.2 R9c's "rendering flattens adaptively at screen resolution."
///
/// <b><c>LayoutView.Instances</c> is skipped</b> — hierarchy rendering is L3.
/// </summary>
public static class LayoutRenderer
{
    private const double MinGridPixelSpacing = 8.0;

    /// <summary>Maps DBU (world, Y-up) coordinates to path-space floats (Y-down/screen-sense),
    /// bounded by the visible extent rather than absolute position (R-L1a-1). The X axis is not
    /// flipped; only Y is, to convert layout's physical Y-up convention to Skia's Y-down one.</summary>
    /// <summary>Internal (not private) so <c>LayoutPathOutlineSeamTests</c> can construct one directly
    /// and call <see cref="BuildPathOutline"/> for a precise, isolated regression test of the
    /// GetFillPath-seam fix — see that method's doc comment.</summary>
    internal readonly struct PathSpace(long originX, long originY, double dbuToUm)
    {
        public double DbuToUm { get; } = dbuToUm;

        public float X(long dbu) => (float)((dbu - originX) * DbuToUm);
        public float Y(long dbu) => (float)(-(dbu - originY) * DbuToUm);

        public float X(double dbu) => (float)((dbu - originX) * DbuToUm);
        public float Y(double dbu) => (float)(-(dbu - originY) * DbuToUm);

        /// <summary>A world-space length (radius, width, corner radius — no origin offset) to path space.</summary>
        public float Len(double worldLen) => (float)(worldLen * DbuToUm);
    }

    // Reused across frames on the calling thread — Avalonia's ICustomDrawOperation hands us the whole
    // render-surface canvas (Bounds is for invalidation/hit-testing only, it does NOT clip Skia), so
    // every draw here must clip itself instead of relying on a caller-supplied clip. [ThreadStatic]
    // keeps this safe if multiple canvases ever render concurrently on different threads.
    [System.ThreadStatic]
    private static SKPaint? _backgroundPaint;

    private static SKPaint BackgroundPaint(SKColor color)
    {
        var paint = _backgroundPaint ??= new SKPaint { Style = SKPaintStyle.Fill };
        paint.Color = color;
        return paint;
    }

    public static LayoutRenderResult Draw(SKCanvas canvas, LayoutView? view, Technology? tech, LayoutViewport vp, LayoutRenderOptions opts)
    {
        var theme = opts.Theme;

        // Clip + explicit fill instead of canvas.Clear(...): Clear fills the ENTIRE current clip
        // region, and with no clip in force that is the whole render surface — wiping every sibling
        // control already painted this frame (toolbar, rulers, metadata bar). See the L1 fix note in
        // src/Ui/CLAUDE.md for the full story (this was the toolbar-invisible-until-hover bug).
        canvas.Save();
        try
        {
            var clipRect = SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height);
            canvas.ClipRect(clipRect);
            canvas.DrawRect(clipRect, BackgroundPaint(theme.Background));

            if (view is not null && opts.ShowGrid)
                DrawGrid(canvas, view, vp, theme);

            // Note: do NOT early-return on an empty Shapes list — the in-progress draw ghost (below)
            // must still render even when the layout has no committed geometry yet (drawing the very
            // first shape).
            if (view is null || vp.Width < 1 || vp.Height < 1 || vp.Zoom <= 0)
                return new LayoutRenderResult([]);

            // ── Group shapes by layer, resolve each layer once ──────────────────
            // Carries the shape's own index so a live move-drag can substitute a translated clone
            // (opts.Overlay.DragOverrides) without ever mutating view.Shapes mid-drag (L1c).
            var byLayer = new Dictionary<LayerKey, List<(int Index, LayoutShape Shape)>>();
            for (int i = 0; i < view.Shapes.Count; i++)
            {
                var shape = view.Shapes[i];
                if (!byLayer.TryGetValue(shape.Layer, out var list))
                    byLayer[shape.Layer] = list = [];
                list.Add((i, shape));
            }

            var layerMap = tech?.Layers.ToDictionary(l => l.Key);
            var unknownLayers = new HashSet<LayerKey>();
            var resolved = new List<(LayerDef Def, List<(int Index, LayoutShape Shape)> Shapes)>(byLayer.Count);
            foreach (var (key, shapes) in byLayer)
            {
                LayerDef def;
                if (layerMap is not null && layerMap.TryGetValue(key, out var found))
                    def = found;
                else
                {
                    if (tech is not null) unknownLayers.Add(key);   // tech resolved but this key is absent — a real gap
                    def = FallbackPalette.For(key);
                }
                resolved.Add((def, shapes));
            }
            resolved.Sort(static (a, b) => a.Def.ZOrder.CompareTo(b.Def.ZOrder));

            // ── Path-space origin + transform (R-L1a-1/2) ───────────────────────
            double centerX = vp.PanX + vp.Width  / (2.0 * vp.Zoom);
            double centerY = vp.PanY + vp.Height / (2.0 * vp.Zoom);
            double spanX   = vp.Width  / vp.Zoom;
            double spanY   = vp.Height / vp.Zoom;
            var (originX, originY) = ComputeOrigin(centerX, centerY, spanX, spanY);

            double dbuToUm = 1.0 / System.Math.Max(1, view.DbuPerMicron);
            var ps = new PathSpace(originX, originY, dbuToUm);

            double scaleUm = vp.Zoom / dbuToUm;                          // device px per micron
            double transX  = (originX - vp.PanX) * vp.Zoom;
            double transY  = vp.Height - (originY - vp.PanY) * vp.Zoom;
            var matrix = SKMatrix.CreateScaleTranslation((float)scaleUm, (float)scaleUm, (float)transX, (float)transY);

            canvas.Save();
            try
            {
                canvas.Concat(in matrix);
                var dragOverrides = opts.Overlay?.DragOverrides ?? EmptyDragOverrides;
                foreach (var (def, shapes) in resolved)
                {
                    if (!def.Visible) continue;
                    DrawLayer(canvas, def, shapes, ps, dragOverrides, scaleUm);
                }

                if (opts.Overlay?.InProgressPrimitive is { } ghost)
                    DrawGhost(canvas, ghost, layerMap, ps);

                if (opts.Overlay?.SelectedIndices is { Count: > 0 } selected)
                    DrawSelectionOutlines(canvas, view, selected, dragOverrides, theme, ps, scaleUm);

                if (opts.Overlay?.Marquee is { } marquee)
                    DrawMarquee(canvas, marquee, theme, ps);
            }
            finally
            {
                canvas.Restore();
            }

            return new LayoutRenderResult(unknownLayers.Count == 0 ? [] : unknownLayers.ToArray());
        }
        finally
        {
            canvas.Restore();
        }
    }

    // ── Origin quantization ──────────────────────────────────────────────────

    /// <summary>
    /// Anchors path space near the viewport centre, quantized to a power-of-two step derived from
    /// the current view span — so the origin changes only roughly once per screen's worth of
    /// panning (relevant once L2 adds path caching on top of this convention; L1a rebuilds every
    /// frame regardless, per the brief's scope fence).
    /// </summary>
    internal static (long OriginX, long OriginY) ComputeOrigin(double centerX, double centerY, double spanX, double spanY)
    {
        double span = System.Math.Max(System.Math.Max(System.Math.Abs(spanX), System.Math.Abs(spanY)), 1.0);
        long step = (long)System.Math.Pow(2, System.Math.Ceiling(System.Math.Log2(span)));
        if (step <= 0) step = 1;
        long ox = (long)System.Math.Round(centerX / step) * step;
        long oy = (long)System.Math.Round(centerY / step) * step;
        return (ox, oy);
    }

    // ── Grid (screen-space — never touches the path-space float32 path) ────────

    private static void DrawGrid(SKCanvas canvas, LayoutView view, LayoutViewport vp, LayoutRenderTheme theme)
    {
        var pitch = LayoutGridMath.ComputeGridPitch(view.SnapDbu, vp.Zoom, MinGridPixelSpacing);
        if (pitch is null) return;

        long minorPitch = pitch.Value;
        long majorPitch = minorPitch * LayoutGridMath.MajorGridStepCount;

        long iStart = (long)System.Math.Floor(vp.VisibleMinX / minorPitch);
        long iEnd   = (long)System.Math.Ceiling(vp.VisibleMaxX / minorPitch);
        long jStart = (long)System.Math.Floor(vp.VisibleMinY / minorPitch);
        long jEnd   = (long)System.Math.Ceiling(vp.VisibleMaxY / minorPitch);

        const long safetyCap = 4096;
        if (iEnd - iStart > safetyCap || jEnd - jStart > safetyCap) return;

        var minorPts = new List<SKPoint>();
        var majorPts = new List<SKPoint>();

        for (long i = iStart; i <= iEnd; i++)
        {
            long wx = i * minorPitch;
            float sx = (float)vp.WorldToScreenX(wx);
            bool iMajor = wx % majorPitch == 0;
            for (long j = jStart; j <= jEnd; j++)
            {
                long wy = j * minorPitch;
                float sy = (float)vp.WorldToScreenY(wy);
                bool jMajor = wy % majorPitch == 0;
                (iMajor && jMajor ? majorPts : minorPts).Add(new SKPoint(sx, sy));
            }
        }

        using var minorPaint = new SKPaint { IsAntialias = true, Color = theme.GridMinor, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round };
        using var majorPaint = new SKPaint { IsAntialias = true, Color = theme.GridMajor, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round };

        if (minorPts.Count > 0) canvas.DrawPoints(SKPointMode.Points, minorPts.ToArray(), minorPaint);
        if (majorPts.Count > 0) canvas.DrawPoints(SKPointMode.Points, majorPts.ToArray(), majorPaint);
    }

    // ── In-progress draw ghost (L1b) ────────────────────────────────────────────

    /// <summary>Draws the not-yet-committed shape above every layer, in its own resolved layer
    /// color, with a faint fill and a dashed outline so it reads as provisional. Reuses
    /// <see cref="BuildShapePath"/> — no second geometry path for the ghost. Never touches
    /// <c>unknownLayers</c>: an uncommitted shape's layer choice isn't a gap to warn about — if it
    /// is placed, the very next frame's normal per-shape resolution will do that.</summary>
    private static void DrawGhost(SKCanvas canvas, LayoutShape ghost, Dictionary<LayerKey, LayerDef>? layerMap, PathSpace ps)
    {
        Rgba rgba = layerMap is not null && layerMap.TryGetValue(ghost.Layer, out var found)
            ? found.Color
            : FallbackPalette.For(ghost.Layer).Color;
        var color = new SKColor(rgba.R, rgba.G, rgba.B);

        using var shapePath = ghost is LabelShape ? null : BuildShapePath(ghost, ps);
        if (ghost is LabelShape label)
        {
            DrawLabelText(canvas, label, ps, color);
            return;
        }
        if (shapePath is null || shapePath.IsEmpty) return;

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(60) };
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0, Color = color.WithAlpha(220),
            PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };

        canvas.DrawPath(shapePath, fillPaint);
        canvas.DrawPath(shapePath, strokePaint);
    }

    // ── Per-layer draw: per-shape fills, one batched hairline stroke ────────────

    private static readonly IReadOnlyDictionary<int, LayoutShape> EmptyDragOverrides = new Dictionary<int, LayoutShape>();

    /// <summary>Device-pixel target for the per-shape outline stroke — doubled from the plain
    /// Skia hairline (which is exactly 1 device pixel) per owner feedback, 2026-07-26.</summary>
    private const double GeometryStrokeDevicePixels = 2.0;

    /// <summary>Device-pixel target for the selection accent outline — also doubled, so a selected
    /// shape reads unmistakably as selected next to its now-thicker geometry outline.</summary>
    private const double SelectionStrokeDevicePixels = 2.0;

    /// <summary>Converts a desired ON-SCREEN stroke width (device pixels) to the equivalent width in
    /// path space, given the current frame's device-pixels-per-micron scale — this is what keeps a
    /// stroke's apparent thickness constant across zoom levels without relying on Skia's <c>StrokeWidth
    /// = 0</c> hairline special case (which can only ever mean exactly 1 device pixel, not N).</summary>
    private static float DevicePixelsToPathSpace(double scaleUm, double devicePixels)
        => (float)(devicePixels / System.Math.Max(scaleUm, 1e-12));

    private static void DrawLayer(SKCanvas canvas, LayerDef def, List<(int Index, LayoutShape Shape)> shapes,
        PathSpace ps, IReadOnlyDictionary<int, LayoutShape> dragOverrides, double scaleUm)
    {
        var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);
        byte fillAlpha = (byte)System.Math.Clamp(System.Math.Round(def.FillOpacity * 255.0), 0, 255);

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(fillAlpha) };
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels),
            Color = color.WithAlpha(255),
        };
        using var strokeBatch = new SKPath();

        foreach (var (index, original) in shapes)
        {
            // A shape being live-move-dragged (Select tool) renders at its translated preview
            // position instead of its stored one — the model itself is untouched until the drag
            // commits as one MoveShapesCommand (R-L1c-3).
            var shape = dragOverrides.TryGetValue(index, out var ov) ? ov : original;

            if (shape is LabelShape label)
            {
                DrawLabelText(canvas, label, ps, color);
                continue;
            }

            using var shapePath = BuildShapePath(shape, ps);
            if (shapePath is null || shapePath.IsEmpty) continue;

            canvas.DrawPath(shapePath, fillPaint);
            strokeBatch.AddPath(shapePath);
        }

        if (!strokeBatch.IsEmpty)
            canvas.DrawPath(strokeBatch, strokePaint);
    }

    // ── Selection outline + marquee (L1c) ───────────────────────────────────────

    /// <summary>Accent outline for every selected shape, drawn above every layer, batched into one
    /// stroked path. Never touches fill — the layer color stays the information the user reads.</summary>
    private static void DrawSelectionOutlines(SKCanvas canvas, LayoutView view, IReadOnlyList<int> selected,
        IReadOnlyDictionary<int, LayoutShape> dragOverrides, LayoutRenderTheme theme, PathSpace ps, double scaleUm)
    {
        using var batch = new SKPath();
        foreach (var idx in selected)
        {
            if (idx < 0 || idx >= view.Shapes.Count) continue;
            var shape = dragOverrides.TryGetValue(idx, out var ov) ? ov : view.Shapes[idx];
            using var outline = BuildOutlinePathForSelection(shape, ps);
            if (outline is null || outline.IsEmpty) continue;
            batch.AddPath(outline);
        }
        if (batch.IsEmpty) return;

        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, SelectionStrokeDevicePixels),
            Color = theme.Selection,
        };
        canvas.DrawPath(batch, paint);
    }

    /// <summary>Same geometry every other draw call uses, plus the two shapes that have no direct
    /// <see cref="BuildShapePath"/> entry: <c>Label</c> (an approximate text footprint, mirroring
    /// <c>LayoutHitTest</c>'s hit-box formula since neither layer has font metrics) and <c>Via</c>
    /// (a circle at its pad radius).</summary>
    private static SKPath? BuildOutlinePathForSelection(LayoutShape shape, PathSpace ps)
    {
        switch (shape)
        {
            case LabelShape label:
            {
                if (string.IsNullOrEmpty(label.Text)) return null;
                long w = System.Math.Max(1, (long)System.Math.Round(label.Text.Length * label.Height * 0.62));
                long h = System.Math.Max(1, label.Height);
                (long x1, long y1, long x2, long y2) = label.Rotation switch
                {
                    LayoutRotation.R0   => (label.X, label.Y, label.X + w, label.Y + h),
                    LayoutRotation.R180 => (label.X - w, label.Y - h, label.X, label.Y),
                    LayoutRotation.R90  => (label.X, label.Y, label.X + h, label.Y + w),
                    LayoutRotation.R270 => (label.X - h, label.Y - w, label.X, label.Y),
                    _                   => (label.X, label.Y, label.X + w, label.Y + h),
                };
                var path = new SKPath();
                path.AddRect(NormalizedRect(ps.X(x1), ps.Y(y1), ps.X(x2), ps.Y(y2)));
                return path;
            }

            case ViaShape via:
            {
                var path = new SKPath();
                path.AddCircle(ps.X(via.X), ps.Y(via.Y), ps.Len(via.PadSize / 2.0));
                return path;
            }

            default:
                return BuildShapePath(shape, ps);
        }
    }

    private static void DrawMarquee(SKCanvas canvas, LayoutMarquee m, LayoutRenderTheme theme, PathSpace ps)
    {
        var rect = NormalizedRect(ps.X(m.X1), ps.Y(m.Y1), ps.X(m.X2), ps.Y(m.Y2));

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection.WithAlpha(50) };
        using var strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0, Color = theme.Selection.WithAlpha(255) };

        canvas.DrawRect(rect, fillPaint);
        canvas.DrawRect(rect, strokePaint);
    }

    // ── Shape -> path-space SKPath ───────────────────────────────────────────────

    private static SKPath? BuildShapePath(LayoutShape shape, PathSpace ps)
    {
        // Path (a trace) needs its own outline construction (centerline -> GetFillPath), not the
        // generic per-shape builder below.
        if (shape is PathShape trace)
            return BuildPathOutline(trace, ps);

        var path = new SKPath();
        switch (shape)
        {
            case RectShape r:
                path.AddRect(NormalizedRect(ps.X(r.X1), ps.Y(r.Y1), ps.X(r.X2), ps.Y(r.Y2)));
                break;

            case PolygonShape p:
                AddPolygonPath(path, p.Xy, ps);
                break;

            case RoundedRectShape rr:
            {
                var rect = NormalizedRect(ps.X(rr.X1), ps.Y(rr.Y1), ps.X(rr.X2), ps.Y(rr.Y2));
                float radius = ps.Len(rr.CornerRadius);
                path.AddRoundRect(rect, radius, radius);
                break;
            }

            case CircleShape c:
                path.AddCircle(ps.X(c.Cx), ps.Y(c.Cy), ps.Len(c.R));
                break;

            case CurveShape curve:
                AddEdgeListPath(path, curve.Xy, curve.Edges, closed: true, ps);
                break;

            case ViaShape via:
                path.AddCircle(ps.X(via.X), ps.Y(via.Y), ps.Len(via.PadSize / 2.0));
                break;

            default:
                path.Dispose();
                return null;
        }
        return path;
    }

    private static SKRect NormalizedRect(float x1, float y1, float x2, float y2) =>
        new(System.Math.Min(x1, x2), System.Math.Min(y1, y2), System.Math.Max(x1, x2), System.Math.Max(y1, y2));

    private static void AddPolygonPath(SKPath path, long[] xy, PathSpace ps)
    {
        int n = xy.Length / 2;
        if (n < 2) return;
        path.MoveTo(ps.X(xy[0]), ps.Y(xy[1]));
        for (int i = 1; i < n; i++)
            path.LineTo(ps.X(xy[2 * i]), ps.Y(xy[2 * i + 1]));
        path.Close();
    }

    /// <summary>Builds an open or closed edge-list path in path space — shared by <c>Curve</c> and
    /// the centerline of <c>Path</c> (docs/design/layout-view.md §3.2 R9a, "one edge vocabulary").</summary>
    private static void AddEdgeListPath(SKPath path, long[] xy, List<LayoutEdge>? edges, bool closed, PathSpace ps)
    {
        int n = xy.Length / 2;
        if (n == 0) return;
        if (n == 1) { path.MoveTo(ps.X(xy[0]), ps.Y(xy[1])); return; }

        path.MoveTo(ps.X(xy[0]), ps.Y(xy[1]));

        int edgeCount = closed ? n : n - 1;
        for (int i = 0; i < edgeCount; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            long wx0 = xy[2 * i], wy0 = xy[2 * i + 1];
            long wx1 = xy[2 * j], wy1 = xy[2 * j + 1];
            var edge = edges is not null && i < edges.Count ? edges[i] : null;
            AppendEdge(path, wx0, wy0, wx1, wy1, edge, ps);
        }

        if (closed) path.Close();
    }

    /// <summary>Appends one edge to <paramref name="path"/>. <paramref name="wx0"/>/<paramref name="wy0"/>/
    /// <paramref name="wx1"/>/<paramref name="wy1"/> are the ORIGINAL DBU (Y-up, world) endpoints —
    /// arc parameters must be derived from these, not from already-flipped path-space floats (see the
    /// type-level doc comment for why). Line and Cubic edges have no orientation sensitivity and are
    /// transformed directly.</summary>
    private static void AppendEdge(SKPath path, long wx0, long wy0, long wx1, long wy1, LayoutEdge? edge, PathSpace ps)
    {
        float bx = ps.X(wx1), by = ps.Y(wy1);

        switch (edge?.Kind ?? EdgeKind.Line)
        {
            case EdgeKind.Line:
                path.LineTo(bx, by);
                break;

            case EdgeKind.Arc:
            {
                var arc = LayoutArc.FromBulge(wx0, wy0, wx1, wy1, edge!.Bulge);   // world space (Y-up)
                if (arc.R <= 0) { path.LineTo(bx, by); break; }

                float pcx = ps.X(arc.Cx), pcy = ps.Y(arc.Cy);
                float pr  = ps.Len(arc.R);
                var rect = new SKRect(pcx - pr, pcy - pr, pcx + pr, pcy + pr);

                // Y was flipped going from world to path space (a reflection), which reverses the
                // sense of "increasing angle" — negate both angles once here, at the single point
                // that converts to Skia's own (path-space-native) degrees/clockwise convention.
                float startDeg = (float)(-arc.StartAngle * 180.0 / System.Math.PI);
                float sweepDeg = (float)(-arc.Sweep      * 180.0 / System.Math.PI);
                path.ArcTo(rect, startDeg, sweepDeg, forceMoveTo: false);
                break;
            }

            case EdgeKind.Cubic:
            {
                float c1x = ps.X(edge!.C1X), c1y = ps.Y(edge.C1Y);
                float c2x = ps.X(edge.C2X),  c2y = ps.Y(edge.C2Y);
                path.CubicTo(c1x, c1y, c2x, c2y, bx, by);
                break;
            }
        }
    }

    // ── PathShape (trace): centerline -> outline via GetFillPath (§1.5 of the L1a brief) ────────

    /// <summary>
    /// Builds a <c>PathShape</c>'s DISPLAY outline — curves stay curves, via Skia's own stroker plus
    /// <see cref="SKPath.Simplify"/>. <c>GetFillPath</c> does not produce a single merged contour: Skia's
    /// stroker emits one contour per segment plus a wedge per join, all overlapping at every bend. That
    /// is invisible when FILLING (the default Winding fill rule composites the overlaps exactly once,
    /// which is why nothing looked wrong for a solid trace) and very visible when hairline-STROKING the
    /// same path (<c>DrawLayer</c>'s batched outline stroke traces every contour edge in the path,
    /// including the internal boundaries where segment quads and join wedges abut one another — those
    /// internal boundaries are the seam artifacts a bent trace showed at each vertex). <c>Simplify</c>
    /// unions the overlapping contours into the real silhouette (plus any genuine holes), so both the
    /// fill and the (now correctly seam-free) stroke are built from the SAME single-contour path — do
    /// not keep an unsimplified copy for the fill and a simplified one for the stroke.
    ///
    /// <b>This is deliberately a SEPARATE outline from L1e's Clipper2 geometry offset, and must stay
    /// that way.</b> Clipper2 operates on flattened (polygonal) geometry, so a curved trace's Clipper2
    /// outline is polygonal — correct for booleans/DRC/Gerber export, wrong for display, which needs
    /// the adaptive, zoom-correct curve tessellation §3.2 R9c specifies. Two outlines, two purposes:
    /// display (here, Skia stroker + Simplify, curves stay curves) vs. geometry (L1e, Clipper2 offset
    /// on the flattened centerline, exact and integer). Do not "unify" them later.
    ///
    /// <c>// L2: cache with the shape path</c> — <c>Simplify</c> is an <c>SkPathOps</c> call, meaningfully
    /// more expensive than plain path construction; fine at L1 scale (paths rebuild every frame anyway),
    /// but it must ride along with L2's per-shape path cache rather than recompute every frame.
    /// </summary>
    internal static SKPath? BuildPathOutline(PathShape trace, PathSpace ps)
    {
        int n = trace.Xy.Length / 2;
        if (n < 2) return null;

        var xy = trace.End == PathEndStyle.Extended ? ExtendedCenterline(trace.Xy, trace.Width) : trace.Xy;

        using var centerline = new SKPath();
        AddEdgeListPath(centerline, xy, trace.Edges, closed: false, ps);

        var cap = trace.End switch
        {
            PathEndStyle.Round  => SKStrokeCap.Round,
            PathEndStyle.Square => SKStrokeCap.Square,
            _                   => SKStrokeCap.Butt,   // Flush, and Extended (handled via the pre-extended centerline above)
        };

        using var strokeForFill = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = ps.Len(trace.Width),
            StrokeCap   = cap,
            StrokeJoin  = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        var outline = new SKPath();
        strokeForFill.GetFillPath(centerline, outline);

        // L2: cache with the shape path — Simplify is an SkPathOps call, not free.
        var simplified = new SKPath();
        if (outline.Simplify(simplified))
        {
            outline.Dispose();
            return simplified;
        }
        simplified.Dispose();
        return outline;   // degenerate input (e.g. zero-width / duplicate-point) — fall back rather than dropping the trace
    }

    /// <summary>Extends the first/last vertex of a centerline outward by <c>width/2</c> along the
    /// tangent to its neighbor — the DBU-space equivalent of an "Extended" end cap, done before any
    /// transform so the extension length is exact in world units regardless of zoom.</summary>
    private static long[] ExtendedCenterline(long[] xy, long width)
    {
        int n = xy.Length / 2;
        if (n < 2) return xy;
        long half = width / 2;
        var result = (long[])xy.Clone();
        ExtendVertexTowardOutward(result, 0, 1, half);
        ExtendVertexTowardOutward(result, n - 1, n - 2, half);
        return result;
    }

    private static void ExtendVertexTowardOutward(long[] xy, int vertexIdx, int neighborIdx, long amount)
    {
        double vx = xy[2 * vertexIdx], vy = xy[2 * vertexIdx + 1];
        double nx = xy[2 * neighborIdx], ny = xy[2 * neighborIdx + 1];
        double dx = vx - nx, dy = vy - ny;
        double len = System.Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return;
        double ux = dx / len, uy = dy / len;
        xy[2 * vertexIdx]     = (long)System.Math.Round(vx + ux * amount);
        xy[2 * vertexIdx + 1] = (long)System.Math.Round(vy + uy * amount);
    }

    // ── Label (annotation / port marker) — rendered as text, not fill+stroke ────

    private static void DrawLabelText(SKCanvas canvas, LabelShape label, PathSpace ps, SKColor color)
    {
        if (string.IsNullOrEmpty(label.Text)) return;

        float sizeUm = System.Math.Max(0.001f, ps.Len(label.Height));
        using var font = new SKFont(SkiaFonts.PlexRegular, sizeUm);
        using var paint = new SKPaint { IsAntialias = true, Color = color };

        canvas.Save();
        canvas.Translate(ps.X(label.X), ps.Y(label.Y));
        float rotationDeg = label.Rotation switch
        {
            LayoutRotation.R90  => -90f,   // path space is Y-down — negate the DBU-space (Y-up) CCW rotation
            LayoutRotation.R180 => 180f,
            LayoutRotation.R270 => 90f,
            _                   => 0f,
        };
        if (rotationDeg != 0f) canvas.RotateDegrees(rotationDeg);
        canvas.DrawText(label.Text, 0, 0, SKTextAlign.Left, font, paint);
        canvas.Restore();
    }
}
