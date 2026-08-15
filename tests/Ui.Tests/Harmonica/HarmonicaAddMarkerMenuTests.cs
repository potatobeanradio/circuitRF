// ================================================================
//  HarmonicaAddMarkerMenuTests.cs — R8B §4
//
//  "Add Load Marker" / "Add Source Marker" on the Smith panel body menu. HarmonicaView cannot be
//  instantiated headlessly (no Avalonia platform), so this drives the extracted selection function
//  directly, in the shape of HarmonicaR6eDialogsAndMenusTests.
// ================================================================

using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Views.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaAddMarkerMenuTests
{
    [Fact]
    public void OnAFreshDocument_TheNextSourceBandIsOne()
    {
        var vm = new HarmonicaViewModel();
        Assert.Equal(1, HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount));
    }

    [Fact]
    public void AfterAdding_TheNextSourceBandIsTwo()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);
        Assert.Equal(2, HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount));
    }

    [Fact]
    public void OnceEveryBandUpToTheHarmonicOrderHasAMarker_ThereIsNoNextBand()
    {
        var vm = new HarmonicaViewModel();
        for (int band = 1; band <= vm.Terminations.HarmonicCount; band++)
            vm.AddMarkerBand(TerminationSideKind.Source, band);

        Assert.Null(HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount));
    }

    [Fact]
    public void OnAFreshDocument_TheLoadSideHasNoNextBand_L1L2L3AlreadyAllHaveMarkers()
    {
        // R8B §3's fresh-document default is L1/L2/L3 — every load band up to K=3 already has a
        // marker, so "Add Load Marker" is disabled from the very first frame.
        var vm = new HarmonicaViewModel();
        Assert.Null(HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Load, vm.Terminations.HarmonicCount));
    }

    [Fact]
    public void TheTwoSidesAreIndependent()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);

        // Adding a source marker must not consume a load band, and vice versa — the load side stays
        // at capacity (null) throughout, untouched by the source-side add.
        Assert.Equal(2, HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount));
        Assert.Null(HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Load, vm.Terminations.HarmonicCount));
    }

    [Fact]
    public void AddingViaAddMarkerBand_MatchesTheMenuSSelection_AndGrowsTheMarkerList()
    {
        var vm = new HarmonicaViewModel();
        int before = vm.Markers.Count;

        int? next = HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount);
        Assert.NotNull(next);
        vm.AddMarkerBand(TerminationSideKind.Source, next!.Value);

        Assert.Equal(before + 1, vm.Markers.Count);
        Assert.Contains(vm.Markers, m => m.Side == TerminationSideKind.Source && m.Band == next.Value);

        // A second add does not re-grow the list once every band is taken.
        while (HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount) is { } band)
            vm.AddMarkerBand(TerminationSideKind.Source, band);

        int countAtCapacity = vm.Markers.Count;
        Assert.Null(HarmonicaView.NextUnusedBand(vm.Markers, TerminationSideKind.Source, vm.Terminations.HarmonicCount));
        Assert.Equal(countAtCapacity, vm.Markers.Count);
    }
}
