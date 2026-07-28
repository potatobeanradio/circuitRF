// Framework-free (no Avalonia) — pulled out of InstanceCellPickerDialog.axaml.cs's code-behind
// specifically so it is headlessly testable, per this project's established answer to "the thing
// under test lives in a Window subclass this test suite can't construct" (see src/Ui/CLAUDE.md's
// ScaleFieldLinker/TraceLabeler precedent). The Window itself is a thin wrapper that calls Collect()
// and turns the result into ListBox items — no filtering/exclusion logic lives in the code-behind.

using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>One row the Instance cell-picker can offer — a candidate cell, whether it is placeable,
/// and why not when it isn't (brief-L3a-followups.md §1/R-fix-1).</summary>
public sealed record InstanceCellChoice(string DisplayName, string AbsoluteCellDir, string? DisabledReason)
{
    public bool IsEnabled => DisabledReason is null;

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
    public static List<InstanceCellChoice> Collect(string workspaceRootDir, string? parentCellDir)
    {
        var items = new List<InstanceCellChoice>();
        if (workspaceRootDir is not { Length: > 0 } root || !Directory.Exists(root)) return items;
        CollectInto(root, root, NormalizeDir(parentCellDir), items);
        return items;
    }

    private static void CollectInto(string root, string dir, string? parentCellDirNormalized, List<InstanceCellChoice> items)
    {
        string ccellPath = Path.Combine(dir, CellFolder.CcellFileName);
        if (File.Exists(ccellPath))
        {
            if (parentCellDirNormalized is not null
                && string.Equals(NormalizeDir(dir), parentCellDirNormalized, System.StringComparison.OrdinalIgnoreCase))
                return; // R-fix-1: the parent cell itself — the one exclusion

            var primary = CellFolder.ResolvePrimary(dir, ViewType.Layout);
            bool hasLayout = primary.State is PrimaryState.SoleFile or PrimaryState.NamedPresent;
            string rel = Path.GetRelativePath(root, dir);
            string name = rel == "." ? Path.GetFileName(dir) : rel;
            items.Add(new InstanceCellChoice(name, dir, hasLayout ? null : "No layout view"));
            return; // a cell folder's own sub-folders (schematic/symbol/layout) are never cells themselves
        }

        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (var sub in subDirs)
        {
            string name2 = Path.GetFileName(sub);
            if (name2.StartsWith('.')) continue; // skip dotfolders (e.g. a future .git)
            CollectInto(root, sub, parentCellDirNormalized, items);
        }
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
