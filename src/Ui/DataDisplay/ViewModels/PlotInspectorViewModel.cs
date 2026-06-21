// ================================================================
//  PlotInspectorViewModel.cs  —  ViewModel for the Plot Properties panel
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
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
    private Action                       _closeAction;
    private readonly DataSourceLibraryViewModel? _library;

    public event EventHandler? PlotNeedsRedraw;
    public event EventHandler? PlotStructureChanged;

    // ---- Color-picker owner + suppress-flyout-dismiss seam (§3) -----------

    /// <summary>Injected by PlotControl when building the inspector flyout. Returns the main
    /// application Window so color-picker dialogs use the correct owner (not the PopupRoot).</summary>
    public Func<Window?>? GetOwnerWindow { get; set; }

    /// <summary>Raised by TraceRowViewModel before opening a color-picker dialog.</summary>
    public event EventHandler? ColorPickStarted;

    /// <summary>Raised by TraceRowViewModel after a color-picker dialog closes.</summary>
    public event EventHandler? ColorPickEnded;

    internal void RaiseColorPickStarted() => ColorPickStarted?.Invoke(this, EventArgs.Empty);
    internal void RaiseColorPickEnded()   => ColorPickEnded?.Invoke(this, EventArgs.Empty);

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

    // All cube transform options — every entry enabled (used for cube-bound traces).
    public static IReadOnlyList<CubeTransformItem> AllCubeTransforms { get; } =
        Enum.GetValues<CubeTransform>().Select(t => new CubeTransformItem(t)).ToList();

    // Transform options for network/S-param traces — dB10, dB, Conj are cube-only and disabled.
    public static IReadOnlyList<CubeTransformItem> AllTransformsForNetwork { get; } =
        Enum.GetValues<CubeTransform>()
            .Select(t => new CubeTransformItem(t,
                enabled: t is not CubeTransform.dB10 and not CubeTransform.dB and not CubeTransform.Conj))
            .ToList();

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

    /// <summary>The document datasource library (null in design mode).</summary>
    public DataSourceLibraryViewModel? Library => _library;

    /// <summary>Live collection of loaded data-source entries, forwarded to each trace row.</summary>
    public ObservableCollection<DataSourceEntryViewModel> LibraryEntries =>
        _library?.Entries ?? _emptyEntries;

    private static readonly ObservableCollection<DataSourceEntryViewModel> _emptyEntries = new();

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
        // Rebuild cube data and axis-role labels so harmonic pin options show the new unit.
        foreach (var vm in Traces) vm.OnFreqUnitChanged();
        RebuildAndNotify();
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
        (_library?.SelectedEntry is { } e && HasPlottableData(e, _plot.PlotType == PlotType.Table));

    /// <summary>True when an entry has anything a trace can be seeded from: a non-empty SNP
    /// (S-parameter network) OR at least one plottable cube (HB/DC/loadpull cube-only results).</summary>
    private static bool HasPlottableData(DataSourceEntryViewModel e, bool allowScalars) =>
        (e.Snp is not null && !e.Snp.IsEmpty) || FirstPlottableCubeName(e, allowScalars) is not null;

    /// <summary>Returns the name of the first plottable cube in the entry's DataSet, applying the
    /// same skip rules as the trace-signal picker (S/Z0, "__"-prefixed, Converged/Residual,
    /// node-indexed current). Rank-0 (scalar) cubes are included only when allowScalars is true.</summary>
    private static string? FirstPlottableCubeName(DataSourceEntryViewModel e, bool allowScalars = false)
    {
        if (e.Data is not { } ds) return null;
        foreach (var group in ds.Groups)
            foreach (var (bareName, cube) in ds.CubesIn(group))
            {
                if (bareName == "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                // Default-group S is owned by the network/SNP path (Touchstone); grouped S is a
                // simulated S cube offered as a first-class cube.
                if (bareName == "S" && group == DataSet.DefaultGroup) continue;
                if (bareName.EndsWith("Converged", StringComparison.Ordinal) ||
                    bareName.EndsWith("Residual",  StringComparison.Ordinal)) continue;
                if ((bareName == "I" || bareName == "INl") && cube.Axes.Any(a => a.Name == "node")) continue;
                if (cube.Rank == 0 && !allowScalars) continue;   // scalars are Table-only
                return group == DataSet.DefaultGroup ? bareName : $"{group}.{bareName}";
            }
        return null;
    }

    // ---- Commands -------------------------------------------------------

    public IRelayCommand AddTraceCommand        { get; }
    public IRelayCommand AddContourTraceCommand { get; }
    public IRelayCommand CloseCommand           { get; }

    // Plot-type set commands (segmented header buttons, §A)
    public IRelayCommand SetPlotTypeRectCommand  { get; }
    public IRelayCommand SetPlotTypeSmithCommand { get; }
    public IRelayCommand SetPlotTypePolarCommand { get; }
    public IRelayCommand SetPlotTypeTableCommand { get; }

    /// <summary>True when the selected data source is a loadpull result eligible for contour authoring.</summary>
    public bool CanAddContourTrace =>
        _library?.SelectedEntry is { } e && IsLoadpullSource(e);

    // ---- Constructor ----------------------------------------------------

    public PlotInspectorViewModel(
        Plot                  plot,
        Action                closeAction,
        DataSourceLibraryViewModel?  library = null)
    {
        _plot        = plot;
        _closeAction = closeAction;
        _library     = library;

        _plotType = plot.PlotType;
        _freqUnit = plot.FreqUnits;

        RebuildTraces();

        AddTraceCommand        = new RelayCommand(AddTrace,        () => CanAddTrace);
        AddContourTraceCommand = new RelayCommand(AddContourTrace, () => CanAddContourTrace);
        CloseCommand           = new RelayCommand(() => _closeAction());

        SetPlotTypeRectCommand  = new RelayCommand(() => PlotType = PlotType.Rect);
        SetPlotTypeSmithCommand = new RelayCommand(() => PlotType = PlotType.Smith);
        SetPlotTypePolarCommand = new RelayCommand(() => PlotType = PlotType.Polar);
        SetPlotTypeTableCommand = new RelayCommand(() => PlotType = PlotType.Table);

        if (_library != null)
        {
            _library.LibraryChanged            += OnLibraryChanged;
            _library.Entries.CollectionChanged += (_, _) => RefreshAddCommand();
            _library.SelectedDataSourceChanged += (_, _) => RefreshAddCommand();
        }
    }

    // ---- Close-action seam (flyout vs Properties pane) -----------------

    /// <summary>Points the shared inspector's Close button at the current flyout's Hide while it is
    /// open; call with a no-op on flyout close so a stale reference is never invoked.</summary>
    public void SetCloseAction(Action closeAction) => _closeAction = closeAction;

    // ---- Library event --------------------------------------------------

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        // Remove network-bound traces whose SNP is no longer in the library.
        var librarySnps = new System.Collections.Generic.HashSet<SNP>(
            _library!.Entries.Select(entry => entry.Snp).OfType<SNP>());

        // Also track current file paths for cube-bound stale detection.
        var libraryPaths = new System.Collections.Generic.HashSet<string>(
            _library.Entries.Select(e2 => e2.FilePath).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        var staleVms = Traces
            .Where(rv =>
            {
                var t = rv.Trace;
                // Contour traces are never stale here — their data lives in ContourData,
                // keyed by SourcePath, not by t.Data / librarySnps.
                if (t.IsContourTrace) return false;
                return t.IsCubeBound
                    ? t.SourcePath is null || !libraryPaths.Contains(t.SourcePath)
                    : t.Data is not null && !librarySnps.Contains(t.Data);
            })
            .ToList();

        foreach (var vm in staleVms)
        {
            vm.UnsubscribeFromLibrary();
            _plot.Traces.Remove(vm.Trace);
            Traces.Remove(vm);
        }

        // Rebuild paths for remaining traces (handles in-place reload/restore).
        foreach (var t in _plot.Traces)
        {
            if (t.IsCubeBound)
            {
                ReseedSliceIfCubeShapeChanged(t, _library);
                TrySetCubeData(t, _library, _plot.PlotType, _plot.FreqUnits);
            }
            else
                t.BuildPath(_plot.PlotType, _plot.FreqUnits);
        }

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
            trace.SourceRef = src.SourceRef;
        }
        else if (_library?.SelectedEntry is { } sel && sel.Snp is not null && !sel.Snp.IsEmpty)
        {
            var snp = sel.Snp!;
            bool isComplex = _plot.PlotType is PlotType.Smith or PlotType.Polar;
            trace = new Trace(
                snp, MatrixType.S, 0, 0,
                isComplex ? DependentVarFormat.Complex : DependentVarFormat.Db);
            trace.SourceRef  = DataSourceRef.Selected;
            trace.SourcePath = _library.SelectedDataSourceAbs;
        }
        else if (_library?.SelectedEntry is { } firstCube &&
                 FirstPlottableCubeName(firstCube, _plot.PlotType == PlotType.Table) is not null)
        {
            // Cube-only source (HB / DC / loadpull result — no S network). Seed a cube-bound trace
            // on the first plottable cube with a default slice (axis 0 = X, the rest pinned at 0).
            trace = BuildSeedCubeTrace(firstCube);
            trace.SourceRef  = DataSourceRef.Selected;
            trace.SourcePath = _library.SelectedDataSourceAbs;
        }
        else return;

        trace.BuildPath(_plot.PlotType, _plot.FreqUnits);
        if (trace.IsCubeBound)
            TrySetCubeData(trace, _library, _plot.PlotType, _plot.FreqUnits);
        _plot.Traces.Add(trace);
        _plot.Autoscale();
        Traces.Add(new TraceRowViewModel(trace, this));
        RefreshAddCommand();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Builds a cube-bound seed trace on the entry's first plottable cube, with a default slice
    /// (axis 0 = KeepAsX, remaining axes pinned at index 0, labels carried for quoted net names).
    /// Mirrors the default-slice construction in TraceRowViewModel.RebuildSignals.
    /// </summary>
    private Trace BuildSeedCubeTrace(DataSourceEntryViewModel entry)
    {
        string cubeName = FirstPlottableCubeName(entry, _plot.PlotType == PlotType.Table)!;
        var    cube     = entry.Data![cubeName];
        int    rank     = cube.Rank;

        // Trace requires an SNP; use a 1-point placeholder (cube path ignores it).
        var trace = new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0,
                              DependentVarFormat.Db);
        trace.SourcePath = entry.FilePath;
        trace.CubeName   = cubeName;

        if (rank == 0)   // scalar: empty slice, bare-name Expression
        {
            trace.Slice      = Array.Empty<AxisSlice>();
            trace.Expression = trace.BuildPickerExpression();      // → bare CubeName (Part A5)
            return trace;
        }

        // Default slice: freq → X when present (S/Y/Z parameter cubes and freq-swept cubes), else the
        // first non-label axis; every other axis pinned at index 0. For an S cube [freq, i, j] (+ optional
        // swept prefix) this yields S(1,1) over frequency with the sweep pinned — the user promotes the
        // sweep to Family or repins i/j via the axis-role editor.
        trace.Slice = TraceRowViewModel.BuildDefaultSlice(cube);

        // First-add nicety on Rect: complex cubes would render <invalid>. S/Y/Z parameter cubes default to
        // dB20 (the natural S-parameter view); other complex cubes to mag(). Seed-time only.
        if (_plot.PlotType == PlotType.Rect && cube.DataKind == DataKind.Complex)
            trace.Transform = TraceRowViewModel.IsParameterCube(cube) ? CubeTransform.dB20 : CubeTransform.Mag;

        trace.Expression = trace.BuildPickerExpression();
        return trace;
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
        {
            if (t.IsCubeBound)
                TrySetCubeData(t, _library, _plot.PlotType, _plot.FreqUnits);
            else
            {
                // Keep per-port Z0 fresh on network-bound traces (handles in-place reload).
                RefreshSourceZ0(t, _library);
                t.BuildPath(_plot.PlotType, _plot.FreqUnits);
            }
        }
        _plot.Autoscale();
        foreach (var vm in Traces) vm.RefreshDescription();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads the source DataSet's Z0 cube and stamps SourceZ0PerPort / SourceZ0IsUnusual
    /// on the trace.  No-op for cube-bound traces (handled by TrySetCubeData path).
    /// </summary>
    private void RefreshSourceZ0(Trace t, DataSourceLibraryViewModel? library)
    {
        if (library is null || t.SourcePath is null) return;

        var entry = library.Entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, t.SourcePath, StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
            TraceRowViewModel.StampSourceZ0OnTrace(t, entry);
    }

    // ---- Cube data resolution (Phase 7.2c-a) ----------------------------

    /// <summary>
    /// When a re-run changes a single-cube trace's bound cube to a different axis-name set OR a
    /// different axis ORDER (added/removed/reordered sweep axis), re-derive the slice from the trace's
    /// shape-independent spec text so the plot adopts the new dimensions. Re-parsing e.g. "Ids"
    /// against the new cube restores its natural default (a family when ≥2 axes are kept, innermost =
    /// X), instead of keeping a stale X or pinning a reappearing axis. t.Expression is left untouched
    /// so it stays shape-independent across reshapes. A pure value/point-count re-run (same names AND
    /// order) is skipped so the user's role choices and pins survive.
    /// </summary>
    private static void ReseedSliceIfCubeShapeChanged(Trace t, DataSourceLibraryViewModel? library)
    {
        if (t.Expression is not { } spec) return;                  // nothing shape-independent to re-derive from
        if (t.CubeName is null || t.Slice is null) return;         // multi-cube expr → handled by TraceExpression

        var entry = library?.Entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, t.SourcePath, StringComparison.OrdinalIgnoreCase));
        var ds = entry?.Data;
        if (ds is null || !ds.Contains(t.CubeName)) return;

        var cube = ds[t.CubeName];
        // Compare axis NAMES in ORDER. The slice is always built in cube-axis order, so reordering a
        // parametric sweep inner↔outer is detected here even though the name SET is unchanged — that
        // reorder flips which axis is innermost (the default plot X). A value/point-count re-run keeps
        // the same names+order → skip and preserve the user's slice exactly.
        var cubeOrder  = cube.Axes.Select(a => a.Name);
        var sliceOrder = t.Slice.Select(s => s.AxisName);
        if (cubeOrder.SequenceEqual(sliceOrder, StringComparer.Ordinal)) return;

        // Structure or order changed. Re-parse the authored spec against the new cube.
        if (CubeTraceSpecParser.TryParse(spec, ds, out var cn, out var sl, out var tf, out _)
            && sl is not null)
        {
            t.CubeName        = cn;
            t.Slice           = sl;
            t.Transform       = tf;
            t.InvalidSpecText = null;
            t.ExpressionError = null;
            // Deliberately do NOT regenerate t.Expression — it must stay shape-independent so the
            // next reshape re-parses the same authored text (e.g. bare "Ids" stays a family).
        }
        else
        {
            // Spec can't apply to the new shape (e.g. an explicitly pinned axis vanished) — best-effort carry.
            t.Slice      = TraceRowViewModel.BuildCarriedSliceFromCube(cube, t.Slice);
            t.Expression = t.BuildPickerExpression();
        }
    }

    /// <summary>
    /// Resolves a cube-bound trace's SourcePath+CubeName+Slice against the library,
    /// slices the DataCube to 1-D, and calls Trace.SetCubeData.
    /// If resolution fails (missing entry, missing cube, wrong rank), Points are cleared.
    /// Static so DataDisplayViewModel can call it during load before an inspector exists.
    /// </summary>
    internal static void TrySetCubeData(Trace t, DataSourceLibraryViewModel? library,
                                        PlotType plotType, FreqUnit freqUnit)
    {
        if (!t.IsCubeBound) return;

        DataSourceEntryViewModel? entry = null;
        if (library is not null && t.SourcePath is not null)
        {
            entry = library.Entries.FirstOrDefault(e =>
                string.Equals(e.FilePath, t.SourcePath, StringComparison.OrdinalIgnoreCase));
        }

        DataSet? ds = entry?.Data;

        // Single-cube specs (picker or typed Name[...]) resolve via the slice path (family-aware).
        // Only multi-cube element-wise expressions go through TraceExpression.
        bool singleCube = t.CubeName is not null && t.Slice is not null;

        // ── Expression path (element-wise multi-cube expressions) ─────────────
        if (t.Expression is not null && !singleCube)
        {
            if (ds is null)
            {
                t.Points.Clear();
                return;
            }
            if (TraceExpression.TryEvaluate(t.Expression, ds, plotType,
                    out var xVals, out var cz, out var rz,
                    out var xName, out var xUnit, out var exprErr))
            {
                t.ExpressionError = null;
                t.InvalidSpecText = null;
                t.SetCubeData(xVals, cz, rz, xName, xUnit, plotType, freqUnit);
            }
            else
            {
                t.ExpressionError = exprErr;
                t.InvalidSpecText = t.Expression;
                t.Points.Clear();
            }
            return;
        }

        // ── Single-slice path (picker-authored, no Expression) ────────────────
        if (ds is null || t.CubeName is null || !ds.Contains(t.CubeName))
        {
            // Cube unavailable in the (new) source → surface "<spec> <invalid>" on the label rather
            // than a silently-empty trace. Cleared below once the cube resolves again.
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();   // family geometry must vanish too (and stop feeding Autoscale)
            return;
        }

        var cube  = ds[t.CubeName];
        var slice = t.Slice;
        if (slice is null)
        {
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // Cube + slice resolved → clear any stale invalid flag left by a prior bad source
        // (covers the scalar, family, all-pinned, and rank-1 success paths below).
        t.InvalidSpecText = null;
        t.ExpressionError = null;

        // Scalar cube (rank 0): operating-point value — valid only on a Table (Part A).
        if (cube.Rank == 0)
        {
            var sr = cube[Array.Empty<object>()];
            t.InvalidSpecText = null;
            t.ExpressionError = null;
            t.SetScalarCubeData(
                sr.IsComplex ? sr.ComplexValue : (System.Numerics.Complex?)null,
                sr.IsReal    ? sr.RealValue    : (double?)null,
                plotType, freqUnit);
            return;
        }

        // ── Family path (Phase 7.3b) ──────────────────────────────────────────
        if (Array.Exists(slice, s => s.Role == AxisRole.FamilyIterate))
        {
            ResolveFamily(t, cube, slice, plotType, freqUnit);
            return;
        }

        // Build indexer args matched by axis NAME (Phase 7.3a: order-independent).
        // Missing slice entries default to PinToIndex/0; extra slice entries are ignored.
        var args = new object[cube.Rank];
        int xDim = -1;
        for (int d = 0; d < cube.Rank; d++)
        {
            var axName = cube.Axes[d].Name;
            AxisSlice? found = null;
            foreach (var s in slice)
            {
                if (s.AxisName == axName) { found = s; break; }
            }
            if (found?.Role == AxisRole.KeepAsX)
            {
                args[d] = found.Value.IsNarrowedRange
                    ? new Range(found.Value.RangeStart, found.Value.RangeEndExclusive)
                    : Range.All;
                xDim = d;
            }
            else
            {
                int idx = Math.Clamp(found?.Index ?? 0, 0, Math.Max(0, cube.Axes[d].Length - 1));
                args[d] = idx;
            }
        }

        // No axis is X → every axis is pinned → scalar (operating-point value).
        // Renders on a Table; <invalid> on Rect/Smith/Polar (handled by SetScalarCubeData).
        if (xDim < 0)
        {
            var sr = cube[args];
            t.InvalidSpecText = null;
            t.ExpressionError = null;
            t.SetScalarCubeData(
                sr.IsComplex ? sr.ComplexValue : (System.Numerics.Complex?)null,
                sr.IsReal    ? sr.RealValue    : (double?)null,
                plotType, freqUnit);
            return;
        }

        var result = cube[args];
        if (!result.IsCube)
        {
            t.Points.Clear();
            return;
        }

        var sliced = result.Cube!;
        if (sliced.Rank != 1)
        {
            t.Points.Clear();
            return;
        }

        var xAxis = sliced.Axes[0];
        Complex[]? complexValues = sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null;
        double[]?  realValues    = sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null;

        t.SetCubeData(xAxis.Values, complexValues, realValues,
                      xAxis.Name, xAxis.Unit, plotType, freqUnit);
    }

    private static void ResolveFamily(Trace t, DataCube cube, AxisSlice[] slice,
                                      PlotType plotType, FreqUnit freqUnit)
    {
        // Find family and X axes by name (slice is name-keyed, order-independent).
        int fDim = -1, xDim = -1;
        for (int d = 0; d < cube.Axes.Count; d++)
        {
            var axName = cube.Axes[d].Name;
            foreach (var s in slice)
            {
                if (s.AxisName == axName)
                {
                    if (s.Role == AxisRole.FamilyIterate) fDim = d;
                    else if (s.Role == AxisRole.KeepAsX)  xDim = d;
                    break;
                }
            }
        }
        if (fDim < 0 || xDim < 0) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }

        var fAxis = cube.Axes[fDim];
        int count = Math.Min(fAxis.Length, Trace.MaxFamilyCurves);

        double[]? xVals = null; string xName = ""; string? xUnit = null;
        var curves = new List<(double, string?, System.Numerics.Complex[]?, double[]?)>(count);

        for (int k = 0; k < count; k++)
        {
            var args = new object[cube.Rank];
            for (int d = 0; d < cube.Rank; d++)
            {
                var ax = cube.Axes[d];
                AxisSlice s = default;
                foreach (var sl in slice) { if (sl.AxisName == ax.Name) { s = sl; break; } }
                if (s.Role == AxisRole.FamilyIterate)     args[d] = k;
                else if (s.Role == AxisRole.KeepAsX)
                    args[d] = s.IsNarrowedRange ? new Range(s.RangeStart, s.RangeEndExclusive) : Range.All;
                else args[d] = Math.Clamp(s.Index, 0, Math.Max(0, ax.Length - 1));
            }
            var res = cube[args];
            if (!res.IsCube || res.Cube!.Rank != 1) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }
            var sliced = res.Cube!;
            if (xVals is null)
            {
                var xa = sliced.Axes[0];
                xVals = xa.Values;
                xName = xa.Name;
                xUnit = string.IsNullOrEmpty(xa.Unit) ? null : xa.Unit;
            }
            curves.Add((fAxis.Values[k],
                        fAxis.Labels is { } L && k < L.Length ? L[k] : null,
                        sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null,
                        sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null));
        }
        if (xVals is null) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }
        t.SetFamilyData(xVals, xName, xUnit, fAxis.Name, curves, plotType, freqUnit);
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
        OnPropertyChanged(nameof(CanAddContourTrace));
        ((RelayCommand)AddContourTraceCommand).NotifyCanExecuteChanged();
    }

    /// <summary>True when the entry has a loadpull DataSet (contains a GammaLoad cube).</summary>
    private static bool IsLoadpullSource(DataSourceEntryViewModel e) =>
        e.Data is { } ds && ds.Groups.Any(g => ds.CubesIn(g).ContainsKey("GammaLoad"));

    private void AddContourTrace()
    {
        var entry = _library?.SelectedEntry;
        if (entry?.Data is null) return;

        var placeholder = new SNP(new double[] { 1e9 }, 1);
        var trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = DataSourceRef.Selected;
        trace.SourcePath = _library!.SelectedDataSourceAbs;

        var plane = (_plot.PlotType is PlotType.Smith or PlotType.Polar)
            ? SurfacePlane.Gamma : SurfacePlane.Z;

        // §4: inherit colormap from the most-recent contour trace, or use the default.
        var lastContourColorMap = _plot.Traces
            .Select(t => t.ContourData)
            .OfType<ContourData>()
            .LastOrDefault()?.ColorMap ?? ContourColorMap.Bone;

        trace.ContourData = new ContourData
        {
            LevelMode       = ContourLevelMode.Count,
            ShowFill        = ContourDefaults.ShowFillDefault(plane),
            DisplayMxp      = true,
            DisplayMxe      = true,
            FadeLineOpacity = (plane == SurfacePlane.Gamma),
            ColorMap        = lastContourColorMap,          // §4
            DrawLabels      = (plane == SurfacePlane.Z),   // §13
        };

        _plot.Traces.Add(trace);
        Traces.Add(new TraceRowViewModel(trace, this));
        // Re-autoscale after RebuildContour() in the VM ctor has populated the grid.
        _plot.Autoscale(force: true);
        RefreshAddCommand();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }
}
