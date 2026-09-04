using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// TM2 R-tm2-13 — <b>Update references</b>: rewrites the stored <c>CellRef</c> on a set of components
/// from the place the cell used to be to the place the forwarding record says it is now.
///
/// <para><b>The only thing that rewrites a stale reference, and a COMMAND for exactly that reason.</b>
/// The stored reference is the only evidence that the design was authored against a different library
/// layout. Adopting the new one on open, on save, or as a side effect of an unrelated edit would erase
/// that evidence and implement nothing — so it is an explicit, undoable, dirtying gesture, which is
/// what keeps it from quietly becoming a convenience later. The same rule SL3 R-sl3-10 makes for
/// <c>AcceptCellInterfaceCommand</c>, and the two are deliberately the same shape.</para>
///
/// <para>The new spelling comes from <c>ExternalCellRef.MakeCellRef</c> — the one producing rule for a
/// cell reference — so a <c>ws://</c> reference stays a <c>ws://</c> reference and a library-relative
/// one stays relative. This command does not compute it; it carries what the report already worked
/// out, so the sentence the user read and the string that is written cannot disagree.</para>
/// </summary>
internal sealed class UpdateCellReferenceCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly (EditableComponent Comp, string? Before, string After, MoveRedirectHit? WasMarked)[] _edits;

    public string Description => _edits.Length == 1
        ? $"Update cell reference on {_edits[0].Comp.InstanceName}"
        : $"Update cell reference on {_edits.Length} instances";

    public UpdateCellReferenceCommand(
        SchematicEditModel model,
        IEnumerable<(EditableComponent Comp, string? Before, string After, MoveRedirectHit? WasMarked)> edits)
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
            comp.CellRef       = after;
            comp.MovedRedirect = null;
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (comp, before, _, wasMarked) in _edits)
        {
            comp.CellRef       = before;
            comp.MovedRedirect = wasMarked;
        }
        _model.NotifyChanged();
    }
}
