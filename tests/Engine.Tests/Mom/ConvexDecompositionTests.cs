// brief-convex-decomposition.md — M1's own gates (Route A: the predicate, not a decomposition).
//
// M0 measured 1,158 refused cells over three shipping PCells × two starters × three densities and
// found 1,152 of them flow-simple in BOTH directions, 6 in exactly one and ZERO in neither — §2's
// outcome 1, which makes this a predicate swap and makes Route B's decomposition unnecessary. The
// table itself is in Ui.Tests (the PCells live in src/Ui); what is here is the structural half:
//
//   R-cvx-5  the sliver merge must still pass the new predicate — the strongest available regression
//            check on it, because merged cells are non-convex TODAY and fill correctly TODAY;
//   R-cvx-7  a cell is still ONE cell. Route A admits cells rather than splitting them, so the
//            invariant to hold is "no subdivision", and N moving is a consequence to be REPORTED;
//   §5       every basis the mesher emits must have a flow-simple support, which is the property the
//            strip construction is right under.

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class ConvexDecompositionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static PlanarProblem Disc(int points = 96, double radiusM = 1.45e-3)
    {
        var ring = new EmPoint[points];
        for (int i = 0; i < points; i++)
        {
            double a = 2.0 * Math.PI * i / points;
            ring[i] = new EmPoint(radiusM * Math.Cos(a), radiusM * Math.Sin(a));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9);
    }

    private static PlanarProblem Taper()
        => new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, -1.45e-3), new EmPoint(10e-3, -0.5e-3),
                                      new EmPoint(10e-3, 0.5e-3), new EmPoint(0, 1.45e-3)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, 10e9);

    /// <summary>A rim whose CURVATURE is concave — the case M1 admits and the pre-M1 predicate did
    /// not. See <c>ConformalBasisTests.ConcaveArc</c> for why this shape and not a notch.</summary>
    private static PlanarProblem ConcaveArc(double len = 10e-3, int stations = 64)
    {
        var ring = new List<EmPoint> { new(0, 0), new(len, 0) };
        for (int i = stations; i >= 0; i--)
        {
            double x = len * i / stations;
            ring.Add(new EmPoint(x, 2.4e-3 + 0.5e-3 * (x / len) * (x / len)));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9);
    }

    private static PlanarMeshSettings At(int cpw) =>
        new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: true, EdgeCells: 3,
            BoundaryCells: PlanarBoundaryCells.Conformal);

    public static TheoryData<string> Parts() => new() { "disc", "taper", "arc" };
    private static PlanarProblem PartNamed(string n) =>
        n switch { "disc" => Disc(), "arc" => ConcaveArc(), _ => Taper() };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cvx-5 — THE SLIVER MERGE MUST ANSWER "FLOW-SIMPLE", and if it does not the predicate is wrong
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A merged cell is non-convex today and passed only because it was never asked; after M1 it IS
    /// asked, on every basis across it. <b>If the new predicate refuses a merged cell the predicate is
    /// wrong</b>, because those cells demonstrably fill correctly today — R-cvx-5, and it is the
    /// strongest regression check available on the swap.
    ///
    /// <para>It holds for a reason rather than by luck: a sliver is absorbed into the neighbour it
    /// shares its largest FACE with, so the union meets any line through it in one interval.</para>
    /// </summary>
    [Theory]
    [InlineData(130)]
    [InlineData(250)]
    public void M1_TheSliverMergeStillPassesTheNewPredicate(int cpw)
    {
        var rep = SurfaceMesher.Mesh(Disc(), At(cpw));
        Assert.True(rep.MergedSliverCount > 0,
            $"cells/λ = {cpw} merged no sliver, so this asserts over an empty set");

        int merged = 0;
        foreach (var cell in rep.Mesh.Cells)
        {
            if (cell.Region is not { Merged: true }) continue;
            merged++;
            double tol = 1e-9 * Math.Min(cell.Width, cell.Height);
            Assert.True(RooftopSupport.IsFlowSimple(cell.Region, alongX: true, tol),
                $"a MERGED cell at ({cell.IX},{cell.IY}) is not x-flow-simple");
            Assert.True(RooftopSupport.IsFlowSimple(cell.Region, alongX: false, tol),
                $"a MERGED cell at ({cell.IX},{cell.IY}) is not y-flow-simple");
        }

        _out.WriteLine($"cells/λ {cpw}: {rep.MergedSliverCount} merge(s), {merged} merged cell(s), " +
                       "every one flow-simple in both directions");

        // The two counts are not equal and should not be: MergedSliverCount counts ABSORPTIONS and one
        // host can absorb up to four (measured: 32 merges into 28 hosts at cells/λ = 250). What must
        // hold is that every absorption produced a host carrying the Merged flag.
        Assert.InRange(merged, 1, rep.MergedSliverCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 — every basis the mesher emits has a flow-simple support
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(Parts))]
    public void M1_EveryEmittedBasisHasAFlowSimpleSupport(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), At(20)).Mesh;

        int cut = 0;
        foreach (var basis in mesh.Bases)
        {
            if (basis.Direction == PlanarBasisDirection.Z) continue;
            var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
            if (mesh.Cells[basis.CellA].IsCut || mesh.Cells[basis.CellB].IsCut) cut++;

            // A support that is not flow-simple spans the gap between two runs of metal, which is
            // route (c)'s own sin: source integrated where there is no conductor.
            Assert.True(sa.FlowSimple && sb.FlowSimple,
                $"{part}: a basis was emitted over a support that is not flow-simple");
        }

        _out.WriteLine($"{part}: {mesh.Bases.Count} bases, {cut} with a cut half, all flow-simple");
        Assert.True(cut > 0, $"{part}: no basis has a cut half — the fixture proves nothing");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cvx-7 — A CELL IS STILL ONE CELL, and what that gate has to be for Route A
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§7 gate 5 asks for N as an EQUALITY against the pre-M1 conformal counts, and that gate is
    /// written for Route B. It does not hold for Route A, and asserting it would be asserting
    /// something Route A does not claim.</b>
    ///
    /// <para>The gate exists so that a decomposition cannot become a SUBDIVISION — more pieces inside
    /// one cell must not become more cells, or the rooftop pairing L8c's fill and L9c's via basis both
    /// assume is broken. Route A never splits a cell; it changes which cells EXIST, because a cell the
    /// old predicate staircased was rounded to whole-or-absent and is now cut. So grid positions that
    /// held no cell can now hold one, and the adjacency count moves with them.</para>
    ///
    /// <para><b>Measured on the shipping PCells (PCB starter, cells/λ = 20): 547 / 704 / 745 → 761 /
    /// 579 → 577.</b> MBend and MTaper are unmoved; MKlopf on-axis gains 16 unknowns and MKlopf Offset
    /// loses 2. Reported in the Ui-side G1 table rather than pinned here.</para>
    ///
    /// <para>What IS asserted is the invariant the gate is actually for: <b>at most one cell per
    /// (layer, IX, IY)</b>, so nothing was subdivided — plus the pairing contract, that every basis
    /// joins two DISTINCT cells exactly once.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Parts))]
    public void M1_NoCellWasSubdivided_AndTheRooftopPairingIsUnchanged(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), At(20)).Mesh;

        var seen = new HashSet<(int, int, int)>();
        foreach (var c in mesh.Cells)
            Assert.True(seen.Add((c.LayerIndex, c.IX, c.IY)),
                $"{part}: grid position ({c.IX},{c.IY}) holds more than one cell — the change has " +
                "become a subdivision, which grows the unknown count and breaks the rooftop pairing");

        var pairs = new HashSet<(int, int, PlanarBasisDirection)>();
        foreach (var b in mesh.Bases)
        {
            Assert.NotEqual(b.CellA, b.CellB);
            Assert.True(pairs.Add((Math.Min(b.CellA, b.CellB), Math.Max(b.CellA, b.CellB), b.Direction)),
                $"{part}: two bases join the same cell pair in the same direction");
        }

        _out.WriteLine($"{part}: {mesh.Cells.Count} cells on {seen.Count} distinct grid positions, " +
                       $"{mesh.Bases.Count} bases on {pairs.Count} distinct pairs");
    }
}
