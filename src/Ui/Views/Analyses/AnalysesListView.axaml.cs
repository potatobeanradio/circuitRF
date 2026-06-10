using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Analyses;

public partial class AnalysesListView : UserControl
{
    public AnalysesListView() => InitializeComponent();

    // ── Selection ─────────────────────────────────────────────────────────────

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && DataContext is AnalysesListViewModel vm)
        {
            var rows = lb.SelectedItems?
                         .OfType<AnalysisRowViewModel>()
                         .ToList() ?? [];
            vm.UpdateSelection(rows);
        }
    }

    // ── Double-click to edit ──────────────────────────────────────────────────

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not AnalysesListViewModel vm) return;
        if (vm.SelectedRow is null) return;
        var window = this.FindAncestorOfType<Window>();
        await vm.EditCommand.ExecuteAsync(window);
    }

    // ── Keyboard copy / paste ─────────────────────────────────────────────────

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AnalysesListViewModel vm) return;
        bool isCtrlOrCmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                           e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!isCtrlOrCmd) return;

        var window = this.FindAncestorOfType<Window>();
        switch (e.Key)
        {
            case Key.C:
                _ = vm.CopyCommand.ExecuteAsync(window);
                e.Handled = true;
                break;
            case Key.V:
                _ = vm.PasteCommand.ExecuteAsync(window);
                e.Handled = true;
                break;
        }
    }
}
