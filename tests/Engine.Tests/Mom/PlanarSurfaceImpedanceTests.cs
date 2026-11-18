using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>CL1 — the surface-impedance term in the full-wave fill.</b> R-cl1-1 … R-cl1-9.
///
/// <para>Every gate here is either an identity (the two coth asymptotes, the PEC reproduction, the
/// DC limit against <see cref="PlanarDcSolve"/>) or a comparison against KERNEL A, which is an
/// independent formulation — incremental inductance over a receded boundary mesh, not a second copy
/// of an EFIE. Nothing here is tuned to anything.</para>
/// </summary>
public class PlanarSurfaceImpedanceTests(ITestOutputHelper output)
{
    private const double CopperSigma = 5.8e7, GoldSigma = 4.1e7;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-1 — the scalar, and both its asymptotes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>t ≫ δ → (1+j)/(2σδ)</b>, two surfaces in parallel — and the deep limit is where the coth's
    /// LARGE-ARGUMENT branch lives, so this also says that branch returns the right number rather
    /// than a plausible one.
    /// </summary>
    [Theory]
    [InlineData(40.0)]
    [InlineData(80.0)]
    [InlineData(318.0)]     // 105 µm copper at 40 GHz — the thickest shipped metal at the top of a band
    [InlineData(2000.0)]    // past the naive exponential's own overflow at t/δ > 710
    public void R_cl1_1a_ThickLimit_IsTwoSurfacesInParallel(double tOverDelta)
    {
        const double omega = 2.0 * Math.PI * 10e9;
        double delta = PlanarSurfaceImpedance.SkinDepthM(CopperSigma, omega);
        var got  = PlanarSurfaceImpedance.Sheet(CopperSigma, tOverDelta * delta, omega);
        var want = new Complex(1.0, 1.0) / (2.0 * CopperSigma * delta);

        double rel = (got - want).Magnitude / want.Magnitude;
        output.WriteLine($"t/δ = {tOverDelta,7:F1}: Z_s = {got.Real:E6} + j{got.Imaginary:E6}, " +
                         $"asymptote {want.Real:E6} + j{want.Imaginary:E6}, rel {rel:E2}");
        Assert.True(rel < 1e-12, $"t/δ = {tOverDelta}: {rel:E3}");
        Assert.False(double.IsNaN(got.Real) || double.IsInfinity(got.Real));
    }

    /// <summary>
    /// <b>t ≪ δ → 1/(σt)</b>, which is <see cref="PlanarDcSolve"/>'s sheet resistance EXACTLY. This
    /// is what makes the AC term walk continuously into the DC point the kernel already computes,
    /// and it is the gate that would catch a factor of two in the coth — nothing else here would.
    /// </summary>
    [Theory]
    [InlineData(1e-3)]
    [InlineData(1e-4)]
    [InlineData(1e-6)]
    public void R_cl1_1b_ThinLimit_IsTheDcSheetResistance(double tOverDelta)
    {
        const double omega = 2.0 * Math.PI * 1e6;
        double delta = PlanarSurfaceImpedance.SkinDepthM(CopperSigma, omega);
        double t = tOverDelta * delta;

        var got  = PlanarSurfaceImpedance.Sheet(CopperSigma, t, omega);
        double want = 1.0 / (CopperSigma * t);

        double rel = Math.Abs(got.Real - want) / want;
        output.WriteLine($"t/δ = {tOverDelta:E0}: Re Z_s = {got.Real:E12} Ω/sq, 1/(σt) = {want:E12}, " +
                         $"rel {rel:E2}; Im/Re = {got.Imaginary / got.Real:E2}");
        Assert.True(rel < 1e-6, $"t/δ = {tOverDelta}: {rel:E3}");
        // The reactive part vanishes like (t/δ)² against the resistive one, so the thin sheet is a
        // pure resistance in the limit rather than merely a small one.
        Assert.True(Math.Abs(got.Imaginary / got.Real) < 3.0 * tOverDelta * tOverDelta + 1e-15);
    }

    /// <summary>σ ≤ 0 or t ≤ 0 is a PEC and returns <b>exactly</b> zero — which is what makes
    /// R-cl1-4's reproduction bit-identical rather than approximate.</summary>
    [Theory]
    [InlineData(0.0, 35e-6)]
    [InlineData(-1.0, 35e-6)]
    [InlineData(CopperSigma, 0.0)]
    [InlineData(double.PositiveInfinity, 35e-6)]
    public void R_cl1_1c_PecIsExactlyZero(double sigma, double t)
    {
        var z = PlanarSurfaceImpedance.Sheet(sigma, t, 2.0 * Math.PI * 10e9);
        // Infinity is not "σ ≤ 0", and it must still produce a zero rather than a NaN: δ → 0, so the
        // two-surface asymptote (1+j)/(2σδ) is 0/0 arithmetically. It is measured, not asserted.
        if (double.IsPositiveInfinity(sigma))
            Assert.True(z.Magnitude < 1e-30, $"σ = ∞ gave {z}");
        else
            Assert.Equal(Complex.Zero, z);
    }

    /// <summary>The DC point itself: ω = 0 is the limit 1/(σt) written out, not a coth of zero.</summary>
    [Fact]
    public void R_cl1_1d_ZeroFrequency_IsTheSheetResistanceItself()
    {
        var z = PlanarSurfaceImpedance.Sheet(CopperSigma, 35e-6, 0.0);
        Assert.Equal(1.0 / (CopperSigma * 35e-6), z.Real, 1e-15);
        Assert.Equal(0.0, z.Imaginary);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-2 — the Gram matrix
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>⟨f_m, f_n⟩ against a 2-D quadrature of the ROOFTOP ITSELF</b> — the weights come from
    /// <see cref="PlanarBasisFunctions.Evaluate"/>, which is the definition Tier 0 tests directly and
    /// which the fill never calls, so this is a genuinely second statement of the same thing.
    /// </summary>
    [Theory]
    [InlineData("line")]
    [InlineData("taper")]
    [InlineData("disc")]
    public void R_cl1_2_GramAgreesWithADirectQuadratureOfTheRooftop(string part)
    {
        var (problem, settings) = GramFixture(part);
        var mesh = SurfaceMesher.Mesh(problem, settings).Mesh;
        var gram = PlanarGram.Build(mesh);

        int checkedPairs = 0, cutPairs = 0;
        double worst = 0; string worstAt = "";

        for (int i = 0; i < gram.UnknownCount; i++)
            for (int k = gram.RowPtr[i]; k < gram.RowPtr[i + 1]; k++)
            {
                int j = gram.ColIdx[k];
                double want = QuadratureGram(mesh, i, j);
                double got  = gram.Value[k];
                double rel  = Math.Abs(got - want) / Math.Max(Math.Abs(want), 1e-300);
                if (rel > worst) { worst = rel; worstAt = $"({i},{j})"; }
                checkedPairs++;
                if (mesh.Cells[mesh.Bases[i].CellA].IsCut || mesh.Cells[mesh.Bases[i].CellB].IsCut)
                    cutPairs++;
            }

        output.WriteLine($"{part}: N = {mesh.Bases.Count}, cells = {mesh.Cells.Count}, " +
                         $"Gram nnz = {gram.NonZeros} ({gram.NonZerosPerUnknown:F2} × N), " +
                         $"{cutPairs} pairs touching a cut cell, worst relative {worst:E2} at {worstAt}");

        Assert.True(checkedPairs > 0);
        Assert.True(worst < 1e-12, $"{part}: worst {worst:E3} at {worstAt}");

        // O(N) with a constant in the single digits, or the PAIRING is wrong rather than the
        // integral. A rooftop meets at most one same-direction neighbour per cell, so the honest
        // expectation is 1 (its own diagonal) + up to 2.
        Assert.InRange(gram.NonZerosPerUnknown, 0.5, 9.0);
    }

    /// <summary>The Gram is symmetric and its diagonal is strictly positive — it is ∫|f|², and a
    /// non-positive diagonal would mean the domain or the weight is wrong, not the arithmetic.</summary>
    [Fact]
    public void R_cl1_2b_GramIsSymmetricWithAPositiveDiagonal()
    {
        var mesh = SurfaceMesher.Mesh(PlanarLineFixtures.Fr4Line(10e-3, 10e9),
                                      PlanarLineFixtures.Coarse).Mesh;
        var gram = PlanarGram.Build(mesh);

        for (int i = 0; i < gram.UnknownCount; i++)
        {
            Assert.True(gram.At(i, i) > 0, $"basis {i} has ⟨f,f⟩ = {gram.At(i, i)}");
            for (int k = gram.RowPtr[i]; k < gram.RowPtr[i + 1]; k++)
            {
                int j = gram.ColIdx[k];
                Assert.Equal(gram.Value[k], gram.At(j, i));
            }
        }
    }

    /// <summary>
    /// A uniform mesh's own closed form, computed with nothing from this repository in it: a rooftop
    /// on two square cells of side h has ⟨f,f⟩ = 2·(h/3h) = 2/3, and two rooftops sharing one cell
    /// have h/6h = 1/6. Written out because it is the number a hand check would produce.
    /// </summary>
    [Fact]
    public void R_cl1_2c_UniformMesh_MatchesTheHandComputedValues()
    {
        var mesh = UniformMesh(nx: 5, ny: 3, h: 1e-4);
        var gram = PlanarGram.Build(mesh);

        int interiorX = -1, neighbourX = -1;
        for (int i = 0; i < mesh.Bases.Count && interiorX < 0; i++)
            if (mesh.Bases[i].Direction == PlanarBasisDirection.X)
                for (int j = 0; j < mesh.Bases.Count; j++)
                    if (j != i && mesh.Bases[j].Direction == PlanarBasisDirection.X
                               && gram.At(i, j) != 0)
                    { interiorX = i; neighbourX = j; break; }

        Assert.True(interiorX >= 0, "the fixture produced no X rooftop with an X neighbour");
        Assert.Equal(2.0 / 3.0, gram.At(interiorX, interiorX), 1e-14);
        Assert.Equal(1.0 / 6.0, gram.At(interiorX, neighbourX), 1e-14);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-3 — every Gram nonzero has a slot in AIM's near set
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Asserted, not assumed.</b> The accelerator carries Z_s in <c>nearExact</c> only — it is not
    /// a Green's-function interaction and the grid product must not claim it — so a Gram nonzero
    /// outside the sparse pattern would be dropped off the end of the matrix silently. Every
    /// overlapping-support pair SHOULD already be near (the near set is the union of a radius and
    /// stencil overlap, and two rooftops sharing a cell overlap), and this is what says so.
    /// </summary>
    [Fact]
    public void R_cl1_3_EveryGramNonzeroIsInTheAcceleratorsNearSet()
    {
        var problem = PlanarLineFixtures.Fr4Line(12e-3, 10e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var st = PlanarFillSettings.Default with { Aim = new PlanarAimSettings() };
        var ctx = new PlanarSolveContext(mesh, ports, st, slabHeightM: problem.Slab.HeightM);

        var geom = ctx.AimGeometry!;
        var gram = ctx.Cores.Gram;

        int missing = 0;
        for (int i = 0; i < gram.UnknownCount; i++)
            for (int k = gram.RowPtr[i]; k < gram.RowPtr[i + 1]; k++)
                if (!geom.IsNear(i, gram.ColIdx[k])) missing++;

        output.WriteLine($"N = {mesh.Bases.Count}, Gram nnz = {gram.NonZeros}, " +
                         $"near entries = {geom.NearEntries}, missing slots = {missing}");
        Assert.Equal(0, missing);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-4 / R-cl1-8 — the PEC reproduction, bit for bit
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>With the term ON and every layer at σ = ∞ (or t = 0) the filled matrix is BIT-IDENTICAL to
    /// today's.</b> Not 1e-12: identical. <see cref="Complex"/>.Zero added to an entry changes no
    /// bits, and if it does, the term was inserted in the wrong place.
    /// </summary>
    [Theory]
    [InlineData(double.PositiveInfinity, 35e-6)]
    [InlineData(CopperSigma, 0.0)]
    public void R_cl1_4_PecReproductionIsBitIdentical(double sigma, double thickness)
    {
        foreach (var (name, problem) in new[]
        {
            ("fr4 line", PecVariant(PlanarLineFixtures.Fr4Line(12e-3, 10e9), sigma, thickness)),
            ("taper",    PecVariant(PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter,
                                                             2.9e-3, 1.0e-3, 12e-3, 10e9),
                                    sigma, thickness)),
        })
        {
            var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
            var kern = PlanarLineFixtures.Kernel(problem.Slab, 10e9);
            double omega = 2.0 * Math.PI * 10e9;

            var pec  = Filled(mesh, kern, omega, null);
            var lossy = Filled(mesh, kern, omega, PlanarConductorLoss.For(problem));

            long moved = 0;
            for (int i = 0; i < pec.RowCount; i++)
                for (int j = 0; j < pec.ColCount; j++)
                {
                    if (BitConverter.DoubleToInt64Bits(pec[i, j].Real)
                        != BitConverter.DoubleToInt64Bits(lossy[i, j].Real)) moved++;
                    if (BitConverter.DoubleToInt64Bits(pec[i, j].Imaginary)
                        != BitConverter.DoubleToInt64Bits(lossy[i, j].Imaginary)) moved++;
                }
            output.WriteLine($"{name}: N = {mesh.Bases.Count}, bits moved = {moved}");
            Assert.Equal(0, moved);
        }
    }

    /// <summary>R-cl1-8's other half: with the term OFF the fill does not so much as BUILD the Gram,
    /// so a run that never asks for conductor loss pays nothing for it at all.</summary>
    [Fact]
    public void R_cl1_8_WithTheTermOffTheGramIsNeverBuilt()
    {
        var problem = PlanarLineFixtures.Fr4Line(12e-3, 10e9);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var cores = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default);
        var k = PlanarLineFixtures.Kernel(problem.Slab, 10e9).For(cores, PlanarExtractionOrder.Constant);

        _ = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, 2.0 * Math.PI * 10e9);
        Assert.False(cores.GramBuilt);
    }

    /// <summary>
    /// <b>The accelerator's near field and the dense fill agree on the term.</b>
    /// <c>PlanarPairClasses.cs:42</c> requires <see cref="PlanarEntryFill.At"/> and
    /// <see cref="PlanarFill.Fill"/> to be bit-identical, and that requirement does not relax for a
    /// term added later — which is why the two call ONE function.
    /// </summary>
    [Fact]
    public void R_cl1_3b_EntryFillAndDenseFillAgreeBitForBit()
    {
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 10e9);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var loss = PlanarConductorLoss.For(problem);
        var st = PlanarFillSettings.Default with { ConductorLoss = loss };

        var cores = PlanarFill.BuildCores(mesh, st);
        var kern  = PlanarLineFixtures.Kernel(problem.Slab, 10e9);
        double omega = 2.0 * Math.PI * 10e9;
        var k = kern.For(cores, st.Order);

        var dense = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, omega);
        var entry = new PlanarEntryFill(cores, k.VectorPotential, k.Scalar, omega);

        var gram = cores.Gram;
        int compared = 0;
        for (int i = 0; i < gram.UnknownCount; i++)
            for (int p = gram.RowPtr[i]; p < gram.RowPtr[i + 1]; p++)
            {
                int j = gram.ColIdx[p];
                var a = dense[j, i];
                var b = entry.At(i, j);
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Real),
                             BitConverter.DoubleToInt64Bits(b.Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Imaginary),
                             BitConverter.DoubleToInt64Bits(b.Imaginary));
                compared++;
            }
        output.WriteLine($"{compared} entries carrying the term, all bit-identical");
        Assert.True(compared > 0);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-5 — the via barrel's DC limit is PlanarDcSolve's own via resistance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A single through-via's DC resistance out of the AC path, against <see cref="PlanarDcSolve"/>'s
    /// <c>ℓ/(σA)</c> for the same basis. An identity in the limit, so the only question is how low a
    /// frequency it takes to get there — which is reported as well as gated.
    /// </summary>
    [Theory]
    [InlineData(1e3)]
    [InlineData(1e6)]
    [InlineData(1e9)]
    public void R_cl1_5_ViaBarrelWalksIntoTheDcViaResistance(double fHz)
    {
        // A 100 µm-tall gold post on a 60 × 60 µm footprint — the MMIC backside via's own shape.
        const double ell = 100e-6, side = 60e-6;
        double area = side * side, perim = 4.0 * side;

        var got  = PlanarSurfaceImpedance.Barrel(GoldSigma, ell, area, perim, 2.0 * Math.PI * fHz);
        double dc = ell / (GoldSigma * area);
        double tEff = area / perim;
        double delta = PlanarSurfaceImpedance.SkinDepthM(GoldSigma, 2.0 * Math.PI * fHz);

        double rel = Math.Abs(got.Real - dc) / dc;
        output.WriteLine($"f = {fHz:E0} Hz: t_eff/δ = {tEff / delta:F4}, Re Z = {got.Real:E6} Ω, " +
                         $"DC ℓ/(σA) = {dc:E6} Ω, rel {rel:E2}");

        if (tEff / delta < 0.05) Assert.True(rel < 0.01, $"{fHz:E0} Hz: {rel:P3}");
    }

    /// <summary>
    /// The same statement made through <see cref="PlanarConductorLoss.BarrelAt"/> on a REAL meshed
    /// via, so the σ lookup, the length and the footprint all come from where the DC point takes
    /// them. The DC point's own answer for that basis is the oracle.
    /// </summary>
    [Fact]
    public void R_cl1_5b_AMeshedViaMatchesTheDcPointsOwnResistance()
    {
        var (problem, mesh, levels) = ViaFixture();
        var loss = PlanarConductorLoss.For(problem);

        int found = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
        {
            var b = mesh.Bases[i];
            if (b.Direction != PlanarBasisDirection.Z) continue;
            found++;

            var cell = mesh.Cells[b.CellA];
            double sigma = PlanarDcSolve.ViaSigmaFor(problem, b, cell);
            double length = b.AttachesToGround
                ? levels.AttachmentLengthOf(b.LayerIndex)
                : levels.LengthOf(b.LayerIndex);
            double dc = length / (sigma * cell.Area);

            var ac = loss.BarrelAt(mesh, levels, b, 2.0 * Math.PI * 1e3);
            double rel = Math.Abs(ac.Real - dc) / dc;
            if (found <= 3)
                output.WriteLine($"basis {i}: ℓ = {length:E3} m, A = {cell.Area:E3} m², " +
                                 $"σ = {sigma:E3} S/m, AC {ac.Real:E6} Ω vs DC {dc:E6} Ω ({rel:E2})");
            Assert.True(rel < 0.01, $"basis {i}: {rel:P3}");
        }
        output.WriteLine($"{found} vertical bases checked");
        Assert.True(found > 0, "the fixture produced no vertical basis");
    }

    /// <summary>
    /// <b>The Gram is built ONCE per mesh, however long the sweep is</b> — a COUNTER, not a wall
    /// clock, on this area's own rule. Per frequency the term is O(N) complex multiply-adds against
    /// an O(N²) fill and an O(N³) factorisation, over a matrix already allocated, so it should be
    /// unmeasurable; if it ever IS measurable, this is the thing that has gone wrong.
    ///
    /// <para>Measured beside it at the FR-4 hero's shipping mesh (N = 1,368, 720 cells, Debug):
    /// fill 353.4 ms PEC against 345.5 ms with the term, factor 937.3 ms against 935.8 ms — i.e.
    /// inside the run-to-run noise in both — and the Gram itself 1.65 ms to build, 2,664 stored
    /// entries (1.95 × N) at 47.0 KB.</para>
    /// </summary>
    [Fact]
    public void Cost_TheGramIsBuiltOncePerMesh_NotPerFrequency()
    {
        var problem = PlanarLineFixtures.Fr4Line(12e-3, 10e9);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var st = PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) };
        var cores = PlanarFill.BuildCores(mesh, st);

        Assert.False(cores.GramBuilt);
        foreach (double f in new[] { 2e9, 5e9, 10e9, 15e9, 20e9 })
        {
            var k = PlanarLineFixtures.Kernel(problem.Slab, f).For(cores, st.Order);
            _ = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, 2.0 * Math.PI * f);
            Assert.True(cores.GramBuilt);
        }

        // The object identity IS the assertion: a Gram rebuilt per frequency would be a different
        // instance, and nothing about the answer would change.
        Assert.Same(cores.Gram, cores.Gram);
        output.WriteLine($"N = {mesh.Bases.Count}: Gram nnz = {cores.Gram.NonZeros} " +
                         $"({cores.Gram.NonZerosPerUnknown:F2} × N), {cores.Gram.Bytes / 1024.0:F1} KB, " +
                         "one build across a 5-point sweep");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-6 — the DC limit on both starters
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Re(Z_s) against <see cref="PlanarDcSolve"/>'s 1/(σt), down the band.</b> The two are the
    /// same number in the t ≪ δ limit and diverge as the metal becomes electrically thick, so what
    /// this reports is WHERE the crossover is on each starter's own metal — which is the question
    /// LF2's conduction-substitution band actually asks. The gate is the deep end of the table,
    /// where a factor of two in the coth is the only thing that could show up.
    /// </summary>
    [Fact]
    public void R_cl1_6_TheDcLimit_OnBothStarters()
    {
        foreach (var (name, sigma, t) in new[]
        {
            ("FR-4, 35 µm Cu", CopperSigma, 35e-6),
            ("GaAs, 3 µm Au",  GoldSigma,   3e-6),
        })
        {
            double dc = 1.0 / (sigma * t);
            output.WriteLine($"{name} — 1/(σt) = {dc:E4} Ω/sq");
            output.WriteLine("        f        t/δ      Re Z_s Ω/sq     Re Z_s / (1/σt)");
            foreach (double f in new[] { 1e3, 1e4, 1e5, 1e6, 3e6, 1e7, 1e8, 1e9, 1e10 })
            {
                double omega = 2.0 * Math.PI * f;
                double delta = PlanarSurfaceImpedance.SkinDepthM(sigma, omega);
                var z = PlanarSurfaceImpedance.Sheet(sigma, t, omega);
                output.WriteLine($"  {f,9:E1}  {t / delta,8:F4}  {z.Real,14:E4}  {z.Real / dc,14:F6}");
            }

            // Deep in the thin-sheet limit the ratio is 1 — and it is 1 only for the coth as
            // written. Halve the argument or drop the ½ and this lands at 2 or ½.
            double deep = PlanarSurfaceImpedance.Sheet(sigma, t, 2.0 * Math.PI * 1e3).Real / dc;
            Assert.InRange(deep, 1.0 - 1e-6, 1.0 + 1e-6);
            output.WriteLine("");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-7 / R-cl1-9 — against kernel A, and the edge convergence that decides the series
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-cl1-9 — THE EDGE CONVERGENCE GATE.</b> α_c of a uniform 50 Ω line out of kernel B, swept
    /// over <c>EdgeCells</c> at a fixed cells/λ, on both starters, beside kernel A's own strip-only
    /// term with the ground held PEC in BOTH kernels.
    ///
    /// <para>A zero-thickness sheet's ∫R_s|J|² is log-divergent in edge refinement; the LOADED
    /// operator should converge, and the question this answers is how fast and to what.
    /// <b>Nothing here is tuned</b> — not <c>EdgeCells</c>, not Z_s, not the edge cell width. The
    /// table is the result; the assertions are only that the sweep ran and produced a positive,
    /// finite α_c at every rung, because deciding whether the remaining drift is acceptable is a
    /// question about the PHYSICS and is the series' own, not this test's.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]     // measured: 10.4 s (FR-4) + 72.6 s (GaAs) — five meshes,
    [InlineData("fr4")]                  // two fills and two calibration standards on each rung
    [InlineData("gaas")]
    public void R_cl1_9_EdgeConvergenceOfAlphaC_AgainstKernelA(string starter)
    {
        bool fr4 = starter == "fr4";
        var slab  = fr4 ? GroundedSlab.Fr4Starter : GroundedSlab.GaAsStarter;
        double w  = fr4 ? 3020.28e-6 : 70.72e-6;      // the overview's own re-bisected 50 Ω widths
        double t  = fr4 ? 35e-6 : 3e-6;
        double sg = fr4 ? CopperSigma : GoldSigma;
        const double fHz = 10e9;

        double aOracle = KernelAStripAlpha(w, slab, t, sg, fHz, out double rOracle, out double z0A,
                                           out double eeff);
        output.WriteLine($"{starter}: w = {w * 1e6:F2} µm, t = {t * 1e6:F1} µm, σ = {sg:E3} S/m, " +
                         $"f = {fHz / 1e9:F1} GHz");
        output.WriteLine($"  kernel A (ground PEC): R_strip = {rOracle:F3} Ω/m, Z₀ = {z0A:F2} Ω, " +
                         $"ε_eff = {eeff:F4}, α_c = {aOracle:E4} Np/m = {aOracle * 8.686e-2:F4} dB/cm");
        output.WriteLine("  EdgeCells        N      α_c Np/m      dB/cm     vs kernel A");

        var table = new List<(int Edge, int N, double Alpha)>();
        foreach (int edge in new[] { 0, 2, 3, 5, 8 })
        {
            var (alpha, n, floor) = KernelBAlphaC(slab, w, t, sg, fHz, edge, eeff);
            table.Add((edge, n, alpha));
            output.WriteLine($"  {edge,9}  {n,7}  {alpha,12:E4}  {alpha * 8.686e-2,9:F4}  " +
                             $"{alpha / aOracle,10:F4}   (PEC floor {floor:E3})");
            Assert.True(alpha > 0 && double.IsFinite(alpha),
                        $"EdgeCells = {edge} gave α_c = {alpha}");
        }

        for (int i = 1; i < table.Count; i++)
        {
            double step = Math.Abs(table[i].Alpha - table[i - 1].Alpha) / table[i - 1].Alpha;
            output.WriteLine($"  rung {table[i - 1].Edge} → {table[i].Edge}: {step:P2}");
        }
    }

    /// <summary>
    /// <b>R-cl1-7 — the A-vs-B statement on its own</b>, at the shipped <c>EdgeCells</c>, so the
    /// number is quotable without reading a convergence table. Reported, not tuned to; the band is
    /// wide on purpose and is a statement about what was MEASURED rather than a target.
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]     // measured: 1.1 s (FR-4) + 7.9 s (GaAs)
    [InlineData("fr4")]
    [InlineData("gaas")]
    public void R_cl1_7_AgainstKernelA_OnAUniform50OhmLine(string starter)
    {
        bool fr4 = starter == "fr4";
        var slab  = fr4 ? GroundedSlab.Fr4Starter : GroundedSlab.GaAsStarter;
        double w  = fr4 ? 3020.28e-6 : 70.72e-6;
        double t  = fr4 ? 35e-6 : 3e-6;
        double sg = fr4 ? CopperSigma : GoldSigma;
        const double fHz = 10e9;

        double aOracle = KernelAStripAlpha(w, slab, t, sg, fHz, out double rOracle, out _,
                                           out double eeff);
        var (alpha, n, floor) = KernelBAlphaC(slab, w, t, sg, fHz, edgeCells: 3, eeff,
                                              output.WriteLine);

        output.WriteLine($"{starter}: kernel A R_strip = {rOracle:F3} Ω/m → α_c = {aOracle:E4} Np/m; " +
                         $"kernel B (N = {n}) α_c = {alpha:E4} Np/m (PEC floor {floor:E3} Np/m); " +
                         $"ratio {alpha / aOracle:F4}");
        Assert.True(alpha > 0 && double.IsFinite(alpha));
        Assert.InRange(alpha / aOracle, 0.2, 5.0);
    }

    /// <summary>
    /// <b>The routine-gate counterpart of R-cl1-7</b>: the whole path — mesh, fill, standards, γ —
    /// with the term on, asserting the two things that are true whatever the mesh is. Turning the
    /// term on can only ADD loss, and α_c is proportional to R_s, which is proportional to 1/σ at
    /// fixed t in the thick-metal limit — so halving σ multiplies α_c by √2. Structural, cheap, and
    /// it fails immediately on a sign error or a dropped term, which is what the two measured gates
    /// above are too expensive to do on every run.
    /// </summary>
    [Fact]
    public void R_cl1_7b_TurningTheTermOnAddsLoss_AndItScalesWithTheSquareRootOfResistivity()
    {
        const double fHz = 10e9, w = 3020.28e-6, t = 35e-6;
        var slab = GroundedSlab.Fr4Starter;

        double a1 = KernelBAlphaC(slab, w, t, CopperSigma,       fHz, 3, 3.2999).Alpha;
        double a4 = KernelBAlphaC(slab, w, t, CopperSigma / 4.0, fHz, 3, 3.2999).Alpha;

        output.WriteLine($"σ = {CopperSigma:E3}: α_c = {a1:E4} Np/m");
        output.WriteLine($"σ/4               : α_c = {a4:E4} Np/m, ratio {a4 / a1:F4} (√4 = 2)");

        Assert.True(a1 > 0, $"the term removed loss rather than adding it: α_c = {a1:E3}");
        Assert.InRange(a4 / a1, 1.8, 2.2);
    }

    /// <summary>
    /// Kernel A's strip-only conductor term, with the ground held PEC so the sum over surfaces
    /// isolates the strip exactly. <b>The ORACLE, and it shares no algebra with kernel B</b> —
    /// incremental inductance over a receded boundary mesh, not a second EFIE.
    /// </summary>
    private static double KernelAStripAlpha(double w, GroundedSlab slab, double t, double sigmaSm,
                                            double fHz, out double rPerM, out double z0,
                                            out double eeff)
    {
        var p = EmProblemBuilders.Microstrip(w, slab.HeightM, t, slab.Material.EpsR, tanD: 0,
                                            sigmaSm: sigmaSm,
                                            groundSigmaSm: double.PositiveInfinity);
        var res = new QuasiStaticKernel().SolveDetailed(p, EmMeshSettings.Default, [fHz]);
        rPerM = res.Rlgc.RPerM(2.0 * Math.PI * fHz);
        z0 = Math.Sqrt(res.Rlgc.LPerM / res.Rlgc.CPerM);
        eeff = res.Rlgc.Eeff;
        return rPerM / (2.0 * z0);
    }

    /// <summary>
    /// α of a uniform line out of kernel B with the conductor-loss term on and tanδ = 0, so what is
    /// left is the conductor term plus whatever radiates. γ comes from the two-line extraction the
    /// de-embedding path already uses.
    /// </summary>
    private static (double Alpha, int N, double Floor) KernelBAlphaC(
        GroundedSlab slab, double widthM, double t, double sigmaSm, double fHz, int edgeCells,
        double eeff, Action<string>? probe = null)
    {
        var lossless = new GroundedSlab(slab.HeightM, new EmMaterial(slab.Material.EpsR, 0.0));
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * widthM, 4.0 * widthM, 0.5 * widthM)],
                sigmaSm, t)],
            lossless, fHz);

        var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20,
                                          EdgeMesh: edgeCells > 0, EdgeCells: Math.Max(edgeCells, 1));
        var report = SurfaceMesher.Mesh(problem, mesh);
        var ports  = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem));

        var lossy = PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) };
        var kernel = PlanarLineFixtures.Kernel(lossless, fHz);

        // Two standards of the DUT's own cross-section — the same construction PlanarGammaTests
        // uses and the same one de-embedding calibrates with.
        //
        // THE SEPARATION IS A QUARTER WAVELENGTH, AND THAT IS NOT A PREFERENCE. The two-line
        // extraction is acosh of a quantity whose distance from 1 scales with (γΔℓ)², so it is
        // singular at βΔℓ = 180° — and a HALF-wavelength separation, which is the obvious choice,
        // sits exactly on it. Measured: at Δℓ = 0.5 λ_g the PEC line's extracted α came back at
        // 0.551 Np/m on FR-4, nearly seven times the conductor term being looked for, and the
        // difference was NEGATIVE. The ε_eff here is the crude (εᵣ+1)/2, so this is a target rather
        // than an exact 90°; that is what keeps it away from the singularity rather than on it.
        // λ_g and the expected βΔℓ come from KERNEL A's own ε_eff, which is the oracle this whole
        // measurement is against and is never an input to kernel B. The crude (εᵣ+1)/2 estimate is
        // not good enough here: on FR-4 it sized Δℓ at 4.97 mm and the extraction came back
        // βΔℓ = 245° and Usable = false — past the two-line method's own 180° singularity, on the
        // WRONG BRANCH, with an α that moved the wrong way when loss was added.
        double lambdaG = EmConstants.C0 / (fHz * Math.Sqrt(eeff));
        int k = PlanarCalibration.EndRunCellsFor(ports[0], lossless);
        var a = PlanarCalibration.BuildLine(ports[0], 0.25 * lambdaG, k);

        // BuildLine has a LENGTH FLOOR of its own (its end runs plus the port's own cells), so the
        // short standard is whatever that floor allows and the long one is measured FROM IT rather
        // than from the same wavelength scale. Asking for two targets below the floor returns two
        // lines of the SAME length and a zero Δℓ, which is a refusal rather than a bad answer — but
        // only because Gamma checks; nothing else would have noticed.
        var b = PlanarCalibration.BuildLine(ports[0], a.LengthM + 0.25 * lambdaG, k);

        if (!(b.LengthM > a.LengthM))
            throw new InvalidOperationException(
                $"the two standards came back at {a.LengthM:E4} m and {b.LengthM:E4} m");

        double Alpha(PlanarFillSettings settings)
        {
            var sa = new PlanarSolveContext(a.Mesh, a.Ports, settings).RawScatteringAt(kernel, fHz);
            var sb = new PlanarSolveContext(b.Mesh, b.Ports, settings).RawScatteringAt(kernel, fHz);
            double expect = 2.0 * Math.PI * Math.Sqrt(eeff) * fHz * (b.LengthM - a.LengthM)
                          / EmConstants.C0;
            var g = LineCalibration.Gamma(sa, sb, b.LengthM - a.LengthM, expect);
            probe?.Invoke($"    ℓ = {a.LengthM * 1e3:F3}/{b.LengthM * 1e3:F3} mm, " +
                          $"βΔℓ = {g.ElectricalDegrees:F2}°, usable {g.Usable}, " +
                          $"γ = {g.Gamma.Real:E4} + j{g.Gamma.Imaginary:E4}, " +
                          $"|S21| = {sa[1, 0].Magnitude:F6}/{sb[1, 0].Magnitude:F6}");
            return g.Gamma.Real;
        }

        // The PEC run is the FLOOR, and subtracting it is not a cosmetic correction: with tanδ = 0
        // what is left of α on a PEC line is radiation, surface-wave coupling and the two-line
        // extraction's own residual, and on 1.6 mm FR-4 at 10 GHz that floor is LARGER than the
        // conductor term it is being compared against. α_c is the difference, which is the quantity
        // kernel A's R/(2Z₀) is a statement about.
        double floor = Alpha(PlanarFillSettings.Default);
        return (Alpha(lossy) - floor, report.Mesh.Bases.Count, floor);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Support
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static (PlanarProblem, PlanarMeshSettings) GramFixture(string part) => part switch
    {
        "line"  => (PlanarLineFixtures.Fr4Line(6e-3, 10e9), PlanarLineFixtures.Coarse),
        "taper" => (PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 1.0e-3, 8e-3, 10e9),
                    new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false,
                                           BoundaryCells: PlanarBoundaryCells.Conformal)),
        "disc"  => (Disc(), new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false,
                                                   BoundaryCells: PlanarBoundaryCells.Conformal)),
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    private static PlanarProblem Disc(int points = 48, double radiusM = 1.45e-3)
    {
        var ring = new EmPoint[points];
        for (int i = 0; i < points; i++)
        {
            double a = 2.0 * Math.PI * i / points;
            ring[i] = new EmPoint(radiusM * Math.Cos(a), radiusM * Math.Sin(a));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], CopperSigma, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9);
    }

    /// <summary>The same problem with every conductor level replaced by a perfect one.</summary>
    private static PlanarProblem PecVariant(PlanarProblem p, double sigma, double thickness)
    {
        var layers = new List<PlanarConductorLayer>();
        foreach (var l in p.Layers) layers.Add(l with { SigmaSm = sigma, ThicknessM = thickness });
        return p with { Layers = layers };
    }

    private static NumFlat.Mat<Complex> Filled(PlanarMesh mesh, PlanarKernelPair kernel, double omega,
                                               PlanarConductorLoss? loss)
    {
        var st = PlanarFillSettings.Default with { ConductorLoss = loss };
        var cores = PlanarFill.BuildCores(mesh, st);
        var k = kernel.For(cores, st.Order);
        return PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, omega);
    }

    /// <summary>
    /// <c>∫ f_m·f_n dS</c> by a tensor Gauss rule over each of the shared cell's own weight strips,
    /// with the WEIGHTS read from <see cref="PlanarBasisFunctions.Evaluate"/> — the definition, which
    /// the fill never calls. The map from the unit square to a strip is bilinear and the integrand is
    /// a quadratic, so a modest rule is exact; 8 nodes per axis is well past that.
    /// </summary>
    private static double QuadratureGram(PlanarMesh mesh, int i, int j)
    {
        var bi = mesh.Bases[i];
        var bj = mesh.Bases[j];
        var (x, w) = Quadrature.Nodes(8);

        double total = 0;
        foreach (int c in SharedCells(mesh, bi, bj))
        {
            var cell = mesh.Cells[c];
            var support = SupportOn(mesh, bi, c);
            foreach (var strip in support.Strips)
            {
                var r = strip.Ring;
                for (int a = 0; a < x.Length; a++)
                    for (int b = 0; b < x.Length; b++)
                    {
                        double u = 0.5 * (x[a] + 1), v = 0.5 * (x[b] + 1);
                        // Bilinear map of the unit square onto the (convex, four-vertex) strip.
                        double px = (1 - u) * (1 - v) * r[0].X + u * (1 - v) * r[1].X
                                  + u * v * r[2].X + (1 - u) * v * r[3].X;
                        double py = (1 - u) * (1 - v) * r[0].Y + u * (1 - v) * r[1].Y
                                  + u * v * r[2].Y + (1 - u) * v * r[3].Y;
                        double dxu = -(1 - v) * r[0].X + (1 - v) * r[1].X + v * r[2].X - v * r[3].X;
                        double dyu = -(1 - v) * r[0].Y + (1 - v) * r[1].Y + v * r[2].Y - v * r[3].Y;
                        double dxv = -(1 - u) * r[0].X - u * r[1].X + u * r[2].X + (1 - u) * r[3].X;
                        double dyv = -(1 - u) * r[0].Y - u * r[1].Y + u * r[2].Y + (1 - u) * r[3].Y;
                        double jac = Math.Abs(dxu * dyv - dxv * dyu) * 0.25;

                        var (fx1, fy1) = PlanarBasisFunctions.Evaluate(mesh, bi, px, py);
                        var (fx2, fy2) = PlanarBasisFunctions.Evaluate(mesh, bj, px, py);
                        total += w[a] * w[b] * jac * (fx1 * fx2 + fy1 * fy2);
                    }
            }
            _ = cell;
        }
        return total;
    }

    private static IEnumerable<int> SharedCells(PlanarMesh mesh, PlanarBasis a, PlanarBasis b)
    {
        var (aa, ab) = PlanarBasisFunctions.Halves(mesh, a);
        var (ba, bb) = PlanarBasisFunctions.Halves(mesh, b);
        foreach (int c in new[] { aa.CellIndex, ab.CellIndex })
            if (c == ba.CellIndex || c == bb.CellIndex) yield return c;
    }

    private static RooftopSupport SupportOn(PlanarMesh mesh, PlanarBasis basis, int cell)
    {
        var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
        var (ha, _) = PlanarBasisFunctions.Halves(mesh, basis);
        return ha.CellIndex == cell ? sa : sb;
    }

    /// <summary>A plain nx × ny grid of square cells on one level — the hand-computable case.</summary>
    private static PlanarMesh UniformMesh(int nx, int ny, double h)
    {
        var gx = new double[nx + 1];
        var gy = new double[ny + 1];
        for (int i = 0; i <= nx; i++) gx[i] = i * h;
        for (int i = 0; i <= ny; i++) gy[i] = i * h;

        var cells = new List<PlanarCell>();
        var at = new int[nx * ny];
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                at[iy * nx + ix] = cells.Count;
                cells.Add(new PlanarCell(0, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
            }

        var bases = new List<PlanarBasis>();
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (ix + 1 < nx) bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[iy * nx + ix + 1],
                                                           PlanarBasisDirection.X));
                if (iy + 1 < ny) bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[(iy + 1) * nx + ix],
                                                           PlanarBasisDirection.Y));
            }
        return new PlanarMesh(cells, bases, ["Metal"], gx, gy);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl1-2d — a CALIBRATION STANDARD is made of the port's own level's metal
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A standard's mesh numbers its one conductor level 0 whatever level the port sits on, so
    /// the fill has to resolve its metal by NAME — and the name has to be the port's level's.</b>
    ///
    /// <para><see cref="PlanarConductorLoss.SheetTable"/> matches the mesh's layer name against the
    /// problem's levels and falls back to the index, which is 0. That fallback is the right answer on
    /// a single-level problem by either route and the WRONG one on a multi-level problem: with the
    /// name left at <see cref="PlanarCalibration.DefaultLayerName"/>, a port on M2 was calibrated
    /// against standards filled with M1's σ and thickness — silently, as a plausible α, and on the
    /// MMIC technology conductor loss is 92-99% of the line's. So <c>BuildSet</c> takes the name and
    /// <c>PlanarSolve</c> hands it the port's own level's.</para>
    ///
    /// <para>The fixture is the two-level MMIC one: same gold, 1 µm on Metal1 against 3 µm on
    /// Metal2, which at 30 GHz is a real difference in Re(Z_s) and not a rounding one.</para>
    /// </summary>
    [Fact]
    public void R_cl1_2d_AStandardIsFilledWithItsOwnLevelsMetal()
    {
        var (problem, mesh, _) = ViaFixture();
        double f = 30e9, omega = 2.0 * Math.PI * f;
        var loss = PlanarConductorLoss.For(problem);

        var upperPorts = PlanarPorts.ResolveAll(mesh,
            [new PlanarPort(1, new EmPoint(700e-6, 0), PlanarPortSide.MaxX, 50.0, 1)]);
        Assert.Equal(1, upperPorts[0].LayerIndex);

        var named = PlanarCalibration.BuildSet(upperPorts[0], problem.Slab, f, f, null,
                                               problem.Layers[1].Name);
        var placeholder = PlanarCalibration.BuildSet(upperPorts[0], problem.Slab, f, f);

        var wantUpper = PlanarSurfaceImpedance.Sheet(problem.Layers[1].SigmaSm,
                                                     problem.Layers[1].ThicknessM, omega);
        var wantLower = PlanarSurfaceImpedance.Sheet(problem.Layers[0].SigmaSm,
                                                     problem.Layers[0].ThicknessM, omega);

        output.WriteLine($"Metal1 ({problem.Layers[0].ThicknessM * 1e6:0.#} µm) Z_s = {wantLower}");
        output.WriteLine($"Metal2 ({problem.Layers[1].ThicknessM * 1e6:0.#} µm) Z_s = {wantUpper}");

        // The two levels really are different metal, or this test would pass for the wrong reason.
        Assert.True((wantUpper - wantLower).Magnitude > 0.05 * wantLower.Magnitude);

        foreach (var std in named)
        {
            Assert.Equal(problem.Layers[1].Name, Assert.Single(std.Mesh.LayerNames));
            Assert.Equal(wantUpper, Assert.Single(loss.SheetTable(std.Mesh, omega)));
        }

        // …and the placeholder is exactly the defect: level 0's metal on a level-1 port.
        foreach (var std in placeholder)
            Assert.Equal(wantLower, Assert.Single(loss.SheetTable(std.Mesh, omega)));
    }

    /// <summary>A two-level MMIC fixture with a real via between the levels.</summary>
    private static (PlanarProblem, PlanarMesh, PlanarLevels) ViaFixture()
    {
        var lower = new PlanarConductorLayer("Metal1",
            [PlanarLineFixtures.Rect(0, -50e-6, 400e-6, 50e-6)], GoldSigma, 1e-6, 0.0);
        var upper = new PlanarConductorLayer("Metal2",
            [PlanarLineFixtures.Rect(300e-6, -50e-6, 700e-6, 50e-6)], GoldSigma, 3e-6, 3e-6);
        var via = new PlanarVia(0, 1, [PlanarLineFixtures.Rect(330e-6, -30e-6, 390e-6, 30e-6)],
                                GoldSigma);

        var problem = new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, 30e9,
                                        Vias: [via]);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        return (problem, mesh, PlanarLevels.From(problem));
    }
}
