// ================================================================
//  HarmonicaR11KOrderAndShortcutTests.cs — Round 11 §1/§3/§4
//
//  Three owner-reported defects, all reachable from one gesture (editing HB Order in the readout
//  strip) plus one addition:
//
//   §1  K = 3 → 5 → 3 did not return the drive-up to what K = 3 originally showed. The solver carried
//       the previous frame's converged spectra forward as a warm start (lever 1) and a K = 5
//       spectrum is the wrong SHAPE for a K = 3 solve — see LadderContinuityTests for the ladder half
//       of this; here is the frame-level half.
//   §3  Editing HB Order made an S1 marker appear on a document that deliberately had none.
//   §4  Ctrl/⌘+L toggles Display ▸ Grid Points.
// ================================================================

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR11KOrderAndShortcutTests
{
    private static readonly HarmonicaSolver.Options TierAOnly = new() { SkipContours = true };

    private static bool SetK(HarmonicaViewModel vm, int k)
        => vm.ApplyInput(HarmonicaInputs.KeyHarmonicCount, k.ToString(CultureInfo.InvariantCulture));

    // ══ §1 — the drive-up depends on the settings, not on the order they were reached in ═════════

    /// <summary>
    /// The owner's own sequence: Class F at the shipped default's K = 3, up to K = 5, back to K = 3.
    /// The third frame must be the first frame — the circuit is identical (bands 4/5 are dropped
    /// again, bands 1–3 are untouched), so anything that differs is state the solver carried across a
    /// structural edit it had no business carrying.
    /// </summary>
    [Fact]
    public void KOrderRoundTrip_ReturnsTheDriveUpToWhatItWas()
    {
        var vm = new HarmonicaViewModel();
        Assert.Equal(3, vm.Model.Settings.HarmonicCount);
        vm.ApplyPaClassPreset(PaClass.F);

        vm.SolveFrame(TierAOnly);
        var first = vm.Frame.PowerSweep;

        Assert.True(SetK(vm, 5));
        vm.SolveFrame(TierAOnly);

        Assert.True(SetK(vm, 3));
        vm.SolveFrame(TierAOnly);
        var back = vm.Frame.PowerSweep;

        Assert.Equal(first.PinAvailDbm.Length, back.PinAvailDbm.Length);
        for (int i = 0; i < first.PinAvailDbm.Length; i++)
        {
            Assert.Equal(first.PinAvailDbm[i], back.PinAvailDbm[i], precision: 9);
            Assert.Equal(first.PoutDbm[i],     back.PoutDbm[i],     precision: 9);
            Assert.Equal(first.GainDb[i],      back.GainDb[i],      precision: 9);
        }
    }

    /// <summary>
    /// The K = 5 leg of the same sequence, on its own terms. Before the fix it read a K = 3 spectrum
    /// at every rung, cold-started the whole ladder, and produced a drive-up with a 6 dB gain step in
    /// it; a real amplifier's gain does not move further than its own 1 dB drive step.
    /// </summary>
    [Fact]
    public void RaisingKMidSession_LeavesASmoothDriveUp()
    {
        var vm = new HarmonicaViewModel();
        vm.ApplyPaClassPreset(PaClass.F);
        vm.SolveFrame(TierAOnly);

        Assert.True(SetK(vm, 5));
        vm.SolveFrame(TierAOnly);

        var s = vm.Frame.PowerSweep;
        Assert.True(s.PinAvailDbm.Length > 2);
        for (int i = 1; i < s.PinAvailDbm.Length; i++)
        {
            double dPin  = s.PinAvailDbm[i] - s.PinAvailDbm[i - 1];
            double dPout = s.PoutDbm[i] - s.PoutDbm[i - 1];
            Assert.True(Math.Abs(dPout) <= dPin + 3.0,
                $"Pout moved {dPout:F2} dB over a {dPin:F2} dB drive step at Pin = {s.PinAvailDbm[i]:F1} dBm");
        }
    }

    /// <summary>The invalidation is keyed on the STRUCTURE, so an ordinary value edit still keeps the
    /// carried-over seed — the whole point of lever 1, which this fix must not disable.</summary>
    [Fact]
    public void TheSeedIsDroppedOnAStructuralEditOnly()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(TierAOnly);
        int afterFirst = vm.SolverStructuralSeedResetCount;
        Assert.Equal(1, afterFirst);                 // the first frame of a session has none to keep

        vm.SolveFrame(TierAOnly);
        Assert.Equal(afterFirst, vm.SolverStructuralSeedResetCount);

        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyVgs, "-3.0"));   // a value edit
        vm.SolveFrame(TierAOnly);
        Assert.Equal(afterFirst, vm.SolverStructuralSeedResetCount);

        Assert.True(SetK(vm, 5));                                    // a structural edit
        vm.SolveFrame(TierAOnly);
        Assert.Equal(afterFirst + 1, vm.SolverStructuralSeedResetCount);
    }

    // ══ §3 — a K edit is not a reason to invent a marker ════════════════════════════════════════

    [Fact]
    public void ChangingK_NeitherCreatesNorDropsAMarkerThatFitsTheNewK()
    {
        var vm = new HarmonicaViewModel();
        Assert.Equal(["L1", "L2", "L3"], vm.Markers.Select(m => m.Name));

        Assert.True(SetK(vm, 5));
        Assert.Equal(["L1", "L2", "L3"], vm.Markers.Select(m => m.Name));
        Assert.DoesNotContain(vm.Markers, m => m.Side == TerminationSideKind.Source);

        Assert.True(SetK(vm, 3));
        Assert.Equal(["L1", "L2", "L3"], vm.Markers.Select(m => m.Name));
    }

    /// <summary>A marker that no longer fits IS dropped — §4.2's own rule, and the half of
    /// <c>RetargetTerminations</c> that must survive the fix.</summary>
    [Fact]
    public void LoweringK_DropsTheMarkersAboveIt_AndTheyDoNotComeBack()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(SetK(vm, 5));
        vm.AddMarkerBand(TerminationSideKind.Load, 5);
        Assert.Contains(vm.Markers, m => m.Band == 5);

        Assert.True(SetK(vm, 3));
        Assert.DoesNotContain(vm.Markers, m => m.Band > 3);
        Assert.False(vm.Terminations.IsMarked(TerminationSide.Load, 3) && vm.Markers.Count > 3);

        Assert.True(SetK(vm, 5));
        Assert.DoesNotContain(vm.Markers, m => m.Band > 3);
    }

    /// <summary>A source marker the user DID turn on survives a K round trip — the fix must not
    /// swing from "always invents S1" to "loses the one you asked for".</summary>
    [Fact]
    public void ASourceMarkerTheUserAddedSurvivesAKRoundTrip()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);
        Assert.Contains(vm.Markers, m => m.Name == "S1");

        Assert.True(SetK(vm, 5));
        Assert.True(SetK(vm, 3));
        Assert.Contains(vm.Markers, m => m.Name == "S1");
    }

    /// <summary>Round 11 §3's own regression risk: the per-band menus used to learn about a K change
    /// only because the marker list happened to be rebuilt. It now has its own signal.</summary>
    [Fact]
    public void TheBandMenusStillTrackK_NowThroughItsOwnSignal()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);
        Assert.Equal(3, menus.ContourHarmonics.Count);

        Assert.True(SetK(vm, 5));
        Assert.Equal(5, menus.ContourHarmonics.Count);
        Assert.Equal(5, menus.LoadBands.Count);
        Assert.Equal(5, menus.SourceBands.Count);

        // …and a detached view model stops listening, or a replaced document keeps rebuilding menus
        // nothing is showing (Detach's own reason, now with two events to unhook rather than one).
        menus.Detach();
        Assert.True(SetK(vm, 2));
        Assert.Equal(5, menus.ContourHarmonics.Count);
    }

    // ══ §4 — Ctrl/⌘+L ═══════════════════════════════════════════════════════════════════════════

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Removes <c>//</c> and <c>/* … */</c> spans, and XML/AXAML <c>&lt;!-- --&gt;</c> ones —
    /// this file's own explanatory comments name both modifiers, so a scan that read them would pass
    /// against code that wires neither.</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
                continue;
            }
            if (i + 3 < src.Length && src.AsSpan(i, 4).SequenceEqual("<!--"))
            {
                i += 4;
                while (i + 2 < src.Length && !src.AsSpan(i, 3).SequenceEqual("-->")) i++;
                i += 2;
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// ⌘L on the two NativeMenu surfaces and Ctrl+L as the view's own KeyBinding. <b>The split is the
    /// assertion, not an accident</b>: a macOS menu key equivalent is consumed by AppKit before
    /// Avalonia's input pipeline runs, so declaring the same gesture on both would give one keystroke
    /// two live handlers and toggle the setting twice — i.e. do nothing at all.
    /// </summary>
    [Fact]
    public void CtrlOrCmdL_TogglesGridPoints_OnEverySurface_AndNeverOnTwoAtOnce()
    {
        string injector = StripComments(Source("src", "Ui", "Harmonica", "HarmonicaAppMenuInjector.cs"));
        Assert.Contains("ToggleShowGridPointsCommand", injector, StringComparison.Ordinal);
        Assert.Contains("new KeyGesture(Key.L, KeyModifiers.Meta)", injector, StringComparison.Ordinal);

        string menuAxaml = StripComments(Source("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml"));
        Assert.Contains("Gesture=\"Cmd+L\"", menuAxaml, StringComparison.Ordinal);
        Assert.Contains("InputGesture=\"Ctrl+L\"", menuAxaml, StringComparison.Ordinal);

        string view = StripComments(Source("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs"));
        Assert.Contains("Key.L, Avalonia.Input.KeyModifiers.Control", view, StringComparison.Ordinal);
        Assert.Contains("menus.ToggleShowGridPointsCommand", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Key.L, Avalonia.Input.KeyModifiers.Meta", view, StringComparison.Ordinal);
    }

    /// <summary>The command the shortcut runs is the menu item's own, so the two cannot disagree —
    /// including about writing the toggle into the appearance block that persists it.</summary>
    [Fact]
    public void TheShortcutRunsTheSameCommandTheMenuItemDoes()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);
        Assert.False(vm.ShowGridPoints);

        menus.ToggleShowGridPointsCommand.Execute(null);
        Assert.True(vm.ShowGridPoints);
        Assert.True(vm.Appearance.ShowGridPoints);

        menus.ToggleShowGridPointsCommand.Execute(null);
        Assert.False(vm.ShowGridPoints);
        Assert.False(vm.Appearance.ShowGridPoints);
    }
}
