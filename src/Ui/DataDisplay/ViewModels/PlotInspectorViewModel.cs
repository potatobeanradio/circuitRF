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
    private bool                         _harmonicWarned;

    public event EventHandler? PlotNeedsRedraw;
    public event EventHandler? PlotStructureChanged;

    /// <summary>
    /// Raises <see cref="PlotStructureChanged"/> from outside this class — used when a
    /// TraceRowViewModel commits a genuine change of which SOURCE a trace is bound to (the picker's
    /// Source selector, R-dd-2, or a drag-dropped dataset, R-dd-3). Switching quantity/matrix-type
    /// within the SAME source only needs a redraw (<c>RebuildAndNotify</c>'s own <c>PlotNeedsRedraw</c>),
    /// but switching SOURCE can change whether the plot's traces span 1 or 2+ distinct datasets —
    /// exactly the input `TraceLabeler.ComputeMinimalLabels`'s alias-qualification decision depends
    /// on — and that recompute only happens in `PlotContainerViewModel.UpdateLabelStrips`, which is
    /// wired to this event, not to `PlotNeedsRedraw`.
    /// </summary>
    public void NotifyStructureChanged() => PlotStructureChanged?.Invoke(this, EventArgs.Empty);

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
    public static IReadOnlyList<TableOptimum>    AllTableOptima    { get; } = Enum.GetValues<TableOptimum>().ToList();
    public static IReadOnlyList<TableReadMode>   AllTableReadModes { get; } = Enum.GetValues<TableReadMode>().ToList();

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
        var oldType = _plot.PlotType;
        var oldVms  = Traces.ToList();

        // Leaving Table: Plot.SetPlotType narrows the deletion to what genuinely cannot exist
        // elsewhere (summary columns, scalar cubes — brief-dd-plot-type-integrity.md §1); everything
        // else survives and is remapped. Mirrors RemoveTrace's own cleanup for whatever didn't
        // survive — RebuildTraces() below discards every VM wrapper regardless (as it always has for
        // any plot-type change), but only a trace actually gone from _plot.Traces needs unsubscribing.
        _plot.SetPlotType(value);
        if (oldType == PlotType.Table && value != PlotType.Table && oldVms.Count > 0)
        {
            var surviving = new HashSet<Trace>(_plot.Traces);
            foreach (var vm in oldVms)
                if (!surviving.Contains(vm.Trace))
                    vm.UnsubscribeFromLibrary();
        }

        RebuildTraces();
        OnPropertyChanged(nameof(IsRectPlot));
        OnPropertyChanged(nameof(IsSmithPlot));
        OnPropertyChanged(nameof(IsPolarPlot));
        OnPropertyChanged(nameof(IsTablePlot));
        OnPropertyChanged(nameof(IsSummaryTable));
        OnPropertyChanged(nameof(AddLoadpullTraceLabel));
        OnPropertyChanged(nameof(IsSummaryAddMode));
        OnPropertyChanged(nameof(InspectorTitle));
        if (IsSummaryTable) RebuildSummary();
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

    /// <summary>True when this Table contains summary columns — gates the summary header controls.</summary>
    public bool IsSummaryTable => _plot.PlotType == PlotType.Table
        && _plot.Traces.Any(t => t.IsSummaryColumn);

    [ObservableProperty] private TableOptimum  _tableOptimum;
    [ObservableProperty] private TableReadMode _tableReadMode;

    partial void OnTableOptimumChanged(TableOptimum value)
    {
        _plot.TableOptimum = value;
        OnPropertyChanged(nameof(IsTableOptimumMxp));
        OnPropertyChanged(nameof(IsTableOptimumMxe));
        RebuildSummary();
    }

    // ── MXP / MXE segmented selector (replaces the Load combobox) ──────────────
    public bool IsTableOptimumMxp => TableOptimum == TableOptimum.Mxp;
    public bool IsTableOptimumMxe => TableOptimum == TableOptimum.Mxe;

    [RelayCommand] private void SetTableOptimumMxp() => TableOptimum = TableOptimum.Mxp;
    [RelayCommand] private void SetTableOptimumMxe() => TableOptimum = TableOptimum.Mxe;

    // ── Summary loadpull-analysis picker (mirrors the contour card's analysis picker) ──
    private bool _suppressSummaryAnalysisSync;

    public ObservableCollection<string> SummaryAvailableAnalyses { get; } = new();

    [ObservableProperty] private string? _summarySelectedAnalysis;

    /// <summary>Show the summary's analysis picker only when the source carries more than one loadpull
    /// view (e.g. a run.npy with both a standalone Loadpull and a Loadpull-Pursuit follow-on).</summary>
    public bool ShowSummaryAnalysisPicker => IsSummaryTable && SummaryAvailableAnalyses.Count > 1;

    partial void OnSummarySelectedAnalysisChanged(string? value)
    {
        if (_suppressSummaryAnalysisSync) return;
        _plot.SummaryLoadpullGroup = value;
        RebuildSummary();
    }

    partial void OnTableReadModeChanged(TableReadMode value)
    {
        _plot.TableReadMode = value;
        RebuildSummary();
        OnPropertyChanged(nameof(IsInterp));
    }

    /// <summary>Checkbox-friendly view of TableReadMode: true = Interp, false = Nearest.</summary>
    public bool IsInterp
    {
        get => TableReadMode == TableReadMode.Interp;
        set
        {
            var target = value ? TableReadMode.Interp : TableReadMode.Nearest;
            if (TableReadMode == target) return;
            TableReadMode = target;            // setter → OnTableReadModeChanged → RebuildSummary
            OnPropertyChanged();
        }
    }

    public double TableCompression
    {
        get => _plot.TableCompression;
        set
        {
            if (Math.Abs(_plot.TableCompression - value) < 1e-9) return;
            _plot.TableCompression = value;
            OnPropertyChanged();
            RebuildSummary();
        }
    }

    /// <summary>Label for the loadpull add-trace button: "+ Summary" on a Table, "+ Contour" otherwise.</summary>
    public string AddLoadpullTraceLabel => _plot.PlotType == PlotType.Table ? "+ Summary" : "+ Contour";

    /// <summary>True when the loadpull add button should add a summary column (Table) vs a contour (Smith/Polar/Rect).</summary>
    public bool IsSummaryAddMode => _plot.PlotType == PlotType.Table;

    public string InspectorTitle => IsTablePlot ? "Table Properties" : "Plot Properties";

    public double FontSize
    {
        get => _plot.FontSize;
        set
        {
            // Fail gracefully on invalid input (empty/garbled NUD text can push NaN): ignore non-finite
            // values and clamp to the supported range so the table can never be driven to a broken size.
            if (double.IsNaN(value) || double.IsInfinity(value)) { OnPropertyChanged(); return; }
            double clamped = Math.Clamp(value, 6.0, 32.0);
            if (_plot.FontSize == clamped) { if (value != clamped) OnPropertyChanged(); return; }
            _plot.FontSize = clamped;
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
    /// (S-parameter network) OR at least one plottable cube (HB/DC/loadpull cube-only results).
    /// Internal (not private) so WorkspaceViewModel's auto-created-Data-Display flow (R-res-10) can pick
    /// Rect vs. Table for the default plot without duplicating the plottability rules.</summary>
    internal static bool HasPlottableData(DataSourceEntryViewModel e, bool allowScalars) =>
        (e.Snp is not null && !e.Snp.IsEmpty) || FirstPlottableCubeName(e, allowScalars) is not null;

    /// <summary>Returns the name of the best cube to seed a trace on, applying the
    /// same skip rules as the trace-signal picker (S/Z0, "__"-prefixed, Converged/Residual,
    /// node-indexed current). Rank-0 (scalar) cubes are included only when allowScalars is true.
    ///
    /// <para><b>Best, not merely first</b> (owner, 2026-08-18): an HB run's first cube is <c>V</c>,
    /// indexed by node AND harmonic, so the trace a run auto-seeded was "the voltage at some node at
    /// some harmonic" — almost never what a designer who wrote a page of measurement expressions wants
    /// to look at first, and a poor thing to start customizing from. A MEASUREMENT is preferred
    /// whenever the run produced one.</para>
    ///
    /// <para><b>Which measurement: the first REAL one, in declaration order.</b> Enumeration order is
    /// the order the designer wrote them in, which is the only opinion the dataset carries about which
    /// one matters — for the shipped <c>FET_Harmonic_Balance_Sweep</c> template that is
    /// <c>Pin_avail_dBm</c>, a plain line against the swept drive (the owner's own choice, and for the
    /// stated reason: it is immediately readable). Real before complex because a complex cube needs a
    /// transform picked for it before it renders as anything. This is a starting point, not a guess at
    /// the user's intent — every other cube is one click away in the trace picker.</para>
    ///
    /// <para>A run with no measurements at all (DC, a bare HB, an imported Touchstone) seeds exactly
    /// what it seeded before.</para></summary>
    private static string? FirstPlottableCubeName(DataSourceEntryViewModel e, bool allowScalars = false)
    {
        if (e.Data is not { } ds) return null;

        string? firstAny          = null;   // today's answer, kept as the fallback
        string? firstMeasurement  = null;   // a complex measurement — better than raw, worse than a real one
        string? firstProbeCurrent = null;   // an IProbe the designer PLACED — see ProbeNames
        var     probeNames        = ProbeNames(ds);

        foreach (var group in ds.Groups)
            foreach (var (bareName, cube) in ds.CubesIn(group))
            {
                if (bareName == "Z0" || bareName == "ToneFreqs" || bareName == "MetaMixOrder" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                // Default-group S is owned by the network/SNP path (Touchstone); grouped S is a
                // simulated S cube offered as a first-class cube.
                if (bareName == "S" && group == DataSet.DefaultGroup) continue;
                if (bareName.EndsWith("Converged", StringComparison.Ordinal) ||
                    bareName.EndsWith("Residual",  StringComparison.Ordinal)) continue;
                if ((bareName == "I" || bareName == "INl") && cube.Axes.Any(a => a.Name == "node")) continue;
                if (cube.Rank == 0 && !allowScalars) continue;   // scalars are Table-only

                // Default- AND measurements-group cubes are emitted BARE — they bare-resolve
                // (DataSet.Resolve tries both), and the same rule is already what the trace picker
                // (TraceRowViewModel.RebuildSignals) and the expression parser (TraceExpression) use, so
                // a seeded trace reads `Pin_avail_dBm` exactly like a typed or picked one. Owner,
                // 2026-08-18: a `measurements.` prefix in the expression box is noise the user never
                // needs to type. Analysis cubes must stay qualified — bare `V` resolves to the wrong
                // group.
                string spec = group is DataSet.DefaultGroup or DataSet.MeasurementsGroup
                    ? bareName
                    : $"{group}.{bareName}";
                firstAny ??= spec;

                if (IsProbeCurrent(cube, probeNames)) firstProbeCurrent ??= spec;

                // The GROUP is the discriminator, not the shape or the name: a run's measurement cubes
                // are filed under DataSet.MeasurementsGroup by the run service and survive the `.npy`
                // there (verified against a real exported run, not assumed). Guessing from the axes
                // would also catch a raw cube that happens to have been reduced to the sweep.
                if (group != DataSet.MeasurementsGroup) continue;

                if (cube.DataKind == DataKind.Real) return spec;
                firstMeasurement ??= spec;
            }

        return firstMeasurement ?? firstProbeCurrent ?? firstAny;
    }

    /// <summary>
    /// The names of the current probes the designer PLACED in the schematic, as the run recorded them
    /// (<c>__ProbeBranches</c>, written by both the DC packer and the HB engine); empty when the run has
    /// none.
    /// </summary>
    private static string[] ProbeNames(DataSet ds)
    {
        foreach (var group in ds.Groups)
            foreach (var (bareName, cube) in ds.CubesIn(group))
                if (bareName == "__ProbeBranches" && cube.Rank == 1 && cube.Axes[0].Labels is { Length: > 0 } labels)
                    return labels;
        return [];
    }

    /// <summary>
    /// Whether <paramref name="cube"/> is the current through the designer's own probes — a branch axis
    /// whose labels are exactly the placed <c>IProbe</c>s.
    ///
    /// <para><b>Why the labels must MATCH the probe list rather than merely exist</b> (owner, 2026-08-18:
    /// "there is a probe usually called IDS or IP1"): a DC run's branch axis IS the probe list, so a curve
    /// tracer's <c>I</c> cube is exactly the quantity the user placed a probe to see — and preferring it
    /// over <c>V</c> is the same argument as preferring a measurement, since both are things the designer
    /// explicitly asked to observe. An HB run's branch axis is NOT that list: it enumerates every device
    /// branch (<c>M1:g</c>, <c>M1:d</c>, …), and seeding one of those over a node voltage would be a
    /// change with nothing behind it. Comparing against <c>__ProbeBranches</c> tells the two apart
    /// exactly, with no rule about which analysis produced the cube.</para>
    /// </summary>
    private static bool IsProbeCurrent(DataCube cube, string[] probeNames)
    {
        if (probeNames.Length == 0) return false;

        var branch = cube.Axes.FirstOrDefault(a => a.Name == "branch");
        return branch?.Labels is { } labels && labels.SequenceEqual(probeNames);
    }

    /// <summary>
    /// Whether an axis indexes a STRUCTURAL element — which node, which branch, which harmonic, which
    /// port, which matrix row/column — rather than a condition the run swept.
    ///
    /// <para>The distinction is what lets a seeded trace tell "the sweep" from "the circuit": a sweep
    /// axis is something to plot ALONG or iterate a family over, a structural axis is something to pin
    /// and let the user repick. <c>freq</c> is deliberately absent — it is a swept condition, and the
    /// preferred X when a cube has one.</para>
    /// </summary>
    private static bool IsStructuralAxis(string name) =>
        name is "node" or "branch" or "harmonic" or "tone" or "port" or "probe" or "mixIndex" or "i" or "j";

    /// <summary>
    /// The slice a seeded cube trace opens with. <see cref="TraceRowViewModel.BuildDefaultSlice"/>'s
    /// answer — first non-structural axis → X, everything else pinned at index 0 — except for the one
    /// case that answer gets backwards: a cube with TWO OR MORE swept axes and no frequency.
    ///
    /// <para><b>The case (owner, 2026-08-18): a curve tracer.</b> A DC analysis swept over VDS and then
    /// VGS produces <c>DC1.I [VGS × VDS × branch]</c> — each <c>parametric_sweep</c> nesting level
    /// PREPENDS its axis, so the OUTERMOST sweep is axis 0 and the innermost is last. The default slice
    /// therefore makes VGS the X axis and pins VDS at its first value: drain current against the GATE
    /// voltage at VDS = 0, which is a flat line. What every one of those runs exists to produce is
    /// <c>I[~, :, "IDS"]</c> — current against VDS, one curve per gate step.</para>
    ///
    /// <para>So when there are two or more swept axes: X is the INNERMOST (last) one, the outermost
    /// becomes the family, and structural axes stay pinned at index 0 carrying their label — which is
    /// what puts the probe's own name <c>"IDS"</c> in the expression rather than a bare index.</para>
    ///
    /// <para><b>A cube with a <c>freq</c> axis is left alone</b>, deliberately. Frequency is always the
    /// natural X, so the default slice is already right there and an S-parameter run already opens on a
    /// readable plot (S(1,1) over frequency, the sweep pinned) — promoting its sweep to a family would be
    /// a change to a case that was not broken. Same for a cube with one swept axis: every single-sweep
    /// HB, every DC operating point and every unswept S-parameter run seeds exactly what it seeded
    /// before.</para>
    /// </summary>
    internal static AxisSlice[] BuildSeedSlice(DataCube cube)
    {
        if (cube.Axes.Any(a => a.Name == "freq")) return TraceRowViewModel.BuildDefaultSlice(cube);

        var sweeps = new List<int>();
        for (int d = 0; d < cube.Rank; d++)
            if (!IsStructuralAxis(cube.Axes[d].Name)) sweeps.Add(d);

        if (sweeps.Count < 2) return TraceRowViewModel.BuildDefaultSlice(cube);

        int xIdx      = sweeps[^1];
        int familyIdx = sweeps[^2];

        // Too many members to draw: the renderer already clamps a family at Trace.MaxFamilyCurves and
        // says so, but a SEEDED trace that silently showed the first 101 of a 500-point sweep would be
        // claiming to be the whole picture. Past the cap the axis is pinned instead — the corrected X
        // survives, because current against VDS at one gate voltage is still the right pair of axes, and
        // the family is one click away in the axis-role editor.
        if (cube.Axes[familyIdx].Length > Trace.MaxFamilyCurves) familyIdx = -1;

        var slice = new AxisSlice[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            var ax = cube.Axes[d];
            slice[d] =
                d == xIdx      ? new AxisSlice(ax.Name, AxisRole.KeepAsX, 0)
              : d == familyIdx ? new AxisSlice(ax.Name, AxisRole.FamilyIterate, 0)
              : new AxisSlice(ax.Name, AxisRole.PinToIndex, 0,
                              Label: ax.Labels is { Length: > 0 } labels ? labels[0] : "");
        }
        return slice;
    }

    // ---- Commands -------------------------------------------------------

    public IRelayCommand AddTraceCommand        { get; }
    public IRelayCommand AddContourTraceCommand { get; }
    public IRelayCommand AddSummaryTraceCommand { get; }
    public IRelayCommand AutoFillSummaryCommand { get; }
    public IRelayCommand CloseCommand           { get; }

    // Plot-type set commands (segmented header buttons, §A)
    public IRelayCommand SetPlotTypeRectCommand  { get; }
    public IRelayCommand SetPlotTypeSmithCommand { get; }
    public IRelayCommand SetPlotTypePolarCommand { get; }
    public IRelayCommand SetPlotTypeTableCommand { get; }

    /// <summary>True when the selected data source is a loadpull result eligible for contour authoring.</summary>
    public bool CanAddContourTrace =>
        _library?.SelectedEntry is { } e && IsLoadpullSource(e);

    /// <summary>True when a contour can be added in the current mode (non-Table plot + loadpull source).
    /// Hides the contour button on Table plots so only "+ Summary" shows there.</summary>
    public bool CanAddContourInCurrentMode =>
        !IsSummaryAddMode && CanAddContourTrace;

    /// <summary>True when a summary column can be added (Table plot + loadpull source).</summary>
    public bool CanAddSummaryTrace =>
        _plot.PlotType == PlotType.Table && _library?.SelectedEntry is { } e && IsLoadpullSource(e);

    /// <summary>True when the auto-fill standard column set action is available.</summary>
    public bool CanAutoFillSummary => CanAddSummaryTrace;

    // ---- Constructor ----------------------------------------------------

    public PlotInspectorViewModel(
        Plot                  plot,
        Action                closeAction,
        DataSourceLibraryViewModel?  library = null)
    {
        _plot        = plot;
        _closeAction = closeAction;
        _library     = library;

        _plotType    = plot.PlotType;
        _freqUnit    = plot.FreqUnits;
        _tableOptimum  = plot.TableOptimum;
        _tableReadMode = plot.TableReadMode;

        RebuildTraces();

        AddTraceCommand        = new RelayCommand(AddTrace,        () => CanAddTrace);
        AddContourTraceCommand = new RelayCommand(AddContourTrace, () => CanAddContourTrace);
        AddSummaryTraceCommand = new RelayCommand(AddSummaryTrace, () => CanAddSummaryTrace);
        AutoFillSummaryCommand = new RelayCommand(AutoFillSummary, () => CanAutoFillSummary);
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

        if (IsSummaryTable) RebuildSummary();
    }

    // ---- Close-action seam (flyout vs Properties pane) -----------------

    /// <summary>Points the shared inspector's Close button at the current flyout's Hide while it is
    /// open; call with a no-op on flyout close so a stale reference is never invoked.</summary>
    public void SetCloseAction(Action closeAction) => _closeAction = closeAction;

    // ---- Library event --------------------------------------------------

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        // Remove network-bound traces whose SNP is no longer in the library.
        //
        // NetworkView belongs in this set as much as Snp does. A SIMULATED run has no Snp by
        // design — its S cube goes through the cube path, which can carry a swept axis an SNP
        // structurally cannot — so its DERIVED traces (stability circles, MaxGain, µ, µ′, K, |Δ|,
        // passivity, group delay) bind to the entry's narrow NetworkView instead. Reading Snp
        // alone made every one of them look like a trace whose source had left the library, so
        // the first LibraryChanged after a re-run DELETED them. Its ordinary S(i,j) traces
        // survived, because those are cube-bound and take the path-keyed branch below — which is
        // why the symptom was "only the derived traces disappear when I re-simulate".
        var librarySnps = new System.Collections.Generic.HashSet<SNP>(
            _library!.Entries.SelectMany(entry => new[] { entry.Snp, entry.NetworkView }).OfType<SNP>());

        // Also track current file paths for cube-bound stale detection.
        var libraryPaths = new System.Collections.Generic.HashSet<string>(
            _library.Entries.Select(e2 => e2.FilePath).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        var staleVms = Traces
            .Where(rv =>
            {
                var t = rv.Trace;
                // Contour and summary-column traces are never stale here — they hold a placeholder SNP and
                // their data is re-derived from the (path-keyed) loadpull source by RebuildContour/
                // RebuildSummary, not bound to a library SNP. Removing them on a re-run would wipe the
                // contour/summary and skip the refresh (IsSummaryTable → false).
                if (t.IsContourTrace || t.IsSummaryColumn) return false;
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

        // Contour traces cache their loadpull surface + metric/freq/analysis pickers keyed by file path.
        // A re-run overwrites run.npy at the same path, so force a refresh from the reloaded data (else the
        // analysis picker keeps listing the PREVIOUS run's analyses).
        foreach (var vm in Traces)
            vm.RefreshContourAfterReload();

        _harmonicWarned = false;   // new source → re-warn once if harmonic cubes found
        _plot.Autoscale();
        if (IsSummaryTable) RebuildSummary();
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
    /// R-dd-3 — drag-drop entry point: an .npy dropped onto this plot from the project tree loads
    /// it as a new dataset (if not already loaded) and opens the Add Trace picker for it in one
    /// gesture. Mirrors the picker's own "Add from file…" flow (R-dd-2) but is triggered by a
    /// drop rather than a combo selection.
    /// </summary>
    public async System.Threading.Tasks.Task AddDatasetFromDropAsync(string absPath)
    {
        if (_library is null) return;
        absPath = System.IO.Path.GetFullPath(absPath);

        var entry = _library.Entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, absPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            await _library.LoadFileAsync(absPath);
            entry = _library.Entries.FirstOrDefault(e =>
                string.Equals(e.FilePath, absPath, StringComparison.OrdinalIgnoreCase));
        }
        if (entry is null) return;   // unreadable / unrecognized extension

        if (!AddTraceCommand.CanExecute(null)) return;
        AddTraceCommand.Execute(null);

        var row = Traces.LastOrDefault();
        if (row is null) return;

        var item = row.AvailableSourceEntries.FirstOrDefault(i => ReferenceEquals(i.Entry, entry));
        if (item is not null) row.SelectedSourceItem = item;
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
        trace.Slice = BuildSeedSlice(cube);

        // First-add nicety on Rect: only COMPLEX cubes get an auto-transform (so they don't render
        // <invalid>); REAL cubes are shown raw — no annoying "mag". (Shared with the signal-switch path.)
        trace.Transform = TraceRowViewModel.DefaultTransformFor(cube, _plot.PlotType, cubeName);

        trace.Expression = trace.BuildPickerExpression();
        return trace;
    }

    public void RemoveTrace(TraceRowViewModel vm)
    {
        vm.UnsubscribeFromLibrary();
        _plot.Traces.Remove(vm.Trace);
        _plot.Autoscale();
        Traces.Remove(vm);
        OnPropertyChanged(nameof(IsSummaryTable));
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

        // "Plot versus" may take its X from a DIFFERENT loaded file (measured Pout against simulated
        // Gain). Null XSourcePath — the ordinary case — means "same source as Y".
        DataSourceEntryViewModel? xEntry = entry;
        if (library is not null && t.XSourcePath is not null)
        {
            xEntry = library.Entries.FirstOrDefault(e =>
                string.Equals(e.FilePath, t.XSourcePath, StringComparison.OrdinalIgnoreCase));
        }

        // Display-only: a cross-source X reads as "alias::Pout" in the spec box and table header.
        t.XSourceAlias = t.IsVersus && xEntry is not null && !ReferenceEquals(xEntry, entry)
            ? (library?.AliasFor(xEntry.FilePath) is { Length: > 0 } a
                ? a
                : System.IO.Path.GetFileNameWithoutExtension(xEntry.DisplayName))
            : null;

        SetCubeDataFrom(t, entry?.Data, plotType, freqUnit, xEntry?.Data);
    }

    /// <summary>
    /// The same resolution, against a <see cref="DataSet"/> already in hand rather than one looked up
    /// in the library.
    ///
    /// <para><b>Split out for harmonicaRF (R-h7-5).</b> harmonicaRF publishes its own <c>DataSet</c>
    /// on each solved frame and has no data-source library at all — it is a live instrument, not a
    /// document over files on disk. Everything below the lookup is identical, which is the point:
    /// there is one slicing implementation, and the picker over harmonicaRF's cubes is the same code
    /// the <c>.cdd</c> trace card runs.</para>
    /// </summary>
    internal static void SetCubeDataFrom(Trace t, DataSet? ds, PlotType plotType, FreqUnit freqUnit,
                                        DataSet? xDs = null)
    {
        try
        {
            SetCubeDataFromCore(t, ds, plotType, freqUnit, xDs);
        }
        catch (Exception ex)
        {
            // Resolving a trace against a data source is a READ. Every foreseeable way it can fail —
            // missing source, missing cube, wrong rank, unparseable spec — already ends as
            // "<invalid>" on the trace card, so an UNforeseen one has no business taking the session
            // down with it: the user loses an unsaved workspace over a curve that could simply have
            // said it could not be drawn.
            //
            // This exists because a reproducible field crash (Windows, 1.0.0-beta.7) landed here as a
            // bare IndexOutOfRangeException out of DataCube's gather, and the report named only the
            // reader — no source, no cube, no slice. The trail note below is what turns the next one
            // into a diagnosis: it records exactly which cube was being sliced and with what.
            //
            // The STACK is the half the first instrumented report (1.0.0-beta.8) still lacked, and it
            // is the half that ends the hunt: every reported failure so far names a well-formed cube
            // and an in-range slice, so the throw is somewhere in this method OTHER than the read the
            // stack was assumed to name. Recording it costs one string on a path that is already
            // failing.
            Diagnostics.CrashReporter.Note($"trace resolve FAILED: {DescribeCubeResolve(t, ds, plotType)} — "
                                           + $"{DescribeException(ex)}");
            t.ExpressionError = ex.Message;
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
        }
    }

    /// <summary>
    /// One line naming everything the crash trail needs to identify a failed resolve: the cube spec,
    /// the shape the DataSet actually holds for it, and the slice the trace asked for. Every step is
    /// individually guarded — this runs while something is already wrong, so it must not throw.
    /// </summary>
    private static string DescribeCubeResolve(Trace t, DataSet? ds, PlotType plotType)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"plot={plotType} cube='{t.CubeName ?? "(null)"}' expr='{t.Expression ?? "(null)"}'");

        try
        {
            if (ds is not null && t.CubeName is { } name && ds.Contains(name))
            {
                var c = ds[name];
                sb.Append($" shape=[{DescribeShape(c)}]");
                sb.Append($" kind={c.DataKind}");
            }
            else sb.Append(" shape=(cube not in source)");
        }
        catch (Exception e) { sb.Append($" shape=(unreadable: {e.GetType().Name})"); }

        // The slice's RANGE fields are printed too. A narrowed X axis is stored beside the pin index,
        // not in it, so the first instrumented reports could not tell a whole-axis X from a narrowed
        // one — and a narrowing is exactly the kind of carried-over state that outlives the shape it
        // was authored against.
        try
        {
            sb.Append(t.Slice is { } s
                ? $" slice=[{string.Join(", ", s.Select(DescribeAxisSlice))}]"
                : " slice=(null)");
        }
        catch (Exception e) { sb.Append($" slice=(unreadable: {e.GetType().Name})"); }

        // The rest of the trace state this resolve actually branches on. Every one of these selects a
        // different path through SetCubeDataFromCore, and none of them was in the first report.
        try
        {
            sb.Append($" transform={t.Transform} z0={t.Z0.Real:G6}{(t.Z0.Imaginary >= 0 ? "+" : "-")}j{Math.Abs(t.Z0.Imaginary):G6}");
            sb.Append($" override={(t.Z0OverrideEnabled ? "on" : "off")}");
            sb.Append($" srcZ0={(t.SourceZ0PerPort is { } z ? z.Length.ToString() : "(null)")}");
            sb.Append($" versus={(t.XSpec is { } xs ? $"'{xs}'" : "(none)")}");
            sb.Append($" family={t.FamilyCurves.Count} markers={t.Markers.Count}");
            sb.Append($" src='{SafeFileName(t.SourcePath)}'");
        }
        catch (Exception e) { sb.Append($" state=(unreadable: {e.GetType().Name})"); }

        // What the group actually holds. "The cube resolved" and "the group is well-formed" are
        // different claims, and the S/Z0 pair is what every network-parameter branch reads.
        try
        {
            if (ds is not null && t.CubeName is { } n2)
            {
                int dot = n2.LastIndexOf('.');
                string group = dot < 0 ? RfCore.Data.DataSet.DefaultGroup : n2[..dot];
                if (ds.ContainsGroup(group))
                    sb.Append($" group[{group}]={{{string.Join(", ",
                        ds.CubesIn(group).Select(kv => $"{kv.Key}:[{DescribeShape(kv.Value)}]:{kv.Value.DataKind}"))}}}");
            }
        }
        catch (Exception e) { sb.Append($" group=(unreadable: {e.GetType().Name})"); }

        return sb.ToString();
    }

    private static string DescribeShape(DataCube c)
        => string.Join(" x ", c.Axes.Select(a => $"{a.Name}[{a.Length}]"));

    private static string DescribeAxisSlice(AxisSlice a)
    {
        string s = $"{a.AxisName}:{a.Role}:{a.Index}";
        if (a.IsNarrowedRange) s += $"({a.RangeStart}..{a.RangeEndExclusive})";
        if (!string.IsNullOrEmpty(a.Label)) s += $"\"{a.Label}\"";
        return s;
    }

    private static string SafeFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(null)";
        try { return System.IO.Path.GetFileName(path); } catch { return "(unreadable)"; }
    }

    /// <summary>
    /// The exception, its inner chain, and its stack — the one thing the previous round's trail note
    /// left out, and the only thing that can name the throwing line when (as here) every value the
    /// note DOES print is in range. Frames are capped so one failure cannot flood the ring-buffered
    /// trail, and every step is guarded because this runs while something is already wrong.
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                sb.Append(ReferenceEquals(e, ex) ? "" : " <- ");
                sb.Append($"{e.GetType().Name}: {e.Message}");
            }

            var frames = (ex.StackTrace ?? "").Split('\n')
                .Select(f => f.TrimEnd('\r').Trim())
                .Where(f => f.Length > 0)
                .Take(16);
            foreach (var f in frames) sb.Append("\n    ").Append(f);
        }
        catch (Exception e) { sb.Append($" (exception unreadable: {e.GetType().Name})"); }
        return sb.ToString();
    }

    private static void SetCubeDataFromCore(Trace t, DataSet? ds, PlotType plotType, FreqUnit freqUnit,
                                            DataSet? xDs)
    {
        if (!t.IsCubeBound) return;

        // "Plot versus": the X side resolves against its own source when one is set, else against
        // the Y side's. Everything below reads xDataSet, so cross-source is not a second code path.
        DataSet? xDataSet = xDs ?? ds;

        // A malformed separator ("A vs B vs C", "Gain vs") is reported HERE as well as at the point
        // of typing, because every edit is followed by a resolve that would otherwise replace the
        // card's message with a confusing downstream parse error.
        if (t.Expression is { } exprText
            && !VersusSpec.TrySplit(exprText, out _, out _, out var splitErr)
            && splitErr.Length > 0)
        {
            t.ExpressionError = splitErr;
            t.InvalidSpecText = exprText;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // Versus is a rectangular idea: a Γ-plane locus has no X axis to redirect.
        if (t.IsVersus && plotType.IsComplex())
        {
            t.ExpressionError = "'vs' is available on Rect and Table plots only.";
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

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
            // Only the Y half goes to the evaluator; the X half is resolved separately below.
            string yExpr = VersusSpec.TrySplit(t.Expression, out var ySide, out _, out _)
                ? ySide : t.Expression;
            if (TraceExpression.TryEvaluate(yExpr, ds, plotType,
                    out var xVals, out var cz, out var rz,
                    out var xName, out var xUnit, out var xLabels, out var exprErr))
            {
                if (t.IsVersus)
                {
                    int yN = cz?.Length ?? rz?.Length ?? 0;
                    if (!TryVersusX(t, xDataSet, ySlice: null, yExpr, yN, out var vx, out var vErr))
                    {
                        t.ExpressionError = vErr;
                        t.InvalidSpecText = t.Expression;
                        t.Points.Clear();
                        return;
                    }
                    xVals = vx; xName = t.XSpec!; xUnit = null; xLabels = null;
                }
                t.ExpressionError = null;
                t.InvalidSpecText = null;
                t.SetSpectrumFundamentals(null);
                // The multi-cube expression text already encodes any transform — mark the values baked so a
                // real result renders as-is (the transform combo must not double-apply on top of it).
                t.SetCubeData(xVals, cz, rz, xName, xUnit, plotType, freqUnit, xLabels, transformBaked: true);
                if (!t.IsVersus)
                {
                    ApplyPinnedSpectral(t, ds);
                    ApplyPinnedAxisDisplay(t, ds);
                }
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

        // brief-dd-z0-renormalization.md §1: a network-parameter cube trace (S/Z/Y element) renders
        // at the trace's OWN reference Z0 rather than the source's. This is the single interception
        // point that feeds Rect/Smith/Polar/Table AND every marker/table readout downstream (via
        // _cubeComplexValues) — do not duplicate this renorm anywhere else in the cube path.
        if (RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, t.CubeName))
            cube = ResolveNetworkParamCube(ds, t, t.CubeName, cube);

        // Cube + slice resolved → clear any stale invalid flag left by a prior bad source
        // (covers the scalar, family, all-pinned, and rank-1 success paths below).
        t.InvalidSpecText = null;
        t.ExpressionError = null;

        // Scalar cube (rank 0): operating-point value — valid only on a Table (Part A).
        if (cube.Rank == 0)
        {
            if (RejectVersusOnScalar(t)) return;
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
            ResolveFamily(t, cube, slice, plotType, freqUnit, ds, xDataSet);
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
            if (RejectVersusOnScalar(t)) return;
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

        // ── Plot versus: X comes from the X spec, not from this cube's swept axis ──
        if (t.IsVersus)
        {
            int yN = complexValues?.Length ?? realValues?.Length ?? 0;
            if (!TryVersusX(t, xDataSet, slice, t.CubeName!, yN, out var vx, out var vErr))
            {
                t.ExpressionError = vErr;
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear();
                t.FamilyCurves.Clear();
                return;
            }
            t.SetSpectrumFundamentals(null);
            // No unit: cube VALUES carry none anywhere in the data model (only axes do), so a versus
            // X axis is labelled by its spec text alone — same as every Y label already is.
            t.SetCubeData(vx, complexValues, realValues, t.XSpec!, null, plotType, freqUnit);
            ApplyPinnedAxisDisplay(t, ds);
            return;
        }

        var toneFreqs1 = GetToneFreqsCube(ds, t.CubeName);
        t.SetSpectrumFundamentals(ResolveFundamentalByX(toneFreqs1, slice, xAxis.Values.Length));
        t.SetCubeData(xAxis.Values, complexValues, realValues,
                      xAxis.Name, xAxis.Unit, plotType, freqUnit, xAxis.Labels);
        ApplyPinnedSpectral(t, ds);
        ApplyPinnedAxisDisplay(t, ds);
    }

    /// <summary>
    /// Resolves the X side of a versus trace and gates it on the point count — the rule that makes a
    /// cross-source X safe (two files whose sweeps disagree can never be silently paired).
    /// </summary>
    private static bool TryVersusX(Trace t, DataSet? xDs, AxisSlice[]? ySlice, string ySpec, int yN,
                                   out double[] xValues, out string error)
    {
        xValues = Array.Empty<double>();
        if (xDs is null)
        {
            error = $"X source for '{t.XSpec}' is not loaded.";
            return false;
        }
        if (!VersusResolver.TryResolveX(t.XSpec!, xDs, ySlice, out xValues, out error))
        {
            if (string.IsNullOrEmpty(error)) error = $"Cannot resolve X side '{t.XSpec}'.";
            return false;
        }
        return VersusResolver.CountsAgree(t.XSpec!, ySpec, xValues.Length, yN, out error);
    }

    /// <summary>A fully-pinned (scalar) Y has no swept axis to plot against. Reports it rather than
    /// rendering a one-point curve at a meaningless X.</summary>
    private static bool RejectVersusOnScalar(Trace t)
    {
        if (!t.IsVersus) return false;
        t.ExpressionError = $"'vs {t.XSpec}' needs a swept Y — this selection is a single value.";
        t.InvalidSpecText = t.Expression ?? t.CubeName;
        t.Points.Clear();
        t.FamilyCurves.Clear();
        return true;
    }

    /// <summary>
    /// Stamps <see cref="Trace.SourceZ0PerPort"/>/<see cref="Trace.SourceZ0IsUnusual"/> on a
    /// cube-bound network-parameter trace from its OWN group's Z0 cube. Returns false — leaving the
    /// trace untouched — when <paramref name="cubeSpec"/> is not a network-parameter cube, which is
    /// the caller's cue that there are no per-port references to carry.
    ///
    /// <para><b>The single stamping site, and it must stay single.</b> The per-port array is the
    /// only faithful record of a run's port references — <c>Data.Z0</c> is one uniform value and
    /// <see cref="Trace.MarkerReferenceZ0"/> falls back to the trace's own (default 50 Ω) <c>Z0</c>
    /// when the array is null, so a trace that loses it silently reports every reflection against
    /// 50 Ω. That is exactly what happened when <c>TraceRowViewModel.RebuildSignals</c> cleared the
    /// array for every cube-bound trace: it runs AFTER <see cref="TrySetCubeData"/> on both the
    /// <c>.cdd</c> load path and the post-run library refresh, so a conjugately-matched port pair
    /// (Term Z = 5+j100 against 5−j100) plotted at the Smith centre — correctly — while its marker
    /// read "impedance=50+j0 Ω" instead of the 5−j100 the port actually sees. Re-stamp here rather
    /// than clearing; never add a second copy of this logic.</para>
    /// </summary>
    internal static bool StampSourceZ0FromCube(DataSet ds, Trace t, string cubeSpec)
    {
        if (!RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, cubeSpec)) return false;

        int dot = cubeSpec.LastIndexOf('.');
        string group  = dot < 0 ? "" : cubeSpec[..dot];
        string z0Spec = group.Length == 0 ? RfCore.Data.NetworkMetrics.Z0CubeName
                                          : $"{group}.{RfCore.Data.NetworkMetrics.Z0CubeName}";

        var z0Cube = ds[z0Spec];
        t.SourceZ0PerPort   = z0Cube.ComplexValues;
        t.SourceZ0IsUnusual = RfCore.Data.DataSetBuilder.ClassifyZ0(z0Cube) != RfCore.Data.Z0Kind.UniformReal;
        return true;
    }

    /// <summary>
    /// Resolves the effective DataCube for a network-parameter cube trace (S/Z/Y element) at the
    /// trace's OWN reference <see cref="Trace.Z0"/> — brief-dd-z0-renormalization.md §1. Also stamps
    /// <see cref="Trace.SourceZ0PerPort"/>/<see cref="Trace.SourceZ0IsUnusual"/> from the group's Z0
    /// cube, reusing the exact two fields the network/SNP path already uses (§3's Y-label token and
    /// §2's badge read them regardless of which path populated them).
    ///
    /// <para>Order with Z/Y conversion (§1): renormalize S first, then convert to Z or Y — so Z/Y
    /// come out mathematically INVARIANT to the trace's Z0 for a REAL target (Z/Y are reference-
    /// independent quantities; pinned by <c>Z0RenormalizationTests</c>'s "order commutes" gate).
    /// <b>Known limitation for a COMPLEX target:</b> <c>RFNetwork.SToS</c> is the power-wave
    /// (Kurokawa) form (uses <c>Conjugate(z0)</c>), while <c>SToZ</c>/<c>SToY</c> are the ORDINARY
    /// (non-power-wave) √Z0 form — no conjugate. The two conventions coincide when Z0 is real but
    /// genuinely diverge for a complex reference, so a Z/Y cube trace's displayed values can shift
    /// slightly under a COMPLEX Z0 override (pinned by
    /// <c>Z0RenormalizationTests.RenormalizeSCube_ThenConvert_DivergesFromDirect_ComplexTarget</c>).
    /// Not introduced by this brief — <c>NetworkMetrics.TwoPortUniformReal</c>/<c>FullUniformReal</c>
    /// (R-stb-1..6) already restrict their own renormalization target to REAL for exactly this
    /// reason. Fixing the underlying convention gap (making SToZ/SToY power-wave-aware, or SToS
    /// ordinary-aware) is out of scope here — it would touch every S/Z/Y conversion call site in the
    /// engine and UI, not just this brief's cube-trace Z0 field.</para>
    /// </summary>
    private static DataCube ResolveNetworkParamCube(DataSet ds, Trace t, string cubeSpec, DataCube cube)
    {
        int dot = cubeSpec.LastIndexOf('.');
        string bare  = dot < 0 ? cubeSpec : cubeSpec[(dot + 1)..];
        string group = dot < 0 ? "" : cubeSpec[..dot];
        string sSpec  = group.Length == 0 ? RfCore.Data.NetworkMetrics.SCubeName  : $"{group}.{RfCore.Data.NetworkMetrics.SCubeName}";
        string z0Spec = group.Length == 0 ? RfCore.Data.NetworkMetrics.Z0CubeName : $"{group}.{RfCore.Data.NetworkMetrics.Z0CubeName}";

        var sCube  = ds[sSpec];
        var z0Cube = ds[z0Spec];
        int nPorts = sCube.Axes[sCube.Rank - 1].Length;
        var z0Src  = z0Cube.ComplexValues;

        StampSourceZ0FromCube(ds, t, cubeSpec);

        // Override OFF ⇒ absolutely no renormalization, whatever the source's per-port references
        // are (brief-dd-z0-nonuniform-override). The cube is returned exactly as simulated/loaded:
        // an S-parameter run with per-port Term impedances renders the match it actually has, not
        // the match it would have if every port were re-terminated at the port-1 reference.
        if (!t.Z0OverrideEnabled) return cube;

        bool identity = true;
        for (int p = 0; p < nPorts; p++)
            if (z0Src[p] != t.Z0) { identity = false; break; }
        if (identity) return cube;

        var z0New   = Enumerable.Repeat(t.Z0, nPorts).ToArray();
        var renormS = RfCore.Data.NetworkMetrics.RenormalizeSCube(sCube, z0Src, z0New);
        if (bare == RfCore.Data.NetworkMetrics.SCubeName) return renormS;

        var matrixType = bare == "Z" ? MatrixType.Z : MatrixType.Y;
        return RfCore.Data.NetworkMetrics.ConvertSCube(renormS, z0New, matrixType);
    }

    private static void ResolveFamily(Trace t, DataCube cube, AxisSlice[] slice,
                                      PlotType plotType, FreqUnit freqUnit, DataSet? ds = null,
                                      DataSet? xDs = null)
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

        // ── Plot versus, family form: each curve gets its OWN X (Pout at 2.0 GHz is not Pout at
        //    2.4 GHz), so the X side iterates the same family axis and is resolved per curve.
        List<double[]>? perCurveX = null;
        if (t.IsVersus)
        {
            var xSource = xDs ?? ds;
            if (xSource is null)
            {
                t.ExpressionError = $"X source for '{t.XSpec}' is not loaded.";
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear(); t.FamilyCurves.Clear();
                return;
            }
            if (!VersusResolver.TryResolveXFamily(t.XSpec!, xSource, slice, fAxis.Name, curves.Count,
                                                  out perCurveX, out var vErr))
            {
                t.ExpressionError = vErr;
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear(); t.FamilyCurves.Clear();
                return;
            }
            for (int k = 0; k < curves.Count; k++)
            {
                int yN = curves[k].Item3?.Length ?? curves[k].Item4?.Length ?? 0;
                if (!VersusResolver.CountsAgree(t.XSpec!, t.CubeName ?? "", perCurveX[k].Length, yN, out var cErr))
                {
                    t.ExpressionError = cErr;
                    t.InvalidSpecText = t.Expression ?? t.CubeName;
                    t.Points.Clear(); t.FamilyCurves.Clear();
                    return;
                }
            }
            xVals = perCurveX[0];       // trace-level anchor; every curve carries its own below
            xName = t.XSpec!;
            xUnit = null;
        }

        var toneFreqs2 = GetToneFreqsCube(ds, t.CubeName);
        t.SetSpectrumFundamentals(t.IsVersus ? null : ResolveFundamentalByX(toneFreqs2, slice, xVals.Length));
        t.SetFamilyData(xVals, xName, xUnit, fAxis.Name, curves, plotType, freqUnit,
                        familyAxisUnit: string.IsNullOrEmpty(fAxis.Unit) ? null : fAxis.Unit,
                        perCurveX: perCurveX);
        // A family trace has no pinned SPECTRAL line (each curve carries its own tag), but it can
        // still carry ordinary pinned axes, and those appear in its label like any other trace's.
        ApplyPinnedAxisDisplay(t, ds);
    }

    private static DataCube? GetToneFreqsCube(DataSet? ds, string? cubeName)
    {
        if (ds is null || cubeName is null) return null;
        int dot = cubeName.IndexOf('.');
        string toneFreqsName = dot < 0 ? "ToneFreqs" : cubeName[..dot] + ".ToneFreqs";
        return ds.Contains(toneFreqsName) ? ds[toneFreqsName] : null;
    }

    // When a spectral axis ("harmonic"/"mixIndex") is PINNED in the slice (X is a sweep, e.g. Pin),
    // surface which spectral line the trace shows + its frequency so the marker box reads the same
    // two rows the spectral-axis-X plot gives. Clears it when no spectral axis is pinned (incl. when
    // the spectral axis is the X axis — then the X-axis marker rows already report it).
    private static void ApplyPinnedSpectral(Trace t, DataSet? ds)
    {
        t.SetPinnedSpectral(null, null, double.NaN);
        if (ds is null || t.CubeName is null || t.Slice is null || !ds.Contains(t.CubeName)) return;

        AxisSlice? pin = null;
        foreach (var s in t.Slice)
            if (s.Role == AxisRole.PinToIndex &&
                (s.AxisName == Trace.HarmonicAxisName || s.AxisName == Trace.MixIndexAxisName))
            { pin = s; break; }
        if (pin is null) return;

        var cube = ds[t.CubeName];
        Axis? axis = null;
        foreach (var a in cube.Axes) if (a.Name == pin.Value.AxisName) { axis = a; break; }
        if (axis is null || axis.Length == 0) return;
        int idx = Math.Clamp(pin.Value.Index, 0, axis.Length - 1);

        string label = axis.Labels is not null && idx < axis.Labels.Length
            ? axis.Labels[idx]
            : axis.Values[idx].ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

        double freqHz;
        if (pin.Value.AxisName == Trace.MixIndexAxisName)
        {
            // The mixIndex value IS the signed product frequency → fold to the single-sided |f|.
            freqHz = Math.Abs(axis.Values[idx]);
        }
        else
        {
            // harmonic: order × f0 (representative fundamental; exact for a non-frequency sweep).
            var tf = GetToneFreqsCube(ds, t.CubeName);
            double f0 = tf is not null && tf.RealValues.Length > 0 ? tf.RealValues[0] : double.NaN;
            freqHz = double.IsNaN(f0) ? double.NaN : Math.Round(axis.Values[idx]) * f0;
        }

        t.SetPinnedSpectral(pin.Value.AxisName, label, freqHz);
    }

    // A pinned axis used to read as its raw INDEX — "DC1.I(VDS=240, branch=0)" — which names neither
    // the value the user swept to nor the quantity they picked. Both answers are on the cube, so the
    // owner resolves them here and hands the finished tokens to the Trace (which never holds a
    // DataSet). Mirrors ApplyPinnedSpectral exactly, including its clear-first contract.
    //
    // Two forms, and the difference is deliberate: a LABELLED axis reads as its label alone ("IDS")
    // because the label already names the quantity, while a swept axis keeps its name and gains its
    // value and unit ("VDS=3.5 V"). The S/Y/Z port axes are excluded — they are written positionally
    // as "S(1,2)" by TraceLabeler and must stay that way.
    internal static void ApplyPinnedAxisDisplay(Trace t, DataSet? ds)
    {
        t.SetPinnedAxisDisplay(null);
        if (ds is null || t.CubeName is null || t.Slice is null || !ds.Contains(t.CubeName)) return;

        var cube = ds[t.CubeName];
        Dictionary<string, string>? map = null;

        foreach (var s in t.Slice)
        {
            if (s.Role != AxisRole.PinToIndex) continue;
            if (s.AxisName is "i" or "j") continue;   // positional port pair — TraceLabeler owns it

            Axis? axis = null;
            foreach (var a in cube.Axes) if (a.Name == s.AxisName) { axis = a; break; }
            if (axis is null || axis.Length == 0) continue;

            int idx = Math.Clamp(s.Index, 0, axis.Length - 1);

            string token;
            if (axis.Labels is not null && idx < axis.Labels.Length &&
                !string.IsNullOrWhiteSpace(axis.Labels[idx]))
            {
                token = axis.Labels[idx];
            }
            else
            {
                string val = axis.Values[idx].ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
                token = string.IsNullOrWhiteSpace(axis.Unit)
                    ? $"{axis.Name}={val}"
                    : $"{axis.Name}={val} {axis.Unit}";
            }

            (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[s.AxisName] = token;
        }

        t.SetPinnedAxisDisplay(map);
    }

    private static double[]? ResolveFundamentalByX(DataCube? toneFreqs, AxisSlice[]? slice, int xAxisLength)
    {
        if (toneFreqs is null || slice is null) return null;
        var result = new double[xAxisLength];
        for (int xi = 0; xi < xAxisLength; xi++)
        {
            var args = new object[toneFreqs.Rank];
            for (int d = 0; d < toneFreqs.Rank; d++)
            {
                string axName = toneFreqs.Axes[d].Name;
                if (axName == "tone") { args[d] = 0; continue; }
                AxisSlice? found = null;
                foreach (var s in slice) { if (s.AxisName == axName) { found = s; break; } }
                if (found?.Role == AxisRole.KeepAsX)
                    args[d] = Math.Clamp(xi, 0, Math.Max(0, toneFreqs.Axes[d].Length - 1));
                else
                    args[d] = Math.Clamp(found?.Index ?? 0, 0, Math.Max(0, toneFreqs.Axes[d].Length - 1));
            }
            var r = toneFreqs[args];
            result[xi] = r.IsReal ? r.RealValue!.Value : 0.0;
        }
        return result;
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

    /// <summary>
    /// Forcibly re-frames the plot to the current data and redraws — called after a loadpull contour's
    /// frequency changed, so the Rect x/y axes snap to the new frequency's RecommendedBox (MXP/MXE region).
    /// Uses <c>force: true</c> like <c>AddContourTrace</c>, because a contour plot keeps autoscale off for
    /// a sticky view, so a non-forced Autoscale() would not re-frame.
    /// </summary>
    public void ForceRescaleAndNotify()
    {
        _plot.Autoscale(force: true);
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshAddCommand()
    {
        OnPropertyChanged(nameof(CanAddTrace));
        ((RelayCommand)AddTraceCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddContourTrace));
        ((RelayCommand)AddContourTraceCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddContourInCurrentMode));
        OnPropertyChanged(nameof(CanAddSummaryTrace));
        ((RelayCommand)AddSummaryTraceCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAutoFillSummary));
        ((RelayCommand)AutoFillSummaryCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AddLoadpullTraceLabel));
        OnPropertyChanged(nameof(IsSummaryAddMode));
        OnPropertyChanged(nameof(IsSummaryTable));
    }

    /// <summary>
    /// True when the entry is a loadpull source eligible for a contour/summary trace. Recognition is
    /// shape-based and group-aware (<see cref="LoadpullRecognition"/>): a simulated LP <c>run.npy</c>
    /// (cubes under an <c>LP1</c> group) is accepted identically to an ingested flat <c>.spl</c>/
    /// <c>.lpcwave</c>. The source-kind fast path keeps measured files eligible even if their cube
    /// layout differs slightly from the canonical engine shape.
    /// </summary>
    private static bool IsLoadpullSource(DataSourceEntryViewModel e) =>
        e.Data is { } ds
        && (e.Kind is SourceKind.Spl or SourceKind.Lpcwave
            || LoadpullRecognition.IsLoadpull(ds));

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
            // brief-dd-loadpull-contour-ux-round8 §2: a Rect grid is far denser in data-space than
            // Smith/Polar, so it needs a wider label pitch than ContourData's 30.0 default. R8A §4.2:
            // the Γ world is the unit disc (longest closed polyline ≈ 2π ≈ 6.28), and 30.0 is 5× that
            // — the world-unit arc walk in ContourRenderer.DrawIsoLines never reached its first label
            // target, so NOT ONE label was ever drawn on a Smith/Polar contour. 0.35 places one label
            // per ~1.1 rad of a rim-scale ring — ~5-6 labels around a full circle, the density the
            // Rect default already achieves on its own axis.
            LabelSpacing = plane switch
            {
                SurfacePlane.Z     => 150.0,
                SurfacePlane.Gamma => 0.35,
                _                  => 30.0,
            },
        };

        _plot.Traces.Add(trace);
        Traces.Add(new TraceRowViewModel(trace, this));
        // Re-autoscale after RebuildContour() in the VM ctor has populated the grid.
        _plot.Autoscale(force: true);
        RefreshAddCommand();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddSummaryTrace()
    {
        var entry = _library?.SelectedEntry;
        if (entry?.Data is null) return;

        var placeholder = new SNP(new double[] { 1e9 }, 1);
        var trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = DataSourceRef.Selected;
        trace.SourcePath = _library!.SelectedDataSourceAbs;

        trace.SummaryColumn = new SummaryColumnData
        {
            Kind           = SummaryColumnKind.Metric,
            MetricName     = "Pout",
            FractionDigits = 1,
        };
        trace.ColumnWidth = trace.SummaryColumn.ColumnWidth > 0
            ? trace.SummaryColumn.ColumnWidth
            : _plot.ColumnWidth;

        _plot.Traces.Add(trace);
        Traces.Add(new TraceRowViewModel(trace, this));
        RebuildSummary();
        RefreshAddCommand();
        OnPropertyChanged(nameof(IsSummaryTable));
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Auto-fill standard column set (Phase 7.5e) ----------------------------

    /// <summary>True when a cube of the given canonical name exists in any group of the dataset.</summary>
    private static bool HasCube(DataSet ds, string name) =>
        ds.Groups.Any(g => ds.CubesIn(g).ContainsKey(name));

    /// <summary>
    /// True when the dataset carries harmonic-indexed load-termination cubes (GammaLoad2/ZLoad2/…),
    /// which the summary table does NOT use — it targets the fundamental (1f0) only (design decision 3).
    /// Presence-gated: returns false for every dataset the current importer produces (fundamental-only).
    /// Convention: harmonic-n cubes are named GammaLoad{n} or ZLoad{n} for n≥2 (trailing digit).
    /// </summary>
    private static bool HasHarmonicLoadCubes(DataSet ds)
    {
        foreach (var g in ds.Groups)
            foreach (var cubeName in ds.CubesIn(g).Keys)
            {
                if ((cubeName.StartsWith("GammaLoad", StringComparison.Ordinal)
                  || cubeName.StartsWith("ZLoad",     StringComparison.Ordinal))
                    && cubeName.Length > 0 && char.IsDigit(cubeName[^1]))
                    return true;
            }
        return false;
    }

    /// <summary>
    /// Replaces the table's summary columns with the standard performance set (design §4), in order,
    /// presence-gated against the dataset. Columns whose backing cube is absent are silently skipped.
    /// (Phase 7.5e.)
    /// </summary>
    private void AutoFillSummary()
    {
        var entry = _library?.SelectedEntry;
        if (entry?.Data is not { } ds) return;

        // Remove existing summary traces first (replace semantics).
        var existing = Traces.Where(vm => vm.Trace.IsSummaryColumn).ToList();
        foreach (var vm in existing)
        {
            vm.UnsubscribeFromLibrary();
            _plot.Traces.Remove(vm.Trace);
            Traces.Remove(vm);
        }

        // Build the standard set in order, presence-gated.
        void AddCol(SummaryColumnKind kind, string metricName, bool present)
        {
            if (!present) return;
            var placeholder = new SNP(new double[] { 1e9 }, 1);
            var trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourceRef  = DataSourceRef.Selected;
            trace.SourcePath = _library!.SelectedDataSourceAbs;
            trace.SummaryColumn = new SummaryColumnData
            {
                Kind           = kind,
                MetricName     = metricName,
                FractionDigits = 1,
            };
            trace.ColumnWidth = _plot.ColumnWidth;
            _plot.Traces.Add(trace);
            Traces.Add(new TraceRowViewModel(trace, this));
        }

        // §4 standard order: VDD, Idq, Zsource, Zin, Zload, Power, Efficiency, Gain, AM/PM, IRL.
        AddCol(SummaryColumnKind.OperatingPoint, "BiasVLoad", HasCube(ds, "BiasVLoad"));
        AddCol(SummaryColumnKind.OperatingPoint, "BiasILoad", HasCube(ds, "BiasILoad"));
        AddCol(SummaryColumnKind.Zsource,        "",          HasCube(ds, "ZSource"));
        AddCol(SummaryColumnKind.Zin,            "",          HasCube(ds, "Zin_real") && HasCube(ds, "Zin_imag"));
        AddCol(SummaryColumnKind.Zload,          "",          HasCube(ds, "ZLoad"));
        AddCol(SummaryColumnKind.Metric,         "Pout_dBm",  HasCube(ds, "Pout_dBm"));
        AddCol(SummaryColumnKind.Metric,         "Efficiency",HasCube(ds, "Efficiency"));
        AddCol(SummaryColumnKind.Metric,         "Gt_dB",     HasCube(ds, "Gt_dB"));
        AddCol(SummaryColumnKind.Metric,         "AMPM_deg",  HasCube(ds, "AMPM_deg"));
        AddCol(SummaryColumnKind.Metric,         "IRL_dB",    HasCube(ds, "IRL_dB"));

        RebuildSummary();
        RefreshAddCommand();
        OnPropertyChanged(nameof(IsSummaryTable));
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- RebuildSummary (Phase 7.5d keystone) ----------------------------------

    /// <summary>
    /// Recomputes the summary table's derived state: Plot.SummaryFreqs and each summary column's
    /// CellsReal/CellsComplex, read at the per-frequency MXP/MXE optimum using the table-wide
    /// compression and read mode. No-op when the plot is not a summary table. (Phase 7.5d.)
    /// </summary>
    // Keep the summary analysis picker (SummaryAvailableAnalyses + SummarySelectedAnalysis) in sync with
    // the recognized loadpull views and the group actually in use. Only mutates the collection when it
    // changes; syncs the selection under a suppress guard so it never re-triggers a rebuild.
    private void RebuildSummaryAnalysisList(IReadOnlyList<LoadpullRecognition.LoadpullView> views, string activeGroup)
    {
        var groups = views.Select(v => v.Group ?? "").ToList();

        // Suppress the selection callback across the ENTIRE mutation: the live ComboBox sets its
        // SelectedItem to null while the ItemsSource is being Cleared, which would otherwise re-enter
        // OnSummarySelectedAnalysisChanged → RebuildSummary → RebuildSummaryAnalysisList mid-loop and
        // double-add every analysis. (Save/restore so an outer suppress is honored.)
        bool prevSuppress = _suppressSummaryAnalysisSync;
        _suppressSummaryAnalysisSync = true;
        try
        {
            if (!groups.SequenceEqual(SummaryAvailableAnalyses, StringComparer.Ordinal))
            {
                SummaryAvailableAnalyses.Clear();
                foreach (var g in groups) SummaryAvailableAnalyses.Add(g);
                OnPropertyChanged(nameof(ShowSummaryAnalysisPicker));
            }
            SummarySelectedAnalysis = activeGroup;
        }
        finally { _suppressSummaryAnalysisSync = prevSuppress; }
    }

    public void RebuildSummary()
    {
        var summaryTraces = _plot.Traces.Where(t => t.IsSummaryColumn).ToList();
        if (summaryTraces.Count == 0)
        {
            _plot.SummaryFreqs = null;
            PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
            return;
        }

        var entry = _library?.SelectedEntry;
        if (entry?.Data is not { } ds)
        {
            _plot.SummaryFreqs = null;
            ClearSummaryCells(summaryTraces);
            PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Harmonic guard (Phase 7.5f): silently noted; summary uses fundamental only.
        // No UI warning seam available in this VM — the detection is the durable value.
        if (HasHarmonicLoadCubes(ds) && !_harmonicWarned)
            _harmonicWarned = true;

        // Group-aware: LP cubes may be top level (flat .spl) or under an analysis group (LP run.npy).
        // When more than one loadpull view exists, honor the user's chosen analysis (persisted on the
        // Plot); otherwise default to the first. Keep the analysis picker in sync with the views.
        var lpViews = LoadpullRecognition.FindLoadpullViews(ds);
        string? wantedGroup = _plot.SummaryLoadpullGroup;
        string lpGroup =
            (!string.IsNullOrEmpty(wantedGroup) && lpViews.Any(v => (v.Group ?? "") == wantedGroup)) ? wantedGroup!
            : lpViews.Count > 0 ? (lpViews[0].Group ?? "") : "";
        RebuildSummaryAnalysisList(lpViews, lpGroup);

        LoadpullSurface surface;
        try { surface = new LoadpullSurface(ds, lpGroup); }
        catch
        {
            _plot.SummaryFreqs = null;
            _plot.SummaryAxisName = null;
            _plot.SummaryAxisUnit = null;
            ClearSummaryCells(summaryTraces);
            PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
            return;
        }

        int nFreq = surface.Frequencies.Count;
        var freqs = new double[nFreq];
        for (int i = 0; i < nFreq; i++) freqs[i] = surface.Frequencies[i];
        _plot.SummaryFreqs = freqs;
        _plot.SummaryAxisName = surface.LeadingAxisName;
        _plot.SummaryAxisUnit = surface.LeadingAxisUnit;

        var constraint = ConstraintSpec.AtCompression(_plot.TableCompression);
        var plane      = SurfacePlane.Z;
        bool nearest   = _plot.TableReadMode == TableReadMode.Nearest;

        var optima = new System.Numerics.Complex?[nFreq];
        for (int fi = 0; fi < nFreq; fi++)
        {
            var mxx = _plot.TableOptimum == TableOptimum.Mxp
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
            t.ColumnWidth = sc.ColumnWidth > 0 ? sc.ColumnWidth : _plot.ColumnWidth;
        }

        NotifySummaryColumnsCompression();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
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

    private void NotifySummaryColumnsCompression()
    {
        foreach (var vm in Traces)
            vm.RaiseSummaryCompressionChanged();
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
