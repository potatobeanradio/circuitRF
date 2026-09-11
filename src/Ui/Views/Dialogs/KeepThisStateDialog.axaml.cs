using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Revision;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the designer decided.</summary>
/// <param name="Label">The optional line. Null when they typed none.</param>
/// <param name="Note">§5.12's longer note, or null. Null writes an entry byte-identical to the one
/// this recorded before the field existed.</param>
/// <param name="LeaveOut">Workspace-relative paths to leave out of this one.</param>
/// <param name="NeverInclude">Patterns to add to <c>.gitignore</c>.</param>
public sealed record KeepThisStateChoice(
    string?                Label,
    IReadOnlyList<string>  LeaveOut,
    IReadOnlyList<string>  NeverInclude,
    string?                Note = null);

/// <summary>
/// One row of the large-file guard — a file, or a whole pattern when the first recording into an
/// existing workspace has hundreds of them (R-rc5-15, R-rc5-17a).
/// </summary>
public sealed partial class LargeFileRow : ObservableObject
{
    /// <summary>
    /// <b>The three choices, and there is deliberately no fourth</b> (R-rc5-17). "Add it once, then
    /// ignore it" is not built, and the reason is mechanical rather than a matter of taste:
    /// <c>.gitignore</c> has no effect on a file that is already kept, so the option resolves to
    /// either changing nothing while telling the user they turned something off — §1.4's worst
    /// outcome — or leaving the history holding one stale version with the current one absent from
    /// every copy anyone else takes.
    /// </summary>
    public static readonly string[] ChoiceLabels =
    [
        "Include it — this is design input",
        "Leave it out this time",
        "Never include files like this",
    ];

    public LargeFileRow(string title, string detail, string pattern, IReadOnlyList<string> paths)
    {
        Title   = title;
        Detail  = detail;
        Pattern = pattern;
        Paths   = paths;
    }

    public string                Title   { get; }
    public string                Detail  { get; }
    public string                Pattern { get; }
    public IReadOnlyList<string> Paths   { get; }

    public IReadOnlyList<string> Choices => ChoiceLabels;

    [ObservableProperty] private int _choiceIndex;

    /// <summary>
    /// R-rc5-16. <b>The consequence of the third choice, at the point of choosing</b> — §10B requires
    /// that sentence to appear where the choice is made and not only in a manual, because a file
    /// matching that pattern is not in the history, and therefore not in a restore and not in a copy.
    /// </summary>
    public string Consequence => ChoiceIndex switch
    {
        0 => RestorePointMessages.IncludeConsequence,
        1 => RestorePointMessages.LeaveOutConsequence,
        _ => RestorePointMessages.NeverIncludeConsequence,
    };

    partial void OnChoiceIndexChanged(int value) => OnPropertyChanged(nameof(Consequence));

    public LargeFileChoice Choice => ChoiceIndex switch
    {
        0 => LargeFileChoice.Include,
        1 => LargeFileChoice.LeaveOutThisTime,
        _ => LargeFileChoice.NeverIncludeFilesLikeThis,
    };
}

/// <summary>
/// The explicit save-point (<c>docs/design/revision-control.md</c> §5.3's second boundary), and the
/// one interactive moment where §8.2's guard is asked (R-rc5-15, R-rc5-15a).
///
/// <para><b>It asks for one optional line</b>, because R-rc5-6b's reasoning applies here too: the
/// intent is the whole value of the entry, and an entry labelled only with a time says nothing that
/// every other entry does not already say. <b>And a longer note behind an expander</b> (§5.12), for
/// the reason <see cref="KeepThisVersionDialog"/>'s carries one — closed by default, because a
/// save-point is something a designer reaches for quickly and a paragraph is never its price.</para>
///
/// <para><b>The guard appears only when there is something to ask about</b>, so the ordinary
/// save-point is one field and a button. When the workspace has hundreds of large files — the first
/// recording into one with four years of accumulated output — <b>the rows are PATTERNS rather than
/// files</b> (R-rc5-17a): a dialog naming hundreds of files is not a dialog.</para>
/// </summary>
public partial class KeepThisStateDialog : Window
{
    /// <summary>
    /// Above this many files, the guard summarises by pattern instead of naming each one.
    ///
    /// <para>It is a READABILITY threshold, which is why it is a small number: the point at which a
    /// list stops being something a person reads and starts being something they dismiss.</para>
    /// </summary>
    public const int SummariseAbove = 8;

    private readonly ObservableCollection<LargeFileRow> _rows = [];

    public KeepThisStateDialog() => InitializeComponent();

    /// <summary>Fills the dialog in. An empty <paramref name="large"/> leaves the guard hidden.</summary>
    public void Present(IReadOnlyList<LargeFile> large, bool isFirstRecording)
    {
        _rows.Clear();

        if (large.Count > SummariseAbove || (isFirstRecording && large.Count > 1))
        {
            foreach (var group in LargeFileGuard.SummariseByPattern(large))
                _rows.Add(new LargeFileRow(
                    $"{group.Pattern} — {group.Count} file(s), {FormatSize(group.TotalBytes)}",
                    string.Join(", ", group.Examples.Take(3))
                        + (group.Examples.Count > 3 ? $", and {group.Examples.Count - 3} more" : ""),
                    group.Pattern,
                    group.Examples));
        }
        else
        {
            foreach (var file in large)
                _rows.Add(new LargeFileRow(
                    file.RelativePath, FormatSize(file.Bytes), file.Pattern, [file.RelativePath]));
        }

        LargeFilesList.ItemsSource = _rows;
        LargeFilesPanel.IsVisible  = _rows.Count > 0;

        LargeFilesHeading.Text = isFirstRecording
            ? "This workspace already holds files that have never been kept."
            : "Something new here is much larger than a design document.";
    }

    private void OnKeep(object? sender, RoutedEventArgs e)
    {
        List<string> leaveOut     = [];
        List<string> neverInclude = [];

        foreach (var row in _rows)
        {
            switch (row.Choice)
            {
                case LargeFileChoice.Include:
                    break;

                case LargeFileChoice.LeaveOutThisTime:
                    leaveOut.AddRange(row.Paths);
                    break;

                // Both: the pattern goes into .gitignore for next time, and the files are left out of
                // THIS one — because a pattern added now does not retroactively remove a file from
                // the recording that is about to happen.
                case LargeFileChoice.NeverIncludeFilesLikeThis:
                    neverInclude.Add(row.Pattern);
                    leaveOut.AddRange(row.Paths);
                    break;
            }
        }

        string? label = LabelBox.Text?.Trim();
        string  note  = MessageNotes.Text(NoteBox.Text);

        Close(new KeepThisStateChoice(
            string.IsNullOrEmpty(label) ? null : label, leaveOut, neverInclude,
            note.Length > 0 ? note : null));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private static string FormatSize(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
         : $"{bytes / 1024.0:0.#} kB";
}
