using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Popup Component Parameters dialog for a Layout PCell instance, opened on double-click
/// (<c>LayoutEditorView.axaml.cs :: OnInstanceDoubleTapped</c>) — the Layout Editor's counterpart to
/// the Schematic Editor's <see cref="ParameterEditorDialog"/>, opened the same non-modal way
/// (<c>Window.Show</c>, not <c>ShowDialog</c>) so the user can keep working on the canvas while it's
/// open. Hosts the SAME <c>PCellParameterListView</c> the docked Properties panel uses — never a
/// second parameter-editing implementation.</summary>
public partial class LayoutPCellParameterDialog : Window
{
    public LayoutPCellParameterDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
