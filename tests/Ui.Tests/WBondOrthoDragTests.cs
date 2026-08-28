using System;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Shift constrains a wBond MOVE drag to ortho (owner, 2026-08-27: dragging a wire — or a point, or a
/// segment — ignored the modifier that already constrained a wire being DRAWN).
///
/// <para>All three subjects are one gesture underneath: <c>WireEdits.Translate</c> of whatever is
/// selected, by the delta the overlay computes. So the constraint sits on the cursor, once, and every
/// selection kind inherits it — which is what these three tests check separately, since "it works for
/// a wire" would not have caught a per-subject implementation.</para>
/// </summary>
public class WBondOrthoDragTests
{
    private static long Mil(double v) => WBondUnits.ToNm(v, WBondUnit.Mil);

    private static WBondDesign Design(int wires = 2)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                new Point3(0, w * Mil(10), 0),
                new Point3(Mil(60), w * Mil(10), 0),
                WBondDefaults.DiameterNm, WBondDefaults.Material, Mil(20), WBondDefaults.Points));
        design.Arrays.Add(array);
        return design;
    }

    /// <summary>Snap off, so the assertion is about the CONSTRAINT and not about which grid point the
    /// constrained cursor then landed on.</summary>
    private static WBondLayoutOverlay Overlay(WBondViewModel vm) => new(vm) { SnapEnabled = false };

    private static long Tol => Mil(3.0);

    /// <summary>One press-drag-release at <paramref name="mods"/>, returning the wire's first foot
    /// before and after.</summary>
    private static (Point3 Before, Point3 After) Drag(
        WBondViewModel vm, WBondLayoutOverlay overlay, long dx, long dy, KeyModifiers mods)
    {
        var wire = vm.Design.AllWires().First();
        var before = wire.Points[0];

        overlay.OnPointerPressed(before.X, before.Y, Tol, mods, 1);
        overlay.OnPointerMoved(before.X + dx, before.Y + dy, 0, leftButtonDown: true, mods);
        overlay.OnPointerReleased(before.X + dx, before.Y + dy);

        return (before, vm.Design.AllWires().First().Points[0]);
    }

    [Fact]
    public void ShiftDraggingAWire_MovesItOnOneAxisOnly()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        // Mostly horizontal: the vertical component is dropped.
        var (before, after) = Drag(vm, overlay, Mil(30), Mil(8), KeyModifiers.Shift);

        Assert.Equal(before.X + Mil(30), after.X);
        Assert.Equal(before.Y, after.Y);
    }

    [Fact]
    public void ShiftPicksTheDominantAxis_SoAMostlyVerticalDragGoesVertical()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        var (before, after) = Drag(vm, overlay, Mil(8), Mil(30), KeyModifiers.Shift);

        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y + Mil(30), after.Y);
    }

    [Fact]
    public void WithoutShift_TheSameDragIsFree()
    {
        // The control: without it these tests would pass on an overlay that had simply stopped moving
        // things diagonally for some other reason.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        var (before, after) = Drag(vm, overlay, Mil(30), Mil(8), KeyModifiers.None);

        Assert.Equal(before.X + Mil(30), after.X);
        Assert.Equal(before.Y + Mil(8), after.Y);
    }

    [Fact]
    public void ShiftDraggingAPOINT_MovesItOnOneAxisOnly()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Points = { new PointRef(0, 0) } };

        var (before, after) = Drag(vm, overlay, Mil(30), Mil(8), KeyModifiers.Shift);

        Assert.Equal(before.X + Mil(30), after.X);
        Assert.Equal(before.Y, after.Y);
    }

    [Fact]
    public void ShiftDraggingASEGMENT_MovesItOnOneAxisOnly()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Segments = { new SegmentRef(0, 0) } };

        var wire = vm.Design.AllWires().First();
        var grab = wire.Points[0];
        long beforeY = wire.Points[0].Y;

        overlay.OnPointerPressed(grab.X, grab.Y, Tol, KeyModifiers.Shift, 1);
        overlay.OnPointerMoved(grab.X + Mil(30), grab.Y + Mil(8), 0, leftButtonDown: true, KeyModifiers.Shift);
        overlay.OnPointerReleased(grab.X + Mil(30), grab.Y + Mil(8));

        Assert.Equal(beforeY, vm.Design.AllWires().First().Points[0].Y);
    }

    [Fact]
    public void TheConstraintIsAnchoredAtThePress_NotAtThePreviousFrame()
    {
        // A per-frame constraint lets a drag ratchet sideways one axis-locked step at a time and end
        // up nowhere near the axis — which looks right for the first frame and wrong by the end.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        var before = vm.Design.AllWires().First().Points[0];
        overlay.OnPointerPressed(before.X, before.Y, Tol, KeyModifiers.Shift, 1);

        for (int i = 1; i <= 6; i++)
            overlay.OnPointerMoved(before.X + Mil(5 * i), before.Y + Mil(2 * i), 0,
                                   leftButtonDown: true, KeyModifiers.Shift);

        overlay.OnPointerReleased(before.X + Mil(30), before.Y + Mil(12));

        var after = vm.Design.AllWires().First().Points[0];
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(before.X + Mil(30), after.X);
    }

    [Fact]
    public void PressingShiftMidDrag_TakesEffectImmediately()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        var before = vm.Design.AllWires().First().Points[0];
        overlay.OnPointerPressed(before.X, before.Y, Tol, KeyModifiers.None, 1);
        overlay.OnPointerMoved(before.X + Mil(20), before.Y + Mil(9), 0,
                               leftButtonDown: true, KeyModifiers.None);

        // Same cursor, Shift now down: the wire snaps back onto the axis through the press point.
        overlay.OnPointerMoved(before.X + Mil(20), before.Y + Mil(9), 0,
                               leftButtonDown: true, KeyModifiers.Shift);
        overlay.OnPointerReleased(before.X + Mil(20), before.Y + Mil(9));

        var after = vm.Design.AllWires().First().Points[0];
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(before.X + Mil(20), after.X);
    }

    // ── A press on a RULER is the layout editor's, not a wire marquee ─────────────────────────────

    /// <summary>
    /// Owner, 2026-08-27: dragging a ruler across a bond wire changed the wire's colour. It was not a
    /// highlight — the overlay had decided the press was on empty space, started a COMPANION MARQUEE,
    /// and on release actually SELECTED every wire the box swept. <c>LayoutHasSomethingAt</c> asked
    /// about shapes and instances but not about rulers, which are the layout editor's third selection
    /// channel and post-date that method.
    /// </summary>
    private static (WBondViewModel Vm, WBondLayoutOverlay Overlay, LayoutView Layout) WithRuler()
    {
        var vm = new WBondViewModel(Design(wires: 1));

        // A ruler crossing the wire, in layout DBU (1 DBU = 1 nm at the default resolution).
        var layout = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        layout.Rulers.Add(new RulerAnnotation
        {
            X1 = Mil(10), Y1 = -Mil(20), X2 = Mil(10), Y2 = Mil(20),
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = Mil(3),
        });

        var overlay = new WBondLayoutOverlay(vm)
        {
            SnapEnabled = false,
            ReferenceLayout = layout,
            WireMarqueeEnabled = false,   // the wirebond-CELL configuration, where the companion marquee runs
        };
        return (vm, overlay, layout);
    }

    [Fact]
    public void APressOnARuler_IsDeclined_AndStartsNoCompanionMarquee()
    {
        var (vm, overlay, _) = WithRuler();

        // On the ruler's line, which is also nowhere near a wire vertex. Declined either way — the
        // companion-marquee path declines too — so the load-bearing half is the SECOND assertion:
        // a companion marquee publishes its base selection as a preview the moment it starts.
        Assert.False(overlay.OnPointerPressed(Mil(10), Mil(10), Mil(1), KeyModifiers.None, 1));
        Assert.Null(vm.PreviewSelection);
    }

    [Fact]
    public void DraggingARulerAcrossAWire_DoesNotHighlightOrSelectIt()
    {
        var (vm, overlay, _) = WithRuler();
        Assert.Empty(vm.Selection.TouchedWires());

        // Press on the ruler, drag across the wire, release — the whole reported gesture.
        overlay.OnPointerPressed(Mil(10), Mil(10), Mil(1), KeyModifiers.None, 1);
        overlay.OnPointerMoved(Mil(10), 0, Mil(1), leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerMoved(Mil(10), -Mil(10), Mil(1), leftButtonDown: true, KeyModifiers.None);

        Assert.Null(vm.PreviewSelection);      // the colour change the owner saw

        overlay.OnPointerReleased(Mil(10), -Mil(10));

        Assert.Empty(vm.Selection.TouchedWires());   // ...and it really did select them
    }

    [Fact]
    public void APressOnGenuinelyEmptySpace_StillStartsTheCompanionMarquee()
    {
        // The control. Without it the fix could have been "never start a companion marquee", which
        // would break the gesture that exists so one box picks up the pads AND the wires.
        var (vm, overlay, _) = WithRuler();

        // Far from the ruler and from every wire.
        Assert.False(overlay.OnPointerPressed(Mil(200), Mil(200), Mil(1), KeyModifiers.None, 1));
        overlay.OnPointerMoved(-Mil(50), -Mil(50), Mil(1), leftButtonDown: true, KeyModifiers.None);

        Assert.NotNull(vm.PreviewSelection);
        Assert.NotEmpty(vm.PreviewSelection!.TouchedWires());
    }
}
