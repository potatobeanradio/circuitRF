using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Matching;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Match;

/// <summary>
/// MN-4's probe (match.md §10): looking outward from a <c>Match</c> pin, fitting the four
/// two-element termination models, and ranking them in Γ.
///
/// <para><b>The oracle is a network whose exact answer is known by construction.</b> A bare
/// 200 Ω ‖ 0.125 pF IS a parallel RC, so the correct fit is not "close" — it is exact, and anything
/// the probe reports beyond rounding is the probe's own error and not the fixture's.</para>
/// </summary>
public class TerminationProbeTests(ITestOutputHelper output)
{
    private const double F1 = 3.3e9;
    private const double F2 = 5.0e9;
    private const int Points = 41;

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string MatchLine(string name = "MN1", string p1 = "p1", string p2 = "p2")
        => $"Match:{name}  {p1} {p2}  Design={MatchEmbedding.EncodeToken(MatchEmbedding.DefaultDesign())}";

    private static TestBench Bench(string cnl, out Library lib)
    {
        var (l, tb) = new CnlReader().Read(cnl);
        lib = l;
        return tb;
    }

    private static TerminationProbe.ProbeResult Run(
        string cnl, int pinIndex = 0, bool conjugate = false,
        double f1 = F1, double f2 = F2, int points = Points,
        double warn = TerminationProbe.DefaultResidualWarning,
        AnalysisSettings? settings = null)
    {
        var tb = Bench(cnl, out var lib);
        return TerminationProbe.Probe(tb, "MN1", pinIndex, f1, f2, points, conjugate,
                                      lib, residualWarning: warn, settings: settings);
    }

    private void Report(string title, TerminationProbe.ProbeResult r)
    {
        output.WriteLine($"── {title}");
        if (r.Refusal is not null) { output.WriteLine($"   REFUSED: {r.Refusal}"); return; }
        foreach (var f in r.Fits)
            output.WriteLine(
                $"   {f.Name,-14} R = {f.R,12:G6} Ω  {(f.Kind == ReactanceKind.C ? "C" : "L")} = "
                + $"{f.Value,12:G6}  |ΔΓ| = {f.Residual:G4}{(f.Physical ? "" : "   (non-physical)")}");
        if (r.Flagged) output.WriteLine($"   FLAG: {r.Flag}");
    }

    // ── §7 row 1: round trip, parallel RC ─────────────────────────────────────

    [Fact]
    public void ParallelRc_ProbesBackToItself_AndRanksParallelRcFirst()
    {
        var r = Run($"""
            {MatchLine()}
            R:RL  p1 0  R=200
            C:CL  p1 0  C={N(0.125e-12)}
            R:RR  p2 0  R=1.25
            """);
        Report(nameof(ParallelRc_ProbesBackToItself_AndRanksParallelRcFirst), r);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(TerminationTopology.Parallel, r.Best!.Topology);
        Assert.Equal(ReactanceKind.C, r.Best.Kind);
        Assert.Equal(200.0, r.Best.R, 200.0 * 1e-3);
        Assert.Equal(0.125e-12, r.Best.Value, 0.125e-12 * 1e-3);
        Assert.Same(r.Best, r.Fits[0]);
        Assert.False(r.Flagged);
    }

    // ── §7 row 2: round trip, series RC ───────────────────────────────────────

    [Fact]
    public void SeriesRc_ProbesBackToItself_AndRanksSeriesRcFirst()
    {
        var r = Run($"""
            {MatchLine()}
            R:RL  p1 n1  R=1.25
            C:CL  n1 0   C={N(10e-12)}
            R:RR  p2 0   R=50
            """);
        Report(nameof(SeriesRc_ProbesBackToItself_AndRanksSeriesRcFirst), r);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(TerminationTopology.Series, r.Best!.Topology);
        Assert.Equal(ReactanceKind.C, r.Best.Kind);
        Assert.Equal(1.25, r.Best.R, 1.25 * 1e-3);
        Assert.Equal(10e-12, r.Best.Value, 10e-12 * 1e-3);
        Assert.False(r.Flagged);
    }

    // ── §7 row 3: both inductive ──────────────────────────────────────────────

    [Fact]
    public void SeriesRl_ProbesBackToItself()
    {
        var r = Run($"""
            {MatchLine()}
            R:RL  p1 n1  R=1.25
            L:LL  n1 0   L={N(153.517e-12)}
            R:RR  p2 0   R=50
            """);
        Report(nameof(SeriesRl_ProbesBackToItself), r);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(TerminationTopology.Series, r.Best!.Topology);
        Assert.Equal(ReactanceKind.L, r.Best.Kind);
        Assert.Equal(1.25, r.Best.R, 1.25 * 1e-3);
        Assert.Equal(153.517e-12, r.Best.Value, 153.517e-12 * 1e-3);
    }

    [Fact]
    public void ParallelRl_ProbesBackToItself()
    {
        // C_eq = 1/(w0^2 L) — match.md §5.1's substitution, so this is the inductive twin of the
        // 200 Ω ‖ 0.125 pF fixture and carries the same Q.
        double omega0 = 2 * Math.PI * Math.Sqrt(F1 * F2);
        double l = 1.0 / (omega0 * omega0 * 0.125e-12);

        var r = Run($"""
            {MatchLine()}
            R:RL  p1 0  R=200
            L:LL  p1 0  L={N(l)}
            R:RR  p2 0  R=1.25
            """);
        Report(nameof(ParallelRl_ProbesBackToItself), r);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(TerminationTopology.Parallel, r.Best!.Topology);
        Assert.Equal(ReactanceKind.L, r.Best.Kind);
        Assert.Equal(200.0, r.Best.R, 200.0 * 1e-3);
        Assert.Equal(l, r.Best.Value, l * 1e-3);
    }

    // ── §7 row 4: topology discrimination, and the evidence for the Γ metric ──

    public static TheoryData<string, double, double, bool> DiscriminationCases() => new()
    {
        // R, C, isParallel — a spread of Q at both topologies.
        { "parallel, Q ~ 0.6",   200,   0.125e-12, true  },
        { "parallel, Q ~ 3.2",  1000,   0.125e-12, true  },
        { "parallel, Q ~ 0.16",   50,   0.125e-12, true  },
        { "series,   Q ~ 3.1",  1.25,     10e-12, false },
        { "series,   Q ~ 0.78",    5,     10e-12, false },
        { "series,   Q ~ 0.39",   10,     10e-12, false },
    };

    /// <summary>One R-and-C load in the stated topology, with the Match still in the schematic.</summary>
    private static string Fixture(double r, double c, bool isParallel) => isParallel
        ? $"""
           {MatchLine()}
           R:RL  p1 0  R={N(r)}
           C:CL  p1 0  C={N(c)}
           R:RR  p2 0  R=50
           """
        : $"""
           {MatchLine()}
           R:RL  p1 n1  R={N(r)}
           C:CL  n1 0   C={N(c)}
           R:RR  p2 0   R=50
           """;

    [Theory]
    [MemberData(nameof(DiscriminationCases))]
    public void TopologyIsDiscriminated(string label, double r, double c, bool isParallel)
    {
        var result = Run(Fixture(r, c, isParallel));
        Report(label, result);

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(isParallel ? TerminationTopology.Parallel : TerminationTopology.Series,
                     result.Best!.Topology);
        Assert.Equal(ReactanceKind.C, result.Best.Kind);
        Assert.Equal(r, result.Best.R, r * 1e-3);
        Assert.Equal(c, result.Best.Value, c * 1e-3);
    }

    /// <summary>
    /// match.md §10.2's metric choice, stated as measured evidence rather than as an assertion of
    /// taste — <b>and the brief's own version of this claim did not survive the measurement.</b>
    ///
    /// <para>MN-4 §7 asks this test to show that an impedance-domain metric "would have got at least
    /// one of these wrong". On the discrimination fixtures it does not, and no fixture was found where
    /// it does: a load that IS exactly one of the four models is reproduced exactly by that model, so
    /// every metric scores it at ~1e-14 and none of them can misrank it. A 1,680-point sweep over
    /// R = 100 Ω..900 Ω, C = 0.05..0.5 pF, a series bond-wire inductance of 0..1 nH and an access
    /// resistance of 0..50 Ω — the region where the parallel R is actually visible, i.e. where
    /// "genuinely parallel" has a determinable meaning at all — put the two metrics on different
    /// models 2 times out of 1,680, both marginal. Every case where they diverge more than that is one
    /// where the resistance is invisible (R >> 1/wC) and the true topology is genuinely ambiguous, so
    /// calling either metric "wrong" there would be reading a preference as a fact.</para>
    ///
    /// <para><b>What IS demonstrable is the operative harm, and it is the reason the choice matters:
    /// a lower impedance-domain residual does not mean a better match.</b> On a network where the two
    /// disagree, the model an impedance-domain ranking selects misstates the reflection by 3x. Γ is
    /// the quantity the synthesis exists to null, so ranking in it is ranking in the thing the user is
    /// about to design against.</para>
    /// </summary>
    [Fact]
    public void TheGammaMetricAgreesWithTheImpedanceMetricWhereverTheTopologyIsDeterminable()
    {
        foreach (var row in DiscriminationCases())
        {
            string label = (string)row[0];
            double r = (double)row[1], c = (double)row[2];
            bool isParallel = (bool)row[3];
            var want = isParallel ? TerminationTopology.Parallel : TerminationTopology.Series;

            var result = Run(Fixture(r, c, isParallel));
            Assert.True(result.Ok, result.Refusal);

            var byZ = result.Fits.Where(f => f.Physical).OrderBy(f => MeanAbsZError(f, result)).First();
            output.WriteLine(
                $"{label,-18} Γ picks {result.Best!.Name,-14}  |ΔZ| picks {byZ.Name,-14}"
                + $"  (|ΔZ| = {MeanAbsZError(byZ, result):G4} Ω)");

            Assert.Equal(want, result.Best.Topology);
            Assert.Equal(want, byZ.Topology);   // measured, not assumed: both metrics are right here
        }
    }

    /// <summary>
    /// The demonstrable half of §10.2's claim: where the two metrics DO pick different models, the
    /// impedance-domain pick reproduces the reflection materially worse.
    /// </summary>
    [Fact]
    public void WhereTheMetricsDisagree_TheImpedancePickIsAMateriallyWorseMatch()
    {
        // 20 Ω of access resistance in front of a 20 kΩ ‖ 0.05 pF device output, over a decade. |Z|
        // runs from ~3.2 kΩ at 1 GHz to ~320 Ω at 10 GHz, and an impedance-domain average is therefore
        // dominated by the low end — which is exactly the over-weighting §10.2 names.
        var result = Run($"""
            {MatchLine()}
            R:RA  p1 n1  R=20
            R:RL  n1 0   R=20000
            C:CL  n1 0   C={N(0.05e-12)}
            R:RR  p2 0   R=50
            """, f1: 1e9, f2: 10e9, points: 61);
        Report(nameof(WhereTheMetricsDisagree_TheImpedancePickIsAMateriallyWorseMatch), result);

        Assert.True(result.Ok, result.Refusal);
        var byZ = result.Fits.Where(f => f.Physical).OrderBy(f => MeanAbsZError(f, result)).First();
        output.WriteLine(
            $"Γ picks {result.Best!.Name} (|ΔΓ| = {result.Best.Residual:G3}); "
            + $"|ΔZ| picks {byZ.Name} (|ΔΓ| = {byZ.Residual:G3})");

        Assert.NotEqual(result.Best.Name, byZ.Name);
        Assert.True(byZ.Residual > 2.0 * result.Best.Residual,
            $"the impedance-domain pick was only {byZ.Residual / result.Best.Residual:F2}x worse in Γ, "
            + "so this fixture no longer shows the two metrics differing in a way that matters.");
    }

    private static double MeanAbsZError(TerminationProbe.ProbeFit fit, TerminationProbe.ProbeResult r)
    {
        double sum = 0;
        for (int i = 0; i < r.Frequencies.Length; i++)
            sum += (fit.ImpedanceAt(r.Frequencies[i]) - r.Impedance[i]).Magnitude;
        return sum / r.Frequencies.Length;
    }

    // ── §7 row 5: conjugate ───────────────────────────────────────────────────

    [Fact]
    public void Conjugate_TurnsAMeasuredParallelRcIntoAParallelRlTarget()
    {
        string cnl = $"""
            {MatchLine()}
            R:RL  p1 0  R=200
            C:CL  p1 0  C={N(0.125e-12)}
            R:RR  p2 0  R=1.25
            """;

        var plain = Run(cnl);
        var conj = Run(cnl, conjugate: true);
        Report("conjugate off", plain);
        Report("conjugate on", conj);

        Assert.True(conj.Ok, conj.Refusal);
        Assert.True(conj.Conjugate);

        // The MEASUREMENT is unchanged by the toggle — it is the same network either way, and every
        // residual on screen stays a statement about it.
        Assert.Equal(plain.Best!.Name, conj.Best!.Name);
        Assert.Equal(plain.Best.Value, conj.Best.Value, plain.Best.Value * 1e-9);

        // The TARGET is the conjugate: same R, same topology, C -> L through §5.1's own identity.
        Assert.Equal(TerminationTopology.Parallel, conj.Target!.Topology);
        Assert.Equal(ReactanceKind.L, conj.Target.Kind);
        Assert.Equal(200.0, conj.Target.R, 200.0 * 1e-3);

        double omega0 = 2 * Math.PI * Math.Sqrt(F1 * F2);
        double expected = 1.0 / (omega0 * omega0 * plain.Best.Value);
        Assert.Equal(expected, conj.Target.Value, expected * 1e-9);

        // …and the whole point of §5: the synthesis reads a termination only through C_eq and Q at
        // band centre, and those are identical to the measured ones. The inductive target is not an
        // approximation of anything the synthesis can see.
        Assert.Equal(plain.Best.Value, conj.Termination!.CeqAt(omega0), plain.Best.Value * 1e-9);
        Assert.Equal(plain.Termination!.QAt(omega0), conj.Termination.QAt(omega0),
                     plain.Termination.QAt(omega0) * 1e-9);
        Assert.True(conj.Termination.Probed);
    }

    /// <summary>
    /// The measured impedance is <b>never</b> conjugated before fitting, and this is why: Z* of a
    /// parallel R‖C is a parallel R with a NEGATIVE capacitance, which fits Z* essentially exactly and
    /// cannot be built. Ranking the four models against Z* therefore awards the answer to whichever
    /// physical model best follows a curve nothing physical produces — here a SERIES R+L, a different
    /// end-arm topology from the parallel R‖L match.md §5.4 says the user needs.
    /// </summary>
    [Fact]
    public void FittingTheConjugatedDataWouldPickTheWrongTopology()
    {
        var plain = Run($"""
            {MatchLine()}
            R:RL  p1 0  R=200
            C:CL  p1 0  C={N(0.125e-12)}
            R:RR  p2 0  R=1.25
            """);
        Assert.True(plain.Ok, plain.Refusal);

        var againstZStar = TerminationProbe.Fit(
            plain.Frequencies, [.. plain.Impedance.Select(Complex.Conjugate)], conjugate: false);
        Report("fitted against Z* (the route NOT taken)", againstZStar);

        Assert.Equal(TerminationTopology.Series, againstZStar.Best!.Topology);
        Assert.Equal(ReactanceKind.L, againstZStar.Best.Kind);
        output.WriteLine(
            $"fitting Z* directly ranks {againstZStar.Best.Name} first; conjugating the fit gives "
            + $"parallel R‖L, which is what match.md §5.4 asks for.");
    }

    // ── §7 row 6: the Match is excluded ───────────────────────────────────────

    [Fact]
    public void TheMatchIsNotInTheCircuitBeingMeasured()
    {
        string external = $"""
            R:RL  p1 0  R=200
            C:CL  p1 0  C={N(0.125e-12)}
            R:RR  p2 0  R=1.25
            """;

        var probed = Run($"{MatchLine()}\n{external}");
        Report("probe (Match deleted by the probe)", probed);

        // The same network with the Match deleted by hand and a 50 Ω Term attached, measured through
        // the ordinary S-parameter path and fitted by the same code.
        var byHand = TerminationProbe.Fit(
            Frequencies(), MeasureZ($"Term:TP  p1 0  Num=1 Z=50\n{external}"), conjugate: false);
        Report("hand-built, Match absent", byHand);

        Assert.True(probed.Ok);
        Assert.True(byHand.Ok);
        Assert.Equal(byHand.Best!.Topology, probed.Best!.Topology);
        Assert.Equal(byHand.Best.Kind, probed.Best.Kind);
        Assert.Equal(byHand.Best.R, probed.Best.R, byHand.Best.R * 1e-9);
        Assert.Equal(byHand.Best.Value, probed.Best.Value, byHand.Best.Value * 1e-9);

        // …and leaving the Match IN is a materially different measurement, which is the whole reason
        // step 2 deletes it. If these ever agreed, the exclusion would be untested rather than safe.
        var withMatch = TerminationProbe.Fit(
            Frequencies(), MeasureZ($"Term:TP  p1 0  Num=1 Z=50\n{MatchLine()}\n{external}"),
            conjugate: false);
        Report("hand-built, Match LEFT IN", withMatch);
        Assert.True(withMatch.Best!.Value > 5.0 * probed.Best.Value,
            $"Leaving the Match in place reported {withMatch.Best.Value:G4} F against the excluded "
            + $"answer's {probed.Best.Value:G4} F, so this fixture cannot show that the exclusion "
            + "matters.");
    }

    private static double[] Frequencies()
    {
        var f = new double[Points];
        for (int i = 0; i < Points; i++) f[i] = F1 + (F2 - F1) * i / (Points - 1.0);
        return f;
    }

    /// <summary>Z looking into port 1 of <paramref name="cnl"/>, through the ordinary engine path.</summary>
    private static Complex[] MeasureZ(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(netlist, Frequencies());
        var cube = ds["S"];
        int np = cube.Axes[1].Length;
        var raw = cube.ComplexValues;
        var z = new Complex[cube.Axes[0].Length];
        for (int i = 0; i < z.Length; i++)
        {
            var g = raw[i * np * np];
            z[i] = 50.0 * (Complex.One + g) / (Complex.One - g);
        }
        return z;
    }

    // ── §7 row 7: a biased FET ────────────────────────────────────────────────

    private static string BiasedFet(double vgs) => $"""
        {MatchLine("MN1", "d", "p2")}
        FET_Curtice:Q1  g d 0  Vto=-2 Beta=0.002 Lambda=0.05 Cgs={N(1e-12)} Cgd={N(0.1e-12)}
        Vdc:VG  g 0   V={N(vgs)}
        R:RD    d dd  R=400
        Vdc:VD  dd 0  V=5
        R:RR    p2 0  R=50
        """;

    [Fact]
    public void BiasedFet_ProbesToASensibleImpedance_AndTheAnswerMovesWithBias()
    {
        var a = Run(BiasedFet(-1.0));
        var b = Run(BiasedFet(-0.2));
        Report("FET at Vgs = -1.0 V", a);
        Report("FET at Vgs = -0.2 V", b);

        Assert.True(a.Ok, a.Refusal);
        Assert.True(b.Ok, b.Refusal);

        // A saturated FET drain into a 400 Ω bias resistor is a parallel RC: r_ds ‖ R_D, with Cgd
        // hanging off it (the gate is an AC ground through the ideal supply).
        Assert.Equal(TerminationTopology.Parallel, a.Best!.Topology);
        Assert.Equal(ReactanceKind.C, a.Best.Kind);
        Assert.InRange(a.Best.R, 1.0, 400.0);
        Assert.InRange(a.Best.Value, 1e-15, 10e-12);

        // Step 4's whole point: the bias network was KEPT, so moving the operating point moves the
        // answer. If it did not, the probe would be reporting a zero-bias linearization.
        Assert.True(Math.Abs(a.Best.R - b.Best!.R) > 0.02 * a.Best.R,
            $"R did not move with bias: {a.Best.R:G6} Ω at -1.0 V vs {b.Best.R:G6} Ω at -0.2 V.");
        output.WriteLine($"R(-1.0 V) = {a.Best.R:G6} Ω,  R(-0.2 V) = {b.Best.R:G6} Ω");
    }

    // ── §7 row 8: a DC failure is a refusal, not an impedance ─────────────────

    [Fact]
    public void DcFailure_Refuses_RatherThanReportingAZeroBiasImpedance()
    {
        // One Newton iteration and no continuation: the DC solve cannot converge, which is exactly
        // the state §1 says must refuse. SParameterEngine's own behaviour here is to warn and
        // linearize at 0 V — the right default for an ordinary analysis, and the wrong one for a
        // measurement the user is about to design a matching network against.
        var settings = new AnalysisSettings
        {
            NonlinearMaxIter = 1,
            DcBiasStepping = DcBiasSteppingMode.Never,
            NonlinearMaxContinuationSteps = 1,
        };
        var r = Run(BiasedFet(-1.0), settings: settings);
        Report(nameof(DcFailure_Refuses_RatherThanReportingAZeroBiasImpedance), r);

        Assert.False(r.Ok);
        Assert.NotNull(r.Refusal);
        Assert.Contains("DC operating point", r.Refusal);
        Assert.Null(r.Termination);
        Assert.Empty(r.Fits);
    }

    // ── §7 row 9: a poor fit is applied AND flagged ───────────────────────────

    [Fact]
    public void AnInBandResonanceIsAppliedButFlagged()
    {
        // A series LC resonant at 4 GHz, in the middle of a 3.3-5 GHz band: the reactance passes
        // through zero and changes sign inside the band, which no two-element model can follow.
        double l = 2e-9;
        double c = 1.0 / (Math.Pow(2 * Math.PI * 4.0e9, 2) * l);

        var r = Run($"""
            {MatchLine()}
            R:RL  p1 n1  R=20
            L:LL  n1 n2  L={N(l)}
            C:CL  n2 0   C={N(c)}
            R:RR  p2 0   R=50
            """);
        Report(nameof(AnInBandResonanceIsAppliedButFlagged), r);

        Assert.True(r.Ok, r.Refusal);
        Assert.NotNull(r.Termination);
        Assert.True(r.Flagged, $"best residual was {r.Best!.Residual:G4}, below the threshold");
        Assert.Contains("not well described by a two-element model", r.Flag);
        output.WriteLine($"best residual = {r.Best!.Residual:G4}");
    }

    [Fact]
    public void TheWarningThresholdIsASetting()
    {
        string cnl = $"""
            {MatchLine()}
            R:RL  p1 0  R=200
            C:CL  p1 0  C={N(0.125e-12)}
            R:RR  p2 0  R=1.25
            """;
        Assert.False(Run(cnl).Flagged);
        // A threshold tight enough to flag an exact fit shows the number is genuinely a knob, and
        // that it only ever ADDS a warning — the result is applied either way.
        var tight = Run(cnl, warn: 1e-18);
        Assert.True(tight.Ok);
        Assert.NotNull(tight.Termination);
        Assert.True(tight.Flagged);
    }

    // ── Refusals that are not about numbers ───────────────────────────────────

    [Fact]
    public void AnUnknownInstanceRefusesByName()
    {
        var tb = Bench($"{MatchLine()}\nR:RL p1 0 R=50\nR:RR p2 0 R=50", out var lib);
        var r = TerminationProbe.Probe(tb, "MN7", 0, F1, F2, Points, false, lib);
        Assert.False(r.Ok);
        Assert.Contains("MN7", r.Refusal);
    }

    [Fact]
    public void TheCallersBenchIsNeverMutated()
    {
        var tb = Bench($"{MatchLine()}\nR:RL p1 0 R=200\nC:CL p1 0 C={N(0.125e-12)}\nR:RR p2 0 R=50",
                       out var lib);
        int instances = tb.Instances.Count;
        int analyses = tb.Analyses.Count;

        var r = TerminationProbe.Probe(tb, "MN1", 0, F1, F2, Points, false, lib);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(instances, tb.Instances.Count);
        Assert.Equal(analyses, tb.Analyses.Count);
        Assert.Contains(tb.Instances, i => i.InstanceName == "MN1");
    }

    /// <summary>
    /// An existing Term keeps its port number and stays in the circuit — it is part of the external
    /// network, and removing it would leave that node open and measure a different circuit.
    /// </summary>
    [Fact]
    public void AnExistingPortIsKeptAndTheProbeTakesTheNextFreeNumber()
    {
        var r = Run($"""
            {MatchLine()}
            Term:T1  src 0   Num=1 Z=50
            R:RS     src p1  R=150
            C:CL     p1 0    C={N(0.125e-12)}
            R:RR     p2 0    R=50
            """);
        Report(nameof(AnExistingPortIsKeptAndTheProbeTakesTheNextFreeNumber), r);

        // 150 Ω in series with the existing 50 Ω Term = 200 Ω, in parallel with the 0.125 pF.
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(TerminationTopology.Parallel, r.Best!.Topology);
        Assert.Equal(200.0, r.Best.R, 200.0 * 1e-3);
        Assert.Equal(0.125e-12, r.Best.Value, 0.125e-12 * 1e-3);
    }

    [Fact]
    public void BothPinsAreProbedIndependently()
    {
        string cnl = $"""
            {MatchLine()}
            R:RL  p1 0  R=200
            C:CL  p1 0  C={N(0.125e-12)}
            R:RR  p2 n1 R=1.25
            C:CR  n1 0  C={N(10e-12)}
            """;
        var left = Run(cnl, pinIndex: 0);
        var right = Run(cnl, pinIndex: 1);
        Report("pin 1", left);
        Report("pin 2", right);

        Assert.Equal(TerminationTopology.Parallel, left.Best!.Topology);
        Assert.Equal(200.0, left.Best.R, 0.2);
        Assert.Equal(TerminationTopology.Series, right.Best!.Topology);
        Assert.Equal(1.25, right.Best.R, 0.002);
    }
    // ══ A degenerate pin says what it is ═════════════════════════════════════

    /// <summary>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"with shunt L = 0, the Termination 2 does not probe
    /// correctly (can't find an impedance)."</i>
    /// </summary>
    /// <remarks>
    /// An ideal 0 H shunt inductor IS a short to ground, so the pin genuinely presents 0 Ω and
    /// applying nothing was right. The MESSAGE was wrong: with no guard the measurement fell through
    /// to the fitter, where all five models fit it perfectly (residual exactly 0) with R = 0 or NaN
    /// and were then each rejected for a non-positive R — so the user was told the fitter had failed
    /// and that negative elements were involved, when the answer is that the pin is shorted.
    ///
    /// <para>The OPEN case has always been caught before the fit and said plainly; this is the same
    /// statement about Γ = -1, which is the half that was missing.</para>
    /// </remarks>
    [Fact]
    public void AShortedPin_IsReportedAsAShort_NotAsAFailureToFit()
    {
        var r = Run($"""
            {MatchLine()}
            Term:T1  p1 0  Num=1 Z=50
            R:R2     p2 0  R=50
            L:L1     p2 0  L=0
            """, pinIndex: 1);

        Report("shunt L = 0 on pin 2", r);
        Assert.False(r.Ok);
        Assert.NotNull(r.Refusal);
        Assert.Contains("short to ground", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Γ = -1", r.Refusal, StringComparison.Ordinal);
        // It names the net to go and look at, not the fitter.
        Assert.Contains("'p2'", r.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("None of the five models", r.Refusal, StringComparison.Ordinal);
    }

    /// <summary>...and a very small but non-zero shunt L is still an ordinary, fitted measurement.</summary>
    [Fact]
    public void ANearlyShortedPin_StillFits()
    {
        var r = Run($"""
            {MatchLine()}
            Term:T1  p1 0  Num=1 Z=50
            R:R2     p2 0  R=50
            L:L1     p2 0  L=1 pH
            """, pinIndex: 1);

        Report("shunt L = 1 pH on pin 2", r);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(ReactanceKind.L, r.Best!.Kind);
        Assert.Equal(TerminationTopology.Parallel, r.Best.Topology);
        Assert.Equal(1e-12, r.Best.Value, 1e-15);
    }

}
