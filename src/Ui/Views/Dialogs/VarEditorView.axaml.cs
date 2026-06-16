using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class VarEditorView : UserControl
{
    public VarEditorView()
    {
        InitializeComponent();
    }

    private VarEditorViewModel? Vm => DataContext as VarEditorViewModel;

    // ── Mode B row event handlers ─────────────────────────────────────────────

    private void OnRowNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is VarRowViewModel row)
            row.CommitName();
    }

    private void OnRowNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
            if (sender is TextBox tb && tb.DataContext is VarRowViewModel row)
                row.CommitName();
    }

    private void OnRowExprLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is VarRowViewModel row)
            row.CommitExpression();
    }

    private void OnRowExprKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
            if (sender is TextBox tb && tb.DataContext is VarRowViewModel row)
                row.CommitExpression();
    }

    private void OnRowUnitLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is VarRowViewModel row)
            row.CommitUnit(tb.Text ?? "");
    }

    private void OnRowUnitKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.Tab)
            if (sender is TextBox tb && tb.DataContext is VarRowViewModel row)
                row.CommitUnit(tb.Text ?? "");
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.IsTextMode == true)
            Vm.ApplyTextCommand.Execute(null);

        var win = TopLevel.GetTopLevel(this) as Window;
        win?.Close();
    }
}
