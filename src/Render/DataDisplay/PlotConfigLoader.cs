// ================================================================
//  PlotConfigLoader.cs  —  a `.cdd`'s PlotContainerConfig, as a live Plot
//
//  RND-4 (R-rnd4-2). This was the body of
//  `DataDisplayViewModel.LoadPlotContainerConfigAsync`, and it is the
//  part of opening a Data Display that is neither asynchronous nor
//  view-model-shaped: read the config, build the Trace objects, restore
//  their markers and properties, resolve them, restore the axis window.
//
//  WHAT STAYED WITH THE VIEW MODEL is the half that mutates a LIBRARY —
//  awaiting a lazy file load, adding a broken-entry placeholder for a
//  source that is gone — and the half that builds the view models around
//  the finished Plot. Both are done by the caller, before and after; this
//  takes a source provider that has already been made ready.
//
//  It exists because the alternative was a second `.cdd` reader in
//  src/Cli, and a document that opens as one picture in the window and a
//  different one on a build machine is the failure this whole brief is
//  arranged to prevent.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static class PlotConfigLoader
{
    /// <summary>
    /// Builds one <see cref="Plot"/> from its saved configuration, resolving every trace against
    /// <paramref name="sources"/>. Contour and summary traces are built but NOT resolved — see
    /// <see cref="ResolveDerived"/> for why that is a second step.
    /// </summary>
    public static Plot LoadPlot(PlotContainerConfig pc, IPlotDataSources sources)
    {
        var plot = new Plot(pc.PlotType, pc.FreqUnit);

        plot.CustomTitle     = pc.CustomTitle;
        plot.CustomTitleOn   = pc.CustomTitleOn;
        plot.CustomXLabel    = pc.CustomXLabel;
        plot.CustomXLabelOn  = pc.CustomXLabelOn;
        plot.CustomYLabel    = pc.CustomYLabel;
        plot.CustomYLabelOn  = pc.CustomYLabelOn;
        plot.CustomY2Label   = pc.CustomY2Label;
        plot.CustomY2LabelOn = pc.CustomY2LabelOn;
        plot.TableViewAscendingSortOrder = pc.TableViewAscendingSortOrder;
        plot.TableViewScrollIndex        = pc.TableViewScrollIndex;
        plot.FontSize                    = pc.FontSize > 0 ? pc.FontSize : 12;
        plot.ColumnWidth                 = pc.FreqColumnWidth > 0 ? pc.FreqColumnWidth : 115;
        plot.TableOptimum                = pc.TableOptimum;
        plot.TableReadMode               = pc.TableReadMode;
        plot.TableCompression            = pc.TableCompression > 0 ? pc.TableCompression : 3.0;
        plot.SummaryLoadpullGroup        = pc.SummaryLoadpullGroup;
        plot.PolarRadial                 = pc.PolarRadial;
        plot.PolarDbFloor                = pc.PolarDbFloor;
        plot.PolarDbRingStep             = pc.PolarDbRingStep > 0 ? pc.PolarDbRingStep : 10.0;
        plot.PolarDbReference            = pc.PolarDbReference;
        plot.PolarDbReferenceValue       = pc.PolarDbReferenceValue;
        plot.PolarDbUnit                 = pc.PolarDbUnit ?? "";
        plot.ShowPolarAngleLabels        = pc.PolarAngleLabels;
        plot.ShowSmithAdmittanceGrid     = pc.SmithAdmittanceGrid;
        plot.SurfaceCamera               = PatternCamera.New(pc.SurfaceAzimuthDeg, pc.SurfaceElevationDeg, pc.SurfaceZoom);
        plot.SurfaceColorMap             = pc.SurfaceColorMap;
        plot.SurfaceShowGroundDisc       = pc.SurfaceShowGroundDisc;
        plot.SurfaceShowAxes             = pc.SurfaceShowAxes;
        plot.SurfaceShowLegend           = pc.SurfaceShowLegend;

        foreach (var traceConfig in pc.Traces)
            if (LoadTrace(traceConfig, pc.PlotType, pc.FreqUnit, sources) is { } trace)
                plot.Traces.Add(trace);

        if (pc.Axes is { } savedAxes)
        {
            // BEFORE the windows, and it has to be: Axes.Window repairs its value differently in
            // each mode (the linear zero-nudge is the log map's undefined point), so a log document
            // whose mode arrived second would have had its left edge nudged to −1e-6 on the way in.
            if (plot.PlotType.IsRect()) plot.Axes.XScale = savedAxes.XScale;
            plot.RestoreAxesFromConfig(
                savedAxes.AutoscaleX, savedAxes.AutoscaleY,
                savedAxes.AutoscaleRightY, savedAxes.AutoscaleMag,
                new PlotRect(savedAxes.WindowX, savedAxes.WindowY,
                         savedAxes.WindowWidth, savedAxes.WindowHeight),
                new PlotRect(savedAxes.WindowSecondaryX, savedAxes.WindowSecondaryY,
                         savedAxes.WindowSecondaryWidth, savedAxes.WindowSecondaryHeight));
        }
        else
            plot.Autoscale();  // no axes config — old file, default to full autoscale
        return plot;
    }


    /// <summary>
    /// One saved <see cref="TraceConfig"/> as a live <see cref="Trace"/>, resolved against
    /// <paramref name="sources"/> — or null when its source is not there to resolve against.
    /// </summary>
    /// <remarks>
    /// <b>This was the body of <see cref="LoadPlot"/>'s loop and it is a pure extraction</b>
    /// (<c>brief-smith-12-overlays-via-the-inspector.md</c> <c>R-smith12-5</c>): the existing `.cdd`
    /// suite is its gate, and if any of that suite had moved, the extraction was wrong.
    ///
    /// <para><b>It exists because a `.csmith` now stores its overlays as trace configs too.</b> The
    /// Smith Chart tool rebuilds its own traces from the design on every edit, so a trace the user
    /// added in the inspector has to be written into the document and restored from it — and
    /// restoring one is exactly this, for one config rather than for a whole plot. A second loader
    /// there would be a second reading of the same thirty fields, free to drift from this one
    /// silently: the symptom would be a setting that survives in a `.cdd` and is lost from a
    /// `.csmith`.</para>
    ///
    /// <para>A null answer is the same "this one is not drawable" the loop used to express by
    /// skipping: a network-bound trace with no SNP, or a cube, contour or summary trace whose
    /// source is not a source at all.</para>
    /// </remarks>
    public static Trace? LoadTrace(TraceConfig traceConfig, PlotType plotType, FreqUnit freqUnit,
                                   IPlotDataSources sources)
    {
        ArgumentNullException.ThrowIfNull(traceConfig);
        ArgumentNullException.ThrowIfNull(sources);

        if (traceConfig.SourcePath is null) return null;

        // traceConfig.SourcePath stores the logical SourceRef (not an absolute/relative path).
        // Whatever LOADING that reference needs has already happened — the caller's business,
        // because in the application it is asynchronous and creates broken-entry placeholders,
        // and headlessly it is a set of files named on a command line.
        string? sref         = traceConfig.SourcePath;
        string? resolvedPath = sources.ResolveAbs(sref);

        bool hasEntry = resolvedPath is not null && sources.Contains(resolvedPath);

        // NetworkView first, Snp as the fallback — the SAME order the picker's own bind uses, so
        // a trace restored from a .cdd and a trace picked in the card end up holding the SAME
        // object for the same source.
        //
        // Reading Snp alone meant a SIMULATED source — which has no Snp by design — produced
        // `snp == null`, and the `snp is null` guard just below dropped every DERIVED trace
        // (stability circles, MaxGain, µ, …) as the display opened. Its S(i,j) traces are
        // cube-bound and never went through this branch, which is why only the metrics were
        // affected. For a Touchstone or a broken entry NetworkView IS Snp, so nothing about
        // those two paths changes — including the broken-entry placeholder the guard below
        // deliberately accepts.
        SNP? snp = resolvedPath is not null ? sources.NetworkFor(resolvedPath) : null;

        bool isCubeBound    = (traceConfig.CubeName is not null && traceConfig.CubeSlice.Count > 0)
                           || traceConfig.Expression is not null;
        bool isContourTrace = traceConfig.ContourTrace is not null;
        bool isSummaryTrace = traceConfig.SummaryColumn is not null;

        // Network-bound: must have a valid SNP. Cube-bound/contour/summary: must have a library entry.
        if (!isCubeBound && !isContourTrace && !isSummaryTrace && snp is null) return null;
        if (isCubeBound  && !hasEntry) return null;
        if (isContourTrace && !hasEntry) return null;
        if (isSummaryTrace && !hasEntry) return null;

        void RestoreMarkers(Trace tr, TraceConfig tcfg)
        {
            foreach (var mc in tcfg.Markers)
            {
                var marker = new Marker(tr, mc.Freq, mc.IsMulti, mc.IsDelta, mc.Index, mc.FreqUnits)
                {
                    Name                   = mc.Name,
                    MatrixFormat           = mc.MatrixFormat,
                    MatrixFormatImpedance  = mc.MatrixFormatImpedance,
                    Style                  = mc.Style,
                    UseNormalizedImpedance = mc.UseNormalizedImpedance,
                    MaximumFractionDigits  = mc.MaximumFractionDigits,
                    InfoBoxPos             = new PlotPoint(mc.InfoBoxX, mc.InfoBoxY),
                    PositionStatic         = new System.Numerics.Vector2(mc.PositionStaticX, mc.PositionStaticY),
                    FreePosition           = mc.FreePosition,
                    MarkerKind             = mc.MarkerKind,
                    ShowInfoBox            = mc.ShowInfoBox,
                    ContourSnapped         = mc.ContourSnapped,
                    VswrEnabled            = mc.VswrEnabled,
                    VswrValue              = mc.VswrValue,
                };
                tr.Markers.Add(marker);
            }
        }

        Trace trace;
        if (isSummaryTrace)
        {
            var sc = traceConfig.SummaryColumn!;
            var placeholder = new SNP(new double[] { 1e9 }, 1);
            trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db, false);
            trace.SummaryColumn = new SummaryColumnData
            {
                Kind           = sc.Kind,
                MetricName     = sc.MetricName,
                Header         = sc.Header,
                FractionDigits = sc.FractionDigits,
                ColumnWidth    = sc.ColumnWidth,
            };
            ApplyProperties(traceConfig.Properties, trace.Properties);
            trace.ExcludeFromAutoscale = traceConfig.ExcludeFromAutoscale;
            trace.SourceRef  = sref;
            trace.SourcePath = resolvedPath;
            RestoreMarkers(trace, traceConfig);
            return trace;
        }
        else if (isContourTrace)
        {
            var ct = traceConfig.ContourTrace!;
            var placeholder = new SNP(new double[] { 1e9 }, 1);
            trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db, false);
            trace.ContourData = new ContourData
            {
                MetricName            = ct.MetricName,
                ContourConstraintKind = ct.ConstraintKind,
                ConstraintMetricName  = ct.ConstraintMetricName,
                ConstraintValue       = ct.ConstraintValue,
                FreqIndex             = ct.FreqIndex,
                LoadpullGroup         = ct.LoadpullGroup,
                LevelMode             = ct.LevelMode,
                LevelStart            = ct.LevelStart,
                LevelStep             = ct.LevelStep,
                LevelStop             = ct.LevelStop,
                LevelCount            = ct.LevelCount,
                ShowIsoLines          = ct.ShowIsoLines,
                ShowFill              = ct.ShowFill,
                DrawLabels            = ct.DrawLabels,
                SelectedFillKind      = ct.SelectedFillKind,
                ColorMap              = ct.ColorMap,
                LabelSpacing          = ct.LabelSpacing,
                DisplayMxp            = ct.DisplayMxp,
                DisplayMxe            = ct.DisplayMxe,
                DisplayGridPoints     = ct.DisplayGridPoints,
                GridPointColor        = new SKColor(ct.GridPointColor),
                LabelForeground       = new SKColor(ct.LabelForeground),
                LineColor             = new SKColor(ct.LineColor),
                StrokeWidth           = ct.StrokeWidth,
                LineColorOverridden   = ct.LineColorOverridden,
                LabelBackground       = new SKColor(ct.LabelBackground),
                GridPointSize         = ct.GridPointSize,
                LevelFontSize         = ct.LevelFontSize,
                FadeLineOpacity       = ct.FadeLineOpacity,
                InterpKernel          = ct.InterpKernel,
                Smoothing             = ct.Smoothing,
                Epsilon               = ct.Epsilon,
            };
            ApplyProperties(traceConfig.Properties, trace.Properties);
            trace.ExcludeFromAutoscale = traceConfig.ExcludeFromAutoscale;
            trace.SourceRef  = sref;
            trace.SourcePath = resolvedPath;
            RestoreMarkers(trace, traceConfig);
            return trace;
        }
        else if (isCubeBound)
        {
            // Use a placeholder SNP (or the actual SNP if the file has one).
            var placeholderSnp = snp ?? new SNP(new double[] { 1e9 }, 2);
            trace = new Trace(placeholderSnp, MatrixType.S, 0, 0,
                              DependentVarFormat.Db, traceConfig.UseSecondaryAxis);
            trace.CubeName   = traceConfig.CubeName;
            trace.Transform  = traceConfig.CubeTransform;
            trace.MirrorPatternAngle = traceConfig.MirrorPatternAngle;
            trace.PatternWholePlane  = traceConfig.PatternWholePlane;
            trace.ReferenceInputPowerDbmOverride = traceConfig.ReferenceInputPowerDbmOverride;
            trace.Slice      = traceConfig.CubeSlice.Count > 0
                ? traceConfig.CubeSlice.Select(s => s.ToSlice()).ToArray()
                : null;
            trace.Expression = traceConfig.Expression;
            // WSP-4: the probe metric rides on the cube-bound trace it already is.
            if (traceConfig.WsProbe is { } wc)
                trace.Wsp = new WspTraceSpec
                {
                    Probe      = wc.Probe,
                    With       = wc.With,
                    Set        = [.. wc.Set],
                    Metric     = wc.Metric,
                    Z0         = ComplexStringHelper.TryParse(wc.Z0, out var wz0) ? wz0 : System.Numerics.Complex.Zero,
                    ActiveSide = wc.ActiveSide,
                    SetIndex   = wc.SetIndex,
                    SourceProbe   = wc.SourceProbe,
                    LoadProbe     = wc.LoadProbe,
                    GammaSMags    = [.. wc.GammaSMags],
                    GammaLMags    = [.. wc.GammaLMags],
                    ThetaStepDeg  = wc.ThetaStepDeg,
                    PassiveSource = wc.PassiveSource,
                };
        }
        else
        {
            trace = new Trace(snp!, traceConfig.MatrixType, traceConfig.Row,
                              traceConfig.Col, traceConfig.YAxis, traceConfig.UseSecondaryAxis);
            trace.Derived = traceConfig.Derived;
        }

        // Ordered port selection for network metrics (R-stb-3a) — restored for every trace
        // kind, since a .cdd may carry a derived trace on either source path.
        trace.InputPort             = traceConfig.InputPort;
        trace.OutputPort            = traceConfig.OutputPort;
        trace.PassivityWholeNetwork = traceConfig.PassivityWholeNetwork;
        trace.PassiveExtraction     = traceConfig.PassiveExtraction;

        trace.SourceRef             = sref;
        trace.SourcePath            = resolvedPath;

        // "Plot versus" (Y vs X). XSourcePath is persisted the same logical way a source alias
        // is — relative to the results root when it lives there — and names a CONCRETE file
        // (never the "Selected" sentinel: an X side belongs to one dataset, not to whichever
        // source happens to be selected).
        trace.XSpec = traceConfig.XSpec;
        if (!string.IsNullOrEmpty(traceConfig.XSourcePath))
            trace.XSourcePath = sources.ResolveAbs(traceConfig.XSourcePath);
        trace.MatrixFormat          = traceConfig.MatrixFormat;
        trace.ColumnWidth           = traceConfig.ColumnWidth > 0 ? traceConfig.ColumnWidth : 115;
        trace.XColumnWidth          = traceConfig.XColumnWidth;
        foreach (var kvp in traceConfig.FamilyColumnWidths)
            trace.FamilyColumnWidths[kvp.Key] = kvp.Value;
        trace.FormatString          = traceConfig.FormatString;
        trace.MaximumFractionDigits = traceConfig.MaximumFractionDigits;

        if (ComplexStringHelper.TryParse(traceConfig.Z0, out System.Numerics.Complex z0))
            trace.Z0 = z0;
        trace.Z0OverrideEnabled = traceConfig.Z0Override;

        ApplyProperties(traceConfig.Properties, trace.Properties);

        // OUT OF THE AUTOSCALE WHEN THE CONFIG SAYS SO. Absent in every `.cdd` written before
        // this field, so those files keep the framing they had; the Smith Chart tool's own
        // reference material is what carries it, and a stability circle far outside the disc
        // is why it has to be carried at all.
        trace.ExcludeFromAutoscale = traceConfig.ExcludeFromAutoscale;

        if (isCubeBound)
            TraceResolve.ResolveCubeTrace(trace, sources, plotType, freqUnit);
        else if (snp is not null && !snp.IsEmpty)
            trace.BuildPath(plotType, freqUnit);

        if (isCubeBound) { RestoreMarkers(trace, traceConfig); return trace; }

        RestoreMarkers(trace, traceConfig);
        return trace;
    }

    /// <summary>
    /// Resolves the two trace kinds whose data is not in the `.cdd` and is not a cube slice either:
    /// a loadpull CONTOUR (re-fitted from its own source) and a summary TABLE (re-derived from the
    /// selected source at the plot's compression, optimum and read mode).
    ///
    /// <para><b>A second step on purpose.</b> In the application these are driven by the trace card
    /// and the plot inspector, which own the metric, frequency and analysis PICKERS that have to
    /// stay in step with the same choice — so they call <see cref="ContourResolve"/> and
    /// <see cref="SummaryResolve"/> themselves, with the picker refresh in between. A caller with no
    /// pickers calls this instead and gets the same two results.</para>
    /// </summary>
    public static void ResolveDerived(Plot plot, IPlotDataSources sources)
    {
        foreach (var t in plot.Traces)
        {
            if (t.ContourData is not { } cd) continue;
            if (t.SourcePath is not { } path || sources.DataFor(path) is not { } ds)
            {
                ContourResolve.ClearGrid(cd);
                continue;
            }

            var    views = LoadpullRecognition.FindLoadpullViews(ds);
            string group = ContourResolve.GroupFor(views, cd.LoadpullGroup);

            LoadpullSurface surface;
            try { surface = new LoadpullSurface(ds, group); }
            catch { ContourResolve.ClearGrid(cd); continue; }

            if (surface.Frequencies.Count == 0) { ContourResolve.ClearGrid(cd); continue; }
            ContourResolve.Rebuild(t, surface, plot.PlotType);
        }

        if (plot.Traces.Any(t => t.IsSummaryColumn))
        {
            var ds = sources.SelectedData;
            var views = ds is null
                ? (IReadOnlyList<LoadpullRecognition.LoadpullView>)Array.Empty<LoadpullRecognition.LoadpullView>()
                : LoadpullRecognition.FindLoadpullViews(ds);
            SummaryResolve.Rebuild(plot, ds, ContourResolve.GroupFor(views, plot.SummaryLoadpullGroup));
        }
    }

    /// <summary>
    /// The per-trace visual properties a `.cdd` persists. Colours are stored as an INDEX into
    /// <see cref="TraceProperties.ColorLUT"/>, never as an ARGB value, which is why the palette
    /// moving from Avalonia's named colours to explicit ones in RND-4 changed no saved file.
    /// </summary>
    internal static void ApplyProperties(TracePropertiesConfig src, TraceProperties dst)
    {
        dst.LineEnabled      = src.LineEnabled;
        dst.LineWidth        = src.LineWidth;
        dst.LineColorIndex   = src.LineColorIndex;
        dst.LineType         = src.LineType;
        dst.MarkerEnabled    = src.MarkerEnabled;
        dst.MarkerSize       = src.MarkerSize;
        dst.MarkerColorIndex = src.MarkerColorIndex;
        dst.MarkerType       = src.MarkerType;
    }
}
