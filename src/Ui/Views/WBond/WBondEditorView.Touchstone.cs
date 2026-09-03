using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// Touchstone export (brief-wbond-wbe M3), plus the small shell-facing surface the standalone
/// binary's menu bar drives.
///
/// <para><b>The menu binds the view's OWN methods, never a second implementation.</b> Undo, redo,
/// copy, paste and Select All All-Wires already exist here as the keyboard gestures' handlers; the
/// standalone shell reaches the same ones, so a menu item and its shortcut can never diverge.</para>
/// </summary>
public partial class WBondEditorView
{
    /// <summary>Status line, for a host that has nowhere else to report to (the standalone shell).</summary>
    internal void ShowShellStatus(string message, bool isWarning = false) => ShowStatus(message, isWarning);

    internal void UndoFromShell() { _bound?.Editor.Undo(); RepaintBoth(); }

    internal void RedoFromShell() { _bound?.Editor.Redo(); RepaintBoth(); }

    private async void OnExportTouchstone(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ExportTouchstoneAsync();

    /// <summary>
    /// Writes the design's array network as a Touchstone file (§11's own requirement — a wBond has
    /// never been able to publish its own network).
    ///
    /// <para><b>The work is in <see cref="WBondPublishCommands"/>, not here.</b> The same action is
    /// reachable from <c>LayoutEditorView</c>'s wire toolbar — a <c>.clay</c> with a <c>.wBond</c>
    /// beside it has no <c>WBondEditorView</c> in it anywhere — so a handler per view would have been
    /// two copies of a file-picker flow that must not drift.</para>
    /// </summary>
    internal async Task ExportTouchstoneAsync()
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var outcome = await WBondPublishCommands.ExportTouchstoneAsync(
            owner, _bound.Editor.Design, ResolveMessages());
        if (!outcome.IsSilent) ShowStatus(outcome.Message, outcome.IsWarning);
    }

    private async void OnCompareDistributedModel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await CompareDistributedModelAsync();

    /// <summary>
    /// <c>Compare Distributed Model…</c> (brief-wbond-mom-w2 §7.3) — the distributed (MoM) model run
    /// next to the lumped one, on a frequency grid the user states.
    ///
    /// <para><b>This view is not the only place it is reachable from</b>, and assuming it was is the bug
    /// the owner reported twice. See <see cref="WBondPublishCommands"/>.</para>
    /// </summary>
    internal async Task CompareDistributedModelAsync()
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var outcome = await WBondPublishCommands.CompareDistributedModelAsync(
            owner, _bound.Editor.Design, ResolveMessages());
        if (!outcome.IsSilent) ShowStatus(outcome.Message, outcome.IsWarning);
    }

    /// <summary>
    /// Where a long wirebond computation reports its live progress from THIS view.
    ///
    /// <para><b>Two hosts, two answers, and the difference is not cosmetic.</b> Inside circuitRF this
    /// view is a document tab and the workspace's Messages panel is on screen beside it, so the run
    /// reports there — the same two live rows an EM run posts, in the same place, which is the whole
    /// point of reusing that mechanism. The standalone <c>wBond</c> binary has no workspace and no
    /// Messages region at all, so it falls back to this view's own status line
    /// (<see cref="WBondStatusMessageSink"/>) rather than running silently for minutes.</para>
    /// </summary>
    private IMessageSink ResolveMessages()
    {
        var workspace = Views.WorkspaceLocator.For(this);

        return workspace?.Messages ?? new WBondStatusMessageSink(ShowStatus);
    }
}
