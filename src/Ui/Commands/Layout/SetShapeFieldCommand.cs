using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Generic undoable field-set for any <see cref="LayoutShape"/> property — mirrors
/// <c>SetSymbolPrimitiveFieldCommand&lt;T&gt;</c>. Stores old + new value; Execute applies new, Undo
/// applies old. The apply closure mutates the shape directly (same reference in
/// <see cref="LayoutView.Shapes"/>). A multi-selection edit chains one of these per shape via
/// <c>CompositeCommand</c> so the whole edit is a single undo entry.
/// </summary>
internal sealed class SetShapeFieldCommand<T> : IUiCommand
{
    private readonly LayoutView _view;
    private readonly string _description;
    private readonly T _oldValue;
    private readonly T _newValue;
    private readonly Action<T> _apply;

    public string Description => _description;

    public SetShapeFieldCommand(LayoutView view, string description, T oldValue, T newValue, Action<T> apply)
    {
        _view = view;
        _description = description;
        _oldValue = oldValue;
        _newValue = newValue;
        _apply = apply;
    }

    public void Execute() { _apply(_newValue); _view.NotifyChanged(); }
    public void Undo()    { _apply(_oldValue); _view.NotifyChanged(); }
}
