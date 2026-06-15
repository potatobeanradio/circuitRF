namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Scans a workspace to count how many cells reference a given cell folder. Used by
/// the Remove Cell dialog to warn the user about broken references.
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
}
