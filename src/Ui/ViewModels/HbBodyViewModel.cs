using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Body VM for Harmonic Balance analysis (analysis-authoring.md §4.2 L3).
///
/// Basic section (always visible): Tone (f₀), Max harmonics, Single/Multi-tone toggle.
/// When Multi-tone: Tone 2 + Max mix order appear inline.
/// Advanced section (Expander, collapsed by default): FFT oversample, tolerance, drive stepping,
/// guard harmonic, Newton λ, max iterations, optional parametric sweep.
///
/// All expression fields have live "≈" previews via AnalysisPreviewHelper.
/// </summary>
public sealed partial class HbBodyViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;

    // ── Drive stepping options (ComboBox) ─────────────────────────────────────
    public static readonly string[] DriveSteppingOptions = ["Always", "IfNecessary", "Never"];

    // ── Frequency unit list exposed for AXAML x:Static binding ───────────────
    public static readonly string[] FreqUnits = FreqUnitHelper.Units;

    // ── Basic: Tone (coefficient + unit) ─────────────────────────────────────
    [ObservableProperty] private string _toneCoeff    = "1";
    [ObservableProperty] private string _toneUnit     = "GHz";
    private string _prevToneUnit = "GHz";
    [ObservableProperty] private string _tonePreview  = "";

    [ObservableProperty] private string _maxHarmonicExpr    = "7";
    [ObservableProperty] private string _maxHarmonicPreview = "";

    // ── Single / Multi-tone toggle ────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleTone), nameof(IsMultiTone), nameof(ToneLabel))]
    private bool _multiTone = false;

    public bool   IsSingleTone => !MultiTone;
    public bool   IsMultiTone  =>  MultiTone;
    public string ToneLabel    =>  MultiTone ? "Tone 1" : "Tone (f₀)";

    // ── Multi-tone extra fields (Tone 2: coefficient + unit) ─────────────────
    [ObservableProperty] private string _tone2Coeff   = "2";
    [ObservableProperty] private string _tone2Unit    = "GHz";
    private string _prevTone2Unit = "GHz";
    [ObservableProperty] private string _tone2Preview = "";

    [ObservableProperty] private string _maxMixOrderExpr    = "5";
    [ObservableProperty] private string _maxMixOrderPreview = "";

    // ── Advanced: Newton / convergence ────────────────────────────────────────
    [ObservableProperty] private bool   _advancedExpanded    = false;
    [ObservableProperty] private string _fftOverSampleExpr   = "1";
    [ObservableProperty] private string _tolExpr             = "1e-6";
    [ObservableProperty] private string _tolPreview          = "";
    [ObservableProperty] private string _driveSteppingExpr   = "IfNecessary";
    [ObservableProperty] private string _guardHarmonicExpr   = "0";
    [ObservableProperty] private string _guardHarmonicPreview = "";
    [ObservableProperty] private string _lambdaExpr          = "1";
    [ObservableProperty] private string _lambdaPreview       = "";
    [ObservableProperty] private string _maxIterExpr         = "100";
    [ObservableProperty] private string _maxIterPreview      = "";

    // ── Parametric sweep (optional, inside Advanced) ──────────────────────────
    [ObservableProperty] private bool   _sweepEnabled    = false;
    [ObservableProperty] private string _sweepVarName    = "";
    [ObservableProperty] private string _sweepStartExpr  = "0";
    [ObservableProperty] private string _sweepStartPreview = "";
    [ObservableProperty] private string _sweepStopExpr   = "1";
    [ObservableProperty] private string _sweepStopPreview = "";
    [ObservableProperty] private string _sweepStepExpr   = "0.1";
    [ObservableProperty] private string _sweepStepPreview = "";

    // ── Constructor ───────────────────────────────────────────────────────────

    public HbBodyViewModel(SchematicEditModel model) => _model = model;
    // Field initializers set all defaults. Previews are empty for bare-number defaults —
    // no need to call property setters in the constructor.

    // ── Preview side-effects ──────────────────────────────────────────────────

    partial void OnToneCoeffChanged(string value)  => TonePreview  = Prev(FreqUnitHelper.ToHzExpr(value, ToneUnit));
    partial void OnTone2CoeffChanged(string value) => Tone2Preview = Prev(FreqUnitHelper.ToHzExpr(value, Tone2Unit));
    partial void OnMaxHarmonicExprChanged(string value)   => MaxHarmonicPreview = Prev(value);
    partial void OnMaxMixOrderExprChanged(string value)   => MaxMixOrderPreview = Prev(value);

    partial void OnToneUnitChanged(string value)
    {
        ToneCoeff     = FreqUnitHelper.Rescale(ToneCoeff, _prevToneUnit, value);
        _prevToneUnit = value;
        TonePreview   = Prev(FreqUnitHelper.ToHzExpr(ToneCoeff, value));
    }

    partial void OnTone2UnitChanged(string value)
    {
        Tone2Coeff     = FreqUnitHelper.Rescale(Tone2Coeff, _prevTone2Unit, value);
        _prevTone2Unit = value;
        Tone2Preview   = Prev(FreqUnitHelper.ToHzExpr(Tone2Coeff, value));
    }
    partial void OnTolExprChanged(string value)           => TolPreview           = Prev(value);
    partial void OnGuardHarmonicExprChanged(string value) => GuardHarmonicPreview = Prev(value);
    partial void OnLambdaExprChanged(string value)        => LambdaPreview        = Prev(value);
    partial void OnMaxIterExprChanged(string value)       => MaxIterPreview       = Prev(value);
    partial void OnSweepStartExprChanged(string value)    => SweepStartPreview    = Prev(value);
    partial void OnSweepStopExprChanged(string value)     => SweepStopPreview     = Prev(value);
    partial void OnSweepStepExprChanged(string value)     => SweepStepPreview     = Prev(value);

    private string Prev(string expr) => AnalysisPreviewHelper.ComputePreview(expr, _model);

    // ── Single / Multi-tone commands ──────────────────────────────────────────

    [RelayCommand] private void SetSingleTone() => MultiTone = false;
    [RelayCommand] private void SetMultiTone()  => MultiTone = true;

    // ── Build ─────────────────────────────────────────────────────────────────

    public HarmonicBalanceAnalysis BuildAnalysis(string name, bool enabled)
    {
        string? sweepVar   = SweepEnabled && SweepVarName.Trim().Length > 0 ? SweepVarName.Trim() : null;
        string? sweepStart = sweepVar is not null ? SweepStartExpr : null;
        string? sweepStop  = sweepVar is not null ? SweepStopExpr  : null;
        string? sweepStep  = sweepVar is not null ? SweepStepExpr  : null;

        HarmonicBalanceAnalysis analysis;

        string toneHz  = FreqUnitHelper.ToHzExpr(ToneCoeff,  ToneUnit);
        string tone2Hz = FreqUnitHelper.ToHzExpr(Tone2Coeff, Tone2Unit);

        if (MultiTone)
        {
            analysis = new HarmonicBalanceAnalysis(name)
            {
                NumFreqsExpr      = "2",
                ToneExprs         = [toneHz, tone2Hz],
                MaxMixOrderExpr   = MaxMixOrderExpr,
                MaxHarmonicExpr   = MaxHarmonicExpr,
                FFTOverSampleExpr = FftOverSampleExpr,
                TolExpr           = TolExpr,
                DriveSteppingExpr = DriveSteppingExpr,
                GuardHarmonicExpr = GuardHarmonicExpr,
                LambdaExpr        = LambdaExpr,
                MaxIterExpr       = MaxIterExpr,
                SweepVarName      = sweepVar,
                SweepStartExpr    = sweepStart,
                SweepStopExpr     = sweepStop,
                SweepStepExpr     = sweepStep,
            };
        }
        else
        {
            analysis = new HarmonicBalanceAnalysis(name)
            {
                ToneExpr          = toneHz,
                MaxHarmonicExpr   = MaxHarmonicExpr,
                FFTOverSampleExpr = FftOverSampleExpr,
                TolExpr           = TolExpr,
                DriveSteppingExpr = DriveSteppingExpr,
                GuardHarmonicExpr = GuardHarmonicExpr,
                LambdaExpr        = LambdaExpr,
                MaxIterExpr       = MaxIterExpr,
                SweepVarName      = sweepVar,
                SweepStartExpr    = sweepStart,
                SweepStopExpr     = sweepStop,
                SweepStepExpr     = sweepStep,
            };
        }

        analysis.Enabled = enabled;
        return analysis;
    }

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    public static HbBodyViewModel FromAnalysis(HarmonicBalanceAnalysis hb, SchematicEditModel model)
    {
        var vm = new HbBodyViewModel(model);

        // Basic: split Hz expressions into (coeff, unit) for display.
        // Set _prevXUnit before ToneUnit so OnToneUnitChanged sees from==to → no rescaling.
        var (toneCoeff, toneUnit) = FreqUnitHelper.Split(hb.ToneExpr);
        vm._prevToneUnit = toneUnit;
        vm.ToneUnit      = toneUnit;
        vm.ToneCoeff     = toneCoeff;
        vm.MaxHarmonicExpr = hb.MaxHarmonicExpr;

        if (int.TryParse(hb.NumFreqsExpr, out int n) && n > 1)
        {
            vm.MultiTone = true;
            string tone2src = hb.ToneExprs.Length > 1 ? hb.ToneExprs[1] : "2e9";
            var (tone2Coeff, tone2Unit) = FreqUnitHelper.Split(tone2src);
            vm._prevTone2Unit  = tone2Unit;
            vm.Tone2Unit       = tone2Unit;
            vm.Tone2Coeff      = tone2Coeff;
            vm.MaxMixOrderExpr = hb.MaxMixOrderExpr;
        }

        // Advanced
        vm.FftOverSampleExpr = hb.FFTOverSampleExpr;
        vm.TolExpr           = hb.TolExpr;
        vm.DriveSteppingExpr = hb.DriveSteppingExpr;
        vm.GuardHarmonicExpr = hb.GuardHarmonicExpr;
        vm.LambdaExpr        = hb.LambdaExpr;
        vm.MaxIterExpr       = hb.MaxIterExpr;

        // Sweep
        if (hb.SweepVarName is not null)
        {
            vm.SweepEnabled   = true;
            vm.SweepVarName   = hb.SweepVarName;
            vm.SweepStartExpr = hb.SweepStartExpr ?? "0";
            vm.SweepStopExpr  = hb.SweepStopExpr  ?? "1";
            vm.SweepStepExpr  = hb.SweepStepExpr  ?? "0.1";
        }

        return vm;
    }
}
