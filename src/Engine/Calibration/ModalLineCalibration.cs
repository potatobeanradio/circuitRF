// PCAL4 — D5 AND D6 WHEN THE PORT REGION CARRIES MORE THAN ONE MODE.
//
// PWL-1 moved this file out of `Mom/` unchanged in arithmetic (it was `PlanarModalCalibration`), so the planar solver
// and the 3D solvers share it. The medium it is handed — Tv, and each mode's quasi-static β — is the caller's.
//
// `docs/sonnet-briefs/brief-portcal-4-modal-error-box.md`. Two ports on conductors that are coupled
// at the reference plane support TWO modes there, with different γ and different Z_c, and no
// assignment of scalars to those two ports can represent that — which is D6's wall, stated in the
// brief's §0 and not a parameterisation PCAL3's wider profile can reach. This file is the matrix
// generalisation: one error box per calibration GROUP, whose blocks are N×N.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE ONE STRUCTURAL FACT THIS WHOLE FILE RESTS ON, AND IT IS A SIMULATOR'S AND NOT A LAB'S
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Real multiline TRL needs a reflect standard because its two error boxes are unrelated. D4 builds
// BOTH boxes of every standard here, as exact mirror images of one another — so with X the left
// box's 2N×2N wave cascade and F the forward/backward flip [[0,I],[I,0]],
//
//     Y = F X⁻¹ F                                                                            (1)
//
// exactly (it is the general "reverse a network" identity, T_rev = F T⁻¹ F). That is what makes the
// closed form below possible at all. With T(ℓ) the standard of plane-to-plane length ℓ,
//
//     T(ℓ)  = X · blkdiag(P(ℓ), P(ℓ)⁻¹) · Y,      P(ℓ) = Ψ·diag(e^{−γ_m ℓ})·Ψᵀ                (2)
//
// where Ψ is the (complex ORTHOGONAL — see D7' below) map from modal wave amplitudes to terminal
// wave amplitudes at the line's own Z_c. Writing W = X·blkdiag(Ψ, Ψ), (1) and (2) give two
// similarity statements with the SAME W:
//
//     M ≡ T(ℓ₂)T(ℓ₁)⁻¹ = W · blkdiag(E(Δℓ), E(Δℓ)⁻¹) · W⁻¹        E(ℓ) = diag(e^{−γ_m ℓ})     (3)
//     T(ℓ₁)·F          = W · [[0, E(ℓ₁)], [E(ℓ₁)⁻¹, 0]] · W⁻¹                                 (4)
//
// (3) is D5, one mode per conductor: the eigenvalues of M are e^{∓γ_mΔℓ} and its eigenvectors are
// W's columns. (4) is what replaces D6's redundant M₁₁ equation: it pins the per-mode scale the
// eigenvectors leave free, up to ONE scalar per mode, and every entry it does not pin is a residual
// that must vanish.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D5' — γ COMES OUT OF A DEGREE-N POLYNOMIAL, STILL IN CLOSED FORM, STILL WITH NO EIGENSOLVER
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// det T = S_LR/S_RL-determinant ratio = 1 for a reciprocal network, so det M = 1 and M's eigenvalues
// come in (μ, 1/μ) pairs. Its characteristic polynomial is therefore PALINDROMIC, and substituting
// s = μ + 1/μ = 2cosh(γΔℓ) reduces a degree-2N polynomial to degree N:
//
//     Q(s) = q_N(s) + c₁q_{N−1}(s) + … + c_{N−1}q₁(s) + c_N,   q_k(s) = μ^k + μ^{−k}
//
// with q₀ = 2, q₁ = s, q_k = s·q_{k−1} − q_{k−2}. At N = 1 that is Q(s) = s − tr(M), i.e. D5's own
// cosh(γΔℓ) = ½tr(M), unchanged. The coefficients come from Faddeev–LeVerrier (traces of powers of
// M, no factorisation), and the degree-N roots from Durand–Kerner — written here rather than
// depended on, as L8a's Bessel functions and D5's own acosh were, and for the same recorded reason.
//
// **The palindrome is CHECKED, not assumed** (`PalindromeResidual`): |c_k − c_{2N−k}| is a direct
// measure of how far the two solved standards are from being the reciprocal mirror-symmetric
// structures D4 built, and it is the first thing to look at when a group's answer is wrong.
//
// The eigenVECTORS are the null spaces of (M − μI), taken from the smallest singular value. That is
// where near-degeneracy bites: two modes with nearly equal γ make (M − μI) nearly rank-deficient by
// TWO, the null space is a plane rather than a line, and no amount of arithmetic recovers which line
// in it is which mode. `ModeSeparationDegrees` and `NullSpaceGap` report exactly that, and
// PCAL4's own refusal is drawn on them (R-pcal4-2, R-pcal4-6).
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D6' — THE ERROR BOX, AND THE TWO AMBIGUITIES D6 HAD BECOME 2N + N OF THEM
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// An eigenvector is determined up to a scale, so (3) gives W only as W₀·D with D = diag(d) unknown,
// 2N scalars. Substituting into (4): with H = W₀⁻¹·(T(ℓ₁)F)·W₀,
//
//     H_{m,N+m}·d_{N+m}/d_m = e^{−γ_m ℓ₁}   ⟹   r_m ≡ d_{N+m}/d_m = e^{−γ_m ℓ₁}/H_{m,N+m}
//
// and EVERY OTHER ENTRY OF H MUST BE ZERO. Those 4N²−2N entries are `CascadeResidual`; the pair
// H_{m,N+m}·H_{N+m,m} = 1 is `ReciprocalPairResidual`. Both are reported per frequency, and both are
// the habit this area already keeps — an honest measure of what was discarded, not a proven
// predictor of accuracy (PCAL1 §5 measured that the scalar version is not even correlated with the
// error, and the brief says in as many words not to assume the matrix one acquires a power the
// scalar one demonstrably lacks).
//
// That leaves N scalars — one per mode — and they are fixed by RECIPROCITY. With X₀ = W₀·blkdiag(I,
// diag(r)) and (A11⁰, A12⁰, A21⁰, A22⁰) its scattering blocks, the physical box is reciprocal
// (A12 = A21ᵀ) only for the per-mode rescaling B = diag(b) with
//
//     B² = (A12⁰)⁻¹·(A21⁰)ᵀ                                                                   (5)
//
// **and (5)'s right-hand side must come out DIAGONAL.** That is not an assumption: it is forced by
// Ψ being complex-orthogonal, and its off-diagonal is `GaugeResidual`. A11⁰ needs no correction at
// all and is the box's external reflection in the terminal basis exactly as it stands — which is a
// free check, since it must be symmetric.
//
// (5) leaves a SIGN per mode, and no bilinear condition can ever remove it: reciprocity, symmetry
// and the de-embedded line are all quadratic in the modal gauge. It is removed by the only linear
// comparison available — the mode's own terminal pattern, `Orient` below — and the margin of that
// decision is reported rather than assumed.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D7' — WHAT THE CALIBRATION CANNOT FIND, AND WHY THAT IS THE SAME SENTENCE D7 ALREADY WROTE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// D7 says the de-embedded S is referenced to the line's own Z_c and the calibration cannot find it.
// Here Z_c is a MATRIX and the statement is unchanged in substance (R-pcal4-4): what comes out of
// this file is the de-embedded scattering at MODAL ports, and turning those into the terminal ports
// a user asked for needs (a) the modal voltage matrix Tv and (b) each mode's own Z_c,m — both of
// which are <see cref="PlanarModalMedium"/>'s, taken from the standards' own electrostatics by the
// same differencing D7 already does, and both of which are reported separately.
//
// The gauge in which this file's boxes are handed over is fixed precisely by "Ti = Tv⁻ᵀ", under
// which Ψ is complex-orthogonal and (5) holds. Every other normalisation of Tv's columns cancels —
// scaling column m by c scales Z_c,m by 1/c² and leaves the modal wave amplitude invariant — which
// is why nothing here has to agree with anything there about column lengths, only about SIGNS.
//
// **Kernel A is still not an input (R-pcal4-5), and the case for lifting that rule got WEAKER
// here, not stronger.** Kernel A would hand over γ_m, Tv and Z_c,m for a coupled cross-section in
// milliseconds and exactly — and the brief's own gate 1 is kernel A. Taking it would make the gate
// a tautology, exactly as `PlanarDeembed`'s header says of the scalar case, and it would do so for
// the ONE quantity (γ_m) this file measures full-wave and kernel A does not compute at all. What is
// taken from the electrostatics is only what D7 already takes: a capacitance, on kernel B's own
// mesh, from kernel B's own static Green's function.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Calibration;

/// <summary>
/// One calibration GROUP's error box at one frequency: everything between the group's N delta-gap
/// sources and their common reference plane, as a reciprocal 2N-port whose external side is the N
/// ports' raw reference impedances and whose internal side is the port region's N MODES.
/// </summary>
/// <param name="A11">External reflection, N×N and symmetric. The one block that needs no gauge at
/// all — it is terminal on both indices.</param>
/// <param name="A12">Internal → external transmission. Equal to <paramref name="A21"/>ᵀ by
/// construction once the gauge is fixed.</param>
/// <param name="A21">External → internal transmission.</param>
/// <param name="A22">Internal reflection — the one facing the DUT, N×N and symmetric.</param>
/// <param name="Gamma">γ per mode, in the order <paramref name="A21"/>'s rows are in.</param>
/// <param name="ElectricalDegrees">βΔℓ per mode over the separation this frequency read.</param>
/// <param name="ModeSeparationDegrees">
/// The smallest |(γ_i − γ_j)·Δℓ| over mode pairs, in degrees — <b>R-pcal4-2's report.</b> This is
/// the quantity that decides whether the modes can be separated AT ALL: at zero the two eigenvalues
/// of the cascade coincide, the null space of (M − μI) is a plane, and which line in it is which
/// mode is not a question the arithmetic can answer.
/// </param>
/// <param name="NullSpaceGap">
/// The worst σ_min/σ_next over the 2N eigenvector solves — the same ill-conditioning seen from the
/// other side, and the one that catches a near-degeneracy the γ separation alone would not (two
/// modes with equal β and different α, say).
/// </param>
/// <param name="PalindromeResidual">How far M's characteristic polynomial is from palindromic,
/// relative to its largest coefficient. Non-zero means the two standards are not the reciprocal,
/// mirror-symmetric pair D4 builds.</param>
/// <param name="CascadeResidual">The largest entry of H that (4) says must vanish, relative to H's
/// largest entry.</param>
/// <param name="ReciprocalPairResidual">max|H_{m,N+m}·H_{N+m,m} − 1| — (4)'s redundant half.</param>
/// <param name="GaugeResidual">The off-diagonal of (5), relative to its diagonal. Non-zero means the
/// port region's modes and the line's modes are not the same modes.</param>
/// <param name="SignMargin">
/// How far from a coin toss the per-mode sign decision was, on [0, 1]: 1 is a mode whose measured
/// terminal pattern is exactly in phase (or exactly out of phase) with the electrostatic one, 0 is
/// a mode for which the two are at right angles and the choice carries no information.
/// </param>
/// <param name="QuasiStaticBetaError">
/// max_m |β_m(TRL) − β_m(electrostatic)| / β_m — <b>R-pcal4-4's separability, as a number.</b> The
/// de-embedding's own accuracy rests on the first; the reference impedance's on the second. A run in
/// which they disagree by 15 % has a perfectly good de-embedding and a reference impedance that is
/// 15 % out, and the two should never be read as one figure of merit.
/// </param>
public sealed record ModalErrorBox(
    Mat<Complex> A11,
    Mat<Complex> A12,
    Mat<Complex> A21,
    Mat<Complex> A22,
    IReadOnlyList<Complex> Gamma,
    IReadOnlyList<double>  ElectricalDegrees,
    double ModeSeparationDegrees,
    double NullSpaceGap,
    double PalindromeResidual,
    double CascadeResidual,
    double ReciprocalPairResidual,
    double GaugeResidual,
    double SignMargin,
    double QuasiStaticBetaError)
{
    /// <summary>How many modes — one per conductor of the group.</summary>
    public int ModeCount => Gamma.Count;
}

/// <summary>
/// D5' and D6' — the N-conductor two-line calibration. See the file header for the derivation and
/// for the one structural fact (mirror-image error boxes) that makes it a closed form rather than a
/// multiline-TRL optimisation.
/// </summary>
public static class ModalLineCalibration
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The 2N-port wave cascade. [b_L; a_L] = T·[a_R; b_R] — the BLOCK form of
    // RFNetwork.SToT2Port's own convention, so the 2-port case is that function entry for entry.
    // R-mom-14 does not apply: RFNetwork has no N-port T and this is not a second copy of one.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 2N-port wave cascade of a scattering matrix whose first N ports are one side.</summary>
    public static Mat<Complex> SToT(Mat<Complex> s, int n)
    {
        var srlInv = Invert(Block(s, n, 0, n));
        return Compose(
            Sub(Block(s, 0, n, n), Block(s, 0, 0, n) * srlInv * Block(s, n, n, n)),
            Block(s, 0, 0, n) * srlInv,
            Negate(srlInv * Block(s, n, n, n)),
            srlInv);
    }

    /// <inheritdoc cref="SToT"/>
    public static Mat<Complex> TToS(Mat<Complex> t, int n)
    {
        var t22Inv = Invert(Block(t, n, n, n));
        return Compose(
            Block(t, 0, n, n) * t22Inv,
            Sub(Block(t, 0, 0, n), Block(t, 0, n, n) * t22Inv * Block(t, n, 0, n)),
            t22Inv,
            Negate(t22Inv * Block(t, n, 0, n)));
    }

    /// <summary>The forward/backward flip [[0, I], [I, 0]] — the F of the file header.</summary>
    public static Mat<Complex> Flip(int n)
    {
        var f = new Mat<Complex>(2 * n, 2 * n);
        for (int i = 0; i < n; i++) { f[i, n + i] = Complex.One; f[n + i, i] = Complex.One; }
        return f;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D5' — the propagation constants
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// M's characteristic polynomial by Faddeev–LeVerrier: p(λ) = λ^m + c[0]λ^{m−1} + … + c[m−1].
    /// Traces of powers, no factorisation, and on a 4×4 or 6×6 it is a handful of multiplies.
    /// </summary>
    public static Complex[] CharacteristicPolynomial(Mat<Complex> m)
    {
        int n = m.RowCount;
        var c = new Complex[n];
        var mk = Identity(n);
        for (int k = 1; k <= n; k++)
        {
            mk = m * mk;
            Complex tr = Complex.Zero;
            for (int i = 0; i < n; i++) tr += mk[i, i];
            c[k - 1] = -tr / k;
            for (int i = 0; i < n; i++) mk[i, i] += c[k - 1];
        }
        return c;
    }

    /// <summary>
    /// The palindromic reduction: the degree-N polynomial in s = μ + 1/μ whose roots are
    /// 2cosh(γ_mΔℓ). Returns the monic polynomial's descending tail (N coefficients).
    /// </summary>
    public static Complex[] ReducedPolynomial(IReadOnlyList<Complex> c, int modes)
    {
        int n = modes;
        var q = new List<Complex[]> { new Complex[] { 2.0 }, new Complex[] { Complex.Zero, Complex.One } };
        for (int k = 2; k <= n; k++)
        {
            var a = q[k - 1];
            var b = q[k - 2];
            var next = new Complex[k + 1];
            for (int i = 0; i < a.Length; i++) next[i + 1] += a[i];
            for (int i = 0; i < b.Length; i++) next[i] -= b[i];
            q.Add(next);
        }

        var acc = new Complex[n + 1];
        for (int i = 0; i < q[n].Length; i++) acc[i] += q[n][i];
        for (int k = 1; k < n; k++)
        {
            var qq = q[n - k];
            for (int i = 0; i < qq.Length; i++) acc[i] += c[k - 1] * qq[i];
        }
        // The constant term is c_N itself and NOT c_N·q₀ — q₀ = 2, and writing it as a q at all
        // doubles it. At N = 1 that is the difference between cosh = tr(M) and cosh = ½tr(M).
        acc[0] += c[n - 1];

        var tail = new Complex[n];
        for (int i = 0; i < n; i++) tail[i] = acc[n - 1 - i] / acc[n];
        return tail;
    }

    /// <summary>How far <paramref name="c"/> is from palindromic, relative to its largest entry.</summary>
    public static double PalindromeResidual(IReadOnlyList<Complex> c)
    {
        int m = c.Count;                                // p is degree m with c[0..m-1] and c_0 = 1
        double scale = 1.0;
        foreach (var v in c) scale = Math.Max(scale, v.Magnitude);
        double worst = (c[m - 1] - Complex.One).Magnitude;     // c_{2N} must be det M = 1
        for (int k = 1; k <= m / 2; k++)
            worst = Math.Max(worst, (c[k - 1] - c[m - k - 1]).Magnitude);
        return worst / scale;
    }

    /// <summary>
    /// Durand–Kerner for a monic polynomial given its descending tail. Deterministic seed, so a
    /// re-run returns the roots in the same order — which matters only for reproducibility, since
    /// every consumer below re-orders them against a physical prediction.
    /// </summary>
    public static Complex[] Roots(IReadOnlyList<Complex> tail)
    {
        int n = tail.Count;
        if (n == 0) return [];
        if (n == 1) return [-tail[0]];

        var z = new Complex[n];
        var seed = new Complex(0.4, 0.9);
        for (int i = 0; i < n; i++) z[i] = Complex.Pow(seed, i + 1);

        Complex P(Complex x)
        {
            Complex v = Complex.One;
            for (int i = 0; i < n; i++) v = v * x + tail[i];
            return v;
        }

        for (int it = 0; it < 500; it++)
        {
            double move = 0;
            for (int i = 0; i < n; i++)
            {
                Complex d = Complex.One;
                for (int j = 0; j < n; j++) if (j != i) d *= z[i] - z[j];
                if (d.Magnitude < 1e-300) continue;
                Complex step = P(z[i]) / d;
                z[i] -= step;
                move = Math.Max(move, step.Magnitude);
            }
            if (move < 1e-15) break;
        }
        return z;
    }

    /// <summary>
    /// The unit null vector of (M − μI), from the smallest singular value, plus the GAP to the next
    /// one. A gap near 1 means the null space is not a line and the mode is not separable — which is
    /// the failure this method exists to make visible rather than to survive.
    /// </summary>
    public static (Complex[] Vector, double Gap) NullVector(Mat<Complex> m, Complex mu)
    {
        int n = m.RowCount;
        var a = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = m[i, j] - (i == j ? mu : Complex.Zero);

        var svd = a.Svd();
        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = Complex.Conjugate(svd.VT[n - 1, i]);
        double gap = svd.S[n - 2] > 0 ? svd.S[n - 1] / svd.S[n - 2] : 1.0;
        return (v, gap);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The whole step
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// γ per mode and the group's error box, from two standards, an expected β per mode, and the
    /// electrostatic modal matrix the signs and the pairing are read against.
    /// </summary>
    /// <param name="sShort">The short standard's raw 2N-port scattering, ports 1..N on one side.</param>
    /// <param name="sLong">The selected long standard's, same port order.</param>
    /// <param name="lShortM">The short standard's plane-to-plane length.</param>
    /// <param name="lLongM">The long one's.</param>
    /// <param name="expectedBetaDeltaL">
    /// βΔℓ predicted for each mode — the previous frequency's measured β scaled by frequency, or the
    /// electrostatic estimate at the first point. <b>The modes are ASSIGNED to these predictions,
    /// never taken in the order the root finder produced them</b> (R-pcal4-3): near a crossing two
    /// modes swap places in any ordering by magnitude, and an index that quietly follows the
    /// ordering rather than the physics reports a discontinuity that is not in the structure.
    /// </param>
    /// <param name="tv">
    /// The electrostatic voltage modal matrix, columns in the same order as
    /// <paramref name="expectedBetaDeltaL"/>. Used for exactly two things: pairing the measured
    /// modes with the electrostatic ones (through <paramref name="expectedBetaDeltaL"/>, which is
    /// derived from it) and the per-mode SIGN, which no bilinear condition can supply. It does not
    /// enter γ, the error box, or any residual.
    /// </param>
    public static ModalErrorBox Solve(
        Mat<Complex> sShort, Mat<Complex> sLong, double lShortM, double lLongM,
        IReadOnlyList<double> expectedBetaDeltaL, Mat<double> tv)
    {
        ArgumentNullException.ThrowIfNull(expectedBetaDeltaL);

        int n = expectedBetaDeltaL.Count;
        if (sShort.RowCount != 2 * n || sLong.RowCount != 2 * n)
            throw new ArgumentException(
                $"A {n}-conductor calibration group's standards are {2 * n}-ports, not " +
                $"{sShort.RowCount}- and {sLong.RowCount}-ports.", nameof(sShort));

        double dl = lLongM - lShortM;
        if (!(dl > 0)) throw new ArgumentOutOfRangeException(nameof(lLongM),
            "The two calibration standards have the same length.");

        var symShort = Symmetrise(sShort);
        var symLong  = Symmetrise(sLong);

        // ── D6'a — THE PRE-PEEL, AND IT IS NOT AN OPTIMISATION: WITHOUT IT THIS FAILS OUTRIGHT ──
        //
        // See the file header's own section. Forming T from S divides by S_RL, and a series delta
        // gap has a₂₁ ∝ ω, so at the bottom of a band |S_RL| ~ 1e-4 and T ~ 1e4. M = T₂T₁⁻¹ then
        // has O(1) entries computed as a cancellation of order |S_RL|^{-2N} — 1e8 at N = 1, which
        // is D5's own documented low-frequency amplification and survivable, and 1e16 at N = 2,
        // which is every digit a double has.
        //
        // The cure is a SIMILARITY, so it costs no accuracy at all: peel a crude per-conductor
        // scalar box off BOTH standards first. With X̂ block-diagonal and its mirror Ŷ = F X̂⁻¹F, the
        // peeled pair is T̃(ℓ) = X̂⁻¹ T(ℓ) Ŷ⁻¹ = (X̂⁻¹X)·T_line(ℓ)·(F(X̂⁻¹X)⁻¹F), i.e. the SAME
        // structure with X replaced by X̂⁻¹X — so M's eigenvalues, and therefore every γ_m, are
        // untouched, and the true box is recovered at the end as X̂·X̃.
        //
        // The crude box is the scalar two-line calibration of each conductor taken on its own, which
        // is not a physical description of a coupled pair and does not have to be: a preconditioner
        // only has to be well SCALED. It is D6 and D5 on the 2×2 sub-matrix of (port k at each end),
        // which is the same arithmetic the ungrouped path runs, and if it fails to produce a finite
        // box the pre-peel is skipped rather than a NaN propagated.
        var preShort = symShort;
        var preLong  = symLong;
        Mat<Complex>? xHat = null;

        var crude = new LineErrorBox[n];
        bool usable = true;
        for (int q = 0; q < n && usable; q++)
        {
            var a = Pair(symShort, q, n);
            var c2 = Pair(symLong,  q, n);
            var g = LineCalibration.Gamma(a, c2, dl);
            var box = LineDeembed.SolveErrorBox(a, c2, lShortM, lLongM, g.Gamma);
            crude[q] = box;
            usable = double.IsFinite(box.A11.Magnitude) && double.IsFinite(box.A22.Magnitude)
                  && box.A21.Magnitude > 0 && double.IsFinite(box.A21.Magnitude);
        }

        if (usable)
        {
            var both = new LineErrorBox[2 * n];
            for (int q = 0; q < n; q++) { both[q] = crude[q]; both[n + q] = crude[q]; }
            var ps = LineDeembed.Apply(symShort, both);
            var pl = LineDeembed.Apply(symLong,  both);
            if (IsFinite(ps) && IsFinite(pl))
            {
                preShort = ps;
                preLong  = pl;
                var d11 = new Mat<Complex>(n, n);
                var d21 = new Mat<Complex>(n, n);
                var d22 = new Mat<Complex>(n, n);
                for (int q = 0; q < n; q++)
                { d11[q, q] = crude[q].A11; d21[q, q] = crude[q].A21; d22[q, q] = crude[q].A22; }
                xHat = SToT(Compose(d11, d21, d21, d22), n);
            }
        }

        var t1 = SToT(preShort, n);
        var t2 = SToT(preLong, n);
        var m  = t2 * Invert(t1);

        var c = CharacteristicPolynomial(m);
        double palindrome = PalindromeResidual(c);
        var roots = Roots(ReducedPolynomial(c, n));

        // ── Assign the roots to the modes, and the 2π branch with them ────────────────────────
        var gamma = AssignBranches(roots, expectedBetaDeltaL, dl);

        // ── W₀, one eigenvector per mode per direction ────────────────────────────────────────
        var w0 = new Mat<Complex>(2 * n, 2 * n);
        double gap = 0;
        for (int mode = 0; mode < n; mode++)
        {
            Complex mu = Complex.Exp(-gamma[mode] * dl);
            var (fwd, gf) = NullVector(m, mu);
            var (bwd, gb) = NullVector(m, Complex.One / mu);
            for (int i = 0; i < 2 * n; i++) { w0[i, mode] = fwd[i]; w0[i, n + mode] = bwd[i]; }
            gap = Math.Max(gap, Math.Max(gf, gb));
        }

        double separation = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                separation = Math.Min(separation,
                    ((gamma[i] - gamma[j]) * dl).Magnitude * 180.0 / Math.PI);
        if (n == 1) separation = double.PositiveInfinity;

        // ── (4) — the per-mode ratio, and everything it says must vanish ──────────────────────
        var h = Invert(w0) * (t1 * Flip(n)) * w0;
        double hScale = 0;
        for (int i = 0; i < 2 * n; i++)
            for (int j = 0; j < 2 * n; j++) hScale = Math.Max(hScale, h[i, j].Magnitude);
        hScale = Math.Max(hScale, 1e-300);

        double cascade = 0, pair = 0;
        for (int i = 0; i < 2 * n; i++)
            for (int j = 0; j < 2 * n; j++)
            {
                bool structural = (i < n && j == i + n) || (j < n && i == j + n);
                if (!structural) cascade = Math.Max(cascade, h[i, j].Magnitude / hScale);
            }

        var r = Identity(n);
        for (int mode = 0; mode < n; mode++)
        {
            r[mode, mode] = Complex.Exp(-gamma[mode] * lShortM) / h[mode, n + mode];
            pair = Math.Max(pair, (h[mode, n + mode] * h[n + mode, mode] - Complex.One).Magnitude);
        }

        var x0 = w0 * Compose(Identity(n), new Mat<Complex>(n, n), new Mat<Complex>(n, n), r);
        if (xHat is { } pre) x0 = pre * x0;          // X = X̂·X̃ — the pre-peel put back, exactly
        var sx0 = TToS(x0, n);

        // ── (5) — reciprocity fixes the per-mode scale, up to a sign ──────────────────────────
        var a12 = Block(sx0, 0, n, n);
        var a21 = Block(sx0, n, 0, n);
        var k = Invert(a12) * Transpose(a21);

        double kDiag = 0, kOff = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i == j) kDiag = Math.Max(kDiag, k[i, j].Magnitude);
                else        kOff  = Math.Max(kOff,  k[i, j].Magnitude);
        double gauge = kDiag > 0 ? kOff / kDiag : 0.0;

        var b = Identity(n);
        for (int mode = 0; mode < n; mode++) b[mode, mode] = Complex.Sqrt(k[mode, mode]);

        // ── the sign, and the only linear comparison there is ─────────────────────────────────
        var unsigned = TToS(x0 * Compose(b, new Mat<Complex>(n, n), new Mat<Complex>(n, n), b), n);
        var signs = Orient(Block(unsigned, n, 0, n), tv, out double margin);
        var bSigned = b * signs;

        var xs  = TToS(x0 * Compose(bSigned, new Mat<Complex>(n, n), new Mat<Complex>(n, n), bSigned), n);

        var degrees = new double[n];
        double betaErr = 0;
        for (int mode = 0; mode < n; mode++)
        {
            degrees[mode] = gamma[mode].Imaginary * dl * 180.0 / Math.PI;
            double predicted = expectedBetaDeltaL[mode];
            if (predicted > 0)
                betaErr = Math.Max(betaErr,
                    Math.Abs(gamma[mode].Imaginary * dl - predicted) / predicted);
        }

        return new ModalErrorBox(
            Block(xs, 0, 0, n), Block(xs, 0, n, n), Block(xs, n, 0, n), Block(xs, n, n, n),
            gamma, degrees, separation, gap, palindrome, cascade, pair, gauge, margin, betaErr);
    }

    /// <summary>
    /// <b>The per-mode SIGN, from the one linear comparison available.</b> A21 in the TERMINAL basis
    /// is Ψ·A21ˢ, and a feed region connects each port predominantly to its own conductor — so row m
    /// of A21ˢ follows column m of Ψ, whose sign pattern is column m of <paramref name="tv"/>'s.
    /// The decision is made on the PHASE of that correlation relative to the mode with the largest
    /// one, because every mode's correlation carries the same unknown common factor (the feed's own
    /// transmission phase) and only the relative half is a decision.
    ///
    /// <para><b>It is a binary choice with a reported margin, not a fit.</b> Ψ and Tv differ by
    /// Z_c^{−½}·(scale), which cannot reverse a dominant component of a passive cross-section, so
    /// the comparison is robust wherever the modes are separable at all — and where they are not,
    /// <see cref="ModalErrorBox.ModeSeparationDegrees"/> has already said so.</para>
    /// </summary>
    public static Mat<Complex> Orient(Mat<Complex> a21Modal, Mat<double> tv, out double margin)
    {
        int n = tv.RowCount;
        var corr = new Complex[n];
        for (int mode = 0; mode < n; mode++)
        {
            Complex s = Complex.Zero;
            for (int j = 0; j < n; j++) s += a21Modal[mode, j] * tv[j, mode];
            corr[mode] = s;
        }

        int reference = 0;
        for (int mode = 1; mode < n; mode++)
            if (corr[mode].Magnitude > corr[reference].Magnitude) reference = mode;

        margin = 1.0;
        var signs = Identity(n);
        if (corr[reference].Magnitude <= 0) return signs;

        for (int mode = 0; mode < n; mode++)
        {
            double phase = (corr[mode] / corr[reference]).Phase;
            signs[mode, mode] = Math.Abs(phase) < Math.PI / 2 ? Complex.One : -Complex.One;
            if (mode != reference)
                margin = Math.Min(margin, Math.Abs(Math.Abs(phase) - Math.PI / 2) / (Math.PI / 2));
        }
        return signs;
    }

    /// <summary>
    /// Which root belongs to which mode, and which 2π. <b>Every permutation is scored</b> rather
    /// than the roots being taken in order: this is the whole of R-pcal4-3's "never resolve a branch
    /// by assuming the modes stay in the same order across frequency".
    /// </summary>
    private static Complex[] AssignBranches(IReadOnlyList<Complex> roots,
                                            IReadOnlyList<double> expectedBetaDeltaL, double dl)
    {
        int n = roots.Count;
        var best = new Complex[n, n];
        var cost = new double[n, n];

        for (int rIdx = 0; rIdx < n; rIdx++)
        {
            Complex s = roots[rIdx];
            Complex disc = Complex.Sqrt(s * s - 4.0);
            foreach (var mu in new[] { 0.5 * (s + disc), 0.5 * (s - disc) })
            {
                if (mu.Magnitude < 1e-300) continue;
                Complex g0 = -Complex.Log(mu);                      // βΔℓ in (−π, π]
                for (int mode = 0; mode < n; mode++)
                {
                    double want = expectedBetaDeltaL[mode];
                    int turns = (int)Math.Round((want - g0.Imaginary) / (2 * Math.PI));
                    Complex g = g0 + new Complex(0, 2 * Math.PI * turns);
                    if (g.Imaginary <= 0) continue;
                    double c = Math.Abs(g.Imaginary - want);
                    if (cost[rIdx, mode] == 0 || c < cost[rIdx, mode])
                    {
                        cost[rIdx, mode] = c;
                        best[rIdx, mode] = g / dl;
                    }
                }
            }
            for (int mode = 0; mode < n; mode++)
                if (cost[rIdx, mode] == 0 && best[rIdx, mode] == Complex.Zero)
                    cost[rIdx, mode] = double.PositiveInfinity;
        }

        var order = BestAssignment(cost, n);
        var gamma = new Complex[n];
        for (int mode = 0; mode < n; mode++) gamma[mode] = best[order[mode], mode];
        return gamma;
    }

    /// <summary>The permutation minimising the total cost. n! over a group of at most a handful of
    /// conductors; a Hungarian solver here would be more code than the thing it replaces.</summary>
    private static int[] BestAssignment(double[,] cost, int n)
    {
        var index = new int[n];
        for (int i = 0; i < n; i++) index[i] = i;
        var best = (int[])index.Clone();
        double bestCost = double.PositiveInfinity;

        void Permute(int k)
        {
            if (k == n)
            {
                double total = 0;
                for (int mode = 0; mode < n; mode++) total += cost[index[mode], mode];
                if (total < bestCost) { bestCost = total; best = (int[])index.Clone(); }
                return;
            }
            for (int i = k; i < n; i++)
            {
                (index[k], index[i]) = (index[i], index[k]);
                Permute(k + 1);
                (index[k], index[i]) = (index[i], index[k]);
            }
        }
        Permute(0);
        return best;
    }

    /// <summary>
    /// The standard IS mirror-symmetric and reciprocal by construction (D4), so any departure is
    /// discretisation noise and averaging is the right use of a known symmetry — D6's own
    /// `Symmetrise`, one conductor wider, and for the identical recorded reason.
    /// </summary>
    private static Mat<Complex> Symmetrise(Mat<Complex> s)
    {
        int p = s.RowCount, n = p / 2;
        var r = new Mat<Complex>(p, p);
        for (int i = 0; i < p; i++)
            for (int j = 0; j < p; j++)
            {
                // reciprocity (i,j)↔(j,i) AND the end-to-end mirror (i,j)↔(i±n, j±n)
                int mi = i < n ? i + n : i - n, mj = j < n ? j + n : j - n;
                r[i, j] = 0.25 * (s[i, j] + s[j, i] + s[mi, mj] + s[mj, mi]);
            }
        return r;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Small dense helpers. Everything here is N ≤ 4 conductors, i.e. at most an 8×8.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 2-port of conductor k on its own — port k at one end and port N+k at the other,
    /// which is what the pre-peel's crude scalar calibration is measured on.</summary>
    private static Mat<Complex> Pair(Mat<Complex> s, int k, int n)
    {
        var r = new Mat<Complex>(2, 2);
        r[0, 0] = s[k, k];     r[0, 1] = s[k, n + k];
        r[1, 0] = s[n + k, k]; r[1, 1] = s[n + k, n + k];
        return r;
    }

    private static bool IsFinite(Mat<Complex> a)
    {
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++)
                if (!double.IsFinite(a[i, j].Real) || !double.IsFinite(a[i, j].Imaginary)) return false;
        return true;
    }

    internal static Mat<Complex> Identity(int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = Complex.One;
        return m;
    }

    internal static Mat<Complex> Block(Mat<Complex> a, int r0, int c0, int n)
    {
        var r = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) r[i, j] = a[r0 + i, c0 + j];
        return r;
    }

    internal static Mat<Complex> Compose(Mat<Complex> a11, Mat<Complex> a12,
                                         Mat<Complex> a21, Mat<Complex> a22)
    {
        int n = a11.RowCount;
        var r = new Mat<Complex>(2 * n, 2 * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                r[i, j] = a11[i, j]; r[i, n + j] = a12[i, j];
                r[n + i, j] = a21[i, j]; r[n + i, n + j] = a22[i, j];
            }
        return r;
    }

    internal static Mat<Complex> Transpose(Mat<Complex> a)
    {
        var r = new Mat<Complex>(a.ColCount, a.RowCount);
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++) r[j, i] = a[i, j];
        return r;
    }

    internal static Mat<Complex> Sub(Mat<Complex> a, Mat<Complex> b)
    {
        var r = new Mat<Complex>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++) r[i, j] = a[i, j] - b[i, j];
        return r;
    }

    internal static Mat<Complex> Negate(Mat<Complex> a)
    {
        var r = new Mat<Complex>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++) r[i, j] = -a[i, j];
        return r;
    }

    /// <summary>Gauss–Jordan with partial pivoting — kernel A's <c>RlgcExtractor.Invert</c>'s own
    /// routine, over complex, for matrices of order at most 8.</summary>
    internal static Mat<Complex> Invert(Mat<Complex> a)
    {
        int n = a.RowCount;
        var w = new Complex[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) w[i, j] = a[i, j];
            w[i, n + i] = Complex.One;
        }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int row = col + 1; row < n; row++)
                if (w[row, col].Magnitude > w[piv, col].Magnitude) piv = row;
            if (w[piv, col].Magnitude < 1e-300)
                throw new InvalidOperationException(
                    "Calibration: a modal calibration matrix is singular — the calibration group's " +
                    "conductors are not independent at the reference plane.");
            if (piv != col)
                for (int j = 0; j < 2 * n; j++) (w[col, j], w[piv, j]) = (w[piv, j], w[col, j]);

            Complex d = w[col, col];
            for (int j = 0; j < 2 * n; j++) w[col, j] /= d;
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                Complex f = w[row, col];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < 2 * n; j++) w[row, j] -= f * w[col, j];
            }
        }
        var res = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) res[i, j] = w[i, n + j];
        return res;
    }
}
