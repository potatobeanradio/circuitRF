using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Archive;

/// <summary>What an archive run actually did, for the message log.</summary>
public sealed class ArchiveWriteResult
{
    public string ZipPath { get; init; } = "";
    public int    FileCount { get; set; }
    public long   UncompressedBytes { get; set; }
    public long   ZipBytes { get; set; }

    /// <summary>Documents whose references were repointed at the archived copies.</summary>
    public List<string> Repointed { get; init; } = [];

    /// <summary>
    /// References left pointing outside the archive because the user did not include them. Reported
    /// rather than silently carried: the recipient WILL hit these, and finding out now is the point.
    /// </summary>
    public List<string> StillExternal { get; init; } = [];

    /// <summary>
    /// Files under <c>results/</c> that a document in the archive references and the user unticked.
    /// The reference still resolves to the right PLACE, so nothing can be repointed — the file is
    /// simply not there, and a Data Display that plots it will come up empty on the other machine.
    /// </summary>
    public List<string> ExcludedResults { get; init; } = [];
}

/// <summary>
/// Writes a workspace, plus whatever the user ticked, into one <c>.zip</c> — and repoints every
/// reference to the copies so the archive opens on someone else's machine.
///
/// <para><b>The archive has ONE root folder, named after the workspace.</b> Unzipping it in a file
/// manager therefore produces the workspace folder rather than scattering a hundred files into
/// whatever folder the recipient happened to be standing in.</para>
/// </summary>
public static class WorkspaceArchiveWriter
{
    public static ArchiveWriteResult Write(WorkspaceArchivePlan plan, string zipPath)
    {
        var result = new ArchiveWriteResult { ZipPath = zipPath };

        var rootName = Path.GetFileName(plan.WorkspaceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName)) rootName = "workspace";

        // absolute source path → where it lands, as a WORKSPACE-relative ref. Everything the rewrite
        // needs, in one map built before a single byte is written.
        var included = BuildIncludedMap(plan);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip    = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // ── The workspace's own files ─────────────────────────────────────
            foreach (var rel in plan.AlwaysIncluded)
            {
                var abs = Path.Combine(plan.WorkspaceDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) continue;

                var entryName = $"{rootName}/{rel}";

                if (string.Equals(rel, ".cws", StringComparison.OrdinalIgnoreCase))
                {
                    var rewritten = RewriteCws(abs, plan, included);
                    if (rewritten is not null)
                    {
                        WriteText(zip, entryName, rewritten, result);
                        result.Repointed.Add(".cws");
                        continue;
                    }
                    WriteFile(zip, entryName, abs, result);
                    continue;
                }

                WriteMaybeRepointed(zip, entryName, abs, rel, plan, included, result);
            }

            // ── Everything the user ticked ────────────────────────────────────
            foreach (var option in plan.Options.Where(o => o.Selected))
            {
                if (!option.IsDirectory)
                {
                    if (!File.Exists(option.SourcePath)) continue;

                    // A ticked RESULT is a document too — a `.cdd` naming an outside Touchstone has
                    // to be repointed exactly like a schematic, or it arrives plotting a path that
                    // exists only on the sender's machine (2026-09-01).
                    WriteMaybeRepointed(zip, $"{rootName}/{option.ArchivePath}", option.SourcePath,
                                        option.ArchivePath, plan, included, result);
                    continue;
                }

                // A row that names its own members copies EXACTLY those, at exactly those offsets —
                // a SPICE deck's include closure, rooted at a directory that routinely holds a great
                // deal the deck never reads. Everything else (a kit) copies its whole folder.
                if (option.Members.Count > 0)
                {
                    foreach (var member in option.Members)
                    {
                        if (!File.Exists(member.SourcePath)) continue;
                        WriteFile(zip, $"{rootName}/{option.ArchivePath}/{member.RelativePath}",
                                  member.SourcePath, result);
                    }
                    continue;
                }

                foreach (var file in WorkspaceArchiveScanner.EnumerateFilesSafe(option.SourcePath))
                {
                    var rel = WorkspaceArchiveScanner.Rel(option.SourcePath, file);
                    if (WorkspaceArchiveScanner.IsSkipped(rel)) continue;
                    WriteFile(zip, $"{rootName}/{option.ArchivePath}/{rel}", file, result);
                }
            }
        }

        result.ZipBytes = WorkspaceArchiveScanner.SizeOf(zipPath);
        return result;
    }

    /// <summary>
    /// Writes one file, repointing its references first when it is a document. A document with
    /// nothing to change is written VERBATIM — re-serializing JSON that did not need to change would
    /// churn every archive for no gain, and would drop anything this build's parser round-trips
    /// imperfectly.
    /// </summary>
    private static void WriteMaybeRepointed(
        ZipArchive zip, string entryName, string abs, string reportAs,
        WorkspaceArchivePlan plan, Dictionary<string, string> included, ArchiveWriteResult result)
    {
        if (DocumentFileRefs.IsDocument(abs))
        {
            var docDir = Path.GetDirectoryName(Path.GetFullPath(abs))!;

            if (included.Count > 0)
            {
                var rewritten = DocumentFileRefs.Rewrite(abs, plan.WorkspaceDir, (referenced, refBase) =>
                    RefFromDocument(referenced, docDir, refBase, plan, included, result));
                if (rewritten is not null)
                {
                    WriteText(zip, entryName, rewritten, result);
                    result.Repointed.Add(reportAs);
                    return;
                }
            }
            else
            {
                // Nothing to repoint, but still worth NOTICING a reference that will not resolve.
                foreach (var referenced in DocumentFileRefs.Find(abs, plan.WorkspaceDir))
                    RefFromDocument(referenced, docDir, RefBase.Document, plan, included, result);
            }
        }

        WriteFile(zip, entryName, abs, result);
    }

    // ── Reference mapping ─────────────────────────────────────────────────────

    /// <summary>
    /// Every included optional item, keyed by its absolute source path, valued by its
    /// workspace-relative destination.
    /// </summary>
    private static Dictionary<string, string> BuildIncludedMap(WorkspaceArchivePlan plan)
    {
        var map = new Dictionary<string, string>(DocumentFileRefs.PathComparer);
        foreach (var o in plan.Options)
        {
            if (!o.Selected) continue;
            if (o.Kind == ArchiveOptionKind.Result) continue;   // results keep their own paths

            // A row with named members maps each MEMBER, not its root folder: the root is a real
            // directory on this machine and the row copies only part of it, so mapping the folder
            // would repoint a reference to a sibling file that is not in the archive.
            if (o.Members.Count > 0)
            {
                foreach (var m in o.Members)
                    map[Path.GetFullPath(m.SourcePath)] = $"{o.ArchivePath}/{m.RelativePath}";
                continue;
            }

            map[Path.GetFullPath(o.SourcePath)] = o.ArchivePath;
        }
        return map;
    }

    /// <summary>
    /// Where a referenced file ends up, as a WORKSPACE-relative path — or null when it is not being
    /// archived (an unticked kit, an unticked external file).
    /// </summary>
    private static string? WorkspaceRelativeDestination(string referenced, Dictionary<string, string> included)
    {
        var full = Path.GetFullPath(referenced);

        if (included.TryGetValue(full, out var direct)) return direct;

        // A file INSIDE an included kit — a `.ccell` naming the kit's own netlist, say. The kit is
        // travelling, so this reference can and must travel with it.
        foreach (var (source, destination) in included)
        {
            if (!WorkspaceArchiveScanner.IsInside(full, source)) continue;
            var rel = WorkspaceArchiveScanner.Rel(source, full);
            return $"{destination}/{rel}";
        }

        return null;
    }

    /// <summary>
    /// The replacement a DOCUMENT should carry: a path relative to the document's own folder, which
    /// is how a document reference resolves once the archive is unpacked anywhere at all.
    /// </summary>
    /// <summary>
    /// The replacement a DOCUMENT should carry, written against <paramref name="refBase"/> — the same
    /// base the original was written against, which is the only base its loader will read the
    /// replacement with. Getting this wrong is silent: the archive reports success and opens with a
    /// reference pointing into a folder that does not exist.
    /// </summary>
    private static string? RefFromDocument(
        string referenced, string documentDir, RefBase refBase, WorkspaceArchivePlan plan,
        Dictionary<string, string> included, ArchiveWriteResult result)
    {
        var full = Path.GetFullPath(referenced);

        // Inside the workspace already — it travels untouched, and rewriting it would only churn.
        if (WorkspaceArchiveScanner.IsInside(full, plan.WorkspaceDir))
        {
            // …unless the user unticked it. Then it is not a reference to repoint, it is a file the
            // recipient will not have, and the only useful thing left to do is say so.
            var excluded = plan.Options.FirstOrDefault(
                o => !o.Selected && DocumentFileRefs.PathComparer.Equals(Path.GetFullPath(o.SourcePath), full));
            if (excluded is not null && !result.ExcludedResults.Contains(full)) result.ExcludedResults.Add(full);
            return null;
        }

        if (WorkspaceRelativeDestination(full, included) is not { } destination)
        {
            if (!result.StillExternal.Contains(full)) result.StillExternal.Add(full);
            return null;
        }

        var destinationAbs = Path.Combine(plan.WorkspaceDir, destination.Replace('/', Path.DirectorySeparatorChar));

        var baseDir = refBase switch
        {
            RefBase.Workspace => plan.WorkspaceDir,
            RefBase.Results   => Path.Combine(plan.WorkspaceDir, WorkspaceArchiveScanner.ResultsFolder),
            _                 => documentDir,
        };

        return Path.GetRelativePath(baseDir, destinationAbs).Replace('\\', '/');
    }

    /// <summary>
    /// Rewrites the <c>.cws</c>'s own three reference lists — LibraryRefs, PdkRefs[].Path and
    /// KnownFiles — at the JSON level rather than through <c>CwsFile</c>.
    ///
    /// <para>A round trip through the typed model would silently drop any field this build does not
    /// know about, and <c>.cws</c> is explicitly a format where that must not happen (see
    /// <c>CwsFile.DockLayout</c>'s own note). Editing the parsed tree changes the three arrays and
    /// leaves every other byte alone.</para>
    /// </summary>
    private static string? RewriteCws(string cwsPath, WorkspaceArchivePlan plan, Dictionary<string, string> included)
    {
        if (included.Count == 0) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(cwsPath)); }
        catch { return null; }
        if (root is not JsonObject obj) return null;

        int changed = 0;

        foreach (var listName in new[] { "LibraryRefs", "KnownFiles" })
            if (obj[listName] is JsonArray array)
                for (int i = 0; i < array.Count; i++)
                    if (array[i]?.GetValue<string>() is { } stored &&
                        RemapStoredRef(stored, plan, included) is { } replacement)
                    {
                        array[i] = replacement;
                        changed++;
                    }

        // MW2 R-mw2-4/-16: ReferencedWorkspaces[].Path is the ONE place a cross-workspace path is
        // written, so repointing an archived reference is this single line rather than a rewrite of
        // every document that used the alias — which is the whole reason the reference is an alias.
        foreach (var listName in new[] { "PdkRefs", "ReferencedWorkspaces" })
            if (obj[listName] is JsonArray entries)
                foreach (var entry in entries)
                    if (entry is JsonObject o2 && o2["Path"]?.GetValue<string>() is { } stored2 &&
                        RemapStoredRef(stored2, plan, included) is { } replacement2)
                    {
                        o2["Path"] = replacement2;
                        changed++;
                    }

        return changed == 0 ? null : root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string? RemapStoredRef(string stored, WorkspaceArchivePlan plan, Dictionary<string, string> included)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        string abs;
        try { abs = Schematic.WorkspaceRefs.Resolve(stored, plan.WorkspaceDir); }
        catch { return null; }

        // Already inside the workspace: it travels as it is.
        if (WorkspaceArchiveScanner.IsInside(abs, plan.WorkspaceDir)) return null;

        return WorkspaceRelativeDestination(abs, included);
    }

    // ── Zip plumbing ──────────────────────────────────────────────────────────

    private static void WriteFile(ZipArchive zip, string entryName, string sourcePath, ArchiveWriteResult result)
    {
        try
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var source = File.OpenRead(sourcePath);
            using var target = entry.Open();
            source.CopyTo(target);

            result.FileCount++;
            result.UncompressedBytes += Math.Max(0, WorkspaceArchiveScanner.SizeOf(sourcePath));
        }
        catch (IOException) { /* a file being written right now is skipped, not fatal */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteText(ZipArchive zip, string entryName, string text, ArchiveWriteResult result)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var target = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        target.Write(bytes, 0, bytes.Length);

        result.FileCount++;
        result.UncompressedBytes += bytes.Length;
    }
}
