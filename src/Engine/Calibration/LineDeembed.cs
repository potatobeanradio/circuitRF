// PWL-1 — THE LINE-STANDARD ERROR BOX AND THE PEEL, INDEPENDENT OF THE SOLVER.
//
// Moved out of `Mom/PlanarDeembed.cs` unchanged in arithmetic (L8d's D6, LFE's lumped box, PCAL4's block peel and the
// renormalisations), so the planar solver and the 3D solvers share one implementation. What stayed in `Mom/` is D7's
// SOURCE of C_pul — the planar kernel's own electrostatics — because that is the one part that depends on how the
// structure was solved. A caller hands this file a γ and a C_pul (or a Z_c) and gets the box and the de-embedded S back.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D6 — THE ERROR BOX, IN CLOSED FORM, AND THE TWO SIGN AMBIGUITIES ARE DIFFERENT PROBLEMS
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// With both boxes of every standard mirror images of one another (the caller builds them so; the planar solver's D4 does
// it by construction) and the section between the reference planes a MATCHED line in its own Z_c, a standard of
// plane-to-plane length ℓ measures
//
//     M₁₁(ℓ) = a₁₁ + a₂₁²·a₂₂·x²/(1 − a₂₂²x²)          x = e^{−γℓ}
//     M₂₁(ℓ) = a₂₁²·x/(1 − a₂₂²x²)
//
// With γ known both x's are known, so with m_i = M₂₁(ℓ_i):
//
//     a₂₂² = (m₂/x₂ − m₁/x₁) / (m₂x₂ − m₁x₁)
//     a₂₁² = m_i(1 − a₂₂²x_i²)/x_i
//     a₁₁  = M₁₁(ℓ_i) − a₂₁²a₂₂x_i²/(1 − a₂₂²x_i²)
//
// The denominator (m₂x₂ − m₁x₁) is ∝ (x₂² − x₁²) and vanishes at βΔℓ = nπ. That is the SAME zero
// TRL's usable interval is drawn around, which is why R-prt-6's [20°, 160°] check is not a separate
// precaution but the same one.
//
// TWO SQUARE ROOTS, AND THEY ARE NOT THE SAME KIND OF PROBLEM:
//
//   • a₂₁ = ±√(a₂₁²) CANCELS EXACTLY when the two ports are identical. The de-embedding below
//     divides by a₂₁(i)·a₂₁(j), so an identical pair contributes a₂₁², which is unambiguous. It does
//     NOT cancel when the two ports have different widths — there it is a hard π in S₂₁, invisible in
//     a magnitude plot. Resolved by continuity in frequency from the principal root.
//   • a₂₂ = ±√(a₂₂²) does NOT cancel, and it is resolved by the REDUNDANT M₁₁ equation: the two
//     lengths must give the same a₁₁, and flipping a₂₂ flips the correction term. The residual of the
//     rejected sign is reported as a de-embedding-quality diagnostic — an honest measure of what was
//     discarded, not a proven predictor of accuracy.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D7 — THE DE-EMBEDDED S IS REFERENCED TO THE LINE'S OWN Z_c, AND THE CALIBRATION CANNOT FIND IT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The algebra above assumed the section between the planes is a MATCHED line, [[0,x],[x,0]], which is only true in the
// line's own Z_c. So the de-embedding's accuracy and Z_c's accuracy are SEPARABLE, Z_c = γ/(jωC_pul) needs a C_pul from
// somewhere else (the caller's electrostatics), and the final renormalisation is RFNetwork's — no second implementation.

using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Calibration;

/// <summary>
/// One port's error box: everything between the delta-gap source and the reference plane, as a
/// reciprocal 2-port whose external side is the raw reference impedance and whose internal side is
/// the line's own Z_c.
/// </summary>
/// <param name="A11">External reflection.</param>
/// <param name="A22">Internal reflection — the one facing the DUT.</param>
/// <param name="A21">Transmission. Determined only up to sign; see the file header.</param>
/// <param name="ConsistencyResidual">
/// How well the two standards agreed on <paramref name="A11"/> with the chosen sign of
/// <paramref name="A22"/>, relative to |a₁₁|. Small is good.
///
/// <para><b>PEEL — IT IS NOT A QUALITY MEASURE, AND AT THE BOTTOM OF A BAND IT POINTS THE WRONG
/// WAY.</b> The header's standing caveat — "an honest measure of what was discarded, not a proven
/// predictor of accuracy" — has a concrete case, and this is it. On the series' own board this
/// residual reads <b>1.95e-10 at 10 MHz and 9.38e-9 at 500 MHz</b>, i.e. it is 48× SMALLER exactly
/// where the published answer is 59 dB wrong. That is not a defect in the number: it measures the
/// two standards' agreement with each other, and two standards whose <c>x = e^{-γℓ}</c> are both
/// approaching 1 agree better and better while determining the error box less and less.
///
/// <para><b>What predicts the error is this residual multiplied by
/// <see cref="PeelAmplification"/></b>, which is <see cref="DeembedErrorFloor"/>. Read that, not
/// this. Measured against the uniform-line control — where a de-embedded S₁₁ must be exactly 0 —
/// the product tracks the realised |ΔS| to a ratio of 0.73-1.06 over three stacks, three mesh
/// densities and three decades of frequency (<c>src/Engine/Mom/RESOLVED.md</c> §PEEL).</para></para>
/// </param>
/// <param name="RejectedResidual">The same quantity for the sign that was NOT chosen. A ratio near 1
/// means the sign was decided by noise.</param>
public sealed record LineErrorBox(
    Complex A11,
    Complex A22,
    Complex A21,
    double  ConsistencyResidual,
    double  RejectedResidual)
{
    /// <summary>
    /// <b>PEEL — the factor by which an ABSOLUTE error anywhere in this box reaches the published
    /// s-parameter: |a₁₁| / |a₂₁|².</b> A property of the PORT, not of how well it was calibrated.
    ///
    /// <para><see cref="LineDeembed.Apply"/> forms <c>Y = (S_meas − a₁₁)/a₂₁²</c>. At the bottom of a band an
    /// edge port is a series gap capacitance, so <c>a₂₁ ∝ ω</c> and <c>a₁₁ → 1</c>: the numerator is
    /// the difference of two numbers that both approach 1 and the denominator is an ω². <b>That ω²
    /// is the amplifier</b>, and it is why the published error at the bottom of a band goes as 1/f
    /// while nothing that is published goes anywhere.</para>
    ///
    /// <para><b>It is NOT the two standards' separation.</b> That was measured directly and it is
    /// inert: a 30× longer second standard — βΔℓ from 0.40° to 11.8° at 10 MHz — moves the
    /// de-embedded |S₁₁| of a uniform line by under 6 %, and at 100 MHz a 30× separation is TEN
    /// TIMES worse. Lengthening Δℓ is the move this number exists to stop someone making.</para>
    /// </summary>
    public double PeelAmplification => A11.Magnitude / Math.Max((A21 * A21).Magnitude, double.Epsilon);

    /// <summary>
    /// <b>LFP — the DUT's OWN share of the raw-S error, in absolute |ΔS| on a raw s-parameter, and
    /// the second of the two terms <see cref="DeembedErrorFloor"/> amplifies.</b> 2e-8.
    ///
    /// <para><b>Why there has to be a second term at all.</b> PEEL derived the floor on a uniform
    /// line, where the DUT's own discretisation error is the same shape as the standards' and
    /// cancels, so <see cref="ConsistencyResidual"/> was the whole of it. On any other structure it
    /// does not cancel, and it is amplified by the same <see cref="PeelAmplification"/> — so a run
    /// carrying only the standards' share under-predicts. Measured on the shipped
    /// <c>examples/PDK PCells</c> spiral: the published floor read 1.106e-2 at 320 MHz against a
    /// realised error of about 0.5, i.e. <b>40× low, on a row that is 9× wrong</b>.</para>
    ///
    /// <para><b>Where 2e-8 comes from, and what it is NOT.</b> It is not the engine's numerical
    /// noise: R-lfp-1 measured that directly by re-solving the same coil with one knob moved at a
    /// time (the DCIM fit's sample count, tolerance, order and path extent; the fill's self, touch,
    /// near, mid and far quadrature; the radial table; the accumulation order; LDLᵀ against LU) and
    /// the raw S moves by <b>5.1e-10 at 320 MHz and 3.3e-9 at 2.08 GHz</b>, dominated by the fill's
    /// SINGULAR quadrature (<c>SelfPanels</c> 4→6 alone is 5.08e-10) rather than by the fit. That is
    /// BELOW <see cref="ConsistencyResidual"/> on every fixture measured, so a floor built on it
    /// would change nothing. <b>2e-8 is the DISCRETISATION share</b> — the meshed structure's own
    /// departure from the drawn one, which no knob reaches (§4 of the brief measures REFINING as a
    /// net loss here, because the port's gap is one cell wide). It is read off the four rows of that
    /// spiral whose σ_max(S) &gt; 1 proves they are not a network, as |ΔS| ÷
    /// <see cref="PeelAmplification"/>: <b>2.0e-8, 4.8e-8, 7.6e-8, 1.0e-7</b> at 160 / 320 / 480 /
    /// 640 MHz. <b>The SMALLEST is taken</b>, because this is a floor and a floor must not
    /// over-claim.</para>
    ///
    /// <para><b>It is a floor and not a bound, and the direction it can be wrong in is stated.</b>
    /// On a uniform line the DUT's share is zero by cancellation, so on a line this term
    /// over-predicts — and in practice it does not bind there at all: on every fixture in
    /// <c>PeelConditioningTests</c> the standards' residual is 1.8e-8 to 5.6e-4, so
    /// <see cref="ConsistencyResidual"/> is the larger of the two and those floors are unchanged to
    /// the last bit. It binds on a part whose calibration is GOOD and whose port is BADLY
    /// CONDITIONED, which is exactly the case that reached a user.</para>
    /// </summary>
    public const double DutRawErrorFloor = 2e-8;

    /// <summary>
    /// <b>PEEL/LFP — what this port's de-embedding is expected to be wrong by, in |ΔS|:
    /// <c>max(<see cref="ConsistencyResidual"/>, <see cref="DutRawErrorFloor"/>) ×
    /// <see cref="PeelAmplification"/></c>.</b> This is the number to read; see
    /// <see cref="ConsistencyResidual"/> for why that one is not.
    ///
    /// <para><b>Two terms, one amplifier.</b> The peel divides by <c>a₂₁²</c>, so an ABSOLUTE error
    /// anywhere in the raw s-parameters it consumes reaches the answer multiplied by
    /// <see cref="PeelAmplification"/> — whether that error is the two standards disagreeing with
    /// each other (<see cref="ConsistencyResidual"/>) or the DUT's own raw solve being what it is
    /// (<see cref="DutRawErrorFloor"/>). PEEL published only the first and measured only the first,
    /// on a fixture where the second cancels.</para>
    ///
    /// <para><b>It is a FLOOR on the instrument's own error and not a bound on the answer.</b> It
    /// says nothing about radiation, and nothing about a DUT meshed so coarsely that its own
    /// physics is wrong. A run whose floor is 0.9 is publishing noise; a run whose floor is 1e-4 may
    /// still be wrong for reasons this cannot see.</para>
    ///
    /// <para><b>The two terms obey DIFFERENT frequency laws and the run's remedy sentence knows
    /// it</b> — the residual rises as ω while <c>|a₂₁|²</c> rises as ω², so the standards' share
    /// falls as 1/f and the DUT's as 1/f². the planar solver's band-edge extrapolation
    /// branches on which term binds.</para>
    /// </summary>
    public double DeembedErrorFloor =>
        Math.Max(ConsistencyResidual, DutRawErrorFloor) * PeelAmplification;

    /// <summary>
    /// <b>LFP — true when <see cref="DutRawErrorFloor"/> is the term that set
    /// <see cref="DeembedErrorFloor"/>,</b> which is what decides whether that floor falls as 1/f or
    /// as 1/f² and therefore what band edge the run's remedy sentence can offer.
    /// </summary>
    public bool FloorIsDutBound => ConsistencyResidual < DutRawErrorFloor;
}

/// <summary>
/// <b>One error box over one GROUP of the run's ports</b> — a 1×1 block for an ordinary port and
/// an N×N one for a calibration group, in <see cref="ModalLineCalibration"/>'s own basis.
/// </summary>
/// <param name="PortIndices">Which rows/columns of the measured s-matrix this block covers, in
/// the block's own order.</param>
public sealed record LineBlockBox(
    IReadOnlyList<int> PortIndices,
    Mat<Complex> A11, Mat<Complex> A12, Mat<Complex> A21, Mat<Complex> A22);

/// <summary>The error box, the peel and the renormalisation of a line-standard calibration. See the file header.</summary>
public static class LineDeembed
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D6 — the error box
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The error box from two standards and an already-extracted γ. <paramref name="previousA21"/>
    /// carries the branch across a sweep; pass null at the first frequency.
    /// </summary>
    public static LineErrorBox SolveErrorBox(
        Mat<Complex> sShort, Mat<Complex> sLong, double lShortM, double lLongM,
        Complex gamma, Complex? previousA21 = null)
    {
        // Symmetrise: the standard IS mirror-symmetric by construction, so any S₁₁ ≠ S₂₂ or
        // S₂₁ ≠ S₁₂ is discretisation noise. Averaging is the right use of a known symmetry, and it
        // is exactly what L7b-b's Symmetrise does for the same reason.
        Complex m11a = 0.5 * (sShort[0, 0] + sShort[1, 1]), m21a = 0.5 * (sShort[1, 0] + sShort[0, 1]);
        Complex m11b = 0.5 * (sLong[0, 0]  + sLong[1, 1]),  m21b = 0.5 * (sLong[1, 0]  + sLong[0, 1]);

        Complex x1 = Complex.Exp(-gamma * lShortM);
        Complex x2 = Complex.Exp(-gamma * lLongM);

        Complex a22sq = (m21b / x2 - m21a / x1) / (m21b * x2 - m21a * x1);

        // The sign of a₂₂ is decided by the REDUNDANT M₁₁ equation, not guessed.
        Complex bestA22 = Complex.Zero, bestA21 = Complex.Zero, bestA11 = Complex.Zero;
        double  bestRes = double.PositiveInfinity, otherRes = double.PositiveInfinity;

        Complex root = Complex.Sqrt(a22sq);
        foreach (var a22 in new[] { root, -root })
        {
            Complex d1 = Complex.One - a22 * a22 * x1 * x1;
            Complex d2 = Complex.One - a22 * a22 * x2 * x2;

            Complex a21sq = 0.5 * (m21a * d1 / x1 + m21b * d2 / x2);

            Complex a11FromShort = m11a - a21sq * a22 * x1 * x1 / d1;
            Complex a11FromLong  = m11b - a21sq * a22 * x2 * x2 / d2;

            double res = (a11FromShort - a11FromLong).Magnitude /
                         Math.Max((a11FromShort + a11FromLong).Magnitude * 0.5, 1e-300);

            if (res < bestRes)
            {
                otherRes = bestRes;
                bestRes  = res;
                bestA22  = a22;
                bestA21  = Complex.Sqrt(a21sq);
                bestA11  = 0.5 * (a11FromShort + a11FromLong);
            }
            else otherRes = Math.Min(otherRes, res);
        }

        // a₂₁'s branch: continuity in frequency. The global sign is free for a symmetric pair of
        // ports (it cancels) and is NOT free when the two ports differ, which is exactly why it is
        // carried rather than recomputed independently at every point.
        if (previousA21 is { } prev &&
            (bestA21 - prev).Magnitude > (-bestA21 - prev).Magnitude)
            bestA21 = -bestA21;

        return new LineErrorBox(bestA11, bestA22, bestA21, bestRes, otherRes);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // LFE — the error box of a GROUNDED edge port, as a lumped section
    // ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // SolveErrorBox above finds a GENERAL reciprocal box (three complex unknowns) from two standards
    // whose lengths differ by Δℓ. Its a₂₂ comes from (m21b/x₂ − m21a/x₁)/(m21b·x₂ − m21a·x₁), a
    // difference quotient whose information is ∝ (x₂² − x₁²), i.e. ∝ γΔℓ. With L8d's series gap that
    // never mattered — a₂₂ sits near ±1 and the box's multiple reflections carry it — but a GROUNDED
    // port is nearly transparent (|a₁₁|, |a₂₂| ~ 1e-2 on a 50 Ω line), and on the quasi-static path
    // the standards are sized from the substrate rather than from λ, so βΔℓ is 0.4° at 100 MHz. a₂₂ is
    // then noise, the one-parameter family the two short standards cannot separate is a SHIFT OF THE
    // REFERENCE PLANE, and an a₂₂ error of 0.03 is a 3 Ω error in a de-embedded 50 Ω line. Measured:
    // consistency residual 1e-2 at 100 MHz rising to 0.37 at 6 GHz, published Zc 46.7 Ω against
    // 51.7 Ω from the same raw solve fitted independently.
    //
    // The raw grounded-port solve itself says what the box is: fitted with a port-free two-length
    // model it is a series Z and a shunt Y — −49.7 pH and 14.9 fF at 100 MHz, −47.1 pH and 14.6 fF at
    // 6 GHz — with a fit residual of 5e-9 to 9e-6. So the box is SOLVED AS THAT: two complex unknowns
    // (series Z_s on the source side, shunt Y_p on the line side), least squares against all four
    // measurements of both standards, with γ and Z_c supplied. The plane-shift family is not in the
    // model (a lumped section has no length), which is the whole of what this buys, and the absolute
    // misfit is published as the consistency residual — scaled by |a₁₁| so DeembedErrorFloor reads
    // the misfit itself, in |ΔS|, as it does for the general box.
    //
    // Used only on the QUASI-STATIC path. Above the crossover the standards are sized to ~60° and the
    // general solve is well conditioned, so the general solve keeps that half of the band and nothing
    // there assumes the box is small against a wavelength.

    /// <summary>
    /// <b>LFE — the error box of a grounded edge port, solved as a lumped series-Z/shunt-Y
    /// section</b> against two standards with γ and the line's Z_c supplied. The header above says
    /// why the general solve cannot be used for it on short standards.
    /// </summary>
    /// <param name="z0">The raw reference impedance on the external (source) side.</param>
    /// <param name="zc">The line's own Z_c — the internal side's reference, as in D7.</param>
    public static LineErrorBox SolveLumpedErrorBox(
        Mat<Complex> sShort, Mat<Complex> sLong, double lShortM, double lLongM,
        Complex gamma, Complex zc, Complex z0)
    {
        Complex m11a = 0.5 * (sShort[0, 0] + sShort[1, 1]), m21a = 0.5 * (sShort[1, 0] + sShort[0, 1]);
        Complex m11b = 0.5 * (sLong[0, 0]  + sLong[1, 1]),  m21b = 0.5 * (sLong[1, 0]  + sLong[0, 1]);
        Complex[] meas = [m11a, m21a, m11b, m21b];

        // Unknowns in normalised form, u = (Z_s/z0, Y_p·z0), so both sit near zero and are O(1) per
        // unit of mismatch. The model is analytic in both, so a complex Gauss-Newton step with the
        // Hermitian normal equations is the textbook least-squares iteration.
        Complex u0 = Complex.Zero, u1 = Complex.Zero;
        Complex[] Model(Complex a, Complex b)
        {
            var s1 = StandardS(a * z0, b / z0, gamma, zc, z0, lShortM);
            var s2 = StandardS(a * z0, b / z0, gamma, zc, z0, lLongM);
            return [s1.S11, s1.S21, s2.S11, s2.S21];
        }

        for (int it = 0; it < 40; it++)
        {
            var f0 = Model(u0, u1);
            const double h = 1e-7;
            var fa = Model(u0 + h, u1);
            var fb = Model(u0, u1 + h);
            Complex jaa = 0, jab = 0, jbb = 0, ga = 0, gb = 0;
            for (int i = 0; i < 4; i++)
            {
                Complex da = (fa[i] - f0[i]) / h, db = (fb[i] - f0[i]) / h, r = meas[i] - f0[i];
                jaa += Complex.Conjugate(da) * da; jab += Complex.Conjugate(da) * db;
                jbb += Complex.Conjugate(db) * db;
                ga  += Complex.Conjugate(da) * r;  gb  += Complex.Conjugate(db) * r;
            }
            Complex det = jaa * jbb - jab * Complex.Conjugate(jab);
            if (det.Magnitude < 1e-300) break;
            Complex du0 = ( jbb * ga - jab * gb) / det;
            Complex du1 = (-Complex.Conjugate(jab) * ga + jaa * gb) / det;
            u0 += du0; u1 += du1;
            if (du0.Magnitude + du1.Magnitude < 1e-15) break;
        }

        var fit = Model(u0, u1);
        double misfit = 0;
        for (int i = 0; i < 4; i++) misfit = Math.Max(misfit, (meas[i] - fit[i]).Magnitude);

        var (a11, a21, a22) = LumpedBox(u0 * z0, u1 / z0, zc, z0);
        double scale = Math.Max(a11.Magnitude, 1e-300);

        // RejectedResidual carries the RIVAL's figure on the same scale: the general solve's own
        // consistency, re-expressed as an absolute misfit over this box's |a₁₁|. A ratio near 1 says
        // the two models agree about these standards; a large one is the ill-conditioning above.
        var general = SolveErrorBox(sShort, sLong, lShortM, lLongM, gamma);
        double rival = general.ConsistencyResidual * general.A11.Magnitude / scale;
        return new LineErrorBox(a11, a22, a21, misfit / scale, rival);
    }

    /// <summary>The box [series Z_s, then shunt Y_p] as S with the external port referenced to
    /// <paramref name="z0"/> and the internal one to <paramref name="zc"/>, in the √Z pseudo-wave
    /// convention <see cref="Renormalise"/> (RFNetwork) uses — the one in which a matched line of
    /// that Z_c reads [[0, x], [x, 0]], which is what <see cref="SolveErrorBox"/>'s cascade assumes.</summary>
    internal static (Complex A11, Complex A21, Complex A22) LumpedBox(Complex zs, Complex yp, Complex zc, Complex z0)
    {
        Complex a = 1 + zs * yp, b = zs, c = yp, d = 1;
        return AbcdToS(a, b, c, d, z0, zc) is var s ? (s.S11, s.S21, s.S22) : default;
    }

    private static (Complex S11, Complex S21, Complex S22) AbcdToS(
        Complex a, Complex b, Complex c, Complex d, Complex z1, Complex z2)
    {
        Complex den = a * z2 + b + c * z1 * z2 + d * z1;
        Complex s11 = (a * z2 + b - c * z1 * z2 - d * z1) / den;
        Complex s22 = (-a * z2 + b - c * z1 * z2 + d * z1) / den;
        Complex s21 = 2 * Complex.Sqrt(z1) * Complex.Sqrt(z2) / den;      // AD − BC = 1 (reciprocal)
        return (s11, s21, s22);
    }

    /// <summary>A standard as the lumped model sees it: box, line of length ℓ, mirrored box, at
    /// <paramref name="z0"/> on both ends.</summary>
    private static (Complex S11, Complex S21) StandardS(Complex zs, Complex yp, Complex gamma,
                                                        Complex zc, Complex z0, double lengthM)
    {
        Complex ch = Complex.Cosh(gamma * lengthM), sh = Complex.Sinh(gamma * lengthM);
        // P = [1 Zs; 0 1]·[1 0; Yp 1],  L = line,  Q = [1 0; Yp 1]·[1 Zs; 0 1]
        Complex pa = 1 + zs * yp, pb = zs, pc = yp, pd = 1;
        Complex la = ch, lb = zc * sh, lc = sh / zc, ld = ch;
        Complex qa = 1, qb = zs, qc = yp, qd = 1 + yp * zs;
        Complex ta = pa * la + pb * lc, tb = pa * lb + pb * ld, tc = pc * la + pd * lc, td = pc * lb + pd * ld;
        Complex a = ta * qa + tb * qc, b = ta * qb + tb * qd, c = tc * qa + td * qc, d = tc * qb + td * qd;
        var s = AbcdToS(a, b, c, d, z0, z0);
        return (s.S11, s.S21);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The de-embedding itself — general P-port, one matrix solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Peels one 2-port error box off each port of a P-port measurement.
    ///
    /// <para>Derivation, so nobody has to trust it: with Γ_e = diag(a₁₁), Γ_i = diag(a₂₂),
    /// T = diag(a₂₁), the wave bookkeeping at the reference planes gives
    /// <c>S_meas = Γ_e + T(I − S Γ_i)⁻¹ S T</c>. Writing <c>Y = T⁻¹(S_meas − Γ_e)T⁻¹</c> makes that
    /// <c>Y = (I − SΓ_i)⁻¹S</c>, hence <c>S = Y(I + Γ_i Y)⁻¹</c> — one inverse, any port count, and
    /// it degenerates to the 2-port T-matrix cascade exactly.</para>
    ///
    /// <para>Note what falls out: Y depends on a₂₁(i)·a₂₁(j), so for IDENTICAL ports it depends on
    /// a₂₁² alone and the square-root ambiguity cancels without being resolved. For unequal ports it
    /// does not, which is what the branch continuity in <see cref="SolveErrorBox"/> is for.</para>
    /// </summary>
    /// <returns>S at the reference planes, referenced to each port's own Z_c (D7).</returns>
    public static Mat<Complex> Apply(Mat<Complex> sMeasured, IReadOnlyList<LineErrorBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        int p = sMeasured.RowCount;
        if (boxes.Count != p)
            throw new ArgumentException($"{p} ports need {p} error boxes, not {boxes.Count}.", nameof(boxes));

        var y = new Mat<Complex>(p, p);
        for (int i = 0; i < p; i++)
            for (int j = 0; j < p; j++)
                y[i, j] = (sMeasured[i, j] - (i == j ? boxes[i].A11 : Complex.Zero))
                          / (boxes[i].A21 * boxes[j].A21);

        // M = I + Γ_i Y, then S = Y·M⁻¹, solved as Mᵀ Sᵀ = Yᵀ rather than by forming an inverse.
        var mt = new Mat<Complex>(p, p);
        for (int i = 0; i < p; i++)
            for (int j = 0; j < p; j++)
                mt[j, i] = (i == j ? Complex.One : Complex.Zero) + boxes[i].A22 * y[i, j];

        var lu = mt.Lu();
        var s  = new Mat<Complex>(p, p);
        for (int r = 0; r < p; r++)
        {
            var rhs = new Vec<Complex>(p);
            for (int c = 0; c < p; c++) rhs[c] = y[r, c];
            var col = lu.Solve(rhs);
            for (int c = 0; c < p; c++) s[r, c] = col[c];
        }
        return s;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // PCAL4 — THE SAME PEEL, WITH BLOCKS
    // ══════════════════════════════════════════════════════════════════════════════════════════


    /// <summary>
    /// <b>PCAL4 — <see cref="Apply"/> with the scalars promoted to blocks, and it is the SAME
    /// derivation.</b> With Γ_e, Γ_i and A21 block-diagonal over the groups,
    /// <c>S_meas = Γ_e + A21ᵀ(I − SΓ_i)⁻¹S A21</c>, so <c>Y = A21⁻ᵀ(S_meas − Γ_e)A21⁻¹</c> makes that
    /// <c>Y = (I − SΓ_i)⁻¹S</c> and <c>S = Y(I + Γ_i Y)⁻¹</c> — one inverse, any port count, and it
    /// degenerates to <see cref="Apply"/>'s division by <c>a₂₁(i)·a₂₁(j)</c> exactly when every block
    /// is 1×1.
    ///
    /// <para><b>It is a separate method and <see cref="Apply"/> is untouched, on purpose.</b> A run
    /// with no multi-conductor group must produce the bytes it produces today (R-pcal4-1), and the
    /// surest way to guarantee that is for it to run the same code — not the same arithmetic
    /// re-expressed over 1×1 matrices, which is the same arithmetic in a different ORDER.</para>
    /// </summary>
    public static Mat<Complex> ApplyBlocks(Mat<Complex> sMeasured, IReadOnlyList<LineBlockBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        int p = sMeasured.RowCount;

        var gammaE = new Mat<Complex>(p, p);
        var gammaI = new Mat<Complex>(p, p);
        var a21    = new Mat<Complex>(p, p);
        var seen   = new bool[p];

        foreach (var b in boxes)
        {
            int n = b.PortIndices.Count;
            for (int i = 0; i < n; i++)
            {
                int r = b.PortIndices[i];
                if ((uint)r >= (uint)p || seen[r])
                    throw new ArgumentException(
                        $"Port index {r} is out of range or covered by two error boxes.", nameof(boxes));
                seen[r] = true;
                for (int j = 0; j < n; j++)
                {
                    int c = b.PortIndices[j];
                    gammaE[r, c] = b.A11[i, j];
                    gammaI[r, c] = b.A22[i, j];
                    a21[r, c]    = b.A21[i, j];
                }
            }
        }
        for (int i = 0; i < p; i++)
            if (!seen[i])
                throw new ArgumentException($"Port {i} has no error box.", nameof(boxes));

        var a21Inv = ModalLineCalibration.Invert(a21);
        var y = ModalLineCalibration.Transpose(a21Inv)
              * ModalLineCalibration.Sub(sMeasured, gammaE) * a21Inv;

        // M = I + Γ_i Y, then S = Y·M⁻¹, solved as Mᵀ Sᵀ = Yᵀ rather than by forming an inverse —
        // Apply's own arrangement, kept so the two read as the one method they are.
        var m = gammaI * y;
        for (int i = 0; i < p; i++) m[i, i] += Complex.One;

        var mt = ModalLineCalibration.Transpose(m);
        var lu = mt.Lu();
        var s  = new Mat<Complex>(p, p);
        for (int r = 0; r < p; r++)
        {
            var rhs = new Vec<Complex>(p);
            for (int c = 0; c < p; c++) rhs[c] = y[r, c];
            var col = lu.Solve(rhs);
            for (int c = 0; c < p; c++) s[r, c] = col[c];
        }
        return s;
    }

    /// <summary>
    /// <b>PCAL4/D7' — the de-embedded answer, from MODAL ports at their own Z_c,m to the TERMINAL
    /// ports the user asked for, at their declared Z₀.</b>
    ///
    /// <para>It goes through Z rather than through a renormalisation because the modal-to-terminal
    /// step is a change of PORT VARIABLES, not of reference impedance: <c>v = Tv·ṽ</c> and
    /// <c>i = Ti·ĩ</c> with <c>Ti = (Tvᵀ)⁻¹</c>, so <c>[Z]_terminal = Tv·[Z]_modal·Tvᵀ</c> and there
    /// is nothing to renormalise until the terminal impedance exists. <see cref="RFNetwork.SToZ"/>
    /// and <see cref="RFNetwork.ZToS"/> do both ends — R-mom-14, no second conversion.</para>
    ///
    /// <para><paramref name="transform"/> is the whole run's block-diagonal Tv: a group's own Tv over
    /// its member ports, and 1 on the diagonal for every ordinary port, whose "modal" port IS its
    /// terminal port.</para>
    /// </summary>
    public static Mat<Complex> ModalToTerminal(Mat<Complex> sModal, Mat<Complex> transform,
                                               IReadOnlyList<Complex> zModal,
                                               IReadOnlyList<Complex> z0)
    {
        ArgumentNullException.ThrowIfNull(zModal);
        ArgumentNullException.ThrowIfNull(z0);

        var zm = new Complex[zModal.Count];
        for (int i = 0; i < zm.Length; i++) zm[i] = zModal[i];

        var z = RFNetwork.SToZ(sModal, zm);
        var t = transform * z * ModalLineCalibration.Transpose(transform);

        var newZ = new Complex[z0.Count];
        for (int i = 0; i < newZ.Length; i++) newZ[i] = z0[i];
        return RFNetwork.ZToS(t, newZ);
    }

    /// <summary>Z_c = γ/(jωC_pul) — the standard γ-and-C route. See the file header for what it
    /// assumes and for why kernel A is its oracle rather than its input.</summary>
    public static Complex CharacteristicImpedance(Complex gamma, double cPerMetre, double fHz) =>
        gamma / (Complex.ImaginaryOne * 2.0 * Math.PI * fHz * cPerMetre);

    /// <summary>
    /// <b>SI-SIG — Z_c = γ/(jωC*) with the shunt loss KEPT, for a conducting substrate.</b> The real C′
    /// above is only right while C″ is small. On 10 Ω·cm silicon at 5 GHz, C = 303 − j400 pF/m, and the
    /// real reading returns 44 − j22 Ω where the line has 27 + j13 Ω: the wrong sign of the phase, and
    /// an error of 0.43 in |S|. Used only where a material states σ, so no other result moves.
    /// </summary>
    public static Complex CharacteristicImpedance(Complex gamma, Complex cPerMetre, double fHz) =>
        gamma / (Complex.ImaginaryOne * 2.0 * Math.PI * fHz * cPerMetre);

    /// <summary>
    /// The published answer: de-embedded S renormalised from each port's own Z_c to its declared
    /// reference impedance. <b>R-prt-9 — this is <c>RFNetwork.SToS</c> and nothing else.</b>
    /// </summary>
    /// <summary>
    /// <b>LFE — the same renormalisation, in the convention the de-embedding actually produces.</b>
    /// <see cref="SolveErrorBox"/>'s cascade and <see cref="SolveLumpedErrorBox"/>'s model both take
    /// the uniform line to be <c>[[0, x], [x, 0]]</c> in its own reference — which is true of
    /// PSEUDO-waves normalised by √Z_c and false of power waves once Z_c is complex. So the S that
    /// <see cref="Apply"/> hands back is a pseudo-wave S, and converting it with the power-wave
    /// <see cref="Renormalise"/> (Kurokawa, through <c>RFNetwork.SToS</c>) reads Z_c's imaginary part
    /// as a reflection of order Im(Z_c)/Re(Z_c). On a lossy line Z_c is complex because of R/(jωL),
    /// most of all at the bottom of a band: measured on a 5 mm, 51.3 − j0.47 Ω laminate line at
    /// 100 MHz the power-wave route published |S₁₁| = 9.7e-3 where the line has 3e-4.
    ///
    /// <para>This goes through Z with √Z on both sides (RFNetwork's own <c>SToZ</c>/<c>ZToS</c>, which
    /// are the pseudo-wave pair), so it is exact for any complex Z_c and identical to
    /// <see cref="Renormalise"/> whenever every Z_c and Z₀ is real.</para>
    /// </summary>
    public static Mat<Complex> RenormalisePseudoWave(Mat<Complex> sAtZc, IReadOnlyList<Complex> zc,
                                                     IReadOnlyList<Complex> z0)
    {
        var z = RFNetwork.SToZ(sAtZc, [.. zc]);
        return RFNetwork.ZToS(z, [.. z0]);
    }

    public static Mat<Complex> Renormalise(Mat<Complex> sAtZc, IReadOnlyList<Complex> zc,
                                           IReadOnlyList<Complex> z0)
    {
        var oldZ = new Complex[zc.Count];
        var newZ = new Complex[z0.Count];
        for (int i = 0; i < zc.Count; i++) oldZ[i] = zc[i];
        for (int i = 0; i < z0.Count; i++) newZ[i] = z0[i];
        return RFNetwork.SToS(sAtZc, oldZ, newZ);
    }
}
