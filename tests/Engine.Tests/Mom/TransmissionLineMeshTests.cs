// M2 — the transmission-line mesh, gated on the MESH and nothing else.
//
// THE BUG THIS SETTING EXISTS FOR, stated once so no future reader has to reconstruct it: the
// transverse pitch was min(λ_g/CellsPerWavelength, narrowest/MinCellsAcrossConductor) — the SAME min
// in both axes — so on any artwork whose metal is narrower than a λ cell the geometry term won in
// BOTH directions and Cells per wavelength and Mesh frequency were structurally INERT. Not "less
// effective than expected": unable to change the mesh at all, at any value. Reproduced on the
// owner's own connector cutout at N = 3,521 for cells/λ = 20, 10 and 5, and for mesh frequencies of
// 10 GHz and 100 MHz, alike.
//
// The first test below is that bug, written so it FAILS if the two settings ever go back to
// competing. Every other test here is a safety property of the fix.
//
// No solve anywhere: a pitch is a mesh observable.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class TransmissionLineMeshTests
{
    private const double W = 200e-6, Arm = 5e-3;

    /// <summary>A 200 µm line 10 mm long. <b>This fixture does NOT show the defect</b>, and that is
    /// worth knowing: a straight x-directed line is 10 mm long measured along x, so
    /// <c>narrowX/4</c> = 2.5 mm and the λ cap DOES bind in x. The bug needs artwork that is narrow
    /// in BOTH axes — see <see cref="Bend"/>.</summary>
    private static PlanarProblem Trace(double fHz = 10e9) =>
        PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, W, 10e-3, fHz);

    /// <summary>
    /// An east-then-north right-angle bend, which is the owner's own geometry in miniature and the
    /// fixture the defect actually needs: it is 200 µm narrow measured along x AND along y, so
    /// <c>min(λ_g/cells, narrowest/across)</c> takes the geometry term in both directions and the two
    /// wavelength controls cannot move the mesh at all.
    /// </summary>
    private static PlanarProblem Bend(double fHz = 10e9) =>
        PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, fHz,
            new PlanarPolygon([
                new EmPoint(0, -0.5 * W), new EmPoint(Arm + 0.5 * W, -0.5 * W),
                new EmPoint(Arm + 0.5 * W, Arm), new EmPoint(Arm - 0.5 * W, Arm),
                new EmPoint(Arm - 0.5 * W, 0.5 * W), new EmPoint(0, 0.5 * W)]));

    private static PlanarPort[] BendPorts() =>
    [
        new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50),
        new PlanarPort(2, new EmPoint(Arm, Arm), PlanarPortSide.MaxY, 50),
    ];

    private static PlanarPort[] EndPorts() =>
    [
        new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50),
        new PlanarPort(2, new EmPoint(10e-3, 0), PlanarPortSide.MaxX, 50),
    ];

    private static PlanarMeshSettings At(int cellsPerWavelength, bool tline) =>
        new(Auto: false, CellsPerWavelength: cellsPerWavelength,
            CurrentModel: tline ? PlanarCurrentModel.TransmissionLine : PlanarCurrentModel.None);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE BUG
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WithItOn_CellsPerWavelength_ActuallyChangesTheMesh()
    {
        var p = Bend();

        // Off, the geometry term wins in both axes and the knob is inert. ASSERTED AS AN EQUALITY,
        // because it is the behaviour the setting exists to escape: if this ever stops being true
        // the setting has become unnecessary, and that is worth being told rather than discovering.
        int off20 = SurfaceMesher.Mesh(p, At(20, false)).UnknownCount;
        int off5  = SurfaceMesher.Mesh(p, At(5,  false)).UnknownCount;
        Assert.Equal(off20, off5);

        int on20 = SurfaceMesher.Mesh(p, At(20, true), ports: BendPorts()).UnknownCount;
        int on5  = SurfaceMesher.Mesh(p, At(5,  true), ports: BendPorts()).UnknownCount;

        Assert.True(on5 < on20,
            $"with the transmission-line mesh on, coarsening cells/λ from 20 to 5 must reduce the " +
            $"unknown count: {on20} -> {on5}");
        Assert.True(on20 <= off20,
            $"and it must never cost more than the per-axis rule: {off20} -> {on20}");
    }

    [Fact]
    public void WithItOn_MeshFrequency_ActuallyChangesTheMesh()
    {
        // The same defect through the other knob — the cap is the PRODUCT f × cells/λ and nothing
        // else, so the two were inert together and they come back together.
        var p = Bend(20e9);

        // ANT-2 left this equality alone, and that is worth a line because it very nearly did not.
        // The detail floor is λ-relative and taken at the MESH FREQUENCY, so lowering the mesh
        // frequency raises it — which is a second, indirect route by which Mesh frequency can move a
        // mesh the per-axis rule would otherwise pin. It is inert on THIS fixture only because the
        // floor is also capped at CapMinFractionOfExtent of the artwork's own extent. On artwork
        // large enough for the λ term to bind, expect this equality to be a strict inequality, and
        // that is the floor working rather than this test rotting.
        int offTop = SurfaceMesher.Mesh(p, At(20, false)).UnknownCount;
        int offLow = SurfaceMesher.Mesh(p, At(20, false) with { MeshFrequencyHz = 1e9 }).UnknownCount;
        Assert.Equal(offTop, offLow);

        int onTop = SurfaceMesher.Mesh(p, At(20, true), ports: BendPorts()).UnknownCount;
        int onLow = SurfaceMesher.Mesh(p, At(20, true) with { MeshFrequencyHz = 1e9 },
                                       ports: BendPorts()).UnknownCount;
        Assert.True(onLow < onTop, $"lowering the mesh frequency must lower N: {onTop} -> {onLow}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The safety properties
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(20)]
    [InlineData(10)]
    [InlineData(5)]
    public void NoPitchIsEverFinerThanThePerAxisRuleWouldHaveUsed(int cellsPerWavelength)
    {
        // THE STRUCTURAL GUARANTEE, and it is what makes the setting safe to reach for: every pitch
        // the field asks for is floored at the one the per-axis rule would have used. Without it a
        // rim sliver in the local width estimate drove a plain 10 mm line to 2.27 MILLION cells —
        // a setting a user turned on to make the mesh smaller made it 11,000× bigger.
        //
        // IT IS ASSERTED ON THE PITCH, NOT ON THE CELL COUNT, and the difference is a real finding
        // rather than a weakening. A coarser bulk gives the graded edge fan FURTHER to climb, and
        // the fan's ratio is clamped at 3× per cell, so each attractor costs about 1.5 extra cells
        // when the bulk goes from ~92 µm to ~476 µm. On artwork with many attractors and little
        // bulk — the 8-segment taper here — that outweighs the bulk saving and the cell count rises
        // slightly (measured: 728 → 858 / 805 / 741 at cells/λ 20 / 10 / 5). The trade is real, it
        // is the same one the edge mesh always made, and the mesher reports it.
        foreach (var p in new[]
        {
            Trace(),
            Bend(),
            PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 2.9e-3, 20e-3, 10e9),
            PlanarLineFixtures.Line(GroundedSlab.GaAsStarter, 72e-6, 2e-3, 20e9),
            PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 0.5e-3, 20e-3, 10e9),
        })
        {
            var off = SurfaceMesher.Mesh(p, At(cellsPerWavelength, false));
            var on  = SurfaceMesher.Mesh(p, At(cellsPerWavelength, true));

            // The BULK is what the field governs; the edge fan's own cells are smaller by design in
            // both meshes. So the comparison is of the largest cell each mesh contains, which is the
            // bulk pitch and nothing else.
            Assert.True(on.MaxCellEdgeM >= off.MaxCellEdgeM * 0.999,
                $"the transmission-line mesh produced a FINER bulk cell than the per-axis rule: " +
                $"{off.MaxCellEdgeM * 1e6:G4} µm -> {on.MaxCellEdgeM * 1e6:G4} µm");

            // And it may not run away in cell count either — the fan trade above is a few per cent,
            // not a factor.
            Assert.True(on.CellCount <= off.CellCount * 1.25,
                $"cell count {off.CellCount} -> {on.CellCount}");
        }
    }

    [Fact]
    public void ItIsOffByDefault_AndOffIsBitIdenticalToNotHavingIt()
    {
        // Every number in this directory's HISTORY.md was taken without it, and a default that
        // changed them would make them unreproducible. Asserted on the GRID rather than on N,
        // because two different meshes can share an unknown count.
        Assert.Equal(PlanarCurrentModel.None, PlanarMeshSettings.Default.CurrentModel);
        Assert.Equal(PlanarCurrentModel.None, PlanarMeshSettings.Default.Resolved.CurrentModel);

        var p = Trace();
        var a = SurfaceMesher.Mesh(p, At(20, false));
        var b = SurfaceMesher.Mesh(p, At(20, false), ports: EndPorts());

        // Handing the mesher ports must change nothing while it is off — the panel and the run reach
        // the mesher from different places and one of them has no ports yet.
        Assert.Equal(a.Mesh.GridX, b.Mesh.GridX);
        Assert.Equal(a.Mesh.GridY, b.Mesh.GridY);
        Assert.Equal(a.UnknownCount, b.UnknownCount);
    }

    [Fact]
    public void ItSurvivesAuto_LikeEveryControlAddedAfterTheFirstThree()
    {
        // A number the user typed must not be discarded because a checkbox is ticked — and this one
        // above all, since it exists because a setting was being silently ignored.
        var s = PlanarMeshSettings.Default with
            { Auto = true, CurrentModel = PlanarCurrentModel.TransmissionLine };
        Assert.Equal(PlanarCurrentModel.TransmissionLine, s.Resolved.CurrentModel);
    }

    /// <summary>The finest grid step anywhere inside [lo, hi] of one axis.</summary>
    private static double FinestStepIn(IReadOnlyList<double> lines, double lo, double hi)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < lines.Count; i++)
        {
            double a = lines[i - 1], b = lines[i];
            if (b < lo || a > hi) continue;
            best = Math.Min(best, b - a);
        }
        return best;
    }

    [Fact]
    public void ItFollowsABend_RatherThanAveragingTheTwoArms()
    {
        // THE OWNER'S OWN REQUIREMENT: "make sure to follow the bends of the geometry to guess which
        // way the current is going." An east-then-north L-bend has no single direction — its two
        // ports state 45°, and so does its area moment, which is the direction of neither arm. The
        // pitch is therefore measured PER POINT, and the test is that the two axes come out
        // DIFFERENT from each other on a straight line and SYMMETRIC on a bend that is itself
        // symmetric under swapping x and y.
        const double w = 200e-6, arm = 5e-3;
        var bend = PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 10e9,
            new PlanarPolygon([
                new EmPoint(0, -0.5 * w), new EmPoint(arm + 0.5 * w, -0.5 * w),
                new EmPoint(arm + 0.5 * w, arm), new EmPoint(arm - 0.5 * w, arm),
                new EmPoint(arm - 0.5 * w, 0.5 * w), new EmPoint(0, 0.5 * w)]));

        var off = SurfaceMesher.Mesh(bend, At(20, false));
        var on  = SurfaceMesher.Mesh(bend, At(20, true), ports: BendPorts());

        // IT MUST SAVE. A bend is the case a single global direction cannot handle at all, so if
        // following the bend bought nothing there would be no reason to have built a field.
        Assert.True(on.CellCount < off.CellCount,
            $"the bend did not get cheaper: {off.CellCount} -> {on.CellCount}");

        // AND EACH ARM MUST KEEP ITS OWN TRANSVERSE RESOLUTION — which is the actual requirement,
        // and is NOT a cell count. A mesh that had averaged the two arms into one 45° direction
        // would coarsen each of them along its own transverse axis, and that shows up here and
        // nowhere else: the y grid must stay fine across the HORIZONTAL arm, and the x grid must
        // stay fine across the VERTICAL one. Asserted against the pitch the per-axis rule itself
        // uses, so this is "no worse than before" rather than a number pulled out of the air.
        double transverse = 200e-6 / PlanarMeshSettings.DefaultMinCellsAcrossConductor;

        double acrossHorizontalArm = FinestStepIn(on.Mesh.GridY, -0.5 * W, 0.5 * W);
        Assert.True(acrossHorizontalArm <= transverse * 1.01,
            $"the y grid across the horizontal arm is {acrossHorizontalArm * 1e6:G4} µm, coarser " +
            $"than the {transverse * 1e6:G4} µm the conductor's own width asks for — the two arms " +
            "have been averaged into one direction instead of followed.");

        double acrossVerticalArm = FinestStepIn(on.Mesh.GridX, Arm - 0.5 * W, Arm + 0.5 * W);
        Assert.True(acrossVerticalArm <= transverse * 1.01,
            $"the x grid across the vertical arm is {acrossVerticalArm * 1e6:G4} µm, coarser than " +
            $"the {transverse * 1e6:G4} µm the conductor's own width asks for.");

        // …and it says what it did. A control that acts on a guess must report the guess.
        Assert.Contains(on.Notes, n => n.Contains("Transmission-line mesh ON", StringComparison.Ordinal));
    }

    [Fact]
    public void OnAStraightLine_TheTwoAxesAreGovernedByDifferentSettings()
    {
        // The owner's own statement of the requirement: for a line running east-west, Cells across
        // is a north-south setting and Cells per wavelength is an east-west one. So changing ONE of
        // them must move ONE axis and leave the other alone.
        // A 50 mm line, not the 10 mm one: on a short line the two edge fans are most of the x grid
        // (about 10 of its 15 lines), so the bulk pitch cannot show through and this test would
        // measure the fan. That is worth stating rather than quietly picking a longer fixture.
        var p = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, W, 50e-3, 10e9);
        PlanarPort[] ends =
        [
            new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50),
            new PlanarPort(2, new EmPoint(50e-3, 0), PlanarPortSide.MaxX, 50),
        ];
        var baseline      = SurfaceMesher.Mesh(p, At(20, true), ports: ends);
        var coarserAlong  = SurfaceMesher.Mesh(p, At(5, true), ports: ends);
        var coarserAcross = SurfaceMesher.Mesh(
            p, At(20, true) with { MinCellsAcrossConductor = 1 }, ports: ends);

        // Cells per wavelength is an EAST-WEST setting on an east-west line: it moves x and leaves y
        // exactly alone. That "exactly" is the orthogonality claim, and it is the whole request.
        Assert.True(coarserAlong.Mesh.GridX.Count < baseline.Mesh.GridX.Count,
            $"cells/λ must move the ALONG (x) grid: {baseline.Mesh.GridX.Count} -> " +
            $"{coarserAlong.Mesh.GridX.Count}");
        Assert.Equal(baseline.Mesh.GridY.Count, coarserAlong.Mesh.GridY.Count);

        // …and Cells across is a NORTH-SOUTH setting: it moves y, and does not move x.
        Assert.True(coarserAcross.Mesh.GridY.Count < baseline.Mesh.GridY.Count,
            $"Cells across must move the ACROSS (y) grid: {baseline.Mesh.GridY.Count} -> " +
            $"{coarserAcross.Mesh.GridY.Count}");
    }

    [Fact]
    public void MovingTheArtwork3Point7mm_LeavesTheMeshUnchanged()
    {
        // L8b's own knife edge, and the pitch field is exactly the class of change that reintroduces
        // it: a column-wise minimum is a STEP function, and a discontinuous size field puts the
        // marcher's landing point on a floating-point comparison. The field is Lipschitz-smoothed at
        // the edge fan's own growth ratio for this reason, and this is the assertion that holds it.
        const double d = 3.7e-3, w = 200e-6, len = 10e-3;
        var at0 = SurfaceMesher.Mesh(Trace(), At(20, true));
        var moved = SurfaceMesher.Mesh(
            PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 10e9,
                PlanarLineFixtures.Rect(d, d - 0.5 * w, d + len, d + 0.5 * w)),
            At(20, true));

        Assert.Equal(at0.CellCount, moved.CellCount);
        Assert.Equal(at0.Mesh.GridX.Count, moved.Mesh.GridX.Count);
        Assert.Equal(at0.Mesh.GridY.Count, moved.Mesh.GridY.Count);
        for (int i = 0; i < at0.Mesh.GridX.Count; i++)
            Assert.Equal(at0.Mesh.GridX[i], moved.Mesh.GridX[i] - d, 15);
    }

    [Fact]
    public void TheAspectCapBindsOnTheBULKPITCH_WhichIsTheOnlyThingItCanBind()
    {
        // A 60:1 cell already ships on a 200 µm × 50 mm trace (§0c), so a high aspect is not new —
        // an UNBOUNDED one is, and the fill's τ binning and SingularExtraction were measured on
        // moderate aspects. The owner's own setup is the case that needs the cap: cells/λ = 5 at a
        // mesh frequency of 100 MHz puts the λ cell at ~0.37 m against 360 µm of metal, an aspect of
        // roughly 4,000:1.
        //
        // IT IS ASSERTED ON THE FIELD, NOT ON THE REALISED CELLS, and that is not a weaker test —
        // it is the only correct one. A tensor grid's cell aspect is the product of two INDEPENDENT
        // axis fields, so a cell where a coarse x column crosses an edge-fan y row can exceed any
        // bulk ratio, and did before this setting existed. What the cap can and must govern is the
        // BULK pitch the field asks for; measured on the realised cells, the same fixture reads
        // 745:1 with the cap working perfectly, because the fan's own c₀ row is 6 µm tall.
        var p = Trace();
        double lambdaG = (p with { MaxFrequencyHz = 1e8 }).GuidedWavelengthM;
        double hWave = lambdaG / 20;

        var field = PlanarMeshPitchField.Build(
            p, hWave, PlanarMeshSettings.DefaultMinCellsAcrossConductor, 2.0,
            floorX: 50e-6, floorY: 50e-6, ports: EndPorts());

        Assert.True(field.Ok);
        Assert.True(field.Capped, "this fixture is chosen because the cap must actually bind on it.");
        Assert.True(field.WorstAspect <= PlanarMeshSettings.MaxCellAspect * 1.001,
            $"the field asked for {field.WorstAspect:G4}:1, past the " +
            $"{PlanarMeshSettings.MaxCellAspect:G3}:1 cap.");
    }
}
