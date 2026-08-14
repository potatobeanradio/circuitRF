using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
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
/// window of its own</b> — a torn-off document, or the standalone binary of §3.1. A docked tab never
/// calls <c>NativeMenu.SetMenu</c> on the shared <c>WorkspaceWindow</c> at all — a window's
/// <c>NativeMenu</c> instance is fixed for its whole lifetime (brief-harmonicarf-r3a §1); instead its
/// own top-level items are injected into circuitRF's app-menu instance while it has focus, and
/// withdrawn on blur. See <c>RecomputeAttachment</c> and <c>src/Ui/RESOLVED.md</c>.</para>
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

    /// <summary>The top-level items currently injected into circuitRF's own app-menu instance (§2.1
    /// of brief-harmonicarf-r3a) — empty whenever this document is not the docked-and-focused holder.
    /// Tracked by reference so withdrawal can only ever remove exactly what was added.</summary>
    private readonly List<NativeMenuItem> _injectedItems = new();

    public HarmonicaMenuView()
    {
        InitializeComponent();
        _ownMenu    = NativeMenu.GetMenu(this);
        _attachedTo = this;
        DataContextChanged     += (_, _) => OnViewModelChanged();
        AttachedToVisualTree   += (_, _) => { RecomputeAttachment(); RebuildNativeBandMenus(); };
        DetachedFromVisualTree += (_, _) => { DetachNativeMenuFromWindow(); WithdrawInjectedItemsIfAny(); };
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
        RefreshInjectedItemsIfAny();
    }

    private void OnBandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildNativeBandMenus();
        RefreshInjectedItemsIfAny();
    }

    /// <summary>
    /// R3A §1/§2.1's whole policy in one place. On macOS, a window's <c>NativeMenu</c> instance is
    /// chosen ONCE, by Avalonia's own <c>AvaloniaNativeMenuExporter</c>, and can never be changed for
    /// that window's lifetime — <c>__MicroComIAvnMenuProxy.Update</c> throws
    /// <c>ArgumentException("The menu being updated does not match.")</c> for any instance other than
    /// the one the exporter was first given (Avalonia 12.0.3's own
    /// <c>AvaloniaNativeMenuExporter.cs</c>/<c>IAvnMenu.cs</c> — see <c>src/Ui/RESOLVED.md</c>). So
    /// this method never tries to change WHICH instance the <c>WorkspaceWindow</c> shows — only two
    /// cases remain:
    /// <list type="bullet">
    /// <item>torn-off document, or the standalone binary: this window has never had a
    /// <c>NativeMenu</c> of its own — own it outright, via <see cref="AttachToWindowOutright"/>;</item>
    /// <item>docked: circuitRF's own app-menu instance is already permanently bound to the
    /// <c>WorkspaceWindow</c>'s exporter (set once, at startup, by
    /// <c>WorkspaceWindow.AttachNativeMenuAtApplicationScope</c>) — <c>NativeMenu.SetMenu</c> is never
    /// called on that window again. Focus (R-h9a-3) is instead expressed by injecting this document's
    /// own top-level items into that SAME instance's <c>Items</c> list while focused, and withdrawing
    /// exactly those items on blur — see <see cref="InjectDockedItemsIfNeeded"/>/
    /// <see cref="WithdrawInjectedItemsIfAny"/> and <see cref="HarmonicaAppMenuInjector"/>.</item>
    /// </list>
    /// </summary>
    private void RecomputeAttachment()
    {
        if (!OperatingSystem.IsMacOS() || _ownMenu is null) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        bool isWorkspaceWindow = window.GetType().Name == WorkspaceWindowTypeName;

        if (isWorkspaceWindow)
        {
            if (_dockedHasFocus) InjectDockedItemsIfNeeded();
            else WithdrawInjectedItemsIfAny();
            return;
        }

        // torn-off document window, or the standalone shell: own the hosting window outright. Not
        // (or no longer) docked, so nothing should be injected into circuitRF's own app menu.
        WithdrawInjectedItemsIfAny();
        AttachToWindowOutright(window);
    }

    /// <summary>
    /// The torn-off/standalone case: this window has never had a <c>NativeMenu</c> of its own, so
    /// binding it here is the FIRST and only bind its exporter will ever accept (§1).
    /// </summary>
    private void AttachToWindowOutright(Window window)
    {
        AvaloniaObject desiredTarget = window;
        if (ReferenceEquals(_attachedTo, desiredTarget)) return;   // already there

        // §2.3 — this window's exporter must never already be bound to a DIFFERENT instance, or this
        // attach is refused forever, not just this once. WorkspaceViewModel.TryWireWindowFocusTracking
        // excludes a HarmonicaDocument (and a WBondDocument) window from ever receiving circuitRF's
        // own shared NativeMenu for exactly this reason — assert the invariant here rather than only
        // trust that call site's own comment.
        Debug.Assert(
            NativeMenu.GetMenu(desiredTarget) is null,
            "A harmonicaRF document window must never have received another NativeMenu instance " +
            "before its own (see WorkspaceViewModel.TryWireWindowFocusTracking's Harmonica/WBond " +
            "exclusion, brief-harmonicarf-r3a §2.3) — this attach can only ever be refused if it has.");

        // R-h9a-1: at any instant a given NativeMenu instance is set on at most one AvaloniaObject.
        // Detach from wherever it currently is BEFORE attaching it elsewhere — attaching the SAME
        // instance to two AvaloniaObjects at once is exactly what crashed AvaloniaNativeMenuExporter.
        if (_attachedTo is { } current && !ReferenceEquals(current, desiredTarget))
        {
            try { NativeMenu.SetMenu(current, null); } catch (Exception) { }
        }

        try
        {
            NativeMenu.SetMenu(desiredTarget, _ownMenu);
            _attachedTo = desiredTarget;
        }
        catch (Exception)
        {
            // A failed menu-bar attach costs a menu bar; an unhandled exception here costs the whole
            // application, and the two are not close. Left cleared rather than restored to its old
            // value: the old value is already known-stale the instant SetMenu refuses it, so keeping
            // it would make the NEXT AttachedToVisualTree's ReferenceEquals early-return skip a retry
            // this document actually needs. This floor should never fire in practice now that §2.3's
            // precondition is asserted above — see App.axaml.cs's Dispatcher.UnhandledException
            // backstop (§2.4) for the case a queued reset throws where no call-site catch can reach it.
            _attachedTo = null;
        }
    }

    /// <summary>
    /// §2.1 — appends this document's own top-level items (Markers / Display / Grid) to circuitRF's
    /// app-menu <c>NativeMenu</c> instance, read off <c>Application.Current</c> — the SAME instance
    /// <c>WorkspaceWindow.AttachNativeMenuAtApplicationScope</c> bound to the shell's exporter at
    /// startup. A no-op if already injected, or if there is nothing to build yet.
    /// </summary>
    private void InjectDockedItemsIfNeeded()
    {
        if (_injectedItems.Count > 0 || _vm is null) return;
        if (Application.Current is not { } app) return;
        if (NativeMenu.GetMenu(app) is not { } appMenu) return;

        var items = HarmonicaAppMenuInjector.BuildTopLevelItems(_vm);
        HarmonicaAppMenuInjector.Inject(appMenu, items);
        _injectedItems.AddRange(items);
    }

    /// <summary>Removes exactly the items <see cref="InjectDockedItemsIfNeeded"/> added, if any.</summary>
    private void WithdrawInjectedItemsIfAny()
    {
        if (_injectedItems.Count == 0) return;
        if (Application.Current is { } app && NativeMenu.GetMenu(app) is { } appMenu)
            HarmonicaAppMenuInjector.Withdraw(appMenu, _injectedItems);
        _injectedItems.Clear();
    }

    /// <summary>Rebuilds the injected set in place when the source data changes (a band toggled, or
    /// the view model swapped) while this document currently holds the takeover — otherwise the
    /// native items would silently drift from the model until the next focus change.</summary>
    private void RefreshInjectedItemsIfAny()
    {
        if (_injectedItems.Count == 0) return;
        WithdrawInjectedItemsIfAny();
        InjectDockedItemsIfNeeded();
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

    /// <summary>Rebuilds the native band submenus AND Contour Harmonic from the view model's own
    /// collections.</summary>
    private void RebuildNativeBandMenus()
    {
        // Reads _ownMenu, not NativeMenu.GetMenu(this) — the instance may currently be attached to a
        // hosting Window rather than to `this` (see RecomputeAttachment), and re-reading it off
        // `this` after it has moved would silently find nothing.
        if (_vm is null || _ownMenu is not { } root) return;

        Fill(FindByHeader(root, "Markers", "Source Bands"), _vm.SourceBands);
        Fill(FindByHeader(root, "Markers", "Load Bands"),   _vm.LoadBands);
        FillHarmonics(FindByHeader(root, "Display", "Contour Harmonic"), _vm.ContourHarmonics);

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

        static void FillHarmonics(NativeMenuItem? host, IReadOnlyList<HarmonicaHarmonicMenuItem> items)
        {
            if (host?.Menu is not { } target) return;
            target.Items.Clear();
            foreach (var it in items)
            {
                var item = new NativeMenuItem(it.Header);
                var captured = it;
                item.Click += (_, _) => captured.SelectCommand.Execute(null);
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
