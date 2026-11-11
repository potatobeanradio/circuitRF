using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>PSV-3 — a planar via that crosses a dielectric interface.</b>
///
/// <para>The basis is one uniform vertical current over the whole via; only the z-integral is split
/// at the interfaces it crosses. Three claims, one test each:</para>
/// <list type="bullet">
///   <item><b>The interface charge is already in the kernels.</b> The integrated MPIE identity holds
///         for a uniform current segment across an interface with the scalar potential taken from
///         the segment's two END charges alone, and the term a jump in 1/ε would add is shown to be
///         large enough that the identity would fail if it were needed.</item>
///   <item><b>The transmitted direct term's coefficient</b> is what the spectral kernel says at
///         k_ρ → ∞, including on a magnetic stack where it is not µ.</item>
///   <item><b>Gate 2 — the split agrees with itself.</b> One via in one medium, split at a fake
///         interface with the same material on both sides, gives the unsplit inductance.</item>
/// </list>
/// </summary>
public sealed class PsvViaThroughInterfaceTests
{
    private readonly ITestOutputHelper _out;
    public PsvViaThroughInterfaceTests(ITestOutputHelper output) => _out = output;

    private static double Rel(Complex expected, Complex actual)
        => (expected - actual).Magnitude / expected.Magnitude;

    /// <summary>10 → 3 εᵣ contrast over a PEC, the shape gate 3 runs in 3D.</summary>
    private static LayerStack TwoDielectrics => new(
        Termination.Pec,
        [new MediumLayer(0.25e-3, new EmMaterial(10.0, 0.002)),
         new MediumLayer(0.25e-3, new EmMaterial(3.0, 0.002))],
        Termination.Air);

    [Fact]
    public void TheInterfaceChargeIsCarried_TheIntegratedIdentityHoldsFromTheEndChargesAlone()
    {
        // A uniform J_z = 1 on [lo, hi], crossing interfaces. The TRUE field at an observer outside
        // the segment is the superposition of point vertical dipoles, each from the transmission-line
        // relation E_z = −k_ρ² I_v^e /(ω² ε_m ε_n) — no MPIE kernel in it. The MPIE side is
        // −jωµ₀∫G_A^zz dz′ minus ∂_z of the END charges' potential, [G_q(z,hi) − G_q(z,lo)]/(jωε₀),
        // with NO term at the interface. Both z′-integrals are taken per region.
        double worst = 0, smallestJumpTerm = double.PositiveInfinity;
        foreach (var (name, stack, lo, hi, observers) in new[]
        {
            ("10|3 over PEC", TwoDielectrics, 0.10e-3, 0.40e-3, new[] { 0.05e-3, 0.45e-3, 0.80e-3 }),
            ("PCB 3-layer",   LayerStacks.Pcb3Layer, 0.40e-3, 1.35e-3, new[] { 0.20e-3, 1.38e-3, 2.0e-3 }),
        })
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double eps0 = EmConstants.Eps0, mu0 = EmConstants.Mu0;
            double kMax = g.K0 * Math.Sqrt(stack.Layers.Select(l => l.Material.EpsR).Append(1.0).Max());
            var sub = ViaZIntegral.SubSpans(new ViaZIntegral.Span(lo, hi), stack);
            Assert.True(sub.Length >= 2, "the fixture must actually cross an interface");

            foreach (double z in observers)
            foreach (double u in new[] { 0.3, 0.9, 2.0, 8.0 })
            {
                Complex kRho = u * g.K0, w = kRho * kRho;
                Complex epsN = eps0 * stack.MaterialOfRegion(stack.RegionOf(z)).EpsComplex;

                Complex trueEz = Complex.Zero, aZz = Complex.Zero;
                foreach (var s in sub)
                {
                    Complex epsM = eps0 * stack.MaterialOfRegion(stack.RegionOf(s.Mid)).EpsComplex;
                    var (zs, ws) = ViaZIntegral.Nodes(s, 24);
                    for (int k = 0; k < zs.Length; k++)
                    {
                        trueEz += ws[k] * -w * g.SeriesCurrent(SurfaceWavePolarization.Tm, w, z, zs[k])
                                  / (g.Omega * g.Omega * epsM * epsN);
                        aZz += ws[k] * g.VerticalKernel(w, z, zs[k]);
                    }
                }

                double gap = double.PositiveInfinity;
                foreach (double zi in stack.InterfaceZ.Append(lo).Append(hi)) gap = Math.Min(gap, Math.Abs(z - zi));
                double step = Math.Min(0.15 * gap, 0.03 / Math.Max(u * g.K0, kMax));
                Complex Dz(double zp, double h) =>
                    (g.KernelAtHeights(GreensKernel.ScalarPotential, kRho, z + h, zp)
                   - g.KernelAtHeights(GreensKernel.ScalarPotential, kRho, z - h, zp)) / (2 * h);
                Complex Rich(double zp) => (4.0 * Dz(zp, step / 2) - Dz(zp, step)) / 3.0;

                Complex mpie = -Complex.ImaginaryOne * g.Omega * mu0 * aZz
                             - (Rich(hi) - Rich(lo)) / (Complex.ImaginaryOne * g.Omega * eps0);
                double r = Rel(trueEz, mpie);
                worst = Math.Max(worst, r);

                // What an interface charge ∝ the jump in 1/ε would add, were the kernels NOT already
                // carrying it: (ε₀/ε_below − ε₀/ε_above)·∂_z G_q(z, z_i) per crossed interface.
                Complex jump = Complex.Zero;
                for (int i = 0; i + 1 < sub.Length; i++)
                {
                    double zi = sub[i].Hi;
                    Complex eb = stack.MaterialOfRegion(stack.RegionOf(sub[i].Mid)).EpsComplex;
                    Complex ea = stack.MaterialOfRegion(stack.RegionOf(sub[i + 1].Mid)).EpsComplex;
                    jump += (1.0 / eb - 1.0 / ea) * Rich(zi) / (Complex.ImaginaryOne * g.Omega * eps0);
                }
                smallestJumpTerm = Math.Min(smallestJumpTerm, jump.Magnitude / trueEz.Magnitude);

                Assert.True(r < 1e-6,
                    $"{name} z={z} u={u}: the end-charge MPIE misses the true field by {r:E2}");
            }
        }

        _out.WriteLine($"Integrated MPIE identity across an interface, end charges only: worst {worst:E2}. " +
                       $"A 1/ε-jump interface term would have moved it by at least {smallestJumpTerm:E2} " +
                       "of the field, so it is not there because it is not needed.");
        Assert.True(smallestJumpTerm > 1e3 * worst, "the check must be able to see an interface term");
    }

    [Fact]
    public void TheTransmittedDirectCoefficient_IsTheKernelsOwnLimit_OnAMagneticStackToo()
    {
        foreach (var (eA, mA, eB, mB) in new[] { (10.0, 1.0, 3.0, 1.0), (4.0, 2.0, 9.0, 1.0) })
        {
            var stack = new LayerStack(Termination.Pec,
                [new MediumLayer(1e-3, new EmMaterial(eA, 0, mA)),
                 new MediumLayer(1e-3, new EmMaterial(eB, 0, mB))], Termination.Air);
            var g = new LayeredSpectralGreens(stack, 10e9);
            double zi = 1e-3, d = 1e-9;
            Complex kRho = 1e5 * g.K0;

            foreach (var (z, zp) in new[] { (zi - d, zi + 2 * d), (zi + 2 * d, zi - d) })
            {
                double delta = Math.Abs(z - zp);
                Complex kz = -Complex.ImaginaryOne * kRho;      // both regions, to 1e-10 at this k_ρ
                Complex measured = g.VerticalKernel(kRho * kRho, z, zp) * 2.0 * Complex.ImaginaryOne * kz
                                 * Complex.Exp(kRho * delta);
                Complex c = g.TransmittedDirectCoefficient(GreensKernel.VerticalVectorPotential, z, zp);
                _out.WriteLine($"εᵣ {eA}/{eB}, µᵣ {mA}/{mB}: kernel limit {measured:G6}, formula {c:G6}");
                Assert.True(Rel(measured, c) < 1e-6, $"coefficient {c} against the kernel's {measured}");
            }
            if (mA == mB) Assert.Equal(1.0, g.TransmittedDirectCoefficient(
                GreensKernel.VerticalVectorPotential, zi - d, zi + d).Real, 12);
        }
    }

    // ── Gate 2 — a via split at an interface with the SAME material on both sides ────────────

    /// <summary>One via over one w×w footprint from z0 to z0+ℓ, and nothing else.</summary>
    private static (PlanarMesh, PlanarLevels) Via(double w, double z0, double ell) =>
        (new PlanarMesh([new PlanarCell(0, 0, 0, 0, 0, w, w), new PlanarCell(1, 1, 0, 0, 0, w, w)],
                        [new PlanarBasis(0, 0, 1, PlanarBasisDirection.Z)],
                        ["L0", "L1"], [0, w], [0, w]),
         new PlanarLevels([z0, z0 + ell]));

    /// <summary>The via's series inductance, with the charge term cancelled algebraically —
    /// <c>ViaPhysicsTests.SeriesInductance</c>'s construction.</summary>
    private static double SeriesInductance(PlanarMesh mesh, PlanarLevels levels, LayerStack stack, double fHz,
                                           PlanarFillSettings? fill = null)
    {
        var cores = PlanarFill.BuildCores(mesh, fill);
        var set = new PlanarKernelSet(new LayeredSpectralGreens(stack, fHz),
                                      PlanarFillSettings.Default.Order, 0.0).For(cores);
        double w1 = 2 * Math.PI * fHz, w2 = 10.0 * w1;
        Complex z1 = PlanarFill.FillMultiLevel(cores, set, levels, w1)[0, 0];
        Complex z2 = PlanarFill.FillMultiLevel(cores, set, levels, w2)[0, 0];
        return ((w2 * z2 - w1 * z1) / (Complex.ImaginaryOne * (w2 * w2 - w1 * w1))).Real;
    }

    [Fact]
    public void Gate2_ASplitAtAFakeInterface_GivesTheUnsplitInductance()
    {
        double w = 40e-6, z0 = 200e-6, f = 5e9;
        double worst = 0;
        var fill = PlanarFillSettings.Default;
        foreach (double ell in new[] { 40e-6, 400e-6 })
        {
            var (mesh, levels) = Via(w, z0, ell);
            double top = z0 + ell;
            double whole = SeriesInductance(mesh, levels,
                new LayerStack(Termination.Pec, [new MediumLayer(top, EmMaterial.Air)], Termination.Air), f, fill);

            foreach (var cuts in new[] { new[] { 0.5 }, new[] { 0.2 }, new[] { 0.3, 0.7 } })
            {
                var layers = new List<MediumLayer>();
                double at = 0;
                foreach (double c in cuts.Select(c => z0 + c * ell).Append(top))
                {
                    layers.Add(new MediumLayer(c - at, EmMaterial.Air));
                    at = c;
                }
                double split = SeriesInductance(mesh, levels,
                    new LayerStack(Termination.Pec, layers, Termination.Air), f, fill);
                double err = split / whole - 1;
                _out.WriteLine($"ℓ/w = {ell / w:F0}, cut at {string.Join("/", cuts)}: " +
                               $"{split * 1e12:F4} pH against {whole * 1e12:F4} pH ({err:P3})");
                worst = Math.Max(worst, Math.Abs(err));
            }
        }
        Assert.True(worst < 5e-3, $"a fake interface moved the via's inductance by up to {worst:P3}");
    }
}
