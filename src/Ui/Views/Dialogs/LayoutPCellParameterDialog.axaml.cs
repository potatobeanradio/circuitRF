using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Popup Component Properties dialog for a Layout PCell instance, opened on double-click
/// (<c>LayoutEditorView.axaml.cs :: OnInstanceDoubleTapped</c>) — the Layout Editor's counterpart to
/// the Schematic Editor's <see cref="ParameterEditorDialog"/>, opened the same non-modal way
/// (<c>Window.Show</c>, not <c>ShowDialog</c>) so the user can keep working on the canvas while it's
/// open. Hosts the SAME <c>LayoutInstancePropertiesView</c> and <c>PCellParameterListView</c> the
/// docked Properties panel uses — never a second implementation of either.
///
/// <para>The instance surface was added after an owner report (2026-09-20): with the parameter list
/// alone, double-clicking a built-in land pattern — a generated cell that declares no parameters,
/// its case and its density being its identity (R-fp1-4c) — opened a window showing a "Parameters"
/// header over nothing, while the docked panel beside it showed the footprint picker, the
/// designator, the placement and the array. The class name is kept so the several doc comments
/// pointing at it stay valid.</para></summary>
public partial class LayoutPCellParameterDialog : Window
{
    public LayoutPCellParameterDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
