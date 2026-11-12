// 3D editor round 3 — what a Project Tree item dropped into a 3D view places. Kept out of the pane so the rule can be
// asserted rather than inferred, and so the drag-over (the cursor it promises) and the drop (what happens) are one
// decision.
//
// A CELL places its 3D view when it has one, else its layout. When its 3D view would make this view contain itself
// (its own cell, or a deeper A → B → A), the layout is placed instead and the note says why: a layout never contains
// a 3D view, and a cell's own layout inside its own 3D view is the ordinary way to start one. A VIEW FILE places
// exactly that view, so a cycle is a refusal. Either way it must be the cell's PRIMARY view — an instance names a
// cell and a view kind, never a file, so a non-primary file would silently place a different one.

using CircuitRF.Design.Cells;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ThreeD;

/// <summary>A drop's decision: the cell and view to place (and the reference to write), or the refusal.</summary>
public sealed record C3dTreeDropPlan(string? CellDir, string? CellRef, C3dInstanceView View, string? Refusal, string? Note = null)
{
    /// <summary>HZN-9 — for a Hertzon design, its name; <see cref="CellDir"/> then holds the <c>.hzn</c> file.</summary>
    public string? Design { get; init; }

    public bool Ok => Refusal is null;
    public static C3dTreeDropPlan Refuse(string why) => new(null, null, C3dInstanceView.ThreeD, why);
}

public static class C3dTreeDrop
{
    /// <summary>
    /// What dropping the drag payload <paramref name="text"/> into the 3D view at <paramref name="c3dPath"/> would place,
    /// or null when the payload is not one a 3D view reads (the cursor answers None and nothing is said).
    /// </summary>
    public static C3dTreeDropPlan? Resolve(string? text, string c3dPath)
    {
        if (HznDesignDragPayload.TryParse(text, out var design)) return ForHertzon(design.HznAbsPath, design.Design, c3dPath);
        if (CellDragPayload.TryParse(text, out var cell)) return ForCell(cell.CellAbsPath, c3dPath);
        if (CellViewDragPayload.TryParse(text, out var view)) return ForFile(view.ViewFileAbsPath, c3dPath);
        // A .c3d / .clay that is a LOOSE file in the tree travels as a workspace file; anything else is not ours.
        if (WorkspaceFileDragPayload.TryParse(text, out var file) && ViewOf(file.FileAbsPath) is not null)
            return ForFile(file.FileAbsPath, c3dPath);
        return null;
    }

    private static C3dTreeDropPlan ForHertzon(string hznPath, string design, string c3dPath)
    {
        hznPath = Path.GetFullPath(hznPath);
        if (!File.Exists(hznPath))
            return C3dTreeDropPlan.Refuse($"'{Path.GetFileName(hznPath)}' does not exist: there is nothing to place.");
        return new(hznPath, ExternalCellRef.MakeCellRef(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, hznPath),
                   C3dInstanceView.Hertzon, null) { Design = design };
    }

    private static C3dTreeDropPlan ForCell(string cellDir, string c3dPath)
    {
        cellDir = Path.GetFullPath(cellDir);
        bool has3D = C3dHierarchy.ViewFile(cellDir, C3dInstanceView.ThreeD) is not null;
        bool hasLayout = C3dHierarchy.ViewFile(cellDir, C3dInstanceView.Layout) is not null;
        if (!has3D && !hasLayout)
            return C3dTreeDropPlan.Refuse($"Cell '{Name(cellDir)}' has no 3D view and no layout: there is nothing to place.");
        if (has3D)
        {
            if (C3dHierarchy.CycleRefusal(c3dPath, cellDir, C3dInstanceView.ThreeD) is not { } cycle)
                return Plan(cellDir, c3dPath, C3dInstanceView.ThreeD);
            if (!hasLayout) return C3dTreeDropPlan.Refuse(cycle);
            return Plan(cellDir, c3dPath, C3dInstanceView.Layout,
                        $"Placing the layout of '{Name(cellDir)}': its 3D view would make this 3D view contain itself.");
        }
        return Plan(cellDir, c3dPath, C3dInstanceView.Layout);
    }

    private static C3dTreeDropPlan ForFile(string file, string c3dPath)
    {
        file = Path.GetFullPath(file);
        if (ViewOf(file) is not { } view)
            return C3dTreeDropPlan.Refuse($"'{Path.GetFileName(file)}' is not a 3D view or a layout: only those can be placed in a 3D view.");
        string noun = CellFolder.ViewNoun(TypeOf(view));
        string? sub = Path.GetDirectoryName(file);
        string? cellDir = sub is not null && string.Equals(Path.GetFileName(sub), CellFolder.SubFolderName(TypeOf(view)), StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(sub) : null;
        if (cellDir is null || !File.Exists(Path.Combine(cellDir, CellFolder.CcellFileName)))
            return C3dTreeDropPlan.Refuse($"'{Path.GetFileName(file)}' belongs to no cell: an instance places a cell's {noun}, so only a cell's own file can be placed.");
        if (string.Equals(file, Path.GetFullPath(c3dPath), StringComparison.OrdinalIgnoreCase))
            return C3dTreeDropPlan.Refuse($"'{Path.GetFileName(file)}' cannot be placed inside itself: a cell cannot contain itself.");
        string? primary = C3dHierarchy.ViewFile(cellDir, view);
        if (primary is null || !string.Equals(Path.GetFullPath(primary), file, StringComparison.OrdinalIgnoreCase))
            return C3dTreeDropPlan.Refuse($"'{Path.GetFileName(file)}' is not the primary {noun} of cell '{Name(cellDir)}': an instance places a cell's " +
                                          $"primary {noun}" + (primary is not null ? $" ('{Path.GetFileName(primary)}')" : "") +
                                          ". Make it primary first, or drop the cell.");
        if (C3dHierarchy.CycleRefusal(c3dPath, cellDir, view) is { } cycle) return C3dTreeDropPlan.Refuse(cycle);
        return Plan(cellDir, c3dPath, view);
    }

    private static C3dTreeDropPlan Plan(string cellDir, string c3dPath, C3dInstanceView view, string? note = null)
        => new(cellDir, ExternalCellRef.MakeCellRef(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, cellDir), view, null, note);

    private static C3dInstanceView? ViewOf(string file)
    {
        string ext = Path.GetExtension(file);
        if (string.Equals(ext, CellFolder.ViewExtension(ViewType.ThreeD), StringComparison.OrdinalIgnoreCase)) return C3dInstanceView.ThreeD;
        if (string.Equals(ext, CellFolder.ViewExtension(ViewType.Layout), StringComparison.OrdinalIgnoreCase)) return C3dInstanceView.Layout;
        return null;
    }

    private static ViewType TypeOf(C3dInstanceView v) => v == C3dInstanceView.Layout ? ViewType.Layout : ViewType.ThreeD;

    private static string Name(string dir) => Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
