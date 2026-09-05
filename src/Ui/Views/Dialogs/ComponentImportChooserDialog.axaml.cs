using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The ranked list of what a scanned folder holds, and which of it can be imported (R-PL1-4).
///
/// <para><see cref="ComponentFolderScan"/> walks the folder, classifies every file by content
/// (R-PL1-28) and returns candidates ranked by completeness first and reader confidence second. This
/// dialog shows that list with the top row preselected, and beneath it one line naming each category
/// of file that was not read, with a count.</para>
///
/// <para>Not shown when the scan found exactly one single-file candidate — there is nothing to choose
/// among. Returns the chosen <see cref="ComponentCandidate"/> via
/// <c>ShowDialog&lt;ComponentCandidate?&gt;</c>, or null on Cancel, which creates nothing.</para>
/// </summary>
public partial class ComponentImportChooserDialog : Window
{
    public ComponentImportChooserDialog()
    {
        InitializeComponent();
        Opened += (_, _) => ImportButton.Focus();
    }

    public ComponentImportChooserDialog(ComponentScanResult scan) : this()
    {
        HeadingText.Text =
            $"This folder holds {Formats(scan)} component format(s). " +
            $"{scan.Candidates.Count} can be imported:";

        CandidateList.ItemsSource = scan.Candidates;
        if (scan.Candidates.Count > 0) CandidateList.SelectedIndex = 0;   // preselect the top row

        SkippedText.Text = scan.SkippedSummary.Count == 0
            ? $"{scan.FilesScanned:N0} file(s) scanned; nothing was skipped."
            : "Not read: " + string.Join(", ", scan.SkippedSummary) + ".";
    }

    /// <summary>How many distinct formats the folder holds at all: the readable candidates plus every
    /// category that was skipped. This is the denominator in the heading.</summary>
    private static int Formats(ComponentScanResult scan)
        => scan.Candidates.Count + scan.SkippedSummary.Count;

    /// <summary>True when Another Folder… was pressed. Avalonia's <c>StorageProvider</c> exposes
    /// <c>OpenFilePickerAsync</c> and <c>OpenFolderPickerAsync</c> as separate calls and one dialog
    /// cannot return both, so this is how the caller knows to open the folder picker — the same shape
    /// R-L4h-5 uses for Gerber.</summary>
    public bool ChooseAnotherFolder { get; private set; }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnAnotherFolderClick(object? sender, RoutedEventArgs e)
    {
        ChooseAnotherFolder = true;
        Close(null);
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
        => Close(CandidateList.SelectedItem as ComponentCandidate);
}
