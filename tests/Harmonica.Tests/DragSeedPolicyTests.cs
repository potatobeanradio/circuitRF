// ================================================================
//  DragSeedPolicyTests.cs — brief-harmonicarf-r4 §5
//
//  The owner's own report: small marker-drag moves feel fast, large ones feel slow, and they suspect
//  cross-frame seeding is the reason ("it's just simply faster to always use DC as the initial
//  condition" — their prior prototype's own behaviour). §5.1 already names the mechanism:
//  PinSearch.Sweep's `priorLevelSpectra` (R-h9r2-19's "lever 1") tries the PREVIOUS FRAME's converged
//  spectrum at each Pin level before anything else — a near-perfect seed on a small move, an actively
//  misleading one on a large move that lands the termination in a different HB solution basin.
//
//  This file measures three policies directly against PinSearch.Sweep (bypassing HarmonicaSolver —
//  the policy lives entirely in whether/when `priorLevelSpectra` is threaded from one frame's Sweep
//  result into the next one's, which is exactly what HarmonicaSolver.Solve does unconditionally today):
//
//    A — today: priorLevelSpectra always threaded from the previous frame.
//    B — the owner's: priorLevelSpectra never threaded. The ladder's first rung still starts from the
//        real DC seed (ctx.Solve's own default when warmStart is null — SeedFromRealDc, already built
//        by §3), every rung after it warm-starts from its own in-ladder predecessor. This is NOT
//        "solve every rung from DC" — it is "the sweep starts from DC and chains up," matching what
//        the owner's prior prototype did.
//    C — B, plus lever 1 re-enabled only when |ΔΓ| is below a threshold.
//
//  Fixture: Hero 2's GaN HEMT under (25 Ω source, 80+j10 Ω load) — the SAME device PinSweepTests uses,
//  which compresses cleanly within the shipped PinMaxDbm=50 range (an unmarked-band shipped-default
//  termination would not compress at all, making the seed-basin question moot). §1 (the early-stop
//  fix) is already landed, so every Sweep call here already stops shortly past compression rather than
//  running the full 61-rung ladder to 50 dBm.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

[Trait("Category", "Benchmark")]
public sealed class DragSeedPolicyTests(ITestOutputHelper output)
{
    private static AnalysisSettings EngineSettings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>Hero 2's GaN HEMT, shipped PinMaxDbm (50, R-h9r2-18) rather than PinSweepTests' own
    /// faster-running 34 default — §5 cares about the shipped ladder's own shape, and §1's early stop
    /// (already landed) is what keeps this affordable despite the higher ceiling.</summary>
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
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 50, PinStepDbm = 1.0,
            TickleEnabled = true, TickleDbm = -50.0,
        },
    };

    private static TerminationSet Terms(CircuitModel m, Complex loadZ)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, loadZ);
        return t;
    }

    // ── the three policies, as a pure function of "what priorLevelSpectra gets passed" ──────────

    private enum Policy { A_Today, B_NeverReuse, C_Hedged }

    private sealed class FrameResult
    {
        public double Ms;
        public int Iterations;
        public int Solves;
        public double DeltaGamma;
    }

    /// <summary>Runs one simulated drag — a sequence of Γ positions — under one policy, threading
    /// `priorLevelSpectra` exactly as <c>HarmonicaSolver.Solve</c> does (always update from the
    /// frame's own solved steps; the POLICY decides only whether the NEXT frame is allowed to READ
    /// it). <paramref name="cHedgeThreshold"/> is only consulted for <see cref="Policy.C_Hedged"/>.</summary>
    private static List<FrameResult> RunDrag(HarmonicaContext ctx, CircuitModel m,
                                              IReadOnlyList<Complex> gammaPath, Policy policy,
                                              double cHedgeThreshold = 0.20)
    {
        var s = m.Settings;
        Dictionary<double, Complex[,]>? priorLevelSpectra = null;
        Complex? lastGamma = null;
        var results = new List<FrameResult>();

        foreach (var gamma in gammaPath)
        {
            double deltaGamma = lastGamma is { } lg ? (gamma - lg).Magnitude : 0.0;
            lastGamma = gamma;

            var terms = Terms(m, HarmonicaDataSet.ImpedanceOf(gamma, m.Settings.Z0));

            bool readLever1 = policy switch
            {
                Policy.A_Today     => true,
                Policy.B_NeverReuse => false,
                Policy.C_Hedged     => deltaGamma < cHedgeThreshold,
                _ => throw new ArgumentOutOfRangeException(nameof(policy)),
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sweep = PinSearch.Sweep(ctx, terms, s.PinStartDbm, s.PinMaxDbm, s.PinStepDbm,
                                        priorLevelSpectra: readLever1 ? priorLevelSpectra : null);
            sw.Stop();

            // Always update, regardless of policy — matches HarmonicaSolver.Solve's own rule
            // (§5.1's own quoted lever). The policy gates the READ, never the write.
            priorLevelSpectra = sweep.Steps.Count > 0
                ? sweep.Steps.ToDictionary(st => Math.Round(st.PavlDbm, 6), st => st.Point.V)
                : priorLevelSpectra;

            results.Add(new FrameResult
            {
                Ms         = sw.Elapsed.TotalMilliseconds,
                Iterations = sweep.Steps.Sum(st => st.Point.Iterations),
                Solves     = sweep.Solves,
                DeltaGamma = deltaGamma,
            });
        }
        return results;
    }

    /// <summary>Best-of-<paramref name="reps"/>, this repo's own timing discipline
    /// (R-L2a-4/HarmonicaRenderBudgetTests' own rule) — a single-pass Stopwatch reading per frame is
    /// noise-dominated at these frame sizes (~10 ms), where JIT/GC/scheduler jitter is comparable to
    /// the whole effect being measured. One discarded warm-up run, then the MINIMUM millisecond
    /// reading per frame INDEX across repeats (the least-polluted observation, this repo's own
    /// estimator) — Iterations/Solves/DeltaGamma are deterministic (no RNG anywhere in this solve
    /// path) so they are read from the last repeat rather than re-aggregated.</summary>
    private static List<FrameResult> RunDragBestOf(HarmonicaContext ctx, CircuitModel m,
                                                    IReadOnlyList<Complex> gammaPath, Policy policy,
                                                    int reps = 5, double cHedgeThreshold = 0.20)
    {
        _ = RunDrag(ctx, m, gammaPath, policy, cHedgeThreshold); // warm-up, discarded

        List<FrameResult>? best = null;
        for (int r = 0; r < reps; r++)
        {
            var run = RunDrag(ctx, m, gammaPath, policy, cHedgeThreshold);
            if (best is null) { best = run; continue; }
            for (int i = 0; i < run.Count; i++)
                if (run[i].Ms < best[i].Ms) best[i] = run[i];
        }
        return best!;
    }

    // ── path generators ──────────────────────────────────────────────────────────────────────

    /// <summary>Small, sub-pixel-scale steps wandering around a fixed centre — the shape a stationary
    /// hand's own jitter or a slow, careful drag produces.</summary>
    private static List<Complex> SmallJumpPath(Complex centre, int frames, double stepMag = 0.004)
    {
        var path = new List<Complex> { centre };
        var cur = centre;
        for (int i = 0; i < frames; i++)
        {
            double angle = i * 2.399963; // golden-angle-ish wander, deterministic, no RNG
            cur += Complex.FromPolarCoordinates(stepMag, angle);
            path.Add(cur);
        }
        return path;
    }

    /// <summary>Large jumps to genuinely different terminations — the shape a fast mouse flick
    /// produces. Bounded to |Γ| &lt; 0.85 so every point stays reachable.</summary>
    private static List<Complex> LargeJumpPath(int frames)
    {
        Complex[] targets =
        [
            new(0.10, 0.05), new(-0.35, 0.40), new(0.55, -0.20), new(-0.20, -0.55),
            new(0.60, 0.30), new(-0.55, 0.15), new(0.15, -0.60), new(0.40, 0.45),
        ];
        return [.. targets.Take(frames)];
    }

    /// <summary>§5.3's own confound control — a LONG tangential drag at CONSTANT |Γ| ≈ 0.5. Large
    /// per-frame Γ MOVEMENT, but never moving toward a harder-to-reach large-|Γ| region, so if this
    /// stays fast the asymmetry is about seed-basin mismatch, not about difficulty/reachability.</summary>
    private static List<Complex> TangentialPath(int frames, double radius = 0.5)
    {
        var path = new List<Complex>();
        for (int i = 0; i < frames; i++)
        {
            double angle = i * (Math.PI / (frames - 1)); // sweeps half the circle, constant radius
            path.Add(Complex.FromPolarCoordinates(radius, angle));
        }
        return path;
    }

    private static void Report(ITestOutputHelper output, string label, List<FrameResult> results)
    {
        // Skip frame 0 (no ΔΓ, not a "drag frame") for the summary stats.
        var frames = results.Skip(1).ToList();
        double meanMs = frames.Average(f => f.Ms);
        double maxMs  = frames.Max(f => f.Ms);
        double meanIters = frames.Average(f => f.Iterations);
        double meanDeltaGamma = frames.Average(f => f.DeltaGamma);
        output.WriteLine($"  {label,-14} mean {meanMs,7:F2} ms   max {maxMs,7:F2} ms   " +
                         $"mean Newton iters/frame {meanIters,6:F1}   mean |ΔΓ| {meanDeltaGamma:F4}");
    }

    // ══ §5.2 — the three policies, small jumps ═══════════════════════════════════════════════

    [Fact]
    public void Policies_SmallJumpFrameTime_AvsBvsC()
    {
        var m = Model();
        var ctx = HarmonicaContext.Create(m, EngineSettings);
        var path = SmallJumpPath(new Complex(0.30, 0.10), frames: 14);

        var a = RunDragBestOf(ctx, m, path, Policy.A_Today);
        var b = RunDragBestOf(ctx, m, path, Policy.B_NeverReuse);
        var c = RunDragBestOf(ctx, m, path, Policy.C_Hedged);

        output.WriteLine("§5.2 — small-jump drag (14 frames, |ΔΓ| ≈ 0.004/frame, best-effort single pass):");
        Report(output, "A (today)", a);
        Report(output, "B (owner's)", b);
        Report(output, "C (hedged)", c);
    }

    // ══ §5.2 — the three policies, large jumps ═══════════════════════════════════════════════

    [Fact]
    public void Policies_LargeJumpFrameTime_AvsBvsC()
    {
        var m = Model();
        var ctx = HarmonicaContext.Create(m, EngineSettings);
        var path = LargeJumpPath(frames: 8);

        var a = RunDragBestOf(ctx, m, path, Policy.A_Today);
        var b = RunDragBestOf(ctx, m, path, Policy.B_NeverReuse);
        var c = RunDragBestOf(ctx, m, path, Policy.C_Hedged);

        output.WriteLine("§5.2 — large-jump drag (8 frames, |ΔΓ| ≈ 0.3-0.9/frame):");
        Report(output, "A (today)", a);
        Report(output, "B (owner's)", b);
        Report(output, "C (hedged)", c);
    }

    // ══ §5.3 — the confound control: tangential drag at constant |Γ| ≈ 0.5 ══════════════════

    [Fact]
    public void Policies_TangentialDragAtConstantGamma_ControlsForLargeVsHardConfound()
    {
        var m = Model();
        var ctx = HarmonicaContext.Create(m, EngineSettings);
        var path = TangentialPath(frames: 13);

        var a = RunDragBestOf(ctx, m, path, Policy.A_Today);
        var b = RunDragBestOf(ctx, m, path, Policy.B_NeverReuse);

        output.WriteLine("§5.3 — tangential drag, constant |Γ| ≈ 0.5, 13 frames spanning a half-circle " +
                         "(large per-frame movement, but never toward a harder region):");
        Report(output, "A (today)", a);
        Report(output, "B (owner's)", b);
    }

    // ══ §5.3 — gradual vs cliff: frame time as a function of jump size, Policy A only ════════

    [Fact]
    public void PolicyA_FrameTimeVsJumpSize_GradualOrCliff()
    {
        var m = Model();
        var ctx = HarmonicaContext.Create(m, EngineSettings);
        var basePoint = new Complex(0.30, 0.10);

        double[] jumpSizes = [0.01, 0.02, 0.05, 0.10, 0.15, 0.20, 0.30, 0.45, 0.60];

        output.WriteLine("§5.3 — Policy A frame time vs |ΔΓ| (each row: converge at basePoint, then " +
                         "ONE frame at basePoint + Δ in a fixed direction, priorLevelSpectra from the " +
                         "base frame):");
        foreach (double jump in jumpSizes)
        {
            var target = basePoint + Complex.FromPolarCoordinates(jump, 0.7); // fixed direction
            var path = new List<Complex> { basePoint, target };
            var a = RunDragBestOf(ctx, m, path, Policy.A_Today);
            var jumpFrame = a[1];
            output.WriteLine($"  |ΔΓ| = {jump,5:F2}   {jumpFrame.Ms,7:F2} ms   " +
                             $"{jumpFrame.Iterations,4} Newton iters   {jumpFrame.Solves,3} solves");
        }
    }

    // ══ §5.2's own "the threshold must be reported with the measurement that chose it" ═══════
    //
    // The tangential control (§5.3) already shows lever 1 STILL winning at |ΔΓ| ≈ 0.13 (A faster
    // than B there, not slower) — a naive small threshold (e.g. the 0.05 §5.2 used as a first guess)
    // would throw that win away. This scans A vs B at exactly the jump sizes GradualOrCliff already
    // measured, so the crossover — the |ΔΓ| past which carrying the prior frame's spectrum stops
    // helping and starts hurting — is read off real data rather than assumed.

    [Fact]
    public void AvsB_CrossoverPoint_WhereLever1StopsHelping()
    {
        var m = Model();
        var ctx = HarmonicaContext.Create(m, EngineSettings);
        var basePoint = new Complex(0.30, 0.10);

        double[] jumpSizes = [0.02, 0.05, 0.10, 0.13, 0.15, 0.20, 0.25, 0.30, 0.45, 0.60];

        output.WriteLine("§5.2/§5.3 — A vs B at the SAME single jump (converge at basePoint, then ONE " +
                         "frame at basePoint + Δ), to find where lever 1 (Policy A) stops winning over " +
                         "never reusing (Policy B):");
        foreach (double jump in jumpSizes)
        {
            var target = basePoint + Complex.FromPolarCoordinates(jump, 0.7);
            var path = new List<Complex> { basePoint, target };
            var a = RunDragBestOf(ctx, m, path, Policy.A_Today)[1];
            var b = RunDragBestOf(ctx, m, path, Policy.B_NeverReuse)[1];
            string verdict = a.Ms < b.Ms * 0.95 ? "A wins" : b.Ms < a.Ms * 0.95 ? "B wins" : "tie";
            output.WriteLine($"  |ΔΓ| = {jump,5:F2}   A {a.Ms,7:F2} ms ({a.Iterations,4} it)   " +
                             $"B {b.Ms,7:F2} ms ({b.Iterations,4} it)   {verdict}");
        }
    }
}
