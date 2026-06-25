using System.Globalization;
using System.Linq;
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

    // ── Constructor ───────────────────────────────────────────────────────────

    public HbBodyViewModel(SchematicEditModel model) => _model = model;
    // Field initializers set all defaults. Previews are empty for bare-number defaults —
    // no need to call property setters in the constructor.

    // ── Preview side-effects ──────────────────────────────────────────────────

    partial void OnToneCoeffChanged(string value)  => TonePreview  = AnalysisPreviewHelper.ComputeFreqPreview(value, ToneUnit,  _model);
    partial void OnTone2CoeffChanged(string value) => Tone2Preview = AnalysisPreviewHelper.ComputeFreqPreview(value, Tone2Unit, _model);
    partial void OnMaxHarmonicExprChanged(string value)   => MaxHarmonicPreview = Prev(value);
    partial void OnMaxMixOrderExprChanged(string value)   => MaxMixOrderPreview = Prev(value);

    partial void OnToneUnitChanged(string value)
    {
        ToneCoeff     = FreqUnitHelper.Rescale(ToneCoeff, _prevToneUnit, value);
        _prevToneUnit = value;
        TonePreview   = AnalysisPreviewHelper.ComputeFreqPreview(ToneCoeff, value, _model);
    }

    partial void OnTone2UnitChanged(string value)
    {
        Tone2Coeff     = FreqUnitHelper.Rescale(Tone2Coeff, _prevTone2Unit, value);
        _prevTone2Unit = value;
        Tone2Preview   = AnalysisPreviewHelper.ComputeFreqPreview(Tone2Coeff, value, _model);
    }
    partial void OnTolExprChanged(string value)           => TolPreview           = Prev(value);
    partial void OnGuardHarmonicExprChanged(string value) => GuardHarmonicPreview = Prev(value);
    partial void OnLambdaExprChanged(string value)        => LambdaPreview        = Prev(value);
    partial void OnMaxIterExprChanged(string value)       => MaxIterPreview       = Prev(value);

    private string Prev(string expr) => AnalysisPreviewHelper.ComputePreview(expr, _model);

    // ── Single / Multi-tone commands ──────────────────────────────────────────

    [RelayCommand] private void SetSingleTone() => MultiTone = false;

    [RelayCommand]
    private void SetMultiTone()
    {
        MultiTone = true;
        // Convenience: adopt the tone frequencies from a PnTone on the schematic so the dialog matches
        // the multi-tone source. Graceful no-op if there's no PnTone (or it has no Freq[i]).
        AdoptPnToneTones();
    }

    /// <summary>
    /// Copies Freq[1]/Freq[2] (expression + unit, var/expression-preserving) from the first PnTone on
    /// the schematic into Tone 1 / Tone 2. No-op when no PnTone exists or a tone field is blank.
    /// </summary>
    private void AdoptPnToneTones()
    {
        var pn = _model.Components.FirstOrDefault(c => c.Symbol == SymbolKind.PnTone);
        if (pn is null) return;

        if (TryReadFreq(pn, 1, out string f1, out string u1))
        {
            _prevToneUnit = u1;   // set before ToneUnit so OnToneUnitChanged sees from==to (no rescale)
            ToneUnit  = u1;
            ToneCoeff = f1;
        }
        if (TryReadFreq(pn, 2, out string f2, out string u2))
        {
            _prevTone2Unit = u2;
            Tone2Unit  = u2;
            Tone2Coeff = f2;
        }
    }

    private static bool TryReadFreq(EditableComponent pn, int i, out string expr, out string unit)
    {
        var p = pn.Parameters.FirstOrDefault(q => q.Name == $"Freq[{i}]");
        if (p is null || string.IsNullOrWhiteSpace(p.Expression)) { expr = ""; unit = ""; return false; }
        expr = p.Expression;
        unit = string.IsNullOrEmpty(p.Unit) ? "Hz" : p.Unit;
        return true;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    public HarmonicBalanceAnalysis BuildAnalysis(string name, bool enabled)
    {
        // Store raw expr + separate unit — do NOT bake via ToHzExpr.
        HarmonicBalanceAnalysis analysis = MultiTone
            ? new HarmonicBalanceAnalysis(name)
            {
                // Mirror Tone 1 into the scalar ToneExpr/ToneUnit as well as ToneExprs[0]. The engine
                // reads ToneExprs[0] for multi-tone, but FromAnalysis and other consumers read the
                // scalar field — keeping both in sync makes the dialog round-trip lossless.
                ToneExpr          = ToneCoeff,
                ToneUnit          = ToneUnit,
                NumFreqsExpr      = "2",
                ToneExprs         = [ToneCoeff, Tone2Coeff],
                ToneUnits         = [ToneUnit,  Tone2Unit],
                MaxMixOrderExpr   = MaxMixOrderExpr,
                MaxHarmonicExpr   = MaxHarmonicExpr,
                FFTOverSampleExpr = FftOverSampleExpr,
                TolExpr           = TolExpr,
                DriveSteppingExpr = DriveSteppingExpr,
                GuardHarmonicExpr = GuardHarmonicExpr,
                LambdaExpr        = LambdaExpr,
                MaxIterExpr       = MaxIterExpr,
            }
            : new HarmonicBalanceAnalysis(name)
            {
                ToneExpr          = ToneCoeff,
                ToneUnit          = ToneUnit,
                MaxHarmonicExpr   = MaxHarmonicExpr,
                FFTOverSampleExpr = FftOverSampleExpr,
                TolExpr           = TolExpr,
                DriveSteppingExpr = DriveSteppingExpr,
                GuardHarmonicExpr = GuardHarmonicExpr,
                LambdaExpr        = LambdaExpr,
                MaxIterExpr       = MaxIterExpr,
            };

        analysis.Enabled = enabled;
        return analysis;
    }

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    public static HbBodyViewModel FromAnalysis(HarmonicBalanceAnalysis hb, SchematicEditModel model)
    {
        var vm = new HbBodyViewModel(model);

        // Read stored raw expr + unit directly.
        // Set _prevXUnit before ToneUnit so OnToneUnitChanged sees from==to → no rescaling.
        // Legacy nicety: when ToneUnit=="Hz" and ToneExpr is a plain number, use Split for
        // pretty display (e.g. "2.4e9" → "2.4" GHz). Never Split a non-numeric expression.
        bool multi = int.TryParse(hb.NumFreqsExpr, out int n) && n > 1;

        // Tone 1's canonical source in multi-tone is ToneExprs[0] (what the engine reads); single-tone
        // uses the scalar ToneExpr. Reading the right field makes a multi-tone round-trip lossless even
        // for analyses whose scalar ToneExpr was never populated (the original dialog-OK bug).
        string toneExpr = multi && hb.ToneExprs.Length > 0 ? hb.ToneExprs[0] : hb.ToneExpr;
        string toneUnit = multi && hb.ToneUnits.Length > 0 ? hb.ToneUnits[0]
                        : string.IsNullOrEmpty(hb.ToneUnit) ? "Hz" : hb.ToneUnit;
        if (toneUnit == "Hz")
        {
            var (tc, tu) = FreqUnitHelper.Split(toneExpr);
            toneExpr = tc; toneUnit = tu;
        }
        vm._prevToneUnit = toneUnit;
        vm.ToneUnit      = toneUnit;
        vm.ToneCoeff     = toneExpr;
        vm.MaxHarmonicExpr = hb.MaxHarmonicExpr;

        if (multi)
        {
            vm.MultiTone = true;
            string tone2src  = hb.ToneExprs.Length  > 1 ? hb.ToneExprs[1]  : "2e9";
            string tone2unit = hb.ToneUnits.Length   > 1 ? hb.ToneUnits[1]  : "Hz";
            if (tone2unit == "Hz")
            {
                var (t2c, t2u) = FreqUnitHelper.Split(tone2src);
                tone2src = t2c; tone2unit = t2u;
            }
            vm._prevTone2Unit  = tone2unit;
            vm.Tone2Unit       = tone2unit;
            vm.Tone2Coeff      = tone2src;
            vm.MaxMixOrderExpr = hb.MaxMixOrderExpr;
        }

        // Advanced
        vm.FftOverSampleExpr = hb.FFTOverSampleExpr;
        vm.TolExpr           = hb.TolExpr;
        vm.DriveSteppingExpr = hb.DriveSteppingExpr;
        vm.GuardHarmonicExpr = hb.GuardHarmonicExpr;
        vm.LambdaExpr        = hb.LambdaExpr;
        vm.MaxIterExpr       = hb.MaxIterExpr;

        return vm;
    }
}
