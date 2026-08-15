// ================================================================
//  InterpolatedOptimumTests.cs — §2A (R-h9b-15/16/17) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class InterpolatedOptimumTests(ITestOutputHelper output)
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

    private static TerminationSet Terms(CircuitModel m)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return t;
    }

    // ══ R-h9b-15 — resolution independence: 96 vs 256 agree ═════════════════════════════════

    [Fact]
    public void InterpolatedArgmax_AgreesCloselyBetweenRaster96And256()
    {
        var model = Model();
        var ctx = HarmonicaContext.Create(model, Settings);
        var grid = new ContourGrid();
        grid.Build(ctx, Terms(model), ContourGrid.RingGrid(rings: 3, spokes: 12, maxGamma: 0.75),
                  TerminationSide.Load, 1);

        // R8A §6 — excludeHoleDiscs: true, explicitly: an optimum search must never extrapolate into
        // a hole (Raster's own default flipped to spanning holes; see ContourGrid's own doc comment).
        var r96  = grid.Raster(GridMetric.PoutDbm, 96,  excludeHoleDiscs: true);
        var r256 = grid.Raster(GridMetric.PoutDbm, 256, excludeHoleDiscs: true);

        var at96  = grid.InterpolatedArgmax(GridMetric.PoutDbm, r96);
        var at256 = grid.InterpolatedArgmax(GridMetric.PoutDbm, r256);

        Assert.NotNull(at96);
        Assert.NotNull(at256);

        double sep = (at96!.Value.Gamma - at256!.Value.Gamma).Magnitude;
        output.WriteLine($"argmax at raster 96: Γ={at96.Value.Gamma:G6}, value={at96.Value.Value:F4}");
        output.WriteLine($"argmax at raster 256: Γ={at256.Value.Gamma:G6}, value={at256.Value.Value:F4}");
        output.WriteLine($"separation = {sep:E4} Γ, value difference = {Math.Abs(at96.Value.Value - at256.Value.Value):E4}");

        // The measured agreement IS the gate — proof the refinement is real rather than a dressed-up
        // cell centre (a bare cell-centre answer would move by roughly the raster's own cell size,
        // ~2/96 ≈ 0.021 Γ here; the refined answer should be far tighter than that).
        Assert.True(sep < 0.01, $"the two rasters' interpolated argmax differ by {sep:E4} Γ");
    }

    // ══ R-h9b-15 — respects the support mask ═════════════════════════════════════════════════

    [Fact]
    public void InterpolatedArgmax_NeverLeavesTheSupportedRegion()
    {
        var model = Model();
        var ctx = HarmonicaContext.Create(model, Settings);
        var grid = new ContourGrid();
        grid.Build(ctx, Terms(model), ContourGrid.RingGrid(rings: 3, spokes: 12, maxGamma: 0.75),
                  TerminationSide.Load, 1);

        var raster = grid.Raster(GridMetric.PoutDbm, 128, excludeHoleDiscs: true);   // R8A §6
        var argmax = grid.InterpolatedArgmax(GridMetric.PoutDbm, raster);
        Assert.NotNull(argmax);

        // A bounding-box check on the CONVERGED points (public API only — ContourGrid.ConvexHull and
        // InSupport's own hull/hole-radius arguments are internal). Weaker than true hull membership,
        // but still a real regression pin: the whole ring's converged points are inside |Γ| = 0.75, so
        // an argmax that escaped the mask by any real margin would trip it.
        double minRe = grid.Points.Where(p => !p.IsHole).Min(p => p.Gamma.Real);
        double maxRe = grid.Points.Where(p => !p.IsHole).Max(p => p.Gamma.Real);
        double minIm = grid.Points.Where(p => !p.IsHole).Min(p => p.Gamma.Imaginary);
        double maxIm = grid.Points.Where(p => !p.IsHole).Max(p => p.Gamma.Imaginary);

        output.WriteLine($"argmax Γ={argmax!.Value.Gamma:G6}, converged bbox Re∈[{minRe:F3},{maxRe:F3}] " +
                         $"Im∈[{minIm:F3},{maxIm:F3}]");
        Assert.InRange(argmax.Value.Gamma.Real,      minRe, maxRe);
        Assert.InRange(argmax.Value.Gamma.Imaginary, minIm, maxIm);
    }

    // ══ "no optimum", never a cross at the origin ════════════════════════════════════════════

    [Fact]
    public void AllHoles_ProducesNoOptimum_NotTheOrigin()
    {
        // A grid whose every point is a hole: PinMax so low nothing compresses.
        var model = Model() with
        {
            Settings = Model().Settings with { PinMaxDbm = -20 },
        };
        var ctx = HarmonicaContext.Create(model, Settings);
        var grid = new ContourGrid();
        grid.Build(ctx, Terms(model), ContourGrid.RingGrid(rings: 2, spokes: 8, maxGamma: 0.5),
                  TerminationSide.Load, 1);

        Assert.Equal(grid.Points.Count, grid.HoleCount);

        var raster = grid.Raster(GridMetric.PoutDbm, 64, excludeHoleDiscs: true);   // R8A §6
        var argmax = grid.InterpolatedArgmax(GridMetric.PoutDbm, raster);
        Assert.Null(argmax);
    }

    [Fact]
    public void EmptyGrid_ProducesNoOptimum()
    {
        var grid = new ContourGrid();
        var raster = grid.Raster(GridMetric.PoutDbm, 32, excludeHoleDiscs: true);   // R8A §6
        Assert.Null(grid.InterpolatedArgmax(GridMetric.PoutDbm, raster));
    }
}
