// ================================================================
//  GoalEditorViewModel.cs  —  the goal editor dialog (brief-tuneopt-10 R-to10-4)
//
//  Left: the TO-9 template catalog, grouped. Right: the goal's fields.
//  A template FILLS the fields and is then forgotten — nothing about a
//  goal made from a template differs from one typed by hand. The
//  analysis follows the expression until the user picks one; the type
//  is five toggles, and the limit boxes shown are the ones that type
//  reads. The expression is checked as it is typed, with the engine's
//  own parse message.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Optimization;

/// <summary>The expression's values across the goal's range, and the limit drawn over them.</summary>
/// <param name="X">The range axis, in base SI.</param>
/// <param name="Y">The expression's value at each point.</param>
/// <param name="LimitLo">The limit (or the band's lower edge) at the first point.</param>
/// <param name="LimitHi">The limit at the last point — the same as <paramref name="LimitLo"/> unless sloped.</param>
/// <param name="Upper">The band's upper edge for <c>in</c>/<c>out</c>; null otherwise.</param>
/// <param name="Type">The goal's type — which side of the limit lines the preview hatches as failing.</param>
public sealed record GoalPreview(double[] X, double[] Y, double? LimitLo, double? LimitHi, double? Upper,
                                 GoalType Type = GoalType.Le);

/// <summary>One section of the template list.</summary>
public sealed record GoalTemplateSection(string Title, IReadOnlyList<GoalTemplate> Templates);

/// <summary>One field a template asks for, with its current choice.</summary>
public sealed partial class GoalTemplateFieldViewModel(GoalTemplateField field, Action changed) : ObservableObject
{
    public GoalTemplateField Field { get; } = field;
    public string Label => Field.Label;
    public IReadOnlyList<string> Choices => Field.Choices;

    [ObservableProperty] private string _value = field.Default;

    partial void OnValueChanged(string value) => changed();
}

public sealed partial class GoalEditorViewModel : ObservableObject
{
    public const string NoAnalysis = "(none — variables only)";

    private readonly IReadOnlyList<Analysis> _analyses;
    private readonly IReadOnlyList<Measurement> _measurements;
    private readonly Func<OptimizationGoal, CancellationToken, Task<GoalPreview?>>? _previewSource;
    private bool _analysisChosen;
    private bool _filling;
    private CancellationTokenSource? _previewCts;

    /// <param name="templates">The catalog for the bench (<see cref="GoalTemplates.For(PreparedCircuit)"/>).</param>
    /// <param name="analyses">The bench's declared analyses.</param>
    /// <param name="measurements">Its <c>measure</c> rows — an expression naming one reads that row's analysis.</param>
    /// <param name="goal">The goal to edit; null for a new one.</param>
    /// <param name="preview">Evaluates a goal over the current results; null when there are none.</param>
    public GoalEditorViewModel(IReadOnlyList<GoalTemplate> templates, IReadOnlyList<Analysis> analyses,
                               IReadOnlyList<Measurement> measurements, OptimizationGoal? goal,
                               Func<OptimizationGoal, CancellationToken, Task<GoalPreview?>>? preview = null)
    {
        _analyses     = analyses;
        _measurements = measurements;
        _previewSource = preview;
        IsNew         = goal is null;

        Sections = [.. templates.GroupBy(t => t.Group).OrderBy(g => g.Key)
                         .Select(g => new GoalTemplateSection(SectionTitle(g.Key), [.. g]))];
        AnalysisChoices = [NoAnalysis, .. analyses.Select(a => a.Name)];

        Load(goal ?? new OptimizationGoal { Name = "G1", Type = GoalType.Le });
        _analysisChosen = goal?.Analysis is not null;
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "Add Goal" : "Edit Goal";

    public IReadOnlyList<GoalTemplateSection> Sections { get; }

    // ---- Templates --------------------------------------------------------

    [ObservableProperty] private GoalTemplate? _selectedTemplate;

    public ObservableCollection<GoalTemplateFieldViewModel> TemplateFields { get; } = [];

    public bool HasTemplateFields => TemplateFields.Count > 0;

    partial void OnSelectedTemplateChanged(GoalTemplate? value)
    {
        TemplateFields.Clear();
        if (value is not null)
            foreach (var f in value.Fields) TemplateFields.Add(new GoalTemplateFieldViewModel(f, ApplyTemplate));
        OnPropertyChanged(nameof(HasTemplateFields));
        ApplyTemplate();
    }

    /// <summary>Fills the fields from the selected template and its field choices. The name, the
    /// expression, the analysis, the range and the type come from it; a limit the template does not
    /// suggest is left as the user typed it.</summary>
    private void ApplyTemplate()
    {
        if (SelectedTemplate is not { } t) return;
        var values = TemplateFields.ToDictionary(f => f.Field.Key, f => f.Value, StringComparer.Ordinal);
        var made   = t.Make(values);
        if (made.Limit.Length == 0) made.Limit = Limit;
        made.Weight = double.TryParse(Weight, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 1;
        Load(made);
        _analysisChosen = made.Analysis is not null;
    }

    // ---- Fields -----------------------------------------------------------

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _expression = "";

    /// <summary>The engine's own message for an expression that does not parse; null when it does.</summary>
    [ObservableProperty] private string? _expressionError;

    public bool HasExpressionError => ExpressionError is not null;

    partial void OnExpressionErrorChanged(string? value) => OnPropertyChanged(nameof(HasExpressionError));

    public IReadOnlyList<string> AnalysisChoices { get; }

    [ObservableProperty] private string _analysis = NoAnalysis;

    /// <summary>The swept axes of the chosen analysis — <c>freq</c> first when it has one.</summary>
    public ObservableCollection<string> AxisChoices { get; } = [];

    [ObservableProperty] private string _axis = "freq";
    [ObservableProperty] private bool _wholeRange = true;
    [ObservableProperty] private string _rangeLo = "";
    [ObservableProperty] private string _rangeHi = "";

    [ObservableProperty] private GoalType _type;
    [ObservableProperty] private string _limit = "";
    [ObservableProperty] private string _upperLimit = "";
    [ObservableProperty] private bool _sloped;
    [ObservableProperty] private string _limitAtHi = "";
    [ObservableProperty] private string _weight = "1";
    [ObservableProperty] private string _scale = "";
    [ObservableProperty] private bool _enabled = true;

    // The five toggles (≤ ≥ = in out) are one choice.
    public bool IsLe  { get => Type == GoalType.Le;  set { if (value) Type = GoalType.Le; } }
    public bool IsGe  { get => Type == GoalType.Ge;  set { if (value) Type = GoalType.Ge; } }
    public bool IsEq  { get => Type == GoalType.Eq;  set { if (value) Type = GoalType.Eq; } }
    public bool IsIn  { get => Type == GoalType.In;  set { if (value) Type = GoalType.In; } }
    public bool IsOut { get => Type == GoalType.Out; set { if (value) Type = GoalType.Out; } }

    /// <summary><c>in</c>/<c>out</c>: a band, two limit boxes.</summary>
    public bool IsBand => Type is GoalType.In or GoalType.Out;

    /// <summary>Only a one-sided or equality limit can slope.</summary>
    public bool CanSlope => !IsBand;

    public bool ShowsLimitAtHi => CanSlope && Sloped;

    public bool HasRangeAxis => AxisChoices.Count > 0;

    partial void OnTypeChanged(GoalType value)
    {
        foreach (var p in new[] { nameof(IsLe), nameof(IsGe), nameof(IsEq), nameof(IsIn), nameof(IsOut),
                                  nameof(IsBand), nameof(CanSlope), nameof(ShowsLimitAtHi) })
            OnPropertyChanged(p);
        RefreshPreview();
    }

    partial void OnSlopedChanged(bool value) { OnPropertyChanged(nameof(ShowsLimitAtHi)); RefreshPreview(); }

    partial void OnExpressionChanged(string value)
    {
        ExpressionError = GoalTemplates.Validate(value);
        // The analysis follows the expression until the user chooses one.
        if (!_filling && !_analysisChosen && ExpressionError is null
            && GoalTemplates.AnalysisReferencedBy(value, _analyses, _measurements) is { } a)
        {
            _filling = true;
            Analysis = a;
            _filling = false;
        }
        RefreshPreview();
    }

    partial void OnAnalysisChanged(string value)
    {
        if (!_filling) _analysisChosen = true;
        AxisChoices.Clear();
        foreach (var ax in AxesOf(value)) AxisChoices.Add(ax);
        if (!AxisChoices.Contains(Axis)) Axis = AxisChoices.FirstOrDefault() ?? "freq";
        OnPropertyChanged(nameof(HasRangeAxis));
        RefreshPreview();
    }

    partial void OnWholeRangeChanged(bool value) => RefreshPreview();
    partial void OnLimitChanged(string value) => RefreshPreview();
    partial void OnUpperLimitChanged(string value) => RefreshPreview();
    partial void OnLimitAtHiChanged(string value) => RefreshPreview();

    private void Load(OptimizationGoal g)
    {
        _filling = true;
        Name       = g.Name;
        Expression = g.Expression;
        Analysis   = g.Analysis is { } a && AnalysisChoices.Contains(a) ? a : NoAnalysis;
        if (g.Range is { } r)
        {
            if (!AxisChoices.Contains(r.Axis)) AxisChoices.Add(r.Axis);
            Axis       = r.Axis;
            RangeLo    = r.Lo;
            RangeHi    = r.Hi;
            WholeRange = false;
        }
        else
        {
            WholeRange = true;
            RangeLo = RangeHi = "";
        }
        Type       = g.Type;
        Limit      = g.Limit;
        UpperLimit = g.UpperLimit ?? "";
        Sloped     = g.LimitAtHi is not null;
        LimitAtHi  = g.LimitAtHi ?? "";
        Weight     = g.Weight.ToString("G6", CultureInfo.InvariantCulture);
        Scale      = g.Scale ?? "";
        Enabled    = g.Enabled;
        _use       = g.Use;
        _extraFrom = g.Extra is null ? null : g.Clone();
        _filling = false;
        RefreshPreview();
    }

    // The goal whose unknown keys (written by a later version) the result carries over.
    private OptimizationGoal? _extraFrom;

    // What the goal serves (use=); the Yield panel sets it, this editor carries it through.
    private GoalUse _use;

    /// <summary>The axes a goal over <paramref name="analysis"/> can range over: a parametric sweep's
    /// variable, then the inner analysis's own (<c>freq</c> for S-parameters and harmonic balance).</summary>
    private IEnumerable<string> AxesOf(string analysis)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var a = _analyses.FirstOrDefault(x => x.Name == analysis); a is not null && seen.Add(a.Name);)
        {
            switch (a)
            {
                case ParametricSweepAnalysis ps:
                    yield return ps.SweepVarName;
                    a = _analyses.FirstOrDefault(x => x.Name == ps.InnerAnalysisName);
                    continue;
                case SParameterAnalysis or HarmonicBalanceAnalysis or LoadpullAnalysis or LoadpullPursuitAnalysis:
                    yield return "freq";
                    break;
            }
            break;
        }
    }

    // ---- The result ---------------------------------------------------------

    /// <summary>Why the goal cannot be kept as it stands; null when it can.</summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "A goal needs a name.";
            if (ExpressionError is { } e) return e;
            if (!double.TryParse(Weight, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) || !(w > 0))
                return "The weight must be a positive number.";
            if (!WholeRange && (RangeLo.Trim().Length == 0 || RangeHi.Trim().Length == 0))
                return "Give both ends of the range, or tick Whole range.";
            if (IsBand && UpperLimit.Trim().Length == 0) return "A band needs both edges.";
            if (ShowsLimitAtHi && LimitAtHi.Trim().Length == 0) return "A sloped limit needs its value at the high end.";
            return null;
        }
    }

    /// <summary>The goal the fields describe. A limit left empty is kept empty (the goal then needs
    /// one before it can run — <c>check</c> says so).</summary>
    public OptimizationGoal Build()
    {
        double.TryParse(Weight, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight);
        return new OptimizationGoal
        {
            Name       = Name.Trim(),
            Expression = Expression.Trim(),
            Analysis   = Analysis == NoAnalysis ? null : Analysis,
            Range      = WholeRange || !HasRangeAxis
                ? null : new GoalRange { Axis = Axis, Lo = RangeLo.Trim(), Hi = RangeHi.Trim() },
            Type       = Type,
            Limit      = Limit.Trim(),
            UpperLimit = IsBand ? UpperLimit.Trim() : null,
            LimitAtHi  = ShowsLimitAtHi ? LimitAtHi.Trim() : null,
            Weight     = weight > 0 ? weight : 1,
            Scale      = Scale.Trim().Length == 0 ? null : Scale.Trim(),
            Enabled    = Enabled,
            Use        = _use,
            Extra      = _extraFrom?.Clone().Extra,
        };
    }

    /// <summary>OK: hands the goal to the panel, which writes it as one undo step. False (and
    /// <see cref="Problem"/> says why) when the fields do not make a goal.</summary>
    public bool TryCommit()
    {
        if (Problem is { } why) { CommitError = why; return false; }
        CommitError = null;
        Committed?.Invoke(Build());
        return true;
    }

    [ObservableProperty] private string? _commitError;

    /// <summary>Raised by <see cref="TryCommit"/> with the goal.</summary>
    public event Action<OptimizationGoal>? Committed;

    // ---- Preview ------------------------------------------------------------

    [ObservableProperty] private GoalPreview? _preview;

    public bool HasPreview => Preview is { X.Length: > 0 };

    partial void OnPreviewChanged(GoalPreview? value) => OnPropertyChanged(nameof(HasPreview));

    /// <summary>Re-evaluates the preview — the newest request wins; nothing when there are no results.</summary>
    private void RefreshPreview()
    {
        if (_filling || _previewSource is null) return;
        _previewCts?.Cancel();
        if (ExpressionError is not null || Expression.Trim().Length == 0) { Preview = null; return; }
        var cts  = _previewCts = new CancellationTokenSource();
        var goal = Build();
        _ = Run();

        async Task Run()
        {
            try
            {
                var p = await _previewSource(goal, cts.Token);
                if (!cts.IsCancellationRequested) Preview = p;
            }
            catch (OperationCanceledException) { }
            catch { if (!cts.IsCancellationRequested) Preview = null; }
        }
    }

    [RelayCommand] private void SetLe()  => Type = GoalType.Le;
    [RelayCommand] private void SetGe()  => Type = GoalType.Ge;
    [RelayCommand] private void SetEq()  => Type = GoalType.Eq;
    [RelayCommand] private void SetIn()  => Type = GoalType.In;
    [RelayCommand] private void SetOut() => Type = GoalType.Out;

    private static string SectionTitle(GoalTemplateGroup g) => g switch
    {
        GoalTemplateGroup.SParameters  => "S-parameters",
        GoalTemplateGroup.WsProbe      => "WSProbe",
        GoalTemplateGroup.Fields       => "Fields (SAR, B1)",
        GoalTemplateGroup.Measurements => "Existing measurements",
        _                              => "Custom",
    };
}
