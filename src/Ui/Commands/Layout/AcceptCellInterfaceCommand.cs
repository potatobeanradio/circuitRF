using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// SL3 R-sl3-10 — <b>Accept the new interface</b> for a set of layout instances: rewrites each one's
/// recorded interface hash to the cell's interface as it is now.
///
/// <para><b>This is the only thing that rewrites a recorded hash, and it exists as a COMMAND for
/// exactly that reason.</b> The recorded hash is the only evidence that the design was authored
/// against a different interface; a product that erases that evidence as a side effect of opening or
/// saving has implemented nothing. Making it an explicit, undoable, dirtying edit is what keeps it
/// from becoming a convenience later.</para>
/// </summary>
internal sealed class AcceptCellInterfaceCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly (LayoutInstance Instance, string? Before, string? After)[] _edits;

    public string Description => "Accept New Interface";

    public AcceptCellInterfaceCommand(
        LayoutView view, IEnumerable<(LayoutInstance Instance, string? Before, string? After)> edits)
    {
        _view  = view;
        _edits = [.. edits];
    }

    /// <summary>How many instances this command actually changes — zero means there is nothing to
    /// execute and the caller should not put it on the undo stack.</summary>
    public int Count => _edits.Length;

    public void Execute()
    {
        lock (_view.RenderLock)
        {
            foreach (var (inst, _, after) in _edits) inst.CellInterfaceHash = after;
            _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            foreach (var (inst, before, _) in _edits) inst.CellInterfaceHash = before;
            _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }
    }
}
