using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Removes a SymbolPin from an EditableSymbol.
/// Records the original insertion index so Undo can restore the pin
/// at the same position in the list (preserving pin order).
/// Both directions call NotifyChanged().
/// </summary>
internal sealed class DeleteSymbolPinCommand : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly SymbolPin      _pin;
    private int                     _savedIndex;

    public string Description => "Delete Pin";

    public DeleteSymbolPinCommand(EditableSymbol symbol, SymbolPin pin)
    {
        _symbol = symbol;
        _pin    = pin;
    }

    public void Execute()
    {
        _savedIndex = _symbol.Pins.IndexOf(_pin);
        _symbol.Pins.RemoveAt(_savedIndex);
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        _symbol.Pins.Insert(_savedIndex, _pin);
        _symbol.NotifyChanged();
    }
}
