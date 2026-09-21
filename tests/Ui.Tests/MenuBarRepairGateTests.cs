using CircuitRF.Ui;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The rule the macOS menu-bar repaint is gated by (2026-09-21): repaint the case AppKit does not
/// repaint itself, and no other. The repaint swaps <c>NSApp.mainMenu</c>, which on the way back from
/// another application replaces the bar the user's click is opening — the owner's report that a menu
/// title sometimes would not pull down until they clicked away and came back.
/// </summary>
public class MenuBarRepairGateTests
{
    /// <summary>A dialog of ours taking focus leaves the application active, and nothing else will
    /// redraw the bar — that is the one activation the repair is for.</summary>
    [Fact]
    public void AnInApplicationFocusChange_ArmsTheRepair()
    {
        var gate = new MenuBarRepairGate();
        gate.NoteDeactivated(applicationStillActive: true);
        Assert.True(gate.TakeRepair());
    }

    /// <summary>Another application taking focus does not: macOS draws the bar on the way back, and
    /// repainting there is what ate the menu-bar click.</summary>
    [Fact]
    public void AnApplicationSwitch_DoesNotArmTheRepair()
    {
        var gate = new MenuBarRepairGate();
        gate.NoteDeactivated(applicationStillActive: false);
        Assert.False(gate.TakeRepair());
    }

    /// <summary>One arm, one repair: an activation that followed no deactivation — the first after
    /// launch, or a re-entrant one from raising a floating panel — repaints nothing.</summary>
    [Fact]
    public void TheArmIsConsumed_SoALaterActivationRepaintsNothing()
    {
        var gate = new MenuBarRepairGate();
        Assert.False(gate.TakeRepair());          // never deactivated

        gate.NoteDeactivated(applicationStillActive: true);
        Assert.True(gate.TakeRepair());
        Assert.False(gate.TakeRepair());
    }

    /// <summary>The escape hatch that checks the diagnosis rather than arguing it: with
    /// <c>CRF_MENU_FIX=always</c> the old unconditional behaviour is back.</summary>
    [Fact]
    public void OnEveryActivation_RepaintsWithoutAnArm()
    {
        var gate = new MenuBarRepairGate { OnEveryActivation = true };
        Assert.True(gate.TakeRepair());
        Assert.True(gate.TakeRepair());
    }
}
