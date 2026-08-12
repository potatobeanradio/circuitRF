// ================================================================
//  HarmonicaOptimumSolveTests.cs — §2A (R-h9b-16/17) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaOptimumSolveTests(ITestOutputHelper output)
{
    [Fact]
    public void FullQualityFrame_SolvesTheOptimum_AndPublishesZin()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 64,
                                                    Quality = FrameQuality.Full });

        var optimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(optimum);
        Assert.NotNull(optimum!.Solved);
        Assert.NotNull(optimum.Published);
        Assert.True(optimum.Published!.Cubes.ContainsKey("Zin"), "the resolved optimum must publish Zin (§4.5.4)");

        output.WriteLine($"Power optimum Γ={optimum.Gamma:G6}, value={optimum.MetricValue:F3}, " +
                         $"solved Pin={optimum.Solved!.PavlDbm:F2} dBm");
    }

    [Fact]
    public void DegradedRung_TracksTheGlyphPosition_ButDoesNotSolveFoms()
    {
        var vm = new HarmonicaViewModel();
        // A coarse, dragging-style rung: the glyph should still track the interpolated surface (cheap,
        // no HB solve), but the expensive FOM drive-up must not run.
        vm.SolveFrame(new HarmonicaSolver.Options
        {
            Rings = 3, Spokes = 12, RasterResolution = 64,
            Quality = FrameQuality.CoarseGrid,
        });

        var optimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(optimum);
        Assert.Null(optimum!.Solved);
        Assert.Null(optimum.Published);
    }

    [Fact]
    public void SkipContoursFrame_HasNoOptimum_NotAStaleOne()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        Assert.Null(vm.Frame.SmithPower.Optimum);
        Assert.Null(vm.Frame.SmithEfficiency.Optimum);
    }

    [Fact]
    public void MeasuredCost_TheOptimumSolvesCostRoughlyTwoDriveUps()
    {
        var vm = new HarmonicaViewModel();

        vm.SolveFrame(new HarmonicaSolver.Options
        {
            Rings = 3, Spokes = 12, RasterResolution = 64, SkipContours = true,
        });
        int gridOnly = vm.LastSolveCount;

        vm.SolveFrame(new HarmonicaSolver.Options
        {
            Rings = 3, Spokes = 12, RasterResolution = 64, Quality = FrameQuality.Full,
        });
        int withOptima = vm.LastSolveCount;

        output.WriteLine($"tier-A-only solves: {gridOnly}; full grid + two optimum drive-ups: {withOptima}");
        Assert.True(withOptima > gridOnly,
            "a full-quality frame with two resolved optima must cost more HB solves than tier A alone");
    }
}
