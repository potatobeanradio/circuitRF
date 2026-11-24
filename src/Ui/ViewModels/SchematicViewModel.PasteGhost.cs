// Paste follows the pointer — brief DFR26-5 R-dfr26-5-1.
//
// A paste used to land at once, one connection-grid step off its original when the original was in view, which put the
// pasted pins straight onto the original's wires and pins and shorted the nets (reported twice). With the pointer over
// the canvas, a paste now ARMS a ghost of the fragment instead: it follows the pointer snapped to the connection grid,
// a click places it there, and Escape (or any tool change) drops it with the sheet unchanged — the layout editor's
// pattern (LayoutEditorViewModel.Clipboard.cs, _pastePlacementInstances). Nothing is in the model while it floats, so
// nothing can connect to it. The placed result keeps SchematicPasteGeometry's guarantees: the delta is a whole number
// of grid steps, and the overlap nudge still runs. Duplicate (Ctrl+D) keeps its own fixed offset and does not come here.

using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class SchematicViewModel
{
    /// <summary>A paste waiting for its click: the fragment at its source coordinates, the point of it that rides the
    /// pointer, and the ghost drawn for it — resolved once at arming, since the fragment does not change while it
    /// floats.</summary>
    private sealed record PendingPaste(
        List<EditableComponent> Comps,
        List<EditableWire> Wires,
        List<EditableCanvasObject> Cobjs,
        double SourceGridSize,
        IReadOnlyList<EditableNetLabel>? NetLabels,
        (double X, double Y) Anchor,
        List<PlacementGhost> Ghosts,
        IReadOnlyList<Core.Design.Analysis>? Analyses = null);

    private PendingPaste? _pendingPaste;
    private (double Dx, double Dy) _pasteGhostDelta;

    /// <summary>The pointer's world position while it is over the canvas, else null. Set by SchematicCanvas; read by
    /// paste to decide between arming a ghost at the pointer and landing in the view.</summary>
    public Func<(double X, double Y)?>? PointerWorldProvider { get; set; }

    /// <summary>True while a pasted fragment follows the pointer, waiting for the click that places it.</summary>
    public bool IsPasteArmed => _pendingPaste is not null;

    /// <summary>Arms <paramref name="comps"/>/<paramref name="wires"/>/<paramref name="cobjs"/> as a ghost at
    /// (<paramref name="wx"/>, <paramref name="wy"/>).</summary>
    private void ArmPasteGhost(
        List<EditableComponent> comps, List<EditableWire> wires, List<EditableCanvasObject> cobjs,
        double sourceGridSize, IReadOnlyList<EditableNetLabel>? netLabels, double wx, double wy,
        IReadOnlyList<Core.Design.Analysis>? analyses = null)
    {
        if (SchematicPasteGeometry.BoundingBox(comps, wires, cobjs) is not { } bb) return;
        SetSelectTool();   // whatever was in progress ends; the ghost is the only thing in flight

        double p = PasteGrid;
        var anchor = (Math.Round((bb.MinX + bb.MaxX) / 2.0 / p) * p, Math.Round((bb.MinY + bb.MaxY) / 2.0 / p) * p);
        var ghosts = comps.Select(c =>
        {
            var res = c.ExternalSymbolRef is { } symRef
                      && (EditModel.SchematicDirectory is not null || CellSymbolResolver.NeedsNoBaseDirectory(symRef))
                ? CellSymbolResolver.Resolve(symRef, EditModel.SchematicDirectory)
                : null;
            var rc = c.ToRenderComponent(cellRefResolution: res);
            return new PlacementGhost(c.X, c.Y, rc.Symbol, rc.Rotation, rc.MirrorX, rc.Ports.Count,
                                      rc.InstanceSymbol?.Primitives ?? rc.CellRefPrimitives, rc.InstanceSymbol?.Pins);
        }).ToList();

        _pendingPaste = new PendingPaste(comps, wires, cobjs, sourceGridSize, netLabels, anchor, ghosts,
                                         analyses is { Count: > 0 } ? analyses : null);
        MovePasteGhost(wx, wy);
    }

    /// <summary>
    /// Raised when something outside the schematic window hands it a fragment to place (the Filter
    /// Designer's Place filter / Place component): the host brings this schematic's window forward
    /// so the ghost is under the pointer the user is about to move.
    /// </summary>
    public event Action? PlacementActivationRequested;

    /// <summary>
    /// Arms <paramref name="fragment"/> as a paste ghost — the one placement path, shared with
    /// Ctrl+V: it follows the pointer on the connection grid, a click places it as one undoable
    /// paste (instances renamed to be unique, the overlap nudge, the selection), and Escape drops it.
    /// The fragment's objects are taken over; pass a model built for the purpose. The fragment's
    /// analyses, when it has any, are added to this sheet in the same undo step as the click that
    /// places it (the LC Filter Designer's ported ladder brings the S-parameter sweep it was designed
    /// over); Escape drops them with the ghost.
    /// </summary>
    /// <remarks>
    /// Armed at the pointer when it is over the canvas, otherwise at the centre of the view, where
    /// it waits until the pointer arrives — the request comes from another window, so the pointer
    /// is usually not over this one yet.
    /// </remarks>
    /// <returns>False when the fragment is empty and nothing was armed.</returns>
    public bool ArmFragmentPlacement(SchematicEditModel fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var comps = fragment.Components.ToList();
        var wires = fragment.Wires.ToList();
        var cobjs = fragment.CanvasObjects.ToList();
        if (comps.Count == 0 && wires.Count == 0 && cobjs.Count == 0) return false;
        var labels = fragment.NetLabels.ToList();

        (double X, double Y) at;
        if (PointerWorldProvider?.Invoke() is { } pointer) at = pointer;
        else if (ViewportProvider?.Invoke() is { } view) at = ((view.MinX + view.MaxX) / 2, (view.MinY + view.MaxY) / 2);
        else at = (0, 0);

        ArmPasteGhost(comps, wires, cobjs, fragment.GridSize > 0 ? fragment.GridSize : EditModel.GridSize,
                      labels.Count > 0 ? labels : null, at.X, at.Y, fragment.Analyses.ToList());
        if (!IsPasteArmed) return false;
        PlacementActivationRequested?.Invoke();
        return true;
    }

    /// <summary>
    /// The Filter the context menu's Synthesise… opens the LC Filter Designer on: the component
    /// <paramref name="id"/> names when it is a Filter, else null — which hides the item.
    /// </summary>
    public EditableComponent? SynthesizeTarget(string? id) =>
        id is not null && EditModel.FindComponent(id) is { Symbol: SymbolKind.Filter } c ? c : null;

    private double PasteGrid => EditModel.GridSize > 0 ? EditModel.GridSize : 100.0;

    /// <summary>The ghost follows the pointer, its anchor snapped onto the connection grid.</summary>
    private void MovePasteGhost(double wx, double wy)
    {
        if (_pendingPaste is not { } pp) return;
        double p = PasteGrid;
        _pasteGhostDelta = (Math.Round(wx / p) * p - pp.Anchor.X, Math.Round(wy / p) * p - pp.Anchor.Y);
        var (ghosts, wires, rects) = PasteGhostOverlay();
        Overlay = Overlay with { DuplicateGhosts = ghosts, DuplicateGhostWires = wires, DuplicateGhostRects = rects };
    }

    /// <summary>The armed paste's ghost at its current delta, in the duplicate drag's overlay slots; all null when
    /// nothing is armed.</summary>
    private (List<PlacementGhost>? Ghosts,
             List<IReadOnlyList<(double X, double Y)>>? Wires,
             List<(double X, double Y, double W, double H)>? Rects) PasteGhostOverlay()
    {
        if (_pendingPaste is not { } pp) return (null, null, null);
        var (dx, dy) = _pasteGhostDelta;
        return (
            pp.Ghosts.Select(g => g with { X = g.X + dx, Y = g.Y + dy }).ToList(),
            pp.Wires.Select(w => (IReadOnlyList<(double X, double Y)>)w.Points.Select(pt => (pt.X + dx, pt.Y + dy)).ToList()).ToList(),
            pp.Cobjs.Select(o =>
            {
                var b = o.GetBoundingBox();
                return (b.MinX + dx, b.MinY + dy, b.MaxX - b.MinX, b.MaxY - b.MinY);
            }).ToList());
    }

    /// <summary>The click: the fragment lands at the ghost's position (nudged off an exact overlap) as one undoable
    /// paste, selected — and, for a single part, in series with a wire it was dropped along.</summary>
    private void PlacePasteGhost(double wx, double wy)
    {
        if (_pendingPaste is not { } pp) return;
        MovePasteGhost(wx, wy);
        var (dx, dy) = SchematicPasteGeometry.Nudge(pp.Comps, _pasteGhostDelta.Dx, _pasteGhostDelta.Dy, PasteGrid,
                                                    OccupiedByComponent);
        ClearPasteGhost();
        CommitPaste(pp.Comps, pp.Wires, pp.Cobjs, pp.SourceGridSize, pp.NetLabels, dx, dy, pp.Analyses);
    }

    /// <summary>Drops the armed paste, if any, leaving the sheet exactly as it was.</summary>
    private void ClearPasteGhost()
    {
        if (_pendingPaste is null) return;
        _pendingPaste = null;
        _pasteGhostDelta = (0, 0);
        Overlay = Overlay with { DuplicateGhosts = null, DuplicateGhostWires = null, DuplicateGhostRects = null };
    }

    private bool OccupiedByComponent(double x, double y)
        => EditModel.Components.Any(c => Math.Abs(c.X - x) < 1e-6 && Math.Abs(c.Y - y) < 1e-6);

    private void CommitPaste(
        List<EditableComponent> comps, List<EditableWire> wires, List<EditableCanvasObject> cobjs,
        double sourceGridSize, IReadOnlyList<EditableNetLabel>? netLabels, double dx, double dy,
        IReadOnlyList<Core.Design.Analysis>? analyses = null)
    {
        SchematicPasteGeometry.Translate(comps, wires, cobjs, dx, dy, netLabels);
        var paste = WithSeriesWireCutsForOne(new SchematicPasteCommand(
            EditModel, comps, wires, cobjs,
            ids => Selection.SetAll(ids),
            sourceGridSize: sourceGridSize,
            messageSink: _messageSink,
            netLabels: netLabels), comps, wires, cobjs);
        // The analyses first, so the one undo step is named for the paste.
        Execute(analyses is { Count: > 0 }
            ? new Commands.CompositeCommand(new Commands.Analysis.AddAnalysesCommand(EditModel, analyses), paste)
            : paste);
    }
}
