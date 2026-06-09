using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Removes a set of SymbolPrimitives from an EditableSymbol by index.
/// Execute() deletes them; Undo() re-inserts them at their original indices.
/// NotifyChanged() is called on the EditableSymbol in both directions.
/// </summary>
internal sealed class DeleteSymbolPrimitivesCommand : IUiCommand
{
    private readonly EditableSymbol                   _symbol;
    // Stored descending by index so Execute can safely RemoveAt without index shifting.
    private readonly List<(int Index, SymbolPrimitive Prim)> _items;

    public string Description => "Delete";

    public DeleteSymbolPrimitivesCommand(EditableSymbol symbol, IEnumerable<int> indices)
    {
        _symbol = symbol;
        // Capture primitive references at command-build time (before any removal).
        _items = indices
            .Where(i => i >= 0 && i < symbol.Primitives.Count)
            .Distinct()
            .OrderByDescending(i => i)
            .Select(i => (i, symbol.Primitives[i]))
            .ToList();
    }

    public void Execute()
    {
        foreach (var (idx, _) in _items)
            _symbol.Primitives.RemoveAt(idx);
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        // Re-insert in ascending order to preserve original positions.
        foreach (var (idx, prim) in Enumerable.Reverse(_items))
            _symbol.Primitives.Insert(idx, prim);
        _symbol.NotifyChanged();
    }
}
