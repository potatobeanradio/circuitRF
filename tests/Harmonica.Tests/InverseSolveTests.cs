// ================================================================
//  InverseSolveTests.cs  —  M2's gate, brief-harmonicarf-h6
//
//  THE ORACLE IS A ROUND TRIP THROUGH THE FORWARD PATH, never another inverse-solve run: take the
//  extrinsic set the solve produced, run it forward through the SAME code the panels read, and check
//  the intrinsic Γ lands on the target. A solver agreeing with itself proves nothing.
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

public sealed class InverseSolveTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>
    /// Hero 2's GaN HEMT, coefficients folded in. <paramref name="package"/> is what makes the
    /// intrinsic plane differ from the extrinsic one by more than the charge terms — a source lead
    /// and a drain resistance are exactly §4.5.3(a)'s case.
    /// </summary>
    private static CircuitModel Model(LumpedPackage? package = null, int k = 3) => new()
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
        Embedding = package is null ? EmbeddingStack.None
                                    : new EmbeddingStack { Package = package },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = k, FrequencyHz = 2e9,
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

    private const double OperatingPointDbm = 22.0;

    /// <summary>
    /// THE ORACLE. Runs an extrinsic set forward and reads every band's intrinsic Γ out of
    /// <c>HarmonicaDataSet</c> — the same cube the panels draw the glyphs from. This deliberately does
    /// not touch <see cref="InverseSolver"/> at all.
    /// </summary>
    private static Complex[] ForwardIntrinsic(HarmonicaContext ctx, TerminationSet terms,
                                              IReadOnlyList<InverseBand> bands, double pavlDbm)
    {
        var pt = ctx.Solve(terms, pavlDbm);
        Assert.True(pt.Converged, "the forward oracle's own HB solve did not converge");
        var ds = HarmonicaDataSet.Build(ctx, pt, terms);
        var cube = ds["Gamma_intr"];
        int nBands = cube.Axes[1].Values.Length;
        var z = cube.ComplexValues;
        return [.. bands.Select(b => z[(int)b.Side * nBands + b.Band])];
    }

    private static TerminationSet ApplyToTerms(TerminationSet baseline,
                                               IReadOnlyList<InverseBand> bands,
                                               IReadOnlyList<Complex> gammas)
    {
        var t = baseline.Clone();
        for (int i = 0; i < bands.Count; i++)
            t.Set(bands[i].Side, bands[i].Band, HarmonicaDataSet.ImpedanceOf(gammas[i]));
        return t;
    }

    private static InverseSolver StartedSolver(HarmonicaContext ctx, TerminationSet terms,
                                               IReadOnlyList<InverseBand> bands,
                                               InverseSolveOptions? opt = null)
    {
        var start = bands.Select(b => HarmonicaDataSet.GammaOf(terms.Z(b.Side, b.Band))).ToArray();
        var s = new InverseSolver(terms, bands, start,
                                  opt ?? new InverseSolveOptions { PavlDbm = OperatingPointDbm });
        Assert.Equal(InverseFailure.None, s.Begin(ctx));
        return s;
    }

    // ══ THE ROUND TRIP ═══════════════════════════════════════════════════════

    [Fact]
    public void TheSolutionRoundTripsThroughTheFORWARDPath_NotThroughAnotherInverseRun()
    {
        var model = Model(new LumpedPackage { Rs = 0.35, Ls = 25e-12, Rd = 0.6 });
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);
        terms.Set(TerminationSide.Load, 2, new Complex(8, -25));

        InverseBand[] bands = [new(TerminationSide.Load, 1), new(TerminationSide.Load, 2)];
        var solver = StartedSolver(ctx, terms, bands);

        // Where the glyphs are now, and where the drag is asking band 1's to go.
        var here   = ForwardIntrinsic(ctx, terms, bands, OperatingPointDbm);
        var target = new[] { here[0] + new Complex(0.05, -0.04), here[1] };

        var r = solver.Step(ctx, target);
        output.WriteLine($"converged={r.Converged} ({r.Failure}) ‖F‖={r.Residual:E3} " +
                         $"after {r.Iterations} iteration(s), {r.Solves} solves");
        Assert.True(r.Converged, $"the inverse solve failed with {r.Failure}");

        // THE GATE — run the answer forward and read the cube.
        var reached = ForwardIntrinsic(ctx, ApplyToTerms(terms, bands, r.Gammas), bands,
                                       OperatingPointDbm);

        for (int i = 0; i < bands.Length; i++)
        {
            double err = (reached[i] - target[i]).Magnitude;
            output.WriteLine($"  {bands[i].Side}{bands[i].Band}: target {Fmt(target[i])} " +
                             $"reached {Fmt(reached[i])}  |err| = {err:E3}");
            Assert.True(err < 1e-3,
                $"{bands[i].Side}{bands[i].Band} landed {err:E3} from its target — the forward path " +
                "does not agree with what the inverse solve claimed.");
        }

        // …and it actually MOVED, so the round trip is not passing because nothing happened.
        Assert.True((reached[0] - here[0]).Magnitude > 0.02,
            "the glyph did not move — this fixture is not exercising the solve");
    }

    // ══ FOUR MARKERS ⇒ AN 8 × 8 SYSTEM, ALL FOUR GLYPHS LAND ═════════════════

    [Fact]
    public void FourMarkedHarmonics_ProduceAnEightByEightSystem_AndAllFourGlyphsLandOnTheirTargets()
    {
        // R-h6-6 — all marked harmonics solve SIMULTANEOUSLY, because they are coupled. Four markers
        // spanning both sides and three bands.
        var model = Model(new LumpedPackage { Rs = 0.3, Ls = 20e-12, Rd = 0.5 }, k: 3);
        var ctx   = HarmonicaContext.Create(model, Settings);

        var terms = Terms(model);
        terms.Set(TerminationSide.Load,   2, new Complex(12, -30));
        terms.Set(TerminationSide.Source, 2, new Complex(18, 22));

        InverseBand[] bands =
        [
            new(TerminationSide.Load,   1), new(TerminationSide.Load,   2),
            new(TerminationSide.Source, 1), new(TerminationSide.Source, 2),
        ];

        var solver = StartedSolver(ctx, terms, bands);
        Assert.Equal(8, solver.Dimension);
        Assert.True(solver.UsesSourceSide);

        var here = ForwardIntrinsic(ctx, terms, bands, OperatingPointDbm);

        // Drag the 2f₀ LOAD glyph; every other glyph supplies its present value as a target. That is
        // the whole point of R-h6-6: holding the others still is itself an equation.
        var target = (Complex[])here.Clone();
        target[1] += new Complex(0.04, 0.035);

        var r = solver.Step(ctx, target);
        output.WriteLine($"8×8: converged={r.Converged} ({r.Failure}) ‖F‖={r.Residual:E3}, " +
                         $"{r.Iterations} iteration(s), {r.Solves} solves");
        Assert.True(r.Converged, $"the 8×8 inverse solve failed with {r.Failure}");

        var reached = ForwardIntrinsic(ctx, ApplyToTerms(terms, bands, r.Gammas), bands,
                                       OperatingPointDbm);
        for (int i = 0; i < bands.Length; i++)
        {
            double err = (reached[i] - target[i]).Magnitude;
            output.WriteLine($"  {bands[i].Side}{bands[i].Band}: |err| = {err:E3} " +
                             $"(extrinsic Γ moved {(r.Gammas[i] - HarmonicaDataSet.GammaOf(terms.Z(bands[i].Side, bands[i].Band))).Magnitude:F4})");
            Assert.True(err < 2e-3,
                $"{bands[i].Side}{bands[i].Band} landed {err:E3} from its target in the 8×8 system");
        }

        // The COUPLING is real and this fixture shows it: moving the 2f₀ load glyph required moving
        // more than the 2f₀ load termination. A per-band solve would have moved exactly one.
        int moved = 0;
        for (int i = 0; i < bands.Length; i++)
            if ((r.Gammas[i] - HarmonicaDataSet.GammaOf(terms.Z(bands[i].Side, bands[i].Band))).Magnitude > 1e-4)
                moved++;
        output.WriteLine($"{moved} of {bands.Length} extrinsic terminations moved to hold four targets");
        Assert.True(moved >= 2,
            "only one termination moved — if the bands were genuinely uncoupled here this fixture " +
            "cannot demonstrate why R-h6-6 solves them simultaneously");
    }

    // ══ R-h6-9 — A FAILED SOLVE MOVES NOTHING ════════════════════════════════

    [Fact]
    public void AnUnreachableTarget_LeavesTheGlyphAndTheExtrinsicSetExactlyWhereTheyWere()
    {
        var model = Model(new LumpedPackage { Rs = 0.35, Ls = 25e-12, Rd = 0.6 });
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);

        InverseBand[] bands = [new(TerminationSide.Load, 1)];
        var solver = StartedSolver(ctx, terms, bands);

        var before = solver.Current.ToArray();
        var here   = ForwardIntrinsic(ctx, terms, bands, OperatingPointDbm);

        // Deliberately unreachable: a long way outside anything an extrinsic termination can produce
        // at this operating point.
        var r = solver.Step(ctx, [new Complex(-40.0, 55.0)]);

        output.WriteLine($"unreachable target → converged={r.Converged}, failure={r.Failure}, " +
                         $"‖F‖={r.Residual:E3}");
        Assert.False(r.Converged);
        Assert.NotEqual(InverseFailure.None, r.Failure);

        // EXACTLY where they were — not nearly.
        Assert.Equal(before.Length, solver.Current.Count);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Real,      solver.Current[i].Real);
            Assert.Equal(before[i].Imaginary, solver.Current[i].Imaginary);
        }
        Assert.Equal(before.Length, r.Gammas.Length);
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], r.Gammas[i]);

        // And the forward path still puts the glyph where it always was.
        var after = ForwardIntrinsic(ctx, ApplyToTerms(terms, bands, solver.Current), bands,
                                     OperatingPointDbm);
        Assert.Equal(here[0].Real,      after[0].Real,      precision: 10);
        Assert.Equal(here[0].Imaginary, after[0].Imaginary, precision: 10);
    }

    // ══ BROYDEN AND FULL-FD-EVERY-FRAME REACH THE SAME ANSWER ════════════════

    [Fact]
    public void BroydenAndFullFdEveryFrame_ReachTheSameAnswer_AndTheCostsAreReported()
    {
        var model = Model(new LumpedPackage { Rs = 0.3, Ls = 20e-12, Rd = 0.5 });
        var terms = Terms(model);
        InverseBand[] bands = [new(TerminationSide.Load, 1), new(TerminationSide.Load, 2)];

        // A short drag: the same eight targets fed to two solvers that differ ONLY in whether the
        // Jacobian is rebuilt from finite differences every frame.
        var ctxA = HarmonicaContext.Create(model, Settings);
        var here = ForwardIntrinsic(ctxA, terms, bands, OperatingPointDbm);
        var path = Enumerable.Range(1, 8)
            .Select(i => new[] { here[0] + new Complex(0.008 * i, -0.006 * i), here[1] })
            .ToArray();

        var broyden = StartedSolver(ctxA, terms, bands);
        var swB = Stopwatch.StartNew();
        foreach (var t in path) Assert.True(broyden.Step(ctxA, t).Converged);
        swB.Stop();

        var ctxB = HarmonicaContext.Create(model, Settings);
        var everyFrame = StartedSolver(ctxB, terms, bands,
            new InverseSolveOptions { PavlDbm = OperatingPointDbm, SourceFdRefreshEveryFrames = 0 });
        var swF = Stopwatch.StartNew();
        foreach (var t in path)
        {
            // "full FD every frame" is the alternative §6.6 rejects — reproduced here by beginning
            // afresh at every frame, which is exactly what rebuilding the Jacobian each time means.
            Assert.Equal(InverseFailure.None, everyFrame.Begin(ctxB));
            Assert.True(everyFrame.Step(ctxB, t).Converged);
        }
        swF.Stop();

        for (int i = 0; i < bands.Length; i++)
        {
            double d = (broyden.Current[i] - everyFrame.Current[i]).Magnitude;
            output.WriteLine($"  {bands[i].Side}{bands[i].Band}: Broyden {Fmt(broyden.Current[i])} " +
                             $"vs full-FD {Fmt(everyFrame.Current[i])}, |Δ| = {d:E3}");
            Assert.True(d < 5e-3, $"the two methods disagree by {d:E3} on {bands[i].Side}{bands[i].Band}");
        }

        output.WriteLine($"Broyden:      {broyden.SolveCount} solves, {broyden.FdBuildCount} FD build(s), " +
                         $"{broyden.BroydenUpdateCount} rank-1 update(s), {swB.Elapsed.TotalMilliseconds:F1} ms " +
                         $"over {path.Length} frames");
        output.WriteLine($"full FD/frame: {everyFrame.SolveCount} solves, {everyFrame.FdBuildCount} FD build(s), " +
                         $"{swF.Elapsed.TotalMilliseconds:F1} ms");

        Assert.True(broyden.SolveCount < everyFrame.SolveCount,
            $"Broyden cost {broyden.SolveCount} solves against full-FD's {everyFrame.SolveCount} — " +
            "if it is not cheaper there is no reason to carry a Jacobian across frames at all");
    }

    // ══ THE SOURCE SIDE (open item 8) ════════════════════════════════════════

    [Fact]
    public void ASourceSideDrag_ConvergesThroughTheSection453Diagonal_NotThroughARatio()
    {
        // R-h6-7 — the source-side residual is the §4.5.3 conversion-matrix DIAGONAL. The fixture has
        // a shared source lead, which is exactly the case where that diagonal departs from the
        // passive source network.
        var model = Model(new LumpedPackage { Rs = 0.4, Ls = 40e-12 });
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);

        InverseBand[] bands = [new(TerminationSide.Source, 1)];
        var solver = StartedSolver(ctx, terms, bands);

        var here   = ForwardIntrinsic(ctx, terms, bands, OperatingPointDbm);
        var target = new[] { here[0] + new Complex(0.03, 0.02) };

        var r = solver.Step(ctx, target);
        output.WriteLine($"source side: converged={r.Converged} ({r.Failure}) ‖F‖={r.Residual:E3}, " +
                         $"{r.Iterations} iteration(s), {r.Solves} solves, {r.FdRefreshes} FD refresh(es)");
        Assert.True(r.Converged, $"the source-side inverse solve failed with {r.Failure}");

        var reached = ForwardIntrinsic(ctx, ApplyToTerms(terms, bands, r.Gammas), bands,
                                       OperatingPointDbm);
        double err = (reached[0] - target[0]).Magnitude;
        output.WriteLine($"  S1: target {Fmt(target[0])} reached {Fmt(reached[0])}  |err| = {err:E3}");
        Assert.True(err < 2e-3, $"the source glyph landed {err:E3} from its target");
    }

    [Fact]
    public void AnActiveFundamentalSourceTermination_IsRefusedByNAME_NotSilentlySolvedAgainstNoDrive()
    {
        // The one candidate that is ill-posed rather than merely unusual: available power is not
        // defined against a source with Re Z ≤ 0, so the drive amplitude — and the whole stated-drive
        // operating point R-h6-11 rests on — collapses. DriveVolts would quietly return 0 V and the
        // solve would converge to the quiescent point with a residual that means nothing.
        var model = Model();
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);

        InverseBand[] bands = [new(TerminationSide.Source, 1)];
        var solver = new InverseSolver(terms, bands, [new Complex(1.4, 0.0)],
                                       new InverseSolveOptions { PavlDbm = OperatingPointDbm });

        var fail = solver.Begin(ctx);
        Assert.Equal(InverseFailure.ActiveSourceFundamental, fail);

        // The guard is specific: the LOAD side at the same |Γ| is fine, because a load's negative
        // resistance does not enter the available-power definition.
        Assert.True(HarmonicaDataSet.ImpedanceOf(new Complex(1.4, 0.0)).Real < 0);
        var loadSolver = new InverseSolver(terms, [new InverseBand(TerminationSide.Load, 1)],
                                           [new Complex(0.3, 0.1)],
                                           new InverseSolveOptions { PavlDbm = OperatingPointDbm });
        Assert.Equal(InverseFailure.None, loadSolver.Begin(ctx));
    }

    // ══ R-h6-10 — an out-of-circle extrinsic SOLUTION is allowed ═════════════

    [Fact]
    public void AnOutOfCircleExtrinsicSolution_IsAllowed_NotClamped()
    {
        // §6.6's closing sentence. Nothing in the solver may clamp |Γ| ≤ 1: the impedance conversion
        // has to carry a negative-resistance load through without complaint, and the termination set
        // has to accept it.
        var z = HarmonicaDataSet.ImpedanceOf(new Complex(1.6, 0.4));
        Assert.True(z.Real < 0, "|Γ| > 1 must map to a negative-resistance impedance, not be clamped");
        Assert.Equal(1.6, HarmonicaDataSet.GammaOf(z).Real, precision: 10);
        Assert.Equal(0.4, HarmonicaDataSet.GammaOf(z).Imaginary, precision: 10);

        var model = Model();
        var terms = Terms(model);
        var solver = new InverseSolver(terms, [new InverseBand(TerminationSide.Load, 1)],
                                       [new Complex(1.6, 0.4)],
                                       new InverseSolveOptions { PavlDbm = OperatingPointDbm });
        var built = solver.TerminationsFor([new Complex(1.6, 0.4)]);
        Assert.True(built.Z(TerminationSide.Load, 1).Real < 0);
    }

    private static string Fmt(Complex z) => $"{z.Real:F5}{(z.Imaginary < 0 ? "" : "+")}{z.Imaginary:F5}j";
}
