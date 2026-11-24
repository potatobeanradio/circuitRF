// ================================================================
//  SummaryResolve.cs  —  the loadpull Performance Summary table's cells
//
//  RND-4 (brief-render-4-data-display.md R-rnd4-2). This was
//  `PlotInspectorViewModel.RebuildSummary` and its two column builders.
//  The same split as ContourResolve: what stayed in the view model is
//  the SELECTED-SOURCE lookup and the analysis picker; what came here is
//  every number the table shows.
//
//  A summary column is the other trace kind whose data is not in the
//  `.cdd` — the file stores a column KIND, a metric name and a header,
//  and the values are re-derived from the loadpull source at the plot's
//  own compression, optimum and read mode. Nothing could be drawn
//  headlessly without it.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Render.DataDisplay;

public static class SummaryResolve
{
    /// <summary>
    /// Recomputes every summary column on <paramref name="plot"/> from <paramref name="ds"/>, and
    /// stamps the plot's own row axis (<c>SummaryFreqs</c> and its name/unit). Returns false — with
    /// the cells cleared and the row axis dropped — when the source carries no loadpull surface, or
    /// when the plot has no summary columns at all.
    /// </summary>
    /// <param name="lpGroup">
    /// The loadpull group to read, as <see cref="ContourResolve.GroupFor"/> resolves it. Passed in
    /// rather than derived here because the caller also has to keep the analysis PICKER in step with
    /// the same choice, and two independent derivations of it is exactly how those two disagree.
    /// </param>
    public static bool Rebuild(Plot plot, DataSet? ds, string lpGroup)
    {
        var summaryTraces = plot.Traces.Where(t => t.IsSummaryColumn).ToList();
        if (summaryTraces.Count == 0)
        {
            plot.SummaryFreqs = null;
            return false;
        }

        if (ds is null)
        {
            plot.SummaryFreqs = null;
            ClearSummaryCells(summaryTraces);
            return false;
        }

        bool atMarker = plot.TableOptimum == TableOptimum.AtMarker;
        LoadpullSurface surface;
        // At a marker the table is re-read on every move of it, so it reads the one cached surface the links use
        // (brief-ddl-3 R-ddl3-7) rather than refitting every column each time.
        try { surface = atMarker ? LoadpullLinks.Surface(ds, lpGroup) ?? throw new InvalidOperationException()
                                 : new LoadpullSurface(ds, lpGroup); }
        catch
        {
            plot.SummaryFreqs = null;
            plot.SummaryAxisName = null;
            plot.SummaryAxisUnit = null;
            ClearSummaryCells(summaryTraces);
            return false;
        }

        int nFreq = surface.Frequencies.Count;
        var freqs = new double[nFreq];
        for (int i = 0; i < nFreq; i++) freqs[i] = surface.Frequencies[i];
        plot.SummaryFreqs = freqs;
        plot.SummaryAxisName = surface.LeadingAxisName;
        plot.SummaryAxisUnit = surface.LeadingAxisUnit;

        var constraint = ConstraintSpec.AtCompression(plot.TableCompression, plot.TableCompressionReference, plot.TableCompressionType);
        var plane      = SurfacePlane.Z;
        bool nearest   = plot.TableReadMode == TableReadMode.Nearest;

        var optima = new System.Numerics.Complex?[nFreq];
        if (atMarker)
        {
            // Every row read at the marker's load (R-ddl3-5): ZL is that load, and a marker snapped to a measured load
            // reads that node's measured values in either mode.
            var pos = plot.TableMarkerPosition;
            for (int fi = 0; fi < nFreq; fi++) optima[fi] = pos?.Z;
            nearest |= pos?.Snapped == true;
        }
        else
        for (int fi = 0; fi < nFreq; fi++)
        {
            var mxx = plot.TableOptimum == TableOptimum.Mxp
                ? surface.MaxPower(fi, constraint, plane)
                : surface.MaxEfficiency(fi, constraint, plane);
            optima[fi] = mxx is null ? (System.Numerics.Complex?)null
                       : (nearest ? mxx.Measured : mxx.Interpolated);
        }

        foreach (var t in summaryTraces)
        {
            var sc = t.SummaryColumn!;
            if (SummaryColumns.IsComplexColumn(sc.Kind))
                sc.CellsComplex = ComputeComplexColumn(surface, sc, optima, freqs, constraint, plane, nearest);
            else
                sc.CellsReal = ComputeRealColumn(surface, sc, optima, constraint, plane, nearest);
            t.ColumnWidth = sc.ColumnWidth > 0 ? sc.ColumnWidth : plot.ColumnWidth;
        }

        return true;
    }

    private static void ClearSummaryCells(IEnumerable<Trace> summaryTraces)
    {
        foreach (var t in summaryTraces)
        {
            if (t.SummaryColumn is not { } sc) continue;
            sc.CellsReal    = null;
            sc.CellsComplex = null;
        }
    }

    private static double[] ComputeRealColumn(
        LoadpullSurface surface, SummaryColumnData sc,
        System.Numerics.Complex?[] optima, ConstraintSpec constraint, SurfacePlane plane, bool nearest)
    {
        int n = optima.Length;
        var cells = new double[n];

        if (sc.Kind == SummaryColumnKind.OperatingPoint)
        {
            // Read the raw bias values (SI base units: Amps for BiasILoad, Volts for BiasVLoad).
            var raw = new double[n];
            double repAbs = double.NaN;   // first finite |value| → drives the magnitude-inferred unit
            for (int fi = 0; fi < n; fi++)
            {
                double? v = surface.OperatingPoint(fi, sc.MetricName);
                raw[fi] = v ?? double.NaN;
                if (double.IsNaN(repAbs) && v is { } vv && !double.IsNaN(vv))
                    repAbs = System.Math.Abs(vv);
            }

            // Bug 5 (option b): pick the display unit + scale from the representative magnitude,
            // stamp the label so AutoHeader and the card unit label stay consistent with the values.
            var (label, scale) = SummaryColumns.OperatingPointUnit(sc.MetricName, repAbs);
            sc.UnitLabel = label;
            for (int fi = 0; fi < n; fi++)
                cells[fi] = double.IsNaN(raw[fi]) ? double.NaN : raw[fi] * scale;
            return cells;
        }

        sc.UnitLabel = "";
        for (int fi = 0; fi < n; fi++)
        {
            if (optima[fi] is not { } coord) { cells[fi] = double.NaN; continue; }
            cells[fi] = surface.MetricAtCoord(fi, sc.MetricName, coord, constraint, plane, nearest: nearest);
        }
        return cells;
    }

    private static System.Numerics.Complex[] ComputeComplexColumn(
        LoadpullSurface surface, SummaryColumnData sc,
        System.Numerics.Complex?[] optima, double[] freqs, ConstraintSpec constraint, SurfacePlane plane, bool nearest)
    {
        int n = optima.Length;
        var cells = new System.Numerics.Complex[n];
        var nan   = new System.Numerics.Complex(double.NaN, double.NaN);
        for (int fi = 0; fi < n; fi++)
        {
            switch (sc.Kind)
            {
                case SummaryColumnKind.Zsource:
                    cells[fi] = surface.SourceZ(fi) ?? nan;
                    break;

                case SummaryColumnKind.Zload:
                    cells[fi] = optima[fi] ?? nan;
                    break;

                case SummaryColumnKind.Zin:
                    if (optima[fi] is { } c)
                    {
                        double re = surface.MetricAtCoord(fi, "Zin_real", c, constraint, plane, nearest: nearest);
                        double im = surface.MetricAtCoord(fi, "Zin_imag", c, constraint, plane, nearest: nearest);
                        cells[fi] = (double.IsNaN(re) || double.IsNaN(im)) ? nan
                                  : new System.Numerics.Complex(re, im);
                    }
                    else cells[fi] = nan;
                    break;

                default:
                    cells[fi] = nan;
                    break;
            }
        }
        return cells;
    }
}
