using System;
using CircuitRF.Core.Design;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Transient analysis card body (type=tran, brief-trn-1 R-trn1-12). Every field is the directive's raw text, resolved at run
/// time. Stop, Step, Method, Initial state and Solver are on the card; every other key under More (brief TRN-5 R-trn5-4), the
/// ngspice-only ones enabled only with that solver. The solver list holds ngspice, disabled until brief TRN-7 builds it; a bench
/// that already says ngspice keeps saying it, with the note beneath.
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

    /// <summary>The tooltip on the disabled ngspice choice, and the note under a bench that already names it.</summary>
    public const string NgspiceNotBuilt = "The ngspice solver is not built yet. Set Solver back to native to run this analysis.";

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

    /// <summary>Whether the ngspice choice can be picked: never, until brief TRN-7.</summary>
    public bool NgspiceAvailable => false;

    public string NgspiceTooltip => NgspiceNotBuilt;

    /// <summary>The line under the solver: empty with the native solver (its space kept, so nothing below moves).</summary>
    public string SolverNote => SolverIndex == 1 ? NgspiceNotBuilt : "";

    partial void OnSolverIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SolverNote));
        OnPropertyChanged(nameof(IsNgspice));
    }

    public string MethodDescription => MethodHelp[Math.Max(0, Array.IndexOf(MethodOptions, Method))];
    partial void OnMethodChanged(string value) => OnPropertyChanged(nameof(MethodDescription));

    public string InitialDescription => InitialHelp[Math.Max(0, Array.IndexOf(InitialOptions, InitialState))];
    partial void OnInitialStateChanged(string value) => OnPropertyChanged(nameof(InitialDescription));

    /// <summary>A transient needs a Stop.</summary>
    public bool IsValid => Stop.Trim().Length > 0;
    partial void OnStopChanged(string value) => OnPropertyChanged(nameof(IsValid));

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
    };

    public TransientAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled,
        StopExpr = Stop.Trim(), StepExpr = Step.Trim(), Method = Method, InitialState = InitialState,
        Solver = SolverIndex == 1 ? TransientAnalysis.SolverNgspice : TransientAnalysis.SolverNative,
        StartExpr = Blank(Start, "0"), MaxStepExpr = Blank(MaxStep, "auto"), RelTolExpr = Blank(RelTol, "1e-3"),
        VTolExpr = Blank(VTol, "1e-6"), ITolExpr = Blank(ITol, "1e-12"), IcExpr = Ic.Trim(),
        SaveSteps = SaveSteps ? "yes" : Analysis.NoiseDefault, MaxStepsExpr = Blank(MaxSteps, TransientAnalysis.MaxStepsDefault),
        SpiceOptions = SpiceOptions.Trim(), SpiceLines = Blank(SpiceLines, TransientAnalysis.SpiceLinesDefault),
        SpiceLineFreq = Blank(SpiceLineFreq, TransientAnalysis.SpiceLineFreqDefault), SpiceFit = Blank(SpiceFit, TransientAnalysis.SpiceFitDefault),
    };

    private static string Blank(string v, string def) => v.Trim().Length == 0 ? def : v.Trim();
}
