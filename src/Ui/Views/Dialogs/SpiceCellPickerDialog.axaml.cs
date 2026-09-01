using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Picks which <c>.model</c> card or <c>.subckt</c> definition in a file to import.
///
/// <para><b>Cards and subcircuits are listed together, not on two tabs.</b> A supplier's file
/// routinely holds both — the subcircuit that is the part, and the cards that are its transistors —
/// and a user opening it has "the file for this part", not a classification. Splitting them would
/// ask them to make a distinction before they can see what is there.</para>
///
/// <para><b>Everything is listed, including what circuitRF cannot build</b>, each with the reason it
/// was refused. Filtering them out would answer "why is my VDMOS not offered?" with an absence — and
/// the reason is precisely the thing the user needs, since it says whether the fix is a different
/// file or a feature circuitRF does not have. A refused row cannot be selected; it is there to be
/// read.</para>
///
/// <para>Mirrors <see cref="PCellGeneratorPickerDialog"/>'s return-or-null contract.</para>
/// </summary>
public partial class SpiceCellPickerDialog : Window
{
    /// <summary>One offered definition. <see cref="Detail"/> is either what would be built or why not.</summary>
    private sealed record Row(SpiceCellCandidate Candidate)
    {
        public string Name      => Candidate.Name;
        public string TypeLabel => Candidate.TypeLabel;
        public string Detail    => Candidate.Detail;
        public bool   Supported => Candidate.IsSupported;

        /// <summary>Refused rows read as unavailable without needing a second colour to explain it.</summary>
        public double NameOpacity => Supported ? 1.0 : 0.55;
    }

    public SpiceCellPickerDialog() => InitializeComponent();

    public SpiceCellPickerDialog(string fileName, IReadOnlyList<SpiceCellCandidate> candidates) : this()
    {
        var rows = candidates.Select(c => new Row(c)).ToList();
        int supported = rows.Count(r => r.Supported);
        int subcircuits = rows.Count(r => r.Candidate.Subcircuit is not null);

        Intro.Text =
            $"{fileName} holds {rows.Count} definition(s) — {subcircuits} subcircuit(s) and "
            + $"{rows.Count - subcircuits} model card(s) — of which circuitRF can build {supported}. "
            + "The chosen one becomes a new cell in this workspace: a subcircuit becomes its own "
            + "netlist with a generic box for a symbol, a model card becomes the native component "
            + "carrying its parameters, and either way the pins are already wired.";

        ChoiceList.ItemsSource  = rows;
        ChoiceList.SelectedItem = rows.FirstOrDefault(r => r.Supported);

        // Something the user cannot import must not leave Import enabled — the click would otherwise
        // have to fail with a message saying what the row already says.
        ChoiceList.SelectionChanged += (_, _) => OkButton.IsEnabled = Selected() is { Supported: true };
        OkButton.IsEnabled = ChoiceList.SelectedItem is Row { Supported: true };

        if (supported < rows.Count)
        {
            Footnote.Text =
                "The greyed rows cannot be imported; each says why. A card names a device circuitRF "
                + "has no model for; a subcircuit holds something it could not read, or calls one "
                + "that does.";
            Footnote.IsVisible = true;
        }

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click     += (_, _) => { if (Selected() is { Supported: true } r) Close(r); };
        ChoiceList.DoubleTapped += (_, _) => { if (Selected() is { Supported: true } r) Close(r); };
    }

    private Row? Selected() => ChoiceList.SelectedItem as Row;

    /// <summary>Shows the picker and returns the chosen definition, or null on cancel.</summary>
    public static async Task<SpiceCellCandidate?> ShowAsync(
        Window? owner, string fileName, IReadOnlyList<SpiceCellCandidate> candidates)
    {
        var dialog = new SpiceCellPickerDialog(fileName, candidates);
        var chosen = owner is null
            ? await dialog.ShowDialog<object?>(new Window())
            : await dialog.ShowDialog<object?>(owner);

        return chosen is Row row ? row.Candidate : null;
    }
}
