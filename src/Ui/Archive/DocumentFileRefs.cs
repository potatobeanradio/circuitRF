using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// Finds — and rewrites — the file references a design document holds: a bitmap underlay's
/// <c>ImagePath</c>/<c>ImagePathRef</c>, a Touchstone a component names in its <c>File</c> parameter,
/// anything else a document points at by path.
///
/// <para><b>One walk serves both jobs, on purpose.</b> The archive dialog has to LIST what would
/// break for the recipient, and the writer has to REPOINT exactly those same references — two
/// separately-written passes would disagree the first time a document format gained a field, and the
/// symptom would be a silently broken reference inside an archive that reported success.</para>
///
/// <para><b>A reference is recognised by what it RESOLVES TO, not by its property name.</b> A design
/// document is JSON with a dozen places a path can sit (and kits add more), so a name list would be
/// permanently incomplete. A string that resolves to a file that actually exists — rooted as-is,
/// relative against the document's own folder, which is the convention
/// <c>SchematicPersistence</c> already follows for bitmaps — is a file reference; nothing else in
/// these files does that by accident (an expression like <c>1.5</c> or <c>50</c> resolves to
/// nothing).</para>
/// </summary>
public static class DocumentFileRefs
{
    /// <summary>Document types whose contents this understands.</summary>
    public static readonly string[] Extensions = [".csch", ".csym", ".clay", ".cdd", ".ccell", ".cnl"];

    public static bool IsDocument(string path) =>
        Array.Exists(Extensions, e => string.Equals(Path.GetExtension(path), e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a document as text. <c>.clay</c> may be gzipped on disk, and a caller that read the raw
    /// bytes as UTF-8 would see binary and quietly find no references at all.
    /// </summary>
    public static string ReadText(string path) => GzipTextFile.ReadAllTextAutoGzip(path);

    /// <summary>Absolute paths this document references and that exist on disk. Duplicates collapsed.</summary>
    public static IReadOnlyList<string> Find(string documentPath)
    {
        var found = new List<string>();
        var seen  = new HashSet<string>(PathComparer);

        try
        {
            var root = JsonNode.Parse(ReadText(documentPath));
            Walk(root, Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? "", abs =>
            {
                if (seen.Add(abs)) found.Add(abs);
                return null;                      // find only — nothing is changed
            });
        }
        catch { /* an unreadable or non-JSON document contributes nothing, and is not an error here */ }

        return found;
    }

    /// <summary>
    /// Rewrites this document's references through <paramref name="rewrite"/> (absolute path in, new
    /// stored ref out; null leaves the reference untouched) and returns the new JSON text, or null
    /// when nothing changed — in which case the caller should archive the original bytes verbatim
    /// rather than a re-serialized copy.
    /// </summary>
    public static string? Rewrite(string documentPath, Func<string, string?> rewrite)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(ReadText(documentPath)); }
        catch { return null; }
        if (root is null) return null;

        int changed = Walk(root, Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? "", rewrite);
        return changed == 0 ? null : root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Resolves one stored reference the way the document loaders do: rooted stays as it is, relative
    /// is taken against the document's own folder.
    /// </summary>
    public static string? TryResolve(string storedRef, string documentDir)
    {
        if (string.IsNullOrWhiteSpace(storedRef)) return null;
        // Cheap rejects first — the overwhelming majority of strings in these files are numbers,
        // names and expressions, and every one of them would otherwise cost a filesystem probe.
        if (storedRef.Length > 4096) return null;
        if (storedRef.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (Path.GetExtension(storedRef).Length is < 2 or > 12) return null;

        try
        {
            var native = storedRef.Replace('/', Path.DirectorySeparatorChar);
            var abs    = Path.IsPathRooted(native)
                ? Path.GetFullPath(native)
                : Path.GetFullPath(Path.Combine(documentDir, native));
            return File.Exists(abs) ? abs : null;
        }
        catch { return null; }
    }

    internal static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static int Walk(JsonNode? node, string documentDir, Func<string, string?> visit)
    {
        int changed = 0;

        switch (node)
        {
            case JsonObject obj:
                foreach (var key in new List<string>(obj.Select(kv => kv.Key)))
                {
                    var child = obj[key];
                    if (child is JsonValue v)
                    {
                        if (TryVisitValue(v, documentDir, visit) is { } replacement)
                        {
                            obj[key] = replacement;
                            changed++;
                        }
                    }
                    else changed += Walk(child, documentDir, visit);
                }
                break;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonValue v)
                    {
                        if (TryVisitValue(v, documentDir, visit) is { } replacement)
                        {
                            arr[i] = replacement;
                            changed++;
                        }
                    }
                    else changed += Walk(child, documentDir, visit);
                }
                break;
        }

        return changed;
    }

    private static string? TryVisitValue(JsonValue value, string documentDir, Func<string, string?> visit)
    {
        if (!value.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        if (TryResolve(s, documentDir) is not { } abs) return null;
        var replacement = visit(abs);
        return replacement is not null && replacement != s ? replacement : null;
    }
}
