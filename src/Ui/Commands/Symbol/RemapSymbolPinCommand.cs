using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Changes the port-index mapping of a SymbolPin.
/// Records the old PortIndex so Undo can restore it.
/// Both directions call NotifyChanged().
/// </summary>
internal sealed class RemapSymbolPinCommand : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly SymbolPin      _pin;
    private readonly int            _oldIndex, _newIndex;

    public string Description => "Remap Pin";

    public RemapSymbolPinCommand(EditableSymbol symbol, SymbolPin pin, int newPortIndex)
    {
        _symbol   = symbol;
        _pin      = pin;
        _oldIndex = pin.PortIndex;
        _newIndex = newPortIndex;
    }

    public void Execute()
    {
        _pin.PortIndex = _newIndex;
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        _pin.PortIndex = _oldIndex;
        _symbol.NotifyChanged();
    }
}
