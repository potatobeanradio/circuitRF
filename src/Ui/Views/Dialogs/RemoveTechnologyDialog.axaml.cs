using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Asks before deleting circuitRF's copy of a technology the user installed, and says what that does
/// and does not affect.
///
/// <para>Returns true only when the affirmative button was pressed. Cancelling — or closing the
/// window — removes nothing, because the caller is what invokes the operation and it does so only on
/// true.</para>
/// </summary>
public partial class RemoveTechnologyDialog : Window
{
    // The XAML loader needs a parameterless constructor (AVLN3001).
    public RemoveTechnologyDialog() : this("", "", false) { }

    /// <param name="name">The technology's own authored name — what the user picked it by.</param>
    /// <param name="filePath">circuitRF's copy, named because it is what is about to be deleted.</param>
    /// <param name="isDefault">Whether new workspaces currently open on it.</param>
    public RemoveTechnologyDialog(string name, string filePath, bool isDefault)
    {
        InitializeComponent();

        HeadlineLabel.Text = $"Remove {name}?";

        DestroysLabel.Text =
            $"This deletes circuitRF's copy — {filePath} — and stops offering it for new workspaces. "
          + "circuitRF is not where the file came from, so if this is the only copy left it cannot be "
          + "brought back from here.";

        // Said only when it is true, and said plainly: a user who removes the default and is then
        // surprised by which technology the next workspace opens on has been told nothing useful.
        DefaultLabel.IsVisible = isDefault;
        DefaultLabel.Text      =
            "New workspaces currently open on this one. After removing it they open on circuitRF's "
          + "own default again, until you choose another.";

        SurvivesLabel.Text =
            "Every workspace already created with it is unaffected — each holds its own copy in its "
          + "own tech/ folder, which is what makes a workspace portable to a machine that never had "
          + "this file.";
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
