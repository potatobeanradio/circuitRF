using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9e / M1 — adaptive frequency sampling: Tier 0, Tier 1 and Tier 4.</b>
///
/// <para>The ladder in §5's order: Tier 0's structural checks (off is bit-identical, the published
/// grid is the requested grid, two runs are identical, the solved count is reported), then Tier 1's
/// DENSE REDUCTION — the strongest single check in the slice — then the measurements R-adf-6 asks
/// for.</para>
///
/// <para><b>R-adf-4: there is no losslessness assertion anywhere in this file, deliberately.</b>
/// L8a wrote the warning, L8d and L9d honoured it, and it is more true here: an open planar
/// structure with vias radiates MORE, not less. Reciprocity is what carries over, and it is
/// structural (L8d's D1) rather than something adaptive sampling could break.</para>
/// </summary>
public sealed class AdaptiveSweepTests
{
    private readonly ITestOutputHelper _out;
    public AdaptiveSweepTests(ITestOutputHelper output) => _out = output;

    /// <summary>The coarse FR-4 line — milliseconds a point, so the ALGEBRA of the scheme is testable
    /// in the routine tier. Every claim here is exact regardless of mesh quality.</summary>
    private static (PlanarProblem P, PlanarMesh M, IReadOnlyList<PlanarPortResolution> Ports) Fixture(
        double lengthM = 8e-3, double fRef = 6e9)
    {
        var line = PlanarLineFixtures.Fr4Line(lengthM, fRef);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line, PlanarLineFixtures.Coarse);
        return (line, mesh, ports);
    }

    private static double[] Grid(double f0, double f1, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 + (f1 - f0) * i / (n - 1);
        return f;
    }

    private static void AssertBitIdentical(PlanarSolveResult a, PlanarSolveResult b, string what)
    {
        Assert.Equal(a.Points.Count, b.Points.Count);
        for (int i = 0; i < a.Points.Count; i++)
        {
            Assert.Equal(a.Points[i].FrequencyHz, b.Points[i].FrequencyHz);
            for (int r = 0; r < a.Points[i].S.RowCount; r++)
            for (int c = 0; c < a.Points[i].S.ColCount; c++)
            {
                Assert.True(a.Points[i].S[r, c].Real      == b.Points[i].S[r, c].Real,      what);
                Assert.True(a.Points[i].S[r, c].Imaginary == b.Points[i].S[r, c].Imaginary, what);
                Assert.True(a.Points[i].RawS[r, c].Real   == b.Points[i].RawS[r, c].Real,   what);
            }
        }
    }

    // =========================================================================================
    // Tier 0 — structural, free.
    // =========================================================================================

    [Fact]
    public void T0_1_WithAdaptiveOFF_TheSweepIsBitIdenticalRunToRun()
    {
        // R-adf-1's first half. The second half — that the OFF path is bit-identical to what L8d
        // itself shipped — is pinned by MultiLevelPortTests.M1_1, which reconstructs L8d's own call
        // sequence by hand and compares at full precision; this slice refactored PlanarSolve.Run's
        // loop body into two local functions, and that test is what says the refactor moved nothing.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 5);

        var a = PlanarSolve.Run(p, m, ports, freqs);
        var b = PlanarSolve.Run(p, m, ports, freqs);

        AssertBitIdentical(a, b, "adaptive OFF must be deterministic to the bit");
        Assert.Equal(freqs.Length, a.SolvedPointCount);
        Assert.True(double.IsNaN(a.WorstAdaptiveDisagreement));
        Assert.Empty(a.SolvedFrequencies);
        _out.WriteLine($"N = {m.Bases.Count}, {freqs.Length} points, all solved; " +
                       $"S21(2 GHz) = {a.Points[0].S[1, 0]}");
    }

    [Fact]
    public void T0_2_ThePublishedGridIsTheREQUESTEDGrid_AndTheSolvedCountIsReported()
    {
        // R-adf-2. The user asked for their own frequency grid and must get exactly that grid back —
        // adaptive sampling is INVISIBLE in the output shape and LOUD in the notes.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 17);

        var run = PlanarSolve.Run(p, m, ports, freqs,
            new PlanarSolveSettings(Deembed: false,
                                    Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-2)));

        Assert.Equal(freqs.Length, run.Points.Count);
        for (int i = 0; i < freqs.Length; i++) Assert.Equal(freqs[i], run.Points[i].FrequencyHz);

        Assert.True(run.SolvedPointCount < freqs.Length,
            $"a smooth line at 1e-2 should not need every point: {run.SolvedPointCount}/{freqs.Length}");
        Assert.NotEmpty(run.SolvedFrequencies);
        Assert.Contains(run.Notes, n => n.Contains("Adaptive frequency sampling"));

        _out.WriteLine($"{run.SolvedPointCount} of {freqs.Length} solved; worst disagreement " +
                       $"{run.WorstAdaptiveDisagreement:E3}.");
        _out.WriteLine(run.Notes.First(n => n.Contains("Adaptive frequency sampling")));
    }

    [Fact]
    public void T0_3_TwoAdaptiveRunsOfTheSameProblem_AreIdenticalToTheBit()
    {
        // R-adf-3. An adaptive scheme HAS state; a set iteration or a floating-point tie in "which
        // interval disagreed most" would make two runs of the same problem choose different point
        // sets and therefore different interpolated values. The refinement here is bisection in
        // ascending INTERVAL INDEX order, so there is no tie to break by magnitude at all.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 17);
        var st = new PlanarSolveSettings(Deembed: false,
                                         Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-2));

        var a = PlanarSolve.Run(p, m, ports, freqs, st);
        var b = PlanarSolve.Run(p, m, ports, freqs, st);

        AssertBitIdentical(a, b, "two adaptive runs must be identical");
        Assert.Equal(a.SolvedFrequencies, b.SolvedFrequencies);
        Assert.Equal(a.WorstAdaptiveDisagreement, b.WorstAdaptiveDisagreement);
    }

    [Fact]
    public void T0_4_TheSeedSetIsADeterministicFunctionOfTheGridAlone()
    {
        Assert.Equal([0, 4], PlanarAdaptiveSweep.SeedIndices(5, 2));
        Assert.Equal([0, 2, 4], PlanarAdaptiveSweep.SeedIndices(5, 3));
        Assert.Equal([0, 1, 2, 3, 4], PlanarAdaptiveSweep.SeedIndices(5, 9));
        Assert.Equal([0], PlanarAdaptiveSweep.SeedIndices(1, 5));
        Assert.Empty(PlanarAdaptiveSweep.SeedIndices(0, 5));

        var a = PlanarAdaptiveSweep.SeedIndices(101, 5);
        Assert.Equal(a, PlanarAdaptiveSweep.SeedIndices(101, 5));
        Assert.Equal(0, a[0]);
        Assert.Equal(100, a[^1]);
    }

    [Theory]
    [InlineData(PlanarInterpolant.CubicSpline)]
    [InlineData(PlanarInterpolant.Rational)]
    public void T0_5_AModelledGridReturnsEveryNODEsOwnMatrixBitForBit(PlanarInterpolant which)
    {
        // The promise R-adf-2 rests on: a solved point must be distinguishable from a modelled one
        // by being EXACTLY what the solver produced. Both interpolants pass through their nodes
        // mathematically; this is a statement about bytes, and it is short-circuited rather than
        // trusted to exact arithmetic.
        double[] nodes = [1e9, 2e9, 4e9, 8e9];
        var values = nodes.Select((f, i) =>
        {
            var m = new Mat<Complex>(2, 2);
            m[0, 0] = new Complex(i * 0.3 + 0.1, -i * 0.7);
            m[1, 0] = new Complex(Math.Sin(i), Math.Cos(i));
            return m;
        }).ToArray();

        double[] targets = [1e9, 1.5e9, 2e9, 3e9, 4e9, 6e9, 8e9];
        var got = PlanarAdaptiveSweep.Model(nodes, values, targets, which);

        for (int t = 0; t < targets.Length; t++)
        {
            int node = Array.IndexOf(nodes, targets[t]);
            if (node < 0) continue;
            Assert.Equal(values[node][0, 0].Real,      got[t][0, 0].Real);
            Assert.Equal(values[node][0, 0].Imaginary, got[t][0, 0].Imaginary);
            Assert.Equal(values[node][1, 0].Real,      got[t][1, 0].Real);
        }

        // …and an interior target is a real interpolation, not a nearest-node copy.
        int mid = Array.IndexOf(targets, 3e9);
        Assert.NotEqual(values[1][0, 0].Real, got[mid][0, 0].Real);
        Assert.NotEqual(values[2][0, 0].Real, got[mid][0, 0].Real);
    }

    [Fact]
    public void T0_6_TheCALIBRATORReplaysWithoutResolving_WhichIsHowTheOrderingCollisionIsPaidFor()
    {
        // §0.2 item 2, measured rather than asserted. PlanarPortCalibrator is stateful and must be
        // stepped in increasing frequency order; adaptive refinement inserts points mid-band. The
        // resolution is to cache the standards' RAW scattering per frequency and replay the cheap
        // branch continuation over the sorted union after every insertion — so the number of SOLVES
        // must equal the number of distinct frequencies, however many replays happened.
        var (p, m, ports) = Fixture();
        var cal = new PlanarPortCalibrator(ports[0], p.Slab, 2e9, 6e9, null, null);
        var kernel = PlanarFrequencyKernel.FromPair(PlanarLineFixtures.Kernel(p.Slab, 2e9));

        double[] fs = [2e9, 3e9, 4e9];
        foreach (double f in fs) cal.At(() => PlanarFrequencyKernel.FromPair(
            PlanarLineFixtures.Kernel(p.Slab, f)), f);
        int afterFirstPass = cal.SolveCount;

        // Three full replays, out of order the first time and sorted afterwards.
        for (int round = 0; round < 3; round++)
        {
            cal.RestartBranchContinuation();
            foreach (double f in fs) cal.At(() => kernel, f);
        }

        Assert.Equal(fs.Length, afterFirstPass);
        Assert.Equal(fs.Length, cal.SolveCount);
        _out.WriteLine($"{fs.Length} distinct frequencies, 4 passes over them, " +
                       $"{cal.SolveCount} solve(s) — the replay is free.");
    }

    [Fact]
    public void T0_7_ARestartedContinuationReproducesTheSequentialAnswerExactly()
    {
        // The half of §0.2 item 2 that actually matters: replaying is only legitimate if it lands on
        // the SAME branch decisions a sequential sweep would have made. Same calibrator, same
        // frequencies, once straight through and once after a restart — bit for bit.
        var (p, _, ports) = Fixture();
        var cal = new PlanarPortCalibrator(ports[0], p.Slab, 2e9, 6e9, null, null);

        double[] fs = [2e9, 3e9, 4e9, 5e9, 6e9];
        PlanarFrequencyKernel K(double f) =>
            PlanarFrequencyKernel.FromPair(PlanarLineFixtures.Kernel(p.Slab, f));

        var first = fs.Select(f => cal.At(() => K(f), f)).ToArray();
        cal.RestartBranchContinuation();
        var again = fs.Select(f => cal.At(() => K(f), f)).ToArray();

        for (int i = 0; i < fs.Length; i++)
        {
            Assert.Equal(first[i].Gamma.Gamma.Real,      again[i].Gamma.Gamma.Real);
            Assert.Equal(first[i].Gamma.Gamma.Imaginary, again[i].Gamma.Gamma.Imaginary);
            Assert.Equal(first[i].Zc.Real,               again[i].Zc.Real);
            Assert.Equal(first[i].Box.A21.Real,          again[i].Box.A21.Real);
        }
        _out.WriteLine($"{fs.Length} frequencies replayed from a restarted branch state: γ, Z_c and " +
                       "the error box are bit-identical. β's 2π unwrap and a₂₁'s sign both land the " +
                       "same way, which is what makes inserting a point mid-band safe.");
    }

    // =========================================================================================
    // Tier 1 — the DENSE reduction, and the scheme's own error.
    // =========================================================================================

    [Fact]
    public void T1_1_ATOLERANCEOfZero_SolvesEveryPoint_AndIsBitIdenticalToTheNonAdaptiveSweep()
    {
        // §5's own "strongest single check in the slice", in the form that is actually decidable:
        // drive the refinement until it has nothing left to refine, and the adaptive path must
        // reproduce the non-adaptive one EXACTLY — not to a tolerance. This is what says the
        // replayed calibration, the modelled grid and the point assembly all collapse onto L8d's
        // own arithmetic when nothing is being approximated.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 5);

        var plain = PlanarSolve.Run(p, m, ports, freqs);
        var dense = PlanarSolve.Run(p, m, ports, freqs,
            new PlanarSolveSettings(Adaptive: new PlanarAdaptiveSettings(Tolerance: 0.0)));

        Assert.Equal(freqs.Length, dense.SolvedPointCount);
        AssertBitIdentical(plain, dense, "a fully-refined adaptive sweep must equal the plain one");
        _out.WriteLine($"{freqs.Length}/{freqs.Length} solved (de-embedded, N = {m.Bases.Count}); " +
                       "every de-embedded and raw " +
                       "s-parameter is bit-identical to the non-adaptive sweep.");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // three refinement sweeps plus the fully-solved truth, 16 s
    public void T1_2_TheSchemesOwnERROR_IsTheDifferenceAgainstTheFullySolvedAnswerOnTheSameGrid()
    {
        // The second half of Tier 1, and the measurement R-adf-6 is built on: ask for a grid the
        // scheme genuinely has to refine, and compare against the fully-solved answer point for
        // point. That difference IS the scheme's error — nothing here is a residual.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 10e9, 33);

        var truth = PlanarSolve.Run(p, m, ports, freqs, new PlanarSolveSettings(Deembed: false));

        _out.WriteLine("  tolerance   solved/33   worst |ΔS| against the fully-solved answer");
        foreach (double tol in new[] { 1e-1, 1e-2, 1e-3 })
        {
            var run = PlanarSolve.Run(p, m, ports, freqs,
                new PlanarSolveSettings(Deembed: false,
                                        Adaptive: new PlanarAdaptiveSettings(Tolerance: tol)));

            double worst = 0;
            for (int i = 0; i < freqs.Length; i++)
                worst = Math.Max(worst, PlanarAdaptiveSweep.WorstAbsDiff(run.Points[i].S, truth.Points[i].S));

            _out.WriteLine($"  {tol,9:G2}   {run.SolvedPointCount,6}/33   {worst:E3}");
            Assert.True(worst < 10 * tol,
                $"the realised error must track the tolerance asked for: {worst:E3} at tol {tol:G2}");
        }
    }

    [Fact]
    public void T1_3_ASOLVEDPointCarriesTheSolversOwnMatrix_AModelledOneDoesNot()
    {
        // The distinction R-adf-2 exists to keep visible, checked end to end rather than only on the
        // pure interpolant (T0_5).
        // The same grid and tolerance T0_2 already measures at 9 of 17 solved, so the fixture is
        // known to produce both kinds of point rather than assumed to.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 17);

        var truth = PlanarSolve.Run(p, m, ports, freqs, new PlanarSolveSettings(Deembed: false));
        var run = PlanarSolve.Run(p, m, ports, freqs,
            new PlanarSolveSettings(Deembed: false,
                                    Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-2)));

        int solvedChecked = 0, modelledChecked = 0;
        for (int i = 0; i < freqs.Length; i++)
        {
            bool isSolved = run.SolvedFrequencies.Contains(freqs[i]);
            if (isSolved)
            {
                Assert.Equal(truth.Points[i].S[1, 0].Real,      run.Points[i].S[1, 0].Real);
                Assert.Equal(truth.Points[i].S[1, 0].Imaginary, run.Points[i].S[1, 0].Imaginary);
                solvedChecked++;
            }
            else modelledChecked++;
        }

        Assert.True(solvedChecked > 0 && modelledChecked > 0,
            "the fixture must exercise both kinds of point");
        _out.WriteLine($"{solvedChecked} solved point(s) carry the solver's own matrix bit for bit; " +
                       $"{modelledChecked} are modelled.");
    }

    // =========================================================================================
    // D3 — WHICH interpolant, decided by measurement on a RESONANT structure.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // a λ_g/4 open stub at the shipping mesh, four sweeps, ~4 min
    public void T4_2_D3_WhichInterpolantWins_MeasuredOnTheResonantCase()
    {
        // D3: "try the spline first, measure both on a structure with a real resonance, and let the
        // measurement decide — the L7b-b Route A/Route B precedent exactly." R-adf-6 adds that the
        // RESONANT case is the one that decides it; a scheme that is excellent on a uniform line
        // proves nothing, which is why the smooth line is measured beside it rather than instead.
        //
        // The fixture is L8e's own λ_g/4 open stub — the same shape L8's phase gate turns on, and
        // the one place in this repository with a measured notch.
        double h = GroundedSlab.Fr4Starter.HeightM;
        double w = PlanarLineFixtures.Fr4HeroWidthM;
        double through = 24e-3, stub = 6.689e-3;   // L8d's own Tier 6 sizing

        var stubProblem = PlanarLineFixtures.Problem(
            GroundedSlab.Fr4Starter, 10e9,
            PlanarLineFixtures.Rect(0, -0.5 * w, through, 0.5 * w),
            PlanarLineFixtures.Rect(0.5 * through - 0.5 * w, 0.5 * w,
                                    0.5 * through + 0.5 * w, 0.5 * w + stub));
        var (sm, sPorts) = PlanarLineFixtures.MeshAndPorts(stubProblem, PlanarLineFixtures.Coarse);

        var smooth = Fixture(20e-3, 10e9);
        double[] band = Grid(3e9, 9e9, 33);

        _out.WriteLine($"resonant fixture: N = {sm.Bases.Count}; smooth: N = {smooth.M.Bases.Count}");
        _out.WriteLine("  structure   interpolant   solved/33   worst |ΔS| vs fully solved");

        foreach (var (name, prob, mesh, ports) in new[]
                 {
                     ("resonant", stubProblem, sm, sPorts),
                     ("smooth  ", smooth.P, smooth.M, smooth.Ports),
                 })
        {
            var truth = PlanarSolve.Run(prob, mesh, ports, band, new PlanarSolveSettings(Deembed: false));

            foreach (var interp in new[] { PlanarInterpolant.CubicSpline, PlanarInterpolant.Rational })
            {
                var run = PlanarSolve.Run(prob, mesh, ports, band,
                    new PlanarSolveSettings(Deembed: false,
                        Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-2, Interpolant: interp)));

                double worst = 0;
                for (int i = 0; i < band.Length; i++)
                    worst = Math.Max(worst,
                        PlanarAdaptiveSweep.WorstAbsDiff(run.Points[i].S, truth.Points[i].S));

                _out.WriteLine($"  {name}   {interp,-12}   {run.SolvedPointCount,6}/33   {worst:E3}");
            }
        }

        _out.WriteLine("\nThe number that decides D3 is the RESONANT row: a scheme that is excellent " +
                       "on a uniform line proves nothing (R-adf-6). Whichever wins there is what " +
                       "PlanarAdaptiveSettings should default to; see src/Engine/Mom/CLAUDE.md §L9e " +
                       "for the reading.");
    }

    // =========================================================================================
    // Tier 4 — the cost, which is the whole reason this slice exists.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // a de-embedded 33-point sweep, twice, ~3 min
    public void T4_1_TheMeasuredCurveOfSolvedPointsVersusTolerance_AndTheSweepTimeAgainstL9dsOwn()
    {
        // §10 item 3. §10.7 predicts 5-10× fewer solves; this says what it actually is, and on
        // which structure class it is worst. TAKE THIS MEASUREMENT ALONE OR NOT AT ALL — L8d's own
        // warning, and it applies here for the same reason: run alongside the other Benchmark tests
        // it reads more than twice as slow.
        var (p, m, ports) = Fixture(20e-3, 10e9);
        double[] freqs = Grid(2e9, 10e9, 33);

        var sw = Stopwatch.StartNew();
        var full = PlanarSolve.Run(p, m, ports, freqs);
        double fullS = sw.Elapsed.TotalSeconds;

        _out.WriteLine($"N = {m.Bases.Count}, {full.StandardCount} standard mesh(es).");
        _out.WriteLine($"fully solved: {freqs.Length} point(s) in {fullS:F2} s " +
                       $"({fullS / freqs.Length:F3} s each)\n");
        _out.WriteLine("  tolerance   solved/33   time (s)   speed-up   worst |ΔS| vs fully solved");

        foreach (double tol in new[] { 1e-1, 1e-2, 1e-3, 1e-4 })
        {
            sw.Restart();
            var run = PlanarSolve.Run(p, m, ports, freqs,
                new PlanarSolveSettings(Adaptive: new PlanarAdaptiveSettings(Tolerance: tol)));
            double s = sw.Elapsed.TotalSeconds;

            double worst = 0;
            for (int i = 0; i < freqs.Length; i++)
                worst = Math.Max(worst, PlanarAdaptiveSweep.WorstAbsDiff(run.Points[i].S, full.Points[i].S));

            _out.WriteLine($"  {tol,9:G2}   {run.SolvedPointCount,6}/33   {s,8:F2}   " +
                           $"{fullS / s,8:F2}×   {worst:E3}");
        }

        _out.WriteLine("\nAgainst L9d's own 71.9 s per de-embedded two-level point and ~73 minutes " +
                       "for 101 points: the saving is exactly the ratio of solved points, because " +
                       "nothing here makes one point cheaper. §10.7 predicts 5-10× fewer solves.");
        Assert.True(full.Points.Count == freqs.Length);
    }
}
