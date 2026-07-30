using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Core.Design;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Analyses;

public partial class AnalysesListView : UserControl
{
    public AnalysesListView() => InitializeComponent();

    private void OnHelp(object? sender, RoutedEventArgs e)
    {
        // Open the Simulations chapter, anchored to the selected analysis when there is one.
        string? anchor = (DataContext as AnalysesListViewModel)?.SelectedRow?.Analysis switch
        {
            DcAnalysis              => "dc",
            SParameterAnalysis      => "s-parameters",
            HarmonicBalanceAnalysis => "harmonic-balance",
            ParametricSweepAnalysis => "parametric-sweep",
            LoadpullPursuitAnalysis => "loadpull-pursuit",
            LoadpullAnalysis        => "loadpull",
            _                       => null,
        };
        DocLauncher.OpenAnalysis(anchor);
    }

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

    // ── Results file override (R-res-2/3) ─────────────────────────────────────

    private void OnResultsFileNameGotFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AnalysesListViewModel vm) vm.ResultsFileNameFocused = true;
    }

    private void OnResultsFileNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || DataContext is not AnalysesListViewModel vm) return;
        vm.ResultsFileNameFocused = false;
        vm.CommitResultsFileName(tb.Text ?? "");
    }

    private void OnResultsFileNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox tb || DataContext is not AnalysesListViewModel vm) return;
        vm.CommitResultsFileName(tb.Text ?? "");
        e.Handled = true;
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
