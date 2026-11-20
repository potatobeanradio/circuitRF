using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Matching;
using CircuitRF.Diagnostics;
using RfCore.Stability;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// A generalised Chebyshev filtering function's characteristic polynomials in the normalised lowpass domain, as
/// descending complex coefficients in s: S11 = F/(εR·E) and S21 = P/(ε·E). E and F are monic of degree N; P is monic
/// over its finite zeros, times j when N − nfz is even, so the matrix it synthesises is real (R. J. Cameron,
/// <a href="https://doi.org/10.1109/22.754877">10.1109/22.754877</a>).
/// </summary>
public sealed record ChebyshevPolynomials(Complex[] E, Complex[] F, Complex[] P, double Epsilon, double EpsilonR)
{
    public int Order => E.Length - 1;

    /// <summary>S11 and S21 at the normalised frequency ω, s = jω.</summary>
    public (Complex S11, Complex S21) Response(double omega)
    {
        var s = new Complex(0.0, omega);
        Complex e = MatchPoly.EvalComplex(E, s);
        return (MatchPoly.EvalComplex(F, s) / (EpsilonR * e), MatchPoly.EvalComplex(P, s) / (Epsilon * e));
    }
}

/// <summary>
/// A coupling matrix in physical terms at a centre f0 and fractional bandwidth FBW (J.-S. Hong and M. J. Lancaster,
/// <i>Microstrip Filters for RF/Microwave Applications</i>, Wiley, 2001, §8.1,
/// <a href="https://doi.org/10.1002/0471221619">10.1002/0471221619</a>): the external Q at each port,
/// Qe = 1/(FBW·M²); the coupling coefficients between resonators, k_ij = FBW·M_ij (zero diagonal); and each
/// resonator's own frequency, from its self-coupling M_ii = (f0/f_i − f_i/f0)/FBW.
/// </summary>
public sealed record CoupledResonators(double QeSource, double QeLoad, double[,] K, double[] ResonantHz);

/// <summary>
/// The N+2 coupling matrix of a generalised Chebyshev filter (docs/design/filter-synthesis.md §2), indexed S, 1 … N,
/// L: the characteristic polynomials by Cameron's recursion, the transversal matrix from the admittance residues,
/// and the rotations to the folded canonical form (R. J. Cameron, <a href="https://doi.org/10.1109/22.754877">
/// 10.1109/22.754877</a> and <a href="https://doi.org/10.1109/TMTT.2002.806937">10.1109/TMTT.2002.806937</a>).
/// The pipeline is after the Standard Coupling Matrix Synthesis Code (MIT,
/// github.com/Yellowbooker/Standard-Coupling-Matrix-Synthesis-Code), reimplemented in the ω domain so that every
/// polynomial but E stays real.
/// </summary>
public static class CouplingMatrix
{
    /// <summary>The highest order the tests hold: rooting the polynomials loses digits as the order grows.</summary>
    public const int MaxOrder = 10;

    /// <summary>
    /// F, P, E and ε for an order-N equiripple passband at <paramref name="returnLossDb"/>, with the finite
    /// transmission zeros <paramref name="zeros"/> (normalised ω, each outside the passband |ω| ≤ 1; the rest at
    /// infinity). All N zeros finite is the fully canonical case, with εR &gt; 1 and a source–load coupling.
    /// </summary>
    public static ChebyshevPolynomials Characteristic(int order, double returnLossDb, IReadOnlyList<double>? zeros = null)
    {
        zeros ??= [];
        if (order < 1 || order > MaxOrder)
            Refuse("order", "Coupling matrix: order {n} is outside 1 to {max}.", ("n", order), ("max", MaxOrder));
        if (!(returnLossDb > 0)) Refuse("return-loss", "Coupling matrix: return loss {rl} dB must be positive.", ("rl", returnLossDb));
        if (zeros.Count > order)
            Refuse("too-many-zeros", "Coupling matrix: {z} transmission zeros, but an order-{n} filter has at most {n}.",
                ("z", zeros.Count), ("n", order));
        foreach (double z in zeros)
            if (!(Math.Abs(z) > 1.0) || !double.IsFinite(z))
                Refuse("zero-in-band", "Coupling matrix: a transmission zero at ω = {z} must lie outside the passband, |ω| > 1.", ("z", z));
        int nfz = zeros.Count;

        // Cameron's recursion: C_N(ω) = cosh Σ arccosh x_n with x_n = (ω − 1/ω_n)/(1 − ω/ω_n), built as U + ω'·V,
        // ω' = √(ω² − 1); the numerator of C_N is U, the reflection zeros. A zero at infinity has 1/ω_n = 0.
        double Inv(int k) => k < nfz ? 1.0 / zeros[k] : 0.0;
        double Root(int k) => Math.Sqrt(1.0 - Inv(k) * Inv(k));
        double[] u = [1.0, -Inv(0)], v = [Root(0)];
        for (int k = 1; k < order; k++)
            (u, v) = (MatchPoly.Add(MatchPoly.Mul(u, [1.0, -Inv(k)]), MatchPoly.Mul([Root(k), 0.0, -Root(k)], v)),
                      MatchPoly.Add(MatchPoly.Mul(v, [1.0, -Inv(k)]), MatchPoly.Mul(u, [Root(k)])));
        double[] fw = [.. u.Select(c => c / u[0])];
        double[] pw = MatchPoly.FromRoots(zeros.Select(z => new Complex(z, 0.0)));

        // ε sets the ripple at ω = 1; εR departs from 1 only when every zero is finite.
        double eps = (MatchPoly.Eval(pw, 1.0) / MatchPoly.Eval(fw, 1.0)).Magnitude / Math.Sqrt(Math.Pow(10, returnLossDb / 10) - 1);
        double epsR = nfz == order ? eps / Math.Sqrt(eps * eps - 1) : 1.0;

        // |E|² = |F/εR|² + |P/ε|²: the roots of F/εR + jP/ε and their conjugates, those in the upper ω half-plane
        // (the left half of s).
        var q = new Complex[order + 1];
        for (int i = 0; i <= order; i++) q[i] = fw[i] / epsR;
        for (int i = 0; i <= nfz; i++) q[order - nfz + i] += new Complex(0.0, pw[i] / eps);
        Complex[] ew = FromRoots(MatchPoly.Roots(q).Select(r => r.Imaginary > 0 ? r : Complex.Conjugate(r)));

        Complex[] p = ToS(pw);
        if ((order - nfz) % 2 == 0) p = [.. p.Select(c => c * Complex.ImaginaryOne)];
        return new ChebyshevPolynomials(ToS(ew), ToS(fw), p, eps, epsR);
    }

    /// <summary>
    /// The N+2 transversal matrix: each resonator k on its own, self-coupled by −λ_k and coupled to the source and the
    /// load by M_Sk = r21k/√r22k and M_Lk = √r22k, from the residues of y21 and y22 at their poles jλ_k; M_SL carries
    /// the direct term of a fully canonical y21.
    /// </summary>
    public static double[,] Transversal(ChebyshevPolynomials poly)
    {
        int n = poly.Order;
        // On s = jω, with E and F back in ω (monic): y22 = j·Im Q/Re Q and y21 = −P(jω)/(ε·jᴺ·Re Q), where
        // Q = E + F/εR and Re, Im take the coefficients' parts. The poles are the real roots of Re Q. A synthesised F is
        // real in ω; an extracted one (FilterExtraction), whose reflection zeros leave the axis, is not.
        // ponytail: residues from the coefficients. Poles a few 1e-4 apart near the band edge (8–2 or 10–4 with zeros
        // close to it) hold the response to about 1e-8 and 3e-7, against 1e-13 with the poles apart; extended
        // precision here if that ever matters.
        Complex[] ew = ToOmega(poly.E), fw = ToOmega(poly.F);
        double[] re = new double[n + 1], im = new double[n + 1];
        for (int i = 0; i <= n; i++)
            (re[i], im[i]) = (ew[i].Real + fw[i].Real / poly.EpsilonR, ew[i].Imaginary + fw[i].Imaginary / poly.EpsilonR);
        double[] dre = [.. re[..^1].Select((c, i) => c * (n - i))];

        var m = new double[n + 2, n + 2];
        double[] lambda = [.. MatchPoly.Roots(re).Select(r => r.Real).Order()];
        for (int k = 0; k < n; k++)
        {
            double l = lambda[k], d = MatchPoly.Eval(dre, l).Real;
            double r22 = -MatchPoly.Eval(im, l).Real / d;
            double r21 = (-MatchPoly.EvalComplex(poly.P, new Complex(0.0, l)) / (poly.Epsilon * JPow(n - 1) * d)).Real;
            m[k + 1, k + 1] = -l;
            m[n + 1, k + 1] = m[k + 1, n + 1] = Math.Sqrt(r22);
            m[0, k + 1] = m[k + 1, 0] = r21 / Math.Sqrt(r22);
        }
        // y21 → j·M_SL as s → ∞, which only a P of full degree reaches.
        if (poly.P.Length == poly.E.Length)
            m[0, n + 1] = m[n + 1, 0] = (-poly.P[0] / (poly.Epsilon * (1.0 + 1.0 / poly.EpsilonR))).Imaginary;
        return m;
    }

    /// <summary>
    /// The folded canonical form of an N+2 matrix: plane rotations about resonator pivots that clear the source row
    /// from the far end inwards, then the load column, alternating towards the centre, leaving the main line, the
    /// cross-diagonal couplings and M_SL (Cameron 2003, <a href="https://doi.org/10.1109/TMTT.2002.806937">
    /// 10.1109/TMTT.2002.806937</a>, Table II).
    /// </summary>
    public static double[,] Folded(double[,] transversal)
    {
        var m = (double[,])transversal.Clone();
        int n = m.GetLength(0);
        for (int i = 1; i <= n - 3; i++)
            for (int j = 1; j <= n - 2 - i; j++)
            {
                if (i % 2 == 1)
                {
                    // Row r: clear M[r, c] against M[r, c − 1], pivot [c − 1, c].
                    int r = (i - 1) / 2, c = n - (i + 1) / 2 - j;
                    Rotate(m, c - 1, c, -m[r, c], m[r, c - 1]);
                }
                else
                {
                    // Column c: clear M[k, c] against M[k + 1, c], pivot [k, k + 1].
                    int c = n - i / 2, k = i / 2 + j;
                    Rotate(m, k, k + 1, m[k, c], m[k + 1, c]);
                }
            }
        return m;
    }

    /// <summary>The folded N+2 matrix of the generalised Chebyshev filter: <see cref="Characteristic"/>, then
    /// <see cref="Transversal"/>, then <see cref="Folded"/>.</summary>
    public static double[,] Synthesise(int order, double returnLossDb, IReadOnlyList<double>? zeros = null)
        => Folded(Transversal(Characteristic(order, returnLossDb, zeros)));

    /// <summary>
    /// The inline all-pole matrix of a lowpass prototype g0 … g(N+1): M(i, i+1) = 1/√(g_i·g(i+1)), source and load
    /// included (Atia and Williams, <a href="https://doi.org/10.1109/TMTT.1972.1127732">10.1109/TMTT.1972.1127732</a>).
    /// </summary>
    public static double[,] AllPole(IReadOnlyList<double> g)
    {
        int n = g.Count;
        var m = new double[n, n];
        for (int i = 0; i + 1 < n; i++) m[i, i + 1] = m[i + 1, i] = 1.0 / Math.Sqrt(g[i] * g[i + 1]);
        return m;
    }

    /// <summary>
    /// S11 and S21 of an N+2 matrix at the normalised frequency ω, from [A] = [G + (s + δ)·U + jM] with unit
    /// terminations G at S and L and U the resonator identity: S21 = 2·A⁻¹(L, S) and S11 = 2·A⁻¹(S, S) − 1. A finite
    /// unloaded Q adds δ = 1/(Qu·FBW) to every resonator (Cameron, Kudsia and Mansour, <i>Microwave Filters for
    /// Communication Systems</i>, 2nd ed., Wiley, 2018, <a href="https://doi.org/10.1002/9781119292371">
    /// 10.1002/9781119292371</a>).
    /// </summary>
    public static (Complex S11, Complex S21) Response(double[,] m, double omega,
        double unloadedQ = double.PositiveInfinity, double fractionalBandwidth = 1.0)
    {
        int n = m.GetLength(0);
        var a = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] = new Complex(0.0, m[i, j]);
        var s = new Complex(1.0 / (unloadedQ * fractionalBandwidth), omega);
        for (int k = 1; k < n - 1; k++) a[k, k] += s;
        a[0, 0] += 1.0;
        a[n - 1, n - 1] += 1.0;
        var e = new Complex[n];
        e[0] = 1.0;
        Complex[] x = new WspMatrix.Lu(a).Solve(e);
        return (2.0 * x[0] - 1.0, 2.0 * x[n - 1]);
    }

    /// <summary>The matrix at a centre and fractional bandwidth as external Qs, coupling coefficients and resonant
    /// frequencies. The source must couple only to resonator 1 and the load only to resonator N.</summary>
    public static CoupledResonators ToPhysical(double[,] m, double centreHz, double fractionalBandwidth)
    {
        CheckBand(centreHz, fractionalBandwidth);
        int n = m.GetLength(0) - 2;
        for (int k = 2; k <= n + 1; k++)
            if (Math.Abs(m[0, k]) > 1e-9 || Math.Abs(m[k - 2, n + 1]) > 1e-9)
                Refuse("port-cross-coupling",
                    "Coupling matrix: the source or load couples past its end resonator, which an external Q cannot carry.");
        var k2 = new double[n, n];
        var f = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) if (i != j) k2[i, j] = fractionalBandwidth * m[i + 1, j + 1];
            double b = fractionalBandwidth * m[i + 1, i + 1] / 2.0;
            f[i] = centreHz * (Math.Sqrt(1.0 + b * b) - b);
        }
        return new CoupledResonators(1.0 / (fractionalBandwidth * m[0, 1] * m[0, 1]),
            1.0 / (fractionalBandwidth * m[n, n + 1] * m[n, n + 1]), k2, f);
    }

    /// <summary>The N+2 matrix back from its physical terms (<see cref="ToPhysical"/>), with positive port couplings.</summary>
    public static double[,] FromPhysical(CoupledResonators r, double centreHz, double fractionalBandwidth)
    {
        CheckBand(centreHz, fractionalBandwidth);
        int n = r.ResonantHz.Length;
        var m = new double[n + 2, n + 2];
        m[0, 1] = m[1, 0] = 1.0 / Math.Sqrt(fractionalBandwidth * r.QeSource);
        m[n, n + 1] = m[n + 1, n] = 1.0 / Math.Sqrt(fractionalBandwidth * r.QeLoad);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) if (i != j) m[i + 1, j + 1] = r.K[i, j] / fractionalBandwidth;
            m[i + 1, i + 1] = (centreHz / r.ResonantHz[i] - r.ResonantHz[i] / centreHz) / fractionalBandwidth;
        }
        return m;
    }

    private static void CheckBand(double centreHz, double fbw)
    {
        if (!(centreHz > 0) || !(fbw > 0))
            Refuse("band-invalid", "Coupling matrix: centre {f} Hz and fractional bandwidth {b} must both be positive.",
                ("f", centreHz), ("b", fbw));
    }

    // M ← R·M·Rᵀ with R the identity but for [cos −sin; sin cos] at rows and columns a, b, and tan θ = num/den, which
    // zeroes the element the caller aimed at. A zero numerator leaves the matrix as it is.
    private static void Rotate(double[,] m, int a, int b, double num, double den)
    {
        if (num == 0.0) return;
        double t = Math.Atan(num / den), c = Math.Cos(t), s = Math.Sin(t);
        int n = m.GetLength(0);
        for (int q = 0; q < n; q++) (m[a, q], m[b, q]) = (c * m[a, q] - s * m[b, q], s * m[a, q] + c * m[b, q]);
        for (int p = 0; p < n; p++) (m[p, a], m[p, b]) = (c * m[p, a] - s * m[p, b], s * m[p, a] + c * m[p, b]);
    }

    // X(s) = j^deg·X_ω(s/j): the coefficient of s^k is a_k·j^(deg−k), i.e. index i is multiplied by j^i.
    internal static Complex[] ToS(Complex[] w) => [.. w.Select((c, i) => c * JPow(i))];
    private static Complex[] ToS(double[] w) => ToS([.. w.Select(c => (Complex)c)]);
    internal static Complex[] ToOmega(Complex[] s) => [.. s.Select((c, i) => c * JPow(-i))];

    private static Complex JPow(int k) => (((k % 4) + 4) % 4) switch
    {
        0 => Complex.One, 1 => Complex.ImaginaryOne, 2 => -Complex.One, _ => -Complex.ImaginaryOne,
    };

    // MatchPoly.FromRoots keeps only the real part; E's roots are not conjugate-closed, so its coefficients are complex.
    internal static Complex[] FromRoots(IEnumerable<Complex> roots)
    {
        Complex[] p = [Complex.One];
        foreach (var r in roots) p = [.. p.Append(Complex.Zero).Select((c, i) => i == 0 ? c : c - p[i - 1] * r)];
        return p;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string code, string message, params (string Name, object? Value)[] args)
        => throw new ParameterRefusalException(Diagnostic.Create("synthesis.cm." + code, DiagnosticSeverity.Error, message, args));
}
