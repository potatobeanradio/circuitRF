// ================================================================
//  MulticonductorLine.cs  —  the modal decomposition of a uniform
//                            N-conductor line, and its 2N-port blocks.
//
//  Route A: the modal matrix comes from the LOSSLESS problem
//  [C]v = λ[L]⁻¹v (real, symmetric-definite, frequency-independent),
//  and loss is carried perturbatively by keeping the diagonals of the
//  full modal matrices formed WITH loss in them. One mode is then an
//  ordinary line, and every block of the 2N-port is
//
//      Tv · diag(x_m) · Ti⁻¹  =  Σ_m (x_m/e_m) · Tv[i,m] · Tv[j,m]
//
//  for the open-circuit Z (x_m = Zc_m·coth γ_m ℓ, Zc_m/sinh γ_m ℓ), and
//  the same sum over Ti for the short-circuit Y. Shared by the MoM
//  quasi-static kernel (Engine: ModalDecomposition, RlgcToSparams) and
//  Core's N-line coupled microstrip, so the arithmetic exists once.
//
//  Reference:
//    • C. R. Paul, "Analysis of Multiconductor Transmission Lines",
//      2nd ed., Wiley-IEEE Press, 2008, doi:10.1109/9780470547212 —
//      the modal (Tv, Ti) formulation of the 2N-port.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using NumFlat;

namespace RfCore;

/// <summary>
/// The lossless modal decomposition of a uniform multiconductor line from its per-unit-length matrices, the
/// per-frequency modal quantities with loss, and the 2N-port blocks they assemble into. Pure functions of
/// [C] (complex: G rides in its imaginary part, <c>Y = jω·C_complex</c>), [C₀], [L] and [R](ω).
/// </summary>
public static class MulticonductorLine
{
    /// <summary>Speed of light in vacuum, m/s, exact by SI definition.</summary>
    public const double SpeedOfLight = 2.99792458e8;

    /// <summary>
    /// <b>The general decomposition.</b> The modal matrix is taken from the <i>lossless</i> problem —
    /// <c>[L][C]·Tv = Tv·diag(1/v_p²)</c>, a real symmetric-definite generalized eigenproblem
    /// <c>[C]v = λ[L]⁻¹v</c> that NumFlat CAN solve — and loss is then carried perturbatively by forming the
    /// full modal matrices <i>with</i> loss in them and keeping only their diagonals. <b>That discard is the
    /// entire approximation</b>, and its size is measurable: see <see cref="ModalPoint.ModeCouplingResidual"/>.
    ///
    /// <para><b>R-gen-1 — <see cref="Symmetrise(Mat{double})"/> is a PRECONDITION of the eigensolve, not a
    /// tidy-up.</b> NumFlat's GEVD reads only the upper triangle and does not check; point collocation does
    /// not produce a symmetric [C]. Handing it the raw [C] silently decomposes a matrix that is not the one
    /// you have.</para>
    /// </summary>
    /// <param name="Tv">
    /// The voltage modal matrix — <c>V_conductor = Tv · V_mode</c>. Columns are modes, in
    /// <see cref="Lambda"/>-ascending order (R-gen-7). Real, and <b>frequency-independent</b>: it comes from
    /// the lossless problem, which has no ω in it, so mode identity is fixed for a whole sweep by
    /// construction.
    /// </param>
    /// <param name="Ti">
    /// The current modal matrix — <c>I_conductor = Ti · I_mode</c>. The biorthogonal partner
    /// <c>(Tvᵀ)⁻¹</c>, <b>rescaled per mode so that each mode's current pattern matches its own voltage
    /// pattern</b> (exactly equal when the two are parallel, which is what a symmetric pair gives). See
    /// <see cref="FromVoltageModalMatrix"/> for why that scaling makes the REPORTED <c>Zc_m</c> come out in
    /// ohms.
    /// </param>
    /// <param name="TvInv">Tv⁻¹.</param>
    /// <param name="TiInv">Ti⁻¹ = diag(1/e)·Tvᵀ, exact.</param>
    /// <param name="Lambda">λ_m = 1/v_p,m² — the generalized eigenvalues, s²/m².</param>
    /// <param name="Eeff">c²λ_m — the mode's effective permittivity.</param>
    /// <param name="LPerM">(Tv⁻¹[L]Ti)_mm — the mode's own inductance, H/m.</param>
    /// <param name="C0PerM">(Ti⁻¹[C₀]Tv)_mm — the mode's air-filled capacitance, F/m.</param>
    /// <param name="CComplexPerM">(Ti⁻¹[C]Tv)_mm — the mode's complex capacitance (G in its imaginary part,
    /// so a mode's shunt admittance stays one complex number).</param>
    /// <param name="CurrentScale">
    /// e_m, the per-mode factor relating <see cref="Ti"/> to the strict biorthogonal partner
    /// (<c>Ti = (Tvᵀ)⁻¹·diag(e)</c>). Exposed so the 2N-port blocks can be assembled as
    /// <c>Σ_m (x_m/e_m)·T[i,m]·T[j,m]</c> (<see cref="Block"/>), which is <b>bit-exactly symmetric</b> — that
    /// is what keeps reciprocity a structural property rather than a numerical one.
    /// </param>
    /// <param name="AsymmetryResidual">The source's own <c>max|C_ij − C_ji| / |C_ij + C_ji|</c>, carried
    /// through so a consumer reports the number its extractor measured.</param>
    public sealed record GeneralModes(
        Mat<double>            Tv,
        Mat<double>            Ti,
        Mat<double>            TvInv,
        Mat<double>            TiInv,
        IReadOnlyList<double>  Lambda,
        IReadOnlyList<double>  Eeff,
        IReadOnlyList<double>  LPerM,
        IReadOnlyList<double>  C0PerM,
        IReadOnlyList<Complex> CComplexPerM,
        IReadOnlyList<double>  CurrentScale,
        double                 AsymmetryResidual)
    {
        /// <summary>How many modes — one per conductor.</summary>
        public int ModeCount => Lambda.Count;

        /// <summary>v_p,m = 1/√λ_m, m/s.</summary>
        public double PhaseVelocity(int mode) => Math.Sqrt(1.0 / Lambda[mode]);

        /// <summary>
        /// The mode's STATIC, lossless characteristic impedance √(L_m/C_m) — the number a coupled-line
        /// designer means by "Z_e" or "Z_o". Ohms, because of the <see cref="Ti"/> normalisation (R-gen-3a).
        /// </summary>
        public double Z0(int mode)
        {
            double c = CComplexPerM[mode].Real;
            return c > 0 && LPerM[mode] > 0 ? Math.Sqrt(LPerM[mode] / c) : 0;
        }

        /// <summary>
        /// For N = 2 only: which mode is the EVEN (c) mode and which the ODD (π) mode, identified from the
        /// sign pattern of <see cref="Tv"/>'s columns rather than from mode order — the two conductors move
        /// together in the even mode and in opposition in the odd one, whatever order <see cref="Lambda"/>
        /// happened to put them in. Returns false when the pattern is not unambiguous, in which case no
        /// Even/Odd alias should be published.
        /// </summary>
        public bool TryIdentifyEvenOdd(out int even, out int odd)
        {
            even = odd = -1;
            if (ModeCount != 2) return false;

            bool Same(int m) => Tv[0, m] * Tv[1, m] > 0;
            bool s0 = Same(0), s1 = Same(1);
            if (s0 == s1) return false;                 // both, or neither — say nothing

            even = s0 ? 0 : 1;
            odd  = s0 ? 1 : 0;
            return true;
        }
    }

    /// <summary>
    /// The per-frequency modal quantities, plus <b>R-gen-5's measurement of what Route A threw away</b>.
    /// </summary>
    /// <param name="Z">Zm_mm = (Tv⁻¹([R]+jω[L])Ti)_mm — the mode's series impedance, Ω/m.</param>
    /// <param name="Y">Ym_mm = (Ti⁻¹(jω[C])Tv)_mm — the mode's shunt admittance, S/m.</param>
    /// <param name="RPerM">(Tv⁻¹[R](ω)Ti)_mm — the mode's series resistance alone, Ω/m.</param>
    /// <param name="ZCouplingResidual">
    /// <c>max_{i≠j}|Zm_ij| / min_i|Zm_ii|</c> — the fraction of the modal series matrix that Route A
    /// discarded at this frequency.
    /// </param>
    /// <param name="YCouplingResidual">The same for Ym.</param>
    public sealed record ModalPoint(
        Complex[] Z,
        Complex[] Y,
        double[]  RPerM,
        double    ZCouplingResidual,
        double    YCouplingResidual)
    {
        /// <summary>
        /// <b>R-gen-5.</b> The thing being thrown away, surfaced rather than assumed small, so a user can see
        /// when Route A's perturbative treatment of loss is under strain. A line for which this is 1e-9 is
        /// being decomposed essentially exactly; one for which it is 0.2 is not.
        /// </summary>
        public double ModeCouplingResidual => Math.Max(ZCouplingResidual, YCouplingResidual);
    }

    /// <summary>
    /// <b>Route A, steps 1–3.</b> Symmetrise, solve the lossless generalized eigenproblem, derive
    /// <c>Ti</c>. Frequency-independent — call once per sweep (or per geometry); <see cref="EvaluateAt"/> is
    /// the only per-frequency part.
    /// </summary>
    /// <param name="cComplex">[C] with G in its imaginary part (−G/ω), F/m.</param>
    /// <param name="c0">[C₀], the same line with every dielectric replaced by vacuum, F/m.</param>
    /// <param name="l">[L], H/m.</param>
    /// <param name="asymmetryResidual">Carried through to <see cref="GeneralModes.AsymmetryResidual"/>.</param>
    /// <exception cref="InvalidOperationException">The eigensolve failed — reported by name rather than as a
    /// bare linear-algebra exception.</exception>
    public static GeneralModes Decompose(Mat<Complex> cComplex, Mat<double> c0, Mat<double> l,
                                         double asymmetryResidual = 0.0)
    {
        ArgumentNullException.ThrowIfNull(cComplex);

        int n = cComplex.RowCount;
        if (n < 1) throw new InvalidOperationException("The multiconductor line has no conductors.");

        // 1 — R-gen-1. NOT a tidy-up: NumFlat's GEVD reads only the upper triangle and never checks.
        var c    = Symmetrise(cComplex);
        var lSym = Symmetrise(l);
        var lInv = Symmetrise(Invert(lSym));

        // 2/3 — the lossless problem and its ordering.
        var tv = VoltageModalMatrix(RealPart(c), lInv, out var lambda);

        return FromVoltageModalMatrix(cComplex, c0, l, tv, lambda, asymmetryResidual);
    }

    /// <summary>
    /// Derive <c>Ti</c> and every per-mode quantity from a GIVEN voltage modal matrix — the tail of
    /// <see cref="Decompose"/>, exposed so a normalisation-invariance test (scale Tv's columns, rebuild,
    /// compare the blocks) exercises the production derivation rather than a restatement of it.
    ///
    /// <para><b>Why <c>Ti</c> is rescaled, and why the rescale cannot move the terminal answer.</b> The
    /// physics fixes <c>Ti</c> only up to one scalar per mode: scaling column m of Ti by e multiplies
    /// <c>Zm_mm</c> by e and divides <c>Ym_mm</c> by e, so γ_m = √(Zm·Ym) is untouched while
    /// <c>Zc_m = √(Zm/Ym)</c> scales by e. The 2N-port blocks are <c>Tv·diag(x_m)·Ti⁻¹</c> with
    /// <c>x_m ∝ Zc_m</c>, so the e in <c>x</c> and the 1/e in <c>Ti⁻¹</c> cancel exactly. The scaling is
    /// therefore <b>purely a reporting choice</b>: the one under which a symmetric pair's reported Zc
    /// reproduces its own Z_e and Z_o.</para>
    ///
    /// <para><b>The choice: "each conductor carries the mode's own current."</b> Take the strict
    /// biorthogonal partner <c>Tib = (Tvᵀ)⁻¹</c> and scale its column m so it is the least-squares closest to
    /// <c>Tv</c>'s column m — which, because <c>Tvᵀ·Tib = I</c>, is simply <c>Ti_m = Tib_m / ‖Tib_m‖²</c>.
    /// When the two are parallel (a symmetric pair: both columns are (1,1) and (1,−1)) it makes them
    /// <b>equal</b>, so <c>Zc_even</c> comes out as √(L_even/C_even) in ohms rather than as the mode's phase
    /// velocity. Under this rule <c>Zm_mm</c> and <c>Ym_mm</c> are ALSO invariant to scaling <c>Tv</c>'s
    /// columns, so no reported per-mode quantity depends on whatever normalisation LAPACK handed back.</para>
    ///
    /// <para><paramref name="lambda"/> carries the lossless eigenvalues 1/v_p² through unchanged; they are a
    /// property of the modes, not of the scaling.</para>
    /// </summary>
    public static GeneralModes FromVoltageModalMatrix(
        Mat<Complex> cComplex, Mat<double> c0, Mat<double> l, Mat<double> tv, IReadOnlyList<double> lambda,
        double asymmetryResidual = 0.0)
    {
        ArgumentNullException.ThrowIfNull(lambda);

        int n = tv.RowCount;
        var c    = Symmetrise(cComplex);
        var c0s  = Symmetrise(c0);
        var lSym = Symmetrise(l);

        var tvInv = Invert(tv);

        // Ti_m = Tib_m / ‖Tib_m‖², with Tib = (Tvᵀ)⁻¹ — i.e. Tib's column m is Tv⁻¹'s ROW m.
        var ti    = new Mat<double>(n, n);
        var tiInv = new Mat<double>(n, n);
        var scale = new double[n];
        for (int m = 0; m < n; m++)
        {
            double norm2 = 0;
            for (int k = 0; k < n; k++) norm2 += tvInv[m, k] * tvInv[m, k];
            if (!(norm2 > 0))
                throw new InvalidOperationException(
                    $"Modal matrix column {m} is degenerate — the modes are not independent.");

            double e = 1.0 / norm2;                       // the per-mode scale, Ti = Tib·diag(e)
            scale[m] = e;
            for (int k = 0; k < n; k++)
            {
                ti[k, m]    = tvInv[m, k] * e;            // Ti  = Tib · diag(e)
                tiInv[m, k] = tv[k, m] * norm2;           // Ti⁻¹ = diag(1/e) · Tvᵀ — EXACT, never a
                                                          // second numerical inversion.
            }
        }

        // The frequency-independent modal quantities. Lmodal, C0modal and Cmodal are diagonal to round-off by
        // construction for a GEVD basis (Tvᵀ[L]⁻¹Tv, Tvᵀ[C₀]Tv and Tvᵀ[C]Tv are all diagonal there); Cmodal
        // picks up off-diagonals only from a dielectric loss distribution that is not proportional to [C].
        var lModal  = DiagonalOf(Mul(Mul(tvInv, lSym), ti));
        var c0Modal = DiagonalOf(Mul(Mul(tiInv, c0s), tv));
        var cModal  = DiagonalOf(Mul(Mul(ToComplex(tiInv), c), ToComplex(tv)));

        var eeff = new double[n];
        var lam  = new double[n];
        for (int m = 0; m < n; m++)
        {
            lam[m]  = lambda[m];
            eeff[m] = lambda[m] * SpeedOfLight * SpeedOfLight;
        }

        return new GeneralModes(tv, ti, tvInv, tiInv, lam, eeff, lModal, c0Modal, cModal, scale, asymmetryResidual);
    }

    /// <summary>
    /// <b>Route A, steps 4–5 — and step 5 IS the approximation.</b> Form the FULL modal matrices with loss
    /// in them at <paramref name="omegaRadS"/>, then keep only their diagonals — and report how much was
    /// discarded.
    /// </summary>
    /// <param name="r">[R](ω), Ω/m.</param>
    /// <param name="l">[L], H/m.</param>
    /// <param name="cComplex">[C] with G in its imaginary part.</param>
    /// <param name="modes">This line's <see cref="Decompose"/>.</param>
    /// <param name="omegaRadS">ω.</param>
    public static ModalPoint EvaluateAt(Mat<double> r, Mat<double> l, Mat<Complex> cComplex, GeneralModes modes,
                                        double omegaRadS)
    {
        ArgumentNullException.ThrowIfNull(modes);

        int n = modes.ModeCount;
        var rs = Symmetrise(r);

        // Zm(ω) = Tv⁻¹·([R] + jω[L])·Ti — real [R] and [L], so the product is assembled once.
        var rModalFull = Mul(Mul(modes.TvInv, rs), modes.Ti);
        var lModalFull = Mul(Mul(modes.TvInv, Symmetrise(l)), modes.Ti);
        var zFull = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            zFull[i, j] = new Complex(rModalFull[i, j], omegaRadS * lModalFull[i, j]);

        // Ym(ω) = Ti⁻¹·(jω[C_complex])·Tv — G rides in Im[C], so there is no separate [G].
        var cModalFull = Mul(Mul(ToComplex(modes.TiInv), Symmetrise(cComplex)), ToComplex(modes.Tv));
        var yFull = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            yFull[i, j] = Complex.ImaginaryOne * omegaRadS * cModalFull[i, j];

        var z = new Complex[n];
        var y = new Complex[n];
        var rr = new double[n];
        for (int m = 0; m < n; m++)
        {
            z[m]  = zFull[m, m];
            y[m]  = yFull[m, m];
            rr[m] = rModalFull[m, m];
        }

        return new ModalPoint(z, y, rr, CouplingResidual(zFull), CouplingResidual(yFull));
    }

    /// <summary>
    /// <b>The modal transform, stated once.</b> <c>Σ_m (x_m/e_m)·t[i,m]·t[j,m]</c>: with
    /// <paramref name="t"/> = <see cref="GeneralModes.Tv"/> it is <c>Tv·diag(x)·Ti⁻¹</c>, a block of the
    /// open-circuit Z; with <see cref="GeneralModes.Ti"/> it is <c>Ti·diag(x)·Tv⁻¹</c>, a block of the
    /// short-circuit Y (both because <c>Ti = (Tvᵀ)⁻¹·diag(e)</c>). Assembled as the sum so that the [i,j] and
    /// [j,i] entries are <b>bit-identical</b>, not merely equal to solver tolerance: reciprocity is
    /// structural.
    /// </summary>
    public static Mat<Complex> Block(Mat<double> t, IReadOnlyList<double> currentScale, Complex[] x)
    {
        int n = t.RowCount;
        var r = new Mat<Complex>(n, n);
        for (int m = 0; m < n; m++)
        {
            var w = x[m] / currentScale[m];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] += w * (t[i, m] * t[j, m]);
        }
        return r;
    }

    /// <summary>√ on the branch with Re ≥ 0 — a propagation constant never has a negative attenuation, and
    /// Z_c of a passive line never has a negative resistance.</summary>
    public static Complex PrincipalSqrt(Complex v)
    {
        var r = Complex.Sqrt(v);
        return r.Real < 0 ? -r : r;
    }

    /// <summary>
    /// <b>Symmetrise, and never assume.</b> A discretised capacitance matrix is not symmetric unless the
    /// discretisation is Galerkin; its residual is a separate number, never averaged out of existence here.
    /// </summary>
    public static Mat<Complex> Symmetrise(Mat<Complex> m)
    {
        var r = new Mat<Complex>(m.RowCount, m.ColCount);
        for (int i = 0; i < m.RowCount; i++)
        for (int j = 0; j < m.ColCount; j++)
            r[i, j] = 0.5 * (m[i, j] + m[j, i]);
        return r;
    }

    /// <inheritdoc cref="Symmetrise(Mat{Complex})"/>
    public static Mat<double> Symmetrise(Mat<double> m)
    {
        var r = new Mat<double>(m.RowCount, m.ColCount);
        for (int i = 0; i < m.RowCount; i++)
        for (int j = 0; j < m.ColCount; j++)
            r[i, j] = 0.5 * (m[i, j] + m[j, i]);
        return r;
    }

    /// <summary>
    /// <b>The lossless modal eigenproblem [C]v = λ[L]⁻¹v, and R-gen-7's physical ordering of its answer.</b>
    /// λ = 1/v_p,m²; the columns of the returned matrix are the voltage eigenvectors, signed so a re-run
    /// reports the same matrix.
    ///
    /// <para>Ordered by λ ASCENDING (fastest mode first), tie-broken by the eigenvector's own
    /// largest-magnitude conductor index so a degenerate pair still orders stably, and finally by the raw
    /// LAPACK index so the comparison is a total order. For a microstrip pair that puts the odd mode — more
    /// field in air, lower ε_eff — at index 0; <see cref="GeneralModes.TryIdentifyEvenOdd"/> names the modes
    /// from their sign pattern, not from their position.</para>
    ///
    /// <para>The column NORMALISATION is whatever the GEVD returned and deliberately not touched: every
    /// quantity a caller derives is invariant to it.</para>
    /// </summary>
    public static Mat<double> VoltageModalMatrix(Mat<double> cReal, Mat<double> lInv, out double[] lambda)
    {
        int n = cReal.RowCount;

        Mat<double> vRaw;
        Vec<double> dRaw;
        try
        {
            var gevd = MatrixDecompositions.Gevd(cReal, lInv);
            vRaw = gevd.V;
            dRaw = gevd.D;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The lossless modal eigenproblem [C]v = λ[L]⁻¹v could not be solved. Both matrices must be " +
                "symmetric positive definite; a degenerate cross-section (a conductor with no capacitance to " +
                "anything, or two conductors solved as one) is the usual cause.", ex);
        }

        // R-gen-7. Order the modes by a PHYSICAL property, never by whatever LAPACK returned.
        var order = SortModes(vRaw, dRaw, n);

        var tv = new Mat<double>(n, n);
        lambda = new double[n];
        for (int m = 0; m < n; m++)
        {
            int src = order[m];
            lambda[m] = dRaw[src];

            // Sign is arbitrary in an eigenvector; pin it so a re-run reports the same Tv.
            int big = 0;
            for (int k = 1; k < n; k++)
                if (Math.Abs(vRaw[k, src]) > Math.Abs(vRaw[big, src])) big = k;
            double sign = vRaw[big, src] < 0 ? -1.0 : 1.0;

            for (int k = 0; k < n; k++) tv[k, m] = sign * vRaw[k, src];
        }
        return tv;
    }

    /// <summary>Gauss–Jordan with partial pivoting. N is a conductor count — single digits.</summary>
    /// <exception cref="InvalidOperationException">The matrix is singular.</exception>
    public static Mat<double> Invert(Mat<double> a)
    {
        int n = a.RowCount;
        var w = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) w[i, j] = a[i, j];
            w[i, n + i] = 1.0;
        }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(w[r, col]) > Math.Abs(w[piv, col])) piv = r;
            if (Math.Abs(w[piv, col]) < 1e-300)
                throw new InvalidOperationException(
                    "The per-unit-length matrix is singular — a conductor set with no reference, or two conductors solved as one.");
            if (piv != col)
                for (int j = 0; j < 2 * n; j++) (w[col, j], w[piv, j]) = (w[piv, j], w[col, j]);

            double d = w[col, col];
            for (int j = 0; j < 2 * n; j++) w[col, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = w[r, col];
                if (f == 0) continue;
                for (int j = 0; j < 2 * n; j++) w[r, j] -= f * w[col, j];
            }
        }
        var inv = new Mat<double>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            inv[i, j] = w[i, n + j];
        return inv;
    }

    /// <summary><c>max_{i≠j}|M_ij| / min_i|M_ii|</c>. Zero for a 1×1 (there is nothing to discard).</summary>
    public static double CouplingResidual(Mat<Complex> m)
    {
        int n = m.RowCount;
        if (n < 2) return 0;

        double off = 0, minDiag = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            minDiag = Math.Min(minDiag, m[i, i].Magnitude);
            for (int j = 0; j < n; j++)
                if (i != j) off = Math.Max(off, m[i, j].Magnitude);
        }
        return minDiag > 0 ? off / minDiag : (off > 0 ? double.PositiveInfinity : 0);
    }

    private static int[] SortModes(Mat<double> v, Vec<double> d, int n)
    {
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;

        int BigIndex(int col)
        {
            int big = 0;
            for (int k = 1; k < n; k++)
                if (Math.Abs(v[k, col]) > Math.Abs(v[big, col])) big = k;
            return big;
        }

        Array.Sort(idx, (a, b) =>
        {
            // A relative tolerance, so a genuinely degenerate pair (R-gen-6 — two identical conductors far
            // apart have the SAME velocity, guaranteed, not as a corner case) falls through to the tie-break
            // rather than ordering on round-off.
            double scale = Math.Max(Math.Abs(d[a]), Math.Abs(d[b]));
            if (Math.Abs(d[a] - d[b]) > 1e-9 * scale) return d[a].CompareTo(d[b]);
            int ba = BigIndex(a), bb = BigIndex(b);
            return ba != bb ? ba.CompareTo(bb) : a.CompareTo(b);
        });
        return idx;
    }

    // ── tiny dense helpers (N is the conductor count — single digits) ──────────────────────────────────

    private static Mat<double> RealPart(Mat<Complex> a)
    {
        var r = new Mat<double>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            r[i, j] = a[i, j].Real;
        return r;
    }

    private static Mat<Complex> ToComplex(Mat<double> a)
    {
        var r = new Mat<Complex>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            r[i, j] = a[i, j];
        return r;
    }

    /// <summary>A·B for small dense real matrices.</summary>
    public static Mat<double> Mul(Mat<double> a, Mat<double> b)
    {
        var r = new Mat<double>(a.RowCount, b.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int k = 0; k < a.ColCount; k++)
        {
            double v = a[i, k];
            if (v == 0) continue;
            for (int j = 0; j < b.ColCount; j++) r[i, j] += v * b[k, j];
        }
        return r;
    }

    /// <summary>A·B for small dense complex matrices.</summary>
    public static Mat<Complex> Mul(Mat<Complex> a, Mat<Complex> b)
    {
        var r = new Mat<Complex>(a.RowCount, b.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int k = 0; k < a.ColCount; k++)
        {
            var v = a[i, k];
            if (v == Complex.Zero) continue;
            for (int j = 0; j < b.ColCount; j++) r[i, j] += v * b[k, j];
        }
        return r;
    }

    private static double[] DiagonalOf(Mat<double> a)
    {
        var d = new double[a.RowCount];
        for (int i = 0; i < a.RowCount; i++) d[i] = a[i, i];
        return d;
    }

    private static Complex[] DiagonalOf(Mat<Complex> a)
    {
        var d = new Complex[a.RowCount];
        for (int i = 0; i < a.RowCount; i++) d[i] = a[i, i];
        return d;
    }
}
