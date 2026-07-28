using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Framework-free helper that resolves whether a cell-reference instance has a navigable primary
/// layout. Mirrors <c>CircuitRF.Ui.Schematic.HierarchyResolver</c> exactly (brief-L3b-hierarchy-
/// navigation.md §1), but delegates the actual resolution chain to <see cref="CellLayoutResolver"/>
/// (already built in L3a) rather than re-deriving it — <see cref="CellLayoutResolver.Resolve"/>
/// already does dir-exists → <c>CellFolder.ResolvePrimary</c> → load, so this file only needs to map
/// its three-state result to a push-in decision + reason.
/// </summary>
internal static class LayoutHierarchyResolver
{
    /// <summary>
    /// Returns <c>true</c> when the instance is a cell reference with a resolvable primary layout.
    /// Sets <paramref name="reason"/> (user-readable) when <c>false</c> — R13a's "disabled with a
    /// reason," since there is nothing to enter (brief §1: "Unresolvable instance: push-in is
    /// disabled with a reason").
    /// </summary>
    public static bool CanPushInto(
        LayoutInstance? instance, LayoutEditorViewModel? parentVm, out string? reason)
    {
        reason = null;
        if (instance is null || parentVm is null)
        { reason = "not an instance"; return false; }
        if (string.IsNullOrEmpty(instance.CellRef))
        { reason = "not a cell instance"; return false; }
        if (parentVm.InstanceBaseDir is not { Length: > 0 } baseDir)
        { reason = "parent layout has no directory (scratch document)"; return false; }

        var res = CellLayoutResolver.Resolve(instance.CellRef, baseDir);
        if (res.State == CellLayoutState.Resolved)
            return true;

        reason = res.State switch
        {
            CellLayoutState.NotFound => "cell reference not found",
            _                        => "cell has no layout view",
        };
        return false;
    }

    /// <summary>
    /// Resolves the absolute path to the primary <c>.clay</c> file for the given cell-reference
    /// instance. Returns <c>null</c> when not resolvable (call <see cref="CanPushInto"/> first).
    /// </summary>
    public static string? ResolvePrimaryPath(LayoutInstance instance, LayoutEditorViewModel parentVm)
    {
        if (!CanPushInto(instance, parentVm, out _)) return null;
        var cellAbsDir = Path.GetFullPath(Path.Combine(parentVm.InstanceBaseDir, instance.CellRef));
        var pr         = CellFolder.ResolvePrimary(cellAbsDir, ViewType.Layout);
        var layoutDir  = CellFolder.SubFolderPath(cellAbsDir, ViewType.Layout);
        return Path.Combine(layoutDir, pr.ResolvedName!);
    }
}
