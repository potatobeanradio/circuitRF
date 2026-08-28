using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The right-click <c>Edit Ruler…</c> popup (docs/design/layout-view.md §9B.6, R-rul-12): "opens the
/// same property set as a modal, so the ruler can be adjusted without the inspector docked — which is
/// the state a user is usually in when they are presenting."
///
/// <para>Hosts the SAME <c>LayoutRulerPropertiesView</c> the docked panel uses, and is shown
/// non-modally (<c>Window.Show</c>) exactly like <see cref="LayoutPCellParameterDialog"/>, so the
/// canvas stays usable while it is open.</para>
/// </summary>
public partial class LayoutRulerEditDialog : Window
{
    public LayoutRulerEditDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
