using System;
using System.Linq;
using CircuitRF.Core.Design;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Transient analysis card body (type=tran, brief-trn-1 R-trn1-12). Every field is the directive's raw text, resolved at run
/// time. Stop, Step, Method, Initial state and Solver are on the card; every other key under More (brief TRN-5 R-trn5-4), the
/// ngspice-only ones enabled only with that solver. brief TRN-7 R-trn7-7 — the ngspice choice is enabled only while discovery finds
/// a usable ngspice (<see cref="NgspiceAvailability"/>), and follows it live; a bench that already says ngspice while none is found
/// keeps saying it, greyed, with the note beneath, and its run is refused rather than run native.
/// </summary>
public sealed partial class TranBodyViewModel : ObservableObject
{
    public static readonly string[] MethodOptions = [TransientAnalysis.MethodTrap, TransientAnalysis.MethodGear2];
    public static readonly string[] MethodHelp =
    [
        "trap — trapezoidal: second order, keeps an undamped ring's amplitude; can ring after a sharp edge",
        "gear2 — second-order backward difference: damps ringing, and slightly damps real oscillations at coarse steps",
    ];

    public static readonly string[] InitialOptions = [TransientAnalysis.InitialOp, TransientAnalysis.InitialZero];
    public static readonly string[] InitialHelp =
    [
        "op — start from the DC operating point, the IC nets held during it",
        "zero — every voltage and current starts at 0 (the IC nets at their values), with no DC solve",
    ];

    /// <summary>The tooltip on the disabled ngspice choice, and the note's tooltip under a bench that already names it, when none is
    /// found.</summary>
    public const string NgspiceMissing =
        "No usable ngspice was found. Install it, or name the one you have, in Settings ▸ Solvers. Until then a run with Solver=ngspice is refused.";

    /// <summary>The note itself: one line, so it fits the space kept for it.</summary>
    public const string NgspiceMissingNote = "No usable ngspice was found: install it, or name yours, in Settings ▸ Solvers.";

    public TranBodyViewModel()
    {
        _availabilityChanged = OnAvailabilityChanged;
        NgspiceAvailability.Subscribe(_availabilityChanged);
    }

    // held here: NgspiceAvailability keeps only a weak reference, so a closed card is not kept alive by it
    private readonly Action _availabilityChanged;

    private void OnAvailabilityChanged()
    {
        OnPropertyChanged(nameof(NgspiceAvailable));
        OnPropertyChanged(nameof(NgspiceTooltip));
        OnPropertyChanged(nameof(NgspiceLabel));
        OnPropertyChanged(nameof(SolverNote));
        OnPropertyChanged(nameof(SolverNoteTip));
        OnPropertyChanged(nameof(SolverGreyed));
    }

    [ObservableProperty] private string _stop = "1e-6";
    [ObservableProperty] private string _step = "";
    [ObservableProperty] private string _method = TransientAnalysis.MethodTrap;
    [ObservableProperty] private string _initialState = TransientAnalysis.InitialOp;
    /// <summary>0 native, 1 ngspice.</summary>
    [ObservableProperty] private int _solverIndex;
    [ObservableProperty] private string _start = "0";
    [ObservableProperty] private string _maxStep = "auto";
    [ObservableProperty] private string _relTol = "1e-3";
    [ObservableProperty] private string _vTol = "1e-6";
    [ObservableProperty] private string _iTol = "1e-12";
    [ObservableProperty] private string _ic = "";
    [ObservableProperty] private bool _saveSteps;
    [ObservableProperty] private string _maxSteps = TransientAnalysis.MaxStepsDefault;
    [ObservableProperty] private bool _moreExpanded;

    // The ngspice-only keys (brief TRN-5 R-trn5-4): under More, enabled only with the ngspice solver, which reads them.
    public static readonly string[] SpiceLinesOptions = [TransientAnalysis.SpiceLinesDefault, "rlgc"];
    public static readonly string[] SpiceLinesHelp =
    [
        "refuse — a transmission line is not written for ngspice; the run names it",
        "rlgc — each line written as ngspice's own lossy line, fitted at Line fit frequency, its error reported",
    ];
    public static readonly string[] SpiceFitOptions = [TransientAnalysis.SpiceFitDefault, "yes"];
    public static readonly string[] SpiceFitHelp =
    [
        "no — a frequency-defined element (an S-parameter file, a filter) is not written for ngspice; the run names it",
        "yes — each written as a subcircuit of circuitRF's own rational fit, labelled in the result",
    ];

    [ObservableProperty] private string _spiceOptions = "";
    [ObservableProperty] private string _spiceLines = TransientAnalysis.SpiceLinesDefault;
    [ObservableProperty] private string _spiceLineFreq = TransientAnalysis.SpiceLineFreqDefault;
    [ObservableProperty] private string _spiceFit = TransientAnalysis.SpiceFitDefault;

    /// <summary>Whether the ngspice-only keys apply: the solver is ngspice.</summary>
    public bool IsNgspice => SolverIndex == 1;

    public string SpiceLinesDescription => SpiceLinesHelp[Math.Max(0, Array.IndexOf(SpiceLinesOptions, SpiceLines))];
    partial void OnSpiceLinesChanged(string value) => OnPropertyChanged(nameof(SpiceLinesDescription));

    public string SpiceFitDescription => SpiceFitHelp[Math.Max(0, Array.IndexOf(SpiceFitOptions, SpiceFit))];
    partial void OnSpiceFitChanged(string value) => OnPropertyChanged(nameof(SpiceFitDescription));

    /// <summary>Whether the ngspice choice can be picked: while discovery finds a usable ngspice.</summary>
    public bool NgspiceAvailable => NgspiceAvailability.Current is not null;

    /// <summary>The ngspice choice's text: its discovered version when there is one.</summary>
    public string NgspiceLabel => NgspiceAvailability.Current is { } p ? $"ngspice {p.Version}" : "ngspice";

    public string NgspiceTooltip => NgspiceAvailability.Current is { } p
        ? $"ngspice {p.Version}, at {p.Path}: the bench is written as a SPICE deck and solved by ngspice"
        : NgspiceMissing;

    /// <summary>The line under the solver: empty unless the bench says ngspice and none is found (its space kept, so nothing below
    /// moves).</summary>
    public string SolverNote => SolverGreyed ? NgspiceMissingNote : "";

    /// <summary>The note's tooltip: the whole sentence, or none.</summary>
    public string? SolverNoteTip => SolverGreyed ? NgspiceMissing : null;

    /// <summary>The bench says ngspice and none is found: the Solver box shows that value greyed.</summary>
    public bool SolverGreyed => SolverIndex == 1 && !NgspiceAvailable && NgspiceAvailability.Known;

    partial void OnSolverIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SolverNote));
        OnPropertyChanged(nameof(SolverNoteTip));
        OnPropertyChanged(nameof(SolverGreyed));
        OnPropertyChanged(nameof(IsNgspice));
    }

    // ── brief MTR-2/MTR-3: what a modulated run keeps ──────────────────────
    public static readonly string[] StoreOptions = [TransientAnalysis.StoreAuto, TransientAnalysis.StoreBaseband, TransientAnalysis.StoreRf];
    public static readonly string[] StoreHelp =
    [
        "auto — baseband when the schematic has a MOD_Source, every output point otherwise",
        "baseband — the envelopes about the MOD_Source's Fc, measured as the envelope analysis measures them (PSD, ACLR, EVM)",
        "rf — every output point of the waveform",
    ];

    [ObservableProperty] private string _store = TransientAnalysis.StoreAuto;
    [ObservableProperty] private string _storeHarm = TransientAnalysis.StoreHarmDefault;
    [ObservableProperty] private string _measureFrom = TransientAnalysis.MeasureFromDefault;

    /// <summary>The highest frequency a MOD_Source on the schematic plays (its Fc), or null when there is none; 0 when one is
    /// there but its Fc is not a plain number.</summary>
    public double? ModSourceHz { get; private set; }

    /// <summary>Reads the schematic's MOD_Sources, for the RF-rate warning.</summary>
    public TranBodyViewModel UseSchematic(CircuitRF.Design.Schematic.SchematicEditModel model)
    {
        var fcs = model.Components.Where(c => c.Symbol == CircuitRF.Design.Schematic.SymbolKind.ModSource)
                                  .Select(c => c.Parameters.FirstOrDefault(p => p.Name == "Fc") is { } fc ? Number(fc.Expression, fc.Unit) ?? 0 : 0)
                                  .ToList();
        ModSourceHz = fcs.Count == 0 ? null : fcs.Max();
        OnPropertyChanged(nameof(StoreWarning));
        return this;
    }

    public string StoreDescription => StoreHelp[Math.Max(0, Array.IndexOf(StoreOptions, Store))];

    /// <summary>The warning under Store: RF storage of a modulated bench keeps every carrier cycle. Empty otherwise; its row is kept
    /// either way, so nothing below moves.</summary>
    public string StoreWarning
    {
        get
        {
            if (Store != TransientAnalysis.StoreRf || ModSourceHz is not { } fc) return "";
            if (fc > 0 && Number(Stop, null) is { } stop && stop > 0)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "RF rate stores every carrier cycle: about {0} points per net", Points(stop * 20 * fc));
            return "RF rate stores every carrier cycle";
        }
    }

    private static string Points(double n) => n >= 1e9 ? $"{n / 1e9:0.#} billion" : n >= 1e6 ? $"{n / 1e6:0.#} million"
        : n >= 1e3 ? $"{n / 1e3:0.#} thousand" : $"{n:0}";

    private static double? Number(string text, string? unit)
    {
        try
        {
            var v = new CircuitRF.Core.Expressions.Evaluator().Eval(text, new CircuitRF.Core.Expressions.Scope("card"),
                                                                     string.IsNullOrWhiteSpace(unit) ? null : unit);
            return v.Kind == CircuitRF.Core.Expressions.ValueKind.Real ? v.AsReal() : null;
        }
        catch (Exception) { return null; }
    }

    partial void OnStoreChanged(string value)
    {
        OnPropertyChanged(nameof(StoreDescription));
        OnPropertyChanged(nameof(StoreWarning));
    }

    public string MethodDescription => MethodHelp[Math.Max(0, Array.IndexOf(MethodOptions, Method))];
    partial void OnMethodChanged(string value) => OnPropertyChanged(nameof(MethodDescription));

    public string InitialDescription => InitialHelp[Math.Max(0, Array.IndexOf(InitialOptions, InitialState))];
    partial void OnInitialStateChanged(string value) => OnPropertyChanged(nameof(InitialDescription));

    /// <summary>A transient needs a Stop.</summary>
    public bool IsValid => Stop.Trim().Length > 0;
    partial void OnStopChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(StoreWarning));
    }

    public static TranBodyViewModel FromAnalysis(TransientAnalysis a) => new()
    {
        Stop = a.StopExpr, Step = a.StepExpr,
        Method = Array.IndexOf(MethodOptions, a.Method.Trim().ToLowerInvariant()) >= 0 ? a.Method.Trim().ToLowerInvariant() : a.Method,
        InitialState = Array.IndexOf(InitialOptions, a.InitialState.Trim().ToLowerInvariant()) >= 0 ? a.InitialState.Trim().ToLowerInvariant() : a.InitialState,
        SolverIndex = a.Solver.Trim().Equals(TransientAnalysis.SolverNgspice, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
        Start = a.StartExpr, MaxStep = a.MaxStepExpr, RelTol = a.RelTolExpr, VTol = a.VTolExpr, ITol = a.ITolExpr, Ic = a.IcExpr,
        SaveSteps = a.SaveStepsOn, MaxSteps = a.MaxStepsExpr,
        SpiceOptions = a.SpiceOptions,
        SpiceLines = Array.IndexOf(SpiceLinesOptions, a.SpiceLines.Trim().ToLowerInvariant()) >= 0 ? a.SpiceLines.Trim().ToLowerInvariant() : a.SpiceLines,
        SpiceLineFreq = a.SpiceLineFreq,
        SpiceFit = Array.IndexOf(SpiceFitOptions, a.SpiceFit.Trim().ToLowerInvariant()) >= 0 ? a.SpiceFit.Trim().ToLowerInvariant() : a.SpiceFit,
        Store = Array.IndexOf(StoreOptions, a.Store.Trim().ToLowerInvariant()) >= 0 ? a.Store.Trim().ToLowerInvariant() : a.Store,
        StoreHarm = a.StoreHarmExpr, MeasureFrom = a.MeasureFromExpr,
    };

    public TransientAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled,
        StopExpr = Stop.Trim(), StepExpr = Step.Trim(), Method = Method, InitialState = InitialState,
        Solver = SolverIndex == 1 ? TransientAnalysis.SolverNgspice : TransientAnalysis.SolverNative,
        StartExpr = Blank(Start, "0"), MaxStepExpr = Blank(MaxStep, "auto"), RelTolExpr = Blank(RelTol, "1e-3"),
        VTolExpr = Blank(VTol, "1e-6"), ITolExpr = Blank(ITol, "1e-12"), IcExpr = Ic.Trim(),
        SaveSteps = SaveSteps ? "yes" : Analysis.NoiseDefault, MaxStepsExpr = Blank(MaxSteps, TransientAnalysis.MaxStepsDefault),
        // On the native solver the ngspice-only keys go back to their defaults: the native run refuses any of them set, and the
        // card greys their fields, so a value left behind by switching from ngspice could not be cleared.
        SpiceOptions = Ng ? SpiceOptions.Trim() : "",
        SpiceLines = Ng ? Blank(SpiceLines, TransientAnalysis.SpiceLinesDefault) : TransientAnalysis.SpiceLinesDefault,
        SpiceLineFreq = Ng ? Blank(SpiceLineFreq, TransientAnalysis.SpiceLineFreqDefault) : TransientAnalysis.SpiceLineFreqDefault,
        SpiceFit = Ng ? Blank(SpiceFit, TransientAnalysis.SpiceFitDefault) : TransientAnalysis.SpiceFitDefault,
        Store = Blank(Store, TransientAnalysis.StoreAuto), StoreHarmExpr = Blank(StoreHarm, TransientAnalysis.StoreHarmDefault),
        MeasureFromExpr = Blank(MeasureFrom, TransientAnalysis.MeasureFromDefault),
    };

    private bool Ng => SolverIndex == 1;

    private static string Blank(string v, string def) => v.Trim().Length == 0 ? def : v.Trim();
}
