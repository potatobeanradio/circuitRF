using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Stops a ComboBox inside a HIDDEN subtree from acting on pointer input — which, left alone, locks
/// up the whole machine.
///
/// <para><b>The failure.</b> <c>ComboBox.OnPointerReleased</c> toggles the dropdown gated on one
/// thing: the <c>:pressed</c> pseudo-class it set on the way down. It never asks whether it is still
/// visible. Its template two-way <c>TemplateBinding</c>s <c>Popup.IsOpen</c> to
/// <c>ComboBox.IsDropDownOpen</c>, and for a ComboBox that is not visible those two never converge —
/// <c>Popup.Open()</c> raises <c>PopupOpened</c>, which writes <c>IsDropDownOpen</c>, which the
/// binding publishes back to <c>IsOpen</c>, whose re-evaluation calls <c>CloseCore()</c>, which
/// re-opens it. The recursion never unwinds, allocates a fresh <c>PopupRoot</c> and a native popup
/// window every turn, and — because an open popup holds a system-wide input grab — the user cannot
/// focus another application to kill it.</para>
///
/// <para><b>Measured on the real thing, not reasoned about.</b> A probe on
/// <c>ComboBox.IsDropDownOpenProperty.Changed</c> caught the first transition with the stack still at
/// ordinary depth: a genuine <c>MouseDevice.MouseUp</c> delivered to a ComboBox whose ancestor chain
/// read <c>CellParameterBodyView &lt; Panel[IsVisible=FALSE] &lt; … &lt; PropertiesView</c> — the
/// Properties inspector's cell-context panel, hidden, in the main window, with
/// <c>IsEffectivelyVisible=false</c>, a null <c>DataContext</c> and a null <c>ItemsSource</c>. A dump
/// of the hung process showed the end state: 5,597 live <c>PopupRoot</c>s against 66 ComboBoxes.</para>
///
/// <para><b>Tunnel, not Bubble</b>, and that is what makes a four-line guard sufficient: the tunnel
/// route runs first, and ComboBox's own handler — on the bubble route — already declines a handled
/// event. Marking it handled up front means the <c>:pressed</c> latch is never set either, so there
/// is no half-state left behind for the next click to trip over.</para>
///
/// <para>Inert in normal use: any ComboBox the user can see and click has
/// <c>IsEffectivelyVisible == true</c> and never reaches the assignment. Applied as a CLASS handler
/// so it covers every ComboBox in the application, including ones in panels that do not exist yet —
/// the Properties inspector is where this was found, but nine tool panels stack their contexts the
/// same way and any of them could deliver the same click.</para>
/// </summary>
internal static class HiddenComboBoxInputGuard
{
    internal static void Install()
    {
        InputElement.PointerPressedEvent.AddClassHandler<ComboBox>(
            (combo, e) => { if (!combo.IsEffectivelyVisible) e.Handled = true; },
            RoutingStrategies.Tunnel, handledEventsToo: true);

        InputElement.PointerReleasedEvent.AddClassHandler<ComboBox>(
            (combo, e) => { if (!combo.IsEffectivelyVisible) e.Handled = true; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
