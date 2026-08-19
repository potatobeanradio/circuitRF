using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Ui.WBond;

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

    // ── wBond links ───────────────────────────────────────────────────────────

    /// <summary>
    /// Repoints every schematic that links a <c>.wBond</c> which has just been renamed, and every
    /// schematic whose link crosses the renamed cell folder.
    ///
    /// <para><b>Matched by RESOLUTION, not by name.</b> A stored link is a path relative to the
    /// schematic that holds it (<see cref="WBondPlacement.ResolveLinkedPath"/>), so two cells can
    /// legitimately own <c>layout/top.wBond</c> and a name-only match would repoint the wrong one.
    /// Each candidate is resolved and compared against the file that actually moved. The old cell
    /// name is substituted first when it appears as a path segment, which is what lets a link FROM
    /// ANOTHER CELL — <c>../../oldName/layout/oldStem.wBond</c>, already dangling after the folder
    /// rename — be recognised and repaired in the same pass.</para>
    /// </summary>
    /// <param name="layoutDirAbs">The renamed cell's <c>layout/</c> folder — where the file that
    /// moved both was and is, since only its stem changed.</param>
    /// <returns>The schematic files that were rewritten.</returns>
    public static IReadOnlyList<string> RewriteWBondLinks(
        string workspaceRootDir,
        string layoutDirAbs,
        string oldStem, string newStem,
        string oldCellName, string newCellName,
        out List<string> failed)
    {
        failed = [];
        var rewritten = new List<string>();
        // The link still SPELLS the old stem, so what a candidate must resolve to is the file's
        // pre-rename path — same folder (it moved with the cell), old name.
        var target = Path.GetFullPath(Path.Combine(layoutDirAbs, oldStem + WBondCell.FileExtension));

        foreach (var cellDir in EnumerateCellFolders(workspaceRootDir))
        {
            var subDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            if (!Directory.Exists(subDir)) continue;

            foreach (var filePath in Directory.EnumerateFiles(subDir, "*.csch"))
            {
                try
                {
                    if (RewriteFileWBondLinks(filePath, target, oldStem, newStem, oldCellName, newCellName))
                        rewritten.Add(filePath);
                }
                catch (Exception ex)
                {
                    failed.Add($"{filePath}: {ex.Message}");
                }
            }
        }

        return rewritten;
    }

    // Returns true if the file was modified.
    private static bool RewriteFileWBondLinks(
        string filePath, string targetAbs,
        string oldStem, string newStem, string oldCellName, string newCellName)
    {
        var schDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (schDir is null) return false;

        var node = JsonNode.Parse(File.ReadAllText(filePath));
        var array = node?["Components"]?.AsArray();
        if (array is null) return false;

        bool changed = false;
        foreach (var item in array)
        {
            var param = item?["Parameters"]?.AsArray()?
                .FirstOrDefault(p => p?["Name"]?.GetValue<string?>() == WBondPlacement.FileParameter);
            if (param is null) continue;

            var stored = param["Expression"]?.GetValue<string?>();
            if (string.IsNullOrWhiteSpace(stored)) continue;
            if (Path.IsPathRooted(stored)) continue;   // an absolute link is the author's own business

            // Tolerate a Windows-authored separator, exactly as ResolveLinkedPath does.
            var normalized = stored.Replace('\\', '/');
            var segments   = normalized.Split('/');
            if (!string.Equals(segments[^1], oldStem + WBondCell.FileExtension,
                               StringComparison.OrdinalIgnoreCase))
                continue;

            // Candidate 1: the link is already inside the renamed folder (a same-cell "../layout/x").
            // Candidate 2: the link names the OLD cell folder and is dangling — substitute and retry.
            string? rewrittenPath = null;
            if (Resolves(schDir, segments, targetAbs)) rewrittenPath = normalized;
            else
            {
                var swapped = (string[])segments.Clone();
                bool any = false;
                for (int i = 0; i < swapped.Length - 1; i++)
                    if (string.Equals(swapped[i], oldCellName, StringComparison.OrdinalIgnoreCase))
                    { swapped[i] = newCellName; any = true; }
                if (any && Resolves(schDir, swapped, targetAbs))
                    rewrittenPath = string.Join('/', swapped);
            }
            if (rewrittenPath is null) continue;

            var parts = rewrittenPath.Split('/');
            parts[^1] = newStem + WBondCell.FileExtension;
            param["Expression"] = JsonValue.Create(string.Join('/', parts));
            changed = true;
        }

        if (!changed) return false;

        File.WriteAllText(filePath, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;

        static bool Resolves(string schDir, string[] segments, string targetAbs)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(Path.Combine(schDir, string.Join('/', segments))),
                    targetAbs, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
