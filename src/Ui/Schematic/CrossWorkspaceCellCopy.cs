using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Schematic;

/// <summary>How a copied cell's sub-cells travel (MW3 §4, R-mw3-7).</summary>
public enum SubCellMode
{
    /// <summary>Copy the whole reachable sub-tree into the destination workspace.</summary>
    Copy,

    /// <summary>Copy the top cell only; its sub-cells stay in their own workspace, addressed through
    /// the <c>ws://</c> alias MW2 §2 defines.</summary>
    KeepReferenced,
}

/// <summary>
/// Copying a cell from one workspace into another — the "Copy" outcome of MW3's gesture, and of
/// <c>File ▸ Add Cell to Workspace…</c>.
///
/// <para><b>Why <c>DuplicateCellAsync</c> is the right starting point and is not sufficient</b>
/// (R-mw3-7): it copies a folder within ONE workspace, where a sub-cell's relative <c>CellRef</c>
/// still resolves from the copy's new sibling position. Across workspaces both the base and the depth
/// change, so every <c>CellRef</c> inside the copy has to be rewritten — and rewritten by RESOLUTION,
/// never by a last-segment name match, which is the same rule MW2 R-mw2-15 had to install in
/// <see cref="CellUsageScanner"/> before external references could ship at all.</para>
///
/// <para><b>The plan is computed before anything is written</b> so the dialog can state what will
/// happen: which folders arrive, which names already exist (R-mw3-9 — collisions are asked about,
/// never auto-suffixed), and which kits the destination has not imported (R-mw3-8 — the trap, because
/// a <c>pdk://</c> reference is not a path and is not rewritten, so the copy resolves in the
/// destination only if that kit is mounted there).</para>
///
/// <para><b>A cross-workspace copy is a FILE operation and is not undoable</b> (R-mw3-10). Per-document
/// undo is not the right shape for a directory tree and a half-working one is worse than none, so the
/// prompt says so and this class simply reports the folder it wrote.</para>
/// </summary>
public static class CrossWorkspaceCellCopy
{
    private readonly record struct ScanKind(ViewType ViewType, string FilePattern, string ArrayPropertyName);

    /// <summary>The two view kinds that can carry a <c>CellRef</c> — the same pair
    /// <see cref="CellUsageScanner"/> scans, and the one list a future view kind has to join.</summary>
    private static readonly ScanKind[] ScannedKinds =
    [
        new(ViewType.Schematic, "*.csch", "Components"),
        new(ViewType.Layout,    "*.clay", "Instances"),
    ];

    // ── The plan ──────────────────────────────────────────────────────────────

    /// <summary>One cell folder that will be copied, and where it lands.</summary>
    public sealed record CopiedFolder(string SourceDir, string DestDir, bool IsTop);

    /// <summary>
    /// What a copy would do, computed without writing anything.
    /// </summary>
    /// <param name="Folders">Every cell folder that travels, the top cell first.</param>
    /// <param name="Collisions">Destination folders that already exist — R-mw3-9's prompt, not a suffix.</param>
    /// <param name="UnimportedKits">Kits the copied cells place that the destination workspace has
    /// not imported. Empty is the ordinary case; non-empty is R-mw3-8's warning.</param>
    /// <param name="NeedsSourceAlias">True when a reference inside the copy will have to point back
    /// into the source workspace — always so for <see cref="SubCellMode.KeepReferenced"/>, and also
    /// when a copied cell reaches something the copy does not carry.</param>
    /// <param name="SourceWorkspaceRoot">The workspace the cell came from, or null when it is in none.</param>
    public sealed record CellCopyPlan(
        string                       SourceCellDir,
        string                       DestCellDir,
        IReadOnlyList<CopiedFolder>  Folders,
        IReadOnlyList<string>        Collisions,
        IReadOnlyList<string>        UnimportedKits,
        bool                         NeedsSourceAlias,
        string?                      SourceWorkspaceRoot);

    /// <summary>
    /// Plans a copy of <paramref name="sourceCellDir"/> into <paramref name="destParentDir"/>.
    /// </summary>
    /// <param name="destWorkspaceRoot">The receiving workspace's root — what the kit check asks
    /// about, since a <c>pdk://</c> reference resolves against the referencing document's OWN parent
    /// workspace (MW1 R-mw1-5).</param>
    /// <param name="topName">The name the copied top cell takes; null keeps its own.</param>
    public static CellCopyPlan Plan(
        string sourceCellDir, string destParentDir, string destWorkspaceRoot,
        SubCellMode mode, string? topName = null)
    {
        string source        = Path.GetFullPath(sourceCellDir);
        string sourceParent  = Path.GetDirectoryName(source) ?? source;
        string? sourceRoot   = WorkspaceRootFinder.WorkspaceDirOf(source);
        string destTop       = Path.Combine(Path.GetFullPath(destParentDir),
                                            topName ?? Path.GetFileName(source));

        var reached = new List<string> { source };
        if (mode == SubCellMode.Copy && sourceRoot is not null)
            CollectHierarchy(source, sourceRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase), reached);

        // Where each reached cell lands. A sub-cell nested INSIDE the top cell folder follows it (so a
        // renamed top does not leave its own children behind at the old name); everything else keeps
        // its offset from the top cell's own parent, which preserves the source's organisation and
        // leaves most sibling references unchanged. A cell that would escape that parent — reachable
        // upward through a `../..` — falls back to a bare leaf name beside the top, because a copy
        // must never write outside the folder it was dropped on.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [source] = destTop };
        var folders = new List<CopiedFolder> { new(source, destTop, IsTop: true) };

        foreach (var dir in reached.Skip(1))
        {
            string dest;
            if (!WorkspaceRootFinder.IsOutside(dir, source))
                dest = Path.Combine(destTop, Path.GetRelativePath(source, dir));
            else
            {
                string rel = Path.GetRelativePath(sourceParent, dir);
                dest = rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
                    ? Path.Combine(Path.GetDirectoryName(destTop)!, Path.GetFileName(dir))
                    : Path.Combine(Path.GetDirectoryName(destTop)!, rel);
            }

            if (map.ContainsKey(dir)) continue;
            map[dir] = Path.GetFullPath(dest);
            folders.Add(new CopiedFolder(dir, Path.GetFullPath(dest), IsTop: false));
        }

        var collisions = folders
            .Where(f => Directory.Exists(f.DestDir) || File.Exists(f.DestDir))
            .Select(f => f.DestDir)
            .ToList();

        var kits = UnimportedKitsOf(reached, destWorkspaceRoot);

        bool needsAlias = sourceRoot is not null
            && (mode == SubCellMode.KeepReferenced
                ? HasSubCellIn(source, sourceRoot)
                : reached.Any(c => ReachesUnmapped(c, sourceRoot, map)));

        return new CellCopyPlan(source, destTop, folders, collisions, kits, needsAlias, sourceRoot);
    }

    // ── Executing it ──────────────────────────────────────────────────────────

    /// <summary>
    /// Performs the planned copy and rewrites every <c>CellRef</c> in it.
    ///
    /// <para>The alias, when one is needed, must already be recorded in the destination workspace's
    /// <c>.cws</c> — <see cref="ExternalCellRef.MakeCellRef"/> is the single production site for both
    /// forms and it reads that table, so a rewrite run before the alias exists would silently emit the
    /// raw relative path R-mw2-5 forbids producing.</para>
    /// </summary>
    /// <returns>The absolute path of the copied top cell.</returns>
    public static string Execute(CellCopyPlan plan)
    {
        foreach (var folder in plan.Folders)
            CopyDirectory(folder.SourceDir, folder.DestDir);

        var map = plan.Folders.ToDictionary(
            f => f.SourceDir, f => f.DestDir, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in plan.Folders)
            RewriteRefsIn(folder, map);

        return plan.DestCellDir;
    }

    private static void RewriteRefsIn(CopiedFolder folder, Dictionary<string, string> map)
    {
        foreach (var kind in ScannedKinds)
        {
            string destSub = CellFolder.SubFolderPath(folder.DestDir, kind.ViewType);
            if (!Directory.Exists(destSub)) continue;

            string sourceSub = CellFolder.SubFolderPath(folder.SourceDir, kind.ViewType);

            foreach (var file in Directory.EnumerateFiles(destSub, kind.FilePattern))
            {
                try { RewriteFile(file, sourceSub, destSub, kind.ArrayPropertyName, map); }
                catch { /* an unreadable view keeps whatever it had — reported by the bad-cell state */ }
            }
        }
    }

    private static void RewriteFile(
        string destFile, string sourceDocDir, string destDocDir,
        string arrayPropertyName, Dictionary<string, string> map)
    {
        var node = JsonNode.Parse(File.ReadAllText(destFile));
        if (node?[arrayPropertyName]?.AsArray() is not { } array) return;

        bool changed = false;
        foreach (var item in array)
        {
            if (item?["CellRef"]?.GetValue<string?>() is not { Length: > 0 } cellRef) continue;

            // A kit reference is not a path and is never rewritten — which is exactly what makes
            // R-mw3-8's warning necessary rather than optional.
            if (PdkKitRegistry.IsKitRef(cellRef)) continue;

            if (ExternalCellRef.ResolveCellDir(cellRef, sourceDocDir) is not { } resolved) continue;
            string abs = Path.GetFullPath(resolved);

            string target = map.TryGetValue(abs, out var copied) ? copied : abs;
            string rewritten = ExternalCellRef.MakeCellRef(destDocDir, target);
            if (string.Equals(rewritten, cellRef, StringComparison.Ordinal)) continue;

            item["CellRef"] = JsonValue.Create(rewritten);
            changed = true;
        }

        if (!changed) return;
        File.WriteAllText(destFile, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Walking the source ────────────────────────────────────────────────────

    /// <summary>Every cell one cell reaches, bounded by its own workspace. A reference that leaves it
    /// is a chain nobody chose, and following it would copy a third project — the same bound
    /// <see cref="Archive.ExternalCellArchive"/> applies for the same reason.</summary>
    private static void CollectHierarchy(
        string cellDir, string workspaceRoot, HashSet<string> seen, List<string> ordered)
    {
        if (!seen.Add(Path.GetFullPath(cellDir))) return;

        foreach (var (docDir, cellRef) in CellRefsOf(cellDir))
        {
            if (PdkKitRegistry.IsKitRef(cellRef)) continue;
            if (ExternalCellRef.ResolveCellDir(cellRef, docDir) is not { } sub) continue;
            if (!Directory.Exists(sub)) continue;
            if (WorkspaceRootFinder.IsOutside(sub, workspaceRoot)) continue;

            string abs = Path.GetFullPath(sub);
            if (seen.Contains(abs)) continue;
            ordered.Add(abs);
            CollectHierarchy(abs, workspaceRoot, seen, ordered);
        }
    }

    /// <summary>True when this cell places another cell of its own workspace — which is what makes
    /// <see cref="SubCellMode.KeepReferenced"/> need an alias back into it.</summary>
    private static bool HasSubCellIn(string cellDir, string workspaceRoot) =>
        CellRefsOf(cellDir).Any(r =>
            !PdkKitRegistry.IsKitRef(r.CellRef)
            && ExternalCellRef.ResolveCellDir(r.CellRef, r.DocDir) is { } sub
            && !WorkspaceRootFinder.IsOutside(sub, workspaceRoot));

    /// <summary>True when this cell reaches a cell of its own workspace that the copy is NOT
    /// carrying — a reference that will have to be written in the alias form.</summary>
    private static bool ReachesUnmapped(
        string cellDir, string workspaceRoot, Dictionary<string, string> map) =>
        CellRefsOf(cellDir).Any(r =>
            !PdkKitRegistry.IsKitRef(r.CellRef)
            && ExternalCellRef.ResolveCellDir(r.CellRef, r.DocDir) is { } sub
            && !WorkspaceRootFinder.IsOutside(sub, workspaceRoot)
            && !map.ContainsKey(Path.GetFullPath(sub)));

    /// <summary>The kits <paramref name="cellDirs"/> place that <paramref name="destWorkspaceRoot"/>
    /// has not imported. Distinct and ordered, because they go into a sentence.</summary>
    private static IReadOnlyList<string> UnimportedKitsOf(
        IEnumerable<string> cellDirs, string destWorkspaceRoot)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in cellDirs)
        foreach (var (_, cellRef) in CellRefsOf(dir))
        {
            if (!PdkKitRegistry.TryParse(cellRef, out string kit, out _)) continue;
            if (!PdkKitRegistry.HasKit(destWorkspaceRoot, kit)) missing.Add(kit);
        }
        return [.. missing];
    }

    /// <summary>Every <c>CellRef</c> in one cell folder's views, with the directory each was read
    /// from — a reference means nothing without the base it resolves against.</summary>
    private static IEnumerable<(string DocDir, string CellRef)> CellRefsOf(string cellDir)
    {
        foreach (var kind in ScannedKinds)
        {
            string sub = CellFolder.SubFolderPath(cellDir, kind.ViewType);
            if (!Directory.Exists(sub)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(sub, kind.FilePattern); }
            catch { continue; }

            foreach (var file in files)
            {
                JsonNode? node;
                try { node = JsonNode.Parse(File.ReadAllText(file)); }
                catch { continue; }

                if (node?[kind.ArrayPropertyName]?.AsArray() is not { } array) continue;
                foreach (var item in array)
                    if (item?["CellRef"]?.GetValue<string?>() is { Length: > 0 } r)
                        yield return (sub, r);
            }
        }
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
