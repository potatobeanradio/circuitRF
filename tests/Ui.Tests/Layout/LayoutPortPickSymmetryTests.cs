// Owner report, 2026-08-09: "When dragging the port, the geometry snap distance appears to be
// asymmetric. In the direction of the arrow pointer it will snap farther than the opposite direction.
// Make them both the same (and the farther distance is working good right now for UX)."
//
// A label's hit box is built from its text, and text runs ONE WAY from a baseline-left origin — so the
// anchor sat on the box's own corner. Measured on a 2-character port of height H, the box reached
// 1.24·H in the text direction (+x at R0, which is also where the arrow points) and ZERO the other
// way. The port could be grabbed from a long way ahead of it and only within the click tolerance
// behind it.
//
// That asymmetry is CORRECT for an annotation and wrong for a port: a port is a marker, and what the
// user aims at is the plane bar and arrow drawn about the conductor end, not the text.

using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortPickSymmetryTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private const long H = 1_000 * Dbu;          // 1 mm text
    private const long Anchor = 6_000 * Dbu;     // well inside the conductor, clear of its own corners

    private static LayoutView Fixture(LayoutRotation rot, out LayoutView view)
    {
        view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });
        view.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = Anchor, Y = 1_450 * Dbu, Text = "P1", Height = H,
            IsPort = true, Rotation = rot, PortDirection = LayoutRotation.R0,
        });
        return view;
    }

    /// <summary>The largest offset from the anchor, along <paramref name="ux"/>/<paramref name="uy"/>,
    /// at which the port (shape index 1) is still the topmost hit. Bisected rather than stepped, so
    /// the answer is exact to a DBU regardless of how large the reach turns out to be.</summary>
    private static long PickReach(LayoutView view, int ux, int uy)
    {
        long lo = 0, hi = 8 * H;
        while (lo < hi)
        {
            long mid = lo + (hi - lo + 1) / 2;
            var hits = LayoutHitTest.HitStack(view, null, Anchor + ux * mid, 1_450 * Dbu + uy * mid, 0);
            if (hits.Count > 0 && hits[0] == 1) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    [Fact]
    public void APortsPickRegionIsSymmetric_AndKeepsTheLargerReach()
    {
        Fixture(LayoutRotation.R0, out var view);

        long plusX  = PickReach(view, +1, 0);
        long minusX = PickReach(view, -1, 0);
        long plusY  = PickReach(view, 0, +1);
        long minusY = PickReach(view, 0, -1);

        Assert.Equal(plusX, minusX);
        Assert.Equal(plusX, plusY);
        Assert.Equal(plusX, minusY);

        // "The farther distance is working good right now" — the symmetric reach is the LARGER of the
        // two former ones (the text extent, 2 chars x H x 0.62 = 1.24 H), not the smaller.
        Assert.Equal((long)Math.Round(2 * H * 0.62), plusX);
    }

    [Theory]
    [InlineData(LayoutRotation.R90)]
    [InlineData(LayoutRotation.R180)]
    [InlineData(LayoutRotation.R270)]
    public void SymmetryHoldsAtEveryTextRotation(LayoutRotation rot)
    {
        // The old box's asymmetry pointed a different way per rotation, so a fix that only handled R0
        // would leave three quarters of the bug in place.
        Fixture(rot, out var view);

        long plusX  = PickReach(view, +1, 0);
        long minusX = PickReach(view, -1, 0);
        long plusY  = PickReach(view, 0, +1);
        long minusY = PickReach(view, 0, -1);

        Assert.Equal(plusX, minusX);
        Assert.Equal(plusX, plusY);
        Assert.Equal(plusX, minusY);
    }

    [Fact]
    public void AnOrdinaryLabelKeepsItsTextShapedBox_BecauseTextGenuinelyRunsOneWay()
    {
        // The non-vacuity control, and the scope fence: this change is about PORTS. An annotation's
        // one-sided box is right — its glyphs really are all on one side of the baseline origin — and
        // making every label symmetric would grow every annotation's pick region for no reason.
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });
        view.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = Anchor, Y = 1_450 * Dbu, Text = "P1", Height = H, IsPort = false,
        });

        Assert.True(PickReach(view, +1, 0) > PickReach(view, -1, 0),
            "an ordinary label's box must still follow its text");
    }

    [Fact]
    public void ThePortIsGrabbableFromBehind_ThroughTheRealPressPath()
    {
        // The end-to-end shape of the report: press BEHIND the port (opposite the arrow) and it must
        // select. Before the fix this point was outside the box entirely.
        Fixture(LayoutRotation.R0, out var view);
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.OnPointerPressed(Anchor - H, 1_450 * Dbu, KeyModifiers.None, 1, 0);

        Assert.Contains(1, vm.SelectedIndices);
    }
}
