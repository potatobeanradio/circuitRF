// PWL-1 — THE LINE-STANDARD CALIBRATION'S γ, AND THE SEPARATIONS IT IS MEASURED OVER, INDEPENDENT OF THE SOLVER.
//
// Moved out of `Mom/PlanarCalibration.cs` unchanged in arithmetic, so the planar solver and the 3D solvers share one
// implementation. Nothing here knows how a standard was solved: it takes the standards' raw S, their lengths, and an
// estimate of β to predict from. The planar solver supplies its estimate from the slab (ε_eff ≈ (εᵣ+1)/2); a 3D caller
// supplies its own. The planar derivations (D5, R-prt-6, M4) are kept with the code they describe.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D5 — γ COMES FROM A 2×2 EIGENVALUE, IN CLOSED FORM
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// With T₁, T₂ the wave-cascade matrices of the two standards' raw S,
//
//     M = T₂ T₁⁻¹ = T_A · diag(e^{−γΔℓ}, e^{+γΔℓ}) · T_A⁻¹
//
// so e^{∓γΔℓ} are M's eigenvalues. And it collapses further than "the quadratic formula": a
// RECIPROCAL 2-port has det T = S₁₂/S₂₁ = 1, so det M = 1, so the two eigenvalues multiply to 1 and
//
//     cosh(γΔℓ) = ½·tr(M)
//
// EXACTLY — no discriminant, no eigensolver, no library.
//
// β is then known only modulo 2π/Δℓ, and that is CHECKED rather than assumed (R-prt-6): the branch
// is anchored at the lowest frequency, where βΔℓ ≪ π, and continued upward by predicting the next
// point's βΔℓ from the last one scaled by frequency.

using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Calibration;

/// <summary>
/// γ from two line standards, the choice of separation per frequency, and the separations a band needs. Solver-independent:
/// see the file header.
/// </summary>
public static class LineCalibration
{
    /// <summary>The speed of light, m/s, exact by SI definition. The same value the EM kernels state, so a planar run is
    /// bit-identical through this class.</summary>
    public const double C0 = 2.99792458e8;

    /// <summary>TRL's own usable interval, and the interval where the error box's denominator is well away
    /// from its zero at βΔℓ = nπ. R-prt-6 flags every frequency outside it.</summary>
    public const double UsableLoDegrees = 20.0;
    public const double UsableHiDegrees = 160.0;

    /// <summary>
    /// The band ratio ONE line separation can cover — 160/20 = 8, straight off the usable interval.
    ///
    /// <para><b>R-prt-6, MEASURED: two standards do NOT suffice for a 2–20 GHz sweep.</b> That is a 10:1 band against an
    /// 8:1 interval, so no single Δℓ exists — aiming 90° at the geometric mean puts the edges at 28° and 285°, and the
    /// measured planar run read 59.7° / 122.5° / 345.4° at 2 / 6 / 20 GHz with the 20 GHz point 6.75e-2 wrong in β while the
    /// other two were 2.5e-4 and 7.8e-4. The number of separations is therefore DERIVED from the band rather than fixed,
    /// and the answer for 2–20 GHz is two of them (three standards).</para>
    /// </summary>
    public const double BandRatioPerSeparation = UsableHiDegrees / UsableLoDegrees;

    /// <summary>
    /// What the separation COUNT is actually derived from — half the theoretical 8:1, and the halving is measured rather
    /// than nervous. Δℓ is fixed before any solve from an ε_eff estimate that runs 15–20% low, so a separation lands ~1.17×
    /// higher in βΔℓ than it was aimed; designing to the full 8:1 leaves no room for that and puts the top of a 5:1 band at
    /// 157°, one point from the edge. Designing to 4:1 costs one extra standard and puts the same band at 105°.
    /// </summary>
    public const double DesignBandRatioPerSeparation = 4.0;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The separations a band needs
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The line SEPARATIONS a band needs — one per <see cref="DesignBandRatioPerSeparation"/> of band, geometrically
    /// spaced so each covers its own sub-band with βΔℓ centred on the usable interval. A narrow band gets one; 2–20 GHz gets
    /// two. <paramref name="separationAt"/> is the separation aimed at one frequency (<see cref="SeparationAt(double, double,
    /// double)"/> or <see cref="SeparationAt(double, Func{double, double}, double)"/>).
    /// </summary>
    public static double[] SuggestDeltas(double fLoHz, double fHiHz, Func<double, double> separationAt)
    {
        ArgumentNullException.ThrowIfNull(separationAt);
        double lo = Math.Max(Math.Min(fLoHz, fHiHz), 1.0);
        double hi = Math.Max(Math.Max(fLoHz, fHiHz), 1.0);

        double ratio = hi / lo;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Log(ratio) / Math.Log(DesignBandRatioPerSeparation)));

        var deltas = new double[n];
        for (int k = 0; k < n; k++)
        {
            // Each separation is centred on its own sub-band's geometric mean.
            double f = lo * Math.Pow(ratio, (k + 0.5) / n);
            deltas[k] = separationAt(f);
        }
        return deltas;
    }

    /// <summary>
    /// The separation aimed at <paramref name="targetDegrees"/> of βΔℓ at one frequency, from an ε_eff estimate. It only has
    /// to be right to a factor for βΔℓ to land inside an 8:1-wide usable interval.
    /// </summary>
    public static double SeparationAt(double fHz, double epsEffEstimate, double targetDegrees)
    {
        double lambda = C0 / (fHz * Math.Sqrt(epsEffEstimate));
        return lambda * (targetDegrees / 360.0);
    }

    /// <summary>The same, from a known β at that frequency, where an ε_eff does not describe the line (a slow wave on a
    /// conducting substrate).</summary>
    public static double SeparationAt(double fHz, Func<double, double> betaAt, double targetDegrees)
    {
        ArgumentNullException.ThrowIfNull(betaAt);
        return targetDegrees * Math.PI / 180.0 / betaAt(fHz);
    }

    /// <summary>
    /// β before any solve, from an ε_eff estimate. Only used to seed the first frequency's branch and separation choice, and
    /// it only has to be right to ~20% for either.
    /// </summary>
    public static double EstimateBeta(double epsEffEstimate, double fHz) =>
        2.0 * Math.PI * fHz * Math.Sqrt(epsEffEstimate) / C0;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D5 — γ from the two standards
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the two-line step produced, plus what R-prt-6 has to report about it.</summary>
    /// <param name="ElectricalDegrees">βΔℓ in degrees — the number that decides whether this
    /// frequency is inside TRL's usable interval.</param>
    /// <param name="Unwrapped">How many 2π were added to the principal branch. Non-zero is fine;
    /// non-zero at the FIRST frequency of a sweep means Δℓ is already too long.</param>
    public sealed record GammaResult(Complex Gamma, double ElectricalDegrees, int Unwrapped, bool Usable)
    {
        public double Alpha => Gamma.Real;
        public double Beta  => Gamma.Imaginary;

        public double EffectivePermittivity(double fHz)
        {
            double b = Beta / (2.0 * Math.PI * fHz / C0);
            return b * b;
        }
    }

    /// <summary>
    /// γ from the two standards' raw s-parameters. <paramref name="expectedBetaDeltaL"/> is the
    /// previous frequency's βΔℓ scaled by frequency; pass NaN at the first point, where the branch is
    /// anchored on βΔℓ &lt; π instead.
    /// </summary>
    public static GammaResult Gamma(Mat<Complex> sShort, Mat<Complex> sLong, double deltaLM,
                                    double expectedBetaDeltaL = double.NaN)
    {
        if (!(deltaLM > 0)) throw new ArgumentOutOfRangeException(nameof(deltaLM));

        var m = RFNetwork.SToT2Port(sLong) * Inverse2(RFNetwork.SToT2Port(sShort));
        Complex w = 0.5 * (m[0, 0] + m[1, 1]);          // det M = 1, so this IS cosh(γΔℓ)

        // cosh is EVEN and 2πj-periodic, so the solutions are ±g₀ + 2πjk. Both families have to be
        // considered together.
        //
        // MEASURED, AND IT WAS A REAL BUG: the obvious rule — "negate g₀ if Re g₀ < 0, because a
        // passive line has α ≥ 0" — is WRONG, because negating flips β as well. On the FR-4 fixture
        // at 20 GHz the principal value came back as (−0.061, +2.2005) with a true αΔℓ of only
        // +0.016: α is two orders of magnitude smaller than β here, so its extracted SIGN is noise,
        // and flipping on it turned a correct β = 804 into 1492. β is the well-determined half, so
        // β is what selects the branch; Re is used only to break a tie when there is no prediction
        // to select against.
        Complex g0 = Acosh(w);

        Complex best = default;
        double  bestErr = double.PositiveInfinity;
        int     k = 0;

        foreach (double sign in new[] { +1.0, -1.0 })
        {
            Complex c  = sign * g0;
            int     kk = double.IsNaN(expectedBetaDeltaL)
                ? (c.Imaginary < 0 ? 1 : 0)                       // anchor βΔℓ into (0, 2π)
                : (int)Math.Round((expectedBetaDeltaL - c.Imaginary) / (2 * Math.PI));

            Complex cc = c + new Complex(0, 2 * Math.PI * kk);
            if (cc.Imaginary <= 0) continue;                       // β > 0 on a forward-travelling wave

            double err = double.IsNaN(expectedBetaDeltaL)
                ? (cc.Real >= 0 ? 0.0 : 1.0)                       // no prediction: fall back on α ≥ 0
                : Math.Abs(cc.Imaginary - expectedBetaDeltaL);

            if (err < bestErr) { bestErr = err; best = cc; k = kk; }
        }

        Complex g = best;
        double deg = g.Imaginary * 180.0 / Math.PI;
        bool usable = deg >= UsableLoDegrees && deg <= UsableHiDegrees;

        return new GammaResult(g / deltaLM, deg, k, usable);
    }

    /// <summary>
    /// γ from a short standard and SEVERAL long ones, choosing per frequency the separation whose
    /// realised βΔℓ sits closest to the middle of the usable interval. This is multiline TRL's own
    /// idea reduced to what a simulator needs.
    /// </summary>
    /// <param name="expectedBetaPerMetre">The running β estimate — the previous frequency's measured
    /// β scaled by frequency, or an estimate at the first point. <b>The choice is made on the
    /// PREDICTION, never on the extracted value, and that is not a stylistic preference:</b> an
    /// aliased separation (βΔℓ past π) reports a wrapped electrical length that can land inside the
    /// usable interval by accident and score better than the correct one.</param>
    /// <param name="index">Which separation was chosen — reported, because "which standard produced
    /// this number" is exactly what someone debugging a bad point needs to know.</param>
    public static GammaResult GammaBest(Mat<Complex> sShort, IReadOnlyList<Mat<Complex>> sLong,
                                        IReadOnlyList<double> deltaLM, double expectedBetaPerMetre,
                                        out int index)
    {
        if (sLong.Count == 0 || sLong.Count != deltaLM.Count)
            throw new ArgumentException("Each long standard needs its own Δℓ.", nameof(sLong));

        index = SelectSeparation(deltaLM, expectedBetaPerMetre);
        return Gamma(sShort, sLong[index], deltaLM[index], expectedBetaPerMetre * deltaLM[index]);
    }

    /// <summary>
    /// Which separation <see cref="GammaBest"/> will choose — <b>a pure function of the Δℓ set and
    /// the PREDICTED β, so it can be asked BEFORE any standard has been solved.</b>
    ///
    /// <para>That is the whole point of it being extracted rather than inlined. <see cref="GammaBest"/>
    /// reads exactly two of the standards' raw matrices at any one frequency — the short line and the
    /// one long line named here — so a driver that knows the answer in advance can solve two standards
    /// instead of all of them.</para>
    ///
    /// <para>The scoring is deliberately keyed on the PREDICTION, never on an extracted electrical length —
    /// see <see cref="GammaBest"/>'s own parameter note for the aliasing reason.</para>
    /// </summary>
    public static int SelectSeparation(IReadOnlyList<double> deltaLM, double expectedBetaPerMetre)
    {
        ArgumentNullException.ThrowIfNull(deltaLM);
        if (deltaLM.Count == 0) throw new ArgumentException("No separations.", nameof(deltaLM));

        double centre = Math.Sqrt(UsableLoDegrees * UsableHiDegrees) * Math.PI / 180.0;

        int index = 0;
        double bestScore = double.PositiveInfinity;
        for (int i = 0; i < deltaLM.Count; i++)
        {
            double predicted = Math.Max(expectedBetaPerMetre * deltaLM[i], 1e-12);
            double score = Math.Abs(Math.Log(predicted / centre));
            if (score < bestScore) { bestScore = score; index = i; }
        }
        return index;
    }

    /// <summary>acosh, principal branch — written here rather than depended on, as the EM kernels' Bessel
    /// functions and Legendre nodes were, and for the same recorded reason.</summary>
    public static Complex Acosh(Complex w) => Complex.Log(w + Complex.Sqrt(w * w - Complex.One));

    /// <summary>The 2×2 inverse, in closed form. A general solver here would be silly.</summary>
    internal static Mat<Complex> Inverse2(Mat<Complex> a)
    {
        Complex det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        var r = new Mat<Complex>(2, 2);
        r[0, 0] =  a[1, 1] / det;
        r[0, 1] = -a[0, 1] / det;
        r[1, 0] = -a[1, 0] / det;
        r[1, 1] =  a[0, 0] / det;
        return r;
    }
}

/// <summary>
/// <b>The separations a band actually builds, and which of them (if any) is a short, frequency-independent one whose γ
/// the caller supplies rather than measures.</b>
/// </summary>
/// <param name="DeltaLM">The separations, measured ladder first and the supplied-γ one (if any) LAST — so a band with
/// none is <see cref="LineCalibration.SuggestDeltas"/>'s own array, index for index.</param>
/// <param name="QuasiStaticIndex">Which entry is the short, frequency-independent one, or −1 when there is none.</param>
/// <param name="CrossoverHz">The crossover this plan was drawn at, carried so a caller reporting which path a point took
/// does not recompute it.</param>
public readonly record struct LineSeparationPlan(double[] DeltaLM, int QuasiStaticIndex, double CrossoverHz)
{
    /// <summary>Whether <paramref name="fHz"/> takes the supplied γ under this plan. <b>The one place the question is
    /// asked</b> — a caller's separation choice, its γ source and its diagnostics all read it, and a second spelling would
    /// let a run solve one standard and calibrate against another.</summary>
    public bool IsQuasiStaticAt(double fHz) => QuasiStaticIndex >= 0 && fHz < CrossoverHz;

    /// <summary>A plan with measured separations only.</summary>
    public static LineSeparationPlan Measured(double[] deltaLM) => new(deltaLM, -1, 0.0);
}
