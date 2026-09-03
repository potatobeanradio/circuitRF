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
    /// <summary>R-L5f-6's stated reason — a parametric cell's geometry is derived, so there is
    /// nothing inside it to edit; the parameters (Properties Inspector, R-L5f-8) are how it's
    /// modified instead.</summary>
    public const string PCellPushInRefusedReason = "A parametric cell's geometry is generated; edit its parameters instead.";

    /// <summary>
    /// Returns <c>true</c> when the instance is a cell reference with a resolvable, NON-PCell primary
    /// layout. Sets <paramref name="reason"/> (user-readable) when <c>false</c> — R13a's "disabled
    /// with a reason," since there is nothing to enter (brief §1: "Unresolvable instance: push-in is
    /// disabled with a reason"; docs/sonnet-briefs/brief-L5-followups.md §3/R-L5f-6: a PCell instance
    /// is resolvable but still refused — its geometry is generated and read-only, pcell-contract.md
    /// R9, so push-in would land the user in a document with nothing they can edit).
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
        if (res.State != CellLayoutState.Resolved)
        {
            reason = res.State switch
            {
                CellLayoutState.NotFound => "cell reference not found",
                _                        => "cell has no layout view",
            };
            return false;
        }

        if (res.View!.PCellOrigin is not null)
        { reason = PCellPushInRefusedReason; return false; }

        return true;
    }

    /// <summary>
    /// Resolves the absolute path to the primary <c>.clay</c> file for the given cell-reference
    /// instance. Returns <c>null</c> when not resolvable (call <see cref="CanPushInto"/> first).
    /// </summary>
    public static string? ResolvePrimaryPath(LayoutInstance instance, LayoutEditorViewModel parentVm)
    {
        if (!CanPushInto(instance, parentVm, out _)) return null;
        var cellAbsDir = ExternalCellRef.ResolveCellDir(instance.CellRef, parentVm.InstanceBaseDir)!;
        var pr         = CellFolder.ResolvePrimary(cellAbsDir, ViewType.Layout);
        var layoutDir  = CellFolder.SubFolderPath(cellAbsDir, ViewType.Layout);
        return Path.Combine(layoutDir, pr.ResolvedName!);
    }

    /// <summary>
    /// brief-L5-followups-3.md §1 (R-L5h-1): true when <paramref name="instance"/> resolves to a
    /// PCell-generated cell — the double-click dispatch (<c>LayoutEditorView.
    /// OnInstanceDoubleTapped</c>) calls this FIRST, before <see cref="CanPushInto"/> is ever reached,
    /// so a PCell instance routes to its parameter editor directly instead of calling push-in and
    /// showing its polite refusal. Deliberately independent of <see cref="CanPushInto"/> (rather than
    /// reusing its own refusal reason string) — a resolvable-but-not-a-PCell instance that is ALSO
    /// unresolvable for some other reason (missing, no layout view) must still fall through to
    /// <c>DoPushInto</c> so ITS OWN correct refusal reason is what the user sees, not this predicate's.
    /// </summary>
    public static bool IsPCellInstance(LayoutInstance? instance, LayoutEditorViewModel? parentVm)
    {
        if (instance is null || parentVm is null) return false;
        if (string.IsNullOrEmpty(instance.CellRef)) return false;
        if (parentVm.InstanceBaseDir is not { Length: > 0 } baseDir) return false;

        var res = CellLayoutResolver.Resolve(instance.CellRef, baseDir);
        return res.State == CellLayoutState.Resolved && res.View!.PCellOrigin is not null;
    }
}
