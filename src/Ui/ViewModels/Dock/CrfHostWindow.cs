using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Host window used for dock tear-offs. Subclasses Dock's <see cref="HostWindow"/> to (1)
/// neutralize the OS close box for floating windows that contain a <b>Tool</b> (Properties,
/// Analyses, Project Tree, Palette, Messages), and (2) give every tear-off window the SAME
/// window-level background <see cref="WorkspaceWindow"/> itself uses.
///
/// Background (crash fix): closing a torn-off TOOL window drives Dock's FactoryBase.CloseDockable
/// down a window-teardown path that dereferences an already-stripped floating RootDock and throws
/// an unrecoverable NullReferenceException (confirmed by instrumentation). Document tear-off windows
/// do not hit that path and close cleanly. Rather than patch the moving null inside the library's
/// teardown, we prevent the crashing entry point: a tool float window's close box is inert -- the
/// user re-docks the panel by dragging its tab back. Document float windows are unaffected and
/// close normally.
///
/// Background (§1, brief-housekeeping-tearoff-palette-repo.md): <c>WorkspaceWindow.axaml</c>
/// explicitly sets <c>Background="{DynamicResource SystemChromeLowColor}"</c> on itself, but
/// Dock's own <see cref="HostWindow"/> base has no such setting — a tear-off window falls back to
/// whatever background the FluentTheme's stock Window template provides, which is close to but not
/// necessarily byte-identical to <c>SystemChromeLowColor</c> depending on theme/compositing
/// specifics. Setting the SAME resource at the window level here, exactly like
/// <c>WorkspaceWindow.axaml</c> does, removes the possibility of the two ever resolving
/// differently — the fix is at application/resource scope, never a hard-coded color (R-hk-2).
/// </summary>
public class CrfHostWindow : HostWindow
{
    public CrfHostWindow()
    {
        this[!BackgroundProperty] = new DynamicResourceExtension("SystemChromeLowColor");
    }

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
