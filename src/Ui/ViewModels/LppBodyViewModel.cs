using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Body VM for Loadpull-Pursuit authoring (loadpull_pursuit.md §3; analysis-authoring.md §4.2 L3).
///
/// All loadpull keys EXCEPT Grid (the pursuit generates its own grid), plus the pursuit keys:
/// EffType, Zsource backoff, SearchMethod; the follow-on group (Create + source match + optional
/// OutputGrid); and the grid-builder VSWR group.
///
/// The tone is a coefficient + unit pair (mirrors HB / the LP body), NOT a combined expression —
/// this is what lets a VAR with or without a unit resolve correctly via var-unit-wins (brief 04b).
/// Fields parallel <see cref="LpBodyViewModel"/> (brief 05 sanctioned fallback (b) — no shared-core
/// refactor, to keep the LP body's gate tests intact).
/// </summary>
public sealed partial class LppBodyViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;
    // Base for storing the picked OutputGrid .gam path relative — MUST be the engine's resolution
    // base (the workspace root, where netlist.cnl is written), NOT the schematic dir. Mirrors the
    // SnP File picker. A mismatch produced a wrong absolute path at run time.
    private readonly string? _workspaceRoot;

    // ── Lists exposed for AXAML binding ───────────────────────────────────────
    public static readonly string[] DriveSteppingOptions = HbBodyViewModel.DriveSteppingOptions;
    public static readonly string[] FreqUnits            = FreqUnitHelper.Units;
    public static readonly string[] SearchMethods        = ["SteepestAscent", "IteratedQuadratic"];
    public static readonly string[] ZsourceModes         = ["MXE", "MXP", "None"];

    /// <summary>Instance names of tuner components in the schematic (any of the three tuner kinds).</summary>
    public IReadOnlyList<string> TunerInstanceNames { get; }
    public bool HasNoTuners => TunerInstanceNames.Count == 0;

    // ── Shared LP fields (minus Grid) ─────────────────────────────────────────
    [ObservableProperty] private string _loadTunerName   = "";
    [ObservableProperty] private string _sourceTunerName = "";

    [ObservableProperty] private string _toneCoeff   = "1";
    [ObservableProperty] private string _toneUnit     = "GHz";
    private string _prevToneUnit = "GHz";
    [ObservableProperty] private string _tonePreview  = "";

    [ObservableProperty] private string _pinStartExpr   = "-20";
    [ObservableProperty] private string _pinStartPreview = "";
    [ObservableProperty] private string _pinMaxExpr      = "10";
    [ObservableProperty] private string _pinMaxPreview   = "";
    [ObservableProperty] private string _pinStepExpr     = "1";
    [ObservableProperty] private string _pinStepPreview  = "";

    [ObservableProperty] private string _compressionExpr    = "3";
    [ObservableProperty] private string _compressionPreview = "";

    // ── Pursuit-specific (Basic) ──────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEffDe), nameof(IsEffPae))]
    private string _effTypeExpr = "DE";   // "DE" | "PAE"
    public bool IsEffDe  => !IsEffPae;
    public bool IsEffPae => EffTypeExpr.Trim().Equals("PAE", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _zsourceOboExpr    = "5";
    [ObservableProperty] private string _zsourceOboPreview = "";
    [ObservableProperty] private string _searchMethodExpr  = "SteepestAscent";

    // ── Follow-on group ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowOnEnabled))]
    private bool _createLoadpullResult = true;
    /// <summary>Drives the IsEnabled of the source-match combo (disabled when Create is off).</summary>
    public bool FollowOnEnabled => CreateLoadpullResult;

    [ObservableProperty] private string _loadpullResultZsource = "MXE";   // "MXE" | "MXP" | "None"
    [ObservableProperty] private string _outputGridPath        = "";       // blank = no file
    [ObservableProperty] private bool   _outputExpanded        = false;

    // ── Grid-builder group (Advanced) ─────────────────────────────────────────
    [ObservableProperty] private string _vswr1Expr            = "1.5";
    [ObservableProperty] private string _vswr1ResolutionExpr  = "4";
    [ObservableProperty] private string _vswr2Expr            = "3";
    [ObservableProperty] private string _vswr2ResolutionExpr  = "4";
    [ObservableProperty] private bool   _keepNonconverging    = false;
    [ObservableProperty] private string _nonconvergentVswrExpr = "1.05";

    // ── Shared Advanced LP fields ─────────────────────────────────────────────
    [ObservableProperty] private bool   _advancedExpanded  = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSweepLoad), nameof(IsSweepSource))]
    private string _sweepExpr = "Load";
    public bool IsSweepLoad   => !IsSweepSource;
    public bool IsSweepSource => SweepExpr.Trim().Equals("Source", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _tuneHarmExpr    = "1";
    [ObservableProperty] private string _tuneHarmPreview = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGainGt), nameof(IsGainGp))]
    private string _gainTypeExpr = "Gt";
    public bool IsGainGt => !IsGainGp;
    public bool IsGainGp => GainTypeExpr.Trim().Equals("Gp", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _maxHarmonicExpr    = "5";
    [ObservableProperty] private string _maxHarmonicPreview = "";
    [ObservableProperty] private string _tickleExpr         = "-50";
    [ObservableProperty] private string _maxIterExpr        = "100";
    [ObservableProperty] private string _maxIterPreview     = "";
    [ObservableProperty] private string _fftOverSampleExpr  = "1";
    [ObservableProperty] private string _tolExpr            = "1e-6";
    [ObservableProperty] private string _tolPreview         = "";
    [ObservableProperty] private string _driveSteppingExpr  = "IfNecessary";
    [ObservableProperty] private string _guardHarmonicExpr  = "0";
    [ObservableProperty] private string _guardHarmonicPreview = "";

    // ── Validation (Grid NOT required for LPP) ────────────────────────────────
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(LoadTunerName)
        && !string.IsNullOrWhiteSpace(SourceTunerName)
        && !string.IsNullOrWhiteSpace(ToneCoeff)
        && !string.IsNullOrWhiteSpace(PinMaxExpr);

    // ── Constructor ───────────────────────────────────────────────────────────

    public LppBodyViewModel(SchematicEditModel model, string? workspaceRoot = null)
    {
        _model = model;
        _workspaceRoot = workspaceRoot;
        TunerInstanceNames = model.Components
            .Where(c => c.Symbol is SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner)
            .Select(c => c.InstanceName)
            .ToList();
    }

    // ── Preview side-effects ──────────────────────────────────────────────────

    partial void OnToneCoeffChanged(string value)
        => TonePreview = AnalysisPreviewHelper.ComputeFreqPreview(value, ToneUnit, _model);

    partial void OnToneUnitChanged(string value)
    {
        ToneCoeff     = FreqUnitHelper.Rescale(ToneCoeff, _prevToneUnit, value);
        _prevToneUnit = value;
        TonePreview   = AnalysisPreviewHelper.ComputeFreqPreview(ToneCoeff, value, _model);
    }

    partial void OnPinStartExprChanged(string value)    => PinStartPreview    = Prev(value);
    partial void OnPinMaxExprChanged(string value)      => PinMaxPreview      = Prev(value);
    partial void OnPinStepExprChanged(string value)     => PinStepPreview     = Prev(value);
    partial void OnCompressionExprChanged(string value) => CompressionPreview = Prev(value);
    partial void OnZsourceOboExprChanged(string value)  => ZsourceOboPreview  = Prev(value);
    partial void OnTuneHarmExprChanged(string value)    => TuneHarmPreview    = Prev(value);
    partial void OnMaxHarmonicExprChanged(string value) => MaxHarmonicPreview = Prev(value);
    partial void OnMaxIterExprChanged(string value)     => MaxIterPreview     = Prev(value);
    partial void OnTolExprChanged(string value)         => TolPreview         = Prev(value);
    partial void OnGuardHarmonicExprChanged(string value) => GuardHarmonicPreview = Prev(value);

    partial void OnLoadTunerNameChanged(string value)   => OnPropertyChanged(nameof(IsValid));
    partial void OnSourceTunerNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    private string Prev(string expr) => AnalysisPreviewHelper.ComputePreview(expr, _model);

    // ── Toggle commands ───────────────────────────────────────────────────────

    [RelayCommand] private void SetEffDe()       => EffTypeExpr = "DE";
    [RelayCommand] private void SetEffPae()      => EffTypeExpr = "PAE";
    [RelayCommand] private void SetSweepLoad()   => SweepExpr = "Load";
    [RelayCommand] private void SetSweepSource() => SweepExpr = "Source";
    [RelayCommand] private void SetGainGt()      => GainTypeExpr = "Gt";
    [RelayCommand] private void SetGainGp()      => GainTypeExpr = "Gp";

    // ── Output .gam grid picker support ───────────────────────────────────────

    /// <summary>Applies a picked absolute .gam save path, stored relative to the WORKSPACE ROOT when
    /// possible (the engine's resolution base, where netlist.cnl is written). Mirrors the SnP picker.</summary>
    public void ApplyPickedOutputGridPath(string absolutePath)
        => OutputGridPath = SnpPathPolicy.ToStored(absolutePath, _workspaceRoot);

    // ── Build ─────────────────────────────────────────────────────────────────

    public LoadpullPursuitAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled         = enabled,
        LoadTunerName   = LoadTunerName?.Trim()   ?? "",
        SourceTunerName = SourceTunerName?.Trim() ?? "",
        ToneExpr        = ToneCoeff,
        ToneUnit        = ToneUnit,
        PinStartExpr    = PinStartExpr,
        PinMaxExpr      = PinMaxExpr,
        PinStepExpr     = PinStepExpr,
        MaxHarmonicExpr = MaxHarmonicExpr,
        SweepExpr       = SweepExpr,
        TuneHarmExpr    = TuneHarmExpr,
        CompressionExpr = CompressionExpr,
        GainTypeExpr    = GainTypeExpr,
        TickleExpr      = TickleExpr,
        MaxIterExpr     = MaxIterExpr,
        FFTOverSampleExpr = FftOverSampleExpr,
        TolExpr         = TolExpr,
        DriveSteppingExpr = DriveSteppingExpr,
        GuardHarmonicExpr = GuardHarmonicExpr,
        // Pursuit keys
        EffTypeExpr               = EffTypeExpr,
        ZsourceOBOExpr            = ZsourceOboExpr,
        SearchMethodExpr          = SearchMethodExpr,
        OutputGridPath            = string.IsNullOrWhiteSpace(OutputGridPath) ? null : OutputGridPath.Trim(),
        Vswr1Expr                 = Vswr1Expr,
        Vswr1ResolutionExpr       = Vswr1ResolutionExpr,
        Vswr2Expr                 = Vswr2Expr,
        Vswr2ResolutionExpr       = Vswr2ResolutionExpr,
        KeepNonconvergingExpr     = KeepNonconverging ? "true" : "false",
        NonconvergentVswrExpr     = NonconvergentVswrExpr,
        CreateLoadpullResultExpr  = CreateLoadpullResult ? "true" : "false",
        LoadpullResultZsourceExpr = LoadpullResultZsource,
        // SourceDirectory set by the extractor/reader at run time, not here.
    };

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    public static LppBodyViewModel FromAnalysis(LoadpullPursuitAnalysis lpp, SchematicEditModel model,
        string? workspaceRoot = null)
    {
        var vm = new LppBodyViewModel(model, workspaceRoot)
        {
            LoadTunerName   = lpp.LoadTunerName,
            SourceTunerName = lpp.SourceTunerName,
            PinStartExpr    = lpp.PinStartExpr,
            PinMaxExpr      = lpp.PinMaxExpr,
            PinStepExpr     = lpp.PinStepExpr,
            MaxHarmonicExpr = lpp.MaxHarmonicExpr,
            SweepExpr       = lpp.SweepExpr,
            TuneHarmExpr    = lpp.TuneHarmExpr,
            CompressionExpr = lpp.CompressionExpr,
            GainTypeExpr    = lpp.GainTypeExpr,
            TickleExpr      = lpp.TickleExpr,
            MaxIterExpr     = lpp.MaxIterExpr,
            FftOverSampleExpr = lpp.FFTOverSampleExpr,
            TolExpr         = lpp.TolExpr,
            DriveSteppingExpr = lpp.DriveSteppingExpr,
            GuardHarmonicExpr = lpp.GuardHarmonicExpr,
            EffTypeExpr           = lpp.EffTypeExpr,
            ZsourceOboExpr        = lpp.ZsourceOBOExpr,
            SearchMethodExpr      = lpp.SearchMethodExpr,
            OutputGridPath        = lpp.OutputGridPath ?? "",
            Vswr1Expr             = lpp.Vswr1Expr,
            Vswr1ResolutionExpr   = lpp.Vswr1ResolutionExpr,
            Vswr2Expr             = lpp.Vswr2Expr,
            Vswr2ResolutionExpr   = lpp.Vswr2ResolutionExpr,
            KeepNonconverging     = ParseBool(lpp.KeepNonconvergingExpr, false),
            NonconvergentVswrExpr = lpp.NonconvergentVswrExpr,
            CreateLoadpullResult  = ParseBool(lpp.CreateLoadpullResultExpr, true),
            LoadpullResultZsource = lpp.LoadpullResultZsourceExpr,
        };

        // Tone: read stored coeff + unit; Split nicety when unit=="Hz" and the expr is a plain number.
        string toneExpr = lpp.ToneExpr;
        string toneUnit = string.IsNullOrEmpty(lpp.ToneUnit) ? "Hz" : lpp.ToneUnit;
        if (toneUnit == "Hz")
        {
            var (tc, tu) = FreqUnitHelper.Split(toneExpr);
            toneExpr = tc; toneUnit = tu;
        }
        vm._prevToneUnit = toneUnit;
        vm.ToneUnit      = toneUnit;
        vm.ToneCoeff     = toneExpr;

        return vm;
    }

    private static bool ParseBool(string s, bool def)
    {
        var t = s.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)  || t == "1" ||
            t.Equals("on",   StringComparison.OrdinalIgnoreCase)  || t.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0" ||
            t.Equals("off",   StringComparison.OrdinalIgnoreCase) || t.Equals("no",  StringComparison.OrdinalIgnoreCase))
            return false;
        return def;
    }
}
