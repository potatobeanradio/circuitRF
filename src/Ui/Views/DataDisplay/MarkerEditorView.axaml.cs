using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class MarkerEditorView : UserControl
{
    public MarkerEditorView() => InitializeComponent();

    private void OnFreqTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is MarkerEditorViewModel vm)
        {
            vm.CommitFrequency();
            e.Handled = true;
        }
    }

    private void OnVswrValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is MarkerEditorViewModel vm)
        {
            vm.CommitVswrValue();
            e.Handled = true;
        }
    }

    private void OnImpedanceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is MarkerEditorViewModel vm)
        {
            vm.CommitImpedance();
            e.Handled = true;
        }
    }
}
