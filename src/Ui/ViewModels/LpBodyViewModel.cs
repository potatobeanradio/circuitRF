using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Body VM for Loadpull analysis authoring (loadpull.md §2.1; analysis-authoring.md §4.2 L3).
///
/// Basic section (always visible): Load/Source tuner pickers, Tone (f₀ coefficient + unit),
/// Grid (.gam file), Pin start/max/step (dBm/dB), Compression (dB).
/// Advanced section (Expander, collapsed): Sweep, TuneHarm, GainType, MaxHarm, Tickle, MaxIter,
/// FFT oversample, Tolerance, DriveStepping, GuardHarmonic.
///
/// The tone is a coefficient + unit pair (NOT a combined expression), mirroring HB — this is what
/// lets a VAR with or without a unit resolve correctly via var-unit-wins at run time (brief 04b).
/// All expression fields have live "≈" previews via AnalysisPreviewHelper.
/// </summary>
public sealed partial class LpBodyViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;
    // Base for storing picked .gam paths relative — MUST be the engine's resolution base (the
    // workspace root, where netlist.cnl is written), NOT the schematic dir. Mirrors the SnP File
    // picker (ParameterEditorViewModel uses SchematicViewModel.WorkspaceRoot). A mismatch here
    // produced a wrong absolute path at run time (relative resolved against the wrong base).
    private readonly string? _workspaceRoot;

    // ── Lists exposed for AXAML x:Static / instance binding ───────────────────
    public static readonly string[] DriveSteppingOptions = HbBodyViewModel.DriveSteppingOptions;
    public static readonly string[] FreqUnits            = FreqUnitHelper.Units;

    /// <summary>Instance names of tuner components in the schematic (any of the three tuner kinds).</summary>
    public IReadOnlyList<string> TunerInstanceNames { get; }

    /// <summary>True when the schematic has no tuner component to pick (drives the inline hint).</summary>
    public bool HasNoTuners => TunerInstanceNames.Count == 0;

    // ── Basic ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private string  _loadTunerName   = "";
    [ObservableProperty] private string  _sourceTunerName = "";

    [ObservableProperty] private string _toneCoeff   = "1";
    [ObservableProperty] private string _toneUnit     = "GHz";
    private string _prevToneUnit = "GHz";
    [ObservableProperty] private string _tonePreview  = "";

    [ObservableProperty] private string _gridPath = "";

    [ObservableProperty] private string _pinStartExpr   = "-20";
    [ObservableProperty] private string _pinStartPreview = "";
    [ObservableProperty] private string _pinMaxExpr      = "10";
    [ObservableProperty] private string _pinMaxPreview   = "";
    [ObservableProperty] private string _pinStepExpr     = "1";
    [ObservableProperty] private string _pinStepPreview  = "";

    [ObservableProperty] private string _compressionExpr    = "3";
    [ObservableProperty] private string _compressionPreview = "";

    // ── Advanced ────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _advancedExpanded  = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSweepLoad), nameof(IsSweepSource))]
    private string _sweepExpr = "Load";   // "Load" | "Source"
    public bool IsSweepLoad   => !IsSweepSource;
    public bool IsSweepSource => SweepExpr.Trim().Equals("Source", System.StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _tuneHarmExpr    = "1";
    [ObservableProperty] private string _tuneHarmPreview = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGainGt), nameof(IsGainGp))]
    private string _gainTypeExpr = "Gt";   // "Gt" | "Gp"
    public bool IsGainGt => !IsGainGp;
    public bool IsGainGp => GainTypeExpr.Trim().Equals("Gp", System.StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _maxHarmonicExpr    = "5";
    [ObservableProperty] private string _maxHarmonicPreview = "";
    [ObservableProperty] private string _tickleExpr         = "-50";   // dBm, or "off"
    [ObservableProperty] private string _maxIterExpr        = "100";
    [ObservableProperty] private string _maxIterPreview     = "";
    [ObservableProperty] private string _fftOverSampleExpr  = "1";
    [ObservableProperty] private string _tolExpr            = "1e-6";
    [ObservableProperty] private string _tolPreview         = "";
    [ObservableProperty] private string _driveSteppingExpr  = "IfNecessary";
    [ObservableProperty] private string _guardHarmonicExpr  = "0";
    [ObservableProperty] private string _guardHarmonicPreview = "";

    // ── Validation ──────────────────────────────────────────────────────────────

    /// <summary>True only when the required fields are non-blank (LoadTuner, SourceTuner, Grid, tone, PinMax).</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(LoadTunerName)
        && !string.IsNullOrWhiteSpace(SourceTunerName)
        && !string.IsNullOrWhiteSpace(GridPath)
        && !string.IsNullOrWhiteSpace(ToneCoeff)
        && !string.IsNullOrWhiteSpace(PinMaxExpr);

    // ── Constructor ───────────────────────────────────────────────────────────

    public LpBodyViewModel(SchematicEditModel model, string? workspaceRoot = null)
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
    partial void OnTuneHarmExprChanged(string value)    => TuneHarmPreview    = Prev(value);
    partial void OnMaxHarmonicExprChanged(string value) => MaxHarmonicPreview = Prev(value);
    partial void OnMaxIterExprChanged(string value)     => MaxIterPreview     = Prev(value);
    partial void OnTolExprChanged(string value)         => TolPreview         = Prev(value);
    partial void OnGuardHarmonicExprChanged(string value) => GuardHarmonicPreview = Prev(value);

    partial void OnLoadTunerNameChanged(string value)   => OnPropertyChanged(nameof(IsValid));
    partial void OnSourceTunerNameChanged(string value) => OnPropertyChanged(nameof(IsValid));
    partial void OnGridPathChanged(string value)        => OnPropertyChanged(nameof(IsValid));

    private string Prev(string expr) => AnalysisPreviewHelper.ComputePreview(expr, _model);

    // ── Sweep / GainType toggle commands ──────────────────────────────────────

    [RelayCommand] private void SetSweepLoad()   => SweepExpr = "Load";
    [RelayCommand] private void SetSweepSource() => SweepExpr = "Source";
    [RelayCommand] private void SetGainGt()      => GainTypeExpr = "Gt";
    [RelayCommand] private void SetGainGp()      => GainTypeExpr = "Gp";

    // ── Grid file picker support ──────────────────────────────────────────────

    /// <summary>
    /// Applies a picked absolute .gam path, stored relative to the WORKSPACE ROOT when possible
    /// (forward-slash, cross-platform) per <see cref="SnpPathPolicy"/> — the engine's resolution base
    /// (where netlist.cnl is written). Mirrors the SnP File picker.
    /// </summary>
    public void ApplyPickedGridPath(string absolutePath)
        => GridPath = SnpPathPolicy.ToStored(absolutePath, _workspaceRoot);

    // ── Build ─────────────────────────────────────────────────────────────────

    public LoadpullAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled         = enabled,
        LoadTunerName   = LoadTunerName?.Trim()   ?? "",
        SourceTunerName = SourceTunerName?.Trim() ?? "",
        GridPath        = GridPath?.Trim()        ?? "",
        ToneExpr        = ToneCoeff,        // coefficient expression
        ToneUnit        = ToneUnit,         // frequency unit (var-unit-wins resolves at run time; brief 04b)
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
        // SourceDirectory set by the extractor/reader at run time, not here.
    };

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    public static LpBodyViewModel FromAnalysis(LoadpullAnalysis lp, SchematicEditModel model,
        string? workspaceRoot = null)
    {
        var vm = new LpBodyViewModel(model, workspaceRoot)
        {
            LoadTunerName   = lp.LoadTunerName,
            SourceTunerName = lp.SourceTunerName,
            GridPath        = lp.GridPath,
            PinStartExpr    = lp.PinStartExpr,
            PinMaxExpr      = lp.PinMaxExpr,
            PinStepExpr     = lp.PinStepExpr,
            MaxHarmonicExpr = lp.MaxHarmonicExpr,
            SweepExpr       = lp.SweepExpr,
            TuneHarmExpr    = lp.TuneHarmExpr,
            CompressionExpr = lp.CompressionExpr,
            GainTypeExpr    = lp.GainTypeExpr,
            TickleExpr      = lp.TickleExpr,
            MaxIterExpr     = lp.MaxIterExpr,
            FftOverSampleExpr = lp.FFTOverSampleExpr,
            TolExpr         = lp.TolExpr,
            DriveSteppingExpr = lp.DriveSteppingExpr,
            GuardHarmonicExpr = lp.GuardHarmonicExpr,
        };

        // Tone: read stored coeff + unit. Legacy nicety mirroring HB: when ToneUnit=="Hz" and ToneExpr
        // is a plain number, Split for pretty display (e.g. "2e9" → "2" GHz). Never Split an expression.
        string toneExpr = lp.ToneExpr;
        string toneUnit = string.IsNullOrEmpty(lp.ToneUnit) ? "Hz" : lp.ToneUnit;
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
}
