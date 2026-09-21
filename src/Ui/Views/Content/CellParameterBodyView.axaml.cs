using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Content;

public partial class CellParameterBodyView : UserControl
{
    // Suppress SelectionChanged re-entrancy when RebuildRows updates bound ComboBox items.
    private bool _suppressUnitCommit;
    private bool _suppressDimCommit;

    public CellParameterBodyView() => InitializeComponent();

    // ── Name TextBox ──────────────────────────────────────────────────────────

    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is CellParameterRowViewModel row)
            row.CommitName();
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (sender is TextBox tb && tb.DataContext is CellParameterRowViewModel row)
            {
                row.CommitName();
                e.Handled = true;
            }
        }
    }

    // ── Default TextBox ───────────────────────────────────────────────────────

    private void OnDefaultLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is CellParameterRowViewModel row)
            row.CommitDefault();
    }

    private void OnDefaultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (sender is TextBox tb && tb.DataContext is CellParameterRowViewModel row)
            {
                row.CommitDefault();
                e.Handled = true;
            }
        }
    }

    // ── Terminals (R-lvs1-4c) ─────────────────────────────────────────────────
    //
    // The WHOLE block commits, not one field: the map's defects are relational, so a row that wrote
    // itself per keystroke would leave the cell reporting a duplicate port while the user was still
    // typing the fix for it. A commit that changes nothing adds nothing to the undo stack.

    private void OnTerminalFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CellTerminalRowViewModel } c
            && c.FindAncestorOfType<CellParameterBodyView>()?.DataContext is CellParameterEditorViewModel vm)
            vm.CommitTerminals();
    }

    private void OnTerminalFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter)) return;
        OnTerminalFieldLostFocus(sender, e);
        e.Handled = true;
    }

    // ── Unit ComboBox ─────────────────────────────────────────────────────────

    private void OnUnitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressUnitCommit) return;
        if (sender is ComboBox cb && cb.DataContext is CellParameterRowViewModel row
            && cb.SelectedItem is string unit)
        {
            _suppressUnitCommit = true;
            row.CommitUnit(unit);
            _suppressUnitCommit = false;
        }
    }

    // ── Dimension ComboBox ────────────────────────────────────────────────────

    private void OnDimensionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDimCommit) return;
        if (sender is ComboBox cb && cb.DataContext is CellParameterRowViewModel row
            && cb.SelectedItem is UnitDimension dim)
        {
            _suppressDimCommit = true;
            row.CommitDimension(dim);
            _suppressDimCommit = false;
        }
    }
}
