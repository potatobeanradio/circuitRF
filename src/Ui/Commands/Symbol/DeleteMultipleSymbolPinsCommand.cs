using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Removes one or more SymbolPins from an EditableSymbol in one undoable step.
/// Deletion order is highest-index-first so indices remain stable during removal.
/// Undo restores each pin at its original list position.
/// </summary>
internal sealed class DeleteMultipleSymbolPinsCommand : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly SymbolPin[]    _pins;
    private readonly int[]          _savedIndices;

    public string Description => _pins.Length == 1 ? "Delete Pin" : "Delete Pins";

    public DeleteMultipleSymbolPinsCommand(EditableSymbol symbol, IEnumerable<SymbolPin> pins)
    {
        _symbol       = symbol;
        _pins         = pins.ToArray();
        _savedIndices = new int[_pins.Length];
    }

    public void Execute()
    {
        for (int i = 0; i < _pins.Length; i++)
            _savedIndices[i] = _symbol.Pins.IndexOf(_pins[i]);

        // Remove highest-index first so earlier positions stay stable.
        foreach (int i in Enumerable.Range(0, _pins.Length).OrderByDescending(i => _savedIndices[i]))
            if (_savedIndices[i] >= 0) _symbol.Pins.RemoveAt(_savedIndices[i]);

        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        // Re-insert in ascending index order to restore original positions.
        foreach (int i in Enumerable.Range(0, _pins.Length).OrderBy(i => _savedIndices[i]))
            if (_savedIndices[i] >= 0) _symbol.Pins.Insert(_savedIndices[i], _pins[i]);

        _symbol.NotifyChanged();
    }
}
