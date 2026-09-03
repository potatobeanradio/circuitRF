using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-L4h-3: the user picked ONE Gerber file — is that really the intent, or was the enclosing folder?
/// A single Gerber file is one layer, with no drill data, no other copper and no board outline, which
/// is almost never what "import this board" means and is nonetheless a perfectly reasonable thing to
/// want when checking one layer.
///
/// <para>Three things this dialog does that a bare yes/no would not. It STATES WHAT THE FOLDER HOLDS
/// (<see cref="GerberImportEntry.FolderSurvey.Question"/> — counted by content, by the same classifier
/// the import itself uses), because a prompt asking a question the user has no basis to answer is worse
/// than no prompt. <b>Whole Folder is the default</b>, because the cost of the wrong answer is
/// asymmetric: a folder imported when one file was wanted is a folder the user deletes, while one file
/// imported when the board was wanted produces a plausible-looking one-layer board. And it offers
/// <b>Another Folder…</b>, which is how R-L4h-5's file-picker-first flow reaches the folder picker at
/// all — Avalonia's <c>StorageProvider</c> cannot return a file and a folder from one dialog.</para>
///
/// <para>Never shown when there is nothing to ask — see
/// <see cref="GerberImportEntry.FolderSurvey.NeedsPrompt"/>.</para>
///
/// <para>Returns the chosen <see cref="GerberImportScope"/> via <c>ShowDialog&lt;GerberImportScope?&gt;</c>,
/// or null on Cancel, which aborts and creates nothing.</para>
/// </summary>
public partial class GerberImportScopeDialog : Window
{
    public GerberImportScopeDialog() => InitializeComponent();

    public GerberImportScopeDialog(GerberImportEntry.FolderSurvey survey) : this()
        => MessageText.Text = survey.Question;

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnWholeFolderClick(object? sender, RoutedEventArgs e)
        => Close(GerberImportScope.EnclosingFolder);

    private void OnThisFileClick(object? sender, RoutedEventArgs e)
        => Close(GerberImportScope.ThisFileOnly);

    private void OnAnotherFolderClick(object? sender, RoutedEventArgs e)
        => Close(GerberImportScope.AnotherFolder);
}
