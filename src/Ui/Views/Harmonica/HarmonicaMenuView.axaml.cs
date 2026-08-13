using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Harmonica;

/// <summary>
/// harmonicaRF's own menu bar (§7.6), on both surfaces.
///
/// <para><b>The in-window <c>Menu</c> is always visible</b>, docked or not. That differs from
/// <c>TornOffFileMenuView</c> on purpose: the File menu it carries duplicates one the workspace bar
/// already shows, whereas harmonicaRF's Markers / Display / Grid menus exist nowhere else. Hiding
/// them while docked would leave the document with no menu set at all.</para>
///
/// <para><b>The macOS <c>NativeMenu</c> is attached to the hosting Window only when harmonicaRF has a
/// window of its own</b> — a torn-off document, or the standalone binary of §3.1. Attaching it from a
/// docked tab would replace circuitRF's application menu bar for the whole app, which is not what a
/// document-scoped menu means. Same per-window attach §4B.1 records as the one that actually works.</para>
///
/// <para><b>The band submenus are built here, not bound.</b> <c>NativeMenu</c> has no
/// <c>ItemsSource</c> — the same limitation <c>WorkspaceWindow</c>'s Window menu works around — so
/// they are rebuilt from the SAME collections the in-window menu binds to. One source, two surfaces.</para>
/// </summary>
public partial class HarmonicaMenuView : UserControl
{
    private HarmonicaMenuViewModel? _vm;

    /// <summary>The ONE <see cref="NativeMenu"/> instance this view owns — captured once, off XAML,
    /// so later attach/detach calls never depend on re-reading it from whichever object currently
    /// happens to carry the attached property (which is exactly the bug: reading it off <c>this</c>
    /// after it had already been moved elsewhere returned null).</summary>
    private NativeMenu? _ownMenu;

    /// <summary>Whichever <see cref="AvaloniaObject"/> currently carries <see cref="_ownMenu"/> — at
    /// most one, per R-h9a-1. Starts as <c>this</c>, since that is what the XAML's own
    /// <c>&lt;NativeMenu.Menu&gt;</c> block attaches it to at load time.</summary>
    private AvaloniaObject? _attachedTo;

    /// <summary>
    /// R-h9a-3 — true while this document is the ACTIVE tab in a workspace window's own DocumentDock.
    /// Set by <see cref="SetDockedFocus"/>, called from <c>HarmonicaView</c>'s own action-seam wiring
    /// (<c>HarmonicaDocumentViewModel.NativeMenuDockedFocusChanged</c>), which
    /// <c>WorkspaceViewModel</c>'s dock-level focus tracking drives. Irrelevant for a torn-off or
    /// standalone window — those already own the bar unconditionally via <see cref="RecomputeAttachment"/>.
    /// </summary>
    private bool _dockedHasFocus;

    public HarmonicaMenuView()
    {
        InitializeComponent();
        _ownMenu    = NativeMenu.GetMenu(this);
        _attachedTo = this;
        DataContextChanged     += (_, _) => OnViewModelChanged();
        AttachedToVisualTree   += (_, _) => { RecomputeAttachment(); RebuildNativeBandMenus(); };
        DetachedFromVisualTree += (_, _) => DetachNativeMenuFromWindow();
    }

    /// <summary>
    /// The window a harmonicaRF document does not own OUTRIGHT — a docked tab shares it with
    /// circuitRF's own menu, taking it over only while focused (R-h9a-3). Resolved by type NAME so
    /// this view takes no dependency on the workspace shell — harmonicaRF ships standalone, where
    /// that type does not exist at all.
    /// </summary>
    private const string WorkspaceWindowTypeName = "WorkspaceWindow";

    /// <summary>
    /// R-h9a-3's own entry point — called by <c>HarmonicaView</c> whenever
    /// <c>WorkspaceViewModel</c>'s dock-level focus tracking says this document became (<c>true</c>)
    /// or stopped being (<c>false</c>) the active docked tab.
    /// </summary>
    public void SetDockedFocus(bool hasFocus)
    {
        _dockedHasFocus = hasFocus;
        RecomputeAttachment();
    }

    private void OnViewModelChanged()
    {
        if (_vm is not null)
        {
            ((INotifyCollectionChanged)_vm.SourceBands).CollectionChanged -= OnBandsChanged;
            ((INotifyCollectionChanged)_vm.LoadBands).CollectionChanged   -= OnBandsChanged;
        }

        _vm = DataContext as HarmonicaMenuViewModel;
        if (_vm is null) return;

        ((INotifyCollectionChanged)_vm.SourceBands).CollectionChanged += OnBandsChanged;
        ((INotifyCollectionChanged)_vm.LoadBands).CollectionChanged   += OnBandsChanged;
        RebuildNativeBandMenus();
    }

    private void OnBandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildNativeBandMenus();

    /// <summary>
    /// R-h9a-2's whole policy in one place: decides which <see cref="AvaloniaObject"/> should carry
    /// <see cref="_ownMenu"/> right now, and moves it there if it isn't already. Three cases —
    /// <list type="bullet">
    /// <item>torn-off document, or the standalone binary: always own the hosting window outright;</item>
    /// <item>docked, and this document currently has focus (R-h9a-3): take over the SAME hosting
    /// window (a <c>WorkspaceWindow</c>), which silently overwrites whatever it had attached —
    /// circuitRF's own menu, restored by <c>WorkspaceViewModel</c>'s focus tracking on blur;</item>
    /// <item>docked, no focus: stay attached to <c>this</c> — inert, exactly as XAML left it.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// R-h9r2-12 — owner-reported: switching the OS colour theme to dark crashed the app with
    /// <c>ArgumentException("The menu being updated does not match.")</c> out of
    /// <c>AvaloniaNativeMenuExporter.Update</c>, thrown from the ATTACH call below (the same exception,
    /// from the same interop layer, as <see cref="DetachNativeMenuFromWindow"/>'s own guarded case — see
    /// that method's doc comment). A theme change fires <c>AttachedToVisualTree</c> here, and
    /// <c>ReferenceEquals(_attachedTo, desiredTarget)</c> found them NOT equal, so this proceeded to
    /// attach and the platform exporter refused it — meaning <c>_attachedTo</c> and what the exporter
    /// itself currently holds had already diverged BEFORE this call, most likely because the OS
    /// appearance-change notification and this view's own <c>AttachedToVisualTree</c>/
    /// <c>ActualThemeVariantChanged</c> handling (R-h9a-8) both touch this window's native menu around
    /// the same theme transition, on cocoa's own callback vs. Avalonia's dispatcher — a genuine
    /// Avalonia.Native race this view cannot see into or repair by re-attaching harder (not headlessly
    /// reproducible; not confirmed further than this). What this method controls is making sure that
    /// divergence can never again take the application down with it, and clearing it once found rather
    /// than trusting stale bookkeeping.
    /// </remarks>
    private void RecomputeAttachment()
    {
        if (!OperatingSystem.IsMacOS() || _ownMenu is null) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        bool isWorkspaceWindow = window.GetType().Name == WorkspaceWindowTypeName;
        AvaloniaObject desiredTarget =
            !isWorkspaceWindow    ? window     // torn-off document window, or the standalone shell
            : _dockedHasFocus     ? window     // docked WITH focus: take over the app menu bar
            :                       this;      // docked, no focus: stay inert on the control

        if (ReferenceEquals(_attachedTo, desiredTarget)) return;   // already there

        // R-h9a-1: at any instant a given NativeMenu instance is set on at most one AvaloniaObject.
        // Detach from wherever it currently is BEFORE attaching it elsewhere — attaching the SAME
        // instance to two AvaloniaObjects at once is exactly what crashed AvaloniaNativeMenuExporter.
        //
        // R-h9r2-12 — detach defensively, not only from where OUR bookkeeping thinks we are: after a
        // visual-tree event our _attachedTo field can disagree with what the platform exporter
        // believes it holds (that disagreement is the crash above), so clear the DESIRED target's menu
        // too, before setting it — cheap, and it removes one more way for the two to disagree. Guarded
        // exactly like the known-crashing detach in DetachNativeMenuFromWindow: a failed clear here
        // costs nothing, an unhandled exception costs the whole application.
        if (_attachedTo is { } current && !ReferenceEquals(current, desiredTarget))
        {
            try { NativeMenu.SetMenu(current, null); } catch (Exception) { }
        }
        try { NativeMenu.SetMenu(desiredTarget, null); } catch (Exception) { }

        try
        {
            NativeMenu.SetMenu(desiredTarget, _ownMenu);
            _attachedTo = desiredTarget;
        }
        catch (Exception)
        {
            // A failed menu-bar attach costs a menu bar; an unhandled exception here costs the whole
            // application, and the two are not close (owner-reported crash, R-h9r2-12). Left cleared
            // rather than restored to its old value: the old value is already known-stale the instant
            // SetMenu refuses it (the exporter's own state no longer matches what we last set there),
            // so keeping it would make the NEXT AttachedToVisualTree's ReferenceEquals early-return
            // skip a retry this document actually needs.
            _attachedTo = null;
        }
    }

    /// <summary>
    /// Runs on <c>DetachedFromVisualTree</c> — a torn-off document window closing, or a docked tab
    /// being removed. Only ever un-attaches from a WINDOW: the ordinary docked-and-unfocused case
    /// (menu still sitting on <c>this</c>, per the XAML's own attach) needs no cleanup, since that
    /// attachment never drove a real platform exporter in the first place (see
    /// <see cref="RecomputeAttachment"/>'s own inert third case). A tab closed WHILE it held the
    /// docked focus (<see cref="_dockedHasFocus"/>) also needs no restore of circuitRF's own menu here
    /// — <c>WorkspaceViewModel</c>'s own dock-level focus tracking sees the tab disappear and does that
    /// restore itself, from the one place that knows what circuitRF's own menu is. Leaving a closed
    /// window holding this menu would leave it owned by a dead exporter — R-h9a-1's other half.
    /// </summary>
    private void DetachNativeMenuFromWindow()
    {
        if (_ownMenu is null || _attachedTo is not Window window) return;

        // A torn-off window's own native teardown can already be under way by the time
        // DetachedFromVisualTree fires here — closing a window detaches its content from the visual
        // tree, but the platform's AvaloniaNativeMenuExporter for that window may by then have already
        // released or reassigned its native menu state. Asking it to Update to null in that state
        // throws ArgumentException("The menu being updated does not match.") from deep inside
        // Avalonia.Native's interop layer (owner-reported: closing a torn-off harmonicaRF window
        // crashed the whole app). The window and its native menu bar are being destroyed regardless,
        // so there is nothing left to clean up if this throws — swallowing it here is strictly safer
        // than an unhandled exception terminating the application over a window that was already
        // closing.
        try { NativeMenu.SetMenu(window, null); }
        catch (Exception) { }
        _attachedTo = null;
    }

    /// <summary>Rebuilds the two native band submenus from the view model's own collections.</summary>
    private void RebuildNativeBandMenus()
    {
        // Reads _ownMenu, not NativeMenu.GetMenu(this) — the instance may currently be attached to a
        // hosting Window rather than to `this` (see RecomputeAttachment), and re-reading it off
        // `this` after it has moved would silently find nothing.
        if (_vm is null || _ownMenu is not { } root) return;

        Fill(FindByHeader(root, "Markers", "Source Bands"), _vm.SourceBands);
        Fill(FindByHeader(root, "Markers", "Load Bands"),   _vm.LoadBands);

        static void Fill(NativeMenuItem? host, IReadOnlyList<HarmonicaBandMenuItem> bands)
        {
            if (host?.Menu is not { } target) return;
            target.Items.Clear();
            foreach (var band in bands)
            {
                var item = new NativeMenuItem(band.Header)
                {
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked  = band.IsPresent,
                    IsEnabled  = band.CanRemove,
                };
                var captured = band;
                // Writing IsPresent is what runs R-h7-2's add/remove — the SAME property the
                // in-window checkbox binds two-way to, so both surfaces go through one path.
                item.Click += (_, _) => captured.IsPresent = !captured.IsPresent;
                target.Items.Add(item);
            }
        }
    }

    /// <summary>Locates a native item by its header path. A header walk rather than a name, because
    /// <c>NativeMenuItem</c> is not a <c>Control</c> and carries no name scope.</summary>
    private static NativeMenuItem? FindByHeader(NativeMenu root, params string[] path)
    {
        NativeMenu? level = root;
        NativeMenuItem? found = null;

        foreach (string header in path)
        {
            found = null;
            if (level is null) return null;
            foreach (var entry in level.Items)
                if (entry is NativeMenuItem item && item.Header == header) { found = item; break; }
            if (found is null) return null;
            level = found.Menu;
        }
        return found;
    }
}
