// The even/odd modal decomposition of a SYMMETRIC coupled pair (brief-L7b §4, R-cpl-7/8/9).
//
// D1 — L7b ships the symmetric pair, and that is the whole phase. A symmetric pair — two identical
// conductors, mirror-symmetric about a plane — decouples into even and odd modes with a FIXED modal
// matrix [1 1; 1 −1]/√2, by symmetry alone. No eigensolver is involved at any point, with or
// without loss: that matrix diagonalises ANY 2×2 of the form [a b; b a] whatever a and b are.
//
// §0 — that is not a convenience, it is the reason this phase is tractable. NumFlat's complex
// eigensolver is Hermitian-only (its own XML: "the matrix to be decomposed must be symmetric
// positive definite… only the upper triangular part is used, and the rest is ignored") and returns
// REAL eigenvalues. For a lossy multiconductor line [Z][Y] = (R + jωL)(G + jωC) is a general
// non-Hermitian complex matrix whose eigenvalues γ² are genuinely complex. Handing it to
// MatrixDecompositions.Evd would read the upper triangle of a matrix that is not symmetric and
// return real numbers for a quantity that is not real — a smooth, plausible, wrong answer, which is
// the failure mode this whole area is built to avoid. Verified against NumFlat 1.3.0 directly, not
// assumed.
//
// The general case — asymmetric pairs and N > 2 — is L7b-b (D2), refused by name here.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The frequency-independent per-unit-length quantities of ONE mode. Deliberately the same shape as
/// the single line's, so <see cref="RlgcToSparams"/> forms γ and Z_c from it with the identical
/// per-frequency code rather than a coupled-line-specific formula (R-cpl-10).
/// </summary>
/// <param name="CComplexPerM">
/// The mode's complex capacitance. Carrying C and G in one complex number is R-mom-6 unchanged:
/// <c>Y = jω·C_complex</c> is exactly <c>G + jωC</c>, so a mode's shunt admittance is a single
/// complex sum of the matrix entries and there is no separate G combination to get wrong.
/// </param>
/// <param name="C0PerM">The air-filled capacitance of the same mode, for ε_eff.</param>
/// <param name="LPerM">The mode's inductance, H/m.</param>
/// <param name="Eeff">C/C₀ for THIS mode — R-cpl-1's whole point.</param>
public sealed record ModeRlgc(Complex CComplexPerM, double C0PerM, double LPerM, double Eeff)
{
    /// <summary>C′ = Re(C) — F/m.</summary>
    public double CPerM => CComplexPerM.Real;

    /// <summary>G(ω) = −ω·Im(C_complex) — S/m.</summary>
    public double GPerM(double omegaRadS) => -omegaRadS * CComplexPerM.Imaginary;

    /// <summary>
    /// The STATIC, lossless characteristic impedance √(L/C) — the number a coupled-line designer
    /// means by "Z_e" or "Z_o", and what R-cpl-9's <c>Z_o &lt; Z_e</c> sanity gate is asserted on.
    /// The frequency-dependent Z_c including R and G is formed per frequency in
    /// <see cref="RlgcToSparams"/>, exactly as for the single line.
    /// </summary>
    public double Z0 => CPerM > 0 ? Math.Sqrt(LPerM / CPerM) : 0;
}

/// <summary>The even and odd modes of a symmetric pair, plus the diagnostics R-cpl-7 requires.</summary>
/// <param name="AsymmetryResidual">R-cpl-7's discretisation-error indicator, carried through from
/// the raw [C] so the consumer of the off-diagonals reports the same number the extractor measured.</param>
public sealed record CoupledModes(
    ModeRlgc Even,
    ModeRlgc Odd,
    double   AsymmetryResidual)
{
    /// <summary>
    /// R-cpl-9's cheapest possible sanity gate: for every real edge-coupled line Z_o &lt; Z_e. A
    /// Maxwell capacitance matrix has NEGATIVE off-diagonals; a "mutual capacitance" matrix has
    /// positive ones, and getting that backwards swaps even and odd. Both answers look physical, and
    /// on a symmetric structure many magnitude plots barely move — so the sign convention is pinned
    /// by an inequality rather than trusted.
    /// </summary>
    public bool SignConventionHolds => Odd.Z0 < Even.Z0;
}

/// <summary>
/// §4 — the modal decomposition, and the symmetry that has to be CHECKED rather than assumed.
/// </summary>
public static class ModalDecomposition
{
    /// <summary>
    /// R-cpl-8's tolerance, applied to the conductors' own WIDTH, THICKNESS and HEIGHT — never to
    /// the solved matrix. 0.1% of a 1.4 mm strip is 1.4 µm, which absorbs DBU rounding while still
    /// refusing a pair a user genuinely drew at two different widths.
    /// </summary>
    public const double GeometricSymmetryTolerance = 1e-3;

    /// <summary>
    /// Above this, <c>|C₁₁ − C₂₂| / |C₁₁ + C₂₂|</c> on a pair that is GEOMETRICALLY symmetric says
    /// the mesh is too coarse to resolve the two conductors alike. It is <b>warned about, never
    /// refused</b> — see <see cref="DiagonalAsymmetry"/>.
    /// </summary>
    public const double DiagonalAsymmetryWarnThreshold = 0.02;

    /// <summary>Above this the coupled off-diagonals carry enough discretisation error to be worth
    /// telling the user about. The engine half measured ~3% on a coupled pair at default settings.</summary>
    public const double AsymmetryResidualWarnThreshold = 0.05;

    /// <summary>
    /// <b>R-cpl-7's number:</b> <c>max|C_ij − C_ji| / |C_ij + C_ji|</c> over the strictly-upper
    /// triangle. Zero for a single conductor, and zero for a pair whose off-diagonals happen to
    /// agree exactly.
    ///
    /// <para>Measured on the RAW, un-symmetrised matrix — measuring it after
    /// <see cref="Symmetrise(Mat{Complex})"/> would report zero by construction and tell nobody
    /// anything. A pair whose C_ij and C_ji sum to zero is degenerate rather than perfectly
    /// symmetric, so that case reports 0 rather than dividing by it.</para>
    /// </summary>
    public static double AsymmetryResidual(Mat<Complex> m)
    {
        double worst = 0;
        for (int i = 0; i < m.RowCount; i++)
        for (int j = i + 1; j < m.ColCount; j++)
        {
            double s = (m[i, j] + m[j, i]).Magnitude;
            if (s <= 0) continue;
            worst = Math.Max(worst, (m[i, j] - m[j, i]).Magnitude / s);
        }
        return worst;
    }

    /// <summary>
    /// <b>R-cpl-7 — symmetrise, and never assume.</b> Point collocation on a piecewise-constant
    /// basis does not produce a symmetric system matrix; only a Galerkin discretisation would. The
    /// residual is reported separately (<see cref="RlgcModel.AsymmetryResidual"/>) rather than being
    /// silently averaged out of existence here.
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
    /// <b>R-cpl-8 — geometric symmetry is a SEPARATE check from matrix symmetry, and it decides
    /// whether the even/odd split is <i>exact</i>.</b> Two identical conductors are what make
    /// <c>[1 1; 1 −1]</c> the correct modal matrix; two lines of different widths, or on different
    /// metal levels, make the even/odd split simply <b>wrong</b> — not approximate.
    ///
    /// <para><b>R-gen-9 — this has stopped being a REFUSAL and become the route selector's input.</b>
    /// L7b-b's general modal decomposition handles an asymmetric pair correctly, so
    /// <c>QuasiStaticKernel.CanSolve</c> no longer calls this. What it still answers — "is this pair
    /// mirror-symmetric?" — is exactly what makes L7b's exact <c>[1 1; 1 −1]</c> construction
    /// applicable as a <i>test oracle</i> (D1), and is the honest place to decide whether an
    /// even/odd vocabulary is meaningful for a given cross-section. The method is kept; its callers
    /// changed.</para>
    ///
    /// <para><b>Checked on the GEOMETRY, deliberately, not on the solved [C].</b> R-cpl-8 words the
    /// symptom as "C₁₁ ≠ C₂₂", and testing that directly is the obvious implementation — but it is
    /// wrong, and measurably so. A genuinely symmetric pair of very thin strips (t/W ≈ 1/1400) comes
    /// out of the default mesh with C₁₁ and C₂₂ differing by <b>6.8%</b>, falling to 0.99% under
    /// <c>Refined(4)</c>: pure discretisation error, converging to zero, on a pair that is
    /// mirror-symmetric by construction. A matrix-based check would refuse that pair as "asymmetric"
    /// and point the user at L7b-b, when what they actually need is a finer mesh. The conductor
    /// outlines are exact and immune to discretisation, so they are what the legality question is
    /// asked of; the matrix version survives as a WARNING (<see cref="DiagonalAsymmetry"/>).</para>
    /// </summary>
    /// <returns>
    /// null when the cross-section is a mirror-symmetric pair (or has fewer than two signal
    /// conductors, where the question does not arise); otherwise a description of why it is not.
    /// </returns>
    public static string? CheckGeometricSymmetry(EmProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // Reference conductors are return paths, not lines — they are not part of the pair.
        var signal = new List<EmConductor>();
        foreach (var c in problem.Conductors)
        {
            bool isRef = false;
            foreach (var p in problem.Ports)
                if (string.Equals(p.ReferenceConductor, c.Name, StringComparison.Ordinal)) isRef = true;
            if (!isRef) signal.Add(c);
        }

        if (signal.Count < 2) return null;              // a single line needs no decomposition
        if (signal.Count > 2)
            return $"This cross-section has {signal.Count} signal conductors, so it is not a pair " +
                   "and the even/odd vocabulary does not apply to it. Its modes come from the " +
                   "general decomposition and are reported on a mode axis.";

        var (ax0, ay0, ax1, ay1) = Polygon2D.Bounds(signal[0].Outline);
        var (bx0, by0, bx1, by1) = Polygon2D.Bounds(signal[1].Outline);
        string a = signal[0].Name, b = signal[1].Name;

        string? Mismatch(string what, double va, double vb, string unit)
        {
            double rel = RelativeGap(va, vb);
            if (rel <= GeometricSymmetryTolerance) return null;
            return $"Conductors '{a}' and '{b}' are not mirror-symmetric: '{a}' has {what} " +
                   $"{va:G4} {unit} but '{b}' has {vb:G4} {unit}, a {rel * 2:P1} difference. The " +
                   "even/odd split assumes identical conductors, so for an asymmetric pair it is " +
                   "not an approximation — it is the wrong decomposition. The general modal " +
                   "decomposition handles it correctly and reports its modes on a mode axis.";
        }

        return Mismatch("width",     ax1 - ax0, bx1 - bx0, "m")
            ?? Mismatch("thickness", ay1 - ay0, by1 - by0, "m")
            ?? Mismatch("its lower surface at y =", ay0, by0, "m");
    }

    /// <summary>
    /// <c>|C₁₁ − C₂₂| / |C₁₁ + C₂₂|</c> on the solved matrix. For a pair that
    /// <see cref="CheckGeometricSymmetry"/> has already accepted this is pure discretisation error,
    /// so it is a <b>mesh-quality indicator reported alongside R-cpl-7's off-diagonal residual</b>,
    /// never a refusal. Measured on a realistic pair (35 µm copper on 1.6 mm FR-4) it is 0.07% at
    /// default settings and 0.001% under <c>Refined(4)</c>.
    /// </summary>
    public static double DiagonalAsymmetry(RlgcModel rlgc)
    {
        ArgumentNullException.ThrowIfNull(rlgc);
        return DiagonalAsymmetry(rlgc.CComplex);
    }

    /// <inheritdoc cref="DiagonalAsymmetry(RlgcModel)"/>
    public static double DiagonalAsymmetry(Mat<Complex> c)
        => c.RowCount < 2 ? 0 : RelativeGap(c[0, 0].Real, c[1, 1].Real);

    /// <summary>
    /// <b>R-cpl-9 — the modal quantities, stated once.</b> With the symmetrised matrices and the
    /// Maxwell capacitance convention already in use (off-diagonals negative):
    ///
    /// <code>
    /// C_even = C₁₁ + C₁₂        C_odd = C₁₁ − C₁₂
    /// L_even = L₁₁ + L₁₂        L_odd = L₁₁ − L₁₂
    /// Z_e    = √(L_even/C_even) Z_o   = √(L_odd/C_odd)
    /// ε_eff,e = C_even / C₀,even       ε_eff,o = C_odd / C₀,odd
    /// </code>
    ///
    /// <para>The same combination applies to [R] and to the complex [C] that carries [G], so a mode
    /// is an ordinary per-unit-length line and γ/Z_c come from the single line's own per-frequency
    /// code (R-cpl-10). <b>The sign convention on C₁₂ is the thing that will silently invert
    /// this</b> — see <see cref="CoupledModes.SignConventionHolds"/>.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This is not a two-conductor model. The GEOMETRIC legality question is
    /// <see cref="CheckGeometricSymmetry"/>, which <c>QuasiStaticKernel.CanSolve</c> asks before any
    /// meshing happens, so a user reaches neither of these.
    /// </exception>
    public static CoupledModes Decompose(RlgcModel rlgc)
    {
        ArgumentNullException.ThrowIfNull(rlgc);
        if (rlgc.ConductorCount != 2)
            throw new InvalidOperationException(
                $"The even/odd decomposition is defined for exactly two conductors; this model has " +
                $"{rlgc.ConductorCount}. The general N-conductor case is built and is what the " +
                $"kernel actually uses: call ModalDecomposition.DecomposeGeneral, which handles any " +
                $"N, symmetric or not. This method survives as the closed-form ORACLE the general " +
                $"path is gated against for a symmetric pair, and as the source of the Even/Odd " +
                $"aliases published at N = 2.");

        var c  = Symmetrise(rlgc.CComplex);
        var c0 = Symmetrise(rlgc.C0);
        var l  = Symmetrise(rlgc.L);

        var cEven = c[0, 0] + c[0, 1];
        var cOdd  = c[0, 0] - c[0, 1];
        double c0Even = c0[0, 0] + c0[0, 1];
        double c0Odd  = c0[0, 0] - c0[0, 1];
        double lEven  = l[0, 0] + l[0, 1];
        double lOdd   = l[0, 0] - l[0, 1];

        return new CoupledModes(
            Even: new ModeRlgc(cEven, c0Even, lEven, c0Even != 0 ? cEven.Real / c0Even : 1.0),
            Odd:  new ModeRlgc(cOdd,  c0Odd,  lOdd,  c0Odd  != 0 ? cOdd.Real  / c0Odd  : 1.0),
            AsymmetryResidual: rlgc.AsymmetryResidual);
    }

    /// <summary>
    /// The mode's series resistance at ω, from the FULL [R] matrix (R-cpl-2's per-conductor
    /// derivatives are what make this well defined): <c>R_even = R₁₁ + R₁₂</c>,
    /// <c>R_odd = R₁₁ − R₁₂</c>, the same combination every other modal quantity uses.
    ///
    /// <para>Frequency-dependent, so it is a method rather than a field on <see cref="ModeRlgc"/> —
    /// and it is the ONLY per-frequency modal quantity, because [R] is the only per-frequency part
    /// of the RLGC model.</para>
    /// </summary>
    public static (double Even, double Odd) ModalR(RlgcModel rlgc, double omegaRadS)
    {
        var r = Symmetrise(rlgc.RMatrix(omegaRadS));
        return (r[0, 0] + r[0, 1], r[0, 0] - r[0, 1]);
    }

    private static double RelativeGap(double a, double b)
    {
        double s = Math.Abs(a + b);
        return s > 0 ? Math.Abs(a - b) / s : 0;
    }

    // ── L7b-b — the GENERAL decomposition (Route A) ────────────────────────────────────────────

    /// <summary>
    /// <b>D1 — the general path SUBSUMES the symmetric pair.</b> Once this exists a symmetric pair
    /// goes through it like everything else; L7b's fixed <c>[1 1; 1 −1]</c> construction survives as
    /// a <i>test oracle</i>, not as a production branch. Two code paths that must agree are two code
    /// paths that will eventually disagree, and the one that drifts would be the rarely-exercised
    /// one.
    ///
    /// <para><b>Route A, and what it approximates.</b> The modal matrix is taken from the
    /// <i>lossless</i> problem — <c>[L][C]·Tv = Tv·diag(1/v_p²)</c>, a real symmetric-definite
    /// generalized eigenproblem <c>[C]v = λ[L]⁻¹v</c> that NumFlat CAN solve — and loss is then
    /// carried perturbatively by forming the full modal matrices <i>with</i> loss in them and keeping
    /// only their diagonals. <b>That discard is the entire approximation</b>, and its size is
    /// measurable: see <see cref="ModalPoint.ModeCouplingResidual"/>.</para>
    ///
    /// <para><b>R-gen-1 — <see cref="Symmetrise(Mat{double})"/> is a PRECONDITION of the eigensolve,
    /// not a tidy-up.</b> NumFlat's GEVD reads only the upper triangle and does not check; point
    /// collocation does not produce a symmetric [C] (R-cpl-7, measured at 0.554% off-diagonal
    /// residual at default mesh settings). Handing it the raw [C] silently decomposes a matrix that
    /// is not the one you have.</para>
    /// </summary>
    /// <param name="Tv">
    /// The voltage modal matrix — <c>V_conductor = Tv · V_mode</c>. Columns are modes, in
    /// <see cref="Lambda"/>-ascending order (R-gen-7). Real, and <b>frequency-independent</b>: it
    /// comes from the lossless problem, which has no ω in it, so mode identity is fixed for a whole
    /// sweep by construction — a real advantage of Route A over a per-frequency Route B.
    /// </param>
    /// <param name="Ti">
    /// The current modal matrix — <c>I_conductor = Ti · I_mode</c>. The biorthogonal partner
    /// <c>(Tvᵀ)⁻¹</c>, <b>rescaled per mode so that each mode's current pattern matches its own
    /// voltage pattern</b> (exactly equal when the two are parallel, which is what a symmetric pair
    /// gives). See <see cref="DecomposeGeneral"/>'s remarks for why that particular scaling is the
    /// one that makes the REPORTED <c>Zc_m</c> come out in ohms rather than in metres per second.
    /// </param>
    /// <param name="Lambda">λ_m = 1/v_p,m² — the generalized eigenvalues, s²/m².</param>
    /// <param name="Eeff">c²λ_m — the mode's effective permittivity.</param>
    /// <param name="LPerM">(Tv⁻¹[L]Ti)_mm — the mode's own inductance, H/m.</param>
    /// <param name="C0PerM">(Ti⁻¹[C₀]Tv)_mm — the mode's air-filled capacitance, F/m.</param>
    /// <param name="CComplexPerM">(Ti⁻¹[C]Tv)_mm — the mode's complex capacitance (R-mom-6 carries
    /// G in its imaginary part, so a mode's shunt admittance stays one complex number).</param>
    /// <param name="CurrentScale">
    /// e_m, the per-mode factor relating <see cref="Ti"/> to the strict biorthogonal partner
    /// (<c>Ti = (Tvᵀ)⁻¹·diag(e)</c>). Exposed so the 2N-port blocks can be assembled as
    /// <c>Σ_m (x_m/e_m)·Tv[i,m]·Tv[j,m]</c>, which is <b>bit-exactly symmetric</b> — that is what
    /// keeps reciprocity a structural property rather than a numerical one.
    /// </param>
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
        /// The mode's STATIC, lossless characteristic impedance √(L_m/C_m) — the number a
        /// coupled-line designer means by "Z_e" or "Z_o". Ohms, because of the
        /// <see cref="Ti"/> normalisation (R-gen-3a).
        /// </summary>
        public double Z0(int mode)
        {
            double c = CComplexPerM[mode].Real;
            return c > 0 && LPerM[mode] > 0 ? Math.Sqrt(LPerM[mode] / c) : 0;
        }

        /// <summary>
        /// For N = 2 only: which mode is the EVEN (c) mode and which the ODD (π) mode, identified
        /// from the sign pattern of <see cref="Tv"/>'s columns rather than from mode order — the two
        /// conductors move together in the even mode and in opposition in the odd one, whatever
        /// order <see cref="Lambda"/> happened to put them in. Returns false when the pattern is not
        /// unambiguous, in which case no Even/Odd alias should be published.
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
    /// The per-frequency modal quantities, plus <b>R-gen-5's measurement of what Route A threw
    /// away</b>.
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
        /// <b>R-gen-5.</b> The exact analogue of R-cpl-7's asymmetry residual: the thing being
        /// thrown away, surfaced rather than assumed small, so a user can see when Route A's
        /// perturbative treatment of loss is under strain. A pair for which this is 1e-9 is being
        /// decomposed essentially exactly; one for which it is 0.2 is not.
        /// </summary>
        public double ModeCouplingResidual => Math.Max(ZCouplingResidual, YCouplingResidual);
    }

    /// <summary>Above this the discarded mode coupling is worth telling the user about.</summary>
    public const double ModeCouplingWarnThreshold = 0.02;

    /// <summary>
    /// <b>Route A, steps 1–3.</b> Symmetrise, solve the lossless generalized eigenproblem, derive
    /// <c>Ti</c>. Frequency-independent — call once per sweep; <see cref="EvaluateAt"/> is the only
    /// per-frequency part.
    ///
    /// <para><b>Why <c>Ti</c> is rescaled, and why the rescale cannot move the terminal answer.</b>
    /// The physics fixes <c>Ti</c> only up to one scalar per mode: scaling column m of Ti by e
    /// multiplies <c>Zm_mm</c> by e and divides <c>Ym_mm</c> by e, so γ_m = √(Zm·Ym) is untouched
    /// while <c>Zc_m = √(Zm/Ym)</c> scales by e. The 2N-port blocks are <c>Tv·diag(x_m)·Ti⁻¹</c>
    /// with <c>x_m ∝ Zc_m</c>, so the e in <c>x</c> and the 1/e in <c>Ti⁻¹</c> cancel exactly
    /// (R-gen-3). The scaling is therefore <b>purely a reporting choice</b> — and R-gen-3a says
    /// which choice to make: the one under which a symmetric pair's reported Zc reproduces L7b's own
    /// Z_e and Z_o.</para>
    ///
    /// <para><b>The choice: "each conductor carries the mode's own current."</b> Take the strict
    /// biorthogonal partner <c>Tib = (Tvᵀ)⁻¹</c> and scale its column m so it is the least-squares
    /// closest to <c>Tv</c>'s column m — which, because <c>Tvᵀ·Tib = I</c>, is simply
    /// <c>Ti_m = Tib_m / ‖Tib_m‖²</c>. When the two are parallel (a symmetric pair: both columns are
    /// (1,1) and (1,−1)) it makes them <b>equal</b>, which is exactly L7b's convention, so
    /// <c>Zc_even</c> comes out as √(L_even/C_even) in ohms rather than as the mode's phase velocity.
    /// A pleasant consequence: under this rule <c>Zm_mm</c> and <c>Ym_mm</c> are ALSO invariant to
    /// scaling <c>Tv</c>'s columns, so no reported per-mode quantity depends on whatever
    /// normalisation LAPACK happened to hand back.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The eigensolve failed — reported by name rather
    /// than as a bare linear-algebra exception.</exception>
    public static GeneralModes DecomposeGeneral(RlgcModel rlgc)
    {
        ArgumentNullException.ThrowIfNull(rlgc);

        int n = rlgc.ConductorCount;
        if (n < 1) throw new InvalidOperationException("MoM: the RLGC model has no conductors.");

        // 1 — R-gen-1. NOT a tidy-up: NumFlat's GEVD reads only the upper triangle and never checks.
        var c    = Symmetrise(rlgc.CComplex);
        var c0   = Symmetrise(rlgc.C0);
        var l    = Symmetrise(rlgc.L);
        var lInv = Symmetrise(RlgcExtractor.Invert(l));

        // 2 — the lossless problem: [C]v = λ[L]⁻¹v, λ = 1/v_p². Both sides real symmetric definite.
        var cReal = RealPart(c);

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
                "MoM: the lossless modal eigenproblem [C]v = λ[L]⁻¹v could not be solved. Both " +
                "matrices must be symmetric positive definite; a degenerate cross-section (a " +
                "conductor with no capacitance to anything, or two conductors solved as one) is " +
                "the usual cause.", ex);
        }

        // 3 — R-gen-7. Order the modes by a PHYSICAL property, never by whatever LAPACK returned.
        var order = SortModes(vRaw, dRaw, n);

        var tv     = new Mat<double>(n, n);
        var lambda = new double[n];
        for (int m = 0; m < n; m++)
        {
            int src = order[m];
            lambda[m] = dRaw[src];

            // Sign is arbitrary in an eigenvector; pin it so a re-run reports the same Tv. Purely
            // cosmetic — every quantity below is invariant to it.
            int big = 0;
            for (int k = 1; k < n; k++)
                if (Math.Abs(vRaw[k, src]) > Math.Abs(vRaw[big, src])) big = k;
            double sign = vRaw[big, src] < 0 ? -1.0 : 1.0;

            for (int k = 0; k < n; k++) tv[k, m] = sign * vRaw[k, src];
        }

        return FromVoltageModalMatrix(rlgc, tv, lambda);
    }

    /// <summary>
    /// Derive <c>Ti</c> and every per-mode quantity from a GIVEN voltage modal matrix. The tail of
    /// <see cref="DecomposeGeneral"/>, exposed for two reasons that are both about not having a
    /// second copy of this arithmetic:
    ///
    /// <list type="bullet">
    ///   <item><b>R-gen-3's normalisation-invariance gate.</b> Scale Tv's columns by a deliberately
    ///     vicious spread, rebuild through this, and assert the 2N-port blocks are unchanged. That
    ///     exercises the production derivation rather than a re-statement of it — which matters,
    ///     because the test exists to catch a wrong <c>Ti</c>.</item>
    ///   <item>If a Route B ever produces a per-frequency Tv, this is where it plugs in.</item>
    /// </list>
    ///
    /// <para><paramref name="lambda"/> carries the lossless eigenvalues 1/v_p² through unchanged;
    /// they are a property of the modes, not of the scaling.</para>
    /// </summary>
    public static GeneralModes FromVoltageModalMatrix(
        RlgcModel rlgc, Mat<double> tv, IReadOnlyList<double> lambda)
    {
        ArgumentNullException.ThrowIfNull(rlgc);
        ArgumentNullException.ThrowIfNull(lambda);

        int n = tv.RowCount;
        var c  = Symmetrise(rlgc.CComplex);
        var c0 = Symmetrise(rlgc.C0);
        var l  = Symmetrise(rlgc.L);

        var tvInv = RlgcExtractor.Invert(tv);

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
                    $"MoM: modal matrix column {m} is degenerate — the modes are not independent.");

            double e = 1.0 / norm2;                       // the per-mode scale, Ti = Tib·diag(e)
            scale[m] = e;
            for (int k = 0; k < n; k++)
            {
                ti[k, m]    = tvInv[m, k] * e;            // Ti  = Tib · diag(e)
                tiInv[m, k] = tv[k, m] * norm2;           // Ti⁻¹ = diag(1/e) · Tvᵀ — EXACT, never a
                                                          // second numerical inversion.
            }
        }

        // The frequency-independent modal quantities. Lmodal, C0modal and Cmodal are diagonal to
        // round-off by construction for a GEVD basis (Tvᵀ[L]⁻¹Tv, Tvᵀ[C₀]Tv and Tvᵀ[C]Tv are all
        // diagonal there); Cmodal picks up off-diagonals only from a dielectric loss distribution
        // that is not proportional to [C] itself.
        var lModal  = DiagonalOf(Mul(Mul(tvInv, l), ti));
        var c0Modal = DiagonalOf(Mul(Mul(tiInv, c0), tv));
        var cModal  = DiagonalOf(Mul(Mul(ToComplex(tiInv), c), ToComplex(tv)));

        var eeff = new double[n];
        var lam  = new double[n];
        for (int m = 0; m < n; m++)
        {
            lam[m]  = lambda[m];
            eeff[m] = lambda[m] * EmConstants.C0 * EmConstants.C0;
        }

        return new GeneralModes(tv, ti, tvInv, tiInv, lam, eeff, lModal, c0Modal, cModal,
                                scale, rlgc.AsymmetryResidual);
    }

    /// <summary>
    /// <b>Route A, steps 4–5 — and step 5 IS the approximation.</b> Form the FULL modal matrices
    /// with loss in them, then keep only their diagonals — and report how much was discarded.
    /// </summary>
    public static ModalPoint EvaluateAt(RlgcModel rlgc, GeneralModes modes, double omegaRadS)
    {
        ArgumentNullException.ThrowIfNull(rlgc);
        ArgumentNullException.ThrowIfNull(modes);

        int n = modes.ModeCount;
        var r = Symmetrise(rlgc.RMatrix(omegaRadS));

        // Zm(ω) = Tv⁻¹·([R] + jω[L])·Ti — real [R] and [L], so the product is assembled once.
        var rModalFull = Mul(Mul(modes.TvInv, r), modes.Ti);
        var lModalFull = Mul(Mul(modes.TvInv, Symmetrise(rlgc.L)), modes.Ti);
        var zFull = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            zFull[i, j] = new Complex(rModalFull[i, j], omegaRadS * lModalFull[i, j]);

        // Ym(ω) = Ti⁻¹·(jω[C_complex])·Tv — R-mom-6: G rides in Im[C], so there is no separate [G].
        var cModalFull = Mul(Mul(ToComplex(modes.TiInv), Symmetrise(rlgc.CComplex)), ToComplex(modes.Tv));
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

    /// <summary><c>max_{i≠j}|M_ij| / min_i|M_ii|</c>. Zero for a 1×1 (there is nothing to discard).</summary>
    internal static double CouplingResidual(Mat<Complex> m)
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

    /// <summary>
    /// R-gen-7 — <b>order the modes deterministically, and say by what</b>: by λ ASCENDING, tie-broken
    /// by the eigenvector's own largest-magnitude conductor index so a degenerate pair still orders
    /// stably, and finally by the raw LAPACK index so the comparison is a total order.
    ///
    /// <para><b>A note on the brief's own wording.</b> R-gen-7 says "ascending — slowest mode first";
    /// those two are not the same thing, because λ = 1/v_p², so ascending λ is <i>fastest</i> mode
    /// first. The operative instruction (ascending, deterministic, a physical property rather than
    /// whatever LAPACK returned) is what is implemented; for a microstrip pair that puts the odd mode
    /// — more field in air, lower ε_eff, higher v_p — at index 0. Nothing downstream depends on which
    /// end is which: <see cref="GeneralModes.TryIdentifyEvenOdd"/> names the modes from their sign
    /// pattern, not from their position.</para>
    /// </summary>
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
            // A relative tolerance, so a genuinely degenerate pair (R-gen-6 — two identical
            // conductors far apart have the SAME velocity, guaranteed, not as a corner case) falls
            // through to the tie-break rather than ordering on round-off.
            double scale = Math.Max(Math.Abs(d[a]), Math.Abs(d[b]));
            if (Math.Abs(d[a] - d[b]) > 1e-9 * scale) return d[a].CompareTo(d[b]);
            int ba = BigIndex(a), bb = BigIndex(b);
            return ba != bb ? ba.CompareTo(bb) : a.CompareTo(b);
        });
        return idx;
    }

    // ── tiny dense helpers (N is the conductor count — single digits) ──────────────────────────

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

    internal static Mat<double> Mul(Mat<double> a, Mat<double> b)
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

    internal static Mat<Complex> Mul(Mat<Complex> a, Mat<Complex> b)
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
