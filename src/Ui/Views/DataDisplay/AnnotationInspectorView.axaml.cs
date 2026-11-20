using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class AnnotationInspectorView : UserControl
{
    public AnnotationInspectorView() => InitializeComponent();

    private async void PickColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AnnotationInspectorViewModel vm || vm.Annotation is not { } target
            || TopLevel.GetTopLevel(this) is not Window window) return;
        var color = (vm.ColorSwatch as ISolidColorBrush)?.Color ?? (Foreground as ISolidColorBrush)?.Color ?? Colors.Black;
        var picked = await new ColorPickerDialog(new Rgba(color.R, color.G, color.B, color.A)).ShowDialog<Rgba?>(window);
        if (picked is { } value && ReferenceEquals(DataContext, vm) && ReferenceEquals(vm.Annotation, target))
            vm.SetColor(new Color(value.A, value.R, value.G, value.B).ToString());
    }

    private void ResetColor(object? sender, RoutedEventArgs e)
        => (DataContext as AnnotationInspectorViewModel)?.SetColor(null);
}