using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Scans a workspace to count (and rewrite) how many cells reference a given cell folder.
///
/// Covers both view kinds a cell can carry a <c>CellRef</c> in: <c>.csch</c> schematics (component
/// instances, under <c>node["Components"]</c>) and <c>.clay</c> layouts (layout instances, under
/// <c>node["Instances"]</c> — brief-L3b-hierarchy-navigation.md §4, the gap L3a left open). The
/// per-value matching logic (last-path-segment comparison against a stored <c>CellRef</c>) IS
/// generic — it was the surrounding file discovery and the hardcoded JSON array key that were
/// <c>.csch</c>-specific, not the matching itself. <see cref="ScannedKinds"/> is the one list that
/// needs a new entry for any future view kind that grows its own <c>CellRef</c>-bearing array.
/// </summary>
public static class CellUsageScanner
{
    private readonly record struct ScanKind(ViewType ViewType, string FilePattern, string ArrayPropertyName);

    private static readonly ScanKind[] ScannedKinds =
    [
        new(ViewType.Schematic, "*.csch", "Components"),
        new(ViewType.Layout,    "*.clay", "Instances"),
    ];

    /// <summary>
    /// Counts DISTINCT cells in the workspace (excluding the target itself) that contain at
    /// least one component/instance (schematic or layout) whose CellRef resolves to
    /// <paramref name="targetCellDir"/>. Best-effort: unreadable files are skipped.
    /// </summary>
    public static int CountReferencingCells(string workspaceRootDir, string targetCellDir)
    {
        targetCellDir = Path.GetFullPath(targetCellDir).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var count = 0;

        foreach (var cellDir in EnumerateCellFolders(workspaceRootDir))
        {
            var normCell = Path.GetFullPath(cellDir).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normCell, targetCellDir, StringComparison.OrdinalIgnoreCase))
                continue;

            if (CellReferencesTarget(cellDir, targetCellDir))
                count++;
        }

        return count;
    }

    // True when any view file (of any ScannedKind) in this cell folder references targetCellDir.
    private static bool CellReferencesTarget(string cellDir, string targetCellDir)
    {
        foreach (var kind in ScannedKinds)
        {
            var subDir = CellFolder.SubFolderPath(cellDir, kind.ViewType);
            if (!Directory.Exists(subDir)) continue;

            foreach (var filePath in Directory.EnumerateFiles(subDir, kind.FilePattern))
            {
                try
                {
                    if (FileReferencesTarget(filePath, kind.ArrayPropertyName, targetCellDir))
                        return true;
                }
                catch
                {
                    // Unreadable/malformed file — skip.
                }
            }
        }

        return false;
    }

    private static bool FileReferencesTarget(string filePath, string arrayPropertyName, string targetCellDir)
    {
        var fileDir = Path.GetDirectoryName(filePath)!;
        var json = File.ReadAllText(filePath);
        var node = JsonNode.Parse(json);
        var array = node?[arrayPropertyName]?.AsArray();
        if (array is null) return false;

        foreach (var item in array)
        {
            if (item is null) continue;
            var cellRef = item["CellRef"]?.GetValue<string?>();
            if (cellRef is null) continue;

            var resolved = Path.GetFullPath(Path.Combine(fileDir, cellRef))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(resolved, targetCellDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Enumerate every cell folder (contains .ccell) under rootDir, recursively.
    private static IEnumerable<string> EnumerateCellFolders(string rootDir)
    {
        if (!Directory.Exists(rootDir)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, CellFolder.CcellFileName)))
                yield return dir;
        }
    }

    /// <summary>
    /// Finds every schematic/layout view file in the workspace that contains a CellRef whose
    /// resolved cell name equals <paramref name="oldCellName"/>, rewrites it to
    /// <paramref name="newCellName"/>, and returns the list of updated paths (for logging).
    /// Best-effort: unreadable or unwritable files are skipped and their paths are added
    /// to <paramref name="failed"/>.
    /// </summary>
    /// <remarks>
    /// A CellRef is stored as a relative path like "../../OldName" inside the file's JSON. The last
    /// path segment (folder name) equals the cell name. We match on the EXACT folder name so we
    /// never do substring replacement.
    /// </remarks>
    public static IReadOnlyList<string> RewriteCellReferences(
        string            workspaceRootDir,
        string            oldCellName,
        string            newCellName,
        out List<string>  failed)
    {
        failed = [];
        var rewritten = new List<string>();

        foreach (var cellDir in EnumerateCellFolders(workspaceRootDir))
        {
            foreach (var kind in ScannedKinds)
            {
                var subDir = CellFolder.SubFolderPath(cellDir, kind.ViewType);
                if (!Directory.Exists(subDir)) continue;

                foreach (var filePath in Directory.EnumerateFiles(subDir, kind.FilePattern))
                {
                    try
                    {
                        if (RewriteFileCellRefs(filePath, kind.ArrayPropertyName, oldCellName, newCellName))
                            rewritten.Add(filePath);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{filePath}: {ex.Message}");
                    }
                }
            }
        }

        return rewritten;
    }

    // Returns true if the file was modified. Matches CellRef path segments — last segment is the
    // cell name. Both .csch and .clay use PascalCase JSON property names (no naming policy), so
    // "CellRef" is the literal key in both.
    private static bool RewriteFileCellRefs(string filePath, string arrayPropertyName, string oldCellName, string newCellName)
    {
        var json = File.ReadAllText(filePath);
        var node = JsonNode.Parse(json);
        if (node is null) return false;

        bool changed = false;
        var array = node[arrayPropertyName]?.AsArray();
        if (array is null) return false;

        foreach (var item in array)
        {
            if (item is null) continue;
            var cellRefNode = item["CellRef"];
            if (cellRefNode is null) continue;

            var cellRef = cellRefNode.GetValue<string?>();
            if (cellRef is null) continue;

            // The last path segment of the CellRef is the cell folder name.
            var lastName = Path.GetFileName(
                cellRef.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(lastName, oldCellName, StringComparison.OrdinalIgnoreCase)) continue;

            // Replace the last path segment with newCellName.
            var dir        = Path.GetDirectoryName(cellRef) ?? "";
            var newCellRef = string.IsNullOrEmpty(dir) ? newCellName : dir + "/" + newCellName;
            item["CellRef"] = JsonValue.Create(newCellRef);
            changed = true;
        }

        if (!changed) return false;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(filePath, node.ToJsonString(opts));
        return true;
    }
}
