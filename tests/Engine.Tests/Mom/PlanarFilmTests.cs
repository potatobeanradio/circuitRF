// EM-TFR — a resistive film on a metal level, in the planar solve. One test per claim.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarFilmTests
{
    private const double Rs = 50.0, W = 40e-6, Contact = 200e-6;

    private static PlanarMeshSettings Mesh(bool merge = false) =>
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 2, MinCellsAcrossConductor: 4,
            RooftopMerging: merge);

    /// <summary>A straight strip: metal, a film of L×W, metal. The metal is perfect when σ is 0, so DC reads the film alone.</summary>
    private static (PlanarProblem Problem, PlanarPort[] Ports) Strip(double squares, double sigma = 0, double t = 0)
    {
        double l = squares * W, end = 2 * Contact + l;
        var outline = PlanarLineFixtures.Rect(0, -W / 2, end, W / 2);
        var film = PlanarLineFixtures.Rect(Contact, -W / 2, Contact + l, W / 2);
        var layer = new PlanarConductorLayer("Metal1", [outline], sigma, t)
        {
            Films = [new PlanarFilmRegion("Resistor", [film], Rs)],
        };
        var problem = new PlanarProblem([layer], GroundedSlab.GaAsStarter, 5e9);
        return (problem, PlanarLineFixtures.EndPorts(problem));
    }

    /// <summary>Gate 1 — the DC network IS the rooftop basis, so R = R_s·L/W for any cell count on a rectangle.</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(10.0)]
    public void DcResistanceIsTheClosedForm(double squares)
    {
        var (problem, ports) = Strip(squares);
        var report = SurfaceMesher.Mesh(problem, Mesh());
        var dc = PlanarDcSolve.Solve(problem, report.Mesh, PlanarPorts.ResolveAll(report.Mesh, ports));
        double r = -1.0 / dc.Y[0, 1].Real;
        Assert.InRange(r / (Rs * squares) - 1, -1e-6, 1e-6);
    }

    /// <summary>Gates 2 and 5 — AC converges on the DC value at low frequency, and the PEC oracle does not short the
    /// resistor: the metal goes perfect and the film stays what it is.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LowFrequencySeriesResistanceIsContinuousIntoDc(bool perfect)
    {
        const double squares = 3;
        var (problem, ports) = Strip(squares, 4.1e7, 3e-6);
        // Two cells across and no fan, because a de-embedded solve runs three meshes and a strip's R does not need
        // more; fine enough along the line that a port's cut stays on the contact.
        var report = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 150, EdgeMesh: false, MinCellsAcrossConductor: 2));
        var settings = PlanarSolveSettings.Default with
        {
            Fill = PlanarFillSettings.Default with { PerfectConductor = perfect },
        };
        var run = PlanarSolve.Run(problem, report.Mesh, PlanarPorts.ResolveAll(report.Mesh, ports), [2e9], settings);
        foreach (var point in run.Points)
        {
            var y21 = FromS(point.S);
            double r = (-1.0 / y21).Real;
            Assert.InRange(r / (Rs * squares) - 1, -0.02, 0.02);
        }
        if (perfect) Assert.Contains(run.Notes, n => n.Contains("still carry their sheet resistance"));
    }

    private static Complex FromS(Mat<Complex> s)
    {
        // Y21 of a 50 Ω two-port.
        Complex d = (1 + s[0, 0]) * (1 + s[1, 1]) - s[0, 1] * s[1, 0];
        return -2 * s[1, 0] / (50.0 * d);
    }

    /// <summary>Gate 6's structural half — no film, no second Gram contribution and no extra table entry.</summary>
    [Fact]
    public void NoFilmLeavesTheGramAndTheTableAsTheyWere()
    {
        var problem = PlanarLineFixtures.GaAsLine(400e-6, 10e9);
        var mesh = SurfaceMesher.Mesh(problem, Mesh()).Mesh;
        var gram = PlanarGram.Build(mesh);
        Assert.Null(gram.Layer2);
        Assert.Null(gram.Value2);
        Assert.Equal(mesh.LayerNames.Count, PlanarConductorLoss.For(problem).SheetTable(mesh, 1e9).Length);
        Assert.Null(PlanarConductorLoss.TableFor(null, mesh, 1e9));
    }

    /// <summary>§4 — a rooftop straddling the boundary carries both classes in one slot, and only there.</summary>
    [Fact]
    public void ARooftopAcrossTheBoundaryHasTwoContributions()
    {
        var (problem, _) = Strip(3, 4.1e7, 3e-6);
        var mesh = SurfaceMesher.Mesh(problem, Mesh()).Mesh;
        var gram = PlanarGram.Build(mesh);
        Assert.NotNull(gram.Layer2);
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            int k = gram.Slot(b, b);
            bool straddles = mesh.Cells[mesh.Bases[b].CellA].Film != mesh.Cells[mesh.Bases[b].CellB].Film;
            Assert.Equal(straddles, gram.Value2![k] != 0);
        }
    }

    /// <summary>§6 — the film's dissipation is its own figure: ½R_s(L/W)I² for a uniform current, and never in the metal's.</summary>
    [Fact]
    public void PowerFilmIsItsOwnClosedForm()
    {
        const double squares = 3;
        var (problem, _) = Strip(squares, 4.1e7, 3e-6);
        var mesh = SurfaceMesher.Mesh(problem, Mesh()).Mesh;
        var current = new Complex[mesh.Bases.Count];
        for (int b = 0; b < mesh.Bases.Count; b++)
            if (mesh.Bases[b].Direction == PlanarBasisDirection.X)
                current[b] = mesh.Cells[mesh.Bases[b].CellA].Height / W;      // 1 A shared across the rows
        var inputs = new PlanarConductorLossInputs(PlanarConductorLoss.For(problem), PlanarGram.Build(mesh), null);
        var power = inputs.PowerOf(mesh, current.ToVector(), 1e9);
        Assert.InRange(power.FilmW / (0.5 * Rs * squares) - 1, -1e-9, 1e-9);
        Assert.True(power.SheetW < 1e-3 * power.FilmW, $"metal {power.SheetW} W beside film {power.FilmW} W");
        Assert.Equal(power.SheetW + power.BarrelW, power.TotalW);
    }

    /// <summary>Gate 7 — no run of merged cells crosses a class boundary, and merging does not move the resistance.</summary>
    [Fact]
    public void MergingRespectsTheClassAndDoesNotMoveTheResistance()
    {
        const double squares = 6;
        var (problem, ports) = Strip(squares);
        double R(bool merge)
        {
            var mesh = SurfaceMesher.Mesh(problem, Mesh(merge)).Mesh;
            return -1.0 / PlanarDcSolve.Solve(problem, mesh, PlanarPorts.ResolveAll(mesh, ports)).Y[0, 1].Real;
        }
        Assert.Equal(R(false), R(true), 9);

        // The notch's fans cross the whole patch, which is what merging is for, and the film's lower edge lies
        // among those fine rows: a run along y through them would cross it unless the class stops it.
        EmPoint P(double x, double y) => new(x * 1e-3, y * 1e-3);
        var patch = new PlanarPolygon(new List<EmPoint>
        {
            P(0, 0), P(4, 0), P(4, 3), P(4.5, 3), P(4.5, -5), P(5.5, -5),
            P(5.5, 3), P(6, 3), P(6, 0), P(10, 0), P(10, 10), P(0, 10),
        });
        var film = PlanarLineFixtures.Rect(1e-3, 3.05e-3, 3e-3, 8e-3);
        var layer = new PlanarConductorLayer("Metal1", [patch], 5.8e7, 35e-6)
        {
            Films = [new PlanarFilmRegion("Resistor", [film], Rs)],
        };
        var fanned = new PlanarProblem([layer], GroundedSlab.Fr4Starter, 5e9);
        var merged = SurfaceMesher.Mesh(fanned, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10,
            EdgeMesh: true, EdgeCells: 2, MinCellsAcrossConductor: 2, RooftopMerging: true)).Mesh;
        Assert.NotNull(merged.Merge);
        Assert.True(merged.Merge!.MergedRunCount > 0, "this fixture must merge something, or the test says nothing");
        for (int b = 0; b < merged.Bases.Count; b++)
            if (merged.Cells[merged.Bases[b].CellA].Film != merged.Cells[merged.Bases[b].CellB].Film)
                Assert.True(merged.Merge.IsSolvedFace(b), $"basis {b} crosses the film boundary inside a merged run");
    }

    /// <summary>Gate 8's engine half — a port may not sit on film, and the refusal names both.</summary>
    [Fact]
    public void APortOnFilmIsRefusedByName()
    {
        double l = 3 * W;
        var outline = PlanarLineFixtures.Rect(0, -W / 2, Contact + l, W / 2);
        var film = PlanarLineFixtures.Rect(Contact, -W / 2, Contact + l, W / 2);
        var layer = new PlanarConductorLayer("Metal1", [outline], 4.1e7, 3e-6)
        {
            Films = [new PlanarFilmRegion("Resistor", [film], Rs)],
        };
        var problem = new PlanarProblem([layer], GroundedSlab.GaAsStarter, 5e9);
        var mesh = SurfaceMesher.Mesh(problem, Mesh()).Mesh;
        var onFilm = new PlanarPort(2, new EmPoint(Contact + l, 0), PlanarPortSide.MaxX, 50.0);
        Assert.False(PlanarPorts.TryResolve(mesh, onFilm, out _, out string? refusal));
        Assert.Contains("Port 2", refusal);
        Assert.Contains("Resistor", refusal);
    }
}
