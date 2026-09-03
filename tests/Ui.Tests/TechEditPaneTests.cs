using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request, 2026-09-02: the layout editor's Technology ▾ ▸ Edit… opens the <c>.ctech</c> in the
/// document area to the RIGHT of the <c>.clay</c>, only as wide as the layer table's Name / Vis / Sel /
/// Color columns — so layers can be toggled with the artwork still on screen beside them.
///
/// <para>These run headlessly for the reason <see cref="SplitDocumentAreaLayoutTests"/> records: the
/// Dock MVVM model types are plain C#. The two halves that need a real Window — the flyout item and the
/// view's own call into the workspace — are pinned by source scan, the idiom this suite already uses
/// for every other dialog and menu.</para>
/// </summary>
public class TechEditPaneTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string RepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    // ── The affordance ────────────────────────────────────────────────────────

    /// <summary>The ellipsis is the promise that something further opens — the owner asked for it the
    /// same day the command started opening a pane rather than just another tab.</summary>
    [Fact]
    public void TheFlyoutItemIsEditWithAnEllipsis()
    {
        var axaml = RepoFile("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        Assert.Contains("<MenuItem Header=\"Edit…\" Click=\"OnEditTechnologyClick\"/>", axaml);
        Assert.DoesNotContain("<MenuItem Header=\"Edit\" Click=\"OnEditTechnologyClick\"/>", axaml);
    }

    /// <summary>
    /// The layout editor asks for the BESIDE-the-layout open, not the plain one. A view that quietly
    /// went back to <c>OpenTechnologyDocument</c> would still work — it would just open a tab, which is
    /// the behaviour this change exists to replace, and nothing would fail.
    /// </summary>
    [Fact]
    public void TheLayoutEditorAsksForThePane_AndHandsOverItsOwnWidth()
    {
        var src = RepoFile("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        var body = src[src.IndexOf("private void OnEditTechnologyClick", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("private void OnOpenSourceWorkspaceClick", StringComparison.Ordinal)];

        Assert.Contains("OpenTechnologyDocumentBesideLayout(techPath, doc, Bounds.Width)", body);
    }

    // ── The width ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The four columns the owner named, read back out of the view that declares them. This is the
    /// half of the width that is arithmetic: if the layer table's columns are ever re-cut, the
    /// constant the pane opens at has to be re-measured, and this is what says so.
    /// </summary>
    [Fact]
    public void TheColumnFloorMatchesTheLayerTablesOwnColumnWidths()
    {
        var axaml = RepoFile("src", "Ui", "Views", "Layout", "TechEditorView.axaml");

        // Name, Vis, Sel, Color — the first four of the column list the header row and every data row
        // both declare. The list appears several times, identically, and any one of them will do.
        var m = Regex.Match(axaml,
            @"<ColumnDefinition Width=""\*"" MinWidth=""(?<name>\d+)""/>\s*" +
            @"<ColumnDefinition Width=""(?<vis>\d+)""/>\s*" +
            @"<ColumnDefinition Width=""(?<sel>\d+)""/>\s*" +
            @"<ColumnDefinition Width=""(?<color>\d+)""/>");
        Assert.True(m.Success, "the layer table's column list changed — re-measure the Edit… pane width");

        double columns = double.Parse(m.Groups["name"].Value)
                       + double.Parse(m.Groups["vis"].Value)
                       + double.Parse(m.Groups["sel"].Value)
                       + double.Parse(m.Groups["color"].Value);

        // Plus the three 4-unit gaps between them and the grid's own 4-unit margin each side.
        Assert.Equal(WorkspaceViewModel.TechEditPaneColumnFloor, columns + 3 * 4 + 2 * 4);
    }

    /// <summary>And the width the pane actually opens at clears that floor — with room left over for a
    /// readable layer NAME, which is the column that would otherwise sit at its 110-unit minimum.</summary>
    [Fact]
    public void ThePaneIsWideEnoughForThoseColumns()
    {
        Assert.True(WorkspaceViewModel.TechEditPaneWidth >= WorkspaceViewModel.TechEditPaneColumnFloor,
            $"{WorkspaceViewModel.TechEditPaneWidth} is narrower than the four columns it must show " +
            $"({WorkspaceViewModel.TechEditPaneColumnFloor}).");
    }

    /// <summary>
    /// A pane is sized as a SHARE of its parent, so the requested pixel width only means anything
    /// against a measured one — and the clamp is what keeps a narrow window from giving the layout less
    /// than half the room, which is the thing the request was actually about.
    /// </summary>
    [Theory]
    [InlineData(1600, 340.0 / 1600)]   // an ordinary window: exactly the width asked for
    [InlineData(600,  0.5)]            // narrow: the layout keeps half rather than shrinking to a sliver
    [InlineData(4000, 0.15)]           // very wide: a floor, so the pane cannot open as a hairline
    [InlineData(0,    0.3)]            // never laid out — a plain share rather than a divide by zero
    public void TheProportionIsTheAskedWidth_Clamped(double available, double expected)
        => Assert.Equal(expected, WorkspaceViewModel.TechEditPaneProportion(available), 6);

    // ── The split itself ──────────────────────────────────────────────────────

    [Fact]
    public void TheTechnologyLandsInItsOwnPane_RightOfTheLayout_AtTheAskedProportion()
    {
        var (f, _, host, layout, tech) = Shell();

        Assert.True(f.SplitDocumentRightOf(tech, layout, 0.2));

        // The layout keeps its strip; the technology is in a new document dock beside it.
        Assert.Same(host, layout.Owner);
        Assert.NotSame(host, tech.Owner);
        var pane = Assert.IsAssignableFrom<IDocumentDock>(tech.Owner);

        // Right of, not left of: the wrapper's children are [host, splitter, pane], in that order.
        var wrapper = Assert.IsAssignableFrom<IProportionalDock>(host.Owner);
        Assert.Equal(Orientation.Horizontal, wrapper.Orientation);
        Assert.Equal(
            new IDockable[] { host, pane },
            wrapper.VisibleDockables!.Where(d => d is not IProportionalDockSplitter).ToArray());

        Assert.Equal(0.2, pane.Proportion, 6);
        Assert.Equal(0.8, host.Proportion, 6);
        Assert.Same(tech, pane.ActiveDockable);
    }

    /// <summary>
    /// A pane that outlives its last document is a dead region the user cannot dismiss — the same
    /// reason a RESTORED extra pane is collapsable, and the primary strip is not.
    /// </summary>
    [Fact]
    public void TheNewPaneIsCollapsable_SoClosingItsLastTabGivesTheSpaceBack()
    {
        var (f, _, _, layout, tech) = Shell();
        f.SplitDocumentRightOf(tech, layout, 0.2);

        Assert.True(((IDocumentDock)tech.Owner!).IsCollapsable);
    }

    /// <summary>The arrangement is an ordinary split document area, so it is captured — and therefore
    /// comes back — exactly like one the user made by dragging a tab to the edge.</summary>
    [Fact]
    public void TheSplitIsCaptured_SoItSurvivesAReopen()
    {
        var (f, root, _, layout, tech) = Shell();
        f.SplitDocumentRightOf(tech, layout, 0.2);

        var keys = new Dictionary<IDockable, string>
        {
            [layout] = "layout/board.clay",
            [tech]   = "tech/pcb.ctech",
        };
        var region = DockLayoutCapture.CaptureDocumentRegion(root, d => keys.GetValueOrDefault(d));

        Assert.NotNull(region);
        Assert.Equal("Horizontal", region!.Orientation);
        Assert.Equal(
            new[] { "layout/board.clay", "tech/pcb.ctech" },
            region.Children.Select(c => string.Join(",", c.Documents)).ToArray());
    }

    /// <summary>A neighbour that is not in a document dock has no strip to split; the caller's document
    /// is left exactly where it was rather than being pulled out of one and dropped nowhere.</summary>
    [Fact]
    public void WithNoHostStrip_NothingMoves()
    {
        var (f, _, host, _, tech) = Shell();
        var orphan = new StubDocument("orphan", StubDocument.StubKind.Welcome);

        Assert.False(f.SplitDocumentRightOf(tech, orphan, 0.2));
        Assert.Same(host, tech.Owner);
    }

    /// <summary>The real shell, with a layout and a technology open as two tabs of the one strip —
    /// which is exactly where Edit… finds them before it splits.</summary>
    private static (CircuitRfDockFactory Factory, IRootDock Root, IDocumentDock Host,
                    IDockable Layout, IDockable Tech) Shell()
    {
        var f = new CircuitRfDockFactory();

        var root = f.CreateLayout();
        f.InitLayout(root);               // sets every Owner — without it there is no tree to split

        var host = f.DocumentDock!;
        f.RemoveWelcomeStub();

        var layout = new StubDocument("board", StubDocument.StubKind.Welcome);
        var tech   = new StubDocument("pcb", StubDocument.StubKind.Welcome);
        f.OpenDocument(layout);
        f.OpenDocument(tech);

        return (f, root, host, layout, tech);
    }
}
