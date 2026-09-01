using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// Works out what an <c>Archive Workspace…</c> would contain: the material that always travels, and
/// the material worth asking about.
///
/// <para>Framework-free (no Avalonia) so the whole decision — what is skipped, what is optional, what
/// each default is — is testable without a window.</para>
/// </summary>
public static class WorkspaceArchiveScanner
{
    /// <summary>Folder inside the archive that receives copied kits.</summary>
    public const string KitsFolder = "kits";

    /// <summary>Folder inside the archive that receives copied external files.</summary>
    public const string ExternalFolder = "external";

    /// <summary>The workspace's own results folder, which is the one branch the dialog itemises.</summary>
    public const string ResultsFolder = "results";

    // ── Skip rules ────────────────────────────────────────────────────────────

    /// <summary>
    /// Directories never archived: circuitRF's own rebuildable caches, and the clutter a file manager
    /// leaves behind. <c>.generated-cells</c> is the "pCell artwork files that are sometimes
    /// generated" — a pure cache that every layout can rebuild from its own recorded snapshots
    /// (<see cref="GeneratedCellsLifecycle"/>), so shipping it would only make the archive bigger.
    /// </summary>
    private static readonly string[] SkippedDirectories =
    [
        GeneratedCellStore.ReservedFolderName,
        "__MACOSX", ".Spotlight-V100", ".Trashes", ".fseventsd", ".TemporaryItems",
    ];

    private static readonly string[] SkippedFileNames =
    [
        ".DS_Store", "Thumbs.db", "desktop.ini", ".localized",
    ];

    private static readonly string[] SkippedExtensions =
    [
        ".tmp", ".temp", ".bak", ".swp", ".crdownload", ".source",
    ];

    /// <summary>True for a path the archive leaves out whatever the user ticks.</summary>
    public static bool IsSkipped(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment.Length == 0) continue;
            if (SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)) return true;
            // AppleDouble sidecars ("._Foo.csch") — metadata for a file that is itself archived.
            if (segment.StartsWith("._", StringComparison.Ordinal)) return true;
        }

        var name = Path.GetFileName(relativePath);
        if (SkippedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (SkippedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return true;
        // AtomicFile's in-flight temp ("<name>.crf-tmp-1234") — present only during a write.
        if (name.Contains(".crf-tmp", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // ── Defaults ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Which heading a results file sits under, and whether it starts ticked.
    ///
    /// <para><b>Everything under <c>results/</c> is ticked by default</b> (owner, 2026-09-01). The
    /// earlier rule left <c>.npy</c> off on the grounds that the recipient can re-simulate it — but
    /// re-simulating needs the whole kit chain, and a <c>.cdd</c> that arrives with no data behind it
    /// renders nothing at all. What the recipient is being sent is the RESULT; an archive that
    /// carries the Data Display and drops the data it plots is the one combination that is never
    /// wanted. The size is on every row and the group headings untick in one click, so the cost of
    /// this default is one tick for the rare sender who does not want the bulk.</para>
    /// </summary>
    public static (string Group, bool Selected) ClassifyResult(string fileName)
    {
        var ext = Path.GetExtension(fileName);

        if (string.Equals(ext, ".cdd", StringComparison.OrdinalIgnoreCase))
            return ("Data Displays", true);

        if (IsTouchstone(ext))
            return ("Touchstone", true);

        if (string.Equals(ext, ".npy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase))
            return ("Analysis", true);

        // The loadpull interchange a Data Display reads back (see the CLI's `lp` verb) — data, not
        // clutter, and unreproducible without re-running the sweep that made it.
        if (string.Equals(ext, ".spl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".lpcwave", StringComparison.OrdinalIgnoreCase))
            return ("Loadpull", true);

        return ("Other", true);
    }

    /// <summary>`.s1p`…`.s99p`, plus the `.ts` spelling.</summary>
    public static bool IsTouchstone(string extension)
    {
        if (string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase)) return true;
        if (extension.Length < 4) return false;
        if (extension[0] != '.') return false;
        if (char.ToLowerInvariant(extension[1]) != 's') return false;
        if (char.ToLowerInvariant(extension[^1]) != 'p') return false;
        for (int i = 2; i < extension.Length - 1; i++)
            if (!char.IsAsciiDigit(extension[i])) return false;
        return extension.Length > 3;
    }

    /// <summary>Order the dialog shows the results headings in.</summary>
    public static readonly string[] ResultGroupOrder = ["Data Displays", "Touchstone", "Loadpull", "Analysis", "Other"];

    // ── The scan ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the plan for the workspace rooted at <paramref name="workspaceDir"/>.
    ///
    /// <para>Kit sizes are left unmeasured (-1): a vendor kit can be tens of thousands of files, and
    /// the dialog must open now and fill the number in behind it — see
    /// <see cref="MeasureDirectory"/>.</para>
    /// </summary>
    public static WorkspaceArchivePlan Scan(string workspaceDir)
    {
        workspaceDir = Path.GetFullPath(workspaceDir);
        var plan = new WorkspaceArchivePlan { WorkspaceDir = workspaceDir };

        var resultsRoot = Path.Combine(workspaceDir, ResultsFolder);

        foreach (var file in EnumerateFilesSafe(workspaceDir))
        {
            var rel = Rel(workspaceDir, file);

            if (IsSkipped(rel)) { plan.SkippedPaths.Add(rel); continue; }

            if (IsInside(file, resultsRoot))
            {
                var (group, selected) = ClassifyResult(Path.GetFileName(file));
                plan.Options.Add(new ArchiveOption
                {
                    Kind        = ArchiveOptionKind.Result,
                    DisplayName = Rel(resultsRoot, file),
                    SourcePath  = file,
                    ArchivePath = rel,
                    Group       = group,
                    Selected    = selected,
                    SizeBytes   = SizeOf(file),
                });
                continue;
            }

            plan.AlwaysIncluded.Add(rel);
            plan.AlwaysIncludedBytes += Math.Max(0, SizeOf(file));
        }

        AddKits(plan);
        AddExternalFiles(plan);

        return plan;
    }

    /// <summary>
    /// Kits the <c>.cws</c> references from OUTSIDE the workspace, ONE ROW PER NAMED KIT, so the
    /// choice is made kit by kit: this one travels inside the archive, that one stays a reference.
    /// A kit that already lives inside the workspace is not offered — it is part of the workspace and
    /// is archived unconditionally.
    ///
    /// <para><b>The name is the kit's own, not its folder's.</b> A PDK reference records the name the
    /// design asks for it by (<c>CwsPdkRef.Provider</c>) — change that and every placed part stops
    /// resolving — and that is the name the user knows the kit by, in the palette, in the Analyses
    /// panel and in Manage PDKs. The folder underneath is routinely called something else entirely
    /// (a version string, a delivery date, an additions folder), so titling the row with it would ask
    /// the user to recognise their kits by an accident of where the vendor's installer put them.</para>
    /// </summary>
    private static void AddKits(WorkspaceArchivePlan plan)
    {
        var cws = TryLoadCws(plan.WorkspaceDir);
        if (cws is null) return;

        // Resolved path → what to call it. Keyed by PATH, because that is what gets copied: two
        // references to one folder are one copy, and titling that copy with only one of the two names
        // would hide a kit the user is deciding about.
        var kits = new Dictionary<string, KitRef>(DocumentFileRefs.PathComparer);

        void Note(string stored, string? name, bool isPdk, bool isLibraryOnly)
        {
            if (string.IsNullOrWhiteSpace(stored)) return;

            string abs;
            try { abs = WorkspaceRefs.Resolve(stored, plan.WorkspaceDir); }
            catch { return; }

            if (IsInside(abs, plan.WorkspaceDir)) return;        // already travelling
            if (!Directory.Exists(abs) && !File.Exists(abs)) return;

            if (!kits.TryGetValue(abs, out var kit))
                kits[abs] = kit = new KitRef(abs);

            if (!string.IsNullOrWhiteSpace(name) && !kit.Names.Contains(name!, StringComparer.OrdinalIgnoreCase))
                kit.Names.Add(name!);
            kit.IsPdk         |= isPdk;
            kit.IsLibraryOnly |= isLibraryOnly;
        }

        // A legacy LibraryRef names no kit of its own, so its folder name is all there is.
        foreach (var stored in cws.LibraryRefs)
            Note(stored, FolderName(stored), isPdk: false, isLibraryOnly: false);

        foreach (var pdk in cws.PdkRefs ?? [])
            Note(pdk.Path, pdk.Provider, isPdk: true, isLibraryOnly: pdk.IsLibraryOnly);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kit in kits.Values.OrderBy(k => k.Title, StringComparer.OrdinalIgnoreCase))
        {
            var isDir = Directory.Exists(kit.Path);

            plan.Options.Add(new ArchiveOption
            {
                Kind        = ArchiveOptionKind.Kit,
                DisplayName = kit.Title,
                Detail      = kit.Detail,
                SourcePath  = kit.Path,
                // The FOLDER name is what lands on disk, not the kit name: a kit's own internal
                // references are written against its folder, and renaming it inside the archive would
                // be a change the kit never agreed to.
                ArchivePath = $"{KitsFolder}/{UniqueName(used, FolderName(kit.Path) ?? "kit")}",
                IsDirectory = isDir,
                Selected    = false,                              // the owner's default: kits are off
                SizeBytes   = isDir ? -1 : SizeOf(kit.Path),
            });
        }
    }

    /// <summary>One referenced kit, and every name this workspace knows it by.</summary>
    private sealed class KitRef(string path)
    {
        public string Path { get; } = path;
        public List<string> Names { get; } = [];
        public bool IsPdk { get; set; }
        public bool IsLibraryOnly { get; set; }

        /// <summary>What the row is called: the kit's own name(s), falling back to its folder.</summary>
        public string Title
        {
            get
            {
                var name = Names.Count > 0
                    ? string.Join(", ", Names)
                    : FolderName(Path) ?? Path;
                // R-pdk: a library-only reference supplies no parts at all — it is the compiled
                // models the OTHER kits' devices need, and a recipient who takes the parts but not
                // this one gets a workspace that opens and will not simulate.
                return IsLibraryOnly ? $"{name} (model library)" : name;
            }
        }

        /// <summary>The tooltip: where it actually is, and what kind of reference it is.</summary>
        public string Detail => $"{(IsPdk ? "PDK" : "Library")} · {Path}";
    }

    private static string? FolderName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return null;
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Files referenced from inside the workspace but living outside it — the ones that would arrive
    /// broken. Known Files come from the <c>.cws</c>; the rest are found by reading each document
    /// (<see cref="DocumentFileRefs"/>).
    /// </summary>
    private static void AddExternalFiles(WorkspaceArchivePlan plan)
    {
        var externals = new Dictionary<string, string>(DocumentFileRefs.PathComparer);   // abs → why

        if (TryLoadCws(plan.WorkspaceDir) is { } cws)
            foreach (var stored in cws.KnownFiles)
            {
                if (string.IsNullOrWhiteSpace(stored)) continue;
                string abs;
                try { abs = WorkspaceRefs.Resolve(stored, plan.WorkspaceDir); }
                catch { continue; }
                if (File.Exists(abs) && !IsInside(abs, plan.WorkspaceDir))
                    externals.TryAdd(abs, "Known File");
            }

        // EVERY document in the archive is read, not only the unconditional ones. A `.cdd` lives
        // under `results/` and is therefore an OPTION, so scanning `AlwaysIncluded` alone was blind
        // to exactly the references a Data Display needs to render without re-simulating — which is
        // the other half of why an archive arrived with no data behind its displays (2026-09-01).
        var documents = plan.AlwaysIncluded
            .Select(rel => Path.Combine(plan.WorkspaceDir, rel.Replace('/', Path.DirectorySeparatorChar)))
            .Concat(plan.Options.Where(o => o.Kind == ArchiveOptionKind.Result).Select(o => o.SourcePath));

        foreach (var abs in documents)
        {
            if (!DocumentFileRefs.IsDocument(abs)) continue;

            foreach (var referenced in DocumentFileRefs.Find(abs, plan.WorkspaceDir))
                if (!IsInside(referenced, plan.WorkspaceDir))
                    externals.TryAdd(referenced, Path.GetFileName(abs));
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abs, _) in externals.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = UniqueName(used, Path.GetFileName(abs));
            plan.Options.Add(new ArchiveOption
            {
                Kind        = ArchiveOptionKind.ExternalFile,
                // Same rule as a kit: the row is named, the path is the tooltip. Two files that
                // share a name are still distinct rows, and their archived copies are already
                // de-duplicated by `UniqueName` above.
                DisplayName = Path.GetFileName(abs),
                Detail      = abs,
                SourcePath  = abs,
                ArchivePath = $"{ExternalFolder}/{name}",
                // ON by default: unlike a kit, this is one small file, and without it the design the
                // recipient opens is missing a piece of itself.
                Selected    = true,
                SizeBytes   = SizeOf(abs),
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursive byte total for a folder, skipping what the archive would skip, and giving up at
    /// <paramref name="fileLimit"/> files rather than walking a vendor kit forever. Returns the bytes
    /// counted; <paramref name="complete"/> says whether the walk finished.
    /// </summary>
    public static long MeasureDirectory(string dir, out bool complete, int fileLimit = 200_000)
    {
        long total = 0;
        int  count = 0;
        complete   = true;

        foreach (var f in EnumerateFilesSafe(dir))
        {
            if (IsSkipped(Rel(dir, f))) continue;
            if (++count > fileLimit) { complete = false; break; }
            total += Math.Max(0, SizeOf(f));
        }

        return total;
    }

    internal static IEnumerable<string> EnumerateFilesSafe(string dir)
    {
        if (!Directory.Exists(dir)) yield break;

        // Manual walk rather than EnumerateFiles(AllDirectories): one unreadable subfolder must not
        // abort the whole enumeration, which is exactly what the recursive overload does.
        var stack = new Stack<string>();
        stack.Push(dir);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(current); }
            catch { continue; }
            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(s);
            }
        }
    }

    internal static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return -1; }
    }

    internal static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static bool IsInside(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var r    = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, DocumentFileRefs.PathComparer == StringComparer.Ordinal
                                        ? StringComparison.Ordinal
                                        : StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "item";
        if (used.Add(name)) return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}{ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static CwsFile? TryLoadCws(string workspaceDir)
    {
        var path = Path.Combine(workspaceDir, ".cws");
        if (!File.Exists(path)) return null;
        try { return WorkspacePersistence.LoadFromFile(path); }
        catch { return null; }
    }
}
