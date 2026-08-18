using System;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>wBond §6.4 transforms, and WB26a's rotate-about-end-point gesture.</summary>
public class WBondTransformTests
{
    private static WBondDesign Design(int wires = 2)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        // The feet sit ABOVE the ground plane deliberately. A wire lying IN the plane has zero loop
        // inductance — its image cancels it exactly — which is a real state the editor has to refuse
        // gracefully (see RefusesAnEditThatMakesTheInductanceSingular) but a nonsense fixture for
        // testing ordinary transforms against.
        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, w * 10, 4), Point3.Mils(100, w * 10, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));

        design.Arrays.Add(array);
        return design;
    }

    private static WBondViewModel Vm(int wires = 2)
    {
        var vm = new WBondViewModel(Design(wires));
        vm.Selection = new WireSelection { Wires = { 0 } };
        return vm;
    }

    // ---------------------------------------------------------------- rotate (WB26a)

    /// <summary>
    /// <b>Each wire turns about its OWN pinned end.</b> That is the fan-out gesture — a ground array
    /// leaving one paddle at a spread of angles — and it is a genuinely different operation from
    /// swinging the selection rigidly about one shared pivot, which is why both exist.
    /// </summary>
    [Fact]
    public void RotateAboutOwnEnd_PinsEachWiresOwnFoot()
    {
        var vm = Vm(2);
        vm.Selection = new WireSelection { Wires = { 0, 1 } };

        var feet = vm.Design.AllWires().Select(w => w.Points[0]).ToList();
        int moved = vm.RotateSelectionAboutOwnEnd(Math.PI / 6, pivotOnInputFoot: true, EditorView.Layout);

        Assert.Equal(2, moved);

        var after = vm.Design.AllWires().ToList();
        for (int i = 0; i < after.Count; i++)
        {
            Assert.Equal(feet[i], after[i].Points[0]);              // pinned EXACTLY, not nearly
            Assert.NotEqual(feet[i].Y, after[i].Points[^1].Y);      // the far end swung
        }
    }

    /// <summary>A rigid rotation moves every wire about ONE pivot, so a pinned foot is not preserved.</summary>
    [Fact]
    public void RotateRigidly_TurnsTheWholeSelectionAboutOnePivot()
    {
        var vm = Vm(2);
        vm.Selection = new WireSelection { Wires = { 0, 1 } };

        var before = vm.Design.AllWires().Select(w => w.Points[0]).ToList();
        vm.RotateSelectionRigidly(Math.PI / 2, new Point3(0, 0, 0), EditorView.Layout);

        var after = vm.Design.AllWires().Select(w => w.Points[0]).ToList();
        Assert.Equal(before[0], after[0]);        // this foot IS the pivot, so it does not move
        Assert.NotEqual(before[1], after[1]);     // the other wire's foot swings around it
    }

    /// <summary>Rotation is the drag path — geometry moved, no point added, so no mesh rebuild.</summary>
    [Fact]
    public void Rotate_UsesTheIncrementalPath()
    {
        var vm = Vm();
        int rebuilds = vm.RebuildCount;

        vm.RotateSelectionAboutOwnEnd(0.2, pivotOnInputFoot: true, EditorView.Layout);

        Assert.Equal(rebuilds, vm.RebuildCount);
        Assert.True(vm.IncrementalUpdateCount > 0);
    }

    /// <summary>
    /// The gesture's pivot is the end FURTHER from the grab — which is what removes the need for a
    /// mode switch. Grabbing near the output foot must leave the INPUT foot exactly where it was.
    /// </summary>
    [Fact]
    public void TheRotateGesture_PinsTheEndFurtherFromTheGrab()
    {
        var vm = Vm(1);
        var overlay = new WBondLayoutOverlay(vm) { WireRotateArmed = true, SnapEnabled = false };

        var wire = vm.Design.AllWires().First();
        var inputFoot = wire.Points[0];
        var outputFoot = wire.Points[^1];
        long tol = WBondUnits.ToNm(5.0, WBondUnit.Mil);

        // Grab near the OUTPUT foot: the input foot is further, so it becomes the pivot.
        Assert.True(overlay.OnPointerPressed(outputFoot.X, outputFoot.Y, tol, KeyModifiers.None, 2));
        overlay.OnPointerMoved(outputFoot.X, outputFoot.Y + WBondUnits.ToNm(40, WBondUnit.Mil),
                               tol, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(outputFoot.X, outputFoot.Y + WBondUnits.ToNm(40, WBondUnit.Mil));

        var after = vm.Design.AllWires().First();
        Assert.Equal(inputFoot, after.Points[0]);
        Assert.NotEqual(outputFoot, after.Points[^1]);
    }

    /// <summary>A whole swing is ONE undo entry, however many frames it took.</summary>
    [Fact]
    public void ARotateGesture_CollapsesToOneUndoEntry()
    {
        var vm = Vm(1);
        var overlay = new WBondLayoutOverlay(vm) { WireRotateArmed = true, SnapEnabled = false };

        var wire = vm.Design.AllWires().First();
        var start = wire.Points.ToArray();
        var grab = wire.Points[^1];
        long tol = WBondUnits.ToNm(5.0, WBondUnit.Mil);

        overlay.OnPointerPressed(grab.X, grab.Y, tol, KeyModifiers.None, 2);
        for (int frame = 1; frame <= 12; frame++)
            overlay.OnPointerMoved(grab.X, grab.Y + frame * WBondUnits.ToNm(3, WBondUnit.Mil),
                                   tol, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(grab.X, grab.Y);

        Assert.NotEqual(start[^1], vm.Design.AllWires().First().Points[^1]);

        vm.Undo();
        Assert.Equal(start, vm.Design.AllWires().First().Points);
        Assert.False(vm.CanUndo);
    }

    // ---------------------------------------------------------------- mirror

    /// <summary>
    /// Mirroring reverses traversal by default, because a mirrored wire's input should stay on the
    /// input side — and getting it wrong flips the sign of every mutual involving that wire (WB3).
    /// </summary>
    [Fact]
    public void Mirror_ReversesTraversalByDefault_AndCanBeSuppressed()
    {
        var reversing = Vm(1);
        var inputBefore = reversing.Design.AllWires().First().Points[0];
        reversing.MirrorSelection('y', 0, reverseTraversal: true);

        // Mirrored about y = 0 AND reversed: the new input end is the old OUTPUT end, mirrored.
        var reversed = reversing.Design.AllWires().First();
        Assert.NotEqual(inputBefore.X, reversed.Points[0].X);

        var keeping = Vm(1);
        var keepInput = keeping.Design.AllWires().First().Points[0];
        keeping.MirrorSelection('y', 0, reverseTraversal: false);

        var kept = keeping.Design.AllWires().First();
        Assert.Equal(keepInput.X, kept.Points[0].X);     // same end still leads
        Assert.Equal(-keepInput.Y, kept.Points[0].Y);    // but it moved across the plane
    }

    // ---------------------------------------------------------------- straighten

    /// <summary>
    /// Straighten keeps the point count and touches x-y only.
    ///
    /// <para>The point count is the user's own choice and undo is what recovers a mistaken
    /// straighten (2026-08-18) — this used to be justified by "re-applying the bound profile puts the
    /// wire back", and there is no longer anything to re-apply from. A straighten that dropped points
    /// would be destructive and would cost a mesh rebuild as well, which is still true.</para>
    /// </summary>
    [Fact]
    public void Straighten_TidiesTheRouteAndLeavesTheLoopAlone()
    {
        var vm = Vm(1);
        var original = vm.Design.AllWires().First().Points.ToArray();
        int rebuilds = vm.RebuildCount;

        // Push an interior point sideways, so the straighten has a route to tidy. Done straight on
        // the model rather than through the view-model, so the undo below unwinds the STRAIGHTEN.
        var wandered = vm.Design.AllWires().First();
        wandered.Points[3] = wandered.Points[3] with
        {
            Y = wandered.Points[3].Y + WBondUnits.ToNm(15, WBondUnit.Mil),
        };
        var bowed = wandered.Points.ToArray();

        vm.StraightenSelection();
        var flat = vm.Design.AllWires().First();
        Assert.Equal(original.Length, flat.Points.Count);

        // The ROUTE is straight again and the LOOP is untouched — z is not this operation's business.
        Assert.Equal(original[3].Y, flat.Points[3].Y);
        Assert.Equal(original.Select(p => p.Z), flat.Points.Select(p => p.Z));

        // Undo is what recovers a mistaken straighten, and it recovers it exactly.
        vm.Undo();
        Assert.Equal(bowed, vm.Design.AllWires().First().Points.ToArray());

        // Not structural — the flat filament layout never changed.
        Assert.Equal(rebuilds, vm.RebuildCount);
    }

    // ---------------------------------------------------------------- duplicate with pitch (WB26)

    /// <summary>
    /// <b>N wires, one array, one rebuild.</b> That is a performance requirement, not a convenience:
    /// creating 200 wires as 200 operations is 200 cold fills, which is the difference between usable
    /// and unusable at the stated 600-wire worst case.
    /// </summary>
    [Fact]
    public void DuplicateWithPitch_MakesNWiresInOneRebuild_InTheSourcesArray()
    {
        var vm = Vm(1);
        int rebuilds = vm.RebuildCount;
        int before = vm.Design.AllWires().Count();

        int made = vm.DuplicateWithPitch(0, 0, WBondUnits.ToNm(6, WBondUnit.Mil), 20);

        Assert.Equal(20, made);
        Assert.Equal(before + 20, vm.Design.AllWires().Count());
        Assert.Equal(rebuilds + 1, vm.RebuildCount);
        Assert.Single(vm.Design.Arrays);
    }

    /// <summary>And the whole batch is one undo entry.</summary>
    [Fact]
    public void DuplicateWithPitch_IsOneUndoEntry()
    {
        var vm = Vm(1);
        int before = vm.Design.AllWires().Count();

        vm.DuplicateWithPitch(0, 0, WBondUnits.ToNm(6, WBondUnit.Mil), 8);
        vm.Undo();

        Assert.Equal(before, vm.Design.AllWires().Count());
    }

    // ---------------------------------------------------------------- bend / extend

    /// <summary>A bend displaces the interior and leaves both feet where they are, so the wire still lands.</summary>
    [Fact]
    public void Bend_PinsBothFeet()
    {
        var vm = Vm(1);

        // Snapshotted, not held by reference: the transform mutates the very Wire object in place,
        // so a captured reference would compare the result against itself.
        var before = vm.Design.AllWires().First().Points.ToArray();

        vm.BendSelection(0, WBondUnits.ToNm(10, WBondUnit.Mil), 0);

        var bent = vm.Design.AllWires().First();
        Assert.Equal(before[0], bent.Points[0]);
        Assert.Equal(before[^1], bent.Points[^1]);
        Assert.NotEqual(before[3].Y, bent.Points[3].Y);
    }

    /// <summary>Extend scales along the wire's own chord; a non-positive factor is refused, not applied.</summary>
    [Fact]
    public void Extend_LengthensAlongTheChord_AndRefusesANonPositiveFactor()
    {
        var vm = Vm(1);
        double before = vm.Design.AllWires().First().ChordLengthMetres();

        Assert.Equal(1, vm.ExtendSelection(1.5));
        Assert.True(vm.Design.AllWires().First().ChordLengthMetres() > before);

        Assert.Equal(0, vm.ExtendSelection(0));
        Assert.Equal(0, vm.ExtendSelection(-1));
    }

    // ---------------------------------------------------------------- unevaluable geometry

    /// <summary>
    /// <b>An edit that makes the inductance singular is refused and undone, never thrown.</b>
    ///
    /// <para>Reachable from an ordinary gesture rather than a defensive nicety: two wires on
    /// identical geometry make the matrix rank-deficient, and nudging one wire onto another is a
    /// handful of keystrokes. Unguarded, the factorisation's exception escapes through the pointer or
    /// key handler and takes the application down.</para>
    /// </summary>
    [Fact]
    public void RefusesAnEditThatMakesTheInductanceSingular_RatherThanThrowing()
    {
        var vm = Vm(2);                     // two wires, 10 mil apart in y
        vm.Selection = new WireSelection { Wires = { 0 } };

        string? refused = null;
        vm.EditRefused += reason => refused = reason;

        var before = vm.Design.AllWires().First().Points.ToArray();

        // Two coarse nudges of 5 mil each land wire 0 exactly on wire 1.
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Layout);
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Layout);   // must not throw

        Assert.NotNull(refused);

        // Rolled back to the state before the offending edit — not left sitting on top of wire 1.
        var after = vm.Design.AllWires().First().Points;
        Assert.NotEqual(vm.Design.AllWires().Last().Points[0].Y, after[0].Y);
        Assert.Equal(before[0].Y + WireEdits.CoarseNudgeNm, after[0].Y);
    }

    /// <summary>Duplicating onto identical geometry is the same class of refusal.</summary>
    [Fact]
    public void DuplicatingWithZeroPitch_IsRefused_NotThrown()
    {
        var vm = Vm(1);
        string? refused = null;
        vm.EditRefused += reason => refused = reason;

        int before = vm.Design.AllWires().Count();
        vm.DuplicateWithPitch(0, 0, 0, 3);      // three copies exactly on top of the original

        Assert.NotNull(refused);
        Assert.Equal(before, vm.Design.AllWires().Count());
    }

    // ---------------------------------------------------------------- clipboard (§6.7)

    /// <summary>
    /// A pasted wire rejoins an array of its ORIGINAL name — the array is what the reduction sums
    /// over (§3.4), so a wire pasted into the wrong array reports its inductance against the wrong
    /// pin while looking perfectly correct on screen.
    /// </summary>
    [Fact]
    public void PastePreservesArrayMembership()
    {
        var source = Vm(1);
        string? text = source.CopySelection();
        Assert.NotNull(text);

        var target = Vm(1);
        int added = target.PasteWires(text, 0, WBondUnits.ToNm(30, WBondUnit.Mil));

        Assert.Equal(1, added);
        Assert.Single(target.Design.Arrays);                     // joined the existing G1
        Assert.Equal(2, target.Design.Arrays[0].Wires.Count);
    }

    /// <summary>Pasting into a design that has no such array CREATES it, rather than dropping the wire.</summary>
    [Fact]
    public void PasteCreatesAMissingArray()
    {
        var source = Vm(1);
        source.Design.Arrays[0].Name = "Vdd";
        string? text = source.CopySelection();

        var target = Vm(1);                                      // its array is called G1
        int added = target.PasteWires(text, 0, WBondUnits.ToNm(30, WBondUnit.Mil));

        Assert.Equal(1, added);
        Assert.Equal(2, target.Design.Arrays.Count);
        Assert.Contains(target.Design.Arrays, a => a.Name == "Vdd");
    }

    /// <summary>
    /// <b>The POINTS travel, and they are the whole of the shape</b> — a cross-design paste keeps the
    /// loop because there is nothing else to keep (2026-08-18).
    ///
    /// <para>This used to assert that the source's loop profile was carried into the destination and
    /// installed there. That machinery is gone; what it was protecting — the pasted wire looking like
    /// the wire that was copied — is asserted directly.</para>
    /// </summary>
    [Fact]
    public void PasteCarriesTheShape()
    {
        var source = Vm(1);
        var copied = source.Design.AllWires().First().Points.ToArray();
        string? text = source.CopySelection();

        // A destination whose own wires have been reshaped — the paste must not adopt its shape.
        var target = Vm(1);
        WireEdits.ScaleHeightAboutChord(target.Design.AllWires().First(), 0.25);

        long dy = WBondUnits.ToNm(30, WBondUnit.Mil);
        target.PasteWires(text, 0, dy);

        var pasted = target.Design.AllWires().Last();
        Assert.Equal(copied.Length, pasted.Points.Count);
        for (int i = 0; i < copied.Length; i++)
            Assert.Equal(copied[i] with { Y = copied[i].Y + dy }, pasted.Points[i]);
    }

    /// <summary>A foreign clipboard is a no-op — never a half-applied paste.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text a user copied")]
    [InlineData("{\"marker\":\"circuitrf/layout-clipboard-v1\",\"Shapes\":[]}")]
    public void AForeignClipboard_PastesNothing(string? text)
    {
        var vm = Vm(1);
        int before = vm.Design.AllWires().Count();

        Assert.Equal(0, vm.PasteWires(text, 0, 1000));
        Assert.Equal(before, vm.Design.AllWires().Count());
    }

    /// <summary>Copy with nothing selected produces nothing to paste.</summary>
    [Fact]
    public void CopyWithNothingSelected_ProducesNoPayload()
        => Assert.Null(new WBondViewModel(Design(1)).CopySelection());

    /// <summary>A paste is one undo entry and one rebuild, however many wires it carried.</summary>
    [Fact]
    public void Paste_IsOneUndoEntryAndOneRebuild()
    {
        var source = Vm(2);
        source.Selection = new WireSelection { Wires = { 0, 1 } };
        string? text = source.CopySelection();

        var target = Vm(1);
        int rebuilds = target.RebuildCount;
        int before = target.Design.AllWires().Count();

        Assert.Equal(2, target.PasteWires(text, 0, WBondUnits.ToNm(30, WBondUnit.Mil)));
        Assert.Equal(rebuilds + 1, target.RebuildCount);

        target.Undo();
        Assert.Equal(before, target.Design.AllWires().Count());
    }

    // ---------------------------------------------------------------- empty selection

    /// <summary>Every transform is a no-op on an empty selection rather than an error.</summary>
    [Fact]
    public void EveryTransform_IsANoOpWithNothingSelected()
    {
        var vm = new WBondViewModel(Design(1));   // no selection
        bool couldUndo = vm.CanUndo;

        Assert.Equal(0, vm.RotateSelectionAboutOwnEnd(0.5, true, EditorView.Layout));
        Assert.Equal(0, vm.RotateSelectionRigidly(0.5, default, EditorView.Layout));
        Assert.Equal(0, vm.MirrorSelection('x', 0));
        Assert.Equal(0, vm.BendSelection(1000, 0, 0));
        Assert.Equal(0, vm.StraightenSelection());
        Assert.Equal(0, vm.ExtendSelection(1.2));

        Assert.Equal(couldUndo, vm.CanUndo);   // and none of them left an entry behind
    }
}
