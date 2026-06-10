using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public enum SaveChangesResult { Cancel, DontSave, Save }

public partial class SaveChangesDialog : Window
{
    /// <summary>Result set when the dialog closes. Also returned by ShowDialog&lt;SaveChangesResult&gt;.</summary>
    public SaveChangesResult Result { get; private set; } = SaveChangesResult.Cancel;

    public SaveChangesDialog() => InitializeComponent();

    /// <param name="message">Body text shown in the dialog.</param>
    /// <param name="saveLabel">Label for the primary (Save/default) button. Default "Save".</param>
    /// <param name="dontSaveLabel">Label for the secondary button. Null = hide the button entirely.</param>
    /// <param name="cancelLabel">Label for the cancel button. Default "Cancel".</param>
    public SaveChangesDialog(
        string  message,
        string  saveLabel     = "Save",
        string? dontSaveLabel = "Don't Save",
        string  cancelLabel   = "Cancel") : this()
    {
        MessageText.Text     = message;
        SaveButton.Content   = saveLabel;
        CancelButton.Content = cancelLabel;
        if (dontSaveLabel is null)
            DontSaveButton.IsVisible = false;
        else
            DontSaveButton.Content = dontSaveLabel;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveChangesResult.Cancel;
        Close(Result);
    }

    private void OnDontSaveClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveChangesResult.DontSave;
        Close(Result);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveChangesResult.Save;
        Close(Result);
    }
}
