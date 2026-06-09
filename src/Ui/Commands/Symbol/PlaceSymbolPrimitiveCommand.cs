using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Appends a new SymbolPrimitive to an EditableSymbol (topmost Z = end of list).
/// Execute() appends it; Undo() removes it.
/// NotifyChanged() is called on the EditableSymbol in both directions.
/// </summary>
internal sealed class PlaceSymbolPrimitiveCommand : IUiCommand
{
    private readonly EditableSymbol  _symbol;
    private readonly SymbolPrimitive _primitive;

    public string Description => "Place";

    public PlaceSymbolPrimitiveCommand(EditableSymbol symbol, SymbolPrimitive primitive)
    {
        _symbol    = symbol;
        _primitive = primitive;
    }

    public void Execute()
    {
        _symbol.Primitives.Add(_primitive);
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        _symbol.Primitives.Remove(_primitive);
        _symbol.NotifyChanged();
    }
}
