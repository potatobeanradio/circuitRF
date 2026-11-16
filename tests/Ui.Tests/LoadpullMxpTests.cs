using CircuitRF.Design.Circuit;
using CircuitRF.Design.Workspace;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Loadpull example's MXP and its contours. Its measured Pout at 3 dB compression nearly ties at its peak (0.2∠±30° and 0.2∠0°),
/// and the surface fitted through them peaks on the real axis between the nodes. MXP is that fitted peak, found over the measured
/// region, so it sits at the centre of the contours drawn from the same fit: the measured argmax is +30°, a tie-break, and the old
/// search (a VSWR 1.2 circle round it) never reached the axis. Default contour levels sit strictly inside the surface's range, so
/// no level is the peak's own value and none draws a ring round one raster node there.
/// </summary>
public sealed class LoadpullMxpTests(ITestOutputHelper output)
{
    [Fact]
    public void TheExamplesMxp_IsTheFittedPeak_OnTheAxisOfItsSymmetricSurface()
    {
        string root = ExampleWorkspaces.ResolveRoot() ?? throw new InvalidOperationException("no examples/ tree");
        var circuit = PreparedCircuit.FromSchematic(Path.Combine(root, "Loadpull", "Loadpull", "schematic", "Loadpull.csch"));
        var run = CircuitEvaluation.Execute(CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest()));
        Assert.Empty(run.Errors);
        var surface = new LoadpullSurface(run.GroupedResults!, "LP1");
        var fit = surface.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.NotNull(fit);
        var (mxp, _) = surface.RecommendedMxx(fit!);
        Assert.NotNull(mxp);
        var p = mxp!.Interpolated;
        double peak = fit!.Rbf.Evaluate(p.Real, p.Imaginary);
        output.WriteLine($"measured argmax {mxp.Measured:F3}, fitted peak {p:F3} at {peak:F3} dBm, measured node there {fit.Rbf.Evaluate(mxp.Measured.Real, mxp.Measured.Imaginary):F3}");
        Assert.True(Math.Abs(p.Imaginary) < 0.02, $"MXP at {p:F3} is off the real axis of a surface symmetric about it");
        Assert.True(peak >= fit.Rbf.Evaluate(mxp.Measured.Real, mxp.Measured.Imaginary), "MXP is below the surface at the measured argmax");

        var grid = surface.Resample(fit);
        var levels = ContourExtractor.LevelsBetween(grid, 10).Levels;
        double max = grid.Values.Where(double.IsFinite).Max(), min = grid.Values.Where(double.IsFinite).Min();
        Assert.All(levels, l => Assert.True(l > min && l < max, $"level {l:F4} is not strictly inside [{min:F4}, {max:F4}]"));
    }
}
