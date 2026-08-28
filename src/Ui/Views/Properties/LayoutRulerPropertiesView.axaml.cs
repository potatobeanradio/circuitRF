using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>
/// The ruler-annotation property surface (docs/design/layout-view.md §9B.6) — hosted BOTH by the
/// docked Layout Properties panel and by the right-click <c>Edit Ruler…</c> modal, so R-rul-12's "the
/// same property set as a modal" is literally the same control rather than a second implementation.
///
/// <para>Commit convention is <see cref="LayoutShapePropertiesView"/>'s exactly: LostFocus commits,
/// Enter commits, Escape reverts, and every field is dispatched generically by its <c>Tag</c>.</para>
/// </summary>
public partial class LayoutRulerPropertiesView : UserControl
{
    public LayoutRulerPropertiesView() => InitializeComponent();

    private LayoutShapePropertiesViewModel? Vm => DataContext as LayoutShapePropertiesViewModel;

    private void OnFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string key }) Vm?.SetFocusedField(key);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        Vm?.SetFocusedField(null);
        Vm?.CommitField(key, tb.Text ?? "");
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        if (e.Key is Key.Enter or Key.Return) Vm?.CommitField(key, tb.Text ?? "");
        else if (e.Key == Key.Escape) Vm?.RevertField(key);
    }
}
