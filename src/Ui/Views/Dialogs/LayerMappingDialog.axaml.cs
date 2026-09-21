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

    /// <summary>WHICH FILE this row is asking about, for a format whose unit of layer identity is a
    /// file — empty for the callers whose source layer has no origin apart from its own name, and the
    /// column then costs nothing. A Gerber set is why it exists: its files all carry the board's own
    /// stem, so every row the identification cascade could not name reads as the same word and the
    /// extension is the only thing that says which layer is being mapped.</summary>
    public string SourceDetail => Row.SourceDetail ?? "";

    public bool HasSourceDetail => Row.SourceDetail is { Length: > 0 };

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

    /// <summary>
    /// R-rail27-1b's column — <b>"in the stackup as"</b>. Empty for every row that is not being
    /// asked, which is every row of every caller but a Gerber import's unclassified files, and the
    /// column then costs nothing.
    /// </summary>
    public ObservableCollection<LayerStackupItem> StackupOptions { get; } = [];

    [ObservableProperty] private LayerActionItem _selectedAction;
    [ObservableProperty] private LayerPickerItem? _selectedMapTarget;
    [ObservableProperty] private LayerStackupItem? _selectedStackup;

    public bool ShowMapTargetCombo =>
        ShowReconciliation && SelectedAction.Action == LayoutFragment.LayerReconciliationAction.MapToExisting;

    /// <summary>True only for a row that is being asked the stackup question.</summary>
    public bool ShowStackupCombo => StackupOptions.Count > 0;

    /// <summary>False on the mint path, where there is no destination technology and therefore no
    /// reconciliation to confirm — the row then shows its source, its file, its shape count and the
    /// stackup question, and nothing else.</summary>
    [ObservableProperty] private bool _showReconciliation = true;

    partial void OnShowReconciliationChanged(bool value) => OnPropertyChanged(nameof(ShowMapTargetCombo));

    partial void OnSelectedActionChanged(LayerActionItem value) => OnPropertyChanged(nameof(ShowMapTargetCombo));

    /// <summary>The choice as it currently stands in the UI — read at OK time.</summary>
    public LayoutFragment.LayerReconciliationChoice CurrentChoice => new(
        SelectedAction.Action,
        SelectedAction.Action == LayoutFragment.LayerReconciliationAction.MapToExisting ? SelectedMapTarget?.Key : null);

    /// <summary>The stackup answer as it currently stands, or the row's own (null where it was never
    /// asked) — read at OK time beside <see cref="CurrentChoice"/>.</summary>
    public LayerStackupChoice? CurrentStackup =>
        StackupOptions.Count == 0 ? Row.Stackup : SelectedStackup?.Choice;

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

        // R-rail27-1b. ARTWORK FIRST and selected, so an import nobody reads behaves exactly as it
        // does now; the copper positions after it, in stack order, which is a question with a small
        // and complete answer set.
        if (row.StackupConductors is { } conductors)
        {
            StackupOptions.Add(new LayerStackupItem(LayerStackupChoice.Artwork, "artwork"));
            StackupOptions.Add(new LayerStackupItem(
                new LayerStackupChoice(true, LayerStackupChoice.AboveTheTop), "copper, above the top"));
            for (int i = 0; i < conductors.Count; i++)
                StackupOptions.Add(new LayerStackupItem(
                    new LayerStackupChoice(true, i), $"copper, after {conductors[i]}"));

            _selectedStackup = StackupOptions[0];
        }

        _selectedMapTarget = (row.Proposed is { } proposed ? availableLayers.FirstOrDefault(l => l.Key == proposed) : null)
            ?? availableLayers.FirstOrDefault();
        _selectedAction = Actions.FirstOrDefault(a => a.Action == row.Choice.Action) ?? Actions[0];
    }
}

/// <summary>Combo item wrapping one <i>"in the stackup as"</i> answer (R-rail27-1b).</summary>
public sealed record LayerStackupItem(LayerStackupChoice Choice, string Label)
{
    public override string ToString() => Label;
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
    /// <param name="destTech">
    /// The resolved destination technology, or <b>null for an import that MINTS one</b>
    /// (R-rail27-1b). A Gerber set imported into a fresh workspace has nothing to reconcile against,
    /// so the reconciliation half of the table has no meaning and is hidden — what is left is the
    /// question that import genuinely cannot answer for itself: which unclassified file is copper,
    /// and where it sits in the copper order.
    /// </param>
    /// <param name="rows">The proposed mapping (docs/sonnet-briefs/brief-L1g-technology-retarget.md §1).</param>
    public LayerMappingDialog(string titleText, string? sourceTechName, Technology? destTech, IReadOnlyList<LayerMappingRow> rows) : this()
    {
        Title = titleText;
        TitleText.Text = titleText;

        HeaderText.Text = destTech is null
            ? "This import creates its own technology, so there is nothing to map these layers onto. "
            + "What it cannot work out for itself is which of these files is COPPER — only a conductor "
            + "enters the stackup, and nothing on it is priced, extracted or usable as a reference "
            + "until it is one."
            : sourceTechName is { Length: > 0 }
                ? $"Moving from '{sourceTechName}' to '{destTech.Name}'. Confirm where each layer goes."
                : $"Moving to '{destTech.Name}'. Confirm where each layer goes.";

        var availableLayers = (destTech?.Layers ?? [])
            .OrderBy(l => l.ZOrder)
            .Select(l => new LayerPickerItem(l.Key, l.Name, l.Color))
            .ToList();

        foreach (var l in availableLayers)
            MapAllUnmatchedCombo.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Key });
        if (MapAllUnmatchedCombo.Items.Count > 0) MapAllUnmatchedCombo.SelectedIndex = 0;

        // The reconciliation half of the table, and the two bulk buttons under it, answer a question
        // that does not exist with no destination technology. Hidden rather than shown empty: a
        // "Map to" combo with nothing in it is a control that reads as broken.
        bool reconciling = destTech is not null;
        MatchHeader.IsVisible = reconciling;
        ActionHeader.IsVisible = reconciling;
        TargetHeader.IsVisible = reconciling;
        BulkActions.IsVisible = reconciling;
        StackupHeader.IsVisible = rows.Any(r => r.StackupConductors is not null);

        _rowVms = rows.Select(r => new LayerMappingRowViewModel(r, availableLayers, techResolved: true)).ToList();
        foreach (var rvm in _rowVms)
        {
            rvm.ShowReconciliation = reconciling;
            rvm.PropertyChanged += OnRowChanged;
        }
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
        var settled = _rowVms
            .Select(r => r.Row with { Choice = r.CurrentChoice, Stackup = r.CurrentStackup })
            .ToList();
        Close(new LayerMappingDialogResult(settled));
    }
}
