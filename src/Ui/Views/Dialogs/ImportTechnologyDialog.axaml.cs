using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.TechImport;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// What the Import Technology dialog returns on Import. <paramref name="LayerTablePath"/> is null
/// when the user chose to import the stack alone.
/// </summary>
/// <param name="RuleDeckPaths">
/// Every file of the process's design-rule deck, or empty when the user declined to import rules (or
/// the kit ships none). A deck is read as one program — see <c>TechnologyScanResult.RuleDeckFiles</c>.
/// </param>
public sealed record ImportTechnologyResult(
    string                Name,
    string                StackFilePath,
    string?               LayerTablePath,
    bool                  SetAsDefault,
    IReadOnlyList<string> RuleDeckPaths,
    IReadOnlyList<string> RuleValueTablePaths);

/// <summary>
/// Picks which of a kit's technology files to build a technology from.
///
/// <para>Both choices are offered rather than guessed at, and for different reasons. A process ships
/// one stack description PER CORNER — typical, fast, slow — and they describe genuinely different
/// silicon, so picking one is the user's call. A kit often carries more than one layer table too (a
/// full one and a cut-down one for a particular view), and they are not interchangeable either.</para>
///
/// <para>Mirrors <see cref="NewTechnologyDialog"/>'s return-or-null ShowDialog contract and its
/// live-validation idiom.</para>
/// </summary>
public partial class ImportTechnologyDialog : Window
{
    private const string NoLayerTable = "(none — stackup only)";

    private readonly string?              _techDir;
    private readonly TechnologyScanResult _scan;
    private          bool                 _nameEditedByUser;
    private          bool                 _settingSuggestedName;

    public ImportTechnologyDialog() : this(new TechnologyScanResult([], [], []), null, "", "") { }

    /// <param name="scan">What the scan turned up. Must contain at least one stack file.</param>
    /// <param name="techDir">Absolute path of the workspace's tech/ folder (may not exist yet).</param>
    /// <param name="scannedFolder">The folder that was scanned, for the header line.</param>
    /// <param name="fallbackName">Name used when the chosen file states none circuitRF can use.</param>
    public ImportTechnologyDialog(
        TechnologyScanResult scan, string? techDir, string scannedFolder, string fallbackName)
    {
        InitializeComponent();

        _scan    = scan;
        _techDir = techDir;

        SourceLabel.Text = scannedFolder.Length > 0
            ? $"Found in {scannedFolder}: {scan.StackFiles.Count} stack description(s), " +
              $"{scan.LayerTables.Count} layer table(s)."
            : "";

        StackCombo.ItemsSource = scan.StackFiles.Select(Describe).ToList();
        if (scan.StackFiles.Count > 0) StackCombo.SelectedIndex = 0;

        var tables = new List<string> { NoLayerTable };
        tables.AddRange(scan.LayerTables.Select(Describe));
        LayerTableCombo.ItemsSource = tables;
        // A layer table is what makes drawn geometry mean anything, so the first one is offered by
        // default; "none" stays available for a kit whose table circuitRF could not read.
        LayerTableCombo.SelectedIndex = scan.LayerTables.Count > 0 ? 1 : 0;

        // A deck is not an alternative to choose between (see TechnologyScanResult.RuleDeckFiles), so
        // it is offered as one checkbox with a count rather than a picker. Absent kit -> absent row.
        if (scan.HasRuleDeck)
        {
            ImportRulesCheck.IsVisible = true;
            RuleDeckLabel.IsVisible    = true;
            RuleDeckLabel.Text =
                $"{scan.RuleDeckFiles.Count} rule-deck file(s)" +
                (scan.RuleValueTables.Count > 0
                    ? $", {scan.RuleValueTables.Count} rule-value table(s)."
                    : ", no rule-value table found — only rules stating their value in place can be read.") +
                " circuitRF checks minimum width and minimum spacing; every other rule the deck states " +
                "is reported at import and not enforced.";
        }

        _fallbackName = fallbackName;
        SuggestNameFromSelection();
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        UpdateView();
    }

    private readonly string _fallbackName = "";

    private static string Describe(TechnologyFileCandidate c) => $"{c.Label}  —  {c.RelativePath}";

    private void OnStackChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A user who has typed their own name keeps it; otherwise the suggestion follows the choice,
        // since the corner is usually part of what distinguishes one from another.
        if (!_nameEditedByUser) SuggestNameFromSelection();
        UpdateView();
    }

    private void SuggestNameFromSelection()
    {
        if (NameBox is null) return;

        string label = SelectedStack()?.Label ?? _fallbackName;
        string name  = NameValidator.Validate(label) is null && label.Length > 0 ? label : _fallbackName;

        _settingSuggestedName = true;
        NameBox.Text = name;
        _settingSuggestedName = false;
    }

    private TechnologyFileCandidate? SelectedStack()
        => StackCombo.SelectedIndex >= 0 && StackCombo.SelectedIndex < _scan.StackFiles.Count
            ? _scan.StackFiles[StackCombo.SelectedIndex]
            : null;

    private TechnologyFileCandidate? SelectedLayerTable()
    {
        int i = LayerTableCombo.SelectedIndex - 1;   // index 0 is the "none" entry
        return i >= 0 && i < _scan.LayerTables.Count ? _scan.LayerTables[i] : null;
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_settingSuggestedName) _nameEditedByUser = true;
        UpdateView();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            TryCommit();
            e.Handled = true;
        }
    }

    private void TryCommit()
    {
        var name  = NameBox.Text?.Trim() ?? "";
        var stack = SelectedStack();

        if (stack is null) return;
        if (NameValidator.Validate(name) is not null) return;
        if (_techDir is not null && File.Exists(Path.Combine(_techDir, $"{name}.ctech"))) return;

        bool wantRules = _scan.HasRuleDeck && ImportRulesCheck.IsChecked == true;

        Close(new ImportTechnologyResult(
            name,
            stack.Path,
            SelectedLayerTable()?.Path,
            SetAsDefaultCheck.IsChecked == true,
            wantRules ? _scan.RuleDeckFiles.Select(f => f.Path).ToList() : [],
            wantRules ? _scan.RuleValueTables.Select(f => f.Path).ToList() : []));
    }

    private void UpdateView()
    {
        if (NameBox is null || OkButton is null) return;

        var name      = NameBox.Text?.Trim() ?? "";
        string? message = NameValidator.Validate(name);

        if (message is null && _techDir is not null && name.Length > 0
            && File.Exists(Path.Combine(_techDir, $"{name}.ctech")))
        {
            message = $"A technology named '{name}' already exists.";
        }

        ValidationMessage.Text      = message;
        ValidationMessage.IsVisible = message is not null;

        bool ok = name.Length > 0 && message is null && SelectedStack() is not null;
        OkButton.IsEnabled = ok;

        PreviewLabel.Text      = ok ? $"Will create: tech/{name}.ctech" : "";
        PreviewLabel.IsVisible = ok;
    }
}
