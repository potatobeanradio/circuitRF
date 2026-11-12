// 3D editor round 3 — a Project Tree cell, .c3d or .clay dragged into the 3D view places an instance of it.
//
// It is Place Cell Instance with the drag as the pick: the first drag-over decides WHAT (C3dTreeDrop) and arms the
// SAME placement the menu arms (BeginInstancePlacement — the same refusals, at the same moment, before anything is
// written), the outline follows the cursor while the drag moves, and the drop is the placing click: one undo entry.
// A drag that cannot be placed answers None and says why on the viewport line, so a refused drop is never silent.
// Leaving the pane disarms what the drag armed.

using CircuitRF.Ui.ThreeD.Hierarchy;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The payload the current drag was decided for; a new payload is decided afresh.</summary>
    private string? _dropText;
    /// <summary>The placement the current drag armed, or null when it was refused.</summary>
    private PlaceInstanceTool? _dropTool;

    /// <inheritdoc/>
    public bool TreeDragOver(string text)
    {
        if (text != _dropText)
        {
            EndTreeDrag();
            if (C3dTreeDrop.Resolve(text, FilePath) is not { } plan) return false;
            _dropText = text;
            if (_tool is { InProgress: true })
            {
                StatusMessage = "Finish or cancel the gesture in progress (Esc) before dropping a cell here.";
                return false;
            }
            if (!plan.Ok) { StatusMessage = plan.Refusal!; return false; }
            if ((plan.View == CircuitRF.Design.ThreeD.C3dInstanceView.Hertzon
                    ? BeginHertzonPlacement(plan.CellRef!, plan.Design!, plan.CellDir!)
                    : BeginInstancePlacement(plan.CellRef!, plan.CellDir!, plan.View)) is not null) return false;
            _dropTool = _tool as PlaceInstanceTool;
            if (plan.Note is { } note) StatusMessage = note;
        }
        return _dropTool is not null && ReferenceEquals(_tool, _dropTool);
    }

    /// <inheritdoc/>
    public bool TreeDrop(string text)
    {
        if (!TreeDragOver(text))
        {
            bool ours = _dropText is not null;
            _dropText = null;
            _dropTool = null;
            return ours;
        }
        var tool = _dropTool!;
        _dropText = null;
        _dropTool = null;
        // The placing click, at the point a click here takes. A refusal (the plane edge-on, say) leaves the placement
        // armed with its reason, exactly as a refused click does: the next click places it.
        Apply(tool.Click(CursorInput()));
        return true;
    }

    /// <inheritdoc/>
    public void TreeDragLeave()
    {
        EndTreeDrag();
        EndFileDrag(dropped: false);
    }

    private void EndTreeDrag()
    {
        if (_dropTool is not null && ReferenceEquals(_tool, _dropTool)) SetTool(null);
        _dropText = null;
        _dropTool = null;
    }
}
