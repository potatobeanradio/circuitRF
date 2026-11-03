using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// Framework-free helper that resolves whether a cell-reference component has a navigable
/// primary schematic.  Extracted so tests can call it without constructing WorkspaceViewModel.
///
/// <para><b>It lives below the UI firewall because the headless side needs the same descent</b>
/// (2026-09-15). <see cref="DiskCellResolver"/> is built on it, and without that the CLI's
/// extraction ran with no <see cref="ICellResolver"/> at all — which <see cref="NetExtractor"/>
/// treats as "flat caller" and answers by SKIPPING every cell instance, silently. A hierarchical
/// design therefore netlisted and simulated as the parts around the hole where its device used to
/// be. Nothing in this file changed on the way over; only the namespace and the accessibility.</para>
/// </summary>
public static class HierarchyResolver
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
        // Brief vendorlib-12 R-vl12-1: a placed library model is somebody else's design. Editing it through Push In
        // would change every design that places it, so it is never pushed into; the reason names the two commands
        // that do change it, each saying what it does. The menu, toolbar 18 and Ctrl/⌘+] all ask here.
        if (VendorLibraries.ModelLibrary.VendorModelRefs.IsRef(comp.CellRef))
        { reason = VendorLibraries.ModelLibrary.VendorModelAuthoring.PushInRefusal; return false; }
        if (parentModel.SchematicDirectory is null)
        { reason = "parent schematic has no directory (scratch document)"; return false; }

        // A ws:// reference resolves through the referencing workspace's alias table; a plain one is
        // the path relative to this schematic's own folder (MW2 R-mw2-2). Both land here, so push-in
        // and the elaborator's descent — which both go through this file — need no second rule.
        // A loose schematic's reference that names nothing is looked for in the workspace it is run
        // from, last of all (ExternalCellRef.FindInHostWorkspace).
        var cellDir = ExternalCellRef.ResolveCellDir(
            comp.CellRef, parentModel.SchematicDirectory, parentModel.HostWorkspaceRoot, out _, out _);
        if (cellDir is null || !Directory.Exists(cellDir))
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
        var cellDir = ExternalCellRef.ResolveCellDir(
            comp.CellRef, parentModel.SchematicDirectory, parentModel.HostWorkspaceRoot, out _, out _)!;
        var pr      = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        return Path.Combine(cellDir, CellFolder.SchematicSubFolder, pr.ResolvedName!);
    }

    /// <summary>
    /// The same answer from the reference and the parent's folder alone, for a caller holding no
    /// live component — the Instances panel's sub-cell search, which reads the placements it walks
    /// off the UI thread. Null wherever <see cref="CanPushInto"/> would refuse.
    /// </summary>
    public static string? ResolvePrimaryPath(string? cellRef, string? schematicDirectory)
    {
        if (cellRef is null || schematicDirectory is null) return null;
        var cellDir = ExternalCellRef.ResolveCellDir(cellRef, schematicDirectory);
        if (cellDir is null || !Directory.Exists(cellDir)) return null;

        var pr = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        return pr.State is PrimaryState.SoleFile or PrimaryState.NamedPresent
            ? Path.Combine(cellDir, CellFolder.SchematicSubFolder, pr.ResolvedName!)
            : null;
    }
}
