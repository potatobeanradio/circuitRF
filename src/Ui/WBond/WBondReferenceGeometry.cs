using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Which of a reference layout's cell references actually resolve, and how to re-point the ones that
/// do not (wbond.md WB35, brief-wbond-wbe R-wbe-6).
///
/// <h3>Why this exists at all, and why it is not a failure path</h3>
/// <para>Inside circuitRF a wBond's reference geometry resolves through <c>CellLayoutResolver</c>
/// against a workspace's cells. Standalone there is no workspace, so the two designs that open
/// completely are one carrying EMBEDDED geometry (§9.1) and one carrying none. Anything else — a
/// bundle whose own <c>CellRef</c>s point outside it, or a reference that was already unresolvable
/// when the bundle was written — resolves nothing.</para>
///
/// <para><b>That is a state to report, not an error to refuse.</b> WB35's rule is unchanged: name the
/// references that could not be resolved, offer to re-point them, never silently substitute and
/// never refuse to open the file. The natural re-point in a workspace-less binary is a folder picker
/// naming the directory those cells live in, which is what <see cref="Repoint"/> consumes.</para>
///
/// <h3>Re-pointing moves only the references that failed</h3>
/// <para>Setting the layout's own base directory would move EVERY reference, including the ones
/// already resolving into an unpacked bundle — turning a partial miss into a total one. So each
/// unresolved instance is re-pointed individually, and only when a real, resolvable cell of that
/// name is actually present in the chosen folder. Nothing is written on a guess.</para>
/// </summary>
public static class WBondReferenceGeometry
{
    /// <summary>
    /// The distinct <c>CellRef</c>s in <paramref name="root"/> (and, recursively, in what it
    /// resolves) that do not resolve against <paramref name="baseDir"/>, in first-seen order.
    /// Empty for a design with no instances at all — having no geometry is not an unresolved
    /// reference.
    /// </summary>
    public static IReadOnlyList<string> Unresolved(LayoutView? root, string? baseDir)
    {
        var missing = new List<string>();
        if (root is null || string.IsNullOrWhiteSpace(baseDir)) return missing;

        Walk(root, baseDir, missing, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        return missing;
    }

    private static void Walk(LayoutView view, string baseDir, List<string> missing,
                             HashSet<string> seen, int depth)
    {
        if (depth > CellHierarchy.MaxDepth) return;

        foreach (var instance in view.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.CellRef)) continue;

            var resolution = CellLayoutResolver.Resolve(instance.CellRef, baseDir);
            if (resolution.State != CellLayoutState.Resolved || resolution.View is null)
            {
                if (!missing.Contains(instance.CellRef, StringComparer.Ordinal))
                    missing.Add(instance.CellRef);
                continue;
            }

            string cellDir = Path.GetFullPath(Path.Combine(baseDir, instance.CellRef));
            if (!seen.Add(cellDir)) continue;

            Walk(resolution.View, CellHierarchy.LayoutBaseDirOf(cellDir), missing, seen, depth + 1);
        }
    }

    /// <summary>
    /// Re-points every unresolved reference in <paramref name="root"/> at a same-named cell folder
    /// inside <paramref name="folder"/>, and reports how many moved.
    ///
    /// <para>A reference is re-pointed only when <paramref name="folder"/> genuinely holds a cell of
    /// that name whose layout view resolves; anything else is left exactly as written, so the report
    /// afterwards is still honest about what is missing. Only the top level is walked — a reference
    /// that resolves after this brings its own sub-tree with it, and one that still does not is
    /// reported again rather than searched for at depth.</para>
    /// </summary>
    /// <returns>The number of instances whose <c>CellRef</c> was changed.</returns>
    public static int Repoint(LayoutView root, string baseDir, string folder)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(baseDir);
        ArgumentNullException.ThrowIfNull(folder);

        int moved = 0;

        foreach (var instance in root.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.CellRef)) continue;
            if (CellLayoutResolver.Resolve(instance.CellRef, baseDir).State == CellLayoutState.Resolved)
                continue;

            string name = LastSegment(instance.CellRef);
            if (name.Length == 0) continue;

            string candidate = Path.Combine(folder, name);
            if (CellLayoutResolver.Resolve(candidate, baseDir).State != CellLayoutState.Resolved) continue;

            instance.CellRef = Path.GetRelativePath(baseDir, Path.GetFullPath(candidate))
                                   .Replace(Path.DirectorySeparatorChar, '/');
            moved++;
        }

        // Instance-only: nothing about the SHAPES changed, so the layout's own path cache and shape
        // spatial index are left alone (the same distinction every other instance edit draws).
        if (moved > 0) root.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        return moved;
    }

    /// <summary>The cell-folder name a reference names, whatever separators it was written with.</summary>
    private static string LastSegment(string cellRef) =>
        cellRef.TrimEnd('/', '\\')
               .Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)
               .LastOrDefault() ?? "";
}
