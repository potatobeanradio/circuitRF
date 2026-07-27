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
/// </summary>
internal sealed class MoveShapesCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<LayoutShape> _shapes;
    private readonly long _dx, _dy;

    public string Description => "Move";

    public MoveShapesCommand(LayoutView view, IEnumerable<LayoutShape> shapes, long dx, long dy)
    {
        _view = view;
        _shapes = shapes.ToList();
        _dx = dx;
        _dy = dy;
    }

    public void Execute()
    {
        foreach (var s in _shapes) LayoutGeometry.TranslateBy(s, _dx, _dy);
        _view.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var s in _shapes) LayoutGeometry.TranslateBy(s, -_dx, -_dy);
        _view.NotifyChanged();
    }
}
