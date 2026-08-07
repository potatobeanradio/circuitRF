using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// <b>M1/R5 of brief-harmonicarf-h4-h5.</b> §3 item 5: "ms for <c>ContourGrid.Raster</c> at 96×96
/// and 256×256, since D5's whole justification is that the difference is worth 6–8× and it should be
/// confirmed on the real path."
///
/// <para>H0–H3 measured the EXTRACT at both resolutions against a hand-built surface. This measures
/// the whole <c>Raster</c> — the RBF evaluation AND the support-mask test, per raster cell — on a
/// real <see cref="ContourGrid"/>, plus <c>Contours</c> (raster + level set + marching squares), which
/// is what a frame actually pays.</para>
///
/// <para><b>The grid is built for real</b> (61 Γ points through <see cref="PinSearch"/>) so the node
/// set, the hole set and the mask geometry are the ones the shipping path produces. <b>The harmonic
/// order is deliberately K = 3 rather than the shipping 5</b>: the raster cost is a function of the
/// node count, the hole count and the resolution ONLY — no part of it touches the HB solution — so
/// lowering K makes the fixture cheaper to BUILD without moving the number being measured. Said
/// rather than implied.</para>
///
/// <para><b>Taken alone</b>, in the non-parallel benchmark collection, best-of-N — the discipline the
/// brief restates "because this repo has now been bitten by it three times".</para>
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class RasterCostTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>Hero 2's GaN HEMT, coefficients folded in so the fixture needs no globals.</summary>
    private static CircuitModel Model() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
        },
    };

    private static (double MinMs, double MedianMs) TimeBestOf(int reps, Action body)
    {
        body();                                     // warm-up
        var ms = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        return (ms[0], ms[reps / 2]);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void R5_RasterAndContoursAtCoarseAndFullResolution()
    {
        var model = Model();
        var ctx   = HarmonicaContext.Create(model, Settings);

        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));

        // 5 rings × 12 spokes + centre = 61 points — the full user grid of §0.2's own measurement.
        var gammaGrid = ContourGrid.RingGrid(rings: 5, spokes: 12, maxGamma: 0.8);
        Assert.Equal(61, gammaGrid.Length);

        var grid = new ContourGrid();
        var swBuild = Stopwatch.StartNew();
        grid.Build(ctx, terms, gammaGrid);
        swBuild.Stop();

        output.WriteLine($"grid: {grid.Points.Count} Γ points, {grid.ConvergedCount} converged, " +
                         $"{grid.HoleCount} holes, {grid.SolveCount} HB solves " +
                         $"({grid.SolveCount / (double)grid.Points.Count:F1} per point), " +
                         $"built in {swBuild.Elapsed.TotalMilliseconds:F0} ms at K=3");

        // The fit is shared between the two resolutions and between the two metrics — warm it once so
        // the raster numbers are the RASTER, not a first-touch factorization (§6.4.1 item 1, and D6's
        // "fit and solve are timed SEPARATELY").
        _ = grid.Fit(GridMetric.PoutDbm);
        int factorizationsAfterWarm = grid.FactorizationCount;

        foreach (int res in new[] { 96, 256 })
        {
            var (rMin, rMed) = TimeBestOf(7, () => grid.Raster(GridMetric.PoutDbm, res));
            var (cMin, cMed) = TimeBestOf(7, () => grid.Contours(GridMetric.PoutDbm, 10, res));
            output.WriteLine($"R5 Raster   @{res}×{res}: min {rMin:F2} ms, median {rMed:F2} ms");
            output.WriteLine($"R5 Contours @{res}×{res} (raster + 10 levels + extract): " +
                             $"min {cMin:F2} ms, median {cMed:F2} ms");
        }

        // The whole point of D5: the coarse raster must be materially cheaper, and the factorization
        // must not be re-paid per raster — otherwise the drag-time saving is imaginary.
        Assert.Equal(factorizationsAfterWarm, grid.FactorizationCount);

        var (min96,  _) = TimeBestOf(7, () => grid.Raster(GridMetric.PoutDbm, 96));
        var (min256, _) = TimeBestOf(7, () => grid.Raster(GridMetric.PoutDbm, 256));
        output.WriteLine($"R5 coarse-vs-full raster ratio: {min256 / min96:F1}× " +
                         $"(§0.3 item 3 predicts 6–8× from the extract measurement)");
        Assert.True(min256 > min96,
            $"the full raster must cost more than the coarse one ({min256:F2} vs {min96:F2} ms)");
    }
}
