using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Pastes a set of primitives and pins into a symbol (undoable).
/// Execute appends them to the symbol's lists; Undo removes them.
/// The <paramref name="onPasted"/> callback is invoked after Execute with the indices of the
/// newly-placed items so the caller can update the canvas selection.
/// </summary>
internal sealed class PasteSymbolSelectionCommand : IUiCommand
{
    private readonly EditableSymbol        _symbol;
    private readonly List<SymbolPrimitive> _prims;
    private readonly List<SymbolPin>       _pins;
    private readonly Action<IEnumerable<int>, IEnumerable<int>>? _onPasted;

    private int _primStartIndex = -1;
    private int _pinStartIndex  = -1;

    public string Description => "Paste";

    public PasteSymbolSelectionCommand(
        EditableSymbol        symbol,
        List<SymbolPrimitive> prims,
        List<SymbolPin>       pins,
        Action<IEnumerable<int>, IEnumerable<int>>? onPasted = null)
    {
        _symbol   = symbol;
        _prims    = prims;
        _pins     = pins;
        _onPasted = onPasted;
    }

    public void Execute()
    {
        _primStartIndex = _symbol.Primitives.Count;
        _pinStartIndex  = _symbol.Pins.Count;

        foreach (var p in _prims) _symbol.Primitives.Add(p);
        foreach (var p in _pins)  _symbol.Pins.Add(p);

        _symbol.NotifyChanged();

        _onPasted?.Invoke(
            Enumerable.Range(_primStartIndex, _prims.Count),
            Enumerable.Range(_pinStartIndex,  _pins.Count));
    }

    public void Undo()
    {
        // Remove pins in reverse order to avoid index shifting.
        for (int i = _pins.Count - 1; i >= 0; i--)
            _symbol.Pins.RemoveAt(_pinStartIndex + i);

        // Remove primitives in reverse order.
        for (int i = _prims.Count - 1; i >= 0; i--)
            _symbol.Primitives.RemoveAt(_primStartIndex + i);

        _symbol.NotifyChanged();
    }
}
