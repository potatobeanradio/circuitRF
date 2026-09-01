using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// Which directory a stored reference is written against. circuitRF has three conventions, and a
/// reference cannot be repointed without knowing which one it belongs to.
/// </summary>
public enum RefBase
{
    /// <summary>The document's own folder — a bitmap underlay's <c>ImagePath</c>/<c>ImagePathRef</c>.</summary>
    Document,

    /// <summary>The workspace root — an SnP component's <c>File</c> parameter
    /// (<c>Elaborator.ResolveSnpFilePath</c>, <c>SnpPathPolicy.ToStored</c>).</summary>
    Workspace,

    /// <summary>The workspace's <c>results/</c> folder — a Data Display's data sources
    /// (<c>DataDisplayConfig.SourceAliases</c>, a trace's <c>SourcePath</c>).</summary>
    Results,
}

/// <summary>
/// Finds — and rewrites — the file references a design document holds: a bitmap underlay's
/// <c>ImagePath</c>/<c>ImagePathRef</c>, a Touchstone a component names in its <c>File</c> parameter,
/// a Data Display's data sources, anything else a document points at by path.
///
/// <para><b>One walk serves both jobs, on purpose.</b> The archive dialog has to LIST what would
/// break for the recipient, and the writer has to REPOINT exactly those same references — two
/// separately-written passes would disagree the first time a document format gained a field, and the
/// symptom would be a silently broken reference inside an archive that reported success.</para>
///
/// <para><b>A reference is recognised by what it RESOLVES TO, not by its property name.</b> A design
/// document is JSON with a dozen places a path can sit (and kits add more), so a name list would be
/// permanently incomplete. A string that resolves to a file that actually exists is a file reference;
/// nothing else in these files does that by accident (an expression like <c>1.5</c> or <c>50</c>
/// resolves to nothing).</para>
///
/// <para><b>A relative reference is tried against all THREE bases, not just the document's own
/// folder</b> (2026-09-01) — see <see cref="RefBase"/>. Resolving document-relative only is why an
/// archive arrived at a colleague with none of its Touchstone files: <c>SnpPathPolicy.ToStored</c>
/// writes an SnP <c>File</c> parameter WORKSPACE-relative and tolerates up to two levels ABOVE the
/// workspace root, so <c>"../refdata/dut.s2p"</c> in <c>ws/Amp/schematic/Amp.csch</c> named a file
/// outside the workspace that the scan never saw, the dialog never offered, and the recipient never
/// got.</para>
///
/// <para><b>Object KEYS are walked as well as values.</b> A <c>.cdd</c> stores its
/// <c>SourceAliases</c> as a map KEYED by each data source's path, so a values-only walk is blind to
/// exactly the references a Data Display needs in order to render anything.</para>
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

    /// <summary>
    /// The directories one document's stored references may be written against.
    /// <paramref name="WorkspaceDir"/> is null when the caller has no workspace, which reduces this
    /// to the document-relative-only behaviour it had before the three bases were separated.
    /// </summary>
    public readonly record struct RefContext(string DocumentDir, string? WorkspaceDir, string Extension = "")
    {
        public static RefContext For(string documentPath, string? workspaceDir) => new(
            Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? "",
            workspaceDir is null ? null : Path.GetFullPath(workspaceDir),
            Path.GetExtension(documentPath));

        public string? ResultsDir =>
            WorkspaceDir is null ? null : Path.Combine(WorkspaceDir, WorkspaceArchiveScanner.ResultsFolder);

        public string? DirFor(RefBase which) => which switch
        {
            RefBase.Document  => DocumentDir,
            RefBase.Workspace => WorkspaceDir,
            RefBase.Results   => ResultsDir,
            _                 => null,
        };
    }

    /// <summary>Absolute paths this document references and that exist on disk. Duplicates collapsed.</summary>
    public static IReadOnlyList<string> Find(string documentPath, string? workspaceDir = null)
    {
        var found = new List<string>();
        var seen  = new HashSet<string>(PathComparer);

        try
        {
            var root = JsonNode.Parse(ReadText(documentPath));
            Walk(root, RefContext.For(documentPath, workspaceDir), (abs, _) =>
            {
                if (seen.Add(abs)) found.Add(abs);
                return null;                      // find only — nothing is changed
            });
        }
        catch { /* an unreadable or non-JSON document contributes nothing, and is not an error here */ }

        return found;
    }

    /// <summary>
    /// Rewrites this document's references through <paramref name="rewrite"/> — absolute path plus
    /// the base the replacement will be READ against, in; the new stored ref out, or null to leave
    /// the reference untouched — and returns the new JSON text, or null when nothing changed, in
    /// which case the caller should archive the original bytes verbatim rather than a re-serialized
    /// copy.
    /// </summary>
    public static string? Rewrite(string documentPath, string? workspaceDir, Func<string, RefBase, string?> rewrite)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(ReadText(documentPath)); }
        catch { return null; }
        if (root is null) return null;

        int changed = Walk(root, RefContext.For(documentPath, workspaceDir), rewrite);
        return changed == 0 ? null : root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Resolves one stored reference the way the document loaders do: rooted stays as it is, relative
    /// is tried against each base in <paramref name="order"/> and reports the one that answered.
    /// </summary>
    public static (string Path, RefBase Base)? TryResolve(string storedRef, RefContext ctx, IEnumerable<RefBase>? order = null)
    {
        if (string.IsNullOrWhiteSpace(storedRef)) return null;
        // Cheap rejects first — the overwhelming majority of strings in these files are numbers,
        // names and expressions, and every one of them would otherwise cost a filesystem probe.
        if (storedRef.Length > 4096) return null;
        if (storedRef.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (Path.GetExtension(storedRef).Length is < 2 or > 12) return null;

        var native = storedRef.Replace('/', Path.DirectorySeparatorChar);

        try
        {
            if (Path.IsPathRooted(native))
            {
                var rooted = Path.GetFullPath(native);
                return File.Exists(rooted) ? (rooted, RefBase.Document) : null;
            }
        }
        catch { return null; }

        foreach (var which in order ?? DefaultOrder)
        {
            if (ctx.DirFor(which) is not { Length: > 0 } dir) continue;
            try
            {
                var abs = Path.GetFullPath(Path.Combine(dir, native));
                if (File.Exists(abs)) return (abs, which);
            }
            catch { }
        }

        return null;
    }

    private static readonly RefBase[] DefaultOrder   = [RefBase.Document, RefBase.Workspace, RefBase.Results];
    private static readonly RefBase[] WorkspaceFirst = [RefBase.Workspace, RefBase.Document, RefBase.Results];
    private static readonly RefBase[] ResultsFirst   = [RefBase.Results,   RefBase.Document, RefBase.Workspace];

    internal static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Which base a reference belongs to when the stored form gives no clue — an ABSOLUTE path.
    ///
    /// <para>This is the one place a property name is consulted, and it cannot be avoided: an
    /// absolute path carries no evidence of the convention its loader will use to read a
    /// REPLACEMENT, and the conventions disagree. Writing the wrong one produces an archive that
    /// reports success and opens with a broken reference — precisely the failure this file exists to
    /// prevent. Only the two non-default conventions need naming; everything else keeps the
    /// document-folder rule it has always had.</para>
    /// </summary>
    private static RefBase BaseForOpaqueRef(JsonObject? owner, RefContext ctx)
    {
        if (owner?["Name"]?.GetValue<string>() is { } name &&
            string.Equals(name, "File", StringComparison.OrdinalIgnoreCase))
            return RefBase.Workspace;                                   // an SnP component parameter

        if (string.Equals(ctx.Extension, ".cdd", StringComparison.OrdinalIgnoreCase))
            return RefBase.Results;                                     // a Data Display data source

        return RefBase.Document;
    }

    private static RefBase[] OrderFor(JsonObject? owner, RefContext ctx) => BaseForOpaqueRef(owner, ctx) switch
    {
        RefBase.Workspace => WorkspaceFirst,
        RefBase.Results   => ResultsFirst,
        _                 => DefaultOrder,
    };

    private static int Walk(JsonNode? node, RefContext ctx, Func<string, RefBase, string?> visit)
    {
        int changed = 0;

        switch (node)
        {
            case JsonObject obj:
            {
                var renames = new List<(string From, string To)>();

                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];

                    if (child is JsonValue v)
                    {
                        if (TryVisitString(Text(v), obj, ctx, visit) is { } replacement)
                        {
                            obj[key] = replacement;
                            changed++;
                        }
                    }
                    else changed += Walk(child, ctx, visit);

                    // The KEY itself may be a reference — a `.cdd`'s SourceAliases map is keyed by
                    // each data source's path.
                    if (TryVisitString(key, obj, ctx, visit) is { } newKey && !obj.ContainsKey(newKey))
                        renames.Add((key, newKey));
                }

                if (renames.Count > 0)
                {
                    Rekey(obj, renames);
                    changed += renames.Count;
                }
                break;
            }

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonValue v)
                    {
                        if (TryVisitString(Text(v), null, ctx, visit) is { } replacement)
                        {
                            arr[i] = replacement;
                            changed++;
                        }
                    }
                    else changed += Walk(child, ctx, visit);
                }
                break;
        }

        return changed;
    }

    private static string? Text(JsonValue value) => value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Renames keys in place, keeping every other entry in its original order.</summary>
    private static void Rekey(JsonObject obj, List<(string From, string To)> renames)
    {
        var map     = renames.ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal);
        var entries = obj.Select(kv => (Key: map.GetValueOrDefault(kv.Key, kv.Key), Value: kv.Value?.DeepClone())).ToList();

        obj.Clear();
        foreach (var (k, val) in entries) obj[k] = val;
    }

    private static string? TryVisitString(string? s, JsonObject? owner, RefContext ctx, Func<string, RefBase, string?> visit)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TryResolve(s, ctx, OrderFor(owner, ctx)) is not { } hit) return null;

        // A rooted reference resolves as itself and so says nothing about how a replacement would be
        // read; the write-back base comes from the property (and the document type) instead.
        var writeBase = Path.IsPathRooted(s!.Replace('/', Path.DirectorySeparatorChar))
            ? BaseForOpaqueRef(owner, ctx)
            : hit.Base;

        var replacement = visit(hit.Path, writeBase);
        return replacement is not null && replacement != s ? replacement : null;
    }
}
