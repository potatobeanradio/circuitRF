namespace CircuitRF.WBond.Tests;

/// <summary>
/// Oracle tiers 4, 5 and 8 of brief-wbond-wba §5 — the array-basis reduction
/// <c>L_arr = (Aᵀ L⁻¹ A)⁻¹</c>.
///
/// <para><b>The derivation was verified numerically before implementation</b> (wbond.md §3.4), so a
/// failure here means the <i>implementation</i> diverged from a derivation known to be correct —
/// report which, rather than editing the formula.</para>
/// </summary>
public class ArrayReductionTests
{
    /// <summary>Builds an InductanceMatrix directly from a dense symmetric array, for algebra-only tests.</summary>
    private static InductanceMatrix FromDense(double[,] values)
    {
        int n = values.GetLength(0);
        var flat = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                flat[i * n + j] = values[i, j];

        return InductanceMatrix.FromDense(flat, n);
    }

    // ---------------------------------------------------------------- tier 4

    /// <summary>
    /// TIER 4 — N identical coupled wires in one array reduce to <c>(L_s + (N−1)M)/N</c>.
    ///
    /// <para>The classic closed form for N parallel equally-coupled inductors, and the sharpest
    /// available check that the reduction is the right one: a plain parallel combination (ignoring
    /// mutuals) would give L_s/N, and summing mutuals the wrong way would give L_s + (N−1)M.</para>
    /// </summary>
    [Theory]
    [InlineData(2, 1.0, 0.3)]
    [InlineData(4, 1.0, 0.3)]
    [InlineData(4, 2.5, -0.4)]
    [InlineData(8, 1.0, 0.9)]
    public void Tier4_IdenticalCoupledWires_ReduceToTheClassicClosedForm(int n, double ls, double m)
    {
        var dense = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                dense[i, j] = i == j ? ls : m;

        var reduction = ArrayReduction.Reduce(FromDense(dense), new int[n], arrayCount: 1);

        double expected = (ls + (n - 1) * m) / n;
        Assert.Equal(expected, reduction[0, 0], Math.Abs(expected) * 1e-12);
    }

    /// <summary>
    /// TIER 4 — with the mutuals removed the reduction must collapse to the plain parallel
    /// combination L_s/N. Guards against a reduction that happens to be right only when coupled.
    /// </summary>
    [Fact]
    public void Tier4_UncoupledWires_ReduceToPlainParallelCombination()
    {
        const int n = 5;
        const double ls = 2.0;
        var dense = new double[n, n];
        for (int i = 0; i < n; i++) dense[i, i] = ls;

        var reduction = ArrayReduction.Reduce(FromDense(dense), new int[n], arrayCount: 1);

        Assert.Equal(ls / n, reduction[0, 0], 1e-14);
    }

    /// <summary>
    /// TIER 4 — a single wire in its own array reduces to itself. The degenerate case the
    /// "ungrouped wires each form their own array" rule relies on (D5).
    /// </summary>
    [Fact]
    public void Tier4_SingleWirePerArray_ReducesToTheWireBasisMatrixItself()
    {
        var dense = new double[,]
        {
            { 2000e-12, 500e-12, 100e-12 },
            { 500e-12, 2100e-12, 300e-12 },
            { 100e-12, 300e-12, 1900e-12 },
        };

        var reduction = ArrayReduction.Reduce(FromDense(dense), [0, 1, 2], arrayCount: 3);

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.Equal(dense[i, j], reduction[i, j], Math.Abs(dense[i, j]) * 1e-9 + 1e-24);
    }

    // ---------------------------------------------------------------- tier 5

    /// <summary>
    /// TIER 5 — the defining identity, checked directly against the two assumptions with no closed
    /// form involved: impose <b>V</b> = <b>Au</b>, solve <b>LI</b> = <b>V</b>, take
    /// <b>J</b> = <b>AᵀI</b>, and require <b>L_arr J</b> = <b>u</b>.
    ///
    /// <para>This is oracle-free — it needs nothing but the assumptions themselves — and it holds for
    /// any geometry, so it is run over random excitations of a real 12-array design.</para>
    /// </summary>
    [Fact]
    public void Tier5_LArrTimesJ_RecoversTheImposedArrayVoltage()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 60, arrayCount: 6));
        var l = InductanceMatrix.Fill(mesh);
        var reduction = ArrayReduction.Reduce(l, mesh);

        int n = mesh.WireCount, m = mesh.ArrayCount;
        var rng = new Random(7);

        for (int trial = 0; trial < 5; trial++)
        {
            // u: an arbitrary per-array voltage.
            var u = new double[m];
            for (int a = 0; a < m; a++) u[a] = rng.NextDouble() * 2.0 - 1.0;

            // V = A u, then solve L I = V.
            var v = new double[n];
            for (int i = 0; i < n; i++) v[i] = u[mesh.ArrayOfWire[i]];

            var factor = CholeskyFactor.Factor(l.Values, n);
            factor.SolveInPlace(v);   // v now holds I

            // J = A^T I
            var j = new double[m];
            for (int i = 0; i < n; i++) j[mesh.ArrayOfWire[i]] += v[i];

            // L_arr J must recover u.
            for (int a = 0; a < m; a++)
            {
                double recovered = 0.0;
                for (int b = 0; b < m; b++) recovered += reduction[a, b] * j[b];
                Assert.Equal(u[a], recovered, 1e-9);
            }
        }
    }

    /// <summary>
    /// TIER 5 — <c>L_arr</c> is symmetric and positive definite for arbitrary real geometry.
    /// Reciprocity is <b>structural</b> here, not a tolerance: L symmetric ⇒ L⁻¹ symmetric ⇒
    /// AᵀL⁻¹A symmetric.
    /// </summary>
    [Fact]
    public void Tier5_LArr_IsSymmetricAndPositiveDefinite()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4));
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);

        int m = mesh.ArrayCount;
        var flat = new double[m * m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
            {
                Assert.Equal(reduction[i, j], reduction[j, i], 1e-30);
                flat[i * m + j] = reduction[i, j];
            }
        }

        // Factorising succeeds exactly when the matrix is positive definite.
        var factor = CholeskyFactor.Factor(flat, m);
        Assert.Equal(m, factor.Order);
    }

    /// <summary>
    /// TIER 5 — current shares sum to the array current, exactly (KCL — assumption 2, returned to
    /// the caller rather than merely assumed).
    /// </summary>
    [Fact]
    public void Tier5_CurrentShares_SumToTheArrayCurrent()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 60, arrayCount: 6));
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);

        var drive = new double[mesh.ArrayCount];
        drive[0] = 1.0;
        drive[3] = -0.4;

        var shares = reduction.CurrentShares(drive);

        var totals = new double[mesh.ArrayCount];
        for (int i = 0; i < mesh.WireCount; i++)
            totals[mesh.ArrayOfWire[i]] += shares[i];

        for (int a = 0; a < mesh.ArrayCount; a++)
            Assert.Equal(drive[a], totals[a], 1e-10);
    }

    /// <summary>
    /// TIER 5 — the physics the current sharing exposes: <b>edge wires of an array carry more
    /// current than centre wires</b>, because they have less mutual coupling to their neighbours.
    ///
    /// <para>Measured on a uniform 6-wire array: the edges carry ~30–40 % more than the middle. This
    /// is a real, well-known array effect and it falls out of the reduction with no extra
    /// machinery — so it is worth pinning, both as physics and as a check that the current-sharing
    /// back-substitution is not silently returning a uniform split.</para>
    /// </summary>
    [Fact]
    public void Tier5_EdgeWiresOfAnArray_CarryMoreCurrentThanCentreWires()
    {
        var design = TestDesigns.ParallelArray(n: 6, pitchMil: 5.0, lengthMil: 100.0, heightMil: 20.0);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);

        var shares = reduction.CurrentShares([1.0]);

        double edge = 0.5 * (shares[0] + shares[5]);
        double centre = 0.5 * (shares[2] + shares[3]);

        Assert.True(edge > centre * 1.15,
            $"Edge wires should carry clearly more current than centre wires: " +
            $"edge={edge:F4} A, centre={centre:F4} A, ratio {edge / centre:F3}.");

        // The split is symmetric about the array's midline.
        Assert.Equal(shares[0], shares[5], 1e-12);
        Assert.Equal(shares[1], shares[4], 1e-12);
    }

    /// <summary>
    /// TIER 5 — an <b>undriven</b> array carries a circulating current that sums to zero.
    ///
    /// <para>Its wires are tied together at both ends, so it is a shorted turn and the driven array
    /// induces a real current distribution in it. A reduction that ignored inter-array coupling
    /// would return exactly zero in every wire.</para>
    /// </summary>
    [Fact]
    public void Tier5_UndrivenArray_CarriesACirculatingCurrentSummingToZero()
    {
        var design = TestDesigns.ParallelArray(n: 6, pitchMil: 5.0, lengthMil: 100.0, heightMil: 20.0, arrays: 2);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);

        var shares = reduction.CurrentShares([1.0, 0.0]);

        double undrivenTotal = 0.0, undrivenAbs = 0.0;
        for (int i = 0; i < mesh.WireCount; i++)
        {
            if (mesh.ArrayOfWire[i] != 1) continue;
            undrivenTotal += shares[i];
            undrivenAbs += Math.Abs(shares[i]);
        }

        Assert.Equal(0.0, undrivenTotal, 1e-12);
        Assert.True(undrivenAbs > 1e-3,
            $"The undriven array is a shorted turn and must carry a real circulating current; " +
            $"total |I| was only {undrivenAbs:E3} A.");
    }

    // ---------------------------------------------------------------- tier 8

    /// <summary>
    /// TIER 8 — <c>L_arr</c> is invariant to the order of wires within an array. The reduction must
    /// depend on membership, not on enumeration order.
    /// </summary>
    [Fact]
    public void Tier8_ReductionIsInvariantToWireOrderWithinAnArray()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 40, arrayCount: 4));
        var l = InductanceMatrix.Fill(mesh);
        var reference = ArrayReduction.Reduce(l, mesh);

        // Permute the wire-to-array map's *storage* order by relabelling nothing but the sequence:
        // reversing the wires inside each array must not move a single value.
        var reversedMap = (int[])mesh.ArrayOfWire.Clone();
        Array.Reverse(reversedMap);

        // Reverse the matrix to match, so the same physical wires stay in the same arrays.
        int n = l.Order;
        var reversedValues = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                reversedValues[i * n + j] = l[n - 1 - i, n - 1 - j];

        var permuted = ArrayReduction.Reduce(
            InductanceMatrix.FromDense(reversedValues, n), reversedMap, mesh.ArrayCount);

        for (int i = 0; i < mesh.ArrayCount; i++)
            for (int j = 0; j < mesh.ArrayCount; j++)
                Assert.Equal(reference[mesh.ArrayCount - 1 - i, mesh.ArrayCount - 1 - j],
                             permuted[i, j],
                             Math.Abs(reference[i, i]) * 1e-9);
    }

    /// <summary>
    /// An empty array is refused with a message naming it, not with a Cholesky pivot failure
    /// (R-wb-1). The failure mode this prevents is a confusing linear-algebra error far from its
    /// cause.
    /// </summary>
    [Fact]
    public void EmptyArray_IsRefusedByName()
    {
        var dense = new double[,] { { 1.0, 0.1 }, { 0.1, 1.0 } };

        // Both wires in array 0; array 1 is declared but empty.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArrayReduction.Reduce(FromDense(dense), [0, 0], arrayCount: 2, arrayNames: ["G1", "G2"]));

        Assert.Contains("G2", ex.Message);
        Assert.Contains("rank-deficient", ex.Message);
    }
}
