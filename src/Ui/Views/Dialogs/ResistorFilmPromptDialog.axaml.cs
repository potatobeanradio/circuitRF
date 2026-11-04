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

    private void Validate()
        => OkButton.IsEnabled = TechEditorViewModel.ParseSheetResistance(ValueBox.Text) is not null
                             && (!_askName || !string.IsNullOrWhiteSpace(NameBox.Text));

    private void OnChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (TechEditorViewModel.ParseSheetResistance(ValueBox.Text) is not { } rs) return;
        Close(((string?, double)?)(_askName ? NameBox.Text!.Trim() : null, rs));
    }
}
