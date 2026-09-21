using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Smith;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The Smith Chart tool's preferred-value list editor
/// (<c>docs/design/smith-chart.md</c> §5.6a).
/// </summary>
/// <remarks>
/// <b>A row commits on focus loss or Enter</b>, which is the VAR editor's own contract and the
/// reason it is here rather than in a binding: a two-way <c>TextBox</c> writing on every keystroke
/// would rewrite the buffer under the caret and rebuild the row list the user is typing into.
/// </remarks>
public partial class SmithPreferredValuesDialog : Window
{
    public SmithPreferredValuesDialog() => InitializeComponent();

    private void OnRowLostFocus(object? sender, RoutedEventArgs e)
        => Commit(sender);

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        Commit(sender);
        e.Handled = true;
    }

    private static void Commit(object? sender)
    {
        if (sender is Control { DataContext: SmithPreferredValueRowViewModel row })
            row.Commit();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
