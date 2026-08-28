using System.Numerics;

namespace CircuitRF.Core.Matching;

/// <summary>
/// match.md §16.3's prototype family: a ladder of <b>single</b> elements matched between F1 and F2,
/// with the impedance ratio pinned by the ladder's own transparency at DC.
/// </summary>
/// <remarks>
/// <para>With <c>u = omega^2</c>, <c>a = F1/F2</c> and the band mapped onto [-1, 1] by
/// <c>x(u) = (2u - 1 - a^2)/(1 - a^2)</c>, the family is</para>
/// <code>
///   Phi(u) = T_n(x(u))^2   (Chebyshev)   or   x(u)^(2n)   (Butterworth)
///   |Gamma|^2 = (K + eps^2 Phi) / (1 + eps^2 Phi)
/// </code>
/// <para><b>Phi is degree 2n in u, not n</b> — match.md §16.3 says "degree n in u", which is a slip;
/// every conclusion it draws from it (2n elements, polynomials of degree 4n in s) is the arithmetic
/// for degree 2n and is right. <c>T_n(x)</c> is degree n in x and x is degree 1 in u, so the SQUARE is
/// degree 2n; at a = 0 the family reduces to <c>T_n(T_2(omega))^2 = T_2n(omega)^2</c>, the classical
/// Chebyshev lowpass of order 2n, which is the same count read another way.</para>
///
/// <h3>Why this does not go through <see cref="MatchPrototypes"/>'s route</h3>
/// <para><b>Because that route cannot solve this family past order 4, and the reason is
/// conditioning rather than a bug in it.</b> §6.2 builds the numerator and denominator as
/// polynomials and root-finds them; here they are degree <b>4n in s</b> — 24 at order 6 — with
/// coefficients spanning many decades, and a companion-matrix root-find on one of those loses enough
/// significance that the Cauer extraction stops mid-way. Measured over a 360-cell sweep (orders 2-6,
/// six bandwidths, six ratios, both families): the polynomial route failed <b>144</b> cells,
/// including <i>every</i> order-6 cell at every K it was tried at.</para>
/// <para>Two changes take that to <b>zero</b> failures over the same sweep:</para>
/// <list type="number">
/// <item><b>The roots are written down instead of searched for.</b> Both polynomials are
/// <c>c + eps^2 Phi</c>, so their roots are the solutions of <c>Phi(x) = -c/eps^2</c> mapped through
/// <c>x -&gt; u -&gt; s</c> — one arccosine (Chebyshev) or one root of unity (Butterworth) and then
/// exact arithmetic. No polynomial of degree 4n is ever formed, let alone factored.</item>
/// <item><b>The continued fraction's degree drop is STRUCTURAL, not measured.</b>
/// <see cref="MatchPrototypes.Extract"/> subtracts <c>g_k s</c> and then asks a tolerance which
/// leading coefficients cancelled; the top TWO always do, identically, and at order 6 the second one
/// survives at 1e-4 of its neighbours after twelve steps of cancellation — enough for the length test
/// to fail and the whole extraction to return null. Dropping them because they are known to be zero
/// removes the question. (The last step is the exception: the remainder is then the terminating
/// resistance itself, so only one comes off — see <see cref="Extract"/>.)</item>
/// </list>
/// <para>None of this touches <see cref="MatchPrototypes"/>, which is still the bandpass route and
/// still passes its own cross-checks.</para>
/// </remarks>
public static class MatchFormPrototype
{
    /// <summary>
    /// The floor on K — match.md §16.9's trap, at <b>1e-12</b> rather than the 1e-6 that section
    /// quotes.
    /// </summary>
    /// <remarks>
    /// <b>K = 0 exactly is the trap; 1e-6 is not the smallest safe value, and it is not a numerical
    /// choice at all. Measured, all three.</b> At K = 0 the numerator's roots sit in DOUBLE pairs on
    /// the jw axis, so "the left half" is not well defined and the tie-break takes both copies of the
    /// lowest pairs rather than one of each — which is not a spectral factor of anything, and every
    /// real-to-real case reports unrealizable there.
    ///
    /// <para>But <b>K is also the in-band return-loss FLOOR</b>: the worst <c>|Gamma|^2</c> is
    /// <c>(K + eps^2)/(1 + eps^2)</c>, so it caps the response at <c>10 log10 K</c> whatever the
    /// family could otherwise reach. At 1e-6 that is -60 dB, which costs 0.12 dB on match.md §16.2's
    /// own -44.3 dB cell (twice what the acceptance test allows) and puts the from-DC reduction
    /// 2.4e-3 off the textbook 0.1 dB table (against the 1e-5 that test asks for). The error scales
    /// as sqrt(K).</para>
    ///
    /// <para><b>Nothing degrades on the way down.</b> Over the 360-cell sweep the class remark
    /// describes, every floor from 1e-6 to 1e-14 extracts all 360 with the terminating ratio exact to
    /// 1e-13; only the K = 0 endpoint fails. 1e-12 is taken because it is where the from-DC reduction
    /// reaches 2.4e-6 — a comfortable margin inside the 1e-5 gate — and the g-values are converged to
    /// five significant figures; a -120 dB response ceiling is past anything a matching network
    /// means. Lower buys nothing and only walks toward the endpoint that does fail.</para>
    ///
    /// <para>match.md's own Golden A was computed at 1e-6 and its Golden E at 1e-9, which is how the
    /// discrepancy was noticed; both are asserted at their own K in the tests rather than against
    /// this constant.</para>
    /// </remarks>
    public const double KFloor = 1e-12;

    /// <summary>
    /// The g-vector of one member: <c>2n</c> element values followed by the terminating resistance
    /// ratio, or null when that member does not extract.
    /// </summary>
    /// <param name="shape"><see cref="ResponseShape.ChebyshevFano"/> or
    /// <see cref="ResponseShape.Butterworth"/>; the two Chebyshev members are the same family here.</param>
    /// <param name="n">Order — the number of in-band match points. The element count is 2n.</param>
    /// <param name="a">F1/F2, in [0, 1).</param>
    /// <param name="r">The impedance ratio the DC pin is taken at.</param>
    /// <param name="k">The one free parameter, in (0, Gamma0^2).</param>
    public static double[]? Gvalues(ResponseShape shape, int n, double a, double r, double k)
    {
        if (!(r > 0.0)) return null;
        double gamma0 = (r - 1.0) / (r + 1.0);
        double g02 = gamma0 * gamma0;
        if (!(k > 0.0) || k >= g02) return null;

        double phi0 = PhiAtDc(shape, n, a);
        if (!(phi0 > 0.0)) return null;
        double eps2 = (g02 - k) / (phi0 * (1.0 - g02));
        return GvaluesAt(shape, n, a, k, eps2);
    }

    /// <summary>
    /// The same family with <c>eps^2</c> supplied directly rather than taken from the DC pin — the
    /// degenerate equal-resistance case of match.md §3.2, where the pin says nothing.
    /// </summary>
    /// <remarks>
    /// With r = 1 the pin reads <c>|Gamma(0)| = 0</c>, which this family can only meet at K = 0 — the
    /// trap <see cref="KFloor"/> exists to avoid. So the pin is dropped, K sits on its floor (a DC
    /// return loss of 90 dB, which is nothing), and eps becomes the free parameter instead.
    /// </remarks>
    public static double[]? GvaluesAt(ResponseShape shape, int n, double a, double k, double eps2)
    {
        if (n < MatchOrders.MinOrder || n > MatchOrders.MaxOrder) return null;
        if (!(a >= 0.0) || a >= 1.0) return null;
        if (!(k > 0.0) || k >= 1.0) return null;
        if (!(eps2 > 0.0) || !double.IsFinite(eps2)) return null;
        if (shape is not (ResponseShape.ChebyshevFano or ResponseShape.ChebyshevTwoEnded
                          or ResponseShape.Butterworth))
            return null;

        double eps = Math.Sqrt(eps2);
        int m = 2 * n;

        double[] den = LeftHalfPlane(RootsInS(shape, n, a, eps, 1.0), m);
        double[] num = LeftHalfPlane(RootsInS(shape, n, a, eps, k), m);
        if (den.Length != m + 1 || num.Length != m + 1) return null;

        // Both factors are monic, so (den - num) loses its leading coefficient identically — which is
        // what makes the ratio one degree higher on top and gives the ladder its pole at infinity.
        double[] p = MatchPoly.Add(den, num);
        double[] q = MatchPoly.Sub(den, num)[1..];
        return Extract(p, q, m);
    }

    /// <summary>Phi(0) — the DC value the pin is written against.</summary>
    public static double PhiAtDc(ResponseShape shape, int n, double a)
    {
        if (!(a >= 0.0) || a >= 1.0) return double.NaN;
        double x0 = -(1.0 + a * a) / (1.0 - a * a);
        if (shape == ResponseShape.Butterworth) return Math.Pow(x0, 2 * n);
        double t = ChebyshevAt(n, x0);
        return t * t;
    }

    /// <summary>
    /// <c>T_n(x)</c> for a real argument, through the hyperbolic form outside [-1, 1] — which is where
    /// x0 always sits, since the band never contains DC in the mapped variable.
    /// </summary>
    public static double ChebyshevAt(int n, double x)
    {
        if (Math.Abs(x) <= 1.0) return Math.Cos(n * Math.Acos(x));
        double v = Math.Cosh(n * Math.Acosh(Math.Abs(x)));
        return x < 0 && n % 2 != 0 ? -v : v;
    }

    /// <summary>
    /// The worst in-band <c>|Gamma|^2</c> of one member — <c>(K + eps^2)/(1 + eps^2)</c>, because
    /// Phi's in-band maximum is 1 in both families by construction.
    /// </summary>
    public static double WorstInBand(double k, double eps2) => (k + eps2) / (1.0 + eps2);

    /// <summary>
    /// match.md §16.3's closed form: the best in-band return loss a Chebyshev member of this family
    /// can reach at order n, bandwidth ratio a and impedance ratio r. <b>Not used by the synthesis</b>
    /// — it is the oracle §13.2 checks the synthesis against, and lives here so a caller can quote it.
    /// </summary>
    public static double BestReturnLossDb(int n, double a, double r)
    {
        double gamma0 = (r - 1.0) / (r + 1.0);
        double g02 = gamma0 * gamma0;
        double x0 = -(1.0 + a * a) / (1.0 - a * a);
        double t = ChebyshevAt(n, x0);
        return 10.0 * Math.Log10(g02 / (g02 + t * t * (1.0 - g02)));
    }

    /// <summary>
    /// The 4n roots in s of <c>c + eps^2 Phi(u)</c>, written down rather than searched for.
    /// </summary>
    /// <remarks>
    /// <c>Phi(x) = -c/eps^2</c> is solved in x — one arccosine for the whole Chebyshev set, since
    /// <c>T_n(x) = -w</c> is <c>T_n(x) = cos(pi - theta)</c> and needs no second one — and each x is
    /// then carried through <c>u = ((1 - a^2)x + 1 + a^2)/2</c> and <c>s = +/- j sqrt(u)</c> by exact
    /// arithmetic. The arccosine is taken in the <c>pi/2 - j asinh(y)</c> form: its argument is
    /// <c>+j y</c> with y &gt; 0, where the standard <c>-j log(z + j sqrt(1 - z^2))</c> has no
    /// cancellation. The other sign of y is the branch where it does, which is why it is not taken.
    /// </remarks>
    private static Complex[] RootsInS(ResponseShape shape, int n, double a, double eps, double c)
    {
        var xs = new List<Complex>(2 * n);
        if (shape == ResponseShape.Butterworth)
        {
            // x^(2n) = -c/eps^2: the 2n roots of a negative real, evenly spaced.
            double rho = Math.Pow(Math.Sqrt(c) / eps, 1.0 / n);
            for (int j = 0; j < 2 * n; j++)
                xs.Add(Complex.FromPolarCoordinates(rho, Math.PI * (2 * j + 1) / (2 * n)));
        }
        else
        {
            // T_n(x)^2 = -c/eps^2, i.e. T_n(x) = +/- j y.
            double y = Math.Sqrt(c) / eps;
            var theta = new Complex(Math.PI / 2.0, -Math.Asinh(y));
            for (int j = 0; j < n; j++)
            {
                xs.Add(Complex.Cos((theta + 2.0 * Math.PI * j) / n));
                xs.Add(Complex.Cos((Math.PI - theta + 2.0 * Math.PI * j) / n));
            }
        }

        var ss = new List<Complex>(4 * n);
        foreach (var x in xs)
        {
            var u = ((1.0 - a * a) * x + (1.0 + a * a)) / 2.0;
            var w = Complex.Sqrt(u);
            ss.Add(Complex.ImaginaryOne * w);
            ss.Add(-Complex.ImaginaryOne * w);
        }
        return [.. ss];
    }

    /// <summary>The monic left-half-plane factor of a root set, as a real polynomial of degree m.</summary>
    private static double[] LeftHalfPlane(Complex[] roots, int m)
    {
        var lhp = roots.Where(z => z.Real < 0.0).ToList();
        if (lhp.Count != m)
            lhp = [.. roots.OrderBy(z => z.Real).ThenBy(z => z.Imaginary).Take(m)];
        return MatchPoly.FromRoots(lhp);
    }

    /// <summary>
    /// Cauer extraction of <paramref name="count"/> elements plus the terminating ratio, with the
    /// remainder's degree taken rather than measured. See the class remark for why.
    /// </summary>
    /// <remarks>
    /// Each removal takes the pair from (degree m+1, degree m) to (m, m-1), so the remainder's top
    /// two coefficients are identically zero — <b>except at the very last removal</b>, where the
    /// remainder IS the terminating resistance and only the leading one goes. Hence the
    /// <c>max(1, ...)</c>: the kept length is the divisor's, one shorter, and never less than the one
    /// constant that has to survive.
    /// </remarks>
    public static double[]? Extract(double[] num, double[] den, int count)
    {
        ArgumentNullException.ThrowIfNull(num);
        ArgumentNullException.ThrowIfNull(den);

        var g = new double[count + 1];
        double[] a = num, b = den;
        for (int i = 0; i < count; i++)
        {
            if (a.Length != b.Length + 1 || b.Length == 0 || b[0] == 0.0) return null;
            double gk = a[0] / b[0];
            if (!double.IsFinite(gk) || gk <= 0.0) return null;

            double[] full = MatchPoly.Sub(a, MatchPoly.Mul([gk, 0.0], b));
            int keep = Math.Max(1, b.Length - 1);
            double[] rem = full[^keep..];
            (a, b) = (b, rem);
            g[i] = gk;
        }
        if (a.Length != 1 || b.Length != 1 || b[0] == 0.0) return null;
        double last = a[0] / b[0];
        if (!double.IsFinite(last) || last <= 0.0) return null;
        g[count] = last;
        return g;
    }
}

/// <summary>
/// match.md §16: the lowpass and highpass forms — a ladder of single elements matched between F1 and
/// F2, the ratio pinned by transparency at DC (or at infinity), absorbing whichever terminations the
/// form can hold.
/// </summary>
/// <remarks>
/// <h3>The orientation is decided by the RATIO, not by the analysis end's topology</h3>
/// <para><b>match.md §16.3 says the ladder starts shunt-first for a parallel analysis end and
/// series-first for a series one, "exactly as today". It cannot: in this family the orientation is
/// not free.</b> The family depends on the terminations only through <c>Gamma0^2</c>, which is the
/// same for r and 1/r, so one extraction serves both — and the terminating value it produces is
/// <c>max(r, 1/r)</c> in every cell measured (five orders x six bandwidths x six ratios x two
/// families). Reading that g-vector as a series-first ladder puts <c>R_far = g_last * R_ana</c>;
/// reading it as its dual, shunt-first, puts <c>R_far = R_ana / g_last</c>. Exactly one of the two
/// lands on the requested resistance.</para>
/// <para>Which is to say the physics, plainly: <b>the low-impedance port sees a series inductor and
/// the high-impedance port sees a shunt capacitor</b> (lowpass; a series C and a shunt L for
/// highpass). That is the L-match rule every order inherits, and it is not a choice the synthesis
/// gets to make.</para>
/// <para><b>The consequence is a real constraint on absorption that §16.4 does not state.</b> Its
/// item 1 says a lowpass ladder absorbs R&#8741;C and R+L; true, but WHICH end takes which is decided
/// by the ratio. A shunt capacitance on the LOW-impedance side of a step-up is not absorbable by
/// either lowpass or highpass form, and the refusal has to say so rather than blaming the kind.</para>
///
/// <h3>match.md §3.7's physical golden values do not describe a 50 ohm far end</h3>
/// <para>Its normalised g-vector is exact and reproduces here to 1e-9. Its physical line —
/// <c>C1 = 15.8222 pF, L1 = 107.376 pH, C2 = 43.0466 pF, L2 = 39.4420 pH</c> for "5 ohm (analysis,
/// parallel side) -&gt; 50 ohm" — is those g's denormalised at R_ana = 5 ohm and read SHUNT-first,
/// which by the paragraph above is the r = 0.1 network: 5 ohm down to <b>0.5 ohm</b>. Simulated as
/// printed against a 50 ohm far end it is a -0.10 dB match, not the -10.511 dB the same block quotes.
/// Series-first at R_ana = 5 ohm into 50 ohm, and shunt-first at R_ana = 50 ohm into 5 ohm, both give
/// -10.511 dB exactly. The absorbing goldens (B, C, D) are all self-consistent as the 5 -&gt; 0.5 ohm
/// problem, which is how the slip was located.</para>
///
/// <h3>K is not monotone in the near-end element</h3>
/// <para>§16.4 item 3 says the two end elements move in opposite directions with K and calls it a 1-D
/// monotone problem. The far element does fall monotonically; the near element <b>rises and then
/// falls</b>, peaking near <c>K = 0.95 Gamma0^2</c> (measured: a = 0.5, n = 2, r = 10 takes g1 from
/// 2.485 at the floor up to 11.00 at K = 0.64 and back to 4.35 at 0.66942). A bisection written for a
/// monotone g1 would find the wrong end of the feasible interval or miss it; a coarse scan followed
/// by a geometric bisection on the boundary it brackets does not care, and is what
/// <see cref="Synthesize"/> runs.</para>
/// </remarks>
public static class MatchFormSynthesis
{
    /// <summary>How many members the K scan looks at before it refines. See the class remark.</summary>
    /// <remarks>
    /// <b>Linear in K, not logarithmic.</b> The interesting structure — the near element's peak, and
    /// the far element's collapse — is all in the top few per cent of [0, Gamma0^2], which a log grid
    /// samples twice and a linear one samples densely. The low decades matter only for locating a
    /// K_min that sits near the floor, and that is what the geometric bisection afterwards is for.
    /// </remarks>
    private const int ScanSamples = 128;

    /// <summary>
    /// Relative slack when asking whether the FAMILY can reach an end element — used to decide
    /// feasibility, never to decide the value.
    /// </summary>
    /// <remarks>
    /// <b>Only ever widens the family's side, and only for the refusal.</b> The chosen member is
    /// required to meet each end EXACTLY (<c>g_synth &gt;= g_actual</c>, no slack), because a
    /// synthesised end element smaller than the termination's own is the "a parasitic cannot be
    /// subtracted" failure — it would leave an element marked absorbed while carrying a value the
    /// termination does not supply, which is the bug match.md's own §4.6 note records from 2026-08-20.
    /// The slack exists so that a termination the family misses by one part in 1e9 is reported as the
    /// near miss it is rather than as an exact refusal.
    /// </remarks>
    private const double AbsorbTolerance = 1e-9;

    /// <summary>Synthesises the basis ladder of a lowpass- or highpass-form design.</summary>
    /// <remarks>
    /// Reached from <see cref="MatchSynthesis.Synthesize"/>, which memoises it, and only after that
    /// method's own termination validation. Nothing of match.md §4.1-§4.3 applies: there is no band
    /// centre, no fractional bandwidth and no Fano root here, which is why the dispatch happens before
    /// any of it rather than inside it.
    /// </remarks>
    public static MatchSynthesisResult Synthesize(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        bool lowpass = design.Form == NetworkForm.Lowpass;

        if (!(design.F2 > 0.0) || !(design.F1 >= 0.0) || !(design.F2 > design.F1))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidTermination,
                "The band must satisfy 0 <= F1 < F2.", null, ("f1", design.F1), ("f2", design.F2)));
        if (!lowpass && !(design.F1 > 0.0))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidTermination,
                "A highpass network is matched between F1 and F2 and pinned at infinity, so F1 must "
                + "be above zero. F1 = 0 is the lowpass form's own degenerate case, not this one.",
                null, ("f1", design.F1), ("f2", design.F2)));

        var valid = MatchOrders.ValidOrders(design.Term1, design.Term2, design.Form);
        if (valid.Count == 0)
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidOrder,
                $"Both terminations are {Topology(design.Term1)}; a {Word(design.Form)} network needs "
                + "an ODD element count to absorb two ends of the same topology, which this form does "
                + "not offer yet (match.md §16.3). Bandpass form absorbs both.",
                null, ("order", design.Order)));
        if (!valid.Contains(design.Order))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidOrder,
                $"Order {design.Order} is outside {valid[0]}..{valid[^1]}.",
                null, ("order", design.Order), ("minValid", valid[0]), ("maxValid", valid[^1])));

        if (design.Response is ResponseShape.Bessel or ResponseShape.ChebyshevTwoEnded)
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.ResponseInfeasible,
                $"{design.Response} is not offered in {Word(design.Form)} form: the impedance ratio is "
                + "pinned at DC, so the family has ONE free parameter and there is neither a second "
                + "prescribed Q to spend it on nor a delay target to shape (match.md §16.2). "
                + "Chebyshev and Butterworth are.",
                null, ("order", design.Order)));

        int n = design.Order, m = 2 * n;
        var family = design.Response == ResponseShape.Butterworth
            ? ResponseShape.Butterworth
            : ResponseShape.ChebyshevFano;

        bool anaIsTerm1 = MatchSynthesis.AnalysisIsTerm1(design);
        Termination ana = anaIsTerm1 ? design.Term1 : design.Term2;
        Termination far = anaIsTerm1 ? design.Term2 : design.Term1;
        int anaEnd = anaIsTerm1 ? 1 : 2, farEnd = anaIsTerm1 ? 2 : 1;

        double rAna = ana.R, rFarTarget = far.R;
        double ratio = rFarTarget / rAna;
        double wRef = 2.0 * Math.PI * (lowpass ? design.F2 : design.F1);
        double a = design.F1 / design.F2;

        // The low-impedance port takes the series element; the high-impedance port the shunt one.
        // With 2n elements the two ends are always of opposite orientation, so this one flag decides
        // the whole ladder. See the class remark for why it is not the analysis end's topology.
        bool shuntFirst = ratio < 1.0;

        // ── Can this form hold what the terminations supply? (match.md §16.4 item 1, corrected) ──
        var refusal = CheckAbsorbable(design, ana, anaEnd, shuntFirst, lowpass, ratio)
                      ?? CheckAbsorbable(design, far, farEnd, !shuntFirst, lowpass, ratio);
        if (refusal is not null) return MatchSynthesisResult.Refuse(refusal);

        double gNearActual = Normalise(design.Form, ana, shuntFirst, rAna, wRef);
        double gFarActual = Normalise(design.Form, far, !shuntFirst, rFarTarget, wRef);

        // ── The one free parameter ───────────────────────────────────────────
        double gamma0 = (ratio - 1.0) / (ratio + 1.0);
        double g02 = gamma0 * gamma0;
        bool pinned = g02 > 4.0 * MatchFormPrototype.KFloor;

        // The degenerate equal-resistance case of match.md §3.2: the DC pin reads |Gamma(0)| = 0,
        // which this family meets only at K = 0. So the pin goes, K sits on its floor, and eps is
        // chosen for a nominal -40 dB in-band. There is then ONE member and no search — a reactive
        // termination either fits it or is refused, which is honest and is why it is recorded here
        // and in RESOLVED.md rather than smoothed over.
        double epsNominal2 = (1e-4 - MatchFormPrototype.KFloor) / (1.0 - 1e-4);
        double kHi = pinned ? g02 * (1.0 - 1e-12) : MatchFormPrototype.KFloor;

        double[]? Member(double k) => pinned
            ? MatchFormPrototype.Gvalues(family, n, a, ratio, k)
            : MatchFormPrototype.GvaluesAt(family, n, a, k, epsNominal2);

        bool Feasible(double[] g) => g[0] >= gNearActual && g[m - 1] >= gFarActual;

        double[]? chosen = null;
        double chosenK = MatchFormPrototype.KFloor, lastBad = double.NaN;
        double maxNear = 0.0, maxFarGivenNear = 0.0;
        bool anyMember = false;

        int samples = pinned ? ScanSamples : 0;
        for (int i = 0; i <= samples; i++)
        {
            double k = samples == 0
                ? MatchFormPrototype.KFloor
                : MatchFormPrototype.KFloor + (kHi - MatchFormPrototype.KFloor) * i / samples;
            var g = Member(k);
            if (g is null) { lastBad = k; continue; }

            anyMember = true;
            maxNear = Math.Max(maxNear, g[0]);
            if (g[0] >= gNearActual) maxFarGivenNear = Math.Max(maxFarGivenNear, g[m - 1]);

            if (Feasible(g)) { chosen = g; chosenK = k; break; }
            lastBad = k;
        }

        if (chosen is null)
        {
            if (!anyMember)
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.NoRealRoot,
                    $"The {Word(design.Form)}-form {family} family produced no realizable member at "
                    + $"order {n} over the band {design.F1 / 1e9:0.###}-{design.F2 / 1e9:0.###} GHz "
                    + $"for a {ratio:0.####} : 1 ratio.",
                    null, ("order", n), ("ratio", ratio)));

            if (maxNear < gNearActual * (1.0 - AbsorbTolerance))
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.AnalysisEndNotAbsorbable,
                    $"Termination {anaEnd} is not absorbable at order {n} in {Word(design.Form)} form: "
                    + $"it supplies a normalised {gNearActual:0.####} against the largest "
                    + $"{maxNear:0.####} any member of the family puts at that end. Try a higher "
                    + "order, the other response family, or bandpass form.",
                    anaEnd,
                    ("gActual", gNearActual), ("gMax", maxNear), ("order", n)));

            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.FarEndNotAbsorbable,
                $"Termination {farEnd} is not absorbable at order {n} in {Word(design.Form)} form: the "
                + $"far end can absorb at most {maxFarGivenNear:0.####} against the {gFarActual:0.####} "
                + $"its termination supplies, once termination {anaEnd}'s own {gNearActual:0.####} is "
                + "met. Try another order, the other response family, or bandpass form.",
                farEnd,
                ("gFar", maxFarGivenNear), ("gActual", gFarActual), ("gNear", gNearActual),
                ("order", n)));
        }

        // Refine the boundary the scan bracketed. Geometric, because K_min routinely sits decades
        // below the first grid step and a linear bisection would spend its whole budget above it.
        if (!double.IsNaN(lastBad) && chosenK > lastBad)
        {
            double lo = lastBad, hi = chosenK;
            for (int it = 0; it < 80; it++)
            {
                double mid = Math.Sqrt(lo * hi);
                if (!double.IsFinite(mid) || mid <= lo || mid >= hi) break;
                var g = Member(mid);
                if (g is not null && Feasible(g)) { hi = mid; chosen = g; chosenK = mid; }
                else lo = mid;
            }
        }

        // ── Build ────────────────────────────────────────────────────────────
        double rFarSynth = shuntFirst ? rAna / chosen[m] : chosen[m] * rAna;
        var net = Build(design, chosen, n, shuntFirst, rAna, rFarSynth, anaIsTerm1, ana, far);

        double gNearSynth = chosen[0], gFarSynth = chosen[m - 1];
        bool needsExcess =
            (gNearActual > 0 && gNearSynth / gNearActual > MatchSynthesis.ExcessRatioThreshold)
            || (gFarActual > 0 && gFarSynth / gFarActual > MatchSynthesis.ExcessRatioThreshold);

        // g[0] = 1 in front, so the vector reads like every other prototype in this library and
        // MatchSynthesis.Fingerprint sees the same shape it always has.
        double[] g5 = [1.0, .. chosen];

        var notes = new List<string>();
        if (!pinned)
            notes.Add(
                "The two port resistances are equal, so there is no DC pin to spend the family's free "
                + "parameter on; a nominal -40 dB in-band member was used (match.md §16.3).");

        return new MatchSynthesisResult
        {
            G = g5,
            Omega0 = design.Omega0,
            W = design.W,
            AnalysisIsTerm1 = anaIsTerm1,
            QAnalysis = gNearSynth,
            QAnalysisActual = gNearActual,
            QFarSynthesised = gFarSynth,
            QFarActual = gFarActual,
            RAnalysis = rAna,
            RFarSynthesised = rFarSynth,
            RFarTarget = rFarTarget,
            Network = net,
            UsedRipplePrototype = false,
            NeedsExcessElement = needsExcess,
            BasisFingerprint = MatchSynthesis.Fingerprint(g5, net),
            Notes = notes,
        };
    }

    /// <summary>
    /// Splits each absorbed end element into the termination's own value plus a real added element —
    /// match.md §4.5's rule, in the prototype units this form works in.
    /// </summary>
    /// <remarks>
    /// <b>All four (series|shunt) x (L|C) combinations add LINEARLY in g here</b>, which is what makes
    /// this three lines against <see cref="MatchQ.SplitExcess"/>'s C_eq machinery. A shunt C's g is
    /// <c>w R C</c> and parallel capacitances add; a series L's is <c>w L / R</c> and series
    /// inductances add; a shunt L's is <c>R / (w L)</c> and parallel inductances add reciprocally,
    /// which is the same thing in g; a series C's is <c>1 / (w R C)</c> and series capacitances
    /// likewise. So the surplus is <c>g_synth - g_actual</c> in every case, denormalised back through
    /// the same rule that read it.
    ///
    /// <para><b>The near end's surplus is <see cref="MatchElement.IsExcess"/>, not
    /// <see cref="MatchElement.IsDetune"/></b> (match.md §16.4 item 3). A detune is §4.6's Q-adjust,
    /// which is a bandpass concept and is not offered here — in this form the added element IS the
    /// adjustment, produced by the ordinary excess rule.</para>
    /// </remarks>
    public static MatchNetwork WithEndSplits(
        MatchNetwork network, MatchSynthesisResult result, MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(design);

        var net = network.Clone();
        bool lowpass = design.Form == NetworkForm.Lowpass;
        double wRef = 2.0 * Math.PI * (lowpass ? design.F2 : design.F1);
        if (!(wRef > 0.0)) return net;

        // Far end first: inserting at the analysis end shifts no far-end index, but not vice versa.
        foreach (int end in (ReadOnlySpan<int>)
                 [result.AnalysisIsTerm1 ? 2 : 1, result.AnalysisIsTerm1 ? 1 : 2])
        {
            Termination term = end == 1 ? design.Term1 : design.Term2;
            if (!term.HasReactance) continue;

            int idx = net.Elements.FindIndex(e => e.AbsorbedEnd == end);
            if (idx < 0) continue;

            var absorbed = net.Elements[idx];
            double rEnd = end == 1 ? net.R1 : net.R2;
            double gSynth = ToPrototype(absorbed.Type, absorbed.IsShunt, absorbed.Value, rEnd, wRef);
            double gActual = ToPrototype(absorbed.Type, absorbed.IsShunt, term.Value, rEnd, wRef);
            if (!(gActual > 0) || gSynth / gActual <= MatchSynthesis.ExcessRatioThreshold) continue;

            double gAdded = gSynth - gActual;
            if (!(gAdded > 0)) continue;

            absorbed.Value = term.Value;
            net.Elements.Insert(end == 1 ? idx : idx + 1, new MatchElement
            {
                Name = (absorbed.Type == ElementType.C ? "CExcess" : "LExcess") + end,
                Type = absorbed.Type,
                IsShunt = absorbed.IsShunt,
                Value = FromPrototype(absorbed.Type, absorbed.IsShunt, gAdded, rEnd, wRef),
                IsExcess = true,
            });
        }

        return net;
    }

    // ── Absorption bookkeeping ────────────────────────────────────────────────

    /// <summary>
    /// The element kind this form puts at an end of the given orientation: a lowpass ladder is series
    /// L and shunt C, a highpass ladder series C and shunt L.
    /// </summary>
    private static (TerminationTopology Topology, ReactanceKind Kind) Holds(bool lowpass, bool isShunt)
        => isShunt
            ? (TerminationTopology.Parallel, lowpass ? ReactanceKind.C : ReactanceKind.L)
            : (TerminationTopology.Series, lowpass ? ReactanceKind.L : ReactanceKind.C);

    private static MatchRefusal? CheckAbsorbable(
        MatchDesign design, Termination term, int end, bool isShunt, bool lowpass, double ratio)
    {
        if (!term.HasReactance) return null;
        var (topology, kind) = Holds(lowpass, isShunt);
        if (term.Topology == topology && term.Kind == kind) return null;

        string has = Topology(term) + (term.Kind == ReactanceKind.C ? " capacitance" : " inductance");
        string wants = (isShunt ? "shunt " : "series ") + (kind == ReactanceKind.C ? "capacitor" : "inductor");

        // The form holds this kind SOMEWHERE — just at the other end, because the low-impedance port
        // always takes the series element and the high-impedance port the shunt one (see the class
        // remark). That is the ratio constraint, and only bandpass form is free of it.
        var (otherTopology, otherKind) = Holds(lowpass, !isShunt);
        bool otherEndHolds = term.Topology == otherTopology && term.Kind == otherKind;

        string why = otherEndHolds
            ? $"but at a ratio of {ratio:0.####} : 1 that end of a {Word(design.Form)} ladder is its "
              + $"{wants}: the LOW-impedance port always takes the series element and the "
              + "high-impedance port the shunt one. Bandpass form carries both at every arm."
            : $"and a {Word(design.Form)} network is {(lowpass ? "series inductors and shunt capacitors" : "series capacitors and shunt inductors")}, "
              + $"so it has nothing to absorb it into. Use {(lowpass ? "highpass" : "lowpass")} or bandpass form.";

        return MatchRefusal.Create(
            MatchRefusalKind.FormCannotAbsorb,
            $"Termination {end} is a {has}, {why}",
            end, ("ratio", ratio), ("end", end));
    }

    /// <summary>The termination's own reactance in prototype units at the end it sits on.</summary>
    private static double Normalise(
        NetworkForm form, Termination term, bool isShunt, double r, double wRef)
    {
        if (!term.HasReactance) return 0.0;
        var type = term.Kind == ReactanceKind.C ? ElementType.C : ElementType.L;
        return ToPrototype(type, isShunt, term.Value, r, wRef);
    }

    /// <summary>A physical element value as the prototype g it stands for.</summary>
    private static double ToPrototype(ElementType type, bool isShunt, double value, double r, double w)
        => (type, isShunt) switch
        {
            (ElementType.C, true) => w * r * value,          // shunt C, lowpass
            (ElementType.L, false) => w * value / r,         // series L, lowpass
            (ElementType.L, true) => r / (w * value),        // shunt L, highpass
            _ => 1.0 / (w * r * value),                      // series C, highpass
        };

    /// <summary>The inverse of <see cref="ToPrototype"/>.</summary>
    private static double FromPrototype(ElementType type, bool isShunt, double g, double r, double w)
        => (type, isShunt) switch
        {
            (ElementType.C, true) => g / (w * r),
            (ElementType.L, false) => g * r / w,
            (ElementType.L, true) => r / (w * g),
            _ => 1.0 / (w * r * g),
        };

    // ── Build ─────────────────────────────────────────────────────────────────

    private static MatchNetwork Build(
        MatchDesign design, double[] g, int n, bool shuntFirst,
        double rAna, double rFar, bool anaIsTerm1, Termination ana, Termination far)
    {
        bool lowpass = design.Form == NetworkForm.Lowpass;
        double wRef = 2.0 * Math.PI * (lowpass ? design.F2 : design.F1);
        int m = 2 * n;

        // In ANALYSIS-END order first: element 0 sits against the analysis port.
        var built = new List<(bool IsShunt, ElementType Type, double Value)>(m);
        for (int k = 0; k < m; k++)
        {
            bool isShunt = shuntFirst == (k % 2 == 0);
            var type = isShunt
                ? (lowpass ? ElementType.C : ElementType.L)
                : (lowpass ? ElementType.L : ElementType.C);
            built.Add((isShunt, type, FromPrototype(type, isShunt, g[k], rAna, wRef)));
        }

        int anaEnd = anaIsTerm1 ? 1 : 2, farEnd = anaIsTerm1 ? 2 : 1;
        var absorbedBy = new int[m];
        if (ana.HasReactance) absorbedBy[0] = anaEnd;
        if (far.HasReactance) absorbedBy[m - 1] = farEnd;

        // Reversal is BY ELEMENT — there are no two-element arms to keep together, which is the whole
        // difference from the bandpass Build.
        var order = Enumerable.Range(0, m).ToList();
        if (!anaIsTerm1) order.Reverse();

        var net = new MatchNetwork
        {
            R1 = anaIsTerm1 ? rAna : rFar,
            R2 = anaIsTerm1 ? rFar : rAna,
        };
        int nl = 0, nc = 0;
        foreach (int i in order)
        {
            var e = built[i];
            net.Elements.Add(new MatchElement
            {
                Name = e.Type == ElementType.L ? $"L{++nl}" : $"C{++nc}",
                Type = e.Type,
                IsShunt = e.IsShunt,
                Value = e.Value,
                ArmIndex = net.Elements.Count,
                AbsorbedEnd = absorbedBy[i],
            });
        }
        return net;
    }

    private static string Topology(Termination t) =>
        t.Topology == TerminationTopology.Parallel ? "parallel" : "series";

    private static string Word(NetworkForm form) => form switch
    {
        NetworkForm.Lowpass => "lowpass",
        NetworkForm.Highpass => "highpass",
        _ => "bandpass",
    };
}
