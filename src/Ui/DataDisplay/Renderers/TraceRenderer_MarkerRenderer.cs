// ================================================================
//  TraceRenderer.cs  —  Skia drawing for Trace objects
//
//  Ported from splotRF/src/Renderers/TraceRenderer_MarkerRenderer.cs —
//  namespace renamed to CircuitRF.Ui.DataDisplay; font seam retargeted
//  from SkiaFonts.Regular/Bold (DejaVu) to SkiaFonts.PlexRegular/PlexBold
//  (IBM Plex).
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    public static class TraceRenderer
    {
        public static void Draw(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Trace                trace,
            TransformSet         tf,
            RenderTheme          theme,
            bool                 stemMode = false)
        {
            float lw    = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            var   props = trace.Properties;

            // ---- Family trace (Phase 7.3b) ----
            // Every curve in the family shares the trace's single line color (no per-curve
            // stepping, no legend) — the family reads as one trace drawn N times.
            if (trace.IsFamily)
            {
                if (!props.LineEnabled) return;
                float strokeW   = lw * (float)props.LineWidth;
                bool  useSecond = trace.UseSecondaryAxis;

                if (stemMode)
                {
                    using var stemPaint = BuildStemPaint(props, lw, strokeW);
                    using var headPaint = BuildHeadPaint(props);
                    foreach (var curve in trace.FamilyCurves)
                        foreach (var pt in curve.Points)
                            DrawStem(canvas, tf, pt.X, pt.Y, useSecond, lw, strokeW, stemPaint, headPaint);
                    return;
                }

                using var paint = new SKPaint
                {
                    Color       = RenderTheme.ToSKColor(props.LineColor, props.LineOpacity),
                    StrokeWidth = strokeW,
                    Style       = SKPaintStyle.Stroke,
                    IsAntialias = true,
                    StrokeCap   = SKStrokeCap.Round,
                    StrokeJoin  = SKStrokeJoin.Round
                };
                if (props.LineType == LineType.Dashed)
                    paint.PathEffect = SKPathEffect.CreateDash(new[] { strokeW * 3f, strokeW * 2f }, 0);
                foreach (var curve in trace.FamilyCurves)
                {
                    using var p = new SKPath();
                    bool first = true;
                    foreach (var pt in curve.Points)
                    {
                        var px = tf.ToCanvas(pt.X, pt.Y, useSecond);
                        if (first) { p.MoveTo(px); first = false; } else p.LineTo(px);
                    }
                    canvas.DrawPath(p, paint);
                }
                return;
            }

            // ---- Stem plot (harmonic-index X-axis) ----
            if (stemMode && props.LineEnabled)
            {
                float     strokeW   = lw * (float)props.LineWidth;
                bool      useSecond = trace.UseSecondaryAxis;
                using var stemPaint = BuildStemPaint(props, lw, strokeW);
                using var headPaint = BuildHeadPaint(props);
                foreach (var pt in trace.Points)
                    DrawStem(canvas, tf, pt.X, pt.Y, useSecond, lw, strokeW, stemPaint, headPaint);
                // fall through to draw point markers as well if enabled
            }
            else if (props.LineEnabled)
            {
                // ---- Line / curve ----
                float strokeW = lw * (float)props.LineWidth;

                using var path  = BuildPath(trace, tf);
                using var paint = new SKPaint
                {
                    Color       = RenderTheme.ToSKColor(props.LineColor, props.LineOpacity),
                    StrokeWidth = strokeW,
                    Style       = SKPaintStyle.Stroke,
                    IsAntialias = true,
                    StrokeCap   = SKStrokeCap.Round,
                    StrokeJoin  = SKStrokeJoin.Round
                };

                if (props.LineType == LineType.Dashed)
                    paint.PathEffect = SKPathEffect.CreateDash(
                        new[] { strokeW * 3f, strokeW * 2f }, 0);

                canvas.DrawPath(path, paint);
            }

            // ---- Point markers ----
            if (props.MarkerEnabled && !trace.IsStabilityCircle)
            {
                float ms           = lw * (float)props.MarkerSize;
                bool  useSecondary = trace.UseSecondaryAxis;

                using var fillPaint = new SKPaint
                {
                    Color       = RenderTheme.ToSKColor(props.MarkerColor, props.MarkerOpacity),
                    Style       = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                using var strokePaint = new SKPaint
                {
                    Color       = new SKColor(0, 0, 0, 200),
                    StrokeWidth = lw / 2f,
                    Style       = SKPaintStyle.Stroke,
                    IsAntialias = true
                };

                foreach (var pt in trace.Points)
                {
                    var px   = tf.ToCanvas(pt.X, pt.Y, useSecondary);
                    var rect = new SKRect(px.X - ms, px.Y - ms, px.X + ms, px.Y + ms);

                    if (props.MarkerType == MarkerType.Square)
                    {
                        canvas.DrawRect(rect, fillPaint);
                        canvas.DrawRect(rect, strokePaint);
                    }
                    else
                    {
                        canvas.DrawOval(rect, fillPaint);
                        canvas.DrawOval(rect, strokePaint);
                    }
                }
            }
        }

        // ---- Stem rendering helpers ------------------------------------

        private static SKPaint BuildStemPaint(TraceProperties props, float lw, float strokeW)
            => new SKPaint
            {
                Color       = RenderTheme.ToSKColor(props.LineColor, props.LineOpacity),
                StrokeWidth = strokeW,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap   = SKStrokeCap.Round,
                PathEffect  = props.LineType == LineType.Dashed
                    ? SKPathEffect.CreateDash(new[] { strokeW * 3f, strokeW * 2f }, 0)
                    : null
            };

        private static SKPaint BuildHeadPaint(TraceProperties props)
            => new SKPaint
            {
                Color       = RenderTheme.ToSKColor(props.LineColor, props.LineOpacity),
                Style       = SKPaintStyle.Fill,
                IsAntialias = true
            };

        private static void DrawStem(
            SKCanvas     canvas,
            TransformSet tf,
            double       worldX,
            double       worldY,
            bool         useSecondary,
            float        lw,
            float        strokeW,
            SKPaint      stemPaint,
            SKPaint      headPaint)
        {
            var basePx = tf.ToCanvas(worldX, 0,      useSecondary);
            var tipPx  = tf.ToCanvas(worldX, worldY, useSecondary);

            float stemLenPx = Math.Abs(basePx.Y - tipPx.Y);

            // dir > 0 when tip is above baseline in canvas space (positive world value)
            float dir = Math.Sign(basePx.Y - tipPx.Y);
            if (dir == 0) dir = 1;

            // Arrowhead height keyed on strokeW so it scales with LineWidth.
            // Floor at 2× strokeW guarantees the rounded cap (radius = strokeW/2) is
            // always inside the triangle; cap at stemLen/3 so short stems stay clean.
            float ah = Math.Max(strokeW * 2f, Math.Min(strokeW * 4f, stemLenPx * 0.33f));

            // Terminate the stem at the arrowhead base so the filled triangle covers
            // the line cap — prevents cap bleed above the apex.
            canvas.DrawLine(basePx.X, basePx.Y, tipPx.X, tipPx.Y + dir * ah, stemPaint);

            using var head = new SKPath();
            head.MoveTo(tipPx.X,             tipPx.Y);
            head.LineTo(tipPx.X - ah * 0.5f, tipPx.Y + dir * ah);
            head.LineTo(tipPx.X + ah * 0.5f, tipPx.Y + dir * ah);
            head.Close();
            canvas.DrawPath(head, headPaint);
        }

        // ---- Path building ----------------------------------------------

        private static SKPath BuildPath(Trace trace, TransformSet tf)
        {
            var  path         = new SKPath();
            bool useSecondary = trace.UseSecondaryAxis;

            if (trace.IsStabilityCircle)
            {
                for (int i = 0; i < trace.StabilityCircleCentres.Count; i++)
                {
                    var   c   = trace.StabilityCircleCentres[i];
                    var   cPx = tf.ToCanvas(c.X, c.Y, useSecondary);
                    var   ePx = tf.ToCanvas(c.X + (float)trace.StabilityCircleRadii[i],
                                            c.Y, useSecondary);
                    float r   = Math.Abs(ePx.X - cPx.X);
                    path.AddCircle(cPx.X, cPx.Y, r);
                }
            }
            else
            {
                bool first = true;
                foreach (var pt in trace.Points)
                {
                    var px = tf.ToCanvas(pt.X, pt.Y, useSecondary);
                    if (first) { path.MoveTo(px); first = false; }
                    else        path.LineTo(px);
                }
            }

            return path;
        }
    }
}


// ================================================================
//  MarkerRenderer.cs  —  Skia drawing for Marker objects
//
//  Two separate rendering contexts:
//    DrawSymbol   — called by PlotRenderer, inside the plot's own Skia
//                   canvas (clipped to the plot area).  Font scales with
//                   canvas height so the glyph stays proportional.
//    DrawInfoBox  — called by MarkerInfoBoxView's ICustomDrawOperation.
//                   Renders at (0,0) within the control's own canvas.
//                   Uses a fixed logical font size so the box is always
//                   readable at the same scale regardless of plot size.
//    MeasureInfoBox — call this to determine the Width/Height that should
//                   be set on the MarkerInfoBoxView before drawing.
//
//  Skia text API: SKFont for size/embolden, SKPaint for color.
// ================================================================

namespace CircuitRF.Ui.DataDisplay
{
    public static class MarkerRenderer
    {

        // ---- Symbol: triangle + name at data location -------------------
        //  Rendered inside the plot's shared Skia canvas.
        //
        //  selectionColor must be resolved on the UI thread by the caller
        //  (PlotControl.Render) and passed in — never call GetTransparentAccent
        //  from inside a ICustomDrawOperation.Render which runs on the compositor thread.

        public static void DrawSymbol(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Marker               marker,
            Trace                trace,
            TransformSet         tf,
            RenderTheme          theme,
            bool                 isSelected     = false,
            SKColor              selectionColor = default)
        {
            bool   useSecondary = trace.UseSecondaryAxis;
            var    dl           = trace.GetMarkerDataLocation(marker);
            var    dataPx       = tf.ToCanvas(dl.X, dl.Y, useSecondary);

            // brief-dd-loadpull-contour-ux-round8 §1: a contour marker in Mode 1 (interpolated —
            // MarkerKind.Contour && !ContourSnapped) is sized/lettered like harmonicaRF's termination
            // marker (canvas-proportional radius, not derived from SymbolTextSize). Mode 2
            // (ContourSnapped) and every non-contour marker keep the original triangle glyph/sizing.
            bool isContourMode1 = marker.MarkerKind == MarkerKind.Contour && !marker.ContourSnapped;

            float ts = isContourMode1
                ? ContourMarkerRadius(canvasSize) * 1.15f
                : SymbolTextSize(marker, canvasSize);

            using var glyphPath = new SKPath();
            if (isContourMode1)
            {
                // Ringed circle: filled disc + thin black stroked ring (design §9) —
                // signals the reading is a 2-D interpolant, not a measured/grid value.
                float r = ContourMarkerRadius(canvasSize);
                glyphPath.AddCircle(dataPx.X, dataPx.Y, r);

                using var discPaint = new SKPaint
                {
                    Color       = ResolveContourMarkerFill(),
                    Style       = SKPaintStyle.Fill,
                    IsAntialias = true,
                };
                canvas.DrawPath(glyphPath, discPaint);

                using var ringPaint = new SKPaint
                {
                    Color       = SKColors.Black,
                    StrokeWidth = Math.Max(1f, ts * 0.08f),
                    Style       = SKPaintStyle.Stroke,
                    IsAntialias = true,
                };
                canvas.DrawPath(glyphPath, ringPaint);

                using var nameFont = new SKFont(SkiaFonts.PlexBold, ts);
                float     tw       = nameFont.MeasureText(marker.Name);
                if (marker.Name.Length <= 2)
                {
                    // Short name: centred inside the disc, harmonicaRF's metrics — always black,
                    // since the disc fill is deliberately light enough to keep it legible.
                    using var namePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                    canvas.DrawText(
                        marker.Name,
                        dataPx.X - tw / 2f,
                        dataPx.Y + ts * 0.36f,
                        SKTextAlign.Left, nameFont, namePaint);
                }
                else
                {
                    // Longer name: keep today's behavior — centred above the glyph.
                    using var namePaint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
                    canvas.DrawText(
                        marker.Name,
                        dataPx.X - tw / 2f,
                        dataPx.Y - ts - 4f,
                        SKTextAlign.Left, nameFont, namePaint);
                }
            }
            else
            {
                using var font  = new SKFont(SkiaFonts.PlexBold, ts);
                using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

                // Name label centred above the triangle apex
                float tw = font.MeasureText(marker.Name);
                canvas.DrawText(
                    marker.Name,
                    dataPx.X - tw / 2f,
                    dataPx.Y - ts - 4f,
                    SKTextAlign.Left, font, paint);

                // Downward-pointing filled triangle (unchanged from prior behavior).
                glyphPath.MoveTo(dataPx.X,           dataPx.Y);
                glyphPath.LineTo(dataPx.X - ts / 2f, dataPx.Y - ts);
                glyphPath.LineTo(dataPx.X + ts / 2f, dataPx.Y - ts);
                glyphPath.Close();

                using var triPaint = new SKPaint
                {
                    Color       = theme.TextColor,
                    Style       = SKPaintStyle.Fill,
                    IsAntialias = true,
                };
                canvas.DrawPath(glyphPath, triPaint);
            }

            // Selection highlight of marker glyph; note to AI tools:  never change this selection algorithm
            if (isSelected)
            {
                using var hlPaint = new SKPaint
                {
                    Color       = selectionColor,
                    StrokeWidth = 2f,
                    Style       = SKPaintStyle.Stroke,
                    IsAntialias = true,
                };
                canvas.DrawPath(glyphPath, hlPaint);
            }
        }

        /// <summary>
        /// Hit radius in canvas pixels for marker symbol drag detection.
        /// 1.5× the contour Mode-1 disc radius for an interpolated contour marker (§1,
        /// brief-dd-loadpull-contour-ux-round8); 1.5× the symbol triangle height otherwise.
        /// </summary>
        public static float SymbolHitRadius(Marker marker, (double W, double H) canvasSize)
        {
            bool isContourMode1 = marker.MarkerKind == MarkerKind.Contour && !marker.ContourSnapped;
            return isContourMode1
                ? ContourMarkerRadius(canvasSize) * 1.5f
                : SymbolTextSize(marker, canvasSize) * 1.5f;
        }

        /// <summary>
        /// Canvas-proportional radius for a Mode-1 contour marker disc — mirrors harmonicaRF's
        /// termination-marker geometry (<c>HarmonicaPanelRenderer.DrawMarkers</c>,
        /// <c>r = max(6f, min(W,H) * 0.020)</c>) so the two read as the same on-screen size on an
        /// equally-sized plot. Canvas-proportional only, per round-7 §2 — the canvas already
        /// encodes zoom, so never multiply by zoomLevel here.
        /// </summary>
        private static float ContourMarkerRadius((double W, double H) canvasSize)
            => Math.Max(6f, (float)(Math.Min(canvasSize.W, canvasSize.H) * 0.020));

        /// <summary>
        /// Fill color for a Mode-1 contour marker disc: a Bone-colormap sample lightened toward
        /// white until its luminance clears a floor, so the always-black marker name stays legible
        /// in both themes. Sample point (t=0.5) and floor (0.70) were picked by eye against a
        /// Bone-filled contour — light enough for black text, still visibly "Bone"-toned rather
        /// than plain white. Mirrors the luminance-*ceiling* helper round-7 §3 added for iso-line
        /// color (<c>ContourRenderer.ResolveBaseLineColor</c>), inverted for a light-background need.
        /// </summary>
        internal static SKColor ResolveContourMarkerFill()
        {
            const double SamplePoint = 0.5;
            const float  LumFloor    = 0.70f;

            var   c   = ContourColormaps.Sample(ContourColorMap.Bone, SamplePoint);
            float lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
            if (lum >= LumFloor) return c;

            float     t = (LumFloor - lum) / (1f - lum);
            byte L(byte ch) => (byte)Math.Clamp((int)Math.Round(ch + (255 - ch) * t), 0, 255);
            return new SKColor(L(c.Red), L(c.Green), L(c.Blue), c.Alpha);
        }

        // ---- VSWR locus overlay -------------------------------------------
        //  Draws a red, no-fill closed polyline through the constant-VSWR locus
        //  around a marker that has VswrEnabled. plane/z0Ref are resolved by the
        //  caller (PlotRenderer) from the host plot + trace.

        /// <summary>
        /// Draws the constant-VSWR locus (red stroke, no fill) around a marker that carries a Z/Γ value.
        /// plane/z0Ref are resolved by the caller (PlotRenderer) from the host plot + trace.
        /// Drawn inside the plot clip. No-op when the marker has no usable coordinate.
        /// </summary>
        public static void DrawVswrLocus(
            SKCanvas canvas, (double W, double H) canvasSize,
            Marker marker, Trace trace, TransformSet tf,
            RfCore.Loadpull.SurfacePlane plane, Complex z0Ref)
        {
            if (!marker.VswrEnabled) return;

            var dl     = trace.GetMarkerDataLocation(marker);
            var center = new Complex(dl.X, dl.Y);

            var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
                center, marker.VswrValue, plane, z0Ref);
            if (pts is null || pts.Length < 2) return;

            float lw = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            using var paint = new SKPaint
            {
                Color       = SKColors.Red,
                StrokeWidth = lw * 1.1f,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeJoin  = SKStrokeJoin.Round,
            };

            using var path = new SKPath();
            var p0 = tf.ToCanvas(pts[0].Real, pts[0].Imaginary, trace.UseSecondaryAxis);
            path.MoveTo(p0);
            for (int i = 1; i < pts.Length; i++)
            {
                var p = tf.ToCanvas(pts[i].Real, pts[i].Imaginary, trace.UseSecondaryAxis);
                path.LineTo(p);
            }
            path.Close();
            canvas.DrawPath(path, paint);
        }

        // ---- Multi-marker vertical line ------------------------------------
        //  Drawn inside the viewport clip rect (caller's responsibility).
        //  A dashed vertical line at the marker's X position spanning the full
        //  viewport height indicates "all traces are sampled at this frequency."

        public static void DrawMultiMarkerLine(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Marker               marker,
            Trace                trace,
            TransformSet         tf,
            RenderTheme          theme)
        {
            var   dl  = trace.GetMarkerDataLocation(marker);
            float cx  = tf.PrimaryToCanvas(dl.X, 0f).X;
            float vpT = (float)(tf.Viewport.Y                         * canvasSize.H);
            float vpB = (float)((tf.Viewport.Y + tf.Viewport.Height)  * canvasSize.H);
            float lw  = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);

            using var paint = new SKPaint
            {
                Color       = RenderTheme.ToSKColor(trace.Properties.LineColor, 0.6f),
                StrokeWidth = lw * 0.8f,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            paint.PathEffect = SKPathEffect.CreateDash(new[] { lw * 4f, lw * 3f }, 0f);
            canvas.DrawLine(cx, vpT, cx, vpB, paint);
        }

        // ---- InfoBox: renders at (0,0) in its own UserControl canvas ----
        //  The control's Width/Height must be pre-set to MeasureInfoBox() dims.

        /// <summary>
        /// Returns the pixel dimensions the MarkerInfoBoxView should be sized to
        /// before calling DrawInfoBox.  Call this whenever the marker data changes.
        /// </summary>
        public static (float W, float H) MeasureInfoBox(
            Marker   marker,
            Trace    trace,
            FreqUnit freqUnit,
            bool     showFilePrefix = true,
            IReadOnlyList<Trace>? otherTraces = null)
        {
            float ts      = InfoBoxTextSize(marker);
            float padding = ts * 0.3f;

            var lines = trace.BuildMarkerBoxLines(marker, freqUnit, showFilePrefix, otherTraces);

            // Use fallback-aware measure so lines containing ∠ (U+2220) are sized correctly.
            // Each line MUST be measured with the same weight DrawInfoBox renders it in — the
            // first (name) line is bold and bold glyphs are wider, so measuring it with the
            // regular face undersizes the box whenever the name is the widest line (which depends
            // on the value string, hence the format-dependent width bug).
            using var probeReg    = new SKFont(SkiaFonts.PlexRegular,   ts);
            using var probeRegFb  = new SKFont(SkiaFonts.DejaVuRegular, ts);
            using var probeBold   = new SKFont(SkiaFonts.PlexBold,      ts);
            using var probeBoldFb = new SKFont(SkiaFonts.DejaVuBold,    ts);
            float maxW = 0f;
            foreach (var (text, bold) in lines)
            {
                var primary  = bold ? probeBold   : probeReg;
                var fallback = bold ? probeBoldFb : probeRegFb;
                maxW = Math.Max(maxW, RendererText.MeasureTextWithFallback(text, primary, fallback));
            }

            float boxW = maxW + padding * 2f;
            float boxH = (ts + padding) * lines.Count + padding;
            return (boxW, boxH);
        }

        /// <summary>
        /// Draws the info box at (0,0) filling the control's bounds.
        /// controlSize is the (W, H) from MeasureInfoBox — pass Bounds.Size here.
        /// </summary>
        public static void DrawInfoBox(
            SKCanvas             canvas,
            (double W, double H) controlSize,
            Marker               marker,
            Trace                trace,
            FreqUnit             freqUnit,
            RenderTheme          theme,
            bool                 showFilePrefix        = true,
            bool                 transparentBackground = false,
            bool                 isSelected            = false,
            SKColor              selectionColor        = default,
            IReadOnlyList<Trace>? otherTraces          = null)
        {
            var lines = trace.BuildMarkerBoxLines(marker, freqUnit, showFilePrefix, otherTraces);

            // Derive font size from the actual control height so the text scales
            // correctly when the info box is zoomed (BoxHeight = logical * zoom).
            // Formula inverted from MeasureInfoBox: H = ts*(1.3*N + 0.3).
            float ts      = (float)(controlSize.H / (1.3 * lines.Count + 0.3));
            float padding = ts * 0.3f;

            var boxRect = new SKRect(0, 0, (float)controlSize.W, (float)controlSize.H);

            // Filled background so the box is legible over traces.
            // Skipped when transparentBackground is true (user preference).
            if (!transparentBackground)
            {
                using var bgPaint = new SKPaint
                {
                    Color       = RenderTheme.WithOpacity(theme.BackgroundColor, 0.90),
                    Style       = SKPaintStyle.Fill,
                    IsAntialias = false
                };
                canvas.DrawRect(boxRect, bgPaint);
            }

            // Border
            using var borderPaint = new SKPaint
            {
                Color       = theme.BorderColor,
                StrokeWidth = 1.1f,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = false
            };
            canvas.DrawRect(boxRect, borderPaint);

            if (isSelected)
            {
                using var hlPaint = new SKPaint
                {
                    Color       = selectionColor,
                    StrokeWidth = 3.1f,
                    Style       = SKPaintStyle.Stroke,
                    IsAntialias = false
                };
                canvas.DrawRect(boxRect, hlPaint);
            }

            // Text lines — use per-glyph DejaVu fallback so ∠ (U+2220) renders correctly.
            for (int i = 0; i < lines.Count; i++)
            {
                var (text, bold) = lines[i];
                using var font   = new SKFont(bold ? SkiaFonts.PlexBold     : SkiaFonts.PlexRegular,   ts);
                using var fontFb = new SKFont(bold ? SkiaFonts.DejaVuBold   : SkiaFonts.DejaVuRegular, ts);
                using var paint  = new SKPaint { Color = theme.TextColor, IsAntialias = true };

                float y = padding + ts * (i + 0.85f) + i * padding;
                RendererText.DrawLeftTextWithFallback(canvas, text, padding, y, font, fontFb, paint);
            }
        }

        // ---- Text size helpers ------------------------------------------

        // Scales with canvas height so the symbol stays proportional to the plot.
        private static float SymbolTextSize(Marker marker, (double W, double H) canvasSize)
        {
            double normalised = 0.025 * marker.Style.FontScale();
            return (float)(normalised * canvasSize.H);
        }

        // Fixed logical size — InfoBox is always readable regardless of plot size.
        public static float InfoBoxTextSize(Marker marker)
            => 12f * (float)marker.Style.FontScale();
    }
}
