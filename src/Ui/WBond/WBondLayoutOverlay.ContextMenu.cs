using System;
using System.Collections.Generic;
using System.Linq;
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

        // A right-click means no drag is running. Any that is still open was STRANDED — its release
        // went somewhere else — and a stranded drag can leave a wire collapsed to its two feet, which
        // is exactly what made "Straighten Wire" report that there was nothing between them (owner,
        // 2026-08-17). Unwinding here is what makes the FIRST opening of the menu describe the wire
        // the user is looking at rather than the one the abandoned gesture left behind.
        AbandonGesture();

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

        items.Add(BuildGroupItem(host, worldX, worldY, tolDbu));

        // Add Vertex sits between the group commands and the deletes, with a separator of its own
        // above it (owner, 2026-08-17): it is the one command here that ADDS something, and grouping
        // it with the three deletes below would read as a fourth delete.
        items.Add(new Separator());
        items.Add(BuildAddVertexItem(worldX, worldY, tolDbu));

        items.Add(new Separator());
        items.AddRange(BuildDeleteItems(worldX, worldY, tolDbu));

        // Straighten Wire LAST, so it lands above the canvas's own Rotate 90° items (owner) — the
        // wire commands stay one block and the layout's own follow after the canvas's separator.
        items.Add(new Separator());
        items.Add(BuildStraightenItem(worldX, worldY, tolDbu));

        return items;
    }

    /// <summary>
    /// "Group Wires As…" / "Group Wire As…" — moves wires into one group (owner, 2026-08-16).
    ///
    /// <para><b>The SELECTION when there is a multi-selection, the clicked wire otherwise</b> (owner,
    /// 2026-08-18: <i>"If I click on wire in layout host, the context menu 'Group Wires As…' is
    /// disabled. For a single wire right-click, this menu should be available."</i>). This is the same
    /// rule <see cref="BuildStraightenItem"/> already follows, and the two are now consistent — they
    /// were not, which is what made a right-click on a wire offer one of them and not the other.</para>
    ///
    /// <para>It used to be selection-scoped only, defended as <i>"a group change that quietly applied
    /// to one wire when the user meant forty is expensive to notice"</i>. That risk is answered by the
    /// rule rather than by the refusal: a MULTI-selection always wins, so a right-click can never
    /// shrink a forty-wire subject down to one. What it could not do before was act on the wire the
    /// user was actually pointing at, and the label says which it is — <c>Group Wire As…</c> for one,
    /// <c>Group N Wires As…</c> for many.</para>
    /// </summary>
    private MenuItem BuildGroupItem(Visual host, double worldX, double worldY, long tolDbu)
    {
        var selected = _vm.Selection.TouchedWires();
        var hit = HitWireAt(worldX, worldY, tolDbu);

        // A single selection does NOT win over the click, for the same reason it does not in
        // Straighten: a right-click on a DIFFERENT wire must never act on the one selected wire the
        // user was not pointing at.
        IReadOnlyCollection<int> targets =
            selected.Count > 1 ? [.. selected]
            : hit.Found        ? [hit.Wire]
            : [];

        var item = new MenuItem
        {
            Header = WBondGroupCommand.Label(targets.Count),
            IsEnabled = targets.Count > 0,
        };

        if (targets.Count == 0) ToolTip.SetTip(item, "Right-click a wire, or select the wires to regroup.");
        else item.Click += async (_, _) => await GroupSelectedWiresAsync(host, targets);

        return item;
    }

    /// <summary>
    /// Opens the group picker on the current wire selection and applies what comes back — the SAME
    /// shared command the wBond Properties panel's own Regroup button runs, so two routes to
    /// "regroup these wires" cannot get the batch-undo or the re-pointed selection differently right.
    /// </summary>
    private async Task GroupSelectedWiresAsync(Visual host, IReadOnlyCollection<int> targets)
    {
        int moved = await WBondGroupCommand.RunAsync(TopLevel.GetTopLevel(host) as Window, _vm, targets);
        if (moved > 0) OverlayChanged?.Invoke();
    }

    /// <summary>
    /// <b>Add Vertex</b> — a new point on the wire nearest the right-click, collinear with its two
    /// neighbours and at their interpolated z (owner, 2026-08-17), so the insert changes the wire's
    /// shape not at all and only gives the user a handle where there was none.
    ///
    /// <para>Click-target-scoped like the deletes below it. A click that landed on a VERTEX rather
    /// than a segment still works — it inserts into the segment starting there (or the one before it
    /// at the far foot), because "add a vertex here" has an obvious meaning at a vertex too and
    /// refusing it would read as the command being broken.</para>
    /// </summary>
    private MenuItem BuildAddVertexItem(double worldX, double worldY, long tolDbu)
    {
        var hit = HitWireAt(worldX, worldY, tolDbu);

        var item = new MenuItem { Header = "Add Vertex", IsEnabled = hit.Found };
        if (!hit.Found)
        {
            ToolTip.SetTip(item, "Right-click a wire.");
            return item;
        }

        item.Click += (_, _) =>
        {
            if (ResolveInsertion(hit, worldX, worldY) is not { } insert) return;
            _vm.AddWirePoint(insert.Wire, insert.Segment, insert.T);
            OverlayChanged?.Invoke();
        };

        return item;
    }

    /// <summary>
    /// Which segment a click means, and where along it — resolved in the LAYOUT's own XY plane.
    ///
    /// <para>A hit on a segment names it outright. A hit on a vertex names the segment that STARTS
    /// there, except at the last point where there is none and the segment before it is meant. The
    /// parameter is the click's projection onto that segment, so the vertex lands under the pointer
    /// rather than at a fixed fraction the user did not choose.</para>
    /// </summary>
    private (int Wire, int Segment, double T)? ResolveInsertion(WireHitTest.Hit hit, double worldX, double worldY)
    {
        var wire = _vm.Design.AllWires().ElementAtOrDefault(hit.Wire);
        if (wire is null || wire.Points.Count < 2) return null;

        int segment = hit.IsSegment ? hit.Point : Math.Min(hit.Point, wire.Points.Count - 2);
        if (segment < 0 || segment >= wire.Points.Count - 1) return null;

        long xNm = WBondSnap.ToNm((long)worldX, DbuPerMicron);
        long yNm = WBondSnap.ToNm((long)worldY, DbuPerMicron);

        var a = wire.Points[segment];
        var b = wire.Points[segment + 1];

        return (hit.Wire, segment, WireEdits.SegmentParameter(a.X, a.Y, b.X, b.Y, xNm, yNm));
    }

    /// <summary>
    /// <b>Straighten Wire / Straighten Wires</b> — every interior point onto the straight line between
    /// its own two feet, in XY only, loop height untouched (owner, 2026-08-17).
    ///
    /// <para><b>The SELECTION when there is a multi-selection, the clicked wire otherwise</b> (owner:
    /// <i>"I want both to work; if the user has multiple wires selected, then all those wires are
    /// straightened"</i>). It is the only item here that reads the selection at all — and only when
    /// there is genuinely more than one wire in it, so a single selection can never make a right-click
    /// on a DIFFERENT wire act somewhere the user is not pointing. Each wire straightens about its own
    /// anchors; see <c>WBondViewModel.StraightenWires</c> for why a shared chord would be wrong.</para>
    ///
    /// <para>Layout-only: this is a statement about the wire's PATH ACROSS THE BOARD, and the profile
    /// view has no XY plane to make it in — its horizontal axis is position along that path.</para>
    /// </summary>
    private MenuItem BuildStraightenItem(double worldX, double worldY, long tolDbu)
    {
        var selected = _vm.Selection.TouchedWires();

        // A multi-selection is the subject; otherwise it is whatever the click landed on — which may
        // be nothing, and then the item says so rather than acting on the one selected wire the user
        // was not pointing at.
        var hit = HitWireAt(worldX, worldY, tolDbu);
        IReadOnlyCollection<int> targets =
            selected.Count > 1 ? [.. selected]
            : hit.Found        ? [hit.Wire]
            : [];

        string? why = _vm.WhyCannotStraighten(targets);

        var item = new MenuItem
        {
            Header = targets.Count > 1 ? "Straighten Wires" : "Straighten Wire",
            IsEnabled = why is null,
        };

        if (why is { } reason) ToolTip.SetTip(item, reason);
        else item.Click += (_, _) => { _vm.StraightenWires(targets); OverlayChanged?.Invoke(); };

        return item;
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
