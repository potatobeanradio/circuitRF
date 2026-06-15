// ================================================================
//  PlotInspectorViewModel.cs  —  ViewModel for the Plot Properties panel
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class PlotInspectorViewModel : ViewModelBase
{
    // ---- Design-time instance (AXAML previewer) -------------------------
    //
    //  Usage in PlotInspectorView.axaml:
    //    <Design.DataContext>
    //        <x:Static Member="vm:PlotInspectorViewModel.DesignInstance"/>
    //    </Design.DataContext>
    //
    //  Two dummy traces give the previewer enough data to render traceCards.

    private static readonly Lazy<PlotInspectorViewModel> _designInstance =
        new(CreateDesignInstance);
    public static PlotInspectorViewModel DesignInstance => _designInstance.Value;

    private static PlotInspectorViewModel CreateDesignInstance()
    {
        // Build two dummy SNPs and traces so the previewer renders traceCards.
        // Signal pickers are empty (no library), but row layout is fully visible.
        var snp1 = new SNP(new[] { 1e9, 2e9, 3e9 }, 2, MatrixType.S, MatrixFormat.MA);
        var snp2 = new SNP(new[] { 1e9, 2e9, 3e9 }, 2, MatrixType.S, MatrixFormat.MA);
        snp1.FilePath = "design_dummy1.s2p";
        snp2.FilePath = "design_dummy2.s2p";

        // for testing Rect
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t1   = new Trace(snp1, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var t2   = new Trace(snp2, MatrixType.S, 1, 0, DependentVarFormat.Phase)
            { UseSecondaryAxis = true };
        t1.BuildPath(PlotType.Rect, FreqUnit.GHz);
        t2.BuildPath(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(t1);
        plot.Traces.Add(t2);

        // for testing Table
        // var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        // var t1   = new Trace(snp1, MatrixType.S, 0, 0, DependentVarFormat.Complex);
        // var t2   = new Trace(snp2, MatrixType.S, 1, 0, DependentVarFormat.Phase)
        //     { UseSecondaryAxis = false };
        // t1.BuildPath(PlotType.Table, FreqUnit.GHz);
        // t2.BuildPath(PlotType.Table, FreqUnit.GHz);
        // plot.Traces.Add(t1);
        // plot.Traces.Add(t2);



        return new PlotInspectorViewModel(plot, () => {}, library: null);
    }

    private readonly Plot                _plot;
    private readonly Action              _closeAction;
    private readonly SnpLibraryViewModel? _library;

    public event EventHandler? PlotNeedsRedraw;
    public event EventHandler? PlotStructureChanged;

    // ---- Static ItemsSource lists ---------------------------------------

    public static IReadOnlyList<PlotType>    AllPlotTypes   { get; } = Enum.GetValues<PlotType>().ToList();
    public static IReadOnlyList<FreqUnit>    AllFreqUnits   { get; } =
        new[] { FreqUnit.Hz, FreqUnit.kHz, FreqUnit.MHz, FreqUnit.GHz };
    public static IReadOnlyList<MatrixType>  AllMatrixTypes { get; } = Enum.GetValues<MatrixType>().ToList();
    public static IReadOnlyList<LineType>       AllLineTypes       { get; } = Enum.GetValues<LineType>().ToList();
    public static IReadOnlyList<PrecisionFormat> AllPrecisionFormats { get; } = Enum.GetValues<PrecisionFormat>().ToList();

    // Marker types as icon-bearing wrappers so the combo can display glyphs.
    public static IReadOnlyList<MarkerTypeItem> AllMarkerTypes { get; } = new[]
    {
        new MarkerTypeItem(MarkerType.Circle, MaterialIconKind.Circle),
        new MarkerTypeItem(MarkerType.Square, MaterialIconKind.Square),
    };

    // Merged line-mode options: [Off, Solid, Dashed, …] for the icon-pick.
    public static IReadOnlyList<LineModeItem> LineModes { get; } =
        new LineModeItem[] { new(true, default) }
        .Concat(Enum.GetValues<LineType>().Select(t => new LineModeItem(false, t)))
        .ToArray();

    // Merged symbol-mode options: [Off, Circle, Square] for the icon-pick.
    public static IReadOnlyList<SymbolModeItem> SymbolModes { get; } = new[]
    {
        new SymbolModeItem(true,  default,          MaterialIconKind.CircleOutline),
        new SymbolModeItem(false, MarkerType.Circle, MaterialIconKind.Circle),
        new SymbolModeItem(false, MarkerType.Square, MaterialIconKind.Square),
    };

    public static IReadOnlyList<ColorItem> ColorItems { get; } = BuildColorItems();

    private static IReadOnlyList<ColorItem> BuildColorItems()
    {
        var names = new Dictionary<int, string>
        {
            {  0, "Black"  }, {  1, "Blue"   }, {  2, "Brown"  }, {  3, "Clear" },
            {  4, "Cyan"   }, {  5, "Gray"   }, {  6, "Green"  }, {  7, "Indigo"},
            {  8, "Mint"   }, {  9, "Orange" }, { 10, "Pink"   }, { 11, "Purple"},
            { 12, "Red"    }, { 13, "Teal"   }, { 14, "White"  }, { 15, "Yellow"},
            { 16, "Accent" }
        };
        return TraceProperties.ColorLUT
            .OrderBy(kv => kv.Key)
            .Select(kv => new ColorItem(kv.Key, kv.Value, names.GetValueOrDefault(kv.Key, kv.Key.ToString())))
            .ToList();
    }

    // ---- Library access (for TraceRowViewModel) -------------------------

    /// <summary>Live collection of loaded SNP entries, forwarded to each trace row.</summary>
    public ObservableCollection<SnpEntryViewModel> LibraryEntries =>
        _library?.Entries ?? _emptyEntries;

    private static readonly ObservableCollection<SnpEntryViewModel> _emptyEntries = new();

    // ---- Plot-level properties ------------------------------------------

    [ObservableProperty]
    private PlotType _plotType;

    partial void OnPlotTypeChanged(PlotType value)
    {
        _plot.SetPlotType(value);
        RebuildTraces();
        OnPropertyChanged(nameof(IsRectPlot));
        OnPropertyChanged(nameof(IsSmithPlot));
        OnPropertyChanged(nameof(IsPolarPlot));
        OnPropertyChanged(nameof(IsTablePlot));
        OnPropertyChanged(nameof(InspectorTitle));
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    private FreqUnit _freqUnit;

    partial void OnFreqUnitChanged(FreqUnit value)
    {
        _plot.FreqUnits = value;
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRectPlot  => _plot.PlotType == PlotType.Rect;
    public bool IsSmithPlot => _plot.PlotType == PlotType.Smith;
    public bool IsPolarPlot => _plot.PlotType == PlotType.Polar;
    public bool IsTablePlot => _plot.PlotType == PlotType.Table;

    public string InspectorTitle => IsTablePlot ? "Table Properties" : "Plot Properties";

    public double FontSize
    {
        get => _plot.FontSize;
        set
        {
            if (_plot.FontSize == value) return;
            _plot.FontSize = value;
            OnPropertyChanged();
            PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Trace list -----------------------------------------------------

    public ObservableCollection<TraceRowViewModel> Traces { get; } = new();

    public bool CanAddTrace =>
        _plot.Traces.Count > 0 ||
        (_library?.Entries.Any(e => e.Snp is not null && !e.Snp.IsEmpty) ?? false);

    // ---- Commands -------------------------------------------------------

    public IRelayCommand AddTraceCommand { get; }
    public IRelayCommand CloseCommand    { get; }

    // Plot-type set commands (segmented header buttons, §A)
    public IRelayCommand SetPlotTypeRectCommand  { get; }
    public IRelayCommand SetPlotTypeSmithCommand { get; }
    public IRelayCommand SetPlotTypePolarCommand { get; }
    public IRelayCommand SetPlotTypeTableCommand { get; }

    // ---- Constructor ----------------------------------------------------

    public PlotInspectorViewModel(
        Plot                  plot,
        Action                closeAction,
        SnpLibraryViewModel?  library = null)
    {
        _plot        = plot;
        _closeAction = closeAction;
        _library     = library;

        _plotType = plot.PlotType;
        _freqUnit = plot.FreqUnits;

        RebuildTraces();

        AddTraceCommand = new RelayCommand(AddTrace, () => CanAddTrace);
        CloseCommand    = new RelayCommand(_closeAction);

        SetPlotTypeRectCommand  = new RelayCommand(() => PlotType = PlotType.Rect);
        SetPlotTypeSmithCommand = new RelayCommand(() => PlotType = PlotType.Smith);
        SetPlotTypePolarCommand = new RelayCommand(() => PlotType = PlotType.Polar);
        SetPlotTypeTableCommand = new RelayCommand(() => PlotType = PlotType.Table);

        if (_library != null)
        {
            _library.LibraryChanged += OnLibraryChanged;
            _library.Entries.CollectionChanged += (_, _) => RefreshAddCommand();
        }
    }

    // ---- Library event --------------------------------------------------

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        // Remove traces whose SNP is no longer present in the library.
        var librarySnps = new System.Collections.Generic.HashSet<SNP>(
            _library!.Entries.Select(entry => entry.Snp).OfType<SNP>());

        var staleVms = Traces
            .Where(rv => rv.Trace.Data is not null && !librarySnps.Contains(rv.Trace.Data))
            .ToList();

        foreach (var vm in staleVms)
        {
            vm.UnsubscribeFromLibrary();
            _plot.Traces.Remove(vm.Trace);
            Traces.Remove(vm);
        }

        // Rebuild paths for remaining traces (handles in-place reload/restore).
        foreach (var t in _plot.Traces)
            t.BuildPath(_plot.PlotType, _plot.FreqUnits);

        // Refresh signal ComboBoxes — needed when an entry is restored in-place
        // (no CollectionChanged fires in that case, only LibraryChanged).
        foreach (var vm in Traces)
            vm.RefreshDataSources();

        _plot.Autoscale();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
        RefreshAddCommand();
    }

    // ---- Trace management -----------------------------------------------

    private void RebuildTraces()
    {
        Traces.Clear();
        foreach (var t in _plot.Traces)
            Traces.Add(new TraceRowViewModel(t, this));
    }

    private void AddTrace()
    {
        Trace trace;

        if (_plot.Traces.Count > 0)
        {
            var src = _plot.Traces.Last();
            trace = new Trace(src, incrementColorBy: 1, includeMarkers: false);
        }
        else if (_library?.Entries.FirstOrDefault(e => e.Snp is not null && !e.Snp.IsEmpty) is { } firstReal)
        {
            var snp = firstReal.Snp!;
            bool isComplex = _plot.PlotType is PlotType.Smith or PlotType.Polar;
            trace = new Trace(
                snp, MatrixType.S, 0, 0,
                isComplex ? DependentVarFormat.Complex : DependentVarFormat.Db);
            trace.SourcePath = snp.FilePath;
        }
        else return;

        trace.BuildPath(_plot.PlotType, _plot.FreqUnits);
        _plot.Traces.Add(trace);
        _plot.Autoscale();
        Traces.Add(new TraceRowViewModel(trace, this));
        RefreshAddCommand();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveTrace(TraceRowViewModel vm)
    {
        vm.UnsubscribeFromLibrary();
        _plot.Traces.Remove(vm.Trace);
        _plot.Autoscale();
        Traces.Remove(vm);
        RefreshAddCommand();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RebuildAndNotify()
    {
        foreach (var t in _plot.Traces)
            t.BuildPath(_plot.PlotType, _plot.FreqUnits);
        _plot.Autoscale();
        foreach (var vm in Traces) vm.RefreshDescription();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when a trace's UseSecondaryAxis flag is toggled in place.
    /// Collection-Changed does not fire in this case, so we sync the
    /// secondary-axis state and viewport here explicitly.
    /// </summary>
    public void OnTraceSecondaryAxisChanged()
    {
        _plot.Axes.ShowSecondary = _plot.NeedsSecondary;
        // Always update viewport: moving a trace between axes shifts the left/right
        // trace counts, which changes the Rect margin widths.
        _plot.SetAxesViewport();
        _plot.Autoscale();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Notify() => PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);

    private void RefreshAddCommand()
    {
        OnPropertyChanged(nameof(CanAddTrace));
        ((RelayCommand)AddTraceCommand).NotifyCanExecuteChanged();
    }
}
