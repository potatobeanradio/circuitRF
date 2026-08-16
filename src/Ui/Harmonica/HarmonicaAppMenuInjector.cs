// ================================================================
//  HarmonicaAppMenuInjector.cs — brief-harmonicarf-r3a §2.1
//
//  On macOS, an AvaloniaNativeMenuExporter binds to the FIRST NativeMenu instance it is ever handed,
//  for the hosting window's whole lifetime (see HarmonicaMenuView's own RecomputeAttachment doc
//  comment, and src/Ui/RESOLVED.md). A DOCKED harmonicaRF document must therefore never call
//  NativeMenu.SetMenu on the WorkspaceWindow at all — instead, while it has focus, its own top-level
//  items (Markers / Display / Grid — not File/Edit/Help, which circuitRF's own bar already carries)
//  are appended to circuitRF's own app-menu NativeMenu INSTANCE, and removed again on blur.
//
//  A THIRD rendering of the same source the in-window Menu and the standalone/torn-off NativeMenu
//  already build from (see HarmonicaMenuView.axaml's own "TWO SURFACES, HAND-MIRRORED" comment) —
//  never a copy of either. Kept off HarmonicaMenuView (a UserControl, not constructible headlessly —
//  see HarmonicaMenuNativeAttachTests) so the "which items go in, and which come back out" decision
//  is a pure function, unit-testable with no Avalonia platform: constructing a plain
//  NativeMenu/NativeMenuItem needs no TopLevel and no platform — only NativeMenu.SetMenu (a real
//  window/exporter operation) does.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;

namespace CircuitRF.Ui.Harmonica;

public static class HarmonicaAppMenuInjector
{
    /// <summary>Builds fresh harmonicaRF / Markers / Display / Grid top-level items from the view
    /// model's own collections and commands. Each call returns brand-new <see cref="NativeMenuItem"/>
    /// instances — never <c>_ownMenu</c>'s own children, which <see cref="NativeMenu"/>'s list
    /// validator refuses to accept a second time (an item that already has a <c>Parent</c> throws
    /// <see cref="System.InvalidOperationException"/>).</summary>
    public static IReadOnlyList<NativeMenuItem> BuildTopLevelItems(HarmonicaMenuViewModel vm)
        => [BuildHarmonicaRf(vm), BuildMarkers(vm), BuildDisplay(vm), BuildGrid(vm)];

    /// <summary>
    /// Appends <paramref name="items"/> to <paramref name="appMenu"/>'s own <c>Items</c> — ATOMICALLY.
    ///
    /// <para>brief-harmonicarf-r6a §1.2 — the owner reported the docked bar showing <c>Markers</c> but
    /// not <c>Display</c>/<c>Grid</c>: exactly the symptom of a <c>foreach</c> loop that adds items one
    /// at a time with no rollback, where a later item throws (<c>NativeMenu</c>'s own list validator
    /// refuses any item that already has a <c>Parent</c>) after an earlier one already succeeded. The
    /// exact throw did not reproduce headlessly against a normal Inject/Withdraw/re-Inject cycle (see
    /// <c>HarmonicaAppMenuInjectorTests</c>), so this fixes the loop to be failure-visible regardless of
    /// cause: on ANY exception partway through, every item added so far in this call is removed again
    /// before the exception propagates — the caller either sees the WHOLE set land, or NONE of it.
    /// </para>
    /// </summary>
    public static void Inject(NativeMenu appMenu, IReadOnlyList<NativeMenuItem> items)
    {
        var added = new List<NativeMenuItem>(items.Count);
        try
        {
            foreach (var item in items)
            {
                appMenu.Items.Add(item);
                added.Add(item);
            }
        }
        catch
        {
            foreach (var item in added) appMenu.Items.Remove(item);
            throw;
        }
    }

    /// <summary>Removes exactly <paramref name="items"/> from <paramref name="appMenu"/> — by
    /// reference, never by header match or index, so a withdrawal can never remove something
    /// circuitRF's own bar (or the user) added in between.</summary>
    public static void Withdraw(NativeMenu appMenu, IReadOnlyList<NativeMenuItem> items)
    {
        foreach (var item in items) appMenu.Items.Remove(item);
    }

    // ── builders — mirrors HarmonicaMenuView.axaml's <NativeMenu.Menu> block, harmonicaRF/Markers/
    //    Display/Grid (Help duplicates what circuitRF's own bar already shows) ─────────────────────

    /// <summary>
    /// brief-harmonicarf-r6a §1.3 — the owner ruling: a DOCKED document gets ONE extra top-level menu
    /// named <c>harmonicaRF</c>, holding the document-scoped items that live in the torn-off File and
    /// Edit menus (minus Undo/Redo — circuitRF's own Edit ▸ Undo already owns ⌘Z on a docked window,
    /// and two Undo items on one gesture is worse than one). This is what closes §2's own "docked has
    /// no route to harmonicaRF's own Settings…" gap. The grouping/order mirrors File's own separators,
    /// with Edit's copy group and Settings… folded in before Close (File's own last item).
    /// </summary>
    private static NativeMenuItem BuildHarmonicaRf(HarmonicaMenuViewModel vm)
        => new("harmonicaRF")
        {
            Menu = MenuOf(
                Item("New",           vm.NewDocumentCommand),
                Item("Open .charm…",  vm.OpenDocumentCommand),
                Item("Save",          vm.SaveDocumentCommand),
                Item("Save As…",      vm.SaveDocumentAsCommand),
                Sep(),
                Item("Set DUT…",      vm.SetDutCommand),
                Item("Refresh DUT",   vm.RefreshDutCommand),
                Sep(),
                Item("Import .gam…",       vm.ImportGamCommand),
                Item("Export .gam…",       vm.ExportGamCommand),
                Item("Export Data…",       vm.ExportDataCommand),
                Item("Export Testbench…",  vm.ExportTestbenchCommand),
                Sep(),
                Item("Settings…", vm.SettingsCommand),
                Sep(),
                Item("Close", vm.CloseDocumentCommand)),
        };

    private static NativeMenuItem BuildMarkers(HarmonicaMenuViewModel vm)
    {
        var sourceBands = new NativeMenuItem("Source Bands") { Menu = new NativeMenu() };
        var loadBands   = new NativeMenuItem("Load Bands")   { Menu = new NativeMenu() };
        FillBands(sourceBands.Menu!, vm.SourceBands);
        FillBands(loadBands.Menu!,   vm.LoadBands);

        // R9D §3.6 — writes ONLY the Load-side markers that already exist; never creates one.
        var presetTerminations = new NativeMenuItem("Preset Terminations")
        {
            Menu = MenuOf(
                Item("Class B",   vm.SetPaClassPresetCommand, "B",        new KeyGesture(Key.B, KeyModifiers.Meta)),
                Item("Class J",   vm.SetPaClassPresetCommand, "J",        new KeyGesture(Key.J, KeyModifiers.Meta)),
                Item("Class J*",  vm.SetPaClassPresetCommand, "JStar",    new KeyGesture(Key.J, KeyModifiers.Meta | KeyModifiers.Shift)),
                Item("Class F",   vm.SetPaClassPresetCommand, "F",        new KeyGesture(Key.F, KeyModifiers.Meta)),
                Item("Class F⁻¹", vm.SetPaClassPresetCommand, "FInverse", new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Shift))),
        };

        return new NativeMenuItem("Markers")
        {
            Menu = MenuOf(
                sourceBands,
                loadBands,
                presetTerminations,
                Sep(),
                Item("Add Load Marker",   vm.AddLoadMarkerCommand,   gesture: new KeyGesture(Key.A, KeyModifiers.Meta)),
                Item("Add Source Marker", vm.AddSourceMarkerCommand)),
        };
    }

    private static NativeMenuItem BuildDisplay(HarmonicaMenuViewModel vm)
    {
        var contourPlane = new NativeMenuItem("Contour Plane")
        {
            Menu = MenuOf(
                Item("Load",   vm.SetGridSideCommand, "Load"),
                Item("Source", vm.SetGridSideCommand, "Source")),
        };
        // Owner-reported (the same bug HarmonicaMenuViewModel.ContourHarmonics/RebuildBandMenus fixed
        // on the other two surfaces): this used to be three hardcoded items and never tracked K, so
        // a docked-and-focused document's injected app menu still offered only f₀/2f₀/3f₀ once K > 3
        // — even though InjectDockedItemsIfNeeded/RefreshInjectedItemsIfAny rebuild this whole set on
        // every band change. Built from vm.ContourHarmonics, the SAME K-length collection the other
        // two surfaces already read, so this one can no longer fall behind them.
        var contourHarmonic = new NativeMenuItem("Contour Harmonic")
        {
            Menu = MenuOf(vm.ContourHarmonics
                .Select(band => Item(band.Header, band.SelectCommand))
                .ToArray()),
        };
        var efficiencyMetric = new NativeMenuItem("Efficiency Metric")
        {
            Menu = MenuOf(
                Item("Drain Efficiency",  vm.SetEfficiencyMetricCommand, "DE"),
                Item("PAE", vm.SetEfficiencyMetricCommand, "PAE")),
        };
        var contourLevels = new NativeMenuItem("Contour Levels")
        {
            Menu = MenuOf(
                Item("5",  vm.SetContourLevelsCommand, "5"),
                Item("10", vm.SetContourLevelsCommand, "10"),
                Item("20", vm.SetContourLevelsCommand, "20")),
        };

        // brief-harmonicarf-r6a §4 — Edit Display / Add Trace… / Remove All Traces (deferred to a
        // harmonicaRF v2) and "Cursor Snap to Compression" (owner request) are removed from this menu
        // too — see HarmonicaMenuView.axaml's matching comments for the two other surfaces. The code
        // behind all of them stays wired; only the menu items are gone.
        return new NativeMenuItem("Display")
        {
            Menu = MenuOf(
                contourPlane,
                contourHarmonic,
                efficiencyMetric,
                Item("Loadline Plane", vm.ToggleLoadlinePlaneCommand),
                contourLevels,
                Item("Iso-line Labels",            vm.ToggleIsoLineLabelsCommand),
                // Round 11 §4 — ⌘L. The Ctrl+L half of "Control/Cmd L" is a KeyBinding on
                // HarmonicaView, not a second gesture here: a NativeMenu key equivalent is consumed by
                // the OS before Avalonia's input pipeline sees it, so declaring BOTH modifiers on both
                // surfaces would give macOS two live handlers for one keystroke and toggle it twice.
                Item("Grid Points",                vm.ToggleShowGridPointsCommand,
                     gesture: new KeyGesture(Key.L, KeyModifiers.Meta)),
                Sep(),
                Item("Power Sweep…", vm.PowerSweepCommand),
                Item("Set Z0…",      vm.SetZ0Command)),
        };
    }

    private static NativeMenuItem BuildGrid(HarmonicaMenuViewModel vm)
    {
        var gridPreset = new NativeMenuItem("Grid Preset")
        {
            Menu = MenuOf(
                Item("3 × 12", vm.SetGridPresetCommand, "3×12"),
                Item("5 × 12", vm.SetGridPresetCommand, "5×12"),
                Item("7 × 16", vm.SetGridPresetCommand, "7×16")),
        };

        return new NativeMenuItem("Grid")
        {
            Menu = MenuOf(
                gridPreset,
                Item("Reset Grid", vm.ResetGridCommand),
                Sep(),
                Item("Import .gam…", vm.ImportGamCommand),
                Item("Export .gam…", vm.ExportGamCommand)),
        };
    }

    private static void FillBands(NativeMenu target, IReadOnlyList<HarmonicaBandMenuItem> bands)
    {
        foreach (var band in bands)
        {
            var item = new NativeMenuItem(band.Header)
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked  = band.IsPresent,
                IsEnabled  = band.CanRemove,
            };
            var captured = band;
            item.Click += (_, _) => captured.IsPresent = !captured.IsPresent;
            target.Items.Add(item);
        }
    }

    private static NativeMenu MenuOf(params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    private static NativeMenuItem Item(string header, System.Windows.Input.ICommand? command,
        object? parameter = null, KeyGesture? gesture = null)
        => new(header) { Command = command, CommandParameter = parameter, Gesture = gesture };

    private static NativeMenuItemSeparator Sep() => new();
}
