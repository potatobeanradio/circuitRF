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
    // True only while CloseForLayoutRebuild is tearing this window down. The tool-float close guard
    // below exists to neutralize the USER's close box, not to make the window immortal.
    private bool _closingForLayoutRebuild;

    public CrfHostWindow()
    {
        this[!BackgroundProperty] = new DynamicResourceExtension("SystemChromeLowColor");

        // P / A must keep working while a floating panel has focus, which it does the moment one is
        // presented — otherwise the toggle dies after two presses and only a click on the shell revives it
        // (owner, 2026-08-17). This is a second TopLevel, so it needs its own registration; the shortcut
        // itself, and why it is not solved by keeping focus in the shell, is in Views.WirePanelKeys.
        Views.WirePanelKeys.Attach(this, Views.WirePanelKeys.ResolveWorkspace);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If this floating window hosts any Tool, cancel the OS close. Re-dock by dragging.
        if (!_closingForLayoutRebuild && FloatsAnyTool())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Closes this floating window as part of replacing the dock layout — a workspace close or
    /// switch, Reset Layout, or the Hide/Show Dockers toggle.
    ///
    /// <para><b>The bug this fixes:</b> replacing <c>WorkspaceViewModel.Layout</c> swaps the MODEL, but
    /// a floating window is a real OS window that nothing closes. Dock's own <c>IDockWindow.Exit()</c>
    /// would do it — except it calls <c>Close()</c>, which <see cref="OnClosing"/> above cancels for
    /// any window hosting a tool. So a torn-off tool panel outlived the workspace it belonged to, and
    /// reopening that workspace restored a SECOND copy of the same panel.</para>
    ///
    /// <para><b>Why it detaches before closing rather than calling <c>Exit()</c>:</b>
    /// <c>HostWindow.OnClosed</c> calls <c>IFactory.CloseWindow</c>, which recursively
    /// <c>CloseDockable</c>s every dockable in the floating layout — precisely the teardown that
    /// crashes <c>FactoryBase.CloseDockable</c> and that this whole class exists to avoid (see the
    /// class note above). Clearing <c>Window</c> first makes <c>OnClosed</c> return at its own
    /// <c>if (Window == null)</c> guard, so the window closes with no cascade at all. The panels
    /// themselves are not lost: they are re-hosted by the layout being built.</para>
    /// </summary>
    internal void CloseForLayoutRebuild()
    {
        _closingForLayoutRebuild = true;

        var dockWindow = Window;
        // Second line of defence only — the factory deregisters this host itself, because this chain
        // is null once Dock has run RemoveWindow and a missed removal leaves a closed window in
        // HostWindows, which crashes the next window drag.
        dockWindow?.Factory?.HostWindows.Remove(this);
        if (dockWindow is not null) dockWindow.Host = null;
        Window = null;

        // A host built but never presented has no platform window to close; calling Close() on it
        // would throw rather than tidy anything up.
        if (PlatformImpl is not null) Close();
    }

    /// <summary>
    /// True when the floated layout contains at least one <see cref="ITool"/>. Walks the dock tree
    /// rooted at this host window's <see cref="IDockWindow.Layout"/>. Defensive against the partially
    /// torn-down state present during close: every dereference is null-guarded.
    /// </summary>
    internal bool FloatsAnyTool()
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
