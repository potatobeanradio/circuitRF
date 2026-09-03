using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.ViewModels;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.Views;

/// <summary>
/// <b>The ✕ on a tool panel's chrome, made to do something.</b> Dock 12.0.0.2 draws
/// <c>PART_CloseButton</c> on a <see cref="ToolChromeControl"/> and never wires it — reported by the
/// owner for the Library palette (2026-09-02) and confirmed for the docked chrome and the floating one
/// alike. It is the same defect already recorded one control over in <c>CircuitRfStyles.axaml</c>, where
/// that chrome's Float / Dock / Dock-as-Tabbed-Document items were found to do nothing and the dead menu
/// button was removed rather than repaired.
///
/// <h3>Why a shared helper, and not a method on one window</h3>
/// <para>The identical button exists on two surfaces — the shell, for a docked panel, and a
/// <c>CrfHostWindow</c>, for a floated one — and the first fix reached only the float, so the docked
/// panel still ignored its own ✕. Same shape, and the same answer, as <see cref="WirePanelKeys"/>:
/// register per <see cref="TopLevel"/> rather than per window class.</para>
///
/// <h3>Tunnelling pointer events, not <c>Button.Click</c> — measured, not preferred</h3>
/// <para>A <c>Button.ClickEvent</c> handler on the window never ran: the button flashes under the press
/// and the event does not arrive, so something between the two marks it handled. The tunnel pair is the
/// one route KNOWN to reach the window, because it is the route a diagnostic click was traced along —
/// source <c>ContentPresenter</c>, straight up through <c>PART_CloseButton</c> to the chrome. Everything
/// else about this button is inference; that is measurement, so it is what the code uses.</para>
///
/// <para>The press is deliberately left UNHANDLED so the button still draws its own press feedback — the
/// one part of it that was ever working — and a release landing anywhere else cancels, as on a real
/// button.</para>
/// </summary>
public static class ToolChromeCloseButton
{
    /// <summary>Dock's own name for the chrome's close button, matched from the visual tree.</summary>
    private const string PartName = "PART_CloseButton";

    /// <summary>
    /// Registers the interception on <paramref name="top"/>.
    /// </summary>
    /// <param name="resolve">The workspace that owns the panel; null disables the panel route.</param>
    /// <param name="unresolved">
    /// What to do when no panel can be identified from the chrome — closing the host window, for a float.
    /// Null where there is nothing sensible to do, which is the shell: it must never close itself
    /// because a panel could not be named.
    /// </param>
    public static void Attach(TopLevel top, Func<WorkspaceViewModel?> resolve, Action? unresolved = null)
    {
        ToolChromeControl? armed = null;

        top.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => armed = ChromeOfCloseButton(e.Source as Visual),
            RoutingStrategies.Tunnel);

        top.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, e) =>
            {
                var pressed = armed;
                armed = null;

                if (pressed is null || !ReferenceEquals(ChromeOfCloseButton(e.Source as Visual), pressed))
                    return;   // released somewhere else: the press is cancelled, as on any button

                e.Handled = true;

                // Posted: the action can close the very window this pointer event is being delivered to.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Close(pressed, resolve, unresolved));
            },
            RoutingStrategies.Tunnel);
    }

    private static void Close(ToolChromeControl chrome, Func<WorkspaceViewModel?> resolve, Action? unresolved)
    {
        var target = chrome.DataContext switch
        {
            IDock dock       => dock.ActiveDockable,
            IDockable single => single,
            _                => null,
        };

        if (target is ITool tool && resolve() is { } workspace && workspace.CloseToolPanel(tool))
            return;

        unresolved?.Invoke();
    }

    /// <summary>
    /// The <see cref="ToolChromeControl"/> whose close button was pressed, or null for any other click.
    /// Walks up from the event source because the source is whatever was hit — a
    /// <c>ContentPresenter</c> inside the button, in the traced click.
    ///
    /// <para>Requiring the chrome ancestor is what keeps this off everything else: a DOCUMENT tab's own
    /// close button lives under a <c>DocumentControl</c>, never under tool chrome, so documents keep
    /// Dock's own close path (and with it the dirty-save prompt).</para>
    /// </summary>
    private static ToolChromeControl? ChromeOfCloseButton(Visual? source)
    {
        bool onCloseButton = false;

        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { Name: PartName }) onCloseButton = true;
            if (v is ToolChromeControl chrome) return onCloseButton ? chrome : null;
        }

        return null;
    }
}
