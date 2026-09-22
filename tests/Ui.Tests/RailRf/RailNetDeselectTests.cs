// A rail picked under "Pick the rail" can be put away again (owner, 2026-09-21).
//
// ── THE REPORT ────────────────────────────────────────────────────────────────────────────────
//
// Select a net in the pick list and it is highlighted in the panel and outlined on the board.
// Escape did nothing to it and neither did clicking empty board: the net picker deliberately keeps
// its own selection rather than joining the window's one, so HasSelection did not admit it existed
// and the handler returned before the command ever ran. The only way back was to pick another net.
//
// The reason the picker stays out of the group is unchanged and still good — it is the operand of
// the button directly beneath it, and clearing it on an unrelated row click would disable that
// button under the user's hand. That argument is about a click somewhere ELSE in the window. It
// says nothing about the two gestures that mean "nothing is selected".

using System;
using System.IO;
using Avalonia.Input;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailNetDeselectTests
{
    /// <summary>
    /// <b>Escape and a click on bare board both clear the picked net — and a click ON the board
    /// leaves it alone.</b>
    /// </summary>
    /// <remarks>
    /// One test because it is one claim: the net is deselectable by the two gestures that say so,
    /// and by nothing else. Asserting only the two clears would pass just as well if every press
    /// anywhere on the board dropped the selection, which is the behaviour the picker was kept out
    /// of the one-selection group to avoid.
    /// </remarks>
    [Fact]
    public void EscapeAndABareBoardClickClearThePickedNet_AClickOnCopperDoesNot()
    {
        var vm = OpenExample();
        var overlay = vm.BoardOverlayLayer;

        vm.SelectNet("+3V3");
        Assert.NotNull(vm.SelectedNet);
        Assert.True(vm.HasSelection);            // what the window's Escape handler gates on

        // A press ON copper is the user pointing at the board, not putting the selection away. It is
        // DECLINED either way — the canvas's own marquee, pan and hit test start on this press.
        Assert.False(overlay.OnPointerPressed(0, 0, Tol, KeyModifiers.None, 1));
        Assert.NotNull(vm.SelectedNet);

        Assert.False(overlay.OnPointerPressed(FarOffCopper, FarOffCopper, Tol, KeyModifiers.None, 1));
        Assert.Null(vm.SelectedNet);
        Assert.Null(vm.NetPreview);               // and the outline came off the board with it
        Assert.False(vm.HasSelection);

        // Escape, which is the same command reached by the key.
        vm.SelectNet("+3V3");
        Assert.True(vm.HasSelection);
        vm.ClearSelectionCommand.Execute(null);

        Assert.Null(vm.SelectedNet);
        Assert.Null(vm.NetPreview);
        Assert.Single(vm.Rails);                  // neither gesture touched the document
    }

    // ── the fixture ───────────────────────────────────────────────────────────────────────────

    /// <summary>The canvas's own click tolerance, in DBU — 10 µm on this board.</summary>
    private const long Tol = 10_000;

    /// <summary>A metre out, in DBU: past anything this board draws.</summary>
    private const long FarOffCopper = 1_000_000_000;

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
