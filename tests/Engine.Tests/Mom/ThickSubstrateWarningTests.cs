// ICF-4 Part B, gate 3 — the warnings of R-icf4-3 and R-icf4-5 name the port. On the functions, not a solve: the
// measurement behind both is src/Engine/Mom/RESOLVED.md §ICF-4 Part A.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class ThickSubstrateWarningTests
{
    private static string Um(double m) => $"{m * 1e6:G4} µm";

    /// <summary>R-icf4-3: a grounded Uncalibrated port 756 µm above the plane passes a quarter wavelength in the
    /// silicon at ~28.8 GHz; the warning names it, its height and the points past it, and says nothing below.</summary>
    [Fact]
    public void ATallUncalibratedPort_IsWarnedByNumber_OnlyWhereItPassesAQuarterWavelength()
    {
        var p = ThickSiliconCoilFixture.Coil(40e9);
        var stack = p.EffectiveStack;
        double z = ThickSiliconCoilFixture.TopZ;
        var port = (Number: 2, HeightM: z, OpticalM: PlanarThickSubstrate.OpticalHeightM(stack, z));

        string? w = PlanarThickSubstrate.TallPortWarning([port], [20e9, 32e9, 40e9], Um);
        Assert.NotNull(w);
        Assert.Contains("port 2 spans 756 µm", w);
        Assert.Contains("32 GHz and 40 GHz", w);
        Assert.DoesNotContain("20 GHz", w);

        Assert.Null(PlanarThickSubstrate.TallPortWarning([port], [10e9, 20e9], Um));
    }

    /// <summary>R-icf4-5: the NOT PASSIVE warning names the port whose incident wave gains, and on a substrate past a
    /// tenth of a wavelength says how thick it is instead of "not identified".</summary>
    [Fact]
    public void TheNonPassiveWarning_NamesTheCarryingPort_AndTheSubstratesElectricalThickness()
    {
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = 0.3; s[0, 1] = 0.05; s[1, 0] = 0.05; s[1, 1] = 1.1;   // port 2 reflects more than it receives
        var carried = PlanarThickSubstrate.CarryingPorts(s, [1, 2]);
        Assert.Equal(2, Assert.Single(carried).Number);

        double z = ThickSiliconCoilFixture.TopZ;
        string? thick = PlanarThickSubstrate.ThickSubstrateSentence(
            z, PlanarThickSubstrate.OpticalHeightM(ThickSiliconCoilFixture.Coil(40e9).EffectiveStack, z), 32e9);
        Assert.NotNull(thick);
        Assert.Contains("0.277 wavelengths at 32 GHz", thick);

        string cause = PlanarSolve.NonPassivityCause(0.1, 0, null, thick);
        Assert.DoesNotContain("not identified", cause);
        string note = PlanarSolve.PassivityNote(
            [new PlanarPassivityExcess(32e9, 1.1) { CarriedBy = carried }], 3, cause)!;
        Assert.Contains("carried by port 2", note);
        Assert.Contains("electrically thick", note);

        // A thin substrate keeps the old sentence.
        Assert.Null(PlanarThickSubstrate.ThickSubstrateSentence(100e-6, 100e-6 * Math.Sqrt(12.9), 10e9));
    }
}
