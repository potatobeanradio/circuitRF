using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-harmonicarf-r3b-frame-rate-and-loadpull.md §1.1 — reproduces the profiled figures on the
/// SHIPPED DEFAULT document (the same fixture <c>HarmonicaViewModel.DefaultModel()</c> builds: Hero
/// 2's GaN HEMT as an SDD, K = 3, no package — InterfaceCount = 2, gridN = 16). Run this BEFORE and
/// AFTER the evaluator work so the payoff of each step is measured, not assumed.
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class EvaluatorPerfCostTests(ITestOutputHelper output)
{
    /// <summary>The shipped default's own equations, duplicated here because Harmonica.Tests does not
    /// reference src/Ui (where HarmonicaViewModel.DefaultModel lives) — same text, byte for byte.</summary>
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

    private static double BestOfMs(int reps, Action body)
    {
        double best = double.MaxValue;
        for (int r = 0; r < reps; r++)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void P1_ShippedDefault_FullProfile()
    {
        var model = DefaultModel();
        var ctx = HarmonicaContext.Create(model);
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Load, 1, new Complex(50, 0));

        // ── warm/cold ctx.Solve ──────────────────────────────────────────────
        var cold = ctx.Solve(terms, -10.0);
        Assert.True(cold.Converged);
        double coldMs = BestOfMs(3, () =>
        {
            var ctx2 = HarmonicaContext.Create(model);
            ctx2.Solve(terms, -10.0);
        });

        var seed = cold.V;
        double warmMs = BestOfMs(20, () => ctx.Solve(terms, -9.9, seed));
        var probe = ctx.Solve(terms, -9.9, seed);
        output.WriteLine($"[diag] warm solve Newton iterations = {probe.Iterations}");

        // ── PinSearch.Sweep, the tier-A ladder ───────────────────────────────
        double sweepMs = double.MaxValue;
        int sweepSolves = 0;
        for (int rep = 0; rep < 5; rep++)
        {
            var sw = Stopwatch.StartNew();
            var result = PinSearch.Sweep(ctx, terms, model.Settings.PinStartDbm, model.Settings.PinMaxDbm,
                                         model.Settings.PinStepDbm);
            sw.Stop();
            sweepMs = Math.Min(sweepMs, sw.Elapsed.TotalMilliseconds);
            sweepSolves = result.Solves;
        }

        // ── one dut.Evaluate ──────────────────────────────────────────────────
        var dut = ctx.DutComponent.Model;
        var pv = new CircuitRF.Core.PortVoltages([-3.05, 48.0]);
        dut.Evaluate(pv); // warm up
        double evalUs = BestOfMs(200, () => dut.Evaluate(pv)) * 1000.0;

        // ── EvalDual at three sizes ──────────────────────────────────────────
        var trivialAst = Parser.Parse("_v1");
        var smallAst   = Parser.Parse(I1Expr);
        var bigAst     = Parser.Parse(I2Expr);
        var noParams = new Dictionary<string, double>(StringComparer.Ordinal);

        const int N = 100_000;
        double trivialUs = BestOfMs(N, () => SddEvaluator.EvalDual(trivialAst, noParams, [-3.05, 48.0])) * 1000.0;
        double smallUs   = BestOfMs(N, () => SddEvaluator.EvalDual(smallAst,   noParams, [-3.05, 48.0])) * 1000.0;
        double bigUs     = BestOfMs(N, () => SddEvaluator.EvalDual(bigAst,     noParams, [-3.05, 48.0])) * 1000.0;

        output.WriteLine("§1.1 reproduction — shipped default document (K=3, no package)");
        output.WriteLine($"cold ctx.Solve ..................  {coldMs:F3} ms");
        output.WriteLine($"warm ctx.Solve ..................  {warmMs:F3} ms");
        output.WriteLine($"PinSearch.Sweep(-10..34 @1dB) ...  {sweepMs:F3} ms   {sweepSolves} solves => {sweepMs / sweepSolves:F3} ms/solve");
        output.WriteLine($"one dut.Evaluate ................  {evalUs:F3} us");
        output.WriteLine($"EvalDual trivial (\"_v1\") ..........  {trivialUs:F3} us");
        output.WriteLine($"EvalDual small  (\"_v1/50\") ........  {smallUs:F3} us");
        output.WriteLine($"EvalDual big    (the drain eqn) ...  {bigUs:F3} us");

        // ── the compiled (slot-resolved) path directly, same expression ──────
        var noControls = Array.Empty<int>();
        var compiledBig = CompiledSddExpr.Compile(bigAst, noParams, 2, noControls, "big");
        compiledBig.EvalDual([-3.05, 48.0], [], "big"); // warm up
        double compiledBigUs = BestOfMs(100_000, () => compiledBig.EvalDual([-3.05, 48.0], [], "big")) * 1000.0;

        var compiledSmall = CompiledSddExpr.Compile(smallAst, noParams, 2, noControls, "small");
        compiledSmall.EvalDual([-3.05, 48.0], [], "small");
        double compiledSmallUs = BestOfMs(100_000, () => compiledSmall.EvalDual([-3.05, 48.0], [], "small")) * 1000.0;

        output.WriteLine($"CompiledSddExpr.EvalDual small ......  {compiledSmallUs:F3} us");
        output.WriteLine($"CompiledSddExpr.EvalDual big .........  {compiledBigUs:F3} us");
    }
}
