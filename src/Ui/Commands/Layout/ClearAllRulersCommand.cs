using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Ctrl+K / Cmd+K — removes EVERY ruler in the document as ONE undo entry (§9B.6 R-rul-13).
///
/// <para><b>No confirmation prompt, deliberately.</b> The operation is undoable, and a prompt on an
/// undoable action trains people to dismiss prompts. Undo restores all of them, in order, in one
/// keystroke — which is the whole reason a prompt is not needed.</para>
/// </summary>
internal sealed class ClearAllRulersCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<RulerAnnotation> _removed;

    public string Description => "Clear All Rulers";

    public ClearAllRulersCommand(LayoutView view)
    {
        _view = view;
        _removed = [.. view.Rulers];
    }

    public void Execute()
    {
        lock (_view.RenderLock)
        {
            _view.Rulers.Clear();
            _view.NotifyChanged();
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            _view.Rulers.Clear();
            _view.Rulers.AddRange(_removed);
            _view.NotifyChanged();
        }
    }
}
