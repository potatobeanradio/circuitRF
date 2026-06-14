// ================================================================
//  TraceRowViewModel.cs  —  Observable wrapper for a single Trace
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class TraceRowViewModel : ViewModelBase
{
    private readonly Trace                  _trace;
    private readonly PlotInspectorViewModel _parent;

    // Prevents OnSelectedDataChanged from firing during construction and RebuildSignals.
    private bool _suppressDataCallback;

    public Trace Trace => _trace;

    // True only for Rect — used for the →R secondary-axis checkbox.
    public bool IsRectPlot => _parent.PlotType == PlotType.Rect;

    // True for Rect or Table — used to show/hide the YAxis format combo.
    public bool IsRectOrTablePlot => _parent.PlotType is PlotType.Rect or PlotType.Table;

    // True only for Table — used to show/hide per-trace number-format controls.
    public bool IsTablePlot    => _parent.PlotType == PlotType.Table;
    public bool IsNotTablePlot => _parent.PlotType != PlotType.Table;

    // ---- Combined data picker (replaces SNP source + Row + Col) ----------
    //
    //  One item per (SNP × matrix-element) plus derived-parameter items for
    //  2-port SNPs.  Rebuilt when MatrixType or the library contents change.
    //  Selection revert is avoided by NOT calling RebuildSignals from
    //  RefreshDescription (which is called from RebuildAndNotify).

    public ObservableCollection<TraceDataItem> AvailableSignals { get; } = new();

    [ObservableProperty]
    private TraceDataItem? _selectedSignal;

    partial void OnSelectedSignalChanged(TraceDataItem? value)
    {
        if (_suppressDataCallback || value == null) return;

        // When the flyout's ComboBox initializes its binding it writes the current
        // selection back to the VM, which would fire RebuildAndNotify → Autoscale.
        // Skip if the trace already matches — nothing actually changed.
        bool alreadyApplied = value.Derived != DerivedParameters.None
            ? (_trace.Data == value.Entry.Snp && _trace.Derived == value.Derived)
            : (_trace.Data == value.Entry.Snp && _trace.Row == value.Row
               && _trace.Col == value.Col && _trace.Derived == DerivedParameters.None);
        if (alreadyApplied) return;

        _trace.Data       = value.Entry.Snp;
        _trace.SourcePath = value.Entry.Snp.FilePath;

        if (value.Derived != DerivedParameters.None)
        {
            _trace.Derived = value.Derived;
        }
        else
        {
            _trace.Derived = DerivedParameters.None;
            _trace.Row     = value.Row;
            _trace.Col     = value.Col;
        }

        _parent.RebuildAndNotify();
    }

    // ---- Matrix type -------------------------------------------------------

    [ObservableProperty]
    private MatrixType _matrixType;

    partial void OnMatrixTypeChanged(MatrixType value)
    {
        _trace.MatrixType = value;
        RebuildSignals();          // labels change (S→Z etc.)
        _parent.RebuildAndNotify();
    }

    // ---- Y-axis format (Rect/Text only) ------------------------------------
    //
    //  AvailableYAxes is built once in the constructor for the current PlotType.
    //  Complex is disabled (but still shown) on Rect so the user can see why
    //  it is not selectable.

    public IReadOnlyList<YAxisItem> AvailableYAxes { get; }

    [ObservableProperty]
    private YAxisItem? _selectedYAxis;

    partial void OnSelectedYAxisChanged(YAxisItem? value)
    {
        if (value == null) return;
        _trace.YAxis = value.Format;
        _parent.RebuildAndNotify();
    }

    // ---- Secondary axis -----------------------------------------------------

    [ObservableProperty]
    private bool _useSecondaryAxis;

    partial void OnUseSecondaryAxisChanged(bool value)
    {
        _trace.UseSecondaryAxis = value;
        OnPropertyChanged(nameof(SecondaryAxisIcon));
        _parent.OnTraceSecondaryAxisChanged();
    }

    /// <summary>Icon that reflects current secondary-axis state.</summary>
    public MaterialIconKind SecondaryAxisIcon =>
        UseSecondaryAxis ? MaterialIconKind.ArrowRight : MaterialIconKind.ArrowLeft;

    public IRelayCommand ToggleSecondaryAxisCommand { get; }

    // ---- Z0 text entry (reference impedance) --------------------------------

    [ObservableProperty]
    private string _z0String = "50";

    partial void OnZ0StringChanged(string value)
    {
        if (ComplexStringHelper.TryParse(value, out Complex z0))
        {
            _trace.Z0 = z0;
            _parent.RebuildAndNotify();
        }
    }

    // ---- Line ---------------------------------------------------------------

    [ObservableProperty]
    private bool _lineEnabled;

    partial void OnLineEnabledChanged(bool value)
    {
        _trace.Properties.LineEnabled = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private double _lineWidth;

    partial void OnLineWidthChanged(double value)
    {
        _trace.Properties.LineWidth = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private LineType _lineType;

    partial void OnLineTypeChanged(LineType value)
    {
        _trace.Properties.LineType = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private int _lineColorIndex;

    partial void OnLineColorIndexChanged(int value)
    {
        _trace.Properties.LineColorStorage = null;
        _trace.Properties.LineColorIndex   = value;
        _parent.Notify();
    }

    // ---- Marker -------------------------------------------------------------

    [ObservableProperty]
    private bool _markerEnabled;

    partial void OnMarkerEnabledChanged(bool value)
    {
        _trace.Properties.MarkerEnabled = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private double _markerSize;

    partial void OnMarkerSizeChanged(double value)
    {
        _trace.Properties.MarkerSize = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private MarkerTypeItem? _selectedMarkerTypeItem;

    partial void OnSelectedMarkerTypeItemChanged(MarkerTypeItem? value)
    {
        if (value == null) return;
        _trace.Properties.MarkerType = value.Value;
        _parent.Notify();
    }

    [ObservableProperty]
    private int _markerColorIndex;

    partial void OnMarkerColorIndexChanged(int value)
    {
        _trace.Properties.MarkerColorStorage = null;
        _trace.Properties.MarkerColorIndex   = value;
        _parent.Notify();
    }

    // ---- Table number-format controls (Table plot only) --------------------

    [ObservableProperty]
    private PrecisionFormat _formatString;

    partial void OnFormatStringChanged(PrecisionFormat value)
    {
        _trace.FormatString = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private int _maximumFractionDigits;

    partial void OnMaximumFractionDigitsChanged(int value)
    {
        _trace.MaximumFractionDigits = value;
        _parent.Notify();
    }

    // ---- Cleanup ------------------------------------------------------------

    /// <summary>
    /// Removes the CollectionChanged subscription this VM added in its constructor.
    /// Call before discarding a TraceRowViewModel that wasn't removed by the user.
    /// </summary>
    internal void UnsubscribeFromLibrary()
    {
        _parent.LibraryEntries.CollectionChanged -= OnLibraryEntriesChanged;
    }

    // ---- Command ------------------------------------------------------------

    public IRelayCommand RemoveCommand { get; }

    // ---- Constructor --------------------------------------------------------

    public TraceRowViewModel(Trace trace, PlotInspectorViewModel parent)
    {
        _trace  = trace;
        _parent = parent;

        _matrixType       = trace.MatrixType;
        _useSecondaryAxis = trace.UseSecondaryAxis;

        _lineEnabled    = trace.Properties.LineEnabled;
        _lineWidth      = trace.Properties.LineWidth;
        _lineType       = trace.Properties.LineType;
        _lineColorIndex = trace.Properties.LineColorIndex;

        _markerEnabled    = trace.Properties.MarkerEnabled;
        _markerSize       = trace.Properties.MarkerSize;
        _markerColorIndex = trace.Properties.MarkerColorIndex;

        _formatString          = trace.FormatString;
        _maximumFractionDigits = trace.MaximumFractionDigits;

        _z0String = ComplexStringHelper.Format(trace.Z0);

        // Marker type via icon wrapper
        _selectedMarkerTypeItem = PlotInspectorViewModel.AllMarkerTypes
            .FirstOrDefault(m => m.Value == trace.Properties.MarkerType);

        RemoveCommand              = new RelayCommand(() => _parent.RemoveTrace(this));
        ToggleSecondaryAxisCommand = new RelayCommand(() => UseSecondaryAxis = !UseSecondaryAxis);

        // Build YAxis items once — plot type doesn't change within a VM lifetime
        // (RebuildTraces() creates fresh VMs on plot-type switch).
        bool isRect = parent.PlotType == PlotType.Rect;
        AvailableYAxes = Enum.GetValues<DependentVarFormat>()
            .Select(f => new YAxisItem(f, enabled: !(isRect && f == DependentVarFormat.Complex)))
            .ToList();
        _selectedYAxis = AvailableYAxes.FirstOrDefault(y => y.Format == trace.YAxis);

        // Build data picker list and select the item matching the current trace state.
        RebuildSignals();

        // Keep AvailableSignals fresh when the library collection changes.
        _parent.LibraryEntries.CollectionChanged += OnLibraryEntriesChanged;
    }

    // ---- Signal list management ---------------------------------------------

    private void OnLibraryEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildSignals();

    private void RebuildSignals()
    {
        AvailableSignals.Clear();

        bool isComplex    = _parent.PlotType is PlotType.Smith or PlotType.Polar;
        bool singleSource = _parent.LibraryEntries.Count == 1;

        foreach (var entry in _parent.LibraryEntries)
        {
            if (entry.Snp.IsEmpty)
            {
                // Broken entry: add a placeholder item for the trace's current row/col
                // so the ComboBox shows something (in red/italic) instead of going blank.
                bool isSource = _trace.Data == entry.Snp;
                int row = isSource ? _trace.Row : 0;
                int col = isSource ? _trace.Col : 0;
                AvailableSignals.Add(new TraceDataItem(entry, MatrixType, row, col,
                    singleSource, isBroken: true));
                continue;
            }

            int ports = entry.Snp.Ports;

            // If this entry is the trace's source and row/col is now out of range
            // (file was restored but is smaller than expected), prepend an OOB placeholder.
            if (_trace.Data == entry.Snp
                && _trace.Derived == DerivedParameters.None
                && (_trace.Row >= ports || _trace.Col >= ports))
            {
                AvailableSignals.Add(new TraceDataItem(entry, MatrixType,
                    _trace.Row, _trace.Col, singleSource, isBroken: true));
            }

            for (int r = 0; r < ports; r++)
                for (int c = 0; c < ports; c++)
                    AvailableSignals.Add(new TraceDataItem(entry, MatrixType, r, c, singleSource));

            if (ports == 2)
            {
                // All derived items are always listed; TraceDataItem.IsEnabled gates each one
                // by plot type (isComplex).  This prevents a stale derived param (e.g. a
                // SourceStabilityCircle trace saved while the plot was a Smith chart and then
                // loaded as a Table) from producing a null match and showing "(load a file...)".
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.SourceStabilityCircle, _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.LoadStabilityCircle,   _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.MuPrime, _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.Mu,      _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.MaxGain, _parent.PlotType, singleSource));
            }
        }

        // Select the item that best matches the current trace state.
        TraceDataItem? match = null;
        if (_trace.Data != null)
        {
            var matchEntry = _parent.LibraryEntries.FirstOrDefault(e => e.Snp == _trace.Data);
            if (matchEntry != null)
            {
                if (matchEntry.Snp.IsEmpty
                    || (_trace.Derived == DerivedParameters.None
                        && (_trace.Row >= matchEntry.Snp.Ports
                            || _trace.Col >= matchEntry.Snp.Ports)))
                {
                    // Broken or OOB — select the placeholder item.
                    match = AvailableSignals.FirstOrDefault(s => s.IsBroken && s.Entry == matchEntry);
                }
                else if (_trace.Derived != DerivedParameters.None)
                {
                    match = AvailableSignals.FirstOrDefault(s => s.Entry == matchEntry
                        && s.Derived == _trace.Derived);
                }
                else
                {
                    match = AvailableSignals.FirstOrDefault(s => s.Entry == matchEntry
                        && s.Row == _trace.Row && s.Col == _trace.Col
                        && s.Derived == DerivedParameters.None && !s.IsBroken);
                }
            }
        }

        _suppressDataCallback = true;
        SelectedSignal = match;
        _suppressDataCallback = false;
    }

    /// <summary>
    /// Called by PlotInspectorViewModel after any library change to refresh the
    /// signal list (e.g. when a broken entry is restored in-place).
    /// </summary>
    internal void RefreshDataSources() => RebuildSignals();

    // ---- Called by PlotInspectorViewModel after trace paths are rebuilt ----
    //
    //  Deliberately does NOT call RebuildSignals() — calling it here would
    //  clear the AvailableSignals collection in the middle of a callback chain
    //  that originated from OnSelectedSignalChanged, causing Avalonia's ComboBox
    //  to reset its SelectedItem to null (the revert bug).

    public void RefreshDescription()
    {
        OnPropertyChanged(nameof(IsRectPlot));
        OnPropertyChanged(nameof(IsRectOrTablePlot));
        OnPropertyChanged(nameof(IsTablePlot));
        OnPropertyChanged(nameof(IsNotTablePlot));
    }
}
