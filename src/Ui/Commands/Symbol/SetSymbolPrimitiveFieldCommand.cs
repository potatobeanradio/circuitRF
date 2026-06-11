using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Generic undoable field-set for any SymbolPrimitive property.
/// Stores old + new value; Execute applies new, Undo applies old.
/// The apply closure directly mutates the primitive (same reference in EditableSymbol.Primitives).
/// </summary>
internal sealed class SetSymbolPrimitiveFieldCommand<T> : IUiCommand
{
    private readonly EditableSymbol _symbol;
    private readonly string         _description;
    private readonly T              _oldValue;
    private readonly T              _newValue;
    private readonly Action<T>      _apply;

    public string Description => _description;

    public SetSymbolPrimitiveFieldCommand(
        EditableSymbol symbol, string description,
        T oldValue, T newValue, Action<T> apply)
    {
        _symbol      = symbol;
        _description = description;
        _oldValue    = oldValue;
        _newValue    = newValue;
        _apply       = apply;
    }

    public void Execute() { _apply(_newValue); _symbol.NotifyChanged(); }
    public void Undo()    { _apply(_oldValue); _symbol.NotifyChanged(); }
}
