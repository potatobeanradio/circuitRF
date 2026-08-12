using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the user chose in <see cref="TechnologyMergeDialog"/>.</summary>
/// <param name="ReplaceWholeFile">
/// Discard the existing technology entirely. Kept as its own flag rather than a fourth
/// <see cref="TechMergeMode"/> because it is not a merge at all — nothing is combined, and giving it
/// the same shape as the others would invite treating it as one more policy.
/// </param>
/// <param name="ReplaceKeys">For <see cref="TechMergeMode.Selective"/>, the items ticked.</param>
public sealed record TechnologyMergeResult(
    TechSection Sections,
    TechMergeMode Mode,
    bool ReplaceWholeFile,
    IReadOnlySet<string> ReplaceKeys);

/// <summary>One row of the per-item conflict list.</summary>
public sealed partial class ConflictRow(TechMergeConflict conflict) : ObservableObject
{
    public string Key => conflict.Key;
    public string Label => conflict.Label;

    /// <summary>Both sides on one line — the choice is meaningless without seeing what changes.</summary>
    public string Detail => $"yours: {conflict.Mine}   →   incoming: {conflict.Theirs}";

    /// <summary>
    /// <b>Defaults to true — take the imported version.</b> Someone who has just chosen to import a
    /// process update is asking for the update; the list exists so they can hold back the few items
    /// they deliberately tuned, not so they have to re-approve the ninety they wanted anyway.
    /// </summary>
    [ObservableProperty] private bool _replace = true;
}

/// <summary>
/// Picks which sections of another technology to bring in, and what to do about collisions.
///
/// <para>One dialog serves re-import and mix-and-match: they are the same question asked about a
/// different source, and two dialogs would drift on the wording of the part that matters most —
/// what happens to the work already in the file.</para>
/// </summary>
public partial class TechnologyMergeDialog : Window
{
    private readonly List<ConflictRow> _conflicts = [];

    // Parameterless ctor for the XAML loader only.
    //
    // InitializeComponent(), NEVER AvaloniaXamlLoader.Load(this) directly: the generated
    // InitializeComponent loads the XAML *and* assigns every x:Name field. Calling the loader alone
    // leaves them all null, which surfaces as a NullReferenceException the first time this dialog
    // touches one of its own controls. See src/Ui/CLAUDE.md.
    public TechnologyMergeDialog() => InitializeComponent();

    public TechnologyMergeDialog(
        string sourceName, TechSection available, bool isReimport,
        IReadOnlyList<TechMergeConflict>? conflicts = null) : this()
    {
        HeaderText.Text = isReimport
            ? $"\"{sourceName}\" already exists in this workspace."
            : $"Import from \"{sourceName}\".";

        SubtitleText.Text = isReimport
            ? "Choose what to take from the process you just imported, and what to do about anything already here."
            : "Choose which parts of that technology to bring into the one you are editing.";

        // Only offer what the source actually carries — a checkbox for a section that does not exist
        // would be a control that silently does nothing.
        Configure(LayersCheck,  available.HasFlag(TechSection.Layers));
        Configure(StackupCheck, available.HasFlag(TechSection.Stackup));
        Configure(RulesCheck,   available.HasFlag(TechSection.DrcRules));

        // Replacing the whole file is only meaningful when there IS a whole file to replace.
        ReplaceAllRadio.IsVisible = isReimport;
        ReplaceAllWarning.IsVisible = isReimport;

        foreach (var c in conflicts ?? []) _conflicts.Add(new ConflictRow(c));

        // The per-item choice is the DEFAULT whenever anything collides: it is the only option that
        // both applies the update the user asked for and lets them hold back what they tuned. With
        // nothing to choose between it is meaningless, so the plain "add what is new" takes over.
        SelectiveRadio.IsEnabled = _conflicts.Count > 0;
        if (_conflicts.Count > 0)
        {
            SelectiveRadio.IsChecked = true;
        }
        else
        {
            SelectiveRadio.Content += "  (nothing collides)";
            KeepMineRadio.IsChecked = true;
        }

        ConflictList.ItemsSource = _conflicts;
        ConflictCountText.Text = _conflicts.Count == 1
            ? "1 item exists in both — tick the ones to replace:"
            : $"{_conflicts.Count} items exist in both — tick the ones to replace:";

        SelectiveRadio.IsCheckedChanged += (_, _) => UpdateConflictPanel();
        KeepMineRadio.IsCheckedChanged  += (_, _) => UpdateConflictPanel();
        ReplaceRadio.IsCheckedChanged   += (_, _) => UpdateConflictPanel();
        ReplaceAllRadio.IsCheckedChanged += (_, _) => UpdateConflictPanel();

        OkButton.Content = isReimport ? "Apply" : "Import";
    }

    private void UpdateConflictPanel() =>
        ConflictPanel.IsVisible = SelectiveRadio.IsChecked == true && _conflicts.Count > 0;

    private static void Configure(CheckBox box, bool available)
    {
        box.IsEnabled = available;
        box.IsChecked = available;
        if (!available) box.Content += "  (not in this file)";
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var c in _conflicts) c.Replace = true;
    }

    private void OnSelectNoneClick(object? sender, RoutedEventArgs e)
    {
        foreach (var c in _conflicts) c.Replace = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        bool replaceAll = ReplaceAllRadio.IsChecked == true;

        var sections = TechSection.None;
        if (LayersCheck.IsChecked  == true) sections |= TechSection.Layers;
        if (StackupCheck.IsChecked == true) sections |= TechSection.Stackup;
        if (RulesCheck.IsChecked   == true) sections |= TechSection.DrcRules;

        // Nothing selected and not replacing wholesale is a no-op; treat it as a cancel rather than
        // reporting "imported nothing", which reads as a failure.
        if (!replaceAll && sections == TechSection.None) { Close(null); return; }

        var mode =
            SelectiveRadio.IsChecked == true ? TechMergeMode.Selective :
            ReplaceRadio.IsChecked   == true ? TechMergeMode.Replace :
                                               TechMergeMode.AddMissingOnly;

        var keys = _conflicts.Where(c => c.Replace).Select(c => c.Key).ToHashSet();
        Close(new TechnologyMergeResult(sections, mode, replaceAll, keys));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
