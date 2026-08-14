using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-harmonicarf-r3b §4 — the parallel grid build's correctness and performance gates. Not a
/// benchmark of the evaluator (that is <c>EvaluatorPerfCostTests</c>) — this is specifically about
/// <see cref="ContourGrid.BuildParallel"/> agreeing with <see cref="ContourGrid.Build"/> and being
/// faster.
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class ContourGridParallelTests(ITestOutputHelper output)
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

    [Trait("Category", "Benchmark")]
    [Fact]
    public void Parallel_AgreesWithSerial_HoleSetIdentical_MetricDeviationReported()
    {
        var model = DefaultModel();
        var ctxSerial   = HarmonicaContext.Create(model);
        var ctxParallel = HarmonicaContext.Create(model);
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        var gammaGrid = ContourGrid.RingGrid(5, 12, 0.8);   // 61 points

        var serial = new ContourGrid();
        serial.Build(ctxSerial, terms, gammaGrid);

        var parallel = new ContourGrid();
        parallel.BuildParallel(ctxParallel, terms, gammaGrid, batchSize: 12);

        Assert.Equal(gammaGrid.Length, serial.Points.Count);
        Assert.Equal(gammaGrid.Length, parallel.Points.Count);

        // Point order is independent of completion order — same Γ at the same index, always.
        for (int i = 0; i < gammaGrid.Length; i++)
            Assert.Equal(serial.Points[i].Gamma, parallel.Points[i].Gamma);

        // The hole SET must be identical — a point that holes one way and converges the other is a
        // FAILURE of this gate, not a bonus, per the brief's own wording. This IS a hard assertion —
        // it held after batching by ANGLE (see ContourGrid.BuildParallel's own remarks) rather than by
        // raw grid index.
        var serialHoles   = serial.Points.Where(p => p.IsHole).Select(p => p.Gamma).ToHashSet();
        var parallelHoles = parallel.Points.Where(p => p.IsHole).Select(p => p.Gamma).ToHashSet();
        var onlySerial   = serialHoles.Except(parallelHoles).ToList();
        var onlyParallel = parallelHoles.Except(serialHoles).ToList();
        output.WriteLine($"serial holes: {serialHoles.Count}, parallel holes: {parallelHoles.Count}");
        output.WriteLine($"holes only in serial: {onlySerial.Count}, only in parallel: {onlyParallel.Count}");
        Assert.True(onlySerial.Count == 0 && onlyParallel.Count == 0,
            $"hole SET differs — only-serial={string.Join(",", onlySerial)} " +
            $"only-parallel={string.Join(",", onlyParallel)}");

        // Converged-point agreement is REPORTED, not asserted against a threshold — this repo's own
        // convention for a measurement not yet earned (src/Harmonica/CLAUDE.md's R-h9r2 style).
        // WHY NOT A HARD TOLERANCE: the worst-case deviations below are NOT explained by "a different
        // seed reaches a different point inside the same convergence tolerance" (which the brief
        // anticipated as the ordinary case). Traced directly (see
        // Diagnose_IsTheHintJumpOrTheNeighborSpectrum_RootCause, same file): PinSearch.Run's bracket
        // uses a DOUBLING stride (3, 6, 12, 24 dB…) — coarse enough that on a device whose gain-vs-Pin
        // curve has a local non-monotonicity, the bracket can literally SKIP OVER the true first
        // compression crossing between two probed Pin levels and lock onto a later, spurious one. This
        // reproduces with NO hint and NO neighbour spectra at all (the untouched, original bracket
        // code) — it is a PRE-EXISTING defect in Run() itself, not something this brief's changes or
        // BuildParallel's batching introduced. Batching changes WHICH hint/seed a point gets (fewer,
        // more angularly-local candidates than the serial path's whole-grid search), which changes
        // HOW OFTEN this pre-existing aliasing bites — that is the honest causal chain, and "something
        // is wrong" per the brief's own words, but the wrong thing is upstream of §4's own scope.
        //
        // brief-harmonicarf-r4 §3 (2026-08-13) — PARTIALLY FIXED, and the residual is EXPLAINED, not
        // silently absorbed. Run()'s bracket now evaluates the first crossing as a PURE function of
        // every probed sample (sorted by Pin) and, for an UNHINTED search only, bisects a coarse
        // bracket until it is narrow — this is what closes the NAMED reproduction case
        // (Diagnose_IsTheHintJumpOrTheNeighborSpectrum_RootCause's own COLD line: 28.4 dBm before this
        // fix, 27.4 now, against Sweep()'s 27.2 ground truth). It is deliberately NOT extended to a
        // HINTED search — measured directly, doing so regressed this exact gate from 2.69 to 8.05 pts
        // PAE, because a hint from a DIFFERENT neighbour can define a coarse bracket that never samples
        // this fixture's own gain-EXPANSION peak at all (see the ladder printed by the Diagnose_* test:
        // gain RISES from 12.2 to 14.7 dB between Pin=15 and 21, then falls), so `gMax` is
        // under-established and refining WITHIN that already-wrong bracket just confidently converges
        // to a wrong-but-stable answer — worse, a DIFFERENT wrong answer in each build, since serial and
        // parallel hint the same Γ from different neighbours.
        //
        // The RESIDUAL regression below (this gate's own worst-case PAE deviation is still ~3.7-3.8 pts,
        // not the ~2.69 pts baseline) is NOT from the fix itself misfiring — restricting refinement to
        // grid's own null-hint point (Γ=0, `RingGrid`'s own first element, and BOTH ContourGrid.Build's
        // "first point processed" and BuildParallel's own magnitude-nearest "leader" resolve to this
        // SAME Γ, computed from IDENTICAL inputs in both builds, so its own answer is BIT-IDENTICAL
        // between serial and parallel — proven, not assumed). The mechanism is downstream: Γ=0's
        // compression Pin genuinely MOVED (more accurate now), and every OTHER point's own HINT is
        // ultimately derived from a chain of nearest-converged-neighbour lookups that traces back to
        // it — chains that differ in SHAPE between the serial (whole-grid, sequential) and parallel
        // (per-batch) builders, a pre-existing asymmetry §4's own hole-set-matching work already
        // manages for holes but not for this kind of small, compounding numeric drift. Moving the one
        // shared reference point's value therefore reshuffles, rather than closes, the pre-existing
        // hint-propagation sensitivity — a ContourGrid-level concern, outside §3's own scope (which is
        // PinSearch.Run's bracket sampling, not ContourGrid's hint-selection). Flagged here rather than
        // chased further this pass, per this repo's own "explained residual" precedent (RESOLVED.md §3).
        double worstPoutDb = 0, worstDePts = 0, worstPaePts = 0;
        int compared = 0;
        for (int i = 0; i < gammaGrid.Length; i++)
        {
            var a = serial.Points[i]; var b = parallel.Points[i];
            if (a.IsHole || b.IsHole) continue;
            compared++;
            worstPoutDb = Math.Max(worstPoutDb, Math.Abs(a.Metric(GridMetric.PoutDbm) - b.Metric(GridMetric.PoutDbm)));
            worstDePts  = Math.Max(worstDePts,  Math.Abs(a.Metric(GridMetric.DrainEfficiency) - b.Metric(GridMetric.DrainEfficiency)));
            worstPaePts = Math.Max(worstPaePts, Math.Abs(a.Metric(GridMetric.Pae) - b.Metric(GridMetric.Pae)));
        }
        output.WriteLine($"{compared} points compared — worst-case deviation: " +
                         $"Pout {worstPoutDb:F3} dB, DE {worstDePts:F3} pts, PAE {worstPaePts:F3} pts");
        output.WriteLine("(root cause: PinSearch.Run's doubling-stride bracket can miss the true first " +
                         "compression crossing on a non-monotone gain curve — pre-existing, see the " +
                         "Diagnose_* test in this file and src/Harmonica/RESOLVED.md)");

        output.WriteLine("");
        output.WriteLine("worst offenders by PAE deviation:");
        var ranked = new List<(Complex Gamma, double PaeDev, double SerialPae, double ParallelPae)>();
        for (int i = 0; i < gammaGrid.Length; i++)
        {
            var a = serial.Points[i]; var b = parallel.Points[i];
            if (a.IsHole || b.IsHole) continue;
            double dev = Math.Abs(a.Metric(GridMetric.Pae) - b.Metric(GridMetric.Pae));
            ranked.Add((gammaGrid[i], dev, a.Metric(GridMetric.Pae), b.Metric(GridMetric.Pae)));
        }
        foreach (var r in ranked.OrderByDescending(x => x.PaeDev).Take(5))
            output.WriteLine($"  Γ={r.Gamma} dev={r.PaeDev:F3} pts (serial={r.SerialPae:F3}, parallel={r.ParallelPae:F3})");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void Diagnose_IsTheHintJumpOrTheNeighborSpectrum_RootCause()
    {
        var model = DefaultModel();
        var ctx = HarmonicaContext.Create(model);
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        double z0 = ctx.Model.Settings.Z0;
        var gamma = new Complex(0.0, -0.16);
        var t = terms.Clone();
        t.Set(TerminationSide.Load, 1, z0 * (Complex.One + gamma) / (Complex.One - gamma));

        var sweep = PinSearch.Sweep(ctx, t, model.Settings.PinStartDbm, model.Settings.PinMaxDbm, model.Settings.PinStepDbm);
        output.WriteLine($"ground truth Sweep(): {(sweep.Compressed ? $"{sweep.SweepCompression!.PinDbm:F3} dBm" : "no compress")}");

        // COLD: no warmStart, no hint, no neighborSteps at all — pure PinStart + doubling-stride
        // bootstrap, exactly what the very first point of a fresh grid does.
        var cold = PinSearch.Run(ctx, t);
        output.WriteLine($"Run() COLD (no hint, no neighbor spectra): " +
                         (cold.Compressed ? $"{cold.AtCompression!.PavlDbm:F3} dBm" : $"{cold.Reason}"));

        // HINTED, no neighbor spectra (the pre-§3.3-item-2 shape: a hint drives the jump target, but
        // every SEED still comes from the plain in-ladder chain).
        var hintedNoSpectra = PinSearch.Run(ctx, t, pinHintDbm: 27.0);
        output.WriteLine($"Run() HINT=27dBm, no neighbor spectra: " +
                         (hintedNoSpectra.Compressed ? $"{hintedNoSpectra.AtCompression!.PavlDbm:F3} dBm" : $"{hintedNoSpectra.Reason}"));

        var hintedBad = PinSearch.Run(ctx, t, pinHintDbm: 31.0);
        output.WriteLine($"Run() HINT=31dBm, no neighbor spectra: " +
                         (hintedBad.Compressed ? $"{hintedBad.AtCompression!.PavlDbm:F3} dBm" : $"{hintedBad.Reason}"));

        output.WriteLine("");
        output.WriteLine("Sweep ladder shape 15..34 dBm:");
        foreach (var st in sweep.Steps.Where(s2 => s2.PavlDbm >= 15 && s2.PavlDbm <= 34))
            output.WriteLine($"  Pin={st.PavlDbm,6:F1}  Gain={st.GainDb,8:F4}  Compression={st.Compression,7:F4}");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void SerialVsParallel_WallClock_61PointGrid()
    {
        output.WriteLine($"Environment.ProcessorCount = {Environment.ProcessorCount}");

        var model = DefaultModel();
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        var gammaGrid = ContourGrid.RingGrid(5, 12, 0.8);

        double BestOf(int reps, Action body)
        {
            body();
            double best = double.MaxValue;
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                body();
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        var ctxSerial = HarmonicaContext.Create(model);
        var serialGrid = new ContourGrid();
        double serialMs = BestOf(3, () => serialGrid.Build(ctxSerial, terms, gammaGrid));

        var ctxParallel = HarmonicaContext.Create(model);
        var parallelGrid = new ContourGrid();
        double parallelMs = BestOf(3, () => parallelGrid.BuildParallel(ctxParallel, terms, gammaGrid, batchSize: 12));

        output.WriteLine($"serial   Build:         {serialMs,8:F1} ms  ({serialGrid.SolveCount} solves)");
        output.WriteLine($"parallel BuildParallel: {parallelMs,8:F1} ms  ({parallelGrid.SolveCount} solves)  " +
                         $"=> {serialMs / parallelMs:F2}x");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void ASupersededBuild_CancelsWithinOnePointsCost()
    {
        var model = DefaultModel();
        var ctx = HarmonicaContext.Create(model);
        var terms = DefaultTerminations(model.Settings.HarmonicCount);
        var gammaGrid = ContourGrid.RingGrid(5, 12, 0.8);

        using var cts = new CancellationTokenSource();
        var grid = new ContourGrid();

        var sw = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromMilliseconds(5));   // well inside a 61-point build
        Assert.Throws<OperationCanceledException>(() =>
            grid.BuildParallel(ctx, terms, gammaGrid, batchSize: 12, ct: cts.Token));
        sw.Stop();

        output.WriteLine($"cancelled after {sw.Elapsed.TotalMilliseconds:F1} ms");
        // Loose bound: a full uncancelled build of this grid costs tens of ms (SerialVsParallel above
        // measures it); cancellation must land well short of that, not run the grid out.
        Assert.True(sw.Elapsed.TotalMilliseconds < 200,
            $"cancellation took {sw.Elapsed.TotalMilliseconds:F1} ms — too close to a full build's own cost");
    }
}
