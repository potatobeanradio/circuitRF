using System;
using System.Collections.Generic;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Drawing the IN-DESIGN ruler annotations — docs/design/layout-view.md §9B.
///
/// <para><b>This is not the ruler strip along the canvas edge</b> (<c>LayoutRulerRenderer</c>,
/// <c>Layout.Ruler*</c> colour roles). That one is chrome: it tracks the viewport, shows a scale, and
/// cannot be placed, saved or selected. The two share a word and nothing else, which is why the roles
/// here are <c>Layout.RulerAnnotation*</c>.</para>
///
/// <para><b>Rulers paint LAST, above every layer and above instances</b> (R-rul-1: a ruler is not on a
/// layer, does not obey layer visibility and never takes a layer colour), and before the transient
/// interaction overlay. Nothing about a layer affects them.</para>
///
/// <para><b>One measurement, several callers</b> (§3 of the brief): the renderer is the only thing in
/// this codebase that knows the font metrics, so hit-test, Zoom-to-Fit, and the clipboard's painted-
/// bounds pass all read the painted extent from <see cref="MeasureRulerWorldBbox"/> rather than
/// re-deriving it. That duplication is the exact mistake <see cref="MeasureLabelWorldBbox"/>'s own doc
/// comment records — a hand-derived footprint that drifted from what was actually drawn.</para>
/// </summary>
public static partial class LayoutRenderer
{
    /// <summary>A typographic point is 1/72 inch; a device-independent pixel is 1/96. The ONE place
    /// <see cref="RulerAnnotation.TextSizePt"/> becomes a device length.</summary>
    internal const double DevicePixelsPerPoint = 96.0 / 72.0;

    /// <summary>The end tick's half-length, in device pixels, for a <see cref="RulerSizeMode.Fixed"/>
    /// ruler. A <see cref="RulerSizeMode.Scaled"/> one derives its own from the text height instead, so
    /// the whole ruler keeps its proportion to the artwork.</summary>
    private const double RulerTickHalfDevicePixels = 6.0;

    /// <summary>Line and tick stroke width, in device pixels, for a <see cref="RulerSizeMode.Fixed"/>
    /// ruler.</summary>
    private const double RulerStrokeDevicePixels = 1.5;

    /// <summary>A <see cref="RulerSizeMode.Scaled"/> ruler's tick half-length and stroke width as
    /// fractions of its own world text height — this is what makes "the ruler keeps its proportion to
    /// the artwork" true of the whole ruler and not just its text.</summary>
    private const double ScaledTickHalfPerTextHeight = 0.5;
    private const double ScaledStrokePerTextHeight   = 0.09;

    /// <summary>Gap between the line/tick and the readout block, and between stacked readout lines,
    /// as a fraction of the text height.</summary>
    private const double RulerTextGapPerHeight  = 0.45;
    private const double RulerLineGapPerHeight  = 0.28;

    /// <summary>Reference size every font measurement is taken at, then scaled linearly. Measuring at
    /// the actual size would mean handing Skia a font size of several million for a world-space
    /// measurement in DBU (which are sub-nanometre at high resolutions) — the metrics are linear in
    /// size, so one measurement at a sane size and a multiply is both cheaper and better conditioned.</summary>
    private const float RulerMetricReferenceSize = 64f;

    // ── Measurement — the one implementation every caller shares ──────────────────────────────────

    private readonly struct RulerMetrics(double advancePerUnit, double ascentPerUnit, double descentPerUnit)
    {
        public double AdvancePerUnit { get; } = advancePerUnit;
        public double AscentPerUnit  { get; } = ascentPerUnit;   // positive, above the baseline
        public double DescentPerUnit { get; } = descentPerUnit;  // positive, below the baseline
    }

    private static RulerMetrics MeasureRulerText(IReadOnlyList<string> lines, LabelFontStyle style)
    {
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(style), RulerMetricReferenceSize);
        double widest = 0;
        foreach (var line in lines)
            widest = Math.Max(widest, font.MeasureText(line));
        return new RulerMetrics(
            widest / RulerMetricReferenceSize,
            -font.Metrics.Ascent / RulerMetricReferenceSize,   // Skia's Ascent is negative (up)
            font.Metrics.Descent / RulerMetricReferenceSize);
    }

    /// <summary>
    /// The readout's lines, top to bottom (§9B.4): the distance always, then the Delta line when
    /// <see cref="RulerAnnotation.ShowComponents"/>, then the caption when non-empty.
    ///
    /// <para><b>R-rul-6: every length goes through <see cref="LayoutUnits.Format"/> in the document's
    /// own display unit</b> — never a hard-coded unit and never a second formatter. Switching a
    /// document from mm to mil re-renders every readout with no stored value changing.</para>
    /// </summary>
    internal static List<string> RulerReadoutLines(RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron)
    {
        // ONE precision AND ONE spelling for the whole readout (RulerAnnotation.FormatLength) — a
        // ruler that reported its components differently from the distance above them would be
        // stating one measurement three ways.
        string suffix = LayoutUnits.Suffix(unit);
        var lines = new List<string>(3)
        {
            $"{ruler.FormatLength(ruler.DistanceDbu, unit, dbuPerMicron)} {suffix}",
        };

        if (ruler.ShowComponents)
        {
            long dx = Math.Abs(ruler.X2 - ruler.X1);
            long dy = Math.Abs(ruler.Y2 - ruler.Y1);
            lines.Add($"Δx {ruler.FormatLength(dx, unit, dbuPerMicron)}"
                      + $"  Δy {ruler.FormatLength(dy, unit, dbuPerMicron)}");
        }

        if (!string.IsNullOrWhiteSpace(ruler.Caption)) lines.Add(ruler.Caption!);
        return lines;
    }

    /// <summary>
    /// The world text height this ruler paints at, in DBU. <see cref="RulerSizeMode.Scaled"/> returns
    /// its own stored height directly; <see cref="RulerSizeMode.Fixed"/> resolves its point size
    /// against the current zoom, which is what makes it the same size on screen at every zoom.
    /// </summary>
    /// <param name="devicePxPerDbu">The viewport's own zoom (device pixels per DBU). A non-positive
    /// value has no scale to resolve <c>Fixed</c> against and falls back to the stored world height, so
    /// a caller with no viewport still gets something rather than a zero-height ruler.</param>
    internal static long ResolveRulerTextHeightDbu(RulerAnnotation ruler, double devicePxPerDbu)
    {
        if (ruler.SizeMode == RulerSizeMode.Scaled) return Math.Max(1, ruler.TextHeightDbu);
        if (devicePxPerDbu <= 0) return Math.Max(1, ruler.TextHeightDbu);
        double devicePx = Math.Max(1.0, ruler.TextSizePt) * DevicePixelsPerPoint;
        return Math.Max(1, (long)Math.Round(devicePx / devicePxPerDbu));
    }

    /// <summary>The tick half-length and stroke width this ruler paints at, in world DBU.</summary>
    private static (double TickHalf, double Stroke) ResolveRulerLineMetrics(
        RulerAnnotation ruler, long textHeightDbu, double devicePxPerDbu)
    {
        if (ruler.SizeMode == RulerSizeMode.Scaled)
            return (textHeightDbu * ScaledTickHalfPerTextHeight, textHeightDbu * ScaledStrokePerTextHeight);

        if (devicePxPerDbu <= 0)
            return (textHeightDbu * ScaledTickHalfPerTextHeight, textHeightDbu * ScaledStrokePerTextHeight);

        return (RulerTickHalfDevicePixels / devicePxPerDbu, RulerStrokeDevicePixels / devicePxPerDbu);
    }

    /// <summary>Everything the draw pass and every measuring caller need, resolved once: the unit
    /// direction, the outward normal the readout is offset along, and the readout block's world
    /// centre and half-extents.</summary>
    private struct RulerGeometry
    {
        public double Ux, Uy;            // unit vector along the ruler, world (Y-up)
        public double Nx, Ny;            // unit normal the readout is pushed out along
        public double TickHalf, Stroke;  // world DBU
        public double TextHeight;        // world DBU
        public double Ascent, Descent;   // world DBU, per line
        public double LineStep;          // world DBU, baseline-to-baseline
        public double BlockCx, BlockCy;  // readout block centre, world DBU
        public double BlockHalfW, BlockHalfH;
        public LabelHAlign HAlign;       // how the lines justify inside the block
        public IReadOnlyList<string> Lines;
    }

    /// <summary>
    /// Where the readout block's CENTRE goes when the ruler carries a hand-placed position — the
    /// anchor names which point of the block sits on <c>(TextX, TextY)</c>, so the centre is that
    /// point plus half the block in the opposite direction.
    ///
    /// <para><b>World space is Y-UP here</b>, which is why <c>Top</c> subtracts and <c>Bottom</c> adds.
    /// <c>Baseline</c> is the FIRST line's baseline: the block's top edge sits one ascent above it.</para>
    /// </summary>
    private static (double Cx, double Cy) AnchorToBlockCentre(
        double ax, double ay, LabelHAlign h, LabelVAlign v, double halfW, double halfH, double ascent)
    {
        double cx = h switch
        {
            LabelHAlign.Left  => ax + halfW,
            LabelHAlign.Right => ax - halfW,
            _                 => ax,
        };
        double cy = v switch
        {
            LabelVAlign.Top      => ay - halfH,
            LabelVAlign.Bottom   => ay + halfH,
            LabelVAlign.Baseline => ay + ascent - halfH,
            _                    => ay,
        };
        return (cx, cy);
    }

    /// <summary>
    /// The one place a ruler's painted geometry is derived. <b>The readout is pushed out along the
    /// normal by the block's OWN half-extent in that direction</b>, not by a fixed gap — which is what
    /// keeps the text clear of the line at every angle, including a vertical ruler whose normal is
    /// horizontal and whose block would otherwise straddle it (§9B.4: "the text never overlaps the
    /// line"). The text itself is never rotated (R-rul-4's own note: rotating it with the line is how
    /// a vertical measurement becomes unreadable in a slide).
    /// </summary>
    private static RulerGeometry? BuildRulerGeometry(
        RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron, double devicePxPerDbu)
    {
        double dx = (double)ruler.X2 - ruler.X1;
        double dy = (double)ruler.Y2 - ruler.Y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return null;

        var g = new RulerGeometry { Ux = dx / len, Uy = dy / len };

        // World Y-up normal, always chosen with a non-negative Y component so a horizontal ruler's
        // readout is consistently ABOVE the line rather than flipping when the two endpoints are
        // placed right-to-left.
        double nx = -g.Uy, ny = g.Ux;
        if (ny < 0) { nx = -nx; ny = -ny; }
        g.Nx = nx; g.Ny = ny;

        long textH = ResolveRulerTextHeightDbu(ruler, devicePxPerDbu);
        g.TextHeight = textH;
        (g.TickHalf, g.Stroke) = ResolveRulerLineMetrics(ruler, textH, devicePxPerDbu);

        g.Lines = RulerReadoutLines(ruler, unit, dbuPerMicron);
        var m = MeasureRulerText(g.Lines, ruler.Style);
        g.Ascent  = m.AscentPerUnit  * textH;
        g.Descent = m.DescentPerUnit * textH;
        g.LineStep = (m.AscentPerUnit + m.DescentPerUnit + RulerLineGapPerHeight) * textH;

        g.BlockHalfW = m.AdvancePerUnit * textH / 2.0;
        double blockH = g.Ascent + g.Descent + (g.Lines.Count - 1) * g.LineStep;
        g.BlockHalfH = blockH / 2.0;

        g.HAlign = ruler.EffectiveTextHAlign;

        // A HAND-PLACED readout wins outright (owner, 2026-08-27): the anchor decides which point of
        // the block lands on the stored coordinate, and the midpoint-plus-normal offset below is not
        // consulted at all. Nothing else in this method changes, so the hit region, Zoom-to-Fit and
        // the clipboard's painted bounds follow the moved text for free — they all read this one
        // geometry rather than re-deriving the midpoint themselves.
        if (ruler.HasTextPosition)
        {
            (g.BlockCx, g.BlockCy) = AnchorToBlockCentre(
                ruler.TextX!.Value, ruler.TextY!.Value, g.HAlign, ruler.EffectiveTextVAlign,
                g.BlockHalfW, g.BlockHalfH, g.Ascent);
            return g;
        }

        double midX = (ruler.X1 + (double)ruler.X2) / 2.0;
        double midY = (ruler.Y1 + (double)ruler.Y2) / 2.0;
        double halfExtentAlongN = Math.Abs(nx) * g.BlockHalfW + Math.Abs(ny) * g.BlockHalfH;
        double push = g.TickHalf + RulerTextGapPerHeight * textH + halfExtentAlongN;
        g.BlockCx = midX + nx * push;
        g.BlockCy = midY + ny * push;

        return g;
    }

    /// <summary>
    /// The world-DBU bbox of everything this ruler PAINTS at the given zoom — the line, both end
    /// ticks, and the readout block. The one measurement <c>LayoutRulerHitTest</c>, Zoom-to-Fit and
    /// <c>LayoutClipboard.ComputeSelectionBounds</c> all share.
    /// </summary>
    public static Bbox MeasureRulerWorldBbox(
        RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron, double devicePxPerDbu)
    {
        var bb = new Bbox(
            Math.Min(ruler.X1, ruler.X2), Math.Min(ruler.Y1, ruler.Y2),
            Math.Max(ruler.X1, ruler.X2), Math.Max(ruler.Y1, ruler.Y2));

        if (BuildRulerGeometry(ruler, unit, dbuPerMicron, devicePxPerDbu) is not { } g) return bb;

        // The ticks reach across the line at each endpoint.
        foreach (var (px, py) in new[] { ((double)ruler.X1, (double)ruler.Y1), ((double)ruler.X2, (double)ruler.Y2) })
        {
            bb = bb.Union(PointBbox(px + g.Nx * g.TickHalf, py + g.Ny * g.TickHalf));
            bb = bb.Union(PointBbox(px - g.Nx * g.TickHalf, py - g.Ny * g.TickHalf));
        }

        return bb.Union(RulerTextWorldBbox(g));
    }

    /// <summary>Just the readout block — what a click on "the number" must hit (R-rul-11).</summary>
    internal static Bbox MeasureRulerTextWorldBbox(
        RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron, double devicePxPerDbu)
        => BuildRulerGeometry(ruler, unit, dbuPerMicron, devicePxPerDbu) is { } g
            ? RulerTextWorldBbox(g)
            : Bbox.Empty;

    /// <summary>
    /// Where <see cref="RulerAnnotation.TextX"/>/<see cref="TextY"/> would have to be for this ruler's
    /// readout to stay EXACTLY where it is drawn now — the inverse of
    /// <see cref="AnchorToBlockCentre"/>, evaluated against its own anchor.
    ///
    /// <para>This is what "start from where it already is" means everywhere the position becomes
    /// explicit: typing into one coordinate box, or reading back a dynamic position. Deriving it here
    /// rather than re-computing the midpoint push at the call site is the same rule the whole file
    /// follows — the renderer is the only thing that knows the font metrics.</para>
    /// </summary>
    public static (long X, long Y)? RulerTextAnchorPoint(
        RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron, double devicePxPerDbu)
    {
        if (BuildRulerGeometry(ruler, unit, dbuPerMicron, devicePxPerDbu) is not { } g) return null;

        double ax = ruler.EffectiveTextHAlign switch
        {
            LabelHAlign.Left  => g.BlockCx - g.BlockHalfW,
            LabelHAlign.Right => g.BlockCx + g.BlockHalfW,
            _                 => g.BlockCx,
        };
        double ay = ruler.EffectiveTextVAlign switch
        {
            LabelVAlign.Top      => g.BlockCy + g.BlockHalfH,
            LabelVAlign.Bottom   => g.BlockCy - g.BlockHalfH,
            LabelVAlign.Baseline => g.BlockCy + g.BlockHalfH - g.Ascent,
            _                    => g.BlockCy,
        };
        return ((long)Math.Round(ax), (long)Math.Round(ay));
    }

    private static Bbox RulerTextWorldBbox(RulerGeometry g) => new(
        (long)Math.Floor(g.BlockCx - g.BlockHalfW), (long)Math.Floor(g.BlockCy - g.BlockHalfH),
        (long)Math.Ceiling(g.BlockCx + g.BlockHalfW), (long)Math.Ceiling(g.BlockCy + g.BlockHalfH));

    private static Bbox PointBbox(double x, double y)
    {
        long ix = (long)Math.Round(x), iy = (long)Math.Round(y);
        return new Bbox(ix, iy, ix, iy);
    }

    /// <summary>
    /// The readout block's size in DEVICE PIXELS at the given zoom — the screen-space half of the
    /// measurement pair. A <see cref="RulerSizeMode.Fixed"/> ruler returns the same height at every
    /// zoom by construction (that IS the mode); a <see cref="RulerSizeMode.Scaled"/> one doubles when
    /// the zoom doubles.
    /// </summary>
    public static (double Width, double Height) MeasureRulerScreenBox(
        RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron, double devicePxPerDbu)
    {
        if (BuildRulerGeometry(ruler, unit, dbuPerMicron, devicePxPerDbu) is not { } g) return (0, 0);
        return (2 * g.BlockHalfW * devicePxPerDbu, 2 * g.BlockHalfH * devicePxPerDbu);
    }

    // ── Draw ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every ruler in the view, plus (when the overlay supplies one) a live placement preview.
    /// Called from <see cref="Draw"/> after every layer and after instances, inside the path-space
    /// transform.
    /// </summary>
    /// <param name="selected">Indices into <paramref name="rulers"/> that additionally take the
    /// <c>Layout.Selection</c> accent — the app's one word for "selected"; a ruler must not invent a
    /// second (§9B.8).</param>
    /// <param name="showEndpointHandles">§9B.6: endpoint handles render only for a single-ruler
    /// selection, mirroring §6.3's vertex-handle rule.</param>
    private static void DrawRulers(
        SKCanvas canvas, IReadOnlyList<RulerAnnotation> rulers, IReadOnlyList<int> selected,
        IReadOnlyDictionary<int, RulerAnnotation>? dragOverrides, RulerAnnotation? preview,
        IReadOnlyList<RulerAnnotation>? ghosts,
        bool showEndpointHandles, LayoutUnit unit, int dbuPerMicron,
        LayoutRenderTheme theme, PathSpace ps, double scaleUm)
    {
        double devicePxPerDbu = scaleUm * ps.DbuToUm;
        if (devicePxPerDbu <= 0) return;

        for (int i = 0; i < rulers.Count; i++)
        {
            var ruler = dragOverrides is not null && dragOverrides.TryGetValue(i, out var overridden)
                ? overridden
                : rulers[i];
            bool isSelected = Contains(selected, i);
            DrawOneRuler(canvas, ruler, unit, dbuPerMicron, theme, ps, scaleUm, devicePxPerDbu,
                         isSelected, isSelected && showEndpointHandles, ghost: false);
        }

        if (preview is { } p)
            DrawOneRuler(canvas, p, unit, dbuPerMicron, theme, ps, scaleUm, devicePxPerDbu,
                         selectedAccent: false, endpointHandles: false, ghost: true);

        // The paste/duplicate ghosts, drawn provisionally exactly like the tool's own preview — a
        // copy being aimed and a ruler being composed look the same because they ARE the same thing
        // to the user: geometry that is not committed yet.
        if (ghosts is not null)
            foreach (var g in ghosts)
                DrawOneRuler(canvas, g, unit, dbuPerMicron, theme, ps, scaleUm, devicePxPerDbu,
                             selectedAccent: false, endpointHandles: false, ghost: true);
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return true;
        return false;
    }

    /// <summary>
    /// Draws every ruler ON TOP of a canvas that is otherwise finished — the second half of
    /// <see cref="LayoutRenderOptions.DeferRulers"/>, which see for why this exists at all.
    ///
    /// <para>It rebuilds the same path-space transform <see cref="Draw"/> used, from the same viewport
    /// and the same view, rather than being handed one — exactly as
    /// <see cref="DrawSnapMarkerOnTop"/> does, and for the same reason: a transform passed across the
    /// seam is a second copy of the rule that decides where anything on this canvas lands.</para>
    /// </summary>
    public static void DrawRulersOnTop(SKCanvas canvas, LayoutView? view, LayoutViewport vp,
                                       LayoutRenderOptions opts)
    {
        if (canvas is null || view is null || !opts.ShowRulers) return;
        if (view.Rulers.Count == 0 && opts.Overlay?.RulerPreview is null
            && opts.Overlay?.RulerPastePreview is not { Count: > 0 }) return;

        double centerX = vp.PanX + vp.Width  / (2.0 * vp.Zoom);
        double centerY = vp.PanY + vp.Height / (2.0 * vp.Zoom);
        var (originX, originY) = ComputeOrigin(centerX, centerY, vp.Width / vp.Zoom, vp.Height / vp.Zoom);

        double dbuToUm = 1.0 / Math.Max(1, view.DbuPerMicron);
        double scaleUm = vp.Zoom / dbuToUm;

        canvas.Save();
        try
        {
            canvas.ClipRect(SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height));
            canvas.Concat(SKMatrix.CreateScaleTranslation(
                (float)scaleUm, (float)scaleUm,
                (float)((originX - vp.PanX) * vp.Zoom),
                (float)(vp.Height - (originY - vp.PanY) * vp.Zoom)));

            DrawRulers(canvas, view.Rulers,
                       opts.Overlay?.SelectedRulerIndices ?? [],
                       opts.Overlay?.RulerDragOverrides,
                       opts.Overlay?.RulerPreview,
                       opts.Overlay?.RulerPastePreview,
                       opts.Overlay?.ShowRulerEndpointHandles == true,
                       view.DisplayUnit, view.DbuPerMicron, opts.Theme,
                       new PathSpace(originX, originY, dbuToUm), scaleUm);
        }
        finally
        {
            canvas.Restore();
        }
    }

    private static void DrawOneRuler(
        SKCanvas canvas, RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron,
        LayoutRenderTheme theme, PathSpace ps, double scaleUm, double devicePxPerDbu,
        bool selectedAccent, bool endpointHandles, bool ghost)
    {
        if (BuildRulerGeometry(ruler, unit, dbuPerMicron, devicePxPerDbu) is not { } g) return;

        var lineColor = selectedAccent ? theme.Selection : theme.RulerAnnotationLine;
        var textColor = selectedAccent ? theme.Selection : theme.RulerAnnotationText;
        if (ghost) { lineColor = lineColor.WithAlpha(150); textColor = textColor.WithAlpha(150); }

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = ps.Len(g.Stroke),
            StrokeCap = SKStrokeCap.Round,
            Color = lineColor,
        };

        canvas.DrawLine(ps.X(ruler.X1), ps.Y(ruler.Y1), ps.X(ruler.X2), ps.Y(ruler.Y2), stroke);

        void Tick(double px, double py)
        {
            canvas.DrawLine(
                ps.X(px + g.Nx * g.TickHalf), ps.Y(py + g.Ny * g.TickHalf),
                ps.X(px - g.Nx * g.TickHalf), ps.Y(py - g.Ny * g.TickHalf), stroke);
        }
        Tick(ruler.X1, ruler.Y1);
        Tick(ruler.X2, ruler.Y2);

        // The readout. Upright at every ruler angle — no canvas rotation here, deliberately.
        float sizePath = Math.Max(0.001f, ps.Len(g.TextHeight));
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(ruler.Style), sizePath);
        using var textPaint = new SKPaint { IsAntialias = true, Color = textColor };

        // The lines justify inside the block per the ruler's own horizontal anchor — Center is what a
        // ruler has always drawn and stays the default. Left/Right are what make a multi-line readout
        // (distance + Delta line + caption) line up down one edge instead of ragged on both.
        var (align, cx) = g.HAlign switch
        {
            LabelHAlign.Left  => (SKTextAlign.Left,  ps.X(g.BlockCx - g.BlockHalfW)),
            LabelHAlign.Right => (SKTextAlign.Right, ps.X(g.BlockCx + g.BlockHalfW)),
            _                 => (SKTextAlign.Center, ps.X(g.BlockCx)),
        };
        // Path space is Y-DOWN: the block's world TOP edge is its smallest path-space Y.
        float topY = ps.Y(g.BlockCy + g.BlockHalfH);
        float baseline = topY + ps.Len(g.Ascent);
        float step = ps.Len(g.LineStep);

        foreach (var line in g.Lines)
        {
            canvas.DrawText(line, cx, baseline, align, font, textPaint);
            baseline += step;
        }

        if (!endpointHandles) return;

        float half = DevicePixelsToPathSpace(scaleUm, HandleSizeDevicePixels) / 2f;
        using var handleFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection };
        foreach (var (hx, hy) in new[] { (ruler.X1, ruler.Y1), (ruler.X2, ruler.Y2) })
        {
            float x = ps.X(hx), y = ps.Y(hy);
            canvas.DrawRect(new SKRect(x - half, y - half, x + half, y + half), handleFill);
        }
    }
}
