using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Single-field name-input dialog.  Returns the validated name via ShowDialog,
/// or null if the user cancels.  Validates with NameValidator on OK.
/// </summary>
public partial class InputNameDialog : Window
{
    public InputNameDialog() => InitializeComponent();

    public InputNameDialog(string title, string prompt, string initialText = "") : this()
    {
        Title            = title;
        PromptLabel.Text = prompt;
        NameBox.Text     = initialText;
        // Pre-selects whatever initialText supplied (brief-cell-first-and-ui-fixes.md R-cc-3: a
        // suggested name is pre-selected so typing replaces it outright) — a no-op when initialText
        // is empty, since SelectAll on empty text selects nothing.
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            TryCommit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void TryCommit()
    {
        var name   = NameBox.Text?.Trim() ?? "";
        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            ValidationMessage.Text      = reason;
            ValidationMessage.IsVisible = true;
            return;
        }
        Close(name);
    }
}
