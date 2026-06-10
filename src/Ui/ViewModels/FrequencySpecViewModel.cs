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

            var (sc, su) = FreqUnitHelper.Split(seed.StartExpr);
            var (ec, eu) = FreqUnitHelper.Split(seed.StopExpr);
            _startCoeff = sc; _startUnit = su; _prevStartUnit = su;
            _stopCoeff  = ec; _stopUnit  = eu; _prevStopUnit  = eu;
            StartPreview = Prev(FreqUnitHelper.ToHzExpr(sc, su));
            StopPreview  = Prev(FreqUnitHelper.ToHzExpr(ec, eu));

            if (seed.Mode == FreqSpecMode.StepSize)
            {
                var (stc, stu) = FreqUnitHelper.Split(seed.StepExpr);
                _stepCoeff = stc; _stepUnit = stu; _prevStepUnit = stu;
                StepPreview = Prev(FreqUnitHelper.ToHzExpr(stc, stu));
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

    partial void OnStartCoeffChanged(string value) => StartPreview = Prev(FreqUnitHelper.ToHzExpr(value, StartUnit));
    partial void OnStopCoeffChanged(string value)  => StopPreview  = Prev(FreqUnitHelper.ToHzExpr(value, StopUnit));
    partial void OnStepCoeffChanged(string value)  => StepPreview  = Prev(FreqUnitHelper.ToHzExpr(value, StepUnit));
    partial void OnNumPointsExprChanged(string value) => NumPointsPreview = Prev(value);

    // Unit changes: rescale coefficient to keep the same Hz value, then refresh preview.
    partial void OnStartUnitChanged(string value)
    {
        StartCoeff     = FreqUnitHelper.Rescale(StartCoeff, _prevStartUnit, value);
        _prevStartUnit = value;
        StartPreview   = Prev(FreqUnitHelper.ToHzExpr(StartCoeff, value));
    }

    partial void OnStopUnitChanged(string value)
    {
        StopCoeff     = FreqUnitHelper.Rescale(StopCoeff, _prevStopUnit, value);
        _prevStopUnit = value;
        StopPreview   = Prev(FreqUnitHelper.ToHzExpr(StopCoeff, value));
    }

    partial void OnStepUnitChanged(string value)
    {
        StepCoeff     = FreqUnitHelper.Rescale(StepCoeff, _prevStepUnit, value);
        _prevStepUnit = value;
        StepPreview   = Prev(FreqUnitHelper.ToHzExpr(StepCoeff, value));
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
        string start = FreqUnitHelper.ToHzExpr(StartCoeff, StartUnit);
        string stop  = FreqUnitHelper.ToHzExpr(StopCoeff,  StopUnit);

        if (Mode == FreqSpecMode.PointCount)
        {
            int pts = int.TryParse(NumPointsExpr.Trim(), out var n) ? Math.Max(1, n) : 101;
            return new FrequencySpec(start, stop, pts, Kind);
        }

        string step = StepCoeff.Trim().Length > 0
            ? FreqUnitHelper.ToHzExpr(StepCoeff, StepUnit)
            : "100e6";
        return new FrequencySpec(start, stop, step, Kind);
    }
}
