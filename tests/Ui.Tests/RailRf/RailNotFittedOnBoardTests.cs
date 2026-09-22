// An unmounted part is MARKED on the board, and its land pattern is left exactly where it is
// (owner, 2026-09-21).
//
// ── THE QUESTION, AND WHY THE ANSWER IS A MARK ────────────────────────────────────────────────
//
// Asked: when a part is unmounted with the parts table's checkbox, should it come off the board
// rendering? No — what sits under a part there is its LAND PATTERN, which is copper, mask and
// silkscreen, and every bit of it is etched and printed whether or not a component is soldered on
// top. RailPart.Mounted's own contract says the .clay is never touched (R-rail23-1c), so removing
// the pads would draw a board nobody fabricated.
//
// What was actually missing is that the board said NOTHING: the table greys the row and the picture
// was identical either way. That is the shape RailMarkerKind.Observation already forbids — "not
// added" and "added with no current" must not look the same — so the part is marked, not removed.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.RailRf;
using System.Collections.Generic;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailNotFittedOnBoardTests
{
    /// <summary>
    /// <b>Unmounting marks the part on the board at the same pads a selection would, re-mounting
    /// takes the mark away, and the artwork never moves.</b>
    /// </summary>
    /// <remarks>
    /// One test because it is one claim with a negative half that carries most of it: a mark that
    /// appeared would pass just as well if the footprint had been dropped from the layout, which is
    /// the thing this design deliberately does not do.
    /// </remarks>
    [Fact]
    public void UnmountingMarksThePartOnTheBoard_AtItsOwnPads_AndLeavesTheArtworkAlone()
    {
        var vm = OpenExample();

        var view = vm.Board!.View!;
        int instancesBefore = view.Instances.Count;
        int shapesBefore = vm.Board!.Shapes.Count;

        var row = vm.Parts.First(p => p.Refdes is { Length: > 0 });
        string refdes = row.Refdes;

        Assert.Empty(vm.BoardOverlayLayer.NotFitted);

        // What the SELECTION resolves for that part — the mark the board already draws when the row
        // is picked. The not-fitted mark has to be that same geometry, off the same resolver.
        vm.SelectedPart = row;
        var selected = vm.PartHighlight;
        Assert.NotNull(selected);
        vm.SelectedPart = null;

        Assert.True(vm.SetPartMounted(refdes, false));

        var mark = Assert.Single(vm.BoardOverlayLayer.NotFitted);
        Assert.Equal(refdes, mark.Label);
        Assert.Equal(selected!.Pads, mark.Pads);
        Assert.Equal(selected.Outline, mark.Outline);

        // THE BOARD IS UNTOUCHED. The land pattern is etched copper — this says what is fitted to
        // the geometry, never what the geometry is.
        Assert.Equal(instancesBefore, view.Instances.Count);
        Assert.Equal(shapesBefore, vm.Board!.Shapes.Count);

        // And it comes back off.
        Assert.True(vm.SetPartMounted(refdes, true));
        Assert.Empty(vm.BoardOverlayLayer.NotFitted);
    }

    /// <summary>
    /// <b>The mark has a colour of its own, on both variants, and it is opaque.</b>
    /// </summary>
    /// <remarks>
    /// The one failure that is invisible everywhere else: a role the theme does not carry resolves
    /// to transparent, and a mark drawn in nothing is a feature that is wired, tested and not on
    /// screen. Distinct from the selection's because the two mean opposite things and can land on
    /// the same pads.
    /// </remarks>
    [Fact]
    public void TheNotFittedMarkHasItsOwnOpaqueColourOnBothVariants()
    {
        foreach (var theme in new[] { RailMapTheme.Light, RailMapTheme.Dark })
        {
            Assert.Equal(255, theme.NotFitted.Alpha);
            Assert.NotEqual(theme.PartSelection, theme.NotFitted);
            Assert.NotEqual(theme.NetPreview, theme.NotFitted);
        }
    }

    /// <summary>
    /// <b>The marks are in the COPIED and EXPORTED picture, and they are there because they were
    /// asked for.</b>
    /// </summary>
    /// <remarks>
    /// <b>Through the real export context</b>, which is the seam <c>RailGraphicExport</c> itself
    /// builds — <c>LayoutRenderOptions.RailMap</c>'s own remarks name the failure this gates: an
    /// overlay nobody added to that explicit list is silently absent from an export, the picture is
    /// still produced, it still looks correct, and the one thing it was copied for is missing.
    ///
    /// <para>The NEGATIVE half is what makes it a gate rather than a colour test: the same context
    /// with no marks must not carry the colour, or the assertion would be satisfied by anything on
    /// the page that happened to be grey.</para>
    /// </remarks>
    [Fact]
    public void TheMarksReachTheExportedPicture_AndOnlyWhenThereAreAny()
    {
        var mark = new RailPartHighlight("C7", [(Mm(10), Mm(0.2)), (Mm(11), Mm(0.2))]);

        string with    = SvgOf(ContextFor([mark]));
        string without = SvgOf(ContextFor([]));

        string colour = Hex(RailMapTheme.Light.NotFitted);
        Assert.Contains(colour, with, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(colour, without, StringComparison.OrdinalIgnoreCase);

        // …and it is the mark, not just ink: the refdes is drawn beside it.
        Assert.Contains("C7", with, StringComparison.Ordinal);
    }

    // ── the fixture ───────────────────────────────────────────────────────────────────────────

    private const int Dbu = 1000;
    private static long Mm(double mm) => (long)(mm * 1000 * Dbu);

    private static string Hex(SkiaSharp.SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>The real export context, through the seam <c>RailGraphicExport</c> itself uses.</summary>
    private static LayoutClipboard.ExportContext ContextFor(IReadOnlyList<RailPartHighlight> notFitted)
    {
        var board = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um };
        board.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4),
        });

        return LayoutClipboard.MakeExportContext(
            RailGraphicExport.PayloadOf(board),
            tech: null,
            LayoutRenderTheme.Light,
            transparent: true,
            baseDir: "",
            railMap: RailMapScene.Build(null, RailMapKind.Drop, Dbu),
            railTheme: RailMapTheme.Light,
            railNotFitted: notFitted);
    }

    private static string SvgOf(LayoutClipboard.ExportContext ctx)
    {
        var svg = LayoutClipboard.TryRenderToSvg(ctx);
        Assert.NotNull(svg);
        return svg!.Value.Svg;
    }


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
