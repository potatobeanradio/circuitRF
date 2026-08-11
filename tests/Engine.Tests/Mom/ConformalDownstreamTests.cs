// Conformal boundary cells — §4: the things downstream of the mesher that are keyed to a
// RECTANGULAR cell. Each is small, and each is a silent wrong answer if missed.
//
// Three of these are the ones §4 says to ASSERT rather than build: a calibration standard and a via
// footprint are Manhattan by construction, so no cell of either can be cut — but "by construction"
// stops being true the first time someone edits the construction, and a standard that quietly
// acquired cut cells would be calibrating out something the DUT does not have.
//
// The fourth (D4) is the one §4 says to BUILD: the current density's transverse extent, which stops
// being the cell's Height the moment the cell is cut. Getting that wrong is wrong exactly on the rim,
// which is the part of a heat map anyone actually looks at.

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class ConformalDownstreamTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static PlanarMeshSettings Settings(PlanarBoundaryCells cells)
        => new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3, BoundaryCells: cells);

    /// <summary>
    /// A line with an oblique V-notch cut into the middle of its top edge — genuinely cut artwork,
    /// so the DUT this standard is built for really does carry cut cells and the assertion below is
    /// not vacuous.
    ///
    /// <para><b>Both ENDS are deliberately straight and full-height.</b> The first version of this
    /// fixture chamfered the MaxX corner, and the run failed with the §4 port refusal firing on port
    /// 2 — correctly. A port on a cut cell is a different port (its reference plane is the shared
    /// edge of two cells whose transverse extent is no longer the grid's), and that refusal already
    /// exists in <c>PlanarPorts</c>. This fixture is about what a STANDARD carries, so its ports
    /// must be resolvable; putting the obliquity in the middle is what makes both claims testable at
    /// once.</para>
    /// </summary>
    private static PlanarProblem ChamferedLine()
        => new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, 0),       new EmPoint(12e-3, 0),
                                      new EmPoint(12e-3, 2.9e-3), new EmPoint(8e-3, 2.9e-3),
                                      new EmPoint(6e-3, 1.8e-3),  new EmPoint(4e-3, 2.9e-3),
                                      new EmPoint(0, 2.9e-3)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, 10e9);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §4 — a calibration standard is a uniform rectangle and CANNOT acquire a cut cell
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void D1_ACalibrationStandardCarriesNoCutCell_EvenWhenTheDutIsFullOfThem()
    {
        var problem = ChamferedLine();
        var report  = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Conformal));

        // The DUT must genuinely be cut, or this proves nothing about standards.
        Assert.True(report.CutCellCount > 0,
            "the fixture produced no cut cells, so a standard having none says nothing");

        var ports = PlanarPorts.ResolveAll(report.Mesh,
            [new PlanarPort(1, new EmPoint(0, 1.45e-3), PlanarPortSide.MinX, 50.0),
             new PlanarPort(2, new EmPoint(12e-3, 1.45e-3), PlanarPortSide.MaxX, 50.0)]);

        foreach (var port in ports)
        {
            int k  = PlanarCalibration.EndRunCellsFor(port, problem.Slab);
            var st = PlanarCalibration.BuildLine(port, 6e-3, k);

            int cut = st.Mesh.Cells.Count(c => c.IsCut);
            _out.WriteLine($"port {port.Number}: DUT cut = {report.CutCellCount}, " +
                           $"standard N = {st.Mesh.Bases.Count}, cells = {st.Mesh.Cells.Count}, cut = {cut}");

            // BuildLine assembles cells from the DUT's own gridlines directly — it never runs the
            // conformal pass, because a standard is a uniform rectangle with no oblique edge to
            // follow. If that ever changes, the de-embedding starts removing an error box the DUT
            // does not have, which is a smooth wrong answer rather than a failure.
            Assert.Equal(0, cut);
            Assert.All(st.Mesh.Cells, c => Assert.Null(c.Region));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §4 — a via footprint is Manhattan, so its cells are whole and its hard gridlines still tile it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void D2_AViaFootprintIsUncut_AndItsHardGridlinesStillTileItExactly()
    {
        // A chamfered upper level over a plain lower one, with a via in the middle of the metal.
        // The upper level is genuinely cut; the via sits far from the chamfer, on continuous metal.
        double side = 40e-6, cx = 150e-6, cy = 60e-6;
        var footprint = new PlanarPolygon([
            new EmPoint(cx - side / 2, cy - side / 2), new EmPoint(cx + side / 2, cy - side / 2),
            new EmPoint(cx + side / 2, cy + side / 2), new EmPoint(cx - side / 2, cy + side / 2)]);

        var lower = new PlanarConductorLayer("Metal1",
            [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(300e-6, 0),
                                new EmPoint(300e-6, 120e-6), new EmPoint(0, 120e-6)])],
            4.1e7, 3e-6, 100e-6);

        var upper = new PlanarConductorLayer("Metal2",
            [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(300e-6, 0),
                                new EmPoint(300e-6, 80e-6), new EmPoint(260e-6, 120e-6),
                                new EmPoint(0, 120e-6)])],
            4.1e7, 3e-6, 103e-6);

        var problem = new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, 30e9,
            Vias: [new PlanarVia(0, 1, [footprint], 4.1e7)]);

        var report = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Conformal));
        _out.WriteLine($"N = {report.UnknownCount}, cells = {report.Mesh.Cells.Count}, " +
                       $"cut = {report.CutCellCount}, vertical = {report.ViaUnknownCount}");

        Assert.True(report.CutCellCount > 0, "the upper level's chamfer produced no cut cells");
        Assert.True(report.ViaUnknownCount > 0,
            "the via produced no vertical unknowns — L9c's own silent failure, and this test would " +
            "then be asserting something about a via that is not in the mesh");

        // Every cell the footprint covers, on either level, must be WHOLE — a via footprint is an
        // interior feature of continuous metal and has no boundary for a cut to follow.
        double x0 = cx - side / 2, x1 = cx + side / 2, y0 = cy - side / 2, y1 = cy + side / 2;
        double covered = 0;
        int touched = 0;
        foreach (var c in report.Mesh.Cells)
        {
            bool inside = c.XMin >= x0 - 1e-15 && c.XMax <= x1 + 1e-15
                       && c.YMin >= y0 - 1e-15 && c.YMax <= y1 + 1e-15;
            if (!inside) continue;
            touched++;
            Assert.False(c.IsCut, $"a via footprint cell at ({c.XMin:E3}, {c.YMin:E3}) was CUT");
            if (c.LayerIndex == 0) covered += c.Width * c.Height;
        }

        Assert.True(touched > 0, "no cell fell inside the via footprint");

        // L9c measured that a via VANISHES silently without hard gridlines. Under the conformal pass
        // the footprint must still be tiled EXACTLY, or the same failure returns with cut cells as
        // the excuse.
        double want = side * side;
        double err  = Math.Abs(covered - want) / want;
        _out.WriteLine($"footprint tiled by {touched} cell(s) across both levels, " +
                       $"lower-level area error {err:E3}");
        Assert.True(err < 1e-12,
            $"the via footprint is tiled to {err:E3} rather than exactly — the hard gridlines no " +
            "longer bound it, which is how a via silently produces no vertical unknowns");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §4 — what MinCellEdgeM / MaxCellEdgeM / CellsAcrossNarrowestConductor MEAN for a cut cell
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void D3_TheReportedCellExtentsAreTheGridsOwn_NotTheCutRegionsOwn()
    {
        var problem = ChamferedLine();
        var stair   = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Staircase));
        var cut     = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Conformal));

        _out.WriteLine($"staircase: min {stair.MinCellEdgeM * 1e6:F3} µm, " +
                       $"max {stair.MaxCellEdgeM * 1e6:F3} µm, across {stair.CellsAcrossNarrowestConductor}");
        _out.WriteLine($"conformal: min {cut.MinCellEdgeM * 1e6:F3} µm, " +
                       $"max {cut.MaxCellEdgeM * 1e6:F3} µm, across {cut.CellsAcrossNarrowestConductor}, " +
                       $"cut = {cut.CutCellCount}, merged = {cut.MergedSliverCount}");

        Assert.True(cut.CutCellCount > 0, "the fixture produced no cut cells");

        // THE DECISION, stated once and gated here: the two EDGE EXTENTS report the GRID's own
        // rectangle, never the cut region's, and each has its own reason:
        //
        //   MaxCellEdgeM  is the quantity λ_g/N caps, and what λ_g/N caps is the grid PITCH.
        //   MinCellEdgeM  would otherwise report a sliver's own extent — which R-cut-3 already
        //                 reports separately, as a merge count — and would make every conformal
        //                 mesh look far finer than it is, which is exactly the wrong thing to tell
        //                 a user deciding whether to refine.
        //
        // The grid is untouched by the boundary model, so both match the staircase's EXACTLY. This
        // is the assertion that fails if someone "helpfully" starts reporting cut extents.
        foreach (var c in cut.Mesh.Cells)
        {
            Assert.True(c.Width  >= cut.MinCellEdgeM - 1e-18);
            Assert.True(c.Height >= cut.MinCellEdgeM - 1e-18);
            Assert.True(c.LongestEdge <= cut.MaxCellEdgeM + 1e-18);
        }
        Assert.Equal(stair.MinCellEdgeM, cut.MinCellEdgeM, 15);
        Assert.Equal(stair.MaxCellEdgeM, cut.MaxCellEdgeM, 15);

        // CellsAcrossNarrowestConductor IS ALLOWED TO DIFFER, and that is correct rather than an
        // oversight — measured 5 staircased against 6 conformal on an earlier chamfered-END fixture,
        // and 6 against 6 on this one, which is why the assertion is an inequality rather than an
        // equality. It is a COUNT of cells covering the narrowest run of metal, and a conformal mesh
        // genuinely covers metal a staircase drops:
        // the partial cells at an oblique rim are solved rather than discarded. So the count can only
        // ever go UP, never down, and reporting the staircase's count for a conformal mesh would
        // understate the discretisation actually used.
        Assert.True(cut.CellsAcrossNarrowestConductor >= stair.CellsAcrossNarrowestConductor,
            $"conformal reported {cut.CellsAcrossNarrowestConductor} cells across the narrowest " +
            $"conductor against the staircase's {stair.CellsAcrossNarrowestConductor} — a conformal " +
            "mesh covers at least as much metal, so this count cannot fall");

        // A cut cell's own area IS smaller than its grid extent — asserted so the test above cannot
        // pass by the cut region and the grid extent happening to coincide.
        var sample = cut.Mesh.Cells.First(c => c.IsCut);
        Assert.True(sample.Area < sample.Width * sample.Height,
            "the sampled cut cell fills its whole grid rectangle, so this test proves nothing");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §4 — the HEAT MAP's transverse extent, which is not Width/Height once a cell is cut
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>J = I / (the cell's transverse extent)</c>. On a WHOLE rectangle that extent is the cell's
    /// own Height (for x-flow); on a CUT cell it is not, and §4 says so directly: "get it right or
    /// the heat map is wrong exactly on the rim, which is the part anyone looks at".
    ///
    /// <para>The shipped answer is the MEAN transverse extent, <c>Area ÷ (longitudinal extent)</c>,
    /// which reduces to Height on a whole rectangle by construction — <c>Area/Width = (W·H)/W</c>.
    /// Both halves of that are asserted here: the reduction (so nothing in the pre-conformal path
    /// moved) and the difference (so the cut cell genuinely stops reporting current spread over
    /// metal that is not there).</para>
    ///
    /// <para><b>The two are exact inverses, and that is the property worth pinning</b> —
    /// <c>ColumnCurrent</c> multiplies the density back by the same extent, so a wrong extent in one
    /// place and not the other would silently break the current balance Tier 4 checks rather than
    /// showing up as a visibly odd colour.</para>
    /// </summary>
    [Fact]
    public void D4_TheCurrentDensitysTransverseExtent_IsTheMeanOnACutCell_AndReducesOnAWholeOne()
    {
        var report = SurfaceMesher.Mesh(ChamferedLine(), Settings(PlanarBoundaryCells.Conformal));
        Assert.True(report.CutCellCount > 0, "the fixture produced no cut cells");

        int whole = 0, cut = 0;
        foreach (var c in report.Mesh.Cells)
        {
            double tranY = c.Area / c.Width;    // the extent a current flowing in x spreads across
            double tranX = c.Area / c.Height;

            if (c.Region is null)
            {
                // Reduction, asserted as an identity rather than to a tolerance.
                Assert.Equal(c.Height, tranY, 15);
                Assert.Equal(c.Width,  tranX, 15);
                whole++;
            }
            else
            {
                // A cut cell holds strictly less metal than its rectangle, so the mean extent is
                // strictly smaller — using Height would report the current as more spread out than
                // it is, i.e. a density too LOW, exactly on the rim.
                Assert.True(tranY < c.Height,
                    $"a cut cell reported a transverse extent of {tranY:E3} against its grid Height " +
                    $"{c.Height:E3} — the mean must be strictly smaller once metal is missing");
                Assert.True(tranX < c.Width);
                cut++;
            }
        }

        _out.WriteLine($"{whole} whole cell(s) reduce exactly; {cut} cut cell(s) report a strictly " +
                       "smaller mean transverse extent");
        Assert.True(whole > 0 && cut > 0, "the fixture must contain both kinds or this proves nothing");
    }
}
