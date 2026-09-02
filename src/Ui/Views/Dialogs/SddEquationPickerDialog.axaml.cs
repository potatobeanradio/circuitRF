using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Picks one equation slot from the set THIS SDD can still use, so a slot is added by name from the
/// component rather than typed from memory of the notation.
///
/// <para><b>Why the SDD does not use the generic "+" (owner report, 2026-09-02).</b> That button adds
/// <c>Name[n]</c> for an ever-increasing n. An SDD's slots are two-dimensional (<c>I[p,w]</c> — port
/// and weighting index) and bounded by the port count, so the generic parser could read neither
/// dimension: it saw the seeded <c>I[1,0]</c> as unindexed, offered <c>I[1]</c> — which is valid
/// sugar for the SAME slot and therefore silently replaced the seeded equation — and a few presses
/// later offered <c>I[3]</c> on a 2-port, which the factory refuses at Run. Every entry here is a
/// slot the component can use, at the value it is created with.</para>
///
/// <para>Returns the chosen slot via <c>ShowDialog</c>, or null on cancel — the same
/// return-or-null contract <see cref="ModelParameterPickerDialog"/> uses.</para>
/// </summary>
public partial class SddEquationPickerDialog : Window
{
    private IReadOnlyList<SddEquationSlot> _all = [];

    public SddEquationPickerDialog() => InitializeComponent();

    public SddEquationPickerDialog(string instanceName, int portCount,
        IReadOnlyList<SddEquationSlot> slots, IReadOnlyList<string>? notes = null) : this()
    {
        _all = slots;

        // A slot the device's own rules suppress reads as a missing feature unless it is said out
        // loud — V[p] on a freshly placed SDD is exactly that case.
        NoteText.Text      = notes is { Count: > 0 } ? string.Join("  ", notes) : "";
        NoteText.IsVisible = NoteText.Text.Length > 0;

        HeaderText.Text =
            $"{instanceName} is a {portCount}-port SDD. Choose an equation slot to add — the list is "
            + "what this device can still take, so nothing here duplicates an equation it already "
            + "carries or names a port it does not have.";

        EmptyText.Text = "This SDD already carries every slot its port count allows.";

        ApplyFilter("");

        SearchBox.TextChanged      += (_, _) => ApplyFilter(SearchBox.Text ?? "");
        ChoiceList.SelectionChanged += (_, _) => ShowDetail();
        ChoiceList.DoubleTapped    += (_, _) => Commit();
        OkButton.Click             += (_, _) => Commit();
        CancelButton.Click         += (_, _) => Close(null);

        Opened += (_, _) => SearchBox.Focus();
    }

    private void ApplyFilter(string query)
    {
        // Matches the summary AND the detail, because the notation is exactly what a user may not
        // remember — someone hunting for "capacitance" should find the charge slot.
        string q = query.Trim();
        var shown = q.Length == 0
            ? _all
            : [.. _all.Where(s =>
                  s.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                  s.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)  ||
                  s.Category.Contains(q, StringComparison.OrdinalIgnoreCase))];

        ChoiceList.ItemsSource   = shown;
        ChoiceList.SelectedIndex = shown.Count > 0 ? 0 : -1;

        bool anything = _all.Count > 0;
        ChoiceList.IsVisible = anything;
        EmptyText.IsVisible  = !anything;
        OkButton.IsEnabled   = shown.Count > 0;
        ShowDetail();
    }

    private void ShowDetail()
        => DetailText.Text = ChoiceList.SelectedItem is SddEquationSlot s ? s.Detail : "";

    private void Commit()
    {
        if (ChoiceList.SelectedItem is SddEquationSlot picked) Close(picked);
    }
}
