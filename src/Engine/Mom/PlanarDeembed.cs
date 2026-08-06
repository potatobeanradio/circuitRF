// L8d — D6 (the error box) and D7 (the reference impedance), which are the two halves of turning a
// raw solve into a publishable s-parameter.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D6 — THE ERROR BOX, IN CLOSED FORM, AND THE TWO SIGN AMBIGUITIES ARE DIFFERENT PROBLEMS
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// With both boxes mirror images of one another (D4 guarantees it) and the section between the
// reference planes a MATCHED line in its own Z_c (which is what D7's whole discussion is about), a
// standard of plane-to-plane length ℓ measures
//
//     M₁₁(ℓ) = a₁₁ + a₂₁²·a₂₂·x²/(1 − a₂₂²x²)          x = e^{−γℓ}
//     M₂₁(ℓ) = a₂₁²·x/(1 − a₂₂²x²)
//
// With γ known from D5 both x's are known, so with m_i = M₂₁(ℓ_i):
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
//     rejected sign is reported as a de-embedding-quality diagnostic — this area's standing habit
//     (AsymmetryResidual, ModeCouplingResidual, SumRuleResidual, FitResidual), with the same caveat:
//     it is an honest measure of what was discarded, not a proven predictor of accuracy.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D7 — THE DE-EMBEDDED S IS REFERENCED TO THE LINE'S OWN Z_c, AND THE CALIBRATION CANNOT FIND IT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// This is a fact about the method, not a gap in the implementation: the algebra above assumed the
// section between the planes is a MATCHED line, [[0,x],[x,0]], which is only true in the line's own
// Z_c. So:
//
//   • The de-embedding's accuracy and Z_c's accuracy are SEPARABLE and are reported separately. The
//     third-line gate lives entirely in the Z_c reference and is blind to Z_c's value.
//   • Z_c = γ/(jωC_pul), with C_pul from DIFFERENCING the two standards' static capacitances, so the
//     end effects cancel EXACTLY rather than being neglected. C(ℓ) is L8c's own
//     PlanarFill.ScalarPotentialMatrix at ω → 0, which is a product surface and already gated.
//   • C_pul is QUASI-STATIC, so Z_c inherits that. R-prt-8 measures the size of it rather than
//     waving at it.
//   • Kernel A is the ORACLE for Z_c, never an input. Reading Z_c or C_pul off QuasiStaticKernel and
//     feeding it into B would make the phase table's own "A and B agree on a uniform line" gate a
//     tautology and would import A's discretisation error into B's answer.
//   • The final renormalisation is RFNetwork.SToS. R-mom-14's rule, again: no second implementation.

using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One port's error box: everything between the delta-gap source and the reference plane, as a
/// reciprocal 2-port whose external side is the raw reference impedance and whose internal side is
/// the line's own Z_c.
/// </summary>
/// <param name="A11">External reflection.</param>
/// <param name="A22">Internal reflection — the one facing the DUT.</param>
/// <param name="A21">Transmission. Determined only up to sign; see the file header.</param>
/// <param name="ConsistencyResidual">How well the two standards agreed on <paramref name="A11"/>
/// with the chosen sign of <paramref name="A22"/>, relative to |a₁₁|. Small is good; it is a
/// diagnostic, not a proven predictor.</param>
/// <param name="RejectedResidual">The same quantity for the sign that was NOT chosen. A ratio near 1
/// means the sign was decided by noise.</param>
public sealed record PlanarErrorBox(
    Complex A11,
    Complex A22,
    Complex A21,
    double  ConsistencyResidual,
    double  RejectedResidual);

/// <summary>The whole per-port calibration at one frequency: γ, the error box, and Z_c.</summary>
public sealed record PlanarPortCalibration(
    int                            PortNumber,
    PlanarCalibration.GammaResult  Gamma,
    PlanarErrorBox                 Box,
    Complex                        Zc,
    double                         CPerMetre);

public static class PlanarDeembed
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D6 — the error box
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The error box from two standards and an already-extracted γ. <paramref name="previousA21"/>
    /// carries the branch across a sweep; pass null at the first frequency.
    /// </summary>
    public static PlanarErrorBox SolveErrorBox(
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

        return new PlanarErrorBox(bestA11, bestA22, bestA21, bestRes, otherRes);
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
    public static Mat<Complex> Apply(Mat<Complex> sMeasured, IReadOnlyList<PlanarErrorBox> boxes)
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
    // D7 — the reference impedance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The static capacitance of a whole meshed sheet to ground, at ω → 0. This is L8c's Tier 5
    /// harness promoted from the test project, because D7 needs it in production — it is assembled
    /// from <see cref="PlanarFill.ScalarPotentialMatrix"/>, which was already a product surface.
    /// </summary>
    public static double StaticCapacitance(PlanarMesh mesh, PlanarKernelTerms staticScalar,
                                           PlanarFillSettings? settings = null)
    {
        var st    = settings ?? PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(mesh, st);
        var p     = PlanarFill.ScalarPotentialMatrix(cores, staticScalar.With(st.Order, cores.RhoFloorM));

        int m = mesh.Cells.Count;
        var a   = new Mat<Complex>(m, m);
        var rhs = new Vec<Complex>(m);
        for (int i = 0; i < m; i++)
        {
            rhs[i] = Complex.One;
            for (int j = 0; j < m; j++) a[i, j] = p[i, j] / EmConstants.Eps0;
        }

        var q = a.Lu().Solve(rhs);
        Complex total = Complex.Zero;
        for (int i = 0; i < m; i++) total += q[i];
        return total.Real;
    }

    /// <summary>
    /// C per unit length, by DIFFERENCING the two standards' total static capacitances. The two
    /// lines are identical except for the bulk cells in the middle, so both end effects — the open
    /// ends and the port neighbourhoods — cancel EXACTLY rather than being neglected.
    /// </summary>
    public static double CapacitancePerMetre(PlanarStandard shortStd, PlanarStandard longStd,
                                             GroundedSlab slab, PlanarFillSettings? settings = null)
    {
        var terms = PlanarKernelTerms.StaticScalar(slab);
        double c1 = StaticCapacitance(shortStd.Mesh, terms, settings);
        double c2 = StaticCapacitance(longStd.Mesh,  terms, settings);
        double dl = longStd.LengthM - shortStd.LengthM;

        if (!(dl > 0))
            throw new InvalidOperationException("The two calibration standards have the same length.");
        return (c2 - c1) / dl;
    }

    /// <summary>Z_c = γ/(jωC_pul) — the standard γ-and-C route. See the file header for what it
    /// assumes and for why kernel A is its oracle rather than its input.</summary>
    public static Complex CharacteristicImpedance(Complex gamma, double cPerMetre, double fHz) =>
        gamma / (Complex.ImaginaryOne * 2.0 * Math.PI * fHz * cPerMetre);

    /// <summary>
    /// The published answer: de-embedded S renormalised from each port's own Z_c to its declared
    /// reference impedance. <b>R-prt-9 — this is <c>RFNetwork.SToS</c> and nothing else.</b>
    /// </summary>
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
