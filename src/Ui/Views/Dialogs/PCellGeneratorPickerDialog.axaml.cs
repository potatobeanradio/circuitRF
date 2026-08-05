using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Picks a parametric cell to place, from EVERY registered generator — circuitRF's own built-ins and
/// whatever a referenced kit contributes, in one list.
///
/// <para><b>Why this exists at all.</b> Placement used to be keyed on <c>SymbolKind</c>, which made a
/// kit's cells structurally unplaceable: they are discovered at run time and there is no enum member
/// to give them. A kit could be referenced, its cells resolved, its geometry generated in tests — and
/// still be unreachable from the application. This is the surface that closes that.</para>
///
/// <para><b>Deliberately NOT the schematic Library Palette.</b> That palette is built from
/// <c>ComponentTypeRegistry</c> and every tile carries a <c>SymbolKind</c> and a symbol glyph. A
/// vendor layout cell has neither, and giving the palette a second kind of tile — with its own
/// glyph story, its own drag payload and its own placement path — would be a large change to a
/// schematic-facing surface for something that is a layout concern. A picker in the layout editor,
/// mirroring the Instance-place picker beside it, says the same thing with far less machinery.</para>
/// </summary>
public partial class PCellGeneratorPickerDialog : Window
{
    /// <summary>One offered generator. <see cref="Summary"/> is what its parameters WOULD be, so a
    /// user can tell two similarly-named cells apart before placing either.</summary>
    private sealed record Row(string Id, IReadOnlyDictionary<string, PCellValue> Parameters)
    {
        public string Summary => Parameters.Count == 0
            ? "no parameters"
            : string.Join(", ", Parameters.OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
                                          .Take(4)
                                          .Select(kv => $"{kv.Key}={kv.Value}"))
              + (Parameters.Count > 4 ? ", …" : "");
    }

    public PCellGeneratorPickerDialog() => InitializeComponent();

    public PCellGeneratorPickerDialog(
        IReadOnlyList<(string Id, IReadOnlyDictionary<string, PCellValue> Parameters)> generators) : this()
    {
        var rows = generators.Select(g => new Row(g.Id, g.Parameters)).ToList();
        ChoiceList.ItemsSource = rows;
        if (rows.Count > 0) ChoiceList.SelectedIndex = 0;

        // Empty is a real state, not an error: a workspace with no kit referenced and no built-in
        // registered has nothing to offer, and saying so plainly beats an empty box.
        ChoiceList.IsVisible = rows.Count > 0;
        EmptyText.IsVisible  = rows.Count == 0;
        OkButton.IsEnabled   = rows.Count > 0;

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click     += (_, _) => Close(Selected());
        ChoiceList.DoubleTapped += (_, _) => { if (Selected() is { } r) Close(r); };
    }

    private object? Selected() => ChoiceList.SelectedItem;

    /// <summary>
    /// Shows the picker and returns the chosen generator's id and the parameters to place it at, or
    /// null on cancel. Mirrors <see cref="InstanceCellPickerDialog"/>'s own return-or-null contract.
    /// </summary>
    public static async Task<(string Id, IReadOnlyDictionary<string, PCellValue> Parameters)?> ShowAsync(
        Window? owner,
        IReadOnlyList<(string Id, IReadOnlyDictionary<string, PCellValue> Parameters)> generators)
    {
        var dialog = new PCellGeneratorPickerDialog(generators);
        var chosen = owner is null
            ? await dialog.ShowDialog<object?>(new Window())
            : await dialog.ShowDialog<object?>(owner);

        return chosen is Row row ? (row.Id, row.Parameters) : null;
    }
}
