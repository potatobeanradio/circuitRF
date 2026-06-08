namespace CircuitRF.Ui.Commands;

/// <summary>
/// Executes two commands atomically as one undoable operation.
/// Execute() runs first then second; Undo() reverses second then first.
/// </summary>
internal sealed class CompositeCommand : IUiCommand
{
    private readonly IUiCommand _first;
    private readonly IUiCommand _second;

    public string Description => _second.Description;

    public CompositeCommand(IUiCommand first, IUiCommand second)
    {
        _first  = first;
        _second = second;
    }

    public void Execute() { _first.Execute(); _second.Execute(); }
    public void Undo()    { _second.Undo();   _first.Undo();   }
}
