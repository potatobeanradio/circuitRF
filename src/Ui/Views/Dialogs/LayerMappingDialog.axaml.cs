using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Combo item wrapping one reconciliation action for a row's Action picker.</summary>
public sealed record LayerActionItem(LayoutFragment.LayerReconciliationAction Action, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One editable row of the shared layer-mapping table (docs/sonnet-briefs/brief-L1g-technology-retarget.md
/// §2). Wraps an immutable <see cref="LayerMappingRow"/> with the bindable state a combo-driven row
/// needs; <see cref="CurrentChoice"/> reads back the user's (or the default's) settled choice.
/// </summary>
public sealed partial class LayerMappingRowViewModel : ObservableObject
{
    public LayerMappingRow Row { get; }

    public string SourceLabel => Row.SourceName is { Length: > 0 } n
        ? $"{n} ({Row.Source.Layer}/{Row.Source.Datatype})"
        : $"{Row.Source.Layer}/{Row.Source.Datatype}";

    public string ShapeCountText => Row.ShapeCount.ToString();

    public string MatchLabel => Row.Match switch
    {
        LayerMatchKind.SameKeySameName       => "same layer",
        LayerMatchKind.ExactName             => "matched by name",
        LayerMatchKind.SameKeyDifferentName  => "same number, different name",
        LayerMatchKind.NoMatch               => "no match",
        _                                     => "",
    };

    public ObservableCollection<LayerActionItem> Actions { get; }
    public ObservableCollection<LayerPickerItem> MapTargets { get; }

    [ObservableProperty] private LayerActionItem _selectedAction;
    [ObservableProperty] private LayerPickerItem? _selectedMapTarget;

    public bool ShowMapTargetCombo => SelectedAction.Action == LayoutFragment.LayerReconciliationAction.MapToExisting;

    partial void OnSelectedActionChanged(LayerActionItem value) => OnPropertyChanged(nameof(ShowMapTargetCombo));

    /// <summary>The choice as it currently stands in the UI — read at OK time.</summary>
    public LayoutFragment.LayerReconciliationChoice CurrentChoice => new(
        SelectedAction.Action,
        SelectedAction.Action == LayoutFragment.LayerReconciliationAction.MapToExisting ? SelectedMapTarget?.Key : null);

    public LayerMappingRowViewModel(LayerMappingRow row, IReadOnlyList<LayerPickerItem> availableLayers, bool techResolved)
    {
        Row = row;

        Actions =
        [
            new LayerActionItem(LayoutFragment.LayerReconciliationAction.KeepUnknown, "Keep as unknown"),
            new LayerActionItem(LayoutFragment.LayerReconciliationAction.MapToExisting, "Map to existing"),
        ];
        if (techResolved)
            Actions.Add(new LayerActionItem(LayoutFragment.LayerReconciliationAction.AddToTechnology, "Add to technology"));

        MapTargets = new ObservableCollection<LayerPickerItem>(availableLayers);

        _selectedMapTarget = (row.Proposed is { } proposed ? availableLayers.FirstOrDefault(l => l.Key == proposed) : null)
            ?? availableLayers.FirstOrDefault();
        _selectedAction = Actions.FirstOrDefault(a => a.Action == row.Choice.Action) ?? Actions[0];
    }
}

/// <summary>Result of the shared layer-mapping dialog: every row's settled choice, or null on
/// cancel. Cancelling abandons the whole caller operation (whole paste, or the whole retarget) —
/// partially reconciling a fragment or a layout would be more confusing than not proceeding at all.</summary>
public sealed record LayerMappingDialogResult(IReadOnlyList<LayerMappingRow> Rows);

/// <summary>
/// One dialog serving both callers (R-L1g-1): cross-technology paste and technology retargeting are
/// the same question — "these shapes were authored against technology A and are moving to technology
/// B; where does each layer go?" — so they share this table instead of two dialogs that could drift.
/// Title/framing differ per caller; the table is identical. Sorted by shape count descending (the
/// layers that matter appear first) via <see cref="LayoutLayerMapping.Propose"/>.
/// </summary>
public partial class LayerMappingDialog : Window
{
    private List<LayerMappingRowViewModel> _rowVms = [];

    public LayerMappingDialog() => InitializeComponent();

    /// <param name="titleText">"Paste into <i>MMIC GaAs</i>" or "Change technology to <i>MMIC GaAs</i>".</param>
    /// <param name="sourceTechName">Name of the technology the geometry came from, or null.</param>
    /// <param name="destTech">The resolved destination technology — <see cref="LayoutLayerMapping.Propose"/>
    /// never returns rows when this is null, so the dialog is never shown in that case.</param>
    /// <param name="rows">The proposed mapping (docs/sonnet-briefs/brief-L1g-technology-retarget.md §1).</param>
    public LayerMappingDialog(string titleText, string? sourceTechName, Technology destTech, IReadOnlyList<LayerMappingRow> rows) : this()
    {
        Title = titleText;
        TitleText.Text = titleText;

        HeaderText.Text = sourceTechName is { Length: > 0 }
            ? $"Moving from '{sourceTechName}' to '{destTech.Name}'. Confirm where each layer goes."
            : $"Moving to '{destTech.Name}'. Confirm where each layer goes.";

        var availableLayers = destTech.Layers
            .OrderBy(l => l.ZOrder)
            .Select(l => new LayerPickerItem(l.Key, l.Name, l.Color))
            .ToList();

        foreach (var l in availableLayers)
            MapAllUnmatchedCombo.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Key });
        if (MapAllUnmatchedCombo.Items.Count > 0) MapAllUnmatchedCombo.SelectedIndex = 0;

        _rowVms = rows.Select(r => new LayerMappingRowViewModel(r, availableLayers, techResolved: true)).ToList();
        foreach (var rvm in _rowVms) rvm.PropertyChanged += OnRowChanged;
        RowsControl.ItemsSource = _rowVms;

        UpdateSummary();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        int shapeTotal = _rowVms.Sum(r => r.Row.ShapeCount);
        int mapped = _rowVms.Count(r => r.SelectedAction.Action != LayoutFragment.LayerReconciliationAction.KeepUnknown);
        int unknown = _rowVms.Count - mapped;
        SummaryText.Text = $"{shapeTotal} shape(s) · {_rowVms.Count} layer(s) → {mapped} mapped, {unknown} unknown";
    }

    private void OnMapAllUnmatchedClick(object? sender, RoutedEventArgs e)
    {
        if (MapAllUnmatchedCombo.SelectedItem is not ComboBoxItem { Tag: LayerKey target }) return;
        var mapAction = _rowVms.SelectMany(r => r.Actions).First(a => a.Action == LayoutFragment.LayerReconciliationAction.MapToExisting);
        foreach (var row in _rowVms.Where(r => r.SelectedAction.Action == LayoutFragment.LayerReconciliationAction.KeepUnknown))
        {
            row.SelectedMapTarget = row.MapTargets.FirstOrDefault(l => l.Key == target);
            row.SelectedAction = row.Actions.First(a => a.Action == LayoutFragment.LayerReconciliationAction.MapToExisting);
            _ = mapAction;
        }
        UpdateSummary();
    }

    private void OnKeepAllUnknownClick(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rowVms)
            row.SelectedAction = row.Actions.First(a => a.Action == LayoutFragment.LayerReconciliationAction.KeepUnknown);
        UpdateSummary();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var settled = _rowVms.Select(r => r.Row with { Choice = r.CurrentChoice }).ToList();
        Close(new LayerMappingDialogResult(settled));
    }
}
