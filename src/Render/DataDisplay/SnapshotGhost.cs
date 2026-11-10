// ================================================================
//  SnapshotGhost.cs  —  a trace's faded "before" copy, drawn from a
//  tuning snapshot (brief-tuneopt-3 R-to3-9, overview D9)
//
//  A snapshot is a DataSet the tuning session froze. Every trace bound to
//  that source draws a copy of itself resolved against the frozen data:
//  same colour, reduced opacity, no point markers, no data markers. The
//  copy lives in Plot.GhostTraces, which nothing that saves, autoscales,
//  lists a legend or hit-tests reads — so it is session-only without any
//  code having to remember to leave it out.
// ================================================================

using System;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class SnapshotGhost
{
    /// <summary>The share of its live trace's opacity a ghost is drawn at.</summary>
    public const double OpacityFactor = 0.35;

    /// <summary>
    /// A ghost of <paramref name="source"/> resolved against <paramref name="snapshot"/>, or null when
    /// the trace has nothing a snapshot can stand in for (a contour, a summary column, an annotation)
    /// or does not resolve against it. Never throws — a ghost is a convenience, and failing to draw
    /// one must not cost the live curve.
    /// </summary>
    public static Trace? Of(Trace source, DataSet snapshot, PlotType plotType, FreqUnit freqUnit,
                            double opacityFactor = OpacityFactor)
    {
        if (source.IsContourTrace || source.IsSummaryColumn) return null;
        try
        {
            var ghost = new Trace(source, includeMarkers: false);
            ghost.Properties.LineOpacity   = source.Properties.LineOpacity * opacityFactor;
            ghost.Properties.MarkerEnabled = false;
            // A ghost is the plain "before" curve: no pass/fail colouring, band or nominal of its own (brief-yield-9).
            ghost.ColorBy     = null;
            ghost.Envelope    = TrialEnvelope.Off;
            ghost.ShowNominal = false;
            ghost.ShowCurves  = true;
            ghost.ShowFitLine = false;

            if (source.IsCubeBound)
            {
                TraceResolve.SetCubeDataFrom(ghost, snapshot, plotType, freqUnit);
                if (ghost.ExpressionError is not null) return null;
            }
            else
            {
                // A network or derived trace reads an SNP: the snapshot's own, built exactly as the
                // live entry builds one (a flat S becomes the Snp; a grouped run's S its network view).
                SNP? snp = snapshot.Contains("S") ? DataSetBuilder.ToSnp(snapshot) : DataSourceView.NetworkViewOf(snapshot);
                if (snp is null || snp.IsEmpty) return null;
                ghost.Data = snp;
                ghost.BuildPath(plotType, freqUnit);
            }

            return ghost.Points.Count > 0 || ghost.FamilyCurves.Count > 0 ? ghost : null;
        }
        catch (Exception ex)
        {
            DataDisplayDiagnostics.Note($"snapshot ghost skipped: {ex.GetType().Name}");
            return null;
        }
    }
}
