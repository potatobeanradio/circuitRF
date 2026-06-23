using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Host window used for dock tear-offs. Subclasses Dock's <see cref="HostWindow"/> solely to
/// neutralize the OS close box for floating windows that contain a <b>Tool</b> (Properties,
/// Analyses, Project Tree, Palette, Messages).
///
/// Background: closing a torn-off TOOL window drives Dock's FactoryBase.CloseDockable down a
/// window-teardown path that dereferences an already-stripped floating RootDock and throws an
/// unrecoverable NullReferenceException (confirmed by instrumentation). Document tear-off windows
/// do not hit that path and close cleanly. Rather than patch the moving null inside the library's
/// teardown, we prevent the crashing entry point: a tool float window's close box is inert -- the
/// user re-docks the panel by dragging its tab back. Document float windows are unaffected and
/// close normally.
/// </summary>
public class CrfHostWindow : HostWindow
{
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If this floating window hosts any Tool, cancel the OS close. Re-dock by dragging.
        if (FloatsAnyTool())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// True when the floated layout contains at least one <see cref="ITool"/>. Walks the dock tree
    /// rooted at this host window's <see cref="IDockWindow.Layout"/>. Defensive against the partially
    /// torn-down state present during close: every dereference is null-guarded.
    /// </summary>
    private bool FloatsAnyTool()
    {
        var layout = Window?.Layout;
        return layout is not null && ContainsTool(layout);
    }

    private static bool ContainsTool(IDockable dockable)
    {
        if (dockable is ITool)
            return true;

        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is not null && ContainsTool(child))
                    return true;
            }
        }

        return false;
    }
}
