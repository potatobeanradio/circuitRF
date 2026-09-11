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
        OnPropertyChanged(nameof(IsPolarDbPlot));
        OnPropertyChanged(nameof(IsTablePlot));
        OnPropertyChanged(nameof(IsSurfacePlot));
        OnPropertyChanged(nameof(HasPatternScale));
        OnPropertyChanged(nameof(HasPatternControls));
        OnPropertyChanged(nameof(CanEditPolarDbReference));
        OnPropertyChanged(nameof(PolarAngleLabels));
        RefreshPolarDbUnitOptions();
        OnPropertyChanged(nameof(PolarDbUnitSelection));
        NotifySurfaceCameraChanged();
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

    /// <summary>ANT-10's 3D pattern surface. <b>Not the default pattern view</b> — the cuts are.</summary>
    public bool IsSurfacePlot => _plot.PlotType == PlotType.Surface3D;

    // ---- The dB radial mode (ANT-7 §2) ----------------------------------
    //
    //  Six plain properties over the Plot's own, in the shape TableCompression already has. Each
    //  ends in ApplyRadialChange, because every one of them moves the SCALE — and on a pattern plot
    //  the scale is what the trace's points are built from, not merely how they are framed.

    /// <summary>True when the polar radius is read in decibels — the pattern mode.</summary>
    public bool PolarRadialIsDb
    {
        get => _plot.PolarRadial == PolarRadialMode.Db;
        set
        {
            var target = value ? PolarRadialMode.Db : PolarRadialMode.Linear;
            if (_plot.PolarRadial == target) return;
            _plot.PolarRadial = target;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPolarDbPlot));
            OnPropertyChanged(nameof(HasPatternScale));
            OnPropertyChanged(nameof(HasPatternControls));
            OnPropertyChanged(nameof(CanEditPolarDbReference));
            // The picker's enable gate is a function of this mode — a dB polar plot is a PATTERN and
            // takes REAL cubes, a linear one is a locus and does not. Alone among the six radial
            // properties this one changes what may be OFFERED, so it is the only one that rebuilds.
            foreach (var vm in Traces) vm.OnPlotPatternModeChanged();
            ApplyRadialChange();
        }
    }

    /// <summary>Gates the dB sub-controls: they say nothing on a linear polar plot.</summary>
    public bool IsPolarDbPlot => IsPolarPlot && _plot.PolarRadial == PolarRadialMode.Db;

    /// <summary>True when the outer ring is the data's own peak. <b>The plot states which either
    /// way</b> — see <see cref="PolarPatternScale.ReferenceCaption"/>.</summary>
    public bool PolarDbNormalised
    {
        get => _plot.PolarDbReference == PolarDbReferenceMode.Peak;
        set
        {
            var target = value ? PolarDbReferenceMode.Peak : PolarDbReferenceMode.Absolute;
            if (_plot.PolarDbReference == target) return;
            _plot.PolarDbReference = target;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PolarDbAbsolute));
            OnPropertyChanged(nameof(CanEditPolarDbReference));
            ApplyRadialChange();
        }
    }

    /// <summary>The inverse, so the absolute-reference box can bind its own enablement.</summary>
    public bool PolarDbAbsolute => !PolarDbNormalised;

    /// <summary>
    /// <b>The outer-ring level is editable only when there IS a scale AND the scale is absolute.</b>
    /// Two gates on one control, so the control binds one property rather than the view making up
    /// the conjunction: the scale controls are greyed rather than hidden off a pattern plot (the row
    /// keeps its width — see the note in <c>PlotInspectorView.axaml</c>), and a NORMALISED plot's
    /// outer ring is the data's own peak and has no level to type.
    /// </summary>
    public bool CanEditPolarDbReference => HasPatternScale && PolarDbAbsolute;

    public double PolarDbReferenceValue
    {
        get => _plot.PolarDbReferenceValue;
        set
        {
            if (!double.IsFinite(value)) { OnPropertyChanged(); return; }
            if (Math.Abs(_plot.PolarDbReferenceValue - value) < 1e-9) return;
            _plot.PolarDbReferenceValue = value;
            OnPropertyChanged();
            ApplyRadialChange();
        }
    }

    public double PolarDbFloor
    {
        get => _plot.PolarDbFloor;
        set
        {
            if (!double.IsFinite(value)) { OnPropertyChanged(); return; }
            double clamped = Math.Clamp(value, -200.0, -1.0);
            if (Math.Abs(_plot.PolarDbFloor - clamped) < 1e-9) { if (value != clamped) OnPropertyChanged(); return; }
            _plot.PolarDbFloor = clamped;
            OnPropertyChanged();
            ApplyRadialChange();
        }
    }

    public double PolarDbRingStep
    {
        get => _plot.PolarDbRingStep;
        set
        {
            if (!double.IsFinite(value)) { OnPropertyChanged(); return; }
            double clamped = Math.Clamp(value, 0.5, 100.0);
            if (Math.Abs(_plot.PolarDbRingStep - clamped) < 1e-9) { if (value != clamped) OnPropertyChanged(); return; }
            _plot.PolarDbRingStep = clamped;
            OnPropertyChanged();
            ApplyRadialChange();
        }
    }

    public string PolarDbUnit
    {
        get => _plot.PolarDbUnit;
        set
        {
            string v = value ?? "";
            if (string.Equals(_plot.PolarDbUnit, v, StringComparison.Ordinal)) return;
            _plot.PolarDbUnit = v;
            OnPropertyChanged();
            RefreshPolarDbUnitOptions();
            OnPropertyChanged(nameof(PolarDbUnitSelection));
            ApplyRadialChange();
        }
    }

    // ---- The radial unit, as a LIST (owner request, 2026-09-11) ----------
    //
    //  It was a free text box, and a unit is not free text: it is printed around the plot as a
    //  claim about what the numbers mean, so "dbi" or "dB/sr" typed in a hurry states something
    //  false rather than failing. These are the spellings an antenna quantity is actually quoted
    //  in, plus the row that takes whatever the CUBE says.

    /// <summary>The row that means "no unit of my own — take the cube's". Empty is what the plot
    /// stores for it; a row has to say something, and a blank row in a list reads as a bug.
    ///
    /// <para><b>A dash, not a sentence</b> (owner, 2026-09-11). The combo sits at the end of a row of
    /// short unit spellings and it is sized by its widest row, so "from the data" set the width of
    /// the control — four times the width of the answer it usually shows. The dash is the
    /// conventional "nothing here" mark in a table of units, and the tooltip carries the
    /// sentence.</para></summary>
    public const string PolarDbUnitFromData = "—";

    /// <summary>The standard rows, after <see cref="PolarDbUnitFromData"/>. <c>dBi</c> and <c>dBd</c>
    /// are gains against the two conventional references, <c>dBsm</c> is a radar cross-section, and
    /// the two parenthesised ones are what <c>Trace.PatternDbUnit</c> composes for a linear cube read
    /// in decibels — a radiation intensity in W/sr and a field in V/m.</summary>
    private static readonly string[] StandardPolarDbUnits =
        ["dB", "dBi", "dBd", "dBsm", "dB(W/sr)", "dB(V/m)"];

    private List<string> _polarDbUnitOptions = [];

    /// <summary>
    /// The rows of the unit combo. <b>Open at the bottom</b>: a unit that arrived from a saved
    /// <c>.cdd</c> or from <c>circuitrf plot --db-unit</c> and is not one of the standard rows is
    /// appended, so opening the Plot Inspector on such a plot SHOWS what it is set to instead of
    /// presenting an empty combo and quietly rewriting the unit on the first click.
    /// </summary>
    public IReadOnlyList<string> PolarDbUnitOptions
    {
        get { if (_polarDbUnitOptions.Count == 0) RefreshPolarDbUnitOptions(); return _polarDbUnitOptions; }
    }

    private void RefreshPolarDbUnitOptions()
    {
        var rows = new List<string>(StandardPolarDbUnits.Length + 2) { PolarDbUnitFromData };
        rows.AddRange(StandardPolarDbUnits);
        string cur = _plot.PolarDbUnit;
        if (cur.Length > 0 && !rows.Contains(cur, StringComparer.Ordinal)) rows.Add(cur);

        if (_polarDbUnitOptions.Count == rows.Count &&
            _polarDbUnitOptions.SequenceEqual(rows, StringComparer.Ordinal)) return;
        _polarDbUnitOptions = rows;
        OnPropertyChanged(nameof(PolarDbUnitOptions));
    }

    /// <summary>What the combo is showing, which is <see cref="PolarDbUnit"/> with the empty string
    /// spelled as its own row.</summary>
    public string PolarDbUnitSelection
    {
        get => _plot.PolarDbUnit.Length == 0 ? PolarDbUnitFromData : _plot.PolarDbUnit;
        set => PolarDbUnit = (value is null || value == PolarDbUnitFromData) ? "" : value;
    }

    // ---- The 3D pattern surface (ANT-10 §3/§5) --------------------------
    //
    //  The dB controls above serve this plot too and are shown for it: a surface HAS a pattern scale
    //  by construction (its radius is dB above a floor), so the floor, the reference and the unit
    //  mean exactly what they mean on the cut. What is extra here is the VIEW — which is the whole
    //  of the new interaction, and §3 keeps it to three things: rotate, zoom, reset.

    /// <summary>The dB scale controls apply on a polar plot IN dB MODE, and on a surface always.</summary>
    public bool HasPatternScale => IsPolarDbPlot || IsSurfacePlot;

    /// <summary>
    /// <b>Whether the pattern ROW is shown at all</b>, which is a wider question than whether the
    /// dB SCALE controls inside it apply.
    ///
    /// <para><b>The row used to be gated on <see cref="HasPatternScale"/>, which made the dB-radial
    /// switch unreachable</b> (found 2026-09-11): that switch is the only way INTO dB mode, it
    /// lives in this row, and on a linear polar plot — the state every polar plot is created in —
    /// the row was hidden, so a polar plot could never be turned into a pattern plot from the GUI
    /// at all. The `.cdd` files that had pattern plots in them had been authored by the CLI. A
    /// control that enters a mode cannot be gated on already being in it.</para>
    /// </summary>
    public bool HasPatternControls => IsPolarPlot || IsSurfacePlot;

    /// <summary>
    /// Bearings printed around the rim — <see cref="Plot.ShowPolarAngleLabels"/>. Turning it on
    /// shrinks the disc to make room, so it goes through <see cref="ApplyRadialChange"/> like every
    /// other control that moves the framing rather than only the ink.
    /// </summary>
    public bool PolarAngleLabels
    {
        get => _plot.ShowPolarAngleLabels;
        set
        {
            if (_plot.ShowPolarAngleLabels == value) return;
            _plot.ShowPolarAngleLabels = value;
            OnPropertyChanged();
            ApplyRadialChange();
        }
    }

    /// <summary>
    /// <b>§3's named views, because "which way am I looking" is the question a 3D picture always
    /// raises.</b> The two principal planes are named by their own φ rather than "E-plane" and
    /// "H-plane" — see <see cref="SurfaceStandardView"/> for why naming them would be a guess about
    /// the antenna that the cube cannot settle.
    /// </summary>
    public IRelayCommand SurfaceViewIsoCommand       { get; }
    public IRelayCommand SurfaceViewBroadsideCommand { get; }
    public IRelayCommand SurfaceViewPhi0Command      { get; }
    public IRelayCommand SurfaceViewPhi90Command     { get; }

    /// <summary>Back to the isometric view AND to zoom 1 — the one control that undoes every gesture,
    /// which a rotate/zoom pair with no reset leaves a user without.</summary>
    public IRelayCommand SurfaceResetCommand { get; }

    public bool SurfaceViewIsIso       => IsSurfacePlot && _plot.SurfaceCamera.Is(SurfaceStandardView.Isometric);
    public bool SurfaceViewIsBroadside => IsSurfacePlot && _plot.SurfaceCamera.Is(SurfaceStandardView.Broadside);
    public bool SurfaceViewIsPhi0      => IsSurfacePlot && _plot.SurfaceCamera.Is(SurfaceStandardView.PhiZeroPlane);
    public bool SurfaceViewIsPhi90     => IsSurfacePlot && _plot.SurfaceCamera.Is(SurfaceStandardView.PhiNinetyPlane);

    public bool SurfaceShowAxes
    {
        get => _plot.SurfaceShowAxes;
        set { if (_plot.SurfaceShowAxes == value) return; _plot.SurfaceShowAxes = value; OnPropertyChanged(); Redraw(); }
    }

    /// <summary>The colour bar — see <see cref="Plot.SurfaceShowLegend"/> for why it is a setting
    /// and why it no longer disappears when the canvas gets small.</summary>
    public bool SurfaceShowLegend
    {
        get => _plot.SurfaceShowLegend;
        set { if (_plot.SurfaceShowLegend == value) return; _plot.SurfaceShowLegend = value; OnPropertyChanged(); Redraw(); }
    }

    public bool SurfaceShowGroundDisc
    {
        get => _plot.SurfaceShowGroundDisc;
        set { if (_plot.SurfaceShowGroundDisc == value) return; _plot.SurfaceShowGroundDisc = value; OnPropertyChanged(); Redraw(); }
    }

    public IReadOnlyList<ContourColorMap> SurfaceColorMaps { get; } = Enum.GetValues<ContourColorMap>();

    public ContourColorMap SurfaceColorMap
    {
        get => _plot.SurfaceColorMap;
        set { if (_plot.SurfaceColorMap == value) return; _plot.SurfaceColorMap = value; OnPropertyChanged(); Redraw(); }
    }

    /// <summary>Called by <c>PlotControl</c> after a drag or a wheel has moved the camera, so the
    /// view buttons stop showing themselves as the active one the moment the user leaves it.</summary>
    public void NotifySurfaceCameraChanged()
    {
        OnPropertyChanged(nameof(SurfaceViewIsIso));
        OnPropertyChanged(nameof(SurfaceViewIsBroadside));
        OnPropertyChanged(nameof(SurfaceViewIsPhi0));
        OnPropertyChanged(nameof(SurfaceViewIsPhi90));
    }

    private void SetSurfaceView(SurfaceStandardView view)
    {
        // The zoom is kept: a user who has zoomed in on a lobe is asking to keep looking at it from
        // a named direction, not to start over. Reset is the control that undoes the zoom.
        _plot.SurfaceCamera = PatternCamera.For(view, _plot.SurfaceCamera.Zoom);
        NotifySurfaceCameraChanged();
        Redraw();
    }

    private void Redraw() => PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);

    private void ApplyRadialChange()
    {
        // force: the window a pattern plot wants is its own unit disc, and the window a linear one
        // wants is framed on the data — so switching between them has to re-frame even when the
        // user had pinned the previous mode's.
        _plot.Autoscale(force: true);
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

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
        name is "node" or "branch" or "harmonic" or "tone" or "port" or "probe" or "mixIndex"
             or "opvar" or "i" or "j";

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

    /// <summary>
    /// <b>The cube a 3D plot should seed a trace on: the first one that can actually BE a
    /// surface.</b>
    ///
    /// <para>Owner, 2026-09-11: "+ Trace" on an empty 3D plot must make something appear. It did
    /// not — a far-field <c>.npy</c> carries an S network as well as its pattern cubes, so the seed
    /// took the S branch and produced S(1,1), which is a function of frequency and has no surface in
    /// it at all. The user clicked a button and the plot stayed empty but for a sentence.</para>
    ///
    /// <para><b>Declaration order, not a name.</b> The test is
    /// <see cref="SurfaceResolve.TryFindAngleAxes"/> — the same one the resolve and the picker's own
    /// greying use — and the first cube that passes it wins. For a far-field run that is
    /// <c>Etheta</c>, which is what was asked for; hard-coding that name would have been a fourth
    /// opinion about which quantities are directional, and would seed nothing at all for a run that
    /// publishes a pattern under any other name.</para>
    ///
    /// <para>Null when the source has no directional cube. The caller then falls back to the
    /// ordinary seed, so the plot still gets a trace whose refusal SAYS why — a button that does
    /// nothing is the thing being fixed here, and an inert button would be the same defect.</para>
    /// </summary>
    private static string? FirstSurfaceCubeName(DataSourceEntryViewModel e)
    {
        if (e.Data is not { } ds) return null;

        foreach (var group in ds.Groups)
            foreach (var (bareName, cube) in ds.CubesIn(group))
            {
                if (bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                if (!SurfaceResolve.TryFindAngleAxes(cube, out _, out _)) continue;
                return group is DataSet.DefaultGroup or DataSet.MeasurementsGroup
                    ? bareName
                    : $"{group}.{bareName}";
            }
        return null;
    }

    /// <summary>
    /// The seed slice for a SURFACE: both angle axes open, everything else pinned at index 0.
    ///
    /// <para>The resolve would open them anyway — it ignores the slice's roles for its two angles —
    /// so this is about what the trace SAYS. <see cref="BuildSeedSlice"/> would have written
    /// <c>farfield.Etheta[:, 0, 0, 1]</c> into the spec box of a plot drawn across every direction.
    /// Which of the two is X and which is the family follows the positional convention the spec
    /// parser reads back (last kept axis is X), so the text round-trips.</para>
    /// </summary>
    private static AxisSlice[] BuildSurfaceSeedSlice(DataCube cube, int thetaDim, int phiDim)
    {
        int xDim = Math.Max(thetaDim, phiDim), famDim = Math.Min(thetaDim, phiDim);
        var slice = new AxisSlice[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            var ax = cube.Axes[d];
            slice[d] =
                d == xDim   ? new AxisSlice(ax.Name, AxisRole.KeepAsX, 0)
              : d == famDim ? new AxisSlice(ax.Name, AxisRole.FamilyIterate, 0)
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
    public IRelayCommand SetPlotTypeSurfaceCommand { get; }

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

        // Every one of these is a breadcrumb (src/Ui/DataDisplay/Gesture.cs): "add a trace to a Smith
        // plot after a run" is the reported gesture nobody has been able to reproduce, and until now a
        // trail could show its FAILURE without showing the click.
        AddTraceCommand        = Gesture.Command("addTrace",        Seeding, AddTrace,        () => CanAddTrace);
        AddContourTraceCommand = Gesture.Command("addContourTrace", Seeding, AddContourTrace, () => CanAddContourTrace);
        AddSummaryTraceCommand = Gesture.Command("addSummaryTrace", Seeding, AddSummaryTrace, () => CanAddSummaryTrace);
        AutoFillSummaryCommand = Gesture.Command("autoFillSummary", Seeding, AutoFillSummary, () => CanAutoFillSummary);
        CloseCommand           = Gesture.Command("closeInspector",  () => _closeAction());

        SetPlotTypeRectCommand  = Gesture.Command("plotType=Rect",  () => PlotType = PlotType.Rect);
        SetPlotTypeSmithCommand = Gesture.Command("plotType=Smith", () => PlotType = PlotType.Smith);
        SetPlotTypePolarCommand = Gesture.Command("plotType=Polar", () => PlotType = PlotType.Polar);
        SetPlotTypeTableCommand = Gesture.Command("plotType=Table", () => PlotType = PlotType.Table);
        SetPlotTypeSurfaceCommand = Gesture.Command("plotType=Surface3D", () => PlotType = PlotType.Surface3D);

        SurfaceViewIsoCommand       = Gesture.Command("surfaceView=iso",       () => SetSurfaceView(SurfaceStandardView.Isometric));
        SurfaceViewBroadsideCommand = Gesture.Command("surfaceView=broadside", () => SetSurfaceView(SurfaceStandardView.Broadside));
        SurfaceViewPhi0Command      = Gesture.Command("surfaceView=phi0",      () => SetSurfaceView(SurfaceStandardView.PhiZeroPlane));
        SurfaceViewPhi90Command     = Gesture.Command("surfaceView=phi90",     () => SetSurfaceView(SurfaceStandardView.PhiNinetyPlane));
        SurfaceResetCommand         = Gesture.Command("surfaceView=reset",     () =>
        {
            _plot.SurfaceCamera = PatternCamera.Default;
            NotifySurfaceCameraChanged();
            Redraw();
        });

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
        else if (_plot.PlotType == PlotType.Surface3D &&
                 _library?.SelectedEntry is { } surfEntry &&
                 FirstSurfaceCubeName(surfEntry) is not null)
        {
            // BEFORE the S-network branch below, and that ORDER is the fix: a far-field .npy carries
            // an S network too, so the seed used to take that branch and produce S(1,1) — a function
            // of frequency, with no surface in it. See FirstSurfaceCubeName.
            trace = BuildSeedCubeTrace(surfEntry);
            trace.SourceRef  = DataSourceRef.Selected;
            trace.SourcePath = _library.SelectedDataSourceAbs;
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
        // On a 3D plot the best cube is the best DRAWABLE one, which is a different question — and
        // the fallback is the ordinary answer, so a source with no pattern in it still seeds a trace
        // that says why it cannot be drawn rather than leaving the button inert.
        string cubeName = (_plot.PlotType == PlotType.Surface3D ? FirstSurfaceCubeName(entry) : null)
                          ?? FirstPlottableCubeName(entry, _plot.PlotType == PlotType.Table)!;
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
        trace.Slice =
            _plot.PlotType == PlotType.Surface3D &&
            SurfaceResolve.TryFindAngleAxes(cube, out int thetaDim, out int phiDim)
                ? BuildSurfaceSeedSlice(cube, thetaDim, phiDim)
                : BuildSeedSlice(cube);

        // First-add nicety on Rect: only COMPLEX cubes get an auto-transform (so they don't render
        // <invalid>); REAL cubes are shown raw — no annoying "mag". (Shared with the signal-switch path.)
        //
        // On a PATTERN plot it is not a nicety but the whole difference between a picture and a
        // refusal: the radius is decibels, so a trace born with no transform is either
        // <invalid> (a complex field) or a linear curve drawn against a dB scale (a real
        // intensity). See TraceRowViewModel.DefaultPatternTransform.
        trace.Transform = TraceRowViewModel.DefaultTransformFor(
            cube, _plot.PlotType, cubeName, HasPatternScale);

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
    /// Resolves a cube-bound trace's SourcePath+CubeName+Slice against the library and fills its
    /// points. Static so DataDisplayViewModel can call it during load before an inspector exists.
    ///
    /// <para>The resolution itself is <see cref="TraceResolve.ResolveCubeTrace"/> below the UI
    /// firewall (RND-4 R-rnd4-2); this is the library adapter around it, and is what makes
    /// <c>circuitrf render</c> resolve a trace the way this window does rather than the way a
    /// second implementation would.</para>
    /// </summary>
    internal static void TrySetCubeData(Trace t, DataSourceLibraryViewModel? library,
                                        PlotType plotType, FreqUnit freqUnit)
        => TraceResolve.ResolveCubeTrace(t, new LibraryDataSources(library), plotType, freqUnit);

    /// <summary>
    /// What an "add a trace" click was working from: the plot it lands on, how many traces are
    /// already there (the add path branches on it — clone the last, or seed from the source), and the
    /// selected source. That triple is the reported gesture, and no trail has ever carried it.
    /// </summary>
    private string? Seeding()
        => $"plot={_plot.PlotType} traces={_plot.Traces.Count} "
         + $"src='{TraceResolve.SafeFileName(_library?.SelectedDataSourceAbs)}'";

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
    /// A trace's pattern branch was flipped to the negative side of broadside — see
    /// <see cref="Trace.MirrorPatternAngle"/>. It moves POINTS, not framing, but it goes through
    /// <see cref="ApplyRadialChange"/> anyway: the mirrored branch doubles the drawn extent about
    /// the centre, and a window pinned to one quadrant would clip the half that just appeared.
    /// </summary>
    public void OnTracePatternMirrorChanged() => ApplyRadialChange();

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
            SummaryResolve.Rebuild(_plot, null, "");
            PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
            return;
        }

        var entry = _library?.SelectedEntry;
        var ds    = entry?.Data;

        // Harmonic guard (Phase 7.5f): silently noted; summary uses fundamental only.
        // No UI warning seam available in this VM — the detection is the durable value.
        if (ds is not null && HasHarmonicLoadCubes(ds) && !_harmonicWarned)
            _harmonicWarned = true;

        // Group-aware: LP cubes may be top level (flat .spl) or under an analysis group (LP run.npy).
        // When more than one loadpull view exists, honor the user's chosen analysis (persisted on the
        // Plot); otherwise default to the first. Keep the analysis picker in sync with the views —
        // the picker is why the group is resolved HERE and handed down rather than inside
        // SummaryResolve: two derivations of one choice is how the table and the combo disagree.
        var lpViews = ds is null
            ? (IReadOnlyList<LoadpullRecognition.LoadpullView>)Array.Empty<LoadpullRecognition.LoadpullView>()
            : LoadpullRecognition.FindLoadpullViews(ds);
        string lpGroup = ContourResolve.GroupFor(lpViews, _plot.SummaryLoadpullGroup);
        if (ds is not null) RebuildSummaryAnalysisList(lpViews, lpGroup);

        if (SummaryResolve.Rebuild(_plot, ds, lpGroup))
            NotifySummaryColumnsCompression();

        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySummaryColumnsCompression()
    {
        foreach (var vm in Traces)
            vm.RaiseSummaryCompressionChanged();
    }

}
