using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  A SPLIT document area survives a workspace close and reopen.
//
//  Owner-reported (2026-07-30): a .ctech docked side-by-side with a schematic came back as an
//  ordinary tab. The cause was structural, not a wiring slip: CwsDockLayout described docked
//  documents only as a flat DocumentOrder list plus an ActiveDocument, which can describe exactly one
//  tab strip — and CircuitRfDockFactory.BuildLayout created exactly one DocumentDock in a fixed slot.
//  Dragging a document to the edge of the document area splits it into two IDocumentDocks, an
//  arrangement neither of those could express. It restored as a tab because a tab was the only thing
//  the schema could say.
//
//  CwsDocumentRegion is the fix: a recursive pane tree, written ONLY when the area is genuinely
//  split, so an unsplit workspace's block stays byte-identical to before and the new code path cannot
//  regress the ordinary layout.
//
//  These run headlessly for the same reason the rest of DockLayoutPersistenceTests does: the Dock
//  MVVM model types are plain C#. InitLayout is deliberately not called (it presents real windows).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class SplitDocumentAreaLayoutTests
{
    // ── Capture ───────────────────────────────────────────────────────────────

    [Fact]
    public void ASplitDocumentArea_IsCaptured_AsTwoPanesWithTheirOwnDocuments()
    {
        var (root, keys) = SplitShell();

        var region = DockLayoutCapture.CaptureDocumentRegion(root, d => keys.GetValueOrDefault(d));

        Assert.NotNull(region);
        Assert.Equal("Horizontal", region!.Orientation);
        Assert.Equal(2, region.Children.Count);

        // Each document ends up in its own pane, and — the part that broke in the first attempt —
        // the tech pane is found even though Dock wrapped it in a ProportionalDock, not a DocumentDock.
        var panes = region.Children.Select(c => string.Join(",", c.Documents)).ToList();
        Assert.Equal(new[] { "Amp/schematic/Amp.csch", "tech/pcb.ctech" }, panes.OrderBy(x => x).ToArray());

        Assert.Single(DockLayoutCapture.EnumerateDocumentDocks(root));
    }

    [Fact]
    public void AnUnsplitDocumentArea_CapturesNoRegion_SoOrdinaryLayoutsAreUnchanged()
    {
        var f   = new CircuitRfDockFactory();
        var doc = new StubDocument("Amp", StubDocument.StubKind.Welcome);
        var dock = new DocumentDock { VisibleDockables = f.CreateList<IDockable>(doc), ActiveDockable = doc };
        var root = new RootDock { VisibleDockables = f.CreateList<IDockable>(dock), ActiveDockable = dock };

        // One strip is exactly what DocumentOrder already describes. A second description of the same
        // thing is two records that can disagree.
        Assert.Null(DockLayoutCapture.CaptureDocumentRegion(root, _ => "Amp/schematic/Amp.csch"));
    }

    [Fact]
    public void TheDefaultShell_WritesNoDocumentRegion()
    {
        var f = new CircuitRfDockFactory();
        var captured = DockLayoutCapture.Capture(f.CreateLayout(), [], documentKey: _ => "x");

        Assert.Null(captured.DocumentRegion);
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    [Fact]
    public void ARegion_RoundTripsThroughTheCwsBlock()
    {
        var layout = new CwsDockLayout
        {
            DocumentRegion = new CwsDocumentRegion
            {
                Orientation = "Horizontal",
                Children =
                {
                    new CwsDocumentRegion { Documents = { "Amp/schematic/Amp.csch" }, Proportion = 0.6 },
                    new CwsDocumentRegion { Documents = { "tech/pcb.ctech" }, Active = "tech/pcb.ctech" },
                },
            },
        };

        var read = DockLayoutSerialization.TryRead(DockLayoutSerialization.Write(layout)).Layout;

        Assert.NotNull(read?.DocumentRegion);
        Assert.Equal("Horizontal", read!.DocumentRegion!.Orientation);
        Assert.Equal(2, read.DocumentRegion.Children.Count);
        Assert.Equal(0.6, read.DocumentRegion.Children[0].Proportion, 6);
        Assert.Equal("tech/pcb.ctech", read.DocumentRegion.Children[1].Active);
    }

    [Fact]
    public void ARegionThatCannotMeanAnything_DegradesToNoSplit_NeverThrows()
    {
        // R-dock-5: a layout problem must never fail the workspace open. Empty panes and a split with
        // nothing in it are the two shapes a hand-edited file most easily produces.
        var layout = new CwsDockLayout
        {
            DocumentRegion = new CwsDocumentRegion
            {
                Orientation = "Horizontal",
                Children =
                {
                    new CwsDocumentRegion(),                                   // leaf, no documents
                    new CwsDocumentRegion { Documents = { "   ", "" } },       // leaf, blank keys
                },
            },
        };

        Assert.Null(DockLayoutSerialization.TryRead(DockLayoutSerialization.Write(layout)).Layout!.DocumentRegion);
    }

    [Fact]
    public void ASplitWithOneSurvivingPane_CollapsesToThatPane()
    {
        var layout = new CwsDockLayout
        {
            DocumentRegion = new CwsDocumentRegion
            {
                Orientation = "Horizontal",
                Children =
                {
                    new CwsDocumentRegion(),                                     // dropped
                    new CwsDocumentRegion { Documents = { "tech/pcb.ctech" } },   // survives
                },
            },
        };

        var read = DockLayoutSerialization.TryRead(DockLayoutSerialization.Write(layout)).Layout!.DocumentRegion;

        Assert.NotNull(read);
        Assert.True(read!.IsLeaf, "a split with one child is that child — the extra level means nothing");
        Assert.Equal(new[] { "tech/pcb.ctech" }, read.Documents);
    }

    // ── Rebuild ───────────────────────────────────────────────────────────────

    [Fact]
    public void ASavedSplit_RebuildsTwoDocumentDocks_SideBySide()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var root = f.CreateLayoutFromState(
            StateWithSplit(), floatingGeometry: null, documentIsOpen: _ => true);

        var docks = DockLayoutCapture.EnumerateDocumentDocks(root).ToList();
        Assert.Equal(2, docks.Count);

        // Both panes must share one horizontal proportional parent — that IS "side by side".
        var split = FindSplitContaining(root, docks[0], docks[1]);
        Assert.NotNull(split);
        Assert.Equal(Orientation.Horizontal, split!.Orientation);

        Assert.Equal(2, f.RestoredDocumentPanes.Count);
        Assert.Equal(new[] { "tech/pcb.ctech" }, f.RestoredDocumentPanes[1].Documents);
    }

    [Fact]
    public void ThePreservedDock_IsPaneZero_SoReopenedTabsAreNotStranded()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();
        var preserved = f.DocumentDock;
        Assert.NotNull(preserved);

        var root = f.CreateLayoutFromState(
            StateWithSplit(), floatingGeometry: null, documentIsOpen: _ => true);

        // Every reopened document lands in the preserved dock before the layout is rebuilt. If the
        // builder handed pane 0 to a fresh empty dock instead, every one of those tabs would vanish
        // from the visible tree.
        Assert.Same(preserved, f.RestoredDocumentPanes[0].Dock);
        Assert.Same(preserved, f.DocumentDock);
        Assert.Contains(preserved!, DockLayoutCapture.EnumerateDocumentDocks(root));
    }

    [Fact]
    public void APaneWhoseDocumentsAreAllClosed_IsNotBuiltAsAnEmptyPane()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        // R-dock-2: the open list decides membership. A pane built for documents that no longer exist
        // would be a permanently blank half-window with no way to dismiss it short of Reset Layout.
        var root = f.CreateLayoutFromState(
            StateWithSplit(), floatingGeometry: null,
            documentIsOpen: key => key == "Amp/schematic/Amp.csch");

        Assert.Single(DockLayoutCapture.EnumerateDocumentDocks(root));
        Assert.Empty(f.RestoredDocumentPanes);
    }

    [Fact]
    public void ALayoutWithNoRegion_BuildsExactlyOneDocumentDock_AsBefore()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var root = f.CreateLayoutFromState(new CwsDockLayout(), floatingGeometry: null, documentIsOpen: _ => true);

        Assert.Single(DockLayoutCapture.EnumerateDocumentDocks(root));
        Assert.Empty(f.RestoredDocumentPanes);
    }

    [Fact]
    public void CaptureThenRebuildThenCapture_ReproducesTheSameSplit()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var rebuilt = f.CreateLayoutFromState(
            StateWithSplit(), floatingGeometry: null, documentIsOpen: _ => true);

        // The rebuilt panes are empty until the workspace moves documents in, so re-capturing keys off
        // the panes' recorded contents rather than their (not yet populated) tabs. What this pins is
        // the SHAPE: two panes, horizontal, in that order.
        var docks = DockLayoutCapture.EnumerateDocumentDocks(rebuilt).ToList();
        Assert.Equal(2, docks.Count);
        Assert.Same(docks[0], f.RestoredDocumentPanes[0].Dock);
        Assert.Same(docks[1], f.RestoredDocumentPanes[1].Dock);
    }

    [Fact]
    public void APaneDockedAgainstTheOuterEdge_IsCaptured_EvenThoughAToolDockSitsInsideTheSplit()
    {
        // Reconstructed from a real failing .cws (New_Best_PCB_Design_Rev9): DocumentRegion was null
        // while DocumentOrder listed 2 of the 3 open documents, and Messages was recorded Side=Bottom
        // — which together pin the tree below. Dropping against the OUTER edge splits the whole
        // document column, so the region legitimately contains the Messages tool dock. Both earlier
        // attempts ascended from a document dock and stopped at the first tool dock, so they never got
        // past DocumentColumn, saw one pane, and wrote nothing.
        var f = new CircuitRfDockFactory();

        var schematic = new StubDocument("PCB_Rev12", StubDocument.StubKind.Welcome);
        var layout    = new StubDocument("PCB_Rev12_lay", StubDocument.StubKind.Welcome);
        var tech      = new StubDocument("pcb", StubDocument.StubKind.Welcome);

        var documents = new DocumentDock
        {
            VisibleDockables = f.CreateList<IDockable>(schematic, layout),
            ActiveDockable   = layout,
        };
        var messages = new ToolDock { VisibleDockables = f.CreateList<IDockable>(new MessagesTool()) };

        var documentColumn = new ProportionalDock
        {
            Orientation      = Orientation.Vertical,
            VisibleDockables = f.CreateList<IDockable>(documents, new ProportionalDockSplitter(), messages),
            ActiveDockable   = documents,
        };

        // The pane Dock builds for a dragged document: a plain ProportionalDock, not a DocumentDock.
        var techPane = new ProportionalDock
        {
            VisibleDockables = f.CreateList<IDockable>(tech),
            ActiveDockable   = tech,
        };

        var split = new ProportionalDock
        {
            Orientation      = Orientation.Horizontal,
            VisibleDockables = f.CreateList<IDockable>(documentColumn, new ProportionalDockSplitter(), techPane),
            ActiveDockable   = documentColumn,
        };

        var leftColumn = new ProportionalDock
        {
            Orientation      = Orientation.Vertical,
            VisibleDockables = f.CreateList<IDockable>(
                new ToolDock { VisibleDockables = f.CreateList<IDockable>(new ProjectTreeTool()) }),
        };

        var outer = new ProportionalDock
        {
            Orientation      = Orientation.Horizontal,
            VisibleDockables = f.CreateList<IDockable>(leftColumn, new ProportionalDockSplitter(), split),
            ActiveDockable   = split,
        };

        var root = new RootDock { VisibleDockables = f.CreateList<IDockable>(outer), ActiveDockable = outer };

        var keys = new Dictionary<IDockable, string>
        {
            [schematic] = "PCB_Rev12/schematic/PCB_Rev12.csch",
            [layout]    = "PCB_Rev12/layout/PCB_Rev12.clay",
            [tech]      = "tech/pcb-2layer_RO4350B_20mil_1oz.ctech",
        };

        var region = DockLayoutCapture.CaptureDocumentRegion(root, d => keys.GetValueOrDefault(d));

        Assert.NotNull(region);
        Assert.Equal("Horizontal", region!.Orientation);
        Assert.Equal(2, region.Children.Count);

        // The tool column and Messages prune themselves — a Tool has no document key — and the
        // single-child levels (Root, Outer, DocumentColumn) collapse away.
        var panes = region.Children.Select(c => string.Join(",", c.Documents)).ToList();
        Assert.Contains("PCB_Rev12/schematic/PCB_Rev12.csch,PCB_Rev12/layout/PCB_Rev12.clay", panes);
        Assert.Contains("tech/pcb-2layer_RO4350B_20mil_1oz.ctech", panes);
    }

    [Fact]
    public void RealSplit_ThroughCaptureSerializeAndRebuild_ComesBackAsTwoPanes()
    {
        // THE test. The first version of this feature passed capture tests and rebuild tests
        // separately — each against a hand-built fixture — and did nothing whatsoever in the app,
        // because the two halves were never joined and the capture fixture was the wrong shape.
        // This drives the library's own split, then the real capture → serialize → read → rebuild
        // chain, which is what actually has to work.
        var (root, keys) = SplitShell();

        var captured = DockLayoutCapture.Capture(root, [], documentKey: d => keys.GetValueOrDefault(d));
        Assert.NotNull(captured.DocumentRegion);

        var reread = DockLayoutSerialization.TryRead(DockLayoutSerialization.Write(captured)).Layout;
        Assert.NotNull(reread?.DocumentRegion);

        var f = new CircuitRfDockFactory();
        f.CreateLayout();
        var rebuilt = f.CreateLayoutFromState(reread!, floatingGeometry: null, documentIsOpen: _ => true);

        Assert.Equal(2, f.RestoredDocumentPanes.Count);
        Assert.Equal(
            new[] { "Amp/schematic/Amp.csch", "tech/pcb.ctech" },
            f.RestoredDocumentPanes.SelectMany(p => p.Documents).OrderBy(x => x).ToArray());

        var docks = DockLayoutCapture.EnumerateDocumentDocks(rebuilt).ToList();
        Assert.Equal(2, docks.Count);
        Assert.Equal(Orientation.Horizontal, FindSplitContaining(rebuilt, docks[0], docks[1])!.Orientation);
    }

    [Fact]
    public void CapturingASplit_ProducesNoNaNProportion_SoTheBlockCanBeWritten()
    {
        // Dock sets Proportion = NaN for "unset", NaN fails every range comparison silently, and
        // System.Text.Json throws on NaN — which would lose the entire layout block behind a generic
        // "window layout was not saved" warning rather than anything pointing here.
        var (root, keys) = SplitShell();
        var captured = DockLayoutCapture.Capture(root, [], documentKey: d => keys.GetValueOrDefault(d));

        Assert.NotNull(DockLayoutSerialization.Write(captured));

        static void AssertFinite(CwsDocumentRegion n)
        {
            Assert.True(double.IsFinite(n.Proportion), "a NaN proportion would break the .cws write");
            foreach (var c in n.Children) AssertFinite(c);
        }
        AssertFinite(captured.DocumentRegion!);
    }

    [Fact]
    public void ClosingTheLastDocumentInAPane_RemovesThePane_NotJustItsContent()
    {
        // Owner-reported: closing the .ctech left a dead "No documents open" region that could not be
        // dismissed. Dock's CollapseDock returns immediately on !IsCollapsable, and the pane had been
        // built with IsCollapsable = false, copied from the primary dock — where it is correct,
        // because the main document area must never vanish.
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var root = f.CreateLayoutFromState(
            StateWithSplit(), floatingGeometry: null, documentIsOpen: _ => true);
        f.InitLayout(root);

        var pane = f.RestoredDocumentPanes[1].Dock;
        var doc  = new StubDocument("pcb", StubDocument.StubKind.Welcome);
        f.AddDockable(pane, doc);

        Assert.Equal(2, DockLayoutCapture.EnumerateDocumentDocks(root).Count());

        f.RemoveDockable(doc, collapse: true);

        Assert.Single(DockLayoutCapture.EnumerateDocumentDocks(root));
        Assert.DoesNotContain(pane, DockLayoutCapture.EnumerateDocumentDocks(root));
    }

    [Fact]
    public void ThePrimaryDock_StaysNonCollapsable_SoTheMainAreaCanNeverVanish()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        f.CreateLayoutFromState(StateWithSplit(), floatingGeometry: null, documentIsOpen: _ => true);

        // The two flags differ on purpose; a future "make them consistent" tidy-up would resurrect
        // one bug or the other.
        Assert.False(f.RestoredDocumentPanes[0].Dock.IsCollapsable, "the main document area must never collapse away");
        Assert.True(f.RestoredDocumentPanes[1].Dock.IsCollapsable,  "an extra pane must give its space back when emptied");
    }

    [Fact]
    public void EveryLayoutField_SurvivesWithMissingPanelsFilled()
    {
        // WithMissingPanelsFilled builds a NEW CwsDockLayout by hand-copying each field, so a field
        // added to the schema and forgotten there is silently discarded on every single restore, with
        // nothing to notice. That is exactly how DocumentRegion was lost while this feature was being
        // written — the split was captured and serialized correctly, then dropped on the way into the
        // builder. Reflection makes the next omission fail here instead of in a bug report.
        var source = new CwsDockLayout
        {
            Version                 = CwsDockLayout.CurrentVersion,
            Screens                 = { new CwsScreen { Width = 1920, Height = 1080 } },
            Panels                  = { new CwsDockPanel { Id = DockPanelIds.Messages, Side = DockSide.Bottom } },
            Sides                   = { new CwsDockSide { Side = DockSide.Left, Proportion = 0.25 } },
            FloatingWindows         = { new CwsFloatingWindow { Panels = { DockPanelIds.Properties } } },
            FloatingDocumentWindows = { new CwsFloatingDocumentWindow { Documents = { "a.csch" } } },
            DocumentOrder           = { "a.csch" },
            ActiveDocument          = "a.csch",
            DocumentRegion          = new CwsDocumentRegion { Documents = { "a.csch" } },
        };

        var merged = DockLayoutDefaults.WithMissingPanelsFilled(source);

        foreach (var prop in typeof(CwsDockLayout).GetProperties())
        {
            if (!prop.CanWrite) continue;

            var value = prop.GetValue(merged);

            // Panels/Sides are deliberately ADDED to (missing defaults filled in), so they are checked
            // for survival of the original entries rather than for equality.
            if (prop.Name is nameof(CwsDockLayout.Panels))
                Assert.Contains(((List<CwsDockPanel>)value!), p => p.Id == DockPanelIds.Messages);
            else if (prop.Name is nameof(CwsDockLayout.Sides))
                Assert.Contains(((List<CwsDockSide>)value!), s => s.Side == DockSide.Left);
            else
                Assert.True(
                    value is not null && !IsEmptyCollection(value),
                    $"CwsDockLayout.{prop.Name} was dropped by WithMissingPanelsFilled — add it to the copy");
        }

        static bool IsEmptyCollection(object v) =>
            v is System.Collections.ICollection { Count: 0 };
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A shell whose document area is split by the LIBRARY'S OWN operation, not by hand.
    ///
    /// <para>This is the correction that mattered. A hand-built "two DocumentDocks in a
    /// ProportionalDock" tree looks obviously right and is WRONG:
    /// <c>FactoryBase.CreateSplitLayout</c>'s non-<c>IDock</c> branch wraps a dragged document in a
    /// plain <c>CreateProportionalDock()</c>, so the second pane is NOT an <c>IDocumentDock</c>. The
    /// first version of this feature passed a full suite against the hand-built shape and did nothing
    /// at all in the app. Driving <c>SplitToDock</c> is what makes these tests about Dock's behaviour
    /// rather than about my model of it.</para>
    /// </summary>
    private static (IRootDock Root, Dictionary<IDockable, string> Keys) SplitShell()
    {
        var f = new CircuitRfDockFactory();

        var schematic = new StubDocument("Amp", StubDocument.StubKind.Welcome);
        var tech      = new StubDocument("pcb", StubDocument.StubKind.Welcome);

        var documents = new DocumentDock
        {
            Id               = "Documents",
            VisibleDockables = f.CreateList<IDockable>(schematic, tech),
            ActiveDockable   = schematic,
        };

        // Mirrors BuildLayout: the document area sits in a vertical column above a tool dock, which is
        // what stops the region walk from ascending past the document area.
        var messages = new ToolDock { VisibleDockables = f.CreateList<IDockable>(new MessagesTool()) };
        var column   = new ProportionalDock
        {
            Orientation      = Orientation.Vertical,
            VisibleDockables = f.CreateList<IDockable>(documents, new ProportionalDockSplitter(), messages),
            ActiveDockable   = documents,
        };

        var root = f.CreateRootDock();
        root.VisibleDockables = f.CreateList<IDockable>(column);
        root.ActiveDockable   = column;
        f.InitLayout(root);

        // The real gesture: drag the tech document out of the strip and onto the right edge of the
        // document area. The removal matters — SplitToDock only WRAPS; it is the drag manager that
        // takes the dockable out of its source first. Without it the document ends up in both places.
        f.RemoveDockable(tech, collapse: false);
        f.SplitToDock(documents, tech, DockOperation.Right);

        return (root, new Dictionary<IDockable, string>
        {
            [schematic] = "Amp/schematic/Amp.csch",
            [tech]      = "tech/pcb.ctech",
        });
    }

    private static CwsDockLayout StateWithSplit() => new()
    {
        DocumentRegion = new CwsDocumentRegion
        {
            Orientation = "Horizontal",
            Children =
            {
                new CwsDocumentRegion { Documents = { "Amp/schematic/Amp.csch" } },
                new CwsDocumentRegion { Documents = { "tech/pcb.ctech" }, Active = "tech/pcb.ctech" },
            },
        },
    };

    private static IProportionalDock? FindSplitContaining(IDockable node, IDockable a, IDockable b)
    {
        if (node is not IDock dock || dock.VisibleDockables is not { } children) return null;

        if (node is IProportionalDock pd && children.Contains(a) && children.Contains(b)) return pd;

        foreach (var child in children)
            if (child is not null && FindSplitContaining(child, a, b) is { } found) return found;

        return null;
    }
}
