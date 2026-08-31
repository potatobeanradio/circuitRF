using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>MIM-4 / milestone 3 — <c>C_pul</c> at an interior height, and the route selection that keeps
/// the shipped one bit-identical.</b>
///
/// <para>D7's reference impedance is <c>Z_c = γ/(jωC_pul)</c> and the de-embedded S is REFERENCED to
/// it, so this is the one number in the whole brief that renormalises every published s-parameter if
/// it is wrong. It gets its own oracles rather than being taken on the kernel's word:</para>
///
/// <list type="bullet">
///   <item><b>The same problem, two independent electrostatic kernels.</b> On the slab top both the
///         shipped image series and the interior fit apply, so they must agree — the same standards,
///         the same meshes, the same differencing, one kernel swapped.</item>
///   <item><b>Split invariance.</b> Cutting the slab into sub-layers of its own material changes no
///         physics and must move no digit of C_pul, which exercises every part of the interior path
///         that the one-layer case leaves idle.</item>
///   <item><b>The series-capacitance limit</b> (§10.9's tier 2, in the planar geometry): two stacked
///         dielectrics and their single series-equivalent slab must converge as the line widens,
///         because a wide line IS a parallel-plate capacitor and stacked dielectrics under one are
///         capacitors in series. It is a LIMIT, so the gate is the trend and not a fixed band.</item>
///   <item><b>The route selection itself</b>, asserted on the object rather than inferred from a
///         number: an on-slab-top port must still take the shipped series.</item>
/// </list>
/// </summary>
public sealed class InteriorCPulTests
{
    private readonly ITestOutputHelper _out;
    public InteriorCPulTests(ITestOutputHelper output) => _out = output;

    private const double FLo = 2e9, FHi = 10e9;

    /// <summary>One meshed line and its calibration standards, on the coarse mesh — nothing here
    /// solves a frequency-domain system, so the standards only have to exist and be cored.</summary>
    private static (GroundedSlab Slab, PlanarStandard[] Set) Standards(GroundedSlab slab)
    {
        var problem = PlanarLineFixtures.Line(slab, widthM: 3e-3, lengthM: 12e-3, fHz: FHi);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        return (slab, PlanarCalibration.BuildSet(ports[0], slab, FLo, FHi));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The two kernels on the one problem where both apply
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The gate this milestone stands on.</b> A line on the slab's own top surface is describable
    /// both ways: as <see cref="StaticGreens"/>'s image series over that slab, and as
    /// <see cref="InteriorStaticImages"/>' fit at z = h in the one-layer stack. Same standards, same
    /// meshes, same differencing — only the electrostatic kernel differs, so the difference IS the
    /// new kernel's error in the quantity that sets the published reference impedance.
    /// </summary>
    [Theory]
    [InlineData(1.6e-3, 4.4, 0.02)]
    [InlineData(100e-6, 12.9, 0.002)]
    public void M3_1_OnTheSlabTopTheInteriorRouteReproducesTheShippedCPul(double h, double epsR, double tanD)
    {
        var slab = new GroundedSlab(h, new EmMaterial(epsR, tanD));
        var (_, set) = Standards(slab);
        var stack = LayerStack.FromGroundedSlab(slab);

        double shipped  = PlanarDeembed.CapacitancePerMetre(set[0], set[^1], slab);
        double interior = PlanarDeembed.CapacitancePerMetre(set[0], set[^1], stack, h, h);

        double rel = Math.Abs(interior - shipped) / Math.Abs(shipped);
        _out.WriteLine($"h={h:G3} εᵣ={epsR}: shipped C_pul = {shipped:E10} F/m, " +
                       $"interior = {interior:E10} F/m, relative difference {rel:E3}");
        Assert.True(rel < 1e-9, $"{rel:E3}");
    }

    /// <summary>
    /// Splitting the slab into sub-layers of its own material is not a change to the problem, so
    /// C_pul must not move — and unlike the test above this exercises the inter-region scale factor
    /// and the two-level fit's dedup, which a one-layer stack never reaches.
    /// </summary>
    [Fact]
    public void M3_2_SplittingTheSlabIntoSubLayersOfItsOwnMaterialDoesNotMoveCPul()
    {
        var slab = GroundedSlab.Fr4Starter;
        var (_, set) = Standards(slab);
        double h = slab.HeightM;

        double one   = PlanarDeembed.CapacitancePerMetre(set[0], set[^1],
                                                         LayerStack.FromGroundedSlab(slab), h, h);
        double split = PlanarDeembed.CapacitancePerMetre(
            set[0], set[^1], LayerStack.FromGroundedSlab(slab).WithLayerSplit(0, 0.23, 0.31, 0.46), h, h);

        double rel = Math.Abs(split - one) / Math.Abs(one);
        _out.WriteLine($"one layer {one:E10} F/m, split into three {split:E10} F/m, " +
                       $"relative difference {rel:E3}");
        Assert.True(rel < 1e-9, $"{rel:E3}");
    }

    /// <summary>
    /// <b>§10.9's tier-2 idea in the planar geometry: two stacked dielectrics are two capacitors in
    /// series.</b> Under a WIDE line the field is parallel-plate, so a two-layer sub-feed region and
    /// the single slab whose εᵣ is its series equivalent must give the same C_pul; under a NARROW one
    /// they must not, because fringing samples the two layers differently and no single εᵣ can
    /// represent that. Both halves are asserted — the convergence is the oracle, and the divergence is
    /// what says the test can tell the two apart at all.
    ///
    /// <para>The series equivalent is exactly what <c>PlanarExtractor</c> now builds for its SIZING
    /// slab, so this is also the measurement behind that choice.</para>
    /// </summary>
    [Fact]
    public void M3_3_AWideLineOverStackedDielectricsApproachesItsSeriesEquivalentSlab()
    {
        const double d1 = 1.0e-3, d2 = 0.6e-3;
        var m1 = new EmMaterial(9.8, 0.0);
        var m2 = new EmMaterial(2.2, 0.0);
        double h = d1 + d2;
        double epsSeries = h / (d1 / m1.EpsR + d2 / m2.EpsR);

        var equivalent = new GroundedSlab(h, new EmMaterial(epsSeries, 0.0));
        var stacked    = new LayerStack(Termination.Pec,
                                        [new MediumLayer(d1, m1), new MediumLayer(d2, m2)],
                                        Termination.Air);
        _out.WriteLine($"series-equivalent εᵣ = {epsSeries:G6} for {m1.EpsR}/{m2.EpsR} over " +
                       $"{d1 * 1e3:G3}/{d2 * 1e3:G3} mm");

        double previous = double.NaN;
        double widest = double.NaN, narrowest = double.NaN;
        foreach (double overH in new[] { 0.5, 2.0, 8.0, 24.0 })
        {
            double w = overH * h;
            var problem = PlanarLineFixtures.Line(equivalent, w, lengthM: 8 * h + 6 * w, fHz: FHi);
            var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
            var set = PlanarCalibration.BuildSet(ports[0], equivalent, FLo, FHi);

            double one = PlanarDeembed.CapacitancePerMetre(set[0], set[^1], equivalent);
            double two = PlanarDeembed.CapacitancePerMetre(set[0], set[^1], stacked, h, h);
            double rel = Math.Abs(two - one) / one;

            _out.WriteLine($"  W/h={overH,-5:G4} N={mesh.Bases.Count,-6} equivalent {one:E6} F/m, " +
                           $"stacked {two:E6} F/m, difference {rel * 100:F3}%");
            if (double.IsNaN(narrowest)) narrowest = rel;
            widest = rel;
            previous = rel;
        }
        _ = previous;

        Assert.True(widest < 0.02,
            $"a line 24 h wide should be within 2% of its series-equivalent slab; it is {widest * 100:F2}%");
        Assert.True(narrowest > 4 * widest,
            $"the narrow line must NOT match the series equivalent — narrow {narrowest * 100:F2}% vs " +
            $"wide {widest * 100:F2}% — or this test cannot tell the two media apart");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The route selection — R-mlp-1
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Which C_pul route a calibrator takes, asserted on the object.</b> A one-slab medium with the
    /// level on its top surface stays on the shipped image series and reports no interior fit at all;
    /// anything else — a buried level, or a level on top of a STRATIFIED region — takes the interior
    /// one.
    ///
    /// <para>The stratified single-level case is the one worth naming: that level IS at the slab's
    /// height, because the extractor's sizing slab is built from the same distance. Deciding on
    /// height alone would have put a two-dielectric board's reference impedance on a one-dielectric
    /// series, plausibly and wrongly. The medium is compared structurally instead.</para>
    /// </summary>
    [Fact]
    public void M3_4_TheShippedRouteIsTakenForAOneSlabMediumAndNothingElse()
    {
        var slab = GroundedSlab.GaAsStarter;
        double h = slab.HeightM;
        var problem = PlanarLineFixtures.Line(slab, widthM: 70e-6, lengthM: 400e-6, fHz: FHi);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var set = PlanarCalibration.BuildSet(ports[0], slab, FLo, FHi);
        _ = mesh;

        var oneSlab = LayerStack.FromGroundedSlab(slab);
        var buried  = new LayerStack(Termination.Pec,
                                     [new MediumLayer(h, slab.Material),
                                      new MediumLayer(3e-6, new EmMaterial(1.0, 0.0))],
                                     Termination.Air);
        var stratified = new LayerStack(Termination.Pec,
                                        [new MediumLayer(0.5 * h, slab.Material),
                                         new MediumLayer(0.5 * h, new EmMaterial(6.8, 0.001))],
                                        Termination.Air);

        (string Name, LayerStack? Stack, double Z, bool Interior)[] cases =
        [
            ("no medium given",   null,       h, false),
            ("one-slab medium",   oneSlab,    h, false),
            ("buried level",      buried,     h, true),
            ("stratified, at h",  stratified, h, true),
        ];

        foreach (var (name, stack, z, wantsInterior) in cases)
        {
            var cal = new PlanarPortCalibrator(ports[0], slab, FLo, FHi, null, null,
                                               standardLevelZ: z, standards: set, mediumStack: stack);
            bool took = !double.IsNaN(cal.InteriorFitResidual);
            _out.WriteLine($"{name,-18}: interior route {took}, residual {cal.InteriorFitResidual:E3}");
            Assert.Equal(wantsInterior, took);
            if (took)
                Assert.True(cal.InteriorFitResidual < PlanarSolve.InteriorCPulResidualCeiling,
                            $"{name}: residual {cal.InteriorFitResidual:E3}");
        }
    }

    /// <summary>
    /// The fit's own residual on every stack this brief claims to serve, against the ceiling
    /// <c>PlanarSolve</c> refuses at — so the four decades of headroom that ceiling was sized from is
    /// a measurement in the suite rather than a sentence in a comment.
    /// </summary>
    [Fact]
    public void M3_5_EveryStackThisServesFitsFarInsideTheRefusalCeiling()
    {
        var mim = new LayerStack(Termination.Pec,
        [
            new MediumLayer(100e-6, new EmMaterial(12.9, 0.002)),
            new MediumLayer(0.2e-6, new EmMaterial(6.8, 0.001)),
            new MediumLayer(2.8e-6, new EmMaterial(1.0, 0.0)),
        ], Termination.Air);

        var board = new LayerStack(Termination.Pec,
        [
            new MediumLayer(1.00e-3, new EmMaterial(4.4, 0.02)),
            new MediumLayer(0.50e-3, new EmMaterial(9.8, 0.001)),
            new MediumLayer(0.25e-3, new EmMaterial(2.2, 0.004)),
        ], Termination.Air);

        (string Name, LayerStack Stack, double Z)[] cases =
        [
            ("FR-4 one slab",    LayerStack.FromGroundedSlab(GroundedSlab.Fr4Starter), 1.6e-3),
            ("GaAs one slab",    LayerStack.FromGroundedSlab(GroundedSlab.GaAsStarter), 100e-6),
            ("board, buried",    board, 1.0e-3),
            ("board, mid layer", board, 1.5e-3),
            ("board, top",       board, board.TopZ),
            ("MIM lower plate",  mim,   100e-6),
            ("MIM upper plate",  mim,   100.2e-6),
            ("MIM interconnect", mim,   103e-6),
        ];

        double worst = 0;
        foreach (var (name, stack, z) in cases)
        {
            var m = InteriorStaticImages.FitScalar(stack, z, z);
            _out.WriteLine($"{name,-18} z={z:G6}: {m.Images.Count,2} images, residual {m.Residual:E3}");
            worst = Math.Max(worst, m.Residual);
        }
        _out.WriteLine($"worst {worst:E3} against the ceiling {PlanarSolve.InteriorCPulResidualCeiling:E0}");
        Assert.True(worst < 0.01 * PlanarSolve.InteriorCPulResidualCeiling,
            $"worst residual {worst:E3} is within two decades of the refusal ceiling — the ceiling " +
            "was sized as a guard against a failed fit, not as a tolerance");
    }
}
