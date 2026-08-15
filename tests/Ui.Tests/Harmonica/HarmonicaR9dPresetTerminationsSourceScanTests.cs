// ================================================================
//  HarmonicaR9dPresetTerminationsSourceScanTests.cs — brief-harmonicarf-r9d §3.8
//
//  Markers ▸ Preset Terminations ▸ Class B / J / J* / F / F⁻¹ across all three menu surfaces
//  (HarmonicaMenuView.axaml's NativeMenu block AND its in-window Menu block, plus
//  HarmonicaAppMenuInjector's docked-macOS build) — the five headers, the five command parameters and
//  the five gestures, on each.
// ================================================================

using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR9dPresetTerminationsSourceScanTests
{
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

    private static void AssertAllFiveHeadersAndParameters(string src)
    {
        Assert.Contains("Class B", src, StringComparison.Ordinal);
        Assert.Contains("Class J", src, StringComparison.Ordinal);
        Assert.Contains("J*", src, StringComparison.Ordinal);
        Assert.Contains("Class F", src, StringComparison.Ordinal);
        Assert.Contains("F⁻¹", src, StringComparison.Ordinal);

        Assert.Contains("\"B\"", src, StringComparison.Ordinal);
        Assert.Contains("\"J\"", src, StringComparison.Ordinal);
        Assert.Contains("\"JStar\"", src, StringComparison.Ordinal);
        Assert.Contains("\"F\"", src, StringComparison.Ordinal);
        Assert.Contains("\"FInverse\"", src, StringComparison.Ordinal);
    }

    // ══ surface 1 — the macOS NativeMenu block, HarmonicaMenuView.axaml ═════════════════════════════

    [Fact]
    public void HarmonicaMenuView_NativeMenu_HasThePresetTerminationsSubmenu()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml");
        Assert.Contains("Header=\"Preset Terminations\"", src, StringComparison.Ordinal);
        AssertAllFiveHeadersAndParameters(src);

        Assert.Contains("Gesture=\"Meta+B\"",        src, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Meta+J\"",        src, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Meta+Shift+J\"",  src, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Meta+F\"",        src, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Meta+Shift+F\"",  src, StringComparison.Ordinal);
    }

    // ══ surface 2 — the in-window Menu block, same file ═════════════════════════════════════════════

    [Fact]
    public void HarmonicaMenuView_InWindowMenu_HasThePresetTerminationsSubmenu()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml");
        Assert.Contains("Header=\"_Preset Terminations\"", src, StringComparison.Ordinal);

        Assert.Contains("InputGesture=\"Ctrl+B\"",        src, StringComparison.Ordinal);
        Assert.Contains("InputGesture=\"Ctrl+J\"",        src, StringComparison.Ordinal);
        Assert.Contains("InputGesture=\"Ctrl+Shift+J\"",  src, StringComparison.Ordinal);
        Assert.Contains("InputGesture=\"Ctrl+F\"",        src, StringComparison.Ordinal);
        Assert.Contains("InputGesture=\"Ctrl+Shift+F\"",  src, StringComparison.Ordinal);
    }

    // ══ surface 3 — the docked-macOS build, HarmonicaAppMenuInjector.cs ═════════════════════════════

    [Fact]
    public void HarmonicaAppMenuInjector_BuildsThePresetTerminationsSubmenu_WithGestures()
    {
        string src = ReadSource("src", "Ui", "Harmonica", "HarmonicaAppMenuInjector.cs");
        Assert.Contains("\"Preset Terminations\"", src, StringComparison.Ordinal);
        AssertAllFiveHeadersAndParameters(src);

        Assert.Contains("new KeyGesture(Key.B, KeyModifiers.Meta)",                     src, StringComparison.Ordinal);
        Assert.Contains("new KeyGesture(Key.J, KeyModifiers.Meta)",                     src, StringComparison.Ordinal);
        Assert.Contains("new KeyGesture(Key.J, KeyModifiers.Meta | KeyModifiers.Shift)", src, StringComparison.Ordinal);
        Assert.Contains("new KeyGesture(Key.F, KeyModifiers.Meta)",                     src, StringComparison.Ordinal);
        Assert.Contains("new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Shift)", src, StringComparison.Ordinal);
    }

    // ══ the view model's own command, exercised directly ════════════════════════════════════════════

    // The shipped default document has no package and no DUT capacitances, so IntrinsicAbcd.ExtrinsicFor
    // is the identity map (see PaClassPresetsTests's own identity check) — L1's written termination is
    // therefore the class's own intrinsic ZL1 exactly, which is enough to tell the five command
    // parameters apart without re-deriving the string→enum switch under test.
    [Theory]
    [InlineData("B",        CircuitRF.Harmonica.PaClass.B)]
    [InlineData("J",        CircuitRF.Harmonica.PaClass.J)]
    [InlineData("JStar",    CircuitRF.Harmonica.PaClass.JStar)]
    [InlineData("F",        CircuitRF.Harmonica.PaClass.F)]
    [InlineData("FInverse", CircuitRF.Harmonica.PaClass.FInverse)]
    public void SetPaClassPresetCommand_MapsEveryParameterToItsClass(string parameter, CircuitRF.Harmonica.PaClass expected)
    {
        var vm = new CircuitRF.Ui.Harmonica.HarmonicaViewModel();
        var menu = new CircuitRF.Ui.Harmonica.HarmonicaMenuViewModel(vm);

        menu.SetPaClassPresetCommand.Execute(parameter);

        double z0 = vm.Model.Settings.Z0;
        var expectedZ1 = CircuitRF.Harmonica.PaClassPresets.IntrinsicLoad(expected, 1, z0);
        var actualZ1   = vm.Terminations.Z(CircuitRF.Harmonica.TerminationSide.Load, 1);
        Assert.Equal(expectedZ1.Real,      actualZ1.Real,      precision: 6);
        Assert.Equal(expectedZ1.Imaginary, actualZ1.Imaginary, precision: 6);
    }
}
