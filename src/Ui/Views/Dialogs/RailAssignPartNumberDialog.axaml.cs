using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-rail27-3a — <b>which part are these?</b>
/// </summary>
/// <remarks>
/// <b>It assigns a part NUMBER and nothing else, and the distinction is the whole argument.</b>
/// <c>RailPartRowViewModel</c> is read-only deliberately — "a row that could be edited here would be
/// a second place the same number lives" — and that rule is about the MODEL: capacitance, ESR, f₀,
/// which belong to the part library. A part number is not a model value. It is the row's own field
/// on the document (<c>RailPart.PartNumber</c>), it is what the library is keyed BY, and choosing it
/// is choosing which library row applies.
///
/// <para>Returns the number to assign, or null on cancel. An EMPTY answer is a real one — it clears
/// the number, which is the state a discovered row starts in — so the result is distinguished by
/// null and not by emptiness.</para>
/// </remarks>
public partial class RailAssignPartNumberDialog : Window
{
    public RailAssignPartNumberDialog() => InitializeComponent();

    /// <param name="refdeses">The rows this will be written on — NAMED, because "assign to the
    /// selection" with the selection out of view is a gesture nobody can check before pressing it.</param>
    /// <param name="known">Part numbers the library and this document already carry.</param>
    /// <param name="initial">What the primary row carries now, so re-opening shows the current
    /// answer rather than a blank field.</param>
    public RailAssignPartNumberDialog(
        IReadOnlyList<string> refdeses, IReadOnlyList<string> known, string? initial = null) : this()
    {
        PromptLabel.Text = refdeses.Count == 1
            ? $"Part number for {refdeses[0]}:"
            : $"Part number for {refdeses.Count} parts — {string.Join(", ", refdeses)}:";

        PartNumberBox.ItemsSource = known;
        if (initial is { Length: > 0 }) PartNumberBox.SelectedItem = initial;

        LibraryNote.Text = known.Count == 0
            ? "Nothing in this document or its part library states a part number yet, so type one. "
            + "Create a part library afterwards to give it a capacitance and a self-resonance — "
            + "until then the row is listed as unresolved and contributes nothing to the curve."
            : $"{known.Count} part number(s) are already known here — pick one, or type a new one.";

        Opened += (_, _) => PartNumberBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
        // The editable combo's own text is the answer — a typed number never becomes a SelectedItem,
        // and reading the selection alone would silently discard exactly the case this dialog exists
        // for. Trimmed here so the view model's "did this change anything" test is on one spelling.
        => Close((PartNumberBox.Text ?? "").Trim());
}
