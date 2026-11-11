// brief-em3d-115 R-em3d115-4 — Palace's MODAL S on a shared port face to TERMINAL S, in the numeric layer.
//
// Pure functions: arrays in, arrays out. No file, no process. PalaceRun (src/Design/Em3d) reads port-S.csv, port-V.csv,
// port-Z.csv and the log's kₙ and hands their values here. The derivation and every choice behind it are in
// src/Design/RESOLVED.md § "Palace modal transform on a shared face — brief-em3d-124"; its harness is
// tools/palace-symmetry-spike/modal.py, which this file ports.
//
// ONE FACE, N ENTRIES. A terminal group is one port face carrying N WavePort entries: Mode k on entry k, the first entry
// Active (the only one Palace gives a Robin term), every entry its own excitation, each VoltagePath on its own terminal.
// Palace's S is then a MODAL matrix, and its two degenerate modes on a homogeneous face come out in an arbitrary mixture
// (Palace issue #328). What it also writes is the TOTAL field's line integral along every entry's path (V_wp), which is a
// terminal voltage whatever the mode basis. Per frequency, over all ports:
//
//     P = 1 + S_m                      (= Gᵀ C: C the modal amplitudes of the total transverse E, a + b)
//     M = V · P⁻¹                      (= T_V G⁻ᵀ, T_V[k, n] = ∫_path_k e_n · dl; per face, by least squares below)
//     T_I = M⁻ᴴ                        (TEM: G = T_Vᵀ T_I*, so G cancels from the currents)
//     G   = 1, except on a degenerate face: real symmetric, unit diagonal, g from |(M Gᵀ)_ii|² = Z_PV[i]
//     C   = G⁻ᵀ · P
//     K   = diag(Re k_active / Re k_m) per face   (the scalar Robin term is the Active entry's; an inactive mode is
//                                                  launched and absorbed at the Active entry's wavenumber)
//     I   = T_I · (2 − K C),   U = M P (= V, less M's off-face part, which is dropped and reported)
//           (EMerge, brief-emw-3: I = T_I · K (2 − C), ModalRobinForm.SharedWavenumber)
//     S_t = √y · (U − Z₀ I)(U + Z₀ I)⁻¹ · √z      (Z₀ each terminal's own reference, as FdtdPortTransform forms it)
//
// THREE CHOICES THAT CARRY THE RESULT (124, measured):
//  - G is fitted only on a face whose modes are DEGENERATE (every entry's Re kₙ within DegenerateTolerance of the Active
//    entry's). On a non-degenerate face G = 1: fitting it there moves S by ≤ 5.5e-4 and is not physics.
//  - g is REAL. A complex g lands on a wrong root with zero residual (6° of phase on 124's air pair).
//  - K uses REAL parts. Palace builds the Robin term and every mode's n×H from Re kₙ; the complex form misses what loss
//    does by 0.0085 against openEMS, the real form by 0.0004.
//
// A one-entry face (an ungrouped wave port) has G = 1 and K = 1, and the transform reduces to referring that port to a mode
// whose voltage along its path is real (the phase of its fitted M, brief-em3d-141) and renormalising it from its Z_PV to its
// Z₀ — what WavePortReferences.Rotate and Renormalise do for a run with no group (gate 3 holds the two equal). A grouped
// face needs no such step: its M is fitted whole, phase included, so U = M P is the field's own line integral.

using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Em3d;

/// <summary>One frequency of a Palace run with wave ports, in port order: the modal S (row = port, column = excitation), the
/// <c>V_wp</c> matrix (row = port, column = excitation), each entry's mode impedance Z_PV and its logged wavenumber kₙ.</summary>
public sealed record ModalTerminalSample(Complex[,] ModalS, Complex[,] V, double[] ZPv, Complex[] Kn);

/// <summary>How a face's Gram matrix is formed. <see cref="Fit"/> is the transform; the others are the wrong forms brief 115's
/// gate 4 shows failing.</summary>
public enum ModalGramForm { Fit, Identity, Complex }

/// <summary>How a face's Robin correction K is formed. <see cref="RealParts"/> is Palace's transform and
/// <see cref="SharedWavenumber"/> EMerge's; <see cref="Complex"/> and <see cref="None"/> are the wrong forms gate 4 shows
/// failing.</summary>
public enum ModalRobinForm
{
    /// <summary>Palace: an inactive entry launched at its own wavenumber and absorbed at the Active one's, D = 2 − K C.</summary>
    RealParts,
    Complex,
    None,
    /// <summary>
    /// brief-emw-3 R-emw3-1 — EMerge: every entry launched AND absorbed at its face's first wavenumber, D = K (2 − C), K from
    /// real parts as <see cref="RealParts"/> forms it. EMerge's port condition is γ = j β₁ and its source −2 j β₁ eₙ for every
    /// mode n of a face, so the port condition per mode gives the modal current dₙ = (β₁/βₙ)(2δₙⱼ − Cₙ). On a degenerate face
    /// K = 1 and this equals the others.
    /// </summary>
    SharedWavenumber,
}

/// <summary>One face's fit: whether its modes are degenerate, and the real g and the fit's largest relative residual when
/// they are (g 0 and residual 0 when not).</summary>
public sealed record ModalFaceFit(IReadOnlyList<int> Entries, bool Degenerate, Complex G, double Residual);

/// <summary>
/// The terminal S at one frequency and what the transform checked on the way: each face's fit, K, the largest part of V no
/// face's own modes explain (<see cref="MOffBlock"/>, relative to V's largest entry), |T_V,ii|² against Z_PV (relative), each
/// excited port's power |S_jj|² + Σ|S_ij|², the singular values' extremes and the largest |S_ij − S_ji|. <see cref="Error"/> names what could not be formed.
/// </summary>
public sealed record ModalTerminalResult(
    Complex[,]?                 S,
    IReadOnlyList<ModalFaceFit> Faces,
    Complex[,]?                 G,
    double[]                    K,
    double                      MOffBlock,
    double[]                    TvMismatch,
    double[]                    PortPower,
    double                      SigmaMin,
    double                      SigmaMax,
    double                      Reciprocity,
    string?                     Error);

public static class ModalTerminalTransform
{
    /// <summary>Relative spread of Re kₙ under which a face's modes are degenerate. Palace logs kₙ to four figures: 124's air
    /// faces agree to all four, its microstrip faces differ by 7–8 %.</summary>
    public const double DegenerateTolerance = 1e-3;

    /// <summary>Whether a face's modes travel at one speed: every Re kₙ within <see cref="DegenerateTolerance"/> of the first
    /// (the Active entry's).</summary>
    public static bool IsDegenerate(IReadOnlyList<Complex> kn)
        => kn.Count > 1 && kn.All(k => Math.Abs(k.Real - kn[0].Real) <= DegenerateTolerance * Math.Abs(kn[0].Real));

    /// <summary>
    /// The terminal S of one frequency. <paramref name="faces"/> lists each face's entries as indices into the sample's port
    /// order, the Active entry first; every port is on exactly one face (an ungrouped wave port is a face of one).
    /// <paramref name="z0"/> is each port's reference, real and positive.
    /// </summary>
    public static ModalTerminalResult Solve(ModalTerminalSample sample, IReadOnlyList<IReadOnlyList<int>> faces, double[] z0,
                                            ModalGramForm gram = ModalGramForm.Fit, ModalRobinForm robin = ModalRobinForm.RealParts)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(z0);
        int n = sample.ModalS.GetLength(0);
        if (sample.V.GetLength(0) != n || sample.ZPv.Length != n || sample.Kn.Length != n || z0.Length != n ||
            faces.Sum(f => f.Count) != n || faces.SelectMany(f => f).Distinct().Count() != n)
            throw new ArgumentException("The sample's matrices, the faces and the references do not describe the same ports.");

        ModalTerminalResult Fail(string why) => new(null, [], null, [], 0, [], [], 0, 0, 0, why);

        var P = ToMat(sample.ModalS);
        for (int i = 0; i < n; i++) P[i, i] += Complex.One;
        // M is block-diagonal by construction (a path on one face sees only that face's modes), so each face's block is fitted
        // from that face's own rows, V_f = M_f · P_f over every excitation, by least squares. P = 1 + S_m itself is never
        // inverted: on a line a whole number of half-wavelengths long its thru terms approach −1 and it is nearly singular
        // (the 3D Wave Ports Coupled Microstrip at 7.5 GHz: |T_V,ii|² off Z_PV by 4.8 % through the inverse, 0.08 % by the
        // fit), while each face's rows [1 + S_ff, S_fo] keep full rank. A one-entry face's T_V is √Z_PV in magnitude, the
        // impedance the renormalisation uses, and its fit's phase: unit power fixes a mode's magnitude and not its phase, and
        // Palace leaves the phase where its eigensolver put it (−9.9° on brief 141's buried line, +0.4° on the top one beside
        // it). Taking √Z_PV real there dropped that phase and made a mixed run's S non-reciprocal; with it, an ungrouped port
        // is treated as WavePortReferences.Rotate and Renormalise treat it. What the full V·P⁻¹ puts outside the blocks is not
        // used; what each face's fit leaves unexplained is the check that a terminal reads only its own face.
        int[] faceOf = new int[n];
        for (int f = 0; f < faces.Count; f++) foreach (int e in faces[f]) faceOf[e] = f;
        var V = ToMat(sample.V);
        var M = new Mat<Complex>(n, n);
        foreach (var face in faces)
        {
            var block = FitFace(V, P, face);
            if (face.Count == 1)
            {
                // brief-em3d-141 — the magnitude Z_PV states, the phase the fit finds (none when the path read nothing).
                Complex fitted = block[0, 0];
                double mag = Complex.Abs(fitted);
                M[face[0], face[0]] = mag > 0 && double.IsFinite(mag) ? Math.Sqrt(sample.ZPv[face[0]]) * fitted / mag
                                                                     : Math.Sqrt(sample.ZPv[face[0]]);
                continue;
            }
            for (int a = 0; a < face.Count; a++)
                for (int b = 0; b < face.Count; b++) M[face[a], face[b]] = block[a, b];
        }
                if (Singular(M)) return Fail("The wave ports' voltage matrix (V_wp) is singular: two entries' voltage paths see the same field.");

        // ── G: a real, symmetric, unit-diagonal Gram per degenerate face ─────────────────────────
        var G = Identity(n);
        var fits = new List<ModalFaceFit>(faces.Count);
        foreach (var face in faces)
        {
            bool degenerate = face.Count > 1 && IsDegenerate([.. face.Select(e => sample.Kn[e])]);
            if (face.Count < 2 || gram == ModalGramForm.Identity || (!degenerate && gram == ModalGramForm.Fit))
            {
                fits.Add(new ModalFaceFit(face, degenerate, 0, 0));
                continue;
            }
            if (face.Count != 2)
                return Fail($"A face of {face.Count} degenerate modes: its Gram matrix has {face.Count * (face.Count - 1) / 2} unknowns and " +
                            $"{face.Count} equations, which this transform does not solve.");
            int a = face[0], b = face[1];
            var (g, residual) = gram == ModalGramForm.Complex
                ? FitComplexG(M[a, a], M[a, b], M[b, a], M[b, b], sample.ZPv[a], sample.ZPv[b])
                : FitRealG(M[a, a], M[a, b], M[b, a], M[b, b], sample.ZPv[a], sample.ZPv[b]);
            G[a, b] = G[b, a] = g;
            fits.Add(new ModalFaceFit(face, degenerate, g, residual));
        }

        // ── K: the Active entry's Robin term, per face ───────────────────────────────────────────
        var K = new Complex[n];
        for (int i = 0; i < n; i++) K[i] = Complex.One;
        if (robin != ModalRobinForm.None)
            foreach (var face in faces)
                foreach (int e in face)
                    K[e] = robin is ModalRobinForm.RealParts or ModalRobinForm.SharedWavenumber
                        ? sample.Kn[face[0]].Real / sample.Kn[e].Real
                        : sample.Kn[face[0]].Real / sample.Kn[e];

        var C = G.Transpose().Inverse() * P;
        var D = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                D[i, j] = robin == ModalRobinForm.SharedWavenumber ? K[i] * ((i == j ? 2 : 0) - C[i, j])
                                                                   : (i == j ? 2 : 0) - K[i] * C[i, j];
        var I = M.Inverse().ConjugateTranspose() * D;
        var U = M * P;                      // V, less the dropped off-face part

        var A = new Mat<Complex>(n, n);     // incident:  U + Z0·I
        var B = new Mat<Complex>(n, n);     // reflected: U − Z0·I
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                A[i, j] = U[i, j] + z0[i] * I[i, j];
                B[i, j] = U[i, j] - z0[i] * I[i, j];
            }
        if (Singular(A)) return Fail("The terminals' incident waves are not independent, so no terminal S can be formed.");
        var BA = B * A.Inverse();
        var S = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) S[i, j] = BA[i, j] * Math.Sqrt(z0[j] / z0[i]);

        // ── the checks (brief 115 §5), on what Palace's files give ────────────────────────────────
        // Every face fitted on its own, one-entry faces included: what the fit leaves of V (a terminal reading another face's
        // modes) and |T_V,ii|² against Z_PV.
        var fit = new Mat<Complex>(n, n);
        foreach (var face in faces)
        {
            var block = FitFace(V, P, face);
            for (int a = 0; a < face.Count; a++)
                for (int b = 0; b < face.Count; b++) fit[face[a], face[b]] = block[a, b];
        }
        var unexplained = V - fit * P;
        double vMax = 0, off = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                vMax = Math.Max(vMax, V[i, j].Magnitude);
                off = Math.Max(off, unexplained[i, j].Magnitude);
            }
        var TV = fit * G.Transpose();
        var tv = new double[n];
        for (int i = 0; i < n; i++) tv[i] = Math.Abs(TV[i, i].Magnitude * TV[i, i].Magnitude - sample.ZPv[i]) / sample.ZPv[i];
        var power = new double[n];
        double recip = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                power[j] += S[i, j].Magnitude * S[i, j].Magnitude;
                recip = Math.Max(recip, (S[i, j] - S[j, i]).Magnitude);
            }
        var sv = ToMat(S).Svd().S;
        double sMax = double.NegativeInfinity, sMin = double.PositiveInfinity;
        for (int i = 0; i < sv.Count; i++) { sMax = Math.Max(sMax, sv[i]); sMin = Math.Min(sMin, sv[i]); }

        var gOut = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) gOut[i, j] = G[i, j];
        return new ModalTerminalResult(S, fits, gOut, [.. K.Select(k => k.Real)], vMax > 0 ? off / vMax : 0, tv, power, sMin, sMax, recip, null);
    }

    /// <summary>
    /// The real g of a two-mode face minimising Σ rᵢ², rᵢ = (|(M Gᵀ)ᵢᵢ|² − Zᵢ)/Zᵢ, with |(M Gᵀ)₁₁|² = |M₁₁ + g M₁₂|² and
    /// |(M Gᵀ)₂₂|² = |g M₂₁ + M₂₂|². Each rᵢ is a quadratic in g, so the objective is a quartic and its stationary points are
    /// a cubic's real roots: every one is evaluated and the least taken, which is what 124's seven starts were for. g stays
    /// inside (−1, 1), where a Gram of two unit modes is positive definite. Returns g and the largest |rᵢ| there.
    /// </summary>
    internal static (double G, double Residual) FitRealG(Complex m11, Complex m12, Complex m21, Complex m22, double z1, double z2)
    {
        // rᵢ = c0 + c1 g + c2 g²
        static (double, double, double) Coeffs(Complex a, Complex b, double z)
            => ((a.Magnitude * a.Magnitude - z) / z, 2 * (a * Complex.Conjugate(b)).Real / z, b.Magnitude * b.Magnitude / z);
        var (p0, p1, p2) = Coeffs(m11, m12, z1);
        var (q0, q1, q2) = Coeffs(m22, m21, z2);
        double R1(double g) => p0 + g * (p1 + g * p2);
        double R2(double g) => q0 + g * (q1 + g * q2);
        double F(double g) => R1(g) * R1(g) + R2(g) * R2(g);
        // F'/2 = Σ (c0 + c1 g + c2 g²)(c1 + 2 c2 g): a cubic
        double c3 = 2 * (p2 * p2 + q2 * q2);
        double c2 = 3 * (p1 * p2 + q1 * q2);
        double c1 = p1 * p1 + 2 * p0 * p2 + q1 * q1 + 2 * q0 * q2;
        double c0 = p0 * p1 + q0 * q1;
        const double edge = 1 - 1e-9;
        var candidates = new List<double> { -edge, 0, edge };
        candidates.AddRange(CubicRealRoots(c3, c2, c1, c0).Where(r => r > -edge && r < edge));
        double best = candidates.MinBy(F);
        return (best, Math.Max(Math.Abs(R1(best)), Math.Abs(R2(best))));
    }

    /// <summary>The wrong form gate 4 shows: g complex, fitted by Newton–Gauss from several real starts. Kept only to be
    /// measured against; never the transform.</summary>
    internal static (Complex G, double Residual) FitComplexG(Complex m11, Complex m12, Complex m21, Complex m22, double z1, double z2)
    {
        double[] Res(double x, double y)
        {
            var g = new Complex(x, y);
            return [((m11 + g * m12).Magnitude * (m11 + g * m12).Magnitude - z1) / z1,
                    ((m22 + g * m21).Magnitude * (m22 + g * m21).Magnitude - z2) / z2];
        }
        (Complex, double) best = (0, double.PositiveInfinity);
        for (double s = -0.9; s <= 0.9 + 1e-12; s += 0.3)
        {
            double x = s, y = 0;
            for (int it = 0; it < 200; it++)
            {
                var r = Res(x, y);
                const double h = 1e-7;
                var rx = Res(x + h, y); var ry = Res(x, y + h);
                double j11 = (rx[0] - r[0]) / h, j12 = (ry[0] - r[0]) / h, j21 = (rx[1] - r[1]) / h, j22 = (ry[1] - r[1]) / h;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 1e-18) { y += 1e-3; continue; }
                double dx = (j22 * r[0] - j12 * r[1]) / det, dy = (-j21 * r[0] + j11 * r[1]) / det;
                x -= dx; y -= dy;
                if (Math.Abs(dx) + Math.Abs(dy) < 1e-14) break;
            }
            var rr = Res(x, y);
            double cost = Math.Max(Math.Abs(rr[0]), Math.Abs(rr[1]));
            if (double.IsFinite(cost) && cost < best.Item2 - 1e-18) best = (new Complex(x, y), cost);
        }
        return best;
    }

    /// <summary>The real roots of a·x³ + b·x² + c·x + d (a quadratic or linear one when the leading terms vanish).</summary>
    internal static IReadOnlyList<double> CubicRealRoots(double a, double b, double c, double d)
    {
        double scale = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Max(Math.Abs(c), Math.Abs(d)));
        if (scale == 0) return [];
        if (Math.Abs(a) <= 1e-14 * scale)
        {
            if (Math.Abs(b) <= 1e-14 * scale) return Math.Abs(c) <= 1e-14 * scale ? [] : [-d / c];
            double disc = c * c - 4 * b * d;
            if (disc < 0) return [];
            double sq = Math.Sqrt(disc);
            return [(-c + sq) / (2 * b), (-c - sq) / (2 * b)];
        }
        // Depressed cubic t³ + p t + q, x = t − b/(3a)
        double B = b / a, C = c / a, Dd = d / a;
        double p = C - B * B / 3, q = 2 * B * B * B / 27 - B * C / 3 + Dd;
        double shift = -B / 3;
        double delta = q * q / 4 + p * p * p / 27;
        var roots = new List<double>();
        if (delta > 0)
        {
            double sq = Math.Sqrt(delta);
            roots.Add(Math.Cbrt(-q / 2 + sq) + Math.Cbrt(-q / 2 - sq) + shift);
        }
        else if (p == 0) roots.Add(shift);
        else
        {
            double r = 2 * Math.Sqrt(-p / 3);
            double phi = Math.Acos(Math.Clamp(3 * q / (p * r), -1, 1)) / 3;
            for (int k = 0; k < 3; k++) roots.Add(r * Math.Cos(phi - 2 * Math.PI * k / 3) + shift);
        }
        // one Newton step each, against the cubic's own conditioning
        for (int k = 0; k < roots.Count; k++)
        {
            double x = roots[k], f = ((a * x + b) * x + c) * x + d, df = (3 * a * x + 2 * b) * x + c;
            if (df != 0) roots[k] = x - f / df;
        }
        return roots;
    }

    /// <summary>
    /// brief-emw-3 R-emw3-5 — <see cref="ModalTerminalResult.MOffBlock"/> for a V other than the one <see cref="Solve"/> was
    /// given: each face's block fitted from that face's own rows of <paramref name="v"/>, and the largest part of
    /// <paramref name="v"/> the fit leaves unexplained, relative to its largest entry. EMerge's run forms the transform's V
    /// from its modes, which no face can fail to explain, so its off-face check reads the V it measured in the field.
    /// </summary>
    public static double OffFaceResidual(Complex[,] v, Complex[,] modalS, IReadOnlyList<IReadOnlyList<int>> faces)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(modalS);
        ArgumentNullException.ThrowIfNull(faces);
        int n = modalS.GetLength(0);
        var P = ToMat(modalS);
        for (int i = 0; i < n; i++) P[i, i] += Complex.One;
        var V = ToMat(v);
        var fit = new Mat<Complex>(n, n);
        foreach (var face in faces)
        {
            var block = FitFace(V, P, face);
            for (int a = 0; a < face.Count; a++)
                for (int b = 0; b < face.Count; b++) fit[face[a], face[b]] = block[a, b];
        }
        var unexplained = V - fit * P;
        double vMax = 0, off = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                vMax = Math.Max(vMax, V[i, j].Magnitude);
                off = Math.Max(off, unexplained[i, j].Magnitude);
            }
        return vMax > 0 ? off / vMax : 0;
    }

    /// <summary>A face's block of M: the least-squares solution of V_f = M_f · P_f over every excitation (column), from the
    /// face's own rows of V and of P = 1 + S_m — M_f = V_f P_fᴴ (P_f P_fᴴ)⁻¹.</summary>
    private static Mat<Complex> FitFace(Mat<Complex> V, Mat<Complex> P, IReadOnlyList<int> face)
    {
        int n = V.ColCount, k = face.Count;
        var vf = new Mat<Complex>(k, n);
        var pf = new Mat<Complex>(k, n);
        for (int a = 0; a < k; a++)
            for (int j = 0; j < n; j++) { vf[a, j] = V[face[a], j]; pf[a, j] = P[face[a], j]; }
        var ph = pf.ConjugateTranspose();
        return vf * ph * (pf * ph).Inverse();
    }

    private static Mat<Complex> ToMat(Complex[,] a)
    {
        var m = new Mat<Complex>(a.GetLength(0), a.GetLength(1));
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++) m[i, j] = a[i, j];
        return m;
    }

    private static Mat<Complex> Identity(int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = Complex.One;
        return m;
    }

    /// <summary>Whether the smallest singular value is below 1e-12 of the largest.</summary>
    private static bool Singular(Mat<Complex> m)
    {
        var s = m.Svd().S;
        double hi = 0, lo = double.PositiveInfinity;
        for (int i = 0; i < s.Count; i++) { hi = Math.Max(hi, s[i]); lo = Math.Min(lo, s[i]); }
        return !(hi > 0) || lo <= 1e-12 * hi;
    }
}
