namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Framework-free helper that resolves whether a cell-reference component has a navigable
/// primary schematic.  Extracted so tests can call it without constructing WorkspaceViewModel.
/// </summary>
internal static class HierarchyResolver
{
    /// <summary>
    /// Returns <c>true</c> when the component is a cell instance with a resolvable primary
    /// schematic.  Sets <paramref name="reason"/> (user-readable) when <c>false</c>.
    /// </summary>
    public static bool CanPushInto(
        EditableComponent? comp, SchematicEditModel? parentModel, out string? reason)
    {
        reason = null;
        if (comp is null || parentModel is null)
            return false;
        if (comp.CellRef is null)
        { reason = "not a cell instance"; return false; }
        if (parentModel.SchematicDirectory is null)
        { reason = "parent schematic has no directory (scratch document)"; return false; }

        var cellDir = Path.GetFullPath(Path.Combine(parentModel.SchematicDirectory, comp.CellRef));
        if (!Directory.Exists(cellDir))
        { reason = "cell reference not found"; return false; }

        var pr = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (pr.State is PrimaryState.SoleFile or PrimaryState.NamedPresent)
            return true;

        reason = pr.State switch
        {
            PrimaryState.NoPrimary           => "cell has no primary schematic",
            PrimaryState.MissingNamedPrimary => "primary schematic missing",
            _                                => "cell has no schematic view",
        };
        return false;
    }

    /// <summary>
    /// Resolves the absolute path to the primary <c>.csch</c> file for the given cell-reference
    /// component.  Returns <c>null</c> when not resolvable (call <see cref="CanPushInto"/> first).
    /// </summary>
    public static string? ResolvePrimaryPath(EditableComponent comp, SchematicEditModel parentModel)
    {
        if (!CanPushInto(comp, parentModel, out _)) return null;
        var cellDir = Path.GetFullPath(Path.Combine(parentModel.SchematicDirectory!, comp.CellRef!));
        var pr      = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        return Path.Combine(cellDir, CellFolder.SchematicSubFolder, pr.ResolvedName!);
    }
}
