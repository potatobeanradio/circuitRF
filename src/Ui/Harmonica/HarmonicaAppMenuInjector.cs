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

using System.Collections.Generic;
using Avalonia.Controls;

namespace CircuitRF.Ui.Harmonica;

public static class HarmonicaAppMenuInjector
{
    /// <summary>Builds fresh Markers / Display / Grid top-level items from the view model's own
    /// collections and commands. Each call returns brand-new <see cref="NativeMenuItem"/> instances —
    /// never <c>_ownMenu</c>'s own children, which <see cref="NativeMenu"/>'s list validator refuses
    /// to accept a second time (an item that already has a <c>Parent</c> throws
    /// <see cref="System.InvalidOperationException"/>).</summary>
    public static IReadOnlyList<NativeMenuItem> BuildTopLevelItems(HarmonicaMenuViewModel vm)
        => [BuildMarkers(vm), BuildDisplay(vm), BuildGrid(vm)];

    /// <summary>Appends <paramref name="items"/> to <paramref name="appMenu"/>'s own <c>Items</c>.</summary>
    public static void Inject(NativeMenu appMenu, IReadOnlyList<NativeMenuItem> items)
    {
        foreach (var item in items) appMenu.Items.Add(item);
    }

    /// <summary>Removes exactly <paramref name="items"/> from <paramref name="appMenu"/> — by
    /// reference, never by header match or index, so a withdrawal can never remove something
    /// circuitRF's own bar (or the user) added in between.</summary>
    public static void Withdraw(NativeMenu appMenu, IReadOnlyList<NativeMenuItem> items)
    {
        foreach (var item in items) appMenu.Items.Remove(item);
    }

    // ── builders — mirrors HarmonicaMenuView.axaml's <NativeMenu.Menu> block, Markers/Display/Grid
    //    only (File/Edit/Help duplicate what circuitRF's own bar already shows) ──────────────────────

    private static NativeMenuItem BuildMarkers(HarmonicaMenuViewModel vm)
    {
        var sourceBands = new NativeMenuItem("Source Bands") { Menu = new NativeMenu() };
        var loadBands   = new NativeMenuItem("Load Bands")   { Menu = new NativeMenu() };
        FillBands(sourceBands.Menu!, vm.SourceBands);
        FillBands(loadBands.Menu!,   vm.LoadBands);

        return new NativeMenuItem("Markers")
        {
            Menu = MenuOf(
                sourceBands,
                loadBands,
                Sep(),
                Item("Reset to Defaults", vm.ResetMarkersCommand)),
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
        var contourHarmonic = new NativeMenuItem("Contour Harmonic")
        {
            Menu = MenuOf(
                Item("f₀",  vm.SetGridHarmonicCommand, "1"),
                Item("2f₀", vm.SetGridHarmonicCommand, "2"),
                Item("3f₀", vm.SetGridHarmonicCommand, "3")),
        };
        var efficiencyMetric = new NativeMenuItem("Efficiency Metric")
        {
            Menu = MenuOf(
                Item("DE",  vm.SetEfficiencyMetricCommand, "DE"),
                Item("PAE", vm.SetEfficiencyMetricCommand, "PAE")),
        };
        var contourLevels = new NativeMenuItem("Contour Levels")
        {
            Menu = MenuOf(
                Item("5",  vm.SetContourLevelsCommand, "5"),
                Item("10", vm.SetContourLevelsCommand, "10"),
                Item("20", vm.SetContourLevelsCommand, "20")),
        };

        return new NativeMenuItem("Display")
        {
            Menu = MenuOf(
                Item("Edit Display",      vm.ToggleEditDisplayCommand),
                Item("Add Trace…",        vm.AddTraceCommand),
                Item("Remove All Traces", vm.RemoveAllTracesCommand),
                Sep(),
                contourPlane,
                contourHarmonic,
                efficiencyMetric,
                Item("Loadline Plane", vm.ToggleLoadlinePlaneCommand),
                contourLevels,
                Item("Iso-line Labels",            vm.ToggleIsoLineLabelsCommand),
                Item("Grid Points",                vm.ToggleShowGridPointsCommand),
                Item("Cursor Snap to Compression",  vm.ToggleCursorSnapCommand),
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
                Item("Solve Now", vm.SolveNowCommand),
                Sep(),
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

    private static NativeMenuItem Item(string header, System.Windows.Input.ICommand? command, object? parameter = null)
        => new(header) { Command = command, CommandParameter = parameter };

    private static NativeMenuItemSeparator Sep() => new();
}
