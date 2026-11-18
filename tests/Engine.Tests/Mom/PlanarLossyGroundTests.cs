using NumFlat;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>CL4 — the laterally infinite ground plane, as a Leontovich SURFACE IMPEDANCE.</b>
/// <c>docs/sonnet-briefs/brief-conductor-loss-4-lossy-ground.md</c>; findings in
/// <c>RESOLVED.md</c> §CL4.
///
/// <para>The brief's four ordered measurements are here in its own order, and each is a TABLE
/// first and an assertion second — the brief was allowed to refuse, so what these tests exist to
/// produce is the number, not a pass. Nothing here is tuned: not <c>EdgeCells</c>, not the
/// validated ranges, not <c>PoleLossCeiling</c>.</para>
///
/// <list type="number">
/// <item><see cref="M1_DcimFit_AgainstDirectSommerfeld"/> — can DCIM still fit a Γ that is now a
/// function of k_ρ? (Yes; a surface impedance adds no branch point, because it replaces the region
/// below by a CONSTANT rather than by a medium with its own k_zb.)</item>
/// <item><see cref="M2_SurfaceWavePoles_LossRatio"/> — where did the surface-wave poles go, against
/// <c>PlanarSurfaceWaveLaunch.PoleLossCeiling</c>?</item>
/// <item><see cref="M4_GroundAttachmentBasis_OnAConductingFloor"/> — is the ground-attachment basis
/// still legitimate? Its refusal is RE-STATED in terms of the new termination, never deleted.</item>
/// <item>What happens below the plane — <c>FrontToBackDb</c> stays refused with its REASON
/// corrected, which is a decision recorded in §CL4 §6 rather than a test.</item>
/// </list>
///
/// <para><b>The one trap that ate the first measurement</b>, kept here because it is invisible:
/// <c>PlanarFillSettings.PerfectConductor</c> makes the STRIP perfect and nothing else, because the
/// plane is in the Green's function and not in the fill. Subtracting a PEC-metal floor taken on the
/// LOSSY-ground stack therefore removes the very term being looked for and reports a ground share
/// of −0.03% with every assertion still green. <see cref="AlphaB"/> takes its floor from the
/// PEC-ground kernel for that reason.</para>
/// </summary>
public sealed class PlanarLossyGroundTests(ITestOutputHelper output)
{
    private const double CopperSigma = 5.8e7, GoldSigma = 4.1e7;

    private static LayerStack WithFloor(LayerStack s, Termination bottom) =>
        new(bottom, s.Layers, s.Top);

    private static IEnumerable<(string Name, LayerStack Pec, double Sigma, double T)> Starters()
    {
        yield return ("FR-4 1.6 mm, 35 µm Cu", LayerStacks.Fr4Slab,  CopperSigma, 35e-6);
        yield return ("GaAs 100 µm, 3 µm Au",  LayerStacks.GaAsSlab, GoldSigma,   3e-6);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl4-1 — the PEC reduction
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void M0_PecReduction_BitIdentical()
    {
        foreach (var (name, pec, _, t) in Starters())
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            foreach (var (how, floor) in new[]
            {
                ("σ = +∞",  Termination.LossyGround(double.PositiveInfinity, t)),
                ("t = 0",   Termination.LossyGround(CopperSigma, 0.0)),
                ("σ = 0",   Termination.LossyGround(0.0, t)),
            })
            {
                var a = new LayeredSpectralGreens(pec, f);
                var b = new LayeredSpectralGreens(WithFloor(pec, floor), f);
                int moved = 0, total = 0;
                foreach (var kind in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
                foreach (double rl in new[] { 1e-3, 1e-2, 0.1, 0.5, 1.0 })
                {
                    double rho = rl * EmConstants.C0 / f, h = pec.TopZ;
                    var x = SommerfeldIntegral.EvaluateLayered(a, kind, rho, h, h).Value;
                    var y = SommerfeldIntegral.EvaluateLayered(b, kind, rho, h, h).Value;
                    total += 2;
                    if (BitConverter.DoubleToInt64Bits(x.Real) != BitConverter.DoubleToInt64Bits(y.Real)) moved++;
                    if (BitConverter.DoubleToInt64Bits(x.Imaginary) != BitConverter.DoubleToInt64Bits(y.Imaginary)) moved++;
                }
                output.WriteLine($"{name}, {f/1e9:F0} GHz, {how}: {moved}/{total} bits moved");
                Assert.Equal(0, moved);
            }
        }
    }

    [Fact]
    public void M0b_StaticRouteIsPecAtEverySigma()
    {
        foreach (var (name, pec, sigma, t) in Starters())
        {
            var lossy = WithFloor(pec, Termination.LossyGround(sigma, t));
            int moved = 0, total = 0;
            foreach (bool scalar in new[] { true, false })
            foreach (double rl in new[] { 1e-3, 1e-2, 0.1, 1.0, 10.0 })
            {
                double rho = rl * pec.TopZ, h = pec.TopZ;
                var x = scalar ? LayeredStaticGreens.ScalarPotential(pec, rho, h, h)
                               : LayeredStaticGreens.VectorPotential(pec, rho, h, h);
                var y = scalar ? LayeredStaticGreens.ScalarPotential(lossy, rho, h, h)
                               : LayeredStaticGreens.VectorPotential(lossy, rho, h, h);
                total += 2;
                if (BitConverter.DoubleToInt64Bits(x.Real) != BitConverter.DoubleToInt64Bits(y.Real)) moved++;
                if (BitConverter.DoubleToInt64Bits(x.Imaginary) != BitConverter.DoubleToInt64Bits(y.Imaginary)) moved++;
            }
            output.WriteLine($"{name}: static route, REAL σ — {moved}/{total} bits moved");
            Assert.Equal(0, moved);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // MEASUREMENT 1 — can DCIM still fit it?
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]          // measured: 5.0 s — two stacks x three frequencies,
    public void M1_DcimFit_AgainstDirectSommerfeld()   // two fits and 17 direct Sommerfeld points each
    {
        foreach (var (name, pec, sigma, t) in Starters())
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            foreach (var (tag, stack) in new[]
            {
                ("PEC  ", pec),
                ("LOSSY", WithFloor(pec, Termination.LossyGround(sigma, t))),
            })
            {
                var g = new LayeredSpectralGreens(stack, f);
                double lam = EmConstants.C0 / f, h = stack.TopZ;
                output.WriteLine($"=== {name}, {f/1e9:F0} GHz, {tag} — CanFit {(Dcim.CanFit(stack).Ok ? "yes":"NO")} ===");
                var models = new Dictionary<GreensKernel, DcimModel>
                {
                    [GreensKernel.ScalarPotential] = Dcim.Fit(g, GreensKernel.ScalarPotential),
                    [GreensKernel.VectorPotential] = Dcim.Fit(g, GreensKernel.VectorPotential),
                };
                foreach (var (kind, m) in models)
                    output.WriteLine($"   {kind,-16} {m.Images.Count} images, {m.SurfaceWaves.Count} SW, " +
                                     $"fit residual {m.FitResidual:E2}, sum-rule {m.SumRuleResidual.Magnitude:E2}");

                output.WriteLine("     rho/lam     scaled G_q     scaled G_A       rel G_q       rel G_A");
                var worstIn = new Dictionary<GreensKernel, (double E, double At)>
                    { [GreensKernel.ScalarPotential] = (0,0), [GreensKernel.VectorPotential] = (0,0) };
                for (double rl = 1e-4; rl <= 1.0001; rl *= Math.Pow(10, 0.25))
                {
                    double rho = rl * lam, fs = 1.0 / (4 * Math.PI * rho);
                    var cells = new List<string>(); var rels = new List<string>();
                    foreach (var kind in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
                    {
                        Complex exact = SommerfeldIntegral.EvaluateLayered(g, kind, rho, h, h).Value;
                        Complex got = models[kind].Evaluate(rho);
                        double abs = (got - exact).Magnitude;
                        double scaled = abs / fs, rel = abs / exact.Magnitude;
                        cells.Add($"{scaled,13:E3}"); rels.Add($"{rel,13:E3}");
                        if (Dcim.WithinValidatedRangeLayered(kind, g, rl).Ok && scaled > worstIn[kind].E)
                            worstIn[kind] = (scaled, rl);
                    }
                    output.WriteLine($"   {rl,9:E2} {string.Join("", cells)} {string.Join("", rels)}");
                }
                foreach (var k in worstIn.Keys)
                    output.WriteLine($"   WORST IN RANGE {k,-16} {worstIn[k].E:E3} at rho/lam {worstIn[k].At:E2} " +
                                     $"(ceiling 1.6e-2)");
                output.WriteLine("");
            }
        }
    }

    /// <summary>
    /// <b>Measurement 1's routine-tier counterpart</b> — one stack, one frequency, the worst
    /// in-range error against the same ceiling. The full six-case sweep is the tagged one above; this
    /// is what fails immediately if a conducting floor ever stops being fittable, which is the part
    /// worth paying for on every run.
    /// </summary>
    [Fact]
    public void M1b_TheLossyFloorIsStillFittable_InsideTheValidatedRange()
    {
        const double f = 10e9;
        var stack = WithFloor(LayerStacks.Fr4Slab, Termination.LossyGround(CopperSigma, 35e-6));
        Assert.True(Dcim.CanFit(stack).Ok);

        var g = new LayeredSpectralGreens(stack, f);
        double lam = EmConstants.C0 / f, h = stack.TopZ;
        foreach (var kind in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
        {
            var m = Dcim.Fit(g, kind);
            double worst = 0, at = 0;
            for (double rl = 1e-4; rl <= 1.0001; rl *= Math.Pow(10, 0.5))
            {
                if (!Dcim.WithinValidatedRangeLayered(kind, g, rl).Ok) continue;
                double rho = rl * lam;
                double scaled = (m.Evaluate(rho) - SommerfeldIntegral.EvaluateLayered(g, kind, rho, h, h).Value)
                                .Magnitude * 4 * Math.PI * rho;
                if (scaled > worst) { worst = scaled; at = rl; }
            }
            output.WriteLine($"{kind,-16} worst in-range scaled error {worst:E3} at rho/lam {at:E2} " +
                             $"(ceiling 1.6e-2), sum-rule {m.SumRuleResidual.Magnitude:E2}");
            // The ceiling is Dcim's own, NOT widened for this brief.
            Assert.True(worst <= 1.6e-2, $"{kind} fit is outside the general stack's validated range");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // MEASUREMENT 2 — where did the surface-wave poles go?
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_SurfaceWavePoles_LossRatio()
    {
        output.WriteLine($"PoleLossCeiling = {PlanarSurfaceWaveLaunch.PoleLossCeiling:G3}");
        foreach (var (name, pec, sigma, t) in Starters())
        {
            output.WriteLine($"=== {name} ===");
            output.WriteLine("      f GHz   mode      PEC |Im|/Re      LOSSY |Im|/Re      delta");
            foreach (double f in new[] { 1e9, 2e9, 5e9, 10e9, 20e9, 40e9 })
            {
                var rp = SurfaceWavePoles.Find(pec, f);
                var rl = SurfaceWavePoles.Find(WithFloor(pec, Termination.LossyGround(sigma, t)), f);
                for (int i = 0; i < Math.Max(rp.Modes.Count, rl.Modes.Count); i++)
                {
                    double a = i < rp.Modes.Count ? Math.Abs(rp.Modes[i].KRho.Imaginary)/Math.Abs(rp.Modes[i].KRho.Real) : double.NaN;
                    double b = i < rl.Modes.Count ? Math.Abs(rl.Modes[i].KRho.Imaginary)/Math.Abs(rl.Modes[i].KRho.Real) : double.NaN;
                    string nm = i < rl.Modes.Count ? rl.Modes[i].Name : rp.Modes[i].Name;
                    output.WriteLine($"   {f/1e9,8:F0}   {nm,-6}  {a,14:E4}  {b,16:E4}  {b-a,12:E3}" +
                                     (b > PlanarSurfaceWaveLaunch.PoleLossCeiling ? "   *** PAST CEILING" : ""));
                }
            }
            output.WriteLine("");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // The scalar itself
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void M_PlaneImpedance_Limits()
    {
        foreach (var (name, sigma, t) in new[]
        {
            ("copper 35 µm", CopperSigma, 35e-6),
            ("gold 3 µm",    GoldSigma,   3e-6),
        })
        {
            output.WriteLine($"=== {name} ===  1/(sigma t) = {1.0/(sigma*t):E4}");
            output.WriteLine("        f        t/delta     Re Z_plane    Re Z_plane/(1/sigma d)   Z_plane/Z_sheet");
            foreach (double f in new[] { 1e3, 1e6, 1e8, 1e9, 1e10, 4e10 })
            {
                double w = 2 * Math.PI * f;
                double d = PlanarSurfaceImpedance.SkinDepthM(sigma, w);
                var zp = PlanarSurfaceImpedance.Plane(sigma, t, w);
                var zs = PlanarSurfaceImpedance.Sheet(sigma, t, w);
                output.WriteLine($"  {f,9:E1}  {t/d,10:F4}  {zp.Real,12:E4}  {zp.Real*sigma*d,20:F6}  {(zp/zs).Real,14:F6}");
            }
            output.WriteLine("");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl4-2 — the epsR = 1 image reduction, at sigma = infinity and at a real sigma
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void M_R_cl4_2_EpsR1ImageReduction()
    {
        var pec = LayerStacks.AirOverGround();
        double h = pec.TopZ;
        const double f = 10e9;

        foreach (var (tag, floor) in new[]
        {
            ("sigma = +inf", Termination.LossyGround(double.PositiveInfinity, 35e-6)),
            ("copper 35 um", Termination.LossyGround(CopperSigma, 35e-6)),
        })
        {
            var g = new LayeredSpectralGreens(WithFloor(pec, floor), f);
            double lambda = 2 * Math.PI / g.K0;
            double worstRel = 0, worstScaled = 0;
            foreach (double over in new[] { 1e-3, 1e-2, 0.1, 0.5, 1.0, 3.0 })
            foreach (var (z, zp) in new[] { (h, h), (h, h + 0.3 * lambda), (h + 0.1*lambda, h + 0.6*lambda) })
            foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
            {
                double rho = over * lambda;
                var got = SommerfeldIntegral.EvaluateLayered(g, kind, rho, z, zp);
                Complex expected = SommerfeldIntegral.FreeSpace(g.K0, Math.Sqrt(rho*rho + (z-zp)*(z-zp)))
                                 - SommerfeldIntegral.FreeSpace(g.K0, Math.Sqrt(rho*rho + (z+zp)*(z+zp)));
                double rel = (expected - got.Value).Magnitude / expected.Magnitude;
                double scaled = (expected - got.Value).Magnitude * 4 * Math.PI * rho;
                worstRel = Math.Max(worstRel, rel);
                worstScaled = Math.Max(worstScaled, scaled);
            }
            output.WriteLine($"{tag}: worst relative {worstRel:E3}, worst scaled {worstScaled:E3} " +
                             "(free space + one NEGATIVE image)");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl4-3 — against kernel A, both surfaces lossy
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [Trait("Category", "Benchmark")]          // measured: 57 s (FR-4) + 1 m 57 s (GaAs) — eight
    [InlineData("fr4")]                       // de-embedding solves and four general-kernel fits each
    [InlineData("gaas")]
    public void M3_GroundShare_AgainstKernelA(string starter)
    {
        bool fr4 = starter == "fr4";
        var slab = fr4 ? GroundedSlab.Fr4Starter : GroundedSlab.GaAsStarter;
        double w  = fr4 ? 3020.28e-6 : 70.72e-6;
        double t  = fr4 ? 35e-6 : 3e-6;
        double sg = fr4 ? CopperSigma : GoldSigma;
        const double fHz = 10e9;

        var (rStrip, z0, eeff) = KernelA(w, slab, t, sg, fHz, double.PositiveInfinity);
        var (rBoth,  _,  _)    = KernelA(w, slab, t, sg, fHz, sg);
        double aStripA = rStrip / (2 * z0), aBothA = rBoth / (2 * z0);
        double aGroundA = aBothA - aStripA;
        output.WriteLine($"{starter}: w = {w*1e6:F2} um, t = {t*1e6:F1} um, sigma = {sg:E3} S/m, f = {fHz/1e9:F0} GHz");
        output.WriteLine($"  kernel A: R_strip = {rStrip:F3}, R_both = {rBoth:F3} Ohm/m, Z0 = {z0:F2}, eeff = {eeff:F4}");
        output.WriteLine($"  kernel A: a_strip {aStripA:E4}  a_ground {aGroundA:E4}  a_total {aBothA:E4} Np/m" +
                         $"   SHARE {aGroundA/aBothA:P2}   (overview {(fr4 ? "21.1%" : "~11%")})");
        output.WriteLine("");
        output.WriteLine("  kernel B. The substrate carries its real tanD and cancels in the differences.");
        output.WriteLine("  (Until CL5 it HAD to: the fit borrowed the direct integrator's lossless-guided");
        output.WriteLine("  refusal through Dcim.FitAtHeights. That is now the integrator's alone and a");
        output.WriteLine("  tanD = 0 stack fits; this measurement keeps the loss anyway, so the numbers here");
        output.WriteLine("  stay the ones CL4 reported.) A1 is the PEC-metal PEC-ground extraction floor;");
        output.WriteLine("  every term below is measured FROM IT, so the ground term is B1 - A1 with the");
        output.WriteLine("  strip perfect on both sides.");

        foreach (double gs in new[] { sg, sg / 4.0 })
        {
            var (a1, a2, b1, b2, n) = AlphaB(slab, w, t, sg, fHz, eeff, gs);
            double aStripB = a2 - a1, aGroundB = b1 - a1, aTotalB = b2 - a1;
            output.WriteLine($"   ground sigma = {gs:E3} S/m, N = {n}");
            output.WriteLine($"     A1 {a1: E5}  A2 {a2: E5}  B1 {b1: E5}  B2 {b2: E5}  Np/m");
            output.WriteLine($"     a_strip {aStripB:E4}  a_ground {aGroundB:E4}  a_total {aTotalB:E4}" +
                             $"   SHARE {aGroundB/aTotalB:P2}");
            output.WriteLine($"     additivity (a_strip + a_ground)/a_total = {(aStripB+aGroundB)/aTotalB:F4}");
            output.WriteLine($"     vs kernel A: strip {aStripB/aStripA:F4}  ground {aGroundB/aGroundA:F4}  " +
                             $"total {aTotalB/aBothA:F4}");
            Assert.True(aGroundB > 0, $"the ground term removed loss rather than adding it: {aGroundB:E3}");
        }
    }

    /// <summary>
    /// <b>Why the ground term over-reads on FR-4 and not on GaAs — the one measurement that
    /// separates the two candidate reasons.</b>
    ///
    /// <para>At 10 GHz the ground term comes out 1.48× kernel A's on 1.6 mm FR-4 and 1.06× on
    /// 100 µm GaAs. Two explanations fit that ordering and they predict opposite things:</para>
    /// <list type="number">
    /// <item>A GEOMETRY effect (w/h is 1.89 against 0.71) — then it does not move with frequency.</item>
    /// <item>The ground absorbing the SURFACE-WAVE and radiated field, which kernel A's
    /// incremental-inductance rule structurally has no term for — then it tracks k₀H, which is
    /// 0.067 on FR-4 at 2 GHz against 0.335 at 10 GHz and 0.021 on GaAs at 10 GHz.</item>
    /// </list>
    /// <para>One substrate, three frequencies, w held at the 10 GHz re-bisected 50 Ω value so the
    /// geometry cannot move.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]          // measured: 44 s — three frequencies of the same
    public void M5_WhereTheGroundTermsExcessComesFrom_Fr4DownTheBand()   // eight-solve measurement
    {
        var slab = GroundedSlab.Fr4Starter;
        const double w = 3020.28e-6, t = 35e-6;
        output.WriteLine("FR-4 1.6 mm, 35 um Cu, w held at 3020.28 um (the 10 GHz 50 Ohm width).");
        output.WriteLine("     f GHz     k0*H    A a_strip   A a_ground   A share |" +
                         "  B a_strip   B a_ground   B share |  ground B/A   strip B/A");
        foreach (double fHz in new[] { 2e9, 10e9, 20e9 })
        {
            var (rStrip, z0, eeff) = KernelA(w, slab, t, CopperSigma, fHz, double.PositiveInfinity);
            var (rBoth,  _,  _)    = KernelA(w, slab, t, CopperSigma, fHz, CopperSigma);
            double aStripA = rStrip / (2 * z0), aGroundA = (rBoth - rStrip) / (2 * z0);

            var (a1, a2, b1, _, _) = AlphaB(slab, w, t, CopperSigma, fHz, eeff, CopperSigma);
            double aStripB = a2 - a1, aGroundB = b1 - a1;
            double k0h = 2 * Math.PI * fHz / EmConstants.C0 * slab.HeightM;
            output.WriteLine($"  {fHz/1e9,8:F0} {k0h,8:F4} {aStripA,11:E4} {aGroundA,12:E4} " +
                             $"{aGroundA/(aStripA+aGroundA),8:P2} |{aStripB,11:E4} {aGroundB,12:E4} " +
                             $"{aGroundB/(aStripB+aGroundB),8:P2} | {aGroundB/aGroundA,11:F3} " +
                             $"{aStripB/aStripA,11:F4}");
        }
    }

    private static (double R, double Z0, double Eeff) KernelA(double w, GroundedSlab slab, double t,
                                                              double sigmaSm, double fHz, double groundSigma)
    {
        var p = EmProblemBuilders.Microstrip(w, slab.HeightM, t, slab.Material.EpsR, tanD: 0,
                                             sigmaSm: sigmaSm, groundSigmaSm: groundSigma);
        var res = new QuasiStaticKernel().SolveDetailed(p, EmMeshSettings.Default, [fHz]);
        return (res.Rlgc.RPerM(2 * Math.PI * fHz),
                Math.Sqrt(res.Rlgc.LPerM / res.Rlgc.CPerM), res.Rlgc.Eeff);
    }

    /// <summary>The four alphas one mesh and one pair of standards produce: {PEC, real} strip
    /// crossed with {PEC, real} ground. <b>PerfectConductor makes the STRIP perfect and nothing
    /// else</b> — the ground is in the Green's function, not in the fill — which is exactly what
    /// makes B1 the ground term on its own.</summary>
    private static (double A1, double A2, double B1, double B2, int N) AlphaB(
        GroundedSlab slab, double widthM, double t, double sigmaSm, double fHz, double eeff,
        double groundSigma)
    {
        double lambdaG = EmConstants.C0 / (fHz * Math.Sqrt(eeff));
        var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3);
        int n = 0;

        (double Pec, double Lossy) Pair(Termination floor)
        {
            var stack = new LayerStack(floor, [new MediumLayer(slab.HeightM, slab.Material)],
                                       Termination.Air);
            var problem = new PlanarProblem(
                [new PlanarConductorLayer("Metal",
                    [PlanarLineFixtures.Rect(0, -0.5*widthM, 4.0*widthM, 0.5*widthM)], sigmaSm, t,
                    ZM: stack.TopZ)],
                slab, fHz, MediumStack: stack);

            var report = SurfaceMesher.Mesh(problem, mesh);
            n = report.Mesh.Bases.Count;
            var ports  = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem));
            var kernel = PlanarFrequencyKernel.Fit(problem, fHz);
            var levels = new PlanarLevels([stack.TopZ]);

            int k = PlanarCalibration.EndRunCellsFor(ports[0], slab);
            var a = PlanarCalibration.BuildLine(ports[0], 0.25 * lambdaG, k);
            var b = PlanarCalibration.BuildLine(ports[0], a.LengthM + 0.25 * lambdaG, k);

            double Alpha(PlanarFillSettings settings)
            {
                var sa = new PlanarSolveContext(a.Mesh, a.Ports, settings, levels).RawScatteringAt(kernel, fHz);
                var sb = new PlanarSolveContext(b.Mesh, b.Ports, settings, levels).RawScatteringAt(kernel, fHz);
                double expect = 2 * Math.PI * Math.Sqrt(eeff) * fHz * (b.LengthM - a.LengthM) / EmConstants.C0;
                return LineCalibration.Gamma(sa, sb, b.LengthM - a.LengthM, expect).Gamma.Real;
            }

            return (Alpha(PlanarFillSettings.Default with { PerfectConductor = true }),
                    Alpha(PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) }));
        }

        var pec   = Pair(Termination.Pec);
        var lossy = Pair(Termination.LossyGround(groundSigma, t));
        return (pec.Pec, pec.Lossy, lossy.Pec, lossy.Lossy, n);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // MEASUREMENT 3 — is the ground-attachment basis still legitimate?
    // ═══════════════════════════════════════════════════════════════════════════════════════

    private static PlanarProblem GroundViaProblem(Termination floor, double fHz = 10e9)
    {
        var pec   = LayerStacks.MmicTwoLevel;
        var stack = new LayerStack(floor, pec.Layers, pec.Top);
        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        var lower = new PlanarConductorLayer("M1", [Rect(0, 0, 400e-6, 100e-6)], GoldSigma, 2e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", [Rect(0, 0, 400e-6, 100e-6)], GoldSigma, 3e-6, stack.TopZ);
        PlanarVia[] vias =
        [
            new(0, 1, [Rect(180e-6, 30e-6, 220e-6, 70e-6)], GoldSigma),
            new(PlanarVia.GroundTerminal, 0, [Rect(60e-6, 30e-6, 100e-6, 70e-6)], GoldSigma),
        ];
        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz, null, stack, vias);
    }

    /// <summary>
    /// <b>The refusal itself, which is the whole of measurement 3's decision and costs nothing.</b>
    /// It is split from the fill comparison below so the routine gate keeps the part that would
    /// catch a deleted refusal — the expensive half only measures how far the matrix moved.
    /// </summary>
    [Fact]
    public void M4a_TheGroundViaRefusal_AsksForAConductorRatherThanAPec()
    {
        const double f = 10e9;
        var verdicts = new List<(string Tag, bool Ok)>();
        foreach (var (tag, floor) in new[]
        {
            ("PEC",              Termination.Pec),
            ("gold 3 um plane",  Termination.LossyGround(GoldSigma, 3e-6)),
            ("PMC",              Termination.Pmc),
            ("open to air",      Termination.Air),
        })
        {
            var ok = GroundViaProblem(floor, f).CanSolve();
            string why = ok.Reason ?? "";
            output.WriteLine($"{tag,-18} CanSolve: {(ok.Ok ? "yes" : "NO — " + why[..Math.Min(100, why.Length)])}");
            verdicts.Add((tag, ok.Ok));
        }

        // A conducting plane is accepted for the same reason a PEC is — it exists, it conducts and
        // electrostatically it is an equipotential, which is the limit the half rooftop's charge
        // conservation lives in. A PMC and an open half-space are neither, and deleting the refusal
        // rather than narrowing it is what this asserts did not happen.
        Assert.True(verdicts[0].Ok, "a PEC floor");
        Assert.True(verdicts[1].Ok, "a conducting plane");
        Assert.False(verdicts[2].Ok, "a PMC floor must still be refused");
        Assert.False(verdicts[3].Ok, "an open half-space must still be refused");
    }

    [Fact]
    [Trait("Category", "Benchmark")]          // measured: 1 m 20 s — two N = 440 multi-level fills
    public void M4_GroundAttachmentBasis_OnAConductingFloor()
    {
        const double f = 10e9;

        // The matrix itself, PEC against a real plane: the attachment basis must still fill, still
        // be symmetric, and the ground block must MOVE (it is the only thing that changed).
        var mp = SurfaceMesher.Mesh(GroundViaProblem(Termination.Pec, f));
        var ml = SurfaceMesher.Mesh(GroundViaProblem(Termination.LossyGround(GoldSigma, 3e-6), f));
        Assert.Equal(mp.Mesh.Bases.Count, ml.Mesh.Bases.Count);

        Mat<Complex> Fill(PlanarProblem p, PlanarMesh mesh)
        {
            var cores = PlanarFill.BuildCores(mesh);
            var set = new PlanarKernelSet(new LayeredSpectralGreens(p.EffectiveStack, f)).For(cores);
            return PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(p), 2 * Math.PI * f);
        }

        var zp = Fill(GroundViaProblem(Termination.Pec, f), mp.Mesh);
        var zl = Fill(GroundViaProblem(Termination.LossyGround(GoldSigma, 3e-6), f), ml.Mesh);

        int n = mp.Mesh.Bases.Count, ground = 0;
        double worstSym = 0, worstAttach = 0, worstOther = 0;
        for (int i = 0; i < n; i++)
        {
            if (mp.Mesh.Bases[i].AttachesToGround) ground++;
            for (int j = 0; j < n; j++)
            {
                worstSym = Math.Max(worstSym, (zl[i, j] - zl[j, i]).Magnitude / (zl[i, j].Magnitude + 1e-300));
                double d = (zl[i, j] - zp[i, j]).Magnitude / (zp[i, j].Magnitude + 1e-300);
                if (mp.Mesh.Bases[i].AttachesToGround || mp.Mesh.Bases[j].AttachesToGround)
                    worstAttach = Math.Max(worstAttach, d);
                else worstOther = Math.Max(worstOther, d);
            }
        }
        output.WriteLine($"N = {n}, {ground} attachment bases. Worst |Z-Zt|/|Z| on the lossy fill: {worstSym:E2}");
        output.WriteLine($"Worst relative move PEC -> lossy plane: attachment rows {worstAttach:E3}, " +
                         $"everything else {worstOther:E3}");
        Assert.True(ground > 0);
        Assert.True(worstSym < 1e-10, "the lossy-floor fill is no longer structurally symmetric");
        Assert.True(worstAttach > 0, "the attachment block did not move at all");
    }
}
