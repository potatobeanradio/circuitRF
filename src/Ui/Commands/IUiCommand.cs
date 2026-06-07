namespace CircuitRF.Ui.Commands;

/// <summary>
/// A reversible editor mutation. Every schematic/symbol/data-display edit is an IUiCommand
/// executed through the UndoRedoStack so that Undo/Redo work correctly across all editors.
/// Global workspace commands (New/Open/Save) are NOT IUiCommands — they use RelayCommands
/// directly on the ViewModel and are not reversible operations.
/// </summary>
public interface IUiCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}
