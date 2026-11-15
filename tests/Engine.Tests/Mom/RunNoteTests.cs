using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// brief-icf-1 R-icf1-2 … R-icf1-4 — three planar run notes that said something other than what happened: the edge-port crossover
/// note on a run whose ports never switch, a CONVERGED verdict on a sweep that compared nothing, and a non-converged sweep reported
/// as a note with no frequency. The coarse FR-4 line, milliseconds a point.
/// </summary>
public sealed class RunNoteTests(ITestOutputHelper output)
{
    private static (PlanarProblem P, PlanarMesh M, IReadOnlyList<PlanarPortResolution> Ports) Line()
    {
        var line = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line, PlanarLineFixtures.Coarse);
        return (line, mesh, ports);
    }

    [Fact]
    public void TheEdgePortCrossoverNote_IsNotGiven_WhenEveryEdgePortIsUncalibrated()
    {
        // 1.6 mm FR-4 crosses over at ~3.05 GHz; a 2 / 4 GHz sweep straddles it. Uncalibrated Ports are grounded at both points.
        var (p, m, ports) = Line();
        var uncal = ports.Select(x => x with { Uncalibrated = true }).ToList();
        var run = PlanarSolve.Run(p, m, uncal, [2e9, 4e9],
            new PlanarSolveSettings(Fill: new PlanarFillSettings { EdgePortReturn = PlanarEdgePortReturn.Ground }, Deembed: true));
        foreach (var n in run.Notes) output.WriteLine(n);
        Assert.DoesNotContain(run.Notes, n => n.StartsWith("Edge ports are ground-referenced below", StringComparison.Ordinal));
    }

    [Fact]
    public void OnTheLocalGrid_TheMeshNote_SaysTheMeshFrequencySetsTheBaseBlock()
    {
        // A narrow GaAs line: the metal, not λ_g/N, sets the pitch, which is where the note used to say the λ knobs are inert.
        var line = PlanarLineFixtures.GaAsLine(2e-3, 10e9);
        var tensor = SurfaceMesher.Mesh(line, PlanarLineFixtures.Coarse);
        var local = SurfaceMesher.Mesh(line, PlanarLineFixtures.Coarse with { LocalGrid = true });
        Assert.Contains(tensor.Notes, n => n.StartsWith("Cells per wavelength and Mesh frequency do NOT set this mesh", StringComparison.Ordinal));
        string note = Assert.Single(local.Notes, n => n.StartsWith("The local grid's base block is λ_g/10", StringComparison.Ordinal));
        output.WriteLine(note);
        Assert.Contains("at 10 GHz, the top of the sweep, so Cells per wavelength and Mesh frequency set it", note, StringComparison.Ordinal);
        Assert.DoesNotContain(local.Notes, n => n.Contains("do NOT set this mesh", StringComparison.Ordinal));
    }

    [Fact]
    public void ASweepTooShortToRefine_ComparedNothing_AndGivesNoVerdict()
    {
        var (p, m, ports) = Line();
        var run = PlanarSolve.Run(p, m, ports, [2e9, 3e9, 4e9, 5e9],
            new PlanarSolveSettings(Deembed: false, Adaptive: new PlanarAdaptiveSettings(Tolerance: 1e-2)));
        string note = Assert.Single(run.Notes, n => n.StartsWith("Adaptive frequency sampling", StringComparison.Ordinal));
        output.WriteLine(note);
        Assert.StartsWith("Adaptive frequency sampling compared nothing: the sweep has 4 points", note, StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERGED", note, StringComparison.Ordinal);
        Assert.Null(run.AdaptiveConverged);
    }

    [Fact]
    public void ASweepThatDidNotConverge_IsAWarning_NamingWhereTheWorstDisagreementIs()
    {
        // Tolerance 0: every interval splits until it runs out of grid, and stops above the tolerance.
        var (p, m, ports) = Line();
        double[] freqs = [.. Enumerable.Range(0, 9).Select(i => 2e9 + 0.5e9 * i)];
        var run = PlanarSolve.Run(p, m, ports, freqs,
            new PlanarSolveSettings(Deembed: false, Adaptive: new PlanarAdaptiveSettings(Tolerance: 0)));
        Assert.False(run.AdaptiveConverged);
        var warning = Assert.Single(run.Findings, f => f.IsWarning && f.Text.StartsWith("Adaptive frequency sampling DID NOT CONVERGE", StringComparison.Ordinal));
        output.WriteLine(warning.Text);
        Assert.Matches(@"\|ΔS\| = \S+ at [0-9.]+ GHz, against a tolerance", warning.Text);
    }
}
