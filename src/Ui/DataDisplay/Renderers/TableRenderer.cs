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
//  Column plan: one XAxis column per distinct-adjacent X group,
//  one TraceValue column per trace.  Two adjacent traces with the
//  same axis name, unit, and values share a single XAxis column.
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
    //  Column-plan types
    // ============================================================

    public enum TableColKind { XAxis, TraceValue }

    public sealed class TableColumn
    {
        public TableColKind Kind;
        public int          FirstTraceIndex;    // XAxis: first trace of this group; TraceValue: the trace
        public string       Header = "";
        public string?      Unit;               // XAxis only
        public double[]     XValues = Array.Empty<double>();  // XAxis: sorted group X; TraceValue: same ref
        public bool         IsFreqUnit;         // XAxis only
        public bool         IsScalar;           // XAxis only: true for scalar (rank-0) anchor column
        public bool         IsNodeAxis;         // XAxis only: true when axis name is "node" → render as integer
        public int          FamilyCurveIndex = -1; // TraceValue only: ≥0 = curve k of a family trace
    }

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
        public int          RowIndex;        // -1 = header, 0+ = data row (absolute)
        public int          ColIndex;        // index into BuildColumns() list
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
            public float              ZoomLevel;       // captured from BuildLayout for helpers that need it
            public float              ScaledPaddingX;  // TextCellPaddingX * ZoomLevel (canvas pixels)
            public float              HeaderH;
            public float              RowH;
            public float              DataStartY;      // top of first data row (HeaderH + HeaderToDataRowPadding)
            public float[]            ColX;
            public float[]            ColW;
            public int                ColCount;
            public int                RowCount;        // max XValues.Length across all XAxis columns
            public int                ScrollIndex;
            public int                VisibleRowCount;
            public List<TableColumn>  Columns;         // the full column plan
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

            using var regularFont      = new SKFont(SkiaFonts.PlexRegular,    fs);
            using var boldFont         = new SKFont(SkiaFonts.PlexBold,       fs);
            using var dejaVuRegular    = new SKFont(SkiaFonts.DejaVuRegular,  fs);
            using var dejaVuBold       = new SKFont(SkiaFonts.DejaVuBold,     fs);

            // Clip to the canvas bounds so text and highlights don't bleed outside the view
            // (especially visible when the user drags the container near a window edge).
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)canvasSize.W, (float)canvasSize.H));

            DrawRowBackgrounds(canvas, canvasSize, layout, theme);
            DrawMarkerHighlights(canvas, layout, plot, theme, selectedMarkers, selectionColor);
            DrawCellBorders(canvas, canvasSize, layout, theme);
            DrawHeaderRow(canvas, layout, plot, theme, boldFont, dejaVuBold, regularFont, showFilePrefix);
            DrawDataRows(canvas, canvasSize, layout, plot, theme, regularFont, dejaVuRegular);
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
            var layout  = BuildLayout(plot, canvasSize, zoomLevel);
            var columns = layout.Columns;
            var result  = new TableHitResult
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
                        if (columns[c].Kind == TableColKind.XAxis)
                        {
                            result.Kind = TableHitKind.FreqHeader;
                        }
                        else
                        {
                            result.Kind     = TableHitKind.TraceHeader;
                            result.HitTrace = plot.Traces[columns[c].FirstTraceIndex];
                        }
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
            if (absRow < 0 || absRow >= layout.RowCount) return result;
            if (rowInView >= layout.VisibleRowCount) return result;

            result.RowIndex = absRow;

            for (int c = 0; c < layout.ColCount; c++)
            {
                if (cx >= layout.ColX[c] && cx < layout.ColX[c] + layout.ColW[c])
                {
                    result.ColIndex = c;
                    if (columns[c].Kind == TableColKind.XAxis)
                    {
                        result.Kind = TableHitKind.FreqCell;
                    }
                    else
                    {
                        result.Kind     = TableHitKind.DataCell;
                        result.HitTrace = plot.Traces[columns[c].FirstTraceIndex];
                    }
                    break;
                }
            }

            if (result.Kind == TableHitKind.None) return result;

            // Upgrade DataCell to MarkerGlyph when this row has a marker on this trace.
            // The ENTIRE cell is the drag/interaction area for a marker.
            if (result.Kind == TableHitKind.DataCell && result.HitTrace is { } trace)
            {
                double freq = absRow < columns[result.ColIndex].XValues.Length
                    ? columns[result.ColIndex].XValues[absRow]
                    : double.NaN;

                if (!double.IsNaN(freq))
                {
                    foreach (var m in trace.Markers)
                    {
                        if (m.Freq != freq) continue;
                        result.Kind      = TableHitKind.MarkerGlyph;
                        result.HitMarker = m;
                        return result;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Returns true when the table has more rows than fit in the visible area.
        /// Used to gate wheel-scroll vs. wheel-zoom.
        /// </summary>
        public static bool CanScroll(Plot plot, (double W, double H) canvasSize, float zoomLevel = 1f)
        {
            var layout = BuildLayout(plot, canvasSize, zoomLevel);
            return layout.RowCount > layout.VisibleRowCount;
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
            using var regularFont   = new SKFont(SkiaFonts.PlexRegular,   fs);
            using var boldFont      = new SKFont(SkiaFonts.PlexBold,      fs);
            using var dejaVuRegular = new SKFont(SkiaFonts.DejaVuRegular, fs);

            var columns = BuildColumns(plot);
            if (colIndex < 0 || colIndex >= columns.Count)
                return MinColumnWidth;

            var col  = columns[colIndex];
            float maxW = boldFont.MeasureText(col.Header);

            // Sort arrow space on XAxis column headers.
            if (col.Kind == TableColKind.XAxis)
            {
                float spaceW = boldFont.MeasureText(" ");
                maxW += spaceW * 1.5f + fs * 0.6f + TextCellPaddingX;
            }

            // Measure every data row in this column.
            for (int ri = 0; ri < col.XValues.Length; ri++)
            {
                string cellText = FormatColumnCell(col, ri, plot);
                float  cellW    = col.Kind == TableColKind.XAxis
                    ? regularFont.MeasureText(cellText)
                    : RendererText.MeasureTextWithFallback(cellText, regularFont, dejaVuRegular);
                maxW = Math.Max(maxW, cellW);
            }

            // If the trace column has any markers, add space for the right-edge marker glyph.
            float markerExtra = 0f;
            if (col.Kind == TableColKind.TraceValue && col.FirstTraceIndex < plot.Traces.Count)
            {
                if (plot.Traces[col.FirstTraceIndex].Markers.Count > 0)
                    markerExtra = MarkerTriangleSize + TextCellPaddingX;
            }

            return maxW + TextCellPaddingX * 2 + markerExtra + 2;
        }

        /// <summary>Total logical width of all columns (all XAxis + all trace columns).</summary>
        public static float TotalColumnWidth(Plot plot)
        {
            var cols  = BuildColumns(plot);
            float total = 0f;
            foreach (var col in cols)
            {
                if (col.Kind == TableColKind.XAxis)
                {
                    var anchor = plot.Traces[col.FirstTraceIndex];
                    double w = anchor.XColumnWidth > 0 ? anchor.XColumnWidth : plot.ColumnWidth;
                    total += (float)Math.Max(MinColumnWidth, w);
                }
                else
                {
                    total += (float)Math.Max(MinColumnWidth, plot.Traces[col.FirstTraceIndex].ColumnWidth);
                }
            }
            return total;
        }

        // ============================================================
        //  Column-plan builder (public so PlotControl can query it)
        // ============================================================

        /// <summary>
        /// Builds the ordered column plan for the table.  Adjacent traces that share
        /// exactly the same X axis (same name, unit, sorted values, and point count)
        /// collapse to a single shared XAxis column.
        /// </summary>
        public static List<TableColumn> BuildColumns(Plot plot)
        {
            var result = new List<TableColumn>(plot.Traces.Count * 2);

            string?  prevAxisName   = null;
            string?  prevUnit       = null;
            double[]? prevRaw       = null;
            double[]? currentXArray = null;

            for (int ti = 0; ti < plot.Traces.Count; ti++)
            {
                var trace = plot.Traces[ti];

                // Determine X identity for this trace.
                string   axisName;
                string?  unit;
                double[] raw;
                bool     isFamilyPath = false;

                if (trace.IsCubeBound && trace.CubeIsScalar)
                {
                    axisName = ""; unit = null; raw = new[] { 0.0 };   // single-row anchor, blank header
                }
                else if (trace.IsCubeBound && trace.IsFamily && trace.FamilyCurves.Count > 0
                         && trace.CubeXValues is { } fxs)
                {
                    axisName = trace.CubeXAxisName ?? "X";
                    unit     = trace.CubeXUnit;
                    raw      = new double[fxs.Count];
                    for (int i = 0; i < fxs.Count; i++) raw[i] = fxs[i];
                    isFamilyPath = true;
                }
                else if (trace.IsCubeBound && trace.CubeXValues is { } xs)
                {
                    axisName = trace.CubeXAxisName ?? "X";
                    unit     = trace.CubeXUnit;
                    raw      = new double[xs.Count];
                    for (int i = 0; i < xs.Count; i++) raw[i] = xs[i];
                }
                else if (trace.IsCubeBound)
                {
                    // Cube-bound but no X values (InvalidSpecText, rank≥3, etc.) — blank column.
                    axisName = ""; unit = null; raw = Array.Empty<double>();
                }
                else if (trace.Data is { } d)
                {
                    axisName = "Freq";
                    unit     = "Hz";
                    raw      = d.Frequencies;
                }
                else
                {
                    axisName = "X";
                    unit     = null;
                    raw      = Array.Empty<double>();
                }

                // Adjacent-dedup: same axis name, same unit, same length, same sorted values.
                bool dedup = currentXArray != null
                    && prevAxisName == axisName
                    && prevUnit     == unit
                    && prevRaw is { } pr
                    && pr.Length    == raw.Length
                    && (raw.Length  == 0 || RawValuesEqual(pr, raw));

                if (!dedup)
                {
                    double[] sorted  = SortUnique(raw, plot.TableViewAscendingSortOrder);
                    bool     isFreq  = IsFreqUnit(unit);
                    bool     isNode  = string.Equals(axisName, "node", StringComparison.OrdinalIgnoreCase);
                    string   xHeader = string.IsNullOrEmpty(unit)
                        ? axisName
                        : isFreq
                            ? $"{axisName} ({plot.FreqUnits.Description()})"
                            : $"{axisName} ({unit})";

                    result.Add(new TableColumn
                    {
                        Kind            = TableColKind.XAxis,
                        FirstTraceIndex = ti,
                        Header          = xHeader,
                        Unit            = unit,
                        XValues         = sorted,
                        IsFreqUnit      = isFreq,
                        IsNodeAxis      = isNode,
                        IsScalar        = trace.IsCubeBound && trace.CubeIsScalar,
                    });
                    currentXArray = sorted;
                }

                prevAxisName = axisName;
                prevUnit     = unit;
                prevRaw      = raw;

                if (isFamilyPath)
                {
                    // Emit one TraceValue column per family curve.
                    string  baseShorthand   = trace.CubeShorthand ?? trace.ShortDescription;
                    string? familyAxisName  = trace.FamilyAxisName;
                    int     cap             = Math.Min(trace.FamilyCurves.Count, Trace.MaxFamilyCurves);
                    for (int k = 0; k < cap; k++)
                    {
                        var    fc          = trace.FamilyCurves[k];
                        string familyLabel = fc.AxisLabel
                            ?? (familyAxisName is not null
                                ? $"{familyAxisName}={fc.AxisValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                                : fc.AxisValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        result.Add(new TableColumn
                        {
                            Kind             = TableColKind.TraceValue,
                            FirstTraceIndex  = ti,
                            Header           = $"{baseShorthand} @ {familyLabel}",
                            XValues          = currentXArray ?? Array.Empty<double>(),
                            FamilyCurveIndex = k,
                        });
                    }
                }
                else
                {
                    // Single TraceValue column.
                    string valHeader = trace.IsCubeBound
                        ? (trace.CubeShorthand ?? trace.ShortDescription)
                        : trace.ShortDescription;
                    result.Add(new TableColumn
                    {
                        Kind            = TableColKind.TraceValue,
                        FirstTraceIndex = ti,
                        Header          = valHeader,
                        XValues         = currentXArray ?? Array.Empty<double>(),
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Formats a single table cell given the column plan entry and the absolute row index.
        /// Returns "" when rowIndex is beyond the column's group length.
        /// </summary>
        public static string FormatColumnCell(TableColumn col, int rowIndex, Plot plot)
        {
            if (rowIndex >= col.XValues.Length) return "";
            double xVal = col.XValues[rowIndex];

            if (col.Kind == TableColKind.XAxis)
            {
                if (col.IsScalar) return "";        // scalar anchor column: no X value
                if (col.IsNodeAxis)
                    return ((long)Math.Round(xVal)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string fmt = $"{plot.FormatString}{plot.MaximumFractionDigits}";
                return col.IsFreqUnit
                    ? (xVal * plot.FreqUnits.Scale()).ToString(fmt)
                    : xVal.ToString(fmt);
            }

            // TraceValue: look up the sample in this trace whose X value matches.
            var trace = plot.Traces[col.FirstTraceIndex];
            if (col.FamilyCurveIndex >= 0)
                return FormatFamilyCellAt(trace, col.FamilyCurveIndex, xVal);
            return trace.IsCubeBound
                ? FormatCubeCellAt(trace, xVal)
                : FormatTraceCell(trace, xVal);
        }

        /// <summary>
        /// Builds the grid of tab-separated text for "Copy Table Data".
        /// Returns the column headers and ALL rows (ignores scroll position).
        /// Cells are blank ("") when a group's trace has fewer rows than the longest group.
        /// </summary>
        public static (string[] headers, string[][] rows) BuildCopyGrid(
            Plot plot, (double W, double H) canvasSize, float zoomLevel = 1f)
        {
            var layout  = BuildLayout(plot, canvasSize, zoomLevel);
            var cols    = layout.Columns;

            var headers = new string[cols.Count];
            for (int c = 0; c < cols.Count; c++)
                headers[c] = cols[c].Header;

            int count = layout.RowCount;
            if (count <= 0) return (headers, Array.Empty<string[]>());

            var rows = new string[count][];
            for (int r = 0; r < count; r++)
            {
                var row = new string[cols.Count];
                for (int c = 0; c < cols.Count; c++)
                    row[c] = FormatColumnCell(cols[c], r, plot);
                rows[r] = row;
            }

            return (headers, rows);
        }

        // ============================================================
        //  Layout builder
        // ============================================================

        private static TableLayout BuildLayout(Plot plot, (double W, double H) canvasSize, float zoomLevel)
        {
            float fs      = (float)(plot.FontSize * zoomLevel);
            var   columns = BuildColumns(plot);

            // Row count = longest XAxis group.
            int rowCount = 0;
            foreach (var col in columns)
                if (col.Kind == TableColKind.XAxis && col.XValues.Length > rowCount)
                    rowCount = col.XValues.Length;

            var layout = new TableLayout
            {
                ZoomLevel      = zoomLevel,
                ScaledPaddingX = TextCellPaddingX * zoomLevel,
                HeaderH        = fs * (1 + RowPaddingFraction * 2),
                RowH           = fs * (1 + RowPaddingFraction),
                RowCount       = rowCount,
                ScrollIndex    = Math.Max(0, plot.TableViewScrollIndex),
                Columns        = columns,
            };

            layout.DataStartY = layout.HeaderH + HeaderToDataRowPadding;

            // Column widths: XAxis columns use plot.ColumnWidth; TraceValue columns use trace.ColumnWidth.
            int colCount = columns.Count;
            layout.ColCount = colCount;
            layout.ColX     = new float[colCount];
            layout.ColW     = new float[colCount];

            for (int c = 0; c < colCount; c++)
            {
                if (columns[c].Kind == TableColKind.XAxis)
                {
                    var anchor = plot.Traces[columns[c].FirstTraceIndex];
                    double w = anchor.XColumnWidth > 0 ? anchor.XColumnWidth : plot.ColumnWidth;
                    layout.ColW[c] = (float)Math.Max(MinColumnWidth, w) * zoomLevel;
                }
                else
                {
                    layout.ColW[c] = (float)Math.Max(MinColumnWidth, plot.Traces[columns[c].FirstTraceIndex].ColumnWidth) * zoomLevel;
                }
            }

            if (colCount > 0) layout.ColX[0] = 0;
            for (int c = 1; c < colCount; c++)
                layout.ColX[c] = layout.ColX[c - 1] + layout.ColW[c - 1];

            // Visible row count (ceiling so the partial last row is drawn, but clamped on draw)
            float availH = (float)canvasSize.H - layout.DataStartY;
            layout.VisibleRowCount = availH > 0 ? (int)Math.Ceiling(availH / layout.RowH) : 0;

            // Clamp scroll index and write back so OnPointerWheel can always apply a correct delta.
            int maxScroll = Math.Max(0, layout.RowCount - layout.VisibleRowCount);
            layout.ScrollIndex        = Math.Clamp(layout.ScrollIndex, 0, maxScroll);
            plot.TableViewScrollIndex = layout.ScrollIndex;

            return layout;
        }

        // ============================================================
        //  Frequency union (kept for backward compatibility)
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
        /// Returns the sorted row axis for the table.  When all traces are cube-bound,
        /// returns the union of their X values (the kept/sweep axis, e.g. Pin values).
        /// Otherwise falls back to <see cref="GetSortedFrequencies"/>.
        /// </summary>
        public static double[] GetSortedRowAxis(Plot plot)
        {
            if (!plot.Traces.All(t => t.IsCubeBound))
                return GetSortedFrequencies(plot);

            var set = new SortedSet<double>();
            foreach (var t in plot.Traces)
                if (t.CubeXValues is { } xs)
                    foreach (var x in xs) set.Add(x);

            double[] arr = set.ToArray();
            if (!plot.TableViewAscendingSortOrder) Array.Reverse(arr);
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
            // Return the X values from the first XAxis column, or empty if no columns.
            var firstX = layout.Columns.FirstOrDefault(c => c.Kind == TableColKind.XAxis);
            if (firstX is null) return Array.Empty<double>();
            int count = Math.Min(layout.VisibleRowCount, firstX.XValues.Length - start);
            if (count <= 0) return Array.Empty<double>();
            var result = new double[count];
            Array.Copy(firstX.XValues, start, result, 0, count);
            return result;
        }

        // ============================================================
        //  Private helpers — value equality + sort
        // ============================================================

        private static bool RawValuesEqual(double[] a, double[] b)
        {
            // Compare sorted sets so different orderings still match.
            if (a.Length != b.Length) return false;
            var sa = new SortedSet<double>(a);
            var sb = new SortedSet<double>(b);
            if (sa.Count != sb.Count) return false;
            using var ea = sa.GetEnumerator();
            using var eb = sb.GetEnumerator();
            while (ea.MoveNext() && eb.MoveNext())
                if (ea.Current != eb.Current) return false;
            return true;
        }

        private static double[] SortUnique(double[] values, bool ascending)
        {
            var set = new SortedSet<double>(values);
            var arr = new double[set.Count];
            set.CopyTo(arr);
            if (!ascending) Array.Reverse(arr);
            return arr;
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

        private static bool IsFreqUnit(string? unit) =>
            unit is "Hz" or "kHz" or "MHz" or "GHz";

        /// <summary>
        /// Looks up <paramref name="xValue"/> in <paramref name="trace"/>.CubeXValues and
        /// returns the post-transform formatted cell string. "NaN" if the value is absent.
        /// </summary>
        private static string FormatCubeCellAt(Trace trace, double xValue)
        {
            var xs = trace.CubeXValues;
            if (xs is null) return "NaN";
            for (int i = 0; i < xs.Count; i++)
                if (xs[i] == xValue)
                    return trace.FormatCubeCell(i, trace.FormatString, trace.MaximumFractionDigits);
            return "NaN";
        }

        private static string FormatFamilyCellAt(Trace trace, int curveIndex, double xValue)
        {
            var xs = trace.CubeXValues;
            if (xs is null) return "";
            for (int i = 0; i < xs.Count; i++)
                if (xs[i] == xValue)
                    return trace.FormatFamilyCell(curveIndex, i, trace.FormatString, trace.MaximumFractionDigits);
            return "";
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
        /// When <paramref name="fallback"/> is provided and alignment is Left, uses per-glyph
        /// DejaVu fallback for any code point the primary font lacks (e.g. ∠ U+2220).
        /// <paramref name="paddingX"/> should be <c>layout.ScaledPaddingX</c> (canvas pixels).
        /// </summary>
        private static void DrawClippedText(
            SKCanvas    canvas, SKFont font, SKPaint paint,
            string      text,
            float       colX, float y, float colW,
            float       paddingX,
            SKTextAlign align,
            SKFont?     fallback = null)
        {
            float available = colW - paddingX * 2;
            if (available <= 0) return;

            string clipped = TruncateMiddle(font, text, available);

            if (align == SKTextAlign.Left && fallback != null)
            {
                RendererText.DrawLeftTextWithFallback(
                    canvas, clipped, colX + paddingX, y, font, fallback, paint);
            }
            else
            {
                float x = align switch
                {
                    SKTextAlign.Center => colX + colW / 2f,
                    SKTextAlign.Right  => colX + colW - paddingX,
                    _                  => colX + paddingX,  // Left
                };
                canvas.DrawText(clipped, x, y, align, font, paint);
            }
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
                if (absRow >= layout.RowCount) break;
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

            for (int planC = 0; planC < layout.Columns.Count; planC++)
            {
                var col = layout.Columns[planC];
                if (col.Kind != TableColKind.TraceValue) continue;

                var   trace = plot.Traces[col.FirstTraceIndex];
                float cellX = layout.ColX[planC];
                float cellW = layout.ColW[planC];

                foreach (var m in trace.Markers)
                {
                    // Find the row within this trace's X group where m.Freq lives.
                    int absRow = Array.FindIndex(col.XValues, v => v == m.Freq);
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
                if (absRow > layout.RowCount) break;
                float y = layout.DataStartY + r * layout.RowH;
                if (y > totalH) break;
                canvas.DrawLine(0, y, (float)canvasSize.W, y, borderPaint);
            }
        }

        private static void DrawHeaderRow(
            SKCanvas canvas, TableLayout layout, Plot plot,
            RenderTheme theme, SKFont boldFont, SKFont dejaVuBold, SKFont regularFont,
            bool showFilePrefix)
        {
            float totalColW = layout.ColCount > 0
                ? layout.ColX[layout.ColCount - 1] + layout.ColW[layout.ColCount - 1]
                : 0f;

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
            float fs    = boldFont.Size;

            for (int c = 0; c < layout.ColCount; c++)
            {
                var col = layout.Columns[c];

                if (col.Kind == TableColKind.XAxis)
                {
                    // Draw X-axis column header (pre-computed in BuildColumns).
                    DrawClippedText(canvas, boldFont, textPaint,
                        col.Header,
                        layout.ColX[c], textY, layout.ColW[c],
                        layout.ScaledPaddingX, CellHeaderHorizAlign, dejaVuBold);

                    // Sort-direction triangle, positioned just right of the header text.
                    float triSize   = fs * 0.6f;
                    float measuredW = Math.Min(
                        boldFont.MeasureText(col.Header),
                        layout.ColW[c] - layout.ScaledPaddingX * 2);
                    float spaceW  = boldFont.MeasureText(" ");
                    float triCx   = layout.ColX[c] + layout.ScaledPaddingX + measuredW + spaceW * 1.5f + triSize * 0.5f;
                    float maxCx   = layout.ColX[c] + layout.ColW[c] - triSize * 0.5f - CellBorderWidth;
                    triCx         = Math.Min(triCx, maxCx);
                    float triMidY = layout.HeaderH * 0.5f;

                    DrawSortArrow(canvas, plot.TableViewAscendingSortOrder, triCx, triMidY, triSize, textPaint);
                }
                else
                {
                    // TraceValue column: compute label considering showFilePrefix.
                    var    trace = plot.Traces[col.FirstTraceIndex];
                    string label;
                    if (col.FamilyCurveIndex >= 0)
                    {
                        // Family trace: col.Header already contains "baseShorthand @ familyLabel".
                        label = col.Header;
                        if (showFilePrefix && trace.SourcePath != null)
                            label = System.IO.Path.GetFileNameWithoutExtension(trace.SourcePath) + ".." + label;
                    }
                    else if (trace.IsCubeBound)
                    {
                        label = trace.CubeShorthand ?? trace.ShortDescription;
                        if (showFilePrefix && trace.SourcePath != null)
                            label = System.IO.Path.GetFileNameWithoutExtension(trace.SourcePath) + ".." + label;
                    }
                    else
                    {
                        label = showFilePrefix ? trace.Description : trace.ShortDescription;
                    }
                    DrawClippedText(canvas, boldFont, textPaint,
                        label, layout.ColX[c], textY, layout.ColW[c],
                        layout.ScaledPaddingX, CellHeaderHorizAlign);
                }
            }
        }

        /// <summary>
        /// Draws a small filled triangle (up = ascending, down = descending) centred at (cx, midY).
        /// </summary>
        private static void DrawSortArrow(
            SKCanvas canvas, bool ascending,
            float cx, float midY, float size, SKPaint textPaint)
        {
            float h = size;
            float hw = size * 0.5f;

            using var path = new SKPath();
            if (ascending)
            {
                // Upward triangle
                path.MoveTo(cx,       midY - h * 0.5f);   // apex
                path.LineTo(cx - hw,  midY + h * 0.5f);   // bottom-left
                path.LineTo(cx + hw,  midY + h * 0.5f);   // bottom-right
            }
            else
            {
                // Downward triangle
                path.MoveTo(cx - hw,  midY - h * 0.5f);   // top-left
                path.LineTo(cx + hw,  midY - h * 0.5f);   // top-right
                path.LineTo(cx,       midY + h * 0.5f);   // apex
            }
            path.Close();

            using var fillPaint = new SKPaint
            {
                Color       = textPaint.Color,
                Style       = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            canvas.DrawPath(path, fillPaint);
        }

        private static void DrawDataRows(
            SKCanvas canvas, (double W, double H) canvasSize,
            TableLayout layout, Plot plot,
            RenderTheme theme, SKFont regularFont, SKFont dejaVuRegular)
        {
            using var textPaint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

            float canvasH = (float)canvasSize.H;

            for (int r = 0; r < layout.VisibleRowCount; r++)
            {
                int absRow = layout.ScrollIndex + r;
                if (absRow >= layout.RowCount) break;

                float rowTop = layout.DataStartY + r * layout.RowH;
                if (rowTop >= canvasH) break;

                // Baseline is always computed from the full row height so vertical
                // spacing is uniform across all rows, including the partially-clipped
                // last row.  The canvas ClipRect (set in Draw) trims any overflow.
                float textY = rowTop + layout.RowH * CellTextVertFraction;

                for (int c = 0; c < layout.ColCount; c++)
                {
                    var col = layout.Columns[c];
                    string cellText = FormatColumnCell(col, absRow, plot);

                    // XAxis cells use the primary font (no ∠); TraceValue cells may contain ∠.
                    DrawClippedText(canvas, regularFont, textPaint,
                        cellText,
                        layout.ColX[c], textY, layout.ColW[c],
                        layout.ScaledPaddingX, CellDataHorizAlign,
                        fallback: col.Kind == TableColKind.XAxis ? null : dejaVuRegular);
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
