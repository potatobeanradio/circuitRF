using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Translates a set of <see cref="LayoutShape"/>s by <c>(Dx, Dy)</c> — mirrors
/// <c>MoveSymbolPrimitivesCommand</c>. Execute applies the delta; Undo applies the negated delta.
///
/// <b>R-L1c-3 — snap the delta, never the resulting vertices.</b> The caller snaps <c>Dx</c>/<c>Dy</c>
/// to <c>SnapDbu</c> (or leaves it unsnapped while Alt is held) BEFORE constructing this command;
/// this command adds that one delta to every vertex of every selected shape uniformly. Rounding each
/// moved vertex onto the snap grid independently would re-snap — and thereby destroy — off-grid
/// geometry (imported GDSII, 45° diagonals, flattened arcs) that legitimately sits between grid
/// points (docs/design/layout-view.md §1.5 R5). Adding the same integer delta everywhere is what
/// keeps every shape's internal relationships exact.
///
/// <b>L2b:</b> stores INDICES rather than shape references — the same indices resolve to the same
/// shape instances for the whole lifetime of this command, since a pure geometry mutation (this
/// command never inserts/removes/reorders <see cref="LayoutView.Shapes"/>) cannot shift anything.
/// That is also exactly what makes this a safe <see cref="LayoutChangeInfo.Updated"/> for the spatial
/// index: <c>Shapes.Count</c> never changes and no OTHER index's occupant ever changes.
/// </summary>
internal sealed class MoveShapesCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<int> _indices;
    private readonly long _dx, _dy;

    public string Description => "Move";

    public MoveShapesCommand(LayoutView view, IEnumerable<int> indices, long dx, long dy)
    {
        _view = view;
        _indices = indices.ToList();
        _dx = dx;
        _dy = dy;
    }

    public void Execute()
    {
        foreach (var i in _indices) LayoutGeometry.TranslateBy(_view.Shapes[i], _dx, _dy);
        _view.NotifyChanged(LayoutChangeInfo.Updated(_indices));
    }

    public void Undo()
    {
        foreach (var i in _indices) LayoutGeometry.TranslateBy(_view.Shapes[i], -_dx, -_dy);
        _view.NotifyChanged(LayoutChangeInfo.Updated(_indices));
    }
}
