using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Dialog for renaming a cell folder. Returns (newName, renamePrimaries) on OK, or (null, false) on cancel.
/// </summary>
public partial class RenameCellDialog : Window
{
    public RenameCellDialog() => InitializeComponent();

    public RenameCellDialog(string currentName) : this()
    {
        Opened += (_, _) =>
        {
            NameBox.Text = currentName;
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close((string?)null, false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            TryCommit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close((string?)null, false);
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
        Close(name, RenamePrimariesBox.IsChecked == true);
    }

    private void Close(string? name, bool renamePrimaries)
        => Close(((string?)name, renamePrimaries));
}
