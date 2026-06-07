using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public enum SaveChangesResult { Cancel, DontSave, Save }

public partial class SaveChangesDialog : Window
{
    public SaveChangesResult Result { get; private set; } = SaveChangesResult.Cancel;

    public SaveChangesDialog() => InitializeComponent();

    public SaveChangesDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveChangesResult.Cancel;
        Close();
    }

    private void OnDontSaveClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveChangesResult.DontSave;
        Close();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveChangesResult.Save;
        Close();
    }
}
