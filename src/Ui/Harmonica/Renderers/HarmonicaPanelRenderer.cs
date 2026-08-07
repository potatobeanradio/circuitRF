using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Renderers;
using RfCore;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica.Renderers;

/// <summary>
/// The four §7.1 panels, drawn.
///
/// <para><b>These are not a second plot renderer.</b> §2's table lists the Smith/Rect plot, its axes
/// and ticks, the iso-line drawing, the grid points and the optima markers as things that already
/// exist and must not be reimplemented — so each panel builds an ordinary <see cref="Plot"/> and
/// hands it to <see cref="PlotRenderer.Draw"/>, with <see cref="HarmonicaRenderTheme.ToPlotTheme"/>
/// supplying harmonicaRF's own palette. What lives here is only the part the existing stack has no
/// concept of: harmonicaRF's markers and their per-band colours, the intrinsic glyphs beneath them,
/// the hollow hole dots, and §7.2's ranked alpha ramp.</para>
/// </summary>
public static class HarmonicaPanelRenderer
{
    // ── shared geometry ──────────────────────────────────────────────────────

    /// <summary>The Γ-plane transform for a Smith/Polar panel of this size — the SAME one
    /// <see cref="PlotRenderer.BuildTransforms"/> produces, so overlays land exactly on the chart the
    /// existing renderer drew rather than on a hand-derived approximation of it.</summary>
    private static TransformSet GammaTransform(Plot plot, (double W, double H) size)
        => PlotRenderer.BuildTransforms(plot, size);

    private static Plot NewSmithPlot(SmithPanelData d)
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz)
        {
            ShowWatermark = false,
        };
        if (!string.IsNullOrEmpty(d.Title)) { plot.CustomTitleOn = true; plot.CustomTitle = d.Title; }
        return plot;
    }

    // ── §7.2 — the Smith panels ──────────────────────────────────────────────

    /// <summary>
    /// Draws one Smith panel: contours (with §7.2's ranked alpha ramp), grid points including the
    /// hollow hole dots, MXP/MXE, then the intrinsic glyphs, then the termination markers on top.
    ///
    /// <para><b>Z-order is load-bearing, not cosmetic.</b> R-h45-4: glyphs are "always BENEATH the
    /// round termination markers". A glyph that covered its marker would hide the thing the user is
    /// dragging.</para>
    /// </summary>
    public static void DrawSmithPanel(SKCanvas canvas, (double W, double H) size,
                                      SmithPanelData d, HarmonicaRenderTheme theme, bool darkMode)
    {
        // ── ANNULUS HEADROOM ─────────────────────────────────────────────────
        //
        // This is not cosmetic and it was found by a failing pixel oracle, not by inspection. The
        // shared complex-plot viewport (PlotRenderer.ComplexSideMargin et al.) insets the chart by
        // 1% of the canvas — so on a 420 px panel the Γ = 1 rim lands at x = 415.8 and there are
        // FOUR pixels left over. R-h45-4's compressed annulus needs up to
        // IntrinsicGlyphScale.DefaultMargin of the rim radius beyond that, so a glyph with
        // |Γ_intr| > 1 would have been drawn straight off the edge of the panel — clipped, i.e.
        // hidden, which is precisely what §4.5 consequence 2 forbids.
        //
        // The fix is local to harmonicaRF: scale the whole panel about its centre so the chart PLUS
        // its annulus fits. Widening PlotRenderer's own margins would have moved every Data Display
        // Smith plot in the application to solve a harmonicaRF problem.
        //
        // Applied UNCONDITIONALLY, not only when a glyph is currently outside: a chart that resized
        // itself the moment a glyph crossed the rim would be far more disorienting than one that is
        // always the same size.
        float k = (float)(1.0 / (1.0 + AnnulusHeadroom));
        float cx = (float)(size.W / 2), cy = (float)(size.H / 2);

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(k);
        canvas.Translate(-cx, -cy);

        var plot = NewSmithPlot(d);
        PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                          watermarkOpacity: 0f);

        var tf = GammaTransform(plot, size);

        canvas.Save();
        canvas.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, size));

        // Beneath everything: R-h6-12's reachable region, so it reads as ground the data sits on
        // rather than as a mark competing with it.
        DrawReachableRegion(canvas, d, tf, theme);

        DrawContours(canvas, d, tf, theme);
        DrawGridPoints(canvas, d, tf, theme, size);
        DrawOptima(canvas, d, tf, theme, size);

        canvas.Restore();

        // Glyphs and markers are drawn UNCLIPPED within the scaled frame: a glyph with |Γ_intr| > 1
        // lands in the compressed annulus just outside the rim, and clipping to the viewport would
        // hide exactly the case the compressed scale exists to show.
        DrawIntrinsicGlyphs(canvas, d, tf, theme, size);
        DrawMarkers(canvas, d, tf, theme, size);

        canvas.Restore();
    }

    /// <summary>
    /// How much room beyond the Γ = 1 rim a Smith panel reserves, as a fraction of the rim radius —
    /// exactly what <see cref="IntrinsicGlyphScale"/> can consume, so the two cannot disagree about
    /// whether an out-of-circle glyph fits.
    /// </summary>
    public const double AnnulusHeadroom = IntrinsicGlyphScale.DefaultMargin;

    /// <summary>
    /// Where a Γ value lands on a Smith panel of this size, INCLUDING the annulus headroom scale.
    /// Callers that need to hit-test or overlay on a harmonicaRF Smith panel must use this rather
    /// than <c>PlotRenderer.BuildTransforms</c> directly, or they will be off by the headroom factor.
    /// </summary>
    public static SKPoint GammaToCanvas(Complex gamma, (double W, double H) size)
    {
        var tf   = HitTestTransform(size);
        var p    = tf.PrimaryToCanvas(gamma.Real, gamma.Imaginary);

        float k  = (float)(1.0 / (1.0 + AnnulusHeadroom));
        float cx = (float)(size.W / 2), cy = (float)(size.H / 2);
        return new SKPoint(cx + (p.X - cx) * k, cy + (p.Y - cy) * k);
    }

    /// <summary>
    /// R-h6-1 — the exact inverse of <see cref="GammaToCanvas"/>, derived from the same
    /// <see cref="AnnulusHeadroom"/> factor <c>k</c>.
    ///
    /// <para><b>A hit-test that inverted <c>PlotRenderer</c>'s own transform would be off by that
    /// factor</b> — visibly, at the rim, which is exactly where markers sit. One transform pair, one
    /// place, so the two can never disagree about where Γ = 0.8 landed.</para>
    ///
    /// <para>The Smith viewport is square and uniformly scaled, so undoing the headroom scale about
    /// the panel centre and then inverting the linear window map is exact rather than iterative.</para>
    /// </summary>
    public static Complex CanvasToGamma(SKPoint canvas, (double W, double H) size)
    {
        float k  = (float)(1.0 / (1.0 + AnnulusHeadroom));
        float cx = (float)(size.W / 2), cy = (float)(size.H / 2);

        // Undo the headroom scale about the centre — the same two lines GammaToCanvas applies, run
        // backwards.
        var p = new SKPoint(cx + (canvas.X - cx) / k, cy + (canvas.Y - cy) / k);

        // Invert the window map by measuring it, rather than reconstructing PlotRenderer's margin
        // arithmetic here: Γ = 0 and Γ = 1 pin the origin and the scale, and the map is affine.
        var tf = HitTestTransform(size);
        var o  = tf.PrimaryToCanvas(0, 0);
        var xr = tf.PrimaryToCanvas(1, 0);
        var yi = tf.PrimaryToCanvas(0, 1);

        double sx = xr.X - o.X, sy = yi.Y - o.Y;
        if (Math.Abs(sx) < 1e-9 || Math.Abs(sy) < 1e-9) return Complex.Zero;

        return new Complex((p.X - o.X) / sx, (p.Y - o.Y) / sy);
    }

    /// <summary>The bare Smith transform both directions are built on. Deliberately private: callers
    /// go through <see cref="GammaToCanvas"/> / <see cref="CanvasToGamma"/>, which carry the annulus
    /// headroom this one knows nothing about.</summary>
    private static TransformSet HitTestTransform((double W, double H) size)
        => PlotRenderer.BuildTransforms(new Plot(PlotType.Smith, FreqUnit.GHz)
                                        { ShowWatermark = false }, size);

    /// <summary>
    /// Where a MARKER or an intrinsic GLYPH lands: the chart transform composed with
    /// <see cref="IntrinsicGlyphScale"/>'s compressed radial scale, which is what puts a
    /// <c>|Γ| &gt; 1</c> value in the annulus instead of off the panel.
    ///
    /// <para><b>Hit-testing must use this pair, not <see cref="GammaToCanvas"/> alone.</b> The two
    /// agree everywhere inside the unit circle and disagree exactly where an active termination or an
    /// out-of-circle glyph sits — which is where R-h6-10 says the interesting cases are.</para>
    /// </summary>
    public static SKPoint MarkerToCanvas(Complex gamma, (double W, double H) size)
        => GammaToCanvas(IntrinsicGlyphScale.DisplayPosition(gamma), size);

    /// <summary>The inverse of <see cref="MarkerToCanvas"/> — canvas pixels back to the true Γ.</summary>
    public static Complex CanvasToMarker(SKPoint canvas, (double W, double H) size)
        => IntrinsicGlyphScale.TruePosition(CanvasToGamma(canvas, size));

    /// <summary>
    /// R-h6-12 — the reachable region, shaded during an intrinsic drag.
    ///
    /// <para><b>The glyph is drawn on the COMPRESSED radial scale, so the region has to be too.</b>
    /// A region drawn in raw Γ and a glyph drawn on <see cref="IntrinsicGlyphScale"/>'s scale would
    /// disagree about whether a target outside the unit circle is reachable — which is precisely the
    /// question the shading exists to answer, and precisely where the intrinsic plane spends most of
    /// its interesting values.</para>
    ///
    /// <para>Filled, not stroked, and that is not the contour-fill ruling: harmonicaRF's no-fill rule
    /// is about ISO-LINES (a fill there would claim a metric value everywhere inside a level). A
    /// reachable region is a region, and a boundary alone would leave "inside or outside?" to the
    /// reader on a shape that is not convex.</para>
    /// </summary>
    private static void DrawReachableRegion(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                            HarmonicaRenderTheme theme)
    {
        var r = d.Reachable;
        if (r is null || r.IsEmpty) return;

        using var path = new SKPath();
        for (int i = 0; i < r.Boundary.Count; i++)
        {
            var shown = IntrinsicGlyphScale.DisplayPosition(r.Boundary[i]);
            var p = tf.PrimaryToCanvas(shown.Real, shown.Imaginary);
            if (i == 0) path.MoveTo(p); else path.LineTo(p);
        }
        path.Close();

        using var fill = new SKPaint
        {
            Color = theme.ReachableRegion.WithAlpha((byte)(theme.ReachableRegion.Alpha * 0.22)),
            IsAntialias = true,
        };
        canvas.DrawPath(path, fill);

        using var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true,
            Color = theme.ReachableRegion.WithAlpha((byte)(theme.ReachableRegion.Alpha * 0.75)),
        };
        canvas.DrawPath(path, edge);
    }

    /// <summary>§7.2's ranked alpha ramp, one flat alpha per polyline — no shader, no per-vertex
    /// work, no geometry cache.</summary>
    private static void DrawContours(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                     HarmonicaRenderTheme theme)
    {
        if (d.Contours.Count == 0) return;

        var levels = d.Levels;
        using var paint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            IsAntialias = true,
        };

        foreach (var poly in d.Contours)
        {
            if (poly.Points.Count < 2) continue;

            int rank = IsoLineAlphaRamp.RankOf(poly.Level, levels);
            byte a   = IsoLineAlphaRamp.AlphaByte(rank, Math.Max(levels.Count, 1),
                                                  theme.IsoAlphaFloor, theme.IsoAlphaExponent);
            paint.Color = theme.Isoline.WithAlpha(ScaleAlpha(theme.Isoline.Alpha, a));

            using var path = new SKPath();
            var p0 = tf.PrimaryToCanvas(poly.Points[0].X, poly.Points[0].Y);
            path.MoveTo(p0);
            for (int i = 1; i < poly.Points.Count; i++)
            {
                var p = tf.PrimaryToCanvas(poly.Points[i].X, poly.Points[i].Y);
                path.LineTo(p);
            }
            if (poly.Closed) path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>The role's own alpha and the ramp's compose — a role a user made translucent stays
    /// translucent, and the ramp still fades within whatever opacity that leaves.</summary>
    private static byte ScaleAlpha(byte roleAlpha, byte rampAlpha)
        => (byte)Math.Clamp((int)Math.Round(roleAlpha * (rampAlpha / 255.0)), 0, 255);

    /// <summary>
    /// R-h45-5 — thrown-out Γ points render as small HOLLOW dots, so a hole reads as measured rather
    /// than as a rendering gap. Converged points are filled.
    /// </summary>
    private static void DrawGridPoints(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                       HarmonicaRenderTheme theme, (double W, double H) size)
    {
        if (d.GridPoints.Count == 0) return;

        float r = (float)(Math.Min(size.W, size.H) * 0.0055);
        r = Math.Max(1.6f, r);

        using var fill = new SKPaint { Color = theme.GridPoint, IsAntialias = true };
        using var hollow = new SKPaint
        {
            Color       = theme.GridPointDropped,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, r * 0.45f),
            IsAntialias = true,
        };

        foreach (var gp in d.GridPoints)
        {
            var p = tf.PrimaryToCanvas(gp.Gamma.Real, gp.Gamma.Imaginary);
            if (gp.IsHole) canvas.DrawCircle(p, r * 1.15f, hollow);
            else           canvas.DrawCircle(p, r, fill);
        }
    }

    private static void DrawOptima(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                   HarmonicaRenderTheme theme, (double W, double H) size)
    {
        float s = (float)(Math.Min(size.W, size.H) * 0.014);
        s = Math.Max(4f, s);

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true,
            Color = theme.AxisLine,
        };

        void Cross(Complex? c)
        {
            if (c is not Complex z) return;
            var p = tf.PrimaryToCanvas(z.Real, z.Imaginary);
            canvas.DrawLine(p.X - s, p.Y, p.X + s, p.Y, paint);
            canvas.DrawLine(p.X, p.Y - s, p.X, p.Y + s, paint);
        }

        Cross(d.Mxp);
        Cross(d.Mxe);
    }

    /// <summary>
    /// R-h45-4 — the intrinsic glyphs: subtle TRIANGULAR markers, always beneath the round
    /// termination markers, in the same per-band colour at reduced saturation. Values come from the
    /// <c>Gamma_intr</c> cube; nothing here recomputes them (§0.3 item 1).
    /// </summary>
    private static void DrawIntrinsicGlyphs(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                            HarmonicaRenderTheme theme, (double W, double H) size)
    {
        float s = (float)(Math.Min(size.W, size.H) * 0.012);
        s = Math.Max(3.5f, s);

        foreach (var m in d.Markers)
        {
            // R-h8-3 — an intrinsic plane nobody has located arrives as NaN and draws NOTHING. Left
            // to Skia a non-finite coordinate is silently dropped on most paths and lands at the
            // origin on some; a glyph sitting at Γ = 0 is a plausible-looking wrong answer.
            if (double.IsNaN(m.GammaIntrinsic.Real) || double.IsNaN(m.GammaIntrinsic.Imaginary))
                continue;

            var shown = IntrinsicGlyphScale.DisplayPosition(m.GammaIntrinsic);
            var p     = tf.PrimaryToCanvas(shown.Real, shown.Imaginary);

            var band = theme.MarkerBand(m.Band);
            // "reduced saturation" — pulled toward the background so a glyph reads as secondary to
            // its marker without losing which band it belongs to.
            var c = Desaturate(band, theme.Background, 0.45);

            using var fill = new SKPaint { Color = c.WithAlpha(190), IsAntialias = true };
            using var path = new SKPath();
            path.MoveTo(p.X, p.Y - s);
            path.LineTo(p.X + s * 0.9f, p.Y + s * 0.75f);
            path.LineTo(p.X - s * 0.9f, p.Y + s * 0.75f);
            path.Close();
            canvas.DrawPath(path, fill);

            // A glyph in the compressed annulus is never silent about it: the outline says the
            // radial scale is not the chart's own out here.
            if (IntrinsicGlyphScale.IsCompressed(m.GammaIntrinsic))
            {
                using var edge = new SKPaint
                {
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true,
                    Color = c.WithAlpha(255),
                    PathEffect = SKPathEffect.CreateDash([2.5f, 2.0f], 0),
                };
                canvas.DrawPath(path, edge);
            }
        }
    }

    private static SKColor Desaturate(SKColor c, SKColor toward, double t)
    {
        byte L(byte a, byte b) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
        return new SKColor(L(c.Red, toward.Red), L(c.Green, toward.Green), L(c.Blue, toward.Blue), c.Alpha);
    }

    /// <summary>
    /// §4.2 — a marker is a filled circle with a thin outline and its name inside, in its BAND's
    /// colour from the five-colour cycle.
    /// </summary>
    private static void DrawMarkers(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                    HarmonicaRenderTheme theme, (double W, double H) size)
    {
        float r  = (float)(Math.Min(size.W, size.H) * 0.020);
        r = Math.Max(6f, r);
        float ts = r * 1.15f;

        using var font   = new SKFont(SkiaFonts.PlexBold, ts);
        using var edge   = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f,
                                         Color = SKColors.Black, IsAntialias = true };
        using var label  = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        foreach (var m in d.Markers)
        {
            // An ACTIVE termination is drawn where it actually is on the compressed radial scale —
            // the same scale the intrinsic glyph uses — rather than clamped to the rim (R-h6-10).
            // Clamping would put two quite different terminations on the same pixel.
            var shown = IntrinsicGlyphScale.DisplayPosition(m.Gamma);
            var p = tf.PrimaryToCanvas(shown.Real, shown.Imaginary);
            using var fill = new SKPaint { Color = theme.MarkerBand(m.Band), IsAntialias = true };
            canvas.DrawCircle(p, r, fill);

            if (m.ExtrinsicIsOutsideUnitCircle) DrawHatchedOutline(canvas, p, r, theme);
            else                                canvas.DrawCircle(p, r, edge);

            float tw = font.MeasureText(m.Name);
            canvas.DrawText(m.Name, p.X - tw / 2f, p.Y + ts * 0.36f, SKTextAlign.Left, font, label);
        }
    }

    /// <summary>
    /// R-h6-10's flag: a marker whose extrinsic Γ is outside the unit circle wears a HATCHED outline
    /// — a heavier dashed ring plus radial ticks, so it is distinguishable at a glance from the
    /// ordinary thin outline and from the intrinsic glyph's own dashed edge.
    ///
    /// <para>It is a flag, never a clamp. "An active source termination is a legitimate thing to
    /// discover, and hiding it would mislead."</para>
    /// </summary>
    private static void DrawHatchedOutline(SKCanvas canvas, SKPoint p, float r,
                                           HarmonicaRenderTheme theme)
    {
        using var ring = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 2.0f, IsAntialias = true,
            Color = theme.AxisLine,
            PathEffect = SKPathEffect.CreateDash([3f, 2.5f], 0),
        };
        canvas.DrawCircle(p, r + 1.5f, ring);

        using var tick = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true,
            Color = theme.AxisLine,
        };
        for (int i = 0; i < 8; i++)
        {
            double a = Math.PI * 2 * i / 8;
            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
            canvas.DrawLine(p.X + ca * (r + 1.5f), p.Y + sa * (r + 1.5f),
                            p.X + ca * (r + 5.0f), p.Y + sa * (r + 5.0f), tick);
        }
    }

    // ── §7.3 — the loadline panel ────────────────────────────────────────────

    /// <summary>
    /// The DCIV family with the time-domain loadline over it, plus §7.3's <b>persistent</b> plane
    /// indicator — "it is never absent", because a loadline shown in the wrong plane is a plausible
    /// picture of the wrong thing.
    /// </summary>
    public static void DrawLoadlinePanel(SKCanvas canvas, (double W, double H) size,
                                         LoadlinePanelData d, HarmonicaRenderTheme theme, bool darkMode)
    {
        var plot = BuildLoadlinePlot(d, theme);
        PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                          watermarkOpacity: 0f);
        DrawPlaneIndicator(canvas, size, d, theme);
    }

    internal static Plot BuildLoadlinePlot(LoadlinePanelData d, HarmonicaRenderTheme theme)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz)
        {
            ShowWatermark = false,
            CustomTitleOn = true, CustomTitle = "",
            CustomXLabelOn = true, CustomXLabel = "Vds (V)",
            CustomYLabelOn = true, CustomYLabel = "Ids (A)",
        };

        foreach (var c in d.Dciv)
        {
            var t = NewRectTrace(c.Vds, c.Ids, theme.DcivFamily, width: 0.8);
            plot.Traces.Add(t);
        }

        if (d.LoadlineVds.Length > 1)
            plot.Traces.Add(NewRectTrace(d.LoadlineVds, d.LoadlineIds, theme.Loadline, width: 1.8));

        AutoScale(plot);
        return plot;
    }

    private static void DrawPlaneIndicator(SKCanvas canvas, (double W, double H) size,
                                           LoadlinePanelData d, HarmonicaRenderTheme theme)
    {
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.032);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.AxisText.WithAlpha(190), IsAntialias = true };
        string text = d.PlaneLabel;
        float tw = font.MeasureText(text);
        canvas.DrawText(text, (float)size.W - tw - 6f, (float)(ts + 4f), SKTextAlign.Left, font, paint);
    }

    // ── §7.4 — the power-sweep panel ─────────────────────────────────────────

    /// <summary>Gain on the left axis, efficiency on the right, against the currently selected
    /// X unit (§7.4's click-to-cycle), with the operating-point cursor.</summary>
    public static void DrawPowerSweepPanel(SKCanvas canvas, (double W, double H) size,
                                           PowerSweepPanelData d, HarmonicaRenderTheme theme, bool darkMode)
    {
        var plot = BuildPowerSweepPlot(d, theme);
        PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                          watermarkOpacity: 0f);
        DrawOperatingCursor(canvas, size, plot, d, theme);
        if (!d.ReachedCompression) DrawDidNotCompressNote(canvas, size, theme);
    }

    internal static Plot BuildPowerSweepPlot(PowerSweepPanelData d, HarmonicaRenderTheme theme)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz)
        {
            ShowWatermark = false,
            CustomTitleOn = true, CustomTitle = "",
            CustomXLabelOn = true, CustomXLabel = d.XUnit.Label(),
            CustomYLabelOn = true, CustomYLabel = "Gain (dB)",
        };

        double[] x = d.XUnit.Values(d);
        if (x.Length > 1)
        {
            plot.Traces.Add(NewRectTrace(x, d.GainDb,        theme.GainTrace,       width: 1.6));
            plot.Traces.Add(NewRectTrace(x, d.EfficiencyPct, theme.EfficiencyTrace, width: 1.6,
                                         secondary: true));
        }

        AutoScale(plot);
        return plot;
    }

    private static void DrawOperatingCursor(SKCanvas canvas, (double W, double H) size, Plot plot,
                                            PowerSweepPanelData d, HarmonicaRenderTheme theme)
    {
        double[] x = d.XUnit.Values(d);
        if (d.CursorIndex < 0 || d.CursorIndex >= x.Length) return;

        var tf  = PlotRenderer.BuildTransforms(plot, size);
        var top = tf.PrimaryToCanvas(x[d.CursorIndex], plot.Axes.Window.Top);
        var bot = tf.PrimaryToCanvas(x[d.CursorIndex], plot.Axes.Window.Bottom);

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true,
            Color = theme.OperatingCursor.WithAlpha(210),
            PathEffect = SKPathEffect.CreateDash([4f, 3f], 0),
        };
        canvas.DrawLine(top.X, top.Y, bot.X, bot.Y, paint);
    }

    /// <summary>§6.3 — "the power-sweep panel still shows the full drive-up at the current L1
    /// position, annotated 'did not reach P-x dB'."</summary>
    private static void DrawDidNotCompressNote(SKCanvas canvas, (double W, double H) size,
                                               HarmonicaRenderTheme theme)
    {
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.032);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.GridPointDropped, IsAntialias = true };
        canvas.DrawText("did not reach compression", 6f, (float)(ts + 4f),
                        SKTextAlign.Left, font, paint);
    }

    // ── §7.7 — a picked trace's own panel ────────────────────────────────────

    /// <summary>
    /// Draws one picked trace (R-h7-5) into its panel. It is an ordinary <see cref="Plot"/> through
    /// <see cref="PlotRenderer.Draw"/> with harmonicaRF's palette — the same route the power-sweep
    /// panel takes, and deliberately not a second renderer.
    /// </summary>
    public static void DrawPickedTracePanel(SKCanvas canvas, (double W, double H) size,
                                            Plot? plot, string? error,
                                            HarmonicaRenderTheme theme, bool darkMode)
    {
        if (plot is not null)
        {
            PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                              watermarkOpacity: 0f);
            return;
        }

        // A spec that no longer resolves must SAY so on the panel. An empty rectangle where a trace
        // used to be reads as a failed solve, which is a different and much more alarming thing.
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.05);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.GridPointDropped, IsAntialias = true };
        canvas.DrawText(error ?? "no trace", 6f, (float)(ts + 4f), SKTextAlign.Left, font, paint);
    }

    // ── trace construction ───────────────────────────────────────────────────

    private static Trace NewRectTrace(double[] x, double[] y, SKColor colour,
                                      double width, bool secondary = false)
    {
        var t = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real,
                          secondaryAxis: secondary);
        t.Properties.LineColorStorage =
            Avalonia.Media.Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue);
        t.Properties.LineWidth   = width;
        t.Properties.LineEnabled = true;
        t.SetCubeData(x, null, y, "x", null, PlotType.Rect, FreqUnit.GHz);
        return t;
    }

    /// <summary>Fits the window to the traces, primary and secondary axes separately. A panel drawn
    /// into an unfitted window would be blank, which reads as "the solve failed".</summary>
    internal static void AutoScale(Plot plot)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minY2 = double.MaxValue, maxY2 = double.MinValue;

        foreach (var t in plot.Traces)
        {
            var r = t.PathBoundingRect();
            if (r.Width <= 0 && r.Height <= 0) continue;
            minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.X + r.Width);
            if (t.UseSecondaryAxis) { minY2 = Math.Min(minY2, r.Y); maxY2 = Math.Max(maxY2, r.Y + r.Height); }
            else                    { minY  = Math.Min(minY,  r.Y); maxY  = Math.Max(maxY,  r.Y + r.Height); }
        }

        if (minX < maxX && minY < maxY)
            plot.Axes.Window = Pad(minX, minY, maxX, maxY);
        if (minX < maxX && minY2 < maxY2)
            plot.Axes.WindowSecondary = Pad(minX, minY2, maxX, maxY2);
    }

    private static Avalonia.Rect Pad(double x0, double y0, double x1, double y1)
    {
        double w = x1 - x0, h = y1 - y0;
        if (w <= 0) w = 1; if (h <= 0) h = 1;
        return new Avalonia.Rect(x0, y0 - h * 0.05, w, h * 1.10);
    }
}
