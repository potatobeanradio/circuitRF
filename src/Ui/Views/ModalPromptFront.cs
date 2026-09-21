using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CircuitRF.Ui.Views;

/// <summary>
/// Keeps a modal prompt in front of the windows standing around its owner, and records that one is
/// open so nothing else raises a window over it.
///
/// <para><b>Owner report, 2026-09-21: quitting with a torn-off Smith Chart window in front left the
/// "Unsaved Changes" prompt underneath it — so circuitRF simply looked like it was refusing to
/// quit.</b> The prompt had been shown, was key, and was answering the keyboard; it was invisible.
/// This is not particular to the Smith Chart, and it is not particular to quitting: it is the
/// arrangement every torn-off document window produces.</para>
///
/// <para><b>Why a dialog can end up underneath.</b> A window shown with <c>ShowDialog(owner)</c> is
/// a CHILD window of that owner, and a child is ordered relative to its own owner — not to the
/// application's other top-levels. That fact is already written down one floor up, in
/// <c>App.ShowReleaseNotes</c>: "an owned window is kept above its OWNER; it is not raised above the
/// application's other windows." A torn-off DOCUMENT window is deliberately a PEER —
/// <c>CircuitRfDockFactory.OwnerModeFor</c> gives it <c>DockWindowOwnerMode.None</c> so that the
/// workspace window can be placed on top of it — so a peer sitting in front
/// of the workspace window sits in front of the workspace window's dialogs too.</para>
///
/// <para><b>The OWNER is activated first, and that order is the whole fix.</b> Activating the dialog
/// on its own cannot lift it past a peer, because its place in the stack is its owner's place plus
/// one. Activating the owner moves the whole group — the shell, this dialog, and the floating TOOL
/// panels the shell also owns — in front of the peer; the dialog is then raised within that
/// group.</para>
///
/// <para><b>Which is also why <see cref="HasOpenPrompt"/> exists.</b> The shell raises every floating
/// tool panel from its own <c>Activated</c> hook (R-dock-14,
/// <c>WorkspaceWindow.RaiseFloatingToolWindows</c>), and those panels are siblings of this dialog
/// under the same owner — so the activation above would otherwise hand them the front and reproduce
/// the report with a floating Properties panel in place of the Smith Chart. That raise stands down
/// while a prompt is open.</para>
/// </summary>
internal static class ModalPromptFront
{
    private static readonly List<(Window Owner, Window Dialog)> _open = [];

    /// <summary>
    /// True when a prompt attached here is open over <paramref name="owner"/>.
    ///
    /// <para>Entries whose window has gone are dropped on the way past. A latch that could stick
    /// would switch the floating-panel raise off for the rest of the session with nothing reported,
    /// which is a worse bug than the one this class fixes.</para>
    /// </summary>
    internal static bool HasOpenPrompt(Window owner)
    {
        _open.RemoveAll(e => e.Dialog.PlatformImpl is null);
        return _open.Any(e => ReferenceEquals(e.Owner, owner));
    }

    /// <summary>
    /// Call from the prompt's constructor. A window that is never shown modally has no owner, so
    /// there is no group to raise and nothing is recorded — attaching is harmless either way.
    /// </summary>
    internal static void Attach(Window dialog)
    {
        Window? owner = null;

        dialog.Opened += (_, _) =>
        {
            // Set by Window.ShowCore before it opens us, and null for a plain Show() with no owner.
            owner = dialog.Owner as Window;
            if (owner is null) return;

            _open.Add((owner, dialog));
            Raise();

            // AND once more on a later dispatcher pass. On macOS the quit prompt is shown from
            // inside AppKit's own applicationShouldTerminate: callback — App.Quit runs on that
            // stack — and window ordering asked for there is not reliably kept once it returns. The
            // second raise costs nothing in the common case where the first one held.
            Dispatcher.UIThread.Post(Raise, DispatcherPriority.Background);
        };

        dialog.Closed += (_, _) =>
        {
            _open.RemoveAll(e => ReferenceEquals(e.Dialog, dialog));
            owner = null;
        };

        void Raise()
        {
            if (dialog.PlatformImpl is null || owner?.PlatformImpl is null) return;
            owner.Activate();   // the group, past whatever peer window was in front of it
            dialog.Activate();  // and this prompt within the group
        }
    }
}
