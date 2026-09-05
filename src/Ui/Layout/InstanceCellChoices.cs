// Framework-free (no Avalonia) — pulled out of InstanceCellPickerDialog.axaml.cs's code-behind
// specifically so it is headlessly testable, per this project's established answer to "the thing
// under test lives in a Window subclass this test suite can't construct" (see src/Ui/CLAUDE.md's
// ScaleFieldLinker/TraceLabeler precedent). The Window itself is a thin wrapper that calls Collect()
// and turns the result into ListBox items — no filtering/exclusion logic lives in the code-behind.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>One row the Instance cell-picker can offer — a candidate cell, whether it is placeable,
/// and why not when it isn't (brief-L3a-followups.md §1/R-fix-1).</summary>
/// <param name="Note">A remark that does NOT disable the row — used where the missing view is
/// something the placement itself offers to create (a schematic placement generates a symbol), so
/// the row must say what will happen rather than pretend the cell is unusable.</param>
public sealed record InstanceCellChoice(
    string DisplayName, string AbsoluteCellDir, string? DisabledReason, string? Note = null)
{
    public bool IsEnabled => DisabledReason is null;

    /// <summary>What the row shows beside its name — the refusal when there is one, the remark
    /// otherwise. One property so the template has one binding and the two can never both render.</summary>
    public string? Annotation => DisabledReason ?? Note;

    public bool HasAnnotation => Annotation is { Length: > 0 };

    /// <summary>Bound directly by the picker's row template — kept here (not computed in the Window)
    /// so the "disabled rows read visually muted" rule is part of the same tested data, not a
    /// second, XAML-only decision that could drift from <see cref="IsEnabled"/>.</summary>
    public double RowOpacity => IsEnabled ? 1.0 : 0.55;
}

/// <summary>
/// R-fix-1 — "exclude the parent cell only; everything else is offered, attempted, and refused with
/// the cycle message." Self-reference is obvious enough that a user never wonders why it's missing, so
/// it is the ONE exclusion; a deeper cycle (A instantiates B; editing B) is NOT obvious, and silently
/// omitting A would leave the user hunting for a cell that appears to have vanished with nothing on
/// screen to explain it — R-L3a-2's edit-time refusal (naming the full path) is the mechanism that
/// actually teaches the user why, which a missing row never could. A cell with no layout view is
/// listed too, disabled with its reason, for the identical "visible and explained, never silently
/// absent" principle.
/// </summary>
public static class InstanceCellChoices
{
    /// <summary>Scans <paramref name="workspaceRootDir"/> for cell folders (a directory carrying
    /// <c>.ccell</c>), returning one <see cref="InstanceCellChoice"/> per cell found — every cell
    /// EXCEPT <paramref name="parentCellDir"/> (normalized, case-insensitive comparison; null means
    /// nothing is excluded — a scratch document has no stable cell folder to compare against). A cell
    /// folder is never itself recursed into further (cells do not nest inside cells in this project's
    /// workspace layout) — only ordinary sub-folders (a Library grouping, say) are. Never throws —
    /// an unreadable sub-directory is simply skipped, matching every other workspace-scan in this
    /// codebase.</summary>
    /// <param name="view">Which view the placement will draw from: <see cref="ViewType.Layout"/> for
    /// an instance in a layout, <see cref="ViewType.Symbol"/> for one in a schematic. The two differ
    /// in what a MISSING view means — see <see cref="RowFor"/>.</param>
    public static List<InstanceCellChoice> Collect(
        string workspaceRootDir, string? parentCellDir, ViewType view = ViewType.Layout)
    {
        var items = new List<InstanceCellChoice>();
        if (workspaceRootDir is not { Length: > 0 } root || !Directory.Exists(root)) return items;
        CollectInto(root, root, NormalizeDir(parentCellDir), view, items);
        return items;
    }

    /// <summary>
    /// <see cref="Collect"/> plus every cell this workspace can reach through its <c>.cws</c> — the
    /// individually referenced cells, and the cells of each referenced workspace the Project Tree
    /// draws as its own sub-tree.
    ///
    /// <para><b>Why the picker offers them.</b> Those rows are already in the Project Tree, and
    /// dragging one onto a canvas already places it; a picker that listed only the local scan would
    /// be strictly less capable than the drag it exists to replace. An alias recorded
    /// <c>CellsOnly</c> is NOT enumerated wholesale — only the cells actually listed against it —
    /// for the same reason the tree does not draw it: referencing one cell must not pull in the other
    /// project's whole catalogue.</para>
    /// </summary>
    public static List<InstanceCellChoice> CollectWithReferences(
        string workspaceRootDir, string? parentCellDir, ViewType view = ViewType.Layout)
    {
        var items = Collect(workspaceRootDir, parentCellDir, view);
        if (workspaceRootDir is not { Length: > 0 } root || !Directory.Exists(root)) return items;

        string? parent = NormalizeDir(parentCellDir);
        var seen = new HashSet<string>(
            items.Select(i => NormalizeDir(i.AbsoluteCellDir) ?? i.AbsoluteCellDir),
            StringComparer.OrdinalIgnoreCase);

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(Path.Combine(root, ".cws")); }
        catch { return items; }

        // One referenced cell = one row, named the way the tree names it: the cell, and the alias it
        // came through, so two cells of the same name from different projects are distinguishable.
        foreach (string cellRef in cws.ReferencedCells ?? [])
        {
            if (ExternalCellRef.ResolveCellDir(cellRef, root) is not { Length: > 0 } dir) continue;
            if (!Add(dir, ExternalCellRef.TryParse(cellRef, out string alias, out _)
                        ? $"{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))} ({alias})"
                        : Path.GetFileName(dir))) continue;
        }

        // A referenced WORKSPACE the tree draws in full: its cells are listed in full too.
        foreach (var entry in cws.ReferencedWorkspaces ?? [])
        {
            if (entry.CellsOnly) continue;
            if (ExternalCellRef.WorkspaceRootForAlias(root, entry.Alias) is not { Length: > 0 } otherRoot) continue;
            if (!Directory.Exists(otherRoot)) continue;

            var theirs = new List<InstanceCellChoice>();
            CollectInto(otherRoot, otherRoot, parent, view, theirs);
            foreach (var t in theirs) Add(t.AbsoluteCellDir, $"{t.DisplayName} ({entry.Alias})");
        }

        return items;

        bool Add(string absDir, string display)
        {
            string key = NormalizeDir(absDir) ?? absDir;
            if (parent is not null && string.Equals(key, parent, StringComparison.OrdinalIgnoreCase)) return false;
            if (!seen.Add(key)) return false;
            items.Add(RowFor(absDir, display, view));
            return true;
        }
    }

    private static void CollectInto(
        string root, string dir, string? parentCellDirNormalized, ViewType view, List<InstanceCellChoice> items)
    {
        string ccellPath = Path.Combine(dir, CellFolder.CcellFileName);
        if (File.Exists(ccellPath))
        {
            if (parentCellDirNormalized is not null
                && string.Equals(NormalizeDir(dir), parentCellDirNormalized, System.StringComparison.OrdinalIgnoreCase))
                return; // R-fix-1: the parent cell itself — the one exclusion

            string rel = Path.GetRelativePath(root, dir);
            string name = rel == "." ? Path.GetFileName(dir) : rel;
            items.Add(RowFor(dir, name, view));
            return; // a cell folder's own sub-folders (schematic/symbol/layout) are never cells themselves
        }

        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (var sub in subDirs)
        {
            string name2 = Path.GetFileName(sub);
            if (name2.StartsWith('.')) continue; // skip dotfolders (e.g. a future .git)
            CollectInto(root, sub, parentCellDirNormalized, view, items);
        }
    }

    /// <summary>
    /// One row, and the whole of the layout/schematic difference.
    ///
    /// <para>In a LAYOUT there is nothing to draw for a cell with no layout view, so the row is
    /// disabled and says why. In a SCHEMATIC the same cell is placeable: the placement path itself
    /// offers to generate a symbol from the cell's ports (<c>SchematicViewModel.
    /// CommitCellPlacementAsync</c>), so disabling the row would hide a working gesture behind a
    /// refusal that is not true. The row is enabled and carries a remark instead.</para>
    /// </summary>
    private static InstanceCellChoice RowFor(string cellDir, string displayName, ViewType view)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, view);
        bool hasView = primary.State is PrimaryState.SoleFile or PrimaryState.NamedPresent;
        if (hasView) return new InstanceCellChoice(displayName, cellDir, null);

        return view == ViewType.Symbol
            ? new InstanceCellChoice(displayName, cellDir, null, "no symbol yet — one will be generated")
            : new InstanceCellChoice(displayName, cellDir, "No layout view");
    }

    /// <summary>Public so the Window's code-behind can normalize <c>CurrentCellDir</c> once, up front,
    /// the same way this class normalizes it internally — kept as one implementation, not two.</summary>
    public static string? NormalizeDir(string? dir)
    {
        if (dir is not { Length: > 0 }) return null;
        try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }
}

/// <summary>
/// What the cell picker hands back. Two forms of the SAME cell, because the two placement paths need
/// different ones and neither can derive the other after the dialog is gone: a layout instance stores
/// a <c>CellRef</c> relative to the document (or a <c>ws://</c> alias), while a schematic placement is
/// handed the absolute folder and makes its own reference against the schematic's directory.
/// </summary>
/// <param name="ReferenceRequested">Set when the user asked to reference a cell from OUTSIDE this
/// workspace instead of choosing one from the list. The other two fields are empty; the caller runs
/// the cross-workspace flow and re-asks. A nested modal is what this avoids — see the picker's own
/// note.</param>
public sealed record CellPickResult(
    string CellRef, string AbsoluteCellDir, bool ReferenceRequested = false)
{
    public static CellPickResult Reference { get; } = new("", "", true);
}
