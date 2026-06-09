using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Appends a new SymbolPin to an EditableSymbol.
/// Execute() appends it; Undo() removes it.
/// NotifyChanged() is called on the EditableSymbol in both directions.
/// </summary>
internal sealed class PlaceSymbolPinCommand : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly SymbolPin      _pin;

    public string Description => "Place Pin";

    public PlaceSymbolPinCommand(EditableSymbol symbol, SymbolPin pin)
    {
        _symbol = symbol;
        _pin    = pin;
    }

    public void Execute()
    {
        _symbol.Pins.Add(_pin);
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        _symbol.Pins.Remove(_pin);
        _symbol.NotifyChanged();
    }
}
