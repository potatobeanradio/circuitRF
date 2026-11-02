// ================================================================
//  TrialRenderer.cs  —  a family or scatter drawn by trial (brief-yield-9)
//
//  DRAW ORDER IS THE REQUIREMENT (R-ya9-1). Behind everything the
//  envelope band; then the members that PASS, in the theme's neutral
//  pass colour at reduced opacity (never the trace's own, which may be
//  the fail colour's red); then those that FAIL, in the fail colour —
//  so a failure is never buried under a thousand passes; then the
//  nominal over all of them in the trace's full colour and width. A
//  member that did not evaluate is not drawn (the legend counts it).
//  A selected trial (R-ya9-6) draws last of all, highlighted, and the
//  rest dim.
//
//  It computes nothing: every category, band, nominal and fit was
//  resolved by TrialResolve.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    /// <summary>What one stroke of a family's plan draws.</summary>
    public enum TrialStrokeKind { Member, Nominal, Selected }

    /// <summary>One stroke of <see cref="TrialRenderer.FamilyPlan"/>: a member (by index), the nominal, or a selected
    /// member; its colour with opacity; its width as a multiple of the trace's.</summary>
    public readonly record struct TrialStroke(TrialStrokeKind Kind, int Member, SKColor Color, float WidthFactor);

    public static class TrialRenderer
    {
        /// <summary>The share of the trace's opacity a passing member is drawn at.</summary>
        public const double PassOpacity = 0.35;

        /// <summary>The share of its own opacity an unselected element keeps while a trial is selected.</summary>
        public const double DimOpacity = 0.2;

        /// <summary>True when <paramref name="trace"/> draws through here rather than the plain family/curve path.</summary>
        public static bool Handles(Trace trace)
        {
            if (trace.DrawsBars) return false;
            if (trace.IsFamily)
                return trace.MemberCategories is not null || trace.MemberSlots is not null || trace.NominalPoints.Count > 0
                    || trace.Band is not null || !trace.ShowCurves || trace.HasTrialSelection;
            return trace.MemberCategories is not null || trace.Fit is not null || trace.HasTrialSelection;
        }

        /// <summary>
        /// The order a family's members draw in (R-ya9-1): passes, then members with no pass/fail (uncoloured or a
        /// category of their own), then fails; a member that did not evaluate is left out. Indices into
        /// <see cref="Trace.FamilyCurves"/>. Selection does not reorder this — selected members draw again, after.
        /// </summary>
        public static IReadOnlyList<int> MemberDrawOrder(Trace trace)
        {
            int n = trace.FamilyCurves.Count;
            var cats = trace.MemberCategories;
            int Layer(int k) => cats is null || k >= cats.Length ? 1 : cats[k] switch
            {
                TrialCategory.Pass => 0, TrialCategory.Fail => 2, TrialCategory.NotEvaluated => -1, _ => 1,
            };
            return [.. Enumerable.Range(0, n).Where(k => Layer(k) >= 0).OrderBy(Layer).ThenBy(k => k)];
        }

        /// <summary>The colour and opacity one element draws in, before any selection dimming.</summary>
        public static (SKColor Color, double Opacity) ElementPaint(Trace trace, int k, RenderTheme theme, bool marker = false)
        {
            var props = trace.Properties;
            SKColor own = marker ? props.MarkerColor : props.LineColor;
            double  op  = marker ? props.MarkerOpacity : props.LineOpacity;
            if (trace.MemberSlots is { } slots && k < slots.Length)
            {
                int start = Math.Max(0, Array.IndexOf(TraceProperties.LineColorOrder, props.LineColorIndex));
                int idx   = TraceProperties.LineColorOrder[(start + slots[k]) % TraceProperties.LineColorOrder.Length];
                return (TraceProperties.ColorLUT[idx], op);
            }
            if (trace.MemberCategories is not { } cats || k >= cats.Length) return (own, op);
            return cats[k] switch
            {
                TrialCategory.Pass => (theme.PassColor, op * (trace.IsFamily ? PassOpacity : 1.0)),
                TrialCategory.Fail => (theme.FailColor, op),
                _                  => (own, op),
            };
        }

        /// <param name="cache">A host that redraws unchanged plots (the Data Display) passes its cache, and the members are
        /// then drawn from a picture once they have not changed between two frames; null draws them directly, as a
        /// vector writer must.</param>
        public static void Draw(SKCanvas canvas, (double W, double H) canvasSize, Trace trace, Plot plot,
                                TransformSet tf, RenderTheme theme, TrialLayerCache? cache = null)
        {
            float lw      = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            var   props   = trace.Properties;
            float strokeW = lw * (float)props.LineWidth;
            bool  sec     = trace.UseSecondaryAxis;
            bool  sel     = trace.HasTrialSelection;

            if (!trace.IsFamily) { DrawScatter(canvas, trace, plot, tf, theme, lw, sel); return; }

            // ── the band, behind ───────────────────────────────────────────────────
            if (trace.Band is { } band && band.X.Length > 1)
            {
                using var fill = new SKPath();
                bool first = true;
                for (int i = 0; i < band.X.Length; i++)
                {
                    if (!double.IsFinite(band.Lo[i]) || !tf.XIsPlottable(band.X[i])) continue;
                    var p = tf.ToCanvas(band.X[i], band.Lo[i], sec);
                    if (first) { fill.MoveTo(p); first = false; } else fill.LineTo(p);
                }
                for (int i = band.X.Length - 1; i >= 0; i--)
                {
                    if (!double.IsFinite(band.Hi[i]) || !tf.XIsPlottable(band.X[i])) continue;
                    fill.LineTo(tf.ToCanvas(band.X[i], band.Hi[i], sec));
                }
                fill.Close();
                using var fillPaint = new SKPaint
                {
                    Color = theme.ToSKColor(props.LineColor, theme.FillOpacity * props.LineOpacity * (sel ? 0.5 : 1)),
                    Style = SKPaintStyle.Fill, IsAntialias = true,
                };
                canvas.DrawPath(fill, fillPaint);
                StrokePolyline(canvas, tf, sec, band.X.Select((x, i) => new Vector2((float)x, (float)band.Centre[i])),
                               theme.ToSKColor(props.LineColor, props.LineOpacity), strokeW * 0.75f);
            }

            // ── the members, the nominal, the selection — in plan order ────────────
            // The members lead the plan, so drawing them first from a picture keeps the order.
            var plan = FamilyPlan(trace, theme);
            int lead = 0;
            while (lead < plan.Count && plan[lead].Kind == TrialStrokeKind.Member) lead++;
            var members = lead == plan.Count ? plan : plan.Take(lead).ToList();

            void Stroke(SKCanvas c, TrialStroke stroke)
            {
                var pts = stroke.Kind == TrialStrokeKind.Nominal ? trace.NominalPoints : trace.FamilyCurves[stroke.Member].Points;
                StrokePolyline(c, tf, sec, pts, stroke.Color, strokeW * stroke.WidthFactor);
            }

            if (lead == 0 || cache is null
                || !cache.TryDraw(canvas, trace, tf, sec, strokeW, members, c => { foreach (var s in members) Stroke(c, s); }))
                for (int i = 0; i < lead; i++) Stroke(canvas, plan[i]);
            for (int i = lead; i < plan.Count; i++) Stroke(canvas, plan[i]);
        }

        /// <summary>
        /// The strokes a family draws over its band, in order (R-ya9-1): the passing members, then members with no
        /// pass/fail, then the failing ones; then the nominal in the trace's full colour; then any selected member,
        /// highlighted at twice the width, the rest having dimmed. A member that did not evaluate is not in it.
        /// </summary>
        public static IReadOnlyList<TrialStroke> FamilyPlan(Trace trace, RenderTheme theme)
        {
            var  props = trace.Properties;
            bool sel   = trace.HasTrialSelection;
            var  plan  = new List<TrialStroke>();
            if (trace.ShowCurves && props.LineEnabled)
                foreach (int k in MemberDrawOrder(trace))
                {
                    if (sel && trace.ElementSelected(k)) continue;   // drawn last, highlighted
                    var (color, opacity) = ElementPaint(trace, k, theme);
                    plan.Add(new(TrialStrokeKind.Member, k, theme.ToSKColor(color, opacity * (sel ? DimOpacity : 1)), 1f));
                }
            if (trace.NominalPoints.Count > 1)
                plan.Add(new(TrialStrokeKind.Nominal, -1,
                             theme.ToSKColor(props.LineColor, sel ? props.LineOpacity * 0.6 : 1.0), 1f));
            if (sel)
                for (int k = 0; k < trace.FamilyCurves.Count; k++)
                    if (trace.ElementSelected(k))
                        plan.Add(new(TrialStrokeKind.Selected, k, theme.ToSKColor(ElementPaint(trace, k, theme).Color, 1.0), 2f));
            return plan;
        }

        /// <summary>A scatter: its points coloured by category, the selection highlighted, and its fit line.</summary>
        private static void DrawScatter(SKCanvas canvas, Trace trace, Plot plot, TransformSet tf, RenderTheme theme,
                                        float lw, bool sel)
        {
            var  props = trace.Properties;
            bool sec   = trace.UseSecondaryAxis;
            if (props.LineEnabled && trace.Points.Count > 1)
                StrokePolyline(canvas, tf, sec, trace.Points,
                               theme.ToSKColor(props.LineColor, props.LineOpacity * (sel ? DimOpacity : 1)),
                               lw * (float)props.LineWidth);

            if (trace.Fit is { } fit)
            {
                var win = plot.Axes.Window;
                var a = tf.ToCanvas(win.Left,  fit.Intercept + fit.Slope * win.Left,  sec);
                var b = tf.ToCanvas(win.Right, fit.Intercept + fit.Slope * win.Right, sec);
                using var fp = new SKPaint
                {
                    Color = theme.ToSKColor(props.LineColor, 0.9), StrokeWidth = lw * (float)props.LineWidth,
                    Style = SKPaintStyle.Stroke, IsAntialias = true,
                };
                canvas.DrawLine(a, b, fp);   // inside the viewport clip every trace is drawn in
            }

            if (!props.MarkerEnabled) return;
            float ms = lw * (float)props.MarkerSize;
            using var stroke = new SKPaint { Color = new SKColor(0, 0, 0, 200), StrokeWidth = lw / 2f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var fillP  = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

            void Dot(int k, double dim, float size)
            {
                var pt = trace.Points[k];
                if (!tf.XIsPlottable(pt.X)) return;
                if (trace.MemberCategories is { } cats && k < cats.Length && cats[k] == TrialCategory.NotEvaluated) return;
                var (color, opacity) = ElementPaint(trace, k, theme, marker: true);
                fillP.Color = theme.ToSKColor(color, opacity * dim);
                var px = tf.ToCanvas(pt.X, pt.Y, sec);
                var r  = new SKRect(px.X - size, px.Y - size, px.X + size, px.Y + size);
                canvas.DrawOval(r, fillP);
                if (dim >= 1) canvas.DrawOval(r, stroke);
            }

            // Passes, then fails, so a fail is never under a pass; the selection last.
            var order = Enumerable.Range(0, trace.Points.Count)
                .OrderBy(k => trace.MemberCategories is { } c && k < c.Length && c[k] == TrialCategory.Fail ? 1 : 0).ToList();
            foreach (int k in order)
                if (!(sel && trace.ElementSelected(k))) Dot(k, sel ? DimOpacity : 1, ms);
            if (sel)
                foreach (int k in order)
                    if (trace.ElementSelected(k)) Dot(k, 1, ms * 1.6f);
        }

        private static void StrokePolyline(SKCanvas canvas, TransformSet tf, bool sec, IEnumerable<Vector2> points,
                                           SKColor color, float width)
        {
            using var path = new SKPath();
            bool first = true;
            foreach (var pt in points)
            {
                // A point a log X axis cannot place breaks the line rather than being bridged (TraceRenderer's rule).
                if (!float.IsFinite(pt.Y) || !tf.XIsPlottable(pt.X)) { first = true; continue; }
                var px = tf.ToCanvas(pt.X, pt.Y, sec);
                if (first) { path.MoveTo(px); first = false; } else path.LineTo(px);
            }
            using var paint = new SKPaint
            {
                Color = color, StrokeWidth = width, Style = SKPaintStyle.Stroke, IsAntialias = true,
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            };
            canvas.DrawPath(path, paint);
        }
    }
}
