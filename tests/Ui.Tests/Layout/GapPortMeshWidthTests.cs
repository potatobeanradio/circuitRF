// An internal delta-gap port's drawn break follows the MESH once a mesh exists.
//
// The width of the break is otherwise a legibility fraction of the port width — a glyph, not a
// dimension. But the gap the solver actually uses is set by the mesh: the cut is a gridline and the
// excitation drives the rooftop spanning the pair of cells either side of it. With the mesh overlay
// drawn underneath, a fixed fraction is a number that means nothing sitting next to numbers that do.
//
// These gates are on the measurement, not on pixels: the two half-widths ARE the two cells, and they
// are returned in the port's own frame rather than in world order.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Tests.Layout;

public class GapPortMeshWidthTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Um(double um) => (long)Math.Round(um * Dbu);

    /// <summary>A 50 Ω line on the PCB starter, meshed by the real mesher — the same artwork the
    /// user-doc figure uses. A hand-built mesh would make this agree with itself and nothing else.</summary>
    private static (PlanarMeshReport Report, LayoutView View, long Xc, long Yc) Meshed(
        PlanarMeshSettings? settings = null)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var view = new LayoutView { DbuPerMicron = Dbu };
        long w = Um(2900), len = Um(9000);
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = len, Y2 = w });

        var planar = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 20e9);
        Assert.True(planar.Ok, planar.Refusal);

        var report = SurfaceMesher.Mesh(planar.Problem!, settings ?? PlanarMeshSettings.Default);
        Assert.NotEmpty(report.Mesh.Cells);
        return (report, view, len / 2, w / 2);
    }

    private static LabelShape Gap(long x, long y, LayoutRotation dir) => new()
    {
        Layer = TopCopper, X = x, Y = y, Text = "3", Height = Um(380),
        IsPort = true, PortDirection = dir,
    };

    private static LayoutPortDirection.PortHint Hint(LayoutView view, LabelShape port)
    {
        var shapes = new List<LayoutShape>(view.Shapes) { port };
        var h = LayoutPortDirection.Resolve(shapes, port);
        Assert.NotNull(h);
        return h!.Value;
    }

    [Fact]
    public void TheBreakIsTheTwoMeshCellsEitherSideOfTheCut()
    {
        var (report, view, xc, yc) = Meshed();
        var port = Gap(xc, yc, LayoutRotation.R0);

        var half = LayoutRenderer.MeshGapHalfWidth(report, port, Hint(view, port), Dbu);
        Assert.NotNull(half);

        // The two half-widths must be two REAL cells of the mesh — found by looking them up in the
        // grid rather than by recomputing them, so the test cannot agree with a bug in the same
        // arithmetic. Metres → DBU is one scalar because PlanarExtractor neither translates nor
        // centres (R-mom-2, and the mesh overlay's own header).
        double toDbu = Dbu * 1e6;
        var gx = report.Mesh.GridX;

        // The CUT itself must be a gridline — the mark is drawn there, not at the label, precisely so
        // the brackets can land on the mesh underneath them.
        int at = -1;
        for (int i = 0; i < gx.Count; i++)
            if (Math.Abs(gx[i] * toDbu - half!.Value.Cut) < 1) { at = i; break; }

        Assert.True(at > 0 && at + 1 < gx.Count,
            "the cut the break is drawn on is not a gridline of the mesh it claims to follow");

        // …and the two half-widths are the two cells that gridline separates.
        Assert.Equal((gx[at] - gx[at - 1]) * toDbu, half!.Value.Back, 1.0);
        Assert.Equal((gx[at + 1] - gx[at]) * toDbu, half.Value.Fwd,  1.0);

        // The cut is where the label asked for, to within the half cell a snap can move it.
        Assert.True(Math.Abs(half.Value.Cut - xc) <= 0.5 * (half.Value.Back + half.Value.Fwd) + 1);
    }

    [Fact]
    public void RefiningTheMeshNarrowsTheBreak()
    {
        // The property that makes the mark worth drawing at all: it is a measurement, so it responds
        // to the setting that governs it. A break that did not move under refinement would be a glyph
        // wearing a measurement's clothes.
        var coarse = Meshed(PlanarMeshSettings.Default with { Auto = false, CellsPerWavelength = 10 });
        var fine   = Meshed(PlanarMeshSettings.Default with { Auto = false, CellsPerWavelength = 40 });

        var portC = Gap(coarse.Xc, coarse.Yc, LayoutRotation.R0);
        var portF = Gap(fine.Xc,   fine.Yc,   LayoutRotation.R0);

        var hc = LayoutRenderer.MeshGapHalfWidth(coarse.Report, portC, Hint(coarse.View, portC), Dbu);
        var hf = LayoutRenderer.MeshGapHalfWidth(fine.Report,   portF, Hint(fine.View,   portF), Dbu);

        Assert.NotNull(hc);
        Assert.NotNull(hf);
        Assert.True(hf!.Value.Back + hf.Value.Fwd < hc!.Value.Back + hc.Value.Fwd,
            $"refining the mesh should narrow the gap; coarse {hc.Value.Back + hc.Value.Fwd}, "
          + $"fine {hf.Value.Back + hf.Value.Fwd}");
    }

    [Fact]
    public void WithNoMeshThereIsNoMeasurement_AndTheMarkRevertsToItsGlyph()
    {
        // Null is the whole contract of the fallback: an invalidated mesh must not leave a stale
        // width on screen looking like a live one.
        var (_, view, xc, yc) = Meshed();
        var port = Gap(xc, yc, LayoutRotation.R0);

        Assert.Null(LayoutRenderer.MeshGapHalfWidth(null, port, Hint(view, port), Dbu));
    }

    [Fact]
    public void TheTwoHalvesAreInThePortsOwnFrame_NotWorldOrder()
    {
        // On a graded mesh the two cells either side of a cut differ, and which is "back" depends on
        // which way the port points. Invisible on a uniform mesh — the two are equal there — which is
        // exactly why it is asserted rather than eyeballed.
        var (report, view, _, yc) = Meshed();

        // Near the end of the line, where edge grading makes the cells genuinely unequal.
        long nearEnd = Um(700);
        var fwd = Gap(nearEnd, yc, LayoutRotation.R0);
        var rev = Gap(nearEnd, yc, LayoutRotation.R180);

        var a = LayoutRenderer.MeshGapHalfWidth(report, fwd, Hint(view, fwd), Dbu);
        var b = LayoutRenderer.MeshGapHalfWidth(report, rev, Hint(view, rev), Dbu);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // Same cut, so the same pair of cells and the same total — but reported the other way round.
        Assert.Equal(a!.Value.Back + a.Value.Fwd, b!.Value.Back + b.Value.Fwd, 1.0);
        Assert.Equal(a.Value.Back, b.Value.Fwd, 1.0);
        Assert.Equal(a.Value.Fwd,  b.Value.Back, 1.0);
    }
}
