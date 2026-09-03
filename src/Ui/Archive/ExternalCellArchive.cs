using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// External cell references, for the workspace archive (MW2 R-mw2-16).
///
/// <para><b>The archive used to drop them silently.</b> <c>DocumentFileRefs</c> recognises a
/// reference by whether the string resolves to a FILE that exists, and a <c>CellRef</c> names a
/// DIRECTORY — so the scan never saw one, the dialog never offered it, and the recipient got an
/// archive whose layouts referenced nothing. That is the same failure mode the <c>SnpPathPolicy</c>
/// note in <c>DocumentFileRefs</c>'s own header records for Touchstone files, arriving through a new
/// door.</para>
///
/// <para><b>What travels is the referenced CELLS, not the whole other workspace</b> — the cell each
/// document names, plus the sub-cells it instantiates, at their own workspace-relative offsets under
/// <c>refs/&lt;alias&gt;/</c>. Keeping those offsets is what lets each level's own relative
/// <c>CellRef</c> go on resolving untouched (R-mw2-17: a reference is to one cell, its sub-cells come
/// along by reference, always). The other workspace's <c>.cws</c> and its default technology travel
/// with them, so the copy IS a workspace and the alias still resolves to one — which is why the
/// repoint is a single line in the referencing <c>.cws</c> rather than a rewrite of every document.
/// </para>
///
/// <para><b>A <c>pdk://</c> reference inside a copied external cell travels only as the kit row it
/// already belongs to.</b> There is no second kit-packaging path here: if this workspace does not
/// itself reference that kit, the kit is not in the archive and the recipient is told so, exactly as
/// for a kit row they left unticked.</para>
/// </summary>
public static class ExternalCellArchive
{
    /// <summary>Folder inside the archive that receives copied referenced workspaces.</summary>
    public const string RefsFolder = "refs";

    /// <summary>One alias's worth of copied content: what to copy, and from where.</summary>
    /// <param name="Alias">The alias as the referencing <c>.cws</c> spells it.</param>
    /// <param name="OtherWorkspaceRoot">Where that workspace is on this machine.</param>
    /// <param name="Members">Files to copy, at their offsets relative to that workspace's root.</param>
    public sealed record AliasContent(
        string Alias, string OtherWorkspaceRoot, IReadOnlyList<ArchiveMember> Members);

    /// <summary>
    /// Every referenced workspace this workspace's documents actually USE, with the files that have
    /// to travel for those uses to keep resolving. An alias declared in the <c>.cws</c> but never
    /// referenced by any document contributes nothing — an archive should not carry another project
    /// because someone once opened the dialog.
    /// </summary>
    public static IReadOnlyList<AliasContent> Collect(string workspaceDir)
    {
        var byAlias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in EnumerateDesignDocuments(workspaceDir))
        foreach (var cellRef in ExternalRefsIn(doc))
        {
            if (!ExternalCellRef.TryParse(cellRef, out string alias, out _)) continue;
            if (ExternalCellRef.ResolveCellDir(cellRef, Path.GetDirectoryName(doc)) is not { } cellDir) continue;
            if (!Directory.Exists(cellDir)) continue;

            if (!byAlias.TryGetValue(alias, out var cells))
                byAlias[alias] = cells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cells.Add(Path.GetFullPath(cellDir));
        }

        var result = new List<AliasContent>();
        foreach (var (alias, topCells) in byAlias.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? otherRoot = ExternalCellRef.WorkspaceRootForAlias(workspaceDir, alias);
            if (otherRoot is null) continue;   // broken alias — reported by the marking, not archived

            // Each referenced cell drags its own hierarchy with it. Walked here rather than trusted to
            // the top cell alone, because a sub-cell's CellRef is relative to ITS level and can point
            // anywhere in that workspace.
            var cellDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var top in topCells) CollectHierarchy(top, otherRoot, cellDirs);

            var members = new List<ArchiveMember>();
            AddWorkspaceSpine(otherRoot, members);
            foreach (var dir in cellDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                AddFilesUnder(dir, otherRoot, members);

            if (members.Count > 0)
                result.Add(new AliasContent(alias, otherRoot, members));
        }

        return result;
    }

    /// <summary>The other workspace's <c>.cws</c> and its default technology — what makes the copied
    /// folder a WORKSPACE rather than a loose pile of cells, so the alias goes on resolving to one and
    /// nothing else in the archive has to be rewritten.</summary>
    private static void AddWorkspaceSpine(string otherRoot, List<ArchiveMember> members)
    {
        string cws = Path.Combine(otherRoot, ".cws");
        if (!File.Exists(cws)) return;
        members.Add(new ArchiveMember(cws, ".cws"));

        try
        {
            var parsed = WorkspacePersistence.LoadFromFile(cws);
            if (parsed.DefaultTechRef is not { Length: > 0 } techRef) return;
            string abs = Path.GetFullPath(Path.Combine(otherRoot, techRef));
            if (File.Exists(abs) && !WorkspaceRootFinder.IsOutside(abs, otherRoot))
                members.Add(new ArchiveMember(abs, Rel(otherRoot, abs)));
        }
        catch { /* an unreadable .cws still travels; it simply names no technology we can follow */ }
    }

    /// <summary>Walks one cell's own instance hierarchy inside its workspace, adding every cell folder
    /// it reaches. Bounded by the workspace: a reference that leaves it is a chain nobody chose, and
    /// following it would archive a third project.</summary>
    private static void CollectHierarchy(string cellDir, string otherRoot, HashSet<string> found)
    {
        if (!found.Add(Path.GetFullPath(cellDir))) return;

        foreach (var doc in EnumerateDesignDocuments(cellDir))
        foreach (var cellRef in AllCellRefsIn(doc))
        {
            if (ExternalCellRef.ResolveCellDir(cellRef, Path.GetDirectoryName(doc)) is not { } sub) continue;
            if (!Directory.Exists(sub)) continue;
            if (WorkspaceRootFinder.IsOutside(sub, otherRoot)) continue;
            CollectHierarchy(sub, otherRoot, found);
        }
    }

    private static void AddFilesUnder(string cellDir, string otherRoot, List<ArchiveMember> members)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(cellDir, "*", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var file in files)
        {
            string rel = Rel(otherRoot, file);
            if (WorkspaceArchiveScanner.IsSkipped(rel)) continue;
            members.Add(new ArchiveMember(file, rel));
        }
    }

    // ── Reading references out of a document ──────────────────────────────────

    private static IEnumerable<string> EnumerateDesignDocuments(string root)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in files)
        {
            var ext = Path.GetExtension(f);
            if (ext.Equals(".csch", StringComparison.OrdinalIgnoreCase)
             || ext.Equals(".clay", StringComparison.OrdinalIgnoreCase))
                yield return f;
        }
    }

    private static IEnumerable<string> ExternalRefsIn(string documentPath) =>
        AllCellRefsIn(documentPath).Where(ExternalCellRef.IsExternalRef);

    /// <summary>Every <c>CellRef</c> value in a <c>.csch</c>/<c>.clay</c>. Read straight out of the
    /// JSON rather than through the document models: this runs over a whole workspace during a
    /// dialog's scan, and it needs one string per instance, not a loaded view.</summary>
    private static IReadOnlyList<string> AllCellRefsIn(string documentPath)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(DocumentFileRefs.ReadText(documentPath)); }
        catch { return []; }

        var refs = new List<string>();
        foreach (var key in new[] { "Components", "Instances" })
        {
            if (node?[key]?.AsArray() is not { } array) continue;
            foreach (var item in array)
                if (item?["CellRef"]?.GetValue<string?>() is { Length: > 0 } r)
                    refs.Add(r);
        }
        return refs;
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
