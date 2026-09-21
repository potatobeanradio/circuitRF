// A left click on the board no longer edits the document, and Ctrl+Z takes an edit back
// (owner, 2026-09-20).
//
// ── THE REPORT ────────────────────────────────────────────────────────────────────────────────
//
// Open the shipped Sensor board, press Run, click inside the green rectangle. What the click did
// was MAKE A RAIL — `rail at (0, 0) µm`, anchored at the pointer, selected — which states no
// reference layer, so the run gate correctly refused the whole rail set and turned the reference
// combo orange. Every further click made another one. Escape was wired to the row selections and
// this is not one; Ctrl+Z did nothing because this window had no undo stack at all.
//
// ── THE FIXTURE IS THE SHIPPED EXAMPLE, DELIBERATELY ──────────────────────────────────────────
//
// It is the document the report is about, it ships, and the defect is in which gesture reaches the
// document rather than in anything a synthetic board would have had to reproduce. RailWindowTests
// picks the same fixture for the same reason.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class PourPickArmingTests
{
    /// <summary>
    /// <b>An unarmed click makes nothing, an armed one makes a rail, and the arming is one-shot.</b>
    /// </summary>
    /// <remarks>
    /// All three in one test because they are one claim: the gesture is unchanged and the press in
    /// front of it is the whole fix. Asserting only that the click stopped working would be a test
    /// that passes when the feature has been deleted.
    /// </remarks>
    [Fact]
    public void ABareClickMakesNoRail_AnArmedOneDoes_AndTheArmingIsOneShot()
    {
        var vm = OpenExample();

        Assert.Equal(["+3V3"], vm.Rails);
        Assert.True(vm.CanRun);

        // UNARMED: the overlay carries no pick at all, so the press is declined before it is even
        // hit-tested and reaches the canvas's own marquee and pan (§11.6).
        Assert.False(vm.IsPickingFromBoard);
        Assert.Null(vm.BoardOverlayLayer.PourPick);

        // ARMED by the button, and the pointer says so — the overlay answers the canvas's own
        // cursor question off the gesture itself, so the two cannot come apart.
        vm.PickFromBoardCommand.Execute(null);
        Assert.True(vm.IsPickingFromBoard);
        Assert.True(vm.BoardOverlayLayer.CrosshairArmed);
        Assert.Equal("Now click the board", vm.PickFromBoardText);

        Assert.True(vm.BoardOverlayLayer.PourPick!(0, 0, Tol));
        Assert.Equal(2, vm.Rails.Count);

        // ONE-SHOT. A tool left armed behind the user is the reported defect with one more step in
        // front of it, so the pick disarms itself and the next click is an ordinary one.
        Assert.False(vm.IsPickingFromBoard);
        Assert.Null(vm.BoardOverlayLayer.PourPick);
        Assert.False(vm.BoardOverlayLayer.CrosshairArmed);
    }

    /// <summary>
    /// <b>Escape disarms it</b> — the third thing this window's Escape has to reach.
    /// </summary>
    /// <remarks>
    /// Through <c>HasSelection</c>, which is the gate the window's own Escape handler reads before
    /// it runs the command. An armed pick that the command cleared but the gate did not admit to
    /// would be Escape silently inert again, which is the shape <c>RailRfViewModel.Selection</c>'s
    /// header is entirely about.
    /// </remarks>
    [Fact]
    public void EscapeDisarmsThePourPick()
    {
        var vm = OpenExample();

        vm.PickFromBoardCommand.Execute(null);
        Assert.True(vm.HasSelection);            // what the window's Escape handler gates on

        vm.ClearSelectionCommand.Execute(null);

        Assert.False(vm.IsPickingFromBoard);
        Assert.Null(vm.BoardOverlayLayer.PourPick);
        Assert.Single(vm.Rails);                 // and it took nothing with it
    }

    /// <summary>
    /// <b>Ctrl+Z takes the rail back</b>, and the run gate is passable again on the other side.
    /// </summary>
    /// <remarks>
    /// The gate is the half that matters. A rail with no reference layer blocks the whole rail set,
    /// so an undo that restored the rail list but left the window refusing would have taken back the
    /// edit and not the consequence — which is what the user is actually pressing the key to escape.
    /// </remarks>
    [Fact]
    public void UndoTakesBackAPickedRail_AndRedoPutsItBack()
    {
        var vm = OpenExample();

        Assert.False(vm.CanUndo);

        vm.PickFromBoardCommand.Execute(null);
        vm.BoardOverlayLayer.PourPick!(0, 0, Tol);

        Assert.Equal(2, vm.Rails.Count);
        Assert.False(vm.CanRun);
        Assert.Contains("remove it with the button beside the selector", vm.RunBlockedReason,
                        StringComparison.Ordinal);
        Assert.True(vm.CanUndo);

        vm.UndoCommand.Execute(null);

        Assert.Equal(["+3V3"], vm.Rails);
        Assert.Equal("+3V3", vm.SelectedRailName);
        Assert.True(vm.CanRun);
        Assert.Equal("", vm.RunBlockedReason);
        Assert.False(vm.IsDirty);                // back to the bytes on disk

        vm.RedoCommand.Execute(null);
        Assert.Equal(2, vm.Rails.Count);
        Assert.True(vm.IsDirty);
    }

    /// <summary>
    /// <b>Undo reaches an ordinary value edit too, not just the rail set.</b>
    /// </summary>
    /// <remarks>
    /// The entry hangs off <c>QueueResolve</c> — this view model's one documented funnel for a
    /// committed edit — rather than off the rail commands, which is what keeps a new edit site from
    /// silently arriving without one. A source's voltage is the cheapest witness to that: nothing in
    /// the undo file names it.
    /// </remarks>
    [Fact]
    public void UndoReachesAValueEditThroughTheEditFunnel()
    {
        var vm = OpenExample();

        var source = Assert.Single(vm.Sources);
        string was = source.VoltageEntry;

        source.VoltageEntry = "3.0 V";
        Assert.NotEqual(was, vm.Sources[0].VoltageEntry);
        Assert.True(vm.CanUndo);

        vm.UndoCommand.Execute(null);
        Assert.Equal(was, vm.Sources[0].VoltageEntry);
    }

    /// <summary>The selected rail survives an undo that did not remove it.</summary>
    /// <remarks>
    /// <c>RebuildRails</c> selects the FIRST rail, which is its contract everywhere else. Left alone
    /// here it would mean that taking back a typed number also moved the whole window to another
    /// rail — §11.3's fourth point is that the selector moves everything.
    /// </remarks>
    [Fact]
    public void UndoKeepsTheSelectedRailWhereItStillExists()
    {
        var vm = OpenExample();

        vm.PickFromBoardCommand.Execute(null);
        vm.BoardOverlayLayer.PourPick!(0, 0, Tol);
        string picked = vm.SelectedRailName!;
        Assert.NotEqual("+3V3", picked);

        // An edit ON that rail, then take it back: the window must still be showing it.
        vm.Sources[0].VoltageEntry = "1.8 V";
        vm.UndoCommand.Execute(null);

        Assert.Equal(picked, vm.SelectedRailName);
    }

    // ── the fixture ───────────────────────────────────────────────────────────────────────────

    /// <summary>The canvas's own click tolerance, in DBU — 10 µm on this board.</summary>
    private const long Tol = 10_000;

    private static RailRfViewModel OpenExample()
    {
        string path = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(path), $"The shipped example moved: {path}");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(path), path);
        vm.LoadDocumentReferences();
        return vm;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
