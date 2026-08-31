using System.Numerics;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>MIM-4 / milestone 2 — the SPATIAL static Green's function at interior heights.</b>
///
/// <para>Milestone 1 pinned the spectral kernel; this file pins the inverse transform, and it does
/// so against three things that share nothing with it: the shipped one-slab image series
/// (<see cref="StaticGreens"/>, validated independently at L8a),
/// <see cref="LayeredStaticGreens"/>'s own adaptive quadrature in the top half-space, and
/// <see cref="InteriorStaticGreens.PotentialByQuadrature"/> — direct numerical Hankel integration —
/// at heights where nothing else in the repository can answer at all.</para>
///
/// <para><b>The method comparison the brief asked for is recorded in
/// <c>src/Engine/Mom/RESOLVED.md</c> §MIM-4, and the loser is quadrature:</b> the image model costs
/// one ~50 ms fit and 0.34 µs per ρ (measured against the shipped one-slab series' own 2.5 µs), and
/// the reference integrator costs 1–270 ms PER ρ on the same stacks. Nothing here re-measures a
/// wall clock; the numbers below are accuracy.</para>
/// </summary>
public sealed class InteriorStaticImageTests
{
    private readonly ITestOutputHelper _out;
    public InteriorStaticImageTests(ITestOutputHelper output) => _out = output;

    private static double Rel(Complex e, Complex a) => (e - a).Magnitude / Math.Max(e.Magnitude, 1e-300);

    private static LayerStack Stratified() => new(
        Termination.Pec,
        [
            new MediumLayer(1.00e-3, new EmMaterial(4.4, 0.02)),
            new MediumLayer(0.50e-3, new EmMaterial(9.8, 0.001)),
            new MediumLayer(0.25e-3, new EmMaterial(2.2, 0.004)),
        ],
        Termination.Air);

    /// <summary>
    /// The shipped MIM technology's shape: 100 µm of GaAs, the lower plate, 0.2 µm of capacitor
    /// dielectric, the air gap to the upper interconnect metal. <b>Three orders of magnitude between
    /// the thinnest and thickest layer</b> — the case the two-level fit exists for and the case the
    /// whole brief exists for.
    /// </summary>
    private static LayerStack MimStack() => new(
        Termination.Pec,
        [
            new MediumLayer(100e-6, new EmMaterial(12.9, 0.002)),
            new MediumLayer(0.2e-6, new EmMaterial(6.8, 0.001)),
            new MediumLayer(2.8e-6, new EmMaterial(1.0, 0.0)),
        ],
        Termination.Air);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Against the shipped kernels
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The rung that ties the fit to the shipped de-embedding path.</b> On the slab top the model
    /// must reproduce <see cref="StaticGreens.ScalarPotential"/>'s exact image series — the function
    /// <see cref="PlanarKernelTerms.StaticScalar"/> carries today.
    ///
    /// <para><b>The gate is stated in two parts on purpose, because a relative one alone would be
    /// dishonest at large ρ.</b> Out to ρ = h the agreement is 1e-10 or better. Past that the kernel
    /// itself collapses — the 1/ρ term cancels EXACTLY over a ground plane (the sum rule below), so
    /// what is left is a dipole falling as 1/ρ³ — and its value is a 45× cancellation among the
    /// images' second moments <c>Σ a b²</c>. A fit exact to 3e-12 in the spectrum still leaves ~1e-6
    /// of THAT cancellation, which is 9e-15 of the near-field scale that actually sets a
    /// capacitance. Constraining the second moment as well as the sum is the named remedy if it ever
    /// binds; it is not built, because the absolute error is four orders below the shipped series'
    /// own truncation.</para>
    /// </summary>
    [Theory]
    [InlineData(1.6e-3, 4.4, 0.02)]
    [InlineData(100e-6, 12.9, 0.002)]
    public void M1_OnTheSlabTopItReproducesTheShippedImageSeries(double h, double epsR, double tanD)
    {
        var slab  = new GroundedSlab(h, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        var model = InteriorStaticImages.FitScalar(stack, h, h);

        _out.WriteLine($"h={h:G3} εᵣ={epsR}: {model.Images.Count} images, spectral residual {model.Residual:E3}");

        double near = 0, far = 0, absolute = 0;
        double reference = StaticGreens.ScalarPotential(slab, h * 1e-4).Magnitude;
        for (double r = h * 1e-4; r < h * 1e4; r *= 10)
        {
            Complex want = StaticGreens.ScalarPotential(slab, r);
            Complex got  = model.Evaluate(r);
            double rel = Rel(want, got);
            _out.WriteLine($"   ρ/h={r / h,-10:G6} {want:G8} vs {got:G8}   rel {rel:E3}   " +
                           $"abs {(want - got).Magnitude:E3}");
            if (r <= h) near = Math.Max(near, rel); else far = Math.Max(far, rel);
            absolute = Math.Max(absolute, (want - got).Magnitude / reference);
        }

        _out.WriteLine($"  worst relative: {near:E3} out to ρ = h, {far:E3} beyond; " +
                       $"worst absolute, as a fraction of G(ρ = h/10⁴): {absolute:E3}");
        Assert.True(near < 1e-9,     $"near-field {near:E3}");
        Assert.True(far  < 1e-4,     $"far-field {far:E3}");
        Assert.True(absolute < 1e-13, $"absolute {absolute:E3}");
    }

    /// <summary>
    /// <b>c_∞ + Σa = 0 over a ground plane, and it is imposed rather than fitted.</b> That sum is the
    /// coefficient of the spatial 1/ρ tail: a grounded structure's potential falls as a dipole, and
    /// an unconstrained least squares gets the cancellation only to its own residual. Measured
    /// before the constraint existed: the spectrum was exact to 2.4e-15 and the far field still 8.5e-6
    /// wrong.
    /// </summary>
    [Fact]
    public void M2_TheSumRuleIsExactWhereverTheStackIsGrounded()
    {
        foreach (var (name, stack, z) in new (string, LayerStack, double)[]
        {
            ("one-slab FR-4",  LayerStack.FromGroundedSlab(GroundedSlab.Fr4Starter), GroundedSlab.Fr4Starter.HeightM),
            ("stratified",     Stratified(), 1.0e-3),
            ("stratified top", Stratified(), Stratified().TopZ),
            ("MIM lower plate", MimStack(), 100e-6),
            ("MIM upper metal", MimStack(), 103e-6),
        })
        {
            var m = InteriorStaticImages.FitScalar(stack, z, z);
            Complex rule = m.Singular;
            foreach (var im in m.Images) rule += im.Amplitude;
            _out.WriteLine($"{name,-16} z={z:G6}: c_∞ + Σa = {rule:E3} ({m.Images.Count} images)");
            Assert.True(rule.Magnitude < 1e-13, $"{name}: {rule}");
        }
    }

    /// <summary>
    /// In the top half-space <see cref="LayeredStaticGreens"/> answers by its own adaptive
    /// quadrature over its own cascade — a completely separate inverse transform.
    /// </summary>
    [Fact]
    public void M3_InTheTopHalfSpaceItAgreesWithLayeredStaticGreens()
    {
        var stack = Stratified();
        double H = stack.TopZ;
        var model = InteriorStaticImages.FitScalar(stack, H, H);

        double worst = 0;
        for (double r = 1e-5; r < 1e-2; r *= 2.5)
        {
            Complex want = LayeredStaticGreens.ScalarPotential(stack, r, H, H);
            Complex got  = model.Evaluate(r);
            worst = Math.Max(worst, Rel(want, got));
        }
        _out.WriteLine($"vs LayeredStaticGreens out to ρ = 6.7 H: worst {worst:E3}");
        Assert.True(worst < 1e-7, $"worst {worst:E3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Where nothing else can answer — against direct Hankel integration
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Interior heights on a stratified stack, against the reference integrator.</b> Every one of
    /// these heights is refused by name by <see cref="LayeredStaticGreens"/>, which is the gap this
    /// brief exists to close.
    /// </summary>
    [Theory]
    [InlineData(1.00e-3)]        // on the first interface — a buried metal level
    [InlineData(1.50e-3)]        // on the second
    [InlineData(1.20e-3)]        // strictly inside a layer
    public void M4_InteriorHeightsAgreeWithDirectHankelIntegration(double z)
    {
        var stack = Stratified();
        var model = InteriorStaticImages.FitScalar(stack, z, z);
        _out.WriteLine($"z={z * 1e3:F2} mm: {model.Images.Count} images, c_∞ = {model.Singular:G6}, " +
                       $"spectral residual {model.Residual:E3}");

        double worst = 0;
        for (double r = 1e-5; r < 1e-2; r *= 3.0)
        {
            Complex want = InteriorStaticGreens.PotentialByQuadrature(stack, true, r, z, z);
            Complex got  = model.Evaluate(r);
            double rel = Rel(want, got);
            _out.WriteLine($"   ρ={r:G4}: {want:G8} vs {got:G8}   rel {rel:E3}");
            worst = Math.Max(worst, rel);
        }
        Assert.True(worst < 1e-7, $"worst {worst:E3}");
    }

    /// <summary>
    /// <b>The thin-film case, which is what the two-level fit is FOR.</b> A 0.2 µm capacitor
    /// dielectric on 100 µm of GaAs puts the shallowest image round trip 500× closer than the
    /// substrate's, and one uniform k grid cannot resolve both: a spacing fine enough for the deep
    /// images spans a range in which the shallow ones have not begun to decay. ρ is swept over five
    /// decades, down to a quarter of the dielectric thickness.
    /// </summary>
    [Theory]
    [InlineData(100.0e-6)]       // the lower plate — GaAs below, capacitor dielectric above
    [InlineData(100.2e-6)]       // the upper plate — dielectric below, air above
    [InlineData(103.0e-6)]       // the interconnect metal
    public void M5_TheThinFilmStackAgreesWithDirectHankelIntegration(double z)
    {
        var stack = MimStack();
        var model = InteriorStaticImages.FitScalar(stack, z, z);
        _out.WriteLine($"z={z * 1e6:F2} µm: {model.Images.Count} images, c_∞ = {model.Singular:G6}, " +
                       $"spectral residual {model.Residual:E3}");

        double worst = 0;
        foreach (double r in new[] { 0.05e-6, 0.5e-6, 2.5e-6, 25e-6, 250e-6 })
        {
            Complex want = InteriorStaticGreens.PotentialByQuadrature(stack, true, r, z, z);
            Complex got  = model.Evaluate(r);
            double rel = Rel(want, got);
            _out.WriteLine($"   ρ={r * 1e6:G4} µm: {want:G8} vs {got:G8}   rel {rel:E3}");
            worst = Math.Max(worst, rel);
        }
        Assert.True(worst < 1e-7, $"worst {worst:E3}");
    }

    /// <summary>
    /// The magnetostatic model on a non-magnetic stack is free space plus ONE perfect negative image
    /// at 2z, exactly — the interior generalisation of what <see cref="StaticGreens.VectorPotential"/>
    /// records for the slab top. Nothing is fitted here that has any business being fitted, so the
    /// gate is machine precision.
    /// </summary>
    [Fact]
    public void M6_TheVectorModelIsFreeSpacePlusOneImageAtEveryInteriorHeight()
    {
        var stack = Stratified();
        foreach (double z in new[] { 0.4e-3, 1.0e-3, 1.6e-3, stack.TopZ })
        {
            var m = InteriorStaticImages.FitVector(stack, z, z);
            double worst = 0;
            for (double r = 1e-5; r < 1e-2; r *= 3.0)
            {
                Complex want = (1.0 / r - 1.0 / Math.Sqrt(r * r + 4 * z * z)) / (4 * Math.PI);
                Complex got  = m.Evaluate(r);
                worst = Math.Max(worst, Rel(want, got));
            }
            _out.WriteLine($"vector z={z * 1e3:F2} mm: {m.Images.Count} images, worst {worst:E3}");
            Assert.True(worst < 1e-12, $"z={z}: worst {worst:E3}");
        }
    }

    /// <summary>
    /// <see cref="InteriorStaticModel.SmoothAtZero"/> is the ρ → 0 value of what is left after the
    /// 1/ρ term is taken out — the <c>Constant</c> extraction coefficient
    /// <see cref="PlanarKernelTerms"/> carries. On the slab top it must equal the one the SHIPPED
    /// <see cref="PlanarKernelTerms.StaticScalar"/> computes from its own closed-form series, which
    /// is the arithmetic milestone 3's singular extraction rests on.
    /// </summary>
    [Theory]
    [InlineData(1.6e-3, 4.4, 0.02)]
    [InlineData(100e-6, 12.9, 0.002)]
    public void M7_TheSmoothPartAtZeroMatchesTheShippedExtractionConstant(double h, double epsR, double tanD)
    {
        var slab  = new GroundedSlab(h, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        var model = InteriorStaticImages.FitScalar(stack, h, h);
        var terms = PlanarKernelTerms.StaticScalar(slab);

        _out.WriteLine($"h={h:G3}: Inverse {terms.Inverse:G10} vs {model.Singular / (4 * Math.PI):G10}");
        _out.WriteLine($"h={h:G3}: Constant {terms.Constant:G10} vs {model.SmoothAtZero:G10}");

        Assert.True(Rel(terms.Inverse, model.Singular / (4 * Math.PI)) < 1e-14);
        Assert.True(Rel(terms.Constant, model.SmoothAtZero) < 1e-8);

        // And the two agree as FUNCTIONS: G(ρ) − c_∞/(4πρ) → SmoothAtZero as ρ → 0.
        double tiny = h * 1e-7;
        Complex smooth = model.Evaluate(tiny) - model.Singular / (4 * Math.PI * tiny);
        Assert.True(Rel(model.SmoothAtZero, smooth) < 1e-6, $"{model.SmoothAtZero} vs {smooth}");
    }

    /// <summary>
    /// The reference integrator refuses by name rather than grinding when its own partition would be
    /// absurd, and it says which instrument to reach for instead. R-mom-17.
    /// </summary>
    [Fact]
    public void M8_TheReferenceIntegratorRefusesAnAbsurdPartitionByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InteriorStaticGreens.PotentialByQuadrature(MimStack(), true, 1.0, 100e-6, 100e-6));
        _out.WriteLine(ex.Message);
        Assert.Contains("ORACLE", ex.Message);
        Assert.Contains("InteriorStaticImages.Fit", ex.Message);
    }
}
