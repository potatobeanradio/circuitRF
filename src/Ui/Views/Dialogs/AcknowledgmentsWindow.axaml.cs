using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class AcknowledgmentsWindow : Window
{
    public AcknowledgmentsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
        => Close();
}
