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

    public void CommitSeverity()
    {
        if (_isRefreshing || StagedSeverity == Rule.Severity) return;
        var before = _owner.SnapshotJson();
        Rule.Severity = StagedSeverity;
        _owner.CommitEdit(before, $"Set severity of {Rule.Name}");
    }
}
