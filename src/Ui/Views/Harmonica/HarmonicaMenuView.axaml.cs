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

    public HarmonicaMenuView()
    {
        InitializeComponent();
        DataContextChanged   += (_, _) => OnViewModelChanged();
        AttachedToVisualTree += (_, _) => { AttachNativeMenuIfOwnWindow(); RebuildNativeBandMenus(); };
    }

    /// <summary>
    /// The window a harmonicaRF document must NOT steal the menu bar from. Resolved by type NAME so
    /// this view takes no dependency on the workspace shell — harmonicaRF ships standalone, where
    /// that type does not exist at all.
    /// </summary>
    private const string WorkspaceWindowTypeName = "WorkspaceWindow";

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

    private void AttachNativeMenuIfOwnWindow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        if (TopLevel.GetTopLevel(this) is not Window window) return;
        if (window.GetType().Name == WorkspaceWindowTypeName) return;   // docked — leave the app bar alone

        if (NativeMenu.GetMenu(this) is { } menu) NativeMenu.SetMenu(window, menu);
    }

    /// <summary>Rebuilds the two native band submenus from the view model's own collections.</summary>
    private void RebuildNativeBandMenus()
    {
        if (_vm is null || NativeMenu.GetMenu(this) is not { } root) return;

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
