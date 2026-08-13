// ================================================================
//  HarmonicaSetZ0DialogTests.cs — brief-harmonicarf-r2b R-h9r2-20
//
//  "Currently no way for the user to set Z0 of the Smith Charts. Add a Set Z0 to menu." Z0 was already
//  user-settable (R-h9b-6's §7.5 input row) — this dialog is a SECOND SURFACE onto the identical write,
//  never a second write path. HarmonicaZ0Tests.cs already gates ApplyInput(KeyZ0, ...)'s own invariant
//  (changing Z0 moves no termination, every marker's Γ moves) — this file pins that the dialog goes
//  through that exact call and touches Model.Settings.Z0 nowhere else.
// ================================================================

using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSetZ0DialogTests
{
    private static string Source() =>
        ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaSetZ0Dialog.axaml.cs");

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Commit_WritesThrough_ApplyInput_KeyZ0_OnlyRoute()
    {
        string src = Source();
        Assert.Contains("_vm.ApplyInput(HarmonicaInputs.KeyZ0, Z0Box.Text", src, StringComparison.Ordinal);

        // Never a second, direct write to the model's own Z0 field.
        Assert.DoesNotContain("Settings.Z0 =", src, StringComparison.Ordinal);
        Assert.DoesNotContain(".Z0 = ", src, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuHooks_AreWiredForBothTheDialogAndThePowerSweep()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        Assert.Contains("menus.SetZ0Hook           = () => RunHook(ShowSetZ0Async);", src, StringComparison.Ordinal);
        Assert.Contains("menus.PowerSweepHook      = () => RunHook(ShowPowerSweepAsync);", src, StringComparison.Ordinal);
        Assert.Contains("new Dialogs.HarmonicaSetZ0Dialog(h)", src, StringComparison.Ordinal);
        Assert.Contains("new Dialogs.HarmonicaPowerSweepDialog(h)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void BothMenuSurfaces_CarryTheNewCommands()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml");
        // NativeMenu (macOS) and the in-window Menu both bind the same two commands.
        int nativeSetZ0 = System.Text.RegularExpressions.Regex.Matches(src,
            @"Command=""\{Binding SetZ0Command\}""").Count;
        int nativePowerSweep = System.Text.RegularExpressions.Regex.Matches(src,
            @"Command=""\{Binding PowerSweepCommand\}""").Count;
        Assert.Equal(2, nativeSetZ0);
        Assert.Equal(2, nativePowerSweep);
    }
}
