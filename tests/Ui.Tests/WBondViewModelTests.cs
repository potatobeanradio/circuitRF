using System.IO;
using System.Linq;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-C2 — the document, the view-model and the live panel (brief-wbond-wbc).
/// </summary>
public class WBondViewModelTests
{
    private static WBondDesign Design(int wires = 4, int arrays = 1)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wires; w++)
            {
                double y = a * 200 + w * 6;
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            }
            design.Arrays.Add(array);
        }
        return design;
    }

    // ---------------------------------------------------------------- the performance seam

    /// <summary>
    /// <b>A drag must take the incremental path, not a rebuild.</b>
    ///
    /// <para>The two produce identical answers and differ by ~30x in cost, so nothing but a counter
    /// can tell them apart — which is exactly why the counters exist. Routing a drag down the
    /// structural path turns a 60 fps editor into a 6 fps one, invisibly.</para>
    /// </summary>
    [Fact]
    public void ADrag_TakesTheIncrementalPath_AndNeverRebuilds()
    {
        var vm = new WBondViewModel(Design(wires: 6));
        int rebuildsBefore = vm.RebuildCount;

        vm.Selection = new WireSelection { Wires = { 0, 1 } };
        for (int frame = 0; frame < 20; frame++)
            vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile);

        Assert.Equal(rebuildsBefore, vm.RebuildCount);
        Assert.Equal(20, vm.IncrementalUpdateCount);
    }

    /// <summary>
    /// A structural change DOES rebuild — and duplicate-with-pitch does it exactly once for the whole
    /// batch, which is the property WB26 is really asking for.
    /// </summary>
    [Fact]
    public void DuplicateWithPitch_RebuildsExactlyOnceForTheWholeBatch()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        int before = vm.RebuildCount;

        int made = vm.DuplicateWithPitch(0, 0, WBondUnits.ToNm(6.0, WBondUnit.Mil), 49);

        Assert.Equal(49, made);
        Assert.Equal(before + 1, vm.RebuildCount);
        Assert.Equal(50, vm.Design.WireCount);
    }

    // ---------------------------------------------------------------- the live readout

    /// <summary>Editing republishes the readout, and the inductance actually moves.</summary>
    [Fact]
    public void RaisingTheLoop_RaisesTheReportedInductance()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        double before = vm.Readout.Rows[0].SelfPicoHenries;

        vm.SelectAllWires();
        int moved = vm.ScaleSelection(spanFactor: 1.0, heightFactor: 1.6, moveOutputFoot: true);

        Assert.Equal(4, moved);
        Assert.True(vm.Readout.Rows[0].SelfPicoHenries > before,
            $"A taller loop must read higher: {before:F1} pH -> {vm.Readout.Rows[0].SelfPicoHenries:F1} pH.");
    }

    /// <summary>The readout event fires on every edit, so the panel and canvas stay in step.</summary>
    [Fact]
    public void EveryEdit_RaisesReadoutChangedAndDirty()
    {
        var vm = new WBondViewModel(Design());
        int readouts = 0, dirty = 0;
        vm.ReadoutChanged += () => readouts++;
        vm.DirtyChanged += () => dirty++;

        vm.Selection = new WireSelection { Wires = { 0 } };
        vm.NudgeSelection(1, 0, coarse: true, EditorView.Layout);

        Assert.Equal(1, readouts);
        Assert.Equal(1, dirty);
    }

    /// <summary>
    /// Reversing a wire is a drag-path edit — it changes no point COUNT — and it changes the readout,
    /// because it negates that wire's off-diagonal mutuals.
    /// </summary>
    [Fact]
    public void ReversingAWire_TakesTheDragPathAndMovesTheReadout()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        int rebuilds = vm.RebuildCount;
        double before = vm.Readout.Rows[0].SelfPicoHenries;

        vm.Selection = new WireSelection { Wires = { 1 } };
        int reversed = vm.ReverseSelection();

        Assert.Equal(1, reversed);
        Assert.Equal(rebuilds, vm.RebuildCount);
        Assert.NotEqual(before, vm.Readout.Rows[0].SelfPicoHenries);
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public void Undo_RestoresTheGeometryAndTheReadout()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        double before = vm.Readout.Rows[0].SelfPicoHenries;
        var pointsBefore = vm.Design.AllWires().First().Points.ToArray();

        vm.SelectAllWires();
        vm.ScaleSelection(spanFactor: 1.0, heightFactor: 2.0, moveOutputFoot: true);
        Assert.NotEqual(before, vm.Readout.Rows[0].SelfPicoHenries);

        Assert.True(vm.CanUndo);
        vm.Undo();

        Assert.Equal(pointsBefore, vm.Design.AllWires().First().Points.ToArray());
        Assert.Equal(before, vm.Readout.Rows[0].SelfPicoHenries, before * 1e-9);
    }

    /// <summary>Undo across a STRUCTURAL change removes the wires it added.</summary>
    [Fact]
    public void Undo_AcrossADuplicate_RemovesTheAddedWires()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        vm.DuplicateWithPitch(0, 0, WBondUnits.ToNm(6.0, WBondUnit.Mil), 9);
        Assert.Equal(10, vm.Design.WireCount);

        vm.Undo();

        Assert.Equal(1, vm.Design.WireCount);
        Assert.Single(vm.Readout.Rows);
        Assert.Equal(1, vm.Readout.Rows[0].WireCount);
    }

    [Fact]
    public void Redo_ReappliesTheEdit()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        vm.SelectAllWires();
        vm.ScaleSelection(spanFactor: 1.0, heightFactor: 1.8, moveOutputFoot: true);
        double raised = vm.Readout.Rows[0].SelfPicoHenries;

        vm.Undo();
        Assert.True(vm.CanRedo);
        vm.Redo();

        Assert.Equal(raised, vm.Readout.Rows[0].SelfPicoHenries, raised * 1e-9);
    }

    /// <summary>
    /// A no-op edit must not leave an empty step on the undo stack.
    ///
    /// <para>This used to be stated of Detach, which had exactly that shape — the second call found
    /// the wire already detached and had to leave the stack alone. With the loop-profile object
    /// removed (2026-08-18) the same obligation sits on the scale gesture, which is where a user
    /// actually meets it: an alt-drag that resolves to a factor of 1 is one frame of a live drag.</para>
    /// </summary>
    [Fact]
    public void AnEditThatChangesNothing_LeavesNoUndoStep()
    {
        var vm = new WBondViewModel(Design(wires: 2));
        vm.Selection = new WireSelection { Wires = { 0 } };

        Assert.Equal(1, vm.ScaleSelection(spanFactor: 1.0, heightFactor: 1.4, moveOutputFoot: true));
        vm.Undo();

        int depthProbe = vm.CanUndo ? 1 : 0;
        Assert.Equal(0, depthProbe);

        // Both factors 1.0: nothing to do, and nothing left behind.
        Assert.Equal(0, vm.ScaleSelection(spanFactor: 1.0, heightFactor: 1.0, moveOutputFoot: true));
        Assert.False(vm.CanUndo);
    }

    // ---------------------------------------------------------------- the panel

    /// <summary>
    /// <b>pH, fixed, never auto-ranged (WB27a / D9).</b> A large and a small array must both read in
    /// pH — the whole reason the unit is fixed is that a drag must not make a number appear to jump
    /// by 1000x when the geometry moved by a mil.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(42.0)]
    [InlineData(1234.5)]
    [InlineData(98765.4)]
    public void ThePanel_AlwaysFormatsInPicoHenries(double value)
    {
        string text = WBondPanelViewModel.FormatPicoHenries(value);

        Assert.EndsWith(" pH", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("nH", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("µH", text, System.StringComparison.Ordinal);
    }

    /// <summary>The panel follows the view-model, including the current-share ramp.</summary>
    [Fact]
    public void ThePanel_FollowsTheViewModel()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 5, arrays: 2)));

        Assert.Equal(2, document.Panel.Rows.Count);
        Assert.Contains("image plane", document.Panel.ReturnPath, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(document.Panel.ReturnPathUndeclared);

        var row = document.Panel.Rows[0];
        Assert.EndsWith(" pH", row.Self, System.StringComparison.Ordinal);
        Assert.Equal(5, row.CurrentRamp.Count);

        // The ramp is normalised to the array's own peak, so its maximum is exactly 1.
        Assert.Equal(1.0, row.CurrentRamp.Max(), 1e-12);
        Assert.All(row.CurrentRamp, v => Assert.InRange(v, 0.0, 1.0));

        // Edge wires carry more than the middle — real array current crowding.
        Assert.True(row.CurrentRamp[0] > row.CurrentRamp[2]);
    }

    /// <summary>An undeclared return path is surfaced as a problem, not left blank (WB20).</summary>
    [Fact]
    public void ThePanel_FlagsAnUndeclaredReturnPath()
    {
        var design = Design();
        design.GroundPlane.Enabled = false;

        var document = new WBondDocumentViewModel(new WBondViewModel(design));

        Assert.True(document.Panel.ReturnPathUndeclared);
        Assert.Contains("UNDECLARED", document.Panel.ReturnPath, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows are updated IN PLACE when the shape is unchanged, so a drag does not churn the bound
    /// collection sixty times a second and make every row flicker.
    /// </summary>
    [Fact]
    public void ThePanel_UpdatesRowsInPlaceDuringADrag()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 4)));
        var firstRow = document.Panel.Rows[0];

        document.Editor.Selection = new WireSelection { Wires = { 0 } };
        for (int i = 0; i < 10; i++)
            document.Editor.NudgeSelection(0, 1, coarse: false, EditorView.Profile);

        Assert.Same(firstRow, document.Panel.Rows[0]);
    }

    // ---------------------------------------------------------------- the document shell

    [Fact]
    public void ADocument_StartsCleanAndScratchAndGoesDirtyOnEdit()
    {
        var document = new WBondDocument(new WBondViewModel(Design()));

        Assert.True(document.IsScratch);
        Assert.False(document.IsDirty);
        // Named the way every other scratch document is (owner, 2026-08-16) — it used to open as the
        // bare word "wBond", which named the tool rather than the document.
        Assert.Equal(WBondDocument.DefaultScratchTitle, document.Title);

        document.ViewModel.Editor.Selection = new WireSelection { Wires = { 0 } };
        document.ViewModel.Editor.NudgeSelection(0, 1, coarse: false, EditorView.Profile);

        Assert.True(document.IsDirty);
        Assert.Contains("•", document.Title, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SavingAndReopening_RoundTripsTheDesignAndClearsDirty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wbond-doc-{System.Guid.NewGuid():N}.wBond");
        try
        {
            var document = new WBondDocument(new WBondViewModel(Design(wires: 3)));
            document.ViewModel.Editor.SelectAllWires();
            document.ViewModel.Editor.ScaleSelection(
                spanFactor: 1.0, heightFactor: 1.5, moveOutputFoot: true);
            Assert.True(document.IsDirty);

            document.Save(path);

            Assert.False(document.IsDirty);
            Assert.False(document.IsScratch);
            Assert.DoesNotContain("•", document.Title, System.StringComparison.Ordinal);

            var reopened = WBondDocument.Open(path);
            Assert.Equal(3, reopened.ViewModel.Editor.Design.WireCount);
            Assert.Equal(
                document.ViewModel.Editor.Readout.Rows[0].SelfPicoHenries,
                reopened.ViewModel.Editor.Readout.Rows[0].SelfPicoHenries,
                1e-9);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>A scratch document with nowhere to save says so rather than throwing something opaque.</summary>
    [Fact]
    public void SavingAScratchDocumentWithNoPath_IsRefusedClearly()
    {
        var document = new WBondDocument(new WBondViewModel(Design()));
        var ex = Assert.Throws<System.InvalidOperationException>(() => document.Save());
        Assert.Contains("path", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
