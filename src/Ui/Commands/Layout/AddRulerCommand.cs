using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Adds a <see cref="RulerAnnotation"/> to a <see cref="LayoutView"/> — docs/design/layout-view.md
/// §9B.6 R-rul-14. <b>Restore-at-original-index, not append</b>, for the same reason
/// <see cref="AddShapeCommand"/> is: the index is captured once on the first
/// <see cref="Execute"/> and every subsequent Undo/Redo re-inserts at that exact position, so the
/// third selection channel's indices mean the same thing after an undo as before it.
///
/// <para>Rulers are NOT in the spatial index (§9B.11), so this passes the default
/// <see cref="LayoutChangeInfo.Full"/> rather than an incremental descriptor — there is nothing
/// ruler-shaped for the index to maintain, and inventing a ruler-specific
/// <c>LayoutChangeKind</c> would add a case every existing consumer would have to learn.</para>
/// </summary>
internal sealed class AddRulerCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly RulerAnnotation _ruler;
    private int _index = -1;

    public string Description => "Add Ruler";

    public AddRulerCommand(LayoutView view, RulerAnnotation ruler)
    {
        _view = view;
        _ruler = ruler;
    }

    public void Execute()
    {
        lock (_view.RenderLock)
        {
            if (_index < 0) _index = _view.Rulers.Count;
            _view.Rulers.Insert(_index, _ruler);
            _view.NotifyChanged();
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            _view.Rulers.Remove(_ruler);
            _view.NotifyChanged();
        }
    }
}
