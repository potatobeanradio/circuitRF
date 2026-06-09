using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Translates a set of SymbolPrimitives by (Dx, Dy) in local coordinates.
/// Execute() applies the delta; Undo() applies the negated delta.
/// NotifyChanged() is called on the EditableSymbol in both directions.
/// </summary>
internal sealed class MoveSymbolPrimitivesCommand : IUiCommand
{
    private readonly EditableSymbol          _symbol;
    private readonly List<SymbolPrimitive>   _primitives;
    private readonly double                  _dx, _dy;

    public string Description => "Move";

    public MoveSymbolPrimitivesCommand(
        EditableSymbol symbol, IEnumerable<SymbolPrimitive> primitives,
        double dx, double dy)
    {
        _symbol     = symbol;
        _primitives = primitives.ToList();
        _dx         = dx;
        _dy         = dy;
    }

    public void Execute()
    {
        foreach (var p in _primitives) SymbolGeometry.TranslateBy(p, _dx, _dy);
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var p in _primitives) SymbolGeometry.TranslateBy(p, -_dx, -_dy);
        _symbol.NotifyChanged();
    }
}
