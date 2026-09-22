using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What this dialog settled — the column names to read the file with, and the unit.</summary>
/// <param name="Columns">One name per column, in column order, in the spelling
/// <c>PlacementFile.Read</c>'s <c>columns</c> parameter takes. An empty string is a column that is
/// not read.</param>
/// <param name="Units">The unit the coordinates are in, as a user stated it.</param>
public sealed record RailPlacementColumnChoice(IReadOnlyList<string> Columns, LayoutUnit Units);

/// <summary>
/// Naming the columns of a placement table that has no header row — <b>the GUI half of a refusal
/// that until now named a command-line flag</b> (field report, 2026-09-22).
/// </summary>
/// <remarks>
/// <c>PlacementFile</c> has refused a headerless file since it was written, and the refusal is
/// right: column order is not a standard, and a positional reading puts the rotation in the Y column
/// silently. What the sentence then said was "Name them with --columns, or state --from to read it
/// as something else" — and a designer met that in a window, where there is no <c>--columns</c>. He
/// had an ordinary headerless placement dump and railRF's only route was a flag he could not reach.
///
/// <para><b>Nothing here is applied without being shown.</b> <c>PlacementColumns.Infer</c> proposes
/// an assignment from the VALUES — there is no header to read — and this puts each proposal directly
/// over the column it claims, above the file's own first rows. A guess and a declaration must never
/// read the same, so the proposal is labelled as one and the evidence for each claim is on the face.
///
/// <para><b>The unit is asked and never inferred.</b> A headerless file declares none, and
/// millimetres and mils differ by a factor of 25.4: a file read at the wrong one still lands every
/// part inside a plausible-looking box, which is the silent failure every reader in this folder is
/// built against.</para>
///
/// <para>Returns null when the user chooses to import the artwork without the placement file, which
/// is an ordinary outcome and not a cancel of the import.</para>
/// </remarks>
public partial class RailPlacementColumnsDialog : Window
{
    public RailPlacementColumnsDialog() => InitializeComponent();

    /// <summary>How many of the file's own rows are shown. Enough to see a column's character,
    /// few enough that the dialog opens instantly on a 50,000-row dump.</summary>
    private const int PreviewRows = 10;

    private static readonly (LayoutUnit Unit, string Label)[] Units =
    [
        (LayoutUnit.Mm,  "millimetres (mm)"),
        (LayoutUnit.Um,  "micrometres (µm)"),
        (LayoutUnit.Mil, "mils (thou)"),
        (LayoutUnit.Inch, "inches (in)"),
    ];

    private readonly List<ComboBox> _roleCombos = [];

    /// <param name="fileName">The file this is about, named so the dialog is checkable against
    /// what was chosen.</param>
    /// <param name="table">The file as parsed. Its own rows are what the proposal is shown over.</param>
    public RailPlacementColumnsDialog(string fileName, DelimitedTable table) : this()
    {
        PromptLabel.Text = $"“{fileName}” — what are its columns?";

        UnitCombo.ItemsSource = Units.Select(u => u.Label).ToList();
        UnitCombo.SelectedIndex = 0;

        var inference = PlacementColumns.Infer(table);
        BuildPreview(table, inference);

        EvidenceText.Text = inference.Evidence.Count == 0
            ? "railRF recognised nothing in this file's values, so every column starts unassigned. "
            + "Reference, X and Y are the three it cannot be read without."
            : "What that proposal was read on: " + string.Join("  ", inference.Evidence);
    }

    /// <summary>
    /// The role pickers over the file's own first rows.
    /// </summary>
    /// <remarks>
    /// Built in code because the column count is the FILE's — there is no fixed shape to declare in
    /// XAML, and a template over a row view model would be a second model of a table this dialog
    /// holds for as long as it is open.
    /// </remarks>
    private void BuildPreview(DelimitedTable table, PlacementColumnInference inference)
    {
        int columns = Math.Max(inference.Roles.Count, table.Rows.Count > 0 ? table.Rows[0].Fields.Count : 0);
        if (columns == 0) return;

        for (int c = 0; c < columns; c++)
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        int rows = Math.Min(PreviewRows, table.Rows.Count);
        for (int r = 0; r <= rows; r++)
            PreviewGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int c = 0; c < columns; c++)
        {
            var combo = new ComboBox
            {
                ItemsSource = PlacementColumns.All.Select(PlacementColumns.LabelFor).ToList(),
                SelectedIndex = Array.IndexOf(
                    PlacementColumns.All,
                    c < inference.Roles.Count ? inference.Roles[c] : PlacementColumnRole.Ignore),
                FontSize = 11,
                MinWidth = 104,
                Margin = new Avalonia.Thickness(2, 0, 2, 4),
            };
            combo.SelectionChanged += (_, _) => ClearRefusal();

            _roleCombos.Add(combo);
            Grid.SetColumn(combo, c);
            Grid.SetRow(combo, 0);
            PreviewGrid.Children.Add(combo);

            for (int r = 0; r < rows; r++)
            {
                var fields = table.Rows[r].Fields;
                PreviewGrid.Children.Add(Cell(c < fields.Count ? fields[c] : "", c, r + 1));
            }
        }
    }

    private static TextBlock Cell(string text, int column, int row)
    {
        var block = new TextBlock
        {
            Text = (text ?? "").Trim(),
            FontSize = 11,
            FontFamily = new FontFamily("monospace"),
            Opacity = 0.85,
            Margin = new Avalonia.Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(block, column);
        Grid.SetRow(block, row);
        return block;
    }

    private void ClearRefusal() => RefusalText.IsVisible = false;

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var roles = _roleCombos
            .Select(c => c.SelectedIndex >= 0 ? PlacementColumns.All[c.SelectedIndex]
                                              : PlacementColumnRole.Ignore)
            .ToList();

        // ── THE THREE THE READER CANNOT DO WITHOUT, AND ONE OF EACH ───────────────────────────
        //
        // Both halves are refusals rather than a silent first-wins, and the second one matters
        // more than it looks: PlacementFile matches a role by NAME, so two columns both called "x"
        // resolve to whichever comes first and the other is simply not read. That is the same
        // class of quiet wrong answer the whole dialog exists to prevent.
        foreach (var required in new[]
                 {
                     PlacementColumnRole.Refdes, PlacementColumnRole.X, PlacementColumnRole.Y,
                 })
            if (!roles.Contains(required))
            {
                Refuse($"No column is named {PlacementColumns.LabelFor(required)}, and a placement "
                     + "table cannot be read without a reference, an X and a Y.");
                return;
            }

        foreach (var role in PlacementColumns.All)
        {
            if (role == PlacementColumnRole.Ignore) continue;
            if (roles.Count(r => r == role) <= 1) continue;

            Refuse($"Two columns are both named {PlacementColumns.LabelFor(role)}. Only the first "
                 + "would be read and the other would be dropped in silence, so railRF will not "
                 + "take it — set one of them to “(not read)”.");
            return;
        }

        Close(new RailPlacementColumnChoice(
            PlacementColumns.ToColumns(roles),
            Units[Math.Max(0, UnitCombo.SelectedIndex)].Unit));
    }

    private void Refuse(string sentence)
    {
        RefusalText.Text = sentence;
        RefusalText.IsVisible = true;
    }
}
