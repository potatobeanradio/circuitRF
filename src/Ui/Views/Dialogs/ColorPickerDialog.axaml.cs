using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class ColorPickerDialog : Window
{
    public ColorPickerDialog() => InitializeComponent();

    public ColorPickerDialog(Rgba seed) : this()
    {
        ColorViewControl.Color = new Color(seed.A, seed.R, seed.G, seed.B);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var c = ColorViewControl.Color;
        Close(new Rgba(c.R, c.G, c.B, c.A));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
