// ================================================================
//  InverseSolveCostTests.cs  —  the measurements brief-harmonicarf-h6 §8.2 and §8.4 ask for.
//
//  §6.6's numbers (8 perturbation solves + residual ≈ 9 ms at start, then 1–2 solves ≈ 2 ms/frame)
//  are ESTIMATES. This is the phase that turns them into measurements, so every one is taken here
//  and reported rather than quoted from the design note.
//
//  Cost discipline (§6): non-parallel collection, best-of-N MINIMUM (not a mean, not a median), and
//  every reported number measured ALONE.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

[Trait("Category", "Benchmark")]
[Collection("HarmonicaBenchmarks")]
public sealed class InverseSolveCostTests(ITestOutputHelper output)
{
    private const int Repeats = 5;
    private const double OperatingPointDbm = 22.0;

    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

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
        Embedding = new EmbeddingStack { Package = new LumpedPackage { Rs = 0.3, Ls = 20e-12, Rd = 0.5 } },
        Bias      = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings  = new HarmonicaSettings
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
        t.Set(TerminationSide.Load,   2, new Complex(8, -25));
        t.Set(TerminationSide.Source, 2, new Complex(18, 22));
        return t;
    }

    private static Complex[] Start(TerminationSet t, IReadOnlyList<InverseBand> b)
        => [.. b.Select(x => HarmonicaDataSet.GammaOf(t.Z(x.Side, x.Band)))];

    /// <summary>Best-of-N MINIMUM. A mean carries whichever repetition the OS decided to interrupt.</summary>
    private static double BestOf(int n, Action body)
    {
        double best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void M2Cost_FdAtStart_PerFrameBroyden_AndTheFdRefreshRateOnARealDrag()
    {
        var model = Model();
        var terms = Terms(model);

        InverseBand[][] cases =
        [
            [new(TerminationSide.Load, 1)],
            [new(TerminationSide.Load, 1), new(TerminationSide.Load, 2)],
            [new(TerminationSide.Load, 1), new(TerminationSide.Load, 2),
             new(TerminationSide.Source, 1), new(TerminationSide.Source, 2)],
        ];

        output.WriteLine("§6.6 estimates: FD at start ≈ 9 ms (8 perturbation solves + residual), " +
                         "per-frame Broyden ≈ 2 ms (1–2 solves), full FD every frame ≈ 30–40 ms.");
        output.WriteLine("");
        output.WriteLine("bands  n×n   FD-at-start   per-frame   solves/frame   FD refreshes   full-FD/frame");

        foreach (var bands in cases)
        {
            var ctx = HarmonicaContext.Create(model, Settings);

            // ── FD at drag start ──────────────────────────────────────────────
            double fdMs = BestOf(Repeats, () =>
            {
                var s = new InverseSolver(terms, bands, Start(terms, bands),
                                          new InverseSolveOptions { PavlDbm = OperatingPointDbm });
                Assert.Equal(InverseFailure.None, s.Begin(ctx));
            });

            // ── a real drag: 30 frames, the dragged glyph walking a short arc ──
            var probe = new InverseSolver(terms, bands, Start(terms, bands),
                                          new InverseSolveOptions { PavlDbm = OperatingPointDbm });
            Assert.Equal(InverseFailure.None, probe.Begin(ctx));
            var here = probe.Evaluate(ctx, probe.Current)!;

            const int Frames = 30;
            Complex[] TargetsAt(int i)
            {
                var t = (Complex[])here.Clone();
                t[0] += new Complex(0.0035 * i, -0.0025 * i);
                return t;
            }

            int solves = 0, refreshes = 0, converged = 0;
            double dragMs = BestOf(Repeats, () =>
            {
                var s = new InverseSolver(terms, bands, Start(terms, bands),
                                          new InverseSolveOptions { PavlDbm = OperatingPointDbm });
                s.Begin(ctx);
                int s0 = s.SolveCount, f0 = s.FdBuildCount, c = 0;
                for (int i = 1; i <= Frames; i++) if (s.Step(ctx, TargetsAt(i)).Converged) c++;
                solves = s.SolveCount - s0; refreshes = s.FdBuildCount - f0; converged = c;
            });

            // ── the alternative §6.6 rejects: full FD every frame ──────────────
            double fullMs = BestOf(Repeats, () =>
            {
                var s = new InverseSolver(terms, bands, Start(terms, bands),
                                          new InverseSolveOptions { PavlDbm = OperatingPointDbm });
                s.Begin(ctx);
                for (int i = 1; i <= Frames; i++) { s.Begin(ctx); s.Step(ctx, TargetsAt(i)); }
            });

            output.WriteLine($"{bands.Length,5}  {2 * bands.Length}×{2 * bands.Length}  " +
                             $"{fdMs,9:F2} ms  {dragMs / Frames,8:F2} ms  " +
                             $"{(double)solves / Frames,12:F2}  " +
                             $"{refreshes,6} / {Frames} frames  {fullMs / Frames,10:F2} ms");

            Assert.Equal(Frames, converged);
            Assert.True(dragMs < fullMs,
                "Broyden must be cheaper than rebuilding the Jacobian every frame — that is the only " +
                "reason §6.6 carries one across frames");
        }
    }

    [Fact]
    public void M3Cost_ReachabilitySamplingAtTheShippingDensity()
    {
        var model = Model();
        var terms = Terms(model);
        var band  = new InverseBand(TerminationSide.Load, 1);

        output.WriteLine("boundary samples   time      solves   dropped   area (Γ²)");
        foreach (int n in new[] { 12, 24, 48 })
        {
            // The context is built OUTSIDE the timed block: elaboration is a structural cost the
            // document has already paid before any drag starts, and folding it in would report the
            // sampler as more expensive than it is.
            var ctx = HarmonicaContext.Create(model, Settings);
            ReachableRegion r = ReachableRegion.Empty;
            double ms = BestOf(3, () => r = Reachability.Sample(
                ctx, terms, band, OperatingPointDbm, boundarySamples: n, interiorSamples: 0));
            output.WriteLine($"{n,16}   {ms,7:F1} ms  {r.Solves,6}   {r.Dropped,7}   {r.Area,9:F4}" +
                             (n == Reachability.DefaultBoundarySamples ? "   ← shipping" : ""));
            Assert.False(r.IsEmpty);
        }

        output.WriteLine("");
        output.WriteLine("Compare: tier A (one Pin drive-up) is ~10 ms on this class of model, and the " +
                         "30 fps budget is 33 ms. The decision this measurement settles is open item 4.");
    }

    /// <summary>
    /// Open item 8, answered with a measurement rather than a prediction: does the SOURCE side, whose
    /// residual is the §4.5.3 diagonal and therefore a function of <c>J</c> itself, need an FD-refresh
    /// cadence of its own?
    /// </summary>
    [Fact]
    public void OpenItem8_TheSourceSideRefreshRate_MeasuredAgainstTheLoadSideOnTheSameDrag()
    {
        var model = Model();
        var terms = Terms(model);

        output.WriteLine("A 60-frame drag on each side, stall-driven refresh only (the load side's own " +
                         "cadence), then the same source drag with a forced every-8-frames refresh.");
        output.WriteLine("");
        output.WriteLine("case                         frames converged   solves/frame   FD refreshes   ms/frame");

        void Run(string label, InverseBand[] bands, int forcedEvery)
        {
            var ctx = HarmonicaContext.Create(model, Settings);
            var opt = new InverseSolveOptions
            {
                PavlDbm = OperatingPointDbm, SourceFdRefreshEveryFrames = forcedEvery,
            };

            var probe = new InverseSolver(terms, bands, Start(terms, bands), opt);
            Assert.Equal(InverseFailure.None, probe.Begin(ctx));
            var here = probe.Evaluate(ctx, probe.Current)!;

            const int Frames = 60;
            Complex[] TargetsAt(int i)
            {
                var t = (Complex[])here.Clone();
                // A curved path, not a straight one: a straight drag never asks the Jacobian to turn,
                // which is exactly the case a Broyden approximation handles best.
                double a = 0.9 * i / Frames;
                t[0] += new Complex(0.06 * Math.Sin(a), 0.06 * (1 - Math.Cos(a)));
                return t;
            }

            int converged = 0, solves = 0, refreshes = 0;
            var sw = Stopwatch.StartNew();
            var s = new InverseSolver(terms, bands, Start(terms, bands), opt);
            Assert.Equal(InverseFailure.None, s.Begin(ctx));
            int s0 = s.SolveCount, f0 = s.FdBuildCount;
            for (int i = 1; i <= Frames; i++) if (s.Step(ctx, TargetsAt(i)).Converged) converged++;
            sw.Stop();
            solves = s.SolveCount - s0; refreshes = s.FdBuildCount - f0;

            output.WriteLine($"{label,-28} {converged,6} / {Frames,-8}  {(double)solves / Frames,12:F2}  " +
                             $"{refreshes,10}   {sw.Elapsed.TotalMilliseconds / Frames,8:F2}");
            Assert.Equal(Frames, converged);
        }

        Run("load only, stall-driven",   [new(TerminationSide.Load, 1)],   0);
        Run("source only, stall-driven", [new(TerminationSide.Source, 1)], 0);
        Run("source only, forced /8",    [new(TerminationSide.Source, 1)], 8);
        Run("both sides, stall-driven",  [new(TerminationSide.Load, 1), new(TerminationSide.Source, 1)], 0);
    }
}
