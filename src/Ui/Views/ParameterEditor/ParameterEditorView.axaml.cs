using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.ParameterEditor;

public partial class ParameterEditorView : UserControl
{
    // Suppress SelectionChanged re-entrancy when RefreshFromModel updates StagedUnit bindings.
    private bool _suppressUnitCommit;

    public ParameterEditorView() => InitializeComponent();

    private ParameterEditorViewModel? Vm => DataContext as ParameterEditorViewModel;

    // ── Instance name ─────────────────────────────────────────────────────────

    private void OnInstanceNameLostFocus(object? sender, RoutedEventArgs e) => Vm?.CommitInstanceName();

    private void OnInstanceNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            Vm?.CommitInstanceName();
            e.Handled = true;
        }
    }

    // ── Parameter name TextBox (editable for extensible types; read-only otherwise) ──

    private void OnParamNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
            row.CommitName();
    }

    private void OnParamNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
            {
                row.CommitName();
                e.Handled = true;
            }
        }
    }

    // ── Parameter expression TextBox ──────────────────────────────────────────

    private void OnParamExprLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
        {
            row.CommitExpression();
            Vm?.TriggerResort();
        }
    }

    private void OnParamExprKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
            {
                row.CommitExpression();
                Vm?.TriggerResort();
                e.Handled = true;
            }
        }
    }

    // ── Unit ComboBox ─────────────────────────────────────────────────────────

    private void OnUnitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressUnitCommit) return;
        if (sender is ComboBox cb && cb.DataContext is ParameterRowViewModel row
            && cb.SelectedItem is string unit)
        {
            _suppressUnitCommit = true;
            row.CommitUnit(unit);
            _suppressUnitCommit = false;
        }
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        // Placeholder: opens local HTML doc for the component type. Real docs wired in a later phase.
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Walk up to the hosting Window and close it (dialog host only; ignored when embedded).
        if (TopLevel.GetTopLevel(this) is Window win)
            win.Close();
    }
}
