// L8c — Tiers 3, 4 and 7: the εᵣ = 1 reduction, the structure of Z, and determinism.
//
// Tier 3 is R-fil-7 and it is deliberately written EARLY: with no slab the Green's function is free
// space plus one image in closed form, for both kernels, so the entire fill can be reproduced by a
// path with no DCIM, no Prony and no Bessel function anywhere in it. It is the test that finds a
// sign, a factor of 4π or a transposed index on the first run, and it did.
//
// The comparison path is Support/PlanarPairOracle — a CROSS-CORRELATION formulation that shares no
// algebra with RectangleIntegrals (see its header). That is D3's standing rule in this area for the
// third time: a second formulation, not a second copy.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarFillTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A small, deliberately IRREGULAR grid: unequal cell widths and heights, so a fill that divides
    /// by "the" cell size rather than by each cell's own area passes on a uniform mesh and fails here.
    /// Dimensions are realistic (sub-millimetre) so the numbers are the ones production sees.
    /// </summary>
    /// <param name="metalAt">
    /// Which grid positions carry metal, or null for a full slab — every caller before the notch
    /// fixture (<c>PlanarPortOnANotchTests</c>) passes null and gets exactly the mesh it always did.
    /// A position without metal contributes no cell and no basis, which is what lets a fixture have
    /// a row whose metal STOPS somewhere other than the mesh's own edge.
    /// </param>
    public static PlanarMesh Grid(double[] gx, double[] gy, Func<int, int, bool>? metalAt = null)
    {
        int nx = gx.Length - 1, ny = gy.Length - 1;
        var cells = new List<PlanarCell>();
        var at = new int[nx * ny];
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (metalAt is not null && !metalAt(ix, iy)) { at[iy * nx + ix] = -1; continue; }
                at[iy * nx + ix] = cells.Count;
                cells.Add(new PlanarCell(0, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
            }

        var bases = new List<PlanarBasis>();
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (at[iy * nx + ix] < 0) continue;
                if (ix + 1 < nx && at[iy * nx + ix + 1] >= 0)
                    bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[iy * nx + ix + 1], PlanarBasisDirection.X));
                if (iy + 1 < ny && at[(iy + 1) * nx + ix] >= 0)
                    bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[(iy + 1) * nx + ix], PlanarBasisDirection.Y));
            }

        return new PlanarMesh(cells, bases, ["Metal"], gx, gy);
    }

    public static PlanarMesh SmallIrregular() =>
        Grid([0.0, 0.30e-3, 0.75e-3, 1.00e-3], [0.0, 0.40e-3, 0.70e-3]);

    /// <summary>A geometrically symmetric plate, meshed symmetrically — Tier 4's permutation test.</summary>
    public static PlanarMesh SymmetricPlate() =>
        Grid([-0.6e-3, -0.2e-3, 0.2e-3, 0.6e-3], [-0.3e-3, 0.0, 0.3e-3]);

    private const double Freq = 10e9;
    private static double Omega => 2.0 * Math.PI * Freq;

    /// <summary>The εᵣ = 1 kernel: free space plus ONE perfect negative image at depth 2h. Both
    /// kernels are this, exactly (L8a Tier 1, 3.8e-10).</summary>
    private static (PlanarKernelTerms A, PlanarKernelTerms Q, Func<double, Complex> Raw) FreeSpaceKernel(
        double h, PlanarExtractionOrder order = PlanarExtractionOrder.Linear)
    {
        double k0 = 2.0 * Math.PI * Freq / EmConstants.C0;
        var terms = PlanarKernelTerms.FreeSpaceWithImage(k0, -Complex.One, 2.0 * h, order);
        Complex Raw(double rho) =>
            SommerfeldIntegral.FreeSpace(k0, rho)
          - SommerfeldIntegral.FreeSpace(k0, Math.Sqrt(rho * rho + 4.0 * h * h));
        return (terms, terms, Raw);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A hand-checkable value first — the self core, against a number obtained outside this repo
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T3_0_TheSelfCoreOfAUnitSquareIsTheMeanReciprocalDistance()
    {
        // ∫∫∫∫ dS dS′/R over the unit square against itself is 2.9732096, the mean reciprocal
        // distance between two random points of a unit square. Area-normalised (which is how the fill
        // stores it) that is the same number, since the areas are 1.
        var mesh = Grid([0.0, 1.0], [0.0, 1.0]);
        var cores = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { Parallel = false });
        double self = cores.ScalarCore(0, 0).Inverse;

        const double reference = 2.9732095802;
        Assert.True(Math.Abs(self - reference) / reference < 2e-6,
            $"self core {self:G12} against {reference:G12} — {Math.Abs(self - reference) / reference:E2} relative");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 3 — R-fil-7: the εᵣ = 1 reduction, the whole matrix, against an independent path
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T3_1_EpsilonOneFill_MatchesTheIndependentCorrelationOracle_EntryByEntry()
    {
        var mesh = SmallIrregular();
        const double h = 0.5e-3;
        var (termsA, termsQ, raw) = FreeSpaceKernel(h);

        var cores = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default);
        var z = PlanarFill.Fill(cores, termsA, termsQ, Omega);

        double worst = 0, scale = 0;
        int n = mesh.Bases.Count;
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                Complex want = PlanarPairOracle.Entry(mesh, i, j, raw, raw, Omega);
                worst = Math.Max(worst, (z[i, j] - want).Magnitude);
                scale = Math.Max(scale, want.Magnitude);
            }

        // 1e-5 is not an aspiration, it is a measured headroom: the fill converges TO this oracle as
        // its own quadrature is refined (verified by sweeping PlanarFillSettings.Finer), while the
        // oracle itself moves by 1e-11 under refinement. The residual is the fill's — 5.0e-6 at the
        // shipped rule — and it sits three decades below L8a's own 6e-3 kernel accuracy, which
        // R-fil-2's report says is where the effort should stop.
        Assert.True(worst / scale < 1e-5,
            $"worst |ΔZ| = {worst:E3} against a matrix scale of {scale:E3} ⇒ {worst / scale:E3} relative");
    }

    [Fact]
    public void T3_2_ScalarPotentialMatrix_MatchesTheIndependentOracle()
    {
        // P on its own, before any signed assembly: if this is wrong, T3_1's failure would be hard to
        // localise, and P is also what Tier 5's capacitance rides on.
        var mesh = SmallIrregular();
        var (_, termsQ, raw) = FreeSpaceKernel(0.5e-3);

        var cores = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default);
        var p = PlanarFill.ScalarPotentialMatrix(cores, termsQ);

        double worst = 0, scale = 0;
        int m = mesh.Cells.Count;
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                Complex want = PlanarPairOracle.Pair(mesh.Cells[a], mesh.Cells[b],
                                                     false, 0, false, 0, true, raw);
                worst = Math.Max(worst, (p[a, b] - want).Magnitude);
                scale = Math.Max(scale, want.Magnitude);
            }

        Assert.True(worst / scale < 1e-5, $"worst |ΔP| relative = {worst / scale:E3}");
    }

    [Fact]
    public void T3_3_DcimAtEpsilonOne_ReproducesTheClosedFormFill()
    {
        // The production path, fitted on a slab that is not there. DCIM must collapse to free space
        // plus one image; L8a measured that at 3.8e-10 on the Green's function itself, and this asks
        // the same question of the assembled matrix.
        var mesh = SmallIrregular();
        const double h = 0.5e-3;
        var slab = new GroundedSlab(h, new EmMaterial(1.0, 0.0));
        var greens = new SpectralGreens(slab, Freq);

        var st = PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(mesh, st);

        var dcimA = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.VectorPotential), st.Order);
        var dcimQ = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.ScalarPotential), st.Order);
        var zDcim = PlanarFill.Fill(cores, dcimA, dcimQ, Omega);

        var (exactA, exactQ, _) = FreeSpaceKernel(h, st.Order);
        var zExact = PlanarFill.Fill(cores, exactA, exactQ, Omega);

        double worst = 0, scale = 0;
        int n = mesh.Bases.Count;
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                worst = Math.Max(worst, (zDcim[i, j] - zExact[i, j]).Magnitude);
                scale = Math.Max(scale, zExact[i, j].Magnitude);
            }

        Assert.True(worst / scale < 1e-7, $"DCIM vs closed form at εᵣ = 1: {worst / scale:E3} relative");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 4 — structure
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T4_1_ZIsComplexSymmetric_BitIdentically()
    {
        // R-fil-2 — a TOLERANCE here would be testing the Green's function's reciprocity, which is a
        // different question. Computed on m ≤ n and mirrored, so the two triangles share bits.
        var mesh = SmallIrregular();
        var (a, q, _) = FreeSpaceKernel(0.5e-3);
        var z = PlanarFill.Fill(PlanarFill.BuildCores(mesh), a, q, Omega);

        int n = mesh.Bases.Count;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(z[i, j].Real),
                             BitConverter.DoubleToInt64Bits(z[j, i].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(z[i, j].Imaginary),
                             BitConverter.DoubleToInt64Bits(z[j, i].Imaginary));
            }
    }

    [Fact]
    public void T4_2_AnXRooftopCouplesToAYRooftopThroughTheSCALARTermAlone()
    {
        // D5, as a formulation fact. The test is worthless unless the scalar part of the SAME pair is
        // demonstrably non-zero, so that is asserted too — otherwise it passes because the entry is
        // zero for an unrelated reason.
        var mesh = SmallIrregular();
        var (a, q, _) = FreeSpaceKernel(0.5e-3);
        var cores = PlanarFill.BuildCores(mesh);
        var z = PlanarFill.Fill(cores, a, q, Omega);
        var p = PlanarFill.ScalarPotentialMatrix(cores, q);

        int mixed = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
            for (int j = 0; j < mesh.Bases.Count; j++)
            {
                if (mesh.Bases[i].Direction == mesh.Bases[j].Direction) continue;
                mixed++;

                var (ma, mb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);
                var (na, nb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[j]);
                Complex scalar = ma.Sign * na.Sign * p[ma.CellIndex, na.CellIndex]
                               + ma.Sign * nb.Sign * p[ma.CellIndex, nb.CellIndex]
                               + mb.Sign * na.Sign * p[mb.CellIndex, na.CellIndex]
                               + mb.Sign * nb.Sign * p[mb.CellIndex, nb.CellIndex];
                Complex expected = scalar / (Complex.ImaginaryOne * Omega * EmConstants.Eps0);

                Assert.True((z[i, j] - expected).Magnitude <= 1e-12 * expected.Magnitude,
                    $"mixed pair ({i},{j}) carries a vector contribution: {z[i, j]} vs {expected}");
                Assert.True(expected.Magnitude > 0,
                    $"mixed pair ({i},{j}) has a zero SCALAR block too — the test proved nothing");
            }

        Assert.True(mixed > 0, "no mixed-direction pairs in the fixture");
    }

    [Fact]
    public void T4_3_TheScalarBlockAssembledFromP_EqualsADirectlyIntegratedOne()
    {
        // D4's claim is that assembling Z^φ from a per-CELL matrix is EXACT, not an approximation.
        // The comparison is against the independent oracle's own per-BASIS scalar integral, so this
        // tests the claim rather than restating it.
        var mesh = SmallIrregular();
        var (_, q, raw) = FreeSpaceKernel(0.5e-3);
        var cores = PlanarFill.BuildCores(mesh);
        var p = PlanarFill.ScalarPotentialMatrix(cores, q);

        double worst = 0, scale = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
            for (int j = i; j < mesh.Bases.Count; j++)
            {
                var (ma, mb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);
                var (na, nb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[j]);
                Complex fromP = ma.Sign * na.Sign * p[ma.CellIndex, na.CellIndex]
                              + ma.Sign * nb.Sign * p[ma.CellIndex, nb.CellIndex]
                              + mb.Sign * na.Sign * p[mb.CellIndex, na.CellIndex]
                              + mb.Sign * nb.Sign * p[mb.CellIndex, nb.CellIndex];

                Complex direct = Complex.Zero;
                foreach (var hm in new[] { ma, mb })
                    foreach (var hn in new[] { na, nb })
                        direct += hm.Sign * hn.Sign
                                * PlanarPairOracle.Pair(mesh.Cells[hm.CellIndex], mesh.Cells[hn.CellIndex],
                                                        false, 0, false, 0, true, raw);

                worst = Math.Max(worst, (fromP - direct).Magnitude);
                scale = Math.Max(scale, direct.Magnitude);
            }

        Assert.True(worst / scale < 1e-5, $"P-assembled vs directly integrated: {worst / scale:E3}");
    }

    [Fact]
    public void T4_4_ASymmetricPlateProducesAMatrixWithTheMatchingPermutationSymmetry()
    {
        // This is what catches a TRANSPOSED INDEX, which no magnitude check will: mirror the plate in
        // x, and the matrix must be invariant under the induced permutation of the basis functions.
        var mesh = SymmetricPlate();
        var (a, q, _) = FreeSpaceKernel(0.4e-3);
        var z = PlanarFill.Fill(PlanarFill.BuildCores(mesh), a, q, Omega);

        int n = mesh.Bases.Count;
        var perm = MirrorPermutation(mesh);
        Assert.DoesNotContain(-1, perm);

        // THE MIRROR CARRIES A SIGN, and getting that wrong is the first thing this test taught.
        // Under x → −x an X-rooftop's own current direction reverses, so its pushforward is MINUS the
        // canonical basis of the mirrored pair; a Y-rooftop's does not. The invariance is therefore
        // Z[σi, σj] = s_i·s_j·Z[i, j] with s = −1 on X and +1 on Y — which means a MIXED pair changes
        // sign. Asserting plain invariance instead fails at 0.78 relative, i.e. loudly, on a matrix
        // that is in fact correct.
        var sign = new double[n];
        for (int i = 0; i < n; i++)
            sign[i] = mesh.Bases[i].Direction == PlanarBasisDirection.X ? -1.0 : 1.0;

        double worst = 0, scale = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                worst = Math.Max(worst, (z[i, j] - sign[i] * sign[j] * z[perm[i], perm[j]]).Magnitude);
                scale = Math.Max(scale, z[i, j].Magnitude);
            }

        Assert.True(worst / scale < 1e-9, $"mirror symmetry violated by {worst / scale:E3} relative");
    }

    /// <summary>Maps each basis onto the one it becomes under x → −x. Built from geometry alone, so
    /// it cannot inherit an indexing mistake from the fill.</summary>
    private static int[] MirrorPermutation(PlanarMesh mesh)
    {
        var perm = new int[mesh.Bases.Count];
        for (int i = 0; i < mesh.Bases.Count; i++)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);
            var ca = mesh.Cells[ha.CellIndex];
            var cb = mesh.Cells[hb.CellIndex];
            double cx = -0.5 * (ca.CenterX + cb.CenterX), cy = 0.5 * (ca.CenterY + cb.CenterY);

            perm[i] = -1;
            for (int j = 0; j < mesh.Bases.Count; j++)
            {
                if (mesh.Bases[j].Direction != mesh.Bases[i].Direction) continue;
                var (ja, jb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[j]);
                var da = mesh.Cells[ja.CellIndex];
                var db = mesh.Cells[jb.CellIndex];
                if (Math.Abs(0.5 * (da.CenterX + db.CenterX) - cx) < 1e-12 &&
                    Math.Abs(0.5 * (da.CenterY + db.CenterY) - cy) < 1e-12)
                { perm[i] = j; break; }
            }
        }
        return perm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The radial table — the one accuracy trade the fill makes for speed, measured not assumed
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T4_5_TheRadialTableAgreesWithDirectKernelEvaluation()
    {
        var mesh = SmallIrregular();
        var greens = new SpectralGreens(GroundedSlab.Fr4Starter, Freq);
        var ta = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.VectorPotential));
        var tq = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.ScalarPotential));

        var tabled = PlanarFillSettings.Default;
        var direct = tabled with { UseRadialTable = false };

        var zTab = PlanarFill.Fill(PlanarFill.BuildCores(mesh, tabled), ta, tq, Omega);
        var zDir = PlanarFill.Fill(PlanarFill.BuildCores(mesh, direct), ta, tq, Omega);

        double worst = 0, scale = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
            for (int j = i; j < mesh.Bases.Count; j++)
            {
                worst = Math.Max(worst, (zTab[i, j] - zDir[i, j]).Magnitude);
                scale = Math.Max(scale, zDir[i, j].Magnitude);
            }

        // Measured at 8e-9 with the default 1/50-of-a-cell spacing — two decades below the fill's own
        // quadrature error, five below the kernel's. The tolerance records that, it does not aspire
        // to it.
        Assert.True(worst / scale < 1e-7,
            $"radial-table interpolation costs {worst / scale:E3} relative — measured, not assumed");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 7 — determinism, and D6's counter
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T7_1_TwoFillsInOneProcessAreBitIdentical()
    {
        // R-fil-11. The fill is parallel over rows; if the answer depended on scheduling this would
        // fail intermittently, which is the worst way to find it.
        var mesh = SmallIrregular();
        var (a, q, _) = FreeSpaceKernel(0.5e-3);

        var z1 = PlanarFill.Fill(PlanarFill.BuildCores(mesh), a, q, Omega);
        var z2 = PlanarFill.Fill(PlanarFill.BuildCores(mesh), a, q, Omega);

        for (int i = 0; i < mesh.Bases.Count; i++)
            for (int j = 0; j < mesh.Bases.Count; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(z1[i, j].Real),
                             BitConverter.DoubleToInt64Bits(z2[i, j].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(z1[i, j].Imaginary),
                             BitConverter.DoubleToInt64Bits(z2[i, j].Imaginary));
            }
    }

    [Fact]
    public void T7_1b_ParallelAndSerialFillsAgreeBitForBit()
    {
        var mesh = SmallIrregular();
        var (a, q, _) = FreeSpaceKernel(0.5e-3);

        var zp = PlanarFill.Fill(PlanarFill.BuildCores(mesh, PlanarFillSettings.Default), a, q, Omega);
        var zs = PlanarFill.Fill(PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { Parallel = false }),
                                 a, q, Omega);

        for (int i = 0; i < mesh.Bases.Count; i++)
            for (int j = 0; j < mesh.Bases.Count; j++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(zp[i, j].Real),
                             BitConverter.DoubleToInt64Bits(zs[i, j].Real));
    }

    [Fact]
    public void T7_2_TheFrequencyIndependentCoreIsBuiltExactlyOnce_ThreePointSweep()
        => CoreCountIs(3);

    /// <summary>
    /// R-mom-11's own second length. <b>Tagged for its own runtime, not for anyone else's</b>: 101
    /// points cost ~23 s and almost all of it is <see cref="Dcim.Fit"/>, which is per-frequency by
    /// construction (L8a's R-lgf-5) and is exactly the cost D6 exists to leave alone.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void T7_2b_TheFrequencyIndependentCoreIsBuiltExactlyOnce_HundredAndOnePointSweep()
        => CoreCountIs(101);

    private static void CoreCountIs(int points)
    {
        // R-fil-9 / D6, in R-mom-11's own shape: a counter, not a comment. The two sweep lengths are
        // the same two RlgcModel.MatrixFillCount uses.
        var mesh = SmallIrregular();
        var freqs = new double[points];
        for (int i = 0; i < points; i++) freqs[i] = 1e9 + i * (19e9 / Math.Max(1, points - 1));

        var result = PlanarSweep.Run(mesh, GroundedSlab.Fr4Starter, freqs, factor: false);

        Assert.Equal(1, result.CoreFillCount);
        Assert.Equal(points, result.Points.Count);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fil-10 — the ceiling refuses before it allocates
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T7_3_AboveR17TheFillRefusesRatherThanAllocating()
    {
        // The mesh is never built at that size — a bare PlanarMesh with the right basis COUNT is
        // enough, because the refusal must come before anything of matrix size is touched.
        int n = SurfaceMesher.UnknownCeiling + 1;
        var cells = new List<PlanarCell>();
        var bases = new List<PlanarBasis>();
        for (int i = 0; i <= n; i++) cells.Add(new PlanarCell(0, i, 0, i, 0, i + 1, 1));
        for (int i = 0; i < n; i++) bases.Add(new PlanarBasis(0, i, i + 1, PlanarBasisDirection.X));
        var mesh = new PlanarMesh(cells, bases, ["Metal"], [], []);

        var ex = Assert.Throws<InvalidOperationException>(() => PlanarSystem.GuardCeiling(mesh.Bases.Count));
        Assert.Contains($"{SurfaceMesher.UnknownCeiling:N0}-unknown ceiling", ex.Message);

        var ex2 = Assert.Throws<InvalidOperationException>(() => PlanarFill.BuildCores(mesh));
        Assert.Contains("ceiling this kernel is built for", ex2.Message);

        // MIM-13 — this used to pin "Lower Cells per wavelength" on both. Neither guard may name a
        // remedy: they have a basis count and no geometry, so they cannot ask which knob BINDS, and
        // naming one unconditionally is the 2026-08-14 owner report's own defect (the user halved the
        // knob the message named, saw the identical count, and stopped). What they owe instead is
        // both ceilings — so a reader can tell 1.9× over from 100× over — and a pointer at the mesh
        // report, where the remedies that act on the artwork in hand are written.
        foreach (string m in new[] { ex.Message, ex2.Message })
        {
            Assert.DoesNotContain("Lower Cells per wavelength", m, StringComparison.Ordinal);
            Assert.DoesNotContain("which is not built", m, StringComparison.Ordinal);
            Assert.Contains($"{SurfaceMesher.AcceleratedUnknownCeiling:N0}", m, StringComparison.Ordinal);
            Assert.Contains("Mesh the setup", m, StringComparison.Ordinal);
        }
    }

    // ── CAP-3 — the cell potential P in bounded panels ────────────────────────────────────────────
    //
    // The scalar block reads P a panel of columns at a time, from rows formed under a byte budget
    // (PlanarPotentialPanels.cs). A budget of a few kB forces a panel per handful of columns and the
    // eviction and re-forming of carried rows; an unbounded one forms every row once in one panel.
    // Every entry is P(min, max) by the one expression either way, so the triangles agree to the bit.
    // (Against the m×m code that preceded it: RESOLVED.md §CAP-3's S dumps, and P3_1's pinned digests.)

    private static void AssertSameBits(BlockLowerMatrix a, BlockLowerMatrix b)
    {
        Assert.Equal(a.Size, b.Size);
        for (int j = 0; j < a.Size; j++)
        {
            var ca = a.Column(j); var cb = b.Column(j);
            for (int i = 0; i < ca.Length; i++)
                if (BitConverter.DoubleToInt64Bits(ca[i].Real) != BitConverter.DoubleToInt64Bits(cb[i].Real) ||
                    BitConverter.DoubleToInt64Bits(ca[i].Imaginary) != BitConverter.DoubleToInt64Bits(cb[i].Imaginary))
                    Assert.Fail($"entry ({j + i}, {j}) differs: {ca[i]} against {cb[i]}");
        }
    }

    [Fact]
    public void CAP3_APanelledFill_IsTheWholeOne_BitForBit_OnOneLevel()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        var pair    = PlanarLineFixtures.Kernel(problem.Slab, 10e9).For(cores, PlanarFillSettings.Default.Order);
        double w    = 2 * Math.PI * 10e9;

        var whole = PlanarFill.FillLower(cores, pair.VectorPotential, pair.Scalar, w, potentialBudgetBytes: long.MaxValue);
        var tight = PlanarFill.FillLower(cores, pair.VectorPotential, pair.Scalar, w, potentialBudgetBytes: 16 << 10);
        AssertSameBits(whole, tight);
    }

    /// <summary>A cheap stand-in for P: any deterministic function of the unordered pair will do,
    /// because what is under test is which row an entry is read from, not what it is.</summary>
    private readonly struct SyntheticPotential : PlanarFill.IPotentialEntry
    {
        public Complex At(int a, int b) => new(1.0 / (1 + b - a) + 1e-3 * a, Math.Sin(0.37 * a + 0.11 * b));
    }

    [Fact]
    public void CAP3_APanelledScalarBlock_ReadsEveryEntryFromTheRightRow_WithAViaTail()
    {
        // The via bases sit after every horizontal one and their foot cells are carried as a separate
        // segment per row, so this is the mesh that reads both halves of a row — and a 2 kB budget
        // forces eviction and re-forming throughout. Against the m×m arithmetic written out directly.
        var stack = LayerStacks.MmicTwoLevel;
        const double len = 400e-6;
        var lower = new PlanarConductorLayer("M1", [PlanarLineFixtures.Rect(0, 0, len, 100e-6)], 4.1e7, 2e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", [PlanarLineFixtures.Rect(0, 0, len, 100e-6)], 4.1e7, 3e-6, stack.TopZ);
        var vias  = new[] { new PlanarVia(0, 1, [PlanarLineFixtures.Rect(0.45 * len, 30e-6, 0.55 * len, 70e-6)], 4.1e7) };
        var problem = new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, 10e9, null, stack, vias);
        var mesh  = SurfaceMesher.Mesh(problem).Mesh;
        Assert.Contains(mesh.Bases, b => b.Direction == PlanarBasisDirection.Z);

        int n = mesh.Bases.Count;
        var halves = new (RooftopHalf A, RooftopHalf B)[n];
        for (int i = 0; i < n; i++) halves[i] = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);
        var pot   = new SyntheticPotential();
        var scale = new Complex(0.3, -2.0);
        Complex P(int a, int b) => pot.At(Math.Min(a, b), Math.Max(a, b));

        var expected = new BlockLowerMatrix(n);
        for (int i = 0; i < n; i++)
        {
            var (ma, mb) = halves[i];
            var col = expected.Column(i);
            for (int j = i; j < n; j++)
            {
                var (na, nb) = halves[j];
                Complex s = ma.Sign * na.Sign * P(ma.CellIndex, na.CellIndex)
                          + ma.Sign * nb.Sign * P(ma.CellIndex, nb.CellIndex)
                          + mb.Sign * na.Sign * P(mb.CellIndex, na.CellIndex)
                          + mb.Sign * nb.Sign * P(mb.CellIndex, nb.CellIndex);
                col[j - i] = scale * s;
            }
        }

        foreach (long budget in new[] { 2L << 10, long.MaxValue })
        {
            var z = new BlockLowerMatrix(n);
            PlanarFill.ScalarBlockInPanels(z, halves, mesh, scale, pot, PlanarFillSettings.Default, budget);
            AssertSameBits(expected, z);
        }
    }
}
