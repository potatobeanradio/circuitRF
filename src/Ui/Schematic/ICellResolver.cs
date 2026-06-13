using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Resolves a cell-instance component to its primary schematic + parameter interface.
/// Implemented by WorkspaceViewModel (registry-else-disk, WYSIWYG). Kept framework-free here.
/// </summary>
public interface ICellResolver
{
    /// <summary>
    /// Resolve <paramref name="cellInstance"/> (which lives inside <paramref name="containingModel"/>)
    /// to its primary schematic. Returns null when unresolvable (no primary schematic, scratch
    /// parent, missing cell) — the extractor then skips the instance with a conflict note.
    /// </summary>
    CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel);
}

/// <param name="CellName">Unique key + Cell.Name (the cell folder leaf name).</param>
/// <param name="Schematic">The cell's primary schematic — in-memory session if open, else disk.</param>
/// <param name="Parameters">The cell's declared parameter interface (from its .ccell).</param>
public sealed record CellResolution(
    string CellName,
    SchematicEditModel Schematic,
    IReadOnlyList<ParameterDeclaration> Parameters);
