using System.Numerics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// Oracle tiers 0, 1 and 5 of brief-wbond-wbb §4 — the exact complex reduction
/// <c>Z_arr(ω) = (AᵀZ(ω)⁻¹A)⁻¹</c>.
/// </summary>
public class ImpedanceReductionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ImpedanceReductionTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static WBondDesign UniformArray(int n = 6, string material = "Gold", int arrays = 1) =>
        TestDesigns.ParallelArray(n, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0,
                                  diameterMil: 1.0, arrays: arrays)
            .WithMaterial(material);

    /// <summary>
    /// TIER 0 — <b>the cross-oracle between the simulator's exact path and the editor's fast one</b>
    /// (WB19b / D7): <c>Z_arr(ω)/jω → L_arr</c> as the conductor becomes ideal.
    ///
    /// <para><b>The limit is approached as 1/√σ, not 1/σ, and that is physics rather than a
    /// tolerance problem.</b> Deep in the skin regime <c>Z_int/R_dc → q/2 + ¼</c> with
    /// <c>q = a/δ ∝ √σ</c>, so <c>R_ac ∝ 1/√σ</c> — and the internal reactance <c>X_int</c> tracks it
    /// term for term, since the same q/2 appears in both. It is the same square-root law as WB4b,
    /// where an 85 °C conductivity drop costs 22 % at DC and only ~10.5 % at RF.</para>
    ///
    /// <para>So this test asserts the <b>convergence law</b> rather than a fixed bound: raising σ by a
    /// decade must shrink the residual by ~√10 ≈ 3.16. That is a far sharper statement — a wrong
    /// reduction can accidentally sit under any single threshold, but it will not obey the exponent.</para>
    /// </summary>
    [Fact]
    public void Tier0_AsTheConductorBecomesIdeal_ZArrOverJOmega_ConvergesToLArrAsInverseRootSigma()
    {
        const double frequency = 1e9;
        double omega = 2.0 * Math.PI * frequency;

        double previousGap = double.MaxValue;
        var gaps = new List<double>();

        foreach (double sigma in new[] { 4.1e7, 4.1e9, 4.1e11, 4.1e13, 4.1e15 })
        {
            var design = UniformArray(arrays: 2);
            design.Materials.Clear();
            design.Materials.Add(new WireMaterial("Ideal", sigma, 0.0, 19_300));
            foreach (var wire in design.AllWires()) wire.Material = "Ideal";

            var reduction = ImpedanceReduction.Create(design, parallel: false);
            var lArr = reduction.InductanceOnlyReduction();
            var zArr = reduction.ArrayImpedance(frequency);

            int m = reduction.ArrayCount;
            double worst = 0.0;
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    worst = Math.Max(worst,
                        Math.Abs(zArr[i * m + j].Imaginary / omega - lArr[i, j]) / Math.Abs(lArr[i, i]));

            Assert.True(worst < previousGap,
                $"The residual must shrink as sigma rises; at sigma={sigma:E1} it was {worst:E3}, " +
                $"up from {previousGap:E3}.");

            gaps.Add(worst);
            previousGap = worst;
        }

        // Two decades of sigma must buy ~10x (sqrt(100)) on the residual. The band is wide enough to
        // tolerate the transition out of the low-q regime at the first step and tight enough that a
        // 1/sigma or a constant-error implementation fails it.
        for (int i = 1; i < gaps.Count; i++)
        {
            double ratio = gaps[i - 1] / gaps[i];
            Assert.True(ratio is > 6.0 and < 16.0,
                $"Two decades of sigma should shrink the residual by ~10x (1/sqrt(sigma)); " +
                $"step {i} gave {ratio:F2}x ({gaps[i - 1]:E3} -> {gaps[i]:E3}).");
        }

        Assert.True(gaps[^1] < 1e-5,
            $"At sigma = 1e8x gold the residual should be well under 1e-5; got {gaps[^1]:E3}.");
    }

    /// <summary>
    /// TIER 1 — a single wire in a single array: the reduction must return exactly that wire's own
    /// series impedance, <c>jω·L + Z_int</c>, with no reduction machinery in the way.
    /// </summary>
    [Theory]
    [InlineData(1e8)]
    [InlineData(1e9)]
    [InlineData(1e10)]
    public void Tier1_SingleWireArray_ReturnsThatWiresOwnSeriesImpedance(double frequency)
    {
        var design = TestDesigns.SingleHorizontalWire(100.0, 20.0, 1.0);
        var reduction = ImpedanceReduction.Create(design, parallel: false);

        var zArr = reduction.ArrayImpedance(frequency);

        double omega = 2.0 * Math.PI * frequency;
        var expected = new Complex(0.0, omega * reduction.Inductance[0, 0])
                     + reduction.WireInternalImpedance(0, frequency);

        Assert.Equal(expected.Real, zArr[0].Real, Math.Abs(expected.Real) * 1e-12);
        Assert.Equal(expected.Imaginary, zArr[0].Imaginary, Math.Abs(expected.Imaginary) * 1e-12);
    }

    /// <summary>
    /// TIER 1 — N identical wires in one array, with the mutuals removed, reduce to the parallel
    /// combination of N identical complex impedances: <c>Z/N</c>. The complex analogue of WB-A's
    /// tier 4, and it exercises R and L together rather than separately.
    /// </summary>
    [Fact]
    public void Tier1_UncoupledIdenticalWires_ReduceToZOverN()
    {
        // Wires far enough apart that the mutual is negligible against the self.
        var design = TestDesigns.ParallelArray(n: 4, pitchMil: 4000.0, lengthMil: 100.0, heightMil: 20.0);
        var reduction = ImpedanceReduction.Create(design, parallel: false);

        const double frequency = 2e9;
        var zArr = reduction.ArrayImpedance(frequency);

        double omega = 2.0 * Math.PI * frequency;
        var single = new Complex(0.0, omega * reduction.Inductance[0, 0])
                   + reduction.WireInternalImpedance(0, frequency);
        var expected = single / 4.0;

        Assert.Equal(expected.Real, zArr[0].Real, Math.Abs(expected.Real) * 5e-3);
        Assert.Equal(expected.Imaginary, zArr[0].Imaginary, Math.Abs(expected.Imaginary) * 5e-3);
    }

    /// <summary>
    /// The reduction is symmetric — reciprocity is structural, exactly as in the real case.
    /// </summary>
    [Fact]
    public void ArrayImpedance_IsSymmetric()
    {
        var reduction = ImpedanceReduction.Create(UniformArray(n: 12, arrays: 3), parallel: false);
        var zArr = reduction.ArrayImpedance(5e9);

        int m = reduction.ArrayCount;
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                Assert.Equal(zArr[i * m + j], zArr[j * m + i]);
    }

    /// <summary>
    /// Every array's self-impedance must be passive: positive real part (it dissipates) and positive
    /// reactance (it is inductive). A sign error in the reduction shows here and nowhere else.
    /// </summary>
    [Theory]
    [InlineData(1e8)]
    [InlineData(1e10)]
    [InlineData(4e10)]
    public void ArrayImpedance_SelfTermsArePassiveAndInductive(double frequency)
    {
        var reduction = ImpedanceReduction.Create(UniformArray(n: 12, arrays: 3), parallel: false);
        var zArr = reduction.ArrayImpedance(frequency);

        int m = reduction.ArrayCount;
        for (int i = 0; i < m; i++)
        {
            Assert.True(zArr[i * m + i].Real > 0.0,
                $"Array {i} has non-positive resistance {zArr[i * m + i].Real:E3} at {frequency:E1} Hz.");
            Assert.True(zArr[i * m + i].Imaginary > 0.0,
                $"Array {i} has non-positive reactance {zArr[i * m + i].Imaginary:E3} at {frequency:E1} Hz.");
        }
    }

    /// <summary>
    /// TIER 5 — <b>how much the exact complex reduction actually differs from reducing R and L
    /// independently.</b> Reported, not gated: this is the number that justifies the owner's D1
    /// decision, and nobody knew it before this test existed.
    /// </summary>
    [Theory]
    [InlineData("Gold", 1e8)]
    [InlineData("Gold", 1e10)]
    [InlineData("Aluminium", 1e8)]
    [InlineData("Aluminium", 1e9)]
    public void Tier5_ExactVersusIndependentReduction_DifferenceIsMeasured(string material, double frequency)
    {
        var design = UniformArray(n: 8, material: material);
        var reduction = ImpedanceReduction.Create(design, parallel: false);

        var exact = reduction.ArrayImpedance(frequency)[0];

        // The independent route: reduce L on its own (the editor's L_arr) and reduce R on its own as
        // a parallel combination of the per-wire resistances, then add.
        double omega = 2.0 * Math.PI * frequency;
        double lArr = reduction.InductanceOnlyReduction()[0, 0];

        double conductanceSum = 0.0;
        for (int w = 0; w < reduction.WireCount; w++)
            conductanceSum += 1.0 / reduction.WireInternalImpedance(w, frequency).Real;
        double rArr = 1.0 / conductanceSum;

        var independent = new Complex(rArr, omega * lArr);

        double resistanceGap = Math.Abs(independent.Real / exact.Real - 1.0);
        double reactanceGap = Math.Abs(independent.Imaginary / exact.Imaginary - 1.0);
        double ratioROverWl = exact.Real / exact.Imaginary;

        _out.WriteLine($"{material,-10} {frequency,8:E1} Hz | R/wL {ratioROverWl,9:E2} | " +
                       $"R gap {resistanceGap,8:P2} | X gap {reactanceGap,8:P3}");

        // Reported, not gated — but the reduction must at least be in the same world.
        Assert.True(resistanceGap < 2.0 && reactanceGap < 0.5,
            $"{material} at {frequency:E1} Hz: R gap {resistanceGap:P2}, X gap {reactanceGap:P2}, " +
            $"R/wL {ratioROverWl:E2} — that is too far apart to be the same physics.");
    }

    /// <summary>
    /// The exact and independent reductions must CONVERGE as R/ωL falls — the property that makes the
    /// independent route defensible at high frequency and not at low.
    /// </summary>
    [Fact]
    public void ExactAndIndependentReductions_ConvergeAsROverWlFalls()
    {
        var design = UniformArray(n: 8, material: "Aluminium");
        var reduction = ImpedanceReduction.Create(design, parallel: false);

        double previousGap = double.MaxValue;

        foreach (double frequency in new[] { 1e7, 1e8, 1e9, 1e10 })
        {
            var exact = reduction.ArrayImpedance(frequency)[0];
            double omega = 2.0 * Math.PI * frequency;
            double lArr = reduction.InductanceOnlyReduction()[0, 0];

            double gap = Math.Abs(exact.Imaginary / (omega * lArr) - 1.0);
            Assert.True(gap < previousGap,
                $"The reactance gap must shrink as R/wL falls; at {frequency:E1} Hz it was {gap:E3}, " +
                $"up from {previousGap:E3}.");
            previousGap = gap;
        }
    }

    /// <summary>
    /// <b>L is filled once and reused across frequencies</b> (R-wbb-3). Evaluating at many frequencies
    /// must not refill it — refilling would cost ~0.15 s per point at 600 wires.
    /// </summary>
    [Fact]
    public void ArrayImpedance_ReusesTheInductanceMatrixAcrossFrequencies()
    {
        var reduction = ImpedanceReduction.Create(UniformArray(n: 8), parallel: false);
        var before = reduction.Inductance;

        foreach (double frequency in new[] { 1e8, 1e9, 1e10 })
            reduction.ArrayImpedance(frequency);

        Assert.Same(before, reduction.Inductance);
        Assert.Same(before.Values, reduction.Inductance.Values);
    }

    // ---------------------------------------------------------------- ComplexLu

    /// <summary>
    /// The complex factorisation against a hand-built inverse, on a matrix that is symmetric but
    /// <b>not</b> Hermitian — the case Cholesky cannot handle.
    /// </summary>
    [Fact]
    public void ComplexLu_SolvesAComplexSymmetricSystem()
    {
        // Symmetric, not Hermitian: A^T = A while A* != A.
        var a = new[]
        {
            new Complex(2, 3), new Complex(1, -1), new Complex(0, 2),
            new Complex(1, -1), new Complex(4, 1), new Complex(-1, 1),
            new Complex(0, 2), new Complex(-1, 1), new Complex(3, -2),
        };
        var b = new[] { new Complex(1, 0), new Complex(0, 1), new Complex(2, -1) };

        var x = ComplexLu.Factor(a, 3).Solve(b);

        // Residual against the original matrix: A x must reproduce b.
        for (int i = 0; i < 3; i++)
        {
            Complex sum = Complex.Zero;
            for (int j = 0; j < 3; j++) sum += a[i * 3 + j] * x[j];
            Assert.Equal(b[i].Real, sum.Real, 1e-12);
            Assert.Equal(b[i].Imaginary, sum.Imaginary, 1e-12);
        }
    }

    /// <summary>
    /// Partial pivoting is doing real work: a matrix with a zero leading pivot is perfectly solvable
    /// and would break an unpivoted factorisation on the first step.
    /// </summary>
    [Fact]
    public void ComplexLu_HandlesAZeroLeadingPivot()
    {
        var a = new[]
        {
            Complex.Zero,      new Complex(1, 0),
            new Complex(1, 0), new Complex(0, 1),
        };
        var b = new[] { new Complex(2, 0), new Complex(0, 0) };

        var x = ComplexLu.Factor(a, 2).Solve(b);

        for (int i = 0; i < 2; i++)
        {
            Complex sum = Complex.Zero;
            for (int j = 0; j < 2; j++) sum += a[i * 2 + j] * x[j];
            Assert.Equal(b[i].Real, sum.Real, 1e-12);
            Assert.Equal(b[i].Imaginary, sum.Imaginary, 1e-12);
        }
    }

    [Fact]
    public void ComplexLu_SingularMatrix_IsRefusedWithAUsefulMessage()
    {
        var singular = new[] { Complex.One, Complex.One, Complex.One, Complex.One };
        var ex = Assert.Throws<InvalidOperationException>(() => ComplexLu.Factor(singular, 2));
        Assert.Contains("singular", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class DesignMaterialExtensions
{
    /// <summary>Sets every wire in a design to one metal — test convenience.</summary>
    public static WBondDesign WithMaterial(this WBondDesign design, string material)
    {
        foreach (var wire in design.AllWires()) wire.Material = material;
        return design;
    }
}
