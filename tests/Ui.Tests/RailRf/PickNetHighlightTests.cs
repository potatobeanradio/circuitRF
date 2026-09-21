// ================================================================
//  PickNetHighlightTests.cs — brief-railrf-19-unreachable-states.md §2
//
//  SELECTING A NET IN THE PICK LIST SHOWS YOU THE NET.
//
//  OnSelectedNetChanged notified the pick command and the button caption and nothing else: the
//  board did not move, nothing lit up, and the only way to find out what a net IS was to commit it
//  as a rail and look at the result. That is backwards — the pick is the moment a user needs to
//  check they picked the right thing, and on a board carrying +3V3, +3V3_A and VDD_IO the name is
//  not enough.
//
//  ── WHAT IS ASSERTED, AND WHAT IS NOT ─────────────────────────────────────────────────────────
//
//  The preview's GEOMETRY is compared against PdnRailRegions.Walk's own answer for the same net,
//  because the requirement is that it is the same walk and not a second one — a picture that could
//  disagree with the solve about what the rail IS is the one thing a preview must never be. The
//  ROLE is compared as colours, in both variants, because "distinct from a committed rail's
//  highlight" is a statement about what a reader sees. The CACHE is compared as a counter, for the
//  reason the solve loop's own counters exist: a timing assertion measures the machine.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PickNetHighlightTests
{
    /// <summary>
    /// <b>Selecting a net publishes a preview, it reaches the board, and it is the walk's own
    /// answer.</b>
    /// </summary>
    /// <remarks>
    /// <b>Nothing was published at HEAD</b> — there was no property to read. The comparison is
    /// against <c>PdnRailRegions.Walk</c> run directly with the same arguments: same islands, same
    /// extent, same total path count. Anything that merely LOOKED right would pass a preview built
    /// from a second, simpler notion of connectivity, which is exactly what R-rail19-2a forbids.
    /// </remarks>
    [Fact]
    public void SelectingANetPublishesThePreviewTheWalkItselfProduces()
    {
        var vm = Example();

        Assert.Null(vm.NetPreview);
        Assert.Null(vm.BoardOverlayLayer.NetPreview);

        vm.SelectNet("+3V3");

        var preview = vm.NetPreview;
        Assert.NotNull(preview);
        Assert.Equal("+3V3", preview!.Label);
        Assert.False(preview.IsEmpty);

        // It reached the PICTURE, not just the view model.
        Assert.Same(preview, vm.BoardOverlayLayer.NetPreview);

        var board = vm.Board!;
        var walked = PdnRailRegions.Walk(
            PdnMeshExtractor.BuildLayerRegions(board.Shapes, board.Technology),
            board.Technology, board.NetPoints, "+3V3",
            vm.Document.Rails[0].ReferenceLayer!.Value, board.ReferenceNet, []);

        var expected = walked.Power.SelectMany(i => i.Copper).Where(c => c.Paths.Count > 0).ToList();

        Assert.Equal(expected.Count, preview.Copper.Count);
        Assert.Equal(expected.Sum(c => c.Paths.Count), preview.Copper.Sum(c => c.Paths.Count));
        Assert.Equal(walked.Power.Aggregate(Bbox.Empty, (b, i) => b.Union(i.Bounds)), preview.Bounds);
    }

    /// <summary>
    /// <b>A preview does not look like a committed rail.</b>
    /// </summary>
    /// <remarks>
    /// R-rail19-2b, and it is the whole reason the preview has a colour role of its own: a preview
    /// drawn in the copper tab's own <c>Rail.CopperHighlight</c> would make a user think they had
    /// already pressed the button. Checked on BOTH variants, because a role that happens to differ
    /// in light and collide in dark is a role that is right half the time.
    /// </remarks>
    [Fact]
    public void ThePreviewRoleIsNotTheCommittedRailsRole()
    {
        Assert.NotEqual(ColorRole.RailCopperHighlight, ColorRole.RailNetPreview);
        Assert.NotEqual(RailMapTheme.Light.CopperHighlight, RailMapTheme.Light.NetPreview);
        Assert.NotEqual(RailMapTheme.Dark.CopperHighlight, RailMapTheme.Dark.NetPreview);

        // And the two marks are separate inputs to the overlay, so one cannot be set by setting the
        // other — a preview and the selected part's outline are answers to different questions.
        var overlay = new RailLayoutOverlay();
        overlay.NetPreview = new RailNetPreview("N", [], Bbox.Empty);
        Assert.Null(overlay.PartHighlight);
    }

    /// <summary>
    /// <b>Clearing the selection clears the preview, and re-selecting the same net does not walk
    /// again.</b>
    /// </summary>
    /// <remarks>
    /// Two halves of the same requirement that the preview is transient state and not a result.
    /// The counter is R-rail19-2c: arrow-keying down a list of two hundred nets must not re-walk two
    /// hundred times, and the cache lives for the loaded board — so the artwork changing is what
    /// drops it, which is the last assertion here.
    /// </remarks>
    [Fact]
    public void ClearingTheSelectionClearsThePreview_AndTheSameNetWalksOnce()
    {
        var vm = Example();

        vm.SelectNet("+3V3");
        int walks = vm.NetWalksPerformed;
        Assert.Equal(1, walks);

        vm.SelectNet(null);
        Assert.Null(vm.NetPreview);
        Assert.Null(vm.BoardOverlayLayer.NetPreview);

        vm.SelectNet("+3V3");
        Assert.NotNull(vm.NetPreview);
        Assert.Equal(walks, vm.NetWalksPerformed);      // the cache, not a second walk

        // And the board moving under it drops the cache. A re-resolved TECHNOLOGY is the cheapest
        // of the three routes to drive here and the least obvious of them: the stackup is what says
        // which layers a via joins, so a galvanic walk taken against the old one is not merely
        // stale, it is of a different connectivity. (The others are a new board and, on a document
        // with a live LayoutView, NotifyArtworkChanged.)
        vm.AdoptTechnology(TechnologyLayerSelection.WithLayers(vm.Board!.Technology, _ => true, null));
        Assert.Null(vm.NetPreview);

        // And the ROW goes with it — a row left highlighted over a board that has stopped outlining
        // it says the pick did nothing.
        Assert.Null(vm.SelectedNet);

        vm.SelectNet("+3V3");
        Assert.Equal(walks + 1, vm.NetWalksPerformed);
    }

    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);

        // THE COPPER READ, INLINE. Since 2026-09-21 the flatten and the galvanic walk happen off the
        // UI thread and their answers arrive on a later turn (RailRfViewModel.NetPreview.cs' header —
        // a real board made the window unresponsive for as long as they took). Nothing about WHAT is
        // computed changed, and that is what these tests are about, so the seam is closed here rather
        // than every assertion below being rewritten as a wait. The deferral itself is asserted by
        // RailRfReportedDefectsTests, which is the file that cares that it IS deferred.
        vm.ReadCopperOffThread = work => { work(); return System.Threading.Tasks.Task.CompletedTask; };

        Assert.Empty(vm.LoadDocumentReferences());
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
