using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Window ▸ Tile. The placement rules (<see cref="DocumentTiling"/>) and the live-tree operation
/// that carries them out (<see cref="CircuitRfDockFactory.ArrangeDocuments"/>).
/// </summary>
public sealed class DocumentTilingTests
{
    private static StubDocument Doc(string name) => new(name, StubDocument.StubKind.Welcome);

    private static TileCandidate C(IDockable d, TileRole role, string? cell, bool visible = false)
        => new(d, role, cell, visible);

    private static string[][] Shape(TilePlan plan) =>
        plan.Lines.SelectMany(l => l).Select(p => p.Select(d => d.Title!).ToArray()).ToArray();

    /// <summary>The motivating case: edit on the left, read the results on the right — whatever order
    /// the tabs were in.</summary>
    [Fact]
    public void SideBySide_PutsTheSchematicLeft_AndItsDisplayRight()
    {
        var dd = Doc("dd"); var sch = Doc("sch");
        var plan = DocumentTiling.Plan([C(dd, TileRole.DataDisplay, "Amp"), C(sch, TileRole.Schematic, "Amp")],
                                       TileArrangement.SideBySide)!;

        Assert.Equal(Orientation.Horizontal, plan.Outer);
        Assert.Equal([["sch"], ["dd"]], Shape(plan));
    }

    /// <summary>Four documents of two cells make a 2×2 whose ROWS are cells: each schematic beside
    /// the display that plots it.</summary>
    [Fact]
    public void Grid_OfTwoCells_PutsEachCellOnItsOwnRow()
    {
        var ddB = Doc("ddB"); var schA = Doc("schA"); var ddA = Doc("ddA"); var schB = Doc("schB");
        var plan = DocumentTiling.Plan(
        [
            C(ddB, TileRole.DataDisplay, "B"), C(schA, TileRole.Schematic, "A"),
            C(ddA, TileRole.DataDisplay, "A"), C(schB, TileRole.Schematic, "B"),
        ], TileArrangement.Grid)!;

        // Cell B's first tab came first, so B is the top row.
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal([["schB"], ["schA"], ["ddB"], ["ddA"]], Shape(plan));
    }

    /// <summary>Three in a grid: schematic over layout on the left, the 3D view full height right.</summary>
    [Fact]
    public void Grid_OfThree_StacksTheFirstColumn()
    {
        var c3d = Doc("c3d"); var lay = Doc("lay"); var sch = Doc("sch");
        var plan = DocumentTiling.Plan(
            [C(c3d, TileRole.ThreeD, "A"), C(lay, TileRole.Layout, "A"), C(sch, TileRole.Schematic, "A")],
            TileArrangement.Grid)!;

        Assert.Equal([2, 1], plan.Lines.Select(l => l.Count));
        Assert.Equal([["sch"], ["lay"], ["c3d"]], Shape(plan));
    }

    /// <summary>Past the pane limit, documents of the same kind share a pane as tabs, and the focused
    /// one stays in front.</summary>
    [Fact]
    public void PastThePaneLimit_SameKindsShareAPane()
    {
        var s1 = Doc("s1"); var s2 = Doc("s2"); var s3 = Doc("s3"); var d1 = Doc("d1"); var l1 = Doc("l1");
        var plan = DocumentTiling.Plan(
        [
            C(s1, TileRole.Schematic, "A"), C(s2, TileRole.Schematic, "B"), C(s3, TileRole.Schematic, "C"),
            C(l1, TileRole.Layout, "A"), C(d1, TileRole.DataDisplay, "A"),
        ], TileArrangement.SideBySide, focus: s2)!;

        Assert.Equal([["s1", "s2", "s3"], ["l1"], ["d1"]], Shape(plan));
        Assert.Same(s2, plan.Active[0]);
    }

    /// <summary>Tile, tile again a different way, merge — on the real shell. Every document stays
    /// docked, the primary strip stays the first pane, the tool panels are untouched, a second tile
    /// replaces the first instead of nesting inside it, and the result is captured like any split.</summary>
    [Fact]
    public void TileRetileMerge_OnTheLiveShell()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        f.InitLayout(root);
        var primary = f.DocumentDock!;
        f.RemoveWelcomeStub();

        var docs = new[] { Doc("a"), Doc("b"), Doc("c"), Doc("d") };
        foreach (var d in docs) f.OpenDocument(d);
        int tools = CountTools(root);

        TileCandidate[] Candidates() =>
            DockLayoutCapture.EnumerateDocumentPanes(root)
                .SelectMany(p => p.VisibleDockables!.Select(d => C(d, TileRole.Other, null)))
                .ToArray();

        Assert.True(f.ArrangeDocuments(root, DocumentTiling.Plan(Candidates(), TileArrangement.Grid)!));
        Assert.Equal(4, DockLayoutCapture.EnumerateDocumentPanes(root).Count());
        Assert.Same(primary, docs[0].Owner);
        int depth = Depth(root, primary);

        var keys = docs.ToDictionary(d => (IDockable)d, d => d.Title!);
        var region = DockLayoutCapture.CaptureDocumentRegion(root, d => keys.GetValueOrDefault(d));
        Assert.Equal(2, region!.Children.Count);
        Assert.All(region.Children, col => Assert.Equal("Vertical", col.Orientation));

        Assert.True(f.ArrangeDocuments(root, DocumentTiling.Plan(Candidates(), TileArrangement.SideBySide)!));
        var panes = DockLayoutCapture.EnumerateDocumentPanes(root).ToList();
        Assert.Equal(3, panes.Count);
        Assert.True(Depth(root, primary) <= depth);

        // Equal thirds in BOTH share fields. The primary strip arrives with the CollapsedProportion it
        // had in the document column (0.8), Dock's panel restores from it, and the panes rendered
        // 55/23/23 while every Proportion read 1/3.
        Assert.All(panes, p => Assert.Equal(1.0 / 3, p.Proportion, 6));
        Assert.All(panes, p => Assert.Equal(1.0 / 3, p.CollapsedProportion, 6));

        // A pane holding two documents shows them in planned order (a move alone reversed them).
        Assert.Equal(["c", "d"], panes[2].VisibleDockables!.Select(d => d.Title));

        Assert.True(f.ArrangeDocuments(root, DocumentTiling.Plan(Candidates(), TileArrangement.MergeAll)!));
        Assert.Equal([primary], DockLayoutCapture.EnumerateDocumentPanes(root));
        Assert.Equal(4, primary.VisibleDockables!.Count);
        Assert.Null(DockLayoutCapture.CaptureDocumentRegion(root, d => keys.GetValueOrDefault(d)));
        Assert.Equal(tools, CountTools(root));
    }

    /// <summary>Merge All Documents is the clean-up for a scattered arrangement: a torn-off document
    /// comes home as a tab of the main strip, and the window it leaves empty is closed.</summary>
    [Fact]
    public void MergeAll_BringsAFloatingDocumentHome_AndClosesItsWindow()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        f.InitLayout(root);
        var primary = f.DocumentDock!;
        f.RemoveWelcomeStub();

        var docked = Doc("docked");
        f.OpenDocument(docked);

        var floated = Doc("floated");
        var window = f.CreateWindowFrom(floated)!;
        root.Windows ??= f.CreateList<IDockWindow>();
        root.Windows.Add(window);
        f.InitDockWindow(window, root, hostWindow: null);
        Assert.NotSame(primary, floated.Owner);

        var plan = DocumentTiling.Plan([C(docked, TileRole.Other, null), C(floated, TileRole.Other, null)],
                                       TileArrangement.MergeAll)!;
        Assert.True(f.ArrangeDocuments(root, plan));

        Assert.Equal(["docked", "floated"], primary.VisibleDockables!.Select(d => d.Title));
        Assert.Empty(root.Windows);
    }

    private static int CountTools(IDockable node) => node switch
    {
        ITool => 1,
        IDock { VisibleDockables: { } kids } => kids.Where(k => k is not null).Sum(k => CountTools(k!)),
        _ => 0,
    };

    private static int Depth(IDockable node, IDockable target, int at = 0)
    {
        if (ReferenceEquals(node, target)) return at;
        if (node is IDock { VisibleDockables: { } kids })
            foreach (var k in kids)
                if (k is not null && Depth(k, target, at + 1) is >= 0 and var d) return d;
        return -1;
    }
}
