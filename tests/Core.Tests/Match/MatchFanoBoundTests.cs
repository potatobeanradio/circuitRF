using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §18.10 — the Fano ceiling, the gap rise and the closed-form loosen hints.
/// </summary>
/// <remarks>
/// <b>Every golden here is recomputed from the two integrals inside the test</b>, in R, C and 2πf,
/// rather than through <c>Termination.QAt</c>. Checking <see cref="MatchFanoBound"/> against its own
/// Q identity would only prove the class agrees with itself; the point of the ceiling is that it is a
/// theorem about the physics, so the physics is written out here a second time.
/// </remarks>
public class MatchFanoBoundTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const double NeperToDb = 8.685889638065035;

    /// <summary>
    /// The owner's report, 2026-08-28: 100 Ω ‖ 0.125 pF into 1.25 Ω + 5 pF series, over three bands
    /// that do not mirror.
    /// </summary>
    private static MatchDesign Owner(int order = 2) => new()
    {
        BandCount = 3,
        F1 = 2.5e9, F2 = 3.0e9, F3 = 4.5e9, F4 = 5.0e9, F5 = 9.0e9, F6 = 10.0e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(100.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 5e-12),
    };

    // ── An independent evaluation of the two integrals ────────────────────────

    /// <summary>
    /// <c>∫ ln(1/|Γ|) dω/ω² ≤ πRC</c> for a series R+C, written out in R, C and 2πf.
    /// </summary>
    private static double SeriesCeilingDb(double r, double c, params (double Lo, double Hi)[] bands)
    {
        double weight = 0.0;
        foreach (var (lo, hi) in bands)
            weight += 1.0 / (2.0 * Math.PI * lo) - 1.0 / (2.0 * Math.PI * hi);
        return -NeperToDb * (Math.PI * r * c / weight);
    }

    /// <summary><c>∫ ln(1/|Γ|) dω ≤ π/(RC)</c> for a parallel R‖C, likewise.</summary>
    private static double ParallelCeilingDb(double r, double c, params (double Lo, double Hi)[] bands)
    {
        double weight = 0.0;
        foreach (var (lo, hi) in bands)
            weight += 2.0 * Math.PI * (hi - lo);
        return -NeperToDb * (Math.PI / (r * c * weight));
    }

    // ── 1. The goldens ────────────────────────────────────────────────────────

    /// <summary>
    /// The three band sets the owner's fixture can be measured over, and the widening cost between
    /// two of them.
    /// </summary>
    [Fact]
    public void TheOwnersFixture_CeilingsOverTheThreeBandSets()
    {
        var d = Owner();
        var e = d.Effective;

        // §18.3's mirror rule first, because every number below depends on it.
        Assert.Equal(2.25e9, e.F1, 1.0);
        Assert.Equal(3.0e9, e.F2, 1.0);
        Assert.Equal(7.5e9, e.F5, 1.0);
        Assert.Equal(10.0e9, e.F6, 1.0);

        var (t1, t2, binding) = MatchFanoBound.Of(d);
        var typed = MatchFanoBound.OfTypedBands(d);
        var span = MatchFanoBound.OfOuterSpan(d);

        // Termination 2 is the wall; termination 1 is 38 dB away and irrelevant.
        Assert.Equal(2, binding.End);
        Assert.Equal(FanoWeight.InverseSquare, t2.Weight);
        Assert.Equal(FanoWeight.BandWidth, t1.Weight);

        double gEff = SeriesCeilingDb(1.25, 5e-12, (2.25e9, 3e9), (4.5e9, 5e9), (7.5e9, 10e9));
        double gTyped = SeriesCeilingDb(1.25, 5e-12, (2.5e9, 3e9), (4.5e9, 5e9), (9e9, 10e9));
        double gSpan = SeriesCeilingDb(1.25, 5e-12, (2.25e9, 10e9));
        double gTerm1 = ParallelCeilingDb(100.0, 0.125e-12, (2.25e9, 10e9));

        output.WriteLine($"effective {t2.CeilingDb:F3}  typed {typed.Term2.CeilingDb:F3}  "
                         + $"span {span.Term2.CeilingDb:F3}  term1 {span.Term1.CeilingDb:F3}");

        Assert.Equal(gEff, t2.CeilingDb, 0.1);
        Assert.Equal(-6.4, t2.CeilingDb, 0.1);
        Assert.Equal(gTyped, typed.Term2.CeilingDb, 0.1);
        Assert.Equal(-10.7, typed.Term2.CeilingDb, 0.1);
        Assert.Equal(gSpan, span.Term2.CeilingDb, 0.1);
        Assert.Equal(-3.1, span.Term2.CeilingDb, 0.1);
        Assert.Equal(gTerm1, span.Term1.CeilingDb, 0.1);
        Assert.Equal(-44.8, span.Term1.CeilingDb, 0.1);

        // The mirror widening's own cost, which is the largest number in the picture.
        Assert.Equal(4.3, t2.CeilingDb - typed.Term2.CeilingDb, 0.1);

        // Band 1 carries two thirds of the budget — which is what makes it the lever §2.1 names.
        Assert.Equal(0.6667, t2.BandShare[0], 0.001);
        Assert.Equal(1.0, t2.BandShare.Sum(), 1e-12);
    }

    /// <summary>One band alone, and the two that remain when it goes.</summary>
    [Fact]
    public void TheOwnersFixture_SingleBandAndRemainingBandCeilings()
    {
        var one = new MatchDesign
        {
            F1 = 2.5e9, F2 = 3.0e9,
            Term1 = new Termination(100.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 5e-12),
        };
        Assert.Equal(-16.1, MatchFanoBound.Of(one).Binding.CeilingDb, 0.1);
        Assert.Equal(SeriesCeilingDb(1.25, 5e-12, (2.5e9, 3e9)),
                     MatchFanoBound.Of(one).Binding.CeilingDb, 0.1);

        // Bands 2 and 3 as typed already mirror (5/4.5 = 10/9), so dropping band 1 needs no widening.
        var two = new MatchDesign
        {
            BandCount = 2, F1 = 4.5e9, F2 = 5.0e9, F3 = 9.0e9, F4 = 10.0e9,
            Term1 = new Termination(100.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 5e-12),
        };
        Assert.False(two.Effective.Widened);
        Assert.Equal(-32.1, MatchFanoBound.Of(two).Binding.CeilingDb, 0.1);
        Assert.Equal(SeriesCeilingDb(1.25, 5e-12, (4.5e9, 5e9), (9e9, 10e9)),
                     MatchFanoBound.Of(two).Binding.CeilingDb, 0.1);
    }

    // ── 2. The identities the class rests on ──────────────────────────────────

    /// <summary>
    /// The two inductive terminations are the two capacitive ones with <c>L/R</c> for <c>RC</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is what lets <see cref="MatchFanoBound.For"/> read Q and not the reactance.</b> A
    /// series R+L is bounded by <c>πR/L</c> and a parallel R‖C by <c>π/(RC)</c>, so equal <c>L/R</c>
    /// and <c>RC</c> is the same bound over any band set; likewise a parallel R‖L's <c>πL/R</c>
    /// against a series R+C's <c>πRC</c>.
    /// </remarks>
    [Theory]
    [InlineData(2.4e9, 2.5e9, 5.15e9, 5.85e9)]
    [InlineData(0.5e9, 0.6e9, 1.65e9, 1.98e9)]
    public void TheInductiveDual_GivesTheSameCeilingAsItsCapacitiveTwin(
        double f1, double f2, double f3, double f4)
    {
        const double R = 20.0, Rc = 5e-11;         // RC = L/R = 50 ps

        MatchDesign With(Termination t) => new()
        {
            BandCount = 2, F1 = f1, F2 = f2, F3 = f3, F4 = f4,
            Term1 = t, Term2 = Termination.Resistive(50.0),
        };

        var parC = new Termination(R, ReactanceKind.C, TerminationTopology.Parallel, Rc / R);
        var serL = new Termination(R, ReactanceKind.L, TerminationTopology.Series, Rc * R);
        var serC = new Termination(R, ReactanceKind.C, TerminationTopology.Series, Rc / R);
        var parL = new Termination(R, ReactanceKind.L, TerminationTopology.Parallel, Rc * R);

        double a = MatchFanoBound.Of(With(parC)).Binding.CeilingDb;
        double b = MatchFanoBound.Of(With(serL)).Binding.CeilingDb;
        double c = MatchFanoBound.Of(With(serC)).Binding.CeilingDb;
        double e = MatchFanoBound.Of(With(parL)).Binding.CeilingDb;

        Assert.Equal(FanoWeight.BandWidth, MatchFanoBound.WeightOf(parC));
        Assert.Equal(FanoWeight.BandWidth, MatchFanoBound.WeightOf(serL));
        Assert.Equal(FanoWeight.InverseSquare, MatchFanoBound.WeightOf(serC));
        Assert.Equal(FanoWeight.InverseSquare, MatchFanoBound.WeightOf(parL));

        Assert.Equal(a, b, Math.Abs(a) * 1e-9);
        Assert.Equal(c, e, Math.Abs(c) * 1e-9);
        Assert.NotEqual(a, c, 1e-3);
    }

    /// <summary>
    /// ω₀ is inert: the ceiling depends on the termination only through <c>RC</c> (or <c>L/R</c>).
    /// </summary>
    [Fact]
    public void TheCeiling_DoesNotDependOnWhereOmega0Is()
    {
        var d = Owner();
        var bands = d.Bands;
        double reference = MatchFanoBound.For(d.Term2, 2, d.Omega0, bands).CeilingDb;

        foreach (double scale in new[] { 0.1, 0.25, 1.0, 4.0, 10.0 })
        {
            double got = MatchFanoBound.For(d.Term2, 2, d.Omega0 * scale, bands).CeilingDb;
            Assert.Equal(reference, got, Math.Abs(reference) * 1e-12);
        }
    }

    /// <summary>A resistive end has no ceiling, and never binds.</summary>
    [Fact]
    public void AResistiveEnd_HasNoCeilingAndNeverBinds()
    {
        var d = new MatchDesign
        {
            F1 = 3.3e9, F2 = 5.0e9,
            Term1 = Termination.Resistive(50.0),
            Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
        };
        var (t1, t2, binding) = MatchFanoBound.Of(d);

        Assert.Equal(FanoWeight.None, t1.Weight);
        Assert.False(t1.IsBounded);
        Assert.Equal(double.NegativeInfinity, t1.CeilingDb);
        Assert.Equal(double.PositiveInfinity, t1.AlphaNepers);
        Assert.Same(t2, binding);
        Assert.True(t2.IsBounded);
    }

    /// <summary>A spec that is not yet a spec comes back unbounded rather than throwing.</summary>
    [Fact]
    public void AHalfTypedSpec_ReturnsNoneRatherThanThrowing()
    {
        var t = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 5e-12);
        foreach (var bands in new IReadOnlyList<(double, double)>[]
                 {
                     [], [(0.0, 1e9)], [(3e9, 3e9)], [(5e9, 3e9)], [(1e9, 4e9), (2e9, 6e9)],
                 })
        {
            var c = MatchFanoBound.For(t, 2, 2 * Math.PI * 4e9, bands);
            Assert.Equal(FanoWeight.None, c.Weight);
            Assert.False(c.IsBounded);
        }
    }

    // ── 3. The theorem ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>No synthesised network may beat its own ceiling.</b>
    /// </summary>
    /// <remarks>
    /// The acceptance test of the whole formula, and it is free: every existing golden is a fixture.
    /// The oracle's worst in-band return loss over the effective bands is compared against the binding
    /// ceiling over the same bands, and a failure here means the weight class is wrong for that
    /// termination kind — not that the fixture drifted.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryGoldenFixture))]
    public void NoSynthesisedNetwork_BeatsItsOwnFanoCeiling(string name, MatchDesign design)
    {
        var result = MatchSynthesis.Synthesize(design);
        Assert.Null(result.Refusal);
        Assert.NotNull(result.Network);

        double worst = design.Bands.Max(b => MatchAbcdOracle.WorstS11Db(result.Network!, b.Lo, b.Hi, 401));
        double ceiling = MatchFanoBound.Of(design).Binding.CeilingDb;

        output.WriteLine($"{name}: worst {worst:F3} dB, ceiling {ceiling:F3} dB, "
                         + $"headroom {worst - ceiling:F3} dB");

        Assert.True(worst >= ceiling - 0.05,
            $"{name}: {worst:F3} dB beats the Fano ceiling {ceiling:F3} dB");
    }

    public static TheoryData<string, MatchDesign> EveryGoldenFixture()
    {
        var data = new TheoryData<string, MatchDesign>();

        // §4.9's interstage problem — the acceptance anchor.
        data.Add("golden n=4", MatchAbcdOracle.GoldenDesign());

        // §16.2's Golden B — a lowpass form absorbing a shunt C — and its inductive highpass dual.
        data.Add("lowpass golden B", new MatchDesign
        {
            Form = NetworkForm.Lowpass, Order = 2, F1 = 2.5e9, F2 = 5.0e9,
            Term1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 25e-12),
            Term2 = Termination.Resistive(0.5),
            AnalysisEnd = AnalysisEndChoice.Term1,
        });
        data.Add("highpass shunt L", new MatchDesign
        {
            Form = NetworkForm.Highpass, Order = 2, F1 = 2.5e9, F2 = 5.0e9,
            Term1 = new Termination(5.0, ReactanceKind.L, TerminationTopology.Parallel, 0.4e-9),
            Term2 = Termination.Resistive(0.5),
            AnalysisEnd = AnalysisEndChoice.Term1,
        });

        // §18.4's dual-band problem, at both orders it quotes.
        foreach (int n in new[] { 1, 2 })
            data.Add($"dual n={n}", new MatchDesign
            {
                BandCount = 2, F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
                Order = n, Response = ResponseShape.ChebyshevFano,
                Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
                Term2 = Termination.Resistive(50.0),
                AnalysisEnd = AnalysisEndChoice.Term1,
            });

        // §18.5's tri-band problem, at both orders MB2 measured.
        foreach (int n in new[] { 1, 2 })
            data.Add($"tri n={n}", new MatchDesign
            {
                BandCount = 3,
                F1 = 0.5e9, F2 = 0.6e9, F3 = 0.9e9, F4 = 1.1e9, F5 = 1.65e9, F6 = 1.98e9,
                Order = n, Response = ResponseShape.ChebyshevFano,
                Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 4e-12),
                Term2 = Termination.Resistive(50.0),
                AnalysisEnd = AnalysisEndChoice.Term1,
            });

        // The owner's own tri-band fixture — the one the whole brief is about.
        data.Add("owner tri n=2", Owner());

        return data;
    }

    // ── 4. The gap rise ───────────────────────────────────────────────────────

    /// <summary>
    /// At orders 1 and 2 the owner's tri-band prototype does not exclude the gaps at all.
    /// </summary>
    /// <remarks>
    /// The middle band maps to <c>u ∈ [0, 0.0042]</c> — a fortieth of the hull — and no degree-1 or
    /// degree-2 polynomial levelled to 1 on <c>[0, 0.0042] ∪ [0.337, 1]</c> exceeds 1 between them.
    /// It IS the single-band hull Chebyshev, which is exactly what the owner saw on screen: a flat
    /// wideband match with no trace of three bands.
    /// </remarks>
    [Fact]
    public void TheOwnersFixture_HasNoGapUntilOrderFour()
    {
        var e = Owner().Effective;
        var iv = e.Intervals;
        Assert.Equal(2, iv.Count);
        Assert.Equal(0.0, iv[0].Lo, 1e-12);
        Assert.Equal(0.00416, iv[0].Hi, 1e-5);
        Assert.Equal(0.33715, iv[1].Lo, 1e-5);
        Assert.Equal(1.0, iv[1].Hi, 1e-12);

        var rise = new double[7];
        for (int n = 1; n <= MatchOrders.MaxOrder; n++)
        {
            var r = MatchFanoBound.GapRise(e, n);
            Assert.Equal(2, r.Count);                 // two frequency gaps, one u-interval
            Assert.Equal(r[0], r[1], 1e-12);
            rise[n] = r[0];
            output.WriteLine($"order {n}: rise x{r[0]:F4}");
        }

        Assert.Equal(0.99, rise[1], 0.01);
        Assert.Equal(0.97, rise[2], 0.01);
        Assert.Equal(1.16, rise[3], 0.01);
        Assert.Equal(2.9, rise[4], 0.05);
        Assert.Equal(8.8, rise[5], 0.1);
        Assert.True(rise[6] > 17.0, $"order 6 rise {rise[6]:F2}");

        Assert.Equal(4, MatchFanoBound.GapOpensAtOrder(e));
    }

    /// <summary>
    /// The dual-band prototype's gap rise is the shifted Chebyshev's, in closed form.
    /// </summary>
    /// <remarks>
    /// <b>One code path serves both band counts.</b> A dual-band passband is the single interval
    /// <c>[a², 1]</c>, on which the exchange returns <c>T_n</c> shifted; its largest value below the
    /// band is at <c>u = 0</c>, which is <c>cosh(n·arccosh((1+a²)/(1−a²)))</c>.
    /// </remarks>
    [Fact]
    public void TheDualBandGapRise_IsTheChebyshevClosedForm()
    {
        // match.md §18.4's own fixture: a = 0.7261974.
        var d = new MatchDesign
        {
            BandCount = 2, F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
        };
        var e = d.Effective;
        Assert.Equal(0.72620, d.A, 1e-5);

        for (int n = 1; n <= MatchOrders.MaxOrder; n++)
        {
            var rise = MatchFanoBound.GapRise(e, n);
            Assert.Single(rise);
            double closed = MatchFanoBound.DualGapRise(d.A, n);
            output.WriteLine($"order {n}: rise x{rise[0]:F6} vs closed form {closed:F6}");
            Assert.Equal(closed, rise[0], closed * 1e-9);
        }

        // A dual-band prototype excludes its gap from the very first order — the owner's tri-band
        // fixture does not, and that difference is the whole of §18.10's second half.
        Assert.Equal(1, MatchFanoBound.GapOpensAtOrder(e));
    }

    /// <summary>A single band has no gap to rise into.</summary>
    [Fact]
    public void ASingleBand_HasNoGapRise()
    {
        var e = MatchAbcdOracle.GoldenDesign().Effective;
        Assert.Empty(MatchFanoBound.GapRise(e, 4));
        Assert.Equal(0, MatchFanoBound.GapOpensAtOrder(e));
    }

    // ── 5. The remedies ───────────────────────────────────────────────────────

    /// <summary>All four remedies on the owner's fixture, at −15 dB.</summary>
    [Fact]
    public void TheOwnersFixture_OffersFourRemediesAtMinusFifteen()
    {
        var d = Owner();
        var r = MatchFanoBound.Remedies(d, MatchFanoBound.HintTargetDb);
        foreach (var x in r) output.WriteLine($"{x.Kind}: {x.Sentence}");

        Assert.Equal(4, r.Count);
        Assert.Equal(["reactance", "edge", "drop", "mirror"], r.Select(x => x.Kind));

        // 1. The termination's own capacitance — a series C, so LARGER is looser.
        Assert.Equal(2, r[0].End);
        Assert.Equal(11.7e-12, r[0].Value, 0.05e-12);
        Assert.Equal("termination 2's capacitance at or above 11.7 pF", r[0].Sentence);

        // 2. Band 1's lower edge, the other two bands held.
        Assert.Equal(1, r[1].Band);
        Assert.Equal(2.8636e9, r[1].Value, 1e6);
        Assert.Equal("band 1 starting at 2.86 GHz instead of 2.25", r[1].Sentence);

        // 3. What band 1 is costing.
        Assert.Equal(1, r[2].Band);
        Assert.Equal(-32.1, r[2].Value, 0.1);
        Assert.Equal("without band 1 the ceiling over bands 2 and 3 is -32.1 dB", r[2].Sentence);

        // 4. The un-widened mirror — band 1 given back rather than band 3 pulled in.
        Assert.Equal(1, r[3].Band);
        Assert.Equal(-13.8, r[3].Value, 0.1);
        Assert.Equal(
            "band 1 as 2.25–2.5 GHz mirrors band 3 without widening (ceiling -13.8 dB)",
            r[3].Sentence);
    }

    /// <summary>
    /// The reactance and edge remedies land ON the target when they are put back through the formula.
    /// </summary>
    [Fact]
    public void TheSolvedRemedies_ReachTheTargetTheyWereSolvedFor()
    {
        var d = Owner();
        var r = MatchFanoBound.Remedies(d, MatchFanoBound.HintTargetDb);

        var withC = d.Clone();
        withC.Term2 = d.Term2 with { Value = r[0].Value };
        Assert.Equal(MatchFanoBound.HintTargetDb, MatchFanoBound.Of(withC).Binding.CeilingDb, 0.01);

        // The edge remedy moves the EFFECTIVE lower edge, so it is checked against the effective set
        // directly rather than through a re-symmetrised design.
        var bands = d.Bands.ToArray();
        bands[0] = (r[1].Value, bands[0].Hi);
        Assert.Equal(
            MatchFanoBound.HintTargetDb,
            MatchFanoBound.For(d.Term2, 2, d.Omega0, bands).CeilingDb, 0.01);
    }

    /// <summary>The mirror remedy's own spec is one <c>Symmetrise3</c> leaves alone.</summary>
    [Fact]
    public void TheMirrorRemedy_ProposesASpecThatNeedsNoWidening()
    {
        var d = Owner();
        var mirror = MatchFanoBound.Remedies(d, MatchFanoBound.HintTargetDb).Single(x => x.Kind == "mirror");

        var proposed = MatchBands.Symmetrise3(2.25e9, 2.5e9, 4.5e9, 5.0e9, 9.0e9, 10.0e9);
        Assert.False(proposed.Widened);
        Assert.False(proposed.Overlaps);

        var probe = new MatchDesign
        {
            BandCount = 3, F1 = 2.25e9, F2 = 2.5e9, F3 = 4.5e9, F4 = 5.0e9, F5 = 9.0e9, F6 = 10.0e9,
            Term1 = d.Term1, Term2 = d.Term2,
        };
        Assert.Equal(mirror.Value, MatchFanoBound.Of(probe).Binding.CeilingDb, 1e-9);

        // The other candidate — pulling band 3 in to 7.5-9 rather than giving band 1 back — is the
        // worse of the two, which is why it is not the one offered.
        var other = new MatchDesign
        {
            BandCount = 3, F1 = 2.5e9, F2 = 3.0e9, F3 = 4.5e9, F4 = 5.0e9, F5 = 7.5e9, F6 = 9.0e9,
            Term1 = d.Term1, Term2 = d.Term2,
        };
        Assert.False(other.Effective.Widened);
        double otherDb = MatchFanoBound.Of(other).Binding.CeilingDb;
        output.WriteLine($"band 3 pulled in: {otherDb:F2} dB against band 1 given back {mirror.Value:F2} dB");
        Assert.Equal(-9.6, otherDb, 0.1);
        Assert.True(mirror.Value < otherDb,
            "the DEEPER ceiling is the better one, the same way -13.8 dB is a better match than -9.6");
    }

    /// <summary>
    /// A remedy pointing the unphysical way is absent, not clamped.
    /// </summary>
    /// <remarks>
    /// A parallel-C end already better than the target would have to grow to reach it, and a hint
    /// telling a user to make a match WORSE is not a hint. The same design at a target it does not
    /// meet offers the entry, so the absence is the direction rule and not a missing formula.
    /// </remarks>
    [Fact]
    public void AnUnphysicalDirection_OmitsTheReactanceRemedy()
    {
        // 20 Ω ‖ 2.5 pF over §18.4's two bands: the ceiling is −87 dB, far better than −15.
        var d = new MatchDesign
        {
            BandCount = 2, F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
        };
        Assert.True(MatchFanoBound.Of(d).Binding.CeilingDb < -80.0);

        var loose = MatchFanoBound.Remedies(d, -15.0);
        Assert.DoesNotContain(loose, x => x.Kind == "reactance");

        // The same end, asked for something it cannot give, does offer the entry.
        var hard = MatchFanoBound.Remedies(d, -95.0);
        var re = Assert.Single(hard, x => x.Kind == "reactance");
        Assert.True(re.Value < 2.5e-12, $"a shunt C must SHRINK to loosen; got {re.Value:E3}");
        Assert.Contains("at or below", re.Sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Δω class's edge remedy narrows the widest band about its own centre.
    /// </summary>
    [Fact]
    public void TheBandWidthClass_NarrowsTheWidestBandSymmetrically()
    {
        var d = new MatchDesign
        {
            BandCount = 2, F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
        };
        var edge = MatchFanoBound.Remedies(d, -95.0).Single(x => x.Kind == "edge");
        output.WriteLine(edge.Sentence);

        // Band 2 (5.15-5.85 effective) is the widest, and the narrowing keeps its centre.
        Assert.Equal(2, edge.Band);
        var (lo, hi) = d.Bands[1];
        double centre = 0.5 * (lo + hi);
        Assert.Contains("narrowed to", edge.Sentence, StringComparison.Ordinal);
        Assert.True(edge.Value > lo);
        Assert.Equal(-95.0, MatchFanoBound.For(
            d.Term1, 1, d.Omega0,
            [d.Bands[0], (edge.Value, 2.0 * centre - edge.Value)]).CeilingDb, 0.01);
    }

    /// <summary>A single-band design has no band to drop and no mirror to un-widen.</summary>
    [Fact]
    public void ASingleBandDesign_OffersOnlyTheTwoRemediesThatApply()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var r = MatchFanoBound.Remedies(d, -30.0);
        foreach (var x in r) output.WriteLine($"{x.Kind}: {x.Sentence}");

        Assert.DoesNotContain(r, x => x.Kind == "drop");
        Assert.DoesNotContain(r, x => x.Kind == "mirror");
        Assert.Contains(r, x => x.Kind == "reactance");
    }

    /// <summary>Nothing to bound, nothing to say.</summary>
    [Fact]
    public void TwoResistiveEnds_OfferNoRemedies()
    {
        var d = new MatchDesign
        {
            F1 = 3.3e9, F2 = 5.0e9,
            Term1 = Termination.Resistive(50.0),
            Term2 = Termination.Resistive(12.5),
        };
        Assert.Empty(MatchFanoBound.Remedies(d, -15.0));
    }
}
