// ================================================================
//  ContourResolve.cs  —  a loadpull contour trace's geometry, from a
//  LoadpullSurface
//
//  RND-4 (brief-render-4-data-display.md R-rnd4-2). This was
//  `TraceRowViewModel.RebuildContour`, and the split is exactly where
//  the view model stopped being one: everything here is arithmetic on
//  the surface, and everything left behind is the LOOKUP that finds the
//  surface (a data-source library the CLI does not have) plus the metric,
//  frequency and analysis pickers that the card draws around it.
//
//  A contour is one of the trace kinds §5.2 of the brief asks to be
//  rendered identically headlessly, and it is the one whose data is not
//  in the `.cdd` at all — the file stores a metric name, a constraint and
//  a level scheme, and the curves are re-derived from the loadpull source
//  every time. So this had to come down whole; there was nothing to draw
//  without it.
// ================================================================

using System;
using System.Linq;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Render.DataDisplay;

public static class ContourResolve
{
    /// <summary>
    /// The loadpull GROUP a contour trace reads from: the one it names when the source still
    /// carries it, else the first recognized view, else the top level. Shared with the summary
    /// table, which resolves its own group the same way against the same recognizer.
    /// </summary>
    public static string GroupFor(System.Collections.Generic.IReadOnlyList<LoadpullRecognition.LoadpullView> views,
                                  string? wanted)
        => (!string.IsNullOrEmpty(wanted) && views.Any(v => (v.Group ?? "") == wanted)) ? wanted!
         : views.Count > 0 ? (views[0].Group ?? "") : "";

    /// <summary>
    /// Fills a contour trace's <see cref="ContourData"/> — the fit, the resampled grid, the fill
    /// grid, the scatter, the level set, the MXP/MXE coordinates and the two marker hooks — from a
    /// loadpull surface. Everything <c>TraceRowViewModel.RebuildContour</c> did except FIND the
    /// surface (a library lookup) and refresh the pickers around it.
    /// </summary>
    /// <returns>False when the surface has no fit at this frequency/metric; the grid is cleared.</returns>
    public static bool Rebuild(Trace trace, LoadpullSurface surface, PlotType parentPlotType)
    {
        var cd = trace.ContourData;
        if (cd is null) return false;
        int freqIdx = Math.Clamp(cd.FreqIndex, 0, Math.Max(0, surface.Frequencies.Count - 1));

        ConstraintSpec constraint = cd.ContourConstraintKind == ConstraintKind.Compression
            ? ConstraintSpec.AtCompression(cd.ConstraintValue)
            : ConstraintSpec.AtConstantMetric(cd.ConstraintMetricName, cd.ConstraintValue);

        var plane = (parentPlotType is PlotType.Smith or PlotType.Polar)
            ? SurfacePlane.Gamma
            : SurfacePlane.Z;

        // brief-dd-z0-renormalization.md §5: Γ plane only — a Z-plane contour has no reference
        // impedance concept (the impedance grid does not move), so z0 stays null there and cannot
        // leak into that fit even if the trace's Z0 field holds a stale override from a prior
        // Smith/Polar view.
        System.Numerics.Complex? z0 = plane == SurfacePlane.Gamma ? trace.Z0 : (System.Numerics.Complex?)null;

        var fit = surface.Fit(freqIdx, cd.MetricName, constraint, plane, z0,
            kernel: cd.InterpKernel, smooth: cd.Smoothing, epsilon: cd.Epsilon);
        if (fit is null) { ClearGrid(cd); return false; }

        var grid    = surface.Resample(fit);
        // §1: for Smith/Polar compute a disk-covering fill grid over [-1,1]×[-1,1]
        // at higher resolution so the TopoMap fill reaches the circular-clip edge.
        var fillGrid = (plane == SurfacePlane.Gamma)
            ? surface.Resample(fit, new ViewBox(-1.0, 1.0, -1.0, 1.0), 80)
            : null;
        var scatter = surface.Reduce(freqIdx, cd.MetricName, constraint, plane, z0);

        ContourLevelSet levels;
        if (cd.LevelMode == ContourLevelMode.Range)
        {
            double step = cd.LevelStep > 0 ? cd.LevelStep : 0.5;
            var    raw  = ContourExtractor.LevelsByStep(grid, step, cd.LevelStart);
            double lo   = Math.Min(cd.LevelStart, cd.LevelStop);
            double hi   = Math.Max(cd.LevelStart, cd.LevelStop);
            double[] filtered = Array.FindAll(raw.Levels, l => l >= lo && l <= hi);
            levels = new ContourLevelSet(filtered);
        }
        else
        {
            levels = ContourExtractor.LevelsBetween(grid, Math.Max(1, cd.LevelCount));
        }

        cd.Grid       = grid;
        cd.FillGrid   = fillGrid;
        cd.Scatter    = scatter;
        cd.Levels     = levels;
        cd.GammaPlane = plane == SurfacePlane.Gamma;

        // Cache MXP / MXE for the renderer (surface stays out of renderer path).
        // MXP/MXE markers are the compression-based recommended terminations — independent of this
        // contour's metric/constraint (so they stay put when plotting e.g. Efficiency at Constant Pout).
        // The markers sit at the fitted surface's peak over the measured region, so each is at the centre of the contours drawn
        // from that fit; the best measured node was a tie-break on a flat peak and drew MXP away from them.
        var (mxpR, mxeR) = surface.RecommendedMxx(fit);
        cd.MxpCoord = mxpR?.Interpolated;
        cd.MxeCoord = mxeR?.Interpolated;

        // Marker surface-evaluation hooks — capture locals so the closures are stable.
        var      evalSurface = surface;
        int      evalFreq    = freqIdx;
        string   evalMetric  = cd.MetricName;
        var      evalConstr  = constraint;
        var      evalPlane   = plane;
        var      evalZ0      = z0;
        RbfKernel evalKernel = cd.InterpKernel;
        double   evalSmooth  = cd.Smoothing;
        double?  evalEps     = cd.Epsilon;

        cd.EvaluateMetric = (coord, snapped) =>
            evalSurface.MetricAtCoord(evalFreq, evalMetric, coord, evalConstr, evalPlane, evalZ0,
                nearest: snapped, kernel: evalKernel, smooth: evalSmooth, epsilon: evalEps);

        var nodeCoords = scatter.Coords;
        cd.NearestNode = coord =>
        {
            if (nodeCoords is null || nodeCoords.Length == 0) return coord;
            int best = 0; double bestD2 = double.PositiveInfinity;
            for (int i = 0; i < nodeCoords.Length; i++)
            {
                double dx = nodeCoords[i].Real - coord.Real;
                double dy = nodeCoords[i].Imaginary - coord.Imaginary;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return nodeCoords[best];
        };

        return true;
    }

    /// <summary>Empties every derived field, so a trace whose source cannot produce a surface draws nothing rather than the previous source's contours.</summary>
    public static void ClearGrid(ContourData cd)
    {
        cd.Grid           = null;
        cd.FillGrid       = null;
        cd.Scatter        = null;
        cd.Levels         = new ContourLevelSet(Array.Empty<double>());
        cd.MxpCoord       = null;
        cd.MxeCoord       = null;
        cd.EvaluateMetric = null;
        cd.NearestNode    = null;
        cd.GammaPlane     = false;
    }

}
