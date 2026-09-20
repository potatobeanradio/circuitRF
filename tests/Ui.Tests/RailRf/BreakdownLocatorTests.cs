// ================================================================
//  BreakdownLocatorTests.cs — brief-railrf-19-unreachable-states.md §3
//
//  SELECT A BREAKDOWN ROW, FIND THE COPPER.
//
//  "22.7 mV, 45 %, 26.5 mm of 0.209 mm BOT copper" is the answer §2.4 exists to produce, and there
//  was no way to find out where on the board that copper is. The parts table — which is the LESS
//  valuable of the two — already marked its selected row. PdnBreakdownRow.GroupKey was written for
//  exactly this and says so in its own summary; what was missing was a row to select and a map from
//  the key to a place.
//
//  ── DRIVEN ON THE SHIPPED EXAMPLE, WITH A REAL SOLVE ──────────────────────────────────────────
//
//  A stubbed result would let the locator agree with a fixture about a group key that the real
//  aggregation does not mint. The whole mechanism is the key, so the key has to be the real one.
//  The fast reading on this board is well under a second.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class BreakdownLocatorTests
{
    /// <summary>
    /// <b>Selecting the top breakdown row marks its group's own copper, and the viewport moves to
    /// contain it.</b>
    /// </summary>
    /// <remarks>
    /// <b>Nothing was published at HEAD</b> — the breakdown was a paragraph of text.
    ///
    /// <para>"Its group's own" is checked against the cells the location carries rather than
    /// against a number: the mark's box has to be the extent of those cells, and every one of them
    /// has to be inside it. A box that merely overlapped the copper would pass a looser assertion
    /// and point at the wrong run.</para>
    ///
    /// <para>And the camera move is the requirement, not a nicety (R-rail19-3a): a 0.2 mm run on a
    /// 30 x 20 mm board at fit zoom is three pixels, and a highlight the user cannot find has
    /// answered nothing.</para>
    /// </remarks>
    [Fact]
    public void SelectingABreakdownRowMarksItsCopperAndBringsItOnScreen()
    {
        var vm = Solved();

        Bbox? asked = null;
        vm.ShowOnBoardHook = region => asked = region;

        var top = vm.BreakdownRows.First(r => vm.SelectedRailResult!
                                                 .BreakdownLocations.ContainsKey(r.GroupKey));
        vm.SelectedBreakdownRow = top;

        var place = vm.BreakdownLocation;
        Assert.NotNull(place);
        Assert.NotEmpty(place!.Cells);
        Assert.All(place.Cells, c => Assert.True(place.Bounds.Contains(c.X, c.Y),
            $"cell {c.X},{c.Y} is outside the group's own bounds."));

        var mark = vm.PartHighlight;
        Assert.NotNull(mark);
        Assert.Equal(place.Bounds, mark!.Body);
        Assert.Equal(mark, vm.BoardOverlayLayer.PartHighlight);

        Assert.NotNull(asked);
        Assert.Equal(place.Bounds, asked!.Value);
        Assert.Equal("", vm.BreakdownLocatorNote);
    }

    /// <summary>
    /// <b>A row that is not copper clears the highlight and says what it is.</b>
    /// </summary>
    /// <remarks>
    /// R-rail19-3b. A source's own series resistance, a part's ESR and an observation port have
    /// nowhere on the board to point at. Leaving the previous row's copper lit would be a locator
    /// pointing at the WRONG thing, which is worse than no locator at all — so the second half of
    /// this test selects a copper row FIRST, which is the state the defect would hide in.
    /// </remarks>
    [Fact]
    public void ARowWithNoCopperClearsTheHighlightAndStatesWhatItIs()
    {
        var vm = Solved();
        var located = vm.SelectedRailResult!.BreakdownLocations;

        var copper = vm.BreakdownRows.First(r => located.ContainsKey(r.GroupKey));
        var notCopper = vm.BreakdownRows.First(r => !located.ContainsKey(r.GroupKey));

        vm.SelectedBreakdownRow = copper;
        Assert.NotNull(vm.BoardOverlayLayer.PartHighlight);

        vm.SelectedBreakdownRow = notCopper;

        Assert.Null(vm.BreakdownLocation);
        Assert.Null(vm.PartHighlight);
        Assert.Null(vm.BoardOverlayLayer.PartHighlight);      // no residue of the first
        Assert.Contains("is not copper", vm.BreakdownLocatorNote, StringComparison.Ordinal);
        Assert.Contains(notCopper.Row.Label, vm.BreakdownLocatorNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The breakdown joins the window's one selection rather than keeping a second.</b>
    /// </summary>
    /// <remarks>
    /// The board can only mark one thing, so a parts-table row and a breakdown row both selected
    /// would leave two marks up and no way to tell which answered which question — the report that
    /// produced <c>RailRfViewModel.Selection.cs</c> in the first place, one list further on.
    /// Escape clears it for the same reason it clears the other four.
    /// </remarks>
    [Fact]
    public void TheBreakdownSelectionIsTheWindowsOneSelection()
    {
        var vm = Solved();
        vm.RebuildParts();

        vm.SelectedPart = vm.Parts.First(p => p.Refdes == "C11");
        vm.SelectedBreakdownRow = vm.BreakdownRows[0];

        Assert.Null(vm.SelectedPart);
        Assert.True(vm.HasRowSelection);

        vm.ClearRowSelectionCommand.Execute(null);

        Assert.Null(vm.SelectedBreakdownRow);
        Assert.Null(vm.BoardOverlayLayer.PartHighlight);
        Assert.Equal("", vm.BreakdownLocatorNote);
    }

    /// <summary>The shipped example, with the fast reading in hand.</summary>
    private static RailRfViewModel Solved()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        Assert.Empty(vm.LoadDocumentReferences());
        Assert.True(vm.CanRun, vm.RunBlockedReason);

        vm.RunCommand.Execute(null);

        Assert.NotNull(vm.SelectedRailResult);
        Assert.NotEmpty(vm.BreakdownRows);
        return vm;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
