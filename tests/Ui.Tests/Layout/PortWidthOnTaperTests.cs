// A port's WIDTH is the metal at its own end face, not the conductor's bounding box.
//
// Found while building the user-doc figure for port placement, on a real MKLOPF: the narrow end of a
// Klopfenstein taper drew a reference-plane bar as tall as the WIDE end and an arrow scaled to match,
// because both came from LayoutPortDirection's bounding box. The same defect is already recorded in
// that file's own header for a port on a PCell INSTANCE, where the cure was the cell's own
// LayoutPin — this is the other route to it: a top-level polygon, with no pin to fall back on.
//
// The quantity is user-visible twice over. It is the bar and the arrow the editor draws, and it is
// the number the Properties Inspector reports for the port.

using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class PortWidthOnTaperTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Um(double um) => (long)Math.Round(um * Dbu);

    /// <summary>
    /// How far a measured width may sit from the exact one, in microns.
    ///
    /// <para><b>It is the measurement inset, not slack.</b> A width is measured a thousandth of the
    /// conductor's length INSIDE the end face — a cut exactly on the face runs along that face's own
    /// edge, where a scanline's crossings are degenerate. On this fixture that is 12 µm of a 12 mm
    /// taper, and at its 1-in-4 flank slope the width there is 2 × 0.25 × 12 µm = 6 µm short of the
    /// face's own. Ten microns covers that and nothing larger; a genuinely wrong answer here is
    /// wrong by millimetres.</para>
    /// </summary>
    private static readonly long Tol = Um(10);

    /// <summary>
    /// A straight-flanked taper, 4 mm wide at x = 0 narrowing to 1 mm at x = 12 mm, centred on y = 0.
    /// Deliberately a plain polygon rather than a generated PCell: the property under test is about
    /// geometry, and a fixture that depends on a generator's output would move when the generator did.
    /// </summary>
    private static PolygonShape Taper() => new()
    {
        Layer = TopCopper,
        Xy =
        [
            Um(0),     Um(2000),
            Um(12000), Um(500),
            Um(12000), Um(-500),
            Um(0),     Um(-2000),
        ],
    };

    private static LabelShape Port(long x, long y, LayoutRotation dir) => new()
    {
        Layer = TopCopper, X = x, Y = y, Text = "1", Height = Um(400),
        IsPort = true, PortDirection = dir,
    };

    private static LayoutPortDirection.PortHint Resolve(LayoutShape metal, LabelShape port)
    {
        var hint = LayoutPortDirection.Resolve([metal, port], port);
        Assert.NotNull(hint);
        return hint!.Value;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The end faces
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EachEndReportsITSOwnWidth_NotTheBoundingBoxs()
    {
        var metal = Taper();

        var wide   = Resolve(metal, Port(Um(0),     0, LayoutRotation.R0));
        var narrow = Resolve(metal, Port(Um(12000), 0, LayoutRotation.R180));

        // The bounding box is 4 mm tall at BOTH ends — which is what both ports used to report, so
        // the narrow end came out 4x too wide. The tolerance is one micron: the measurement cuts a
        // thousandth of the length inside the face, so on a straight flank it is short by exactly
        // that fraction of the taper's own slope.
        Assert.Equal(Um(4000), wide.WidthDbu,   Tol);
        Assert.Equal(Um(1000), narrow.WidthDbu, Tol);

        // And the bbox answer really is the wrong one, so this test cannot pass by coincidence.
        var bb = LayoutGeometry.BboxOf(metal);
        Assert.Equal(Um(4000), LayoutPortDirection.WidthAcross(bb, LayoutRotation.R180));
    }

    [Fact]
    public void AUniformLineIsUNCHANGED_SoNothingThatWasRightMoved()
    {
        // The box IS the metal on a rectangle, to the DBU. Every port on straight artwork — which is
        // nearly all of them — must report exactly what it always did.
        var line = new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Um(20000), Y2 = Um(2900) };

        foreach (var (x, dir) in new[] { (0L, LayoutRotation.R0), (Um(20000), LayoutRotation.R180) })
            Assert.Equal(Um(2900), Resolve(line, Port(x, Um(1450), dir)).WidthDbu);
    }

    [Fact]
    public void ThePlaneIsCENTREDOnTheMetal_NotOnTheBox()
    {
        // An off-centre run: the bounding box's mid-height is not where the conductor is at either
        // end, so a bar centred on the box sits beside the metal rather than across it.
        var wedge = new PolygonShape
        {
            Layer = TopCopper,
            Xy = [Um(0), Um(0), Um(10000), Um(4000), Um(10000), Um(3000), Um(0), Um(-1000)],
        };

        var lo = Resolve(wedge, Port(Um(0), Um(-500), LayoutRotation.R0));
        var hi = Resolve(wedge, Port(Um(10000), Um(3500), LayoutRotation.R180));

        // Low-x end spans y = -1000..0, so its centre is -500; high-x end spans 3000..4000 → 3500.
        // The box's own mid-height is 1500, which is inside neither.
        Assert.Equal(Um(-500), lo.PlaneY, Tol);
        Assert.Equal(Um(3500), hi.PlaneY, Tol);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // An interior station — what an internal delta-gap port's marker is measured at
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AnInteriorStationMeasuresTheMetalTHERE()
    {
        var metal = Taper();
        var bb    = LayoutGeometry.BboxOf(metal);

        // Halfway along a straight-flanked 4 mm -> 1 mm taper the metal is 2.5 mm.
        var mid = LayoutPortDirection.SpanAt(metal, bb, LayoutRotation.R0, acrossAt: 0,
                                             alongAt: Um(6000));
        Assert.NotNull(mid);
        Assert.Equal(Um(2500), mid!.Value.Width, Tol);

        // A quarter along it is 3.25 mm — so the measurement really follows the station rather than
        // returning one number for the whole conductor.
        var quarter = LayoutPortDirection.SpanAt(metal, bb, LayoutRotation.R0, acrossAt: 0,
                                                 alongAt: Um(3000));
        Assert.Equal(Um(3250), quarter!.Value.Width, Tol);

        // With no station it falls back to the end face, which is the edge port's own question.
        var face = LayoutPortDirection.SpanAt(metal, bb, LayoutRotation.R0, acrossAt: 0);
        Assert.Equal(Um(4000), face!.Value.Width, Tol);
    }

    [Fact]
    public void ACutAcrossTWORunsKeepsTheOneThePortIsOn()
    {
        // Two parallel lines, 1 mm and 3 mm wide. A cut crosses both; a port is on exactly one of
        // them, and summing the two would report a width no port has.
        var pair = new PolygonShape
        {
            Layer = TopCopper,
            Xy = [Um(0), Um(0), Um(10000), Um(0), Um(10000), Um(1000), Um(0), Um(1000)],
        };
        var other = new RectShape { Layer = TopCopper, X1 = 0, Y1 = Um(5000), X2 = Um(10000), Y2 = Um(8000) };

        // Asked of the first shape only — which is what the lookup hands over, since it returns the
        // smallest-area shape under the point rather than a union of everything.
        var hint = Resolve(pair, Port(Um(0), Um(500), LayoutRotation.R0));
        Assert.Equal(Um(1000), hint.WidthDbu, Tol);

        var onOther = LayoutPortDirection.Resolve([pair, other, Port(Um(0), Um(6500), LayoutRotation.R0)],
                                                  Port(Um(0), Um(6500), LayoutRotation.R0));
        Assert.Equal(Um(3000), onOther!.Value.WidthDbu, Tol);
    }

    [Fact]
    public void AShapeWithNothingToMeasure_FallsBackToTheBoxRatherThanDrawingNothing()
    {
        // A port whose conductor is a placed instance has no top-level shape at all. The lookup then
        // carries no shape, and the bounding box is the honest fallback — the marker still draws.
        var bb = new Bbox(0, 0, Um(10000), Um(2900));
        var hint = LayoutPortDirection.Resolve(
            (x, y) => new LayoutPortDirection.ConductorInfo(bb, null),
            Port(0, Um(1450), LayoutRotation.R0));

        Assert.NotNull(hint);
        Assert.Equal(Um(2900), hint!.Value.WidthDbu);
    }
}
