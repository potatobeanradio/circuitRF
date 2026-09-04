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
/// <summary>
/// How many cells reference one cell, and — MW2 R-mw2-14 — whether any of them are in a DIFFERENT
/// open workspace. The two are reported separately because the confirmation has to word them
/// differently: a referrer in a workspace nobody has open cannot be found at all, so the honest
/// sentence is "no other open workspace references this," never "nothing references this."
/// </summary>
/// <param name="Count">Referencing cells in every scanned workspace, the target's own excluded.</param>
/// <param name="OtherWorkspaceRoots">The other open workspaces that hold at least one referrer.</param>
public readonly record struct CellUsage(int Count, IReadOnlyList<string> OtherWorkspaceRoots);

public static class CellUsageScanner
{
    private readonly record struct ScanKind(ViewType ViewType, string FilePattern, string ArrayPropertyName);

    private static readonly ScanKind[] ScannedKinds =
    [
        new(ViewType.Schematic, "*.csch", "Components"),
        new(ViewType.Layout,    "*.clay", "Instances"),
    ];

    /// <summary>
    /// Counts DISTINCT cells (excluding the target itself) that contain at least one
    /// component/instance — schematic or layout — whose <c>CellRef</c> resolves to
    /// <paramref name="targetCellDir"/>. Best-effort: unreadable files are skipped.
    /// </summary>
    /// <param name="otherOpenWorkspaceRoots">
    /// Every OTHER workspace open in this process (MW2 R-mw2-14). An external reference means a cell
    /// in another workspace can be a referrer, and this scan enumerated one workspace only — so
    /// deleting a cell in A that B references reported "0 references", deleted it, and broke B with
    /// nothing said. A referrer in a workspace nobody has open still cannot be found, which is why
    /// the caller's wording has to be "no other OPEN workspace references this."
    /// </param>
    public static CellUsage CountReferencingCells(
        string workspaceRootDir, string targetCellDir,
        IEnumerable<string>? otherOpenWorkspaceRoots = null)
    {
        string target = Normalize(targetCellDir);

        int count = CountIn(workspaceRootDir, target);
        List<string>? others = null;

        foreach (var root in otherOpenWorkspaceRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (string.Equals(Normalize(root), Normalize(workspaceRootDir), StringComparison.OrdinalIgnoreCase))
                continue;
            int n = CountIn(root, target);
            if (n == 0) continue;
            count += n;
            (others ??= []).Add(root);
        }

        return new CellUsage(count, others ?? []);
    }

    /// <summary>
    /// Counts cells in <paramref name="workspaceRootDir"/> that place at least one cell through the
    /// workspace alias <paramref name="alias"/> — what "removing this reference breaks N cells" is
    /// asking, before the alias is taken out of the <c>.cws</c>.
    ///
    /// <para>Matched on the reference's SPELLING, not on what it resolves to, and that is the
    /// difference from <see cref="CountReferencingCells"/>: the alias is exactly what is about to
    /// stop resolving, so the question is which documents name it — including the ones whose target
    /// is already missing, which are the documents a removal is most likely to be aimed at.</para>
    /// </summary>
    public static int CountCellsUsingWorkspaceAlias(string workspaceRootDir, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return 0;

        var count = 0;
        foreach (var cellDir in EnumerateCellFolders(workspaceRootDir))
            if (CellUsesAlias(cellDir, alias))
                count++;
        return count;
    }

    private static bool CellUsesAlias(string cellDir, string alias)
    {
        foreach (var kind in ScannedKinds)
        {
            var subDir = CellFolder.SubFolderPath(cellDir, kind.ViewType);
            if (!Directory.Exists(subDir)) continue;

            foreach (var filePath in Directory.EnumerateFiles(subDir, kind.FilePattern))
            {
                try
                {
                    var array = JsonNode.Parse(File.ReadAllText(filePath))?[kind.ArrayPropertyName]?.AsArray();
                    if (array is null) continue;

                    foreach (var item in array)
                    {
                        if (item?["CellRef"]?.GetValue<string?>() is not { } cellRef) continue;
                        if (ExternalCellRef.TryParse(cellRef, out string a, out _)
                            && string.Equals(a, alias, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch
                {
                    // Unreadable/malformed file — skip, exactly as the counter above does.
                }
            }
        }
        return false;
    }

    private static int CountIn(string workspaceRootDir, string normalizedTargetCellDir)
    {
        var count = 0;
        foreach (var cellDir in EnumerateCellFolders(workspaceRootDir))
        {
            if (string.Equals(Normalize(cellDir), normalizedTargetCellDir, StringComparison.OrdinalIgnoreCase))
                continue;

            if (CellReferencesTarget(cellDir, normalizedTargetCellDir))
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

            if (ResolvesToTarget(cellRef, fileDir, targetCellDir)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether one stored <c>CellRef</c>, read against the file that holds it, names
    /// <paramref name="targetCellDir"/> — the ONE matching rule, shared by the counter and the
    /// rewriter (MW2 R-mw2-15). A <c>ws://</c> reference expands through the referencing workspace's
    /// alias table first, so an external reference is compared by what it resolves to rather than by
    /// how it is spelled.
    /// </summary>
    private static bool ResolvesToTarget(string cellRef, string fileDir, string targetCellDir)
    {
        if (ExternalCellRef.ResolveCellDir(cellRef, fileDir) is not { } resolved) return false;
        return string.Equals(Normalize(resolved), targetCellDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return path; }
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
    /// Repoints every schematic/layout view file whose <c>CellRef</c> RESOLVES to
    /// <paramref name="oldCellDirAbs"/> so it names <paramref name="newCellName"/> instead, and
    /// returns the list of updated paths (for logging). Best-effort: unreadable or unwritable files
    /// are skipped and their paths are added to <paramref name="failed"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Matched by RESOLUTION, not by the last path segment</b> — MW2 R-mw2-15, and the
    /// reason external cell references could not ship without it. The old rule rewrote EVERY
    /// <c>CellRef</c> ending in the renamed name, including one pointing at a different cell of the
    /// same name that was never renamed. <c>workspace-and-project-tree.md</c> §4.1 stated that
    /// deliberately and named its own consequence — a name-keyed rewriter cannot tell
    /// <c>parts/R0402</c> from <c>board/R0402</c> — as a bounded, accepted risk. It stops being
    /// bounded the moment <c>ws://Other/cells/Amp</c> also ends in <c>Amp</c>: repointing that
    /// produces a reference to something that does not exist, in a workspace the rename had no
    /// business touching. <see cref="ResolvesToTarget"/> is the same rule the counter above already
    /// used correctly in this same file.</para>
    ///
    /// <para><b>It is called AFTER the folder has been moved</b>, so <paramref name="oldCellDirAbs"/>
    /// no longer exists — which is fine and is the point: a stored reference still SPELLS the old
    /// name, so resolving it lands on exactly that path. Path arithmetic needs no filesystem.</para>
    /// </remarks>
    /// <param name="oldCellDirAbs">The cell folder's absolute path BEFORE the rename.</param>
    /// <param name="alsoScanWorkspaceRoots">
    /// Other workspaces open in this process. A reference from one of them resolves to the same
    /// folder and is repaired in the same pass; leaving them out would rename a cell in A and break
    /// B silently, which is the failure R-mw2-14 names for deletion arriving through the rename door.
    /// </param>
    public static IReadOnlyList<string> RewriteCellReferences(
        string               workspaceRootDir,
        string               oldCellDirAbs,
        string               newCellName,
        out List<string>     failed,
        IEnumerable<string>? alsoScanWorkspaceRoots = null)
    {
        failed = [];
        var rewritten = new List<string>();
        string target = Normalize(oldCellDirAbs);

        var roots = new List<string> { workspaceRootDir };
        foreach (var extra in alsoScanWorkspaceRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            if (roots.Any(r => string.Equals(Normalize(r), Normalize(extra), StringComparison.OrdinalIgnoreCase)))
                continue;
            roots.Add(extra);
        }

        foreach (var root in roots)
        foreach (var cellDir in EnumerateCellFolders(root))
        {
            foreach (var kind in ScannedKinds)
            {
                var subDir = CellFolder.SubFolderPath(cellDir, kind.ViewType);
                if (!Directory.Exists(subDir)) continue;

                foreach (var filePath in Directory.EnumerateFiles(subDir, kind.FilePattern))
                {
                    try
                    {
                        if (RewriteFileCellRefs(filePath, kind.ArrayPropertyName, target, newCellName))
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

    // Returns true if the file was modified. Both .csch and .clay use PascalCase JSON property names
    // (no naming policy), so "CellRef" is the literal key in both.
    private static bool RewriteFileCellRefs(
        string filePath, string arrayPropertyName, string targetCellDir, string newCellName)
    {
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
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
            if (!ResolvesToTarget(cellRef, fileDir, targetCellDir)) continue;

            item["CellRef"] = JsonValue.Create(WithLastSegment(cellRef, newCellName));
            changed = true;
        }

        if (!changed) return false;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(filePath, node.ToJsonString(opts));
        return true;
    }

    /// <summary>
    /// The same reference with its final segment replaced — the one thing a rename changes.
    ///
    /// <para>A <c>ws://</c> reference is taken apart by its own parser rather than by
    /// <c>Path.GetDirectoryName</c>: on a platform whose separator is <c>/</c> that call collapses
    /// <c>ws://</c> to <c>ws:/</c>, which produces a reference that resolves to nothing and looks
    /// like a typo the user made.</para>
    /// </summary>
    private static string WithLastSegment(string cellRef, string newCellName)
    {
        if (ExternalCellRef.TryParse(cellRef, out string alias, out string rel))
        {
            var segments = rel.Split('/');
            segments[^1] = newCellName;
            return ExternalCellRef.RefFor(alias, string.Join('/', segments));
        }

        var dir = Path.GetDirectoryName(cellRef.TrimEnd('/', '\\')) ?? "";
        return string.IsNullOrEmpty(dir) ? newCellName : dir.Replace('\\', '/') + "/" + newCellName;
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
