// PCAL4 — D7' : THE REFERENCE IMPEDANCE OF A CALIBRATION GROUP IS A MODAL ONE, AND IT IS STILL
// TAKEN FROM THE STANDARDS' OWN ELECTROSTATICS.
//
// D7 says the de-embedded s-parameters are referenced to the line's own Z_c and that the two-line
// calibration cannot find it — so Z_c comes from γ (full-wave, from the calibration) and C_pul
// (quasi-static, from DIFFERENCING the two standards' static capacitances, so every end effect
// cancels exactly). R-pcal4-4 keeps that sentence and makes it a matrix:
//
//     Tvᵀ[C]Tv = diag(C_m)        Z_c,m = γ_m / (jω C_m)        [Z_c] = Tv·diag(Z_c,m)·Tvᵀ
//
// with Tv the voltage modal matrix and, critically, **Ti = (Tvᵀ)⁻¹** — the STRICT biorthogonal
// normalisation and not R-gen-3a's reporting one. Under it, and only under it, the map from modal
// wave amplitudes to terminal wave amplitudes at [Z_c] is complex-ORTHOGONAL, which is the condition
// ModalLineCalibration's gauge step (5) rests on. Both normalisations are correct and they are for
// different purposes; the eigenproblem underneath them is ONE, and is
// `ModalDecomposition.VoltageModalMatrix`.
//
// **Tv's column LENGTHS do not matter and nothing here normalises them.** Scaling column m by c
// scales C_m by c² and Z_c,m by 1/c², and the modal wave amplitude — (ṽ + Z_c,m·ĩ)/(2√Z_c,m), with
// ṽ = Tv⁻¹v and ĩ = Tvᵀi — is invariant. Column SIGNS do matter, and are the one thing
// ModalLineCalibration has to agree with this file about; `Orient` is where that is settled.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHY [L] COMES FROM AN AIR-FILLED SOLVE AND NOT FROM KERNEL A (R-pcal4-5)
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Tv needs [L] as well as [C], and the quasi-TEM identity [L] = μ₀ε₀[C₀]⁻¹ is the only route to it
// that does not import another kernel's answer. So the standards are solved electrostatically TWICE
// — once in the dielectric and once with the dielectric removed — on kernel B's own mesh with kernel
// B's own static Green's function. That is D7's arithmetic, twice, over N drives instead of one, and
// it doubles the electrostatic step (R-pcal4-7 reports it).
//
// Reading [C], [C₀], Tv or Z_c off `QuasiStaticKernel` instead would be faster and exact, and it is
// exactly what `PlanarDeembed`'s header forbids — PCAL4's own gate 1 IS kernel A, so taking it makes
// the gate a tautology. The case is WEAKER here than in the scalar case, not stronger: the one
// quantity a coupled cross-section makes genuinely hard is γ_m, and γ_m is measured full-wave by the
// calibration and is not something kernel A computes at all.

using System.Numerics;
using NumFlat;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>PCAL4/D7' — a calibration group's quasi-static modal description, from its own standards.</b>
/// Frequency-independent: computed once per group and reused across the sweep, exactly as D7's
/// scalar <c>C_pul</c> is.
/// </summary>
/// <param name="Tv">The voltage modal matrix; column m is mode m's terminal voltage pattern.</param>
/// <param name="Lambda">1/v_p,m² — the lossless eigenvalues, s²/m².</param>
/// <param name="CModePerM">
/// <c>(Tvᵀ[C]Tv)_mm</c> — mode m's own per-unit-length capacitance, complex because R-mom-6 carries
/// G in its imaginary part. This is the C of <c>Z_c,m = γ_m/(jωC_m)</c>, and at one conductor it is
/// D7's own <c>C_pul</c> exactly.
/// </param>
/// <param name="CPerMetre">[C] per metre, from differencing the two standards.</param>
/// <param name="C0PerMetre">[C₀] per metre, the same geometry with the dielectric removed.</param>
/// <param name="ReportedZcScale">
/// <b>e_m — what a mode's Z_c has to be MULTIPLIED by to read in the convention a coupled-line
/// designer means by "Z_e" and "Z_o", which is R-gen-3a's and kernel A's.</b>
///
/// <para>It is not a correction and nothing in the algebra reads it. The calibration's gauge
/// requires <c>Ti = (Tvᵀ)⁻¹</c>; R-gen-3a's REPORTING convention instead takes
/// <c>Ti_m = Tib_m/‖Tib_m‖²</c> with <c>Tib = (Tvᵀ)⁻¹</c>, which is a different scaling of the same
/// modal currents (<c>e_m = 1/‖Tib_m‖²</c>, exactly 2 for a symmetric pair) and therefore a
/// different number for the same physics. Publishing the gauge's own value would be R-gen-3a's own
/// trap seen from the other side: a reference impedance that is a plausible number in units nobody
/// else uses — and it would disagree with kernel A by a factor of 2 on the series' own fixture,
/// where the two otherwise agree to the A-vs-B floor.</para>
/// </param>
/// <param name="ModeCouplingResidual">
/// The largest off-diagonal of <c>Tvᵀ[C]Tv</c> relative to its smallest diagonal — what the modal
/// reduction discarded. Non-zero because Tv diagonalises the LOSSLESS problem and [C] is complex;
/// it is `ModalDecomposition`'s own Route-A residual, and carries that finding's own caveat: an
/// honest measure of what was thrown away, not a predictor of the error.
/// </param>
public sealed record PlanarModalMedium(
    Mat<double>            Tv,
    IReadOnlyList<double>  Lambda,
    IReadOnlyList<Complex> CModePerM,
    Mat<Complex>           CPerMetre,
    Mat<Complex>           C0PerMetre,
    IReadOnlyList<double>  ReportedZcScale,
    double                 ModeCouplingResidual)
{
    /// <summary>How many modes — one per conductor of the group.</summary>
    public int ModeCount => Lambda.Count;

    /// <summary>The quasi-static β of mode m, rad/m. The calibration's own branch prediction at the
    /// first frequency of a sweep, and the thing R-pcal4-4's separability is measured against.</summary>
    public double Beta(double fHz, int mode) => 2.0 * Math.PI * fHz * Math.Sqrt(Lambda[mode]);

    /// <summary>Z_c,m = γ_m/(jωC_m) — D7's formula, per mode, in the CALIBRATION's own gauge.</summary>
    public Complex Zc(Complex gamma, double fHz, int mode) =>
        gamma / (Complex.ImaginaryOne * 2.0 * Math.PI * fHz * CModePerM[mode]);

    /// <summary>The same number in the convention a coupled-line designer and kernel A both use —
    /// see <see cref="ReportedZcScale"/>. For REPORTING only.</summary>
    public Complex ReportedZc(Complex gamma, double fHz, int mode) =>
        Zc(gamma, fHz, mode) * ReportedZcScale[mode];

    /// <summary>
    /// Extract the modal description from a group's two calibration standards. Both are solved
    /// electrostatically with and without the dielectric — four static solves of N drives each,
    /// sharing one factorisation per mesh per medium on the dense route.
    /// </summary>
    public static PlanarModalMedium Extract(
        PlanarStandard shortStd, PlanarStandard longStd, GroundedSlab slab,
        PlanarFillSettings? settings = null,
        PlanarFillCores? shortCores = null, PlanarFillCores? longCores = null)
    {
        ArgumentNullException.ThrowIfNull(shortStd);
        ArgumentNullException.ThrowIfNull(longStd);

        int n = shortStd.ConductorCount;
        var shortMap = shortStd.ConductorOfCell
                    ?? throw new InvalidOperationException(
                           "A calibration group's standard must carry its per-cell conductor map.");
        var longMap  = longStd.ConductorOfCell
                    ?? throw new InvalidOperationException(
                           "A calibration group's standard must carry its per-cell conductor map.");

        var c  = ModalDecomposition.Symmetrise(PlanarDeembed.CapacitanceMatrixPerMetre(
                     shortStd, longStd, slab, shortMap, longMap, n, airFilled: false,
                     settings, shortCores, longCores));
        var c0 = ModalDecomposition.Symmetrise(PlanarDeembed.CapacitanceMatrixPerMetre(
                     shortStd, longStd, slab, shortMap, longMap, n, airFilled: true,
                     settings, shortCores, longCores));

        // [L] = μ₀ε₀[C₀]⁻¹, so [L]⁻¹ = [C₀]/(μ₀ε₀) with no inversion at all — and μ₀ε₀ is 1/c² to
        // the last bit here (EmConstants.Eps0 is derived).
        double mu0Eps0 = EmConstants.Mu0 * EmConstants.Eps0;
        var lInv  = new Mat<double>(n, n);
        var cReal = new Mat<double>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                lInv[i, j]  = c0[i, j].Real / mu0Eps0;
                cReal[i, j] = c[i, j].Real;
            }

        var tv = ModalDecomposition.VoltageModalMatrix(cReal, lInv, out var lambda);

        // ── The column NORMALISATION is free, and it is taken so the REPORT reads in ohms ────────
        //
        // Nothing downstream depends on it — scaling column m by c scales C_m by c² and Z_c,m by
        // 1/c², and the modal wave amplitude is invariant — but the GEVD hands back columns scaled
        // so that Tvᵀ[L]⁻¹Tv = I, i.e. of order √(μ₀ε₀/C₀) ≈ 1e-6, which makes every REPORTED Z_c,m
        // come out around 1e8. That is R-gen-3a's own trap one file over, seen from the other side:
        // a reference impedance that is a plausible number in the wrong units. Largest entry = 1 per
        // column puts it back in ohms, and the sign pinning above already makes that entry positive.
        for (int m = 0; m < n; m++)
        {
            double big = 0;
            for (int i = 0; i < n; i++) big = Math.Max(big, Math.Abs(tv[i, m]));
            if (big > 0) for (int i = 0; i < n; i++) tv[i, m] /= big;
        }

        // Tvᵀ[C]Tv — diagonal for the lossless part by construction, and its off-diagonal is what
        // the loss's own modal coupling costs.
        var modeC = new Complex[n];
        double off = 0, dia = double.PositiveInfinity;
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                Complex sum = Complex.Zero;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) sum += tv[i, a] * c[i, j] * tv[j, b];
                if (a == b) { modeC[a] = sum; dia = Math.Min(dia, sum.Magnitude); }
                else off = Math.Max(off, sum.Magnitude);
            }

        // e_m = 1/‖Tib_m‖², Tib = (Tvᵀ)⁻¹ — R-gen-3a's reporting scale, computed the way
        // ModalDecomposition computes it rather than restated.
        var tib = Transpose(Invert(Transpose(tv)));
        var scale = new double[n];
        for (int m = 0; m < n; m++)
        {
            double sq = 0;
            for (int i = 0; i < n; i++) sq += tib[i, m] * tib[i, m];
            scale[m] = sq > 0 ? 1.0 / sq : 1.0;
        }

        return new PlanarModalMedium(tv, lambda, modeC, c, c0, scale, dia > 0 ? off / dia : 0.0);
    }

    private static Mat<double> Transpose(Mat<double> a)
    {
        var r = new Mat<double>(a.ColCount, a.RowCount);
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++) r[j, i] = a[i, j];
        return r;
    }

    private static Mat<double> Invert(Mat<double> a)
    {
        int n = a.RowCount;
        var c = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) c[i, j] = a[i, j];
        var inv = ModalLineCalibration.Invert(c);
        var r = new Mat<double>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) r[i, j] = inv[i, j].Real;
        return r;
    }
}
