using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Group Wires As…" — pick the group a selection of wires should belong to (owner, 2026-08-16).
///
/// <para><b>One dialog, two entry points.</b> The layout view's wire context menu and the wBond
/// Properties panel both open THIS, so "how do I change a wire's group" has one answer that behaves
/// the same either way. A second copy of the picker would be a second place for the New Group rule
/// and the count to drift.</para>
///
/// <para><b>The count is on the face of it</b>, not implied by whatever happens to be highlighted
/// behind the modal — the owner asked for it explicitly as a sanity check, and it is the one number
/// that cannot be recovered after the fact: a regroup of forty wires and a regroup of four look
/// identical in the panel until the inductance is read.</para>
/// </summary>
public partial class WBondGroupWiresDialog : Window
{
    /// <summary>The trailing entry that means "a group that does not exist yet".</summary>
    public const string NewGroupSentinel = "New Group…";

    private string _suggested = "G1";

    // Parameterless ctor satisfies the Avalonia XAML resource loader.
    public WBondGroupWiresDialog() => InitializeComponent();

    /// <summary>
    /// Shows the picker. Returns the chosen group name — existing or newly typed — or null on cancel.
    /// </summary>
    /// <param name="wireCount">How many wires the caller is about to move; shown, not used.</param>
    /// <param name="groups">Existing group names, in the order the profile view draws them.</param>
    /// <param name="current">The group to pre-select, when the selection is already all in one.</param>
    /// <param name="suggestedNewName">An unused name, so "New Group…" opens with a valid answer.</param>
    public static async Task<string?> ShowAsync(
        Window? owner,
        int wireCount,
        IReadOnlyList<string> groups,
        string? current,
        string suggestedNewName)
    {
        if (owner is null) return null;
        ArgumentNullException.ThrowIfNull(groups);

        var dlg = new WBondGroupWiresDialog { _suggested = suggestedNewName };

        dlg.CountText.Text = wireCount == 1
            ? "1 wire selected."
            : $"{wireCount} wires selected.";

        var items = new List<string>(groups) { NewGroupSentinel };
        dlg.GroupCombo.ItemsSource = items;
        dlg.GroupCombo.SelectedItem =
            items.FirstOrDefault(g => string.Equals(g, current, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault();

        dlg.Validate();

        return await dlg.ShowDialog<string?>(owner);
    }

    private bool IsNewGroup => GroupCombo.SelectedItem as string == NewGroupSentinel;

    private void OnGroupChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Seeded on the way in rather than left blank: an empty box with a disabled OK reads as the
        // dialog being stuck, and the suggestion is always a name that is actually free.
        if (IsNewGroup && string.IsNullOrWhiteSpace(NewNameBox.Text)) NewNameBox.Text = _suggested;

        NewNameBox.IsVisible = IsNewGroup;
        if (IsNewGroup) { NewNameBox.Focus(); NewNameBox.SelectAll(); }

        Validate();
    }

    private void OnNewNameChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void OnNewNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (!OkButton.IsEnabled) { e.Handled = true; return; }
        OnOk(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    /// <summary>Live validation, so OK reflects whether the current choice can actually be used.</summary>
    private void Validate()
    {
        bool ok = !IsNewGroup || !string.IsNullOrWhiteSpace(NewNameBox.Text);

        OkButton.IsEnabled = ok;
        ErrorText.IsVisible = !ok;
        ErrorText.Text = ok ? "" : "Enter a name for the new group.";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close(IsNewGroup ? NewNameBox.Text?.Trim() : GroupCombo.SelectedItem as string);
}
