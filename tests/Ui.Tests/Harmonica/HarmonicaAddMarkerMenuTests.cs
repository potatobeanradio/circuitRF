// ================================================================
//  HarmonicaAddMarkerMenuTests.cs — R8B §4
//
//  "Add Load Marker" / "Add Source Marker" on the Smith panel body menu. HarmonicaView cannot be
//  instantiated headlessly (no Avalonia platform), so this drives the extracted selection function
//  directly, in the shape of HarmonicaR6eDialogsAndMenusTests.
// ================================================================

using System.Linq;
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

    // R9A §1 — the markers a Smith panel DRAWS are the frame's own snapshot, not vm.Markers. Adding
    // or removing a band must re-stamp that snapshot immediately, with no frame published in between,
    // or the new/removed marker is fully hit-testable and completely invisible.

    [Fact]
    public void AddingASourceBandMarker_WithNoFramePublished_AppearsInBothPanelsSnapshotsImmediately()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);

        Assert.Contains(vm.Frame.SmithPower.Markers,
            m => m.Side == TerminationSideKind.Source && m.Band == 1);
        Assert.Contains(vm.Frame.SmithEfficiency.Markers,
            m => m.Side == TerminationSideKind.Source && m.Band == 1);
    }

    [Fact]
    public void RemovingABandTwoMarker_WithNoFramePublished_LeavesBothPanelsSnapshotsImmediately()
    {
        var vm = new HarmonicaViewModel();
        var marker = vm.Markers.Single(m => m.Side == TerminationSideKind.Load && m.Band == 2);

        vm.RemoveMarkerBand(TerminationSideKind.Load, 2);

        Assert.DoesNotContain(vm.Frame.SmithPower.Markers, m => ReferenceEquals(m, marker));
        Assert.DoesNotContain(vm.Frame.SmithEfficiency.Markers, m => ReferenceEquals(m, marker));
    }

    [Fact]
    public void SyncingTheMarkerSnapshot_DoesNotDisturbTheContourLayer()
    {
        var vm = new HarmonicaViewModel();
        var contours = vm.Frame.SmithPower.Contours;
        var gridPoints = vm.Frame.SmithPower.GridPoints;
        var optimum = vm.Frame.SmithPower.Optimum;

        vm.AddMarkerBand(TerminationSideKind.Source, 1);

        Assert.Same(contours, vm.Frame.SmithPower.Contours);
        Assert.Same(gridPoints, vm.Frame.SmithPower.GridPoints);
        Assert.Same(optimum, vm.Frame.SmithPower.Optimum);
    }
}
