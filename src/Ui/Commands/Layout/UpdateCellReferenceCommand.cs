using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// TM2 R-tm2-13 — <b>Update references</b> for a set of layout instances: rewrites each one's stored
/// <c>CellRef</c> from the place the cell used to be to the place the forwarding record says it is
/// now.
///
/// <para><b>The only thing that rewrites a stale reference, and a COMMAND for exactly that
/// reason.</b> The stored reference is the only evidence that the design was authored against a
/// different library layout; adopting the new one on open or on save would erase that evidence and
/// implement nothing. The same argument, and the same shape, as
/// <see cref="AcceptCellInterfaceCommand"/> beside it.</para>
/// </summary>
internal sealed class UpdateCellReferenceCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly (LayoutInstance Instance, string? Before, string After)[] _edits;

    public string Description => "Update References";

    public UpdateCellReferenceCommand(
        LayoutView view, IEnumerable<(LayoutInstance Instance, string? Before, string After)> edits)
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
            foreach (var (inst, _, after) in _edits) inst.CellRef = after;
            _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            foreach (var (inst, before, _) in _edits) inst.CellRef = before ?? "";
            _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }
    }
}
