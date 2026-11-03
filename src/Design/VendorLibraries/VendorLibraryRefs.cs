using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// A workspace's vendor-library references — the <c>vendorlib://</c> part reference, how it resolves,
/// and the edits a user can make to the list (brief vendorlib-1 R-vl1-1).
///
/// <para><b>Framework-free, and the only place these rules live.</b> File ▸ Add Vendor Library…, the
/// Manage Libraries dialog and <c>circuitrf import library</c> all call these functions, on
/// <c>PdkReferenceManager</c>'s terms; none of them holds a rule of its own.</para>
///
/// <code>
/// File:  vendorlib://Caps/capacitors/series-a.lib
/// .cws:  VendorLibraries: [ { Name: "Caps", Path: "/libraries/caps" } ]
/// </code>
/// </summary>
public static class VendorLibraryRefs
{
    /// <summary>Marks a SpiceModel <c>File</c> as a part of a referenced vendor library.</summary>
    public const string Scheme = "vendorlib://";

    // ── The reference form ────────────────────────────────────────────────────

    public static bool IsVendorRef(string? file)
        => file is not null && file.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <c>vendorlib://Name/rel/path</c>. The name runs to the FIRST <c>/</c> and the path takes
    /// the rest — the split <see cref="ExternalCellRef.TryParse"/> makes for <c>ws://</c>, and for the
    /// same reason: the remainder is the library's own spelling.
    /// </summary>
    public static bool TryParse(string? file, out string name, out string libraryRelPath)
    {
        name = libraryRelPath = "";
        if (!IsVendorRef(file)) return false;

        string rest = file!.Trim()[Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;

        name           = rest[..slash];
        libraryRelPath = rest[(slash + 1)..];
        return true;
    }

    /// <summary>The stored <c>File</c> value for one file of one library, with <c>/</c> separators.</summary>
    public static string RefFor(string name, string libraryRelPath)
        => $"{Scheme}{name}/{libraryRelPath.Replace('\\', '/').TrimStart('/')}";

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// The absolute file a <c>vendorlib://</c> reference names, resolved through the nearest ancestor
    /// <c>.cws</c> of <paramref name="schematicDir"/> — the walk-up every SpiceModel path already
    /// takes, so the editor, the extractor and the CLI cannot disagree.
    ///
    /// <para>Null with a <paramref name="reason"/> when it cannot be resolved, and the reasons are
    /// deliberately different: a name the workspace does not reference is not the same problem as a
    /// referenced library whose folder is missing, and reporting them alike sends the user to the
    /// wrong repair (<c>pdk-import.md</c> §5).</para>
    /// </summary>
    public static string? ResolveFile(string? file, string? schematicDir, out string? reason)
    {
        reason = null;
        if (!TryParse(file, out string name, out string rel))
        {
            reason = $"'{file}' is not a vendor-library reference (vendorlib://<library>/<file>).";
            return null;
        }

        string? wsRoot = WorkspaceRootFinder.WorkspaceDirOf(schematicDir);
        if (wsRoot is null)
        {
            reason = $"'{file}' names a vendor library, and this document belongs to no workspace " +
                     "to look the library up in.";
            return null;
        }

        string? root = ResolveLibraryRoot(wsRoot, name, out reason);
        if (root is null) return null;

        try { return Path.GetFullPath(Path.Combine(root, CircuitRF.Core.RefPath.ToNative(rel))); }
        catch (Exception ex)
        {
            reason = $"'{file}' is not a usable path: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The library folder <paramref name="name"/> names in the workspace at
    /// <paramref name="workspaceRoot"/>. Null with a reason for an unknown name, an unset
    /// <c>${NAME}</c> token, or a folder that is not there.
    /// </summary>
    public static string? ResolveLibraryRoot(string workspaceRoot, string name, out string? reason)
    {
        reason = null;
        var map = MapFor(workspaceRoot);
        if (!map.TryGetValue(name, out string? stored))
        {
            reason = $"This workspace references no vendor library named '{name}'. Add it with " +
                     "File ▸ Add Vendor Library…, or rename the reference in the workspace file.";
            return null;
        }

        if (WorkspaceRefs.ResolveExternalRef(stored, workspaceRoot) is not { } root)
        {
            reason = $"The vendor library '{name}' is referenced at '{stored}', which names an unset " +
                     "environment variable.";
            return null;
        }
        if (!Directory.Exists(root))
        {
            reason = $"The vendor library '{name}' is referenced at '{root}', which is not there. " +
                     "Repair the reference in File ▸ Manage Libraries….";
            return null;
        }
        return root;
    }

    // Name → stored path for one workspace, re-read when the .cws's time or length changes. Asked per
    // placed part per resolve, so it is memoised; keyed on the file's stat rather than invalidated by
    // hand, because an Add from the CLI and one from the GUI both rewrite the file.
    private static readonly Dictionary<string, (DateTime Mtime, long Length, Dictionary<string, string> Map)> _memo =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _gate = new();

    private static Dictionary<string, string> MapFor(string workspaceRoot)
    {
        string cws = Path.Combine(workspaceRoot, WorkspacePersistence.FileName);
        DateTime mtime; long length;
        try
        {
            var info = new FileInfo(cws);
            if (!info.Exists) return new(StringComparer.OrdinalIgnoreCase);
            (mtime, length) = (info.LastWriteTimeUtc, info.Length);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }

        lock (_gate)
            if (_memo.TryGetValue(cws, out var hit) && hit.Mtime == mtime && hit.Length == length)
                return hit.Map;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in WorkspacePersistence.LoadFromFile(cws).VendorLibraries ?? [])
                if (!string.IsNullOrWhiteSpace(r.Name)) map.TryAdd(r.Name, r.Path);
        }
        catch { /* a .cws this build cannot read references nothing, which is not an error here */ }

        lock (_gate) _memo[cws] = (mtime, length, map);
        return map;
    }

    // ── The list ──────────────────────────────────────────────────────────────

    /// <param name="Name">The library's name — what every placed part's reference carries.</param>
    /// <param name="StoredPath">Exactly what <c>.cws</c> holds.</param>
    /// <param name="ResolvedPath">The stored path made absolute, whether or not it exists; null for an unset token.</param>
    /// <param name="Parts">Parts in the last finished index of it, or null when none has finished.</param>
    public sealed record Status(string Name, string StoredPath, string? ResolvedPath, bool Exists, int? Parts)
    {
        /// <summary>True when the library is outside the workspace and so does not travel with it.</summary>
        public bool IsExternal => Path.IsPathRooted(StoredPath) || StoredPath.Contains("${", StringComparison.Ordinal);
    }

    /// <summary>What the workspace references, and whether each is there — without reading any library.</summary>
    public static IReadOnlyList<Status> List(string workspaceRoot, IEnumerable<CwsVendorLibraryRef> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);
        return [.. refs.Select(r =>
        {
            string? resolved = WorkspaceRefs.ResolveExternalRef(r.Path, workspaceRoot);
            bool exists = resolved is not null && Directory.Exists(resolved);
            int? parts = exists ? VendorLibraryIndex.Latest(resolved!)?.Entries.Count : null;
            return new Status(r.Name, r.Path, resolved, exists, parts);
        })];
    }

    /// <summary>
    /// Adds — or repairs — the reference to the library at <paramref name="folder"/>, under
    /// <paramref name="name"/> (the folder's own name when null). Builds the index first: a folder with
    /// no readable part is refused with the build's sentence and the list is left unchanged.
    ///
    /// <para><b>Replaces by name, never appends a duplicate</b> — so "the library moved" is the same
    /// gesture as adding it, and every placed part resolves again without a schematic being
    /// touched.</para>
    /// </summary>
    /// <returns>The build, or null with <paramref name="problem"/> set.</returns>
    public static VendorLibraryBuild? AddOrRepair(
        string? workspaceRoot, List<CwsVendorLibraryRef> refs, string folder, string? name, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(refs);
        problem = null;

        if (!Directory.Exists(folder))
        {
            problem = $"'{folder}' is not a folder.";
            return null;
        }

        string chosen = string.IsNullOrWhiteSpace(name) ? DefaultName(folder) : name.Trim();
        if (NameProblem(chosen) is { } bad) { problem = bad; return null; }

        var build = VendorLibraryIndex.Build(folder);
        if (build.Refusal is { } refusal) { problem = refusal; return null; }
        VendorLibraryIndex.Remember(build);

        refs.RemoveAll(r => string.Equals(r.Name, chosen, StringComparison.OrdinalIgnoreCase));
        refs.Add(new CwsVendorLibraryRef
        {
            Name = chosen,
            Path = WorkspaceRefs.ToStoredRef(build.Root, workspaceRoot),
        });
        return build;
    }

    /// <summary>The name a library is offered under: its folder's name.</summary>
    public static string DefaultName(string folder)
    {
        string n = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return n.Length == 0 ? "Vendor library" : n;
    }

    /// <summary>Why <paramref name="name"/> cannot name a library, or null. It is the host part of a
    /// <c>vendorlib://</c> reference, so it may not hold a separator.</summary>
    public static string? NameProblem(string name)
        => string.IsNullOrWhiteSpace(name)
            ? "A vendor library needs a name."
            : name.IndexOfAny(['/', '\\']) >= 0
                ? $"'{name}' cannot name a vendor library: a name may not contain '/' or '\\'."
                : null;

    /// <summary>
    /// Drops a reference. Nothing is deleted from any schematic — parts placed from it keep their
    /// references and draw as unresolved until it is added back.
    /// </summary>
    /// <returns>How many of <paramref name="placedFiles"/> (placed parts' <c>File</c> values) this leaves unresolved.</returns>
    public static int Remove(List<CwsVendorLibraryRef> refs, string name, IEnumerable<string> placedFiles)
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(placedFiles);
        refs.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        return placedFiles.Count(f => (TryParse(f, out string n, out _) || ModelLibrary.VendorModelRefs.TryParse(f, out n, out _, out _))
                                   && string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    // ── What a picked folder is ───────────────────────────────────────────────

    /// <summary>What Manage Libraries' Add found a picked folder to be.</summary>
    public enum FolderKind
    {
        /// <summary>Readable <c>.subckt</c> parts and no kit structure.</summary>
        VendorLibrary,
        /// <summary>No kit structure and no readable part, but encrypted ones: refused with the build's sentence.</summary>
        UnreadableLibrary,
        /// <summary>Anything else — handed to the kit import, which says what it makes of it.</summary>
        Kit,
    }

    /// <summary>
    /// Classifies a folder by CONTENT so Add never has to ask which kind it is (R-vl1-4b): readable
    /// <c>.subckt</c> parts and no kit structure is a vendor library.
    ///
    /// <para><b>The kit test is structural and cheap on purpose.</b> Running the kit import over a
    /// folder to see what it holds was measured at over two minutes on a generated 35,000-part
    /// library — it reads netlists — so it cannot be the question asked before the answer is known.
    /// A kit is recognised instead by what a vendor library never has: a device-provider manifest, or
    /// any file that is neither a SPICE model file nor documentation (symbols, catalogs, artwork,
    /// compiled models, layer data). The index then decides between a library and a refusal.</para>
    /// </summary>
    /// <remarks><b>A file named after one of the library's own parts is not kit structure.</b> An edition
    /// can ship a schematic symbol per part beside its models — tens of thousands of small files, each
    /// named after the part it draws — and counting those as "a file that is neither a model nor
    /// documentation" sent the whole library to the kit import. So the manifest is checked first, the
    /// index is built, and only then are the other files judged, with each part's own companion files
    /// set aside by name.</remarks>
    public static (FolderKind Kind, VendorLibraryBuild? Build) Classify(string folder)
    {
        if (HasManifest(folder)) return (FolderKind.Kit, null);
        var build = VendorLibraryIndex.Build(folder);
        if (HasKitStructure(folder, build)) return (FolderKind.Kit, null);
        if (build.Entries.Count > 0) return (FolderKind.VendorLibrary, build);
        if (build.EncryptedFiles > 0) return (FolderKind.UnreadableLibrary, build);
        return (FolderKind.Kit, null);
    }

    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".htm", ".html", ".md", ".rtf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".url",
        ".png", ".jpg", ".jpeg", ".gif", ".zip",
    };

    private static bool HasManifest(string folder)
        => File.Exists(Path.Combine(folder, CircuitRF.Core.Devices.External.DeviceWorkerManifest.FileName));

    private static bool HasKitStructure(string folder, VendorLibraryBuild build)
    {
        var spice = new HashSet<string>(Schematic.SpiceModelPeek.FileExtensions, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in FilesOutsideModelFolders(folder))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith('.')) continue;
                string ext = Path.GetExtension(f);
                if (spice.Contains(ext) || DocumentationExtensions.Contains(ext)) continue;
                if (build.Find(Path.GetFileNameWithoutExtension(f)) is not null) continue;   // a part's own companion
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// Every file under <paramref name="folder"/> except those inside a model folder (brief vendorlib-9 R-vl9-1a). A
    /// model's <c>.csch</c>, <c>.cnl</c>, <c>.va</c> and <c>.s2p</c> are its own files, not kit structure: judging
    /// them would send every model library to the kit import. A <c>library.clib</c> is the library's own manifest.
    /// </summary>
    private static IEnumerable<string> FilesOutsideModelFolders(string folder)
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (ModelLibrary.ModelFolder.Is(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir))
                if (!Path.GetFileName(f).Equals(ModelLibrary.ModelFolder.LibraryFileName, StringComparison.OrdinalIgnoreCase))
                    yield return f;
            foreach (string d in Directory.EnumerateDirectories(dir)) stack.Push(d);
        }
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    /// <param name="PartsIndexed">Parts a fresh index found. −1 when the library could not be read.</param>
    /// <param name="FilesUnreadable">Encrypted, empty or unreadable files, counted.</param>
    /// <param name="PlacedChecked">Placed parts from this library, each looked for in that index.</param>
    /// <param name="PlacedMissing">Of those, the ones the library no longer holds.</param>
    public sealed record ValidationResult(
        string Name, string? LibraryPath, int PartsIndexed, int FilesUnreadable,
        int PlacedChecked, int PlacedMissing,
        IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
    {
        public bool IsClean => Problems.Count == 0;

        /// <summary>The library-wide sentence about files that could not be read, or null — one of
        /// <see cref="Notes"/>, held apart because <c>check</c> reports it as a warning and the rest
        /// as information (brief vendorlib-3 R-vl3-4c).</summary>
        public string? UnreadableNote { get; init; }

        /// <summary>One line saying what was CHECKED, not merely whether it passed.</summary>
        public string Summary => PartsIndexed < 0
            ? $"{Name}: could not be read."
            : $"{Name}: {VendorLibraryBuild.Plural(PartsIndexed, "part", "parts")} indexed, " +
              $"{VendorLibraryBuild.Plural(FilesUnreadable, "file", "files")} unreadable, " +
              $"{VendorLibraryBuild.Plural(PlacedChecked, "placed part", "placed parts")} checked" +
              (PlacedMissing > 0 ? $", {PlacedMissing:N0} no longer in the library" : "") +
              (IsClean ? " — no problems found." : ".");
    }

    /// <summary>
    /// Re-indexes the library and checks every placed part against it — the explicit action, since an
    /// open is silent. <paramref name="placedParts"/> are the <c>File</c>/<c>Name</c> pairs of placed
    /// SpiceModel instances; those naming other libraries are ignored.
    /// </summary>
    public static ValidationResult Validate(
        string workspaceRoot, CwsVendorLibraryRef reference,
        IEnumerable<(string File, string Name)> placedParts)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(placedParts);

        var placed = placedParts
            .Where(p => (TryParse(p.File, out string n, out _)
                         || ModelLibrary.VendorModelRefs.TryParse(p.File, out n, out _, out _))
                     && string.Equals(n, reference.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        string? root = WorkspaceRefs.ResolveExternalRef(reference.Path, workspaceRoot);
        if (root is null || !Directory.Exists(root))
            return new ValidationResult(reference.Name, root, -1, 0, placed.Count, 0,
                [$"The library folder '{root ?? reference.Path}' is not there."], []);

        var build = VendorLibraryIndex.Build(root);
        VendorLibraryIndex.Remember(build);
        int unreadable = build.EncryptedFiles + build.EmptyFiles + build.UnreadableFiles;

        var problems = new List<string>();
        var notes    = new List<string>();
        if (build.Refusal is { } refusal)
            return new ValidationResult(reference.Name, root, -1, unreadable, placed.Count, 0, [refusal], []);

        var held = new HashSet<(string, string)>(
            build.Entries.Select(e => (e.File.ToLowerInvariant(), e.Part.ToLowerInvariant())));
        int missing = 0;
        foreach (var (file, part) in placed)
        {
            // A placed model is its vendormodel:// reference with an empty Name: the reference names the folder.
            if (ModelLibrary.VendorModelRefs.TryParse(file, out _, out string modelRel, out _))
            {
                if (build.Entries.Any(e => e.IsModel && e.File.Equals(modelRel, StringComparison.OrdinalIgnoreCase))) continue;
                missing++;
                problems.Add($"The model {modelRel} is placed in this workspace but the library no longer holds it.");
                continue;
            }
            TryParse(file, out _, out string rel);
            if (held.Contains((rel.Replace('\\', '/').ToLowerInvariant(), part.Trim().ToLowerInvariant()))) continue;
            missing++;
            problems.Add($"'{part}' in {rel} is placed in this workspace but the library no longer holds it.");
        }

        // The model folders (brief vendorlib-9 R-vl9-4a): brief 6's checks and D1's — the one function check runs.
        foreach (var (folder, reason) in build.UnreadableModels)
            problems.Add($"{folder}: {reason}");
        foreach (var e in build.Entries.Where(e => e.IsModel))
            foreach (var d in ModelLibrary.ModelFolderCheck.Validate(Path.Combine(root, CircuitRF.Core.RefPath.ToNative(e.File))))
                (d.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error ? problems : notes).Add($"{e.File}: {d.Render()}");
        foreach (var d in ModelLibrary.ModelLibraryCheck.Validate(root))
            problems.Add(d.Render());

        if (build.UnreadableSentence is { } s) notes.Add(s);
        if (build.Duplicates.Count > 0)
            notes.Add($"{VendorLibraryBuild.Plural(build.Duplicates.Count, "part name is", "part names are")} " +
                      $"defined in more than one file; the first by path is used (e.g. '{build.Duplicates[0].Part}' " +
                      $"in {build.Duplicates[0].IndexedFile}, not {build.Duplicates[0].SkippedFile}).");
        if (build.UnparsedFields > 0)
            notes.Add($"{VendorLibraryBuild.Plural(build.UnparsedFields, "header field", "header fields")} " +
                      "did not parse and were left empty.");

        return new ValidationResult(reference.Name, root, build.Entries.Count, unreadable,
                                    placed.Count, missing, problems, notes)
            { UnreadableNote = build.UnreadableSentence };
    }
}
