// A PORT ON A CONFORMAL FEED — the bug this file exists for, and the measurement that bounds the fix.
//
// Reported 2026-08-11, on the simplest possible planar setup: one MKlopf taper on the PCB starter,
// a port label at each end, Boundary cells = Conformal. Every run ended in
//
//     "Port 1 lands on a CONFORMAL boundary cell … Put the port on a straight, axis-aligned feed …
//      or set Boundary cells back to Staircase for this run."
//
// **The refusal's own premise was false.** Its comment reads "a port belongs on a drawn feed, which
// is Manhattan, so this should never fire" — but a taper's flanks are oblique from its very first
// cell, so the outermost cell of the port's transverse run is cut on MKlopf and MTaper alike, and
// the refusal fired on the parts a user is most likely to select. Conformal boundary cells were
// therefore unusable end to end, on exactly the geometry they exist for.
//
// It lives in Ui.Tests because MKlopfPCell is in src/Ui, and because the claim being gated is about
// the PRODUCT path (drawn artwork → extractor → port inference → resolution), not about the kernel.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Em;

public class ConformalPortTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The reported setup, verbatim: Z1 = 50, Z2 = 12, Γmax = 0.05, L = 50.8 mm,
    /// Offset = 10.16 mm, SmoothSteps = 1, with a port label at each end of the taper.</summary>
    private static List<LayoutShape> ReportedSetup()
    {
        Assert.True(PCellRegistry.TryGet("MKLOPF", out var gen));
        var map = new Dictionary<string, PCellValue>(StringComparer.Ordinal)
        {
            ["Z1"] = PCellValue.Real(50), ["Z2"] = PCellValue.Real(12),
            ["GammaMax"] = PCellValue.Real(0.05), ["L"] = PCellValue.Real(0.0508),
            ["Offset"] = PCellValue.Real(0.01016), ["SmoothSteps"] = PCellValue.Real(1),
        };
        var shapes = new List<LayoutShape>(
            gen(map, StarterTechnologies.Pcb2Layer(), PCellLayerSelection.Default).Shapes);

        shapes.Add(new LabelShape { Layer = new(1, 0), X = 0, Y = 0,
                                    Text = "P1", Height = 1_016_000, IsPort = true });
        shapes.Add(new LabelShape { Layer = new(1, 0), X = 50_800_000, Y = 10_160_000,
                                    Text = "P2", Height = 1_016_000, IsPort = true });
        return shapes;
    }

    private static PlanarMeshSettings At(PlanarBoundaryCells cells, int cpw = 20) =>
        new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: true, EdgeCells: 3, BoundaryCells: cells);

    private static (PlanarProblem Problem, IReadOnlyList<PlanarPort> Ports) Extract()
    {
        var shapes = ReportedSetup();
        var x = PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 20e9);
        Assert.True(x.Ok, x.Refusal);
        var pe = EmPortExtraction.Extract(shapes, x.Problem!, Dbu);
        Assert.True(pe.Ok, pe.Refusal);
        return (x.Problem!, pe.Ports);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The bug: it must RESOLVE, on the reported setup, with conformal cells on
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheReportedMKlopfSetup_ResolvesBothPortsWithConformalCellsOn()
    {
        var (problem, ports) = Extract();
        var mesh = SurfaceMesher.Mesh(problem, At(PlanarBoundaryCells.Conformal)).Mesh;

        foreach (var port in ports)
        {
            bool ok = PlanarPorts.TryResolve(mesh, port, out var res, out string? refusal);
            Assert.True(ok, $"port {port.Number} was refused: {refusal}");
            Assert.True(res!.BasisCount > 1, $"port {port.Number} resolved onto {res.BasisCount} basis");
            Console.WriteLine(res.Describe());
        }
    }

    /// <summary>
    /// The staircase must be BIT-IDENTICAL through the same code, or the fix has moved a number a
    /// user already recorded. Every quantity the calibration and the report read, as equalities.
    /// </summary>
    [Fact]
    public void TheStaircasedPortIsUnMOVED_BitForBit()
    {
        var (problem, ports) = Extract();
        var mesh = SurfaceMesher.Mesh(problem, At(PlanarBoundaryCells.Staircase)).Mesh;

        foreach (var port in ports)
        {
            var r = PlanarPorts.Resolve(mesh, port);

            // The two paths through the new code must collapse onto L8d's own arithmetic here: one
            // subtraction for the width, the gridlines copied verbatim, and no conformal note.
            Assert.Equal(r.GridWidthM, r.WidthM);
            Assert.Equal(0, r.CutCellCount);
            Assert.Equal(0.0, r.UndrivenMetalM);
            Assert.DoesNotContain("follow the metal", r.Describe(), StringComparison.Ordinal);

            var g = r.Direction == PlanarBasisDirection.X ? mesh.GridY : mesh.GridX;
            int lo = g.ToList().FindIndex(v => v == r.TransverseLines[0]);
            Assert.True(lo >= 0, "the transverse lines are no longer the grid's own");
            for (int i = 0; i < r.TransverseLines.Count; i++)
                Assert.Equal(g[lo + i], r.TransverseLines[i]);
            Assert.Equal(g[lo + r.BasisCount] - g[lo], r.WidthM);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE COST OF THE FIX, MEASURED — and it is not zero
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>What a conformal port does WORSE than a staircased one, as a number rather than a caveat.</b>
    ///
    /// <para>R-cut-4's <c>Anchored</c> test is all-or-nothing over a support's strips, and a shallow
    /// oblique rim leaves a sliver strip at the top of the outermost cell whose metal does not reach
    /// the shared face. The whole rooftop is declined for it, so the port drives one fewer cell at
    /// each end of its transverse run than the metal has. The port is no longer REFUSED — that is the
    /// fix — but the width it drives is short by that much, and it is reported rather than absorbed.</para>
    ///
    /// <para><b>Measured on the reported setup (2026-08-11): port 1's undriven fraction is 17.3% at
    /// cells/λ = 20, 11.4% at 40 and 5.0% at 80; port 2's is 2.1% / 1.2% / 0.8%.</b> Port 1 is the
    /// narrow 50 Ω end — 7 cells across at the shipping density — which is why it is the one that
    /// hurts. The driven width converges upward on the drawn 3.0 mm (2.483 → 2.660 → 2.849 mm) and
    /// passes the staircase's own 2.837 mm by cells/λ = 80.</para>
    ///
    /// <para><b>The concrete way to close it, deliberately NOT taken here:</b> accept a nearly-swept
    /// support instead of refusing it. The unswept strip is ~0.56% of that cell's area and the
    /// resulting basis carries 0.994 A instead of 1.000 A — against losing 12.5% of the port's width
    /// by dropping it. That trade looks overwhelming, and it is still a separate act: it retires L8c's
    /// EXACT ∫f·û dℓ = 1 A (gated at machine precision by <c>ConformalBasisTests.B3</c>) in favour of
    /// a bounded one, which needs its own measurement of what the deficit does to an s-parameter.</para>
    /// </summary>
    [Fact]
    public void TheUndrivenMetalAtAConformalPort_IsReported_AndFallsWithRefinement()
    {
        var (problem, ports) = Extract();

        Console.WriteLine("  cells/λ  port  bases   driven width   undriven      undriven %");
        var byPort = new Dictionary<int, List<double>>();

        foreach (int cpw in new[] { 20, 40, 80 })
        {
            var mesh = SurfaceMesher.Mesh(problem, At(PlanarBoundaryCells.Conformal, cpw)).Mesh;
            foreach (var port in ports)
            {
                var r = PlanarPorts.Resolve(mesh, port);
                double frac = r.UndrivenMetalM / (r.UndrivenMetalM + r.WidthM);
                Console.WriteLine($"  {cpw,7}  {r.Number,4}  {r.BasisCount,5}   {r.WidthM:E4}   " +
                                  $"{r.UndrivenMetalM:E4}     {frac:P2}");
                (byPort.TryGetValue(r.Number, out var l) ? l : byPort[r.Number] = []).Add(frac);
            }
        }

        foreach (var (number, fracs) in byPort)
        {
            // NON-VACUITY: if nothing is undriven anywhere, this fixture stopped exercising the
            // limitation and the trend below is a trend in zeros.
            Assert.True(fracs[0] > 0, $"port {number} has no undriven metal at cells/λ = 20");

            // The claim that makes it a mesh-refinement instruction rather than a permanent defect.
            Assert.True(fracs[^1] < fracs[0],
                $"port {number}: refining did not shrink the undriven fraction " +
                $"({fracs[0]:P2} → {fracs[^1]:P2}), so it is not an outermost-cell effect and the " +
                "note's own advice is wrong");
        }
    }

    /// <summary>
    /// <b>A port whose own cells ARE cut and DO carry rooftops — and the measurement that says its
    /// width is STILL the grid extent.</b>
    ///
    /// <para>The obvious worry about a cut cell at a port is that its shared edge is shorter than the
    /// gridline, so the port is narrower than it reports. <b>On a pair that survives R-cut-4 it is
    /// not, and the reason is structural rather than lucky:</b> the face is short only where the
    /// cell's metal is absent over a transverse band, and for a monotone rim that same band leaves one
    /// of the two halves un-swept by the shared edge — so the pair was already refused and is not in
    /// the run. A slanted END CAP is the clean case: the cut is on the OUTER face, both halves stay
    /// anchored, and the shared edge is fully metal.</para>
    ///
    /// <para>So the width-from-metal path exists and is correct, and this fixture shows the condition
    /// under which it collapses onto L8d's own arithmetic — which is why the resolution branches on
    /// the DIFFERENCE rather than on "is anything cut", and why a conformal port is bit-identical to a
    /// staircased one in everything except which cells carry a rooftop.</para>
    /// </summary>
    [Fact]
    public void APortOnASlantedEnd_TakesItsWidthFromTheMetal_AndTheStandardFollows()
    {
        // A 12 × 2.9 mm line whose MinX end slants across the first ~2 mm, so the slant genuinely
        // crosses the port's own reference-plane column rather than dying out before it.
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [new PlanarPolygon([new EmPoint(2.0e-3, 0), new EmPoint(12e-3, 0),
                                    new EmPoint(12e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
                5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9);

        var mesh = SurfaceMesher.Mesh(problem, At(PlanarBoundaryCells.Conformal)).Mesh;
        var r = PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(0.2e-3, 2.7e-3),
                                                         PlanarPortSide.MinX, 50.0));

        Console.WriteLine(r.Describe());
        Console.WriteLine($"[slant] width {r.WidthM:E6} against grid {r.GridWidthM:E6} — " +
                          $"{(r.GridWidthM - r.WidthM) / r.GridWidthM:P3} narrower, {r.CutCellCount} cut cell(s)");

        // NON-VACUITY: without a cut cell in the port's own run this is the staircase path again and
        // the equality below is trivially true.
        Assert.True(r.CutCellCount > 0, "the slanted end put no cut cell in the port's run");

        // THE MEASUREMENT: an anchored pair's shared edge is fully metal, so the port's width is the
        // grid extent to the last bit even though its cells are cut — see the summary for why.
        Assert.Equal(r.GridWidthM, r.WidthM);
        Assert.Equal(0.0, r.UndrivenMetalM);

        // …and D4's standard is therefore the DUT's own cross-section, verbatim, with no cut cell in
        // it. Asserted on the standard's resolved port rather than on the lines it was handed.
        var st = PlanarCalibration.BuildLine(r, 6e-3, PlanarCalibration.EndRunCellsFor(r, problem.Slab));
        Assert.Equal(r.WidthM, st.Port1.WidthM, 15);
        Assert.Equal(r.BasisCount, st.Port1.BasisCount);
        Assert.All(st.Mesh.Cells, c => Assert.Null(c.Region));
    }

    /// <summary>
    /// <b>BOTH end caps of the reported taper earn a graded fan — the second thing the report turned
    /// up, and it is about the EDGE MESH rather than about boundary cells.</b>
    ///
    /// <para>The user asked why the metal near port 2 is finely meshed and the metal near port 1 is
    /// not. Measured: the 12 Ω cap is 20.292 mm and the 50 Ω cap is 2.998 mm against a "long enough to
    /// crowd" threshold of 0.2 × 21.989 = 4.398 mm — the polygon's own bounding box, which on this
    /// part is set by the WIDE end. So the narrow end, where the crowding is strongest and where the
    /// port sits, was graded by nothing at all: 356.9 µm cells all the way to x = 0 against
    /// 356.9 / 237.1 / 141.7 / 94.3 µm at the far end.</para>
    ///
    /// <para>Both caps qualify now — a cap has both its corners convex — and the fan at x = 0 mirrors
    /// the one at x = 50.8 mm. See <c>EdgeAttractorCapTests</c> for the rule itself, including the
    /// staircase it must still refuse.</para>
    /// </summary>
    [Fact]
    public void BothEndCapsOfTheTaper_EarnAGradedFan_NotJustTheWideOne()
    {
        var (problem, _) = Extract();
        var (attX, _) = SurfaceMesher.EdgeAttractors(problem);

        var (x0, _, x1, _) = problem.Bounds();
        Assert.Contains(attX, v => Math.Abs(v - x0) < 1e-9);
        Assert.Contains(attX, v => Math.Abs(v - x1) < 1e-9);

        var g = SurfaceMesher.Mesh(problem, At(PlanarBoundaryCells.Conformal)).Mesh.GridX;
        var head = Enumerable.Range(0, 4).Select(i => g[i + 1] - g[i]).ToArray();
        var tail = Enumerable.Range(g.Count - 5, 4).Select(i => g[i + 1] - g[i]).ToArray();
        Console.WriteLine($"  first 4 x-cells: {string.Join(", ", head.Select(v => $"{v * 1e6:F1}"))} µm");
        Console.WriteLine($"  last  4 x-cells: {string.Join(", ", tail.Select(v => $"{v * 1e6:F1}"))} µm");

        // The narrow end's fan must GROW inward, and its first cell must be a small fraction of bulk.
        Assert.True(head[0] < head[1] && head[1] < head[2],
            "the fan at x = 0 is not graded: " + string.Join(", ", head));
        Assert.True(head[0] < 0.5 * (g[^1] - g[^2] > 0 ? head[3] : head[3]),
            $"the first cell at x = 0 is {head[0] * 1e6:F1} µm against a bulk cell of {head[3] * 1e6:F1} µm");

        // …and it mirrors the wide end's, which is what "same physics, same treatment" means here.
        Assert.Equal(head[0], tail[^1], 12);
    }

    /// <summary>
    /// The note has to SAY it. A port that silently drives less metal than the artwork has is the
    /// smooth-plausible-wrong failure this whole area is organised against, and the resolution's
    /// description is what <c>PlanarSolve</c> puts in the run notes.
    /// </summary>
    [Fact]
    public void TheRunNoteNamesTheUndrivenMetal()
    {
        var (problem, ports) = Extract();
        var mesh = SurfaceMesher.Mesh(problem, At(PlanarBoundaryCells.Conformal)).Mesh;

        string note = PlanarPorts.Resolve(mesh, ports[0]).Describe();
        Assert.Contains("carries", note, StringComparison.Ordinal);
        Assert.Contains("NO rooftop", note, StringComparison.Ordinal);
        Assert.Contains("Cells per wavelength", note, StringComparison.Ordinal);
    }
}
