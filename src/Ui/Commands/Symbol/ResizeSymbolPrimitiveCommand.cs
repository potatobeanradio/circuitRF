using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Resizes a single SymbolPrimitive by a scale factor (sx, sy) about a fixed reference
/// point (top-left of the original bbox).  Undo applies the inverse scale.
/// </summary>
internal sealed class ResizeSymbolPrimitiveCommand : IUiCommand
{
    private readonly EditableSymbol  _symbol;
    private readonly SymbolPrimitive _prim;
    private readonly double          _refX, _refY;
    private readonly double          _sx, _sy;

    public string Description => "Resize";

    public ResizeSymbolPrimitiveCommand(
        EditableSymbol symbol, SymbolPrimitive prim,
        double refX, double refY, double sx, double sy)
    {
        _symbol = symbol;
        _prim   = prim;
        _refX   = refX; _refY = refY;
        _sx     = sx;   _sy   = sy;
    }

    public void Execute()
    {
        SymbolGeometry.ScaleBy(_prim, _refX, _refY, _sx, _sy);
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        SymbolGeometry.ScaleBy(_prim, _refX, _refY, 1.0 / _sx, 1.0 / _sy);
        _symbol.NotifyChanged();
    }
}
