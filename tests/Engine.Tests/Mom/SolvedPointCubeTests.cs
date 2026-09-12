// ================================================================
//  SolvedPointCubeTests.cs — the result says which of its points were SOLVED.
//
//  Owner report, 2026-09-11: a sweep was STOPPED after six points and the plot drew a hundred
//  markers. Both halves of that are separate facts and both are correct on their own — an
//  adaptively sampled sweep publishes the whole requested grid (R-adf-2), and a marker is drawn
//  at every sample a trace has. Together they assert ninety-four solves that never happened.
//
//  This file holds the half that lives in the engine: the published DataSet carries the mask, so
//  the distinction survives the .npy and anything that reads the result can ask. The other half —
//  a marker stands only on a solved point — is in tests/Ui.Tests/DataDisplay.
// ================================================================

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class SolvedPointCubeTests(ITestOutputHelper output)
{
    private const double FHz = 6e9;

    private static double[] Grid(double f0, double f1, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 + (f1 - f0) * i / (n - 1);
        return f;
    }

    private static PlanarKernelResult Run(double[] freqs, PlanarAdaptiveSettings? adaptive)
    {
        var problem = PlanarLineFixtures.Fr4Line(8e-3, FHz);
        return new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), freqs,
            new PlanarSolveSettings(Deembed: false, Adaptive: adaptive));
    }

    private static double[] MaskOf(PlanarKernelResult r)
        => r.Data[$"{PlanarKernel.DiagnosticsGroup}.{SampleProvenance.SolvedCubeName}"].RealValues;

    /// <summary>
    /// The mask names exactly the frequencies the kernel solved — not a count beside the data, a
    /// value per published point, which is the only form a plot can act on.
    /// </summary>
    [Fact]
    public void AnAdaptiveSweep_FlagsEveryPointItSolved_AndOnlyThose()
    {
        double[] freqs = Grid(2e9, 6e9, 17);
        var run = Run(freqs, new PlanarAdaptiveSettings(Tolerance: 1e-2));

        var mask = MaskOf(run);
        Assert.Equal(freqs.Length, mask.Length);

        int solved = 0;
        for (int i = 0; i < freqs.Length; i++)
        {
            bool wasSolved = run.Solve.SolvedFrequencies.Contains(freqs[i]);
            Assert.Equal(wasSolved ? 1.0 : 0.0, mask[i]);
            if (wasSolved) solved++;
        }

        // The fixture has to exercise BOTH kinds of point or this file proves nothing — the same
        // grid and tolerance AdaptiveSweepTests.T1_3 is built on, for that reason.
        Assert.True(solved > 0 && solved < freqs.Length,
                    $"the fixture must model some points: {solved} of {freqs.Length} solved");
        output.WriteLine($"{solved} of {freqs.Length} point(s) solved; the rest are flagged modelled.");
    }

    /// <summary>
    /// With adaptive sampling off the cube is still there and is all ones. Emitted unconditionally
    /// so a reader gets "all of them" rather than a missing cube to interpret — ANT-9's own rule for
    /// PointAddedBySearch, which sits beside this one.
    /// </summary>
    [Fact]
    public void APlainSweep_SaysEveryPointWasSolved_RatherThanSayingNothing()
    {
        double[] freqs = Grid(2e9, 6e9, 5);
        var mask = MaskOf(Run(freqs, adaptive: null));

        Assert.Equal(freqs.Length, mask.Length);
        Assert.All(mask, v => Assert.Equal(1.0, v));
    }

    /// <summary>
    /// The mask rides the same frequency axis the S cube does, because that is what lets a reader
    /// line the two up without matching floating-point values by hand.
    /// </summary>
    [Fact]
    public void TheMaskSitsOnTheSweepsOwnFrequencyAxis()
    {
        double[] freqs = Grid(2e9, 6e9, 5);
        var run  = Run(freqs, adaptive: null);
        var cube = run.Data[$"{PlanarKernel.DiagnosticsGroup}.{SampleProvenance.SolvedCubeName}"];

        Assert.Equal(1, cube.Rank);
        Assert.Equal("freq", cube.Axes[0].Name);
        Assert.Equal("Hz",   cube.Axes[0].Unit);
        Assert.Equal(freqs,  cube.Axes[0].Values);
    }
}
