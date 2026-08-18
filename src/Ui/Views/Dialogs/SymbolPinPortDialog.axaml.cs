using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Owner, 2026-08-17: "If user double clicks on a placed Pin in the Symbol editor, a small dialog
/// should pop up allowing the user to change that Pin's Port number."
///
/// <para>Returns the chosen 1-BASED port number via <c>ShowDialog&lt;int?&gt;</c>, or null on Cancel.
/// One-based because that is the number shown on the canvas glyph and in the Properties inspector;
/// <c>SymbolPin.PortIndex</c> is 0-based underneath and the conversion happens in exactly one place —
/// <c>SymbolEditorViewModel.SetPinPortNumber</c> — so the two spellings cannot drift apart here.</para>
///
/// <para>All validation rules live in <see cref="SymbolPinPortInput"/>, which is framework-free and
/// tested; this file only shows what that returns and gates the OK button on it.</para>
/// </summary>
public partial class SymbolPinPortDialog : Window
{
    private int? _declaredPortCount;

    public SymbolPinPortDialog() : this(1, null) { }

    /// <param name="portNumber">The pin's current 1-based port number.</param>
    /// <param name="portCount">The owning cell's declared port count, or null for an orphan symbol.</param>
    public SymbolPinPortDialog(int portNumber, int? portCount)
    {
        InitializeComponent();

        _declaredPortCount = portCount;
        PortBox.Text = portNumber.ToString(CultureInfo.InvariantCulture);

        if (portCount is { } n)
        {
            PortCountNote.Text = n == 1
                ? "This cell declares 1 port."
                : $"This cell declares {n} ports.";
            PortCountNote.IsVisible = true;
        }

        Revalidate();

        // Selected on open, so typing replaces the number rather than appending to it — the whole
        // point of the dialog is to change a value that is already there.
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PortBox.Focus();
            PortBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnPortTextChanged(object? sender, TextChangedEventArgs e) => Revalidate();

    private void Revalidate()
    {
        var result = SymbolPinPortInput.Validate(PortBox.Text, _declaredPortCount);

        // The error wins the line when there is one; the out-of-range NOTE uses it too, because a
        // valid-but-unusual number is worth a word and the two can never both apply.
        string? message = result.Error ?? result.Note;
        ErrorText.Text      = message ?? "";
        ErrorText.IsVisible = message is not null;
        ErrorText.Opacity   = result.Error is not null ? 1.0 : 0.7;

        OkButton.IsEnabled = result.IsValid;
    }

    // Enter is the default button, but a TextBox swallows it in some layouts — and pressing it on an
    // invalid value must do nothing rather than close the dialog on a number that was refused.
    private void OnPortBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        TryAccept();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryAccept();

    private void TryAccept()
    {
        var result = SymbolPinPortInput.Validate(PortBox.Text, _declaredPortCount);
        if (!result.IsValid) { Revalidate(); return; }
        Close(result.PortNumber);
    }
}
