using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.DataDisplay.Controls;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class DataDisplayView : UserControl
{
    public DataDisplayView()
    {
        InitializeComponent();
    }

    private void OnPlotControlDoubleTapped(object? sender, TappedEventArgs e)
        => ThePlotControl.HandleDoubleTapAt(e.GetPosition(ThePlotControl));
}
