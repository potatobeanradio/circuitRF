using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The layout view's wire context menu (wbond.md §6.2/§6.3) — selection commands above the
/// separator, deletes below it.
///
/// <para><b>Wires and layout geometry are two independent selections that can be held at once.</b>
/// The wires live in <c>WBondViewModel.Selection</c> (flat indices into the design) and the geometry
/// lives in <c>LayoutEditorViewModel</c>'s own shape and instance sets. Neither clears the other, so
/// "select the pads and the wires landing on them" is one gesture and one selection — which is what
/// makes moving, copying and deleting them together mean anything.</para>
///
/// <h3>The deletes are click-target-scoped</h3>
/// <para>Delete Vertex and Delete Segment act on what the right-click actually LANDED on, resolved
/// through <see cref="WireHitTest.HitTestLayout"/> at the canvas's own hit tolerance — the same
/// shape the Layout Editor's edge/vertex/bitmap items already have. They are always present and
/// disabled with a reason when the click found nothing, never silently absent: an item that vanishes
/// reads as the feature being broken, and one that no-ops reads as the click having missed.</para>
/// </summary>
public partial class WBondEditorView
{
    private void OnWireSelectionMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || _bound is null) { e.Cancel = true; return; }

        // Consumed once per opening, and a null target cancels — matching LayoutCanvas's own contract
        // (it deliberately clears the target for the macOS Control+click case, which is the
        // insert-vertex gesture and must not pop a menu).
        if (LayoutCanvasCtrl.ConsumeContextMenuTarget() is not { } target) { e.Cancel = true; return; }

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

        var items = new List<object>
        {
            selectAll,
            selectWires,
            invertWires,
            deselect,
            new Separator(),
        };

        items.AddRange(BuildGroupItems());
        items.Add(new Separator());
        items.AddRange(BuildDeleteItems(target.Wx, target.Wy));

        menu.ItemsSource = items;
    }

    /// <summary>
    /// "Group Wires As…" — moves the whole wire selection into one group (owner, 2026-08-16).
    ///
    /// <para>Selection-scoped, not click-scoped, and that is the point: regrouping is normally done
    /// to a marquee-full of wires at once. Disabled with its reason when nothing is selected rather
    /// than acting on whatever the pointer happens to be over — a group change that quietly applied
    /// to one wire when the user meant forty is expensive to notice.</para>
    /// </summary>
    private List<object> BuildGroupItems()
    {
        var touched = _bound!.Editor.Selection.TouchedWires();

        var item = new MenuItem
        {
            Header = WBondGroupCommand.Label(touched.Count),
            IsEnabled = touched.Count > 0,
        };

        if (touched.Count == 0) ToolTip.SetTip(item, "Select the wires to regroup first.");
        else item.Click += async (_, _) => await GroupSelectedWiresAsync();

        return [item];
    }

    /// <summary>
    /// Delete Vertex, Delete Segment, Delete Wire — in that order (owner), acting on whatever the
    /// right-click landed on.
    /// </summary>
    private List<object> BuildDeleteItems(double wx, double wy)
    {
        var hit = HitWireAt(wx, wy);
        var vm = _bound!.Editor;

        MenuItem Item(string header, string? disabledReason, System.Action action)
        {
            var mi = new MenuItem { Header = header, IsEnabled = disabledReason is null };
            if (disabledReason is { } reason) ToolTip.SetTip(mi, reason);
            else mi.Click += (_, _) => { action(); RepaintBoth(); };
            return mi;
        }

        // A hit on a SEGMENT names the segment; a hit on a POINT names the point. WireHitTest reports
        // which, so the two items are never both live on the same click — the one that does not match
        // says why rather than acting on a neighbour the user did not point at.
        string? vertexWhy =
            !hit.Found         ? "Right-click a wire vertex."
            : hit.IsSegment    ? "That is a segment, not a vertex."
            : vm.WhyCannotDeletePoint(hit.Wire, hit.Point);

        string? segmentWhy =
            !hit.Found         ? "Right-click a wire segment."
            : !hit.IsSegment   ? "That is a vertex, not a segment."
            : vm.WhyCannotDeleteSegment(hit.Wire, hit.Point);

        string? wireWhy = hit.Found ? vm.WhyCannotDeleteWire(hit.Wire) : "Right-click a wire.";

        return
        [
            Item("Delete Vertex",  vertexWhy,  () => vm.DeleteWirePoint(hit.Wire, hit.Point)),
            Item("Delete Segment", segmentWhy, () => vm.DeleteWireSegment(hit.Wire, hit.Point)),
            Item("Delete Wire",    wireWhy,    () => vm.DeleteWire(hit.Wire)),
        ];
    }

    /// <summary>
    /// What a right-click at layout world coordinates <paramref name="wx"/>/<paramref name="wy"/>
    /// landed on.
    ///
    /// <para>The canvas works in the layout's database units and a wire point is stored in
    /// nanometres; the two coincide only at the 1,000 DBU/µm default, which is why
    /// <see cref="WBondSnap"/> is crossed explicitly here rather than assumed.</para>
    /// </summary>
    private WireHitTest.Hit HitWireAt(double wx, double wy)
    {
        if (_bound is null) return WireHitTest.Hit.None;

        int dbuPerMicron = _bound.ReferenceLayout?.Model.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;

        long xNm = WBondSnap.ToNm((long)wx, dbuPerMicron);
        long yNm = WBondSnap.ToNm((long)wy, dbuPerMicron);
        double tolNm = WBondSnap.ToNm(LayoutCanvasCtrl.ContextMenuHitTolDbu, dbuPerMicron);

        return WireHitTest.HitTestLayout(_bound.Editor.Mesh, xNm, yNm, tolNm);
    }

    /// <summary>
    /// Opens the group picker on the current wire selection and applies what comes back.
    ///
    /// <para><b>Internal</b> so the wBond Properties panel reaches the same command — the owner asked
    /// for both routes, and two implementations of "regroup these wires" would be two chances to get
    /// the batch-undo and the re-pointed selection wrong.</para>
    /// </summary>
    /// <returns>How many wires changed group.</returns>
    internal async Task<int> GroupSelectedWiresAsync()
    {
        int moved = await WBondGroupCommand.RunAsync(
            TopLevel.GetTopLevel(this) as Window, _bound?.Editor);

        if (moved > 0) RepaintBoth();
        return moved;
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
