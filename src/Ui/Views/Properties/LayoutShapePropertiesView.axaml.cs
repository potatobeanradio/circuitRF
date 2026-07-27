using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>Layout Editor's shape-properties panel (L1c). Commit convention mirrors
/// <c>LayoutEditorView.axaml.cs</c>'s toolbar fields exactly: LostFocus commits, Enter commits.</summary>
public partial class LayoutShapePropertiesView : UserControl
{
    public LayoutShapePropertiesView() => InitializeComponent();

    private LayoutShapePropertiesViewModel? Vm => DataContext as LayoutShapePropertiesViewModel;

    private void OnNetCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitNetText(tb.Text ?? ""); }
    private void OnNetKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitNetText(tb.Text ?? ""); }

    private void OnCornerRadiusCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitCornerRadiusText(tb.Text ?? ""); }
    private void OnCornerRadiusKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitCornerRadiusText(tb.Text ?? ""); }

    private void OnRadiusCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitRadiusText(tb.Text ?? ""); }
    private void OnRadiusKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitRadiusText(tb.Text ?? ""); }

    private void OnPathWidthCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitPathWidthText(tb.Text ?? ""); }
    private void OnPathWidthKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitPathWidthText(tb.Text ?? ""); }

    private void OnLabelTextCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitLabelText(tb.Text ?? ""); }
    private void OnLabelTextKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitLabelText(tb.Text ?? ""); }

    private void OnLabelHeightCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitLabelHeightText(tb.Text ?? ""); }
    private void OnLabelHeightKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitLabelHeightText(tb.Text ?? ""); }

    private void OnFlattenTolCommit(object? sender, RoutedEventArgs e) { if (sender is TextBox tb) Vm?.CommitFlattenTolText(tb.Text ?? ""); }
    private void OnFlattenTolKeyDown(object? sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) Vm?.CommitFlattenTolText(tb.Text ?? ""); }
}
