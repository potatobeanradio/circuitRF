// ================================================================
//  AxesRenderer.cs  —  Skia drawing for rectangular, polar, and Smith grids
//
//  Ported from splotRF/src/Renderers/AxesRenderer.cs — namespace
//  renamed to CircuitRF.Ui.DataDisplay; font seam retargeted from
//  SkiaFonts.Regular/Bold (DejaVu) to SkiaFonts.PlexRegular/PlexBold
//  (IBM Plex).
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using SkiaSharp;
using RfCore;

namespace CircuitRF.Render.DataDisplay
{
    public static class AxesRenderer
    {
        // ---- Tunable constants ------------------------------------------

        public const float DescriptionStripPad = 3f;

        // ================================================================
        //  Rectangular grid
        // ================================================================

        public static void DrawRectGrid(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Axes                 axes,
            TransformSet         tf,
            PlotDetail           detail,
            RenderTheme          theme)
        {
            float lw = LineWidth(canvasSize);
            var (minorTicks, majorTicks, labels) = detail.Properties();

            var (textFont, textPaint) = MakeTextObjects(axes.FontSizeTicks, lw, theme);
            using var _tf = textFont;
            using var _tp = textPaint;

            DrawBorder(canvas, axes, tf, lw, theme);

            if (!majorTicks) return;

            var ticks = axes.Ticks(minorTicks);

            using var majorGridPath  = new SKPath();
            using var majorTickPath  = new SKPath();

            // One SI prefix per AXIS, chosen from its own window, never per tick: picking per value
            // puts "900p" and "1n" on adjacent gridlines and reads as a jump in the data.
            int xGroup  = EngineeringFormat.GroupFor(
                EngineeringFormat.AxisMagnitude(axes.Window.Left, axes.Window.Right));
            int yGroup  = EngineeringFormat.GroupFor(
                EngineeringFormat.AxisMagnitude(axes.Window.Top, axes.Window.Bottom));
            int y2Group = EngineeringFormat.GroupFor(
                EngineeringFormat.AxisMagnitude(axes.WindowSecondary.Top, axes.WindowSecondary.Bottom));

            foreach (var tx in ticks.MajorX)
            {
                var top = tf.PrimaryToCanvas(tx, axes.Window.Top);
                var bot = tf.PrimaryToCanvas(tx, axes.Window.Bottom);
                majorGridPath.MoveTo(top); majorGridPath.LineTo(bot);

                var tickBase = tf.PrimaryToCanvas(tx, axes.Window.Top);
                var tickTip  = tf.PrimaryToCanvas(tx, axes.Window.Top + axes.TickLengthY);
                majorTickPath.MoveTo(tickBase); majorTickPath.LineTo(tickTip);

                if (labels)
                {
                    double v     = EngineeringFormat.SnapNearZero(tx, axes.XTick);
                    string label = EngineeringFormat.Tick(v, xGroup, axes.NumDigitsXAxis);
                    float  tw    = textFont.MeasureText(label);
                    canvas.DrawText(label,
                        tickBase.X - tw / 2f,
                        tickBase.Y + textFont.Size * 1.4f,
                        SKTextAlign.Left, textFont, textPaint);
                }
            }

            using var majorGridPathY  = new SKPath();
            using var majorTickPathY  = new SKPath();
            using var majorGridPathY2 = new SKPath();
            using var majorTickPathY2 = new SKPath();

            foreach (var (yPrimary, ySecondary) in ticks.MajorY)
            {
                if (!double.IsFinite(yPrimary)) continue;

                var left  = tf.PrimaryToCanvas(axes.Window.Left,  yPrimary);
                var right = tf.PrimaryToCanvas(axes.Window.Right, yPrimary);
                majorGridPathY.MoveTo(left); majorGridPathY.LineTo(right);

                var tl0 = tf.PrimaryToCanvas(axes.Window.Left,                    yPrimary);
                var tl1 = tf.PrimaryToCanvas(axes.Window.Left + axes.TickLengthX, yPrimary);
                majorTickPathY.MoveTo(tl0); majorTickPathY.LineTo(tl1);

                if (labels)
                {
                    double v     = EngineeringFormat.SnapNearZero(yPrimary, axes.YTick);
                    string label = EngineeringFormat.Tick(v, yGroup, axes.NumDigitsLeftY);
                    float  tw    = textFont.MeasureText(label);
                    canvas.DrawText(label,
                        tl0.X - tw - lw * 4f,
                        tl0.Y + textFont.Size * 0.35f,
                        SKTextAlign.Left, textFont, textPaint);
                }

                if (axes.ShowSecondary)
                {
                    if (axes.SecondaryShareGrid)
                    {
                        var tr0 = tf.PrimaryToCanvas(axes.Window.Right - axes.TickLengthX, yPrimary);
                        var tr1 = tf.PrimaryToCanvas(axes.Window.Right,                    yPrimary);
                        majorTickPathY.MoveTo(tr0); majorTickPathY.LineTo(tr1);
                    }
                    else if (double.IsFinite(ySecondary))
                    {
                        var sr0 = tf.SecondaryToCanvas(axes.WindowSecondary.Right - axes.TickLengthX, ySecondary);
                        var sr1 = tf.SecondaryToCanvas(axes.WindowSecondary.Right,                    ySecondary);
                        majorTickPathY2.MoveTo(sr0); majorTickPathY2.LineTo(sr1);

                        var sl = tf.SecondaryToCanvas(axes.WindowSecondary.Left,  ySecondary);
                        var sr = tf.SecondaryToCanvas(axes.WindowSecondary.Right, ySecondary);
                        majorGridPathY2.MoveTo(sl); majorGridPathY2.LineTo(sr);
                    }

                    if (labels && double.IsFinite(ySecondary))
                    {
                        double v2     = EngineeringFormat.SnapNearZero(ySecondary, axes.Y2Tick);
                        string label2 = EngineeringFormat.Tick(v2, y2Group, axes.NumDigitsRightY);
                        var    rPt    = tf.SecondaryToCanvas(axes.WindowSecondary.Right, ySecondary);
                        canvas.DrawText(label2,
                            rPt.X + lw * 4f,
                            rPt.Y + textFont.Size * 0.35f,
                            SKTextAlign.Left, textFont, textPaint);
                    }
                }
            }

            using var gridPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.GridColor, axes.MinorTransparencyScale),
                StrokeWidth = (float)axes.GridThicknessFactor * lw,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = false
            };
            // Grid and tick STROKES are clipped to the plot box. The tick NUMBERS drawn in the
            // loops above are not — they live in the margin by design — and neither is DrawBorder,
            // whose frame is meant to straddle the edge at full thickness.
            //
            // Smith and Polar have always clipped their grids (see DrawPolarGrid/DrawSmithGrid);
            // Rect was the one that did not. A gridline landing exactly ON an edge — which happens
            // whenever a pan brings the window boundary onto the tick lattice — has half its
            // 1.25 px stroke outside the axes, so it painted a full-height line one pixel beyond
            // the axis (owner, 2026-08-21: "I even see some ticks leave the world space and render
            // outside the rect plot's box").
            var plotBox = PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize);

            canvas.Save();
            canvas.ClipRect(plotBox);
            canvas.DrawPath(majorGridPath,  gridPaint);
            canvas.DrawPath(majorGridPathY, gridPaint);
            if (axes.ShowSecondary && !axes.SecondaryShareGrid)
                canvas.DrawPath(majorGridPathY2, gridPaint);

            using var tickPaint = StrokePaint(theme.TickColor, (float)axes.TickThicknessFactor * lw);
            canvas.DrawPath(majorTickPath,  tickPaint);
            canvas.DrawPath(majorTickPathY, tickPaint);
            if (axes.ShowSecondary) canvas.DrawPath(majorTickPathY2, tickPaint);
            canvas.Restore();

            if (!minorTicks) return;

            using var minorGridPath  = new SKPath();
            using var minorTickPath  = new SKPath();
            using var minorGridPathY = new SKPath();
            using var minorTickPathY = new SKPath();

            foreach (var tx in ticks.MinorX)
            {
                minorGridPath.MoveTo(tf.PrimaryToCanvas(tx, axes.Window.Top));
                minorGridPath.LineTo(tf.PrimaryToCanvas(tx, axes.Window.Bottom));
                minorTickPath.MoveTo(tf.PrimaryToCanvas(tx, axes.Window.Top));
                minorTickPath.LineTo(tf.PrimaryToCanvas(tx, axes.Window.Top + axes.TickLengthY / 2));
            }

            foreach (var ty in ticks.MinorY)
            {
                minorGridPathY.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,  ty));
                minorGridPathY.LineTo(tf.PrimaryToCanvas(axes.Window.Right, ty));
                minorTickPathY.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,                    ty));
                minorTickPathY.LineTo(tf.PrimaryToCanvas(axes.Window.Left + axes.TickLengthX / 2, ty));
            }

            using var mgPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.MinorGridColor, axes.MinorTransparencyScale * 0.4),
                StrokeWidth = (float)axes.GridThicknessFactor * lw / 2f,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = false
            };
            canvas.Save();
            canvas.ClipRect(plotBox);
            canvas.DrawPath(minorGridPath,  mgPaint);
            canvas.DrawPath(minorGridPathY, mgPaint);

            using var mtPaint = StrokePaint(theme.TickColor, (float)axes.TickThicknessFactor * lw / 2f);
            canvas.DrawPath(minorTickPath,  mtPaint);
            canvas.DrawPath(minorTickPathY, mtPaint);
            canvas.Restore();
        }

        // ================================================================
        //  Polar grid
        // ================================================================

        /// <summary>
        /// The polar grid: the two diameters, the ring lattice, the minor tick marks along the
        /// axes, and the radius numbers.
        ///
        /// <para><b>The rings are computed here from the framed radius, not taken from
        /// <see cref="Axes.Ticks"/>, and that is the whole fix for a non-unity scale.</b> The tick
        /// set is built for a RECTANGULAR axis: <c>MinorX</c> is the lattice with every major
        /// multiple REMOVED, which is exactly right when the majors are drawn as their own darker
        /// gridlines and exactly wrong as a set of radii — the rings then had a hole in them
        /// wherever a major tick fell. It only ever looked acceptable at unity because the numbers
        /// happen to come out even there (0.2, 0.6, 1.0). Off unity it fell apart in three
        /// different ways at once: a window of ±2 drew eight rings with a gap where 1.0 should be,
        /// ±3 drew nothing but half-integers (0.5, 1.5, 2.5), and ±1.2 drew the unit-circle lattice
        /// with no ring at the boundary at all — so the plot had no frame and nothing said what its
        /// outer radius was.</para>
        ///
        /// <para>What is drawn instead is a lattice chosen for a RADIUS: a nice step
        /// (<see cref="PolarRings"/>) giving about five rings, every multiple of it present, and
        /// the boundary ring at exactly the window's own radius always drawn and always labelled.
        /// The number of rings is therefore the same at every scale, which is the property that was
        /// missing — a polar plot of tens of ohms now reads exactly like the unit-circle one.</para>
        ///
        /// <para>The radius numbers carry the axis's SI prefix through the same
        /// <see cref="EngineeringFormat"/> path the rectangular ticks use (one group for the whole
        /// axis, never one per ring), and are dropped from the inside outwards wherever the next
        /// one out has already taken the space — so a tight lattice thins its labels rather than
        /// overprinting them.</para>
        /// </summary>
        /// <param name="pattern">
        /// <b>ANT-7 §2 — the dB RADIAL MODE, and it is the only thing on this plot that changes.</b>
        /// Non-null turns the 1/2/5 lattice below into a decibel one: the outer ring is the scale's
        /// reference at world radius 1, the centre is its floor, and the rings fall at
        /// <see cref="PolarPatternScale.RingsDb"/> and are labelled in dB. Null is every polar plot
        /// that existed before, unchanged.
        /// </param>
        /// <param name="bearings">
        /// <b>Draw the 30&#176; spokes and print the angle outside the boundary ring</b>
        /// (<see cref="Plot.ShowPolarAngleLabels"/>). Requires the caller to have reserved
        /// <see cref="PlotRenderer.ComplexAngleLabelMargin"/> in the viewport; nothing here checks,
        /// because the same predicate drives both and a plot with one and not the other is not a
        /// state this code can be in.
        /// </param>
        public static void DrawPolarGrid(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Axes                 axes,
            TransformSet         tf,
            RenderTheme          theme,
            PolarPatternScale?   pattern = null,
            bool                 bearings = false)
        {
            float lw     = LineWidth(canvasSize);
            float gridSw = lw * (float)axes.GridThicknessFactor;

            // The exact plot box for everything except the boundary ring, which is drawn at the end
            // under a box one stroke wider. The ring's radius is at the window's own edge, so it is
            // tangent to the box at four points and the OUTER half of its stroke falls outside —
            // an exact clip takes that half away and the outline reads thinner at top, bottom, left
            // and right than on the diagonals (owner, 2026-09-08). Only the ring gets the slack:
            // widening the clip for the whole grid would let the diameters' square end caps poke a
            // pixel past the frame, which is the same defect one step smaller.
            var exactClip = PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize);

            canvas.Save();
            canvas.ClipRect(exactClip);

            using var axisPaint = StrokePaint(theme.GridColor, gridSw);
            using var ringPaint = StrokePaint(
                RenderTheme.WithOpacity(theme.GridColor, axes.MinorTransparencyScale), gridSw);
            using var tickPaint = StrokePaint(
                theme.TickColor, lw * (float)axes.TickThicknessFactor);

            using var cross = new SKPath();
            cross.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,  0.0));
            cross.LineTo(tf.PrimaryToCanvas(axes.Window.Right, 0.0));
            cross.MoveTo(tf.PrimaryToCanvas(0.0, axes.Window.Top));
            cross.LineTo(tf.PrimaryToCanvas(0.0, axes.Window.Bottom));
            canvas.DrawPath(cross, axisPaint);

            // A complex plot's window is a square centred on the origin — Plot.Autoscale and the
            // axis-limits flyout both go through Plot.SquareCentredOnOrigin — so half its width IS
            // the framed radius. Taken from the X edges because the rings are drawn in X pixels.
            double rMax = Math.Max(Math.Abs(axes.Window.Left), Math.Abs(axes.Window.Right));
            var    ctr  = tf.PrimaryToCanvas(0.0, 0.0);
            var    edge = tf.PrimaryToCanvas(rMax, 0.0);
            float  pxMax = Math.Abs(edge.X - ctr.X);

            if (!(rMax > 0) || !(pxMax > 0) || !float.IsFinite(pxMax))
            {
                canvas.Restore();
                return;
            }

            float  pxPerWorld  = pxMax / (float)rMax;
            var    (step, sub) = PolarRings(rMax);

            // A multiple landing close to the boundary is dropped rather than drawn beside it: the
            // boundary ring is always present, and two circles a fraction of a step apart read as
            // a rendering fault rather than as a lattice. Two fifths of a step is what a radius of
            // 12.5 needs — its lattice runs 2, 4, … 12, and the last of those is half a step from
            // the edge.
            double lastMultiple = rMax - step * 0.4;

            var rings  = new List<double>();
            var labels = new List<string>();

            if (pattern is { } pat)
            {
                // A dB lattice, innermost first so the shared drawing loop below (which treats the
                // LAST entry as the boundary) needs no second form. The boundary is at world radius
                // 1 by construction — not at rMax — so panning or zooming a pattern plot moves the
                // whole disc without ever moving the outer ring off its own reference.
                var dbRings = pat.RingsDb;
                for (int k = dbRings.Count - 1; k >= 0; k--)
                {
                    rings.Add(pat.Radius(dbRings[k]));
                    labels.Add(pat.RingLabel(dbRings[k], withUnit: k == 0));
                }
                if (rings.Count == 0) { rings.Add(1.0); labels.Add(pat.RingLabel(pat.ReferenceDb, true)); }
                // Five dB marks on a ten dB lattice, the same "a minor tick lands on a number worth
                // reading" rule the linear branch applies to its own mantissa.
                sub = 2;
            }
            else
            {
                for (int n = 1; n * step <= lastMultiple; n++) rings.Add(n * step);
                rings.Add(rMax);
            }

            float boundaryPx = pattern is null ? pxMax : (float)rings[^1] * pxPerWorld;

            for (int i = 0; i < rings.Count - 1; i++)
                canvas.DrawCircle(ctr.X, ctr.Y, (float)rings[i] * pxPerWorld, ringPaint);

            // ── The bearing SPOKES (owner request, 2026-09-11) ─────────────────────────────────
            //
            // Drawn at the same weight as the rings, and only where the two diameters have not
            // already drawn one — the 0/90/180/270 spokes ARE the cross above, at axis weight, and
            // a second line over them reads as a thicker one of a different colour.
            if (bearings)
            {
                float rPx = pattern is null ? pxMax : (float)rings[^1] * pxPerWorld;
                using var spokes = new SKPath();
                for (int a = 0; a < 360; a += BearingStepDeg)
                {
                    if (a % 90 == 0) continue;
                    var (ux, uy) = BearingUnit(a, pattern is not null);
                    spokes.MoveTo(ctr.X, ctr.Y);
                    spokes.LineTo(ctr.X + (float)ux * rPx, ctr.Y + (float)uy * rPx);
                }
                canvas.DrawPath(spokes, ringPaint);
            }

            // Minor radii are marked as short ticks ACROSS the two diameters rather than as rings
            // of their own. Five subdivisions of five rings is twenty-five circles, which stops
            // reading as a grid and starts reading as shading; on the axes they give the same fine
            // reading at a fifth of the ink, and they are the polar counterpart of the minor ticks
            // DrawRectGrid puts on its own two edges.
            double minorStep = step / sub;
            var    minorRadii = new List<double>();
            if (pattern is { } patM)
            {
                // Halfway between each pair of dB rings, in dB — which is NOT halfway in radius,
                // because the radius is linear in decibels and the rings are evenly spaced in them.
                // (It is, here; the arithmetic is written as a dB step regardless so a non-uniform
                // lattice would still land right.)
                var db = patM.RingsDb;
                for (int k = 0; k + 1 < db.Count; k++)
                    minorRadii.Add(patM.Radius(0.5 * (db[k] + db[k + 1])));
                if (db.Count > 0)
                {
                    double last = db[^1] - patM.RingStepDb * 0.5;
                    if (last > patM.FloorDb) minorRadii.Add(patM.Radius(last));
                }
                minorStep = minorRadii.Count > 1
                    ? Math.Abs(minorRadii[0] - minorRadii[1])
                    : (minorRadii.Count == 1 ? minorRadii[0] : 0.0);
            }
            else
            {
                for (int n = 1; n * minorStep <= lastMultiple; n++)
                    if (n % sub != 0) minorRadii.Add(n * minorStep);     // a major radius: its ring is drawn
            }

            if (sub > 1 && minorStep * pxPerWorld >= MinMinorTickSpacingPx)
            {
                float  halfX     = (float)(axes.TickLengthX / 2);
                float  halfY     = (float)(axes.TickLengthY / 2);

                using var minorTicks = new SKPath();
                foreach (double rm in minorRadii)
                {
                    foreach (double s in new[] { rm, -rm })
                    {
                        minorTicks.MoveTo(tf.PrimaryToCanvas(s, -halfY));
                        minorTicks.LineTo(tf.PrimaryToCanvas(s,  halfY));
                        minorTicks.MoveTo(tf.PrimaryToCanvas(-halfX, s));
                        minorTicks.LineTo(tf.PrimaryToCanvas( halfX, s));
                    }
                }
                canvas.DrawPath(minorTicks, tickPaint);
            }

            // The boundary ring, and only the boundary ring, under the widened box.
            canvas.Restore();
            canvas.Save();
            canvas.ClipRect(SKRect.Inflate(exactClip, gridSw, gridSw));
            canvas.DrawCircle(ctr.X, ctr.Y, boundaryPx, axisPaint);

            canvas.Restore();

            // The bearings themselves, in the margin PlotRenderer.ComputeViewport reserved for
            // them — so they are drawn UNCLIPPED by the viewport box, and never over the disc.
            // Without that reserved margin this would clip to nothing rather than overprint, which
            // is why the two are written as one feature.
            if (bearings)
                DrawPolarBearings(canvas, axes, theme, lw, ctr, boundaryPx, pattern is not null);

            canvas.Save();
            canvas.ClipRect(exactClip);

            // The radius numbers, whatever the radius is. This used to be gated on
            // `axes.Window.Width < 8` as well, which was safe only while a Polar plot could never
            // frame anything much bigger than the unit circle: once autoscale is allowed off that
            // floor (Plot.UnityMinimumApplies), a locus of tens of ohms drew a grid of unlabelled
            // rings and there was nothing on the plot to say what scale it was at.
            if (canvasSize.W > 250)
            {
                var (lblFont, lblPaint) = MakeTextObjects(axes.FontSizeTicks * 0.85, lw, theme);
                using var _lf1 = lblFont;
                using var _lp1 = lblPaint;
                lblPaint.Color = RenderTheme.WithOpacity(lblPaint.Color, axes.MinorTransparencyScale);

                int   group = EngineeringFormat.GroupFor(EngineeringFormat.AxisMagnitude(rMax));
                float gap   = lw * 2.5f;
                float baseY = ctr.Y - (float)(axes.TickLengthY / 2) * pxPerWorld - gap;

                // Outwards-in, so the boundary number — the one that says what scale the plot is
                // at — is the one that survives a lattice too tight to label completely.
                float takenLeft = float.MaxValue;
                for (int i = rings.Count - 1; i >= 0; i--)
                {
                    // A dB lattice labels itself: its numbers are already round and its outer ring
                    // carries the unit, so the SI-prefix grouping the linear branch needs would turn
                    // "-20 dB" into "-20" with a stray prefix on the axis.
                    string text  = pattern is not null && i < labels.Count
                        ? labels[i]
                        : EngineeringFormat.Tick(rings[i], group, axes.NumDigitsXAxis);
                    float  right = ctr.X + (float)rings[i] * pxPerWorld - gap;
                    float  left  = right - lblFont.MeasureText(text);
                    if (right > takenLeft - gap) continue;

                    canvas.DrawText(text, left, baseY, SKTextAlign.Left, lblFont, lblPaint);
                    takenLeft = left;
                }
            }

            canvas.Restore();
        }

        /// <summary>
        /// The ring lattice for a polar plot framed at <paramref name="rMax"/>: the radial step and
        /// the number of subdivisions of it that carry a minor tick.
        ///
        /// <para>The step is the nearest 1/2/5 decade to <c>rMax / 5</c>, so the plot carries
        /// roughly five rings whatever its scale, and the numbers on them are the ones a reader
        /// would have chosen — a unit-circle plot gets 0.2 &#8230; 1.0 (the companion to a Smith
        /// chart's own radial grid), ±2 gets 0.5 &#8230; 2.0, ±50 gets 10 &#8230; 50.</para>
        ///
        /// <para>Subdivisions follow the step's own mantissa rather than a constant, so a minor
        /// tick always lands on a number worth reading: a step of 1 or 5 divides by five, a step of
        /// 2 divides by four (giving halves, not fifths of a two).</para>
        /// </summary>
        /// <summary>Spacing of the bearing spokes and their labels, in degrees. 30 is what an
        /// antenna-range plot is read on, and it divides 360 into twelve legible marks.</summary>
        internal const int BearingStepDeg = 30;

        /// <summary>
        /// The unit vector for a bearing, in CANVAS axes (y DOWN), under the plot's own angular
        /// convention.
        ///
        /// <para><paramref name="compass"/> true is the PATTERN convention —
        /// 0&#176; at the top, increasing clockwise — and it is
        /// <see cref="PolarPatternAngle.Point"/>'s, restated here in canvas axes rather than called,
        /// because that one returns WORLD coordinates on the unit disc and this has only a pixel
        /// centre and a pixel radius to work from. False is the complex plane's: 0&#176; at the
        /// right, increasing counter-clockwise, which is the angle a locus plot's own points carry.</para>
        /// </summary>
        internal static (double X, double Y) BearingUnit(double deg, bool compass)
        {
            double rad = (compass ? 90.0 - deg : deg) * Math.PI / 180.0;
            return (Math.Cos(rad), -Math.Sin(rad));
        }

        /// <summary>
        /// The bearing numbers, printed just outside the boundary ring in the margin
        /// <see cref="PlotRenderer.ComplexAngleLabelMargin"/> reserved. Each is centred on its own
        /// spoke, which is what puts "0" over the top of a pattern plot and "90" off its right
        /// shoulder without a per-quadrant alignment table.
        /// </summary>
        private static void DrawPolarBearings(SKCanvas canvas, Axes axes, RenderTheme theme,
                                              float lw, SKPoint ctr, float boundaryPx, bool compass)
        {
            var (font, paint) = MakeTextObjects(axes.FontSizeTicks * 0.85, lw, theme);
            using var _f = font;
            using var _p = paint;

            float gap = lw * 3f;
            for (int a = 0; a < 360; a += BearingStepDeg)
            {
                var (ux, uy) = BearingUnit(a, compass);
                string text = a.ToString(CultureInfo.InvariantCulture);

                // Centred ON the spoke: the anchor is pushed out by the text's own half-extent in
                // the spoke's direction, so the label clears the ring by `gap` whatever bearing it
                // is at. The vertical half-extent uses the cap height rather than the full line, so
                // a label at the top and one at the bottom sit the same distance off the ring.
                float halfW = font.MeasureText(text) / 2f;
                float halfH = font.Size * 0.36f;
                float rx    = boundaryPx + gap + halfW;
                float ry    = boundaryPx + gap + halfH;

                float x = ctr.X + (float)ux * rx;
                float y = ctr.Y + (float)uy * ry + halfH;
                canvas.DrawText(text, x, y, SKTextAlign.Center, font, paint);
            }
        }

        internal static (double Step, int Subdivisions) PolarRings(double rMax)
        {
            if (!(rMax > 0) || !double.IsFinite(rMax)) return (0, 0);

            double raw = rMax / PolarRingTarget;
            double mag = Math.Pow(10.0, Math.Floor(Math.Log10(raw)));
            double m   = raw / mag;

            // The thresholds are nudged inwards by a tolerance because the mantissa is a QUOTIENT
            // and a decade boundary does not survive one: rMax = 1.5 gives m = 2.9999999999999996
            // and rMax = 0.75 gives m = 1.4999999999999998, so a bare `m < 3` and `m < 1.5` both
            // take the finer branch and the plot comes back with seven rings and a tick comb twice
            // the density of its neighbours — for a radius one digit long.
            const double tol = 1e-9;
            double nice = m < 1.5 - tol ? 1.0
                        : m < 3.0 - tol ? 2.0
                        : m < 7.0 - tol ? 5.0
                        :                 10.0;
            return (nice * mag, nice == 2.0 ? 4 : 5);
        }

        /// <summary>Rings a polar plot aims to carry, at any scale — what <see cref="PolarRings"/>
        /// searches its step for.</summary>
        private const int PolarRingTarget = 5;

        /// <summary>How far the Smith chart's grid numbers are allowed to shrink with the disc
        /// before they stop shrinking and start being dropped instead. A face much under half the
        /// tick size stops being read and starts being texture, and a chart that small has more to
        /// gain from three legible numbers than from ten unreadable ones.</summary>
        private const double SmithLabelMinScale = 0.45;

        /// <summary>Font sizes of disc RADIUS below which the Smith grid carries no numbers at all.
        /// At the floor scale a number is about two font sizes wide, so a disc under this is one a
        /// single label would very nearly span — there is no chart left in there to annotate.
        /// </summary>
        private const double SmithLabelMinDiscFaces = 4.0;

        /// <summary>Canvas pixels below which the polar minor ticks stop being drawn. Absolute
        /// rather than a multiple of the line width on purpose: this is a question about what the
        /// eye can still separate, and a comb finer than a few pixels reads as a grey band on the
        /// axis however thin its strokes are.</summary>
        private const float MinMinorTickSpacingPx = 4f;

        // ================================================================
        //  Smith chart grid
        // ================================================================

        public static void DrawSmithGrid(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Axes                 axes,
            TransformSet         tf,
            RenderTheme          theme)
        {
            float lw     = LineWidth(canvasSize);
            float gridSw = lw * (float)axes.GridThicknessFactor;

            // The OUTLINE is separated from the grid it bounds, rather than the clip being widened
            // to fit it. Both are needed and they want opposite things: the constant-R and
            // constant-X arcs must be cut off exactly at |Γ| = 1, and the outline — the r = 0
            // circle, which IS that boundary — must not be, since a clip cut to its own radius
            // takes the outer half of its stroke away all the way round. That is why the outline
            // read thinner than the arcs inside it.
            //
            // Widening the disc clip by a stroke fixed the outline and let the reactance arcs run
            // past the boundary by their own thickness (owner, 2026-09-08). Arcs meet the boundary
            // at a shallow angle, so a stroke's width of radial slack shows up as a much longer
            // tail along the edge — an arc leaves the disc visibly where a circle concentric with
            // it would not. So the arcs keep the exact clip they always had, and the outline is
            // drawn afterwards, outside it.
            var   unitCtr = tf.PrimaryToCanvas(0.0, 0.0);
            var   unitEdge = tf.PrimaryToCanvas(1.0, 0.0);
            float unitPxR = Math.Abs(unitEdge.X - unitCtr.X);

            var exactClip = PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize);

            canvas.Save();
            canvas.ClipRect(exactClip);

            bool clipToUnit = axes.Window.Width == 2
                           && axes.Window.X     == -1
                           && axes.Window.Y     == -1;
            if (clipToUnit)
            {
                using var unitPath = new SKPath();
                unitPath.AddCircle(unitCtr.X, unitCtr.Y, unitPxR);
                canvas.ClipPath(unitPath, SKClipOperation.Intersect, antialias: true);
            }

            using var smithPaint = StrokePaint(theme.GridColor, gridSw);

            // OPAQUE, and the transparency is applied once to the whole family below. Painting each
            // arc at MinorTransparencyScale composited every crossing: two 50%% strokes over each
            // other read 75%, three read 87.5%, so the grid was darkest exactly where it is
            // busiest and the chart looked stippled rather than ruled (owner, 2026-09-08). Drawn
            // into a layer at full opacity, an overlap is the same colour as a single stroke,
            // because opaque over opaque is opaque; the layer is then composited once.
            using var minorPaint = StrokePaint(theme.GridColor.WithAlpha(255), gridSw);

            // Only the ALPHA of a SaveLayer paint matters — it is what the finished layer is
            // composited with.
            using var arcLayerPaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha(
                    (byte)Math.Clamp(axes.MinorTransparencyScale * 255.0, 0, 255))
            };

            using var realAxis = new SKPath();
            realAxis.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,  0.0));
            realAxis.LineTo(tf.PrimaryToCanvas(axes.Window.Right, 0.0));
            if (!clipToUnit)
            {
                realAxis.MoveTo(tf.PrimaryToCanvas(1.0, axes.Window.Top));
                realAxis.LineTo(tf.PrimaryToCanvas(1.0, axes.Window.Bottom));
            }

            double[] constantRValues = { 0, 0.5, 1, 2, 5, 10, -0.5, -0.8, -1.2, -1.5, -2, -2.5, -3, -4, -7, -12 };
            double[] constantXValues = { 0.2, 0.5, 1, 2, 5, 10 };

            // The circles that carry a NUMBER, in the order the numbers are PLACED — which is a
            // priority, because a label landing on one already placed is dropped (see below). The
            // unit circle goes first: r = 1 is the chart's own centre and x = ±1 its cardinal arcs,
            // and a zoomed-out chart with no 1 on it says nothing at all. The rest run outwards-in
            // from the roomy r = 0 side, so what is lost is what piles into r → ∞ at the right.
            //
            // Resistance is the positive family only: r = 0 is the outline and the negative values
            // are the extended chart outside |Γ| = 1, and neither has ever been labelled. Reactance
            // is `constantXValues` reordered — that array is INDEXED by the arc mask tables above
            // and must keep its own order.
            double[] constantRLabelValues = { 1, 0.5, 2, 5, 10 };
            double[] constantXLabelValues = { 1, 0.2, 0.5, 2, 5, 10 };

            var rCircles = new (float cx, float cy, float r, double rVal)[constantRValues.Length];
            for (int i = 0; i < constantRValues.Length; i++)
            {
                double rv     = constantRValues[i];
                double radius = 1.0 / (1.0 + rv);
                double centreX = 1.0 - radius;
                var    cPx    = tf.PrimaryToCanvas(centreX, 0.0);
                var    ePx    = tf.PrimaryToCanvas(centreX + radius, 0.0);
                float  pxR    = Math.Abs(ePx.X - cPx.X);
                rCircles[i]   = (cPx.X, cPx.Y, pxR, rv);
            }

            var xCircles = new (float cx, float cy, float r, double xVal)[constantXValues.Length];
            for (int i = 0; i < constantXValues.Length; i++)
            {
                double xv     = constantXValues[i];
                double radius = 1.0 / xv;
                var    cPx    = tf.PrimaryToCanvas(1.0, -radius);
                var    ePx    = tf.PrimaryToCanvas(1.0 + radius, -radius);
                float  pxR    = Math.Abs(ePx.X - cPx.X);
                xCircles[i]   = (cPx.X, cPx.Y, pxR, xv);
            }

            var rMaskTable = new System.Collections.Generic.Dictionary<int, int[]>
            {
                [1]  = new[] { 2 },
                [2]  = new[] { 4 },
                [3]  = new[] { 4 },
                [4]  = new[] { 5 },
                [11] = new[] { 2 },
                [12] = new[] { 4 },
                [13] = new[] { 4 },
                [14] = new[] { 5 },
            };

            var xMaskTable = new System.Collections.Generic.Dictionary<int, int[]>
            {
                [0] = new[] {  3, 13 },
                [1] = new[] {  2, 13 },
                [2] = new[] {  4, 14 },
                [3] = new[] {  4, 14 },
                [4] = new[] {  4, 14 },
            };

            SKPath EvenOddExclusionPath(IEnumerable<(float cx, float cy, float r)> circles)
            {
                var p = new SKPath { FillType = SKPathFillType.EvenOdd };
                p.AddRect(SKRect.Create(0, 0, (float)canvasSize.W, (float)canvasSize.H));
                foreach (var (cx, cy, r) in circles)
                    p.AddCircle(cx, cy, r);
                return p;
            }

            canvas.SaveLayer(arcLayerPaint);

            for (int i = 0; i < rCircles.Length; i++)
            {
                var (cx, cy, pxR, rVal) = rCircles[i];
                if (pxR <= 0 || !float.IsFinite(pxR)) continue;

                // r = 0 IS the unit circle — the chart's outline. It is drawn after this layer,
                // at full strength like the real axis, and never inside it: it is not one of the
                // arcs the transparency is for.
                if (rVal == 0) continue;

                var paint = minorPaint;

                if (rMaskTable.TryGetValue(i, out int[]? xMaskIndices))
                {
                    float realY = tf.PrimaryToCanvas(0.0, 0.0).Y;
                    var maskedCircles = xMaskIndices
                        .SelectMany(xi =>
                        {
                            var (xcx, xcy, xr, _) = xCircles[xi];
                            return new[] { (xcx, xcy, xr), (xcx, 2f * realY - xcy, xr) };
                        });

                    canvas.Save();
                    using var clipPath = EvenOddExclusionPath(maskedCircles);
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                    canvas.DrawCircle(cx, cy, pxR, paint);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawCircle(cx, cy, pxR, paint);
                }
            }

            float realAxisY = tf.PrimaryToCanvas(0.0, 0.0).Y;

            for (int i = 0; i < xCircles.Length; i++)
            {
                var (cx, cy, pxR, _) = xCircles[i];
                if (pxR <= 0 || !float.IsFinite(pxR)) continue;

                float conjCy = 2f * realAxisY - cy;

                if (xMaskTable.TryGetValue(i, out int[]? rMaskIndices))
                {
                    var maskCircles = rMaskIndices
                        .Select(ri => { var (rcx, rcy, rr, _) = rCircles[ri]; return (rcx, rcy, rr); });

                    canvas.Save();
                    using var clipPath = EvenOddExclusionPath(maskCircles);
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                    canvas.DrawCircle(cx,     cy,     pxR, minorPaint);
                    canvas.DrawCircle(cx,     conjCy, pxR, minorPaint);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawCircle(cx,     cy,     pxR, minorPaint);
                    canvas.DrawCircle(cx,     conjCy, pxR, minorPaint);
                }
            }

            canvas.Restore();                       // composite the arc layer, once

            canvas.DrawPath(realAxis, smithPaint);

            // Out of the disc clip: the outline, and only the outline. It and the real axis stay at
            // full strength — they are the chart's frame and its reference, not grid. The rect clip
            // is widened by one stroke for the outline alone, because the disc is tangent to the
            // plot box at four points and an exact box clip halves the stroke there exactly as the
            // disc clip halved it everywhere.
            canvas.Restore();
            canvas.Save();
            canvas.ClipRect(clipToUnit ? SKRect.Inflate(exactClip, gridSw, gridSw) : exactClip);
            if (unitPxR > 0 && float.IsFinite(unitPxR))
                canvas.DrawCircle(unitCtr.X, unitCtr.Y, unitPxR, smithPaint);

            // ---- The grid numbers ----------------------------------------------------------
            //
            // A Smith chart's grid is FIXED in the Γ plane — its arcs ARE the unit disc's — so
            // unlike a polar plot's rings it cannot be re-latticed for a wider window. Zoom out and
            // the whole chart is simply a smaller object in a bigger box, and its numbers have to
            // travel with it. They did not: the size came from the CANVAS (`FontSizeTicks * lw`),
            // so as the window widened the anchors closed on each other at constant text size and
            // the numbers printed over one another — crowded at ±2, a blob by ±5, a smudge at ±10
            // (owner, 2026-09-08).
            //
            // Two things are done about it and BOTH are needed. The text scales with the disc, so
            // the chart stays self-similar as far down as text can still be read; and what will not
            // fit even then is DROPPED rather than overprinted, because past the scale floor there
            // is no size that makes room. Scaling alone ends in two-pixel mush; thinning alone
            // throws away numbers a smaller face would have fitted.
            if (canvasSize.W > 250 && unitPxR > 0 && float.IsFinite(unitPxR))
            {
                // The disc's radius runs as 1/(half-window), so this IS the chart's own zoom, and
                // it is exactly 1 at the unit window — where nothing about the picture changes. It
                // comes from the window's WIDTH rather than from unitPxR so that panning, which
                // moves the disc without resizing it, cannot change the size of a number.
                double zoom  = axes.Window.Width > 2.0 ? 2.0 / axes.Window.Width : 1.0;
                double scale = Math.Max(SmithLabelMinScale, zoom);

                var (lblFont, lblPaint) = MakeTextObjects(axes.FontSizeTicks * 0.85 * scale, lw, theme);
                using var _lf2 = lblFont;
                using var _lp2 = lblPaint;
                lblPaint.Color = RenderTheme.WithOpacity(lblPaint.Color, axes.MinorTransparencyScale);

                lblFont.GetFontMetrics(out var metrics);

                // Below this the chart is a glyph rather than a chart: a number is about as wide as
                // the disc it would annotate, and thinning would leave one arbitrary survivor
                // sitting on a smudge. Nothing is the better answer there than something.
                bool numbered = unitPxR >= lblFont.Size * SmithLabelMinDiscFaces;

                // What has already been placed. A Smith chart's numbers crowd in TWO dimensions —
                // the reactance arcs' labels come down the outside of the disc towards the same
                // r → ∞ point the resistance numbers run into — so this is a rect overlap
                // test, not the one-dimensional "taken so far" edge DrawPolarGrid gets away with.
                var   placed = new List<SKRect>();
                float pad    = lblFont.Size * 0.30f;

                void Place(string text, float left, float baselineY)
                {
                    var box = new SKRect(left, baselineY + metrics.Ascent,
                                         left + lblFont.MeasureText(text), baselineY + metrics.Descent);
                    var probe = SKRect.Inflate(box, pad, pad);
                    foreach (var taken in placed)
                        if (taken.IntersectsWith(probe)) return;

                    placed.Add(box);
                    canvas.DrawText(text, left, baselineY, SKTextAlign.Left, lblFont, lblPaint);
                }

                if (numbered)
                {
                    foreach (double rVal in constantRLabelValues)
                    {
                        var g  = RfHelpers.Z2G(new Complex(rVal, 0));
                        var pt = tf.PrimaryToCanvas(g.Real, g.Imaginary);
                        Place(" " + rVal.ToString("G4"), pt.X, pt.Y - metrics.Ascent);
                    }

                    foreach (double xVal in constantXLabelValues)
                    {
                        var g   = RfHelpers.Z2G(new Complex(0, xVal));
                        var ptU = tf.PrimaryToCanvas( g.Real,  g.Imaginary);
                        var ptD = tf.PrimaryToCanvas( g.Real, -g.Imaginary);

                        string textU = " "  + xVal.ToString("G4");
                        string textD = " -" + xVal.ToString("G4");

                        // Past x = 1 the label would run off the right of the disc, so it hangs
                        // from the anchor's left instead. Unchanged from before the thinning.
                        float leftU = xVal <= 1.0 ? ptU.X : ptU.X - lblFont.MeasureText(textU);
                        float leftD = xVal <= 1.0 ? ptD.X : ptD.X - lblFont.MeasureText(textD);

                        Place(textU, leftU, ptU.Y - metrics.Ascent);
                        Place(textD, leftD, ptD.Y - metrics.Descent);
                    }
                }
            }

            canvas.Restore();
        }

        // ================================================================
        //  Title, X-label, and global Y-label rendering
        // ================================================================

        public struct LabelHitRects
        {
            public SKRect Title;
            public SKRect XLabel;
            public SKRect YLabel;
            public SKRect Y2Label;
        }

        public static LabelHitRects DrawTitleAndAxisLabels(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Plot                 plot,
            TransformSet         tf,
            RenderTheme          theme,
            Func<Trace, string?>? aliasFor = null,
            bool? alwaysShowSource = null)
        {
            float lw = LineWidth(canvasSize);
            var (font, paint) = MakeTextObjects(plot.Axes.FontSizeLabel * 1.4, lw, theme);
            using var _f = font;
            using var _p = paint;
            if (plot.CustomTitleBold) font.Typeface = SkiaFonts.PlexBold;

            // Per-glyph DejaVu fallback, for the same reason the markers and the Table already have
            // one: IBM Plex does not cover every code point circuitRF's own strings are built from,
            // and Skia draws a missing glyph as a NOTDEF box rather than substituting anything.
            // The one that reached a plot was the group separator U+25B8 "▸" — a probe pair is
            // named "WSProbe GATE→DRAIN ▸ block", and dropped into a Y-axis label it rendered as a
            // rectangle (owner, 2026-09-08). Measured rather than assumed: of the characters these
            // labels can carry, Plex lacks U+25B8, U+2220 and U+2225 and has U+2192, so the arrow
            // was never the problem and the triangle beside it always was.
            using var titleFallback = new SKFont(
                plot.CustomTitleBold ? SkiaFonts.DejaVuBold : SkiaFonts.DejaVuRegular, font.Size);

            float w  = (float)canvasSize.W;
            float h  = (float)canvasSize.H;

            float vpLeft    = (float)(tf.Viewport.X                        * w);
            float vpTop     = (float)(tf.Viewport.Y                        * h);
            float vpRight   = (float)((tf.Viewport.X + tf.Viewport.Width)  * w);
            float vpBottom  = (float)((tf.Viewport.Y + tf.Viewport.Height) * h);
            float vpCenterX = (vpLeft + vpRight) / 2f;
            float vpCenterY = (vpTop  + vpBottom) / 2f;

            var rects = new LabelHitRects();

            string title = plot.Title;
            if (!string.IsNullOrEmpty(title))
            {
                // Shrink-to-fit: a long title (e.g. "Pout (dBm) at Constant Eff=…") can be wider
                // than the control. Since the PlotControl now clips to its bounds, scale the title
                // font down so the full text fits within the canvas width instead of being clipped.
                float avail   = w - 4f * lw;                 // small side margin
                float titleW  = RendererText.MeasureTextWithFallback(title, font, titleFallback);
                float titleSz = font.Size;
                if (titleW > avail && titleW > 0f)
                {
                    titleSz = Math.Max(font.Size * (avail / titleW), font.Size * 0.5f);
                    font.Size = titleSz;
                    titleFallback.Size = titleSz;      // the two must stay the same size to measure as one
                }
                float tw = RendererText.MeasureTextWithFallback(title, font, titleFallback);
                float tx = vpCenterX - tw / 2f;
                float ty = vpTop / 2f + font.Size * 0.35f;
                RendererText.DrawLeftTextWithFallback(canvas, title, tx, ty, font, titleFallback, paint);
                rects.Title = new SKRect(tx, ty - font.Size, tx + tw, ty + font.Size * 0.5f);
            }

            // X label(s). One centred label when every trace shares an X quantity (the case that
            // has always existed); ONE ROW PER TRACE, in the trace's own colour, when they do not —
            // the mirror of how the Y labels have always been drawn, and what makes a "plot versus"
            // plot readable when two traces have different X quantities.
            using (var xFont = new SKFont(SkiaFonts.PlexRegular,
                       (float)(plot.Axes.FontSizeTicks * 0.9f * lw)))
            using (var xFallback = new SKFont(SkiaFonts.DejaVuRegular, xFont.Size))
            {
                float rowH = xFont.Size * 1.25f;
                if (plot.XLabelsDiffer)
                {
                    var xTraces = plot.XLabelTraces;
                    float top = vpBottom + (h - vpBottom) / 2f
                              - rowH * (xTraces.Count - 1) / 2f + xFont.Size * 0.35f;
                    for (int i = 0; i < xTraces.Count; i++)
                    {
                        string lbl = plot.XLabelFor(xTraces[i]);
                        if (string.IsNullOrEmpty(lbl)) continue;
                        float tw = RendererText.MeasureTextWithFallback(lbl, xFont, xFallback);
                        float tx = vpCenterX - tw / 2f;
                        float ty = top + rowH * i;
                        paint.Color = RenderTheme.ToSKColor(xTraces[i].Properties.LineColor);
                        RendererText.DrawLeftTextWithFallback(canvas, lbl, tx, ty, xFont, xFallback, paint);
                        if (i == 0)
                            rects.XLabel = new SKRect(tx, ty - xFont.Size, tx + tw,
                                                      ty + rowH * (xTraces.Count - 1) + xFont.Size * 0.5f);
                    }
                    paint.Color = theme.TextColor;
                }
                else
                {
                    string xLabel = plot.XLabel;
                    if (!string.IsNullOrEmpty(xLabel))
                    {
                        float tw = RendererText.MeasureTextWithFallback(xLabel, xFont, xFallback);
                        float tx = vpCenterX - tw / 2f;
                        float ty = vpBottom + (h - vpBottom) / 2f + xFont.Size * 0.35f;
                        RendererText.DrawLeftTextWithFallback(canvas, xLabel, tx, ty, xFont, xFallback, paint);
                        rects.XLabel = new SKRect(tx, ty - xFont.Size, tx + tw, ty + xFont.Size * 0.5f);
                    }
                }
            }

            {
                using var yFont     = new SKFont(SkiaFonts.PlexRegular, (float)(plot.Axes.FontSizeTicks * 0.9f * lw));
                using var yFallback = new SKFont(SkiaFonts.DejaVuRegular, yFont.Size);
                using var yPaint = new SKPaint { IsAntialias = true };
                float sw     = yFont.Size * 1.5f;
                float maxLen = h - 12f;

                void DrawAt(string text, SKColor color, float cx, bool rotRight)
                {
                    if (cx < 0f || cx > w) return;
                    string s = text;
                    while (s.Length > 1 && RendererText.MeasureTextWithFallback(s, yFont, yFallback) > maxLen)
                        s = s[..^1];
                    if (s.Length < text.Length) s = "…" + s.TrimStart();
                    yPaint.Color = color;
                    float tw = RendererText.MeasureTextWithFallback(s, yFont, yFallback);
                    canvas.Save();
                    canvas.Translate(cx, vpCenterY);
                    canvas.RotateDegrees(rotRight ? 90f : -90f);
                    RendererText.DrawLeftTextWithFallback(
                        canvas, s, -tw / 2f, yFont.Size * 0.35f, yFont, yFallback, yPaint);
                    canvas.Restore();
                }

                var (leftAnchor, rightAnchor) =
                    ComputeYLabelAnchors(plot.Axes, vpLeft, vpRight, lw);

                // Compute per-plot minimal labels (same policy as the label-strip controls, and now
                // the same alias resolver too — this Rect Y-axis margin label used to fall back to
                // the raw file-stem heuristic regardless of any alias the user set, since no
                // resolver was ever threaded down to this renderer).
                // Supplied by the caller, which is the layer that can see the LIBRARY (this
                // renderer cannot). Falls back to the setting alone only when no caller supplied
                // it — reading just the setting here is what dropped the "multiple sources
                // loaded" half of the convention for the Rect Y-axis labels.
                bool alwaysSource = alwaysShowSource
                                    ?? AppSettings.Current.AlwaysDisplayDataSourcePrefix;
                var  allLabels    = TraceLabeler.ComputeMinimalLabels(plot.Traces, alwaysSource, aliasFor);
                var  labelLookup  = new Dictionary<Trace, string>();
                for (int i = 0; i < plot.Traces.Count; i++)
                    labelLookup[plot.Traces[i]] = allLabels[i];

                // Reference X-axis = first trace's cube X-axis (the plot's X axis). Cube traces whose X-axis
                // name differs are softly flagged "dimension mismatch" but still attempt to render.
                string? refCubeXAxis = plot.Traces.Count > 0 && plot.Traces[0].IsCubeBound
                    ? plot.Traces[0].CubeXAxisName
                    : null;

                string LabelFor(Trace t)
                {
                    string networkFallback = labelLookup.GetValueOrDefault(t, t.ShortDescription);
                    // Suppressed when the plot draws a per-trace X label row: the rows already state
                    // each trace's X quantity, so repeating "dimension mismatch" on every Y label is
                    // noise on exactly the plots ("Gain vs Pout" beside "Gain vs Pin") where the
                    // difference is deliberate.
                    bool mismatch = t.IsCubeBound
                                    && refCubeXAxis != null
                                    && !plot.XLabelsDiffer
                                    && !string.Equals(t.CubeXAxisName, refCubeXAxis, StringComparison.Ordinal);
                    return t.RectYLabel(networkFallback, mismatch);
                }

                string yLabel = plot.YLabel;
                if (!string.IsNullOrEmpty(yLabel))
                {
                    float cx = leftAnchor - sw * 0.5f;
                    DrawAt(yLabel, theme.TextColor, cx, false);
                    rects.YLabel = new SKRect(cx - sw * 0.5f, vpTop, leftAnchor, vpBottom);
                }
                else if (!plot.CustomYLabelOn)
                {
                    var leftTraces = plot.LeftAxisTraces;
                    int col = 0;
                    for (int i = 0; i < leftTraces.Count; i++)
                    {
                        if (leftTraces[i].IsContourTrace) continue;
                        float cx = leftAnchor - sw * (col + 0.5f);
                        DrawAt(LabelFor(leftTraces[i]),
                               RenderTheme.ToSKColor(leftTraces[i].Properties.LineColor),
                               cx, false);
                        col++;
                    }
                }

                string y2Label = plot.Y2Label;
                if (!string.IsNullOrEmpty(y2Label) && plot.Axes.ShowSecondary)
                {
                    float cx = rightAnchor + sw * 0.5f;
                    DrawAt(y2Label, theme.TextColor, cx, true);
                    rects.Y2Label = new SKRect(rightAnchor, vpTop, cx + sw * 0.5f, vpBottom);
                }
                else if (plot.Axes.ShowSecondary && !plot.CustomY2LabelOn)
                {
                    var rightTraces = plot.RightAxisTraces;
                    int col2 = 0;
                    for (int i = 0; i < rightTraces.Count; i++)
                    {
                        if (rightTraces[i].IsContourTrace) continue;
                        float cx = rightAnchor + sw * (col2 + 0.5f);
                        DrawAt(LabelFor(rightTraces[i]),
                               RenderTheme.ToSKColor(rightTraces[i].Properties.LineColor),
                               cx, true);
                        col2++;
                    }
                }
            }

            return rects;
        }

        public static void DrawComplexXLabels(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Plot                 plot,
            TransformSet         tf,
            RenderTheme          theme)
        {
            float lw = LineWidth(canvasSize);
            float w  = (float)canvasSize.W;
            float h  = (float)canvasSize.H;

            float vpTop     = (float)(tf.Viewport.Y * h);
            float vpBottom  = (float)((tf.Viewport.Y + tf.Viewport.Height) * h);
            float vpCenterX = (float)((tf.Viewport.X + tf.Viewport.Width / 2.0) * w);

            string title = plot.Title;
            if (!string.IsNullOrEmpty(title) && vpTop > 4f)
            {
                float titleSz = (float)(plot.Axes.FontSizeLabel * 1.4f * lw);
                using var tf2 = new SKFont(SkiaFonts.PlexRegular, titleSz);
                using var tp2 = new SKPaint { Color = theme.TextColor, IsAntialias = true };
                // Shrink-to-fit so a long contour title ("X at Constant Y=…") fits the control
                // width rather than being clipped at the now-bounded edges.
                float availT = w - 4f * lw;
                float measT  = tf2.MeasureText(title);
                if (measT > availT && measT > 0f)
                    tf2.Size = Math.Max(titleSz * (availT / measT), titleSz * 0.5f);
                float tw2 = tf2.MeasureText(title);
                canvas.DrawText(title, vpCenterX - tw2 / 2f,
                    vpTop / 2f + tf2.Size * 0.35f, SKTextAlign.Left, tf2, tp2);
            }

            // ── ANT-7 §4 — a pattern plot states what it is, and the freq row is not what it is ──
            //
            //  The per-trace row below reads Min/MaxFreq, which for a CUBE-bound trace is simply the
            //  first and last value of its X axis — on a pattern that is the θ range, so the row read
            //  "freq (0 to 90 GHz)". These lines replace it: whether the radius is normalised or
            //  absolute and what its reference is, and what the θ span means. The cut, the port and
            //  the frequency are the trace's own pinned axes and are already in its label strip.
            if (plot.IsPolarPattern && PatternCaption.Lines(plot) is { Count: > 0 } captions)
            {
                float capSize = (float)(plot.Axes.FontSizeLabel * lw);
                if (capSize < 4f) return;
                float capLineH = capSize * 1.2f;

                using var capFont  = new SKFont(SkiaFonts.PlexRegular, capSize);
                using var capPaint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

                for (int i = 0; i < captions.Count; i++)
                {
                    float cy = vpBottom + capLineH * (i + 0.8f) + 2f * lw;
                    if (cy > h) break;

                    // Shrink-to-fit rather than clip: the reference sentence is the one line that
                    // must never be cut in half, since half of "normalised — outer ring = peak …" is
                    // a claim about an absolute level.
                    capFont.Size = capSize;
                    float avail  = w - 4f * lw;
                    float meas   = capFont.MeasureText(captions[i]);
                    if (meas > avail && meas > 0f)
                        capFont.Size = Math.Max(capSize * (avail / meas), capSize * 0.5f);

                    float cw = capFont.MeasureText(captions[i]);
                    canvas.DrawText(captions[i], vpCenterX - cw / 2f, cy, SKTextAlign.Left, capFont, capPaint);
                }
                return;
            }

            var traces = plot.Traces;
            if (traces.Count == 0) return;

            bool hasCustomX = plot.CustomXLabelOn && !string.IsNullOrEmpty(plot.CustomXLabel);

            // Contour traces never emit freq X-labels; filter them out.
            var nonContourTraces = traces.Where(t => !t.IsContourTrace).ToList();
            if (!hasCustomX && nonContourTraces.Count == 0) return;

            bool multiTrace = nonContourTraces.Count > 1;
            int  drawCount  = hasCustomX ? 1 : nonContourTraces.Count;

            float fontSizePx = (float)(plot.Axes.FontSizeLabel * lw);
            if (fontSizePx < 4f) return;

            float lineH = fontSizePx * 1.2f;

            using var font  = new SKFont(SkiaFonts.PlexRegular, fontSizePx);
            using var paint = new SKPaint { IsAntialias = true };

            for (int i = 0; i < drawCount; i++)
            {
                string label;
                if (hasCustomX)
                {
                    label       = plot.CustomXLabel;
                    paint.Color = theme.TextColor;
                }
                else
                {
                    var    t   = nonContourTraces[i];
                    double min = t.MinFreq;
                    double max = t.MaxFreq;
                    if (!double.IsFinite(min) || !double.IsFinite(max)) continue;

                    double scale = plot.FreqUnits.Scale();
                    string sMin  = (scale * min).ToString("G4");
                    string sMax  = (scale * max).ToString("G4");
                    label       = $"freq ({sMin} to {sMax} {plot.FreqUnits.Description()})";
                    paint.Color = multiTrace
                        ? RenderTheme.ToSKColor(nonContourTraces[i].Properties.LineColor)
                        : theme.TextColor;
                }

                float ty = vpBottom + lineH * (i + 0.8f) + 2f * lw;
                if (ty > h) break;

                float tw = font.MeasureText(label);
                canvas.DrawText(label, vpCenterX - tw / 2f, ty,
                    SKTextAlign.Left, font, paint);
            }
        }

        public static LabelHitRects ComputeLabelHitRects(
            Plot plot, (double W, double H) canvasSize)
        {
            if (!plot.PlotType.IsRect()) return default;

            var tf = PlotRenderer.BuildTransforms(plot, canvasSize);
            float lw = LineWidth(canvasSize);

            float titleSzPx  = (float)(plot.Axes.FontSizeLabel  * 1.4 * lw);
            float xLabelSzPx = (float)(plot.Axes.FontSizeTicks  * 0.9 * lw);

            float w  = (float)canvasSize.W;
            float h  = (float)canvasSize.H;

            float vpLeft    = (float)(tf.Viewport.X                     * w);
            float vpTop     = (float)(tf.Viewport.Y                     * h);
            float vpRight   = (float)((tf.Viewport.X + tf.Viewport.Width)  * w);
            float vpBottom  = (float)((tf.Viewport.Y + tf.Viewport.Height) * h);
            float vpCenterX = (vpLeft + vpRight) / 2f;
            float vpCenterY = (vpTop  + vpBottom) / 2f;

            var rects = new LabelHitRects();

            float ApproxWidth(string s, float sz) => s.Length * sz * 0.55f;

            if (!string.IsNullOrEmpty(plot.Title))
            {
                float tw = ApproxWidth(plot.Title, titleSzPx);
                float tx = vpCenterX - tw / 2f;
                float ty = vpTop / 2f + titleSzPx * 0.35f;
                rects.Title = new SKRect(tx, ty - titleSzPx * 1.2f, tx + tw, ty + titleSzPx * 0.5f);
            }

            if (!string.IsNullOrEmpty(plot.XLabel))
            {
                float tw = ApproxWidth(plot.XLabel, xLabelSzPx);
                float tx = vpCenterX - tw / 2f;
                float ty = vpBottom + (h - vpBottom) / 2f + xLabelSzPx * 0.35f;
                rects.XLabel = new SKRect(tx, ty - xLabelSzPx * 1.2f, tx + tw, ty + xLabelSzPx * 0.5f);
            }

            float yFontSz = (float)(plot.Axes.FontSizeTicks * 0.9f * lw);
            float ySw     = yFontSz * 1.5f;

            var (leftAnchorH, rightAnchorH) =
                ComputeYLabelAnchors(plot.Axes, vpLeft, vpRight, lw);

            if (!string.IsNullOrEmpty(plot.YLabel))
                rects.YLabel = new SKRect(leftAnchorH - ySw, vpTop, leftAnchorH, vpBottom);

            if (!string.IsNullOrEmpty(plot.Y2Label) && plot.Axes.ShowSecondary)
                rects.Y2Label = new SKRect(rightAnchorH, vpTop, rightAnchorH + ySw, vpBottom);

            return rects;
        }

        // ================================================================
        //  Private helpers
        // ================================================================

        private static void DrawBorder(
            SKCanvas canvas, Axes axes, TransformSet tf, float lw, RenderTheme theme)
        {
            using var path = new SKPath();
            var bl = tf.PrimaryToCanvas(axes.Window.Left,  axes.Window.Top);
            var tl = tf.PrimaryToCanvas(axes.Window.Left,  axes.Window.Bottom);
            var tr = tf.PrimaryToCanvas(axes.Window.Right, axes.Window.Bottom);
            var br = tf.PrimaryToCanvas(axes.Window.Right, axes.Window.Top);

            path.MoveTo(bl); path.LineTo(tl);
            path.MoveTo(bl); path.LineTo(br);

            if (axes.ShowSecondary)
            {
                path.MoveTo(tl); path.LineTo(tr);
                path.MoveTo(tr); path.LineTo(br);
            }

            using var p = StrokePaint(theme.BorderColor, 2f * (float)axes.GridThicknessFactor * lw);
            canvas.DrawPath(path, p);
        }

        public static float LineWidth((double W, double H) canvas) =>
            (float)(Math.Min(canvas.W, canvas.H) / 200.0);

        private static SKPaint StrokePaint(SKColor color, float width) =>
            new SKPaint
            {
                Color       = color,
                StrokeWidth = width,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap   = SKStrokeCap.Square
            };

        private static (SKFont Font, SKPaint Paint) MakeTextObjects(
            double fontSizeFactor, float lw, RenderTheme theme)
        {
            var font  = new SKFont(SkiaFonts.PlexRegular, (float)(fontSizeFactor * lw));
            var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
            return (font, paint);
        }

        /// <summary>
        /// Width of the widest tick number ACTUALLY DRAWN on one Y axis — measured by walking the
        /// same tick set, applying the same format string, and applying the same near-zero
        /// normalisation that <see cref="DrawRectGrid"/>'s own tick-label loop uses, so the two can
        /// never disagree about how wide the number column is.
        ///
        /// **Measuring only the window endpoints (Top/Bottom) is NOT equivalent and was the bug**:
        /// tick labels are drawn at every major gridline, and an intermediate tick can format wider
        /// than both endpoints — e.g. an axis spanning -10…10 draws "-10" and "10" at its ends but
        /// also "-7.5" in between, which is wider than either. The anchor derived from it then sat
        /// too close to the axis and the rotated per-trace Y label overlapped the tick numbers.
        /// This affects BOTH axes symmetrically (left labels are right-aligned ending at the left
        /// anchor; right labels start at the right anchor), so both are computed the same way here.
        ///
        /// Falls back to the endpoint measurement only when the tick set yields nothing finite
        /// (degenerate axis), so the label can never end up flush against the axis.
        /// </summary>
        internal static float MaxYTickLabelWidth(SKFont font, Axes axes, bool secondary)
        {
            // MajorY is identical for minorTicks true/false (minor ticks populate separate lists),
            // so the cheaper call is safe and matches what the draw path lays out.
            var ticks  = axes.Ticks(minorTicks: false);
            int digits = secondary ? axes.NumDigitsRightY : axes.NumDigitsLeftY;

            // The SAME group, tick step and snap the draw loop uses. An SI prefix is a whole
            // character wide, so measuring without it puts the rotated axis label on top of the
            // numbers — which is the failure this method's own remarks were written about.
            var    window   = secondary ? axes.WindowSecondary : axes.Window;
            double tickStep = secondary ? axes.Y2Tick : axes.YTick;
            int    group    = EngineeringFormat.GroupFor(
                EngineeringFormat.AxisMagnitude(window.Top, window.Bottom));

            float max = 0f;
            foreach (var (primary, secondaryValue) in ticks.MajorY)
            {
                // The draw loop skips the whole iteration on a non-finite PRIMARY (the padding NaNs
                // Axes.Ticks appends to equalise list lengths), then guards the secondary label on
                // its own finiteness — mirror both, or a padded NaN row would be measured as a
                // label that is never actually drawn.
                if (!double.IsFinite(primary)) continue;
                double raw = secondary ? secondaryValue : primary;
                if (!double.IsFinite(raw)) continue;

                double v = EngineeringFormat.SnapNearZero(raw, tickStep);
                max = Math.Max(max, font.MeasureText(EngineeringFormat.Tick(v, group, digits)));
            }
            if (max > 0f) return max;

            return Math.Max(font.MeasureText(EngineeringFormat.Tick(window.Top,    group, digits)),
                            font.MeasureText(EngineeringFormat.Tick(window.Bottom, group, digits)));
        }

        /// <summary>
        /// X anchors for the rotated per-trace Y-label columns: just outside the widest actual tick
        /// number on each side, plus <see cref="DescriptionStripPad"/>. Shared by the draw path and
        /// the hit-rect path — these were two hand-maintained copies of the same expression, which
        /// is exactly how a label and its own clickable region drift apart.
        /// </summary>
        /// <remarks>
        /// The <paramref name="tickFont"/> overload is the real implementation; the convenience
        /// overload below just supplies the tick font the renderer draws with. Taking the font as a
        /// parameter is also what makes this headlessly testable — <c>SkiaFonts.PlexRegular</c>
        /// cannot load without a live Avalonia platform (see src/Ui/CLAUDE.md), so a test passes
        /// <c>SKTypeface.Default</c> instead and still exercises the real geometry.
        /// </remarks>
        internal static (float Left, float Right) ComputeYLabelAnchors(
            SKFont tickFont, Axes axes, float vpLeft, float vpRight, float lw)
        {
            float leftTickW  = MaxYTickLabelWidth(tickFont, axes, secondary: false);
            float rightTickW = MaxYTickLabelWidth(tickFont, axes, secondary: true);
            return (vpLeft  - lw * 4f - leftTickW  - DescriptionStripPad * lw,
                    vpRight + lw * 4f + rightTickW + DescriptionStripPad * lw);
        }

        private static (float Left, float Right) ComputeYLabelAnchors(
            Axes axes, float vpLeft, float vpRight, float lw)
        {
            using var tickFont = new SKFont(SkiaFonts.PlexRegular, (float)(axes.FontSizeTicks * lw));
            return ComputeYLabelAnchors(tickFont, axes, vpLeft, vpRight, lw);
        }
    }
}
