using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>MIM-4 / milestone 4 — the de-embedded s-parameter gates, on a level the old refusals would not
/// let run at all.</b>
///
/// <para>Milestones 1–3 gate the kernel, the inversion and C_pul against oracles. These gate the
/// PUBLISHED answer, which is the only thing a user sees, and they are §10.9's own list applied on
/// the far side of the retired refusals:</para>
/// <list type="bullet">
///   <item>a uniform section, de-embedded, is MATCHED in its own Z_c — <c>|S₁₁|</c> is the whole
///         de-embedding error rolled into one number, and it is blind to whether Z_c itself is right,
///         which is why the phase gate sits beside it;</item>
///   <item>its phase is <c>−βℓ</c> at the drawn metal edges, i.e. the reference planes are where the
///         run says they are;</item>
///   <item>a line of length 2ℓ equals two cascaded lines of length ℓ.</item>
/// </list>
///
/// <para>Every one of these is <c>Category=Benchmark</c>: a de-embedded point on the general kernel
/// solves the DUT and its calibration standards, and §L9d measured that at 71.9 s for two levels.</para>
/// </summary>
public sealed class InteriorDeembedGateTests
{
    private readonly ITestOutputHelper _out;
    public InteriorDeembedGateTests(ITestOutputHelper output) => _out = output;

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    /// <summary>
    /// <b>A line on the UPPER level of an MMIC stack.</b> The lower level is empty, so the DUT is an
    /// ordinary uniform microstrip — but its metal sits 3 µm above the substrate's top surface with
    /// an encapsulation between, which is exactly the electrostatic problem the shipped image series
    /// does not solve and which <c>PlanarSolve</c> refused by name before this brief.
    /// </summary>
    private static PlanarProblem UpperLevelLine(double fHz, double lengthM, double widthM)
    {
        var stack = LayerStacks.MmicTwoLevel;
        return new PlanarProblem(
        [
            new PlanarConductorLayer("M1", [], 4.1e7, 2e-6, stack.InterfaceZ[1]),
            new PlanarConductorLayer("M2", [Rect(0, 0, lengthM, widthM)], 4.1e7, 3e-6, stack.TopZ),
        ], GroundedSlab.GaAsStarter, fHz, null, stack);
    }

    private static PlanarPort[] EndPorts(double lengthM, double widthM) =>
    [
        new PlanarPort(1, new EmPoint(0,       0.5 * widthM), PlanarPortSide.MinX, 50.0, 1),
        new PlanarPort(2, new EmPoint(lengthM, 0.5 * widthM), PlanarPortSide.MaxX, 50.0, 1),
    ];

    private static PlanarFrequencyPoint RunLine(double f, double lengthM, double widthM)
    {
        var problem = UpperLevelLine(f, lengthM, widthM);
        var mesh    = SurfaceMesher.Mesh(problem).Mesh;
        var ports   = PlanarPorts.ResolveAll(mesh, EndPorts(lengthM, widthM));
        var run     = PlanarSolve.Run(problem, mesh, ports, [f]);
        return run.Points[0];
    }

    /// <summary>
    /// <b>The gate that is blind to Z_c, and the one that is not, on the same run.</b> In the line's
    /// own reference a uniform section is matched, so |S₁₁| carries the whole de-embedding error;
    /// ∠S₂₁ = −βℓ then says the reference planes are the drawn metal edges and not somewhere the
    /// calibration invented.
    ///
    /// <para><see cref="PlanarSolve.Run"/> publishes at the ports' declared 50 Ω, so the answer is
    /// renormalised BACK to the Z_c the run itself reports — which is the number the interior
    /// electrostatics produced, so this measures that number rather than assuming it.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void G1_AUniformLineOnABuriedLevelDeembedsToAMatchedSection()
    {
        const double f = 10e9, len = 600e-6, w = 60e-6;
        var pt = RunLine(f, len, w);

        var zc = new[] { pt.Calibrations[0].Zc, pt.Calibrations[1].Zc };
        var atZc = PlanarDeembed.Renormalise(pt.S, [50.0, 50.0], zc);

        var g = pt.Calibrations[0].Gamma;
        Complex expected = Complex.Exp(-g.Gamma * len);
        double phaseErr = Math.Abs(atZc[1, 0].Phase - expected.Phase);
        double magErr   = Math.Abs(atZc[1, 0].Magnitude - expected.Magnitude);

        _out.WriteLine($"upper-level line ℓ = {len * 1e6:F0} µm, w = {w * 1e6:F0} µm at {f / 1e9:F0} GHz");
        _out.WriteLine($"  C_pul = {pt.Calibrations[0].CPerMetre:E6} F/m, Z_c = {zc[0]:F3} Ω, " +
                       $"βℓ = {g.Beta * len:F4} rad");
        _out.WriteLine($"  at Z_c: S₁₁ = {atZc[0, 0]:F6}, S₂₁ = {atZc[1, 0]:F6}, expected {expected:F6}");
        _out.WriteLine($"  ∠ error {phaseErr:E2} rad, |·| error {magErr:E2}");
        _out.WriteLine($"  published at 50 Ω: S₁₁ = {pt.S[0, 0]:F6}, S₂₁ = {pt.S[1, 0]:F6}");

        Assert.True(zc[0].Real > 0, $"Z_c = {zc[0]} has no positive real part");
        Assert.True(atZc[0, 0].Magnitude < 3e-2,
            $"|S₁₁| = {atZc[0, 0].Magnitude:E3} on a section that should be matched in its own Z_c");
        Assert.True(phaseErr < 3e-2, $"∠S₂₁ is {phaseErr:E2} rad from −βℓ");
        Assert.True(magErr < 3e-2, $"|S₂₁| is {magErr:E2} from e^(−αℓ)");

        // Reciprocity is structural and costs nothing to say out loud.
        Assert.True((pt.S[1, 0] - pt.S[0, 1]).Magnitude < 1e-9);
    }

    /// <summary>
    /// §10.9's cascade identity, on the buried level: a uniform line of length 2ℓ must equal two
    /// cascaded lines of length ℓ.
    ///
    /// <para><b>ONE calibration, applied to both lines</b> — T4_2's own shape, and the reason is not
    /// tidiness. Two independent <c>PlanarSolve.Run</c>s calibrate independently, and a₂₁'s SIGN is a
    /// continuation carried from the previous frequency: at a single frequency there is nothing to
    /// continue from, so two runs can land on opposite signs and S₂₁ differs by exactly π. Measured
    /// while writing this test — expected <c>&lt;0.902, −0.425&gt;</c>, got <c>&lt;−0.877, 0.461&gt;</c>,
    /// magnitudes agreeing to 0.6% — which is a fact about comparing two calibrations, not about the
    /// interior electrostatics. The two runs' Z_c also differed by 5.6%, because a standard reproduces
    /// its DUT's own longitudinal gridlines (D4) and the two DUTs are different lengths.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void G2_TheCascadeIdentityHoldsOnABuriedLevel()
    {
        const double f = 10e9, w = 60e-6;

        var problem = UpperLevelLine(f, 600e-6, w);
        var mesh    = SurfaceMesher.Mesh(problem).Mesh;
        var ports   = PlanarPorts.ResolveAll(mesh, EndPorts(600e-6, w));
        double z    = problem.LevelZ(1);
        var levels  = new PlanarLevels([z], problem.EffectiveStack.InterfaceZ[0]);
        var kernel  = PlanarFrequencyKernel.Fit(problem, f);

        var cal = new PlanarPortCalibrator(ports[0], problem.Slab, f, f, null, null,
                                           standardLevelZ: z, mediumStack: problem.EffectiveStack);
        Assert.False(double.IsNaN(cal.InteriorFitResidual));   // it IS on the interior route
        var c = cal.At(() => kernel, f);

        int k    = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);
        var one  = PlanarCalibration.BuildLine(ports[0], cal.Standards[0].LengthM, k);
        var two  = PlanarCalibration.BuildLine(ports[0], 2 * one.LengthM, k);

        Mat<Complex> Deembed(PlanarStandard std) => PlanarDeembed.Apply(
            new PlanarSolveContext(std.Mesh, std.Ports, null, levels, problem.Slab.HeightM)
                .RawScatteringAt(kernel, f), [c.Box, c.Box]);

        var s1 = Deembed(one);
        var s2 = Deembed(two);

        // The exp() corrects for length quantisation: BuildLine rounds up to a whole bulk cell.
        Complex expected = s1[1, 0] * s1[1, 0]
                         * Complex.Exp(-c.Gamma.Gamma * (two.LengthM - 2 * one.LengthM));
        double rel = (s2[1, 0] - expected).Magnitude / expected.Magnitude;

        _out.WriteLine($"interior C_pul = {c.CPerMetre:E6} F/m, Z_c = {c.Zc:F3} Ω, " +
                       $"fit residual {cal.InteriorFitResidual:E3}");
        _out.WriteLine($"ℓ  = {one.LengthM * 1e6:F1} µm (βℓ = {c.Gamma.Beta * one.LengthM:F3} rad) " +
                       $"→ S₂₁ = {s1[1, 0]:F6}");
        _out.WriteLine($"2ℓ = {two.LengthM * 1e6:F1} µm → S₂₁ = {s2[1, 0]:F6}, expected {expected:F6}");
        _out.WriteLine($"relative {rel:E3}");

        // The same band T4_2 uses on the slab top, and for the same reason: what limits it is direct
        // radiative and surface-wave coupling between the ports (§L8d's own T4_6), not the algebra.
        Assert.True(rel < 3e-2, $"the cascade identity fails by {rel:E3}");
    }
}
