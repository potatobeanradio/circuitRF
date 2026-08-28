using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Translates a set of <see cref="RulerAnnotation"/>s by <c>(Dx, Dy)</c> — the ruler analogue of
/// <see cref="MoveShapesCommand"/>, and it follows the same R-L1c-3 rule: the CALLER snaps the delta,
/// and this command adds that one integer delta to both endpoints of every named ruler. Snapping each
/// endpoint independently would re-quantise a ruler deliberately placed on an off-grid geometry snap
/// target — which for a measurement means silently changing the number it reports.
/// </summary>
internal sealed class MoveRulersCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<int> _indices;
    private readonly long _dx, _dy;

    public string Description => "Move Ruler";

    public MoveRulersCommand(LayoutView view, IEnumerable<int> indices, long dx, long dy)
    {
        _view = view;
        _indices = indices.ToList();
        _dx = dx;
        _dy = dy;
    }

    private void Apply(long dx, long dy)
    {
        lock (_view.RenderLock)
        {
            foreach (var i in _indices)
            {
                if (i < 0 || i >= _view.Rulers.Count) continue;
                // Endpoints AND a hand-placed readout — RulerAnnotation.TranslateBy is the single
                // place that pairing is written down, so a move, a nudge and a paste cannot disagree
                // about whether the label comes along.
                _view.Rulers[i].TranslateBy(dx, dy);
            }
            _view.NotifyChanged();
        }
    }

    public void Execute() => Apply(_dx, _dy);
    public void Undo()    => Apply(-_dx, -_dy);
}
