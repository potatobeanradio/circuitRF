using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Moves one or more SymbolPins to explicit (newX, newY) positions in one undoable step.
/// Used for both drag-move (uniform delta) and rotate (per-pin positions).
/// </summary>
internal sealed class MoveMultipleSymbolPinsCommand : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly SymbolPin[]    _pins;
    private readonly double[]       _oldX, _oldY, _newX, _newY;

    public string Description => _pins.Length == 1 ? "Move Pin" : "Move Pins";

    public MoveMultipleSymbolPinsCommand(EditableSymbol symbol,
        IEnumerable<(SymbolPin Pin, double NewX, double NewY)> moves)
    {
        _symbol = symbol;
        var list = moves.ToList();
        _pins = list.Select(m => m.Pin).ToArray();
        _oldX = list.Select(m => m.Pin.LocalX).ToArray();
        _oldY = list.Select(m => m.Pin.LocalY).ToArray();
        _newX = list.Select(m => m.NewX).ToArray();
        _newY = list.Select(m => m.NewY).ToArray();
    }

    public void Execute()
    {
        for (int i = 0; i < _pins.Length; i++) { _pins[i].LocalX = _newX[i]; _pins[i].LocalY = _newY[i]; }
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        for (int i = 0; i < _pins.Length; i++) { _pins[i].LocalX = _oldX[i]; _pins[i].LocalY = _oldY[i]; }
        _symbol.NotifyChanged();
    }
}
