using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class SchematicViewModel
{
    /// <summary>
    /// brief vendorlib-12 R-vl12-3d: placed library parts pointed at a workspace copy of their model — ONE undo entry for
    /// all of them. Each keeps its name, position, wires and parameter values; only its reference changes, and the
    /// stored <c>Glyph</c> row goes with the library reference, since the copy draws its own symbol (the one the model
    /// drew, copied with the cell, so the pins land where the wires already end). False when there is nothing to do or
    /// the schematic has no folder to write a relative reference from.
    /// </summary>
    public bool RepointToCell(IReadOnlyList<EditableComponent> parts, string cellDir)
    {
        if (parts.Count == 0 || EditModel.SchematicDirectory is not { } dir) return false;
        string cellRef = ExternalCellRef.MakeCellRef(dir, cellDir);
        var commands = parts.Select(c => (IUiCommand)new SetCellRefCommand(EditModel, c, cellRef,
                c.Parameters.Where(p => p.Name != VendorModelRefs.GlyphParameter))).ToList();
        Execute(new CommandBatch(parts.Count == 1 ? "Point at copy" : $"Point {parts.Count} parts at copy", commands));
        return true;
    }

    /// <summary>Every part in this schematic placing the same library model as <paramref name="comp"/>, by its reference.</summary>
    public IReadOnlyList<EditableComponent> SameLibraryModel(EditableComponent comp)
    {
        if (comp.CellRef is not { } r || !VendorModelRefs.IsRef(r)) return [comp];
        string bare = VendorModelRefs.Bare(r);
        return [.. EditModel.Components.Where(c => c.CellRef is { } cr && VendorModelRefs.IsRef(cr)
                                                   && VendorModelRefs.Bare(cr).Equals(bare, StringComparison.OrdinalIgnoreCase))];
    }
}
