using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // One source for the version, and it is the build's own: see CircuitRF.Ui.AppVersion.
        VersionText.Text = $"Version {AppVersion.Display}";
    }

    private async void OnAcknowledgmentsClicked(object? sender, RoutedEventArgs e)
    {
        await new AcknowledgmentsWindow().ShowDialog(this);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
        => Close();
}
