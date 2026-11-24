// ================================================================
//  MarkerInfoBoxViewModel.cs
//
//  ViewModel for a single draggable MarkerInfoBox overlay.
//  One instance per (Marker, Trace) pair, owned by DataDisplayViewModel.
//
//  Position, size, zoom, drag and selection are CanvasBoxViewModel's,
//  shared with a floating legend; this adds the marker, its readout and
//  its menu actions. The position is written back to Marker.InfoBoxPos.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class MarkerInfoBoxViewModel : CanvasBoxViewModel
{
    // ---- Model references -------------------------------------------

    public Marker                  Marker    { get; }
    public Trace                   Trace     { get; }
    public Marker DisplayMarker => Container.PlotVM.Plot.ResolveMarker(Trace, Marker);

    private readonly Func<FreqUnit>       _getFreqUnit;
    private readonly Func<RenderTheme>    _getTheme;

    // ---- Constructor ------------------------------------------------

    public MarkerInfoBoxViewModel(
        Marker                 marker,
        Trace                  trace,
        PlotContainerViewModel container,
        Func<FreqUnit>         getFreqUnit,
        Func<RenderTheme>      getTheme,
        DataDisplayViewModel   parent,
        double                 logicalLeft,
        double                 logicalTop)
        : base(container, parent, logicalLeft, logicalTop)
    {
        Marker       = marker;
        Trace        = trace;
        _getFreqUnit = getFreqUnit;
        _getTheme    = getTheme;

        RefreshSize();
    }

    protected override void CommitPosition(double left, double top) => Marker.InfoBoxPos = new PlotPoint(left, top);

    // ---- Called when marker data changes (symbol dragged) -----------

    public void OnMarkerMoved()
    {
        RefreshSize();
        OnPropertyChanged(nameof(NeedsRedraw));
        // Redraw the PlotControl so the marker glyph moves to the new frequency position.
        Container.RequestPlotRedraw();
    }

    public bool TrySetFrequency(double frequencyHz)
    {
        if (!Trace.Markers.Contains(Marker)) return false;
        double beforeFrequency = Marker.Freq;
        var beforePosition = Marker.PositionStatic;
        var beforeFunction = Marker.Function;
        var resolved = DisplayMarker;
        if (resolved.Unavailable) return false;
        Marker.Freq = resolved.Freq;
        Marker.PositionStatic = resolved.PositionStatic;
        if (!Trace.TrySetMarkerFrequency(Marker, frequencyHz, out _))
        {
            Marker.Freq = beforeFrequency;
            Marker.PositionStatic = beforePosition;
            return false;
        }
        if (beforeFunction is MarkerFunction.Minimum or MarkerFunction.Maximum)
            Marker.Function = MarkerFunction.Normal;
        double afterFrequency = Marker.Freq;
        var afterPosition = Marker.PositionStatic;
        if (beforeFrequency != afterFrequency || beforePosition != afterPosition || beforeFunction != Marker.Function)
            Container.PushUndoCommand(new MoveMarkerFrequencyCommand(this, beforeFrequency, beforePosition,
                afterFrequency, afterPosition, beforeFunction, Marker.Function));
        OnMarkerMoved();
        return true;
    }

    // Triggers a Skia repaint without a size refresh (e.g., theme change).
    public void RequestRedraw() => OnPropertyChanged(nameof(NeedsRedraw));

    public void RefreshForViewport()
    {
        RefreshSize();
        RequestRedraw();
    }

    // Called by DataDisplayViewModel when the library entry count changes so that
    // ShowFilePrefix is re-evaluated and the box text/size updates accordingly.
    public void OnLibraryEntryCountChanged()
    {
        RefreshSize();
        RequestRedraw();
    }

    // Dummy property — PropertyChanged on this triggers InvalidateVisual in the view.
    public int NeedsRedraw { get; private set; }

    // ---- Marker removal -------------------------------------------------

    public void RemoveMarker()
    {
        // Route through the parent so the removal is recorded on the undo stack.
        _parent.RemoveMarkerWithUndo(Marker, Trace, Container);
    }

    // ---- Change to a different trace ------------------------------------

    public void ChangeToTrace(Trace newTrace)
    {
        Trace.Markers.Remove(Marker);
        var moved = new Marker(newTrace, Marker.Freq, Marker.IsMulti, Marker.IsDelta,
                               Marker.Index, Marker.FreqUnits)
        {
            Name                   = Marker.Name,
            Style                  = Marker.Style,
            MatrixFormat           = Marker.MatrixFormat,
            MaximumFractionDigits  = Marker.MaximumFractionDigits,
            UseNormalizedImpedance = Marker.UseNormalizedImpedance,
            FormatString           = Marker.FormatString,
            InfoBoxPos             = Marker.InfoBoxPos,

            // A FREELY-PLACED MARKER KEEPS ITS PLACE (owner instruction, 2026-09-19). Its position
            // IS its position — nothing resolves it against the trace it is stored on — so
            // re-pointing it at another curve must not move it, and the frequency snapping below
            // has nothing to say about it. What DOES change is that it is no longer sitting on
            // whatever a shift-drag had landed it on: the new trace runs somewhere else, so the
            // marker is floating again and draws as the ring rather than the triangle.
            FreePosition           = Marker.FreePosition,
            PositionStatic         = Marker.PositionStatic,
            SnappedToCurve         = false,
            Function               = Marker.Function,
        };
        // Keep the marker's x-position as close as possible to where it was on the old trace,
        // so the user can track where it landed. For network (freq-swept) traces x == frequency,
        // so snap to the new trace's nearest available frequency to the old marker's Freq.
        if (!moved.FreePosition
            && !newTrace.IsCubeBound && newTrace.Data?.Frequencies is { Length: > 0 } newFreqs)
        {
            double best = newFreqs[0], bestDiff = Math.Abs(Marker.Freq - newFreqs[0]);
            for (int i = 1; i < newFreqs.Length; i++)
            {
                double d = Math.Abs(Marker.Freq - newFreqs[i]);
                if (d < bestDiff) { bestDiff = d; best = newFreqs[i]; }
            }
            moved.Freq = best;
        }
        newTrace.Markers.Add(moved);
        if (!moved.FreePosition && newTrace.IsStabilityCircle)
        {
            // temporarily set the current PositionStatic to center so SnapMarkerToStabilityCircle will
            // put the marker glyph on the circle's point closest to center of Smith Chart
            int fi = Array.FindIndex(newTrace.Data!.Frequencies, f => f >= moved.Freq - 1e-6);
            if (fi < 0) fi = newTrace.Data!.Frequencies.Length - 1;
            moved.PositionStatic = new System.Numerics.Vector2(0,0);
            newTrace.SnapMarkerToStabilityCircle(moved, fi);
        }
        Container.RequestPlotRedraw();
        _parent.OnContainerPlotChanged(Container);
    }

    // ---- Helpers ----------------------------------------------------

    // True when the file prefix should appear in the marker info box and context menu.
    // Always true when AlwaysDisplayDataSourcePrefix is set in Settings; otherwise
    // mirrors the library-count heuristic (multiple loaded SNPs).
    public bool ShowFilePrefix =>
        AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
            (_parent.Library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);

    /// <summary>All traces in the same plot except the one that owns this marker.</summary>
    public IReadOnlyList<Trace> OtherTraces =>
        Container.PlotVM.Plot.Traces.Where(t => t != Trace).ToList();

    /// <summary>Every trace in this box's plot, in placement order — what
    /// <see cref="Trace.BuildMarkerBoxLines"/> needs to add the multi-marker rows and, for a
    /// contour marker, one row per contour trace. Null for a plain marker so the box keeps
    /// its single-trace shape.</summary>
    public IReadOnlyList<Trace>? PlotTraces =>
        Marker.IsMulti || Trace.IsContourTrace ? Container.PlotVM.Plot.Traces.ToList() : null;

    private void RefreshSize()
    {
        var (w, h) = MarkerRenderer.MeasureInfoBox(DisplayMarker, Trace, _getFreqUnit(), ShowFilePrefix, PlotTraces);
        SetLogicalSize(w, h);
    }

    public FreqUnit    FreqUnit => _getFreqUnit();
    public RenderTheme Theme    => _getTheme();
    public PlotType    PlotType => Container.PlotVM.Plot.PlotType;

    // When selection state changes, the marker symbol in the PlotControl must also redraw.
    protected override void OnSelectionChanged() => Container.RequestPlotRedraw();
}
