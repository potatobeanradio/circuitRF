using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>One lossy surface's contribution to Wheeler's incremental-inductance rule.</summary>
/// <param name="Name">"conductor:strip", "ground" — what a diagnostic prints.</param>
/// <param name="DLdn">
/// ∂[L]/∂n for receding <i>this one surface</i>, H/m per metre of recession — the FULL N×N
/// derivative, not its [0,0] entry.
///
/// <para><b>R-cpl-2.</b> Kernel A receded every conductor together and read [0,0], and said so in
/// its own comment: "the per-conductor σ split only matters when they differ, which kernel A's
/// single-line scope does not exercise." For a coupled pair [R] is a matrix, so each conductor must
/// be receded <i>alone</i> to get its own columns — receding both together sums two surfaces into
/// one derivative and there is no way to take them apart again afterwards.</para>
/// </param>
/// <param name="SigmaSm">The surface's own conductivity, so each carries its own R_s.</param>
public sealed record LossSurface(string Name, Mat<double> DLdn, double SigmaSm);

/// <summary>
/// The frequency-independent per-unit-length model. <b>R-mom-11: [C], [C₀] and ∂L/∂n are
/// frequency-independent and are computed exactly once for a whole sweep</b> — this is the property
/// that makes v1 "dramatically snappier than the thing that replaces it", and it is easy to lose in
/// a later refactor, so <see cref="MatrixFillCount"/> exists to be asserted by a test rather than
/// protected by a comment.
/// </summary>
/// <param name="Eeff">
/// <b>The SINGLE-CONDUCTOR effective permittivity, C[0,0]/C₀[0,0], and nothing else.</b>
///
/// <para>R-cpl-1: a coupled pair has TWO effective permittivities — even and odd — and they differ
/// substantially, because the odd mode pulls far more field into the air gap. One number here is
/// not a rounding issue, it is the wrong physical quantity. The modal pair comes from
/// <c>ModalDecomposition</c>, which reads the matrices; nothing on the coupled path reads this
/// field. It survives because the single-line s-parameter path and the Kirschning–Jansen
/// dispersion correction both genuinely want a scalar, and both are single-microstrip-only.</para>
/// </param>
/// <param name="RdcPerM">
/// R-cpl-3: the DIAGONAL matrix of per-conductor DC series resistance, 1/(σ_k·A_k). Kernel A summed
/// every conductor into one scalar, which is only right when there is one — two conductors in
/// parallel do not have one series resistance between them.
/// </param>
/// <param name="AsymmetryResidual">
/// R-cpl-7: <c>max|C_ij − C_ji| / |C_ij + C_ji|</c> over the raw, un-symmetrised [C]. Zero for a
/// single conductor.
///
/// <para>Point collocation on a piecewise-constant basis does not produce a symmetric system matrix
/// — only a Galerkin discretisation would — so this residual is a <b>discretisation-error
/// indicator</b>, not a bug. L7b is the first phase to consume the off-diagonals, so it is surfaced
/// as a named number rather than silently averaged away: a user tightening the mesh should be able
/// to watch it fall.</para>
/// </param>
public sealed record RlgcModel(
    IReadOnlyList<string>      ConductorNames,
    Mat<Complex>               CComplex,
    Mat<double>                C0,
    Mat<double>                L,
    double                     Eeff,
    IReadOnlyList<LossSurface> LossSurfaces,
    Mat<double>                RdcPerM,
    double                     WheelerValidAboveHz,
    int                        MatrixFillCount,
    double                     AsymmetryResidual,
    IReadOnlyList<string>      Notes)
{
    /// <summary>How many conductors this model describes.</summary>
    public int ConductorCount => CComplex.RowCount;

    /// <summary>C′ = Re(C) — F/m, the single-conductor convenience. See <see cref="Eeff"/>.</summary>
    public double CPerM => CComplex[0, 0].Real;

    /// <summary>L — H/m, the single-conductor convenience. See <see cref="Eeff"/>.</summary>
    public double LPerM => L[0, 0];

    /// <summary>
    /// G = ω·C″ = −ω·Im(C_complex) — R-mom-6 done exactly. G ∝ ω for a constant tanδ falls out
    /// rather than being asserted. Single-conductor convenience; <see cref="GMatrix"/> is the
    /// general form.
    /// </summary>
    public double GPerM(double omegaRadS) => -omegaRadS * CComplex[0, 0].Imaginary;

    /// <summary>[G](ω) = −ω·Im([C]) — S/m.</summary>
    public Mat<double> GMatrix(double omegaRadS)
    {
        var g = new Mat<double>(CComplex.RowCount, CComplex.ColCount);
        for (int i = 0; i < g.RowCount; i++)
        for (int j = 0; j < g.ColCount; j++)
            g[i, j] = -omegaRadS * CComplex[i, j].Imaginary;
        return g;
    }

    /// <summary>
    /// R(ω) for the single-conductor case — <c>RMatrix(ω)[0,0]</c>. Kept because the single-line
    /// s-parameter path reads a scalar and there is no reason to make it index a 1×1.
    /// </summary>
    public double RPerM(double omegaRadS) => RMatrix(omegaRadS)[0, 0];

    /// <summary>
    /// [R](ω) = Σ_surfaces (R_s,k(ω)/µ₀)·(∂[L]/∂n)_k, with R_s = √(ωµ₀/2σ) = 1/(σδ). The DIAGONAL
    /// is floored by the DC value (R-mom-13): <c>R_ii = √(R_dc,ii² + R_wheeler,ii²)</c>. That blend
    /// is the standard smooth interpolation between two asymptotes, <b>not</b> physics — below
    /// <see cref="WheelerValidAboveHz"/> the rule's δ ≪ t premise fails and the DC floor is what is
    /// actually being reported.
    ///
    /// <para><b>The floor is diagonal-only, deliberately.</b> [R_dc] is diagonal (R-cpl-3 — a
    /// conductor's DC series resistance is its own), so applying the same √(a²+b²) blend to an
    /// off-diagonal would reduce to <c>|R_wheeler,ij|</c> and silently strip the SIGN of a mutual
    /// resistance. Off-diagonals pass through as computed.</para>
    /// </summary>
    public Mat<double> RMatrix(double omegaRadS)
    {
        int n = CComplex.RowCount;
        var skin = new Mat<double>(n, n);
        foreach (var s in LossSurfaces)
        {
            if (double.IsPositiveInfinity(s.SigmaSm) || s.SigmaSm <= 0) continue;  // perfect metal
            double rs = Math.Sqrt(omegaRadS * EmConstants.Mu0 / (2.0 * s.SigmaSm));
            // `rs / Mu0 * DLdn`, in that association — NOT `(rs / Mu0) * DLdn` hoisted out of the
            // loop. Re-associating moves the single-conductor answer by one ulp, and C1's gate is
            // that it does not move at all.
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                skin[i, j] += rs / EmConstants.Mu0 * s.DLdn[i, j];
        }

        var r = new Mat<double>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            r[i, j] = i == j
                ? Math.Sqrt(RdcPerM[i, i] * RdcPerM[i, i] + skin[i, i] * skin[i, i])
                : skin[i, j];
        return r;
    }
}

/// <summary>
/// Capacitance → RLGC (§5 of the brief).
/// <list type="number">
///   <item><b>[C]</b> — the charge solve with the real stackup and complex ε*.</item>
///   <item><b>[C₀]</b> — the same solve with every material replaced by air (all K = 0, so the
///     dielectric rows drop out and only the conductor block is solved).</item>
///   <item><b>ε_eff = C/C₀</b>.</item>
///   <item><b>[L] = µ₀ε₀[C₀]⁻¹</b> — the TEM identity. No second formulation.</item>
///   <item><b>G</b> — already in [C]'s imaginary part. Nothing further to compute.</item>
///   <item><b>[R]</b> — Wheeler's incremental inductance rule, below.</item>
/// </list>
///
/// <para><b>R-mom-12.</b> The naive reading of Wheeler — "recede every surface by δ/2, re-solve,
/// difference" — makes the recession frequency-dependent and forces a refill per frequency,
/// destroying R-mom-11 for no accuracy gain. ∂L/∂n here is a purely <i>geometric</i> derivative,
/// evaluated once by a single finite-difference recession; the frequency dependence enters only
/// through R_s. The recession is applied to <b>every</b> lossy surface — the signal conductors
/// (outline shrunk inward) <i>and</i> the ground plane (moved down, equivalently h → h+Δ). Omitting
/// the ground-plane term is the common error and it under-reports microstrip loss noticeably.</para>
///
/// <para>The perturbed geometry is re-meshed from the <i>same</i>
/// <see cref="ConductorMeshTemplate"/>, so the finite difference is not contaminated by the
/// discretisation changing underneath it.</para>
/// </summary>
public static class RlgcExtractor
{
    /// <summary>Recession as a fraction of min(thickness, width) — the brief's Δ = min(t, W)/50.</summary>
    public const double DefaultRecessionFraction = 1.0 / 50.0;

    public static RlgcModel Extract(EmProblem problem, EmMeshReport report,
                                    double recessionFraction = DefaultRecessionFraction)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(report);

        int fills = 0;
        var notes = new List<string>();

        // 1 & 2 — the two capacitance solves. Both frequency-independent (R-mom-11).
        var cComplex = ChargeSolver.MaxwellCapacitance(report.Mesh); fills++;
        var c0Mesh   = ChargeSolver.AirFilled(report.Mesh);
        var c0Cx     = ChargeSolver.MaxwellCapacitance(c0Mesh);      fills++;
        var c0        = RealPart(c0Cx);

        // 3 & 4
        double eeff = c0[0, 0] != 0 ? cComplex[0, 0].Real / c0[0, 0] : 1.0;
        var l = Scale(Invert(c0), EmConstants.Mu0 * EmConstants.Eps0);

        // R-cpl-7 — measured on the RAW [C], before anything symmetrises it. This is the number a
        // user tightening the mesh should be able to watch fall, so it must not be computed from an
        // already-averaged matrix, which would report zero by construction.
        double residual = ModalDecomposition.AsymmetryResidual(cComplex);
        int nCond = problem.Conductors.Count;
        var names0 = new List<string>(nCond);
        foreach (var c in problem.Conductors) names0.Add(c.Name);
        if (nCond > 1)
        {
            notes.Add($"[C] asymmetry residual max|C_ij − C_ji|/|C_ij + C_ji| = {residual:P2} " +
                      "— point collocation on a piecewise-constant basis is not symmetric, so this " +
                      "is a discretisation-error indicator and should fall under mesh refinement.");
            double diag = ModalDecomposition.DiagonalAsymmetry(cComplex);
            notes.Add($"[C] diagonal asymmetry |C₁₁ − C₂₂|/|C₁₁ + C₂₂| = {diag:P2} — on a pair the " +
                      "geometry says is symmetric this is mesh-quality only, and it too falls under " +
                      "refinement.");
            if (diag > ModalDecomposition.DiagonalAsymmetryWarnThreshold)
                notes.Add($"That diagonal asymmetry is above {ModalDecomposition.DiagonalAsymmetryWarnThreshold:P0}; " +
                          "the mesh is not resolving the two conductors alike, so the even/odd split " +
                          "carries that much error. Refine the mesh before trusting Z_e/Z_o.");

            if (residual > ModalDecomposition.AsymmetryResidualWarnThreshold)
                notes.Add($"That residual is above {ModalDecomposition.AsymmetryResidualWarnThreshold:P0}; the coupled " +
                          "off-diagonals carry that much discretisation error. Refine the mesh " +
                          "(raise MinCellsAcrossWidth or EdgeCells) before trusting tight-coupling results.");
        }

        // 6 — Wheeler.
        var outlines = new List<IReadOnlyList<EmPoint>>(nCond);
        foreach (var c in problem.Conductors) outlines.Add(Polygon2D.AsCcw(c.Outline));
        var names = names0;

        // ONE Δ shared by every conductor, not one per conductor. That keeps the single-line answer
        // bit-for-bit what it was, and for the symmetric pair L7b actually ships the conductors are
        // identical so a per-conductor Δ would be the same number anyway.
        double delta = RecessionDelta(outlines, report, recessionFraction);
        var surfaces = new List<LossSurface>();

        // (a) R-cpl-2 — signal conductors, ONE AT A TIME. Receding them together sums their
        // surfaces into a single derivative that cannot be taken apart again, which is exactly the
        // collapse kernel A's own comment described and this milestone opens up.
        for (int k = 0; k < nCond; k++)
        {
            var receded = new List<IReadOnlyList<EmPoint>>(outlines);
            receded[k] = Polygon2D.OffsetInward(outlines[k], delta);

            var recMesh = BoundaryMesher.ConductorsOnly(receded, names, report.Template, problem.Ground);
            var recC0   = RealPart(ChargeSolver.MaxwellCapacitance(recMesh)); fills++;
            var recL    = Scale(Invert(recC0), EmConstants.Mu0 * EmConstants.Eps0);
            var dLdn    = Derivative(recL, l, delta);

            surfaces.Add(new LossSurface($"conductor:{names[k]}", dLdn, problem.Conductors[k].SigmaSm));
            if (dLdn[k, k] <= 0)
                notes.Add($"∂L/∂n for receding conductor '{names[k]}' came out non-positive " +
                          $"({dLdn[k, k]:G4}) — receding metal must always increase that conductor's " +
                          "own L, so this indicates a meshing or sign fault.");
        }

        // (b) the ground plane — move it DOWN by Δ (equivalently h → h+Δ).
        if (problem.Ground is not null)
        {
            var gndMesh = BoundaryMesher.ConductorsOnly(
                outlines, names, report.Template,
                problem.Ground with { Y = problem.Ground.Y - delta });
            var gndC0 = RealPart(ChargeSolver.MaxwellCapacitance(gndMesh)); fills++;
            var gndL  = Scale(Invert(gndC0), EmConstants.Mu0 * EmConstants.Eps0);
            var dLdnGnd = Derivative(gndL, l, delta);
            surfaces.Add(new LossSurface("ground", dLdnGnd, problem.Ground.SigmaSm));
            if (dLdnGnd[0, 0] <= 0)
                notes.Add($"∂L/∂n for the ground-plane recession came out non-positive ({dLdnGnd[0, 0]:G4}).");
        }

        // R-cpl-3 — R_dc is DIAGONAL: each conductor has its own DC series resistance, and adding
        // them is only right when there is one. An infinite ground plane has none.
        var rdc = new Mat<double>(nCond, nCond);
        for (int i = 0; i < nCond; i++)
        {
            double sigma = problem.Conductors[i].SigmaSm;
            if (double.IsPositiveInfinity(sigma) || sigma <= 0) continue;
            double area = Polygon2D.Area(outlines[i]);
            if (area > 0) rdc[i, i] = 1.0 / (sigma * area);
        }

        double wheelerHz = 0;
        foreach (double f in report.WheelerValidAboveHz) wheelerHz = Math.Max(wheelerHz, f);

        notes.Add($"Wheeler recession Δ = {delta:G4} m; ∂L/∂n evaluated once, frequency-independent (R-mom-12).");
        notes.Add($"[C], [C₀] and ∂L/∂n filled {fills}× total — independent of the frequency count (R-mom-11).");

        return new RlgcModel(names, cComplex, c0, l, eeff, surfaces, rdc, wheelerHz, fills, residual, notes);
    }

    private static double RecessionDelta(IReadOnlyList<IReadOnlyList<EmPoint>> outlines,
                                         EmMeshReport report, double fraction)
    {
        double smallest = double.MaxValue;
        foreach (var o in outlines)
        {
            var (x0, y0, x1, y1) = Polygon2D.Bounds(o);
            smallest = Math.Min(smallest, Math.Min(x1 - x0, y1 - y0));
        }
        if (!(smallest < double.MaxValue) || smallest <= 0) smallest = 1e-6;

        double d = smallest * fraction;
        // Also keep Δ small against the smallest mesh cell, so the perturbed mesh stays a
        // perturbation of the original rather than a different mesh.
        if (report.MinCellLength > 0) d = Math.Min(d, 0.5 * report.MinCellLength);
        return d;
    }

    // ── tiny dense linear algebra (M is the conductor count — 1 for kernel A) ──────────────────

    private static Mat<double> RealPart(Mat<Complex> a)
    {
        var r = new Mat<double>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            r[i, j] = a[i, j].Real;
        return r;
    }

    /// <summary>
    /// The finite-difference derivative (a − b)/Δ, elementwise. Spelled as a DIVISION rather than a
    /// multiply by 1/Δ: the two differ in the last ulp, and C1's gate is that the single-conductor
    /// answer does not move at all from what kernel A computed.
    /// </summary>
    private static Mat<double> Derivative(Mat<double> a, Mat<double> b, double delta)
    {
        var r = new Mat<double>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            r[i, j] = (a[i, j] - b[i, j]) / delta;
        return r;
    }

    private static Mat<double> Scale(Mat<double> a, double s)
    {
        var r = new Mat<double>(a.RowCount, a.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            r[i, j] = a[i, j] * s;
        return r;
    }

    /// <summary>Gauss-Jordan with partial pivoting. M is the conductor count — 1 today.</summary>
    internal static Mat<double> Invert(Mat<double> a)
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
                throw new InvalidOperationException("MoM: [C₀] is singular — the conductor set has no reference.");
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
}
