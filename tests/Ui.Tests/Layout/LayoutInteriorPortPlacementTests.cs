// Owner, 2026-09-09, two reports about ports that are INSIDE a piece of metal rather than at its end:
//
//   (1) "an internal port is rendered with a ground (3 horizontal lines) glyph … they are
//       distracting", and the ring itself is drawn larger than the metal it sits on.
//   (2) "it is hard to place an internal port inside a geometry because we always use edge port
//       snapping, so the port always seems to go to the edge."
//
// (2) is the load-bearing one, and the FIRST answer to it here was wrong: it made a snap-ON click land
// on the conductor's centre line, which is the geometry-snap toggle doing the opposite of what the
// toolbar says. The owner's own rule, later the same day, is the one these tests hold — "when geometry
// snap is on, the port should be snapping to the edge for placement and for drags. when snap is off,
// then port can be placed anywhere and renders as internal port does."
//
// What makes that work without a port TYPE in the .clay — which deliberately carries none — is that
// the toggle decides by MOVING THE LABEL rather than by recording a mode: on the boundary the port is
// drawn as an edge port, in the middle it is drawn as an internal one, and a file re-opened months
// later draws the same way it did (LayoutPortDirection.PortHint.Interior).

using Avalonia.Input;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutInteriorPortPlacementTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Um(double um) => (long)Math.Round(um * Dbu);

    /// <summary>A 20 × 2.9 mm run of metal on a 1 mm grid, with a generous snap tolerance — the
    /// combination that used to make an interior click impossible: every point inside the trace is
    /// within tolerance of a long side.</summary>
    private static LayoutEditorViewModel Fixture(long snapDbu = 1_000 * Dbu)
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = snapDbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20_000), Y2 = Um(2_900) });
        return new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Port, GeometrySnapEnabled = true };
    }

    private static LabelShape Place(LayoutEditorViewModel vm, long x, long y, long tol)
    {
        vm.OnPointerMoved(x, y, leftDown: false, KeyModifiers.None, 0, 0, tol);
        vm.OnPointerPressed(x, y, KeyModifiers.None, 1, 0, 0, tol);
        return Assert.Single(vm.Model.Shapes.OfType<LabelShape>(), l => l.IsPort);
    }

    /// <summary>
    /// <b>Geometry snap ON means the port goes to the EDGE</b> — the owner's own rule, 2026-09-09:
    /// "when geometry snap is on, the port should be snapping to the edge for placement and for
    /// drags. when snap is off, then port can be placed anywhere and renders as internal port does."
    ///
    /// <para>An intermediate attempt read the first report the other way and put a snap-ON click on
    /// the conductor's CENTRE LINE. That is the toggle doing the opposite of what the toolbar says,
    /// and interior placement now has one route rather than two contradictory ones: switch snapping
    /// off. The <c>WithGeometrySnapOFF_</c> tests below are that route, and they are what makes this
    /// pair non-vacuous — the same click, the same fixture, the two different answers.</para>
    ///
    /// <para><b>And it reaches the edge from anywhere on the metal, not only from within tolerance.</b>
    /// Snapping is a tolerance query, so in the middle of a wide conductor there is no candidate at
    /// all — the toggle would do nothing in exactly the place the two answers differ most, leaving no
    /// way to ask for the edge but to zoom in until it came within eight pixels.</para>
    /// </summary>
    [Fact]
    public void WithGeometrySnapON_AClickInsideTheMetal_LandsOnTheEdge()
    {
        // 200 µm from the low-y side of a 2.9 mm trace: the nearest boundary is that side.
        var vm = Fixture();
        var port = Place(vm, Um(10_400), Um(200), tol: Um(600));

        Assert.Equal(0, port.Y);                  // the low-y side, which is the edge it was nearest
        Assert.Equal(Um(10_000), port.X);         // and the 1 mm grid still decides the along coordinate
    }

    [Fact]
    public void WithGeometrySnapON_AClickDEEPInsideTheMetal_StillReachesTheEdge()
    {
        // The dead centre of the trace — which is itself a snap FEATURE, so this is the second half
        // of the rule too: a query that answers with a point in the middle of the metal is still a
        // port in the middle of the metal, and geometry snap being on says it should not be.
        var vm = Fixture();
        var port = Place(vm, Um(10_000), Um(1_450), tol: Um(50));

        Assert.Equal(0, port.Y);
    }

    [Fact]
    public void APortSnappedToASIDEOfTheTrace_NamesTHATFace_NotTheWayTheMetalRuns()
    {
        // The direction has to describe where the port ENDED UP. "The way the metal runs" is the right
        // answer for a port left in the middle of the conductor (see the snap-OFF test below) and the
        // wrong one for a port snap has just put on a side face: it would be stamped R0 while standing
        // on the low-y edge, so it drew as an internal port sitting on the boundary — neither answer.
        var vm = Fixture();
        var port = Place(vm, Um(10_400), Um(200), tol: Um(600));

        Assert.Equal(LayoutRotation.R90, port.PortDirection);   // current flows +y, into the metal

        // …and it therefore draws as an EDGE port: its bar is on the face it names.
        var hint = Assert.NotNull(LayoutPortDirection.Resolve(vm.Model.Shapes, port));
        Assert.False(hint.Interior);
    }

    [Fact]
    public void AClickAtTheCONDUCTORSEND_StillSnapsToTheEndFeature()
    {
        // The half that must not regress: an edge port is placed by aiming at an END, and an end's
        // features are at the extreme of the metal in the direction it runs. Nothing about the
        // interior rule may reach that case.
        var vm = Fixture();
        var port = Place(vm, Um(120), Um(90), tol: Um(600));

        Assert.Equal(0, port.X);
        Assert.Equal(0, port.Y);
    }

    [Fact]
    public void WithGeometrySnapOFF_ThePortLandsExactlyWhereItWasClicked()
    {
        // The interior rule is a GEOMETRY SNAP and is gated as one. It was not, when first written:
        // with the toggle off a mid-trace click was still pulled to the centre line, which is the
        // toolbar's own switch failing to do the one thing it says (owner, 2026-09-09 — turning
        // geometry snap off is a route to interior placement in its own right, and it has to be an
        // honest one).
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20_000), Y2 = Um(2_900) });

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Port,
            GeometrySnapEnabled = false,
        };

        var port = Place(vm, Um(10_400), Um(200), tol: Um(600));

        Assert.Equal(Um(10_400), port.X);
        Assert.Equal(Um(200), port.Y);          // NOT the centre line at 1450
    }

    [Fact]
    public void APortInsideTheMetalFacesAlongIt_EvenWithGeometrySnapOFF()
    {
        // The direction is a question about the POINT, not about how the point was arrived at — so it
        // is deliberately NOT gated on the snap toggle. A delta gap placed with snapping off draws its
        // brackets perpendicular to this, and would draw them along the trace it is meant to cut.
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20_000), Y2 = Um(2_900) });

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Port,
            GeometrySnapEnabled = false,
        };

        var port = Place(vm, Um(10_400), Um(200), tol: Um(600));

        Assert.Equal(LayoutRotation.R0, port.PortDirection);
    }

    [Fact]
    public void AtTheCONDUCTORSEND_TheNearestSideStillDecidesTheDirection()
    {
        // The non-vacuity guard for the two above: an END is where DirectionAt's nearest-side
        // inference is the right answer, and the interior rule must not reach it. Without this, a
        // rule that simply always answered "along" would pass both of them.
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20_000), Y2 = Um(2_900) });

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Port,
            GeometrySnapEnabled = false,
        };

        var port = Place(vm, Um(120), Um(90), tol: Um(600));

        Assert.Equal(LayoutPortDirection.DirectionAt(
                         new LayoutPortDirection.ConductorInfo(
                             new Bbox(0, 0, Um(20_000), Um(2_900)), null), Um(120), Um(90)),
                     port.PortDirection);
    }

    [Fact]
    public void TheINTERNALPortsMarkStaysINSIDETheMetal_AndHasNoGroundSymbolUnderIt()
    {
        // A differential render is the oracle: the same frame with and without the port mark differs
        // in exactly the mark's own pixels, whatever colour the theme gives it. Every one of them has
        // to be within the conductor — which the ring was not (0.55 of the width is wider than the
        // metal), and which the three ground bars hanging below it never could be.
        // The baseline holds the metal alone: with the port present but no mark declared it renders as
        // an EDGE port, whose bar and arrow are a second difference and would mask the one being
        // measured. So the diff here is the whole port — its ring and its name — against bare metal.
        var bare = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Um(1_000) };
        bare.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20_000), Y2 = Um(2_900) });

        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Um(1_000) };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20_000), Y2 = Um(2_900) });
        view.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = Um(10_000), Y = Um(1_450), Text = "P1", Height = Um(500),
            IsPort = true, PortDirection = LayoutRotation.R0,
        });

        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var vp = LayoutViewport.ZoomToFit(bb, 900, 300, 0.2);

        var plain = Render(bare, vp);
        foreach (var l in view.Shapes.OfType<LabelShape>())
            if (l.IsPort) l.PortKind = PlanarPortKind.Internal;
        var withMark = Render(view, vp);

        // The metal's own rows on screen, from the same viewport transform the renderer used.
        double yTop = vp.WorldToScreenY(Um(2_900)), yBot = vp.WorldToScreenY(0);
        int rowLo = (int)Math.Ceiling(Math.Min(yTop, yBot)), rowHi = (int)Math.Floor(Math.Max(yTop, yBot));

        int differing = 0;
        for (int y = 0; y < plain.Height; y++)
        for (int x = 0; x < plain.Width; x++)
        {
            if (plain.GetPixel(x, y) == withMark.GetPixel(x, y)) continue;
            differing++;
            Assert.InRange(y, rowLo, rowHi);
        }

        Assert.True(differing > 0, "the internal port's mark was not drawn at all");
        plain.Dispose();
        withMark.Dispose();
    }

    /// <summary>The port TYPE is on the LABEL (2026-09-14), so a render of a given type is a render
    /// of a layout whose port states it — there is no mark list to hand the renderer any more.</summary>
    private static SKBitmap Render(LayoutView view, LayoutViewport vp)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        surface.Canvas.Clear(LayoutRenderTheme.Light.Background);
        LayoutRenderer.Draw(surface.Canvas, view, StarterTechnologies.Pcb2Layer(), vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light });
        using var img = surface.Snapshot();
        return SKBitmap.FromImage(img);
    }

    /// <summary>
    /// <b>Overlapping copper is one conductor, and the port goes to ITS edge</b> (round-7 field
    /// report). A placed part's footprint pad lying over a board's own pad stood 6 µm proud of it; the
    /// board pad's end-face midpoint was a snap feature 6 µm INSIDE the copper, the port landed there
    /// and was drawn as an edge port on a face that was not an edge.
    /// </summary>
    [Fact]
    public void APortSnappedToAPadUnderAnotherPad_LandsOnTheMergedCoppersEdge()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        // The board pad is the SMALLER shape, so it is the one the lookup lands on — as on the reported
        // board, where the footprint pad is inside a placed instance and never the first answer.
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = Um(-5000), Y1 = Um(-240), X2 = Um(300), Y2 = Um(240) }); // trace
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = Um(300),   Y1 = Um(-278), X2 = Um(700), Y2 = Um(282) }); // board pad
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = Um(150),   Y1 = Um(-290), X2 = Um(706), Y2 = Um(260) }); // footprint pad
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Port, GeometrySnapEnabled = true };

        var port = Place(vm, Um(700), Um(2), Um(20));

        Assert.Equal(Um(706), port.X);
        Assert.Equal(LayoutRotation.R180, port.PortDirection);
        Assert.Equal(CircuitRF.Engine.Mom.PlanarPortKind.Edge, port.PortKind);
    }
}
