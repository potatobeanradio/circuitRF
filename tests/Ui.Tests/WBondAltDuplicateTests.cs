using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Alt-drag DUPLICATES a wBond selection (owner, 2026-08-27) — the copy gesture the layout editor has
/// had for primitives since R-dup-1, now for wires.
///
/// <para><b>The load-bearing question is the collision with WB24b</b>, which already spent Alt on
/// "stretch the span" in this same view. What separates them is WHAT IS UNDER THE HAND: a grab on a
/// FOOT stretches, a grab anywhere else copies. Both halves are pinned here, because a change that
/// gave the copy the whole modifier would pass every copy test and silently delete the stretch.</para>
/// </summary>
public class WBondAltDuplicateTests
{
    private static long Mil(double v) => WBondUnits.ToNm(v, WBondUnit.Mil);

    /// <summary>A level east-west wire per array row, feet at x = 0 and x = 30 mil.</summary>
    private static WBondDesign Design(int wires = 1)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                new Point3(0, w * Mil(10), 0),
                new Point3(Mil(30), w * Mil(10), 0),
                WBondDefaults.DiameterNm, WBondDefaults.Material, Mil(20), WBondDefaults.Points));
        design.Arrays.Add(array);
        return design;
    }

    private static WBondLayoutOverlay Overlay(WBondViewModel vm) =>
        new(vm, frameBudgetMs: 1e9) { SnapEnabled = false, GridPitchNm = 0 };

    private static long Tol => Mil(1.0);

    /// <summary>
    /// A grab on the wire's BODY — an INTERIOR vertex, so it is neither a foot nor part of an outer
    /// segment. On the default seed (7 points) that is most of the wire.
    /// </summary>
    private static (long X, long Y) Body(WBondViewModel vm)
    {
        var w = vm.Design.AllWires().First();
        var mid = w.Points[w.Points.Count / 2];
        return (mid.X, mid.Y);
    }

    /// <summary>A grab on an OUTER SEGMENT — between the foot and the first interior vertex. Since
    /// 2026-08-27 this stretches, like the foot itself.</summary>
    private static (long X, long Y) OuterSegment(WBondViewModel vm)
    {
        var w = vm.Design.AllWires().First();
        return ((w.Points[0].X + w.Points[1].X) / 2, (w.Points[0].Y + w.Points[1].Y) / 2);
    }

    private static (long X, long Y) Foot(WBondViewModel vm)
    {
        var w = vm.Design.AllWires().First();
        return (w.Points[^1].X, w.Points[^1].Y);
    }

    // ── The copy ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AltDraggingAWiresBODY_CopiesIt_AndLeavesTheOriginalWhereItWas()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var original = vm.Design.AllWires().Single().Points.ToList();
        var (bx, by) = Body(vm);

        Assert.True(overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1));
        Assert.True(overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt));
        overlay.OnPointerReleased(bx, by + Mil(20));

        var wires = vm.Design.AllWires().ToList();
        Assert.Equal(2, wires.Count);

        // The original is untouched…
        Assert.Contains(wires, w => w.Points.SequenceEqual(original));
        // …and the copy is the whole wire, offset by the drag.
        Assert.Contains(wires, w => w.Points.SequenceEqual(
            original.Select(p => new Point3(p.X, p.Y + Mil(20), p.Z))));
    }

    [Fact]
    public void TheGhostFollowsTheCursor_WhileTheOriginalStaysPut()
    {
        // "Render the wire ghost (and original wire) just like we do for primitives" — the ghost is
        // overlay state and the design is untouched until the release.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var original = vm.Design.AllWires().Single().Points.ToList();
        var (bx, by) = Body(vm);

        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);

        Assert.True(overlay.DuplicateDragArmed);
        Assert.Single(vm.Design.AllWires());                                  // nothing added yet
        Assert.Equal(original, vm.Design.AllWires().Single().Points);         // nothing moved either
    }

    [Fact]
    public void TheCopyIsWhatEndsUpSelected()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var (bx, by) = Body(vm);
        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(bx, by + Mil(20));

        int selected = Assert.Single(vm.Selection.TouchedWires());
        Assert.Equal(Mil(20), vm.Design.AllWires().ElementAt(selected).Points[0].Y);
    }

    [Fact]
    public void TheWholeCopyIsOneUndoEntry()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var (bx, by) = Body(vm);
        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx, by + Mil(6), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerMoved(bx, by + Mil(13), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(bx, by + Mil(20));

        Assert.Equal(2, vm.Design.AllWires().Count());

        vm.Undo();
        Assert.Single(vm.Design.AllWires());
    }

    [Fact]
    public void APointOrSegmentSelection_CopiesTheWHOLEWireItBelongsTo()
    {
        // TouchedWires, the same rule the clipboard uses — half a wire is not a thing the design can
        // hold, so a copy of a point selection has to be a copy of its wire.
        foreach (var selection in new[]
        {
            new WireSelection { Points = { new PointRef(0, 1) } },
            new WireSelection { Segments = { new SegmentRef(0, 1) } },
        })
        {
            var vm = new WBondViewModel(Design());
            var overlay = Overlay(vm);
            vm.Selection = selection;

            var (bx, by) = Body(vm);
            overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
            overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
            overlay.OnPointerReleased(bx, by + Mil(20));

            var wires = vm.Design.AllWires().ToList();
            Assert.Equal(2, wires.Count);
            Assert.Equal(wires[0].Points.Count, wires[1].Points.Count);
        }
    }

    [Fact]
    public void AMixedWirePointAndSegmentSelection_CopiesEachTouchedWireExactlyOnce()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);
        vm.Selection = new WireSelection
        {
            Wires = { 0 },
            Points = { new PointRef(1, 1) },
            Segments = { new SegmentRef(2, 0), new SegmentRef(2, 1) },   // same wire twice, on purpose
        };

        var (bx, by) = Body(vm);
        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx + Mil(40), by, Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(bx + Mil(40), by);

        Assert.Equal(6, vm.Design.AllWires().Count());          // 3 originals + 3 copies, not 4
        Assert.Equal(3, vm.Selection.TouchedWires().Count);
    }

    [Fact]
    public void ACopyThatWentNowhere_AddsNothing()
    {
        // Two wires on identical geometry are a singular inductance matrix — WBondViewModel's own
        // paste-pitch note records that as a real, reported failure.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var (bx, by) = Body(vm);
        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx + 200, by, Tol, leftButtonDown: true, KeyModifiers.Alt);   // inside the threshold
        overlay.OnPointerReleased(bx + 200, by);

        Assert.Single(vm.Design.AllWires());
    }

    // ── …and the stretch it must not have eaten ──────────────────────────────────────────────────

    [Fact]
    public void AltDraggingAFOOT_StillStretchesTheSpan_AndCopiesNothing()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var (fx, fy) = Foot(vm);
        Assert.Equal(Mil(30), fx);

        Assert.True(overlay.OnPointerPressed(fx, fy, Tol, KeyModifiers.Alt, 1));
        Assert.False(overlay.DuplicateDragArmed);
        Assert.True(overlay.OnPointerMoved(fx + Mil(7), fy, Tol, leftButtonDown: true, KeyModifiers.Alt));
        overlay.OnPointerReleased(fx + Mil(7), fy);

        var wire = vm.Design.AllWires().Single();          // still ONE wire
        Assert.Equal(Mil(37), wire.Points[^1].X - wire.Points[0].X);
        Assert.Equal(0L, wire.Points[0].X);                // the far foot is the anchor
    }

    [Theory]
    [InlineData(true)]    // the first outer segment
    [InlineData(false)]   // …and the last
    public void AltDraggingAnOUTERSEGMENT_StretchesToo(bool first)
    {
        // Owner, 2026-08-27, refining the foot-only rule: the outer segments are the part of the wire
        // that comes down to the pad, so grabbing one and pulling is the same physical gesture — and a
        // foot alone is a small target at any real zoom.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var w = vm.Design.AllWires().First();
        var (gx, gy) = first
            ? ((w.Points[0].X + w.Points[1].X) / 2, (w.Points[0].Y + w.Points[1].Y) / 2)
            : ((w.Points[^1].X + w.Points[^2].X) / 2, (w.Points[^1].Y + w.Points[^2].Y) / 2);

        Assert.True(overlay.OnPointerPressed(gx, gy, Tol, KeyModifiers.Alt, 1));
        Assert.False(overlay.DuplicateDragArmed);
        overlay.OnPointerMoved(gx + Mil(7), gy, Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(gx + Mil(7), gy);

        Assert.Single(vm.Design.AllWires());     // stretched, not copied
    }

    [Fact]
    public void AnINTERIORSegment_IsStillBody_AndCopies()
    {
        // The line the rule draws. Without this, "outer segments stretch" could be implemented as
        // "every segment stretches", which would leave the copy gesture reachable only on a vertex.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var w = vm.Design.AllWires().First();
        Assert.True(w.Points.Count >= 5, "the seed must have interior segments for this to mean anything");
        int mid = w.Points.Count / 2;
        long gx = (w.Points[mid].X + w.Points[mid + 1].X) / 2;
        long gy = (w.Points[mid].Y + w.Points[mid + 1].Y) / 2;

        overlay.OnPointerPressed(gx, gy, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(gx, gy + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(gx, gy + Mil(20));

        Assert.Equal(2, vm.Design.AllWires().Count());
    }

    [Fact]
    public void OnAThreePointWire_BothSegmentsAreOuter_AndTheApexVertexIsStillBody()
    {
        // The boundary of the rule, stated as it actually is rather than as it reads: with three
        // points there are two segments and BOTH are outer, so every grab on the LINE stretches — but
        // the apex VERTEX is neither foot, so it is body and copies. The two halves of the rule
        // disagree at exactly one place on this wire, and that is the vertex, which is a precise
        // target with its own drawn dot.
        WBondDesign Seed()
        {
            var d = new WBondDesign();
            var a = new WireArray { Name = "G1" };
            a.Wires.Add(LoopShape.CreateSeedWire(
                new Point3(0, 0, 0), new Point3(Mil(30), 0, 0),
                WBondDefaults.DiameterNm, WBondDefaults.Material, Mil(20), points: 3));
            d.Arrays.Add(a);
            return d;
        }

        // On the second segment, between the apex and the far foot — away from every vertex dot.
        var vm = new WBondViewModel(Seed());
        var overlay = Overlay(vm);
        vm.SelectAllWires();
        var w = vm.Design.AllWires().Single();
        Assert.Equal(3, w.Points.Count);

        long sx = (w.Points[1].X + w.Points[2].X) / 2;
        long sy = (w.Points[1].Y + w.Points[2].Y) / 2;
        overlay.OnPointerPressed(sx, sy, Tol, KeyModifiers.Alt, 1);
        Assert.False(overlay.DuplicateDragArmed);
        overlay.OnPointerMoved(sx + Mil(7), sy, Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(sx + Mil(7), sy);
        Assert.Single(vm.Design.AllWires());

        // …and the apex vertex, which copies.
        var vm2 = new WBondViewModel(Seed());
        var overlay2 = Overlay(vm2);
        vm2.SelectAllWires();
        var apex = vm2.Design.AllWires().Single().Points[1];

        overlay2.OnPointerPressed(apex.X, apex.Y, Tol, KeyModifiers.Alt, 1);
        overlay2.OnPointerMoved(apex.X, apex.Y + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay2.OnPointerReleased(apex.X, apex.Y + Mil(20));
        Assert.Equal(2, vm2.Design.AllWires().Count());
    }

    [Fact]
    public void WithoutAlt_TheSameBodyDragJustMovesTheWire()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var (bx, by) = Body(vm);
        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.None, 1);
        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(bx, by + Mil(20));

        var wire = Assert.Single(vm.Design.AllWires());
        Assert.Equal(Mil(20), wire.Points[0].Y);
    }

    // ── Arming and disarming mid-gesture ─────────────────────────────────────────────────────────

    [Fact]
    public void AltPressedMidDrag_PutsTheOriginalBackAndTakesTheCopyInstead()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var original = vm.Design.AllWires().Single().Points.ToList();
        var (bx, by) = Body(vm);

        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.None, 1);
        overlay.OnPointerMoved(bx, by + Mil(10), Tol, leftButtonDown: true, KeyModifiers.None);

        // A plain move so far — the original really has moved.
        Assert.NotEqual(original, vm.Design.AllWires().Single().Points);

        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);

        // …and now it is back where it started, with the copy in flight instead.
        Assert.True(overlay.DuplicateDragArmed);
        Assert.Equal(original, vm.Design.AllWires().Single().Points);

        overlay.OnPointerReleased(bx, by + Mil(20));

        var wires = vm.Design.AllWires().ToList();
        Assert.Equal(2, wires.Count);
        Assert.Contains(wires, w => w.Points.SequenceEqual(original));
    }

    [Theory]
    [InlineData(true)]    // grabbed a FOOT
    [InlineData(false)]   // grabbed an OUTER SEGMENT
    public void AltTakenMidDrag_CopiesEvenWhenThePressGrabbedAnEND(bool foot)
    {
        // Owner, 2026-08-27: a plain drag that then took Alt did nothing at all. _grabbedEnd decides
        // which gesture the PRESS offers, and once the press is past with no Alt on it the stretch can
        // never arm — it needs a reference span from that press — so Alt arriving later can only mean
        // copy. Consulting the grab a second time left a dead zone with neither gesture available.
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var w = vm.Design.AllWires().First();
        var (gx, gy) = foot
            ? (w.Points[^1].X, w.Points[^1].Y)
            : ((w.Points[0].X + w.Points[1].X) / 2, (w.Points[0].Y + w.Points[1].Y) / 2);

        var original = w.Points.ToList();

        overlay.OnPointerPressed(gx, gy, Tol, KeyModifiers.None, 1);          // no Alt at the press
        overlay.OnPointerMoved(gx, gy + Mil(10), Tol, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerMoved(gx, gy + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);

        // The cursor's own answer, which is what tells the user before the release.
        Assert.True(overlay.DuplicateDragArmed);
        // …and the original is back where it started, with the ghost carrying the copy.
        Assert.Equal(original, vm.Design.AllWires().Single().Points);

        overlay.OnPointerReleased(gx, gy + Mil(20));

        var wires = vm.Design.AllWires().ToList();
        Assert.Equal(2, wires.Count);
        Assert.Contains(wires, x => x.Points.SequenceEqual(original));
    }

    [Fact]
    public void AltReleasedMidDrag_GoesBackToMoving_AndLandsWhereTheHandIs()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var (bx, by) = Body(vm);

        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
        Assert.True(overlay.DuplicateDragArmed);

        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.None);
        Assert.False(overlay.DuplicateDragArmed);

        overlay.OnPointerReleased(bx, by + Mil(20));

        // No copy, and the wire is at the cursor — the whole travel since the press, not just what
        // happened after Alt was let go.
        var wire = Assert.Single(vm.Design.AllWires());
        Assert.Equal(Mil(20), wire.Points[0].Y);
    }

    [Fact]
    public void LosingFocusMidCopy_CommitsNothing()
    {
        var vm = new WBondViewModel(Design());
        var overlay = Overlay(vm);
        vm.SelectAllWires();

        var original = vm.Design.AllWires().Single().Points.ToList();
        var (bx, by) = Body(vm);

        overlay.OnPointerPressed(bx, by, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(bx, by + Mil(20), Tol, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnFocusLost();

        Assert.False(overlay.DuplicateDragArmed);
        Assert.Single(vm.Design.AllWires());
        Assert.Equal(original, vm.Design.AllWires().Single().Points);
    }
}
