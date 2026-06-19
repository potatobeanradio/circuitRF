using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Scans a workspace to count (and rewrite) how many cells reference a given cell folder.
/// </summary>
public static class CellUsageScanner
{
    /// <summary>
    /// Counts DISTINCT cells in the workspace (excluding the target itself) that contain at
    /// least one schematic component whose CellRef resolves to <paramref name="targetCellDir"/>.
    /// Best-effort: unreadable schematics are skipped.
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

    // True when any .csch in this cell folder's schematic subfolder references targetCellDir.
    private static bool CellReferencesTarget(string cellDir, string targetCellDir)
    {
        var schematicSubDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        if (!Directory.Exists(schematicSubDir)) return false;

        foreach (var cschPath in Directory.EnumerateFiles(schematicSubDir, "*.csch"))
        {
            try
            {
                var schematicDir = Path.GetDirectoryName(cschPath)!;
                var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);

                foreach (var comp in model.Components)
                {
                    if (comp.CellRef is null) continue;

                    var resolved = Path.GetFullPath(
                            Path.Combine(schematicDir, comp.CellRef))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (string.Equals(resolved, targetCellDir, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // Unreadable schematic — skip.
            }
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
    /// Finds every .csch in the workspace that contains a CellRef whose resolved cell name
    /// equals <paramref name="oldCellName"/>, rewrites it to <paramref name="newCellName"/>,
    /// and returns the list of updated .csch paths (for logging).
    /// Best-effort: unreadable or unwritable schematics are skipped and their paths are added
    /// to <paramref name="failed"/>.
    /// </summary>
    /// <remarks>
    /// A CellRef is stored as a relative path like "../../OldName" inside a .csch JSON file.
    /// The last path segment (folder name) equals the cell name. We match on the EXACT folder
    /// name so we never do substring replacement.
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
            var schematicSubDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            if (!Directory.Exists(schematicSubDir)) continue;

            foreach (var cschPath in Directory.EnumerateFiles(schematicSubDir, "*.csch"))
            {
                try
                {
                    if (RewriteSchematicCellRefs(cschPath, oldCellName, newCellName))
                        rewritten.Add(cschPath);
                }
                catch (Exception ex)
                {
                    failed.Add($"{cschPath}: {ex.Message}");
                }
            }
        }

        return rewritten;
    }

    // Returns true if the file was modified.  Matches CellRef path segments — last segment is the cell name.
    // .csch uses PascalCase JSON property names (no naming policy), so we use "Components" and "CellRef".
    private static bool RewriteSchematicCellRefs(string cschPath, string oldCellName, string newCellName)
    {
        var json = File.ReadAllText(cschPath);
        var node = JsonNode.Parse(json);
        if (node is null) return false;

        bool changed   = false;
        var components = node["Components"]?.AsArray();
        if (components is null) return false;

        foreach (var comp in components)
        {
            if (comp is null) continue;
            var cellRefNode = comp["CellRef"];
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
            comp["CellRef"] = JsonValue.Create(newCellRef);
            changed = true;
        }

        if (!changed) return false;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(cschPath, node.ToJsonString(opts));
        return true;
    }
}
