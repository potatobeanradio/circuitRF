using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for one row in the ParameterEditorView — wraps a single EditableParameter.
/// Expression/Unit/ShowOnSchematic are staged; the row commits through the command stack.
/// Also computes the inline value preview with an honest "=" / "≈" prefix (expressions.md §9.1).
/// When NameEditable is true (extensible component types), StagedName can be committed
/// via CommitName().
/// </summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly EditableParameter  _param;
    private readonly SchematicViewModel _schematicVm;
    private readonly SymbolKind         _ownerSymbol;
    private readonly EditableComponent? _ownerComp;
    private bool _isRefreshing;

    // Mirrors ComponentModelFactory's private RxCurrentEq/RxCurrentEq1/RxChargeEq1/RxWeightFn.
    // Duplicated here (with this comment) because those fields are private to Core.
    private static readonly Regex RxSddH  = new(@"^H\[(\d+)\]$",      RegexOptions.Compiled);
    private static readonly Regex RxSddI1 = new(@"^I\[(\d+)\]$",      RegexOptions.Compiled);
    private static readonly Regex RxSddI2 = new(@"^I\[(\d+),(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxSddQ  = new(@"^Q\[(\d+)\]$",      RegexOptions.Compiled);

    public string Name         => _param.Name;
    public bool   NameEditable { get; }
    public bool   NameReadOnly => !NameEditable;
    public string NameWatermark { get; }
    public string[] UnitOptions { get; }

    [ObservableProperty] private string _stagedName       = "";
    [ObservableProperty] private string _stagedExpression = "";
    [ObservableProperty] private string _stagedUnit       = "";
    [ObservableProperty] private bool   _showOnSchematic;
    [ObservableProperty] private string _nameError = "";

    public bool HasNameError => NameError.Length > 0;
    partial void OnNameErrorChanged(string? oldValue, string newValue)
        => OnPropertyChanged(nameof(HasNameError));

    // ── Value preview ("= <evaluated>" / "≈ <rounded>") ───────────────────────
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

    public ParameterRowViewModel(
        EditableParameter  param,
        SchematicViewModel schematicVm,
        SymbolKind         ownerSymbol,
        EditableComponent? ownerComp = null)
    {
        _param       = param;
        _schematicVm = schematicVm;
        _ownerSymbol = ownerSymbol;
        _ownerComp   = ownerComp;
        UnitOptions  = ComponentTypeRegistry.UnitOptions(param.Dimension);
        NameEditable = ComponentTypeRegistry.UserParamTemplate(ownerSymbol) is not null;
        NameWatermark = (ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd) ? "I[p,w] · Q[p] · H[w]" : "";

        _isRefreshing = true;
        _stagedName       = param.Name;
        _stagedExpression = param.Expression;
        _stagedUnit       = param.Unit;
        _showOnSchematic  = param.ShowOnSchematic;
        _isRefreshing = false;

        RecomputePreview();
    }

    /// <summary>Commit the staged name to the model (no-op if unchanged or invalid).</summary>
    public void CommitName()
    {
        if (_isRefreshing || !NameEditable) return;
        string name = StagedName.Trim();

        if (name.Length == 0)
        {
            NameError = "Name cannot be empty";
            return;
        }
        if (name == _param.Name) { NameError = ""; return; }

        // Duplicate check against sibling params
        if (_ownerComp is not null &&
            _ownerComp.Parameters.Any(p => !ReferenceEquals(p, _param) && p.Name == name))
        {
            NameError = $"\"{name}\" already exists";
            return;
        }

        // SDD-specific grammar validation — only for SDD/FetSdd owners.
        if (_ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd)
        {
            if (!TryValidateSddName(name, out string sddError))
            {
                NameError = sddError;
                return;
            }
        }

        NameError = "";
        _schematicVm.Execute(new SetParameterNameCommand(_schematicVm.EditModel, _param, name));
    }

    /// <summary>
    /// Validates an SDD equation parameter name against the accepted grammar.
    /// Returns true (error = "") when the name is valid.
    /// Returns false with a user-facing error message when it is not.
    /// </summary>
    internal static bool TryValidateSddName(string name, out string error)
    {
        // H[w] — check first because it has distinct error messages.
        var mH = RxSddH.Match(name);
        if (mH.Success)
        {
            int w = int.Parse(mH.Groups[1].Value, CultureInfo.InvariantCulture);
            if (w < 2)
            {
                error = "H[0] and H[1] are built-in (1 and jω) — not user-definable";
                return false;
            }
            error = "";
            return true;
        }
        // H[…] with non-integer or empty index.
        if (name.StartsWith("H[", StringComparison.Ordinal))
        {
            error = "H[w] requires an integer weight ≥ 2";
            return false;
        }

        // I[p,w] — two-index form.
        var mI2 = RxSddI2.Match(name);
        if (mI2.Success)
        {
            int p = int.Parse(mI2.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p >= 1) { error = ""; return true; }
            error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
            return false;
        }

        // I[p] — single-index current.
        var mI1 = RxSddI1.Match(name);
        if (mI1.Success)
        {
            int p = int.Parse(mI1.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p >= 1) { error = ""; return true; }
            error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
            return false;
        }

        // Q[p] — single-index charge.
        var mQ = RxSddQ.Match(name);
        if (mQ.Success)
        {
            int p = int.Parse(mQ.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p >= 1) { error = ""; return true; }
            error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
            return false;
        }

        error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
        return false;
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
        StagedName       = _param.Name;
        StagedExpression = _param.Expression;   // fires OnStagedExpressionChanged → RecomputePreview
        StagedUnit       = _param.Unit;
        ShowOnSchematic  = _param.ShowOnSchematic;
        NameError        = "";
        _isRefreshing = false;
        RecomputePreview();   // also recompute in case the expression text was unchanged but a
                              // referenced value elsewhere in the schematic changed
    }

    // ── Preview computation ────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the value preview from the current staged expression, evaluated against the
    /// schematic's current state, with the honest "=" / "≈" prefix of expressions.md §9.1. Shows a
    /// preview ONLY when:
    ///   • the owner is not an SDD/FetSdd device (their equations aren't scalar-evaluable here);
    ///   • the expression is more than a bare number/blank (no "= 2.5" noise on a literal);
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

            // Gate 3: only scalar Real / Complex preview (Cube/Bool/String/All ⇒ no preview).
            // Honest "=" / "≈" prefix per expressions.md §9.1 — shared with the analysis-dialog hint:
            // "=" when the shown digits reconstruct the value, "≈" only when genuinely rounded.
            return AnalysisPreviewHelper.FormatValueHonest(value);
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
}
