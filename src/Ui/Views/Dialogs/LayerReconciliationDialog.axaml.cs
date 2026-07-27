using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Result of one layer-reconciliation prompt (docs/sonnet-briefs/brief-L1f-clipboard.md
/// R-L1f-3) — the chosen action for this layer, and whether that SAME action (and, for Map, the
/// same target) should be silently applied to every other absent layer in this paste without asking
/// again.</summary>
public readonly record struct LayerReconciliationDialogResult(
    LayoutFragment.LayerReconciliationChoice Choice, bool ApplyToAllRemaining);

/// <summary>
/// "Layer Not Found" prompt — shown once per distinct absent layer key during a paste (R-L1f-3),
/// never once per shape. Offers Keep-as-unknown (default) / Map to an existing destination layer /
/// Add to the technology (only when a technology actually resolved), plus an "Apply to all
/// remaining" checkbox. Mirrors <see cref="OffsetDialog"/>/<see cref="FlattenToPolygonDialog"/>'s
/// shape: a <see cref="Window"/> returning a typed result via <c>ShowDialog&lt;T&gt;</c>, or null on
/// cancel — cancelling here abandons the WHOLE paste (the caller treats a null result as "stop
/// prompting and paste nothing"), since partially reconciling a fragment would be more confusing
/// than not pasting at all.
/// </summary>
public partial class LayerReconciliationDialog : Window
{
    private readonly LayerKey _key;

    public LayerReconciliationDialog() => InitializeComponent();

    public LayerReconciliationDialog(LayoutEditorViewModel vm, LayerKey key, IReadOnlyList<LayerDef> fragmentLayers) : this()
    {
        _key = key;

        string sourceName = LayoutEditorViewModel.FragmentLayerDisplayName(key, fragmentLayers);
        HeaderText.Text = $"The pasted geometry uses layer {key.Layer}/{key.Datatype} (\"{sourceName}\"), " +
                           "which is not defined in the destination technology.";

        foreach (var layer in vm.AvailableLayers)
            MapTargetCombo.Items.Add(new ComboBoxItem { Content = layer.Name, Tag = layer.Key });
        if (MapTargetCombo.Items.Count > 0) MapTargetCombo.SelectedIndex = 0;

        // "Add to the technology" only makes sense once a technology has actually resolved — with
        // no technology at all, every layer already renders identically via FallbackPalette and
        // there is nothing to add to.
        AddRadio.IsVisible = vm.Technology is not null;
    }

    private void OnMapTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MapTargetCombo.SelectedItem is not null) MapRadio.IsChecked = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var action = ResolveReconciliationAction();
        LayerKey? target = MapTargetCombo.SelectedItem is ComboBoxItem { Tag: LayerKey k } ? k : null;

        var choice = new LayoutFragment.LayerReconciliationChoice(
            action, action == LayoutFragment.LayerReconciliationAction.MapToExisting ? target : null);

        Close(new LayerReconciliationDialogResult(choice, ApplyToAllCheck.IsChecked == true));
    }

    private LayoutFragment.LayerReconciliationAction ResolveReconciliationAction()
    {
        if (MapRadio.IsChecked == true) return LayoutFragment.LayerReconciliationAction.MapToExisting;
        if (AddRadio.IsChecked == true) return LayoutFragment.LayerReconciliationAction.AddToTechnology;
        return LayoutFragment.LayerReconciliationAction.KeepUnknown;
    }
}
