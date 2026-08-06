using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>One entry in a DRC rule row's Layer combo — a display label paired with the
/// underlying <see cref="LayerKey"/>, since a record struct's default ToString() is not fit for
/// display and the Layer combo must offer a closed set (never free text).</summary>
public sealed record LayerOptionItem(LayerKey Key, string Label);

/// <summary>
/// VM for one row in the .ctech editor's DRC-rule grid. Nothing executes these rules until L5b —
/// L0d only makes them editable and correct. Value is a physical dimension (parsed/formatted via
/// <see cref="LayoutUnits"/>, same convention as stackup thickness); Layer is picked from the
/// current layer table, never free text.
/// </summary>
public sealed partial class DrcRuleRowViewModel : ObservableObject
{
    private readonly TechEditorViewModel _owner;
    private bool _isRefreshing;

    internal DrcRule Rule { get; }

    [ObservableProperty] private string _stagedName = "";
    [ObservableProperty] private DrcRuleKind _stagedKind;
    [ObservableProperty] private LayerOptionItem? _stagedLayer;
    [ObservableProperty] private string _stagedValueText = "";
    [ObservableProperty] private string? _valueError;
    public bool HasValueError => ValueError is not null;
    partial void OnValueErrorChanged(string? value) => OnPropertyChanged(nameof(HasValueError));
    [ObservableProperty] private DrcSeverity _stagedSeverity;

    // ── v2: the expression operands and the kind-specific fields ────────────────
    // A rule measures a layer EXPRESSION, not necessarily a bare layer, and three kinds carry
    // values that are not lengths. Without these the editor could show an imported rule but not
    // edit it — and a field a user can see and cannot change reads as a bug.

    [ObservableProperty] private string _stagedRegionA = "";
    [ObservableProperty] private string? _regionAError;
    public bool HasRegionAError => RegionAError is not null;
    partial void OnRegionAErrorChanged(string? value) => OnPropertyChanged(nameof(HasRegionAError));

    [ObservableProperty] private string _stagedRegionB = "";
    [ObservableProperty] private string? _regionBError;
    public bool HasRegionBError => RegionBError is not null;
    partial void OnRegionBErrorChanged(string? value) => OnPropertyChanged(nameof(HasRegionBError));

    [ObservableProperty] private string _stagedWindowText = "";
    [ObservableProperty] private string _stagedMinRatioText = "";
    [ObservableProperty] private string _stagedMaxRatioText = "";
    [ObservableProperty] private DrcNetScope _stagedNetScope;

    /// <summary>Every kind, for the Kind combo — grown from two to eight in v2.</summary>
    public static IReadOnlyList<DrcRuleKind> AllKinds { get; } = Enum.GetValues<DrcRuleKind>();

    public static IReadOnlyList<DrcNetScope> AllNetScopes { get; } = Enum.GetValues<DrcNetScope>();

    // Visibility, driven by the staged kind so the row reshapes as soon as the combo changes rather
    // than after a commit. A field that does not apply is HIDDEN, not disabled: a disabled box for
    // "second region" on a width rule invites the question of what it would have meant.
    public bool ShowRegionB   => StagedKind is DrcRuleKind.MinSeparation or DrcRuleKind.MinEnclosure
                                            or DrcRuleKind.MinOverlap or DrcRuleKind.AntennaRatio;
    public bool ShowValue     => StagedKind is not (DrcRuleKind.Density or DrcRuleKind.AntennaRatio);
    public bool ShowDensity   => StagedKind is DrcRuleKind.Density;
    public bool ShowMaxRatio  => StagedKind is DrcRuleKind.Density or DrcRuleKind.AntennaRatio;
    public bool ShowNetScope  => StagedKind is DrcRuleKind.MinSpacing or DrcRuleKind.MinSeparation;

    /// <summary>What the Value box means for the current kind — a length for most, an AREA for one.</summary>
    public string ValueLabel => StagedKind == DrcRuleKind.MinArea ? "Area:" : "Value:";

    /// <summary>
    /// The hint under the region box. Stated per row rather than once in the tab header because the
    /// syntax is the part of this editor a user is least likely to know, and a hint they have to
    /// scroll to find is a hint they do not read.
    /// </summary>
    public static string RegionHint =>
        "Blank = this rule's own layer. Otherwise: 8/0, and(8/0, 10/0), not(8/0, 19/0), " +
        "sized(8/0, 100), interacting(8/0, 10/0), with_area(8/0, 100, )";

    partial void OnStagedKindChanged(DrcRuleKind value)
    {
        OnPropertyChanged(nameof(ShowRegionB));
        OnPropertyChanged(nameof(ShowValue));
        OnPropertyChanged(nameof(ShowDensity));
        OnPropertyChanged(nameof(ShowMaxRatio));
        OnPropertyChanged(nameof(ShowNetScope));
        OnPropertyChanged(nameof(ValueLabel));
    }

    public ObservableCollection<LayerOptionItem> LayerOptions { get; } = [];

    public IRelayCommand RemoveCommand { get; }

    public DrcRuleRowViewModel(DrcRule rule, TechEditorViewModel owner)
    {
        Rule  = rule;
        _owner = owner;
        RemoveCommand = new RelayCommand(() => owner.RemoveDrcRule(this));
        RefreshFromModel();
    }

    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName     = Rule.Name;
        StagedKind     = Rule.Kind;
        StagedValueText = LayoutUnits.Format(Rule.ValueDbu, _owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron);
        ValueError     = null;
        StagedSeverity = Rule.Severity;

        StagedRegionA      = Rule.RegionA ?? "";
        StagedRegionB      = Rule.RegionB ?? "";
        RegionAError       = null;
        RegionBError       = null;
        StagedWindowText   = Rule.WindowDbu is { } w
            ? LayoutUnits.Format(w, _owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron) : "";
        StagedMinRatioText = Rule.MinRatio?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        StagedMaxRatioText = Rule.MaxRatio?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        StagedNetScope     = Rule.NetScope;

        LayerOptions.Clear();
        foreach (var l in _owner.Working.Layers)
            LayerOptions.Add(new LayerOptionItem(l.Key, $"{l.Name} ({l.Key.Layer}/{l.Key.Datatype})"));
        StagedLayer = LayerOptions.FirstOrDefault(o => o.Key == Rule.Layer)
                      ?? new LayerOptionItem(Rule.Layer, $"{Rule.Layer.Layer}/{Rule.Layer.Datatype}");
        _isRefreshing = false;
    }

    public void CommitName()
    {
        var name = StagedName.Trim();
        if (name.Length == 0 || name == Rule.Name) { RefreshFromModel(); return; }
        var before = _owner.SnapshotJson();
        Rule.Name = name;
        _owner.CommitEdit(before, $"Rename DRC rule to {name}");
    }

    public void CommitKind()
    {
        if (_isRefreshing || StagedKind == Rule.Kind) return;
        var before = _owner.SnapshotJson();
        Rule.Kind = StagedKind;
        _owner.CommitEdit(before, $"Set kind of {Rule.Name}");
    }

    public void CommitLayer()
    {
        if (_isRefreshing || StagedLayer is null || StagedLayer.Key == Rule.Layer) return;
        var before = _owner.SnapshotJson();
        Rule.Layer = StagedLayer.Key;
        _owner.CommitEdit(before, $"Set layer of {Rule.Name}");
    }

    public void CommitValue()
    {
        if (!LayoutUnits.TryParse(StagedValueText, _owner.Working.DefaultDisplayUnit,
                LayoutUnits.DefaultDbuPerMicron, out var dbu) || dbu <= 0)
        {
            ValueError = "Enter a positive length, e.g. 6mil, 4u, 100 um.";
            return;
        }
        ValueError = null;
        if (dbu == Rule.ValueDbu) return;
        var before = _owner.SnapshotJson();
        Rule.ValueDbu = dbu;
        _owner.CommitEdit(before, $"Set value of {Rule.Name}");
        RefreshFromModel();
    }

    public void CommitRegionA() => CommitRegion(isSecond: false);
    public void CommitRegionB() => CommitRegion(isSecond: true);

    /// <summary>
    /// Commits one of the two region expressions. Blank clears it, which means "this rule's own
    /// layer" — the same convention the engine reads, so the editor cannot express something the
    /// checker would interpret differently.
    /// </summary>
    private void CommitRegion(bool isSecond)
    {
        string text = (isSecond ? StagedRegionB : StagedRegionA).Trim();
        string? current = isSecond ? Rule.RegionB : Rule.RegionA;

        if (text.Length == 0)
        {
            if (isSecond) RegionBError = null; else RegionAError = null;
            if (current is null) return;

            var clearBefore = _owner.SnapshotJson();
            if (isSecond) Rule.RegionB = null; else Rule.RegionA = null;
            _owner.CommitEdit(clearBefore, $"Clear region of {Rule.Name}");
            return;
        }

        if (!Drc.DrcLayerExprParser.TryParse(text, out var expr, out string? error) || expr is null)
        {
            if (isSecond) RegionBError = error; else RegionAError = error;
            return;
        }

        // Store the CANONICAL rendering, not the user's own spacing — so the `.ctech` gets one
        // stable spelling and a re-save does not churn the file on whitespace.
        string canonical = Drc.DrcLayerExprParser.Format(expr);
        if (isSecond) RegionBError = null; else RegionAError = null;
        if (canonical == current) { RefreshFromModel(); return; }

        var before = _owner.SnapshotJson();
        if (isSecond) Rule.RegionB = canonical; else Rule.RegionA = canonical;
        _owner.CommitEdit(before, $"Set region of {Rule.Name}");
        RefreshFromModel();
    }

    public void CommitWindow()
    {
        string text = StagedWindowText.Trim();
        long? value = null;

        if (text.Length > 0)
        {
            if (!LayoutUnits.TryParse(text, _owner.Working.DefaultDisplayUnit,
                    LayoutUnits.DefaultDbuPerMicron, out long dbu) || dbu <= 0)
                return;
            value = dbu;
        }

        if (value == Rule.WindowDbu) return;
        var before = _owner.SnapshotJson();
        Rule.WindowDbu = value;
        _owner.CommitEdit(before, $"Set density window of {Rule.Name}");
        RefreshFromModel();
    }

    public void CommitMinRatio() => CommitRatio(isMax: false);
    public void CommitMaxRatio() => CommitRatio(isMax: true);

    private void CommitRatio(bool isMax)
    {
        string text = (isMax ? StagedMaxRatioText : StagedMinRatioText).Trim();
        double? value = null;

        if (text.Length > 0)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) || v < 0)
                return;

            // A density is a FRACTION, but people type percentages. Accepting both and normalising
            // here is friendlier than rejecting "40" — and an antenna limit is a plain ratio that is
            // legitimately far above 1, so the conversion is scoped to density alone.
            if (Rule.Kind == DrcRuleKind.Density && v > 1.0) v /= 100.0;
            value = v;
        }

        double? current = isMax ? Rule.MaxRatio : Rule.MinRatio;
        if (Nullable.Equals(value, current)) return;

        var before = _owner.SnapshotJson();
        if (isMax) Rule.MaxRatio = value; else Rule.MinRatio = value;
        _owner.CommitEdit(before, $"Set ratio of {Rule.Name}");
        RefreshFromModel();
    }

    public void CommitNetScope()
    {
        if (_isRefreshing || StagedNetScope == Rule.NetScope) return;
        var before = _owner.SnapshotJson();
        Rule.NetScope = StagedNetScope;
        _owner.CommitEdit(before, $"Set net scope of {Rule.Name}");
    }

    public void CommitSeverity()
    {
        if (_isRefreshing || StagedSeverity == Rule.Severity) return;
        var before = _owner.SnapshotJson();
        Rule.Severity = StagedSeverity;
        _owner.CommitEdit(before, $"Set severity of {Rule.Name}");
    }
}
