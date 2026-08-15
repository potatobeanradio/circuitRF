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
                }
                else if (DocumentFileRefs.IsDocument(abs) && included.Count > 0)
                {
                    var docDir = Path.GetDirectoryName(Path.GetFullPath(abs))!;
                    var rewritten = DocumentFileRefs.Rewrite(abs, referenced =>
                        RefFromDocument(referenced, docDir, plan, included, result));
                    if (rewritten is not null)
                    {
                        WriteText(zip, entryName, rewritten, result);
                        result.Repointed.Add(rel);
                        continue;
                    }
                }
                else if (DocumentFileRefs.IsDocument(abs))
                {
                    // Nothing to repoint, but still worth NOTICING an outside reference.
                    var docDir = Path.GetDirectoryName(Path.GetFullPath(abs))!;
                    foreach (var referenced in DocumentFileRefs.Find(abs))
                        RefFromDocument(referenced, docDir, plan, included, result);
                }

                WriteFile(zip, entryName, abs, result);
            }

            // ── Everything the user ticked ────────────────────────────────────
            foreach (var option in plan.Options.Where(o => o.Selected))
            {
                if (!option.IsDirectory)
                {
                    if (File.Exists(option.SourcePath))
                        WriteFile(zip, $"{rootName}/{option.ArchivePath}", option.SourcePath, result);
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
    private static string? RefFromDocument(
        string referenced, string documentDir, WorkspaceArchivePlan plan,
        Dictionary<string, string> included, ArchiveWriteResult result)
    {
        var full = Path.GetFullPath(referenced);

        // Inside the workspace already — it travels untouched, and rewriting it would only churn.
        if (WorkspaceArchiveScanner.IsInside(full, plan.WorkspaceDir)) return null;

        if (WorkspaceRelativeDestination(full, included) is not { } destination)
        {
            if (!result.StillExternal.Contains(full)) result.StillExternal.Add(full);
            return null;
        }

        var destinationAbs = Path.Combine(plan.WorkspaceDir, destination.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetRelativePath(documentDir, destinationAbs).Replace('\\', '/');
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

        if (obj["PdkRefs"] is JsonArray pdks)
            foreach (var entry in pdks)
                if (entry is JsonObject pdk && pdk["Path"]?.GetValue<string>() is { } stored &&
                    RemapStoredRef(stored, plan, included) is { } replacement)
                {
                    pdk["Path"] = replacement;
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
