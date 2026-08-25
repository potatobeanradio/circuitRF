// Owner report, 2026-08-25: "in my current NewPortTest.clay file the port2 hitbox is interfering
// with the port3, so I currently can't drag select p3, even though port 2 is far from port 3."
//
// Measured on that file: the anchors are 0.381 mm apart while each port's pick square is 2.52 mm
// across (2 characters at a 1.016 mm label height), so each anchor sits deep inside the other's box.
// The square is generous ON PURPOSE — a port is a marker and the user aims at its bar and arrow, not
// at a glyph — but `LayoutGeometry.BboxOf` of a label is a zero-area POINT, so HitStack's
// smaller-area-wins term scored both at 0 and the sort fell through to ascending list index. The
// port written earlier in the .clay won every overlapping pick, and the later one could not be
// grabbed at all.

using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Em;

public class PortPickNearestAnchorTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>The owner's own geometry: 1.016 mm-tall two-character port labels 0.381 mm apart.</summary>
    private static LayoutView TwoCloseP0rts()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(3.5), Y2 = Mm(0.7) });
        view.Shapes.Add(new LabelShape   // index 1 — the EARLIER one, which used to always win
        {
            Layer = TopCopper, X = 1473200, Y = 196761, Text = "P2",
            Height = 1016000, IsPort = true, PortDirection = LayoutRotation.R180,
        });
        view.Shapes.Add(new LabelShape   // index 2
        {
            Layer = TopCopper, X = 1092200, Y = 165100, Text = "P3",
            Height = 1016000, IsPort = true, PortDirection = LayoutRotation.R180,
        });
        return view;
    }

    [Fact]
    public void ClickingOnP3sAnchor_PicksP3_NotTheEarlierPortWhoseBoxAlsoCoversIt()
    {
        var view = TwoCloseP0rts();
        var hits = LayoutHitTest.HitStack(view, null, 1092200, 165100, 0);

        // Both are still REACHABLE — overlap cycling depends on the full stack — but the nearest
        // anchor must come first, because that is the one the click was aimed at.
        Assert.Contains(1, hits);
        Assert.Contains(2, hits);
        Assert.Equal(2, hits[0]);
    }

    [Fact]
    public void ClickingOnP2sAnchor_StillPicksP2()
    {
        var view = TwoCloseP0rts();
        var hits = LayoutHitTest.HitStack(view, null, 1473200, 196761, 0);
        Assert.Equal(1, hits[0]);
    }

    /// <summary>
    /// <b>A pointer press on P3 arms a move drag of P3.</b> The report is about dragging, not about
    /// an index in a list, so this drives the real pointer path and asserts which shape ends up
    /// selected — the thing a drag would then move.
    /// </summary>
    /// <summary>
    /// <b>SUPERSEDED IN SHAPE, 2026-08-25 — the ordering RULE survives, its fixture does not.</b> A
    /// port is now picked by its MARK (owner: "make the hitbox/highlight the arrow boundary box for
    /// edge and internal ports"), and both ports in the fixture above name the same conductor end, so
    /// on real artwork they now share one plane and therefore one region — no distance rule can
    /// separate them, and overlap cycling is what reaches the second.
    ///
    /// <para>The nearest-anchor rule still decides between two point-like shapes wherever the mark
    /// cannot be resolved, which is the case this drives: two ports on bare dielectric, with no
    /// conductor to give either a plane. Without the rule the earlier one in the file wins every
    /// overlapping pick and the later is unreachable, which was the original report.</para>
    /// </summary>
    [Fact]
    public void WithNoConductorToResolve_TheNearerPortStillWins()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(new LabelShape        // index 0 — the EARLIER one, which used to always win
        {
            Layer = TopCopper, X = 1473200, Y = 196761, Text = "P2",
            Height = 1016000, IsPort = true, PortDirection = LayoutRotation.R180,
        });
        view.Shapes.Add(new LabelShape        // index 1
        {
            Layer = TopCopper, X = 1092200, Y = 165100, Text = "P3",
            Height = 1016000, IsPort = true, PortDirection = LayoutRotation.R180,
        });

        var vm = new LayoutEditorViewModel(view);
        vm.OnPointerPressed(1092200, 165100, Avalonia.Input.KeyModifiers.None, 1, Mm(0.02));

        Assert.Equal([1], vm.SelectedIndices);
    }

    /// <summary>
    /// The term is scoped to POINT-LIKE shapes, so ordering between real geometry is untouched: two
    /// identical rectangles of equal area still tie-break by list index, not by which one the click
    /// happened to land nearer the middle of. Without this scoping the fix would silently change
    /// picking for every overlapping same-size shape in every layout.
    /// </summary>
    [Fact]
    public void TwoEqualAreaRectangles_StillTieBreakByIndex_NotByDistance()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        // Overlapping, same size, so the same area — the later one's centre is nearer the query point.
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0,       Y1 = 0, X2 = Mm(2),       Y2 = Mm(2) });
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = Mm(0.5), Y1 = 0, X2 = Mm(2.5),     Y2 = Mm(2) });

        // Inside both, but nearer the SECOND rectangle's centre.
        var hits = LayoutHitTest.HitStack(view, null, Mm(1.5), Mm(1), 0);

        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0]);   // list index still decides, exactly as before
    }
}
