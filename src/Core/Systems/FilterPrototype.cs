using System.Numerics;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>The prototype response families an ideal <c>Filter</c> can take (brief-sys-6).</summary>
public enum FilterResponse
{
    /// <summary>Maximally flat in magnitude. No shape parameter — <c>|S21|² = 1/(1 + Ω^{2n})</c>.</summary>
    Butterworth,

    /// <summary>Equiripple passband, monotone stopband. Reads <c>Ripple</c>.</summary>
    Chebyshev,

    /// <summary>Maximally flat passband, equiripple stopband. Reads <c>Astop</c>.</summary>
    InvChebyshev,

    /// <summary>Maximally flat GROUP DELAY. No shape parameter, and no magnitude claim at all.</summary>
    Bessel,

    /// <summary>Equiripple in both bands. Reads <c>Ripple</c> and <c>Astop</c>.</summary>
    Elliptic,
}

/// <summary>
/// One lowpass prototype response, as the three real polynomials the scattering matrix of a
/// lossless reciprocal two-port is made of. Pure arithmetic: it knows nothing about frequency,
/// impedance, insertion loss or the netlist, and it is what
/// <see cref="CircuitRF.Core.Devices.FilterModel"/> and <c>DuplexerModel</c> both evaluate.
///
/// <para><b>Why a transfer function and not an LC ladder.</b> The obvious implementation —
/// synthesise a doubly-terminated ladder from g-values and stamp the elements, exactly as
/// <c>MatchModel</c> does — cannot take an arbitrary source/load impedance ratio: the termination
/// ratio is fixed by the family and the order, so <c>Zin</c> and <c>Zout</c> would become a
/// constrained pair with refusals attached. Stamped as an S-matrix instead, the reference
/// impedances are simply what S is DEFINED against, every pair works, and the block is a lossless
/// impedance transformer in the bargain. That is the Match component's territory and this is not
/// it; the two share polynomial helpers and nothing else.</para>
///
/// <para><b>The form.</b> With <c>s</c> the prototype variable and <c>Ω</c> real:</para>
/// <code>
///   S11(jΩ) = α·F(jΩ)/E(jΩ)      S21(jΩ) = S12(jΩ) = β·P(jΩ)/E(jΩ)
///   S22(jΩ) = −α·F(−jΩ)/E(jΩ)
/// </code>
/// <para><c>E</c> is strictly Hurwitz and monic (which is what makes the response CAUSAL — a
/// magnitude-only response has no phase, no group delay, and would make the Bessel option
/// meaningless); <c>P</c> carries the transmission zeros and is <c>[1]</c> for a family whose zeros
/// are all at infinity; <c>F</c> carries the reflection zeros. <c>α</c> and <c>β</c> are positive
/// reals fixed by the family's own <c>ε</c>.</para>
///
/// <para><b>The <c>S22</c> row is derived, not asserted.</b> Losslessness plus reciprocity gives
/// <c>S11·conj(S21) + S21·conj(S22) = 0</c>, hence <c>S22 = −conj(S11)·S21/conj(S21)</c>; with real
/// coefficients <c>conj(X(jΩ)) = X(−jΩ)</c>, and with <c>P</c> even (true of every family here) the
/// <c>P</c> factors cancel and what is left is the line above. Writing <c>S22 = ±S11</c> instead
/// would be a parity assumption that holds for the all-pole families and fails on nothing until it
/// does.</para>
///
/// <para><b>Which realisation.</b> <c>F</c>'s overall sign is a free choice — it selects between a
/// network and its dual, both lossless, both reciprocal, both with exactly the stated magnitudes.
/// It is pinned to a positive leading coefficient so a highpass at DC is an OPEN at port 1 (the
/// series-first ladder) rather than a short, which is the answer a reader expects and the one the
/// limits gate asserts.</para>
/// </summary>
public sealed class FilterPrototype
{
    private FilterPrototype(FilterResponse response, int order,
                            double[] e, double[] f, double[] p, double alpha, double beta)
    {
        Response = response;
        Order    = order;
        E = e; F = f; P = p;
        Alpha = alpha; Beta = beta;
    }

    /// <summary>The family this was built from.</summary>
    public FilterResponse Response { get; }

    /// <summary>The PROTOTYPE order — see <see cref="FilterNetwork"/> for why a bandpass is twice it.</summary>
    public int Order { get; }

    /// <summary>The strictly-Hurwitz, monic denominator, descending.</summary>
    public double[] E { get; }

    /// <summary>The reflection numerator, descending.</summary>
    public double[] F { get; }

    /// <summary>The transmission numerator, descending; <c>[1]</c> when every zero is at infinity.</summary>
    public double[] P { get; }

    /// <summary>The scalar on <c>F/E</c>.</summary>
    public double Alpha { get; }

    /// <summary>The scalar on <c>P/E</c>.</summary>
    public double Beta { get; }

    /// <summary>
    /// The prototype's S at a real prototype frequency <paramref name="omega"/> (the transformed
    /// variable, not the user's frequency).
    /// </summary>
    public (Complex S11, Complex S21, Complex S22) At(double omega)
    {
        if (double.IsInfinity(omega)) return AtInfinity();

        var jw = new Complex(0.0, omega);
        Complex e = MatchPoly.Eval(E, jw);
        return (Alpha * MatchPoly.Eval(F,  jw) / e,
                Beta  * MatchPoly.Eval(P,  jw) / e,
               -Alpha * MatchPoly.Eval(F, -jw) / e);
    }

    /// <summary>
    /// The <c>Ω → ±∞</c> limit, computed from leading coefficients rather than evaluated at a large
    /// number.
    ///
    /// <para><b>It is reached by ordinary use, not by an edge case hunt.</b> A highpass at DC is
    /// <c>Ω = −ω_c/ω → −∞</c> and a bandpass at DC likewise; those are the frequencies an
    /// S-parameter sweep starts at and the DC operating point every HB run solves first. Evaluating
    /// the ratio at 1e300 instead overflows both polynomials and returns NaN.</para>
    ///
    /// <para>The sign of the infinity does not appear: where a limit is non-zero the degrees are
    /// equal, so the <c>(jΩ)^d</c> factors cancel exactly.</para>
    /// </summary>
    public (Complex S11, Complex S21, Complex S22) AtInfinity()
    {
        int dE = E.Length - 1, dF = F.Length - 1, dP = P.Length - 1;

        Complex s11 = dF == dE ? new Complex(Alpha * F[0] / E[0], 0.0) : Complex.Zero;
        Complex s21 = dP == dE ? new Complex(Beta  * P[0] / E[0], 0.0) : Complex.Zero;
        // The leading term of F(−jΩ) is F[0]·(−1)^dF·(jΩ)^dF.
        Complex s22 = dF == dE
            ? new Complex(-Alpha * F[0] * (dF % 2 == 0 ? 1.0 : -1.0) / E[0], 0.0)
            : Complex.Zero;
        return (s11, s21, s22);
    }

    // ── construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the prototype for one family.
    ///
    /// <para><b>A parameter the family does not read is IGNORED, not refused.</b> A user switching
    /// Chebyshev to Butterworth should not have to clear a ripple field first, so
    /// <paramref name="rippleDb"/> reaches Butterworth and Bessel and is simply not looked at.</para>
    /// </summary>
    /// <param name="response">The family.</param>
    /// <param name="order">Prototype order, ≥ 1.</param>
    /// <param name="rippleDb">Passband ripple, dB. Read by Chebyshev and Elliptic.</param>
    /// <param name="astopDb">Stopband floor, dB. Read by inverse Chebyshev and Elliptic.</param>
    public static FilterPrototype Create(FilterResponse response, int order,
                                         double rippleDb = 0.1, double astopDb = 40.0)
    {
        if (order < 1)
            throw new ArgumentOutOfRangeException(nameof(order),
                $"A filter order is at least 1; got {order}.");

        return response switch
        {
            FilterResponse.Butterworth  => FromCharacteristic(response, order, [1.0], Monomial(order), 1.0),
            FilterResponse.Chebyshev    => FromCharacteristic(response, order, [1.0],
                                               MatchPoly.ChebyshevT(order), EpsFromDb(rippleDb, nameof(rippleDb))),
            FilterResponse.InvChebyshev => FromCharacteristic(response, order,
                                               InverseChebyshevDenominator(order), Monomial(order),
                                               EpsFromDb(astopDb, nameof(astopDb))),
            FilterResponse.Bessel       => BuildBessel(order),
            FilterResponse.Elliptic     => EllipticPrototype.Build(order, rippleDb, astopDb),
            _ => throw new ArgumentOutOfRangeException(nameof(response), response, "unknown response family"),
        };
    }

    /// <summary>
    /// <c>ε = sqrt(10^(db/10) − 1)</c> — the same arithmetic for a passband ripple and for a
    /// stopband floor, because both name the depth of an equiripple excursion measured in dB.
    /// </summary>
    internal static double EpsFromDb(double db, string what)
    {
        if (!(db > 0.0) || !double.IsFinite(db))
            throw new ArgumentOutOfRangeException(what,
                $"A ripple or stopband figure is a positive number of dB; got {db:G6}.");
        return Math.Sqrt(Math.Pow(10.0, db / 10.0) - 1.0);
    }

    /// <summary>
    /// The families defined by a characteristic function <c>C_n(Ω) = N(Ω)/D(Ω)</c>, which is all of
    /// them except Bessel:
    /// <code>
    ///   |S21|² = D²/(D² + ε²N²)      |S11|² = ε²N²/(D² + ε²N²)
    /// </code>
    /// <para>Both numerators and the shared denominator are rewritten from Ω to s by
    /// <see cref="ToS"/>, the denominator is spectrally factored by <see cref="MatchPrototypes.Hurwitz"/>,
    /// and the two scalars fall out of one number: the ratio between the denominator's own leading
    /// coefficient and that of <c>E(s)E(−s)</c>.</para>
    /// </summary>
    internal static FilterPrototype FromCharacteristic(
        FilterResponse response, int order, double[] dOmega, double[] nOmega, double eps)
    {
        // G(Ω) = D² + ε²N², the squared magnitude of the denominator. Even in Ω, because D and N
        // each have uniform parity and are squared.
        double[] g = MatchPoly.Add(MatchPoly.Mul(dOmega, dOmega),
                                   Scale(MatchPoly.Mul(nOmega, nOmega), eps * eps));

        double[] gs = ToS(g);
        double[] e  = MatchPoly.Trim(MatchPrototypes.Hurwitz(gs));
        double[] f  = NormaliseLeading(ToS(nOmega));
        double[] p  = NormaliseTrailing(ToS(dOmega));

        double lambda = SpectralScale(gs, e);
        double beta   = 1.0 / Math.Sqrt(lambda);
        return new FilterPrototype(response, order, e, f, p, eps * beta, beta);
    }

    /// <summary>
    /// Bessel, which is <b>not</b> of the characteristic-function form and must not be forced into
    /// one: <c>S21(s) = θ_n(0)/θ_n(s)</c> from the reverse Bessel polynomial, normalised for unit
    /// group delay at DC.
    ///
    /// <para>Its <c>|S21|</c> is neither equiripple nor maximally flat in MAGNITUDE — it is
    /// maximally flat in group delay, which is the only reason to choose it, and which is what its
    /// gate measures. Stating a ripple or a stopband floor for it would be describing a different
    /// filter.</para>
    ///
    /// <para><b>Its <c>F</c> is the one that needs a factorisation of its own.</b> There is no
    /// <c>N(Ω)</c> to rewrite, so <c>F</c> comes from Feldtkeller directly:
    /// <c>F(s)F(−s) ∝ E(s)E(−s) − β²</c>. That polynomial is even and vanishes to second order at
    /// <c>s = 0</c> — <c>|S11|</c> is zero at DC and the zero is double, as it must be for a
    /// spectral density — so the two factors of <c>s</c> are divided out EXACTLY (by dropping two
    /// coefficients) rather than left for a root finder to discover approximately. What remains has
    /// no roots on the imaginary axis at all, which is the case <see cref="MatchPrototypes.Hurwitz"/>
    /// handles cleanly.</para>
    /// </summary>
    private static FilterPrototype BuildBessel(int order)
    {
        // MatchPoly.ReverseBessel is normalised to θ(0) = 1, which is exactly the unit-delay-at-DC
        // normalisation; rescaling it monic below cancels out of S21 = β·1/E and changes nothing.
        double[] theta = MatchPoly.ReverseBessel(order);
        double[] e     = Scale(theta, 1.0 / theta[0]);
        double   beta  = e[^1];                                   // E(0), so S21(0) = 1 exactly

        double[] ee = MatchPoly.Mul(e, Reflect(e));               // = |E(jΩ)|² on the axis
        double[] qf = (double[])ee.Clone();
        qf[^1] -= beta * beta;                                    // − β², the Feldtkeller step

        // qf is even with a double root at the origin: its constant term is zero by construction and
        // its s¹ term is zero by evenness. Drop both and factor what is left.
        double[] r  = qf.Length > 2 ? qf[..^2] : [qf[0]];
        double[] rh = r.Length <= 1 ? [1.0] : MatchPoly.Trim(MatchPrototypes.Hurwitz(r));
        double[] f  = NormaliseLeading([.. rh, 0.0]);             // × s

        double lambdaF = qf[0] / MatchPoly.Mul(f, Reflect(f))[0];
        return new FilterPrototype(FilterResponse.Bessel, order, e, f, [1.0],
                                   Math.Sqrt(lambdaF), beta);
    }

    /// <summary>
    /// The ratio <c>λ</c> in <c>Q(s) = λ·E(s)·E(−s)</c>, from the two leading coefficients.
    /// <c>α</c> and <c>β</c> are both <c>1/sqrt(λ)</c> times their own factor, so getting this
    /// wrong scales the whole response rather than distorting it — which is why it is computed
    /// rather than reasoned about per family.
    /// </summary>
    private static double SpectralScale(double[] q, double[] e)
    {
        double lambda = q[0] / MatchPoly.Mul(e, Reflect(e))[0];
        if (!(lambda > 0.0) || !double.IsFinite(lambda))
            throw new InvalidOperationException(
                $"Spectral factorisation produced a non-positive scale ({lambda:G6}); the response " +
                "polynomial is not a valid squared magnitude.");
        return lambda;
    }

    // ── polynomial plumbing ───────────────────────────────────────────────────

    /// <summary>Ω^n, descending.</summary>
    internal static double[] Monomial(int n)
    {
        var a = new double[n + 1];
        a[0] = 1.0;
        return a;
    }

    /// <summary>
    /// The inverse-Chebyshev denominator <c>U(Ω) = Ω^n·T_n(1/Ω)</c>, which is <c>T_n</c>'s own
    /// coefficient array REVERSED — and whose roots are the filter's transmission zeros,
    /// <c>Ω_k = 1/cos((2k−1)π/2n)</c>, sitting in the stopband on the jΩ axis where the family's
    /// whole character comes from.
    ///
    /// <para>At odd <paramref name="n"/>, <c>T_n</c> has no constant term, so the reversal has a
    /// leading zero and <c>U</c> is genuinely of degree <c>n−1</c>: one transmission zero has gone
    /// to infinity, which is why an odd-order inverse Chebyshev keeps rolling off in the far
    /// stopband and an even-order one does not. <see cref="MatchPoly.Trim"/> is what makes that a
    /// degree rather than a leading zero — and it is a RELATIVE trim, which is the trap
    /// <see cref="MatchPoly.Trim"/>'s own remark was written for.</para>
    /// </summary>
    internal static double[] InverseChebyshevDenominator(int n)
    {
        double[] t = MatchPoly.ChebyshevT(n);
        var u = new double[t.Length];
        for (int i = 0; i < t.Length; i++) u[i] = t[t.Length - 1 - i];
        return MatchPoly.Trim(u);
    }

    /// <summary>
    /// Rewrites a real polynomial in the real frequency Ω as a real polynomial in <c>s</c> such that
    /// evaluating it at <c>s = jΩ</c> returns the original value, up to a unimodular constant.
    ///
    /// <para>Substituting <c>Ω = s/j</c> multiplies the term of power <c>k</c> by <c>(−j)^k</c>,
    /// which is real for even <c>k</c> and imaginary for odd <c>k</c>. Every polynomial this is
    /// asked for has UNIFORM parity — <c>Ω^n</c>, <c>T_n</c>, the inverse-Chebyshev denominator, the
    /// elliptic rational function's two halves, and any even sum of squares — so one factor of
    /// <c>j</c> on the odd ones makes the whole array real at once. A mixed-parity input is a bug
    /// upstream and says so rather than silently dropping an imaginary part.</para>
    /// </summary>
    internal static double[] ToS(double[] inOmega)
    {
        double[] a = MatchPoly.Trim(inOmega);
        int d = a.Length - 1;

        int parity = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == 0.0) continue;
            int k = (d - i) & 1;
            if (parity < 0) parity = k;
            else if (parity != k)
                throw new InvalidOperationException(
                    "A response polynomial in Ω has mixed parity; it cannot be rewritten in s as a " +
                    "real polynomial. Every characteristic function this file builds is even or odd.");
        }
        if (parity < 0) return [0.0];

        var outS = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int k = d - i;
            // (−j)^k, times j when k is odd so the whole array comes out real.
            int half = parity == 0 ? k / 2 : (k - 1) / 2;
            outS[i] = a[i] * (half % 2 == 0 ? 1.0 : -1.0);
        }
        return outS;
    }

    /// <summary><c>p(−s)</c>: the odd powers change sign.</summary>
    internal static double[] Reflect(double[] p)
    {
        var r = new double[p.Length];
        int d = p.Length - 1;
        for (int i = 0; i < p.Length; i++) r[i] = ((d - i) % 2 == 0) ? p[i] : -p[i];
        return r;
    }

    private static double[] Scale(double[] a, double k) => [.. a.Select(x => x * k)];

    /// <summary>
    /// Pins the overall sign of <c>F</c>. See the class remarks: the sign selects a network or its
    /// dual, and a positive leading coefficient is the one that makes a highpass an OPEN at DC.
    /// </summary>
    private static double[] NormaliseLeading(double[] a) => a[0] < 0.0 ? Scale(a, -1.0) : a;

    /// <summary>
    /// Pins the overall sign of <c>P</c> on its LOWEST-order term rather than its leading one, so a
    /// lowpass at DC is <c>S21 = +1</c> — an ideal through — rather than a through with a sign flip
    /// in it. The two ends of <c>P</c> can disagree in sign, so which one is chosen matters.
    /// </summary>
    private static double[] NormaliseTrailing(double[] a)
    {
        for (int i = a.Length - 1; i >= 0; i--)
        {
            if (a[i] == 0.0) continue;
            return a[i] < 0.0 ? Scale(a, -1.0) : a;
        }
        return a;
    }
}
