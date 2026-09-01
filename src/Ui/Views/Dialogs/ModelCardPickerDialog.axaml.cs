using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Picks which <c>.model</c> card in a file to import.
///
/// <para><b>Every card is listed, including the ones circuitRF cannot build</b>, each with the
/// reason it was refused. Filtering them out would answer "why is my VDMOS not offered?" with an
/// absence — and the reason is precisely the thing the user needs, since it says whether the fix is
/// a different file or a feature circuitRF does not have. A refused row cannot be selected for
/// import; it is there to be read.</para>
///
/// <para>Mirrors <see cref="PCellGeneratorPickerDialog"/>'s return-or-null contract.</para>
/// </summary>
public partial class ModelCardPickerDialog : Window
{
    /// <summary>One offered card. <see cref="Detail"/> is either what would be built or why not.</summary>
    private sealed record Row(ModelCardTranslation Translation)
    {
        public string Name      => Translation.Card.Name;
        public string TypeLabel => "." + Translation.Card.ModelType.Trim().ToUpperInvariant();
        public bool   Supported => Translation.IsSupported;

        /// <summary>Refused rows read as unavailable without needing a second colour to explain it.</summary>
        public double NameOpacity => Supported ? 1.0 : 0.55;

        public string Detail => Translation.Binding is { } b
            ? $"{b.Parameters.Count} parameter(s)"
              + (b.Unmapped.Count > 0 ? $" — {b.Unmapped.Count} not carried: {string.Join(", ", b.Unmapped)}" : "")
            : Translation.Refusal ?? "";
    }

    public ModelCardPickerDialog() => InitializeComponent();

    public ModelCardPickerDialog(string fileName, IReadOnlyList<ModelCardTranslation> translations) : this()
    {
        var rows = translations.Select(t => new Row(t)).ToList();
        int supported = rows.Count(r => r.Supported);

        Intro.Text =
            $"{fileName} holds {rows.Count} model card(s), {supported} of which circuitRF can build. "
            + "The chosen card becomes a new cell in this workspace — a schematic holding the native "
            + "component with these parameters and its pins already wired, plus an editable copy of "
            + "that component's symbol.";

        ChoiceList.ItemsSource = rows;
        ChoiceList.SelectedItem = rows.FirstOrDefault(r => r.Supported);

        // A card the user cannot import must not leave Import enabled — the click would otherwise
        // have to fail with a message saying what the row already says.
        ChoiceList.SelectionChanged += (_, _) => OkButton.IsEnabled = Selected() is { Supported: true };
        OkButton.IsEnabled = ChoiceList.SelectedItem is Row { Supported: true };

        if (supported < rows.Count)
        {
            Footnote.Text =
                "The greyed cards name a device circuitRF has no model for; each row says which. "
                + "They cannot be imported.";
            Footnote.IsVisible = true;
        }

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click     += (_, _) => { if (Selected() is { Supported: true } r) Close(r); };
        ChoiceList.DoubleTapped += (_, _) => { if (Selected() is { Supported: true } r) Close(r); };
    }

    private Row? Selected() => ChoiceList.SelectedItem as Row;

    /// <summary>Shows the picker and returns the chosen card, or null on cancel.</summary>
    public static async Task<ModelCardTranslation?> ShowAsync(
        Window? owner, string fileName, IReadOnlyList<ModelCardTranslation> translations)
    {
        var dialog = new ModelCardPickerDialog(fileName, translations);
        var chosen = owner is null
            ? await dialog.ShowDialog<object?>(new Window())
            : await dialog.ShowDialog<object?>(owner);

        return chosen is Row row ? row.Translation : null;
    }
}
