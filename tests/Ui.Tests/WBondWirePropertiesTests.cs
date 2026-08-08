using System;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Properties Inspector's wire context (wbond.md §6.9) and the mixed clipboard (§6.7).
/// </summary>
public class WBondWirePropertiesTests
{
    private const long Mil = 25_400;

    private static WBondViewModel MakeEditor(int wires = 2)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "GND" };

        for (int i = 0; i < wires; i++)
        {
            array.Wires.Add(LoopProfile.BallBond(20 * Mil).CreateWire(
                new Point3(0, i * 6 * Mil, 0),
                new Point3(60 * Mil, i * 6 * Mil, 8 * Mil),
                diameterNm: Mil,
                material: "Gold"));
        }

        design.Arrays.Add(array);
        return new WBondViewModel(design);
    }

    /// <summary>
    /// A one-wire design that can actually be reduced.
    ///
    /// <para><b>The loop is real, deliberately.</b> A wire lying flat in the ground plane has zero loop
    /// inductance — its image cancels it exactly — so the matrix is singular and the edit is REFUSED
    /// and rolled back. That refusal is correct behaviour; a fixture that trips it is testing the
    /// refusal, not the edit.</para>
    /// </summary>
    private static WBondDesign ImportableDesign(string groupName)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = groupName };
        array.Wires.Add(LoopProfile.WedgeBond(15 * Mil).CreateWire(
            new Point3(0, 30 * Mil, 0), new Point3(50 * Mil, 30 * Mil, 0), Mil, "Gold"));
        design.Arrays.Add(array);
        return design;
    }

    private static WBondWirePropertiesViewModel Bind(WBondViewModel vm)
    {
        var panel = new WBondWirePropertiesViewModel();
        panel.SetContext(vm);
        return panel;
    }

    // ---------------------------------------------------------------- gating

    [Fact]
    public void Panel_IsEmpty_WithNothingSelected()
    {
        var panel = Bind(MakeEditor());
        Assert.True(panel.IsEmptyState);
    }

    /// <summary>Two wires have no shared coordinate list to edit, so the panel says so rather than
    /// showing one wire's numbers as if they were both.</summary>
    [Fact]
    public void Panel_IsEmpty_WithMoreThanOneWireSelected()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);

        vm.SelectAllWires();

        Assert.True(panel.IsEmptyState);
        Assert.Contains("2", panel.EmptyMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_ShowsTheWire_WhenExactlyOneIsSelected()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);

        vm.Selection = new WireSelection { Wires = [0] };

        Assert.False(panel.IsEmptyState);
        Assert.Equal("GND", panel.GroupName);
        Assert.Equal("Gold", panel.Material);
        Assert.NotNull(panel.VertexRows);
        Assert.Equal(vm.Design.AllWires().First().Points.Count, panel.VertexRows!.Count);
    }

    /// <summary>
    /// A PARTIAL selection — one point of one wire — still shows that wire. Selecting a vertex to
    /// read its coordinate is the most likely reason to open this panel at all.
    /// </summary>
    [Fact]
    public void Panel_ShowsTheWire_WhenOnlyOneOfItsPointsIsSelected()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);

        vm.Selection = new WireSelection { Points = [new PointRef(1, 2)] };

        Assert.False(panel.IsEmptyState);
        Assert.Equal("GND", panel.GroupName);
    }

    // ---------------------------------------------------------------- live

    /// <summary>
    /// <b>Coordinates follow a drag live.</b> A wBond drag mutates the wire's points in place and
    /// raises <c>ReadoutChanged</c> every frame, so the panel refreshes on that — no drag-override
    /// machinery, unlike the layout editor.
    /// </summary>
    [Fact]
    public void Coordinates_UpdateLive_WhenTheWireIsDragged()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);

        vm.Selection = new WireSelection { Wires = [0] };
        string before = panel.VertexRows![1].XText;

        // A nudge is the same code path a drag frame takes: move points, then CommitPointMove.
        vm.NudgeSelection(1, 0, coarse: true, EditorView.Layout);

        Assert.NotEqual(before, panel.VertexRows![1].XText);
    }

    /// <summary>
    /// The row COLLECTION is not replaced while the point count is unchanged — replacing it every
    /// drag frame would reset scroll position and destroy the field being typed into.
    /// </summary>
    [Fact]
    public void Rows_AreRefreshedInPlace_NotRebuilt_DuringADrag()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);

        vm.Selection = new WireSelection { Wires = [0] };
        var rows = panel.VertexRows;
        _ = rows![0].XText;   // materialise one

        for (int i = 0; i < 20; i++) vm.NudgeSelection(1, 0, coarse: false, EditorView.Layout);

        Assert.Same(rows, panel.VertexRows);
    }

    /// <summary>A long wire materialises only the rows actually asked for.</summary>
    [Fact]
    public void Rows_AreVirtualised_NotAllMaterialisedUpFront()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "Long" };
        array.Wires.Add(LoopProfile.BallBond(20 * Mil, points: 101).CreateWire(
            new Point3(0, 0, 0), new Point3(100 * Mil, 0, 0), Mil, "Gold"));
        design.Arrays.Add(array);

        var vm = new WBondViewModel(design);
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        Assert.Equal(101, panel.VertexRows!.Count);
        Assert.Equal(0, panel.VertexRows.MaterializedCount);

        _ = panel.VertexRows[5];
        Assert.Equal(1, panel.VertexRows.MaterializedCount);
    }

    // ---------------------------------------------------------------- edits

    [Fact]
    public void EditingACoordinate_MovesThePoint_AndIsUndoable()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        long originalZ = vm.Design.AllWires().First().Points[1].Z;

        panel.VertexRows![1].Commit('z', "30 mil");

        Assert.Equal(30 * Mil, vm.Design.AllWires().First().Points[1].Z);

        vm.Undo();
        Assert.Equal(originalZ, vm.Design.AllWires().First().Points[1].Z);
    }

    /// <summary>A coordinate accepts any unit, through the one shared parser.</summary>
    [Theory]
    [InlineData("2 mil", 2L * 25_400)]
    [InlineData("50um", 50_000L)]
    [InlineData("1.6mm", 1_600_000L)]
    public void EditingACoordinate_AcceptsAnyUnit(string typed, long expectedNm)
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.VertexRows![1].Commit('z', typed);

        Assert.Equal(expectedNm, vm.Design.AllWires().First().Points[1].Z);
    }

    [Fact]
    public void EditingACoordinate_WithGarbage_ShowsAnError_AndLeavesTheModelAlone()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        var before = vm.Design.AllWires().First().Points[1];
        panel.VertexRows![1].Commit('x', "not a number");

        Assert.True(panel.VertexRows[1].HasError);
        Assert.Equal(before, vm.Design.AllWires().First().Points[1]);
    }

    [Fact]
    public void EditingTheDiameter_AppliesToThatWireOnly()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitDiameter("2 mil");

        var wires = vm.Design.AllWires().ToList();
        Assert.Equal(2 * Mil, wires[0].DiameterNm);
        Assert.Equal(Mil, wires[1].DiameterNm);
    }

    [Fact]
    public void EditingTheMaterial_AppliesToThatWireOnly()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitMaterial("Aluminium");

        var wires = vm.Design.AllWires().ToList();
        Assert.Equal("Aluminium", wires[0].Material);
        Assert.Equal("Gold", wires[1].Material);
    }

    /// <summary>Loop height is reported by the definition — max z minus min z (§3.0).</summary>
    [Fact]
    public void LoopHeightReadout_IsMaxZMinusMinZ()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        var wire = vm.Design.AllWires().First();
        Assert.Equal(panel.Format(wire.LoopHeightNm), panel.LoopHeightText);
    }

    // ---------------------------------------------------------------- mixed clipboard

    /// <summary>A wires-only copy writes the PLAIN payload, so it still pastes anywhere it used to.</summary>
    [Fact]
    public void Compose_WiresOnly_WritesThePlainWirePayload_NotAnEnvelope()
    {
        string? text = WBondMixedClipboard.Compose("{\"Marker\":\"wires\"}", null);

        Assert.Equal("{\"Marker\":\"wires\"}", text);
        Assert.False(WBondMixedClipboard.IsMixed(text));
    }

    [Fact]
    public void Compose_LayoutOnly_WritesThePlainLayoutPayload_NotAnEnvelope()
    {
        string? text = WBondMixedClipboard.Compose(null, "{\"Marker\":\"layout\"}");

        Assert.Equal("{\"Marker\":\"layout\"}", text);
        Assert.False(WBondMixedClipboard.IsMixed(text));
    }

    [Fact]
    public void Compose_BothKinds_WrapsThemAndRoundTrips()
    {
        string? text = WBondMixedClipboard.Compose("WIRES-JSON", "LAYOUT-JSON");

        Assert.True(WBondMixedClipboard.TryParse(text, out var payload));
        Assert.Equal("WIRES-JSON", payload.Wires);
        Assert.Equal("LAYOUT-JSON", payload.Layout);
    }

    [Fact]
    public void Compose_NothingSelected_WritesNothing()
    {
        Assert.Null(WBondMixedClipboard.Compose(null, null));
        Assert.Null(WBondMixedClipboard.Compose("", "   "));
    }

    /// <summary>Foreign clipboard text is refused rather than half-read.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text a user copied")]
    [InlineData("{\"Marker\":\"circuitrf/wbond-clipboard-v1\"}")]
    [InlineData("{ truncated")]
    public void TryParse_RefusesAnythingThatIsNotTheEnvelope(string? text)
    {
        Assert.False(WBondMixedClipboard.TryParse(text, out _));
    }

    /// <summary>
    /// <b>Paste-whatever.</b> Every payload a user can produce must survive the ONE unwrap both
    /// editors read through, so a copy made anywhere pastes into whichever editor they land in.
    /// </summary>
    [Fact]
    public void Unwrap_MixedEnvelope_YieldsBothHalves()
    {
        var (wires, layout) = WBondMixedClipboard.Unwrap(
            WBondMixedClipboard.Compose("WIRES", "LAYOUT"));

        Assert.Equal("WIRES", wires);
        Assert.Equal("LAYOUT", layout);
    }

    /// <summary>
    /// A PLAIN payload is offered to both parsers unchanged — each refuses what is not its own, which
    /// is what lets an editor take the part it understands without knowing what the other can hold.
    /// </summary>
    [Fact]
    public void Unwrap_PlainPayload_IsOfferedToBothParsers()
    {
        var (wires, layout) = WBondMixedClipboard.Unwrap("{\"Marker\":\"something\"}");

        Assert.Equal("{\"Marker\":\"something\"}", wires);
        Assert.Equal("{\"Marker\":\"something\"}", layout);
    }

    /// <summary>
    /// <b>A mixed copy pastes into the LAYOUT EDITOR.</b> Without the unwrap at the layout paste
    /// path, the envelope fails that editor's own marker check and the paste silently does nothing —
    /// which is exactly the "I copied something and paste did nothing" failure.
    /// </summary>
    [Fact]
    public void MixedPayload_YieldsALayoutFragment_ForTheLayoutEditorsOwnPastePath()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 500, Y2 = 500, Layer = new LayerKey(2, 0) });
        var layout = new LayoutEditorViewModel(view);
        layout.SelectAllCommand.Execute(null);

        string mixed = WBondMixedClipboard.Compose(
            "{\"Marker\":\"circuitrf/wbond-clipboard-v1\"}",
            LayoutFragment.Serialize(layout.BuildCopyPayload()!))!;

        // The plain layout parser refuses the envelope outright...
        Assert.False(LayoutFragment.TryDeserialize(mixed, out _));

        // ...and succeeds once it goes through the shared unwrap, which is what the paste path does.
        var (_, layoutJson) = WBondMixedClipboard.Unwrap(mixed);
        Assert.True(LayoutFragment.TryDeserialize(layoutJson, out var fragment));
        Assert.Single(fragment!.Shapes);
    }

    // ---------------------------------------------------------------- editable height / span / group

    /// <summary>Setting a single wire's loop height lands it exactly, by the definition (§3.0).</summary>
    [Fact]
    public void CommitLoopHeight_SetsMaxZMinusMinZ_OnThatWireOnly()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        long before = vm.Design.AllWires().Last().LoopHeightNm;
        panel.CommitLoopHeight("30 mil");

        var wires = vm.Design.AllWires().ToList();
        Assert.InRange(wires[0].LoopHeightNm, 30 * Mil - 2, 30 * Mil + 2);
        Assert.Equal(before, wires[1].LoopHeightNm);
    }

    /// <summary>
    /// A bound wire is DETACHED by a single-wire height edit — it cannot both follow a shared shape
    /// and stand at its own height — and the panel shows the binding is gone.
    /// </summary>
    [Fact]
    public void CommitLoopHeight_DetachesABoundWire_AndSaysSo()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        Assert.NotEqual("(free)", panel.ProfileBinding);

        panel.CommitLoopHeight("30 mil");

        Assert.Equal("(free)", panel.ProfileBinding);
        Assert.NotNull(vm.Design.AllWires().Last().ProfileBinding);   // its sibling is untouched
    }

    /// <summary>A height below the wire's own foot drop is refused by name, not silently clamped.</summary>
    [Fact]
    public void CommitLoopHeight_BelowTheFootDrop_IsRefusedWithAReason()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        long before = vm.Design.AllWires().First().LoopHeightNm;
        panel.CommitLoopHeight("1 mil");   // the fixture's feet are 8 mil apart in z

        Assert.True(panel.HasLoopHeightError);
        Assert.Contains("foot drop", panel.LoopHeightError!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, vm.Design.AllWires().First().LoopHeightNm);
    }

    /// <summary>Span moves the output foot and leaves the input foot exactly where it was.</summary>
    [Fact]
    public void CommitSpan_MovesTheOutputFoot_AndPinsTheInputFoot()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        var inputFootBefore = vm.Design.AllWires().First().Points[0];
        panel.CommitSpan("100 mil");

        var wire = vm.Design.AllWires().First();
        Assert.Equal(inputFootBefore, wire.Points[0]);

        double spanNm = wire.ChordLengthMetres() * WBondUnits.NmPerMetre;
        Assert.InRange(spanNm, 100 * Mil - 50, 100 * Mil + 50);
    }

    /// <summary>Span does NOT detach a bound wire — a profile applies between whatever feet it has.</summary>
    [Fact]
    public void CommitSpan_LeavesABoundWireBound()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitSpan("100 mil");

        Assert.NotEqual("(free)", panel.ProfileBinding);
    }

    [Fact]
    public void AvailableGroups_ListsEveryGroupPlusTheNewGroupEntry()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        Assert.Equal(["GND", WBondWirePropertiesViewModel.NewGroupSentinel], panel.AvailableGroups);
    }

    /// <summary>Moving a wire to a NEW group creates it, and the panel keeps showing that wire.</summary>
    [Fact]
    public void CommitGroup_ToANewName_CreatesTheGroup_AndKeepsTheWireSelected()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitGroup("Vdd");

        Assert.Equal(2, vm.Design.Arrays.Count);
        Assert.Equal("Vdd", panel.GroupName);
        Assert.False(panel.IsEmptyState);
        Assert.Single(vm.Selection.TouchedWires());

        // The now-empty source group is LEFT in place — a group is a named terminal, and moving the
        // last wire off a pin is not the same statement as deleting the pin.
        Assert.Contains(vm.Design.Arrays, a => a.Name == "GND");
    }

    [Fact]
    public void CommitGroup_ToAnExistingName_MovesTheWireThere()
    {
        var vm = MakeEditor();
        vm.Design.Arrays.Add(new WireArray { Name = "Vdd" });

        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitGroup("Vdd");

        Assert.Single(vm.Design.Arrays.First(a => a.Name == "Vdd").Wires);
        Assert.Equal("Vdd", panel.GroupName);
    }

    [Fact]
    public void CommitGroup_IsUndoable()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitGroup("Vdd");
        vm.Undo();

        Assert.Single(vm.Design.Arrays.Where(a => a.Wires.Count > 0));
        Assert.Equal(2, vm.Design.Arrays.First(a => a.Name == "GND").Wires.Count);
    }

    // ---------------------------------------------------------------- structural undo

    /// <summary>
    /// <b>Deleting wires is undoable.</b> The undo snapshot used to capture points alone and could
    /// only drop TRAILING wires, so a deletion survived Ctrl+Z entirely — found by the group-move test
    /// above, but it was never specific to that edit. Membership is now captured by wire REFERENCE, so
    /// the deleted wire itself comes back.
    /// </summary>
    [Fact]
    public void DeletingWires_IsUndoable()
    {
        var vm = MakeEditor();
        vm.Selection = new WireSelection { Wires = [0] };

        var survivor = vm.Design.AllWires().Last();
        vm.DeleteSelectedWires();
        Assert.Equal(1, vm.Design.WireCount);

        vm.Undo();

        Assert.Equal(2, vm.Design.WireCount);
        Assert.Same(survivor, vm.Design.AllWires().Last());   // the same object, not a reconstruction
    }

    /// <summary>
    /// Deleting a whole group is undoable, group and all.
    ///
    /// <para>Two groups, deleting one — deleting the ONLY group leaves a design with no wires at all,
    /// which the physics refuses (there is no loop to reduce) and rolls back. That refusal is correct
    /// and is a different behaviour from this test's subject.</para>
    /// </summary>
    [Fact]
    public void DeletingAGroup_IsUndoable()
    {
        var vm = MakeEditor();
        vm.MergeWires(ImportableDesign("Vdd"));
        Assert.Equal(2, vm.Design.Arrays.Count);

        vm.DeleteGroup(1);
        Assert.Single(vm.Design.Arrays);

        vm.Undo();

        Assert.Equal(2, vm.Design.Arrays.Count);
        Assert.Equal("Vdd", vm.Design.Arrays[1].Name);
    }

    /// <summary>Merging imported wires in is undoable — the DXF Import Wires path.</summary>
    [Fact]
    public void MergingWires_IsUndoable()
    {
        var vm = MakeEditor();

        vm.MergeWires(ImportableDesign("Imported"));
        Assert.Equal(3, vm.Design.WireCount);

        vm.Undo();

        Assert.Equal(2, vm.Design.WireCount);
        Assert.DoesNotContain(vm.Design.Arrays, a => a.Name == "Imported");
    }

    /// <summary>Redo puts a structural change back.</summary>
    [Fact]
    public void StructuralUndo_RedoesCleanly()
    {
        var vm = MakeEditor();
        vm.Selection = new WireSelection { Wires = [0] };

        vm.DeleteSelectedWires();
        vm.Undo();
        vm.Redo();

        Assert.Equal(1, vm.Design.WireCount);
    }

    /// <summary>The sentinel is never committed as a group name — the view resolves it first.</summary>
    [Fact]
    public void CommitGroup_WithTheSentinel_DoesNothing()
    {
        var vm = MakeEditor();
        var panel = Bind(vm);
        vm.Selection = new WireSelection { Wires = [0] };

        panel.CommitGroup(WBondWirePropertiesViewModel.NewGroupSentinel);

        Assert.Single(vm.Design.Arrays);
        Assert.Equal("GND", panel.GroupName);
    }

    /// <summary>
    /// A real mixed round trip: wires and layout geometry out and back, both halves intact.
    /// </summary>
    [Fact]
    public void MixedRoundTrip_CarriesBothHalves()
    {
        var vm = MakeEditor();
        vm.SelectAllWires();
        string wiresJson = vm.CopySelection()!;

        var view = new LayoutView();
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, Layer = new LayerKey(1, 0) });
        var layout = new LayoutEditorViewModel(view);
        layout.SelectAllCommand.Execute(null);
        string layoutJson = LayoutFragment.Serialize(layout.BuildCopyPayload()!);

        string text = WBondMixedClipboard.Compose(wiresJson, layoutJson)!;

        Assert.True(WBondMixedClipboard.TryParse(text, out var payload));

        // The wires half is still readable by the plain wire parser it came from.
        Assert.NotNull(WBondClipboard.TryParse(payload.Wires));

        // And the layout half by the plain layout parser.
        Assert.True(LayoutFragment.TryDeserialize(payload.Layout, out var fragment));
        Assert.Single(fragment!.Shapes);
    }
}
