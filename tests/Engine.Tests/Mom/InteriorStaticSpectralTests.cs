using System.Numerics;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>MIM-4 / milestone 1 — the interior-height static SPECTRAL kernel, oracle'd before any inverse
/// transform exists.</b>
///
/// <para>L8a's ladder rule applies verbatim and is the reason this file has no ρ in it: <i>a
/// spectral function that is wrong produces a spatial function that is wrong in a way no downstream
/// oracle can localise</i>. Every tier here is exact or independently computed, and each passes
/// before the next is written.</para>
///
/// <list type="bullet">
///   <item><b>Tier 0</b> — structural and free: symmetry in (z, z′), continuity of G̃ and of the
///         flux P·∂G̃/∂z across an interface, and the refusal for a point inside a wall.</item>
///   <item><b>Tier 1</b> — the exact reductions. A homogeneous "stack" over a PEC floor is free
///         space plus one image AT EVERY PAIR OF HEIGHTS, and splitting it into layers of the same
///         material may not move a digit.</item>
///   <item><b>Tier 2</b> — the shipped one-slab kernel: with both points on the slab top this must
///         reproduce <c>1 + Γ_e</c>, the spectral form <see cref="StaticGreens"/>'s image series
///         inverts, and it must agree with <see cref="LayeredStaticGreens"/> everywhere that class
///         is defined.</item>
///   <item><b>Tier 3</b> — an INDEPENDENT finite-volume solve of the same Sturm-Liouville problem,
///         sharing no reflection coefficient, no cascade and no exponential basis with the thing
///         under test. Gated on Richardson behaviour (halving h must quarter the error) as well as
///         on the error itself, so a passing number means the FD is converging onto the kernel
///         rather than the two agreeing on a wrong answer.</item>
/// </list>
/// </summary>
public sealed class InteriorStaticSpectralTests
{
    private readonly ITestOutputHelper _out;
    public InteriorStaticSpectralTests(ITestOutputHelper output) => _out = output;

    private static double Rel(Complex expected, Complex actual, double floor = 1e-300) =>
        (expected - actual).Magnitude / Math.Max(expected.Magnitude, floor);

    /// <summary>
    /// <b>Deviation measured against the kernel's OWN scale, not against the sample's magnitude.</b>
    /// G̃ is bounded by ~2/ε, so 1 is the right normalisation floor — and it has to be, because as
    /// k → 0 the kernel is a difference of two nearly-equal exponentials (in the closed-form ORACLE
    /// exactly as much as in the production path) and its value decays to zero while the absolute
    /// error stays at roundoff. A purely relative gate there measures the subtraction, not the
    /// formulation: at k·H = 1e-9 the oracle itself is only good to 2e-7 relative. What the spatial
    /// integral consumes is the absolute error, which is what this reports.
    /// </summary>
    private static double Dev(Complex expected, Complex actual) =>
        (expected - actual).Magnitude / Math.Max(expected.Magnitude, 1.0);

    /// <summary>A genuinely multilayer, genuinely lossy stack — nothing about it is degenerate.</summary>
    private static LayerStack Stratified() => new(
        Termination.Pec,
        [
            new MediumLayer(1.00e-3, new EmMaterial(4.4, 0.02)),
            new MediumLayer(0.50e-3, new EmMaterial(9.8, 0.001)),
            new MediumLayer(0.25e-3, new EmMaterial(2.2, 0.004)),
        ],
        Termination.Air);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 0 — structural
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_1_TheKernelIsSymmetricInTheTwoHeights()
    {
        var stack = Stratified();
        double[] hs = [0.2e-3, 1.0e-3, 1.2e-3, 1.5e-3, 1.75e-3, 2.5e-3];

        foreach (double k in new[] { 1.0, 137.0, 2_000.0, 40_000.0 })
            foreach (double a in hs)
                foreach (double b in hs)
                {
                    Complex ab = InteriorStaticGreens.SpectralScalar(stack, k, a, b);
                    Complex ba = InteriorStaticGreens.SpectralScalar(stack, k, b, a);
                    Assert.Equal(ab.Real, ba.Real, 15);
                    Assert.Equal(ab.Imaginary, ba.Imaginary, 15);
                }
    }

    /// <summary>
    /// G̃ is continuous in z and the FLUX <c>P·∂G̃/∂z</c> is continuous too — the two interface
    /// conditions the formulation is built on, checked on the OUTPUT rather than assumed from the
    /// derivation. A sign flipped in <c>InterfaceCoefficient</c> survives every reduction test in
    /// Tier 1 (where every coefficient is zero) and dies here.
    /// </summary>
    [Fact]
    public void T0_2_ThePotentialAndItsFluxAreContinuousAcrossEveryInterface()
    {
        var stack = Stratified();
        double zp = 1.9e-3;                                     // observer up in the half-space

        // The probe offset is a compromise and is sized rather than guessed: the residual below is
        // the GENUINE variation of G̃ over 2ε (≈ 2εk·G̃, i.e. 1e-6 at k = 5000), while the flux's
        // difference quotient loses |G̃|·2^-52/(2ε) to roundoff. 3e-11 m puts both near 1e-6.
        double eps = 3e-11;

        foreach (double k in new[] { 500.0, 5_000.0 })
            for (int i = 0; i <= stack.LayerCount; i++)
            {
                double zi = stack.InterfaceZ[i];
                if (zi <= 0) continue;                          // the PEC floor has no "below"

                Complex below = InteriorStaticGreens.SpectralScalar(stack, k, zi - eps, zp);
                Complex above = InteriorStaticGreens.SpectralScalar(stack, k, zi + eps, zp);
                Assert.True(Rel(below, above) < 1e-5,
                    $"G̃ jumped at interface {i} (k={k}): {below} vs {above}");

                // Flux, one-sided, so the difference quotient never straddles the interface.
                Complex dBelow = (InteriorStaticGreens.SpectralScalar(stack, k, zi - eps, zp)
                                - InteriorStaticGreens.SpectralScalar(stack, k, zi - 3 * eps, zp)) / (2 * eps);
                Complex dAbove = (InteriorStaticGreens.SpectralScalar(stack, k, zi + 3 * eps, zp)
                                - InteriorStaticGreens.SpectralScalar(stack, k, zi + eps, zp)) / (2 * eps);
                Complex fBelow = InteriorStaticGreens.Weight(stack, true, i)     * dBelow;
                Complex fAbove = InteriorStaticGreens.Weight(stack, true, i + 1) * dAbove;

                _out.WriteLine($"interface {i} k={k}: flux {fBelow:G6} vs {fAbove:G6}");
                Assert.True(Rel(fBelow, fAbove) < 1e-4,
                    $"P·∂G̃/∂z jumped at interface {i} (k={k}): {fBelow} vs {fAbove}");
            }
    }

    [Fact]
    public void T0_3_APointInsideASolidWallIsRefusedByName()
    {
        var stack = Stratified();

        var below = InteriorStaticGreens.CanEvaluateAt(stack, -1e-6);
        Assert.False(below.Ok);
        Assert.Contains("PEC", below.Reason);
        Assert.Contains("solid wall", below.Reason);

        // Interior heights are exactly what this class exists for and must NOT be refused.
        Assert.True(InteriorStaticGreens.CanEvaluateAt(stack, 0.5e-3).Ok);
        Assert.True(InteriorStaticGreens.CanEvaluateAt(stack, stack.TopZ).Ok);
        Assert.True(InteriorStaticGreens.CanEvaluateAt(stack, 10 * stack.TopZ).Ok);

        var walled = new LayerStack(Termination.Pec, [new MediumLayer(1e-3, new EmMaterial(4.4, 0.0))],
                                    Termination.Pec);
        var above = InteriorStaticGreens.CanEvaluateAt(walled, 2e-3);
        Assert.False(above.Ok);
        Assert.Contains("solid wall", above.Reason);

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => InteriorStaticGreens.SpectralScalar(stack, 100.0, -1e-6, 1e-3));
        Assert.Contains("solid wall", thrown.Message);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 1 — the exact reductions
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The brief's own first oracle, at ARBITRARY heights.</b> With one material everywhere over
    /// a PEC floor, every interface coefficient is zero and the answer is free space plus exactly
    /// one image at −z′ — for every pair of heights, interior ones included, not only for a source
    /// on a surface. This is the interior generalisation of the εᵣ = 1 collapse that validated L8a.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(4.4, 0.02)]
    [InlineData(12.9, 0.002)]
    public void T1_1_AHomogeneousStackOverAPecFloorIsFreeSpacePlusOneImage(double epsR, double tanD)
    {
        var mat   = new EmMaterial(epsR, tanD);
        var stack = new LayerStack(Termination.Pec,
                                   [new MediumLayer(0.6e-3, mat), new MediumLayer(0.4e-3, mat)],
                                   Termination.OpenTo(mat));
        Complex eps = mat.EpsComplex;

        double[] hs = [1e-6, 0.2e-3, 0.6e-3, 0.99e-3, 1.0e-3, 1.4e-3];
        double worst = 0;

        foreach (double k in new[] { 1e-3, 1.0, 137.0, 5_000.0, 250_000.0 })
            foreach (double z in hs)
                foreach (double zp in hs)
                {
                    Complex want = (Math.Exp(-k * Math.Abs(z - zp)) - Math.Exp(-k * (z + zp))) / eps;
                    Complex got  = InteriorStaticGreens.SpectralScalar(stack, k, z, zp);
                    worst = Math.Max(worst, Dev(want, got));
                }

        _out.WriteLine($"εᵣ={epsR} tanδ={tanD}: worst deviation {worst:E3}");
        Assert.True(worst < 1e-14, $"worst {worst:E3}");
    }

    /// <summary>
    /// The magnetostatic half of the same statement — and it is a different assertion, not a
    /// re-run: the vector problem's weight is 1/µ, so a stack that is uniform in µ but stratified in
    /// ε must give free space plus one image even though every ELECTROSTATIC coefficient is
    /// non-zero. A weight taken from the wrong material dies here and nowhere else.
    /// </summary>
    [Fact]
    public void T1_2_TheVectorKernelDoesNotSeeANonMagneticStratification()
    {
        var stack = Stratified();
        double worst = 0;

        foreach (double k in new[] { 1.0, 137.0, 5_000.0, 60_000.0 })
            foreach (double z in new[] { 0.3e-3, 1.0e-3, 1.6e-3, 1.75e-3, 2.2e-3 })
                foreach (double zp in new[] { 0.3e-3, 1.2e-3, 1.75e-3, 3.0e-3 })
                {
                    Complex want = Math.Exp(-k * Math.Abs(z - zp)) - Math.Exp(-k * (z + zp));
                    Complex got  = InteriorStaticGreens.SpectralVector(stack, k, z, zp);
                    worst = Math.Max(worst, Dev(want, got));
                }

        _out.WriteLine($"vector on a stratified non-magnetic stack: worst {worst:E3}");
        Assert.True(worst < 1e-14, $"worst {worst:E3}");
    }

    /// <summary>
    /// <b>L9a's Tier-2 pattern, at interior heights.</b> Splitting a layer into sub-layers of the
    /// SAME material changes no physics, so the kernel must not move — and unlike Tier 1 this
    /// exercises the inter-region scale factor, which is the one piece of the formulation that has
    /// no analogue in the shipped code. A ratio bookkeeping error that Tier 1's zero coefficients
    /// hide shows up here immediately.
    /// </summary>
    [Fact]
    public void T1_3_SplittingALayerIntoSubLayersOfItsOwnMaterialMovesNothing()
    {
        var stack = Stratified();
        var split = stack.WithLayerSplit(0, 0.17, 0.41, 0.42).WithLayerSplit(3, 0.5, 0.5);
        Assert.Equal(6, split.LayerCount);
        Assert.Equal(stack.TopZ, split.TopZ, 15);

        double worst = 0;
        foreach (bool scalar in new[] { true, false })
            foreach (double k in new[] { 1e-2, 1.0, 137.0, 4_000.0, 90_000.0 })
                foreach (double z in new[] { 0.05e-3, 0.4e-3, 1.0e-3, 1.3e-3, 1.75e-3, 2.4e-3 })
                    foreach (double zp in new[] { 0.05e-3, 0.9e-3, 1.51e-3, 1.75e-3, 2.4e-3 })
                    {
                        var a = InteriorStaticGreens.Spectral(InteriorStaticGreens.Build(stack, scalar, k), z, zp);
                        var b = InteriorStaticGreens.Spectral(InteriorStaticGreens.Build(split, scalar, k), z, zp);
                        double d = Dev(a, b);
                        if (d > worst)
                        {
                            worst = d;
                            _out.WriteLine($"  new worst {d:E3}: scalar={scalar} k={k} z={z} z'={zp} " +
                                           $"{a:G8} vs {b:G8}");
                        }
                    }

        _out.WriteLine($"split invariance: worst deviation {worst:E3}");
        Assert.True(worst < 1e-14, $"worst {worst:E3}");
    }

    /// <summary>
    /// <b>The k → 0 trap, named in the class remarks and measured here.</b> Over a PEC floor ψ↓
    /// vanishes at the top of a layer as k → 0, so the naive inter-region matching ratio is 0/0
    /// exactly at k = 0 and small/small beside it. The analytic cancellation is supposed to remove
    /// it; this drives a CROSS-REGION pair down to k·H = 1e-9 and demands full precision all the
    /// way, which the naive form cannot give.
    /// </summary>
    [Fact]
    public void T1_4_TheCrossRegionRatioSurvivesArbitrarilySmallK()
    {
        var mat   = new EmMaterial(4.4, 0.02);
        var stack = new LayerStack(Termination.Pec,
                                   [new MediumLayer(0.6e-3, mat), new MediumLayer(0.4e-3, mat)],
                                   Termination.OpenTo(mat));
        Complex eps = mat.EpsComplex;
        double z = 0.3e-3, zp = 0.8e-3;                          // regions 1 and 2 — cross-region

        double worst = 0;
        for (double kh = 1e-9; kh <= 1.0; kh *= 10)
        {
            double k = kh / stack.TopZ;
            // The oracle is written with expm1 so the SUBTRACTION is exact in it; what is left is
            // the production path's own, which is the thing being measured.
            Complex want = -Math.Exp(-k * (zp - z)) * ExpM1(-2.0 * k * z) / eps;
            Complex got  = InteriorStaticGreens.SpectralScalar(stack, k, z, zp);
            double rel = Rel(want, got, 1e-280), abs = (want - got).Magnitude;
            _out.WriteLine($"k·H = {kh:E1}: want {want:G8}, got {got:G8}, rel {rel:E3}, abs {abs:E3}");
            worst = Math.Max(worst, abs);
        }
        Assert.True(worst < 1e-15, $"worst absolute {worst:E3}");
    }

    /// <summary>e^x − 1 without the cancellation. .NET has no expm1.</summary>
    private static double ExpM1(double x) =>
        Math.Abs(x) < 1e-5 ? x * (1 + x / 2 * (1 + x / 3 * (1 + x / 4))) : Math.Exp(x) - 1;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 2 — against the shipped kernels
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With both points on the slab's own top surface this must be <c>1 + Γ_e</c> — the spectral
    /// form whose inverse transform IS <see cref="StaticGreens.ScalarPotential"/>'s image series.
    /// That series was validated independently at L8a, so this is the rung that ties the general
    /// formulation to the shipped one.
    /// </summary>
    [Theory]
    [InlineData(4.4, 0.02, 1.6e-3)]
    [InlineData(12.9, 0.002, 100e-6)]
    public void T2_1_OnTheSlabTopItIsOnePlusGammaE(double epsR, double tanD, double h)
    {
        var slab  = new GroundedSlab(h, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        Complex kIm = (1.0 - slab.EpsComplex) / (1.0 + slab.EpsComplex);

        double worst = 0;
        foreach (double kh in new[] { 1e-4, 0.05, 0.7, 3.0, 25.0, 400.0 })
        {
            double k = kh / h;
            Complex x = Math.Exp(-2.0 * k * h);
            Complex want = 1.0 + (kIm - x) / (1.0 - kIm * x);     // 1 + Γ_e, written out
            Complex got  = InteriorStaticGreens.SpectralScalar(stack, k, h, h);
            worst = Math.Max(worst, Dev(want, got));
        }
        _out.WriteLine($"1 + Γ_e reduction: worst {worst:E3}");
        Assert.True(worst < 1e-14, $"worst {worst:E3}");
    }

    /// <summary>
    /// Everywhere <see cref="LayeredStaticGreens"/> is defined — both points in the top half-space —
    /// the two must agree exactly. That class's own spectral form is <c>e^{−k|z−z′|} + Γ e^{−kσ}</c>
    /// with Γ from its cascade; this compares the whole kernel, cascade included.
    /// </summary>
    [Fact]
    public void T2_2_ItAgreesWithLayeredStaticGreensSpectralFormInTheTopHalfSpace()
    {
        var stack = Stratified();
        double H = stack.TopZ;
        double worst = 0;

        foreach (bool scalar in new[] { true, false })
            foreach (double k in new[] { 1.0, 137.0, 3_000.0, 50_000.0 })
                foreach (double z in new[] { H, H + 0.1e-3, H + 1.0e-3 })
                    foreach (double zp in new[] { H, H + 0.4e-3 })
                    {
                        Complex g = LayeredStaticGreens.Reflection(stack, scalar, k);
                        Complex want = Math.Exp(-k * Math.Abs(z - zp))
                                     + g * Math.Exp(-k * (z + zp - 2 * H));
                        Complex got = InteriorStaticGreens.Spectral(
                            InteriorStaticGreens.Build(stack, scalar, k), z, zp);
                        worst = Math.Max(worst, Dev(want, got));
                    }

        _out.WriteLine($"vs LayeredStaticGreens' spectral form: worst {worst:E3}");
        Assert.True(worst < 1e-14, $"worst {worst:E3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 3 — the independent solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The rung that actually validates the kernel on a stratified stack at interior heights.</b>
    /// A finite-volume discretisation of
    /// <c>d/dz(P dG̃/dz) − k²P G̃ = −2k δ(z − z′)</c> on a uniform grid with every interface ON a
    /// node, solved by a complex tridiagonal elimination. It shares nothing with the production
    /// path: no reflection coefficient, no cascade, no exponential basis, no Wronskian.
    ///
    /// <para>Gated on Richardson behaviour as well as on the error, because a second-order scheme
    /// agreeing to 1e-4 proves nothing on its own — halving h must QUARTER the error, which is what
    /// says the FD is converging onto the kernel rather than the two sharing a mistake.</para>
    /// </summary>
    [Fact]
    public void T3_1_AnIndependentFiniteVolumeSolveReproducesTheKernelAtInteriorHeights()
    {
        var stack = Stratified();
        (double Z, double Zp)[] pairs =
        [
            (0.40e-3, 0.40e-3),          // same layer, coincident
            (0.40e-3, 0.85e-3),          // same layer, apart
            (1.00e-3, 1.00e-3),          // exactly on an interface (the metal-level case)
            (0.30e-3, 1.30e-3),          // two layers apart
            (0.75e-3, 1.75e-3),          // interior to the top surface
            (1.60e-3, 1.65e-3),          // inside the thin top layer
        ];

        foreach (double k in new[] { 800.0, 4_000.0 })
            foreach (var (z, zp) in pairs)
            {
                Complex exact = InteriorStaticGreens.SpectralScalar(stack, k, z, zp);
                Complex c1 = FiniteVolume(stack, k, z, zp, 350);
                Complex c2 = FiniteVolume(stack, k, z, zp, 700);

                double e1 = Rel(exact, c1), e2 = Rel(exact, c2);
                double ratio = e2 > 0 ? e1 / e2 : double.PositiveInfinity;
                _out.WriteLine($"k={k,-7} z={z * 1e3:F2}mm z'={zp * 1e3:F2}mm  " +
                               $"exact {exact:G8}  err {e1:E2} -> {e2:E2}  ratio {ratio:F2}");

                Assert.True(e2 < 3e-4, $"finer FD error {e2:E3} at k={k}, z={z}, z'={zp}");
                Assert.True(ratio > 3.2, $"Richardson ratio {ratio:F2} — the FD is not converging " +
                                         $"quadratically onto the kernel at k={k}, z={z}, z'={zp}");
            }
    }

    /// <summary>
    /// Solves the Sturm-Liouville problem on <c>[0, TopZ]</c> with the exact outgoing condition
    /// <c>P dG̃/dz = −k P_top G̃</c> at the top face (legitimate because nothing sources the half-space
    /// above) and <c>G̃ = 0</c> on the PEC floor. <paramref name="cells"/> is the number of uniform
    /// cells; every interface and both heights must land on a node, which
    /// <see cref="Stratified"/>'s thicknesses and the chosen pairs arrange.
    /// </summary>
    private static Complex FiniteVolume(LayerStack stack, double k, double z, double zp, int cells)
    {
        double top = stack.TopZ, h = top / cells;
        int n = cells + 1;                                        // nodes 0..cells

        int Node(double zz)
        {
            double x = zz / h;
            int i = (int)Math.Round(x);
            if (Math.Abs(x - i) > 1e-9)
                throw new InvalidOperationException($"z = {zz} is not on the FD grid (h = {h}).");
            return i;
        }

        // P on the face between nodes j and j+1, and the cell-average P at node j.
        Complex FaceP(int j) => InteriorStaticGreens.Weight(stack, true, stack.RegionOf((j + 0.5) * h));
        Complex NodeP(int j)
        {
            if (j == 0)     return FaceP(0);
            if (j == cells) return FaceP(cells - 1);
            return 0.5 * (FaceP(j - 1) + FaceP(j));
        }

        var lower = new Complex[n];
        var diag  = new Complex[n];
        var upper = new Complex[n];
        var rhs   = new Complex[n];

        for (int j = 1; j < cells; j++)
        {
            Complex pm = FaceP(j - 1), pp = FaceP(j);
            lower[j] = pm / h;
            upper[j] = pp / h;
            diag[j]  = -(pm + pp) / h - k * k * h * NodeP(j);
        }

        // PEC floor: G̃(0) = 0.
        diag[0] = Complex.One; upper[0] = Complex.Zero; rhs[0] = Complex.Zero;

        // Top face, half a cell: the outgoing flux −k·P_top·G̃ replaces the missing neighbour.
        Complex pTop = InteriorStaticGreens.Weight(stack, true, stack.RegionCount - 1);
        lower[cells] = FaceP(cells - 1) / h;
        diag[cells]  = -FaceP(cells - 1) / h - k * pTop - k * k * (h / 2) * NodeP(cells);

        int src = Node(zp);
        rhs[src] += -2.0 * k;

        // Thomas elimination, complex.
        for (int j = 1; j < n; j++)
        {
            Complex m = lower[j] / diag[j - 1];
            diag[j] -= m * upper[j - 1];
            rhs[j]  -= m * rhs[j - 1];
        }
        var x = new Complex[n];
        x[n - 1] = rhs[n - 1] / diag[n - 1];
        for (int j = n - 2; j >= 0; j--) x[j] = (rhs[j] - upper[j] * x[j + 1]) / diag[j];

        return x[Node(z)];
    }
}
