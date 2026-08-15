using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-harmonicarf-r3b §3.1 — the loadpull hole DIAGNOSTIC. Deliverable one is a measurement, not a
/// fix: for the shipped default document on a 5×12 ring grid, report per-Γ <see cref="PinStopReason"/>,
/// solve count, and which STAGE failed (tickle/PinStart/bracket/secant, and at what Pin); the totals;
/// the same grid with the warm start and Pin hint disabled; and, for three failing points, whether
/// <see cref="PinSearch.Sweep"/> converges at the same termination.
///
/// <para><b>R9C — this diagnosis is CONFIRMED AND CLOSED.</b> <see cref="ContourGrid.Build"/> and
/// <see cref="ContourGrid.BuildParallel"/> no longer call <see cref="PinSearch.Run"/> for a grid point
/// at all — both now walk <see cref="PinSearch.Sweep"/>'s ladder (R9C §3), which is exactly the fix
/// this file's own §3's finding pointed at. <see cref="FailingPoints_RunUnderSweep_ForComparison"/>'s
/// own output labels ("Run()=...") are therefore now a HISTORICAL comparison — <c>p.Result</c> there
/// comes from the grid's OWN (now ladder-based) search, not literally <c>PinSearch.Run</c>; kept
/// unrenamed so this file still reads as the record of what was found and fixed. A residual handful of
/// genuine holes remain on this file's own larger/denser 61-point grid (2/61 at maxGamma 0.8, measured)
/// — this project's 37-point shipped-default fixture is the one R9C's own gate holds to zero
/// (<c>ContourGridTests.R9C_ShippedDefault_3x12Grid_HasNoHoles</c>); see <c>src/Harmonica/RESOLVED.md</c>'s
/// own R9C entry for the measurement this file's finding fed into.</para>
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class LoadpullHoleDiagnosticTests(ITestOutputHelper output)
{
    private const string I1Expr = "_v1/50";
    private const string I2Expr =
        "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
        "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";

    private static CircuitModel DefaultModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = I1Expr,
                ["I[2,0]"] = I2Expr,
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34, PinStepDbm = 1.0,
        },
    };

    private static TerminationSet DefaultTerminations(int harmonics)
    {
        var t = new TerminationSet(harmonics);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        return t;
    }

    private sealed record PointDiagnosis(
        Complex Gamma, PinStopReason Reason, int Solves,
        PinSearchStage? FailedStage, double? FailedPinDbm);

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(0.8)]
    [InlineData(0.85)]
    [InlineData(0.9)]
    public void HoleHistogram_5x12RingGrid(double maxGamma)
    {
        var model = DefaultModel();
        var ctx = HarmonicaContext.Create(model);
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        var gammaGrid = ContourGrid.RingGrid(5, 12, maxGamma);

        var probesByGamma = new Dictionary<Complex, List<PinSearchProbe>>();
        var grid = new ContourGrid();
        grid.Build(ctx, terms, gammaGrid, onPointProbe: (g, p) =>
        {
            if (!probesByGamma.TryGetValue(g, out var list)) probesByGamma[g] = list = [];
            list.Add(p);
        });

        var diagnoses = new List<PointDiagnosis>();
        foreach (var pt in grid.Points)
        {
            var probes = probesByGamma.TryGetValue(pt.Gamma, out var l) ? l : [];
            PinSearchStage? failStage = null; double? failPin = null;
            if (pt.Result.Reason == PinStopReason.NonConvergence && probes.Count > 0)
            {
                var last = probes[^1];
                failStage = last.Stage; failPin = last.PinDbm;
            }
            diagnoses.Add(new PointDiagnosis(pt.Gamma, pt.Result.Reason, pt.Result.Solves, failStage, failPin));
        }

        int converged = diagnoses.Count(d => d.Reason == PinStopReason.Compression);
        int pinMax = diagnoses.Count(d => d.Reason == PinStopReason.PinMax);
        int nonConv = diagnoses.Count(d => d.Reason == PinStopReason.NonConvergence);

        output.WriteLine($"═══ MaxGamma = {maxGamma}, {gammaGrid.Length}-point ring grid, shipped default ═══");
        output.WriteLine($"converged={converged}  PinMax={pinMax}  NonConvergence={nonConv}  " +
                         $"(converged fraction {converged / (double)gammaGrid.Length:P1})");

        var nonConvPoints = diagnoses.Where(d => d.Reason == PinStopReason.NonConvergence).ToList();
        if (nonConvPoints.Count > 0)
        {
            output.WriteLine("");
            output.WriteLine("NonConvergence points — failing stage and Pin level:");
            var byStage = nonConvPoints.GroupBy(d => d.FailedStage);
            foreach (var g in byStage)
                output.WriteLine($"  stage={g.Key,-10} count={g.Count()}  " +
                                 $"Pin range [{g.Min(d => d.FailedPinDbm):F1}, {g.Max(d => d.FailedPinDbm):F1}] dBm");
        }

        var pinMaxPoints = diagnoses.Where(d => d.Reason == PinStopReason.PinMax).ToList();
        if (pinMaxPoints.Count > 0)
            output.WriteLine($"PinMax points: {pinMaxPoints.Count} — never compressed by 3 dB before {model.Settings.PinMaxDbm} dBm");

        // §3.2's contagion check — a contiguous arc of holes (rather than scattered ones) is what
        // "the isolines do not close above 200 Ω" would look like.
        var holeGammas = diagnoses.Where(d => d.Reason != PinStopReason.Compression)
                                   .Select(d => d.Gamma).ToList();
        if (holeGammas.Count > 0)
        {
            double meanMag = holeGammas.Average(g => g.Magnitude);
            double meanAngDeg = holeGammas.Average(g => g.Phase * 180.0 / Math.PI);
            output.WriteLine($"hole centroid: |Γ|≈{meanMag:F2} ∠{meanAngDeg:F0}° " +
                             "(a tight angular cluster here is the 'arc' contagion pattern §3.2 names)");
        }
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void ColdEveryPoint_NoWarmStart_NoPinHint_ForComparison()
    {
        var model = DefaultModel();
        var ctxWarm = HarmonicaContext.Create(model);
        var ctxCold = HarmonicaContext.Create(model);
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        var gammaGrid = ContourGrid.RingGrid(5, 12, 0.8);

        var warmGrid = new ContourGrid();
        warmGrid.Build(ctxWarm, terms, gammaGrid);

        // Cold: every point solved directly with no warm start and no Pin hint — bypasses
        // ContourGrid's own neighbour-seeding entirely.
        int coldConverged = 0, coldPinMax = 0, coldNonConv = 0, coldSolves = 0;
        foreach (var gamma in gammaGrid)
        {
            var z = 50.0 * (Complex.One + gamma) / (Complex.One - gamma);
            var t = terms.Clone();
            t.Set(TerminationSide.Load, 1, z);
            var result = PinSearch.Run(ctxCold, t, warmStart: null, pinHintDbm: null);
            coldSolves += result.Solves;
            switch (result.Reason)
            {
                case PinStopReason.Compression:    coldConverged++; break;
                case PinStopReason.PinMax:         coldPinMax++; break;
                case PinStopReason.NonConvergence: coldNonConv++; break;
            }
        }

        output.WriteLine($"═══ warm-start comparison, {gammaGrid.Length}-point grid ═══");
        output.WriteLine($"WARM (neighbour-seeded): converged={warmGrid.ConvergedCount}  " +
                         $"holes={warmGrid.HoleCount}  solves={warmGrid.SolveCount}");
        output.WriteLine($"COLD (no warm start/hint): converged={coldConverged}  PinMax={coldPinMax}  " +
                         $"NonConvergence={coldNonConv}  solves={coldSolves}");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void FailingPoints_RunUnderSweep_ForComparison()
    {
        // §3's own sharpest tool: at a load the owner reports converging under tier-A's ladder but
        // holing under the grid's Run(), Sweep should succeed where Run failed. Reproduce that
        // directly: take up to three NonConvergence/PinMax points from the 0.8 grid and re-drive them
        // with PinSearch.Sweep at the SAME termination.
        var model = DefaultModel();
        var ctx = HarmonicaContext.Create(model);
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        var gammaGrid = ContourGrid.RingGrid(5, 12, 0.8);

        var grid = new ContourGrid();
        grid.Build(ctx, terms, gammaGrid);

        var failing = grid.Points.Where(p => p.IsHole).Take(3).ToList();
        output.WriteLine($"═══ {failing.Count} failing point(s) re-driven with PinSearch.Sweep ═══");
        if (failing.Count == 0)
        {
            output.WriteLine("no holes on this grid — nothing to compare (report this explicitly rather " +
                             "than silently passing).");
            return;
        }

        foreach (var p in failing)
        {
            var t = terms.Clone();
            t.Set(TerminationSide.Load, 1, p.Z);
            var sweep = PinSearch.Sweep(ctx, t, model.Settings.PinStartDbm, model.Settings.PinMaxDbm,
                                        model.Settings.PinStepDbm);
            output.WriteLine($"Γ={p.Gamma,-24} Z={p.Z,-24} Run()={p.Result.Reason,-16} " +
                             $"Sweep()={sweep.Reason,-16} " +
                             (sweep.Compressed
                                 ? $"compresses at Pin={sweep.SweepCompression!.PinDbm:F1} dBm"
                                 : "does not compress"));
        }
    }
}
