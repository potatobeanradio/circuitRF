using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class NonlinearCvEditorView : UserControl
{
    public NonlinearCvEditorView()
    {
        InitializeComponent();
    }

    private NonlinearCvEditorViewModel? Vm => DataContext as NonlinearCvEditorViewModel;

    // ── V column handlers ─────────────────────────────────────────────────────

    private void OnRowVLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
        {
            row.CommitV();
            Vm?.Validate();
        }
    }

    private void OnRowVKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
        {
            if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
            {
                row.CommitV();
                Vm?.Validate();
            }
            e.Handled = true; // prevent IsDefault Apply from firing on cell Enter
        }
    }

    // ── C column handlers ─────────────────────────────────────────────────────

    private void OnRowCLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
        {
            row.CommitC();
            Vm?.Validate();
        }
    }

    private void OnRowCKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
        {
            if (sender is TextBox tb && tb.DataContext is CvRowViewModel row)
            {
                row.CommitC();
                Vm?.Validate();
            }
            e.Handled = true; // prevent IsDefault Apply from firing on cell Enter
        }
    }

    // ── Text mode handler ─────────────────────────────────────────────────────

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
        => Vm?.Validate();

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Close discards staged edits — do NOT apply.
        var win = TopLevel.GetTopLevel(this) as Window;
        win?.Close();
    }
}
