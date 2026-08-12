// ================================================================
//  HarmonicaZ0Tests.cs — §3 (R-h9b-6) of brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaZ0Tests(ITestOutputHelper output)
{
    [Fact]
    public void ChangingZ0_LeavesTheImpedanceBitIdentical_ButMovesTheMarkersGamma()
    {
        var vm = new HarmonicaViewModel();
        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 });
        vm.SetMarkerImpedance(marker, new Complex(80, 10));

        var zBefore = vm.Terminations.Z(TerminationSide.Load, marker.Band);
        var gammaBefore = marker.Gamma;

        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyZ0, "75"));

        var zAfter = vm.Terminations.Z(TerminationSide.Load, marker.Band);
        output.WriteLine($"Z before {zBefore}, Z after {zAfter}; Gamma before {gammaBefore}, after {marker.Gamma}");

        Assert.Equal(zBefore, zAfter);              // bit-identical — a Z0 change moves no impedance
        Assert.NotEqual(gammaBefore, marker.Gamma);  // but the marker's Γ re-expresses against the new Z0

        // And the new Γ is exactly what GammaOf(Z, 75) gives.
        Assert.Equal(HarmonicaDataSet.GammaOf(zAfter, 75.0), marker.Gamma);
    }

    [Fact]
    public void ChangingZ0_IsNotStructural()
    {
        var vm = new HarmonicaViewModel();
        string before = vm.Model.StructuralKey;

        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyZ0, "93"));

        Assert.Equal(before, vm.Model.StructuralKey);
        Assert.Equal(93.0, vm.Model.Settings.Z0);
    }

    [Fact]
    public void Z0_RoundTripsThroughCharm_AndAnOlderCharmOpensAt50Ohms()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyZ0, "42"));

        string json = vm.ToCharmJson();

        var vm2 = new HarmonicaViewModel();
        vm2.LoadCharm(json, null);
        Assert.Equal(42.0, vm2.Model.Settings.Z0);

        var vm3 = new HarmonicaViewModel();
        vm3.LoadCharm("""{ "FormatVersion": 1 }""", null);
        Assert.Equal(50.0, vm3.Model.Settings.Z0);
    }

    [Fact]
    public void Z0_MustBePositive()
    {
        var vm = new HarmonicaViewModel();
        Assert.False(vm.ApplyInput(HarmonicaInputs.KeyZ0, "-5"));
        Assert.NotNull(vm.InputError);
        Assert.Equal(50.0, vm.Model.Settings.Z0);
    }
}
