using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-L4b-4: "$INSUNITS absent or 0 must not be guessed silently... ask — a drawing interpreted at
/// 1000x the intended scale is the worst possible silent failure." Shown only when
/// <c>DxfUnits.NanometersPerDrawingUnit(reader.InsUnits)</c> is null; defaults to millimeters per the
/// brief's own stated default. Returns the chosen <c>$INSUNITS</c> value via <c>ShowDialog&lt;int?&gt;</c>,
/// or null on Cancel (aborts the whole import — see <c>DxfImport.Import</c>'s <c>resolveUnits</c> contract).
/// </summary>
public partial class DxfUnitsPromptDialog : Window
{
    public DxfUnitsPromptDialog() => InitializeComponent();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        int chosen = DxfUnits.DefaultPromptUnits;
        if (InchRadio.IsChecked == true) chosen = DxfUnits.Inches;
        else if (MicronRadio.IsChecked == true) chosen = DxfUnits.Microns;
        else if (CmRadio.IsChecked == true) chosen = DxfUnits.Centimeters;
        else if (MRadio.IsChecked == true) chosen = DxfUnits.Meters;
        else if (FootRadio.IsChecked == true) chosen = DxfUnits.Feet;
        else if (MmRadio.IsChecked == true) chosen = DxfUnits.Millimeters;
        Close(chosen);
    }
}
