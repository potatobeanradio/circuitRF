using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// "Group Wires As…", in one place (owner, 2026-08-16: <i>"a few different ways to do it are a good
/// idea"</i> — the layout view's wire context menu and the Properties panel).
///
/// <para><b>Two entry points, one command.</b> The two surfaces differ only in which window owns the
/// modal; everything else — which wires, which group is pre-selected, the batch move, the re-pointed
/// selection — is the same, and duplicating it would be duplicating the parts that are easy to get
/// subtly wrong (a per-wire undo entry, a selection left pointing at pre-move indices).</para>
/// </summary>
internal static class WBondGroupCommand
{
    /// <summary>
    /// Opens the picker on <paramref name="targets"/> — or on <paramref name="vm"/>'s current wire
    /// selection when none are given — and applies what comes back. A cancelled dialog, an empty
    /// subject or a missing owner window are all a clean no-op.
    /// </summary>
    /// <param name="targets">
    /// The wires to regroup, by flat <c>AllWires</c> index. <b>Null means "the current selection"</b>,
    /// which is what the Properties panel's button wants — it is shown for a selection and states its
    /// counts on the line above. The layout view's context menu passes an explicit set instead, because
    /// a right-click on a wire has a subject of its own (owner, 2026-08-18).
    /// </param>
    /// <returns>How many wires changed group.</returns>
    internal static async Task<int> RunAsync(Window? owner, WBondViewModel? vm,
                                             IReadOnlyCollection<int>? targets = null)
    {
        if (owner is null || vm is null) return 0;

        var touched = (targets ?? vm.Selection.TouchedWires()).ToList();
        if (touched.Count == 0) return 0;

        // Pre-select the group they are already in, when they share one — the common case is "these
        // forty are in G1 and belong in GND", and starting on G1 makes that visible.
        var groups = touched.Select(vm.GroupNameOfWire).Distinct().ToList();
        string? current = groups.Count == 1 ? groups[0] : null;

        string? chosen = await WBondGroupWiresDialog.ShowAsync(
            owner, touched.Count, vm.GroupNames, current, vm.SuggestGroupName());

        return string.IsNullOrWhiteSpace(chosen) ? 0 : vm.MoveWiresToGroup(touched, chosen);
    }

    /// <summary>
    /// The command's label for a given selection size — shown on the CONTEXT-MENU item, so the count
    /// the user is about to act on is visible before the dialog opens as well as inside it.
    ///
    /// <para>The Properties panel's button no longer uses it (owner, 2026-08-17): that panel states
    /// the wire and group counts on its own message line directly above the button, and carrying the
    /// wire count in both places said it twice. A context-menu item has no such line.</para>
    /// </summary>
    internal static string Label(int wireCount) => wireCount switch
    {
        0 => "Group Wires As…",
        1 => "Group Wire As…",
        _ => $"Group {wireCount} Wires As…",
    };
}
