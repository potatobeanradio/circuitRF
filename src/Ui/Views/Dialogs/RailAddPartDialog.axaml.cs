using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Which parts are on this rail — <b>the gesture the parts pane has been promising since brief 26
/// and did not have</b> (field report, 2026-09-22).
/// </summary>
/// <remarks>
/// A designer placed two footprints on an imported board, gave them reference designators, and
/// reported twice in one session that they never appeared in the parts table. They had not: brief
/// 26's discovery was the only producer of a row, it needs a completed extraction, and it then only
/// offers a part it can PROVE bridges the rail and its reference — which on a Gerber set with no
/// netlist it can never prove about a hand-placed footprint. Meanwhile
/// <c>RailRfViewModel.PartsEmptyText</c> said rows "can still be typed", and there was nowhere to
/// type one.
///
/// <para><b>This is not a second discovery.</b> Nothing here classifies a board. A row added through
/// this dialog is a user's ASSERTION that the part is on this rail and carries
/// <c>RailPartOrigin.Typed</c>; a row brief 26 offers is a conclusion railRF reached and carries
/// <c>Artwork</c>. Confusing the two would put a provenance nobody stated beside numbers nobody
/// stated, which is the failure this whole tool is built to prevent.</para>
///
/// <para><b>The board contributes the NAMES and nothing else</b> — no capacitance, no ESR and no
/// connection type is derived from a land pattern, exactly as brief 26 refuses to derive them. What
/// the list buys over the text box beside it is that the designators are the board's own spelling
/// rather than one typed from memory.</para>
///
/// <para>Returns the designators to add, or null on cancel. An empty list is never returned — the
/// OK button refuses rather than closing on nothing, so a press that would do nothing says why.</para>
/// </remarks>
public partial class RailAddPartDialog : Window
{
    public RailAddPartDialog() => InitializeComponent();

    private IReadOnlyList<RailAddablePart> _offered = [];

    /// <param name="railName">The rail the rows land on — NAMED, because a window with several
    /// rails has a selector somewhere else on it and "add a part" with no rail in the sentence is a
    /// gesture nobody can check before pressing it.</param>
    /// <param name="offered">Designators the board carries that this rail does not.</param>
    /// <param name="hasBoard">Whether a board is loaded at all, which is what separates "this board
    /// places nothing else" from "there is no board to place anything".</param>
    public RailAddPartDialog(
        string railName,
        IReadOnlyList<RailAddablePart> offered,
        bool hasBoard) : this()
    {
        _offered = offered;

        PromptLabel.Text = $"Add parts to rail “{railName}”.";

        PlacedList.ItemsSource = offered;

        PlacedNote.Text =
            offered.Count > 0
                ? $"{offered.Count} part(s) are placed on the board and are not on this rail yet. "
                + "Pick the ones that sit between this rail and its reference — railRF does not "
                + "decide that for you here, and nothing is read off the land pattern."
            : hasBoard
                ? "Every designator the board places is already on this rail, so there is nothing "
                + "to pick. Type one below for a part the artwork does not carry."
                : "No board is loaded, so there are no placed designators to pick from. A part row "
                + "can still be typed — artwork is optional, and the numbers come from the part "
                + "library either way.";

        Opened += (_, _) =>
        {
            if (offered.Count > 0) PlacedList.Focus();
            else TypedBox.Focus();
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // THE RECORDS THEMSELVES, never the rendered label — see the list's own note in the XAML.
        foreach (var part in PlacedList.SelectedItems?
                     .OfType<RailAddablePart>() ?? [])
            if (seen.Add(part.Refdes)) chosen.Add(part.Refdes);

        foreach (string typed in (TypedBox.Text ?? "").Split(
                     [',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(typed)) chosen.Add(typed);

        // R-rail22-2a's rule, one pane down: no silent return from a visible control. The dialog
        // does not close on nothing chosen — the answer is one control away.
        if (chosen.Count == 0)
        {
            PlacedNote.Text = "Nothing is chosen. Pick one or more of the placed parts above, or "
                            + "type a reference designator below.";
            if (_offered.Count > 0) PlacedList.Focus(); else TypedBox.Focus();
            return;
        }

        Close(chosen);
    }
}
