using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// SL3 R-sl3-10 — <b>Accept the new interface</b>: rewrites the recorded interface hash on a set of
/// components to the cell's interface as it is now, and clears their mark.
///
/// <para><b>The only thing that rewrites a recorded hash, and a COMMAND for exactly that reason.</b>
/// The recorded hash is the only evidence that the design was authored against a different interface.
/// A product that erases that evidence on open, on save, or as a side effect of an unrelated edit has
/// implemented nothing — so the rewrite is an explicit, undoable, dirtying gesture, which is what
/// keeps it from quietly becoming a convenience later.</para>
/// </summary>
internal sealed class AcceptCellInterfaceCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly (EditableComponent Comp, string? Before, string? After, bool WasMarked)[] _edits;

    public string Description => _edits.Length == 1
        ? $"Accept new interface on {_edits[0].Comp.InstanceName}"
        : $"Accept new interface on {_edits.Length} instances";

    public AcceptCellInterfaceCommand(
        SchematicEditModel model,
        IEnumerable<(EditableComponent Comp, string? Before, string? After, bool WasMarked)> edits)
    {
        _model = model;
        _edits = [.. edits];
    }

    /// <summary>How many components this actually changes — zero means there is nothing to do and the
    /// caller should not put it on the undo stack.</summary>
    public int Count => _edits.Length;

    public void Execute()
    {
        foreach (var (comp, _, after, _) in _edits)
        {
            comp.CellInterfaceHash = after;
            comp.InterfaceChanged  = false;
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (comp, before, _, wasMarked) in _edits)
        {
            comp.CellInterfaceHash = before;
            comp.InterfaceChanged  = wasMarked;
        }
        _model.NotifyChanged();
    }
}
