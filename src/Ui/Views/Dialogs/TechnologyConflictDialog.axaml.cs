using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Which of §5C.2a/R47e's remedies the user picked.</summary>
public enum TechnologyRemedy
{
    /// <summary>Copy their .ctech into this workspace's tech/ and write it as THIS layout's TechRef.
    /// The narrowest correct fix, and the default: it changes one document.</summary>
    UseForThisLayout,

    /// <summary>The same copy, written to .cws DefaultTechRef — which re-points every layout in the
    /// workspace that has not deviated (R47f states how many before it acts).</summary>
    UseForThisWorkspace,

    /// <summary>Copy the cell into this workspace instead of referencing it, and place the copy.</summary>
    CopyCellIn,
}

/// <summary>
/// §5C.2a/R47e — the refusal, carrying its own remedies.
///
/// <para><b>Why this dialog exists at all.</b> R47 refuses correctly and then left the user to repair
/// it by hand: the sentence named two technologies and two workspaces and ended in prose with nothing
/// to click. The repair people actually reached for — copy the other workspace's <c>.ctech</c> in and
/// make it the default — works, and silently re-points every other layout in the workspace (R47f).
/// Offering the narrow fix first is the whole point.</para>
///
/// <para><b>Nothing here is remembered across placements.</b> Which technology a layout should draw
/// with is not a preference; a silently reused answer would retarget a second document without
/// asking.</para>
/// </summary>
public partial class TechnologyConflictDialog : Window
{
    public TechnologyConflictDialog() : this("cell", "their technology", "this workspace", "") { }

    /// <param name="cellName">The cell being placed.</param>
    /// <param name="theirTechName">The technology it is drawn with, already rendered as name plus
    /// file name by <c>ExternalRefCheck.TechnologyDisplay</c> — a workspace can hold several files
    /// claiming one name, and which FILE is meant is the question these choices turn on.</param>
    /// <param name="workspaceName">The receiving workspace.</param>
    /// <param name="detail">The gate's own sentence — which layer disagrees, and how. Shown in full
    /// rather than summarised: it names the one key the user has to look at.</param>
    public TechnologyConflictDialog(
        string cellName, string theirTechName, string workspaceName, string detail)
    {
        InitializeComponent();

        HeaderText.Text = $"{cellName} is drawn with a different technology";
        DetailText.Text = detail;

        UseForLayoutRadio.Content = $"Use {theirTechName} for this layout";
        UseForLayoutNote.Text =
            $"Copies it into {workspaceName} and points this layout at it. Nothing else in "
          + $"{workspaceName} changes.";

        UseForWorkspaceRadio.Content = $"Use {theirTechName} for all of {workspaceName}";
        UseForWorkspaceNote.Text =
            "Makes it the workspace default, which every layout that has not chosen its own "
          + "technology draws with. You will be told how many that is before it happens.";

        CopyCellRadio.Content = $"Copy {cellName} into {workspaceName} instead";
        CopyCellNote.Text =
            "Takes a copy rather than a reference, so it stops tracking the original. You choose "
          + "whether its technology comes with it.";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(
        UseForWorkspaceRadio.IsChecked == true ? TechnologyRemedy.UseForThisWorkspace
      : CopyCellRadio.IsChecked        == true ? TechnologyRemedy.CopyCellIn
      : (TechnologyRemedy?)TechnologyRemedy.UseForThisLayout);
}
