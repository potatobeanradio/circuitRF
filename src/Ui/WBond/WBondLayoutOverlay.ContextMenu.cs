using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The layout canvas's WIRE context menu (wbond.md §6.2/§6.3) — selection commands above the
/// separator, group in the middle, deletes below it.
///
/// <h3>Why it lives on the overlay rather than in a view</h3>
/// <para>WB39a: the wBond editor HOSTS <c>LayoutEditorView</c> instead of transcribing it, so there is
/// exactly one <c>ContextMenu</c> over the layout canvas and exactly one <c>Opening</c> handler — the
/// Layout Editor's own. A second menu declared by the wBond view would have to replace it, which is
/// how the shell got duplicated in the first place. The overlay is already the seam through which
/// everything wire-shaped reaches that canvas, so it is the seam the menu goes through too.</para>
///
/// <para><b>And it is what WB40 needs anyway.</b> Push into a wirebond cell in the ordinary Layout
/// Editor and its wires are there, with their own menu, because the overlay travelled with the cell —
/// not because that view learned anything about wires.</para>
///
/// <h3>Wires and layout geometry are two independent selections that can be held at once</h3>
/// <para>The wires live in <c>WBondViewModel.Selection</c> (flat indices into the design) and the
/// geometry lives in <see cref="LayoutEditorViewModel"/>'s own shape and instance sets. Neither clears
/// the other, so "select the pads and the wires landing on them" is one gesture and one selection —
/// which is what makes moving, copying and deleting them together mean anything.</para>
///
/// <h3>The deletes are click-target-scoped</h3>
/// <para>Delete Vertex and Delete Segment act on what the right-click actually LANDED on, resolved
/// through <see cref="WireHitTest.HitTestLayout"/> at the canvas's own hit tolerance — the same shape
/// the Layout Editor's edge/vertex/bitmap items already have. They are always present and disabled
/// with a reason when the click found nothing, never silently absent: an item that vanishes reads as
/// the feature being broken, and one that no-ops reads as the click having missed.</para>
/// </summary>
public sealed partial class WBondLayoutOverlay
{
    /// <inheritdoc/>
    public IReadOnlyList<object> BuildContextMenuItems(
        double worldX, double worldY, long tolDbu, LayoutEditorViewModel? layout, Visual host)
    {
        // At depth the wires are a locked reference (WB27) — every gesture there belongs to the layout
        // editor, and offering wire commands that cannot fire would be worse than offering none.
        if (IsAtDepth) return [];

        int wireCount = _vm.Design.WireCount;
        int selectedWires = _vm.Selection.TouchedWires().Count;
        bool hasLayout = layout is not null;

        var selectAll = new MenuItem { Header = "Select All", IsEnabled = wireCount > 0 || hasLayout };
        selectAll.Click += (_, _) =>
        {
            // The geometry half goes through the layout editor's OWN SelectAllCommand rather than a
            // reimplementation, so it keeps picking up whatever that command decides belongs in a
            // select-all (instances and PCells included — a lesson that command already learned once).
            _vm.SelectAllWires();
            layout?.SelectAllCommand.Execute(null);
            OverlayChanged?.Invoke();
        };

        var selectWires = new MenuItem { Header = "Select All Wires", IsEnabled = wireCount > 0 };
        selectWires.Click += (_, _) => { _vm.SelectAllWires(); OverlayChanged?.Invoke(); };

        var invertWires = new MenuItem { Header = "Invert Wire Selection", IsEnabled = wireCount > 0 };
        invertWires.Click += (_, _) => { _vm.InvertWireSelection(); OverlayChanged?.Invoke(); };

        var deselect = new MenuItem { Header = "Deselect All", IsEnabled = selectedWires > 0 || hasLayout };
        deselect.Click += (_, _) =>
        {
            _vm.ClearSelection();
            layout?.DeselectAllCommand.Execute(null);
            OverlayChanged?.Invoke();
        };

        var items = new List<object>
        {
            selectAll,
            selectWires,
            invertWires,
            deselect,
            new Separator(),
        };

        items.Add(BuildGroupItem(host));
        items.Add(new Separator());
        items.AddRange(BuildDeleteItems(worldX, worldY, tolDbu));

        return items;
    }

    /// <summary>
    /// "Group Wires As…" — moves the whole wire selection into one group (owner, 2026-08-16).
    ///
    /// <para>Selection-scoped, not click-scoped, and that is the point: regrouping is normally done to
    /// a marquee-full of wires at once. Disabled with its reason when nothing is selected rather than
    /// acting on whatever the pointer happens to be over — a group change that quietly applied to one
    /// wire when the user meant forty is expensive to notice.</para>
    /// </summary>
    private MenuItem BuildGroupItem(Visual host)
    {
        var touched = _vm.Selection.TouchedWires();

        var item = new MenuItem
        {
            Header = WBondGroupCommand.Label(touched.Count),
            IsEnabled = touched.Count > 0,
        };

        if (touched.Count == 0) ToolTip.SetTip(item, "Select the wires to regroup first.");
        else item.Click += async (_, _) => await GroupSelectedWiresAsync(host);

        return item;
    }

    /// <summary>
    /// Opens the group picker on the current wire selection and applies what comes back — the SAME
    /// shared command the wBond Properties panel's own Regroup button runs, so two routes to
    /// "regroup these wires" cannot get the batch-undo or the re-pointed selection differently right.
    /// </summary>
    private async Task GroupSelectedWiresAsync(Visual host)
    {
        int moved = await WBondGroupCommand.RunAsync(TopLevel.GetTopLevel(host) as Window, _vm);
        if (moved > 0) OverlayChanged?.Invoke();
    }

    /// <summary>
    /// Delete Vertex, Delete Segment, Delete Wire — in that order (owner), acting on whatever the
    /// right-click landed on.
    /// </summary>
    private List<object> BuildDeleteItems(double worldX, double worldY, long tolDbu)
    {
        var hit = HitWireAt(worldX, worldY, tolDbu);

        MenuItem Item(string header, string? disabledReason, Action action)
        {
            var mi = new MenuItem { Header = header, IsEnabled = disabledReason is null };
            if (disabledReason is { } reason) ToolTip.SetTip(mi, reason);
            else mi.Click += (_, _) => { action(); OverlayChanged?.Invoke(); };
            return mi;
        }

        // A hit on a SEGMENT names the segment; a hit on a POINT names the point. WireHitTest reports
        // which, so the two items are never both live on the same click — the one that does not match
        // says why rather than acting on a neighbour the user did not point at.
        string? vertexWhy =
            !hit.Found      ? "Right-click a wire vertex."
            : hit.IsSegment ? "That is a segment, not a vertex."
            : _vm.WhyCannotDeletePoint(hit.Wire, hit.Point);

        string? segmentWhy =
            !hit.Found       ? "Right-click a wire segment."
            : !hit.IsSegment ? "That is a vertex, not a segment."
            : _vm.WhyCannotDeleteSegment(hit.Wire, hit.Point);

        string? wireWhy = hit.Found ? _vm.WhyCannotDeleteWire(hit.Wire) : "Right-click a wire.";

        return
        [
            Item("Delete Vertex",  vertexWhy,  () => _vm.DeleteWirePoint(hit.Wire, hit.Point)),
            Item("Delete Segment", segmentWhy, () => _vm.DeleteWireSegment(hit.Wire, hit.Point)),
            Item("Delete Wire",    wireWhy,    () => _vm.DeleteWire(hit.Wire)),
        ];
    }

    /// <summary>
    /// What a right-click at layout world coordinates landed on.
    ///
    /// <para>The canvas works in the layout's database units and a wire point is stored in nanometres;
    /// the two coincide only at the 1,000 DBU/µm default, which is why <see cref="WBondSnap"/> is
    /// crossed explicitly here rather than assumed.</para>
    /// </summary>
    private WireHitTest.Hit HitWireAt(double worldX, double worldY, long tolDbu)
    {
        long xNm = WBondSnap.ToNm((long)worldX, DbuPerMicron);
        long yNm = WBondSnap.ToNm((long)worldY, DbuPerMicron);
        double tolNm = WBondSnap.ToNm(tolDbu, DbuPerMicron);

        return WireHitTest.HitTestLayout(_vm.Mesh, xNm, yNm, tolNm);
    }
}
