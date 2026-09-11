using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Revision;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the designer decided.</summary>
/// <param name="Title">The line they wrote. Blank is allowed and lists as
/// <see cref="CommitMessage.UntitledVersion"/> — never as a bare time.</param>
/// <param name="Note">§5.12's longer note, or null when they wrote none — which is most versions, and
/// writes a message byte-identical to the one this recorded before the field existed.</param>
public sealed record KeepThisVersionChoice(string? Title, string? Note = null);

/// <summary>
/// <b>The explicit commit</b> (<c>docs/design/revision-control.md</c> §5.2, §5.5, §8.3;
/// RC-7 R-rc7-1, R-rc7-5, R-rc7-6, R-rc7-21).
///
/// <para><b>One field in front, and a second behind an expander</b> (§5.12). The title is what a
/// designer scans a list for six weeks later; <i>why</i> is the part the files cannot recover, and it
/// does not fit on one line. Most versions never need the second, which is why it is closed by
/// default — a field in front of everybody makes the one that matters harder to reach.</para>
///
/// <para>The large-file guard is deliberately
/// not repeated here: it belongs to the boundary that first records a file, and by the time a designer
/// keeps a version the question has been asked at a save-point or answered in <c>.gitignore</c>.
/// Asking twice would leave a file out that they had just said to include.</para>
///
/// <para><b>Two things this dialog says that no other one does.</b> It tells the designer, before they
/// press the button, that this version will record having been brought back from an earlier state
/// (R-rc7-6) — because discovering that in the list afterwards is how a history comes to read as a
/// change of mind. And it carries §8.3's sentence (R-rc7-21): rewriting a history is possible, it is
/// not circuitRF's to do, and here is why. <b>Stated, never offered</b> — there is no button, and
/// there is deliberately no code path behind one.</para>
/// </summary>
public partial class KeepThisVersionDialog : Window
{
    public KeepThisVersionDialog()
    {
        InitializeComponent();

        // RC-11 R-rc11-15. The escape-hatch paragraph keeps its subject — a FILE in the history that
        // must not be there — and stops claiming the part §5.11 now governs, which is the line a
        // person wrote. The second sentence is what makes the first read as the narrow rule it is:
        // without it, a designer who came here worried about a careless title would read "circuitRF
        // never alters what was recorded" and stop looking.
        RewritingText.Text = HistoryMessages.RewritingIsYoursToDo
                           + "\n\n"
                           + HistoryMessages.TitlesAreYoursToCorrect;
    }

    /// <summary>Fills the dialog in. A null <paramref name="restoredFrom"/> leaves the notice
    /// hidden, which is the ordinary case.</summary>
    public void Present(RestoredFrom? restoredFrom)
    {
        RestoredPanel.IsVisible = restoredFrom is not null;
        if (restoredFrom is { } from) RestoredText.Text = CommitMessage.RestoredLine(from);
    }

    private void OnKeep(object? sender, RoutedEventArgs e)
    {
        string? title = TitleBox.Text?.Trim();
        string  note  = MessageNotes.Text(NoteBox.Text);

        Close(new KeepThisVersionChoice(string.IsNullOrEmpty(title) ? null : title,
                                        note.Length > 0 ? note : null));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
