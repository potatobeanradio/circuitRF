using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Asks for the one value a resistive film has no meaning without, and for a new layer its name. OK is
/// disabled until the sheet resistance is a positive number, by the parser the card and the drawing use.</summary>
public partial class ResistorFilmPromptDialog : Window
{
    private bool _askName;

    public ResistorFilmPromptDialog() => InitializeComponent();

    /// <summary>The answer, or null on cancel. The name is null when it was not asked for.</summary>
    public static async Task<(string? Name, double OhmPerSq)?> AskAsync(Window owner, string prompt, bool askName)
    {
        var dialog = new ResistorFilmPromptDialog { _askName = askName };
        dialog.PromptText.Text = prompt;
        dialog.NameRow.IsVisible = askName;
        dialog.Validate();
        dialog.Opened += (_, _) => (askName ? dialog.NameBox : dialog.ValueBox).Focus();
        return await dialog.ShowDialog<(string?, double)?>(owner);
    }

    /// <summary>＋ Film Level's form: the drawing layer from a picker ending in <see cref="TechEditorViewModel.NewFilmLayerChoice"/>,
    /// whose name box stays in place and takes text only for that row, and where the level goes. Null on cancel.</summary>
    public static async Task<(string Layer, string? Name, double OhmPerSq, string Below)?> AskForLevelAsync(
        Window owner, IReadOnlyList<string> layers, IReadOnlyList<string> below, string? defaultBelow)
    {
        var dialog = LevelForm(layers, below, defaultBelow);
        dialog.Opened += (_, _) => dialog.ValueBox.Focus();
        if (await dialog.ShowDialog<(string?, double)?>(owner) is not { } answer) return null;
        return ((string)dialog.LayerBox.SelectedItem!, answer.Item1, answer.Item2, (string)dialog.BelowBox.SelectedItem!);
    }

    /// <summary>The ＋ Film Level form, filled in and not yet shown.</summary>
    public static ResistorFilmPromptDialog LevelForm(IReadOnlyList<string> layers, IReadOnlyList<string> below, string? defaultBelow)
    {
        var dialog = new ResistorFilmPromptDialog { _askName = true };
        dialog.PromptText.Text = "New film level";
        dialog.LayerRow.IsVisible = true;
        dialog.BelowRow.IsVisible = true;
        dialog.LayerBox.ItemsSource = layers;
        dialog.LayerBox.SelectedIndex = 0;
        dialog.BelowBox.ItemsSource = below;
        dialog.BelowBox.SelectedItem = defaultBelow;
        dialog._levelForm = true;
        dialog.Validate();
        return dialog;
    }

    private bool _levelForm;

    private bool CreatesLayer => !_levelForm || Equals(LayerBox.SelectedItem, TechEditorViewModel.NewFilmLayerChoice);

    private void Validate()
    {
        NameBox.IsEnabled = CreatesLayer;
        OkButton.IsEnabled = TechEditorViewModel.ParseSheetResistance(ValueBox.Text) is not null
                          && (!_askName || !CreatesLayer || !string.IsNullOrWhiteSpace(NameBox.Text))
                          && (!_levelForm || BelowBox.SelectedItem is not null);
    }

    private void OnChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void OnPicked(object? sender, SelectionChangedEventArgs e) => Validate();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (TechEditorViewModel.ParseSheetResistance(ValueBox.Text) is not { } rs) return;
        Close(((string?, double)?)(_askName && CreatesLayer ? NameBox.Text!.Trim() : null, rs));
    }
}
