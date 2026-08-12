// ================================================================
//  HarmonicaDefaultMarkersTests.cs — §8 (R-h9b-14) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaDefaultMarkersTests
{
    [Fact]
    public void ANewDocument_OpensWithExactlyFiveMarkers_S1S2L1L2L3()
    {
        var vm = new HarmonicaViewModel();

        Assert.Equal(5, vm.Markers.Count);
        var names = vm.Markers.Select(m => m.Name).ToArray();
        Assert.Equal(["S1", "S2", "L1", "L2", "L3"], names);
    }

    [Fact]
    public void TheNewDefaults_AreNotLeftAtTheUnmarkedNearShort()
    {
        var vm = new HarmonicaViewModel();

        foreach (var band in new[] { (TerminationSideKind.Source, 2), (TerminationSideKind.Load, 2),
                                     (TerminationSideKind.Load, 3) })
        {
            var m = vm.Markers.Single(x => x.Side == band.Item1 && x.Band == band.Item2);
            Assert.True(m.Gamma.Magnitude < 0.99,
                $"{m.Name} sits on the rim at Γ ≈ 1 — the unmarked near-short is not a useful default");
        }
    }

    [Fact]
    public void Band1_IsStillTheOnlyOneThatRefusesRemoval()
    {
        var vm = new HarmonicaViewModel();

        // The new defaults do not make S2/L2/L3 unremovable — only band 1, on both sides.
        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Source, 2));
        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Load,   2));
        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Load,   3));
        Assert.False(vm.RemoveMarkerBand(TerminationSideKind.Source, 1));
        Assert.False(vm.RemoveMarkerBand(TerminationSideKind.Load,   1));
        Assert.Equal(2, vm.Markers.Count);
    }

    [Fact]
    public void ALoadedCharmMarkingOnlyS1AndL1_StillProducesExactlyThoseTwoMarkers()
    {
        // §4.2 — RebuildMarkersFromTerminations derives markers from the file's own marked bands; an
        // unmarked band is the ABSENCE of a marker, not a default. The constructor's new S2/L2/L3
        // defaults must not survive a load that never marked them.
        var writer = new HarmonicaViewModel();
        foreach (var band in new[] { (TerminationSideKind.Source, 2), (TerminationSideKind.Load, 2),
                                     (TerminationSideKind.Load, 3) })
            writer.RemoveMarkerBand(band.Item1, band.Item2);
        writer.SetMarkerImpedance(writer.Markers.Single(m => m.Band == 1 && m.Side == TerminationSideKind.Source),
                                  new Complex(25, 0));
        writer.SetMarkerImpedance(writer.Markers.Single(m => m.Band == 1 && m.Side == TerminationSideKind.Load),
                                  new Complex(80, 10));

        string json = writer.ToCharmJson();

        var reader = new HarmonicaViewModel();
        Assert.Equal(5, reader.Markers.Count);          // the fresh document's own defaults, pre-load

        reader.LoadCharm(json, null);

        Assert.Equal(2, reader.Markers.Count);
        Assert.Contains(reader.Markers, m => m is { Side: TerminationSideKind.Source, Band: 1 });
        Assert.Contains(reader.Markers, m => m is { Side: TerminationSideKind.Load,   Band: 1 });
    }
}
