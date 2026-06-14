using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons.Avalonia;
using Material.Icons;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// HIG plan dialog: shows everything that will be created/saved, lets the user review
/// and edit names, and returns a confirmed <see cref="SavePlan"/> (or null on Cancel).
/// </summary>
public partial class SavePlanDialog : Window
{
    // ── State ──────────────────────────────────────────────────────────────────

    private readonly SavePlanBuilder _builder = null!;
    private SaveMode _mode = SaveMode.EachOwnCell;

    private string _workspaceName = "";
    private string _sharedCellName = "";
    private readonly Dictionary<string, string>    _cellNameOverrides    = new();
    private readonly Dictionary<string, TextBlock> _errorLabels          = new();
    private readonly Dictionary<string, TextBlock> _saveLocationLabels   = new();
    private readonly Dictionary<string, TextBlock> _saveNameLabels       = new();

    // ── Constructor ────────────────────────────────────────────────────────────

    public SavePlanDialog() => InitializeComponent();

    /// <summary>Opens the dialog pre-populated from the given initial plan.</summary>
    public SavePlanDialog(SavePlan initialPlan, SavePlanBuilder builder) : this()
    {
        _builder = builder;

        // Seed name state from initial plan.
        if (initialPlan.WorkspaceStep is { } ws)
            _workspaceName = ws.Name;

        if (initialPlan.CellSteps.Count > 0)
            _sharedCellName = initialPlan.CellSteps[0].Name;

        foreach (var step in initialPlan.SaveSteps)
            _cellNameOverrides[step.Document.Id] = step.TargetCellName;

        // Show mode toggle only when scratch schematics need cells.
        if (initialPlan.CellSteps.Count > 0)
        {
            ModeToggleArea.IsVisible = true;
            SharedCellNameBox.Text   = _sharedCellName;
        }

        RebuildRows(initialPlan);
        UpdateSaveAllEnabled();
    }

    // ── Mode toggle ────────────────────────────────────────────────────────────

    private void OnModeRadioChecked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var newMode = EachOwnCellRadio.IsChecked == true
            ? SaveMode.EachOwnCell
            : SaveMode.AllInOneCell;
        if (newMode == _mode) return;
        _mode = newMode;

        SharedCellNameBox.IsEnabled = _mode == SaveMode.AllInOneCell;
        AllInOneCellHint.IsVisible  = _mode == SaveMode.AllInOneCell;

        var plan = RebuildPlan();
        RebuildRows(plan);
        UpdateSaveAllEnabled();
    }

    private void OnSharedCellNameChanged(object? sender, TextChangedEventArgs e)
    {
        _sharedCellName = SharedCellNameBox.Text?.Trim() ?? "";
        if (_errorLabels.TryGetValue("__shared__", out var lbl))
        {
            lbl.Text      = NameValidator.Validate(_sharedCellName);
            lbl.IsVisible = lbl.Text is not null;
        }
        var plan = RebuildPlan();
        RebuildRows(plan);
        UpdateSaveAllEnabled();
    }

    // ── Plan rebuild ──────────────────────────────────────────────────────────

    private SavePlan RebuildPlan() => _builder.Build(
        mode:                  _mode,
        workspaceNameOverride: _workspaceName.Length > 0 ? _workspaceName : null,
        allInOneCellName:      _mode == SaveMode.AllInOneCell && _sharedCellName.Length > 0
                                 ? _sharedCellName : null,
        cellNameOverrides:     _mode == SaveMode.EachOwnCell ? _cellNameOverrides : null);

    // ── Row construction ──────────────────────────────────────────────────────

    private void RebuildRows(SavePlan plan)
    {
        _errorLabels.Clear();
        _saveLocationLabels.Clear();
        _saveNameLabels.Clear();
        PlanRowsPanel.Children.Clear();

        // Column header.
        PlanRowsPanel.Children.Add(BuildHeaderRow());
        PlanRowsPanel.Children.Add(new Border
        {
            Height     = 1,
            Margin     = new Thickness(0, 2, 0, 4),
            Opacity    = 0.3,
            Background = Brushes.Gray,
        });

        // Workspace step.
        if (plan.WorkspaceStep is { } wsStep)
            PlanRowsPanel.Children.Add(BuildWorkspaceRow(wsStep));

        // Cell steps.
        foreach (var cellStep in plan.CellSteps)
        {
            var docId = _mode == SaveMode.AllInOneCell
                ? "__shared__"
                : plan.SaveSteps
                    .FirstOrDefault(s => string.Equals(
                        s.TargetCellName, cellStep.Name, StringComparison.OrdinalIgnoreCase))
                    ?.Document.Id ?? cellStep.Name;

            PlanRowsPanel.Children.Add(BuildCellRow(cellStep, docId));
        }

        // Save steps.
        foreach (var saveStep in plan.SaveSteps)
            PlanRowsPanel.Children.Add(BuildSaveRow(saveStep));
    }

    private static Grid BuildHeaderRow()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,90,*,160,80") };
        static TextBlock H(string text) => new()
        {
            Text     = text, FontSize = 10, Opacity = 0.5,
            Margin   = new Thickness(4, 0, 0, 0),
        };
        var h1 = H("Action");   Grid.SetColumn(h1, 1); grid.Children.Add(h1);
        var h2 = H("Location"); Grid.SetColumn(h2, 2); grid.Children.Add(h2);
        var h3 = H("Name");     Grid.SetColumn(h3, 3); grid.Children.Add(h3);
        return grid;
    }

    private StackPanel BuildWorkspaceRow(WorkspaceStep wsStep)
    {
        var container = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        var grid      = BuildRowGrid(MaterialIconKind.FolderOutline, "Create workspace",
                                     Elide(wsStep.ParentDir + Path.DirectorySeparatorChar),
                                     wsStep.ParentDir);

        var nameBox = new TextBox { Text = _workspaceName, FontSize = 12, MaxLength = 200 };
        nameBox.TextChanged += (_, _) =>
        {
            _workspaceName = nameBox.Text?.Trim() ?? "";
            SetError("__ws__", NameValidator.Validate(_workspaceName));
            UpdateSaveAllEnabled();
        };
        Grid.SetColumn(nameBox, 3);
        grid.Children.Add(nameBox);

        container.Children.Add(grid);
        container.Children.Add(MakeErrorLabel("__ws__", indent: 116));
        return container;
    }

    private StackPanel BuildCellRow(CellStep cellStep, string docId)
    {
        var container = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        var grid      = BuildRowGrid(MaterialIconKind.Folder, "Create cell",
                                     "(workspace root)", null);

        var initialName = _mode == SaveMode.AllInOneCell
            ? _sharedCellName
            : (_cellNameOverrides.TryGetValue(docId, out var ov) ? ov : cellStep.Name);

        var nameBox = new TextBox { Text = initialName, FontSize = 12, MaxLength = 200 };
        nameBox.TextChanged += (_, _) =>
        {
            var v = nameBox.Text?.Trim() ?? "";
            if (_mode == SaveMode.EachOwnCell)
            {
                _cellNameOverrides[docId] = v;
                if (_saveLocationLabels.TryGetValue(docId, out var locLabel))
                {
                    var newDest = $"{v}/schematic/";
                    locLabel.Text = Elide(newDest);
                    ToolTip.SetTip(locLabel, newDest);
                }
                if (_saveNameLabels.TryGetValue(docId, out var nameLabel))
                    nameLabel.Text = $"{v}.csch";
            }
            SetError(docId, NameValidator.Validate(v));
            UpdateSaveAllEnabled();
        };
        Grid.SetColumn(nameBox, 3);
        grid.Children.Add(nameBox);

        if (cellStep.IsTestBench)
        {
            var badge = new TextBlock
            {
                Text = "(TestBench)", FontSize = 11, Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(badge, 4);
            grid.Children.Add(badge);
        }

        container.Children.Add(grid);
        container.Children.Add(MakeErrorLabel(docId, indent: 116));
        return container;
    }

    private StackPanel BuildSaveRow(SaveStep saveStep)
    {
        var container = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var dest      = $"{saveStep.TargetCellName}/schematic/";
        var grid      = BuildRowGrid(MaterialIconKind.FileOutline, "Save",
                                     Elide(dest), dest);

        // Track the location TextBlock (grid child index 2) for live cell-name updates.
        _saveLocationLabels[saveStep.Document.Id] = (TextBlock)grid.Children[2];

        var nameLabel = new TextBlock
        {
            Text = saveStep.FileName, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameLabel, 3);
        grid.Children.Add(nameLabel);

        // Track the name TextBlock (grid child index 3) for live cell-name updates.
        _saveNameLabels[saveStep.Document.Id] = nameLabel;

        if (saveStep.IsPrimary)
        {
            var badge = new TextBlock
            {
                Text = "(primary)", FontSize = 11, Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(badge, 4);
            grid.Children.Add(badge);
        }

        container.Children.Add(grid);
        return container;
    }

    private static Grid BuildRowGrid(
        MaterialIconKind icon, string verb, string dest, string? tooltip)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("26,90,*,160,80"),
            MinHeight         = 30,
        };
        var ico = new MaterialIcon
        {
            Kind = icon, Width = 16, Height = 16, Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(ico, 0);
        grid.Children.Add(ico);

        var verbLabel = new TextBlock
        {
            Text = verb, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(verbLabel, 1);
        grid.Children.Add(verbLabel);

        var destLabel = new TextBlock
        {
            Text = dest, FontSize = 12, Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0),
        };
        if (tooltip is not null)
            ToolTip.SetTip(destLabel, tooltip);
        Grid.SetColumn(destLabel, 2);
        grid.Children.Add(destLabel);

        return grid;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Elide(string path)
    {
        const int max = 40;
        if (path.Length <= max) return path;
        var sep   = Path.DirectorySeparatorChar;
        var parts = path.TrimEnd(sep).Split(sep);
        return parts.Length >= 2
            ? $"…{sep}{parts[^1]}{(path.EndsWith(sep) ? sep.ToString() : "")}"
            : $"…{path[^(max - 1)..]}";
    }

    private TextBlock MakeErrorLabel(string key, double indent)
    {
        var lbl = new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.OrangeRed,
            Margin = new Thickness(indent, 0, 0, 2),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };
        _errorLabels[key] = lbl;
        return lbl;
    }

    private void SetError(string key, string? error)
    {
        if (!_errorLabels.TryGetValue(key, out var lbl)) return;
        lbl.Text      = error;
        lbl.IsVisible = error is not null;
    }

    // ── Validation + Save All enable ──────────────────────────────────────────

    private void UpdateSaveAllEnabled()
    {
        var plan  = RebuildPlan();
        var valid = true;

        if (plan.WorkspaceStep is not null)
            valid &= _workspaceName.Length > 0 && NameValidator.Validate(_workspaceName) is null;

        if (_mode == SaveMode.AllInOneCell)
            valid &= _sharedCellName.Length > 0 && NameValidator.Validate(_sharedCellName) is null;
        else
            foreach (var s in plan.CellSteps)
                valid &= s.Name.Length > 0 && NameValidator.Validate(s.Name) is null;

        SaveAllButton.IsEnabled = valid;
    }

    // ── Button handlers ────────────────────────────────────────────────────────

    private void OnSaveAllClick(object? sender, RoutedEventArgs e)
        => Close(RebuildPlan());

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(null);
}
