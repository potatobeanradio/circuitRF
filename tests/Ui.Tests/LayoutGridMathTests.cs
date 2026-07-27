using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1a gate 7: grid decimation never draws sub-pixel, and degrades to "no grid" ──

public class LayoutGridMathTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(17)]
    [InlineData(123)]
    public void CeilingNiceStep_ReturnsValueFromOneTwoFiveSequence_AndIsGreaterOrEqual(double x)
    {
        double step = LayoutGridMath.CeilingNiceStep(x);
        Assert.True(step >= x - 1e-9, $"step {step} should be >= {x}");

        // Verify it is of the form {1,2,5} * 10^k.
        double mag = System.Math.Pow(10, System.Math.Round(System.Math.Log10(step / 1.0)));
        bool isNice = new[] { 1.0, 2.0, 5.0 }.Any(m =>
        {
            for (int k = -6; k <= 12; k++)
            {
                double candidate = m * System.Math.Pow(10, k);
                if (System.Math.Abs(candidate - step) < candidate * 1e-6) return true;
            }
            return false;
        });
        Assert.True(isNice, $"{step} is not a {{1,2,5}}x10^k value");
    }

    [Fact]
    public void ComputeGridPitch_WideZoomSweep_NeverBelowThreshold_OrNull()
    {
        const long snapDbu = 1000; // 1 um snap
        // Sweep across 20 decades of zoom (extremely zoomed out to extremely zoomed in).
        for (int exp = -12; exp <= 8; exp++)
        {
            double zoom = System.Math.Pow(10, exp); // px per DBU
            var pitch = LayoutGridMath.ComputeGridPitch(snapDbu, zoom, minPixelSpacing: 8.0);

            if (pitch is null) continue; // "disappears rather than degenerating" — acceptable

            double px = pitch.Value * zoom;
            Assert.True(px >= 8.0 - 1e-6, $"zoom={zoom:E2} pitch={pitch} px={px} fell below the 8px floor");
        }
    }

    [Fact]
    public void ComputeGridPitch_AlreadyCoarseEnough_ReturnsSnapPitchUnchanged()
    {
        const long snapDbu = 1_000_000; // 1 mm
        double zoom = 1.0; // 1 px per DBU -> absurdly coarse already
        var pitch = LayoutGridMath.ComputeGridPitch(snapDbu, zoom);
        Assert.Equal(snapDbu, pitch);
    }

    [Fact]
    public void ComputeGridPitch_ZeroOrNegativeInputs_ReturnsNull()
    {
        Assert.Null(LayoutGridMath.ComputeGridPitch(0, 1.0));
        Assert.Null(LayoutGridMath.ComputeGridPitch(-100, 1.0));
        Assert.Null(LayoutGridMath.ComputeGridPitch(1000, 0));
        Assert.Null(LayoutGridMath.ComputeGridPitch(1000, -1));
    }

    [Fact]
    public void ComputeGridPitch_ExtremelyZoomedOut_DisappearsRatherThanDegenerating()
    {
        // At zoom this small, even a pitch spanning the entire long range would not reach 8px —
        // the grid must vanish, not return some absurd or overflowing pitch.
        var pitch = LayoutGridMath.ComputeGridPitch(1, 1e-30);
        Assert.Null(pitch);
    }

    [Fact]
    public void MajorGridStep_IsFiveMinorSteps()
    {
        Assert.Equal(5, LayoutGridMath.MajorGridStepCount);
    }

    // ── Ruler tick step (gate 8) ──────────────────────────────────────────────

    [Fact]
    public void RulerTickStep_NeverProducesCollidingLabels_AcrossZoomSweep()
    {
        for (int exp = -6; exp <= 6; exp++)
        {
            double zoom = System.Math.Pow(10, exp);
            long stepDbu = LayoutGridMath.ComputeRulerTickStepDbu(zoom, LayoutUnit.Mil, 25_400, minLabelPixelSpacing: 60.0);
            Assert.True(stepDbu > 0);
            double px = stepDbu * zoom;
            Assert.True(px >= 60.0 * 0.999, $"zoom={zoom:E2} stepDbu={stepDbu} px={px} would collide");
        }
    }

    [Fact]
    public void RulerTickStep_DifferentDisplayUnits_BothProduceValidExactSteps()
    {
        long stepUm  = LayoutGridMath.ComputeRulerTickStepDbu(0.5, LayoutUnit.Um, 1000);
        long stepMil = LayoutGridMath.ComputeRulerTickStepDbu(0.5, LayoutUnit.Mil, 25_400);
        Assert.True(stepUm > 0);
        Assert.True(stepMil > 0);
    }
}
