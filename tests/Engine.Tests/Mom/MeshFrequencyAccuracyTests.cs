// M0's OWN GATE — the accuracy measurement, and it is the deliverable rather than a pass/fail.
//
// brief-em-sweep-performance.md's M0: "Without this the parameter is a foot-gun." Sizing the mesh
// below the sweep's top is a pure cost reduction with a real accuracy cost, and the only defensible
// way to ship the control is to say what that cost actually IS — measured, on a real design, per
// decade of the band, alongside N and wall clock.
//
// The reference is TODAY'S behaviour (mesh sized at the sweep's top), so what is reported is the
// error the CONTROL introduces, not the solver's own error against physics. That is the right
// comparison: a user choosing a mesh frequency is asking "how much do I give up against what I
// would have got", and L8d already measured the absolute residual separately (~6.0e-3 at 10 GHz on
// 1.6 mm FR-4, and L9d ~1e-2 on a two-level structure) — those are the numbers this table has to be
// read against to decide whether a value is defensible.
//
// Category=Benchmark, and taken ALONE: L8d's own standing warning is that a benchmark sharing a run
// read more than twice as slow, and that number reached the design note before it was checked.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class MeshFrequencyAccuracyTests(ITestOutputHelper output)
{
    private const double SweepTopHz = 20e9;

    /// <summary>1–20 GHz, spread so both decades of the band are sampled rather than only its top.</summary>
    private static readonly double[] BoardBand =
        [1e9, 2e9, 3.5e9, 5e9, 7e9, 10e9, 13e9, 16e9, 20e9];

    /// <summary>
    /// The GaAs band starts at 5 GHz, not 1 GHz, and that is a PRE-EXISTING refusal rather than
    /// anything M0 introduced: a 100 µm substrate at 1 GHz has PathExtent·k₀H = 0.63, so DCIM's own
    /// sampling path stops before it reaches the k_ρ ~ 1/H scale the stack's image structure lives
    /// at, and <c>Dcim.CanFitAtFrequency</c> refuses by name (L9e's D8). 5 GHz clears it with room.
    /// Both bands keep the SAME 20 GHz top so the mesh-frequency ratios stay comparable.
    /// </summary>
    private static readonly double[] MmicBand =
        [5e9, 9e9, 13e9, 17e9, 20e9];

    private sealed record Run(double MeshFreqHz, int Unknowns, double WallMs,
                              IReadOnlyList<Mat<Complex>> S);

    private static Run Solve(PlanarProblem problem, IReadOnlyList<PlanarPort> ports,
                             double[] band, double? meshFreqHz)
    {
        var settings = PlanarMeshSettings.Default with { MeshFrequencyHz = meshFreqHz };
        var report   = SurfaceMesher.Mesh(problem, settings);
        Assert.True(report.CanSolve, report.Refusal ?? "over budget");

        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);

        var sw = Stopwatch.StartNew();
        var res = PlanarSolve.Run(problem, report.Mesh, resolved, band);
        sw.Stop();

        return new Run(meshFreqHz ?? SweepTopHz, report.UnknownCount, sw.Elapsed.TotalMilliseconds,
                       [.. res.Points.Select(p => p.S)]);
    }

    /// <summary>Worst |ΔS| over every matrix entry at every band point in [lo, hi).</summary>
    private static double WorstDelta(Run reference, Run other, double[] band, double loHz, double hiHz)
    {
        double worst = 0;
        for (int i = 0; i < band.Length; i++)
        {
            if (band[i] < loHz || band[i] >= hiHz) continue;
            var a = reference.S[i];
            var b = other.S[i];
            for (int r = 0; r < a.RowCount; r++)
                for (int c = 0; c < a.ColCount; c++)
                    worst = Math.Max(worst, Complex.Abs(a[r, c] - b[r, c]));
        }
        return worst;
    }

    private void Report(string title, PlanarProblem problem, IReadOnlyList<PlanarPort> ports,
                        double[] band, double splitHz)
    {
        // Reference FIRST, and it is today's behaviour exactly: MeshFrequencyHz unset.
        var full    = Solve(problem, ports, band, null);
        var half    = Solve(problem, ports, band, SweepTopHz / 2);
        var quarter = Solve(problem, ports, band, SweepTopHz / 4);

        output.WriteLine("");
        output.WriteLine($"=== M0 accuracy — {title} ===");
        output.WriteLine($"sweep {band[0] / 1e9:G4}–{band[^1] / 1e9:G4} GHz, {band.Length} points, " +
                         $"de-embedded, cells/λ = {PlanarMeshSettings.DefaultCellsPerWavelength}");
        output.WriteLine("");
        output.WriteLine($"| mesh sized at | N | wall clock | worst |ΔS| {band[0] / 1e9:G4}–{splitHz / 1e9:G4} GHz " +
                         $"| worst |ΔS| {splitHz / 1e9:G4}–{band[^1] / 1e9:G4} GHz | worst |ΔS| whole band |");
        output.WriteLine("|---|---|---|---|---|---|");
        output.WriteLine($"| {full.MeshFreqHz / 1e9:G4} GHz (sweep top — the reference) | {full.Unknowns} " +
                         $"| {full.WallMs / 1000:F1} s | — | — | — |");

        foreach (var run in new[] { half, quarter })
        {
            output.WriteLine(
                $"| {run.MeshFreqHz / 1e9:G4} GHz | {run.Unknowns} | {run.WallMs / 1000:F1} s " +
                $"| {WorstDelta(full, run, band, 0, splitHz):G3} " +
                $"| {WorstDelta(full, run, band, splitHz, double.PositiveInfinity):G3} " +
                $"| {WorstDelta(full, run, band, 0, double.PositiveInfinity):G3} |");
        }

        output.WriteLine("");
        output.WriteLine($"N ratio full/half = {(double)full.Unknowns / half.Unknowns:F2}×, " +
                         $"full/quarter = {(double)full.Unknowns / quarter.Unknowns:F2}×");
        output.WriteLine($"wall-clock ratio full/half = {full.WallMs / half.WallMs:F2}×, " +
                         $"full/quarter = {full.WallMs / quarter.WallMs:F2}×");

        // NOT a tolerance gate — the numbers above are the deliverable.
        //
        // And deliberately NOT an "N falls monotonically" gate either: it DOES NOT, and the first
        // version of this test asserted that it did. On a narrow conductor the outermost edge cell
        // is anchored to the conductor WIDTH while the bulk cell is anchored to λ, so coarsening the
        // λ cap widens the gap the graded fan has to bridge and can cost more cells than it saves —
        // measured on the 72 µm GaAs line, N goes 773 -> 705 -> 2,014 across the three rows above.
        // See MeshFrequencyTests.OnANarrowConductor_LoweringIt_CanRAISETheUnknownCount.
        //
        // What IS asserted is the thing a broken control would violate and a mere accuracy loss
        // would not: every coarsened run still produces a solvable, finite answer everywhere.
        foreach (var run in new[] { half, quarter })
            foreach (var s in run.S)
                for (int r = 0; r < s.RowCount; r++)
                    for (int c = 0; c < s.ColCount; c++)
                        Assert.True(double.IsFinite(s[r, c].Real) && double.IsFinite(s[r, c].Imaginary));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M0Accuracy_Fr4Hero_ReportedPerDecade()
    {
        // §10.7's own worked example: 50 Ω microstrip on 1.6 mm FR-4, W ≈ 2.9 mm, 20 mm long.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, SweepTopHz);
        Report("§10.7's FR-4 hero (2.9 × 20 mm, 1.6 mm FR-4)", problem,
               PlanarLineFixtures.EndPorts(problem), BoardBand, 10e9);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M0Accuracy_GaAsHero_ReportedPerDecade()
    {
        // The MMIC counterpart, on a substrate where λ_g is very different — the mesh-frequency
        // trade is a property of the STACK as much as of the artwork, so one board is not evidence.
        var problem = PlanarLineFixtures.GaAsLine(2e-3, SweepTopHz);
        Report("The GaAs MMIC counterpart (72 µm × 2 mm, 100 µm GaAs)", problem,
               PlanarLineFixtures.EndPorts(problem), MmicBand, 10e9);
    }
}
