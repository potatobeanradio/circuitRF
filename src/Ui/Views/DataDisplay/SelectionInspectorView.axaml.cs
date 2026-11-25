using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.DataDisplay;

/// <summary>The Properties panel for two or more selected items (<see cref="SelectionInspectorViewModel"/>).</summary>
public partial class SelectionInspectorView : UserControl
{
    public SelectionInspectorView() => InitializeComponent();

    private SelectionInspectorViewModel? Vm => DataContext as SelectionInspectorViewModel;

    private async void PickAccent(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not Window window) return;
        var start = vm.PickerStart;
        var picked = await new ColorPickerDialog(new Rgba(start.R, start.G, start.B, 255)).ShowDialog<Rgba?>(window);
        if (picked is { } value && ReferenceEquals(DataContext, vm) && vm.IsActive)
            vm.SetAccent(Color.FromRgb(value.R, value.G, value.B));
    }

    private void ResetAccent(object? sender, RoutedEventArgs e) => Vm?.SetAccent(null);
}
