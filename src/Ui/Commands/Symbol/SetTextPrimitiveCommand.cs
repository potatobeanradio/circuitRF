using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Sets the Content, FontSize, and FontStyle of a TextPrimitive.
/// Used by the primitive inspector (Layer 11d) so font edits are undoable.
/// </summary>
internal sealed class SetTextPrimitiveCommand : IUiCommand
{
    private readonly EditableSymbol  _symbol;
    private readonly TextPrimitive   _prim;
    private readonly string          _newContent;
    private readonly double          _newFontSize;
    private readonly SymbolFontStyle _newFontStyle;
    private readonly string          _oldContent;
    private readonly double          _oldFontSize;
    private readonly SymbolFontStyle _oldFontStyle;

    public string Description => "Edit Text";

    public SetTextPrimitiveCommand(
        EditableSymbol symbol, TextPrimitive prim,
        string newContent, double newFontSize, SymbolFontStyle newFontStyle)
    {
        _symbol       = symbol;
        _prim         = prim;
        _newContent   = newContent;
        _newFontSize  = newFontSize;
        _newFontStyle = newFontStyle;
        _oldContent   = prim.Content;
        _oldFontSize  = prim.FontSize;
        _oldFontStyle = prim.FontStyle;
    }

    public void Execute()
    {
        _prim.Content   = _newContent;
        _prim.FontSize  = _newFontSize;
        _prim.FontStyle = _newFontStyle;
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        _prim.Content   = _oldContent;
        _prim.FontSize  = _oldFontSize;
        _prim.FontStyle = _oldFontStyle;
        _symbol.NotifyChanged();
    }
}
