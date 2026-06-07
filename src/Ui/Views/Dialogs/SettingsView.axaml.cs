using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class SettingsView : Window
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
