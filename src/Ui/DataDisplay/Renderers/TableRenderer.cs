// ================================================================
//  TableRenderer.cs  —  Renders PlotType.Table to a Skia canvas
//
//  Ported from splotRF/src/Renderers/TableRenderer.cs — namespace
//  renamed to CircuitRF.Ui.DataDisplay; font seam retargeted from
//  SkiaFonts.Regular/Bold (DejaVu) to SkiaFonts.PlexRegular/PlexBold
//  (IBM Plex).
//
//  Layout model
//  ─────────────────────────────────────────────────────────────
//  Column 0 : frequency (Plot.ColumnWidth)
//  Columns 1…N : one per Trace (Trace.ColumnWidth each)
//  Header row height = fontSize * (1 + RowPaddingFraction*2)
//  Data  row height  = fontSize * (1 + RowPaddingFraction)
//  Text is clipped to column width with middle-truncation ("…").
//  ─────────────────────────────────────────────────────────────
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using RfCore;

namespace CircuitRF.Ui.DataDisplay
{
    // ============================================================
    //  Public hit-test types
    // ============================================================

    public enum TableHitKind
    {
        None,
        FreqHeader,
        TraceHeader,
        FreqCell,
        DataCell,
        ResizeHandle,  // right edge of a column header
        MarkerGlyph,
    }

    public struct TableHitResult
    {
        public TableHitKind Kind;
        public int          RowIndex;        // -1 = header, 0+ = data row (absolute freq index)
        public int          ColIndex;        // 0 = freq col, 1+ = trace index (0-based into plot.Traces)
        public int          ResizeColIndex;  // column whose RIGHT edge is being dragged
        public Marker?      HitMarker;
        public Trace?       HitTrace;
    }

    // ============================================================
    //  TableRenderer
    // ============================================================

    public static class TableRenderer
    {
        // ---- Developer-tweakable rendering constants ----

        private const float CellBorderWidth       = 0.5f;
        private const float HeaderBorderWidth      = 1.0f;
        public  const float RowPaddingFraction     = 0.35f;  // public for PageUp/Down visible-row calc
        private const float OddRowAlphaFraction    = 0.07f;
        private const float HeaderBgAlphaFraction  = 0.12f;
        private const float MarkerFillAlpha        = 0.22f;
        private const float MarkerStrokeAlpha      = 0.65f;
        private const float ResizeHandleHitZone    = 5f;
        private const float ResizeHandleLineAlpha  = 0.30f;
        private const float MarkerTriangleSize     = 9f;
        // MinColumnWidth is a LOGICAL unit (pre-zoom) — BuildLayout scales it by zoomLevel.
        public  const float MinColumnWidth         = 40f;
        // TextCellPaddingX is a LOGICAL unit — BuildLayout scales it into layout.ScaledPaddingX.
        private const float TextCellPaddingX       = 5f;

        // Gap between the bottom of the header row and the top of the first data row.
        public  const float HeaderToDataRowPadding = 2f;

        // Horizontal alignment of cell content (SKTextAlign.Left / Center / Right).
        private const SKTextAlign CellDataHorizAlign   = SKTextAlign.Left;
        private const SKTextAlign CellHeaderHorizAlign = SKTextAlign.Left;

        // Vertical position of the text baseline within a row (0 = top, 1 = bottom).
        // ~0.80 looks visually centred for typical Latin fonts.
        private const float CellTextVertFraction   = 0.80f;
        private const float HeaderTextVertFraction = 0.75f;

        // ============================================================
        //  Internal layout struct
        // ============================================================

        private struct TableLayout
        {
            // ColX and ColW are in CANVAS pixels (logical * zoomLevel).
            // All other measurements involving positions are also in canvas pixels.
            public float    ZoomLevel;       // captured from BuildLayout for helpers that need it
            public float    ScaledPaddingX;  // TextCellPaddingX * ZoomLevel (canvas pixels)
            public float    HeaderH;
            public float    RowH;
            public float    DataStartY;      // top of first data row (HeaderH + HeaderToDataRowPadding)
            public float[]  ColX;
            public float[]  ColW;
            public int      ColCount;
            public double[] Frequencies;
            public int      ScrollIndex;
            public int      VisibleRowCount;
        }

        // ============================================================
        //  Public API
        // ============================================================

        /// <summary>
        /// Draws the full table into <paramref name="canvas"/>.
        /// Called by PlotRenderer for both screen and PDF/SVG export.
        /// </summary>
        public static void Draw(
            SKCanvas          canvas,
            (double W, double H) canvasSize,
            Plot              plot,
            RenderTheme       theme,
            float             zoomLevel       = 1f,
            bool              showFilePrefix  = false,
            HashSet<Marker>?  selectedMarkers = null,
            SKColor           selectionColor  = default)
        {
            var layout = BuildLayout(plot, canvasSize, zoomLevel);

            float fs = layout.RowH / (1 + RowPaddingFraction); // effective font size from row height

            using var regularFont = new SKFont(SkiaFonts.PlexRegular, fs);
            using var boldFont    = new SKFont(SkiaFonts.PlexBold,    fs);

            // Clip to the canvas bounds so text and highlights don't bleed outside the view
            // (especially visible when the user drags the container near a window edge).
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)canvasSize.W, (float)canvasSize.H));

            DrawRowBackgrounds(canvas, canvasSize, layout, theme);
            DrawMarkerHighlights(canvas, layout, plot, theme, selectedMarkers, selectionColor);
            DrawCellBorders(canvas, canvasSize, layout, theme);
            DrawHeaderRow(canvas, layout, plot, theme, boldFont, regularFont, showFilePrefix);
            DrawDataRows(canvas, canvasSize, layout, plot, theme, regularFont);
            DrawResizeHandles(canvas, layout, theme);

            canvas.Restore();
        }

        /// <summary>Returns which table element is under canvas point (cx, cy).</summary>
        public static TableHitResult HitTest(
            float cx, float cy,
            Plot  plot,
            (double W, double H) canvasSize,
            float zoomLevel = 1f)
        {
            var layout = BuildLayout(plot, canvasSize, zoomLevel);
            var result = new TableHitResult
                { Kind = TableHitKind.None, RowIndex = -1, ColIndex = -1, ResizeColIndex = -1 };

            // ---- Header row ----
            if (cy >= 0 && cy < layout.HeaderH)
            {
                for (int c = 0; c < layout.ColCount; c++)
                {
                    float rightEdge = layout.ColX[c] + layout.ColW[c];
                    if (Math.Abs(cx - rightEdge) <= ResizeHandleHitZone)
                    {
                        result.Kind           = TableHitKind.ResizeHandle;
                        result.ResizeColIndex = c;
                        return result;
                    }
                }
                for (int c = 0; c < layout.ColCount; c++)
                {
                    if (cx >= layout.ColX[c] && cx < layout.ColX[c] + layout.ColW[c])
                    {
                        result.RowIndex = -1;
                        result.ColIndex = c;
                        result.Kind     = c == 0 ? TableHitKind.FreqHeader : TableHitKind.TraceHeader;
                        if (c > 0) result.HitTrace = plot.Traces[c - 1];
                        return result;
                    }
                }
                return result;
            }

            // ---- Data rows ----
            float dataY = cy - layout.DataStartY;
            if (dataY < 0) return result;

            int rowInView = (int)(dataY / layout.RowH);
            int absRow    = layout.ScrollIndex + rowInView;
            if (absRow < 0 || absRow >= layout.Frequencies.Length) return result;
            if (rowInView >= layout.VisibleRowCount) return result;

            result.RowIndex = absRow;
            double freq = layout.Frequencies[absRow];

            for (int c = 0; c < layout.ColCount; c++)
            {
                if (cx >= layout.ColX[c] && cx < layout.ColX[c] + layout.ColW[c])
                {
                    result.ColIndex = c;
                    result.Kind     = c == 0 ? TableHitKind.FreqCell : TableHitKind.DataCell;
                    if (c > 0) result.HitTrace = plot.Traces[c - 1];
                    break;
                }
            }

            if (result.Kind == TableHitKind.None) return result;

            // Upgrade DataCell to MarkerGlyph when this row has a marker on this trace.
            // The ENTIRE cell is the drag/interaction area for a marker — no need to hit
            // a small triangle.
            if (result.ColIndex > 0 && result.HitTrace is { } trace)
            {
                float rowTop = layout.DataStartY + rowInView * layout.RowH;

                foreach (var m in trace.Markers)
                {
                    // The marker is at this row when its frequency matches the row's global-union
                    // frequency. Using a direct freq comparison (not trace-local index) correctly
                    // handles the case where the trace has no data at this frequency (NaN cell).
                    if (m.Freq != freq) continue;

                    // Entire cell is the hit area.
                    result.Kind      = TableHitKind.MarkerGlyph;
                    result.HitMarker = m;
                    return result;
                }
            }

            return result;
        }

        /// <summary>
        /// Measures all rows (not just visible) and returns a stable auto-fit column width
        /// in LOGICAL units (pre-zoom), suitable for storing in plot.ColumnWidth / trace.ColumnWidth.
        /// </summary>
        public static float CalcFitWidth(
            Plot plot, int colIndex,
            (double W, double H) canvasSize,
            float zoomLevel = 1f)
        {
            // Measure at unscaled font size so the result is zoom-independent logical units.
            float fs = (float)plot.FontSize;
            using var regularFont = new SKFont(SkiaFonts.PlexRegular, fs);
            using var boldFont    = new SKFont(SkiaFonts.PlexBold,    fs);

            double[] freqs = GetSortedFrequencies(plot);
            float maxW = 0;

            // Measure header — freq column includes the sort-direction arrow that is drawn at render time.
            string sortArrow  = plot.TableViewAscendingSortOrder ? " ▲" : " ▼";
            string headerText = colIndex == 0
                ? $"Freq ({plot.FreqUnits.Description()}){sortArrow}"
                : (colIndex - 1 < plot.Traces.Count ? plot.Traces[colIndex - 1].ShortDescription : "");
            maxW = Math.Max(maxW, boldFont.MeasureText(headerText));

            // Measure data rows
            foreach (double freq in freqs)
            {
                string cellText;
                if (colIndex == 0)
                {
                    double fScaled = freq * plot.FreqUnits.Scale();
                    string fmt = $"{plot.FormatString}{plot.MaximumFractionDigits}";
                    cellText = fScaled.ToString(fmt);
                }
                else
                {
                    int traceIdx = colIndex - 1;
                    if (traceIdx >= plot.Traces.Count) continue;
                    cellText = FormatTraceCell(plot.Traces[traceIdx], freq);
                }
                maxW = Math.Max(maxW, regularFont.MeasureText(cellText));
            }

            // If the trace column has any markers, add space for the right-edge marker glyph.
            float markerExtra = 0f;
            if (colIndex > 0 && colIndex - 1 < plot.Traces.Count)
            {
                if (plot.Traces[colIndex - 1].Markers.Count > 0)
                    markerExtra = MarkerTriangleSize + TextCellPaddingX;
            }

            return maxW + TextCellPaddingX * 2 + markerExtra + 2;
        }

        /// <summary>Total logical width of all columns (freq + all traces).</summary>
        public static float TotalColumnWidth(Plot plot)
        {
            float total = (float)Math.Max(MinColumnWidth, plot.ColumnWidth);
            foreach (var t in plot.Traces)
                total += (float)Math.Max(MinColumnWidth, t.ColumnWidth);
            return total;
        }

        // ============================================================
        //  Layout builder
        // ============================================================

        private static TableLayout BuildLayout(Plot plot, (double W, double H) canvasSize, float zoomLevel)
        {
            float fs = (float)(plot.FontSize * zoomLevel);

            var layout = new TableLayout
            {
                ZoomLevel     = zoomLevel,
                ScaledPaddingX = TextCellPaddingX * zoomLevel,
                HeaderH       = fs * (1 + RowPaddingFraction * 2),
                RowH          = fs * (1 + RowPaddingFraction),
                Frequencies   = GetSortedFrequencies(plot),
                ScrollIndex   = Math.Max(0, plot.TableViewScrollIndex),
            };

            layout.DataStartY = layout.HeaderH + HeaderToDataRowPadding;

            // Column widths are stored in the model as LOGICAL units (pre-zoom).
            // Multiply by zoomLevel to get canvas pixel widths.
            int colCount = 1 + plot.Traces.Count;
            layout.ColCount = colCount;
            layout.ColX     = new float[colCount];
            layout.ColW     = new float[colCount];

            layout.ColW[0] = (float)Math.Max(MinColumnWidth, plot.ColumnWidth) * zoomLevel;
            for (int c = 1; c < colCount; c++)
                layout.ColW[c] = (float)Math.Max(MinColumnWidth, plot.Traces[c - 1].ColumnWidth) * zoomLevel;

            layout.ColX[0] = 0;
            for (int c = 1; c < colCount; c++)
                layout.ColX[c] = layout.ColX[c - 1] + layout.ColW[c - 1];

            // Visible row count (ceiling so the partial last row is drawn, but clamped on draw)
            float availH = (float)canvasSize.H - layout.DataStartY;
            layout.VisibleRowCount = availH > 0 ? (int)Math.Ceiling(availH / layout.RowH) : 0;

            // Clamp scroll index and write back so OnPointerWheel can always apply a correct delta
            int maxScroll = Math.Max(0, layout.Frequencies.Length - layout.VisibleRowCount);
            layout.ScrollIndex          = Math.Clamp(layout.ScrollIndex, 0, maxScroll);
            plot.TableViewScrollIndex   = layout.ScrollIndex;  // keep model in bounds

            return layout;
        }

        // ============================================================
        //  Frequency union
        // ============================================================

        public static double[] GetSortedFrequencies(Plot plot)
        {
            var set = new SortedSet<double>();
            foreach (var t in plot.Traces)
                foreach (double f in t.Data.Frequencies)
                    set.Add(f);

            double[] arr = set.ToArray();
            if (!plot.TableViewAscendingSortOrder)
                Array.Reverse(arr);
            return arr;
        }

        /// <summary>
        /// Returns only the rows currently visible on screen (respects scroll index,
        /// canvas size, and sort order).  Used for "Copy Table Data" clipboard copy.
        /// </summary>
        public static double[] GetVisibleFrequencies(
            Plot plot, (double W, double H) canvasSize, float zoomLevel = 1f)
        {
            var layout = BuildLayout(plot, canvasSize, zoomLevel);
            int start  = layout.ScrollIndex;
            int count  = Math.Min(layout.VisibleRowCount, layout.Frequencies.Length - start);
            if (count <= 0) return Array.Empty<double>();
            var result = new double[count];
            Array.Copy(layout.Frequencies, start, result, 0, count);
            return result;
        }

        // ============================================================
        //  Cell formatting — no unit suffix (user reads from header)
        // ============================================================

        internal static string FormatTraceCell(Trace trace, double freq)
        {
            int fi = Array.FindIndex(trace.Data.Frequencies, f => f == freq);
            if (fi < 0) return "NaN";

            // Stability circle: show Inside / Outside
            if (trace.IsStabilityCircle)
            {
                if (fi >= trace.StabilityCircleStableInside.Count) return "NaN";
                return trace.StabilityCircleStableInside[fi] ? "Inside" : "Outside";
            }

            // Derived scalar (Mu, MuPrime, MaxGain) — no units in cell
            if (trace.IsDerived)
            {
                double v = trace.DataPointScalar(freq);
                if (!double.IsFinite(v)) return "NaN";
                string fmt = $"{trace.FormatString}{trace.MaximumFractionDigits}";
                return v.ToString(fmt);
            }

            // Complex matrix element
            if (trace.YAxis == DependentVarFormat.Complex)
            {
                var raw = trace.DataPoint(freq);
                if (!double.IsFinite(raw.Real) || !double.IsFinite(raw.Imaginary)) return "NaN";
                string fmt = $"{trace.FormatString}{trace.MaximumFractionDigits}";
                return trace.MatrixFormat switch
                {
                    MatrixFormat.RI => FormatRI(raw, fmt),
                    MatrixFormat.MA => FormatMA(raw, fmt),
                    MatrixFormat.DB => FormatDB(raw, fmt),
                    _               => FormatMA(raw, fmt),
                };
            }

            // Scalar (dB, Mag, Phase, Real, Imag) — no unit suffix in cell
            {
                double scalar = trace.DataPointScalar(freq);
                if (!double.IsFinite(scalar)) return "NaN";
                string fmt = $"{trace.FormatString}{trace.MaximumFractionDigits}";
                return scalar.ToString(fmt);
            }
        }

        private static string FormatRI(System.Numerics.Complex c, string fmt)
        {
            string sign = c.Imaginary >= 0 ? "+" : "-";
            return $"{c.Real.ToString(fmt)}{sign}j{Math.Abs(c.Imaginary).ToString(fmt)}";
        }

        private static string FormatMA(System.Numerics.Complex c, string fmt)
        {
            double angle = c.Phase * 180.0 / Math.PI;
            return $"{c.Magnitude.ToString(fmt)}∠{angle:F1}°";
        }

        private static string FormatDB(System.Numerics.Complex c, string fmt)
        {
            double db    = 20.0 * Math.Log10(Math.Max(c.Magnitude, 1e-300));
            double angle = c.Phase * 180.0 / Math.PI;
            return $"{db.ToString(fmt)}∠{angle:F1}°";
        }

        // ============================================================
        //  Text helpers
        // ============================================================

        private static string TruncateMiddle(SKFont font, string text, float maxWidth)
        {
            if (font.MeasureText(text) <= maxWidth) return text;

            const string ellipsis = "…";
            float ellipsisW = font.MeasureText(ellipsis);
            if (ellipsisW >= maxWidth) return ellipsis;

            float budget     = (maxWidth - ellipsisW) / 2f;
            int   leftLen    = 0;
            int   rightStart = text.Length;

            for (int l = 1; l <= text.Length; l++)
            {
                if (font.MeasureText(text.Substring(0, l)) > budget) { leftLen = l - 1; break; }
                leftLen = l;
            }
            for (int r = text.Length - 1; r >= 0; r--)
            {
                if (font.MeasureText(text.Substring(r)) > budget) { rightStart = r + 1; break; }
                rightStart = r;
            }

            string left  = leftLen    > 0           ? text.Substring(0, leftLen) : "";
            string right = rightStart < text.Length ? text.Substring(rightStart) : "";
            return left + ellipsis + right;
        }

        /// <summary>
        /// Draws text in a column cell, respecting horizontal alignment and column padding.
        /// Text is middle-truncated when wider than the available space.
        /// <paramref name="paddingX"/> should be <c>layout.ScaledPaddingX</c> (canvas pixels).
        /// </summary>
        private static void DrawClippedText(
            SKCanvas  canvas, SKFont font, SKPaint paint,
            string    text,
            float     colX, float y, float colW,
            float     paddingX,
            SKTextAlign align)
        {
            float available = colW - paddingX * 2;
            if (available <= 0) return;

            string clipped = TruncateMiddle(font, text, available);

            float x = align switch
            {
                SKTextAlign.Center => colX + colW / 2f,
                SKTextAlign.Right  => colX + colW - paddingX,
                _                  => colX + paddingX,  // Left
            };
            canvas.DrawText(clipped, x, y, align, font, paint);
        }

        // ============================================================
        //  Draw helpers
        // ============================================================

        private static void DrawRowBackgrounds(
            SKCanvas canvas, (double W, double H) canvasSize,
            TableLayout layout, RenderTheme theme)
        {
            using var evenPaint = new SKPaint { Color = theme.BackgroundColor, IsAntialias = false };
            using var oddPaint  = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.TextColor, OddRowAlphaFraction),
                IsAntialias = false,
            };

            // Fill entire canvas with even-row background
            canvas.DrawRect(0, 0, (float)canvasSize.W, (float)canvasSize.H, evenPaint);

            float canvasH = (float)canvasSize.H;

            // Paint alternating odd rows, clamped to canvas height
            for (int r = 0; r < layout.VisibleRowCount; r++)
            {
                int absRow = layout.ScrollIndex + r;
                if (absRow >= layout.Frequencies.Length) break;
                if (absRow % 2 == 0) continue;

                float rowTop = layout.DataStartY + r * layout.RowH;
                float rowH   = Math.Min(layout.RowH, canvasH - rowTop);
                if (rowH <= 0) break;
                canvas.DrawRect(0, rowTop, (float)canvasSize.W, rowH, oddPaint);
            }
        }

        private static void DrawMarkerHighlights(
            SKCanvas canvas, TableLayout layout, Plot plot,
            RenderTheme theme, HashSet<Marker>? selectedMarkers, SKColor selectionColor)
        {
            float canvasH = (float)(layout.DataStartY + layout.VisibleRowCount * layout.RowH);

            for (int ti = 0; ti < plot.Traces.Count; ti++)
            {
                var   trace    = plot.Traces[ti];
                int   colIndex = ti + 1;
                float cellX    = layout.ColX[colIndex];
                float cellW    = layout.ColW[colIndex];

                foreach (var m in trace.Markers)
                {
                    int absRow = Array.FindIndex(layout.Frequencies, f => f == m.Freq);
                    if (absRow < 0) continue;
                    int rowInView = absRow - layout.ScrollIndex;
                    if (rowInView < 0 || rowInView >= layout.VisibleRowCount) continue;

                    float rowTop = layout.DataStartY + rowInView * layout.RowH;
                    float rowH   = Math.Min(layout.RowH, canvasH - rowTop);
                    if (rowH <= 0) continue;

                    SKColor color = RenderTheme.ToSKColor(trace.Properties.LineColor);

                    using var fillPaint = new SKPaint
                    {
                        Color       = RenderTheme.WithOpacity(color, MarkerFillAlpha),
                        IsAntialias = false,
                    };
                    using var strokePaint = new SKPaint
                    {
                        Color       = RenderTheme.WithOpacity(color, MarkerStrokeAlpha),
                        Style       = SKPaintStyle.Stroke,
                        StrokeWidth = CellBorderWidth,
                        IsAntialias = false,
                    };

                    canvas.DrawRect(cellX, rowTop, cellW, rowH, fillPaint);
                    canvas.DrawRect(cellX, rowTop, cellW, rowH, strokePaint);
                    DrawMarkerGlyph(canvas, cellX, cellW, rowTop, rowH, color,
                        MarkerTriangleSize * layout.ZoomLevel);

                    // Selection highlight — drawn on top so it's always visible.
                    if (selectedMarkers?.Contains(m) == true && selectionColor != default)
                    {
                        using var selPaint = new SKPaint
                        {
                            Color       = selectionColor,
                            Style       = SKPaintStyle.Stroke,
                            StrokeWidth = Math.Max(1.5f, 2f * layout.ZoomLevel),
                            IsAntialias = false,
                        };
                        canvas.DrawRect(cellX, rowTop, cellW, rowH, selPaint);
                    }
                }
            }
        }

        /// <summary>
        /// Draws a left-pointing triangle on the RIGHT-MIDDLE edge of the cell,
        /// indicating the marker's position and pointing toward the cell data.
        /// </summary>
        private static void DrawMarkerGlyph(
            SKCanvas canvas,
            float cellX, float cellW, float rowTop, float rowH,
            SKColor color,
            float triSize)   // caller passes MarkerTriangleSize * layout.ZoomLevel
        {
            float s    = triSize;
            float midY = rowTop + rowH * 0.5f;
            // Right edge of the triangle, inset by 1 scaled pixel from the cell border
            float rx   = cellX + cellW - 1f;

            using var path = new SKPath();
            path.MoveTo(rx,     midY - s * 0.5f);  // top-right vertex
            path.LineTo(rx,     midY + s * 0.5f);  // bottom-right vertex
            path.LineTo(rx - s, midY);              // left tip — points INTO the cell (←)
            path.Close();

            using var fillPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(color, MarkerStrokeAlpha),
                IsAntialias = true,
            };
            canvas.DrawPath(path, fillPaint);
        }

        private static void DrawCellBorders(
            SKCanvas canvas, (double W, double H) canvasSize,
            TableLayout layout, RenderTheme theme)
        {
            using var borderPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.GridColor, 0.5),
                StrokeWidth = CellBorderWidth,
                IsAntialias = false,
            };

            float totalH = (float)canvasSize.H;

            // Vertical column borders
            for (int c = 0; c < layout.ColCount; c++)
            {
                float x = layout.ColX[c] + layout.ColW[c];
                canvas.DrawLine(x, 0, x, totalH, borderPaint);
            }

            // Horizontal row borders (data rows only)
            for (int r = 0; r <= layout.VisibleRowCount; r++)
            {
                int absRow = layout.ScrollIndex + r;
                if (absRow > layout.Frequencies.Length) break;
                float y = layout.DataStartY + r * layout.RowH;
                if (y > totalH) break;
                canvas.DrawLine(0, y, (float)canvasSize.W, y, borderPaint);
            }
        }

        private static void DrawHeaderRow(
            SKCanvas canvas, TableLayout layout, Plot plot,
            RenderTheme theme, SKFont boldFont, SKFont regularFont,
            bool showFilePrefix)
        {
            float totalColW = layout.ColX[layout.ColCount - 1] + layout.ColW[layout.ColCount - 1];

            using var bgPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.TextColor, HeaderBgAlphaFraction),
                IsAntialias = false,
            };
            canvas.DrawRect(0, 0, totalColW, layout.HeaderH, bgPaint);

            using var textPaint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
            using var borderPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.BorderColor, 0.6),
                StrokeWidth = HeaderBorderWidth,
                IsAntialias = false,
            };

            canvas.DrawLine(0, layout.HeaderH, totalColW, layout.HeaderH, borderPaint);

            float textY = layout.HeaderH * HeaderTextVertFraction;

            // Freq column header — sort-direction arrow shows current order.
            string sortArrow    = plot.TableViewAscendingSortOrder ? " ▲" : " ▼";
            DrawClippedText(canvas, boldFont, textPaint,
                $"Freq ({plot.FreqUnits.Description()}){sortArrow}",
                layout.ColX[0], textY, layout.ColW[0],
                layout.ScaledPaddingX, CellHeaderHorizAlign);

            // Trace column headers
            for (int ti = 0; ti < plot.Traces.Count; ti++)
            {
                int   c     = ti + 1;
                var   trace = plot.Traces[ti];
                string label = showFilePrefix ? trace.Description : trace.ShortDescription;
                DrawClippedText(canvas, boldFont, textPaint,
                    label, layout.ColX[c], textY, layout.ColW[c],
                    layout.ScaledPaddingX, CellHeaderHorizAlign);
            }
        }

        private static void DrawDataRows(
            SKCanvas canvas, (double W, double H) canvasSize,
            TableLayout layout, Plot plot,
            RenderTheme theme, SKFont regularFont)
        {
            using var textPaint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

            double freqScale = plot.FreqUnits.Scale();
            string freqFmt   = $"{plot.FormatString}{plot.MaximumFractionDigits}";
            float  canvasH   = (float)canvasSize.H;

            for (int r = 0; r < layout.VisibleRowCount; r++)
            {
                int absRow = layout.ScrollIndex + r;
                if (absRow >= layout.Frequencies.Length) break;

                double freq   = layout.Frequencies[absRow];
                float  rowTop = layout.DataStartY + r * layout.RowH;
                if (rowTop >= canvasH) break;

                // Baseline is always computed from the full row height so vertical
                // spacing is uniform across all rows, including the partially-clipped
                // last row.  The canvas ClipRect (set in Draw) trims any overflow.
                float textY = rowTop + layout.RowH * CellTextVertFraction;

                // Frequency cell
                DrawClippedText(canvas, regularFont, textPaint,
                    (freq * freqScale).ToString(freqFmt),
                    layout.ColX[0], textY, layout.ColW[0],
                    layout.ScaledPaddingX, CellDataHorizAlign);

                // Trace data cells
                for (int ti = 0; ti < plot.Traces.Count; ti++)
                {
                    int c = ti + 1;
                    DrawClippedText(canvas, regularFont, textPaint,
                        FormatTraceCell(plot.Traces[ti], freq),
                        layout.ColX[c], textY, layout.ColW[c],
                        layout.ScaledPaddingX, CellDataHorizAlign);
                }
            }
        }

        private static void DrawResizeHandles(SKCanvas canvas, TableLayout layout, RenderTheme theme)
        {
            using var handlePaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.TextColor, ResizeHandleLineAlpha),
                StrokeWidth = 1.5f,
                IsAntialias = false,
            };

            float top = layout.HeaderH * 0.15f;
            float bot = layout.HeaderH * 0.85f;

            for (int c = 0; c < layout.ColCount; c++)
            {
                float x = layout.ColX[c] + layout.ColW[c];
                canvas.DrawLine(x, top, x, bot, handlePaint);
            }
        }
    }
}
