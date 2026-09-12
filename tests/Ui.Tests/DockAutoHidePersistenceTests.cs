using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Auto-hide survives saving and reopening a workspace</b> (owner, 2026-09-11: auto-hiding the
/// Project Tree and saving the workspace lost the panel completely on reopen).
///
/// <para>The bug was one fact with two consequences. A panel Dock has auto-hidden is not in the dock
/// TREE — it hangs off the root's own <c>LeftPinnedDockables</c> and its three siblings — so
/// <c>DockLayoutCapture</c>'s walk never saw it, and the "a panel of the default layout we did not find
/// must be closed" fallback at the end of that walk wrote it into the <c>.cws</c> as <c>Open = false</c>.
/// A closed panel is one the builder places nowhere, so the panel did not come back at all — not as a
/// strip, not docked, not anywhere.</para>
///
/// <para>These run against the REAL factory and the REAL <c>Factory.PinDockable</c> — the auto-hide the
/// user's own click performs — because the whole risk here is a mismatch between what Dock does to its
/// model and what this codebase believes it does. A test that auto-hid a panel by writing to the pinned
/// list itself would agree with the restore by construction and prove nothing about the gesture.</para>
/// </summary>
public sealed class DockAutoHidePersistenceTests
{
    private static (CircuitRfDockFactory Factory, IRootDock Root) NewShell()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        // Owners and back-references, which PinDockable needs to find the root from a tool. Safe
        // headlessly here because this layout has no floating windows for ShowWindows to present.
        f.InitLayout(root);
        return (f, root);
    }

    private static CwsDockLayout Capture(IRootDock root) => DockLayoutCapture.Capture(root, []);

    private static CwsDockLayout RoundTripThroughJson(CwsDockLayout layout)
    {
        var read = DockLayoutSerialization.TryRead(DockLayoutSerialization.Write(layout));
        Assert.Null(read.Report);
        Assert.NotNull(read.Layout);
        return read.Layout!;
    }

    private static CwsDockPanel Panel(CwsDockLayout l, string id) => Assert.Single(l.Panels, p => p.Id == id);

    private static IRootDock Reopen(CwsDockLayout saved)
    {
        // A different factory, as a different session is: nothing carries over but the file.
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayoutFromState(saved);
        f.InitLayout(root);
        return root;
    }

    // ── The reported bug ──────────────────────────────────────────────────────

    /// <summary>
    /// Auto-hide the Project Tree, save, reopen: it comes back auto-hidden on the left — and, above all,
    /// it comes back.
    /// </summary>
    [Fact]
    public void AutoHiddenProjectTree_SurvivesSaveAndReopen()
    {
        var (factory, root) = NewShell();
        var tree = factory.ProjectTreeTool!;

        factory.PinDockable(tree);
        Assert.True(factory.IsDockablePinned(tree, root));   // the gesture did what the test assumes

        var saved = RoundTripThroughJson(Capture(root));

        // What the .cws says: open, and auto-hidden. NOT closed — that was the bug.
        var entry = Panel(saved, DockPanelIds.ProjectTree);
        Assert.True(entry.Open);
        Assert.True(entry.AutoHidden);
        Assert.Equal(DockSide.Left, entry.Side);

        // What the reopened window has: a strip on the left holding the Project Tree.
        var reopened = Reopen(saved);
        Assert.Contains(reopened.LeftPinnedDockables ?? [], d => ReferenceEquals(d, ((CircuitRfDockFactory)tree.Factory!).ProjectTreeTool)
                                                             || (d as ITool)?.Id == DockPanelIds.ProjectTree);
        Assert.Equal(DockSide.Left, Panel(Capture(reopened), DockPanelIds.ProjectTree).Side);
        Assert.True(Panel(Capture(reopened), DockPanelIds.ProjectTree).AutoHidden);
    }

    /// <summary>
    /// The panel's own instance is auto-hidden in the reopened shell, and Dock agrees — this is the
    /// assertion that would fail if the restore put the tool in a pinned list without the rest of the
    /// state Dock reads back out of one.
    /// </summary>
    [Fact]
    public void ReopenedAutoHiddenPanel_IsPinnedAccordingToDockItself()
    {
        var (factory, root) = NewShell();
        factory.PinDockable(factory.PropertiesTool!);

        var saved = RoundTripThroughJson(Capture(root));

        var next = new CircuitRfDockFactory();
        var reopened = next.CreateLayoutFromState(saved);
        next.InitLayout(reopened);

        Assert.True(next.IsDockablePinned(next.PropertiesTool!, reopened));
        Assert.True(next.IsToolAutoHidden(next.PropertiesTool!));
        Assert.False(next.IsAutoHiddenToolShowing(next.PropertiesTool!));   // a strip, not flown out
    }

    // ── The home it goes back to ──────────────────────────────────────────────

    /// <summary>
    /// The dock the panel was auto-hidden FROM is rebuilt — empty — so the panel has a group, a width
    /// and somewhere to return to. That is what Dock itself leaves behind when the last tool of a dock is
    /// auto-hidden, and rebuilding anything else would make un-hiding land the panel somewhere new.
    /// </summary>
    [Fact]
    public void HomeDock_IsRebuiltEvenWhenEveryPanelInItIsAutoHidden()
    {
        var (factory, root) = NewShell();

        // The default layout's Messages+DRC group along the bottom — auto-hide BOTH, so nothing visible
        // is left to keep the dock alive.
        factory.PinDockable(factory.MessagesTool!);
        factory.PinDockable(factory.DrcTool!);

        var saved = RoundTripThroughJson(Capture(root));
        Assert.True(Panel(saved, DockPanelIds.Messages).AutoHidden);
        Assert.True(Panel(saved, DockPanelIds.Drc).AutoHidden);

        var next = new CircuitRfDockFactory();
        var reopened = next.CreateLayoutFromState(saved);
        next.InitLayout(reopened);

        // Both strips are there…
        Assert.Equal(2, (reopened.BottomPinnedDockables ?? []).Count);

        // …and un-hiding one puts it back in a tool dock that is in the tree, not in a new column
        // somewhere Dock invented.
        next.PinDockable(next.MessagesTool!);
        Assert.False(next.IsDockablePinned(next.MessagesTool!, reopened));
        Assert.True(DockLayoutCapture.Contains(reopened, next.MessagesTool!));
        Assert.Equal(DockSide.Bottom, Panel(Capture(reopened), DockPanelIds.Messages).Side);
    }

    /// <summary>
    /// A panel auto-hidden out of a TABBED group leaves its co-tenant alone, and comes back to the same
    /// group rather than to a column of its own.
    /// </summary>
    [Fact]
    public void AutoHidingOneTabOfAGroup_LeavesTheOtherDockedAndKeepsTheGroup()
    {
        var (factory, root) = NewShell();
        factory.PinDockable(factory.ProjectTreeTool!);       // tabbed with the Palette by default

        var saved = RoundTripThroughJson(Capture(root));

        var palette = Panel(saved, DockPanelIds.Palette);
        var tree    = Panel(saved, DockPanelIds.ProjectTree);

        Assert.False(palette.AutoHidden);
        Assert.True (palette.Active);                        // the group still has a tab in front
        Assert.Equal(palette.Group, tree.Group);             // same group…
        Assert.True (tree.Order > palette.Order);            // …appended after it, where un-hiding puts it
        Assert.False(tree.Active);                           // never the front tab of a strip it is not in

        var reopened = Reopen(saved);
        var captured = Capture(reopened);
        Assert.False(Panel(captured, DockPanelIds.Palette).AutoHidden);
        Assert.True (Panel(captured, DockPanelIds.ProjectTree).AutoHidden);
        Assert.Equal(Panel(captured, DockPanelIds.Palette).Group, Panel(captured, DockPanelIds.ProjectTree).Group);
    }

    /// <summary>
    /// <b>Capture → build → capture is stable.</b> The group and order of an auto-hidden panel are read
    /// off its home dock, and the two routes into that state record the home in different fields
    /// (<c>Owner</c> for the user's gesture, <c>OriginalOwner</c> for a restore). Reading only one of them
    /// would work on the first save and quietly fall back to the default placement on the second.
    /// </summary>
    [Fact]
    public void SecondSaveOfARestoredLayout_SaysTheSameThingAsTheFirst()
    {
        var (factory, root) = NewShell();
        factory.PinDockable(factory.AnalysesTool!);          // group 1 of the left column by default

        var first  = RoundTripThroughJson(Capture(root));
        var second = RoundTripThroughJson(Capture(Reopen(first)));
        var third  = RoundTripThroughJson(Capture(Reopen(second)));

        foreach (var id in new[] { DockPanelIds.Analyses, DockPanelIds.Properties, DockPanelIds.ProjectTree })
        {
            var a = Panel(first, id);
            var b = Panel(second, id);
            var c = Panel(third, id);
            Assert.Equal((a.Side, a.Group, a.Order, a.AutoHidden, a.Open), (b.Side, b.Group, b.Order, b.AutoHidden, b.Open));
            Assert.Equal((b.Side, b.Group, b.Order, b.AutoHidden, b.Open), (c.Side, c.Group, c.Order, c.AutoHidden, c.Open));
        }
    }

    // ── The rest of the app stops treating it as missing ──────────────────────

    /// <summary>
    /// Every side works, not just the left. The strip goes on the side the panel's own dock is aligned
    /// to, which is what Dock keys its un-hide off.
    /// </summary>
    [Theory]
    [InlineData(DockSide.Left)]
    [InlineData(DockSide.Right)]
    [InlineData(DockSide.Top)]
    [InlineData(DockSide.Bottom)]
    public void EverySide_RoundTrips(string side)
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayoutFromState(new CwsDockLayout
        {
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 1.0 },
                new CwsDockPanel { Id = DockPanelIds.Properties,  Side = side, Group = side == DockSide.Left ? 1 : 0, Order = 0, Active = true, Proportion = 0.4 },
            ],
        });
        f.InitLayout(root);

        f.PinDockable(f.PropertiesTool!);

        var saved = RoundTripThroughJson(Capture(root));
        Assert.True(Panel(saved, DockPanelIds.Properties).AutoHidden);
        Assert.Equal(side, Panel(saved, DockPanelIds.Properties).Side);

        var next = new CircuitRfDockFactory();
        var reopened = next.CreateLayoutFromState(saved);
        next.InitLayout(reopened);
        Assert.True(next.IsDockablePinned(next.PropertiesTool!, reopened));
        Assert.Equal(side, Panel(Capture(reopened), DockPanelIds.Properties).Side);
    }

    /// <summary>
    /// An auto-hidden panel is recorded ONCE. R-dock-1 makes the id the identity, and a second row for
    /// the same panel is a row some reader will act on instead of the right one.
    /// </summary>
    [Fact]
    public void AnAutoHiddenPanel_IsNamedExactlyOnce()
    {
        var (factory, root) = NewShell();
        factory.PinDockable(factory.ProjectTreeTool!);

        var captured = Capture(root);
        Assert.Single(captured.Panels, p => p.Id == DockPanelIds.ProjectTree);

        // …including while it is flown out, when Dock ALSO has it in root.PinnedDock.
        factory.PreviewPinnedDockable(factory.ProjectTreeTool!);
        Assert.Single(Capture(root).Panels, p => p.Id == DockPanelIds.ProjectTree);
        Assert.True(Panel(Capture(root), DockPanelIds.ProjectTree).AutoHidden);
    }

    /// <summary>
    /// Flying the panel out is not un-hiding it: the factory reports both facts separately, which is what
    /// lets a toolbar toggle read as a plain two-state control over a panel that stays auto-hidden.
    /// </summary>
    [Fact]
    public void FlyingOut_IsReportedSeparatelyFromBeingAutoHidden()
    {
        var (factory, _) = NewShell();
        var tool = factory.ProjectTreeTool!;

        factory.PinDockable(tool);
        Assert.True (factory.IsToolAutoHidden(tool));
        Assert.False(factory.IsAutoHiddenToolShowing(tool));

        factory.ToggleAutoHiddenTool(tool);
        Assert.True(factory.IsToolAutoHidden(tool));
        Assert.True(factory.IsAutoHiddenToolShowing(tool));

        factory.ToggleAutoHiddenTool(tool);
        Assert.True (factory.IsToolAutoHidden(tool));
        Assert.False(factory.IsAutoHiddenToolShowing(tool));
    }

    /// <summary>
    /// <c>TryFindTool</c> still says no — the panel is in no tree — which is exactly why the callers that
    /// used to rely on it alone had to learn about auto-hide. Pinned here so the next reader does not
    /// "fix" TryFindTool into finding it and quietly re-enable the float-a-second-copy path.
    /// </summary>
    [Fact]
    public void TryFindTool_DoesNotFindAnAutoHiddenPanel_SoTheFactoryIsAskedInstead()
    {
        var (factory, _) = NewShell();
        factory.PinDockable(factory.PaletteTool!);

        Assert.False(factory.TryFindTool(factory.PaletteTool!, out _, out _));
        Assert.True (factory.IsToolAutoHidden(factory.PaletteTool!));
    }

    // ── Reading a file ────────────────────────────────────────────────────────

    /// <summary>
    /// Auto-hidden is a kind of open. A closed entry claiming it is a contradiction, and the reader
    /// settles it rather than leaving the builder to.
    /// </summary>
    [Fact]
    public void AClosedEntryCannotAlsoBeAutoHidden()
    {
        var read = RoundTripThroughJson(new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.Messages, Open = false, AutoHidden = true, Active = true }],
        });

        Assert.False(Panel(read, DockPanelIds.Messages).AutoHidden);
    }

    /// <summary>
    /// A <c>.cws</c> written before auto-hide was persisted reads back exactly as it did — every panel
    /// docked, which is what those files meant.
    /// </summary>
    [Fact]
    public void ALayoutWrittenBeforeThisExisted_RestoresEveryPanelDocked()
    {
        var saved = RoundTripThroughJson(DockLayoutDefaults.Default());
        Assert.All(saved.Panels, p => Assert.False(p.AutoHidden));

        var reopened = Reopen(saved);
        Assert.Empty(reopened.LeftPinnedDockables ?? []);
        Assert.Empty(reopened.BottomPinnedDockables ?? []);
    }
}
