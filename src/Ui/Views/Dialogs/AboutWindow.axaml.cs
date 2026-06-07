using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private async void OnAcknowledgmentsClicked(object? sender, RoutedEventArgs e)
    {
        await new AcknowledgmentsWindow().ShowDialog(this);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
        => Close();
}
