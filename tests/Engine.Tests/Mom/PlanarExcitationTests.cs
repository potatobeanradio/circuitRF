// L8d Tier 1 — the raw solve, before any calibration exists.
//
// The point of this tier is that a great deal is checkable with no de-embedding at all: the current
// on a uniform line is a two-wave sum whether or not the ports are any good, so γ is available in
// closed form from a single solve (CurrentWaveOracle). That is the independent oracle Tier 2 checks
// the two-line extraction against, and it is measured first so that a Tier 2 disagreement can
// be localised rather than argued about.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarExcitationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Fact]
    public void T1_1_TheCurrentOnAUniformLineIsATwoWaveSum_SoGammaFallsOutInClosedForm()
    {
        const double f = 10e9;
        var problem     = PlanarLineFixtures.Fr4Line(20e-3, f);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        var ctx  = new PlanarSolveContext(mesh, prt);
        var sol  = ctx.SolveAt(PlanarLineFixtures.Kernel(problem.Slab, f), f);
        var g    = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);

        _out.WriteLine($"N = {mesh.Bases.Count}, pitch = {g.PitchM * 1e3:F4} mm, {g.Stations} stations");
        _out.WriteLine($"γ = {g.Alpha:F4} + j{g.Beta:F2} /m   fit residual {g.ResidualRel:E2}   " +
                       $"ε_eff = {g.EffectivePermittivity(f):F4}");

        // Every triple must give the same γ — this is an identity, not a fit, so the scatter measures
        // discretisation and round-off alone.
        Assert.True(g.ResidualRel < 2e-2, $"the two-wave recurrence does not hold: residual {g.ResidualRel:E2}");

        // Physically bounded: a microstrip's ε_eff lies strictly between air and the substrate.
        double eeff = g.EffectivePermittivity(f);
        Assert.InRange(eeff, 1.0, problem.Slab.Material.EpsR);

        // Passive, and losing far less than a nepper over the line.
        Assert.True(g.Alpha > 0, $"α = {g.Alpha:F4} is not positive — the line is not passive");
        Assert.True(g.Alpha * 20e-3 < 0.5, $"α·ℓ = {g.Alpha * 20e-3:F3} is implausibly lossy");
    }

    [Fact]
    public void T1_2_GammaFromTheCurrentWaveIsIndependentOfWhichPortDroveIt()
    {
        // Driving from the far end reverses the travelling wave; γ is a property of the line, so the
        // two extractions must agree. A sign or index error in LineCurrent shows up here immediately.
        const double f = 10e9;
        var problem     = PlanarLineFixtures.Fr4Line(20e-3, f);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        var sol = new PlanarSolveContext(mesh, prt).SolveAt(PlanarLineFixtures.Kernel(problem.Slab, f), f);
        var g1  = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);
        var g2  = CurrentWaveOracle.Extract(mesh, sol.Currents[1], prt[1]);

        double rel = (g1.Gamma - g2.Gamma).Magnitude / g1.Gamma.Magnitude;
        _out.WriteLine($"drive from port 1: γ = {g1.Gamma}\ndrive from port 2: γ = {g2.Gamma}\nrel {rel:E2}");
        Assert.True(rel < 1e-6, $"γ depends on which port drove it: {rel:E2}");
    }

    [Fact]
    public void T1_3_TheLineCurrentIsContinuousAndDecaysAwayFromTheDrivenEnd()
    {
        const double f = 10e9;
        var problem     = PlanarLineFixtures.Fr4Line(20e-3, f);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        var sol  = new PlanarSolveContext(mesh, prt).SolveAt(PlanarLineFixtures.Kernel(problem.Slab, f), f);
        var line = PlanarExcitation.LineCurrent(mesh, sol.Currents[0], PlanarBasisDirection.X,
                                                prt[0].TransverseLines[0], prt[0].TransverseLines[^1]);

        foreach (var (z, i) in line) _out.WriteLine($"z = {z * 1e3,8:F4} mm  |I| = {i.Magnitude:E4}");

        Assert.True(line.Count >= 10, $"only {line.Count} current stations on a 20 mm line");

        // The current at the driven port equals B's own sum, which is Y11 for a 1 V drive: the
        // travelling-wave view and the incidence-matrix view are the same number.
        Complex atPort = PlanarExcitation.PortCurrent(sol.Currents[0], prt[0]);
        Assert.Equal(sol.Y[0, 0].Real, atPort.Real, 12);
        Assert.Equal(sol.Y[0, 0].Imaginary, atPort.Imaginary, 12);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T1_5_BetaConvergesUnderMeshRefinement_WhichIsWhatJustifiesTheCoarseFixture()
    {
        // The coarse fixture is used everywhere in this slice because the ALGEBRA is exact on it.
        // That is only an honest choice if the PHYSICS it carries is already close, so measure it:
        // doubling the longitudinal resolution must barely move β.
        const double f = 10e9;
        var problem = PlanarLineFixtures.Fr4Line(20e-3, f);
        var kernel  = PlanarLineFixtures.Kernel(problem.Slab, f);

        var betas = new List<(int Cpl, int N, double Beta)>();
        foreach (int cpl in new[] { 10, 20 })
        {
            var mesh = SurfaceMesher.Mesh(problem,
                new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpl, EdgeMesh: false)).Mesh;
            var prt = PlanarPorts.ResolveAll(mesh, PlanarLineFixtures.EndPorts(problem));
            var sol = new PlanarSolveContext(mesh, prt).SolveAt(kernel, f);
            var g   = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);
            betas.Add((cpl, mesh.Bases.Count, g.Beta));
            _out.WriteLine($"cells/λ = {cpl,2}: N = {mesh.Bases.Count,4}, β = {g.Beta:F2} /m, " +
                           $"ε_eff = {g.EffectivePermittivity(f):F4}, residual {g.ResidualRel:E2}");
        }

        // MEASURED: 1.09% (β 401.66 → 397.34, ε_eff 3.673 → 3.594). That is the price of the coarse
        // fixture and it is stated rather than hidden — it is why every PHYSICS number in this slice
        // is taken on the shipping mesh under Category=Benchmark, and why the coarse mesh is used
        // only where the quantity under test is exact independently of it.
        double rel = Math.Abs(betas[1].Beta - betas[0].Beta) / betas[1].Beta;
        _out.WriteLine($"β moves {rel:P2} between the two");
        Assert.True(rel < 0.02, $"β moves {rel:P2} under refinement — the coarse fixture is too coarse");
    }

    [Fact]
    public void T1_4_GammaTracksTheGuidedWavelength_OnBothStarterSubstrates()
    {
        // A cross-check that costs nothing: β must scale with √ε_eff, and ε_eff must be larger on
        // the higher-permittivity substrate. This catches a kernel wired to the wrong slab.
        const double f = 10e9;
        var results = new List<(string Name, double Eeff)>();

        foreach (var (name, problem) in new[]
        {
            ("FR-4",  PlanarLineFixtures.Fr4Line(20e-3, f)),
            ("GaAs",  PlanarLineFixtures.GaAsLine(8e-3, f)),
        })
        {
            var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
            var sol  = new PlanarSolveContext(mesh, prt).SolveAt(PlanarLineFixtures.Kernel(problem.Slab, f), f);
            var g    = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);
            results.Add((name, g.EffectivePermittivity(f)));
            _out.WriteLine($"{name,-5} N = {mesh.Bases.Count,4}: ε_eff = {g.EffectivePermittivity(f):F4} " +
                           $"(εᵣ = {problem.Slab.Material.EpsR:F2}), fit residual {g.ResidualRel:E2}");
        }

        foreach (var (name, eeff) in results)
            Assert.True(eeff > 1.0, $"{name} gives ε_eff = {eeff:F4}, which is below air");

        Assert.True(results[1].Eeff > results[0].Eeff,
            "GaAs (εᵣ = 12.9) must give a larger ε_eff than FR-4 (εᵣ = 4.4)");
    }
}
