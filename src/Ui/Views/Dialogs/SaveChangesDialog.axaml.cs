using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

public enum SaveChangesResult { Cancel, DontSave, Save }

public partial class SaveChangesDialog : Window
{
    /// <summary>Result set when the dialog closes. Also returned by ShowDialog&lt;SaveChangesResult&gt;.</summary>
    public SaveChangesResult Result { get; private set; } = SaveChangesResult.Cancel;

    public SaveChangesDialog()
    {
        InitializeComponent();

        // This is the prompt that stands between the user and quitting, and a torn-off document
        // window in front of the shell used to hide it completely — see ModalPromptFront.
        ModalPromptFront.Attach(this);
    }

    /// <param name="message">Body text shown in the dialog.</param>
    /// <param name="saveLabel">Label for the primary (Save/default) button. Default "Save".</param>
    /// <param name="dontSaveLabel">Label for the secondary button. Null = hide the button entirely.</param>
    /// <param name="cancelLabel">Label for the cancel button. Default "Cancel".</param>
    /// <param name="destructive">The primary button DESTROYS something (a remove, a move to the
    /// Trash): Cancel becomes the default button, answers Enter and Escape, and holds the focus.
    /// A removal can now be started from the keyboard (Delete in the Workspace panel), and a confirm
    /// that Enter accepts turns a stray Delete-then-Enter into a deleted cell.</param>
    /// <param name="bullets">A list shown after the message, one bullet per item, indented.</param>
    /// <param name="footer">Text shown after the list.</param>
    public SaveChangesDialog(
        string  message,
        string  saveLabel     = "Save",
        string? dontSaveLabel = "Don't Save",
        string  cancelLabel   = "Cancel",
        string  title         = "Save Changes",
        bool    destructive   = false,
        IReadOnlyList<string>? bullets = null,
        string? footer        = null) : this()
    {
        Title                = title;
        MessageText.Text     = message;
        foreach (string item in bullets ?? [])
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
            row.Children.Add(new TextBlock { Text = "•", FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
            var text = new TextBlock { Text = item, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            BulletList.Children.Add(row);
        }
        BulletList.IsVisible = BulletList.Children.Count > 0;
        FooterText.Text      = footer;
        FooterText.IsVisible = footer is { Length: > 0 };
        SaveButton.Content   = saveLabel;
        CancelButton.Content = cancelLabel;
        if (dontSaveLabel is null)
            DontSaveButton.IsVisible = false;
        else
            DontSaveButton.Content = dontSaveLabel;

        if (destructive)
        {
            SaveButton.IsDefault   = false;
            SaveButton.Classes.Remove("accent");
            CancelButton.IsDefault = true;
            CancelButton.IsCancel  = true;
            CancelButton.Classes.Add("accent");
            // A focused button takes Enter itself, ahead of IsDefault — so the focus has to be on
            // Cancel too, or whichever button the window happened to focus first would answer it.
            Opened += (_, _) => CancelButton.Focus();
        }
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
