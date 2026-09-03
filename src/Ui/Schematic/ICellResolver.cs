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

/// <param name="CellName">The cell folder leaf name — what the cell is CALLED, and the name it gets
/// in the elaborated library when nothing else has claimed it. It is not the identity: see
/// <paramref name="CellKey"/>.</param>
/// <param name="Schematic">The cell's primary schematic — in-memory session if open, else disk.</param>
/// <param name="Parameters">The cell's declared parameter interface (from its .ccell).</param>
/// <param name="CellKey">What makes this cell THAT cell — its absolute folder. Null falls back to
/// <paramref name="CellName"/>, which is what every caller predating MW2 effectively used.
///
/// <para><b>The distinction is load-bearing since external references exist</b> (MW2). Two
/// workspaces that reference each other routinely both hold a cell called <c>Amp</c>, and the
/// elaborator keys its library and its cycle guard on this: name-keyed, the second <c>Amp</c> was
/// silently given the first one's contents, and a design instantiating both was reported as a cell
/// instantiating itself and skipped. Neither is visible in the netlist that comes out.</para></param>
public sealed record CellResolution(
    string CellName,
    SchematicEditModel Schematic,
    IReadOnlyList<ParameterDeclaration> Parameters,
    string? CellKey = null)
{
    /// <summary>The identity the elaborator keys on — the cell folder, or the name when no caller
    /// supplied one.</summary>
    public string Key => CellKey ?? CellName;
}
