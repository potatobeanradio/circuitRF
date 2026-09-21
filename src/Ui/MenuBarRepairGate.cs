namespace CircuitRF.Ui;

/// <summary>
/// Decides whether an activation is one the macOS menu bar repair
/// (<see cref="MacOsAppMenu.RedrawMenuBar"/>) should run on. One per shell window.
///
/// <para><b>Why a gate at all — the repair has a cost, and the owner found it (2026-09-21).</b>
/// Clicking a menu title sometimes did not pull the menu down; clicking away to another
/// application and back made it work again, and sometimes it took a few attempts. That is the
/// shape of a menu-bar click being eaten by the activation it causes: clicking the bar of a
/// background application activates it and opens the menu in one gesture, and the repair —
/// <c>setMainMenu:</c> twice, synchronously, from <c>Activated</c> — replaces the very bar the
/// click is opening. The user then clicks a title that is, for that instant, on a menu that is no
/// longer installed.</para>
///
/// <para><b>What the gate is, stated as the rule rather than as the symptom: repaint only the
/// case AppKit does not repaint itself.</b> The fault the repair exists for is an in-application
/// focus change — the About dialog takes key from the shell window and gives it back, and the bar
/// is never redrawn (see <see cref="MacOsAppMenu.RedrawMenuBar"/>). Coming back from ANOTHER
/// application is the opposite: it is the workaround the owner has been using since the first
/// report, so on that path macOS already draws the bar correctly and the repair can only cost
/// something. The two are told apart at the moment the window is DEACTIVATED, which is the one
/// moment they differ — the application is still active when a dialog of ours takes focus, and it
/// is not when another application does.</para>
///
/// <para><b>One arm, one repair.</b> The flag is consumed, so an activation that follows no
/// deactivation at all — the first one after launch, or a re-entrant one from raising a floating
/// panel — does not repaint a bar nothing has disturbed.</para>
///
/// <para><b><c>CRF_MENU_FIX=always</c> restores the old unconditional behaviour</b>, which is how
/// this diagnosis is checked rather than argued: with it set the menu should go back to refusing
/// to pull down on the first click into the application.</para>
/// </summary>
internal sealed class MenuBarRepairGate
{
    private bool _armed;

    /// <summary>Repaint on every activation, as before the gate. <c>CRF_MENU_FIX=always</c>.</summary>
    internal bool OnEveryActivation { get; init; } =
        System.Environment.GetEnvironmentVariable("CRF_MENU_FIX") == "always";

    /// <summary>
    /// The window has stopped being active. <paramref name="applicationStillActive"/> is
    /// <c>[NSApp isActive]</c> read after AppKit has settled — true when focus went to another
    /// window of ours (a dialog), false when it went to another application.
    /// </summary>
    internal void NoteDeactivated(bool applicationStillActive) => _armed = applicationStillActive;

    /// <summary>
    /// Whether this activation should repaint the bar, consuming the arm if it does.
    /// </summary>
    internal bool TakeRepair()
    {
        if (OnEveryActivation) return true;
        bool armed = _armed;
        _armed = false;
        return armed;
    }
}
