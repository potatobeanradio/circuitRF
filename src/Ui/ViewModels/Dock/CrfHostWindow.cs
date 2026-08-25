using System;
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

    /// <summary>
    /// <b>How wide a floating window holding an EM setup is allowed to be.</b> Owner request,
    /// 2026-08-25 — 1600, then 800, then 700. (The first was a no-op: the window measured 1600
    /// already, so capping it there changed nothing, which is why it still read as too wide.)
    ///
    /// <para>The EM Setup panel is one narrow scrolling column of labelled fields, and almost
    /// nothing in it reflows into extra width: every numeric box is <c>TextBox.num</c>, a fixed
    /// 86 units wide; the analysis combo caps at 200; the frequency group is three 150-wide blocks.
    /// A tear-off that inherits the shell's size — which is what Dock's drag hands it, and on a
    /// maximized shell that is the whole screen — is mostly empty chrome beside a column of
    /// controls.</para>
    ///
    /// <para><b>The stackup table is what sets the floor, and 700 is close to it.</b> Its row is
    /// <c>76,*,90,190,*</c> plus spacing — about 380 units of FIXED columns — so at 700 the two
    /// stretching columns (conductor name and drawing layers) get roughly 150 each. Both already
    /// carry <c>TextTrimming="CharacterEllipsis"</c>, so a long conductor name trims rather than
    /// overflowing; that is the visible cost of this number, and it is the owner's chosen trade. A
    /// cap much below the fixed columns' own ~380 would squeeze them to nothing.</para>
    ///
    /// <para><b>Logical units, not device pixels.</b> Avalonia's <c>Window.Width</c> is in logical
    /// units, so on a 2× Retina display this window is 1600 device pixels across. That is the same
    /// convention every other window size in this codebase uses — see
    /// <c>ScreenPlacement.DeviceToLogical</c>, which exists because mixing the two is invisible on
    /// an unscaled display.</para>
    /// </summary>
    internal const double EmSetupFloatMaxWidth = 700;

    /// <summary>
    /// Applies <see cref="EmSetupFloatMaxWidth"/> once the window is actually on screen.
    ///
    /// <para><b>Here rather than in <c>CircuitRfDockFactory.CreateWindowFrom</c>, and the ordering is
    /// the reason.</b> A width set during window creation is not final: <c>DockWindowOptions.ApplyTo</c>
    /// assigns geometry unconditionally, which is the same overwrite the <c>OwnerMode</c> override
    /// beside it already has to work around, and Dock's drag tear-off supplies the dragged tab's own
    /// bounds through exactly that path. This is the last point in the sequence that this codebase
    /// owns, so it is the only one that cannot be silently undone.</para>
    ///
    /// <para><b>It is a CAP, not an initial size</b> — a narrower window is left alone, and the user
    /// can still widen this one by hand for as long as it is open. Re-floating it applies the cap
    /// again, because the cap is about what the panel's content can use rather than about what was
    /// last done to the window.</para>
    ///
    /// <para><b>The height bonus rides INSIDE the narrowing, and that is what stops it ratcheting.</b>
    /// Written as an unconditional <c>Height += 200</c> it would compound across launches: this
    /// window's geometry is captured into the <c>.cws</c>, so next launch would restore 200 taller,
    /// add 200 again, and keep going. Gating it on the width cap actually firing makes it
    /// idempotent — a restored float is already within the cap, so it is left exactly as the user
    /// last sized it, while a fresh tear-off (which arrives at the shell's full width) is narrowed
    /// and given the height back in the same breath.</para>
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Width <= EmSetupFloatMaxWidth || !FloatsAnyEmSetup()) return;

        Width  = EmSetupFloatMaxWidth;
        Height = Math.Min(Height + EmSetupFloatExtraHeight, AvailableHeight());
    }

    /// <summary>
    /// <b>What the window gains in height when it is narrowed</b> (owner request, 2026-08-25: "add
    /// 200 pixels to the window's height"). The panel is one long scrolling column, so height it
    /// does not have is height the user pays for in scrolling — and taking width away makes several
    /// of its groups (the two-column Analysis grid, the Frequency wrap panel) taller still.
    /// </summary>
    internal const double EmSetupFloatExtraHeight = 200;

    /// <summary>
    /// The working height of the screen this window is on, so the bonus cannot push the window's
    /// bottom off it. Falls back to <see cref="double.PositiveInfinity"/> — i.e. no limit — when
    /// there is no screen to ask, which is the headless case and every case where
    /// <c>ScreenPlacement</c> is the backstop anyway.
    /// </summary>
    private double AvailableHeight()
    {
        var screen = Screens?.ScreenFromWindow(this);
        if (screen is null) return double.PositiveInfinity;

        // WorkingArea is DEVICE pixels while Height is logical — mixing them is the bug
        // AvaloniaScreenSource exists to prevent, and it is invisible on an unscaled display.
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        return screen.WorkingArea.Height / scaling;
    }

    /// <summary>True when the floated layout contains at least one EM setup document. Same walk, and
    /// the same null-guarding against a partially torn-down tree, as
    /// <see cref="FloatsAnyTool"/>.</summary>
    internal bool FloatsAnyEmSetup()
    {
        var layout = Window?.Layout;
        return layout is not null && ContainsEmSetup(layout);
    }

    private static bool ContainsEmSetup(IDockable dockable)
    {
        if (dockable is Layout.Em.EmSetupDocument)
            return true;

        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is not null && ContainsEmSetup(child))
                    return true;
            }
        }

        return false;
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
