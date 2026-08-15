// ================================================================
//  HarmonicaDefaultMarkersTests.cs — §8 (R-h9b-14) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md, superseded by R8B §3
//
//  R8B §3: "By default, S1 and S2 termination markers are turned off." The fresh-document default is
//  now L1/L2/L3 only — see HarmonicaDefaultMarkerSetTests.cs for the fuller R8B §3 coverage (the
//  Source band-1 termination staying at 50 Ω with no marker, band-1 removal on both sides, etc.). This
//  file keeps the load-path/round-trip cases that are still this file's own subject.
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
    public void ANewDocument_OpensWithExactlyThreeMarkers_L1L2L3_NoSourceMarker()
    {
        var vm = new HarmonicaViewModel();

        Assert.Equal(3, vm.Markers.Count);
        var names = vm.Markers.Select(m => m.Name).ToArray();
        Assert.Equal(["L1", "L2", "L3"], names);
    }

    [Fact]
    public void TheDefaults_ForL2L3_MatchTheUnmarkedBandEpsilon()
    {
        // R-h9r2-1 (§2): S2/L2/L3 default to the SAME unmarked-band epsilon TerminationSet already
        // answers for "no marker at all" (Z = 1e-6 Ω). L1 is the one band this constructor still gives
        // a real starting impedance (80+j10 Ω) — see HarmonicaViewModel's own constructor comment.
        var vm = new HarmonicaViewModel();
        var expected = HarmonicaDataSet.GammaOf(new Complex(TerminationSet.UnmarkedBandOhms, 0),
                                                vm.Model.Settings.Z0);

        foreach (var band in new[] { (TerminationSideKind.Load, 2), (TerminationSideKind.Load, 3) })
        {
            var m = vm.Markers.Single(x => x.Side == band.Item1 && x.Band == band.Item2);
            Assert.Equal(expected.Real,      m.Gamma.Real,      precision: 9);
            Assert.Equal(expected.Imaginary, m.Gamma.Imaginary, precision: 9);
        }
    }

    [Fact]
    public void Band1_IsRemovableOnBothSides_R8BSuperseded()
    {
        // R8B §3.3 supersedes the old "band 1 always refuses" rule: it is removable on both sides now
        // (the termination stays in place — see HarmonicaDefaultMarkerSetTests for that half).
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);

        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Load, 2));
        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Load, 3));
        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Load, 1));
        Assert.True(vm.RemoveMarkerBand(TerminationSideKind.Source, 1));
        Assert.Empty(vm.Markers);
    }

    [Fact]
    public void ALoadedCharmMarkingOnlyS1AndL1_StillProducesExactlyThoseTwoMarkers()
    {
        // §4.2 — RebuildMarkersFromTerminations derives markers from the file's own marked bands; an
        // unmarked band is the ABSENCE of a marker, not a default. The constructor's new L2/L3
        // defaults must not survive a load that never marked them, and a marked S1 must come back.
        var writer = new HarmonicaViewModel();
        writer.RemoveMarkerBand(TerminationSideKind.Load, 2);
        writer.RemoveMarkerBand(TerminationSideKind.Load, 3);
        writer.SetMarkerImpedance(writer.AddMarkerBand(TerminationSideKind.Source, 1), new Complex(25, 0));
        writer.SetMarkerImpedance(writer.Markers.Single(m => m.Band == 1 && m.Side == TerminationSideKind.Load),
                                  new Complex(80, 10));

        string json = writer.ToCharmJson();

        var reader = new HarmonicaViewModel();
        Assert.Equal(3, reader.Markers.Count);          // the fresh document's own R8B §3 defaults, pre-load

        reader.LoadCharm(json, null);

        Assert.Equal(2, reader.Markers.Count);
        Assert.Contains(reader.Markers, m => m is { Side: TerminationSideKind.Source, Band: 1 });
        Assert.Contains(reader.Markers, m => m is { Side: TerminationSideKind.Load,   Band: 1 });
    }
}
