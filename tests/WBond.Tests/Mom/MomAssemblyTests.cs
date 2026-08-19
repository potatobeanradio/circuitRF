using System.Numerics;
using CircuitRF.WBond.Mom;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// <b>P</b>'s definiteness, the segment-count convergence that justifies the default of 24, and the
/// structure of <b>G</b>, <b>K̃</b>, <b>W</b> and <b>H</b>.
/// </summary>
public sealed class MomAssemblyTests
{
    private static WireMomMesh Mesh(WBondDesign design, int target = 24) =>
        WireMomMesh.Build(design, WireMomSettings.Default with { TargetSegmentsPerWire = target });

    private static WBondDesign BallBond(int wires = 4, int arrays = 2) =>
        TestDesigns.PowerAmplifier(wireCount: wires, arrayCount: arrays, pointsPerWire: 7);

    // ---------------------------------------------------------------- 9.6

    /// <summary>
    /// <b>P</b> is symmetric positive definite. <see cref="CholeskyFactor.Factor"/> succeeding <i>is</i>
    /// the assertion — it throws on a non-SPD matrix — and a P that fails it means a broken image sign
    /// or an overlapping cell, which is far cheaper to learn here than inside WM-2's solve.
    ///
    /// <para><b>The brief asks for this "with images on and with images off", and a design with the
    /// plane off cannot be meshed</b> (§3.4's refusal). So the images-off case is built here as the
    /// direct-only potential matrix over the same cells, which is numerically what "images off" means,
    /// and it is checked for the same property.</para>
    /// </summary>
    [Fact]
    public void P_IsPositiveDefinite_WithAndWithoutTheImageTerm()
    {
        var mesh = Mesh(BallBond());

        CholeskyFactor.Factor(NodePotential.Fill(mesh, parallel: false), mesh.NodeCount);

        int nn = mesh.NodeCount;
        var direct = new double[nn * nn];
        double scale = 1.0 / (4.0 * Math.PI * PotentialCoefficients.Epsilon0);
        for (int m = 0; m < nn; m++)
            for (int n = m; n < nn; n++)
            {
                double acc = 0.0;
                for (int ci = mesh.NodeCellStart[m]; ci < mesh.NodeCellStart[m + 1]; ci++)
                    for (int cj = mesh.NodeCellStart[n]; cj < mesh.NodeCellStart[n + 1]; cj++)
                        acc += PotentialCoefficients.Kernel(
                            in mesh.Halves[mesh.NodeCellIndex[ci]], in mesh.Halves[mesh.NodeCellIndex[cj]]);

                double v = scale * acc / (mesh.NodeCellLength[m] * mesh.NodeCellLength[n]);
                direct[m * nn + n] = v;
                direct[n * nn + m] = v;
            }

        CholeskyFactor.Factor(direct, nn);
    }

    // ---------------------------------------------------------------- 9.7

    /// <summary>
    /// The convergence table that justifies <see cref="WireMomSettings.TargetSegmentsPerWire"/> = 24.
    ///
    /// <para>The quantity is the wire's total capacitance to the plane, <c>Σ_{m,n} (P⁻¹)_{mn}</c>, on a
    /// single ball bond over ground at 6, 12, 24 and 48 segments. Measured (see
    /// <c>src/WBond/Mom/RESOLVED.md</c> for the table): it rises monotonically and the 24 → 48 change is
    /// a fraction of the 12 → 24 one, which is what makes 24 a defensible default rather than a
    /// guess.</para>
    ///
    /// <para><b>L needs no convergence test</b> — the subdivision-invariance gate proves it is exactly
    /// invariant, so only the charge side has anything to converge.</para>
    /// </summary>
    [Fact]
    public void TotalCapacitanceConvergesMonotonically_AndTwentyFourIsEnough()
    {
        var design = new WBondDesign
        {
            Arrays = { new WireArray { Name = "G1", Wires = { TestDesigns.BallBond(0, 100, 0, 4, 1, 22, points: 7) } } },
        };

        var c = new double[4];
        int[] targets = [6, 12, 24, 48];
        for (int i = 0; i < targets.Length; i++)
        {
            var mesh = Mesh(design, targets[i]);
            Assert.True(mesh.SegmentCount <= 60, $"§9.7 must stay cheap: N_s = {mesh.SegmentCount}.");
            c[i] = TotalCapacitance(mesh);
        }

        for (int i = 1; i < c.Length; i++)
            Assert.True(c[i] > c[i - 1],
                $"C must rise monotonically with refinement: {string.Join(", ", c.Select(v => v.ToString("E6")))}");

        double step1224 = c[2] - c[1];
        double step2448 = c[3] - c[2];
        Assert.True(step2448 < step1224,
            $"The 24->48 change ({step2448:E3} F) must be below the 12->24 change ({step1224:E3} F).");
        Assert.True(step2448 / c[3] < 0.01,
            $"24 -> 48 moves C by {step2448 / c[3]:P3}, which is what makes 24 the shipped default.");
    }

    /// <summary><c>Σ_{m,n} (P⁻¹)_{mn}</c> — the total charge that one volt on every cell puts on the wire.</summary>
    private static double TotalCapacitance(WireMomMesh mesh)
    {
        int nn = mesh.NodeCount;
        var factor = CholeskyFactor.Factor(NodePotential.Fill(mesh, parallel: false), nn);
        var ones = new double[nn];
        Array.Fill(ones, 1.0);
        factor.SolveInPlace(ones);
        return ones.Sum();
    }

    // ---------------------------------------------------------------- 9.8

    [Fact]
    public void G_IsSymmetricAndPositiveDefinite()
    {
        var mesh = Mesh(BallBond());
        var assembly = MomAssembly.Build(mesh);

        AssertSymmetric(assembly.G, assembly.ReducedCount, 1e-12, "G");
        CholeskyFactor.Factor(assembly.G, assembly.ReducedCount);
    }

    [Fact]
    public void H_IsSymmetricPositiveDefinite_AndIsTheLeadingBlockOfGInverse()
    {
        var mesh = Mesh(BallBond());
        var assembly = MomAssembly.Build(mesh);
        int t = assembly.TerminalCount;

        Assert.Equal(4, t);
        Assert.Equal(t * t, assembly.H.Length);
        AssertSymmetric(assembly.H, t, 1e-12, "H");
        CholeskyFactor.Factor(assembly.H, t);
    }

    /// <summary>
    /// <c>K̃</c> is symmetric, positive <b>semi</b>-definite, and its nullity is exactly the loop count
    /// <c>W − M</c> — a direct check on the claim that <c>null(K̃) = null(Ãᵀ)</c>.
    ///
    /// <para>The rank is checked without an eigensolver, and the check is two-sided. Deflating
    /// <c>K̃</c> by the <i>explicit</i> loop vectors (+1 along one wire, −1 along another wire of the
    /// same array — the loop terminal shorting creates) must turn it positive definite; deflating it by
    /// one loop vector too few must not. Cholesky succeeding and failing in those two places pins the
    /// nullity from both sides.</para>
    /// </summary>
    [Theory]
    [InlineData(4, 2)]    // 2 loops
    [InlineData(6, 2)]    // 4 loops
    [InlineData(3, 3)]    // 0 loops -- one wire per array, so K~ is already definite
    public void KTilde_IsSymmetricPsd_WithNullityEqualToTheLoopCount(int wires, int arrays)
    {
        var mesh = Mesh(BallBond(wires, arrays), target: 8);
        var assembly = MomAssembly.Build(mesh);
        int n = assembly.SegmentCount;

        AssertSymmetric(assembly.KTilde, n, 1e-12, "K~");

        var loops = LoopVectors(mesh);
        Assert.Equal(mesh.WireCount - mesh.ArrayCount, loops.Count);
        Assert.Equal(mesh.Report.LoopCount, loops.Count);

        // Every loop vector really is in the null space of A~^T, hence of K~.
        double scale = Enumerable.Range(0, n).Max(i => Math.Abs(assembly.KTilde[i * n + i]));
        foreach (var z in loops)
        {
            for (int r = 0; r < mesh.ReducedCount; r++)
            {
                double acc = 0.0;
                for (int k = 0; k < n; k++)
                {
                    if (mesh.ReducedStart(k) == r) acc += z[k];
                    if (mesh.ReducedEnd(k) == r) acc -= z[k];
                }
                Assert.True(Math.Abs(acc) < 1e-12, "A loop vector must satisfy A~^T z = 0.");
            }

            double quadratic = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) quadratic += z[i] * assembly.KTilde[i * n + j] * z[j];
            Assert.True(Math.Abs(quadratic) < 1e-9 * scale, $"z^T K~ z = {quadratic:E3} must vanish.");
        }

        // The nullity, pinned from BOTH sides without an eigensolver. The loop vectors above give
        // nullity >= loops.Count; deflating by all of them and finding a healthy Cholesky gives
        // nullity <= loops.Count, because the deflation adds a subspace of exactly that dimension.
        //
        // The pivot is compared rather than merely required to exist: an under-deflated matrix is
        // singular in exact arithmetic but factorises anyway with a pivot ~1e-16 of the scale, so
        // "Cholesky threw" is not a reliable rank test and the RATIO is.
        double full = MinPivot(Deflate(assembly.KTilde, n, loops, loops.Count, scale), n);
        Assert.True(full > 0.0, "Deflating K~ by every loop vector must leave a positive definite matrix.");

        if (loops.Count > 0)
        {
            double under = MinPivot(Deflate(assembly.KTilde, n, loops, loops.Count - 1, scale), n);
            Assert.True(under < 1e-6 * full,
                $"One loop short of full deflation must leave K~ numerically singular: smallest pivot " +
                $"{under:E3} against {full:E3} when fully deflated.");
        }
    }

    /// <summary>The smallest Cholesky pivot, or 0 if the factorisation fails outright.</summary>
    private static double MinPivot(double[] m, int n)
    {
        CholeskyFactor factor;
        try { factor = CholeskyFactor.Factor(m, n); }
        catch (InvalidOperationException) { return 0.0; }

        double min = double.MaxValue;
        for (int i = 0; i < n; i++) min = Math.Min(min, factor.Lower[i * n + i]);
        return min;
    }

    /// <summary>
    /// One loop per wire past the first in each array: <c>+1</c> along that wire's segments and
    /// <c>−1</c> along the array's first wire's. Both wires are shorted at both ends, so this is a
    /// closed current loop that moves no charge onto any node.
    /// </summary>
    private static List<double[]> LoopVectors(WireMomMesh mesh)
    {
        var loops = new List<double[]>();
        var firstOfArray = new int[mesh.ArrayCount];
        Array.Fill(firstOfArray, -1);

        for (int w = 0; w < mesh.WireCount; w++)
        {
            int a = mesh.ArrayOfWire[w];
            if (firstOfArray[a] < 0) { firstOfArray[a] = w; continue; }

            var z = new double[mesh.SegmentCount];
            for (int k = mesh.WireSegStart[w]; k < mesh.WireSegStart[w] + mesh.WireSegCount[w]; k++) z[k] = 1.0;
            int f = firstOfArray[a];
            for (int k = mesh.WireSegStart[f]; k < mesh.WireSegStart[f] + mesh.WireSegCount[f]; k++) z[k] = -1.0;
            loops.Add(z);
        }

        return loops;
    }

    /// <summary><c>K̃ + σ Σ z zᵀ</c> over the first <paramref name="count"/> loop vectors.</summary>
    private static double[] Deflate(double[] k, int n, List<double[]> loops, int count, double sigma)
    {
        var m = (double[])k.Clone();
        for (int l = 0; l < count; l++)
        {
            var z = loops[l];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) m[i * n + j] += sigma * z[i] * z[j];
        }
        return m;
    }

    /// <summary>
    /// §2.6 item 4: <c>Eᵀ G⁻¹ Ãᵀ = (Ã G⁻¹ E)ᵀ = Wᵀ</c>, so <b>W</b> serves both places and is computed
    /// once. Checked against an independently formed <c>Ã G⁻¹ E</c>.
    /// </summary>
    [Fact]
    public void W_IsBothATildeGInverseE_AndTheLeadingRowsOfGInverseATildeTranspose()
    {
        var mesh = Mesh(BallBond(), target: 8);
        var assembly = MomAssembly.Build(mesh);

        int nr = assembly.ReducedCount, ns = assembly.SegmentCount, t = assembly.TerminalCount;
        var gFactor = CholeskyFactor.Factor(assembly.G, nr);

        // Independently: the first T columns of G^-1, then A~ applied on the left.
        var gInvE = new double[nr * t];
        for (int port = 0; port < t; port++)
        {
            var e = new double[nr];
            e[port] = 1.0;
            gFactor.SolveInPlace(e);
            for (int r = 0; r < nr; r++) gInvE[r * t + port] = e[r];
        }

        double scale = 0.0;
        for (int i = 0; i < assembly.W.Length; i++) scale = Math.Max(scale, Math.Abs(assembly.W[i]));

        for (int k = 0; k < ns; k++)
            for (int port = 0; port < t; port++)
            {
                double expected = gInvE[mesh.ReducedStart(k) * t + port] - gInvE[mesh.ReducedEnd(k) * t + port];
                Assert.True(Math.Abs(assembly.W[k * t + port] - expected) < 1e-10 * scale,
                    $"W[{k},{port}] = {assembly.W[k * t + port]:E6} against A~G^-1E = {expected:E6}.");
            }
    }

    private static void AssertSymmetric(double[] m, int n, double tolerance, string name)
    {
        double scale = 0.0;
        for (int i = 0; i < n * n; i++) scale = Math.Max(scale, Math.Abs(m[i]));

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                Assert.True(Math.Abs(m[i * n + j] - m[j * n + i]) <= tolerance * scale,
                    $"{name} is not symmetric at [{i},{j}].");
    }

    // ---------------------------------------------------------------- the near/far threshold

    /// <summary>
    /// The shipped <see cref="WireMomSettings.FarThresholdFactor"/> keeps the per-wire self capacitance
    /// inside 0.1 % of an all-near reference, on a design with widely separated arrays — which is the
    /// only kind on which the threshold means anything, since within one array no pair is ever far.
    ///
    /// <para>This is the gate behind the sweep recorded on that setting: 3.5, the value the
    /// <i>wire</i>-basis kernel ships, is outside this bound at half-cell scale, and 4.0 is the
    /// smallest swept value inside it.</para>
    /// </summary>
    [Fact]
    public void TheShippedFarThreshold_KeepsSelfCapacitanceInsideOneTenthOfAPercent()
    {
        var mesh = Mesh(TestDesigns.PowerAmplifier(wireCount: 8, arrayCount: 4, pointsPerWire: 7));
        Assert.True(mesh.SegmentCount <= 250, $"Routine tests stay at N_s <= 250: {mesh.SegmentCount}.");

        var shipped = WireCapacitance(mesh, WireMomSettings.Default.FarThresholdFactor);
        var exact = WireCapacitance(mesh, double.PositiveInfinity);

        int w = mesh.WireCount;
        for (int i = 0; i < w; i++)
        {
            double error = Math.Abs(shipped[i * w + i] - exact[i * w + i]) / Math.Abs(exact[i * w + i]);
            Assert.True(error < 1e-3, $"Wire {i}'s self capacitance is {error:P4} off the all-near reference.");
        }

        // And 3.5 -- the wire-basis kernel's own value -- really is outside it, or this test is not
        // holding anything shut.
        var wireBasisValue = WireCapacitance(mesh, PotentialCoefficients.FarThresholdFactor);
        double worst35 = 0.0;
        for (int i = 0; i < w; i++)
            worst35 = Math.Max(worst35, Math.Abs(wireBasisValue[i * w + i] - exact[i * w + i]) / Math.Abs(exact[i * w + i]));
        Assert.True(worst35 > 1e-4, $"3.5 measured {worst35:P4} here; if it were free of error the shipped 4.0 would be pointless.");
    }

    /// <summary><c>C = (Bᵀ P B)⁻¹</c> in the wire basis, at one near/far threshold.</summary>
    private static double[] WireCapacitance(WireMomMesh mesh, double farThresholdFactor)
    {
        var p = NodePotential.Fill(mesh, parallel: false, farThresholdFactor: farThresholdFactor);
        var b = NodePotential.WireReduction(mesh);
        int nn = mesh.NodeCount, w = mesh.WireCount;

        var pb = new double[nn * w];
        for (int m = 0; m < nn; m++)
            for (int n = 0; n < nn; n++)
            {
                double v = p[m * nn + n];
                for (int j = 0; j < w; j++) pb[m * w + j] += v * b[n * w + j];
            }

        var reduced = new double[w * w];
        for (int m = 0; m < nn; m++)
            for (int i = 0; i < w; i++)
            {
                double bm = b[m * w + i];
                if (bm == 0.0) continue;
                for (int j = 0; j < w; j++) reduced[i * w + j] += bm * pb[m * w + j];
            }

        var factor = CholeskyFactor.Factor(reduced, w);
        var inverse = new double[w * w];
        for (int col = 0; col < w; col++)
        {
            var e = new double[w];
            e[col] = 1.0;
            factor.SolveInPlace(e);
            for (int r = 0; r < w; r++) inverse[r * w + col] = e[r];
        }
        return inverse;
    }

    // ---------------------------------------------------------------- D(omega)

    /// <summary>
    /// <b>D summed over a wire's segments equals that wire's own D exactly</b>, because the scaling is
    /// by length and lengths add. That additivity is half of the identity gate against the analytic
    /// model; the other half is that partial inductance is additive under subdivision.
    /// </summary>
    [Theory]
    [InlineData(1e8)]
    [InlineData(1e10)]
    [InlineData(4e10)]
    public void SegmentInternalImpedance_SumsToTheWiresOwn(double frequencyHz)
    {
        var design = BallBond();
        var mesh = Mesh(design);
        var d = SegmentInternalZ.Create(mesh).Diagonal(frequencyHz);

        var reduction = ImpedanceReduction.Create(design, parallel: false);

        for (int w = 0; w < mesh.WireCount; w++)
        {
            Complex sum = Complex.Zero;
            for (int k = mesh.WireSegStart[w]; k < mesh.WireSegStart[w] + mesh.WireSegCount[w]; k++) sum += d[k];

            var expected = reduction.WireInternalImpedance(w, frequencyHz);
            Assert.Equal(expected.Real, sum.Real, 12);
            Assert.True(Math.Abs(sum.Real - expected.Real) / expected.Real < 1e-12);
            Assert.True(Math.Abs(sum.Imaginary - expected.Imaginary) / expected.Imaginary < 1e-12);
        }
    }

    /// <summary>The Bessel evaluation is cached per distinct (radius, sigma), not per segment.</summary>
    [Fact]
    public void InternalImpedanceGroupsByRadiusAndConductivity()
    {
        var mesh = Mesh(BallBond(wires: 6, arrays: 3));
        var d = SegmentInternalZ.Create(mesh);

        Assert.Equal(mesh.SegmentCount, d.SegmentCount);
        Assert.Equal(1, d.GroupCount);   // one radius, one metal, however many segments

        mesh.Wires[0].DiameterNm *= 2;
        Assert.Equal(2, SegmentInternalZ.Create(Mesh(mesh.Design, 24)).GroupCount);
    }
}
