using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Docking;
using Dock.Model.Controls;
using Dock.Model.Core;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's FIFTH batch (2026-08-17), all of it downstream of WB-F's hosting change: the snap glyph
/// stopped being refreshed while the wire overlay owned the gesture, and clicking empty space stopped
/// clearing the layout's own selection.
///
/// <para><b>One root cause behind the first three reports.</b> <c>LayoutCanvas</c> offers the overlay
/// every press and move first, and anything it consumes never reaches
/// <c>LayoutEditorViewModel.OnPointerMoved</c> — which is the only thing that ever refreshed or cleared
/// the marker, and the only thing that ever cleared the layout selection. Three symptoms, one seam.</para>
/// </summary>
public class WBondRound5Tests
{
    private const int Dbu = 1000;   // 1 DBU = 1 nm, the default resolution

    /// <summary>An array of ball-bonded wires running east from the origin, pitched in y.</summary>
    private static WBondDesign Design(int wires = 2)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, w * 6.0, 4), Point3.Mils(100, w * 6.0, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
        design.Arrays.Add(array);

        return design;
    }

    /// <summary>A layout holding one pad, with its lower-left corner at (padX, padY) in DBU.</summary>
    private static LayoutView PadAt(long padX, long padY, long size = 20_000)
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = padX, Y1 = padY, X2 = padX + size, Y2 = padY + size,
        });
        return view;
    }

    private static WBondLayoutOverlay Overlay(WBondViewModel vm, LayoutView? view = null) =>
        new(vm, frameBudgetMs: 1e9)
        {
            ReferenceLayout = view,
            SnapToleranceNm = WBondUnits.ToNm(2.0, WBondUnit.Mil),
            GridPitchNm = 0,   // grid off, so only a GEOMETRY snap can produce a glyph
        };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1. The glyph while the OVERLAY owns the gesture
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Draw Wire publishes a glyph mid-draw</b> (owner: "snap glyphs do not render in Draw Wire mode
    /// in the middle of a draw"). The second foot is snapped on every move — that answer was simply
    /// never handed to anything that draws.
    /// </summary>
    [Fact]
    public void MidDraw_TheOverlayPublishesTheSnapItIsActuallyUsing()
    {
        var vm = new WBondViewModel(Design());
        var pad = PadAt(500_000, 500_000);
        var overlay = Overlay(vm, pad);
        overlay.WireDrawArmed = true;

        // First click places the start foot; nothing is being drawn yet before it.
        Assert.True(overlay.OnPointerPressed(100_000, 100_000, 500, KeyModifiers.None, 1));

        // …now move near the pad's lower-left corner. The glyph is the corner, not the cursor.
        Assert.True(overlay.OnPointerMoved(500_800, 500_800, 500, leftButtonDown: false, KeyModifiers.None));

        var marker = overlay.SnapMarker;
        Assert.NotNull(marker);
        Assert.Equal(500_000, marker!.Value.X);
        Assert.Equal(500_000, marker.Value.Y);

        // Away from everything, with the grid off, there is nothing to mark — and marking the bare
        // cursor position would say nothing.
        Assert.True(overlay.OnPointerMoved(9_000_000, 9_000_000, 500, leftButtonDown: false, KeyModifiers.None));
        Assert.Null(overlay.SnapMarker);
    }

    /// <summary>
    /// <b>The glyph disappears when the wire is dragged away from the vertex it was grabbed by</b>
    /// (owner: "the wire vertex snap glyph does not disappear when I drag a wire by its vertex").
    ///
    /// <para>It was the last HOVER's marker, left standing by the layout editor for the whole drag
    /// because the overlay had taken the gesture. The overlay now publishes its own, which tracks what
    /// the drag is really snapping to and is null the moment nothing is in range.</para>
    /// </summary>
    [Fact]
    public void DraggingAWireByAVertex_PublishesAMarkerThatTracksAndThenClears()
    {
        var vm = new WBondViewModel(Design(1));
        var foot = vm.Design.AllWires().First().Points[0];

        long grabX = WBondSnap.ToDbu(foot.X, Dbu), grabY = WBondSnap.ToDbu(foot.Y, Dbu);
        long padX = grabX + 200_000, padY = grabY;
        var overlay = Overlay(vm, PadAt(padX, padY));

        Assert.True(overlay.OnPointerPressed(grabX, grabY, 500, KeyModifiers.None, 1));

        // Drag onto the pad's corner: the marker is the corner it landed on…
        Assert.True(overlay.OnPointerMoved(padX + 800, padY + 800, 500,
                                           leftButtonDown: true, KeyModifiers.None));
        Assert.NotNull(overlay.SnapMarker);

        // …and dragging on into open space leaves nothing marked, rather than a stale glyph sitting on
        // the vertex the wire has been dragged off.
        Assert.True(overlay.OnPointerMoved(9_000_000, 9_000_000, 500, leftButtonDown: true, KeyModifiers.None));
        Assert.Null(overlay.SnapMarker);
    }

    /// <summary>
    /// The marker is scoped to a LIVE gesture. Once the drag is released there is nothing to snap and
    /// nothing should be marked — a glyph that outlives its gesture is exactly the reported bug in the
    /// other direction.
    /// </summary>
    [Fact]
    public void AfterTheGestureEnds_NothingIsMarked()
    {
        var vm = new WBondViewModel(Design(1));
        var foot = vm.Design.AllWires().First().Points[0];
        long grabX = WBondSnap.ToDbu(foot.X, Dbu), grabY = WBondSnap.ToDbu(foot.Y, Dbu);

        var overlay = Overlay(vm, PadAt(grabX + 200_000, grabY));

        overlay.OnPointerPressed(grabX, grabY, 500, KeyModifiers.None, 1);
        overlay.OnPointerMoved(grabX + 200_800, grabY + 800, 500, leftButtonDown: true, KeyModifiers.None);
        Assert.NotNull(overlay.SnapMarker);

        overlay.OnPointerReleased(grabX + 200_800, grabY + 800);
        Assert.Null(overlay.SnapMarker);
    }

    /// <summary>
    /// A GRID snap marks nothing, deliberately: the layout editor's own marker has never marked the
    /// grid either, and a glyph under every cursor position would carry no information.
    /// </summary>
    [Fact]
    public void AGridSnapMarksNothing()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = Overlay(vm);
        overlay.GridPitchNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        overlay.WireDrawArmed = true;

        overlay.OnPointerPressed(0, 0, 500, KeyModifiers.None, 1);
        overlay.OnPointerMoved(9_000_000, 9_000_000, 500, leftButtonDown: false, KeyModifiers.None);

        Assert.Null(overlay.SnapMarker);
    }


    /// <summary>
    /// A ROTATE marks nothing. It swings a wire about a pinned end and consults no snap at all, so any
    /// marker during one could only be a leftover from the gesture before it — which is the frozen-glyph
    /// bug wearing a different hat.
    /// </summary>
    [Fact]
    public void ARotateMarksNothing()
    {
        var vm = new WBondViewModel(Design(1));
        var foot = vm.Design.AllWires().First().Points[0];
        long grabX = WBondSnap.ToDbu(foot.X, Dbu), grabY = WBondSnap.ToDbu(foot.Y, Dbu);

        var overlay = Overlay(vm, PadAt(grabX + 200_000, grabY));

        // A first gesture leaves a marker behind…
        overlay.OnPointerPressed(grabX, grabY, 500, KeyModifiers.None, 1);
        overlay.OnPointerMoved(grabX + 200_800, grabY + 800, 500, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(grabX + 200_800, grabY + 800);

        // …and a rotate press must not surface it. The wire has moved, so grab its foot where it is now.
        var moved = vm.Design.AllWires().First().Points[0];
        overlay.WireRotateArmed = true;
        overlay.OnPointerPressed(WBondSnap.ToDbu(moved.X, Dbu), WBondSnap.ToDbu(moved.Y, Dbu),
                                 500, KeyModifiers.None, 1);

        Assert.Null(overlay.SnapMarker);
    }
    /// <summary>
    /// The layout editor accepts an overlay's marker as a DISPLAY-only one and renders it — and clearing
    /// it really clears the rendered slot, which is the half the frozen-glyph bug needed.
    /// </summary>
    [Fact]
    public void TheLayoutEditorRendersAndClearsAnOverlayMarker()
    {
        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu });
        Assert.Null(layout.Overlay.SnapMarker);

        var candidate = new SnapCandidate(SnapFeatureKind.CornerEndpoint, 1234, 5678, default, false, -1);
        layout.SetOverlaySnapMarker(candidate);

        Assert.Equal(candidate, layout.Overlay.SnapMarker);

        layout.SetOverlaySnapMarker(null);
        Assert.Null(layout.Overlay.SnapMarker);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2. Clicking empty space deselects the LAYOUT too
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A press on genuinely empty space is "deselect everything"</b> (owner: "clicking on the canvas
    /// of the wBond hosted layout does not deselect selected objects in the layout").
    ///
    /// <para>The wire marquee consumes that press, so the layout editor never got to clear its own
    /// selection. The overlay now says the press hit nothing, and the canvas clears it.</para>
    /// </summary>
    [Fact]
    public void APressOnEmptySpace_ReportsItselfSoTheLayoutSelectionCanBeCleared()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = Overlay(vm, PadAt(500_000, 500_000));

        Assert.True(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, KeyModifiers.None, 1));
        Assert.True(overlay.ConsumedPressWasEmptySpace);
    }

    /// <summary>
    /// A press on a WIRE is not an empty-space click — the two selections are deliberately independent
    /// (§6.3), so picking a wire must not throw away a layout selection the user is holding beside it.
    /// </summary>
    [Fact]
    public void APressOnAWire_IsNotAnEmptySpaceClick()
    {
        var vm = new WBondViewModel(Design(1));
        var foot = vm.Design.AllWires().First().Points[0];
        var overlay = Overlay(vm);

        Assert.True(overlay.OnPointerPressed(
            WBondSnap.ToDbu(foot.X, Dbu), WBondSnap.ToDbu(foot.Y, Dbu), 500, KeyModifiers.None, 1));
        Assert.False(overlay.ConsumedPressWasEmptySpace);
    }

    /// <summary>
    /// A press the overlay DECLINED is not reported either — it already reaches the layout editor,
    /// which clears its own selection exactly as it always did. Reporting it as well would be a second
    /// clear on the same click.
    /// </summary>
    [Fact]
    public void ADeclinedPress_IsNotReported()
    {
        var vm = new WBondViewModel(Design(1));

        // Dead centre of a pad: round 4's rule hands this to the layout editor.
        var overlay = Overlay(vm, PadAt(500_000, 500_000));
        Assert.False(overlay.OnPointerPressed(510_000, 510_000, 500, KeyModifiers.None, 1));
        Assert.False(overlay.ConsumedPressWasEmptySpace);

        // …and so is empty space when the wire marquee is off, which is the wirebond-CELL default.
        var cellOverlay = Overlay(vm);
        cellOverlay.WireMarqueeEnabled = false;
        Assert.False(cellOverlay.OnPointerPressed(9_000_000, 9_000_000, 500, KeyModifiers.None, 1));
        Assert.False(cellOverlay.ConsumedPressWasEmptySpace);
    }

    /// <summary>
    /// The canvas is what joins the two — asserted in source, because <c>Ui.Tests</c> calls no Avalonia
    /// runtime API and this is the wiring that made all three reports one fix.
    /// </summary>
    [Fact]
    public void TheCanvasPushesTheMarkerAndClearsTheLayoutSelection()
    {
        var code = Read("src", "Ui", "Controls", "LayoutCanvas.cs");

        // Pushed after every gesture the overlay consumed — press, move AND release.
        Assert.Equal(3, System.Text.RegularExpressions.Regex
            .Matches(code, @"PushOverlaySnapMarker\(\);").Count);

        Assert.Contains("_viewModel?.SetOverlaySnapMarker(_canvasOverlay?.SnapMarker)", code, StringComparison.Ordinal);
        Assert.Contains("if (_canvasOverlay.ConsumedPressWasEmptySpace)", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.DeselectAllCommand.Execute(null)", code, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!, .. parts]));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3. Update Layout from Schematic writes the cell's wires (§9.5/WB41)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The wires land beside the <c>.clay</c>, as the cell's own <c>.wBond</c></b> (owner: "Update
    /// Layout from Schematic with a wBond component will create a layout, but no wBond component").
    ///
    /// <para>A wBond has no layout view to place — WB23 keeps every wire out of the <c>.clay</c> — so
    /// the generator resolved nothing for it and logged "no layout view — skipped". The sidecar is where
    /// the wires actually belong, and it is the SAME file <c>WBondCell</c> already loads, which is why
    /// this one change also answers "will the wires be shown when I open the .clay?".</para>
    /// </summary>
    [Fact]
    public void UpdateLayoutFromSchematic_WritesTheCellsWirebondFile()
    {
        using var cell = new TempCell("amp");

        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(null, "W1"));

        var result = WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        Assert.Equal(WBondCellSeeding.Outcome.Created, result.Outcome);
        Assert.True(result.HasSidecar);
        Assert.True(File.Exists(cell.WBondPath));

        // …and it is exactly the file the layout side goes looking for.
        Assert.Equal(result.Path, WBondCell.FindFor(cell.ClayPath));

        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(layout, cell.ClayPath));
        Assert.True(layout.HasWireDesign);
        Assert.True(layout.WireDesign!.WireCount > 0);

        // WB23: the artwork is untouched — no wire became a shape.
        Assert.Empty(layout.Model.Shapes);
    }

    /// <summary>
    /// <b>A pre-2026-08-17 cell keeps the wires it has, and is not given a second set.</b> Seeding a
    /// fresh file into <c>layout/</c> would SHADOW the legacy one (attachment resolution prefers the
    /// stem-paired file), so the wires the user has been editing would silently stop being the ones
    /// drawn and simulated — a re-run of Update Layout would quietly revert their work to whatever the
    /// schematic payload last held. Keep theirs; name the move.
    /// </summary>
    [Fact]
    public void ALegacyRootWirebondFile_IsKept_AndNotShadowedByANewOne()
    {
        using var cell = new TempCell("amp");

        // What a pre-move workspace looks like: wires at the cell root, edited to 3 wires in layout.
        string legacy = Path.Combine(cell.CellDir, "amp.wBond");
        WBondIo.WriteFile(legacy, Design(3));

        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(null, "W1"));

        var result = WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        Assert.Equal(WBondCellSeeding.Outcome.KeptExisting, result.Outcome);
        Assert.Equal(legacy, result.Path);
        Assert.False(File.Exists(cell.WBondPath));                 // nothing was written to shadow it
        Assert.Contains(result.Messages, m => m.Contains("layout/amp.wBond", StringComparison.Ordinal));

        // …and the layout still resolves to the user's edited wires, not to a regenerated pair.
        Assert.Equal(legacy, WBondCell.FindFor(cell.ClayPath));
    }

    /// <summary>A schematic with no wBond is left completely alone, and says nothing.</summary>
    [Fact]
    public void NoWBondInTheSchematic_WritesNothingAndSaysNothing()
    {
        using var cell = new TempCell("amp");

        var result = WBondCellSeeding.Seed(new SchematicEditModel(), cell.CellDir, "amp");

        Assert.Equal(WBondCellSeeding.Outcome.NoWBond, result.Outcome);
        Assert.Empty(result.Messages);
        Assert.Empty(Directory.GetFiles(cell.LayoutDir, "*.wBond"));
    }

    /// <summary>
    /// <b>A re-run never overwrites wires the user has moved.</b> That is the whole reason WB41 refuses
    /// to make this a PCell: a generator would regenerate over the layout-driven edits on every run.
    /// The sidecar is written once and thereafter kept, with the remedy for reconciling it named.
    /// </summary>
    [Fact]
    public void ARerun_KeepsTheWiresTheUserEditedInTheLayout()
    {
        using var cell = new TempCell("amp");
        string sidecar = cell.WBondPath;

        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(null, "W1"));
        WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        // The user edits the wires in the layout — a second wire in the same array.
        var edited = WBondIo.ReadFile(sidecar);
        edited.Arrays[0].Wires.Add(LoopShape.CreateSeedWire(
            Point3.Mils(50, 50, 4), Point3.Mils(150, 50, 1),
            WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
            WBondViewModel.DefaultNewWireLoopHeightNm));
        WBondIo.WriteFile(sidecar, edited);

        var again = WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        Assert.Equal(WBondCellSeeding.Outcome.KeptExisting, again.Outcome);
        Assert.Equal(2, WBondIo.ReadFile(sidecar).WireCount);   // the edit survived

        // …and it says NOTHING about it (owner, 2026-08-17: "I already know that the wires are in the
        // layout. I am updating it, so why would the system give me this warning?"). The expected
        // outcome stated as a warning trains people to skim the pane, which costs the messages that
        // matter — the DRIFT one below is what still has to be said.
        Assert.Empty(again.Messages);
    }

    /// <summary>
    /// An array ADDED on the schematic since the sidecar was written is <b>merged into it</b>, and the
    /// addition is named.
    ///
    /// <para><b>This test used to assert the opposite</b> — <c>KeptExisting</c> plus a "pins have moved"
    /// drift report — and the owner reported that as a bug on 2026-08-17: <i>"add another array, then do
    /// another Update Layout from Schematic, the new array that I created in schematic does not show up
    /// in the layout."</i> WB41's never-overwrite rule is right about EXISTING arrays and was wrong
    /// about a new one: adding an array touches no wire that is already drawn, so refusing to add it
    /// protected nothing and dropped the thing the command had just been asked to do. Drift is still
    /// reported for the direction that genuinely cannot be resolved — an array drawn in the layout that
    /// the component no longer declares (<see cref="AnArrayOnlyInTheLayout_IsKeptAndReported"/>).</para>
    /// </summary>
    [Fact]
    public void AnArrayAddedOnTheSchematic_IsMergedIntoTheSidecar()
    {
        using var cell = new TempCell("amp");

        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);
        WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        // The schematic gains an array; the sidecar still has one.
        var grown = WBondEmbedding.DefaultDesign();
        grown.Arrays.Add(new WireArray
        {
            Name = "G2",
            Wires = { LoopShape.CreateSeedWire(
                Point3.Mils(0, 20, 4), Point3.Mils(30, 20, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
                WBondViewModel.DefaultNewWireLoopHeightNm) },
        });
        WBondPlacement.ApplyDesign(comp, grown);

        var again = WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        Assert.Equal(WBondCellSeeding.Outcome.Merged, again.Outcome);
        Assert.Contains("'G2'", again.Messages[0], StringComparison.Ordinal);

        var onDisk = WBondIo.ReadFile(Path.Combine(cell.CellDir, "layout", "amp.wBond"));
        Assert.Equal(2, onDisk.Arrays.Count);
        Assert.Equal("G2", onDisk.Arrays[1].Name);
    }

    /// <summary>
    /// The direction that genuinely cannot be resolved: an array drawn in the LAYOUT that the component
    /// no longer declares. It is kept and reported — deleting is the one direction that destroys drawn
    /// work irrecoverably, and the array may have been removed from the component by accident.
    ///
    /// <para>The remedy named must match this direction. The message this replaces said "use Update
    /// Schematic from Layout, or delete the file to re-seed it", which told a user who had just ADDED an
    /// array on the schematic to pull the layout back over it — i.e. to throw that array away.</para>
    /// </summary>
    [Fact]
    public void AnArrayOnlyInTheLayout_IsKeptAndReported()
    {
        using var cell = new TempCell("amp");

        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);
        WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        // The layout gains an array the component knows nothing about.
        string sidecar = Path.Combine(cell.CellDir, "layout", "amp.wBond");
        var edited = WBondIo.ReadFile(sidecar);
        edited.Arrays.Add(new WireArray
        {
            Name = "D1",
            Wires = { LoopShape.CreateSeedWire(
                Point3.Mils(0, 40, 4), Point3.Mils(30, 40, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
                WBondViewModel.DefaultNewWireLoopHeightNm) },
        });
        WBondIo.WriteFile(sidecar, edited);

        var again = WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        Assert.Equal(2, WBondIo.ReadFile(sidecar).Arrays.Count);   // kept, not deleted

        string said = string.Join("\n", again.Messages);
        Assert.Contains("'D1'", said, StringComparison.Ordinal);
        Assert.Contains("add the array back", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Update Schematic from Layout", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two wBonds in one schematic have no single answer — merging their arrays would break each one's
    /// array-to-pin mapping. The first is written and the rest are NAMED, so what is missing from the
    /// layout can be read rather than guessed at.
    /// </summary>
    [Fact]
    public void ASecondWBond_IsNamedRatherThanSilentlyDropped()
    {
        using var cell = new TempCell("amp");

        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(null, "W1"));
        model.Components.Add(WBondPlacement.BuildCarrying(null, "W2"));

        var result = WBondCellSeeding.Seed(model, cell.CellDir, "amp");

        Assert.Equal(WBondCellSeeding.Outcome.Created, result.Outcome);
        Assert.Contains(result.Messages, m => m.Contains("'W2'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A wBond is no longer offered to the instance generator at all, so it can never be reported as
    /// "no layout view — skipped" again. WB23 is the reason: there is no instance to place.
    /// </summary>
    [Fact]
    public void TheGeneratorNoLongerTreatsAWBondAsAnInstance()
    {
        var code = Read("src", "Ui", "Layout", "SchematicToLayoutGenerator.cs");

        int at = code.IndexOf("private static bool IsPhysical", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("SymbolKind.WBond", code[at..(at + 900)], StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout view attaches an overlay that arrives while the document is ALREADY open — which is
    /// the ordinary case now, since Update Layout from Schematic writes the sidecar into a layout it has
    /// just brought to the front. <c>WireDesign</c> is assigned LAST for exactly this reason.
    /// </summary>
    [Fact]
    public void AttachingWiresToAnOpenSession_NotifiesTheView()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.WBondPath, Design(2));

        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }, cell.ClayPath);

        bool overlayReadyWhenNotified = false;
        layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LayoutEditorViewModel.WireDesign) && layout.WireDesign is not null)
                overlayReadyWhenNotified = layout.WireOverlay is not null && layout.WireEditor is not null;
        };

        Assert.True(WBondCell.TryAttach(layout, cell.ClayPath));
        Assert.True(overlayReadyWhenNotified,
            "WireDesign must be the LAST assignment in AttachWireDesign — a view attaching the overlay " +
            "on that notification would otherwise find it null.");

        // …and the view really does watch that property.
        Assert.Contains("nameof(LayoutEditorViewModel.WireDesign)",
                        Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"),
                        StringComparison.Ordinal);
    }

    /// <summary><c>&lt;cell&gt;/layout/&lt;cell&gt;.clay</c> on disk, cleaned up afterwards.</summary>
    private sealed class TempCell : IDisposable
    {
        public string Root { get; }
        public string CellDir { get; }
        public string LayoutDir { get; }
        public string ClayPath { get; }

        /// <summary>Where the wires belong since WB40's 2026-08-17 revision: in <c>layout/</c>, stem-paired
        /// with the <c>.clay</c>. NOT the cell root, which is now the legacy branch.</summary>
        public string WBondPath { get; }

        public TempCell(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), "crf-wb5-" + Guid.NewGuid().ToString("N")[..8]);
            CellDir = Path.Combine(Root, name);
            LayoutDir = Path.Combine(CellDir, "layout");
            Directory.CreateDirectory(LayoutDir);
            ClayPath = Path.Combine(LayoutDir, name + ".clay");
            WBondPath = Path.Combine(LayoutDir, name + ".wBond");
            File.WriteAllText(ClayPath, "{}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4. A wirebond cell in the LAYOUT editor: undo, and marquee
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Undo reaches a wire edit made from the layout</b> (owner: "Undo does not work from the layout
    /// on a wire").
    ///
    /// <para>It never could: the workspace routes Undo to <c>LayoutDocument.UndoRedo</c>, which is the
    /// session's COMMAND stack, and a wire edit lives in the wires' own SNAPSHOT stack. The two cannot
    /// be merged, so the document answers "what did I do last" from the <c>EditSequence</c> stamps.</para>
    /// </summary>
    [Fact]
    public void UndoFromTheLayout_ReachesAWireEdit()
    {
        var (layout, doc) = WirebondCell(wires: 3);
        Assert.False(doc.CanUndoLast);

        layout.WireEditor!.SelectAllWires();
        Assert.True(layout.WireEditor.DeleteSelectedWires() > 0);

        Assert.True(doc.CanUndoLast);
        Assert.True(layout.UndoTakesWires);

        doc.UndoLast();
        Assert.Equal(3, layout.WireDesign!.WireCount);

        // …and Redo puts it back.
        Assert.True(doc.CanRedoLast);
        doc.RedoLast();
        Assert.NotEqual(3, layout.WireDesign.WireCount);
    }

    /// <summary>
    /// <b>Whichever was edited LAST is what Undo takes.</b> Routing by focus is wrong (a wire drag
    /// happens on the layout canvas) and "layout first" would undo a shape move made ten minutes ago
    /// instead of the wire just dragged.
    /// </summary>
    [Fact]
    public void UndoTakesWhicheverHistoryWasEditedLast()
    {
        var (layout, doc) = WirebondCell(wires: 2);

        // A wire edit, then an artwork edit: the artwork's entry is the newer one.
        layout.WireEditor!.SelectAllWires();
        layout.WireEditor.StraightenSelection();
        layout.Execute(new AddShapeCommand(layout.Model,
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));

        Assert.False(layout.UndoTakesWires);
        doc.UndoLast();
        Assert.Empty(layout.Model.Shapes);

        // …and now the wire edit is once again the most recent thing done.
        Assert.True(layout.UndoTakesWires);
    }

    /// <summary>
    /// A layout with NO wires behaves exactly as it always did — the whole point of defaulting the new
    /// interface members to the single-stack behaviour.
    /// </summary>
    [Fact]
    public void AnOrdinaryLayout_UndoesExactlyAsBefore()
    {
        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu });
        var doc = new LayoutDocument("plain.clay", layout);

        Assert.False(doc.CanUndoLast);
        Assert.False(layout.UndoTakesWires);

        layout.Execute(new AddShapeCommand(layout.Model,
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));

        Assert.True(doc.CanUndoLast);
        doc.UndoLast();
        Assert.Empty(layout.Model.Shapes);
    }

    /// <summary>
    /// The menu label names the entry Undo would REALLY take — a label reading "Undo Add Shape" while
    /// Ctrl+Z undoes a wire drag is worse than no label.
    /// </summary>
    [Fact]
    public void TheUndoLabel_NamesWhicheverHistoryWillBeTaken()
    {
        var (layout, doc) = WirebondCell(wires: 2);

        layout.Execute(new AddShapeCommand(layout.Model,
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));
        Assert.DoesNotContain("wirebond", doc.UndoLastDescription, StringComparison.OrdinalIgnoreCase);

        layout.WireEditor!.SelectAllWires();
        layout.WireEditor.StraightenSelection();
        Assert.Contains("wirebond", doc.UndoLastDescription, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The wire history raises no <c>UndoRedoStack</c> notification, so the workspace has to hear about
    /// it separately or the Undo command stays DISABLED after a wire edit and Ctrl+Z appears to do
    /// nothing — which is what the report actually looked like.
    /// </summary>
    [Fact]
    public void AWireEdit_RaisesItsOwnHistoryNotification()
    {
        var (layout, _) = WirebondCell(wires: 2);

        int notifications = 0;
        layout.WireHistoryChanged += () => notifications++;

        layout.WireEditor!.SelectAllWires();
        layout.WireEditor.StraightenSelection();
        Assert.True(notifications > 0);

        int afterEdit = notifications;
        layout.UndoLast();
        Assert.True(notifications > afterEdit, "an undo moves the history too, so it must notify as well");
    }

    /// <summary>
    /// <b>One marquee, both selections</b> (owner: "I also cannot select wires using marquee select").
    ///
    /// <para>In a wirebond cell the artwork is the subject, so the LAYOUT's own marquee keeps the
    /// gesture — the overlay declines every event — but the wires are content of that same cell and a
    /// box round them has to pick them up. The overlay follows the box without consuming anything, which
    /// is §6.3's "two independent selections held at once" applied to a drag instead of a click.</para>
    /// </summary>
    [Fact]
    public void AMarqueeInAWirebondCell_SelectsTheWiresWithoutStealingTheGesture()
    {
        var (layout, _) = WirebondCell(wires: 3);
        var overlay = layout.WireOverlay!;
        overlay.SnapEnabled = false;

        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);

        // Every event DECLINED — the layout editor's own marquee runs for the same drag.
        Assert.False(overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1));
        Assert.False(overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None));

        // …and the wires inside the box are previewed live while it is dragged.
        Assert.NotNull(layout.WireEditor!.PreviewSelection);

        Assert.False(overlay.OnPointerReleased(-far, -far));

        Assert.Equal(3, layout.WireEditor.Selection.TouchedWires().Count);
        Assert.Null(layout.WireEditor.PreviewSelection);   // no stale highlight outlives the gesture
    }

    /// <summary>
    /// A press that lands ON layout geometry is a MOVE drag, not a marquee — no companion box, or
    /// nudging a bond pad would silently replace the wire selection.
    /// </summary>
    [Fact]
    public void APressOnLayoutGeometryInAWirebondCell_StartsNoCompanionMarquee()
    {
        var (layout, _) = WirebondCell(wires: 3);
        layout.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0), X1 = 500_000, Y1 = 500_000, X2 = 520_000, Y2 = 520_000,
        });

        var overlay = layout.WireOverlay!;
        overlay.SnapEnabled = false;

        layout.WireEditor!.SelectAllWires();
        var before = layout.WireEditor.Selection;

        Assert.False(overlay.OnPointerPressed(510_000, 510_000, 500, KeyModifiers.None, 1));
        Assert.False(overlay.OnPointerMoved(600_000, 600_000, 500, leftButtonDown: true, KeyModifiers.None));
        Assert.False(overlay.OnPointerReleased(600_000, 600_000));

        Assert.Same(before, layout.WireEditor.Selection);
    }

    /// <summary>
    /// The COMPANION marquee draws no box of its own: the layout editor draws one for the same gesture,
    /// and a second at the same coordinates is a visible double stroke.
    /// </summary>
    [Fact]
    public void TheCompanionMarquee_DrawsNoBoxOfItsOwn()
    {
        var (layout, _) = WirebondCell(wires: 2);
        var overlay = layout.WireOverlay!;
        overlay.SnapEnabled = false;

        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);
        overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1);
        overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None);

        // MarqueePreview is what the overlay's OWN box publishes — null here, because this box is not
        // the overlay's. The wire preview itself lives on the view model and IS set (asserted above).
        Assert.Null(overlay.MarqueePreview);
    }

    /// <summary>
    /// The wBond EDITOR is unchanged: there the wires are the subject, the overlay owns the marquee
    /// outright, and it consumes the gesture.
    /// </summary>
    [Fact]
    public void TheWBondEditorsOwnMarquee_StillConsumesTheGesture()
    {
        var vm = new WBondViewModel(Design(3));
        var overlay = Overlay(vm);
        overlay.SnapEnabled = false;

        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);
        Assert.True(overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None));
        Assert.NotNull(overlay.MarqueePreview);
        Assert.True(overlay.OnPointerReleased(-far, -far));

        Assert.Equal(3, vm.Selection.TouchedWires().Count);
    }


    /// <summary>
    /// <b>Pressing a bond pad no longer throws away the wire selection.</b> Found while wiring the
    /// companion marquee, and it is a real defect of its own: the wire selection was resolved BEFORE the
    /// overlay discovered the press belonged to the layout editor, so nudging a pad silently cleared the
    /// wires the user had picked.
    ///
    /// <para>That contradicts §6.3's own contract — "neither selection clears the other, so 'select the
    /// pads and the wires landing on them' is one gesture and one selection" — which is the entire basis
    /// for holding both at once. A press on genuinely EMPTY space still clears, because nothing was
    /// clicked.</para>
    /// </summary>
    [Fact]
    public void PressingLayoutGeometry_LeavesTheWireSelectionAlone()
    {
        var vm = new WBondViewModel(Design(3));
        var overlay = Overlay(vm, PadAt(500_000, 500_000));   // the wBond editor's own configuration
        overlay.SnapEnabled = false;

        vm.SelectAllWires();
        Assert.Equal(3, vm.Selection.TouchedWires().Count);

        // Dead centre of the pad: the layout editor's press…
        Assert.False(overlay.OnPointerPressed(510_000, 510_000, 500, KeyModifiers.None, 1));
        Assert.Equal(3, vm.Selection.TouchedWires().Count);

        // …and empty space still means "nothing is selected".
        Assert.True(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, KeyModifiers.None, 1));
        Assert.True(vm.Selection.IsEmpty);
    }

    /// <summary>
    /// <b>One rule, two editors.</b> The wBond editor and a wirebond cell in the ordinary Layout Editor
    /// ask the identical question — which of these two histories did the user touch last — so both route
    /// through <c>EditSequence</c> rather than each carrying its own comparison, which is two chances to
    /// get the direction wrong.
    /// </summary>
    [Fact]
    public void BothEditorsRouteUndoThroughTheSharedRule()
    {
        foreach (var file in new[]
        {
            Path.Combine("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs"),
            Path.Combine("src", "Ui", "Layout", "LayoutEditorViewModel.Wires.cs"),
        })
        {
            string code = Read(file.Split(Path.DirectorySeparatorChar));
            Assert.Contains("EditSequence.UndoTakesFirst", code, StringComparison.Ordinal);
            Assert.Contains("EditSequence.RedoTakesFirst", code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Undo takes the more recent entry; redo takes the OLDEST undone one, because that is the entry the
    /// last undo produced. Asserted on the arithmetic itself — the two directions are easy to write the
    /// same way by accident.
    /// </summary>
    [Fact]
    public void TheSharedRule_ComparesInOppositeDirectionsForUndoAndRedo()
    {
        Assert.True(EditSequence.UndoTakesFirst(true, 9, true, 4));
        Assert.False(EditSequence.UndoTakesFirst(true, 4, true, 9));

        Assert.False(EditSequence.RedoTakesFirst(true, 9, true, 4));
        Assert.True(EditSequence.RedoTakesFirst(true, 4, true, 9));

        // …and a history with nothing in it never wins, whatever its stamp says.
        Assert.False(EditSequence.UndoTakesFirst(false, 99, true, 1));
        Assert.True(EditSequence.UndoTakesFirst(true, 1, false, 99));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5. The docked panels, and the parameter dialog (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The wires follow the LAYOUT's snap and display unit.</b> In the wBond editor
    /// <c>WBondDocumentViewModel</c> keeps these in step; a wirebond cell in the ordinary Layout Editor
    /// had nothing doing it, so the wire grid was drawn at pitch 0 (no grid at all in the docked Wire
    /// Profile view) and its rulers stayed on the wBond default while the layout's Unit box said
    /// something else.
    /// </summary>
    [Fact]
    public void AWirebondCellsWires_FollowTheLayoutsSnapAndUnit()
    {
        var (layout, _) = WirebondCell(wires: 2);

        layout.SnapDbu = LayoutUnits.ToDbu(2m, LayoutUnit.Mil, Dbu);
        layout.DisplayUnit = LayoutUnit.Mil;

        Assert.Equal(WBondUnits.ToNm(2.0, WBondUnit.Mil), layout.WireGridPitchNm);
        Assert.Equal(WBondUnits.ToNm(2.0, WBondUnit.Mil), layout.WireOverlay!.GridPitchNm);
        Assert.Equal(WBondUnit.Mil, layout.WireEditor!.DisplayUnit);

        // …and both follow a later change, because there is ONE Snap box and ONE Unit box in that editor.
        layout.DisplayUnit = LayoutUnit.Um;
        Assert.Equal(WBondUnit.Um, layout.WireEditor.DisplayUnit);

        layout.SnapDbu = LayoutUnits.ToDbu(5m, LayoutUnit.Um, Dbu);
        Assert.Equal(WBondUnits.ToNm(5.0, WBondUnit.Um), layout.WireOverlay.GridPitchNm);
    }

    /// <summary>
    /// The docked Wire Profile panel gets that pitch too — it draws its own grid from a number no other
    /// object can derive for it, and left unset the panel showed no grid at all.
    /// </summary>
    [Fact]
    public void TheDockedProfileTool_CarriesTheLayoutsGridPitch()
    {
        var (layout, _) = WirebondCell(wires: 2);
        layout.SnapDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Mil, Dbu);

        var tool = new WBondProfileTool();
        tool.SetActiveLayout(layout);

        Assert.Equal(WBondUnits.ToNm(1.0, WBondUnit.Mil), tool.GridPitchNm);

        // …live, so changing Snap in the layout redraws the docked grid.
        layout.SnapDbu = LayoutUnits.ToDbu(10m, LayoutUnit.Mil, Dbu);
        Assert.Equal(WBondUnits.ToNm(10.0, WBondUnit.Mil), tool.GridPitchNm);

        // An ordinary layout leaves it at zero — no wires, no grid to draw.
        tool.SetActiveLayout(new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }));
        Assert.Equal(0, tool.GridPitchNm);
    }

    /// <summary>
    /// <b>Selecting an array from the docked panel repaints the layout showing those wires</b> (owner:
    /// "when the array name is double clicked in the Array Inductance panel, the wire arrays are not
    /// selected in the layout host").
    ///
    /// <para>The selection DID change — nothing repainted. A selection changes no geometry, so it raises
    /// no <c>ReadoutChanged</c>, and the overlay itself was never touched.</para>
    /// </summary>
    [Fact]
    public void SelectingAnArrayFromThePanel_RepaintsTheLayoutsOverlay()
    {
        var (layout, _) = WirebondCell(wires: 3);

        int repaints = 0;
        layout.WireOverlay!.OverlayChanged += () => repaints++;

        layout.WireEditor!.SelectArray(0);

        Assert.Equal(3, layout.WireEditor.Selection.TouchedWires().Count);
        Assert.True(repaints > 0, "a selection change has to reach the canvas drawing those wires");
    }

    /// <summary>
    /// The docked Wire Profile view highlights the same selection, for the same reason — it repaints on
    /// <c>ReadoutChanged</c>, which a selection does not raise.
    /// </summary>
    [Fact]
    public void TheProfileView_RepaintsOnASelectionChange()
    {
        var code = Read("src", "Ui", "Views", "WBond", "WBondProfileView.axaml.cs");

        int at = code.IndexOf("OnEditorPropertyChanged", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("nameof(WBondViewModel.Selection)", code[at..], StringComparison.Ordinal);
    }

    /// <summary>
    /// A dock TAB already says "Array Inductance", so the panel must not say it again (owner). Inline in
    /// the wBond editor there is no tab, and the heading is the only label there is.
    /// </summary>
    [Fact]
    public void TheDockedInductancePanel_DoesNotRepeatItsOwnTitle()
    {
        Assert.Equal("Array Inductance", new WBondInductanceTool().Title);

        Assert.Contains("Panel.ShowHeading = false;",
                        Read("src", "Ui", "Views", "WBond", "WBondInductanceToolView.axaml.cs"),
                        StringComparison.Ordinal);

        // …and the wBond editor does NOT suppress it.
        Assert.DoesNotContain("ShowHeading",
                              Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs"),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Temp and GroundPlane sit at the BOTTOM of the wBond panel</b> (owner). They are ordinary engine
    /// values and belong under what the component IS — its wires, arrays and artwork — rather than in
    /// front of it. Every type with no custom panel of its own is unaffected, which is all the rest.
    /// </summary>
    [Fact]
    public void TheGenericRows_ComeAfterTheWBondPanel()
    {
        var xaml = Read("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml");

        int wbond   = xaml.IndexOf("IsVisible=\"{Binding IsWBond}\"", StringComparison.Ordinal);
        int generic = xaml.IndexOf("ItemsSource=\"{Binding Rows}\"", StringComparison.Ordinal);

        Assert.True(wbond >= 0 && generic >= 0);
        Assert.True(wbond < generic, "the wBond panel must come before the generic rows in the StackPanel");
    }

    /// <summary>
    /// <b>Update Layout writes only the component the user is editing.</b> Every other wBond in the
    /// schematic is named rather than written, so the file the user gets holds exactly what they asked
    /// for.
    /// </summary>
    [Fact]
    public void UpdateLayoutForOneWBond_WritesThatComponentAndNamesTheRest()
    {
        using var cell = new TempCell("amp");

        var model = new SchematicEditModel();
        var first = WBondPlacement.BuildCarrying(null, "W1");
        var second = WBondPlacement.BuildCarrying(TwoArrayDesign(), "W2");
        model.Components.Add(first);
        model.Components.Add(second);

        var result = WBondCellSeeding.Seed(model, cell.CellDir, "amp", only: second);

        Assert.Equal(WBondCellSeeding.Outcome.Created, result.Outcome);
        Assert.Equal(2, WBondIo.ReadFile(result.Path!).Arrays.Count);   // W2's design, not W1's
        Assert.Contains(result.Messages, m => m.Contains("'W1'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A component deleted while the dialog was open is REFUSED by name, never silently replaced by a
    /// different wBond's wires.
    /// </summary>
    [Fact]
    public void UpdateLayoutForAWBondThatIsGone_IsRefusedByName()
    {
        using var cell = new TempCell("amp");

        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(null, "W1"));
        var removed = WBondPlacement.BuildCarrying(null, "W9");

        var result = WBondCellSeeding.Seed(model, cell.CellDir, "amp", only: removed);

        Assert.Equal(WBondCellSeeding.Outcome.NoWBond, result.Outcome);
        Assert.Contains("'W9'", result.Messages[0], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(cell.LayoutDir, "*.wBond"));
    }

    /// <summary>
    /// The button is absent with no workspace to write into — one that could only report "no workspace is
    /// open" is worse than none, and the schematic-wide command refuses on the same grounds.
    /// </summary>
    [Fact]
    public void TheUpdateLayoutButton_IsAbsentWithNoWorkspace()
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: true);

        Assert.True(editor.IsWBond);
        Assert.False(editor.CanUpdateWBondLayout);

        vm.UpdateWBondLayout = (_, _) => { };
        editor.SetTargetDirect(vm, comp, showClose: true);
        Assert.True(editor.CanUpdateWBondLayout);
    }

    /// <summary>
    /// It runs the update and THEN asks its host to close — driven by the view model's own event, not by a
    /// second <c>Click</c> handler, because Avalonia raises <c>Click</c> BEFORE it executes
    /// <c>Command</c> and closing first would tear the DataContext down before the update ran.
    /// </summary>
    [Fact]
    public void TheUpdateLayoutButton_UpdatesThenAsksItsHostToClose()
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);

        var updated = new System.Collections.Generic.List<string>();
        vm.UpdateWBondLayout = (_, c) => updated.Add(c.InstanceName);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: true);

        int closes = 0;
        editor.WBondLayoutUpdated += () => closes++;

        editor.UpdateWBondLayoutCommand.Execute(null);

        Assert.Equal(["W1"], updated);
        Assert.Equal(1, closes);

        // The XAML must NOT also carry a Click handler on that button — see the summary above.
        var xaml = Read("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml");
        int at = xaml.IndexOf("UpdateWBondLayoutCommand", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.DoesNotContain("Click=", xaml[(at - 400)..(at + 200)], StringComparison.Ordinal);
    }

    /// <summary>
    /// It is narrower than the Design-menu command by construction: the instance generator does not run
    /// at all, so nothing else in the layout can move under the user while they are editing wires.
    /// </summary>
    [Fact]
    public void AWBondOnlyUpdate_NeverRunsTheInstanceGenerator()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.SchematicToLayout.cs");

        int at = code.IndexOf("private void RunLayoutUpdate", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = code[at..code.IndexOf("private void SeedWBondSidecar", StringComparison.Ordinal)];
        int guard = body.IndexOf("if (onlyWBond is null)", StringComparison.Ordinal);
        int run   = body.IndexOf("SchematicToLayoutGenerator.Run", StringComparison.Ordinal);

        Assert.True(guard >= 0 && run > guard, "the generator must sit inside the onlyWBond-is-null guard");
    }


    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6. The array double-click, properly this time — and the profile view's snap
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The panel's gestures act on an editor that cannot go stale</b> (owner, 2026-08-17, reported
    /// twice: "the array name double-click is still not working").
    ///
    /// <para>The first fix made the selection REPAINT, and was inert because the selection never
    /// happened. The view had its editor pushed in by each host, and the docked host pushed it exactly
    /// once — on its own <c>DataContextChanged</c>, which fires when the TOOL is bound and never again,
    /// while the editor that tool points at changes with every document activation. It was null for the
    /// life of the panel and every gesture returned immediately.</para>
    ///
    /// <para>It lives on the FORMATTER now: every host that has rows to format has the editor that
    /// produced them, and both are assigned together, so there is no second moment to forget.</para>
    /// </summary>
    [Fact]
    public void ThePanelsEditor_FollowsEveryDocumentActivation()
    {
        var tool = new WBondInductanceTool();
        Assert.Null(tool.Panel.Editor);

        // …the shape that used to break it: the tool instance never changes, only its Editor does.
        var first = new WBondViewModel(Design(2));
        tool.SetActiveWBond(first);
        Assert.Same(first, tool.Panel.Editor);

        var second = new WBondViewModel(Design(3));
        tool.SetActiveWBond(second);
        Assert.Same(second, tool.Panel.Editor);

        // …and nothing active means the gestures act on nothing, rather than on the previous document.
        tool.SetActiveWBond(null);
        Assert.Null(tool.Panel.Editor);
    }

    /// <summary>A wirebond cell reaches the panel the same way, through the layout it is following.</summary>
    [Fact]
    public void ThePanelsEditor_FollowsAWirebondCell()
    {
        var (layout, _) = WirebondCell(wires: 3);

        var tool = new WBondInductanceTool();
        tool.SetActiveLayout(layout);

        Assert.Same(layout.WireEditor, tool.Panel.Editor);

        tool.SetActiveLayout(new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }));
        Assert.Null(tool.Panel.Editor);
    }

    /// <summary>The wBond editor's own inline panel gets it from the same place.</summary>
    [Fact]
    public void TheInlinePanelsEditor_IsTheDocumentsOwn()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(2)));
        Assert.Same(document.Editor, document.Panel.Editor);
    }

    /// <summary>
    /// No host pushes it any more — that push is what went stale, and a source scan is the only way to
    /// keep one from being added back.
    /// </summary>
    [Fact]
    public void NoHostPushesTheEditorIntoThePanel()
    {
        foreach (var file in new[] { "WBondInductanceToolView.axaml.cs", "WBondEditorView.axaml.cs" })
            Assert.DoesNotContain(".Editor =",
                                  Read("src", "Ui", "Views", "WBond", file), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The profile view lands points on the grid it draws</b> (owner: "the Wire Profile view is not
    /// respecting the snap resolution").
    ///
    /// <para>It drew the grid and ignored it — exactly the failure the layout overlay's own note warns
    /// about, guarded there when it was written and never guarded here. Grid only, because this canvas's
    /// axes are span and z and there is no artwork in that plane to snap to.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1200.0, 1000.0)]
    [InlineData(1600.0, 2000.0)]
    [InlineData(-1200.0, -1000.0)]
    [InlineData(-1600.0, -2000.0)]
    public void TheProfileCanvas_RoundsOntoItsGrid(double raw, double expected)
    {
        Assert.Equal(expected, WBondProfileCanvas.SnapToPitch(raw, 1000, KeyModifiers.None));
    }

    /// <summary>
    /// Pitch 0 is "no grid" and Alt is the app-wide snap suppressor (R-snp-11) — both leave the point
    /// exactly where the hand put it.
    /// </summary>
    [Fact]
    public void TheProfileCanvasSnap_IsOffAtPitchZeroAndUnderAlt()
    {
        Assert.Equal(1234.5, WBondProfileCanvas.SnapToPitch(1234.5, 0, KeyModifiers.None));
        Assert.Equal(1234.5, WBondProfileCanvas.SnapToPitch(1234.5, 1000, KeyModifiers.Alt));
    }

    /// <summary>
    /// Every place a point is placed in that canvas goes through the snap: the drag's baseline, its
    /// per-frame cursor, and the wire tool's ghost AND commit — which must be the same point, or the wire
    /// lands somewhere the ghost was not.
    /// </summary>
    [Fact]
    public void EveryPlacementInTheProfileCanvas_GoesThroughTheSnap()
    {
        var code = Read("src", "Ui", "Controls", "WBondProfileCanvas.cs");

        Assert.Contains("_lastZNm = SnapNm(z, e.KeyModifiers);", code, StringComparison.Ordinal);
        Assert.Contains("span = SnapNm(span, e.KeyModifiers);", code, StringComparison.Ordinal);

        // Both unproject calls — the ghost's and the commit's — snap, and there are exactly two.
        Assert.Equal(2, System.Text.RegularExpressions.Regex
            .Matches(code, @"ProfileProjection\.Unproject\(SnapNm\(span\), SnapNm\(z\)").Count);
        Assert.DoesNotContain("ProfileProjection.Unproject(span, z", code, StringComparison.Ordinal);

        // Alt-drag scales instead of placing, so it must NOT be snapped — asserted because snapping a
        // scale factor is the obvious wrong generalisation.
        int alt = code.IndexOf("if (_altDrag) { AltDragFrame(span, z); return; }", StringComparison.Ordinal);
        int snap = code.IndexOf("span = SnapNm(span, e.KeyModifiers);", StringComparison.Ordinal);
        Assert.True(alt >= 0 && snap > alt, "the alt-drag branch must return before the snap");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  7. The dockables: their titles, their toolbar buttons, and their first appearance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Each panel's tab says WHOSE wires it is showing</b> (owner, 2026-08-17). A workspace can hold
    /// several cells with a wBond in each and these panels follow whichever layout is active, so a tab
    /// reading only "Wire Profile" says nothing about what is on screen — and the answer changes under
    /// the user as they switch tabs.
    /// </summary>
    [Fact]
    public void BothPanelTitles_NameWhatTheyAreShowing()
    {
        var (layout, _) = WirebondCell(wires: 2);

        var profile = new WBondProfileTool();
        var inductance = new WBondInductanceTool();

        Assert.Equal("Wire Profile", profile.Title);
        Assert.Equal("Array Inductance", inductance.Title);

        profile.SetActiveLayout(layout);
        inductance.SetActiveLayout(layout);

        Assert.Equal("Wire Profile — amp", profile.Title);
        Assert.Equal("Array Inductance — amp", inductance.Title);

        // …and back to the bare name when there is nothing to qualify it.
        profile.SetActiveLayout(new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }));
        Assert.Equal("Wire Profile", profile.Title);
    }

    /// <summary>A wBond DOCUMENT names itself the same way — §10.1's second surface.</summary>
    [Fact]
    public void APanelFollowingAWBondDocument_NamesThatDocument()
    {
        var tool = new WBondInductanceTool();
        tool.SetActiveWBond(new WBondViewModel(Design(2)), "bondlist.wBond");

        Assert.Equal("Array Inductance — bondlist.wBond", tool.Title);

        tool.SetActiveWBond(null);
        Assert.Equal("Array Inductance", tool.Title);
    }

    /// <summary>
    /// The tab's <b>Id</b> never moves with its title — the id is what a <c>.cws</c> stores and what
    /// layout capture/restore matches on, so a retitled panel must still come back where the user put it.
    /// </summary>
    [Fact]
    public void RetitlingAPanel_LeavesItsDockIdAlone()
    {
        var (layout, _) = WirebondCell(wires: 1);

        var tool = new WBondProfileTool();
        tool.SetActiveLayout(layout);

        Assert.Equal(DockPanelIds.WBondProfile, tool.Id);
        Assert.NotEqual(tool.Id, tool.Title);
    }

    /// <summary>
    /// <b>The two toolbar buttons appear only where they can do something</b>: on a wirebond cell, and
    /// only with a dock to put a panel in. The second half is what keeps them out of the standalone wBond
    /// app (owner, 2026-08-17), whose window hosts both panels inline and has no dock at all.
    /// </summary>
    [Fact]
    public void TheWirePanelButtons_AreGatedOnWiresAndOnAWorkspace()
    {
        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");

        int at = code.IndexOf("private void UpdateWirePanelButtonStates", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = code[at..(at + 700)];
        Assert.Contains("HasWireDesign == true", body, StringComparison.Ordinal);
        Assert.Contains("workspace is not null", body, StringComparison.Ordinal);

        // All three chrome elements come and go together — a separator left behind is a stray line.
        foreach (var name in new[] { "WirePanelSeparator", "WireProfileBtn", "WireInductanceBtn" })
            Assert.Contains($"{name}.IsVisible = show;", body, StringComparison.Ordinal);

        // …and the two buttons say whether their panel is on screen (owner, 2026-08-17), read from the
        // dock tree rather than tracked here — a panel is also closed by its own tab X.
        Assert.Contains("UpdateWirePanelCheckedStates();", body, StringComparison.Ordinal);
        Assert.Contains("WireProfileBtn.IsChecked = workspace?.IsToolPanelShowing(DockPanelIds.WBondProfile) == true;",
                        code, StringComparison.Ordinal);
        Assert.Contains("WireInductanceBtn.IsChecked = workspace?.IsToolPanelShowing(DockPanelIds.WBondInductance) == true;",
                        code, StringComparison.Ordinal);
    }

    /// <summary>They TOGGLE, and their tooltips name the keys (owner asked for "(P)" and "(A)").</summary>
    [Fact]
    public void TheWirePanelButtons_ToggleAndNameTheirKeys()
    {
        var xaml = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        Assert.Contains("Wire Profile panel  (P)", xaml, StringComparison.Ordinal);
        Assert.Contains("Array Inductance panel  (A)", xaml, StringComparison.Ordinal);

        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        Assert.Contains("ToggleToolPanelCommand.Execute(panelId)", code, StringComparison.Ordinal);

        // P and A reach the same toggle — their own gates are asserted by ThePanelKeyHandler_KeepsEveryGate.
        Assert.Contains("e.Key == Key.P ? Docking.DockPanelIds.WBondProfile : Docking.DockPanelIds.WBondInductance",
                        Read("src", "Ui", "Views", "WirePanelKeys.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The keys work from EVERY window they can be pressed in, not just the shell.</b>
    ///
    /// <para>Owner, 2026-08-17: <i>"when those windows are floating I can only toggle them twice before I
    /// am forced to click on the canvas — this works perfectly when they are docked."</i> Presenting a
    /// floating panel activates it, so the third press lands in the panel's own OS window — a second
    /// <c>TopLevel</c>, with no handler on it. Docked panels never showed it because everything is inside
    /// the one window.</para>
    ///
    /// <para>The fix is one registration per <c>TopLevel</c>, NOT keeping focus in the shell: stealing
    /// focus back from a window the user just asked to see is the same class of patch that lost to Dock's
    /// focus handling three times, and it would make the panel's own fields untypeable.</para>
    /// </summary>
    [Fact]
    public void ThePanelKeys_AreRegisteredOnFloatingWindowsToo()
    {
        // The shell, and the host window every floating panel lives in.
        Assert.Contains("AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel)",
                        Read("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"), StringComparison.Ordinal);

        var host = Read("src", "Ui", "ViewModels", "Dock", "CrfHostWindow.cs");
        Assert.Contains("Views.WirePanelKeys.Attach(this, () => Views.WorkspaceLocator.For(this));",
                        host, StringComparison.Ordinal);

        // A float has no view model of its own, so it resolves the workspace through the shell that
        // OWNS it (MW1 R-mw1-11/14) — not through whichever workspace window happens to be first in
        // the process, which with two open is an arbitrary one. Finding none (the standalone wBond
        // app) is still a no-op, not a crash.
        var keys = Read("src", "Ui", "Views", "WirePanelKeys.cs");
        Assert.Contains("WorkspaceLocator.For(source)", keys, StringComparison.Ordinal);
        Assert.Contains("if (vm is null || !vm.WirePanelKeysApply) return false;", keys, StringComparison.Ordinal);

        // Tunnel on both, so it is seen whatever has focus WITHIN each window.
        Assert.Contains("RoutingStrategies.Tunnel", keys, StringComparison.Ordinal);

        // And the shortcut is not re-implemented anywhere: one body, two registrations.
        Assert.DoesNotContain("Key.A", Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Both panels are revealed the FIRST time a cell's wires reach its layout, and only then</b>
    /// (owner). Someone who has just generated wires has no reason to know two panels exist; a re-run
    /// leaves the arrangement exactly as they have since set it, because a command that re-opens a panel
    /// you closed on purpose is worse than one that never opened it.
    /// </summary>
    [Fact]
    public void TheTwoPanels_AreRevealedOnlyOnTheFirstSeed()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.SchematicToLayout.cs");

        int at = code.IndexOf("if (seeded.Outcome == WBondCellSeeding.Outcome.Created)", StringComparison.Ordinal);
        Assert.True(at >= 0, "the reveal must be gated on the CREATED outcome, not on every run");

        // The two panel ids moved into ShowWBondPanels on 2026-08-17, which ARRANGES them the first
        // time this installation ever needs them rather than floating two windows over the layout.
        string body = code[at..(at + 200)];
        Assert.Contains("ShowWBondPanels()", body, StringComparison.Ordinal);

        // ShowToolPanel, never ToggleToolPanel: a reveal must not CLOSE a panel that happens to be open.
        var panels = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.WBondPanels.cs");
        Assert.Contains("DockPanelIds.WBondProfile", panels, StringComparison.Ordinal);
        Assert.Contains("DockPanelIds.WBondInductance", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleToolPanel", panels, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The Properties panel switches to the wire inspector when a wire is clicked in a wirebond
    /// cell</b> (owner, 2026-08-17). It had no wire routing at all there: clicking a wire changed
    /// <c>WireEditor.Selection</c>, which the layout inspector cannot see and nothing was watching.
    ///
    /// <para>Wires win a tie, and that is the same rule — and the same reason — the wBond document's own
    /// routing uses: a LAYOUT selection can outlive a wire press, because the overlay consumes a press on
    /// a wire without the layout editor ever seeing it, so reading that stale one as intent would flip the
    /// panel away from the wire just clicked.</para>
    /// </summary>
    [Fact]
    public void TheWirePropertiesContext_IsReachableFromAWirebondCell()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");

        int at = code.IndexOf("private void RefreshLayoutPropertiesContext", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = code[at..(at + 600)];
        Assert.Contains("panel.SetActiveWire(wires)", body, StringComparison.Ordinal);
        Assert.Contains("panel.SetActiveLayout(vm)", body, StringComparison.Ordinal);

        // The wire selection is FOLLOWED, or the panel would only ever switch on a tab change.
        Assert.Contains("WatchWirebondCellProperties", code, StringComparison.Ordinal);
        Assert.Contains("nameof(WBond.WBondViewModel.Selection)", code, StringComparison.Ordinal);

        // …and dropped when a non-layout document takes over, on the same rule as the wBond watch.
        Assert.Contains("if (activeDockable is not LayoutDocument) StopWatchingWirebondCellProperties();",
                        code, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  8. The two dockables come back where they were left (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Docked, both panels survive a full save/reopen cycle exactly where they were put</b> — capture,
    /// JSON, read, and re-apply on a FRESH factory, which is what a relaunch does.
    ///
    /// <para>Worth pinning at this layer because the two are the first panels deliberately ABSENT from
    /// both shipped default layouts: every other panel would still land somewhere plausible from the
    /// default if the restore lost it, so a defect here is invisible on any of them.</para>
    /// </summary>
    [Fact]
    public void BothPanels_SurviveASaveAndReopen_Docked()
    {
        var arranged = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Right, Proportion = 0.25 }],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.ProjectTree,     Side = DockSide.Left,  Group = 0, Order = 0, Active = true, Proportion = 1.0 },
                new CwsDockPanel { Id = DockPanelIds.WBondProfile,    Side = DockSide.Right, Group = 0, Order = 0, Active = true, Proportion = 0.5 },
                new CwsDockPanel { Id = DockPanelIds.WBondInductance, Side = DockSide.Left,  Group = 1, Order = 0, Active = true, Proportion = 0.4 },
            ],
        };

        var reopened = SaveAndReopen(arranged);

        var profile = Assert.Single(reopened.Panels, p => p.Id == DockPanelIds.WBondProfile);
        Assert.True(profile.Open);
        Assert.Equal(DockSide.Right, profile.Side);
        Assert.True(profile.Active);

        var inductance = Assert.Single(reopened.Panels, p => p.Id == DockPanelIds.WBondInductance);
        Assert.True(inductance.Open);
        Assert.Equal(DockSide.Left, inductance.Side);
        Assert.NotEqual(
            Assert.Single(reopened.Panels, p => p.Id == DockPanelIds.ProjectTree).Group,
            inductance.Group);
    }

    /// <summary>
    /// <b>Floating, they come back floating — and at the same place.</b> "Including whether they were
    /// docked or not" is the owner's own wording, and floating is how these two FIRST appear: the
    /// first-seed reveal and the toolbar buttons both open a closed panel in its own window.
    /// </summary>
    [Fact]
    public void BothPanels_SurviveASaveAndReopen_Floating()
    {
        var arranged = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.2 }],
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 500, Y = 300, Width = 420, Height = 320,
                                        Panels = [DockPanelIds.WBondProfile], Active = DockPanelIds.WBondProfile },
                new CwsFloatingWindow { X = 950, Y = 310, Width = 300, Height = 500,
                                        Panels = [DockPanelIds.WBondInductance], Active = DockPanelIds.WBondInductance },
            ],
        };

        var reopened = SaveAndReopen(arranged);

        Assert.DoesNotContain(reopened.Panels, p => p.Id == DockPanelIds.WBondProfile);
        Assert.DoesNotContain(reopened.Panels, p => p.Id == DockPanelIds.WBondInductance);

        var profile = Assert.Single(reopened.FloatingWindows, w => w.Panels.Contains(DockPanelIds.WBondProfile));
        Assert.Equal(500, profile.X);
        Assert.Equal(300, profile.Y);
        Assert.Equal(420, profile.Width);

        var inductance = Assert.Single(reopened.FloatingWindows, w => w.Panels.Contains(DockPanelIds.WBondInductance));
        Assert.Equal(950, inductance.X);
        Assert.Equal(500, inductance.Height);
    }

    /// <summary>
    /// A panel the user CLOSED stays closed. Neither is in a shipped default layout, so nothing fills one
    /// back in — which is the behaviour that makes the two above meaningful rather than accidental.
    /// </summary>
    [Fact]
    public void AClosedPanel_StaysClosedAcrossAReopen()
    {
        var arranged = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.2 }],
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 1.0 }],
        };

        var reopened = SaveAndReopen(arranged);

        Assert.DoesNotContain(reopened.Panels, p => p.Id == DockPanelIds.WBondProfile);
        Assert.DoesNotContain(reopened.FloatingWindows, w => w.Panels.Contains(DockPanelIds.WBondProfile));
        Assert.DoesNotContain(reopened.Panels, p => p.Id == DockPanelIds.WBondInductance);
    }

    /// <summary>
    /// <b>Moving a panel now ARMS the save.</b> That was the whole bug: the `.cws` was written only on an
    /// explicit save, the tree-filter debounce, clean exit or a workspace switch — <b>never because a
    /// panel moved</b> — so an arrangement was recorded only by accident, whenever something unrelated
    /// happened to trigger a save while the panels were where the user wanted them.
    /// </summary>
    [Fact]
    public void ADockRearrangement_ArmsTheWorkspaceSave()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private void WireDockArrangementPersistence", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = code[at..code.IndexOf("private void OnDockArrangementChanged", StringComparison.Ordinal)];
        foreach (var ev in new[] { "DockableDocked", "DockableUndocked", "DockableClosed",
                                   "DockableMoved", "DockableSwapped", "WindowMoveDragEnd",
                                   "WindowOpened", "WindowClosed" })
            Assert.Contains($"_factory.{ev}", body, StringComparison.Ordinal);

        // NOT the bulk events: those fire while a layout is being built, which is not an arrangement
        // change and would arm a write on every rebuild.
        Assert.DoesNotContain("_factory.DockableAdded", body, StringComparison.Ordinal);

        // Activation IS subscribed here since 2026-08-18 — a tab switch changes whether a panel is in
        // VIEW, which the toolbar toggles read — but it must never reach the save. It fires on every
        // click and in bulk during a build; arming a `.cws` write from it is the bug this whole method
        // exists to fix, pointed the other way.
        Assert.Contains("_factory.ActiveDockableChanged += (_, _) => RaiseToolPanelVisibilityChanged();",
                        body, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveDockableChanged += (_, _) => OnDockArrangementChanged()",
                              body, StringComparison.Ordinal);

        Assert.Contains("WireDockArrangementPersistence();",
                        Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>And a REBUILD must not arm it</b> — that guard prevents data loss, not noise. Applying a layout
    /// raises the very events above, so a restore would arm a save of what it just applied; and when a
    /// restore has DEGRADED to the default (R-dock-5), that debounced write would overwrite the user's
    /// good saved arrangement three seconds after they opened the workspace.
    /// </summary>
    [Fact]
    public void ALayoutRebuild_DoesNotArmTheSave()
    {
        var docking = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        Assert.Contains("if (_layoutRebuildDepth > 0) return;", docking, StringComparison.Ordinal);

        // ApplyDockLayout raises the guard around the whole rebuild, InitLayout included.
        int apply = docking.IndexOf("private void ApplyDockLayout(", StringComparison.Ordinal);
        Assert.True(apply >= 0);
        Assert.Contains("_layoutRebuildDepth++", docking[apply..(apply + 300)], StringComparison.Ordinal);
        Assert.Contains("finally { _layoutRebuildDepth--; }", docking[apply..(apply + 300)], StringComparison.Ordinal);

        // …and so does the workspace-open clean-slate rebuild, which is the one that could clobber.
        Assert.Contains("WhileRebuildingLayout(() =>",
                        Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"), StringComparison.Ordinal);
    }


    /// <summary>
    /// <b>A panel docked BESIDE the documents comes back beside the documents</b> (owner, 2026-08-17: the
    /// Array Inductance panel docked immediately left of the layout came back <i>below the Properties
    /// inspector</i>).
    ///
    /// <para>Its <see cref="CwsDockPanel.Side"/> was captured correctly as Left — what the schema could
    /// not say was WHICH left column. There are two: the outer one at the window edge, and one between it
    /// and the document tabs. With only "Left" to work from, the restore put it in the one it could
    /// express: another row of the outer column, under whatever was already there.</para>
    /// </summary>
    [Fact]
    public void APanelDockedBesideTheDocuments_ComesBackBesideTheDocuments()
    {
        var arranged = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.22 }],
            Panels =
            [
                // The outer left column, as shipped.
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 0.55 },
                new CwsDockPanel { Id = DockPanelIds.Properties,  Side = DockSide.Left, Group = 1, Order = 0, Active = true, Proportion = 0.45 },

                // …and the panel dropped against the document area's own left edge.
                new CwsDockPanel { Id = DockPanelIds.WBondInductance, Side = DockSide.Left, Group = 0, Order = 0,
                                   Active = true, Proportion = 0.18, Inboard = true },
            ],
        };

        var reopened = SaveAndReopen(arranged);

        var inductance = Assert.Single(reopened.Panels, p => p.Id == DockPanelIds.WBondInductance);
        Assert.True(inductance.Open);
        Assert.Equal(DockSide.Left, inductance.Side);
        Assert.True(inductance.Inboard, "the panel must come back in the column beside the documents");

        // …and the outer column is untouched: still Left, still NOT inboard, still its own width.
        foreach (var id in new[] { DockPanelIds.ProjectTree, DockPanelIds.Properties })
        {
            var outer = Assert.Single(reopened.Panels, p => p.Id == id);
            Assert.Equal(DockSide.Left, outer.Side);
            Assert.False(outer.Inboard);
        }

        // TWO Left entries now, and they are different columns at different widths: the outer one keeps
        // its own, and the inboard column records ITS width rather than having it inferred from a panel.
        var outerSide = Assert.Single(reopened.Sides, x => x.Side == DockSide.Left && !x.Inboard);
        Assert.Equal(0.22, outerSide.Proportion, 3);

        var inboardSide = Assert.Single(reopened.Sides, x => x.Side == DockSide.Left && x.Inboard);
        Assert.InRange(inboardSide.Proportion, 0.01, 0.99);
    }

    /// <summary>
    /// <b>The layout document keeps its width across a reopen</b> (owner, 2026-08-17, with the workspace
    /// that showed it).
    ///
    /// <para>An inboard column's width used to be read off its first PANEL's proportion — a different
    /// measurement entirely: a panel's proportion is its share of its own column, measured DOWN, not the
    /// column's share of the document row, measured ACROSS. The owner's workspace stacked two wirebond
    /// panels 0.67/0.33 in a right inboard column, so it reopened with the column claiming <b>0.67 of the
    /// window's width</b> and the layout document squeezed into the third that was left.</para>
    ///
    /// <para>The 0.67 is the trap in one number: it is a perfectly valid proportion, so nothing complained
    /// — it was simply an answer to a different question.</para>
    /// </summary>
    [Fact]
    public void AnInboardColumnsWidth_IsNotTheStackedPanelsShareOfIt()
    {
        // The owner's own arrangement: two panels stacked in one right inboard column.
        var arranged = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Right, Proportion = 0.1125 }],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.WBondInductance, Side = DockSide.Right, Group = 0, Order = 0,
                                   Active = true, Proportion = 0.668, Inboard = true },
                new CwsDockPanel { Id = DockPanelIds.WBondProfile,    Side = DockSide.Right, Group = 1, Order = 0,
                                   Active = true, Proportion = 0.332, Inboard = true },
            ],
        };

        var f = new CircuitRfDockFactory();
        f.CreateLayout();
        var built = f.CreateLayoutFromState(arranged);

        var column = AllDocks(built).OfType<Dock.Model.Core.IDock>()
                                    .Single(d => d.Id == "RightInboardColumn");

        // Whatever it is, it is NOT the panels' vertical share — and it leaves the documents the bulk of
        // the row, which is the thing the owner actually saw go wrong.
        Assert.NotEqual(0.668, column.Proportion, 3);
        Assert.InRange(column.Proportion, 0.0, 0.4);

        // And the panels keep their own stacking, which is what 0.668/0.332 always meant.
        var docks = column.VisibleDockables!.OfType<Dock.Model.Controls.IToolDock>().ToList();
        Assert.Equal(2, docks.Count);
        Assert.Equal(0.668, docks[0].Proportion, 3);
        Assert.Equal(0.332, docks[1].Proportion, 3);
    }

    /// <summary>
    /// …and once a workspace has been saved WITH that width, it comes back exactly. The round trip is the
    /// point: the capture has to write the column's own proportion for the build to read it.
    /// </summary>
    [Fact]
    public void AnInboardColumnsWidth_SurvivesTheRoundTrip()
    {
        var arranged = new CwsDockLayout
        {
            Sides =
            [
                new CwsDockSide { Side = DockSide.Right, Proportion = 0.1125 },
                new CwsDockSide { Side = DockSide.Right, Proportion = 0.16, Inboard = true },
            ],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.Palette, Side = DockSide.Right, Group = 0, Order = 0,
                                   Active = true, Proportion = 1.0 },
                new CwsDockPanel { Id = DockPanelIds.WBondInductance, Side = DockSide.Right, Group = 0, Order = 0,
                                   Active = true, Proportion = 0.668, Inboard = true },
                new CwsDockPanel { Id = DockPanelIds.WBondProfile,    Side = DockSide.Right, Group = 1, Order = 0,
                                   Active = true, Proportion = 0.332, Inboard = true },
            ],
        };

        var reopened = SaveAndReopen(arranged);

        var inboard = Assert.Single(reopened.Sides, x => x.Side == DockSide.Right && x.Inboard);
        Assert.Equal(0.16, inboard.Proportion, 3);

        // The outer right column (the Palette) is a separate entry and keeps its own width.
        var outer = Assert.Single(reopened.Sides, x => x.Side == DockSide.Right && !x.Inboard);
        Assert.Equal(0.1125, outer.Proportion, 3);
    }

    /// <summary>
    /// The structure itself, not just its encoding: an inboard panel is built into its own column beside
    /// the document area, and the outer column still exists separately.
    /// </summary>
    [Fact]
    public void AnInboardPanel_IsBuiltIntoItsOwnColumn()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var root = f.CreateLayoutFromState(new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.22 }],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 1.0 },
                new CwsDockPanel { Id = DockPanelIds.WBondInductance, Side = DockSide.Left, Group = 0, Order = 0,
                                   Active = true, Proportion = 0.18, Inboard = true },
            ],
        });

        var ids = AllDockIds(root).ToList();
        Assert.Contains("LeftColumn", ids);
        Assert.Contains("LeftInboardColumn", ids);
        Assert.Contains("DocumentRow", ids);

        // …and NOT when nothing is inboard, so the ordinary layout keeps exactly the tree it always had.
        var plain = new CircuitRfDockFactory();
        plain.CreateLayout();
        Assert.DoesNotContain("DocumentRow", AllDockIds(plain.CreateLayoutFromState(DockLayoutDefaults.Default())).ToList());
    }

    /// <summary>
    /// Top and bottom panels are inboard by construction — the builder has always put them inside the
    /// document column — so the flag is normalised away rather than carrying a distinction that does not
    /// exist and could not be honoured.
    /// </summary>
    [Fact]
    public void InboardIsNormalisedAwayOnTopAndBottom()
    {
        var written = DockLayoutSerialization.Write(new CwsDockLayout
        {
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.Messages, Side = DockSide.Bottom, Group = 0, Order = 0, Active = true, Proportion = 0.2, Inboard = true },
                new CwsDockPanel { Id = DockPanelIds.WBondProfile, Side = DockSide.Right, Group = 0, Order = 0, Active = true, Proportion = 0.2, Inboard = true },
            ],
        });

        var read = DockLayoutSerialization.TryRead(written).Layout!;
        Assert.False(Assert.Single(read.Panels, p => p.Id == DockPanelIds.Messages).Inboard);
        Assert.True(Assert.Single(read.Panels, p => p.Id == DockPanelIds.WBondProfile).Inboard);
    }

    /// <summary>
    /// <b>The schema version is deliberately NOT bumped for it.</b> Bumping would make an older build
    /// refuse the whole block as "newer than this build understands" and fall back to the default layout —
    /// losing every panel position to gain one flag. An unknown property is simply ignored on read, so an
    /// additive field costs a round trip through an older build nothing.
    /// </summary>
    [Fact]
    public void TheInboardFlagIsAdditive_NotAVersionBump()
    {
        Assert.Equal(1, CwsDockLayout.CurrentVersion);

        // A block written before the flag existed reads back with every panel outboard.
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {"Version":1,"Panels":[{"Id":"WBondInductance","Open":true,"Side":"Left","Proportion":0.2,"Group":0,"Order":0,"Active":true}]}
            """);

        var read = DockLayoutSerialization.TryRead(legacy);
        Assert.Null(read.Report);
        Assert.False(Assert.Single(read.Layout!.Panels).Inboard);
    }


    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  9. P and A toggle repeatably, and put the panel back where it was (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>P and A do not depend on where keyboard focus is</b> (owner, 2026-08-17, reported twice: "I am
    /// pressing 'A' repeatedly, but the Array Inductance only toggles once — I need to click on canvas to
    /// get it to toggle more than once").
    ///
    /// <para>They were handled by the view's canvas-focus-gated tunnel handler, and that shape cannot work
    /// for this action: closing a dockable moves focus off the canvas, so the very act the key performs
    /// disarms the key. Re-asserting canvas focus afterwards was a patch on the symptom. The handler lives
    /// on the TOP LEVEL now, because the feature is "these keys toggle these panels while working on a
    /// wirebond cell" — not "while one particular control has focus".</para>
    /// </summary>
    [Fact]
    public void ThePanelKeys_AreHandledOnTheShellWindow_NotOnAView()
    {
        var window = Read("src", "Ui", "Views", "WorkspaceWindow.axaml.cs");

        // The same window tunnel handler the placement-rotate shortcut already uses, and for the same
        // stated reason: it fires regardless of which control has focus.
        Assert.Contains("if (TryHandleWirePanelKeys(e)) return;", window, StringComparison.Ordinal);
        Assert.Contains("AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel)",
                        window, StringComparison.Ordinal);

        // The layout view no longer claims them at all — neither on canvas focus nor on its own TopLevel.
        var view = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        Assert.DoesNotContain("Key.P", view, StringComparison.Ordinal);
        Assert.DoesNotContain("OnTopLevelKeyDown", view, StringComparison.Ordinal);

        // …and the toggle no longer fights for focus, so it is left where the user wants it.
        int toggle = view.IndexOf("private void ToggleWirePanel(", StringComparison.Ordinal);
        Assert.True(toggle >= 0);
        Assert.DoesNotContain("FocusCanvasDeferred", view[toggle..(toggle + 200)], StringComparison.Ordinal);
    }

    /// <summary>
    /// Every gate on that handler is load-bearing, and each is here so a well-meaning simplification has
    /// to argue with a test first.
    /// </summary>
    [Fact]
    public void ThePanelKeyHandler_KeepsEveryGate()
    {
        var keys = Read("src", "Ui", "Views", "WirePanelKeys.cs");

        int at = keys.IndexOf("public static bool Handle", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = keys[at..(at + 900)];

        Assert.Contains("e.KeyModifiers != KeyModifiers.None", body, StringComparison.Ordinal);   // Ctrl+A stays Select All
        Assert.Contains("vm.WirePanelKeysApply", body, StringComparison.Ordinal);                 // the right document
        Assert.Contains("IsTypingInAField(top.FocusManager?.GetFocusedElement())", body, StringComparison.Ordinal);

        // The shell still marks it handled, so nothing downstream sees a stray letter.
        Assert.Contains("e.Handled = true;",
                        Read("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"), StringComparison.Ordinal);

        // The document gate itself: wires to look at, and not mid-label.
        var vm = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
        int gate = vm.IndexOf("public bool WirePanelKeysApply", StringComparison.Ordinal);
        Assert.True(gate >= 0);
        string gateBody = vm[gate..(gate + 400)];
        Assert.Contains("is LayoutDocument", gateBody, StringComparison.Ordinal);
        Assert.Contains("vm.HasWireDesign", gateBody, StringComparison.Ordinal);
        Assert.Contains("!vm.IsTypingLabel", gateBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A hidden panel goes back where it was, not into a floating window.</b>
    ///
    /// <para><c>ShowToolPanel</c>'s only answer for a panel that is not in the tree is to float one — right
    /// for the View menu, wrong for a toggle whose whole point is to undo the hide. The toggle records the
    /// place first and restores to it, and only falls through to a float when nothing is remembered.</para>
    /// </summary>
    [Fact]
    public void TheToggleRestoresTheRememberedPlace_AndOnlyFloatsAsALastResort()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private void ToggleToolPanel(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = code[at..code.IndexOf("private void HideFloatingPanel", StringComparison.Ordinal)];

        // Remembered BEFORE anything that hides it, while there is still a place to record — and that
        // holds for BOTH ways of hiding one, the docked hide and the floating window close.
        int remember = body.IndexOf("RememberPanelHome(panelId, tool);", StringComparison.Ordinal);
        int floating = body.IndexOf("HideFloatingPanel(panelId, tool, window)", StringComparison.Ordinal);
        int hide     = body.IndexOf("DockPanelHiding.Hide(_factory, tool)", StringComparison.Ordinal);
        Assert.True(remember >= 0 && floating > remember && hide > remember);

        // …and the float is the FALLBACK, not the answer.
        Assert.Contains("if (!RestorePanelToItsHome(panelId, tool)) ShowToolPanel(panelId);",
                        body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Three states — and the two-state rule this replaces (owner, 2026-08-17 → 2026-08-18).</b>
    ///
    /// <para>It was two: showing ANYWHERE meant the next press hides it, because an earlier bring-forward
    /// middle state made the key read as non-deterministic — a panel tabbed with another took THREE presses
    /// for one cycle. The owner reversed it on 2026-08-18: <i>"if the Properties is tabbed behind Analyses,
    /// I want Properties to come to the front. This should be true to [any] window tool that is behind
    /// another pane."</i></para>
    ///
    /// <para><b>What stops the old complaint coming back is not in this method</b> — it is that
    /// <c>IsToolPanelShowing</c> now reports a panel behind another tab as NOT showing. The old middle
    /// state was invisible: the panel counted as showing, so the press that merely raised it looked like a
    /// press that did nothing. With "showing" meaning "in view", every press moves between the two states
    /// the user can see, so the pair of tests below have to hold together or the reversal is a regression.</para>
    /// </summary>
    [Fact]
    public void TheToggleBringsAPanelForwardBeforeItWouldEverHideOne()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private void ToggleToolPanel(", StringComparison.Ordinal);
        int end = code.IndexOf("private void BringToFront", StringComparison.Ordinal);
        Assert.True(at >= 0 && end > at);

        string body = code[at..end];

        // The middle branch: behind a sibling tab, raise it — and RETURN, so no press can both raise and
        // hide, and nothing is remembered for a panel that has not moved.
        int front  = body.IndexOf("if (!IsFrontTab(tool, parent))", StringComparison.Ordinal);
        int raise  = body.IndexOf("BringToFront(tool, parent, window);", StringComparison.Ordinal);
        int remember = body.IndexOf("RememberPanelHome(panelId, tool);", StringComparison.Ordinal);
        Assert.True(front >= 0 && raise > front && remember > raise);

        // ONE restore, and one hide per kind of home — docked or floating. The floating branch is an
        // alternative way to reach the hidden state, not a fourth state: it returns rather than falling on
        // through, so no press can leave the panel half-hidden.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"RestorePanelToItsHome\(panelId, tool\)"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"HideFloatingPanel\(panelId, tool, window\)"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"DockPanelHiding\.Hide\("));
    }

    /// <summary>
    /// The other half: a panel BEHIND another tab reports as not showing, so every control bound to
    /// <c>IsToolPanelShowing</c> still reads as a plain two-state toggle. Drop this and the three-state
    /// toggle becomes the very thing the owner rejected on 2026-08-17.
    /// </summary>
    [Fact]
    public void APanelBehindAnotherTab_DoesNotCountAsShowing()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at  = code.IndexOf("public bool IsToolPanelShowing(", StringComparison.Ordinal);
        int end = code.IndexOf("private static bool IsFrontTab(", StringComparison.Ordinal);
        Assert.True(at >= 0 && end > at);

        Assert.Contains("&& IsFrontTab(tool, parent);", code[at..end], StringComparison.Ordinal);

        // A dock holding one dockable shows it whatever ActiveDockable says — otherwise the first press of
        // the button is spent "raising" a panel that is already the only thing in its dock.
        int front = code.IndexOf("private static bool IsFrontTab(", StringComparison.Ordinal);
        string body = code[front..(front + 400)];
        Assert.Contains("ReferenceEquals(parent.ActiveDockable, tool)", body, StringComparison.Ordinal);
        Assert.Contains("parent.VisibleDockables is not { Count: > 1 }", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A restored panel is the tab IN FRONT.</b> Owner, 2026-08-17: pressing A brought the Array
    /// Inductance panel back <i>behind</i> the Wire Profile it shares a dock with.
    ///
    /// <para>The cause is in <c>BuildSide</c>: it resolves the active tab as
    /// <c>ordered.FirstOrDefault(p => p.Active)</c> over panels sorted by <c>Order</c>, so leaving the old
    /// active flag set meant the panel with the LOWER order won. Only one panel in a group can be in
    /// front, so the restore clears the flag on the group it is rejoining.</para>
    /// </summary>
    [Fact]
    public void ARestoredPanel_BecomesTheFrontTabOfItsGroup()
    {
        var docking = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = docking.IndexOf("private bool RestorePanelToItsHome", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = docking[at..docking.IndexOf("private void OnToolPanelClosing", StringComparison.Ordinal)];

        // The schema path clears every sibling in the same (side, group, inboard) group first…
        Assert.Contains("sibling.Active = false;", body, StringComparison.Ordinal);
        Assert.Contains("sibling.Inboard == d.Inboard", body, StringComparison.Ordinal);

        // …and the targeted path states it directly, rather than trusting an insert to imply it.
        Assert.Contains("dock.ActiveDockable = tool;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule it depends on, asserted against the real builder: within a group, the panel marked
    /// <c>Active</c> is the tab in front — and with two marked, the lower <c>Order</c> wins, which is
    /// exactly why the restore has to clear the others.
    /// </summary>
    [Fact]
    public void TheBuilderPutsTheActivePanelInFront_AndLowerOrderWinsATie()
    {
        CwsDockPanel Panel(string id, int order, bool active) =>
            new() { Id = id, Side = DockSide.Right, Group = 0, Order = order, Active = active, Proportion = 0.2 };

        Assert.Equal(DockPanelIds.WBondInductance, FrontTabOfRightColumn(
            Panel(DockPanelIds.WBondProfile, 0, false),
            Panel(DockPanelIds.WBondInductance, 1, true)));

        // Both marked: the lower Order wins — the bug's own mechanism.
        Assert.Equal(DockPanelIds.WBondProfile, FrontTabOfRightColumn(
            Panel(DockPanelIds.WBondProfile, 0, true),
            Panel(DockPanelIds.WBondInductance, 1, true)));
    }

    private static string? FrontTabOfRightColumn(params CwsDockPanel[] panels)
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var root = f.CreateLayoutFromState(new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Right, Proportion = 0.25 }],
            Panels = [.. panels],
        });

        var dock = AllDocks(root).OfType<Dock.Model.Controls.IToolDock>()
            .First(d => d.VisibleDockables?.Any(x => x.Id == DockPanelIds.WBondProfile) == true);

        return dock.ActiveDockable?.Id;
    }

    /// <summary>
    /// <b>The cheap path must not rebuild the shell.</b> Restoring by rebuilding re-realises every
    /// document's view, which throws away the pan and zoom of every open canvas — not a price a keystroke
    /// should pay. The full rebuild is kept only for the case the cheap one cannot serve: the panel's
    /// column no longer exists at all, or the place came from a `.cws` rather than from this session.
    /// </summary>
    [Fact]
    public void RestoringInPlace_PrefersATargetedInsertOverARebuild()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private bool RestorePanelToItsHome", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = code[at..code.IndexOf("private void OnToolPanelClosing", StringComparison.Ordinal)];

        int insert  = body.IndexOf("_factory.InsertDockable(dock, tool, at)", StringComparison.Ordinal);
        int rebuild = body.IndexOf("ApplyDockLayout(live)", StringComparison.Ordinal);
        Assert.True(insert >= 0 && rebuild > insert, "the targeted insert must be tried first");

        // A remembered dock is verified to still be IN the tree — a collapsed or dragged-away dock is a
        // live object with a stale place in it, and inserting there hides the panel where nobody can see it.
        Assert.Contains("DockLayoutCapture.Contains(root, dock)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Contains</c> answers exactly that question, including for a dock that has been detached.
    /// </summary>
    [Fact]
    public void ContainsFindsALiveDockAndRejectsADetachedOne()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayoutFromState(new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.2 }],
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 1.0 }],
        });

        var live = AllDocks(root).OfType<Dock.Model.Mvvm.Controls.ToolDock>().First();
        Assert.True(DockLayoutCapture.Contains(root, live));

        Assert.False(DockLayoutCapture.Contains(root, new Dock.Model.Mvvm.Controls.ToolDock { Id = "Orphan" }));
    }

    /// <summary>
    /// <b>The place survives a restart.</b> A closed panel is not in the live tree, so its place would be
    /// forgotten the moment the workspace was saved with it hidden — and the next session's first press
    /// would float it, which is the behaviour being fixed. It is written as an <c>Open = false</c> entry,
    /// which every reader ignores (<c>BuildSide</c> filters on <c>Open</c>) and the next session seeds its
    /// memory from.
    /// </summary>
    [Fact]
    public void AClosedPanelsPlace_IsWrittenToTheWorkspaceAndReadBack()
    {
        var docking = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        Assert.Contains("DockersCollapsed ? _preCollapseLayout : CaptureDockLayoutForPersistence()",
                        docking, StringComparison.Ordinal);

        int cap = docking.IndexOf("private CwsDockLayout? CaptureDockLayoutForPersistence", StringComparison.Ordinal);
        Assert.True(cap >= 0);
        Assert.Contains("Open = false", docking[cap..(cap + 900)], StringComparison.Ordinal);

        // Seeded from the file BEFORE the layout is applied — the apply drops closed entries.
        int seed = docking.IndexOf("SeedPanelHomesFrom(layout);", StringComparison.Ordinal);
        int apply = docking.IndexOf("ApplyDockLayout(layout, placer);", StringComparison.Ordinal);
        Assert.True(seed >= 0 && apply > seed);

        // …and a closed entry really does place nothing when the layout is built.
        var f = new CircuitRfDockFactory();
        f.CreateLayout();
        var built = f.CreateLayoutFromState(new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.2 }],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.ProjectTree,      Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 1.0 },
                new CwsDockPanel { Id = DockPanelIds.WBondInductance,  Side = DockSide.Left, Group = 0, Order = 0, Active = false, Proportion = 0.2, Open = false, Inboard = true },
            ],
        });

        Assert.DoesNotContain(DockPanelIds.WBondInductance,
                              DockLayoutCapture.Capture(built, []).Panels.Select(p => p.Id));
        Assert.DoesNotContain("LeftInboardColumn", AllDockIds(built).ToList());
    }

    /// <summary>
    /// Closing a panel by its own tab X leaves the same trail back as the P/A toggle — one route to
    /// remembering, so every way of hiding a panel behaves the same on the way back.
    /// </summary>
    [Fact]
    public void ClosingAPanelByItsOwnTab_AlsoRemembersItsPlace()
    {
        var docking = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        Assert.Contains("_factory.DockableClosing   += (_, e) => OnToolPanelClosing(e.Dockable);",
                        docking, StringComparison.Ordinal);

        int at = docking.IndexOf("private void OnToolPanelClosing", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = docking[at..(at + 400)];
        Assert.Contains("DockPanelIds.All.Contains(id)", body, StringComparison.Ordinal);
        Assert.Contains("RememberPanelHome(id, tool)", body, StringComparison.Ordinal);
    }

    /// <summary>Every dock in a tree.</summary>
    private static IEnumerable<Dock.Model.Core.IDockable> AllDocks(Dock.Model.Core.IDockable? d)
    {
        if (d is null) yield break;
        yield return d;
        if (d is not Dock.Model.Core.IDock dock || dock.VisibleDockables is null) yield break;
        foreach (var child in dock.VisibleDockables)
            foreach (var found in AllDocks(child)) yield return found;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 10. Hide/restore in place — no rebuild, no flash, no resize (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A hidden panel comes back into the SAME dock with the tree otherwise untouched</b> — which is
    /// what "no flash" means structurally: nothing outside that one dock changed, so nothing outside it is
    /// re-realised.
    ///
    /// <para>Owner: "I see the entire workspace dock redraw when the Array Inductance is brought back…
    /// when I dock it manually using the Dock system there is no flash." Closing the panel let its owner
    /// dock collapse out of the tree, leaving a full rebuild as the only way back.</para>
    /// </summary>
    [Fact]
    public void HidingAndRestoringAPanel_PutsItBackWithoutTouchingTheRest()
    {
        var (factory, root, inductance, ownerDock) = ShellWithTwoToolColumns();

        string before = TreeShape(root);
        double proportion = ownerDock.Proportion;

        DockPanelHiding.Hide(factory, inductance);
        Assert.DoesNotContain(inductance, VisibleTools(root));

        Assert.True(DockPanelHiding.Restore(factory, root, inductance));

        Assert.Contains(inductance, VisibleTools(root));
        Assert.Equal(before, TreeShape(root));
        Assert.Equal(proportion, ownerDock.Proportion, 6);
        Assert.Same(ownerDock, inductance.Owner);
    }

    /// <summary>
    /// <b>The emptied dock is left exactly where it is — and that is the whole of the size fix.</b>
    ///
    /// <para>Owner, 2026-08-17: <i>"repeatedly pressing A or P results in the panel height getting smaller
    /// and smaller."</i> An earlier version detached the emptied <c>ToolDock</c> and one adjacent splitter,
    /// on the reasoning that an empty proportional child would sit there as a blank strip. Laid out for
    /// real, an emptied dock and its splitter both render at <b>0 px</b> — Dock collapses them itself, and
    /// the reasoning had never been measured.</para>
    ///
    /// <para>The detach was also the CAUSE of the shrink, by a route no fix at this layer could undo:
    /// removing the dock leaves its sibling alone, so <c>ProportionalStackPanel</c> renormalises the
    /// sibling's CONTROL to 1.0 as a local value; re-asserting the remembered proportions on the MODEL
    /// cannot beat a local value, so the next layout pass normalises against that 1.0 and writes the drift
    /// back. Measured: 0.668 → 0.40 → 0.29 → 0.22 → 0.18. Left alone, it round-trips exactly, forever.</para>
    /// </summary>
    [Fact]
    public void HidingTheOnlyPanelInADock_LeavesTheDockAndItsSplitterAlone()
    {
        var (factory, root, inductance, ownerDock) = ShellWithTwoToolColumns();

        var column = (IDock)ownerDock.Owner!;

        // The column's own children — the docks and the splitter between them — NOT their contents, which
        // is the one thing hiding a panel is supposed to change.
        string ChildrenOfColumn() =>
            string.Join(",", column.VisibleDockables!.Select(d => d.GetType().Name + ":" + d.Id));

        string columnBefore = ChildrenOfColumn();
        var proportionsBefore = AllDocks(root).OfType<IDock>().Select(d => d.Proportion).ToList();

        DockPanelHiding.Hide(factory, inductance);

        // The dock is still there, still in its column, still its own size — only empty.
        Assert.Equal(columnBefore, ChildrenOfColumn());
        Assert.Contains(ownerDock, column.VisibleDockables!);
        Assert.Equal(proportionsBefore, AllDocks(root).OfType<IDock>().Select(d => d.Proportion));

        DockPanelHiding.Restore(factory, root, inductance);
        Assert.Equal(proportionsBefore, AllDocks(root).OfType<IDock>().Select(d => d.Proportion));
    }

    /// <summary>
    /// The same holds however many cycles it runs for — the owner's report was about repetition, so the
    /// test repeats. Nothing drifts, because nothing is touched.
    /// </summary>
    [Fact]
    public void ToggglingAPanelRepeatedly_NeverChangesAnySize()
    {
        var (factory, root, inductance, _) = ShellWithTwoToolColumns();

        var want = AllDocks(root).OfType<IDock>().Select(d => d.Proportion).ToList();

        for (int cycle = 0; cycle < 5; cycle++)
        {
            DockPanelHiding.Hide(factory, inductance);
            Assert.True(DockPanelHiding.Restore(factory, root, inductance));

            Assert.Equal(want, AllDocks(root).OfType<IDock>().Select(d => d.Proportion));
        }
    }

    /// <summary>
    /// A panel TABBED with another is no different — hide and restore are the library's, whole.
    /// </summary>
    [Fact]
    public void HidingATabbedPanel_LeavesItsCoTenantShowing()
    {
        var factory = new Dock.Model.Mvvm.Factory();

        var profile = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.WBondProfile, Title = "Wire Profile" };
        var inductance = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.WBondInductance, Title = "Array Inductance" };

        var shared = new Dock.Model.Mvvm.Controls.ToolDock
        {
            Id = "Shared", Proportion = 0.3,
            VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(profile, inductance),
            ActiveDockable = inductance,
        };
        var root = RootAround(factory, shared);

        DockPanelHiding.Hide(factory, inductance);

        Assert.Contains(shared, AllDocks(root));
        Assert.Contains(profile, VisibleTools(root));
        Assert.Equal(0.3, shared.Proportion, 6);

        Assert.True(DockPanelHiding.Restore(factory, root, inductance));
        Assert.Contains(inductance, VisibleTools(root));
        Assert.Same(shared, inductance.Owner);
    }

    /// <summary>
    /// If the dock the panel belonged to has left the tree — a shell rebuilt underneath a hidden panel —
    /// the restore REFUSES, and the caller falls back to rebuilding from the remembered placement.
    ///
    /// <para><b>Reachability from the root is the test</b>, not merely that the dock took the tool back: an
    /// orphaned dock accepts it just as readily and would report success while the panel is nowhere on
    /// screen.</para>
    /// </summary>
    [Fact]
    public void RestoringIntoADockThatHasLeftTheTree_IsRefused()
    {
        var (factory, root, inductance, ownerDock) = ShellWithTwoToolColumns();

        DockPanelHiding.Hide(factory, inductance);

        var column = (IDock)ownerDock.Owner!;
        column.VisibleDockables!.Remove(ownerDock);

        Assert.False(DockPanelHiding.Restore(factory, root, inductance));
    }

    /// <summary>
    /// The toggle takes that path FIRST — before the targeted insert and long before the rebuild, which is
    /// the one that redraws the shell.
    /// </summary>
    [Fact]
    public void TheRestoreTriesHideRestoreBeforeAnythingThatRebuilds()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private bool RestorePanelToItsHome", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = code[at..code.IndexOf("private void OnToolPanelClosing", StringComparison.Ordinal)];

        int hidden  = body.IndexOf("DockPanelHiding.Restore", StringComparison.Ordinal);
        int insert  = body.IndexOf("_factory.InsertDockable", StringComparison.Ordinal);
        int rebuild = body.IndexOf("ApplyDockLayout(live)", StringComparison.Ordinal);

        Assert.True(hidden >= 0 && insert > hidden && rebuild > insert);

        // …and hiding really is a hide, not a close.
        Assert.Contains("DockPanelHiding.Hide(_factory, tool)", code, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 11. …and the same, for a panel in a FLOATING window (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The measured fact the floating fix rests on</b>, pinned here rather than believed: Dock's
    /// <c>HideDockable</c> files a floating tool under the FLOAT's root, not the shell's, and leaves the
    /// window itself open and empty.
    ///
    /// <para>That is the whole of the owner's report in one assertion — "their window contents disappears
    /// and the window is not closed" is the empty window below, and the flash is the shell root's hidden
    /// list coming back empty, so the restore misses the cheap path and rebuilds. If a future Dock release
    /// changes either, this test says so and the floating branch can be deleted.</para>
    /// </summary>
    [Fact]
    public void DocksOwnHide_FilesAFloatingToolUnderTheFloatRootAndLeavesTheWindowOpen()
    {
        var (factory, shellRoot, floatRoot, window, inductance, floatDock) = ShellWithAFloatingPanel();

        DockPanelHiding.Hide(factory, inductance);

        // Not where the docked restore looks…
        Assert.DoesNotContain(inductance, shellRoot.HiddenDockables ?? []);
        Assert.Contains(inductance, floatRoot.HiddenDockables ?? []);

        // …and the window is still there, with nothing in it.
        Assert.Contains(window, shellRoot.Windows!);
        Assert.Empty(floatDock.VisibleDockables!);
    }

    /// <summary>
    /// So a floating panel is hidden by CLOSING its window: it leaves the root's window list and takes
    /// nothing else with it. No rebuild is involved, which is why there is no flash.
    /// </summary>
    [Fact]
    public void ClosingAFloatingWindow_RemovesItFromTheRootAndTouchesNothingElse()
    {
        var factory = new CircuitRfDockFactory();
        var root = factory.CreateLayout() as IRootDock;
        Assert.NotNull(root);

        string before = TreeShape(root!);

        var window = factory.CreateDockWindow();
        window.Id = "FloatingWindow";
        root!.Windows ??= factory.CreateList<Dock.Model.Core.IDockWindow>();
        root.Windows.Add(window);

        factory.CloseFloatingWindow(root, window);

        Assert.DoesNotContain(window, root.Windows);
        Assert.Equal(before, TreeShape(root));   // the shell itself never moved
    }

    /// <summary>
    /// A float the user has dragged a SECOND panel into is still that other panel's window, so hiding one
    /// of them must not close it. <c>HoldsOtherTools</c> is the question that decides.
    /// </summary>
    [Fact]
    public void AFloatSharedWithAnotherPanel_IsNotTheHiddenPanelsToClose()
    {
        var (_, _, _, _, inductance, floatDock) = ShellWithAFloatingPanel();

        Assert.False(DockPanelHiding.HoldsOtherTools(floatDock, inductance));

        var profile = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.WBondProfile, Title = "Wire Profile" };
        floatDock.VisibleDockables!.Add(profile);

        Assert.True(DockPanelHiding.HoldsOtherTools(floatDock, inductance));
    }

    /// <summary>
    /// The toggle branches on floating BEFORE it reaches the docked hide — a floating panel must never go
    /// down the hide path at all, for the reason the characterisation test above measures.
    /// </summary>
    [Fact]
    public void TheToggleClosesAFloatingPanelsWindow_BeforeItWouldEverHideOne()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private void ToggleToolPanel", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = code[at..code.IndexOf("private void HideFloatingPanel", StringComparison.Ordinal)];

        int floating = body.IndexOf("HideFloatingPanel(panelId, tool, window)", StringComparison.Ordinal);
        int hide     = body.IndexOf("DockPanelHiding.Hide(_factory, tool)", StringComparison.Ordinal);

        Assert.True(floating >= 0 && hide > floating);
        Assert.Contains("_factory.CloseFloatingWindow(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it comes BACK as a float, at its own remembered rectangle, ahead of both the targeted insert and
    /// the rebuild — the rectangle is the whole of what "where it was" means for a floating panel.
    /// </summary>
    [Fact]
    public void RestoringAFloatedPanel_ReopensItsOwnWindowBeforeAnythingThatRebuilds()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private bool RestorePanelToItsHome", StringComparison.Ordinal);
        string body = code[at..code.IndexOf("private void OnToolPanelClosing", StringComparison.Ordinal)];

        int refloat = body.IndexOf("_factory.FloatTool(tool,", StringComparison.Ordinal);
        int insert  = body.IndexOf("_factory.InsertDockable", StringComparison.Ordinal);
        int rebuild = body.IndexOf("ApplyDockLayout(live)", StringComparison.Ordinal);

        Assert.True(refloat >= 0 && insert > refloat && rebuild > insert);

        // R-dock-6: a remembered rectangle is never trusted straight onto the screen — the monitor it was
        // on may be gone.
        Assert.Contains("placer.Place(new ScreenRect(saved.X, saved.Y, saved.Width, saved.Height))",
                        body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closing the float raises <c>DockableClosing</c>, which arrives back at <c>RememberPanelHome</c> after
    /// the window has left the tree — a second pass that finds nothing. It must not overwrite the rectangle
    /// recorded a moment earlier, or the panel comes back as a fresh default-placed float.
    /// </summary>
    [Fact]
    public void AHomeThatNamesNoPlace_NeverOverwritesOneThatDoes()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        int at = code.IndexOf("private void RememberPanelHome", StringComparison.Ordinal);
        string body = code[at..code.IndexOf("private bool RestorePanelToItsHome", StringComparison.Ordinal)];

        Assert.Contains(
            "if (hostDock is null && docked is null && floated is null && _panelHomes.ContainsKey(panelId)) return;",
            body, StringComparison.Ordinal);
    }

    /// <summary>One tool alone in a floating window, beside a shell root that holds the documents.</summary>
    private static (Dock.Model.Mvvm.Factory Factory, IRootDock ShellRoot, IRootDock FloatRoot,
                    Dock.Model.Core.IDockWindow Window, Dock.Model.Core.IDockable Tool, IDock FloatDock)
        ShellWithAFloatingPanel()
    {
        var factory = new Dock.Model.Mvvm.Factory();

        var tree = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.ProjectTree, Title = "Project Tree" };
        var treeDock = new Dock.Model.Mvvm.Controls.ToolDock
        {
            Id = "TreeDock", Proportion = 0.25,
            VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(tree), ActiveDockable = tree,
        };
        var shellRoot = RootAround(factory, treeDock);

        var inductance = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.WBondInductance, Title = "Array Inductance" };
        var floatDock = new Dock.Model.Mvvm.Controls.ToolDock
        {
            Id = "FloatingToolDock",
            VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(inductance), ActiveDockable = inductance,
        };

        var floatRoot = factory.CreateRootDock();
        floatRoot.Id = "FloatingRoot";
        floatRoot.VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(floatDock);
        floatRoot.ActiveDockable = floatRoot.DefaultDockable = floatDock;

        var window = factory.CreateDockWindow();
        window.Id = "FloatingWindow";
        window.Layout = floatRoot;
        floatRoot.Window = window;          // the back-reference every hand-built float needs
        window.Owner = shellRoot;

        shellRoot.Windows = factory.CreateList(window);
        factory.InitLayout(shellRoot);

        return (factory, shellRoot, floatRoot, window, inductance, floatDock);
    }

    /// <summary>Two tool columns beside a document dock, the inductance panel alone in the second.</summary>
    private static (Dock.Model.Mvvm.Factory Factory, IRootDock Root,
                    Dock.Model.Core.IDockable Tool, IDock OwnerDock) ShellWithTwoToolColumns()
    {
        var factory = new Dock.Model.Mvvm.Factory();

        var tree = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.ProjectTree, Title = "Project Tree" };
        var inductance = new Dock.Model.Mvvm.Controls.Tool { Id = DockPanelIds.WBondInductance, Title = "Array Inductance" };

        var treeDock = new Dock.Model.Mvvm.Controls.ToolDock
        {
            Id = "TreeDock", Proportion = 0.6,
            VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(tree), ActiveDockable = tree,
        };
        var inductanceDock = new Dock.Model.Mvvm.Controls.ToolDock
        {
            Id = "InductanceDock", Proportion = 0.4,
            VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(inductance), ActiveDockable = inductance,
        };

        var column = new Dock.Model.Mvvm.Controls.ProportionalDock
        {
            Id = "LeftColumn", Orientation = Dock.Model.Core.Orientation.Vertical,
            VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(
                treeDock, new Dock.Model.Mvvm.Controls.ProportionalDockSplitter(), inductanceDock),
        };

        return (factory, RootAround(factory, column), inductance, inductanceDock);
    }

    private static IRootDock RootAround(Dock.Model.Mvvm.Factory factory, Dock.Model.Core.IDockable side)
    {
        var documents = new Dock.Model.Mvvm.Controls.DocumentDock
        {
            Id = "Documents", VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(),
        };
        var outer = new Dock.Model.Mvvm.Controls.ProportionalDock
        {
            Id = "OuterLayout", Orientation = Dock.Model.Core.Orientation.Horizontal,
            VisibleDockables = factory.CreateList(
                side, new Dock.Model.Mvvm.Controls.ProportionalDockSplitter(), (Dock.Model.Core.IDockable)documents),
        };

        var root = factory.CreateRootDock();
        root.Id = "Root";
        root.VisibleDockables = factory.CreateList<Dock.Model.Core.IDockable>(outer);
        root.ActiveDockable = outer;
        root.DefaultDockable = outer;
        factory.InitLayout(root);
        return root;
    }

    /// <summary>The tree as text, so "nothing else changed" is one comparison.</summary>
    private static string TreeShape(Dock.Model.Core.IDockable d, int depth = 0)
    {
        var s = new string(' ', depth * 2) + d.GetType().Name + " " + d.Id + "\n";
        if (d is IDock dock && dock.VisibleDockables is not null)
            foreach (var child in dock.VisibleDockables)
                s += TreeShape(child, depth + 1);
        return s;
    }

    private static IEnumerable<Dock.Model.Core.IDockable> VisibleTools(Dock.Model.Core.IDockable root) =>
        AllDocks(root).Where(d => d is Dock.Model.Controls.ITool);
    /// <summary>Every dock Id in a tree, for a structural assertion.</summary>
    private static IEnumerable<string> AllDockIds(Dock.Model.Core.IDockable? d)
    {
        if (d is null) yield break;
        if (d.Id is { Length: > 0 }) yield return d.Id;
        if (d is not Dock.Model.Core.IDock dock || dock.VisibleDockables is null) yield break;
        foreach (var child in dock.VisibleDockables)
            foreach (var id in AllDockIds(child)) yield return id;
    }
    /// <summary>
    /// Save the arrangement, then reopen it the way a RELAUNCH does: a brand-new factory builds its
    /// default layout first (fresh tool instances) and only then applies the workspace's own.
    /// </summary>
    private static CwsDockLayout SaveAndReopen(CwsDockLayout arranged)
    {
        var session1 = new CircuitRfDockFactory();
        session1.CreateLayout();
        var live = session1.CreateLayoutFromState(arranged);

        var json = DockLayoutSerialization.Write(DockLayoutCapture.Capture(live, []));
        var read = DockLayoutSerialization.TryRead(json);
        Assert.Null(read.Report);

        var session2 = new CircuitRfDockFactory();
        session2.CreateLayout();
        return DockLayoutCapture.Capture(session2.CreateLayoutFromState(read.Layout!), []);
    }
    /// <summary>A two-array design, so a targeted seed can be told apart from the default one.</summary>
    private static WBondDesign TwoArrayDesign()
    {
        var design = WBondEmbedding.DefaultDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G2",
            Wires = { LoopShape.CreateSeedWire(Point3.Mils(0, 20, 4), Point3.Mils(30, 20, 1),
                                               WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
                                               WBondViewModel.DefaultNewWireLoopHeightNm) },
        });
        return design;
    }
    /// <summary>A wirebond cell whose sidecar is on disk, attached to a live layout document.</summary>
    private static (LayoutEditorViewModel Layout, LayoutDocument Doc) WirebondCell(int wires)
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-wb5u-" + Guid.NewGuid().ToString("N")[..8]);
        string cellDir = Path.Combine(root, "amp");
        Directory.CreateDirectory(Path.Combine(cellDir, "layout"));
        string clay = Path.Combine(cellDir, "layout", "amp.clay");
        File.WriteAllText(clay, "{}");

        string sidecar = Path.Combine(cellDir, "layout", "amp.wBond");
        WBondIo.WriteFile(sidecar, Design(wires));

        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }, clay);
        Assert.True(WBondCell.TryAttach(layout, clay));

        try { Directory.Delete(root, recursive: true); } catch { /* the design is in memory now */ }

        return (layout, new LayoutDocument("amp.clay", layout, clay));
    }
}
