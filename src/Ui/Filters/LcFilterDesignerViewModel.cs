using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Elaboration;
using CircuitRF.Ui.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NumFlat;
using RfCore;
using SkiaSharp;

namespace CircuitRF.Ui.Filters;

/// <summary>A response family as the specification names it. <see cref="Value"/> is what the Filter block stores.</summary>
public sealed record FilterResponseChoice(FilterResponse Value, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// One parameter Apply to Filter writes: its name, its expression and, for a quantity whose unit
/// the window chooses (the edges, Ap), that unit. Null leaves the parameter's unit as it is.
/// </summary>
public sealed record FilterAssignment(string Name, string Expression, string? Unit = null)
{
    /// <summary>Name and expression, for callers that do not need the unit.</summary>
    public void Deconstruct(out string name, out string expression)
    {
        name = Name;
        expression = Expression;
    }
}

/// <summary>One row of the Elliptic type picker.</summary>
public sealed record EllipticTypeChoice(EllipticType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How the response plots pick their frequency span.</summary>
public enum FilterPlotRangeMode
{
    /// <summary>Outer mask edges widened by ± band percent — Match Designer's rule.</summary>
    BandPercent,

    /// <summary>Explicit Fmin / Fmax in the current frequency unit.</summary>
    Manual,
}

/// <summary>One row of the Plot range mode ComboBox.</summary>
public sealed record FilterPlotRangeChoice(FilterPlotRangeMode Value, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// The LC Filter Designer. The specification is the one on the assistant screenshots — source and
/// load, first element, order, the mask frequencies, Ap and As, and the response family — and the
/// curves are the Data Display's own plots, filled from the ideal response and from the ladder run
/// through the S-parameter engine.
/// </summary>
public sealed partial class LcFilterDesignerViewModel : ObservableObject
{
    /// <summary>The response list, in the names the specification uses.</summary>
    public IReadOnlyList<FilterResponseChoice> ResponseChoices { get; } =
    [
        new(FilterResponse.Butterworth, "Maximally Flat"),
        new(FilterResponse.Chebyshev, "Chebyshev"),
        new(FilterResponse.InvChebyshev, "Inverse Chebyshev"),
        new(FilterResponse.Elliptic, "Elliptic"),
        new(FilterResponse.Bessel, "Bessel-Thomson"),
        new(FilterResponse.Gaussian, "Gaussian"),
    ];

    /// <summary>Lowpass, highpass, bandpass, bandstop.</summary>
    public IReadOnlyList<NetworkForm> Forms { get; } =
        [NetworkForm.Lowpass, NetworkForm.Highpass, NetworkForm.Bandpass, NetworkForm.Bandstop];

    /// <summary>Series or parallel first arm.</summary>
    public IReadOnlyList<FilterFirstElement> FirstElements { get; } = Enum.GetValues<FilterFirstElement>();

    /// <summary>The elliptic types: Auto is a at odd order and c at even order.</summary>
    public IReadOnlyList<EllipticTypeChoice> EllipticTypeChoices { get; } =
    [
        new(EllipticType.Auto, "Auto"),
        new(EllipticType.A, "a — classical"),
        new(EllipticType.B, "b — even, unequal terminations"),
        new(EllipticType.C, "c — even, equal terminations"),
    ];

    /// <summary>Exact, or an IEC series.</summary>
    public IReadOnlyList<FilterSeriesKind> SeriesKinds { get; } = Enum.GetValues<FilterSeriesKind>();

    /// <summary>Compute the smallest order that meets the mask, or type one.</summary>
    public IReadOnlyList<FilterOrderMode> OrderModes { get; } =
        [FilterOrderMode.Minimum, FilterOrderMode.Specify];

    /// <summary>± band around the mask, or a typed Fmin / Fmax.</summary>
    public IReadOnlyList<FilterPlotRangeChoice> PlotRangeChoices { get; } =
    [
        new(FilterPlotRangeMode.BandPercent, "± band"),
        new(FilterPlotRangeMode.Manual, "Manual"),
    ];

    /// <summary>The frequency unit the four edge boxes are typed in.</summary>
    public IReadOnlyList<string> Units { get; } = ["Hz", "kHz", "MHz", "GHz"];

    /// <summary>Per-element library picks the table's Series / Part dropdowns wrote.</summary>
    private readonly Dictionary<string, (FilterLibrarySeries Series, FilterPart Part)> _partOverrides = new(StringComparer.Ordinal);
    /// <summary>The Data Display the two plots are hosted on. Markers, colours and the clipboard
    /// are its own.</summary>
    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(),
                                                        addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>The container holding <see cref="MagnitudePlot"/>.</summary>
    public PlotContainerViewModel MagnitudeContainer { get; }

    /// <summary>The container holding <see cref="PhasePlot"/>.</summary>
    public PlotContainerViewModel PhaseContainer { get; }

    /// <summary>|S21| of the ideal response and of the ladder.</summary>
    public Plot MagnitudePlot => MagnitudeContainer.PlotVM.Plot;

    /// <summary>Phase and group delay of the ladder.</summary>
    public Plot PhasePlot => PhaseContainer.PlotVM.Plot;

    /// <summary>Filter-response plot is on. The phase plot takes the whole pane when this is off.</summary>
    [ObservableProperty] private bool _showMagnitude = true;

    /// <summary>Phase / group-delay plot is on. Off at startup — the response plot fills the pane alone.</summary>
    [ObservableProperty] private bool _showPhase;

    /// <summary>The realization, one row per element.</summary>
    public ObservableCollection<FilterElementRow> Elements { get; } = [];

    /// <summary>Inductor series found in the workspace's vendor libraries.</summary>
    public ObservableCollection<FilterLibrarySeries> InductorSeries { get; } = [];

    /// <summary>Capacitor series found in the workspace's vendor libraries.</summary>
    public ObservableCollection<FilterLibrarySeries> CapacitorSeries { get; } = [];

    /// <summary>The netlist of the realization last built, snapped values included.</summary>
    public string Cnl { get; private set; } = "";

    /// <summary>Set when the assistant was opened on a placed Filter, so Apply can write it back.</summary>
    public EditableComponent? Target { get; set; }

    /// <summary>The schematic that owns <see cref="Target"/>.</summary>
    public global::CircuitRF.Ui.ViewModels.SchematicViewModel? Schematic { get; set; }

    /// <summary>A workspace root, so Load library series has somewhere to look.</summary>
    public string? WorkspaceRoot { get; set; }

    [ObservableProperty] private FilterResponse _response = FilterResponse.Butterworth;
    [ObservableProperty] private FilterResponseChoice? _responseChoice;
    [ObservableProperty] private NetworkForm _form = NetworkForm.Bandpass;
    [ObservableProperty] private FilterFirstElement _first = FilterFirstElement.Parallel;
    [ObservableProperty] private EllipticTypeChoice? _ellipticTypeChoice;
    [ObservableProperty] private bool _isElliptic;

    /// <summary>
    /// True when the response reads the Ripple box — Chebyshev and Elliptic. The other families
    /// build at a fixed internal ripple, so the box is disabled rather than silently ignored.
    /// </summary>
    [ObservableProperty] private bool _rippleRelevant;
    [ObservableProperty] private FilterSeriesKind _series = FilterSeriesKind.Exact;
    [ObservableProperty] private FilterOrderMode _orderMode = FilterOrderMode.Minimum;
    /// <summary>The prefix each edge box is typed in (owner, 2026-11-21: one per box, not one for all).</summary>
    [ObservableProperty] private string _unitA = "GHz";
    [ObservableProperty] private string _unitB = "GHz";
    [ObservableProperty] private string _unitC = "GHz";
    [ObservableProperty] private string _unitD = "GHz";

    /// <summary>The prefixes of the manual plot range's Fmin and Fmax boxes.</summary>
    [ObservableProperty] private string _unitFmin = "GHz";
    [ObservableProperty] private string _unitFmax = "GHz";

    /// <summary>
    /// The unit the plot's frequency axis is drawn in: the passband edge's own box (Fp, or Fp1 on a
    /// band form). Setting it sets every box's prefix at once, converting each box's text so the
    /// frequency it states is unchanged.
    /// </summary>
    public string Unit
    {
        get => Form switch
        {
            NetworkForm.Lowpass or NetworkForm.Bandstop => UnitA,
            _                                           => UnitB,
        };
        set
        {
            UnitA = value; UnitB = value; UnitC = value; UnitD = value;
            UnitFmin = value; UnitFmax = value;
        }
    }
    [ObservableProperty] private string _orderText = "5";
    [ObservableProperty] private string _apText = "3";
    [ObservableProperty] private string _rippleText = "0.1";
    [ObservableProperty] private string _asText = "20";
    [ObservableProperty] private string _sourceText = "50";
    [ObservableProperty] private string _loadText = "50";
    [ObservableProperty] private string _faText = "0.5";
    [ObservableProperty] private string _fbText = "1";
    [ObservableProperty] private string _fcText = "2";
    [ObservableProperty] private string _fdText = "2.5";
    [ObservableProperty] private bool _showC;
    [ObservableProperty] private bool _showD;
    [ObservableProperty] private bool _orderEditable;
    [ObservableProperty] private string _labelA = "Fs1";
    [ObservableProperty] private string _labelB = "Fp1";
    [ObservableProperty] private string _labelC = "Fp2";
    [ObservableProperty] private string _labelD = "Fs2";
    [ObservableProperty] private Avalonia.Media.IBrush _swatchA = StopSwatch;
    [ObservableProperty] private Avalonia.Media.IBrush _swatchB = PassSwatch;
    [ObservableProperty] private Avalonia.Media.IBrush _swatchC = PassSwatch;
    [ObservableProperty] private Avalonia.Media.IBrush _swatchD = StopSwatch;
    private static readonly Avalonia.Media.IBrush PassSwatch =
        Avalonia.Media.Brush.Parse(FilterMaskColors.PassBrush);
    private static readonly Avalonia.Media.IBrush StopSwatch =
        Avalonia.Media.Brush.Parse(FilterMaskColors.StopBrush);
    [ObservableProperty] private string _tipA = "";
    [ObservableProperty] private string _tipB = "";
    [ObservableProperty] private string _tipC = "";
    [ObservableProperty] private string _tipD = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _designInfo = "";
    [ObservableProperty] private FilterLibrarySeries? _inductorChoice;
    [ObservableProperty] private FilterLibrarySeries? _capacitorChoice;
    [ObservableProperty] private bool _libraryAvailable;

    /// <summary>How far outside the mask the plots run, as a percent of the outer span (Match's ± band).</summary>
    [ObservableProperty] private string _plotBandPercentText = "10";

    /// <summary>How many frequency points the plots run at.</summary>
    [ObservableProperty] private string _plotPointsText = "401";

    /// <summary>± band around the mask edges, or an explicit Fmin / Fmax.</summary>
    [ObservableProperty] private FilterPlotRangeMode _plotRangeMode = FilterPlotRangeMode.BandPercent;

    /// <summary>The ComboBox selection for <see cref="PlotRangeMode"/>.</summary>
    [ObservableProperty] private FilterPlotRangeChoice? _plotRangeChoice;

    /// <summary>True when the Fmin / Fmax boxes are the plot span.</summary>
    [ObservableProperty] private bool _plotRangeIsManual;
    /// <summary>Manual plot start, in the current frequency unit.</summary>
    [ObservableProperty] private string _plotFminText = "0.4";

    /// <summary>Manual plot stop, in the current frequency unit.</summary>
    [ObservableProperty] private string _plotFmaxText = "2.8";


    /// <summary>True while Design rebuilds rows — suppresses per-row redesign loops.</summary>
    private bool _fillingRows;

    /// <summary>Last successful design — plot-range edits re-simulate over a new grid without
    /// re-synthesising the ladder.</summary>
    private Spec? _lastSpec;
    private FilterNetwork? _lastIdeal;

    /// <summary>The ideal response of the last design, the curve the block reproduces.</summary>
    internal FilterNetwork? LastIdeal => _lastIdeal;
    private double _lastRs;
    private double _lastRl;
    /// <summary>Ap — passband attenuation used for the order mask.</summary>
    public string TipAp { get; } = "Passband attenuation, in dB. Used with As and the edges to size the order.";

    /// <summary>Ripple — Chebyshev / elliptic prototype ripple.</summary>
    public string TipRipple { get; } = "Passband ripple for Chebyshev and elliptic prototypes, in dB.";

    /// <summary>As — stopband attenuation floor.</summary>
    public string TipAs { get; } = "Stopband attenuation, in dB.";

    /// <summary>Tooltip for the elliptic type picker.</summary>
    public string TipEllipticType { get; } =
        "Elliptic type. An even order of type a has no LC ladder; b moves its highest transmission " +
        "zero to infinity (unequal terminations) and c also moves the lowest reflection zero to DC " +
        "(equal terminations). Auto is a at odd order and c at even order.";

    /// <summary>The ladder as the Match Designer's own schematic canvas draws it.</summary>
    [ObservableProperty] private MatchLadderLayout _ladder = MatchLadderLayout.Empty;

    /// <summary>Dashed Ap/As and Fp/Fs lines on the magnitude plot.</summary>
    internal FilterSpecMaskOverlay MagnitudeMask { get; private set; } = null!;

    /// <summary>True when a placed Filter can take Apply.</summary>
    public bool CanApply => Target is { Symbol: SymbolKind.Filter } && Schematic is not null;

    /// <summary>
    /// The schematic Place filter / Place component go into when the window was opened standalone
    /// (Tools ▸ LC Filter Designer): the workspace's last active schematic, read when the button is
    /// pressed, not when the window opened. Opened on a Filter, it is that Filter's schematic.
    /// </summary>
    public Func<global::CircuitRF.Ui.ViewModels.SchematicViewModel?>? SchematicProvider { get; set; }

    /// <summary>Where a placement lands right now, or null when there is no schematic.</summary>
    public global::CircuitRF.Ui.ViewModels.SchematicViewModel? PlacementSchematic =>
        Schematic ?? SchematicProvider?.Invoke();

    /// <summary>True when Place filter and Place component can run.</summary>
    [ObservableProperty] private bool _canPlace;

    /// <summary>Why they cannot, or what they do — the buttons' tooltip.</summary>
    [ObservableProperty] private string _placeTip = "";

    /// <summary>Whether Place filter replaces the Filter the window was opened on (phase 2 of FD-P).</summary>
    [ObservableProperty] private bool _replaceTarget;

    /// <summary>The toolbar's Replace toggle.</summary>
    [RelayCommand]
    private void ToggleReplace() => ReplaceTarget = CanReplaceTarget && !ReplaceTarget;

    /// <summary>True when the window was opened on a placed Filter, so Place filter can replace it.</summary>
    public bool CanReplaceTarget => Target is { Symbol: SymbolKind.Filter } && Schematic is not null;

    /// <summary>
    /// Re-reads whether a placement can run: after every design, and when the window is activated
    /// (a schematic may have been opened or closed behind it).
    /// </summary>
    public void RefreshPlacement()
    {
        if (_lastSpec is null)
        {
            CanPlace = false;
            PlaceTip = Status.Length > 0 ? Status : "There is no design to place.";
        }
        else if (PlacementSchematic is null)
        {
            CanPlace = false;
            PlaceTip = "There is no schematic to place into. Open a schematic and try again.";
        }
        else
        {
            CanPlace = true;
            PlaceTip = "Places the design on the schematic: it follows the pointer, a click puts it down, "
                     + "Escape drops it.";
        }
    }

    /// <summary>
    /// Place filter: the realised ladder at the values the table shows, ending in two wire stubs
    /// labelled IN and OUT (no terminations), armed as a paste ghost on the schematic. With
    /// <see cref="ReplaceTarget"/> on a Filter the window was opened on, the ladder takes the
    /// block's place instead (<see cref="ReplaceFilter"/>).
    /// </summary>
    /// <returns>What happened, for the status line.</returns>
    public string PlaceFilter()
    {
        RefreshPlacement();
        if (!CanPlace || PlacementSchematic is not { } schematic) return PlaceTip;
        if (ReplaceTarget && CanReplaceTarget) return ReplaceFilter();
        var (inName, outName) = FilterPlacement.EndNames(schematic.EditModel);
        var fragment = FilterPlacement.LadderFragment(Ladder, inName, outName);
        return schematic.ArmFragmentPlacement(fragment)
            ? $"Click on the schematic to place the ladder. Its ends are the nets {inName} and {outName}."
            : "There is no ladder to place.";
    }

    /// <summary>
    /// Place component: one Filter block carrying this specification and every Designer setting —
    /// written by <see cref="AppliedParameters"/>, the function Apply uses, so the two cannot disagree.
    /// </summary>
    public string PlaceComponent()
    {
        RefreshPlacement();
        if (!CanPlace || PlacementSchematic is not { } schematic) return PlaceTip;
        if (AppliedParameters(FilterPlacement.DefaultParameters()) is not { } parameters)
            return "There is no design to place.";
        return schematic.ArmFragmentPlacement(FilterPlacement.ComponentFragment(parameters))
            ? "Click on the schematic to place the Filter."
            : "There is no design to place.";
    }

    /// <summary>
    /// Replaces the Filter the window was opened on with the ladder, as ONE undo step: the ladder
    /// goes where the block was, its two ends wired to the block's two pins, and the block is
    /// removed. Phase 2 of FD-P.
    /// </summary>
    public string ReplaceFilter()
    {
        if (Target is not { Symbol: SymbolKind.Filter } target || Schematic is not { } schematic)
            return "The window was not opened on a placed Filter.";
        if (!schematic.EditModel.Components.Contains(target))
            return "The Filter this window was opened on is no longer on the schematic.";
        if (Ladder.Elements.Count == 0) return "There is no ladder to place.";
        var fragment = FilterPlacement.ReplacementFragment(schematic.EditModel, target, Ladder);
        schematic.Execute(new global::CircuitRF.Ui.Commands.Schematic.FilterReplaceCommand(
            schematic.EditModel, target, fragment, ids => schematic.Selection.SetAll(ids)));
        Target = null;
        Schematic = null;
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanReplaceTarget));
        ReplaceTarget = false;
        SchematicProvider ??= () => schematic;
        RefreshPlacement();
        return $"Replaced {target.InstanceName} with the ladder. Undo puts the Filter back.";
    }

    public LcFilterDesignerViewModel()
    {
        ResponseChoice = ResponseChoices[0];
        EllipticTypeChoice = EllipticTypeChoices[0];
        PlotRangeChoice = PlotRangeChoices[0];
        OrderEditable = false;
        PlotRangeIsManual = false;
        MagnitudeContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, 0, 0, 640, 280);
        PhaseContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, 0, 280, 640, 220);
        MagnitudePlot.Axes.LockedPanning = true;
        PhasePlot.Axes.LockedPanning = true;
        MagnitudeMask = new FilterSpecMaskOverlay(MagnitudePlot);
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
        ApplyLabels();
    }

    partial void OnFormChanged(NetworkForm value) => ApplyLabels();
    partial void OnResponseChanged(FilterResponse value)
    {
        var match = ResponseChoices.First(c => c.Value == value);
        if (!Equals(ResponseChoice, match)) ResponseChoice = match;
        OrderEditable = OrderMode == FilterOrderMode.Specify
                        || value is FilterResponse.Bessel or FilterResponse.Gaussian;
        IsElliptic = value == FilterResponse.Elliptic;
        RippleRelevant = value is FilterResponse.Chebyshev or FilterResponse.Elliptic;
        Design();
    }

    partial void OnEllipticTypeChoiceChanged(EllipticTypeChoice? value) => Design();

    partial void OnResponseChoiceChanged(FilterResponseChoice? value)
    {
        if (value is not null && value.Value != Response) Response = value.Value;
    }

    partial void OnFirstChanged(FilterFirstElement value) => Design();
    partial void OnSeriesChanged(FilterSeriesKind value) => Design();
    // A prefix change rescales its own box, so the frequency it states is unchanged, and redraws.
    partial void OnUnitAChanged(string? oldValue, string newValue) { FaText = ConvertEdge(FaText, ScaleOf(oldValue ?? newValue), ScaleOf(newValue)); Design(); }
    partial void OnUnitBChanged(string? oldValue, string newValue) { FbText = ConvertEdge(FbText, ScaleOf(oldValue ?? newValue), ScaleOf(newValue)); Design(); }
    partial void OnUnitCChanged(string? oldValue, string newValue) { FcText = ConvertEdge(FcText, ScaleOf(oldValue ?? newValue), ScaleOf(newValue)); Design(); }
    partial void OnUnitDChanged(string? oldValue, string newValue) { FdText = ConvertEdge(FdText, ScaleOf(oldValue ?? newValue), ScaleOf(newValue)); Design(); }
    partial void OnUnitFminChanged(string? oldValue, string newValue) { PlotFminText = ConvertEdge(PlotFminText, ScaleOf(oldValue ?? newValue), ScaleOf(newValue)); UpdatePlots(); }
    partial void OnUnitFmaxChanged(string? oldValue, string newValue) { PlotFmaxText = ConvertEdge(PlotFmaxText, ScaleOf(oldValue ?? newValue), ScaleOf(newValue)); UpdatePlots(); }

    partial void OnPlotRangeModeChanged(FilterPlotRangeMode value)
    {
        PlotRangeIsManual = value == FilterPlotRangeMode.Manual;
        var match = PlotRangeChoices.First(c => c.Value == value);
        if (!Equals(PlotRangeChoice, match)) PlotRangeChoice = match;
        UpdatePlots();
    }

    partial void OnPlotRangeChoiceChanged(FilterPlotRangeChoice? value)
    {
        if (value is not null && value.Value != PlotRangeMode) PlotRangeMode = value.Value;
    }
    partial void OnOrderModeChanged(FilterOrderMode value)
    {
        OrderEditable = value == FilterOrderMode.Specify
                        || Response is FilterResponse.Bessel or FilterResponse.Gaussian;
        Design();
    }
    partial void OnInductorChoiceChanged(FilterLibrarySeries? value) => Design();
    partial void OnCapacitorChoiceChanged(FilterLibrarySeries? value) => Design();

    /// <summary>The edge labels follow the form. Two edges for lowpass and highpass, four otherwise.</summary>
    public void ApplyLabels()
    {
        switch (Form)
        {
            case NetworkForm.Lowpass:
                LabelA = "Fp"; LabelB = "Fs"; ShowC = false; ShowD = false;
                (SwatchA, SwatchB) = (PassSwatch, StopSwatch);
                TipA = "Passband frequency."; TipB = "Stopband frequency edge.";
                TipC = TipD = "";
                break;
            case NetworkForm.Highpass:
                LabelA = "Fs"; LabelB = "Fp"; ShowC = false; ShowD = false;
                (SwatchA, SwatchB) = (StopSwatch, PassSwatch);
                TipA = "Stopband frequency edge."; TipB = "Passband frequency.";
                TipC = TipD = "";
                break;
            case NetworkForm.Bandstop:
                LabelA = "Fp1"; LabelB = "Fs1"; LabelC = "Fs2"; LabelD = "Fp2";
                (SwatchA, SwatchB, SwatchC, SwatchD) = (PassSwatch, StopSwatch, StopSwatch, PassSwatch);
                ShowC = true; ShowD = true;
                TipA = "Lower passband frequency."; TipB = "Lower stopband frequency edge.";
                TipC = "Upper stopband frequency edge."; TipD = "Upper passband frequency.";
                break;
            default:
                LabelA = "Fs1"; LabelB = "Fp1"; LabelC = "Fp2"; LabelD = "Fs2";
                (SwatchA, SwatchB, SwatchC, SwatchD) = (StopSwatch, PassSwatch, PassSwatch, StopSwatch);
                ShowC = true; ShowD = true;
                TipA = "Lower stopband frequency edge."; TipB = "Lower passband frequency.";
                TipC = "Upper passband frequency."; TipD = "Upper stopband frequency edge.";
                break;
        }
        OrderEditable = OrderMode == FilterOrderMode.Specify
                        || Response is FilterResponse.Bessel or FilterResponse.Gaussian;
        Design();
    }

    /// <summary>
    /// Rebuilds the realization and redraws both plots. Every edit calls this; the work runs off
    /// the UI thread after a short debounce, and only the latest request's result is applied —
    /// a superseded one is cancelled or dropped.
    /// </summary>
    [RelayCommand]
    public void Design() => _ = DesignAsync();

    private CancellationTokenSource? _designCts;
    private int _designGeneration;

    /// <summary>The debounce before a design starts, so a burst of edits designs once.</summary>
    internal const int DesignDebounceMs = 80;

    /// <summary>
    /// The design pipeline. Reads the boxes on the calling (UI) thread, synthesises and simulates
    /// on the thread pool, and applies the result back on the calling thread.
    /// </summary>
    public async Task DesignAsync(int debounceMs = DesignDebounceMs)
    {
        _designCts?.Cancel();
        var cts = new CancellationTokenSource();
        _designCts = cts;
        int generation = ++_designGeneration;
        if (debounceMs > 0)
        {
            try { await Task.Delay(debounceMs, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
        if (generation != _designGeneration) return;

        if (!TrySpec(out var spec, out string? error))
        {
            Elements.Clear();
            Cnl = "";
            Ladder = MatchLadderLayout.Empty;
            _lastSpec = null;
            _lastIdeal = null;
            Status = error ?? "";
            DesignInfo = "";
            ClearPlots();
            ShowMaskAlone();
            RefreshPlacement();
            return;
        }

        var freqs = Frequencies(spec);
        var snap = new SnapInputs(Series, new Dictionary<string, (FilterLibrarySeries, FilterPart)>(_partOverrides),
                                  InductorChoice, CapacitorChoice);
        Status = "Designing…";
        DesignOutcome outcome;
        try
        {
            outcome = await Task.Run(() => Compute(spec, freqs, snap, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (generation != _designGeneration) return;
        Apply(spec, outcome);
    }

    /// <summary>What one design produced, ready to be shown.</summary>
    private sealed record DesignOutcome(
        FilterNetwork? Ideal, FilterRealization? Real, List<FilterElement>? Used, List<SnapRow> Rows,
        string Cnl, SNP? IdealSnp, SNP? LadderSnp, double Rs, double Rl, string Status, bool Failed);

    /// <summary>The window state the snap reads, copied so the pool thread never touches the view model.</summary>
    private sealed record SnapInputs(
        FilterSeriesKind Series, Dictionary<string, (FilterLibrarySeries Series, FilterPart Part)> Overrides,
        FilterLibrarySeries? Inductors, FilterLibrarySeries? Capacitors);

    /// <summary>One table row's content, before it becomes a <see cref="FilterElementRow"/>.</summary>
    private sealed record SnapRow(
        FilterElement Exact, double Used, string SeriesName, string PartName, string Tolerance,
        FilterLibrarySeries? Series, FilterPart? Chosen);

    private static DesignOutcome Compute(Spec spec, double[] freqs, SnapInputs snap, CancellationToken ct)
    {
        // The ideal transfer can fail at an order the ladder still reaches; prefer the ladder over a
        // blank window.
        FilterNetwork? ideal = null;
        string idealNote = "";
        try
        {
            ideal = FilterNetwork.Create(spec.Response, spec.Form, spec.Order,
                                          spec.FcHz, spec.F1Hz, spec.F2Hz, spec.RippleDb, spec.AsDb, 0,
                                          spec.EllipticType);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            idealNote = e.Message;
        }
        ct.ThrowIfCancellationRequested();

        var built = FilterLadder.Synthesize(spec.Response, spec.Form, spec.Order,
                                             spec.FcHz, spec.F1Hz, spec.F2Hz, spec.RippleDb,
                                             spec.SourceOhms, spec.First, spec.AsDb, spec.EllipticType);
        if (ideal is null && !built.Ok)
            return new DesignOutcome(null, null, null, [], "", null, null, 0, 0,
                                     idealNote.Length > 0 ? idealNote : (built.Refusal?.Message ?? "No ladder."),
                                     Failed: true);
        ct.ThrowIfCancellationRequested();

        double rs = spec.SourceOhms;
        double rl = built.Realization?.LoadOhms ?? spec.LoadOhms;
        SNP? idealSnp = ideal is null ? null : IdealSnp(ideal, freqs, rs, rl);

        string note = "", cnl = "";
        SNP? ladderSnp = null;
        List<FilterElement>? used = null;
        var rows = new List<SnapRow>();
        if (built.Realization is { } real)
        {
            used = Snap(real, snap, rows);
            cnl = FilterLadder.ToCnl(used, real.SourceOhms, real.LoadOhms);
            rs = real.SourceOhms;
            rl = real.LoadOhms;
            try
            {
                ladderSnp = Run(cnl, freqs, real.SourceOhms);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
            {
                note = e.Message;
            }
            if (Math.Abs(real.LoadOhms - spec.LoadOhms) / spec.LoadOhms > 0.01)
                note = $"The ladder's load is {real.LoadOhms:0.###} Ω. The requested load is " +
                       $"{spec.LoadOhms:0.###} Ω, which Place as Filter keeps; the ladder is built " +
                       "for the ratio the family produces. " + note;
            if (real.Note.Length > 0) note = real.Note + " " + note;
        }
        else
        {
            note = built.Refusal?.Message ?? "";
        }
        return new DesignOutcome(ideal, built.Realization, used, rows, cnl, idealSnp, ladderSnp, rs, rl,
                                 note.Length > 0 ? note.Trim() : idealNote, Failed: false);
    }

    /// <summary>Shows one design. Runs on the UI thread.</summary>
    private void Apply(Spec spec, DesignOutcome r)
    {
        Elements.Clear();
        Cnl = r.Cnl;
        Ladder = MatchLadderLayout.Empty;
        _lastSpec = null;
        _lastIdeal = null;
        if (r.Failed)
        {
            Status = r.Status;
            DesignInfo = "";
            ClearPlots();
            RefreshPlacement();
            return;
        }

        if (r.Real is { } real && r.Used is { } used)
        {
            FillRows(r.Rows);
            Ladder = LadderOf(used, real.Arms, real.SourceOhms, real.LoadOhms);
        }

        _lastSpec = spec;
        _lastIdeal = r.Ideal;
        _lastRs = r.Rs;
        _lastRl = r.Rl;

        SNP? response = r.LadderSnp ?? r.IdealSnp;
        double minIl = response is null ? 0 : MinimumInsertionLoss(response, Frequencies(spec), spec);
        DesignInfo = $"Order: {spec.Order}" +
                     (spec.OrderFromMinimum ? " (minimum for the mask)" : "") +
                     (spec.Response == FilterResponse.Elliptic
                         ? $"\nElliptic type: {EllipticTypes.Letter(spec.EllipticType)}" : "") +
                     $"\nMinimum insertion loss: {minIl:0.0000} dB" +
                     (r.Real is { } rr ? $"\nLoad: {rr.LoadOhms:0.###} Ω" : "");
        Status = r.Status;
        BuildPlots(r.IdealSnp, r.LadderSnp, spec);
        RefreshPlacement();
    }

    /// <summary>Indexes the workspace's vendor libraries into inductor and capacitor series.</summary>
    [RelayCommand]
    public void LoadLibraries()
    {
        InductorSeries.Clear();
        CapacitorSeries.Clear();
        var notes = new List<string>();
        foreach (var s in FilterValues.LibrarySeries(WorkspaceRoot, notes))
        {
            if (s.Inductors) InductorSeries.Add(s);
            else CapacitorSeries.Add(s);
        }
        LibraryAvailable = InductorSeries.Count > 0 || CapacitorSeries.Count > 0;
        Status = LibraryAvailable
            ? $"{InductorSeries.Count} inductor series, {CapacitorSeries.Count} capacitor series."
            : notes.FirstOrDefault()
              ?? "This workspace has no vendor library with inductor or capacitor series.";
        if (LibraryAvailable) Design();
    }

    /// <summary>
    /// Reads a placed Filter's parameters into the window: the block's own specification, and the
    /// Designer settings Apply stores beside it (mask edges, Ap, order mode, first element, value
    /// series and library picks). A Filter placed before those existed carries none of them, and
    /// each falls back to what the window derived before: Ap from Ripple, the mask's other edges
    /// from the cutoff, Specify for the order (the block's Order is what it is).
    /// </summary>
    public void SeedFrom(EditableComponent component)
    {
        EditableParameter? Param(string name) => component.Parameters.FirstOrDefault(p => p.Name == name);
        string Expr(string name) => Param(name)?.Expression.Trim() ?? "";
        static bool Num(string text, out double v) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        if (Enum.TryParse<FilterResponse>(Expr("Response"), true, out var response)) Response = response;
        if (Enum.TryParse<NetworkForm>(Expr("Form"), true, out var form)) Form = form;
        if (Num(Expr("Order"), out double order))
            OrderText = ((int)order).ToString(CultureInfo.InvariantCulture);
        OrderMode = Enum.TryParse<FilterOrderMode>(Expr("OrderMode"), true, out var mode) ? mode : FilterOrderMode.Specify;
        if (Enum.TryParse<FilterFirstElement>(Expr("First"), true, out var first)) First = first;
        if (Enum.TryParse<FilterSeriesKind>(Expr("Values"), true, out var values)) Series = values;

        // The boxes are filled in GHz, then each is shown in the unit its edge was applied in.
        Unit = "GHz";
        double? Ghz(string name)
        {
            var p = Param(name);
            if (p is null || !Num(p.Expression.Trim(), out double v) || !(v > 0)) return null;
            return v / 1e9 * ScaleOf(UnitOf(p.Unit));
        }
        double Ohms(string name, double fallback) => Num(Expr(name), out double v) ? v : fallback;

        // The stored mask edges go into their boxes first; SeedEdges then puts the block's own
        // cutoff over the edge it names and keeps every stored edge that still sits on the right
        // side of it.
        string[] boxes = Form switch
        {
            NetworkForm.Lowpass  => ["Fp", "Fs", "", ""],
            NetworkForm.Highpass => ["Fs", "Fp", "", ""],
            NetworkForm.Bandstop => ["Fp1", "Fs1", "Fs2", "Fp2"],
            _                    => ["Fs1", "Fp1", "Fp2", "Fs2"],
        };
        string? Stored(string name) => name.Length > 0 && Ghz(name) is { } g ? FormatEdge(g) : null;
        FaText = Stored(boxes[0]) ?? FaText;
        FbText = Stored(boxes[1]) ?? FbText;
        FcText = Stored(boxes[2]) ?? FcText;
        FdText = Stored(boxes[3]) ?? FdText;
        SeedEdges(Ghz("Fc") ?? 1, Ghz("F1") ?? 0.9, Ghz("F2") ?? 1.1);

        if (Num(Expr("Ripple"), out double ripple))
            RippleText = ripple.ToString("0.####", CultureInfo.InvariantCulture);
        ApText = Num(Expr("Ap"), out double ap) ? ap.ToString("0.####", CultureInfo.InvariantCulture) : RippleText;
        if (EllipticTypes.TryParse(Expr("EllipticType"), out var et))
            EllipticTypeChoice = EllipticTypeChoices.First(c => c.Value == et);
        if (Num(Expr("Astop"), out double ast))
            AsText = ast.ToString("0.####", CultureInfo.InvariantCulture);
        SourceText = Ohms("Zin", 50).ToString("0.####", CultureInfo.InvariantCulture);
        LoadText = Ohms("Zout", 50).ToString("0.####", CultureInfo.InvariantCulture);
        // Each box's prefix: its own mask edge's unit, else the cutoff it holds, else GHz. Setting
        // it converts the box out of GHz.
        string BoxUnit(int i, string cutoff)
        {
            var p = boxes[i].Length > 0 ? Param(boxes[i]) : null;
            if (p is not null && !string.IsNullOrWhiteSpace(p.Expression)) return UnitOf(p.Unit);
            return UnitOf(Param(cutoff)?.Unit);
        }
        bool edgeForm = Form is NetworkForm.Lowpass or NetworkForm.Highpass;
        UnitA = BoxUnit(0, edgeForm ? "Fc" : "F1");
        UnitB = BoxUnit(1, edgeForm ? "Fc" : "F1");
        UnitC = BoxUnit(2, "F2");
        UnitD = BoxUnit(3, "F2");
        UnitFmin = UnitFmax = Unit;
        SeedLibrary(Expr("LSeries"), Expr("CSeries"), Expr("Parts"));
        ApplyLabels();
    }

    /// <summary>One of the window's units, from a parameter's unit; GHz for anything else.</summary>
    private string UnitOf(string? unit) =>
        Units.FirstOrDefault(u => string.Equals(u, unit?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "GHz";

    /// <summary>
    /// Finds the library series and hand-picked parts a Filter names again, by loading this
    /// workspace's libraries. A name that is not there any more is said, not silently dropped.
    /// </summary>
    private void SeedLibrary(string lSeries, string cSeries, string parts)
    {
        _partOverrides.Clear();
        if (lSeries.Length == 0 && cSeries.Length == 0 && parts.Length == 0) return;
        LoadLibraries();
        var missing = new List<string>();
        FilterLibrarySeries? Find(IEnumerable<FilterLibrarySeries> list, string key)
        {
            if (key.Length == 0) return null;
            var s = list.FirstOrDefault(x => x.Key == key);
            if (s is null) missing.Add(key);
            return s;
        }
        InductorChoice = Find(InductorSeries, lSeries);
        CapacitorChoice = Find(CapacitorSeries, cSeries);
        foreach (var (element, key, partName) in ParseParts(parts))
        {
            var series = InductorSeries.Concat(CapacitorSeries).FirstOrDefault(x => x.Key == key);
            var part = series?.Parts.FirstOrDefault(x => x.Name == partName);
            if (series is null || part is null) { missing.Add($"{key}|{partName}"); continue; }
            _partOverrides[element] = (series, part);
        }
        if (missing.Count > 0)
            Status = "Not in this workspace's libraries: " + string.Join(", ", missing.Distinct()) + ".";
        Design();
    }

    /// <summary>The Parts value as written: <c>element=library: series|part</c>, separated by ';'.</summary>
    internal static IEnumerable<(string Element, string SeriesKey, string Part)> ParseParts(string text)
    {
        foreach (string item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            int bar = item.LastIndexOf('|');
            if (eq <= 0 || bar <= eq + 1 || bar == item.Length - 1) continue;
            yield return (item[..eq].Trim(), item[(eq + 1)..bar].Trim(), item[(bar + 1)..].Trim());
        }
    }

    /// <summary>The hand-picked parts, written the way <see cref="ParseParts"/> reads them.</summary>
    private string PartsText() =>
        string.Join("; ", _partOverrides.OrderBy(o => o.Key, StringComparer.Ordinal)
                                        .Select(o => $"{o.Key}={o.Value.Series.Key}|{o.Value.Part.Name}"));

    /// <summary>
    /// Puts a Filter's own cutoff (GHz) into the boxes that edge belongs to — the passband edges,
    /// or for inverse Chebyshev the stopband edges, by form — and keeps the mask's other edges
    /// where they were when they still sit on the right side, else a fifth of the way out.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="FilterAssignments"/>, so Apply then Seed gives back the edges the
    /// window built at. Reading F1/F2 into the first two boxes regardless of form put a bandpass's
    /// passband into its Fs1/Fp1 boxes and a bandstop's into Fp1/Fs1.
    /// </remarks>
    private void SeedEdges(double fcGhz, double f1Ghz, double f2Ghz)
    {
        double A() => Parse(FaText);
        double B() => Parse(FbText);
        double C() => Parse(FcText);
        double D() => Parse(FdText);
        static double Parse(string t) =>
            double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
        bool inv = Response == FilterResponse.InvChebyshev;
        double a, b, c = C(), d = D();
        switch (Form)
        {
            case NetworkForm.Lowpass:          // A Fp, B Fs
                (a, b) = inv ? (A(), fcGhz) : (fcGhz, B());
                if (!(a > 0 && b > a)) { if (inv) a = b / 1.25; else b = a * 1.25; }
                break;
            case NetworkForm.Highpass:         // A Fs, B Fp
                (a, b) = inv ? (fcGhz, B()) : (A(), fcGhz);
                if (!(a > 0 && b > a)) { if (inv) b = a * 1.25; else a = b / 1.25; }
                break;
            case NetworkForm.Bandstop:         // A Fp1, B Fs1, C Fs2, D Fp2
                if (inv)
                {
                    (b, c) = (f1Ghz, f2Ghz);
                    a = A(); d = D();
                    if (!(a > 0 && b > a)) a = b - 0.5 * (c - b);
                    if (!(d > c)) d = c + 0.5 * (c - b);
                    if (!(a > 0)) a = b / 1.25;
                }
                else
                {
                    (a, d) = (f1Ghz, f2Ghz);
                    b = B(); c = C();
                    if (!(b > a && c > b && d > c)) { double w = (d - a) / 5; b = a + 2 * w; c = d - 2 * w; }
                }
                break;
            default:                           // A Fs1, B Fp1, C Fp2, D Fs2
                if (inv)
                {
                    (a, d) = (f1Ghz, f2Ghz);
                    b = B(); c = C();
                    if (!(b > a && c > b && d > c)) { double w = (d - a) / 5; b = a + w; c = d - w; }
                }
                else
                {
                    (b, c) = (f1Ghz, f2Ghz);
                    a = A(); d = D();
                    if (!(a > 0 && b > a)) a = b - 0.5 * (c - b);
                    if (!(d > c)) d = c + 0.5 * (c - b);
                    if (!(a > 0)) a = b / 1.25;
                }
                break;
        }
        FaText = FormatEdge(a);
        FbText = FormatEdge(b);
        FcText = FormatEdge(c);
        FdText = FormatEdge(d);
    }

    /// <summary>
    /// The parameters Apply to Filter and Place component write, from the specification the last
    /// design was BUILT from rather than from box positions — the cutoff edges are the ones the
    /// ladder used, so the block and the ladder are the same response — plus every Designer setting
    /// the block keeps for the round trip. Frequencies in the window's unit. Null before a design.
    /// </summary>
    /// <remarks>
    /// The mask edges of the OTHER forms are written blank, so a Filter that changed form does not
    /// keep a stale pair the Designer would read back.
    /// </remarks>
    public IReadOnlyList<FilterAssignment>? FilterAssignments()
    {
        if (_lastSpec is not { } spec) return null;
        // Each edge is written in the prefix of the box it was typed in.
        string[] units = [UnitA, UnitB, UnitC, UnitD];
        double[] boxHz = [spec.EdgeAHz, spec.EdgeBHz, spec.EdgeCHz, spec.EdgeDHz];
        string In(double hz, int box) =>
            (hz / ScaleOf(units[box])).ToString("0.#########", CultureInfo.InvariantCulture);
        // A cutoff is one of the boxes (which one depends on form and family): the box it equals.
        int BoxOf(double hz, int fallback)
        {
            for (int i = 0; i < 4; i++) if (boxHz[i] == hz) return i;
            return fallback;
        }
        var list = new List<FilterAssignment>
        {
            new("Response", spec.Response.ToString()),
            new("Form", spec.Form.ToString()),
            new("Order", spec.Order.ToString(CultureInfo.InvariantCulture)),
            new("Ripple", RippleText.Trim()),
            new("Astop", AsText.Trim()),
            new("Zin", SourceText.Trim()),
            new("Zout", LoadText.Trim()),
            new("EllipticType", (EllipticTypeChoice?.Value ?? EllipticType.Auto).ToString()),
            new("Ap", ApText.Trim(), "dB"),
            new("OrderMode", OrderMode.ToString()),
            new("First", spec.First.ToString()),
            new("Values", Series.ToString()),
            new("LSeries", InductorChoice?.Key ?? ""),
            new("CSeries", CapacitorChoice?.Key ?? ""),
            new("Parts", PartsText()),
            new("Synthesis", "On"),
        };
        bool edge = spec.Form is NetworkForm.Lowpass or NetworkForm.Highpass;
        if (edge)
        {
            int k = BoxOf(spec.FcHz, 0);
            list.Add(new("Fc", In(spec.FcHz, k), units[k]));
        }
        else
        {
            int k1 = BoxOf(spec.F1Hz, 1), k2 = BoxOf(spec.F2Hz, 2);
            list.Add(new("F1", In(spec.F1Hz, k1), units[k1]));
            list.Add(new("F2", In(spec.F2Hz, k2), units[k2]));
        }
        string[] boxNames = spec.Form switch
        {
            NetworkForm.Lowpass  => ["Fp", "Fs", "", ""],
            NetworkForm.Highpass => ["Fs", "Fp", "", ""],
            NetworkForm.Bandstop => ["Fp1", "Fs1", "Fs2", "Fp2"],
            _                    => ["Fs1", "Fp1", "Fp2", "Fs2"],
        };
        var mask = Enumerable.Range(0, 4).Where(i => boxNames[i].Length > 0)
            .Select(i => (Name: boxNames[i], Value: In(boxHz[i], i), Unit: units[i])).ToList();
        foreach (string name in (string[])["Fp", "Fs", "Fp1", "Fs1", "Fs2", "Fp2"])
        {
            var m = mask.FirstOrDefault(m => m.Name == name);
            // The other form's edges are written blank, in the passband box's unit.
            list.Add(m.Name is null ? new(name, "", Unit) : new(name, m.Value, m.Unit));
        }
        return list;
    }

    /// <summary>
    /// <paramref name="current"/> with <see cref="FilterAssignments"/> written over it: each named
    /// parameter takes the new expression (and its unit when one is stated), and one the Filter
    /// does not carry yet — it was placed before that parameter existed — is added with the
    /// registry's own unit and dimension. Null before a design.
    /// </summary>
    public List<EditableParameter>? AppliedParameters(IEnumerable<EditableParameter> current)
    {
        if (FilterAssignments() is not { } assignments) return null;
        var updated = current.Select(p => p.Clone()).ToList();
        var defaults = ComponentTypeRegistry.DefaultParameters(SymbolKind.Filter, 0);
        foreach (var a in assignments)
        {
            var p = updated.FirstOrDefault(x => x.Name == a.Name);
            if (p is null)
            {
                int k = defaults.ToList().FindIndex(x => x.Name == a.Name);
                p = new EditableParameter { Name = a.Name };
                if (k >= 0)
                {
                    var d = defaults[k];
                    p.Unit = d.Unit;
                    p.Dimension = d.Dimension;
                    p.ShowOnSchematic = d.ShowOnSchematic;
                }
                updated.Add(p);
            }
            p.Expression = a.Expression;
            if (a.Unit is { } unit) p.Unit = unit;
        }
        return updated;
    }

    /// <summary>
    /// Commits the ± band / points boxes (Return or focus loss). Rebuilds only the frequency grid
    /// and the plots — the ladder, table and order stay as Design left them.
    /// </summary>
    public void CommitPlotWindow(double? bandPercent, int? points)
    {
        if (bandPercent is { } p && double.IsFinite(p) && p >= 0)
            PlotBandPercentText = p.ToString("0.#", CultureInfo.InvariantCulture);
        if (points is { } n && n >= 2)
            PlotPointsText = Math.Min(n, 20001).ToString(CultureInfo.InvariantCulture);
        UpdatePlots();
    }

    /// <summary>Formats an L/C value the way the Exact / Used columns do.</summary>
    public string FormatElementValue(bool inductor, double v) => Eng(inductor, v);

    /// <summary>A Series / Part dropdown changed — keep the override and redesign.</summary>
    internal void OnRowPartChanged(FilterElementRow row)
    {
        if (_fillingRows) return;
        if (row.SelectedSeries is { } s && row.SelectedPart is { } p)
            _partOverrides[row.Name] = (s, p);
        else
            _partOverrides.Remove(row.Name);
        Design();
    }

    /// <summary>
    /// Re-runs the ideal and ladder response on a new frequency grid and redraws both plots.
    /// Does not touch the realization, the table or the order.
    /// </summary>
    public void UpdatePlots()
    {
        if (_lastSpec is not { } spec)
        {
            Design();
            return;
        }

        var freqs = Frequencies(spec);
        SNP? idealSnp = _lastIdeal is null ? null : IdealSnp(_lastIdeal, freqs, _lastRs, _lastRl);
        SNP? ladderSnp = null;
        if (!string.IsNullOrEmpty(Cnl))
        {
            try { ladderSnp = Run(Cnl, freqs, _lastRs); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
            {
                Status = e.Message;
            }
        }
        BuildPlots(idealSnp, ladderSnp, spec);
    }

    /// <summary>Parses the "± band" box ("10%", "10", "0.1 %").</summary>
    public static double? ParseBandPercent(string? text)
    {
        string t = (text ?? "").Trim().TrimEnd('%').Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0
            ? v
            : null;
    }

    /// <summary>Parses the "points" box.</summary>
    public static int? ParsePlotPoints(string? text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : null;

    private void ApplyLabelsWithoutDesign() { }

    private EllipticType SelectedEllipticType => EllipticTypeChoice?.Value ?? EllipticType.Auto;


    private sealed record Spec(
        FilterResponse Response, NetworkForm Form, int Order, bool OrderFromMinimum,
        FilterFirstElement First, EllipticType EllipticType,
        double FcHz, double F1Hz, double F2Hz, double RippleDb, double AsDb,
        double SourceOhms, double LoadOhms,
        double PassLoHz, double PassHiHz, bool PassIsOutside,
        double ApDb, double EdgeAHz, double EdgeBHz, double EdgeCHz, double EdgeDHz);

    private bool TrySpec(out Spec spec, out string? error)
    {
        spec = new Spec(FilterResponse.Butterworth, NetworkForm.Lowpass, 1, false,
                        FilterFirstElement.Series, EllipticType.A, 1, 0, 0, 0.1, 20, 50, 50, 0, 1, false,
                        3, 0, 0, 0, 0);
        error = null;
        _mask = null;
        if (!double.TryParse(ApText, NumberStyles.Float, CultureInfo.InvariantCulture, out double ap) || ap < 0)
        { error = "Ap is a number of dB, zero or more."; return false; }
        if (!double.TryParse(AsText, NumberStyles.Float, CultureInfo.InvariantCulture, out double astop) || !(astop > 0))
        { error = "As is a positive number of dB."; return false; }
        if (!double.TryParse(SourceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rs) || !(rs > 0))
        { error = "The source impedance is a positive number of ohms."; return false; }
        if (!double.TryParse(LoadText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rl) || !(rl > 0))
        { error = "The load impedance is a positive number of ohms."; return false; }
        if (!Hz(FaText, ScaleOf(UnitA), out double a) || !Hz(FbText, ScaleOf(UnitB), out double b))
        { error = "A frequency edge is a positive number."; return false; }
        Hz(FcText, ScaleOf(UnitC), out double c);
        Hz(FdText, ScaleOf(UnitD), out double d);

        double fc, f1, f2, passLo, passHi, fp = 0, fs = 0, fp1 = 0, fp2 = 0, fs1 = 0, fs2 = 0;
        bool outside;
        switch (Form)
        {
            case NetworkForm.Lowpass:
                // Inverse Chebyshev's Fc is the stopband edge; every other family's is the passband.
                _mask = new MaskSpec(Form, ap, astop, a, b, 0, 0);
                fp = a; fs = b;
                fc = Response == FilterResponse.InvChebyshev ? b : a;
                f1 = f2 = 0; passLo = 0; passHi = a; outside = false;
                break;
            case NetworkForm.Highpass:
                _mask = new MaskSpec(Form, ap, astop, a, b, 0, 0);
                fs = a; fp = b;
                fc = Response == FilterResponse.InvChebyshev ? a : b;
                f1 = f2 = 0; passLo = b; passHi = double.PositiveInfinity; outside = false;
                break;
            case NetworkForm.Bandstop:
                // A Fp1, B Fs1, C Fs2, D Fp2. The prototype's Ω = 1 is the PASSBAND edge, which on a
                // bandstop bounds the rejected band from outside — Fp1 and Fp2 — except for inverse
                // Chebyshev, whose Ω = 1 is where Astop is first met: Fs1 and Fs2.
                _mask = new MaskSpec(Form, ap, astop, a, b, c, d);
                if (!(a > 0) || !(b > a) || !(c > b) || !(d > c))
                { error = "A bandstop needs 0 < Fp1 < Fs1 < Fs2 < Fp2."; return false; }
                fp1 = a; fs1 = b; fs2 = c; fp2 = d;
                fc = 0; passLo = a; passHi = d; outside = true;
                (f1, f2) = Response == FilterResponse.InvChebyshev ? (b, c) : (a, d);
                break;
            default:
                // A Fs1, B Fp1, C Fp2, D Fs2. Inverse Chebyshev is built at its stop edges, as on a
                // lowpass.
                _mask = new MaskSpec(Form, ap, astop, a, b, c, d);
                if (!(b > 0) || !(c > b)) { error = "A bandpass needs 0 < Fp1 < Fp2."; return false; }
                fs1 = a; fp1 = b; fp2 = c; fs2 = d;
                fc = 0; passLo = b; passHi = c; outside = false;
                (f1, f2) = Response == FilterResponse.InvChebyshev && a > 0 && d > c ? (a, d) : (b, c);
                break;
        }

        bool fromMin = false;
        int order;
        if (OrderMode == FilterOrderMode.Minimum
            && Response is not (FilterResponse.Bessel or FilterResponse.Gaussian))
        {
            var n = FilterOrder.Minimum(Response, Form, ap, astop, fp, fs, fp1, fp2, fs1, fs2,
                                        SelectedEllipticType);
            if (n is null)
            {
                error = "The mask edges do not form a transition (Fs must sit outside the passband).";
                return false;
            }
            order = n.Value;
            // An even-order inverse Chebyshev, or a type-a elliptic, has no LC ladder; step to the
            // next odd. Auto, b and c already answered an order that builds.
            if ((Response == FilterResponse.InvChebyshev
                 || (Response == FilterResponse.Elliptic && SelectedEllipticType == EllipticType.A))
                && (order & 1) == 0)
                order++;
            fromMin = true;
            OrderText = order.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (!int.TryParse(OrderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out order) || order < 1)
            { error = "Order is a whole number, 1 or more."; return false; }
        }

        // Synthesis ripple is its own box. Order still uses Ap with As and the edges.
        if (!double.TryParse(RippleText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rippleIn)
            || rippleIn < 0)
        { error = "Ripple is a number of dB, zero or more."; return false; }
        double ripple = Response is FilterResponse.Chebyshev or FilterResponse.Elliptic
            ? (rippleIn > 0 ? rippleIn : 0.1)
            : 0.1;
        var type = Response == FilterResponse.Elliptic
            ? EllipticTypes.Resolve(SelectedEllipticType, order)
            : EllipticType.A;
        spec = new Spec(Response, Form, order, fromMin, First, type, fc, f1, f2, ripple, astop, rs, rl,
                        passLo, passHi, outside, ap, a, b, c, d);
        return true;
    }

    private static bool Hz(string text, double scale, out double hz)
    {
        hz = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0))
            return false;
        hz = v * scale;
        return true;
    }


    /// <summary>
    /// The value each element is built with — the exact one, the series' nearest preferred value,
    /// or a library part — and what its table row says. Pure: reads only <paramref name="snap"/>.
    /// </summary>
    private static List<FilterElement> Snap(FilterRealization real, SnapInputs snap, List<SnapRow> rows)
    {
        var used = new List<FilterElement>(real.Elements.Count);
        foreach (var e in real.Elements)
        {
            double value = FilterValues.Snap(e.Inductor, e.Value, snap.Series);
            string partName = "";
            string seriesName = snap.Series == FilterSeriesKind.Exact ? "" : snap.Series.ToString();
            string tolerance = FilterValues.ToleranceOf(snap.Series);
            FilterLibrarySeries? series = e.Inductor ? snap.Inductors : snap.Capacitors;
            FilterPart? chosen = null;

            if (snap.Overrides.TryGetValue(e.Name, out var ov) && ov.Series.Inductors == e.Inductor)
            {
                series = ov.Series;
                chosen = ov.Part;
                value = chosen.Value;
                partName = chosen.Name;
                seriesName = series.Label;
                tolerance = chosen.Tolerance;
            }
            else if (series is not null && FilterValues.Nearest(e.Value, series.Parts) is { } near)
            {
                chosen = near;
                value = near.Value;
                partName = near.Name;
                seriesName = series.Label;
                tolerance = near.Tolerance;
            }

            used.Add(e with { Value = value });
            rows.Add(new SnapRow(e, value, seriesName, partName, tolerance, series, chosen));
        }
        return used;
    }

    /// <summary>Fills the element table from a design's rows. UI thread.</summary>
    private void FillRows(List<SnapRow> rows)
    {
        bool library = LibraryAvailable && (InductorSeries.Count > 0 || CapacitorSeries.Count > 0);
        int row = 0;
        _fillingRows = true;
        try
        {
            foreach (var r in rows)
            {
                var e = r.Exact;
                var choices = e.Inductor ? InductorSeries : CapacitorSeries;
                Elements.Add(new FilterElementRow(
                    this, e.Name,
                    (e.Shunt ? "shunt " : "series ") + (e.Inductor ? "L" : "C"),
                    Eng(e.Inductor, e.Value), e.Value, e.Inductor,
                    Eng(e.Inductor, r.Used), r.SeriesName, r.PartName, r.Tolerance,
                    (row++ & 1) == 0 ? "#FFFFFF" : "#F0F0F0",
                    library && choices.Count > 0,
                    r.Series, r.Chosen, choices));
            }
        }
        finally
        {
            _fillingRows = false;
        }
    }

    private static string Eng(bool inductor, double v)
    {
        (double Scale, string Unit) pick = inductor
            ? v >= 1e-6 ? (1e6, "µH") : v >= 1e-9 ? (1e9, "nH") : (1e12, "pH")
            : v >= 1e-6 ? (1e6, "µF") : v >= 1e-9 ? (1e9, "nF") : (1e12, "pF");
        return (v * pick.Scale).ToString("0.####", CultureInfo.InvariantCulture) + " " + pick.Unit;
    }

    /// <summary>
    /// The same placement the Match Designer uses, so the filter is a circuitRF schematic rather
    /// than a table of numbers.
    /// </summary>
    private static MatchLadderLayout LadderOf(IReadOnlyList<FilterElement> used,
                                              IReadOnlyList<FilterArm> arms, double rs, double rl) =>
        FilterLadderDrawing.Build(used, arms, rs, rl, Eng);

    private double[] Frequencies(Spec spec)
    {
        int n = 401;
        if (ParsePlotPoints(PlotPointsText) is { } pts) n = Math.Clamp(pts, 2, 20001);

        double lo, hi;
        if (PlotRangeMode == FilterPlotRangeMode.Manual
            && Hz(PlotFminText, ScaleOf(UnitFmin), out double manLo)
            && Hz(PlotFmaxText, ScaleOf(UnitFmax), out double manHi)
            && manHi > manLo)
        {
            lo = manLo;
            hi = manHi;
        }
        else
        {
            // Outer edges of the typed mask, then ± PlotBandFraction like the Match Designer.
            var edges = new List<double>(4);
            void Add(double hz) { if (hz > 0 && double.IsFinite(hz)) edges.Add(hz); }
            Add(spec.EdgeAHz);
            Add(spec.EdgeBHz);
            if (spec.Form is NetworkForm.Bandpass or NetworkForm.Bandstop)
            {
                Add(spec.EdgeCHz);
                Add(spec.EdgeDHz);
            }

            if (edges.Count >= 2)
            {
                lo = edges.Min();
                hi = edges.Max();
            }
            else if (spec.Form == NetworkForm.Lowpass)
            {
                lo = Math.Max(spec.FcHz / 50, 1e3);
                hi = spec.FcHz * 20;
            }
            else if (spec.Form == NetworkForm.Highpass)
            {
                lo = Math.Max(spec.FcHz / 20, 1e3);
                hi = spec.FcHz * 50;
            }
            else
            {
                lo = Math.Max(spec.F1Hz / 5, 1e3);
                hi = spec.F2Hz * 5;
            }

            double fraction = 0.10;
            if (ParseBandPercent(PlotBandPercentText) is { } pct) fraction = pct / 100.0;
            double span = hi - lo;
            if (!(span > 0)) span = Math.Max(hi, 1.0);
            double pad = span * Math.Max(0.0, fraction);
            lo = Math.Max(lo - pad, span * 1e-6);
            hi = hi + pad;
        }

        var f = new double[n];
        for (int i = 0; i < n; i++)
            f[i] = lo + (hi - lo) * i / (n - 1.0);
        return f;
    }

    private static string ConvertEdge(string text, double fromScale, double toScale)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return text;
        return FormatEdge(v * fromScale / toScale);
    }

    private static string FormatEdge(double v) =>
        v.ToString("0.########", CultureInfo.InvariantCulture);

    private static double ScaleOf(string unit) => unit switch
    {
        "kHz" => 1e3,
        "MHz" => 1e6,
        "GHz" => 1e9,
        _ => 1,
    };

    private static SNP IdealSnp(FilterNetwork ideal, double[] freqs, double rs, double rl)
    {
        var mats = new Mat<Complex>[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
        {
            var (s11, s21, s22) = ideal.At(2 * Math.PI * freqs[i]);
            mats[i] = new Mat<Complex>(2, 2);
            mats[i][0, 0] = s11;
            mats[i][0, 1] = s21;
            mats[i][1, 0] = s21;
            mats[i][1, 1] = s22;
        }
        var snp = new SNP(freqs, mats, MatrixType.S, MatrixFormat.RI, new Complex(rs, 0));
        return snp;
    }

    private static SNP Run(string cnl, double[] freqs, double z0)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(netlist, freqs);
        var cube = ds["S"];
        int nf = cube.Axes[0].Length, np = cube.Axes[1].Length;
        var raw = cube.ComplexValues;
        var mats = new Mat<Complex>[nf];
        for (int f = 0; f < nf; f++)
        {
            mats[f] = new Mat<Complex>(np, np);
            for (int i = 0; i < np; i++)
                for (int j = 0; j < np; j++)
                    mats[f][i, j] = raw[f * np * np + i * np + j];
        }
        return new SNP(cube.Axes[0].Values, mats, MatrixType.S, MatrixFormat.RI, new Complex(z0, 0));
    }

    private static double MinimumInsertionLoss(SNP snp, double[] freqs, Spec spec)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < freqs.Length && i < snp.Frequencies.Length; i++)
        {
            double f = freqs[i];
            bool pass = spec.PassIsOutside
                ? f <= spec.PassLoHz || f >= spec.PassHiHz
                : f >= spec.PassLoHz && f <= spec.PassHiHz;
            if (!pass) continue;
            double il = -20.0 * Math.Log10(Math.Max(snp.Matrices[i][1, 0].Magnitude, 1e-30));
            if (il < best) best = il;
        }
        return double.IsPositiveInfinity(best) ? 0 : Math.Max(0, best);
    }

    private void BuildPlots(SNP? ideal, SNP? ladder, Spec spec)
    {
        MagnitudePlot.Traces.Clear();
        PhasePlot.Traces.Clear();
        MagnitudePlot.FreqUnits = FreqUnitOf(Unit);
        PhasePlot.FreqUnits = MagnitudePlot.FreqUnits;

        // CubeName must stay null. A non-null name marks the trace cube-bound, and BuildPath
        // then ignores the SNP (Trace.IsCubeBound). These are matrix traces.
        if (ideal is not null)
        {
            var idealTrace = new Trace(ideal, MatrixType.S, 1, 0, DependentVarFormat.Db, false, Style(0));
            idealTrace.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
            MagnitudePlot.Traces.Add(idealTrace);
        }

        if (ladder is not null)
        {
            var ladderTrace = new Trace(ladder, MatrixType.S, 1, 0, DependentVarFormat.Db, false, Style(1));
            ladderTrace.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
            MagnitudePlot.Traces.Add(ladderTrace);

            var s11 = new Trace(ladder, MatrixType.S, 0, 0, DependentVarFormat.Db, true, Style(2));
            s11.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
            MagnitudePlot.Traces.Add(s11);

            var phase = new Trace(ladder, MatrixType.S, 1, 0, DependentVarFormat.Phase, false, Style(0));
            phase.BuildPath(PlotType.Rect, PhasePlot.FreqUnits);
            PhasePlot.Traces.Add(phase);
            if (MatchDesignerViewModel.GroupDelayTrace(ladder, PhasePlot.FreqUnits) is { } delay)
                PhasePlot.Traces.Add(delay);
        }

        MagnitudeMask.Lines = MaskLines(spec);

        MagnitudePlot.CustomTitleOn = true;
        MagnitudePlot.CustomTitle = "Filter response";
        MagnitudePlot.Autoscale(force: true);
        PhasePlot.CustomTitleOn = true;
        PhasePlot.CustomTitle = "Phase and group delay";
        PhasePlot.Autoscale(force: true);
        Announce();
    }

    /// <summary>The mask as typed — what the plot draws whether or not a design came of it.</summary>
    internal sealed record MaskSpec(NetworkForm Form, double ApDb, double AsDb,
                                    double AHz, double BHz, double CHz, double DHz);

    private MaskSpec? _mask;

    /// <summary>The last mask the boxes described, for a test or a caller that draws it.</summary>
    internal MaskSpec? Mask => _mask;

    private FilterMaskLine[] MaskLines(Spec spec) =>
        MaskLinesFor(new MaskSpec(spec.Form, spec.ApDb, spec.AsDb,
                                  spec.EdgeAHz, spec.EdgeBHz, spec.EdgeCHz, spec.EdgeDHz),
                     LabelA, LabelB, LabelC, LabelD);

    /// <summary>
    /// The mask as the plot draws it, coloured by ROLE: every passband edge and the −Ap limit in
    /// one colour, every stopband edge and the −As limit in another. Each limit spans only its own
    /// band — −Ap over a passband, −As over a stopband — and a band open to one side runs to the
    /// plot's edge (NaN), so the picture is the stepped mask a trace has to stay inside.
    /// </summary>
    internal static FilterMaskLine[] MaskLinesFor(MaskSpec m, string la, string lb, string lc, string ld)
    {
        var list = new List<FilterMaskLine>(10);
        double ap = -m.ApDb, ast = -m.AsDb;
        void Edge(double hz, bool pass, string label)
        {
            if (hz > 0 && double.IsFinite(hz))
                list.Add(new FilterMaskLine(hz, pass ? FilterMaskColors.Pass : FilterMaskColors.Stop,
                                            Vertical: true, label));
        }
        void Limit(bool pass, double x0, double x1)
        {
            double y = pass ? ap : ast;
            if (!double.IsFinite(y)) return;
            if (double.IsFinite(x0) && double.IsFinite(x1) && !(x1 > x0)) return;
            list.Add(new FilterMaskLine(y, pass ? FilterMaskColors.Pass : FilterMaskColors.Stop,
                                        Vertical: false, pass ? "Ap" : "As", x0, x1, Forbid: pass ? -1 : 1));
        }
        const double Open = double.NaN;
        switch (m.Form)
        {
            case NetworkForm.Lowpass:
                Edge(m.AHz, true, la); Edge(m.BHz, false, lb);
                Limit(true, Open, m.AHz); Limit(false, m.BHz, Open);
                break;
            case NetworkForm.Highpass:
                Edge(m.AHz, false, la); Edge(m.BHz, true, lb);
                Limit(false, Open, m.AHz); Limit(true, m.BHz, Open);
                break;
            case NetworkForm.Bandstop:
                Edge(m.AHz, true, la); Edge(m.BHz, false, lb); Edge(m.CHz, false, lc); Edge(m.DHz, true, ld);
                Limit(true, Open, m.AHz); Limit(false, m.BHz, m.CHz); Limit(true, m.DHz, Open);
                break;
            default:
                Edge(m.AHz, false, la); Edge(m.BHz, true, lb); Edge(m.CHz, true, lc); Edge(m.DHz, false, ld);
                Limit(false, Open, m.AHz); Limit(true, m.BHz, m.CHz); Limit(false, m.DHz, Open);
                break;
        }
        return [.. list];
    }

    /// <summary>
    /// A refused specification still draws its mask, framed on its own edges, so an edge typed on
    /// the wrong side of another is visible rather than replaced by a blank plot.
    /// </summary>
    private void ShowMaskAlone()
    {
        if (_mask is not { } m) return;
        var hz = new[] { m.AHz, m.BHz, m.CHz, m.DHz }.Where(f => f > 0 && double.IsFinite(f)).ToArray();
        if (hz.Length == 0) return;
        MagnitudePlot.FreqUnits = FreqUnitOf(Unit);
        double scale = ScaleOf(Unit);
        double lo = hz.Min() / scale, hi = hz.Max() / scale;
        double pad = Math.Max(hi - lo, hi * 0.1) * 0.1;
        double depth = double.IsFinite(m.AsDb) ? m.AsDb + 20 : 60;
        MagnitudeMask.Lines = MaskLinesFor(m, LabelA, LabelB, LabelC, LabelD);
        MagnitudePlot.Axes.Window = new PlotRect(Math.Max(0, lo - pad), -depth, hi - lo + 2 * pad, depth + 5);
        Announce();
    }

    private void ClearPlots()
    {
        MagnitudePlot.Traces.Clear();
        PhasePlot.Traces.Clear();
        MagnitudeMask.Lines = [];
        Announce();
    }

    private void Announce()
    {
        MagnitudeContainer.OnPlotChanged(this, EventArgs.Empty);
        PhaseContainer.OnPlotChanged(this, EventArgs.Empty);
        MagnitudeContainer.RequestPlotRedraw();
        PhaseContainer.RequestPlotRedraw();
    }

    private static FreqUnit FreqUnitOf(string unit) => unit switch
    {
        "Hz" => FreqUnit.Hz,
        "kHz" => FreqUnit.kHz,
        "MHz" => FreqUnit.MHz,
        _ => FreqUnit.GHz,
    };

    private static TraceProperties Style(int order)
    {
        int index = TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length];
        var props = new TraceProperties
        {
            LineColorIndex = index,
            MarkerColorIndex = index,
            FillColorIndex = index,
            Custom = false,
        };
        return props;
    }
}
