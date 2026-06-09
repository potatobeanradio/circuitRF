using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Relocates a SymbolPin to a new on-P position.
/// Records the old position so Undo can restore it.
/// Both directions call NotifyChanged().
/// </summary>
internal sealed class MoveSymbolPinCommand : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly SymbolPin      _pin;
    private readonly double         _oldX, _oldY, _newX, _newY;

    public string Description => "Move Pin";

    public MoveSymbolPinCommand(EditableSymbol symbol, SymbolPin pin, double newX, double newY)
    {
        _symbol = symbol;
        _pin    = pin;
        _oldX   = pin.LocalX;
        _oldY   = pin.LocalY;
        _newX   = newX;
        _newY   = newY;
    }

    public void Execute()
    {
        _pin.LocalX = _newX;
        _pin.LocalY = _newY;
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        _pin.LocalX = _oldX;
        _pin.LocalY = _oldY;
        _symbol.NotifyChanged();
    }
}
