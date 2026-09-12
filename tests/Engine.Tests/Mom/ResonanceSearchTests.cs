using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>ANT-9 — the resonance search.</b> §5's gates, in two halves.
///
/// <para><b>The first half solves nothing.</b> A resonance search must be gated on a response whose
/// f₀ and Q are known in CLOSED FORM, or the test is comparing one estimate against another and can
/// only detect that they disagree, never which is wrong. So the probe delegate here evaluates an
/// analytic RLC one-port: the whole search runs in microseconds and every assertion is against an
/// exact number. This is the same reason <c>PlanarAdaptiveSweep</c> is a pure file — the decisions
/// are testable without a 72-second solve.</para>
///
/// <para>The second half is on the real driver, and its subject is not physics but the INVARIANT:
/// with the search off nothing changed, and with it on nothing of the user's own grid moved.</para>
/// </summary>
public sealed class ResonanceSearchTests
{
    private readonly ITestOutputHelper _out;
    public ResonanceSearchTests(ITestOutputHelper output) => _out = output;

    private const double Z0 = 50.0;

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Analytic one-ports. Every f₀ and Q below is exact by construction.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Series RLC: X rises through zero at f₀, Q = 2πf₀L/R.</summary>
    private static Func<double, Complex> SeriesRlc(double f0, double q, double r)
    {
        double l = q * r / (2 * Math.PI * f0);
        double c = 1 / (Math.Pow(2 * Math.PI * f0, 2) * l);
        return f => new Complex(r, 2 * Math.PI * f * l - 1 / (2 * Math.PI * f * c));
    }

    /// <summary>Parallel RLC: X FALLS through zero at f₀, and Z peaks real at R.</summary>
    private static Func<double, Complex> ParallelRlc(double f0, double q, double r)
    {
        double c = q / (2 * Math.PI * f0 * r);
        double l = 1 / (Math.Pow(2 * Math.PI * f0, 2) * c);
        return f => Complex.One /
                    new Complex(1 / r, 2 * Math.PI * f * c - 1 / (2 * Math.PI * f * l));
    }

    private static Mat<Complex> S11Of(Complex z)
    {
        var m = new Mat<Complex>(1, 1);
        m[0, 0] = (z - Z0) / (z + Z0);
        return m;
    }

    /// <summary>
    /// The probe: a node set that grows. It is the ONLY thing standing where a full
    /// fill-factor-excite-de-embed cycle stands in <c>PlanarSolve.Run</c>, and the search cannot
    /// tell the difference — which is the property that makes this file run in milliseconds.
    /// </summary>
    private sealed class AnalyticProbe
    {
        private readonly Func<double, Complex> _z;
        private readonly SortedDictionary<double, Mat<Complex>> _solved = new();
        public int Calls { get; private set; }

        public AnalyticProbe(Func<double, Complex> z, IEnumerable<double> seed)
        {
            _z = z;
            foreach (double f in seed) _solved[f] = S11Of(_z(f));
        }

        public PlanarResonanceNodes Nodes =>
            new([.. _solved.Keys], [.. _solved.Values]);

        public PlanarResonanceNodes Probe(double f)
        {
            if (!_solved.ContainsKey(f)) { _solved[f] = S11Of(_z(f)); Calls++; }
            return Nodes;
        }
    }

    private static double[] Grid(double f0, double f1, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 + (f1 - f0) * i / (n - 1);
        return f;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // §5 — a synthetic high-Q response is found, to a STATED accuracy in f0 and Q.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A1_AHighQResonanceNarrowerThanTheGridIsFound_WithF0AndQToAStatedAccuracy()
    {
        // ANT-9 §1's own numbers: 1.50-2.00 GHz asked for at 10 MHz spacing, and a resonance
        // narrower than that spacing. Q = 214 gives a half-power bandwidth of 8.6 MHz — the feature
        // is genuinely invisible to the requested grid, which is the whole premise of the phase.
        const double f0 = 1.8412e9, q = 214, r = 50;
        var probe = new AnalyticProbe(SeriesRlc(f0, q, r), Grid(1.5e9, 2.0e9, 9));

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 1.5e9, 2.0e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default, probe.Probe);

        var found = Assert.Single(outcome.Resonances);

        double fErr = Math.Abs(found.FrequencyHz - f0) / f0;
        double qErr = Math.Abs(found.Q - q) / q;
        _out.WriteLine($"true f0 = {f0 / 1e9:F6} GHz, Q = {q}");
        _out.WriteLine($"found    = {found.FrequencyHz / 1e9:F6} GHz, Q = {found.Q:F2}, " +
                       $"R = {found.ResistanceOhm:F2} ohm, {found.Kind}");
        _out.WriteLine($"relative error: f0 {fErr:E2}, Q {qErr:E2}");
        _out.WriteLine($"bracketed to {found.LocatedToHz / 1e3:F1} kHz in " +
                       $"{outcome.AddedFrequencies.Count} added solve(s)");
        _out.WriteLine($"half-power BW = {found.HalfPowerBandwidthHz / 1e6:F3} MHz, " +
                       $"-10 dB BW = {found.MatchedBandwidthHz / 1e6:F3} MHz, " +
                       $"|S| at f0 = {found.ReturnLossDb:F1} dB");

        // THE STATED ACCURACY. f0 lands inside its own reported bracket, which is the claim the
        // search actually makes; 1e-5 relative is what that bracket is worth at the default
        // tolerance. Q comes off a secant across that bracket and is a derivative, so it is held
        // an order looser — deliberately, rather than by tuning the number until it passed.
        Assert.True(fErr < 1e-5, $"f0 relative error {fErr:E2}");
        Assert.True(qErr < 1e-3, $"Q relative error {qErr:E2}");
        Assert.True(Math.Abs(found.FrequencyHz - f0) <= found.LocatedToHz,
                    "f0 must lie inside the bracket the search reports it to");
        Assert.Equal(PlanarResonanceKind.Series, found.Kind);

        // The half-power bandwidth is f0/Q by definition, so it inherits the Q error and nothing
        // more — a tighter check here would be checking the same number twice.
        Assert.Equal(f0 / q, found.HalfPowerBandwidthHz, f0 / q * 1e-4);

        // The -10 dB bandwidth is MEASURED off the curve. Its closed form holds only for the
        // LINEARISED reactance (|S| = -10 dB at X = (2/3)Z0, with X = 2QR(f-f0)/f0), and the true
        // series reactance carries a 1/f term the linearisation drops — so the two agree to a few
        // parts in a thousand and not better. That gap is the LINEARISATION's, not the search's, and
        // the tolerance says so rather than being tightened until something passed.
        double expectedMatched = 2 * (2.0 / 3.0) * Z0 / (2 * q * r) * f0;
        double bwErr = Math.Abs(found.MatchedBandwidthHz - expectedMatched) / expectedMatched;
        _out.WriteLine($"-10 dB BW vs linearised closed form: {bwErr:E2} relative");
        Assert.True(bwErr < 1e-2,
                    $"-10 dB bandwidth {found.MatchedBandwidthHz / 1e6:F4} MHz vs closed form " +
                    $"{expectedMatched / 1e6:F4} MHz");

        Assert.False(outcome.CapBound);
    }

    [Fact]
    public void A2_AParallelResonanceIsLabELLEDParallel_AndTheSameOneFormulaGivesItsQ()
    {
        // The header's claim that Q needs no branch: the parallel form reduces to the series form
        // with the sign the falling slope supplies. If that reduction were wrong this Q would be
        // out by the factor the two textbook expressions differ by, which is exactly the kind of
        // error a single worked case catches and no amount of reading does.
        const double f0 = 2.4e9, q = 80, r = 220;
        var probe = new AnalyticProbe(ParallelRlc(f0, q, r), Grid(2.0e9, 3.0e9, 9));

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 2.0e9, 3.0e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default, probe.Probe);

        var found = Assert.Single(outcome.Resonances);
        _out.WriteLine($"found {found.FrequencyHz / 1e9:F6} GHz, Q = {found.Q:F3} (true {q}), " +
                       $"R = {found.ResistanceOhm:F1} ohm (true {r}), {found.Kind}");
        _out.WriteLine($"|S| at f0 = {found.ReturnLossDb:F2} dB; matched BW refusal: " +
                       $"{found.MatchedBandwidthRefusal ?? "(none)"}");

        Assert.Equal(PlanarResonanceKind.Parallel, found.Kind);
        Assert.True(Math.Abs(found.Q - q) / q < 1e-3, $"Q = {found.Q}");
        Assert.True(Math.Abs(found.ResistanceOhm - r) / r < 1e-3, $"R = {found.ResistanceOhm}");

        // 220 ohm into 50 never reaches -10 dB. That is an ordinary edge-fed patch, and the answer
        // is a REFUSAL naming the reason, not a zero and not an omitted field.
        Assert.True(double.IsNaN(found.MatchedBandwidthHz));
        Assert.NotNull(found.MatchedBandwidthRefusal);
        Assert.Contains("MATCH", found.MatchedBandwidthRefusal!, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // §5 — a two-resonance span returns BOTH.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A3_ATwoResonanceSpanReturnsBOTH_NotTheFirstAndNotTheStrongest()
    {
        // Two parallel tanks in series — the ordinary equivalent circuit of a patch with a
        // higher-order mode, and the case ANT-9 §3 calls normal rather than an edge case.
        var t1 = ParallelRlc(1.85e9, 120, 180);
        var t2 = ParallelRlc(2.45e9, 90, 150);
        var probe = new AnalyticProbe(f => t1(f) + t2(f), Grid(1.6e9, 2.7e9, 13));

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 1.6e9, 2.7e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default with { MaxAddedPoints = 40 }, probe.Probe);

        foreach (var r in outcome.Resonances)
            _out.WriteLine($"  {r.FrequencyHz / 1e9:F5} GHz  Q = {r.Q:F1}  R = {r.ResistanceOhm:F1} " +
                           $"ohm  {r.Kind}  (bracketed to {r.LocatedToHz / 1e3:F1} kHz)");
        _out.WriteLine($"added {outcome.AddedFrequencies.Count}, discarded " +
                       $"{outcome.DiscardedCrossings}, cap bound {outcome.CapBound}");

        Assert.True(outcome.Resonances.Count >= 2,
                    $"both tanks must be reported; got {outcome.Resonances.Count}");

        // Each tank is pulled off its own f0 by the OTHER tank's reactance, so the gate is that both
        // are located near where they belong and are ordered — not that they sit on the nominal
        // numbers, which for coupled resonators they do not.
        Assert.Contains(outcome.Resonances, r => Math.Abs(r.FrequencyHz - 1.85e9) / 1.85e9 < 0.05);
        Assert.Contains(outcome.Resonances, r => Math.Abs(r.FrequencyHz - 2.45e9) / 2.45e9 < 0.05);
        Assert.Equal(outcome.Resonances.OrderBy(r => r.FrequencyHz).ToList(), outcome.Resonances);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // §5 — a structure with NO resonance terminates and SAYS SO.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A4_AStructureWithNoResonanceTerminatesWithinTheCap_AndSaysSo()
    {
        // A 6 dB attenuator: Z is real and constant, so Im(Z_in) is identically zero. The strict
        // sign test is what makes this produce NO candidate rather than one per sample — an
        // identically-zero reactance is a matched port, and a search that reported a resonance at
        // every frequency of a matched port would be worse than useless.
        var probe = new AnalyticProbe(_ => new Complex(100.0, 0.0), Grid(1e9, 5e9, 11));

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 1e9, 5e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default, probe.Probe);

        _out.WriteLine(outcome.Note);
        _out.WriteLine($"added {outcome.AddedFrequencies.Count} (cap " +
                       $"{PlanarResonanceSettings.Default.MaxAddedPoints}), " +
                       $"discarded {outcome.DiscardedCrossings}, cap bound {outcome.CapBound}");

        Assert.Empty(outcome.Resonances);
        Assert.True(outcome.AddedFrequencies.Count <= PlanarResonanceSettings.Default.MaxAddedPoints,
                    "the cap is a ceiling on added solves and must hold");
        Assert.Contains("NO RESONANCE", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A5_TheCapBindsAndIsREPORTED_RatherThanTheSearchQuietlyStoppingShort()
    {
        // A cap of two cannot bracket a Q = 500 resonance to 1e-4. The answer is not "nothing found"
        // and not a silently coarse number: it is the cap, named, with what would lift it.
        var probe = new AnalyticProbe(SeriesRlc(1.8412e9, 500, 50), Grid(1.5e9, 2.0e9, 5));

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 1.5e9, 2.0e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default with { MaxAddedPoints = 2 }, probe.Probe);

        _out.WriteLine(outcome.Note);
        Assert.True(outcome.CapBound);
        Assert.Equal(2, outcome.AddedFrequencies.Count);
        Assert.Contains("cap", outcome.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A6_TheSearchNeverPublishesAFrequencyOutsideTheSpanItWasGiven()
    {
        // The narrowed property is "may add points BETWEEN the ones you asked for", not "may sweep
        // wherever it likes". Extrapolating past the ends would be a different and much larger
        // claim — the interpolant has no information out there at all.
        var probe = new AnalyticProbe(SeriesRlc(1.8412e9, 214, 50), Grid(1.5e9, 2.0e9, 9));

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 1.5e9, 2.0e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default, probe.Probe);

        Assert.All(outcome.AddedFrequencies, f => Assert.InRange(f, 1.5e9, 2.0e9));
        Assert.Equal(outcome.AddedFrequencies.OrderBy(f => f).ToList(), outcome.AddedFrequencies);
        _out.WriteLine($"added, ascending: " +
                       string.Join(", ", outcome.AddedFrequencies.Select(f => $"{f / 1e9:F6}")));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // The driver. Structural, on the real sweep.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private static (PlanarProblem P, PlanarMesh M, IReadOnlyList<PlanarPortResolution> Ports) Fixture()
    {
        var line = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line, PlanarLineFixtures.Coarse);
        return (line, mesh, ports);
    }

    /// <summary>
    /// <b>A fixture the search actually finds something in, and it is a line.</b>
    ///
    /// <para>The 50 ohm fixture above, referenced to 50 ohm, is the WRONG subject for a flagging
    /// test: it is matched, so Im(Z_in) sits at zero without ever changing sign, the search
    /// correctly finds nothing, adds nothing, and a test of "added points are flagged" passes
    /// vacuously with no added point in it.</para>
    ///
    /// <para>A MISMATCHED line is a resonator — Z_in rotates around the Smith chart and Im(Z_in)
    /// crosses zero every quarter wavelength, with Re(Z_in) swinging between Zc²/Z0 and Z0. Half a
    /// metre of nothing: the same geometry, referenced to 15 ohm instead, and long enough that the
    /// band covers a crossing.</para>
    /// </summary>
    private static (PlanarProblem P, PlanarMesh M, IReadOnlyList<PlanarPortResolution> Ports)
        ResonantFixture()
    {
        var line = PlanarLineFixtures.Fr4Line(22e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line, PlanarLineFixtures.Coarse, z0: 15.0);
        return (line, mesh, ports);
    }

    [Fact]
    public void B1_WithTheSearchOFF_NothingIsAddedAndNothingIsFlagged()
    {
        // §5's first gate, and the one that makes the mode safe to have: an existing .cem re-run
        // must produce the same frequency list and the same s-parameters. The search defaults to
        // null, so this is the path every file on disk takes.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 9);
        var st = new PlanarSolveSettings(
            Deembed: false, Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-2));

        var a = PlanarSolve.Run(p, m, ports, freqs, st);
        var b = PlanarSolve.Run(p, m, ports, freqs, st);

        Assert.Equal(freqs.Length, a.Points.Count);
        Assert.Empty(a.AddedFrequencies);
        Assert.Empty(a.Resonances);
        Assert.All(a.Points, pt => Assert.False(pt.AddedBySearch));
        for (int i = 0; i < freqs.Length; i++)
        {
            Assert.Equal(freqs[i], a.Points[i].FrequencyHz);
            for (int r = 0; r < a.Points[i].S.RowCount; r++)
            for (int c = 0; c < a.Points[i].S.ColCount; c++)
            {
                Assert.True(a.Points[i].S[r, c].Real      == b.Points[i].S[r, c].Real);
                Assert.True(a.Points[i].S[r, c].Imaginary == b.Points[i].S[r, c].Imaginary);
                Assert.True(a.Points[i].RawS[r, c].Real   == b.Points[i].RawS[r, c].Real);
            }
        }
        _out.WriteLine($"{a.Points.Count} published, {a.SolvedPointCount} solved, " +
                       $"converged = {a.AdaptiveConverged}");
    }

    [Fact]
    public void B2_WithTheSearchON_TheUsersOwnGridIsUntouchedAndEveryAddedPointIsFLAGGED()
    {
        // The narrowed property in the exact form a user experiences it. Turning the search on may
        // add points; it may NOT move a point the user asked for that was actually solved. A found
        // point that could not be told apart from a requested one would be the never-add property
        // broken quietly, which is worse than not having the mode.
        var (p, m, ports) = ResonantFixture();

        // THIRTEEN points, not nine, and the difference is the whole of ANT-9 §1. On a 500 MHz grid
        // Im(Z_in) of this line is negative at every single point — the crossing near 3.6 GHz lives
        // entirely between two of them — so the search correctly finds nothing and this test would
        // be vacuous again for a second, more interesting reason. A 400 MHz grid straddles it.
        double[] freqs = Grid(2e9, 6e9, 11);
        var off = new PlanarAdaptiveSettings(Tolerance: 1e-2);
        var on  = off with { Search = PlanarResonanceSettings.Default with { MaxAddedPoints = 4 } };

        var a = PlanarSolve.Run(p, m, ports, freqs, new PlanarSolveSettings(Deembed: false, Adaptive: off));
        var b = PlanarSolve.Run(p, m, ports, freqs, new PlanarSolveSettings(Deembed: false, Adaptive: on));

        // Every requested frequency is still published, in order, and still labelled as the user's.
        var requested = b.Points.Where(pt => !pt.AddedBySearch).Select(pt => pt.FrequencyHz).ToArray();
        Assert.Equal(freqs, requested);
        Assert.Equal(b.Points.OrderBy(pt => pt.FrequencyHz).Select(pt => pt.FrequencyHz),
                     b.Points.Select(pt => pt.FrequencyHz));

        // Added points are enumerated AND flagged, and the two agree.
        var flagged = b.Points.Where(pt => pt.AddedBySearch).Select(pt => pt.FrequencyHz).ToArray();
        Assert.Equal(b.AddedFrequencies, flagged);
        Assert.DoesNotContain(flagged, f => freqs.Contains(f));
        Assert.True(flagged.Length <= 4, "the cap must hold on the real driver too");

        // NOT VACUOUS. Without this the whole test passes on a sweep that added nothing, which is
        // exactly what it did on the matched fixture and exactly what it must not be allowed to do.
        Assert.NotEmpty(flagged);

        // A point SOLVED in the search-off run is published with the identical matrix in the
        // search-on run. Modelled points legitimately move — they are modelled from a richer node
        // set — and asserting otherwise would be asserting that the added points changed nothing,
        // which would mean they were not worth adding.
        var solvedOff = a.SolvedFrequencies.ToHashSet();
        int checkedCount = 0;
        foreach (var pt in b.Points.Where(pt => solvedOff.Contains(pt.FrequencyHz)))
        {
            var was = a.Points.Single(q => q.FrequencyHz == pt.FrequencyHz);
            for (int r = 0; r < was.S.RowCount; r++)
            for (int c = 0; c < was.S.ColCount; c++)
            {
                Assert.True(was.S[r, c].Real      == pt.S[r, c].Real,
                            $"solved point {pt.FrequencyHz:E6} moved when the search was turned on");
                Assert.True(was.S[r, c].Imaginary == pt.S[r, c].Imaginary);
            }
            checkedCount++;
        }
        _out.WriteLine($"off: {a.Points.Count} published / {a.SolvedPointCount} solved; " +
                       $"on: {b.Points.Count} published / {b.SolvedPointCount} solved, " +
                       $"{flagged.Length} added, {checkedCount} solved points compared unchanged");
        foreach (var r in b.Resonances)
            _out.WriteLine($"  resonance {r.FrequencyHz / 1e9:F5} GHz Q = {r.Q:F2} {r.Kind}");
        _out.WriteLine(b.Notes.First(n => n.StartsWith("Adaptive frequency sampling", StringComparison.Ordinal)));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // §4 — the report leads with convergence.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1e-1)]   // loose: converges after a handful of points
    [InlineData(0.0)]    // impossible: cannot converge, and must say so FIRST
    public void C1_TheAdaptiveReportLeadsWithConvergedOrNotConverged(double tolerance)
    {
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 9);

        var run = PlanarSolve.Run(p, m, ports, freqs,
            new PlanarSolveSettings(Deembed: false,
                                    Adaptive: new PlanarAdaptiveSettings(Tolerance: tolerance)));

        string note = run.Notes.First(n => n.StartsWith("Adaptive frequency sampling", StringComparison.Ordinal));
        _out.WriteLine(note);

        // §4's requirement literally: the verdict is the LEAD, not the last clause. Asserted as a
        // prefix rather than by splitting on the first full stop — the very next thing the note says
        // is a |ΔS| with a decimal point in it, and a sentence-splitter would cut the number in half.
        Assert.StartsWith(
            tolerance > 0
                ? "Adaptive frequency sampling CONVERGED:"
                : "Adaptive frequency sampling DID NOT CONVERGE:",
            note, StringComparison.Ordinal);
        Assert.Equal(tolerance > 0, run.AdaptiveConverged);

        // And the point COUNT — the thing that used to lead — comes after it.
        Assert.True(note.IndexOf("CONVERGE", StringComparison.Ordinal)
                    < note.IndexOf("requested point(s) were solved", StringComparison.Ordinal),
                    "the verdict must precede the point count, which is what ANT-9 §4 reverses");

        if (run.AdaptiveConverged == false)
        {
            // §4: say what would help, in the same sentence, rather than leaving the remedy to be
            // worked out. With the search off, that includes naming the search.
            Assert.Contains("What would help", note, StringComparison.Ordinal);
            Assert.Contains("resonance search", note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void C2_APercentageIsNotPresentedAsASavingWhenItIsNotOne()
    {
        // §4's third rule and §6's fourth prohibition. At tolerance 0 every point is solved, so the
        // adaptive path saved exactly nothing, and the note has to say that rather than reporting
        // "9 of 9 solved" in the tone of an achievement.
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 9);

        var run = PlanarSolve.Run(p, m, ports, freqs,
            new PlanarSolveSettings(Deembed: false, Adaptive: new PlanarAdaptiveSettings(Tolerance: 0.0)));

        string note = run.Notes.First(n => n.StartsWith("Adaptive frequency sampling", StringComparison.Ordinal));
        _out.WriteLine(note);
        Assert.Equal(freqs.Length, run.SolvedPointCount);
        Assert.Contains("saved nothing here", note, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // STOP — finish now and keep what is found (owner request, 2026-09-11)
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A stop takes no further probe and reports what it had.</b> This search is the long tail of
    /// an EM run — every probe is a full-wave frequency point, and nothing outside it can see how
    /// many more it intends to take — which is what the Stop was asked for.
    ///
    /// <para>Three separate claims, because a stop that only did the first would be a stop that
    /// throws work away: it STOPS (probes cease within one of the request), it KEEPS (whatever was
    /// located is still in the outcome), and it SAYS SO in its own note and in a flag distinct from
    /// the point cap — "you set the cap too low" and "you pressed Stop" are different things to tell
    /// someone.</para>
    /// </summary>
    [Fact]
    public void AStop_TakesNoFurtherProbe_KeepsWhatWasFound_AndSaysSo()
    {
        const double f0 = 1.8412e9, q = 214, r = 50;
        var probe = new AnalyticProbe(SeriesRlc(f0, q, r), Grid(1.5e9, 2.0e9, 9));

        // Stop after the fourth probe: enough to have bracketed the resonance, nowhere near enough
        // to have converged on it at the default tolerance.
        int probes = 0;
        const int stopAfter = 4;
        PlanarResonanceNodes Counting(double f)
        {
            probes++;
            return probe.Probe(f);
        }

        var outcome = PlanarResonanceSearch.Search(
            probe.Nodes, 1.5e9, 2.0e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
            PlanarResonanceSettings.Default, Counting, stopped: () => probes >= stopAfter);

        _out.WriteLine($"{probes} probe(s) taken, stop asked for at {stopAfter}");
        _out.WriteLine(outcome.Note);

        // It STOPPED. The check is at a probe boundary, so one more may be in flight when the stop
        // is read — never more than that, which is the granularity cancellation already has.
        Assert.InRange(probes, stopAfter, stopAfter + 1);

        // It KEPT what it had: the bracket at the moment of the stop is made of SOLVED ends, so the
        // resonance reported from it is real — just not pinned as tightly as the tolerance asked.
        var found = Assert.Single(outcome.Resonances);
        Assert.InRange(found.FrequencyHz, 1.5e9, 2.0e9);
        Assert.True(found.LocatedToHz > 0);
        _out.WriteLine($"kept f0 = {found.FrequencyHz / 1e9:F6} GHz, located to " +
                       $"{found.LocatedToHz / 1e6:F3} MHz (true {f0 / 1e9:F6})");

        // And it SAID SO, in its own words and in a flag that is not the cap's.
        Assert.True(outcome.StoppedEarly);
        Assert.Contains("STOPPED at the user's request", outcome.Note, StringComparison.Ordinal);
    }

    /// <summary>A search never asked to stop is BYTE-IDENTICAL to one with no stop predicate at
    /// all — the property that makes the parameter safe to have added to every caller.</summary>
    [Fact]
    public void AStopThatNeverFires_ChangesNothing()
    {
        const double f0 = 1.8412e9, q = 214, r = 50;

        PlanarResonanceOutcome Run(Func<bool>? stopped)
        {
            var probe = new AnalyticProbe(SeriesRlc(f0, q, r), Grid(1.5e9, 2.0e9, 9));
            return PlanarResonanceSearch.Search(
                probe.Nodes, 1.5e9, 2.0e9, Z0, PlanarInterpolant.CubicSpline, 1e-3,
                PlanarResonanceSettings.Default, probe.Probe, stopped);
        }

        var without = Run(null);
        var never   = Run(() => false);

        Assert.False(without.StoppedEarly);
        Assert.False(never.StoppedEarly);
        Assert.Equal(without.Note, never.Note);
        Assert.Equal(without.AddedFrequencies, never.AddedFrequencies);
        Assert.Equal(without.Resonances.Count, never.Resonances.Count);
        for (int i = 0; i < without.Resonances.Count; i++)
        {
            Assert.Equal(without.Resonances[i].FrequencyHz, never.Resonances[i].FrequencyHz);
            Assert.Equal(without.Resonances[i].Q,           never.Resonances[i].Q);
        }
    }

    /// <summary>
    /// A progress sink that runs ON the reporting thread. <see cref="Progress{T}"/> posts through
    /// the captured synchronisation context, so a stop armed from inside one arrives an unknown
    /// number of work units late — which is the one thing a test counting work units after a stop
    /// cannot tolerate. <see cref="IProgress{T}"/> is an interface for exactly this reason.
    /// </summary>
    private sealed class SyncProgress(Action<RunProgress> on) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => on(value);
    }

    /// <summary>
    /// <b>A stop is answered inside a refinement ROUND, not at the end of one</b> (owner report,
    /// 2026-09-11: Stop was pressed and the solver went on solving frequencies).
    ///
    /// <para>The adaptive refinement's outer loop reads the stop once per round, and a round is not
    /// one solve — every interval that fails its tolerance splits in two, so the probe list doubles
    /// and a late round is dozens of full-wave points. Reading the stop only between rounds meant
    /// waiting for all of them. This counts SOLVES, not seconds: with the stop armed at the fourth,
    /// a run that reads it per probe cannot get past the fifth, while the same run left alone solves
    /// far more.</para>
    /// </summary>
    [Fact]
    public void AStoppedAdaptiveSweep_TakesNoFurtherProbesInTheRoundItWasStoppedIn()
    {
        var (p, m, ports) = ResonantFixture();
        double[] freqs = Grid(2e9, 6e9, 17);

        // A tolerance this fixture cannot meet on this grid, so refinement keeps splitting and the
        // rounds really do grow — without that there is no batch for a stop to land inside of.
        var st = new PlanarSolveSettings(
            Deembed: false, Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-4));

        var free = PlanarSolve.Run(p, m, ports, freqs, st);

        // PAST the five seed points on purpose: what is being gated is the refinement BATCH, which
        // is where the reported run would not stop. The seed loop has a stop of its own and a floor
        // of two nodes under it, and arming inside it would test that instead.
        const int ArmAt = 6;
        RunControl? control = null;
        control = new RunControl
        {
            // No throttle: the gate is the COUNT of solves after the stop, so every tick has to be
            // seen. The default 40 ms floor would hide the very ticks being counted.
            MinReportIntervalMs = 0,
            Progress = new SyncProgress(pr => { if (pr.Completed >= ArmAt) control!.RequestStop(); }),
        };

        var stopped = PlanarSolve.Run(p, m, ports, freqs, st, control);

        _out.WriteLine($"free: {free.SolvedPointCount} solved of {freqs.Length}");
        _out.WriteLine($"stopped: {stopped.SolvedPointCount} solved, armed at {ArmAt}");

        // The stop is read BEFORE each probe, so the solve that armed it is the last one.
        Assert.InRange(stopped.SolvedPointCount, 2, ArmAt + 1);
        Assert.True(free.SolvedPointCount > ArmAt + 1,
                    $"the unstopped run must actually do more work than the gate allows the stopped " +
                    $"one, or this proves nothing (it solved {free.SolvedPointCount})");

        // A stopped adaptive run is still a COMPLETE result on the user's own grid — and it says so.
        Assert.Equal(freqs.Length, stopped.Points.Count);
        var note = stopped.Notes.FirstOrDefault(n => n.StartsWith("STOPPED EARLY", StringComparison.Ordinal));
        Assert.NotNull(note);

        // And it claims NO convergence verdict: refinement was cut short, so the worst disagreement
        // it had measured is a maximum over intervals it reached and says nothing about the rest.
        Assert.Null(stopped.AdaptiveConverged);
        Assert.DoesNotContain("CONVERGED", note, StringComparison.Ordinal);
        _out.WriteLine(note);
        _out.WriteLine(note);
    }

    /// <summary>
    /// <b>A stop asked for before the first point still publishes ONE.</b> The mesh and the core
    /// fill run before any point does and on a large board are minutes of their own, so a stop can
    /// genuinely arrive with nothing solved. Publishing a sweep of no points is not "keep what you
    /// solved" — and the empty result would be written straight over the last good `.snp` at the
    /// setup's own predictable path.
    /// </summary>
    [Fact]
    public void AStopAskedForBeforeTheFirstPoint_StillPublishesOne()
    {
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 5);

        var control = new RunControl { MinReportIntervalMs = 0 };
        control.RequestStop();                      // before the run has taken a single point

        var run = PlanarSolve.Run(p, m, ports, freqs, new PlanarSolveSettings(Deembed: false), control);

        Assert.Single(run.Points);
        Assert.Equal(freqs[0], run.Points[0].FrequencyHz);
        var note = run.Notes.FirstOrDefault(n => n.StartsWith("STOPPED EARLY", StringComparison.Ordinal));
        Assert.NotNull(note);
        _out.WriteLine(note);
    }

    /// <summary>
    /// <b>A STOPPED run still has a far field, and the stop reaches INTO the block</b> (owner
    /// report, 2026-09-11 — the second report of "I pressed Stop and it kept going", this time on a
    /// 101-point patch antenna whose far-field block was still climbing through 71 of 101 patterns
    /// long after the button was pressed).
    ///
    /// <para><b>Both halves of that are requirements and the first cut had each of them alone.</b>
    /// The cut before this one declined the remaining patterns outright and handed back
    /// s-parameters with no pattern at all, which is the half of the answer nobody on an antenna was
    /// waiting for. The cut after it took the whole block on the grounds that a pattern SOLVES
    /// nothing — true, and irrelevant to how long it takes: ANT-12 measured ~6.4 s per pattern at
    /// N = 1,611 against ~4.6 s for the de-embedded solve it rides on, so at one pattern per solved
    /// point the block is the LONGEST part of the run and sitting through all of it is not
    /// stopping.</para>
    ///
    /// <para>So: patterns are taken until the stop, with a floor of ONE, and the note says which of
    /// the two happened — the cubes are indistinguishable otherwise, differing only in how many
    /// slices the frequency axis carries.</para>
    /// </summary>
    [Fact]
    public void AStoppedRun_KeepsTheFarFieldItTook_AndStopsTakingMore()
    {
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 9);

        // A coarse pattern grid: this gates the CONTROL FLOW around the block, and a 1 degree
        // hemisphere would pay for angular resolution no assertion here reads.
        var st = new PlanarSolveSettings(
            Deembed: false,
            Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-4),
            FarField: new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(30, 45), freqs));

        var free = PlanarSolve.Run(p, m, ports, freqs, st);
        Assert.NotNull(free.FarField);
        Assert.True(free.FarField!.FrequenciesHz.Count > 1,
                    "the unstopped run must take more than one pattern or this proves nothing");

        // ── Stop pressed while it is still SOLVING ────────────────────────────────────────────
        RunControl? solving = null;
        solving = new RunControl
        {
            MinReportIntervalMs = 0,
            Progress = new SyncProgress(pr => { if (pr.Completed >= 4) solving!.RequestStop(); }),
        };
        var stoppedSolving = PlanarSolve.Run(p, m, ports, freqs, st, solving);

        _out.WriteLine($"stopped while solving: {stoppedSolving.SolvedPointCount} solved, " +
                       $"{stoppedSolving.FarField?.FrequenciesHz.Count ?? 0} pattern frequency(ies)");

        // There IS a far field — that is the floor, and it is what a stopped antenna run can plot.
        Assert.NotNull(stoppedSolving.FarField);
        Assert.NotNull(stoppedSolving.Metrics);
        Assert.NotEmpty(stoppedSolving.FarField!.FrequenciesHz);
        // And the s-parameters are untouched: the whole requested grid is still published.
        Assert.Equal(freqs.Length, stoppedSolving.Points.Count);

        // ── Stop landing INSIDE the block: it takes the one it is on, then no more ────────────
        RunControl? inBlock = null;
        inBlock = new RunControl
        {
            MinReportIntervalMs = 0,
            Progress = new SyncProgress(pr =>
            {
                if (pr.Stage.StartsWith("far field", StringComparison.Ordinal) && pr.StageCompleted >= 1)
                    inBlock!.RequestStop();
            }),
        };
        var stoppedInBlock = PlanarSolve.Run(p, m, ports, freqs, st, inBlock);

        _out.WriteLine($"stopped in the block: {stoppedInBlock.FarField?.FrequenciesHz.Count ?? 0} " +
                       $"of {free.FarField.FrequenciesHz.Count} pattern frequency(ies)");

        Assert.NotNull(stoppedInBlock.FarField);
        Assert.NotEmpty(stoppedInBlock.FarField!.FrequenciesHz);
        Assert.True(stoppedInBlock.FarField.FrequenciesHz.Count < free.FarField.FrequenciesHz.Count,
                    "a stop inside the far-field block must stop it taking patterns");
        Assert.Contains(stoppedInBlock.Notes,
                        n => n.StartsWith("The far field was CUT SHORT", StringComparison.Ordinal));

        // Every pattern that WAS taken is a whole one — the block is truncated, never a pattern.
        Assert.Equal(stoppedInBlock.FarField.FrequenciesHz.Count * ports.Count,
                     stoppedInBlock.FarField.Patterns.Count);
        Assert.Equal(free.FarField.Patterns.Count / free.FarField.FrequenciesHz.Count,
                     stoppedInBlock.FarField.Patterns.Count / stoppedInBlock.FarField.FrequenciesHz.Count);
    }

    /// <summary>
    /// <b>A stopped SWEEP publishes the prefix it solved and names what is missing.</b> The fixed-grid
    /// half of the same request: nothing downstream distinguishes a stopped result from a completed
    /// one — same DataSet, same cubes, same `.snp` — which is exactly why the note is mandatory.
    /// </summary>
    [Fact]
    public void AStoppedFixedGridSweep_PublishesThePrefix_AndNamesWhatIsNotInIt()
    {
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 6e9, 9);

        // The point loop reads the stop at the TOP of each point, and a progress tick is the only
        // seam a test has into it — so the stop is armed from inside the sink, after two points.
        RunControl? control = null;
        int ticks = 0;
        control = new RunControl
        {
            Progress = new Progress<RunProgress>(_ => { if (++ticks >= 2) control!.RequestStop(); }),
        };

        var run = PlanarSolve.Run(p, m, ports, freqs,
                                  new PlanarSolveSettings(Deembed: false), control);

        var note = run.Notes.FirstOrDefault(n => n.StartsWith("STOPPED EARLY", StringComparison.Ordinal));
        _out.WriteLine($"{run.Points.Count} of {freqs.Length} point(s) published");
        _out.WriteLine(note ?? "(no stop note)");

        // The PREFIX, and it is a real sweep: fewer points than asked for, every one of them solved
        // rather than modelled, and in ascending order like any other.
        Assert.NotNull(note);
        Assert.InRange(run.Points.Count, 1, freqs.Length - 1);
        Assert.Contains("PREFIX", note, StringComparison.Ordinal);
        Assert.Contains($"of {freqs.Length} requested", note, StringComparison.Ordinal);
        for (int i = 0; i < run.Points.Count; i++)
            Assert.Equal(freqs[i], run.Points[i].FrequencyHz);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // The far field AT the found resonance (owner report, 2026-09-11).
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The run published a pattern at every solved grid point and none at the frequency the
    /// search went and found.</b> The far-field block runs over the grid BEFORE the search, and its
    /// stores were keyed by grid index, so a found point had nowhere to put one — which on an
    /// antenna is the one frequency both switches were turned on for.
    ///
    /// <para>The gate is a frequency that is in the far-field set and is NOT on the requested grid:
    /// that can only have come from the search. Asserting merely "more slices than before" would
    /// pass on an extra grid point, and asserting on a pattern's CONTENT would be gating this
    /// change on the physics of the fixture rather than on the plumbing it actually fixes.</para>
    /// </summary>
    [Fact]
    public void AFoundResonance_GetsAFarFieldPatternOfItsOwn_AtAFrequencyNotOnTheGrid()
    {
        var (p, m, ports) = ResonantFixture();
        double[] freqs = Grid(2e9, 6e9, 11);

        // A COARSE pattern grid. The direction count is the whole cost of a pattern and none of the
        // subject of this test — the subject is which FREQUENCIES get one.
        var far = new PlanarFarFieldSettings(
            Grid: PlanarFarFieldGrid.Hemisphere(30, 60), FrequenciesHz: freqs);

        var adaptive = new PlanarAdaptiveSettings(Tolerance: 1e-2)
        {
            Search = PlanarResonanceSettings.Default with { MaxAddedPoints = 4 },
        };

        var run = PlanarSolve.Run(p, m, ports, freqs,
                                  new PlanarSolveSettings(Deembed: false, Adaptive: adaptive,
                                                          FarField: far));

        Assert.NotEmpty(run.Resonances);
        Assert.NotNull(run.FarField);

        var farF   = run.FarField!.FrequenciesHz;
        var offGrid = farF.Where(f => !freqs.Contains(f)).ToArray();

        _out.WriteLine($"{farF.Count} far-field slice(s); {offGrid.Length} off the requested grid");
        foreach (var r in run.Resonances)
            _out.WriteLine($"  resonance {r.FrequencyHz / 1e9:F5} GHz Q = {r.Q:F2} {r.Kind}");
        foreach (double f in offGrid) _out.WriteLine($"  extra pattern at {f / 1e9:F5} GHz");

        // The fix itself: at least one pattern at a frequency the user did not ask for, which is
        // only reachable through the search.
        Assert.NotEmpty(offGrid);

        // And it is AT a resonance — within the bracket the search reports having located it to,
        // never somewhere else that merely happens to be off-grid.
        foreach (double f in offGrid)
            Assert.Contains(run.Resonances,
                            r => Math.Abs(r.FrequencyHz - f) <= Math.Max(r.LocatedToHz, 1e-6 * r.FrequencyHz));

        // The set is still ascending and still carries one slice per port per frequency.
        Assert.Equal(farF.OrderBy(f => f), farF);
        Assert.Equal(farF.Count * run.FarField.PortNumbers.Count, run.FarField.Patterns.Count);

        // Every requested grid point that was SOLVED still has its own slice — the resonance
        // patterns are additions, never replacements.
        foreach (double f in run.SolvedFrequencies.Where(freqs.Contains))
            Assert.Contains(f, farF);

        Assert.Contains(run.Notes, n => n.Contains("AT the resonance", StringComparison.Ordinal));
    }

}
