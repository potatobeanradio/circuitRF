using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;       // ParametricSweepAnalysis, SweepKind, SweepAxisMode, SweepExpander, SweepSpec
using CircuitRF.Core.Expressions;  // Units
using CircuitRF.Ui.Schematic;      // SchematicEditModel, SymbolKind
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// One parametric-sweep axis row in the analysis editor.
///
/// Holds: variable name (combo + soft warning for unknown names), mode (StepSize /
/// PointCount / List), per-mode fields, Lin/Log kind, optional display unit, and
/// live point-count preview + inline error.
/// </summary>
public sealed partial class SweepAxisRowViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;

    // ── Enabled ───────────────────────────────────────────────────────────────

    /// <summary>When false, this axis collapses out of the result (its Start/Stop/Step is kept).</summary>
    [ObservableProperty] private bool _enabled = true;

    // ── Variable name ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VarNameError), nameof(HasVarNameError), nameof(Preview))]
    [NotifyPropertyChangedFor(nameof(EffectiveUnit))]
    private string _varName = "";

    // Changing the swept variable invalidates any unit inherited from the PREVIOUS variable.
    // Example bug: a Loadpull/Pursuit freq sweep restored with Unit="GHz" (the LPP hint is "RFfreq"),
    // then re-pointed at a drain-voltage var "VDD", kept scaling VDD by 1e9. Clearing lets EffectiveUnit
    // re-derive from the new variable's own declared unit — a unit is applied only when the swept
    // variable itself declares one. FromPsa sets VarName BEFORE Unit, so restore is unaffected.
    partial void OnVarNameChanged(string value) => Unit = "";

    /// <summary>Placeholder/hint for the Variable box. The editor sets this per analysis type:
    /// "e.g. RFfreq" for Loadpull/LP-Pursuit (freq sweeps), "e.g. Pavl" otherwise.</summary>
    [ObservableProperty]
    private string _variablePlaceholder = "e.g. Pavl";

    // ── Mode ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepSize), nameof(IsPointCount), nameof(IsList))]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private SweepAxisMode _mode = SweepAxisMode.StepSize;

    public bool IsStepSize   => Mode == SweepAxisMode.StepSize;
    public bool IsPointCount => Mode == SweepAxisMode.PointCount;
    public bool IsList       => Mode == SweepAxisMode.List;

    // ── StepSize / PointCount shared fields ───────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _startExpr = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _stopExpr = "1";

    /// <summary>Step expression (StepSize mode) or point-count expression (PointCount mode).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _stepOrCountExpr = "0.1";

    // ── List mode ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _listExpr = "";

    // ── Lin / Log ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinear), nameof(IsLog), nameof(Preview))]
    private SweepKind _sweepKind = SweepKind.Linear;

    public bool IsLinear => SweepKind == SweepKind.Linear;
    public bool IsLog    => SweepKind == SweepKind.Log;

    // ── Optional display unit (general; empty = inherit from swept VAR) ──────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _unit = "";

    /// <summary>
    /// The unit actually applied to Start/Stop/Step coefficients.
    /// = <see cref="Unit"/> when the user has set one; otherwise the swept VAR's declared unit.
    /// Exposed so AXAML can show the inherited unit as placeholder text.
    /// Note: var-unit-wins does NOT apply here — the chosen field/inherited unit always governs
    /// (unlike the freq preview, where a var's own unit overrides the field unit).
    /// </summary>
    public string EffectiveUnit =>
        !string.IsNullOrEmpty(Unit) ? Unit : GetVarUnit(_model, VarName.Trim());

    // ── Known variable names (populated from VAR components) ─────────────────

    public IReadOnlyList<string> KnownVarNames { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SweepAxisRowViewModel(SchematicEditModel model)
    {
        _model       = model;
        KnownVarNames = GetKnownVarNames(model);
    }

    // ── Computed: variable-name warning ──────────────────────────────────────

    public string? VarNameError =>
        VarName.Trim().Length > 0 && !KnownVarNames.Contains(VarName.Trim(), StringComparer.OrdinalIgnoreCase)
            ? $"'{VarName.Trim()}' is not defined in a VAR block — will fail at run time."
            : null;

    public bool HasVarNameError => VarNameError is not null;

    // ── Computed: live preview (point count / range) ──────────────────────────

    public string Preview
    {
        get
        {
            try
            {
                double[] pts = BuildValues() ?? [];
                if (pts.Length == 0) return "";
                if (pts.Length == 1)
                    return $"1 pt: {Fmt(pts[0])}";
                return $"{pts.Length} pts: {Fmt(pts[0])} … {Fmt(pts[^1])}";
            }
            catch
            {
                return "";
            }
        }
    }

    // ── Mode commands (segmented buttons) ─────────────────────────────────────

    [RelayCommand] private void SetStepSize()   => Mode = SweepAxisMode.StepSize;
    [RelayCommand] private void SetPointCount() => Mode = SweepAxisMode.PointCount;
    [RelayCommand] private void SetList()       => Mode = SweepAxisMode.List;
    [RelayCommand] private void SetLinear()     => SweepKind = SweepKind.Linear;
    [RelayCommand] private void SetLog()        => SweepKind = SweepKind.Log;

    // ── Build values → double[] ───────────────────────────────────────────────

    /// <summary>
    /// Expands the axis to a concrete value array.  Returns null when the inputs are
    /// invalid (non-parseable, empty list).  Caller should validate before committing.
    /// </summary>
    public double[]? BuildValues()
    {
        if (Mode == SweepAxisMode.List)
        {
            var pts = SweepExpander.ExpandList(ListExpr);
            return pts.Length > 0 ? pts : null;
        }

        if (!TryResolve(StartExpr, out double start) ||
            !TryResolve(StopExpr,  out double stop)  ||
            !TryResolve(StepOrCountExpr, out double stepOrCount))
            return null;

        double m = Units.Scale(EffectiveUnit) ?? 1.0;
        return SweepExpander.ExpandSweep(
            start * m,
            stop  * m,
            Mode == SweepAxisMode.StepSize ? stepOrCount * m : stepOrCount,
            Mode, SweepKind);
    }

    /// <summary>True when the row is complete enough to include in a build.</summary>
    public bool IsValid =>
        VarName.Trim().Length > 0 && BuildValues() is { Length: > 0 };

    /// <summary>
    /// Builds a <see cref="SweepSpec"/> for StepSize or PointCount mode.
    /// Returns null when in List mode or when any expression fails to resolve.
    /// </summary>
    public SweepSpec? BuildSpec()
    {
        if (Mode == SweepAxisMode.List) return null;

        if (!TryResolve(StartExpr, out double start) ||
            !TryResolve(StopExpr,  out double stop)  ||
            !TryResolve(StepOrCountExpr, out double stepOrCount))
            return null;

        // Store coefficients (unscaled) + EffectiveUnit; Part A of brief-sweep-range-units
        // applies the unit multiplier when ParametricSweepAnalysis materializes SweepValues.
        return new SweepSpec(start, stop, stepOrCount, Mode, SweepKind, EffectiveUnit);
    }

    // ── Serialization-restore factory ────────────────────────────────────────

    /// <summary>
    /// Restores a row from an existing <see cref="ParametricSweepAnalysis"/>.
    /// When the PSA carries a <see cref="SweepSpec"/>, restores Start/Stop/Step fields;
    /// otherwise falls back to List mode with the concrete values.
    /// </summary>
    public static SweepAxisRowViewModel FromPsa(ParametricSweepAnalysis psa, SchematicEditModel model)
    {
        var vm = new SweepAxisRowViewModel(model);
        vm.VarName = psa.SweepVarName;
        vm.Enabled = psa.Enabled;

        if (psa.Spec is { } spec)
        {
            vm.Mode            = spec.Mode;
            vm.SweepKind       = spec.Kind;
            vm.StartExpr       = spec.Start.ToString("G", CultureInfo.InvariantCulture);
            vm.StopExpr        = spec.Stop.ToString("G", CultureInfo.InvariantCulture);
            vm.StepOrCountExpr = spec.StepOrCount.ToString("G", CultureInfo.InvariantCulture);
            vm.Unit            = spec.Unit;
        }
        else
        {
            vm.Mode     = SweepAxisMode.List;
            vm.ListExpr = string.Join(", ",
                psa.SweepValues.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)));
        }
        return vm;
    }

    /// <summary>
    /// Builds a row from legacy HB sweep expression strings (migration path).
    /// Loads as StepSize mode with the original expressions so the user can edit them.
    /// </summary>
    public static SweepAxisRowViewModel FromLegacyHbSweep(
        string varName, string startExpr, string stopExpr, string stepExpr,
        SchematicEditModel model)
    {
        var vm = new SweepAxisRowViewModel(model);
        vm.VarName         = varName;
        vm.Mode            = SweepAxisMode.StepSize;
        vm.StartExpr       = startExpr;
        vm.StopExpr        = stopExpr;
        vm.StepOrCountExpr = stepExpr;
        return vm;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool TryResolve(string expr, out double value)
    {
        if (double.TryParse(expr.Trim(),
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value))
            return true;

        // Expression-valued coefficient (e.g. a VAR reference): resolve its RAW value against the
        // unit-stripped design scope. The row's own EffectiveUnit scaling is applied by BuildValues,
        // so this must NOT apply units (and must keep full precision, not the display-rounded text).
        return AnalysisPreviewHelper.TryResolveCoefficient(expr, _model, out value);
    }

    private static string Fmt(double v) =>
        v.ToString(Math.Abs(v) >= 1e6 || (Math.Abs(v) > 0 && Math.Abs(v) < 0.01)
            ? "G4" : "G6", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> GetKnownVarNames(SchematicEditModel model)
    {
        return model.Components
            .Where(c => c.Symbol == SymbolKind.Var)
            .SelectMany(c => c.Parameters)
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The swept VAR's declared unit, or "" when it declares none.
    ///
    /// <para>Reads the row's unit COLUMN, and falls back to a unit written inline in the expression
    /// ("RFfreq = 2 GHz") — the same lift <see cref="NetExtractor"/> applies when it builds the
    /// actual <c>Variable</c>. Both have to agree or the editor would show a blank inherited unit
    /// while the run inherited GHz.</para>
    /// </summary>
    private static string GetVarUnit(SchematicEditModel model, string varName)
    {
        if (string.IsNullOrEmpty(varName)) return "";
        return model.Components
            .Where(c => c.Symbol == SymbolKind.Var)
            .SelectMany(c => c.Parameters)
            .Where(p => p.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
            .Select(p => !string.IsNullOrEmpty(p.Unit)
                ? p.Unit
                : NetExtractor.LiftInlineUnit(p.Expression).Unit ?? "")
            .FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? "";
    }
}
