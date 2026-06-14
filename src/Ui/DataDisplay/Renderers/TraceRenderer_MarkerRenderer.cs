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
            RenderTheme          theme)
        {
            float lw    = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            var   props = trace.Properties;

            // ---- Line / curve ----
            if (props.LineEnabled)
            {
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
            float  ts           = SymbolTextSize(marker, canvasSize);

            using var font  = new SKFont(SkiaFonts.PlexBold, ts);
            using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

            // Name label centred above the triangle apex
            float tw = font.MeasureText(marker.Name);
            canvas.DrawText(
                marker.Name,
                dataPx.X - tw / 2f,
                dataPx.Y - ts - 4f,
                SKTextAlign.Left, font, paint);

            // Downward-pointing filled triangle
            using var triPath = new SKPath();
            triPath.MoveTo(dataPx.X,            dataPx.Y);
            triPath.LineTo(dataPx.X - ts / 2f,  dataPx.Y - ts);
            triPath.LineTo(dataPx.X + ts / 2f,  dataPx.Y - ts);
            triPath.Close();

            using var triPaint = new SKPaint
            {
                Color       = theme.TextColor,
                Style       = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawPath(triPath, triPaint);


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
                canvas.DrawPath(triPath, hlPaint);
            }
        }

        /// <summary>
        /// Hit radius in canvas pixels for marker symbol drag detection.
        /// Returns 1.5× the symbol triangle height.
        /// </summary>
        public static float SymbolHitRadius(Marker marker, (double W, double H) canvasSize)
            => SymbolTextSize(marker, canvasSize) * 1.5f;

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

            using var probeFont = new SKFont(SkiaFonts.PlexRegular, ts);
            float maxW = 0f;
            foreach (var (text, _) in lines)
                maxW = Math.Max(maxW, probeFont.MeasureText(text));

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

            // Text lines
            for (int i = 0; i < lines.Count; i++)
            {
                var (text, bold) = lines[i];
                using var font  = new SKFont(bold ? SkiaFonts.PlexBold : SkiaFonts.PlexRegular, ts);
                using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

                float y = padding + ts * (i + 0.85f) + i * padding;
                canvas.DrawText(text, padding, y, SKTextAlign.Left, font, paint);
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
