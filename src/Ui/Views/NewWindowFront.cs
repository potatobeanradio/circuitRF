using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CircuitRF.Ui.Views;

/// <summary>
/// Keeps a newly shown UNOWNED window in front of the window it was opened from, for the moment
/// right after it appears (field report, 2026-09-23).
/// </summary>
/// <remarks>
/// <b>The report.</b> A `.crail` double-clicked in the project tree opened its railRF window BEHIND
/// the workspace. Closing it and opening it again brought it up in front. The window is unowned on
/// purpose so that it can go behind the workspace later (see <c>RailRfWindow.ShowUnowned</c>), which
/// also means nothing prevents the workspace from being raised over it while it is still appearing.
///
/// <para><b>Why the mechanism is not pinned down.</b> The window is shown from inside the second
/// press of a double-click, while that press is still being handled by the workspace window. The
/// workspace is then activated once more — by the platform finishing an application activation
/// that the first click started, or by ordering that was deferred to the mouse-up. Neither can be
/// observed from here (the GUI cannot be launched from the test host), and both fit the report
/// exactly: it happens on the first open, when the gesture is what brought the application or the
/// window forward, and not on a later one, when both were already in front.</para>
///
/// <para><b>So the rule reacts to the effect, not the cause.</b> If the opener is activated within
/// <see cref="Grace"/> of the new window appearing, and nobody has pressed a mouse button in either
/// window since, the new window is activated again. A press in the OPENER is someone choosing to go
/// back to it, so the hold is released and never overrides it. A press in the new window means it
/// already has the user's attention. Either way the hold ends after the first re-activation, so it
/// cannot fight anything for longer than one exchange.</para>
/// </remarks>
internal static class NewWindowFront
{
    /// <summary>How long after <c>Show</c> an activation of the opener counts as part of the
    /// gesture that opened the window. Generous on purpose: a Debug build's first open spends most
    /// of this constructing the window, and the activation that steals it arrives after that.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    /// <summary>The decision, with no window in it.</summary>
    internal sealed class Hold(DateTime shownAtUtc)
    {
        public bool Released { get; private set; }

        /// <summary>A mouse button was pressed in either window. Whichever it was, the user has
        /// now chosen, and the hold is released.</summary>
        public void Pressed() => Released = true;

        /// <summary>True when the opener's activation at <paramref name="nowUtc"/> should be undone.
        /// Releases the hold either way, because only the FIRST activation is undone.</summary>
        public bool OpenerActivated(DateTime nowUtc)
        {
            bool undo = !Released && nowUtc - shownAtUtc <= Grace;
            Released = true;
            return undo;
        }
    }

    /// <summary>Call immediately after <paramref name="shown"/>'s <c>Show()</c>.</summary>
    public static void Keep(Window shown, Window? opener)
    {
        if (opener is null || ReferenceEquals(opener, shown)) return;

        var hold = new Hold(DateTime.UtcNow);

        void OnPressed(object? _, PointerPressedEventArgs __) => hold.Pressed();

        void OnOpenerActivated(object? _, EventArgs __)
        {
            bool undo = hold.OpenerActivated(DateTime.UtcNow);
            Detach();
            if (!undo) return;

            // Posted rather than immediate, so the activation in progress completes first. Activating
            // from inside another window's Activated handler works against it rather than after it.
            Dispatcher.UIThread.Post(() =>
            {
                if (shown.PlatformImpl is not null && shown.IsVisible) shown.Activate();
            }, DispatcherPriority.Background);
        }

        void Detach()
        {
            opener.Activated -= OnOpenerActivated;
            opener.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            shown.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        }

        // Tunnelled with handledEventsToo, so a press that a control inside marks handled still
        // counts as a choice.
        opener.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        shown.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        opener.Activated += OnOpenerActivated;
        shown.Closed += (_, _) => Detach();

        // Nothing is left subscribed once the grace has passed.
        DispatcherTimer.RunOnce(Detach, Grace);
    }
}
