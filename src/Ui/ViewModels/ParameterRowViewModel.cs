using System;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for one row in the ParameterEditorView — wraps a single EditableParameter.
/// Expression/Unit/ShowOnSchematic are staged; the row commits through the command stack.
/// Also computes the inline "≈ value" preview (parameter-editor.md, "Value preview").
/// </summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly EditableParameter _param;
    private readonly SchematicViewModel _schematicVm;
    private readonly SymbolKind _ownerSymbol;
    private bool _isRefreshing;

    public string Name => _param.Name;
    public string[] UnitOptions { get; }

    [ObservableProperty] private string _stagedExpression = "";
    [ObservableProperty] private string _stagedUnit = "";
    [ObservableProperty] private bool   _showOnSchematic;

    // ── Value preview ("≈ <evaluated>") ───────────────────────────────────────
    // Subtle grey, non-interactive (the view makes it selectable-but-read-only). Empty string ⇒
    // the view hides it. Recomputed when the staged expression changes and on RefreshFromModel.

    [ObservableProperty] private string _valuePreview = "";

    public bool HasValuePreview => ValuePreview.Length > 0;
    partial void OnValuePreviewChanged(string? oldValue, string newValue)
        => OnPropertyChanged(nameof(HasValuePreview));

    partial void OnShowOnSchematicChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing) return;
        _schematicVm.Execute(new SetParameterVisibilityCommand(_schematicVm.EditModel, _param, newValue));
    }

    partial void OnStagedExpressionChanged(string? oldValue, string newValue)
    {
        // Live preview as the user types the expression (cheap; no model mutation).
        // Not gated by _isRefreshing — the preview should also update on refresh/undo.
        RecomputePreview();
    }

    public ParameterRowViewModel(EditableParameter param, SchematicViewModel schematicVm, SymbolKind ownerSymbol)
    {
        _param       = param;
        _schematicVm = schematicVm;
        _ownerSymbol = ownerSymbol;
        UnitOptions  = ComponentTypeRegistry.UnitOptions(param.Dimension);

        _isRefreshing = true;
        _stagedExpression = param.Expression;
        _stagedUnit       = param.Unit;
        _showOnSchematic  = param.ShowOnSchematic;
        _isRefreshing = false;

        RecomputePreview();
    }

    /// <summary>Commit the staged expression to the model (no-op if unchanged).</summary>
    public void CommitExpression()
    {
        string expr = StagedExpression.Trim();
        if (expr.Length == 0 || expr == _param.Expression) return;
        _schematicVm.Execute(new EditParameterCommand(_schematicVm.EditModel, _param, expr, _param.Unit));
    }

    /// <summary>Commit a unit selection to the model (no-op if unchanged).</summary>
    public void CommitUnit(string unit)
    {
        if (unit == _param.Unit) return;
        _schematicVm.Execute(new EditParameterCommand(_schematicVm.EditModel, _param, _param.Expression, unit));
    }

    /// <summary>Refresh staged values from the model (called after external edits or undo).</summary>
    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedExpression = _param.Expression;   // fires OnStagedExpressionChanged → RecomputePreview
        StagedUnit       = _param.Unit;
        ShowOnSchematic  = _param.ShowOnSchematic;
        _isRefreshing = false;
        RecomputePreview();   // also recompute in case the expression text was unchanged but a
                              // referenced value elsewhere in the schematic changed
    }

    // ── Preview computation ────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the "≈ value" preview from the current staged expression, evaluated against the
    /// schematic's current state. Shows a preview ONLY when (parameter-editor.md "Value preview"):
    ///   • the owner is not an SDD/FetSdd device (their equations aren't scalar-evaluable here);
    ///   • the expression is more than a bare number/blank (no "≈ 2.5" noise on a literal);
    ///   • evaluation succeeds and yields a single Real (or Complex) value.
    /// Any parse/resolve/cycle/type error, or a non-scalar result (e.g. a Cube/sweep), yields an
    /// empty preview (no error surfaced). All failure is swallowed — a preview never throws.
    /// </summary>
    private void RecomputePreview()
    {
        ValuePreview = ComputePreview(StagedExpression);
    }

    private string ComputePreview(string expression)
    {
        // Gate 1: SDD/FetSdd → never evaluate (device-equation params, not scalar).
        if (_ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd) return "";

        // Gate 2: blank or bare-number → no preview (a literal needs no "≈").
        string expr = expression.Trim();
        if (expr.Length == 0) return "";
        if (IsBareNumber(expr)) return "";

        try
        {
            var scope = DesignScope.Build(_schematicVm.EditModel, selfName: _param.Name);
            // No unit passed: preview shows the RAW evaluated value (display-unit scaling deferred;
            // and the engine's Units table is ASCII-keyed, mismatching the glyph ComboBox strings).
            var value = new Evaluator().Eval(expr, scope);

            return value.Kind switch
            {
                // Gate 3: only scalar Real / Complex preview. Cube/Bool/String/All ⇒ no preview.
                ValueKind.Real    => "≈ " + FormatReal(value.AsReal()),
                ValueKind.Complex => "≈ " + FormatComplex(value.AsComplex()),
                _                 => "",
            };
        }
        catch
        {
            // Unresolved name, parse error, cycle, type error, domain error, division by zero, …
            // → simply no preview. The preview is advisory and must never raise to the user.
            return "";
        }
    }

    /// <summary>True if the trimmed text is just a numeric literal (so a preview would be noise).</summary>
    private static bool IsBareNumber(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                           CultureInfo.InvariantCulture, out _);

    /// <summary>Formats a real preview value compactly (engineering-ish, ~4 significant digits).</summary>
    private static string FormatReal(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(CultureInfo.InvariantCulture);
        // "G4" gives ~4 significant digits and switches to exponent form for very large/small mags.
        return v.ToString("G4", CultureInfo.InvariantCulture);
    }

    private static string FormatComplex(Complex c)
    {
        string re = FormatReal(c.Real);
        string im = FormatReal(Math.Abs(c.Imaginary));
        string sign = c.Imaginary < 0 ? "-" : "+";
        return $"{re} {sign} {im}j";
    }
}
