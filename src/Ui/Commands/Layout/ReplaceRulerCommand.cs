using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Swaps one <see cref="RulerAnnotation"/> for another at the same index — the ruler analogue of
/// <see cref="ReplaceShapeCommand"/>, used for an endpoint drag (§9B.6 R-rul-14). Both the before and
/// after are held, so Undo restores the exact object that was there rather than reconstructing it.
///
/// <para><b>Ordinary property edits do NOT come through here</b> — they use
/// <see cref="SetShapeFieldCommand{T}"/>, which is already generic over "a field on something this
/// <see cref="LayoutView"/> owns" (see its own doc comment) and folds into one
/// <see cref="CompositeCommand"/> per multi-selection edit. This command exists for the case where
/// the whole object changes at once.</para>
/// </summary>
internal sealed class ReplaceRulerCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly int _index;
    private readonly RulerAnnotation _before, _after;

    public string Description => "Edit Ruler";

    public ReplaceRulerCommand(LayoutView view, int index, RulerAnnotation before, RulerAnnotation after)
    {
        _view = view;
        _index = index;
        _before = before;
        _after = after;
    }

    private void Put(RulerAnnotation r)
    {
        lock (_view.RenderLock)
        {
            if (_index >= 0 && _index < _view.Rulers.Count) _view.Rulers[_index] = r;
            _view.NotifyChanged();
        }
    }

    public void Execute() => Put(_after);
    public void Undo()    => Put(_before);
}
