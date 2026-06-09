// Framework-free mutable working copy of a Symbol — edited by SymbolEditorViewModel.
// No Skia / Avalonia references.

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Mutable working copy of a <see cref="Symbol"/> used by the symbol editor.
/// Commands hold a reference and call <see cref="NotifyChanged"/> after mutating it.
/// The <see cref="SymbolPrimitive"/> instances are shared with the original; callers
/// who need isolation should clone via <see cref="FromSymbol"/>.
/// </summary>
public sealed class EditableSymbol
{
    public List<SymbolPrimitive> Primitives { get; } = [];
    public List<SymbolPin>       Pins       { get; } = [];

    /// <summary>
    /// Number of ports this symbol can map pins to.
    /// Set on load; author can adjust it from the editor.
    /// Must be ≥ 1 for a symbol with at least one port.
    /// </summary>
    public int  PortCount    { get; set; } = 1;

    /// <summary>
    /// False for built-in / system symbols — the editor opens them read-only.
    /// True (default) for user-authored .csym files.
    /// </summary>
    public bool UserEditable { get; set; } = true;

    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Creates a new EditableSymbol from an immutable Symbol.
    /// The primitive instances are the same objects (no deep copy) — the editor
    /// mutates them in-place and commands record before/after via TranslateBy.
    /// PortCount is carried over from the Symbol.
    /// </summary>
    public static EditableSymbol FromSymbol(Symbol symbol)
    {
        var e = new EditableSymbol();
        e.Primitives.AddRange(symbol.Primitives);
        e.Pins.AddRange(symbol.Pins);
        e.PortCount = symbol.PortCount;
        return e;
    }

    /// <summary>Produces an immutable snapshot of the current state.</summary>
    public Symbol ToSymbol() =>
        new(Primitives.AsReadOnly(), Pins.AsReadOnly(), PortCount);
}
