// ================================================================
//  HarmonicaDefaultMarkerSetTests.cs — R8B §3
//
//  "By default, S1 and S2 termination markers are turned off. User must turn them on from the menu
//  to activate them. Also, set S1 to be ZS1=50 ohms by default." The trap: an unmarked band is
//  TerminationSet.UnmarkedBandOhms (a near-short), so simply deleting the S1 marker would have
//  changed the circuit the moment it went away. The fix keeps the Source band-1 TERMINATION at 50 Ω
//  even with no marker on it — this file pins that the view (Markers) and the model (Terminations)
//  really are independent here.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaDefaultMarkerSetTests
{
    [Fact]
    public void AFreshDocument_HasNoSourceMarker_AndExactlyThreeMarkersTotal()
    {
        var vm = new HarmonicaViewModel();

        Assert.Equal(3, vm.Markers.Count);
        Assert.DoesNotContain(vm.Markers, m => m.Side == TerminationSideKind.Source);
        Assert.Equal(new[] { ("L", 1), ("L", 2), ("L", 3) },
            vm.Markers.Select(m => (m.Side == TerminationSideKind.Source ? "S" : "L", m.Band)));
    }

    [Fact]
    public void AFreshDocument_SourceBand1Is50Ohms_EvenWithNoMarker()
    {
        var vm = new HarmonicaViewModel();

        Assert.Equal(new Complex(50, 0), vm.Terminations.Z(TerminationSide.Source, 1));
        Assert.Equal(new Complex(TerminationSet.UnmarkedBandOhms, 0), vm.Terminations.Z(TerminationSide.Source, 2));
    }

    [Fact]
    public void AFreshDocument_L1IsUnchanged_At80Plus10j()
    {
        var vm = new HarmonicaViewModel();
        var l1 = vm.Markers.Single(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        Assert.Equal(new Complex(80, 10), vm.Terminations.Z(TerminationSide.Load, 1));
        Assert.Equal(HarmonicaDataSet.GammaOf(new Complex(80, 10), vm.Model.Settings.Z0), l1.Gamma);
    }

    [Fact]
    public void AddingTheS1Marker_DoesNotChangeTheCircuit()
    {
        var vm = new HarmonicaViewModel();
        var before = vm.Terminations.Z(TerminationSide.Source, 1);

        var s1 = vm.AddMarkerBand(TerminationSideKind.Source, 1);

        Assert.Equal(HarmonicaDataSet.GammaOf(new Complex(50, 0), vm.Model.Settings.Z0), s1.Gamma);
        Assert.Equal(before, vm.Terminations.Z(TerminationSide.Source, 1));
    }

    [Fact]
    public void RemovingTheS1Marker_AlsoDoesNotChangeTheCircuit()
    {
        var vm = new HarmonicaViewModel();
        var s1 = vm.AddMarkerBand(TerminationSideKind.Source, 1);
        var before = vm.Terminations.Z(TerminationSide.Source, 1);

        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Source, 1));

        Assert.Equal(before, vm.Terminations.Z(TerminationSide.Source, 1));
        Assert.DoesNotContain(vm.Markers, m => m.Side == TerminationSideKind.Source);
    }

    [Fact]
    public void RemovingL1_TheFundamentalOnTheLoadSide_AlsoLeavesTheTerminationInPlace()
    {
        // R8B §3.3 — band 1 is removable on BOTH sides now, and neither side's removal moves the
        // circuit. Previously this refused outright.
        var vm = new HarmonicaViewModel();
        var l1Before = vm.Terminations.Z(TerminationSide.Load, 1);

        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Load, 1));

        Assert.Equal(l1Before, vm.Terminations.Z(TerminationSide.Load, 1));
        Assert.DoesNotContain(vm.Markers, m => m.Side == TerminationSideKind.Load && m.Band == 1);
    }

    [Fact]
    public void ALoadedCharmFile_WithSourceMarkers_IsUnaffected()
    {
        // The constructor's new default only ever runs for a BRAND NEW document —
        // RebuildMarkersFromTerminations (the load path) replaces Markers wholesale from whatever
        // TerminationSet the file actually carried, independent of this brief's change. Round-tripped
        // through the real .charm save/load path rather than calling the private rebuild directly.
        var saved = new HarmonicaViewModel();
        saved.SetMarkerImpedance(saved.AddMarkerBand(TerminationSideKind.Source, 1), new Complex(25, 0));
        saved.SetMarkerImpedance(saved.AddMarkerBand(TerminationSideKind.Source, 2), new Complex(30, -5));
        string json = saved.ToCharmJson();

        var loaded = new HarmonicaViewModel();
        var unresolved = loaded.LoadCharm(json, baseDirectory: null);
        Assert.Empty(unresolved);

        Assert.Contains(loaded.Markers, m => m.Side == TerminationSideKind.Source && m.Band == 1);
        Assert.Contains(loaded.Markers, m => m.Side == TerminationSideKind.Source && m.Band == 2);
        Assert.Equal(new Complex(25, 0), loaded.Terminations.Z(TerminationSide.Source, 1));
    }
}
