using System;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for one S-parameter frequency-sweep segment (analysis-authoring.md §4.2 L2).
/// Wraps a FrequencySpec: observable Start/Stop/Step coefficient + unit selectors with
/// live "≈" previews via AnalysisPreviewHelper. The unit ComboBox rescales the coefficient
/// automatically when the unit changes so the Hz value stays constant.
/// Mode/Kind are toggled via Commands. CanRemoveSelf is set by the parent SpBodyViewModel.
/// </summary>
public sealed partial class FrequencySpecViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;
    private Action<FrequencySpecViewModel>? _removeCallback;

    // ── Frequency unit list exposed for AXAML x:Static binding ───────────────
    public static readonly string[] FreqUnits = FreqUnitHelper.Units;

    // ── Start frequency: coefficient + unit ──────────────────────────────────
    [ObservableProperty] private string _startCoeff = "1";
    [ObservableProperty] private string _startUnit  = "GHz";
    private string _prevStartUnit = "GHz";

    // ── Stop frequency: coefficient + unit ───────────────────────────────────
    [ObservableProperty] private string _stopCoeff = "10";
    [ObservableProperty] private string _stopUnit  = "GHz";
    private string _prevStopUnit = "GHz";

    // ── Step frequency: coefficient + unit (Step mode only) ──────────────────
    [ObservableProperty] private string _stepCoeff = "100";
    [ObservableProperty] private string _stepUnit  = "MHz";
    private string _prevStepUnit = "MHz";

    // ── Point count (Points mode) ─────────────────────────────────────────────
    [ObservableProperty] private string _numPointsExpr = "101";

    // ── Sweep mode / kind ─────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepMode), nameof(IsPointsMode))]
    private FreqSpecMode _mode = FreqSpecMode.PointCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinear), nameof(IsLog))]
    private SweepKind _kind = SweepKind.Linear;

    // ── Preview labels (shown under expression TextBoxes) ─────────────────────
    [ObservableProperty] private string _startPreview     = "";
    [ObservableProperty] private string _stopPreview      = "";
    [ObservableProperty] private string _stepPreview      = "";
    [ObservableProperty] private string _numPointsPreview = "";

    // ── Remove gate ───────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _canRemoveSelf = true;

    // ── Derived toggle bools ──────────────────────────────────────────────────
    public bool IsStepMode   => Mode == FreqSpecMode.StepSize;
    public bool IsPointsMode => Mode == FreqSpecMode.PointCount;
    public bool IsLinear     => Kind == SweepKind.Linear;
    public bool IsLog        => Kind == SweepKind.Log;

    // ── Constructor ───────────────────────────────────────────────────────────

    public FrequencySpecViewModel(SchematicEditModel model, FrequencySpec? seed = null)
    {
        _model = model;

        if (seed is not null)
        {
            _mode = seed.Mode;
            _kind = seed.Kind;

            // Read stored raw expr + unit directly.
            // Legacy nicety: when unit=="Hz" and expr is a plain number, use Split for pretty
            // display (e.g. old baked "1e9" → "1" GHz). Never Split a non-numeric expression.
            string startExpr = seed.StartExpr;
            string startUnit = string.IsNullOrEmpty(seed.StartUnit) ? "Hz" : seed.StartUnit;
            if (startUnit == "Hz")
            {
                var (sc, su) = FreqUnitHelper.Split(startExpr);
                startExpr = sc; startUnit = su;
            }
            _startCoeff = startExpr; _startUnit = startUnit; _prevStartUnit = startUnit;
            StartPreview = AnalysisPreviewHelper.ComputeFreqPreview(startExpr, startUnit, _model);

            string stopExpr = seed.StopExpr;
            string stopUnit = string.IsNullOrEmpty(seed.StopUnit) ? "Hz" : seed.StopUnit;
            if (stopUnit == "Hz")
            {
                var (ec, eu) = FreqUnitHelper.Split(stopExpr);
                stopExpr = ec; stopUnit = eu;
            }
            _stopCoeff = stopExpr; _stopUnit = stopUnit; _prevStopUnit = stopUnit;
            StopPreview = AnalysisPreviewHelper.ComputeFreqPreview(stopExpr, stopUnit, _model);

            if (seed.Mode == FreqSpecMode.StepSize)
            {
                string stepExpr = seed.StepExpr;
                string stepUnit = string.IsNullOrEmpty(seed.StepUnit) ? "Hz" : seed.StepUnit;
                if (stepUnit == "Hz")
                {
                    var (stc, stu) = FreqUnitHelper.Split(stepExpr);
                    stepExpr = stc; stepUnit = stu;
                }
                _stepCoeff = stepExpr; _stepUnit = stepUnit; _prevStepUnit = stepUnit;
                StepPreview = AnalysisPreviewHelper.ComputeFreqPreview(stepExpr, stepUnit, _model);
            }
            else
            {
                _numPointsExpr = (seed.NumPoints ?? 101).ToString(CultureInfo.InvariantCulture);
            }
        }
        // Default case: field initializers set 1 GHz start, 10 GHz stop, 101 pts.
        // _prevXUnit fields are already set to match by field initializers.
    }

    // ── Preview side-effects ───────────────────────────────────────────────────

    partial void OnStartCoeffChanged(string value) => StartPreview = AnalysisPreviewHelper.ComputeFreqPreview(value, StartUnit, _model);
    partial void OnStopCoeffChanged(string value)  => StopPreview  = AnalysisPreviewHelper.ComputeFreqPreview(value, StopUnit,  _model);
    partial void OnStepCoeffChanged(string value)  => StepPreview  = AnalysisPreviewHelper.ComputeFreqPreview(value, StepUnit,  _model);
    partial void OnNumPointsExprChanged(string value) => NumPointsPreview = Prev(value);

    // Unit changes: rescale coefficient to keep the same Hz value, then refresh preview.
    partial void OnStartUnitChanged(string value)
    {
        StartCoeff     = FreqUnitHelper.Rescale(StartCoeff, _prevStartUnit, value);
        _prevStartUnit = value;
        StartPreview   = AnalysisPreviewHelper.ComputeFreqPreview(StartCoeff, value, _model);
    }

    partial void OnStopUnitChanged(string value)
    {
        StopCoeff     = FreqUnitHelper.Rescale(StopCoeff, _prevStopUnit, value);
        _prevStopUnit = value;
        StopPreview   = AnalysisPreviewHelper.ComputeFreqPreview(StopCoeff, value, _model);
    }

    partial void OnStepUnitChanged(string value)
    {
        StepCoeff     = FreqUnitHelper.Rescale(StepCoeff, _prevStepUnit, value);
        _prevStepUnit = value;
        StepPreview   = AnalysisPreviewHelper.ComputeFreqPreview(StepCoeff, value, _model);
    }

    private string Prev(string hzExpr) => AnalysisPreviewHelper.ComputePreview(hzExpr, _model);

    // ── Mode toggle commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void SetModeStep()
    {
        Mode = FreqSpecMode.StepSize;
        if (StepCoeff.Length == 0) StepCoeff = "100"; // StepUnit defaults to MHz
    }

    [RelayCommand]
    private void SetModePoints() => Mode = FreqSpecMode.PointCount;

    // ── Kind toggle commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void SetKindLinear() => Kind = SweepKind.Linear;

    [RelayCommand]
    private void SetKindLog() => Kind = SweepKind.Log;

    // ── Remove ────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRemoveSelf))]
    private void Remove() => _removeCallback?.Invoke(this);

    internal void SetRemoveCallback(Action<FrequencySpecViewModel> cb) => _removeCallback = cb;

    // ── Build ─────────────────────────────────────────────────────────────────

    public FrequencySpec Build()
    {
        // Store raw coeff + unit — do NOT bake via ToHzExpr.
        if (Mode == FreqSpecMode.PointCount)
        {
            int pts = int.TryParse(NumPointsExpr.Trim(), out var n) ? Math.Max(1, n) : 101;
            return new FrequencySpec(StartCoeff, StopCoeff, pts, Kind, StartUnit, StopUnit);
        }

        string step     = StepCoeff.Trim().Length > 0 ? StepCoeff : "100";
        string stepUnit = StepCoeff.Trim().Length > 0 ? StepUnit  : "MHz";
        return new FrequencySpec(StartCoeff, StopCoeff, step, Kind, StartUnit, StopUnit, stepUnit);
    }
}
