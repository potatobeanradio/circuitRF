using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// The one place that knows where Dock keeps an <b>auto-hidden</b> tool panel — the panel collapsed to
/// a labelled strip at the edge of the window, which flies out when its tab is clicked.
///
/// <h3>Why it needs a file of its own</h3>
/// <para>An auto-hidden panel is <b>not in the dock tree</b>. Dock (whose name for this is
/// <i>pinned</i>) takes the dockable out of its tool dock's <c>VisibleDockables</c> and puts it in one
/// of four lists hanging directly off the root — <c>LeftPinnedDockables</c> and its siblings. Every
/// walker in this codebase descends <c>VisibleDockables</c>, so all of them are blind to it: the
/// layout capture did not record it, <c>TryFindTool</c> reported it as not present, and the "a panel
/// we did not find must be closed" fallback wrote it into the <c>.cws</c> as closed. Auto-hiding the
/// Project Tree and saving therefore lost the panel outright on reopen (owner, 2026-09-11).</para>
///
/// <para>The fix is not to teach each walker about pinned lists — it is to have one named place that
/// answers the four questions anybody asks about an auto-hidden panel, so the next walker added has
/// something to call instead of a fifth private guess.</para>
///
/// <h3>The FLYOUT is a fifth place, and it is not the same question</h3>
/// <para>While a strip's tab is showing its panel, Dock ALSO puts that dockable in
/// <c>IRootDock.PinnedDock</c> — a transient one-item tool dock that is not in the tree either. The
/// dockable stays in its side list throughout, so <see cref="IsAutoHidden"/> is true the whole time
/// and <see cref="IsFlyoutShowing"/> is the separate question "is it on screen right now". A caller
/// that conflates the two makes a panel that is merely flown out look permanently docked, and a
/// toggle bound to it stops working.</para>
///
/// <para>Framework-free (<c>Dock.Model</c> only), like <see cref="DockPanelHiding"/> beside it, so the
/// whole auto-hide round trip is testable against a real <c>Factory</c> rather than through a
/// window.</para>
/// </summary>
public static class DockAutoHide
{
    /// <summary>Every pinned list of <paramref name="root"/>, paired with the <see cref="DockSide"/> it is.</summary>
    public static IEnumerable<(string Side, IList<IDockable> Dockables)> Lists(IRootDock root)
    {
        if (root.LeftPinnedDockables   is { } l) yield return (DockSide.Left,   l);
        if (root.RightPinnedDockables  is { } r) yield return (DockSide.Right,  r);
        if (root.TopPinnedDockables    is { } t) yield return (DockSide.Top,    t);
        if (root.BottomPinnedDockables is { } b) yield return (DockSide.Bottom, b);
    }

    /// <summary>The side <paramref name="dockable"/> is auto-hidden on, or null when it is not.</summary>
    public static string? SideOf(IRootDock? root, IDockable? dockable)
    {
        if (root is null || dockable is null) return null;

        foreach (var (side, list) in Lists(root))
            if (list.Contains(dockable))
                return side;

        return null;
    }

    /// <summary>Whether <paramref name="dockable"/> is auto-hidden — flown out or not.</summary>
    public static bool IsAutoHidden(IRootDock? root, IDockable? dockable) => SideOf(root, dockable) is not null;

    /// <summary>
    /// Whether an auto-hidden <paramref name="dockable"/> is currently flown out over the canvas.
    /// See the remarks on this class for why this is a different question from
    /// <see cref="IsAutoHidden"/>.
    /// </summary>
    public static bool IsFlyoutShowing(IRootDock? root, IDockable? dockable) =>
        root?.PinnedDock?.VisibleDockables?.Contains(dockable!) == true;

    /// <summary>
    /// Auto-hides <paramref name="tool"/> on <paramref name="side"/> of a layout being BUILT.
    ///
    /// <para><b>Not <c>IFactory.PinDockable</c>, on purpose.</b> That method performs the user's
    /// gesture on a LIVE tree: it needs the dockable to be in a tool dock, reachable from the root by
    /// its <c>Owner</c> chain, which is only true after <c>InitLayout</c> has run — i.e. after the
    /// window is already showing the panel docked. Calling it then would restore the layout and
    /// immediately re-arrange it, which is visible. The builder instead assembles the same end state
    /// directly: the tool is never put in the tool dock's visible list at all, so there is nothing to
    /// take out of it.</para>
    ///
    /// <para><paramref name="home"/> is the (empty) tool dock the panel came from, recorded as
    /// <see cref="IDockable.OriginalOwner"/> — the field Dock's own un-hide reads to decide where the
    /// panel goes back to. Without it the panel returns to whichever dock happens to match the
    /// alignment, which for a side with two stacked groups is a coin flip; with it, it returns to its
    /// own group. It is also what lets the next capture find the panel's home again, so an auto-hidden
    /// panel's group and tab order survive any number of save/reopen cycles rather than decaying to
    /// the default on the second one.</para>
    ///
    /// <para><c>InitLayout</c> finishes the job: <c>InitDockable</c> on a root walks the four pinned
    /// lists as well as the tree, so the tool gets its <c>Owner</c>, its <c>Factory</c> and its
    /// <c>DockingState</c> of <c>Pinned</c> with nothing further from us.</para>
    ///
    /// <para><paramref name="flyoutWidth"/>/<paramref name="flyoutHeight"/> are the size the panel
    /// flies out at, in logical pixels, and they are the one piece of auto-hide state the user's own
    /// gesture sets that a rebuilt layout does not. <c>PinDockable</c> calls Dock's internal
    /// <c>UpdatePinnedBoundsFromVisible</c> on the way past, seeding the rectangle from the panel's
    /// docked size; this method assembles the end state directly and never goes past that line, so
    /// without this the rectangle stays unset and every reopened flyout is the library's default width
    /// however wide the user had made it (owner, 2026-09-11). Both dimensions or neither — see
    /// <see cref="CwsDockPanel.AutoHiddenWidth"/> for why a half-set rectangle is overwritten on the
    /// first layout pass.</para>
    /// </summary>
    public static void Pin(IFactory factory, IRootDock root, string side, IDockable tool, IDock? home,
                           double flyoutWidth = 0.0, double flyoutHeight = 0.0)
    {
        var list = side switch
        {
            DockSide.Right  => root.RightPinnedDockables  ??= factory.CreateList<IDockable>(),
            DockSide.Top    => root.TopPinnedDockables    ??= factory.CreateList<IDockable>(),
            DockSide.Bottom => root.BottomPinnedDockables ??= factory.CreateList<IDockable>(),
            _               => root.LeftPinnedDockables   ??= factory.CreateList<IDockable>(),
        };

        if (list.Contains(tool)) return;

        tool.OriginalOwner = home;

        if (double.IsFinite(flyoutWidth)  && flyoutWidth  > 0.0
         && double.IsFinite(flyoutHeight) && flyoutHeight > 0.0)
            tool.SetPinnedBounds(0.0, 0.0, flyoutWidth, flyoutHeight);

        list.Add(tool);
    }

    /// <summary>
    /// The tool dock an auto-hidden <paramref name="dockable"/> belongs to, or null when there is none
    /// to be had.
    ///
    /// <para>Two fields, because the two ways a panel gets auto-hidden leave the record in different
    /// places. The user's own gesture (<c>PinDockable</c>) removes the dockable from its tool dock but
    /// leaves <see cref="IDockable.Owner"/> pointing at it; a layout RESTORE writes the same dock to
    /// <see cref="IDockable.OriginalOwner"/> (see <see cref="Pin"/>), and <c>InitLayout</c> then
    /// re-points <c>Owner</c> at the root. Reading both means a captured layout describes the same
    /// home whichever route the panel took — which is what makes capture → restore → capture stable
    /// rather than losing the grouping on the second cycle.</para>
    ///
    /// <para>Never returns <c>PinnedDock</c>: while the panel is flown out, that transient one-item
    /// dock is its <c>Owner</c>, and recording it as a home would file the panel's placement against a
    /// container that ceases to exist the moment the flyout closes.</para>
    /// </summary>
    public static IToolDock? HomeOf(IRootDock? root, IDockable dockable)
    {
        var pinnedDock = root?.PinnedDock;

        if (dockable.OriginalOwner is IToolDock original && !ReferenceEquals(original, pinnedDock))
            return original;

        if (dockable.Owner is IToolDock owner && !ReferenceEquals(owner, pinnedDock))
            return owner;

        return null;
    }
}
