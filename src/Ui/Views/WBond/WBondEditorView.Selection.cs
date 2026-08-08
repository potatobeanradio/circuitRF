using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// Selection commands over the layout view — wires, geometry, and both together (wbond.md §6.2).
///
/// <para><b>Wires and layout geometry are two independent selections that can be held at once.</b>
/// The wires live in <c>WBondViewModel.Selection</c> (flat indices into the design) and the geometry
/// lives in <c>LayoutEditorViewModel</c>'s own shape and instance sets. Neither clears the other, so
/// "select the pads and the wires landing on them" is one gesture and one selection — which is what
/// makes moving, copying and deleting them together mean anything.</para>
/// </summary>
public partial class WBondEditorView
{
    private void OnWireSelectionMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || _bound is null) { e.Cancel = true; return; }

        int wireCount = _bound.Editor.Design.WireCount;
        int selectedWires = _bound.Editor.Selection.TouchedWires().Count;
        bool hasLayout = _bound.ReferenceLayout is not null;

        var selectAll = new MenuItem
        {
            Header = "Select All",
            IsEnabled = wireCount > 0 || hasLayout,
        };
        selectAll.Click += (_, _) => SelectAllIncludingWires();

        var selectWires = new MenuItem
        {
            Header = "Select All Wires",
            IsEnabled = wireCount > 0,
        };
        selectWires.Click += (_, _) => { _bound.Editor.SelectAllWires(); RepaintBoth(); };

        var invertWires = new MenuItem
        {
            Header = "Invert Wire Selection",
            IsEnabled = wireCount > 0,
        };
        invertWires.Click += (_, _) => { _bound.Editor.InvertWireSelection(); RepaintBoth(); };

        var deselect = new MenuItem
        {
            Header = "Deselect All",
            IsEnabled = selectedWires > 0 || hasLayout,
        };
        deselect.Click += (_, _) =>
        {
            _bound.Editor.ClearSelection();
            _bound.ReferenceLayout?.DeselectAllCommand.Execute(null);
            RepaintBoth();
        };

        menu.ItemsSource = new List<object>
        {
            selectAll,
            selectWires,
            invertWires,
            new Separator(),
            deselect,
        };
    }

    /// <summary>
    /// Select All means everything selectable in this editor — every wire AND every piece of layout
    /// geometry — because the two are one design as far as the user is concerned.
    ///
    /// <para>The geometry half goes through the layout editor's OWN <c>SelectAllCommand</c> rather
    /// than a reimplementation, so it keeps picking up whatever that command decides belongs in a
    /// select-all (instances and PCells included — a lesson that command already learned once).</para>
    /// </summary>
    internal void SelectAllIncludingWires()
    {
        if (_bound is null) return;

        _bound.Editor.SelectAllWires();
        _bound.ReferenceLayout?.SelectAllCommand.Execute(null);
        RepaintBoth();
    }
}
