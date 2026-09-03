using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the user chose for one cell arriving from another workspace.</summary>
/// <param name="Reference">True to reference the cell where it is; false to copy it in.</param>
/// <param name="SubCells">Only meaningful when copying — a referenced cell's sub-cells are ALWAYS
/// by reference (MW2 R-mw2-17), which is why the nested choice is disabled under Reference rather
/// than offering a third combination that does not exist.</param>
public sealed record AddCellChoice(bool Reference, SubCellMode SubCells);

/// <summary>
/// The prompt MW3 §1 specifies: a cell has arrived from another workspace, and the receiving
/// workspace asks whether to take a copy of it or to reference it where it is.
///
/// <para><b>R-mw3-1 — the sub-cell choice is nested under Copy and disabled under Reference</b>,
/// because a referenced cell's sub-cells are always by reference. Offering the fourth combination
/// would imply a mode that does not exist.</para>
///
/// <para><b>R-mw3-2 — the last choice is remembered for the SESSION and pre-selected</b>, so moving
/// six cells is six confirmations rather than six decisions. Deliberately NOT persisted across
/// launches: the right answer depends on what the user is doing that day, and a silently remembered
/// "Reference" would be a nasty surprise months later.</para>
/// </summary>
public partial class AddCellToWorkspaceDialog : Window
{
    // Session memory (R-mw3-2). Static, never written to AppPreferences.
    private static bool        _lastReference;
    private static SubCellMode _lastSubCells = SubCellMode.Copy;

    private readonly bool _referenceAllowed;

    public AddCellToWorkspaceDialog() : this("Cell", "Source", "This workspace", null, [], false) { }

    /// <param name="cellName">The cell being added.</param>
    /// <param name="sourceWorkspaceName">The workspace it lives in — "(no workspace)" when it is in none.</param>
    /// <param name="destWorkspaceName">The workspace receiving it.</param>
    /// <param name="referenceRefusal">MW2 R-mw2-7's sentence when the two technologies differ, which
    /// makes BOTH Reference and "keep sub-cells referenced" unavailable; null when they are allowed.</param>
    /// <param name="unimportedKits">Kits the copied cells place that the destination has not imported
    /// (R-mw3-8) — stated before the copy, because a <c>pdk://</c> reference is not rewritten and a
    /// cell full of pin-less placeholders is the outcome this warning exists to prevent.</param>
    /// <param name="hasSubCells">Whether the cell places any cell of its own workspace. When it does
    /// not, the nested choice changes nothing and is hidden rather than shown inert.</param>
    public AddCellToWorkspaceDialog(
        string cellName, string sourceWorkspaceName, string destWorkspaceName,
        string? referenceRefusal, IReadOnlyList<string> unimportedKits, bool hasSubCells)
    {
        InitializeComponent();

        _referenceAllowed = referenceRefusal is null;

        HeaderText.Text = $"{cellName}   →   {destWorkspaceName}";

        CopyRadio.Content      = $"Copy the cell into {destWorkspaceName}";
        SubRefRadio.Content    = $"Keep them referenced in {sourceWorkspaceName}";
        ReferenceRadio.Content = $"Reference {sourceWorkspaceName}'s cell from {destWorkspaceName}";
        ReferenceNoteText.Text =
            $"Adds \"{sourceWorkspaceName}\" to {destWorkspaceName}'s referenced workspaces.";

        SubCellPanel.IsVisible = hasSubCells;

        if (!_referenceAllowed)
        {
            ReferenceRadio.IsEnabled     = false;
            SubRefRadio.IsEnabled        = false;
            ReferenceNoteText.IsVisible  = false;   // the refusal replaces it rather than sitting under it
            RefusalText.Text             = referenceRefusal;
            RefusalText.IsVisible        = true;
            SubRefRefusalText.Text   = "Unavailable for the same reason.";
            SubRefRefusalText.IsVisible = hasSubCells;
        }

        if (unimportedKits.Count > 0)
        {
            string kits = string.Join(", ", unimportedKits);
            string s    = unimportedKits.Count == 1 ? "kit" : "kits";
            KitWarningText.Text =
                $"{cellName} uses parts from {s} {kits}, which {destWorkspaceName} has not imported. "
              + "Copy it anyway and the parts show as unresolved until you import the kit"
              + (_referenceAllowed ? ", or reference the cell instead." : ".");
            KitWarningText.IsVisible = true;
        }

        // R-mw3-2's pre-selection, clamped to what is actually offered here.
        bool reference = _lastReference && _referenceAllowed;
        ReferenceRadio.IsChecked = reference;
        CopyRadio.IsChecked      = !reference;

        bool subRef = _lastSubCells == SubCellMode.KeepReferenced && _referenceAllowed;
        SubRefRadio.IsChecked  = subRef;
        SubCopyRadio.IsChecked = !subRef;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        bool reference = ReferenceRadio.IsChecked == true && _referenceAllowed;
        var  subCells  = SubRefRadio.IsChecked == true && _referenceAllowed
            ? SubCellMode.KeepReferenced
            : SubCellMode.Copy;

        _lastReference = reference;
        _lastSubCells  = subCells;

        Close(new AddCellChoice(reference, subCells));
    }
}
