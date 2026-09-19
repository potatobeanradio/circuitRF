using System.Threading.Tasks;

namespace CircuitRF.Ui;

/// <summary>
/// A standalone top-level window that <b>is</b> a document — one window, one file, its own unsaved
/// state — and therefore has to be asked before circuitRF quits.
/// </summary>
/// <remarks>
/// <b>Owner report, 2026-09-19:</b> quitting circuitRF with a dirty <c>.crail</c> open took the
/// document with it, unasked. The window's own <c>OnClosing</c> prompt was correct and was never
/// reached: <see cref="App.QuitAsync"/> asked every <c>WorkspaceWindow</c> and nothing else, and the
/// last one closing runs <c>CloseAllFloatingWindows</c> and <c>Environment.Exit</c> in the same
/// dispatcher pass — so the <c>e.Cancel = true</c> that these windows use to re-issue their close
/// after a modal answer had no later pass to be re-issued on. The process was gone before the dialog
/// could be shown.
///
/// <para><b>An interface rather than a type check</b>, for <see cref="ICrfMenuWindow"/>'s own reason:
/// the quit path would otherwise carry a growing <c>OfType&lt;…&gt;</c> list of concrete window
/// classes, and a new standalone document window would be silently absent from it — which is exactly
/// the failure this exists to fix, one window later.</para>
///
/// <para><b>Confirming is not closing.</b> The quit asks EVERY window before closing ANY of them
/// (MW1 R-mw1-18), so an implementation must settle its unsaved work, mark itself clear to close and
/// return — leaving the window on screen. Cancelling anywhere leaves everything exactly as it was.</para>
/// </remarks>
public interface ICrfDocumentWindow
{
    /// <summary>
    /// Settles this window's unsaved work and marks it clear to close, without closing it. Returns
    /// <c>false</c> when the user cancelled — nothing saved, nothing discarded, the quit off.
    /// Idempotent: a window that has already confirmed says yes without asking again.
    /// </summary>
    Task<bool> ConfirmCloseAsync();
}
