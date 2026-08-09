// Finds a kit's own parametric-cell library and declares it to circuitRF.
//
// A vendor kit's layout cells are routinely PROGRAMS — a package of scripts written against the
// parametric-cell API that circuitRF's own `tools/pcell-python/cni` implements. circuitRF can already
// run them (see PCellWorkerResolver and docs/design/pcell-vendor-bridge.md); what it could not do was
// FIND them, because the only way to declare one was a `pcell-generators.json` a kit author writes by
// hand — and a vendor kit knows nothing about circuitRF and ships none.
//
// So this discovers the package structurally and writes that declaration for the user. Nothing here
// names a supplier, a kit or a device: the one thing it matches on is the API's own module name,
// which is a FORMAT marker in exactly the sense every other recogniser in this repository uses.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>A parametric-cell package found inside a kit, and how to import it.</summary>
/// <param name="PythonPathRoot">
/// The directory that must be on <c>PYTHONPATH</c> for <see cref="PackageName"/> to import — the
/// first ancestor of the package that is not itself a package.
/// </param>
/// <param name="PackageName">The dotted module name, e.g. <c>somekit_cells.devices</c>.</param>
/// <param name="CellModuleCount">
/// How many of its modules are written against the cell API. Reported rather than used as a
/// threshold: one is a real (if small) library, and the count is what makes the choice checkable.
/// </param>
public sealed record KitPCellPackage(string PythonPathRoot, string PackageName, int CellModuleCount);

/// <summary>
/// Discovers a kit's parametric-cell package and writes the declaration that makes it reachable.
/// Framework-free (no Avalonia / Skia).
/// </summary>
public static class KitPCellLibrary
{
    /// <summary>
    /// The module a parametric cell imports its base class from. This is the marker, and it is the
    /// API's own name — the same kind of structural, supplier-free recogniser
    /// <c>PdkFormatRegistry</c> keys every other format off.
    /// </summary>
    public const string ApiModule = "cni.dlo";

    /// <summary>How far below the kit root to look. Deep enough for a tool-integration subtree,
    /// shallow enough that the search cannot wander into unrelated territory.</summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>Directories that never hold a kit's own source.</summary>
    private static readonly string[] SkipDirs =
        ["__pycache__", ".git", ".svn", ".hg", "node_modules", ".venv", "venv", "site-packages"];

    /// <summary>Bounded so a pathological tree costs a message rather than a hang.</summary>
    private const int MaxDirectories = 4000;

    /// <summary>Only the head of a module is read — the imports are at the top.</summary>
    private const int PeekBytes = 4096;

    /// <summary>The generated entry script's file name.</summary>
    public const string EntryScriptName = "kit_entry.py";

    // ── discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The parametric-cell package inside <paramref name="kitRoot"/>, or null when the kit has none —
    /// which is the ordinary case for a kit whose layouts are fixed artwork rather than programs, and
    /// is not an error.
    ///
    /// <para>When a kit holds several, the one with the most cell modules wins, ties broken by path so
    /// the answer is the same on every machine. A kit with two genuinely different cell libraries is
    /// not something to guess at silently — <paramref name="alsoFound"/> names the runners-up so the
    /// caller can say what it passed over.</para>
    /// </summary>
    public static KitPCellPackage? Find(
        string kitRoot, out IReadOnlyList<KitPCellPackage> alsoFound, int maxDepth = DefaultMaxDepth)
    {
        alsoFound = [];
        if (string.IsNullOrWhiteSpace(kitRoot) || !Directory.Exists(kitRoot)) return null;

        var found = new List<KitPCellPackage>();
        int visited = 0;

        Walk(kitRoot, 0);

        if (found.Count == 0) return null;

        var ordered = found
            .OrderByDescending(p => p.CellModuleCount)
            .ThenBy(p => p.PackageName, StringComparer.Ordinal)
            .ToList();

        var winner = ordered[0];

        // A wrapper package and the subpackage inside it are the same library seen at two levels, not
        // two libraries — reporting the one that lost as an alternative would offer the user a choice
        // that is not one.
        alsoFound =
        [
            .. ordered.Skip(1).Where(p =>
                !p.PackageName.StartsWith(winner.PackageName + ".", StringComparison.Ordinal) &&
                !winner.PackageName.StartsWith(p.PackageName + ".", StringComparison.Ordinal)),
        ];
        return winner;

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth || visited >= MaxDirectories) return;
            visited++;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            // A qualifying package is recorded AND descended into. A kit's cell library is routinely a
            // SUBpackage of a wrapper whose own top level holds a module or two of shared helpers — and
            // the wrapper is not the thing to register, because registration walks one package's own
            // modules and finds nothing in a wrapper (measured: the wrapper yields no cells, the
            // subpackage yields all of them). Picking the richest candidate is what gets that right.
            if (TryDescribePackage(dir) is { } pkg) found.Add(pkg);

            foreach (var sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (name.Length == 0 || name[0] == '.' ) continue;
                if (SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                Walk(sub, depth + 1);
            }
        }
    }

    /// <summary>Convenience overload for callers that do not care what was passed over.</summary>
    public static KitPCellPackage? Find(string kitRoot, int maxDepth = DefaultMaxDepth)
        => Find(kitRoot, out _, maxDepth);

    /// <summary>
    /// <paramref name="dir"/> as a cell package, or null when it is not one. A package is a directory
    /// with an <c>__init__.py</c> holding at least one module written against the cell API.
    /// </summary>
    private static KitPCellPackage? TryDescribePackage(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "__init__.py"))) return null;

        int cellModules = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.py"))
                if (MentionsApi(file)) cellModules++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (cellModules == 0) return null;

        // The dotted name is however many package levels sit above this one, which is exactly what an
        // `import` has to spell — computed from the directory tree rather than guessed from the leaf,
        // because a leaf name alone imports only when the package happens to be top-level.
        var parts = new List<string> { Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)) };
        string root = dir;
        while (Path.GetDirectoryName(root) is { Length: > 0 } parent
               && File.Exists(Path.Combine(parent, "__init__.py")))
        {
            parts.Insert(0, Path.GetFileName(Path.TrimEndingDirectorySeparator(parent)));
            root = parent;
        }

        string pathRoot = Path.GetDirectoryName(root) ?? root;
        return new KitPCellPackage(pathRoot, string.Join('.', parts), cellModules);
    }

    private static bool MentionsApi(string pyFile)
    {
        try
        {
            using var stream = File.OpenRead(pyFile);
            var buffer = new byte[PeekBytes];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return false;
            string head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            return head.Contains(ApiModule, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ── declaration ───────────────────────────────────────────────────────────

    /// <summary>The workspace folder a kit's generated declaration lives in.</summary>
    public static string FolderNameFor(string kitName)
    {
        var safe = new string([.. (kitName ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        if (string.IsNullOrWhiteSpace(safe)) safe = "kit";
        return safe + "-pcells";
    }

    /// <summary>
    /// Writes the declaration that makes <paramref name="pkg"/>'s cells reachable, into the WORKSPACE
    /// — never into the kit, which is routinely read-only and is not ours to edit.
    ///
    /// <para><b>Only ever creates; never overwrites.</b> Both files are ordinary text a user may
    /// correct — an interpreter that has the kit's own dependencies, a different package to register —
    /// and rewriting them on every open would silently discard that. Returns the folder either way, so
    /// a caller can name it.</para>
    /// </summary>
    /// <param name="problem">Non-null when nothing could be written. Never thrown: a declaration that
    /// could not be written costs the kit's layout artwork, not the import.</param>
    /// <returns>The folder holding the declaration, or null when it could not be written.</returns>
    public static string? EnsureDeclared(
        string workspaceRootDir, string kitName, KitPCellPackage pkg, out string? problem, out bool created)
    {
        problem = null;
        created = false;

        if (string.IsNullOrWhiteSpace(workspaceRootDir) || !Directory.Exists(workspaceRootDir))
        {
            problem = "no workspace is open, so there is nowhere to record it.";
            return null;
        }

        string dir = Path.Combine(workspaceRootDir, FolderNameFor(kitName));
        string manifestPath = Path.Combine(dir, PCellGeneratorManifest.FileName);
        string entryPath    = Path.Combine(dir, EntryScriptName);

        try
        {
            if (File.Exists(manifestPath)) return dir;   // the user's, now — leave it alone

            Directory.CreateDirectory(dir);

            var manifest = new PCellGeneratorManifest
            {
                Entry      = EntryScriptName,
                PythonPath = [StoreRef(pkg.PythonPathRoot, dir)],
                Sources    = [StoreRef(Path.Combine(pkg.PythonPathRoot, pkg.PackageName.Replace('.', Path.DirectorySeparatorChar)), dir)],
            };

            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, WriteOpts));
            if (!File.Exists(entryPath)) File.WriteAllText(entryPath, EntryScript(kitName, pkg));
            created = true;
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// A path as the manifest should store it: relative when it stays inside the folder tree the
    /// manifest lives in, absolute otherwise. Same rule <c>WorkspaceRefs</c> follows, and for the same
    /// reason — a relative reference survives the tree being moved, and nothing makes a reference to
    /// somewhere else portable, so storing it plainly is the honest option.
    /// </summary>
    private static string StoreRef(string target, string manifestDir)
    {
        try
        {
            string rel = Path.GetRelativePath(manifestDir, target);
            // Only when it genuinely stays within the workspace — a chain of "..", which is what a
            // kit outside it produces, is worse than an absolute path: it looks portable and is not.
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (ArgumentException) { /* fall through to absolute */ }
        return Path.GetFullPath(target);
    }

    private static string EntryScript(string kitName, KitPCellPackage pkg) =>
        $"""
        # Generated by circuitRF so '{kitName}' offers its own layout cells.
        #
        # This is an ordinary script and it is yours to edit: register a different package, register
        # more than one, or do whatever setup the kit's own cells need before they are registered.
        # circuitRF writes it once and never rewrites it.

        import circuitrf_pcell as crf
        from cni.bridge import register_kit

        result = register_kit("{pkg.PackageName}")
        for problem in result.problems:
            print(problem, file=__import__("sys").stderr)

        crf.run()

        """;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    };
}
