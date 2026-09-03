using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views;

/// <summary>
/// <c>P</c> / <c>A</c> — toggle the Wire Profile and Array Inductance panels (wbond.md §10.1).
///
/// <h3>Why this is a shared helper and not a method on one window</h3>
/// <para>Owner, 2026-08-17: <i>"when those windows are floating I can only toggle them twice before I am
/// forced to click on the canvas — this works perfectly when they are docked."</i></para>
///
/// <para>Presenting a floating window <b>activates</b> it, so the third press is delivered to the panel's
/// own OS window — a different <c>TopLevel</c>, which had no handler on it. Two presses, then dead until a
/// click puts focus back in the shell. Docked panels never showed it because everything is inside the one
/// window.</para>
///
/// <para><b>The rule this codifies, learned the expensive way over four attempts:</b> a shortcut whose own
/// action can move focus must not be gated on focus, and must be reachable from every surface focus can
/// land on. The previous fix moved the handler off the layout view onto the shell window, which covered
/// every control <i>in that window</i>; a float is one more window. So the handler is registered per
/// <c>TopLevel</c> — the shell and every <see cref="ViewModels.Dock.CrfHostWindow"/> — and its gate stays
/// what it became then: <i>which document is active</i>, which the action does not change.</para>
///
/// <para>Deliberately NOT solved by keeping focus in the shell when a panel floats. Stealing focus back
/// from a window the user just asked to see is the same class of patch as the ones that lost to Dock's own
/// focus handling three times, and it would make the panel unusable — its own fields could never be typed
/// into.</para>
/// </summary>
public static class WirePanelKeys
{
    /// <summary>
    /// Registers the shortcut on <paramref name="top"/>. Tunnel, so it is seen whatever has focus inside
    /// that window, exactly like the placement-rotate handler it sits beside in the shell.
    /// </summary>
    public static void Attach(TopLevel top, Func<WorkspaceViewModel?> resolve) =>
        top.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => { if (Handle(top, resolve(), e)) e.Handled = true; },
            RoutingStrategies.Tunnel);

    /// <summary>
    /// Handles the key if it is one of ours and the context allows. Returns whether it was consumed, so a
    /// window with its own tunnel handler can fold this in and stop.
    /// </summary>
    public static bool Handle(TopLevel top, WorkspaceViewModel? vm, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Key is not (Key.P or Key.A)) return false;
        if (vm is null || !vm.WirePanelKeysApply) return false;

        // A bare letter typed into a FIELD is text, not a command — and a floating panel is mostly
        // fields, so this matters more here than it did in the shell.
        if (IsTypingInAField(top.FocusManager?.GetFocusedElement())) return false;

        vm.ToggleToolPanelCommand.Execute(
            e.Key == Key.P ? Docking.DockPanelIds.WBondProfile : Docking.DockPanelIds.WBondInductance);

        return true;
    }

    /// <summary>
    /// The workspace behind a window that has no view model of its own — a floating panel's host.
    ///
    /// <para>The same lookup <c>WorkspaceViewModel.ShellWindow</c> uses in the other direction. Null in the
    /// standalone wBond app, which has no workspace, so the shortcut is simply absent there rather than
    /// needing a second gate.</para>
    /// </summary>
    public static WorkspaceViewModel? ResolveWorkspace() => WorkspaceLocator.Any();

    /// <summary>
    /// The workspace the key press belongs to — resolved from the window it arrived in
    /// (MW1 R-mw1-14), so a shortcut pressed in one workspace window never toggles a panel in
    /// another. <see cref="ResolveWorkspace"/> stays for the callers that genuinely have no visual.
    /// </summary>
    public static WorkspaceViewModel? ResolveWorkspaceFor(object? source)
        => WorkspaceLocator.For(source);

    /// <summary>
    /// Whether focus is in a text field — the same three control types <c>WBondEditorView</c> uses for its
    /// own single-letter shortcuts.
    /// </summary>
    private static bool IsTypingInAField(IInputElement? focused) =>
        focused is TextBox or AutoCompleteBox or ComboBox { IsEditable: true };
}
