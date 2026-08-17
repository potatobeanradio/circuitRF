using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// Hiding a tool panel and putting it back <b>exactly</b> where it was — same slot, same size, no rebuild.
///
/// <h3>Why not just close and re-open it</h3>
/// <para>Owner, 2026-08-17: <i>"I see the entire workspace dock redraw when the Array Inductance is
/// brought back — I see a flash. When I dock it manually using the Dock system there is no flash."</i>
/// Closing a panel lets its owner dock collapse out of the tree, and the only way back is then to rebuild
/// the whole shell from a captured layout — which re-realises every document view. That is both the flash
/// and the reason the keyboard shortcut stopped working: the view handling the key was re-created
/// underneath it.</para>
///
/// <para><c>HideDockable</c> moves the dockable to the root's hidden list and records its
/// <see cref="IDockable.OriginalOwner"/>; <c>RestoreDockable</c> puts it back there. That is the whole
/// mechanism, and it is deliberately <b>all</b> of it — see below.</para>
///
/// <h3>The emptied dock is Dock's business, not ours — measured, after getting it wrong</h3>
/// <para>An earlier version of this file detached the emptied <c>ToolDock</c> (and one adjacent splitter)
/// from its parent and re-attached it on the way back, on the reasoning that <i>a proportional child with
/// no content is a blank strip taking its share of the window</i>. <b>That reasoning was never measured,
/// and it is false.</b> Laid out for real, an emptied dock and its splitter both render at <b>0 px</b> —
/// Dock collapses them itself.</para>
///
/// <para>Worse, the detach was the cause of a second owner report — <i>"repeatedly pressing A or P results
/// in the panel height getting smaller and smaller"</i> — and the mechanism is worth stating, because it
/// defeats any fix attempted at this layer:</para>
/// <list type="number">
///   <item>Removing the dock leaves its sibling alone in the column, so <c>ProportionalStackPanel</c>
///     renormalises the sibling's <b>control</b> to 1.0 as a LOCAL value, which two-way-binds back to the
///     model.</item>
///   <item>Re-inserting the dock and re-asserting the remembered proportions on the MODEL cannot undo that:
///     a local value on the control outranks the style-priority binding, so the survivor's control keeps
///     its 1.0 and never sees the model write.</item>
///   <item>The next layout pass normalises 0.668 against 1.0 → 0.40/0.60, and writes THAT back to the
///     model. Every cycle takes another bite: 0.668 → 0.40 → 0.29 → 0.22 → 0.18.</item>
/// </list>
/// <para>Left alone, the collapse is Dock's own and reverses exactly: 0.668/0.332 returns to 0.668/0.332
/// on every cycle indefinitely. <b>The lesson is the reason this note is long: the "tidying up" was doing
/// the library's job for it, badly, and the bug it caused could not be fixed from the layer that caused
/// it.</b></para>
///
/// <h3>DOCKED panels only — a floating one is closed, not hidden</h3>
/// <para>Owner, 2026-08-17: <i>"lots of issues getting A or P to toggle when they are floating — their
/// window contents disappears and the window is not closed, and I see that flash bug too."</i> Both
/// symptoms are one measured fact, again confirmed against a real <c>Factory</c>: <b><c>HideDockable</c>
/// files a floating tool under the FLOAT's own root, not the shell's.</b> So the empty floating window
/// stays on screen (the vanished contents), and a restore that looks in the shell root's hidden list never
/// finds it and falls through to the rebuild (the flash).</para>
///
/// <para>The asymmetry is what the two cases actually are. A docked panel's place is a <i>slot in a
/// tree</i>, which the library holds open for us. A floating panel's place is a <i>rectangle on a
/// screen</i>, which is a value: write it down, close the window outright, re-open one there. See
/// <see cref="HoldsOtherTools"/>, the one question worth asking before closing one.</para>
///
/// <para>Framework-free (<c>Dock.Model</c> only), so the whole hide/restore round trip is testable against
/// a real <c>Factory</c> rather than through a window.</para>
/// </summary>
public static class DockPanelHiding
{
    /// <summary>
    /// Hides <paramref name="tool"/>, leaving the dock it came from exactly as it is.
    ///
    /// <para>A one-line wrapper on purpose. It is a named seam rather than a call to
    /// <c>HideDockable</c> at the call site so that the reasoning above — in particular everything this
    /// deliberately does NOT do to the emptied dock — has somewhere to live.</para>
    /// </summary>
    public static void Hide(IFactory factory, IDockable tool) => factory.HideDockable(tool);

    /// <summary>
    /// Puts <paramref name="tool"/> back where <see cref="Hide"/> found it.
    /// </summary>
    /// <returns>
    /// True when the tool is visible in the tree afterwards. False when its owner has since left the tree
    /// entirely — a shell rebuilt underneath a hidden panel — so the caller can fall back to rebuilding
    /// from the remembered placement. <b>Reachability from the root is the test, not merely that the dock
    /// took the tool back</b>: an orphaned dock accepts it just as readily and reports success while the
    /// panel is nowhere on screen.
    /// </returns>
    public static bool Restore(IFactory factory, IRootDock root, IDockable tool)
    {
        factory.RestoreDockable(tool);

        return tool.Owner is IDock back
            && back.VisibleDockables?.Contains(tool) == true
            && Contains(root, back);
    }

    /// <summary>
    /// True when <paramref name="layout"/> holds a tool other than <paramref name="tool"/> — i.e. whether
    /// closing the window around it would take somebody else's panel with it.
    ///
    /// <para>Asked only of FLOATING panels, where hiding means closing a window, which is not a per-panel
    /// operation at all. A docked panel's dock may be shared too, and the library handles that itself.</para>
    /// </summary>
    public static bool HoldsOtherTools(IDockable layout, IDockable tool)
    {
        if (ReferenceEquals(layout, tool)) return false;
        if (layout is ITool) return true;
        if (layout is not IDock dock || dock.VisibleDockables is null) return false;

        foreach (var child in dock.VisibleDockables)
            if (child is not null && HoldsOtherTools(child, tool)) return true;

        return false;
    }

    /// <summary>Whether <paramref name="target"/> is still somewhere in the tree.</summary>
    private static bool Contains(IDockable root, IDockable target)
    {
        if (ReferenceEquals(root, target)) return true;
        if (root is not IDock dock || dock.VisibleDockables is null) return false;

        foreach (var child in dock.VisibleDockables)
            if (child is not null && Contains(child, target)) return true;

        return false;
    }
}
