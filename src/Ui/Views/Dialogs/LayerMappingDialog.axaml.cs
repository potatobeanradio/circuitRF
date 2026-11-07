using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
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

    /// <summary>
    /// Brief DFR38-1's column, <b>"Read as"</b>: a DXF row mapped to copper, a drill layer or the outline.
    /// Empty for every other row and every other caller, and the column then costs nothing.
    /// </summary>
    public ObservableCollection<DxfReadingItem> ReadingOptions { get; } = [];

    private readonly Technology? _readingTech;

    [ObservableProperty] private DxfReadingItem? _selectedReading;
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

    partial void OnSelectedActionChanged(LayerActionItem value)
    {
        OnPropertyChanged(nameof(ShowMapTargetCombo));
        OfferReadings(null);
    }

    partial void OnSelectedMapTargetChanged(LayerPickerItem? value) => OfferReadings(null);

    /// <summary>True only for a row with more than one way to read it.</summary>
    public bool ShowReadingCombo => ReadingOptions.Count > 1;

    /// <summary>The readings this row's target allows, <paramref name="keep"/> selected or else the first. Items
    /// before selection, in this one path, or the combo clears what it cannot find.</summary>
    private void OfferReadings(DxfLayerReading? keep)
    {
        if (Row.Reading is null) return;
        var target = SelectedAction.Action == LayoutFragment.LayerReconciliationAction.MapToExisting ? SelectedMapTarget?.Key : null;
        ReadingOptions.Clear();
        foreach (var reading in DxfBoardReading.Offered(_readingTech, target))
            ReadingOptions.Add(new DxfReadingItem(reading, DxfBoardReading.Label(reading)));
        SelectedReading = ReadingOptions.FirstOrDefault(o => o.Reading == keep) ?? ReadingOptions[0];
        OnPropertyChanged(nameof(ShowReadingCombo));
    }
    /// <summary>The choice as it currently stands in the UI — read at OK time.</summary>
    public LayoutFragment.LayerReconciliationChoice CurrentChoice => new(
        SelectedAction.Action,
        SelectedAction.Action == LayoutFragment.LayerReconciliationAction.MapToExisting ? SelectedMapTarget?.Key : null);

    /// <summary>The stackup answer as it currently stands, or the row's own (null where it was never
    /// asked) — read at OK time beside <see cref="CurrentChoice"/>.</summary>
    public LayerStackupChoice? CurrentStackup =>
        StackupOptions.Count == 0 ? Row.Stackup : SelectedStackup?.Choice;

    /// <summary>The reading as it currently stands — null on every row that was not asked.</summary>
    public DxfLayerReading? CurrentReading => Row.Reading is null ? null : SelectedReading?.Reading ?? DxfLayerReading.AsDrawn;

    public LayerMappingRowViewModel(LayerMappingRow row, IReadOnlyList<LayerPickerItem> availableLayers, bool techResolved,
                                    Technology? readingTech = null)
    {
        Row = row;
        _readingTech = readingTech;

        Actions =
        [
            new LayerActionItem(LayoutFragment.LayerReconciliationAction.KeepUnknown, "Keep as unknown"),
            new LayerActionItem(LayoutFragment.LayerReconciliationAction.MapToExisting, "Map to existing"),
        ];
        if (techResolved)
            Actions.Add(new LayerActionItem(LayoutFragment.LayerReconciliationAction.AddToTechnology, "Add to technology"));
        if (row.Reading is not null)
            Actions.Add(new LayerActionItem(LayoutFragment.LayerReconciliationAction.Ignore, "Ignore"));

        MapTargets = new ObservableCollection<LayerPickerItem>(availableLayers);

        // R-rail27-1b. ARTWORK FIRST and selected, so an import nobody reads behaves exactly as it
        // does now; the copper positions after it, in stack order, which is a question with a small
        // and complete answer set; then "vias", for a Gerber drawing of the plated holes.
        if (row.StackupConductors is { } conductors)
        {
            StackupOptions.Add(new LayerStackupItem(LayerStackupChoice.Artwork, "artwork"));
            StackupOptions.Add(new LayerStackupItem(
                new LayerStackupChoice(true, LayerStackupChoice.AboveTheTop), "copper, above the top"));
            for (int i = 0; i < conductors.Count; i++)
                StackupOptions.Add(new LayerStackupItem(
                    new LayerStackupChoice(true, i), $"copper, after {conductors[i]}"));
            StackupOptions.Add(new LayerStackupItem(LayerStackupChoice.Vias, "vias"));

            _selectedStackup = StackupOptions[0];
        }

        // The row's own answer first: a row can arrive mapped to something other than what was proposed.
        _selectedMapTarget = ((row.Choice.MapTarget ?? row.Proposed) is { } proposed ? availableLayers.FirstOrDefault(l => l.Key == proposed) : null)
            ?? availableLayers.FirstOrDefault();
        _selectedAction = Actions.FirstOrDefault(a => a.Action == row.Choice.Action) ?? Actions[0];
        OfferReadings(row.Reading);
    }
}

/// <summary>Combo item wrapping one <i>"read as"</i> answer (brief DFR38-1).</summary>
public sealed record DxfReadingItem(DxfLayerReading Reading, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Combo item wrapping one <i>"in the stackup as"</i> answer (R-rail27-1b).</summary>
public sealed record LayerStackupItem(LayerStackupChoice Choice, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Result of the shared layer-mapping dialog: every row's settled choice, or null on
/// cancel. Cancelling abandons the whole caller operation (whole paste, or the whole retarget) —
/// partially reconciling a fragment or a layout would be more confusing than not proceeding at all.
/// <para><see cref="Target"/> is the Gerber import's Technology choice (R-gt-8), already committed — a
/// catalog technology has been copied into <c>tech/</c> by the time this is returned. Null for every
/// other caller.</para></summary>
public sealed record LayerMappingDialogResult(IReadOnlyList<LayerMappingRow> Rows, ImportTechnologyTarget? Target = null);

/// <summary>
/// The table's row view models for one destination technology — framework-free, so what a Technology
/// choice does to the table (above all R-gt-4: a technology an import USES offers no Add to technology)
/// is testable without a window.
/// </summary>
public static class LayerMappingRows
{
    /// <summary>The layers a row may be mapped onto: the destination's own, in the order its .ctech lists them.</summary>
    public static List<LayerPickerItem> Targets(Technology? destTech) => (destTech?.Layers ?? [])
        .Select(l => new LayerPickerItem(l.Key, l.Name, l.Color))
        .ToList();

    /// <param name="allowAddToTechnology">False for a technology that is used and never changed.</param>
    public static List<LayerMappingRowViewModel> Build(
        IReadOnlyList<LayerMappingRow> rows, Technology? destTech, bool allowAddToTechnology)
    {
        var targets = Targets(destTech);
        return rows.Select(r => new LayerMappingRowViewModel(r, targets, allowAddToTechnology, destTech)).ToList();
    }
}

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

    // The Gerber import's Technology row (R-gt-8). Null for every other caller.
    private ImportMappingRequest? _request;
    private string _format = "Gerber";
    private IReadOnlyList<ImportTechnologyChoice> _choices = [];
    private List<ComboBoxItem> _choiceItems = [];
    private Technology? _workspaceTech;
    private string? _workspaceRoot;

    // The DXF import's preview (brief DFR38-1 R-dfr38-1-6). Null for every other caller.
    private DxfMappingRequest? _dxf;
    private Technology? _dxfTech;
    private int _previewGeneration;
    private readonly Avalonia.Threading.DispatcherTimer _previewDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>The join tolerance as typed, in microns, or the default where the box does not read as a number.</summary>
    public double JoinToleranceMicrons =>
        double.TryParse(JoinToleranceBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double um) && um >= 0
            ? um : DxfBoardReading.DefaultJoinToleranceMicrons;

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
        ShowTable(sourceTechName, destTech, rows, allowAddToTechnology: true);
    }

    /// <summary>
    /// The Gerber import's form (brief-gerber-import-target-technology R-gt-8): the same table, under a
    /// <b>Technology</b> row that chooses which technology the import lands in. Changing it re-proposes
    /// the rows against that technology through <paramref name="request"/> — the import's own post-read
    /// stage, run again with nothing read twice (R-gt-2).
    /// </summary>
    /// <param name="workspaceTech">The workspace's own technology: what New donates layer names from.</param>
    /// <param name="choices"><see cref="ImportTechnologyChoices.Build"/>'s list.</param>
    /// <param name="selected"><see cref="ImportTechnologyChoices.DefaultIndex"/> (D3).</param>
    /// <param name="format">What is being imported, as the table names its source: "Gerber" or "Board".</param>
    public LayerMappingDialog(
        string titleText, Technology? workspaceTech, ImportMappingRequest request,
        IReadOnlyList<ImportTechnologyChoice> choices, int selected, string? workspaceRoot,
        string format = "Gerber") : this()
    {
        Title = titleText;
        TitleText.Text = titleText;
        _format = format;
        _request = request;
        _choices = choices;
        _workspaceTech = workspaceTech;
        _workspaceRoot = workspaceRoot;

        TechnologyRow.IsVisible = true;
        var items = new List<ComboBoxItem>(choices.Count);
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice.Label, IsEnabled = choice.IsEnabled };
            ToolTip.SetTip(item, choice.ToolTip);
            items.Add(item);
        }
        _choiceItems = items;
        TechnologyCombo.ItemsSource = items;
        TechnologyCombo.SelectedIndex = selected;
        ShowChoice(choices[selected]);
        TechnologyCombo.SelectionChanged += (_, _) =>
        {
            if (TechnologyCombo.SelectedIndex is >= 0 and var i && i < _choices.Count) ShowChoice(_choices[i]);
        };
    }

    /// <summary>
    /// A DXF import's form (brief DFR38-1): the table's rows are what model space reaches, each with Ignore and
    /// a reading, over a preview drawn by the layout editor's renderer from what those answers would import.
    /// Nothing is written until the import returns from the dialog.
    /// </summary>
    public LayerMappingDialog(string titleText, Technology destTech, DxfMappingRequest request) : this()
    {
        Title = titleText;
        TitleText.Text = titleText;
        _format = "DXF";
        _dxf = request;
        _dxfTech = destTech;
        Height = 820;

        ShowTable("DXF", destTech, request.Rows, allowAddToTechnology: true);
        ReadingHeader.IsVisible = true;
        JoinToleranceRow.IsVisible = true;
        PreviewPane.IsVisible = true;
        ContentGrid.RowDefinitions[1].Height = new GridLength(1.4, GridUnitType.Star);

        JoinToleranceBox.Text = DxfBoardReading.DefaultJoinToleranceMicrons.ToString(System.Globalization.CultureInfo.InvariantCulture);
        JoinToleranceBox.TextChanged += (_, _) => RestartPreviewDelay();
        _previewDelay.Tick += (_, _) => { _previewDelay.Stop(); StartPreview(); };
        PreviewHost.SizeChanged += (_, _) => RestartPreviewDelay();
        Closed += (_, _) => { _previewDelay.Stop(); _previewGeneration++; };
    }

    private void RestartPreviewDelay()
    {
        if (_dxf is null) return;
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private List<LayerMappingRow> SettledRows() => _rowVms
        .Select(r => r.Row with { Choice = r.CurrentChoice, Stackup = r.CurrentStackup, Reading = r.CurrentReading })
        .ToList();

    /// <summary>Reads and rasters on a worker, as the import chooser's previews do: everything it touches is
    /// private to that one call, and only the newest result is shown. The task is for a headless capture to wait on.</summary>
    public Task StartPreview()
    {
        if (_dxf is not { } request) return Task.CompletedTask;

        int generation = ++_previewGeneration;
        var rows = SettledRows();
        double tolerance = JoinToleranceMicrons;
        var tech = _dxfTech;
        double scaling = RenderScaling;
        // The host's bounds: an Image with no source yet arranges to nothing, so its own would never grow.
        int pxW = (int)(PreviewHost.Bounds.Width * scaling), pxH = (int)(PreviewHost.Bounds.Height * scaling);
        var theme = LayoutRenderTheme.FromTheme(ThemeService.Active,
            ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light);

        return Task.Run(() =>
        {
            var preview = request.Preview(rows, tolerance);
            var bitmap = ComponentPreviewRenderer.RenderFootprint(preview.View, tech, theme, pxW, pxH, scaling);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (generation != _previewGeneration) { bitmap?.Dispose(); return; }
                (PreviewImage.Source as IDisposable)?.Dispose();
                PreviewImage.Source = bitmap;
                PreviewNotes.Text = string.Join("\n", preview.Messages);
            });
        });
    }

    private ImportTechnologyChoice? SelectedChoice =>
        TechnologyCombo.SelectedIndex is >= 0 and var i && i < _choices.Count ? _choices[i] : null;

    /// <summary>The rows as the import would propose them for <paramref name="choice"/>, in the table.</summary>
    /// <remarks>The "in the stackup as" answers already given are KEPT, by file: they are what made the
    /// technology just chosen enabled at all (<see cref="ImportTechnologyChoice.EnabledFor"/>), and a
    /// re-proposal that reset them would disable the very entry that is selected.</remarks>
    private void ShowChoice(ImportTechnologyChoice choice)
    {
        var request = _request!;
        var rows = choice.Kind == ImportTechnologyChoiceKind.New
            ? (request.Target.IsUse ? request.Repropose(null) : request.Rows)
            : request.Repropose(choice.Technology);
        var answered = _rowVms
            .Where(r => r.ShowStackupCombo && r.SelectedStackup is { Choice.AsCopper: true } or { Choice.AsVias: true })
            .GroupBy(StackupKey)
            .ToDictionary(g => g.Key, g => g.First().SelectedStackup!.Choice);
        ShowTable(_format, choice.Technology ?? _workspaceTech, rows, choice.AllowsAddToTechnology, answered);
    }

    private static string StackupKey(LayerMappingRowViewModel row) => row.Row.SourceDetail ?? row.Row.SourceName ?? "";

    /// <summary>The copper the set holds as the table now stands: the files the cascade identified, and
    /// the ones answered "copper" in the "in the stackup as" column.</summary>
    private int CopperAsAnswered() =>
        (_request?.CopperCount ?? 0) +
        _rowVms.Count(r => r.ShowStackupCombo && r.SelectedStackup is { Choice.AsCopper: true });

    /// <summary>D4 again, against <see cref="CopperAsAnswered"/>. A selected technology that no longer
    /// matches falls back to New rather than staying selected while disabled.</summary>
    private void RegateTechnologies()
    {
        if (_request is null) return;
        int copper = CopperAsAnswered();
        for (int i = 0; i < _choices.Count && i < _choiceItems.Count; i++)
            _choiceItems[i].IsEnabled = _choices[i].EnabledFor(copper);
        // Posted: this runs inside one of the table's own rows' change notifications, and falling back
        // re-proposes — rebuilds — the table that row belongs to.
        if (SelectedChoice is { } selected && !selected.EnabledFor(copper))
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (SelectedChoice is { } still && !still.EnabledFor(CopperAsAnswered())) TechnologyCombo.SelectedIndex = 0;
            });
    }

    private void ShowTable(string? sourceTechName, Technology? destTech, IReadOnlyList<LayerMappingRow> rows,
                           bool allowAddToTechnology,
                           IReadOnlyDictionary<string, LayerStackupChoice>? answered = null)
    {
        foreach (var old in _rowVms) old.PropertyChanged -= OnRowChanged;

        HeaderText.Text = destTech is null && _format == "Board"
            ? "This import creates its own technology from the board's layers and stackup, so there is nothing to map "
            + "these layers onto. Choose an existing technology above to import into it instead."
            : destTech is null
            ? "This import creates its own technology, so there is nothing to map these layers onto. "
            + "What it cannot work out for itself is which of these files is COPPER — only a conductor "
            + "enters the stackup, and nothing on it is priced, extracted or usable as a reference "
            + "until it is one — and which is the VIAS."
            : sourceTechName is { Length: > 0 }
                ? $"Moving from '{sourceTechName}' to '{destTech.Name}'. Confirm where each layer goes."
                : $"Moving to '{destTech.Name}'. Confirm where each layer goes.";

        var availableLayers = LayerMappingRows.Targets(destTech);

        MapAllUnmatchedCombo.Items.Clear();
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

        _rowVms = LayerMappingRows.Build(rows, destTech, allowAddToTechnology);
        foreach (var rvm in _rowVms)
        {
            rvm.ShowReconciliation = reconciling;
            if (answered is not null && answered.TryGetValue(StackupKey(rvm), out var kept) &&
                rvm.StackupOptions.FirstOrDefault(o => o.Choice == kept) is { } option)
                rvm.SelectedStackup = option;
            rvm.PropertyChanged += OnRowChanged;
        }
        RowsControl.ItemsSource = _rowVms;

        UpdateSummary();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateSummary();
        if (e.PropertyName == nameof(LayerMappingRowViewModel.SelectedStackup)) RegateTechnologies();
        if (e.PropertyName is nameof(LayerMappingRowViewModel.SelectedReading) or nameof(LayerMappingRowViewModel.SelectedAction)
                           or nameof(LayerMappingRowViewModel.SelectedMapTarget))
            RestartPreviewDelay();
    }

    private void UpdateSummary()
    {
        int shapeTotal = _rowVms.Sum(r => r.Row.ShapeCount);
        int ignored = _rowVms.Count(r => r.SelectedAction.Action == LayoutFragment.LayerReconciliationAction.Ignore);
        int mapped = _rowVms.Count(r => r.SelectedAction.Action != LayoutFragment.LayerReconciliationAction.KeepUnknown) - ignored;
        int unknown = _rowVms.Count - mapped - ignored;
        SummaryText.Text = $"{shapeTotal} shape(s) · {_rowVms.Count} layer(s) → {mapped} mapped, {unknown} unknown"
                         + (ignored > 0 ? $", {ignored} ignored" : "");
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
        var settled = SettledRows();

        ImportTechnologyTarget? target = null;
        if (_request is not null && SelectedChoice is { } choice)
        {
            // D2: a catalog technology is copied into tech/ NOW, on Continue — never on selection, so
            // a dialog that is cancelled has written nothing.
            try { target = ImportTechnologyChoices.Commit(choice, _workspaceRoot); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            {
                SummaryText.Text = $"The technology could not be copied into tech/: {ex.Message}";
                return;
            }
        }
        Close(new LayerMappingDialogResult(settled, target));
    }
}
