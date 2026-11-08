using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Workspace;

/// <summary>One entry in the shipped examples index — a real workspace folder and how to name it.</summary>
/// <param name="Folder">The folder under the examples root. A leaf name, never a path.</param>
/// <param name="Title">What the menu calls it.</param>
/// <param name="Summary">One sentence, for the menu's tooltip.</param>
/// <param name="Directory">Where that folder actually is on this machine, resolved.</param>
public sealed record ExampleWorkspace(string Folder, string Title, string Summary, string Directory)
{
    /// <summary>The workspace manifest inside it.</summary>
    public string CwsPath => Path.Combine(Directory, WorkspaceFileName);

    internal const string WorkspaceFileName = ".cws";
}

/// <summary>
/// The example workspaces circuitRF ships — <b>ordinary workspace FOLDERS, copied out on request</b>
/// (File ▸ New ▸ Examples).
///
/// <h3>Why they are files and not an embedded resource</h3>
/// <para>They are real workspaces in the repository at <c>examples/</c>, openable in place, and the
/// owner edits them there (2026-09-15). An embedded resource would have to be unpacked before it
/// could be opened at all, which means the copy the application ships and the copy anyone edits are
/// two different artifacts with nothing keeping them in step. The shipped tree is the SAME tree —
/// <c>src/Ui/CircuitRF.Ui.csproj</c> copies <c>examples/**</c> into the app output the same way it
/// copies the user documentation and the PCell Python package.</para>
///
/// <h3>The index is data beside them, not a list in here</h3>
/// <para><c>examples/examples.json</c> gives the order, the menu titles and the one-line summaries.
/// Discovery-by-directory-scan was the alternative and it cannot express either of the first two;
/// a list compiled into circuitRF cannot be edited alongside the thing it describes. A folder named
/// in the index that is not on disk is SKIPPED rather than shown as a menu item that fails when
/// pressed — the two can disagree on a partial install, and the menu is the wrong place to find
/// out.</para>
///
/// <para>Framework-free. Reading the index writes nothing. <see cref="Install"/> is the headless
/// copy <c>circuitrf new example</c> makes; File ▸ New ▸ Examples is <c>ExampleWorkspaceInstall</c>
/// in <c>src/Ui</c>, which shares <see cref="InstallRefusal"/> and copies through
/// <c>WorkspaceCopy</c> so the copy's references are also repaired.</para>
/// </summary>
public static class ExampleWorkspaces
{
    /// <summary>The folder the examples live in, beside the executable and in the source tree.</summary>
    public const string RootFolderName = "examples";

    /// <summary>The index file inside it.</summary>
    public const string IndexFileName = "examples.json";

    /// <summary>Where a macOS .app keeps the examples, beside <c>Contents/MacOS</c> (see <see cref="ResolveRoot"/>).</summary>
    public const string MacBundleResourcesFolder = "Resources";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Every shipped example, in index order, filtered to the ones actually present.
    ///
    /// <para>Empty when the examples were not installed, which is a supported state and not an
    /// error: the menu hides itself rather than offering rows that cannot work.</para>
    /// </summary>
    /// <param name="searchFrom">Where to start looking. Null uses the running application's own
    /// directory — the only right answer in an installed build, and one a test cannot supply.</param>
    public static IReadOnlyList<ExampleWorkspace> All(string? searchFrom = null)
    {
        if (ResolveRoot(searchFrom) is not { } root) return [];

        IndexFile? index;
        try
        {
            index = JsonSerializer.Deserialize<IndexFile>(
                File.ReadAllText(Path.Combine(root, IndexFileName)), _json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
        if (index?.Examples is not { Count: > 0 } entries) return [];

        var found = new List<ExampleWorkspace>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Folder is not { Length: > 0 } folder) continue;

            // A leaf name, never a path: the index is data, and data that can name "../.." is data
            // that can reach out of the examples tree.
            if (folder.Contains('/') || folder.Contains('\\') || folder is "." or "..") continue;

            string dir = Path.Combine(root, folder);
            if (!File.Exists(Path.Combine(dir, ExampleWorkspace.WorkspaceFileName))) continue;

            found.Add(new ExampleWorkspace(
                folder,
                e.Title   is { Length: > 0 } t ? t : folder,
                e.Summary ?? "",
                dir));
        }
        return found;
    }

    /// <summary>
    /// The examples root, or null when there is none.
    ///
    /// <para>Beside the executable first — that is where a published build puts it — then walking up
    /// for the source tree, which is what makes <c>dotnet run</c> and a test find the same files.
    /// The same two-step <c>DocLauncher</c> uses for the bundled documentation, and for the same
    /// reason: a build-file change that works in one and fails in the other is how
    /// <c>tools/pcell-python</c> shipped broken once.</para>
    ///
    /// <para>On macOS the bundle scripts move the tree to <c>Contents/Resources/examples</c>, one
    /// level over from the executable in <c>Contents/MacOS</c>: codesign reads a dotted DIRECTORY
    /// under <c>Contents/MacOS</c> as a nested bundle and rejects the whole app, and the Model
    /// Composer example ships its fits in <c>&lt;name&gt;.cmc-cache/</c>. Windows and Linux keep
    /// the tree beside the executable.</para>
    /// </summary>
    public static string? ResolveRoot(string? searchFrom = null)
    {
        string baseDir = searchFrom ?? AppContext.BaseDirectory;

        string bundled = Path.Combine(baseDir, RootFolderName);
        if (File.Exists(Path.Combine(bundled, IndexFileName))) return Path.GetFullPath(bundled);

        string macResources = Path.Combine(baseDir, "..", MacBundleResourcesFolder, RootFolderName);
        if (File.Exists(Path.Combine(macResources, IndexFileName))) return Path.GetFullPath(macResources);

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, RootFolderName);
            if (File.Exists(Path.Combine(candidate, IndexFileName))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    // ── Finding one by name ──────────────────────────────────────────────────

    /// <summary>
    /// The example a caller named — by its folder or its menu title, ignoring case — or null.
    /// Exact, never a nearest match: a caller that asked for one example and was handed another would
    /// study the wrong design and say nothing about it.
    /// </summary>
    public static ExampleWorkspace? Find(IReadOnlyList<ExampleWorkspace> all, string name)
    {
        string wanted = name.Trim();
        return all.FirstOrDefault(e => string.Equals(e.Folder, wanted, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(e => string.Equals(e.Title,  wanted, StringComparison.OrdinalIgnoreCase));
    }

    // ── Installing one ───────────────────────────────────────────────────────

    /// <summary>Where <paramref name="example"/> would land under <paramref name="parentDir"/>: a
    /// folder named after the example, as File ▸ New ▸ Examples puts it.</summary>
    public static string DestinationFor(ExampleWorkspace example, string parentDir)
        => Path.Combine(parentDir, example.Folder);

    /// <summary>
    /// The sentence explaining why <paramref name="example"/> cannot be installed under
    /// <paramref name="parentDir"/>, or null when it can. Asked BEFORE anything is created, so a
    /// refusal never leaves a half-made workspace. Shared by File ▸ New ▸ Examples
    /// (<c>ExampleWorkspaceInstall</c>) and <c>circuitrf new example</c>.
    ///
    /// <para><b>An existing folder is a refusal, not an overwrite and not a rename.</b> Installing the
    /// same example twice into one place is a thing people do; overwriting would discard what they
    /// changed in the first copy, and silently landing in <c>S-Parameters 2</c> puts a folder
    /// somewhere they did not name.</para>
    /// </summary>
    public static string? InstallRefusal(ExampleWorkspace? example, string? parentDir)
    {
        if (example is null) return "There is no example to install.";
        if (!File.Exists(example.CwsPath))
            return $"The '{example.Title}' example is not installed with this copy of circuitRF.";

        if (string.IsNullOrWhiteSpace(parentDir))
            return "Choose a folder to put the example in.";

        if (WorkspaceCreate.UnwritableParentRefusal(
                parentDir, $"The '{example.Title}' example cannot be created there") is { } readOnly)
            return readOnly;

        string dest = DestinationFor(example, parentDir);
        if (Directory.Exists(dest) || File.Exists(dest))
            return $"'{dest}' already exists. Choose a different folder, or rename what is there — "
                 + "circuitRF will not write over a workspace you may have changed.";

        return WorkspaceTree.CopyRefusal(example.Directory, dest);
    }

    /// <summary>
    /// Copies <paramref name="example"/> into <paramref name="parentDir"/> by
    /// <see cref="WorkspaceTree.CopyFiles"/> — the headless install <c>circuitrf new example</c>
    /// performs. File ▸ New ▸ Examples also repairs the copy's references (<c>WorkspaceCopy</c>,
    /// in <c>src/Ui</c>); a shipped example references nothing outside itself and is stored in the
    /// spelling that repair would write, so the two copies are the same bytes —
    /// <c>ExamplesCliTests</c> compares them for every example.
    /// </summary>
    /// <exception cref="InvalidOperationException">Refused; see <see cref="InstallRefusal"/>.</exception>
    public static WorkspaceTreeCopyResult Install(ExampleWorkspace example, string parentDir)
    {
        if (InstallRefusal(example, parentDir) is { } refusal)
            throw new InvalidOperationException(refusal);

        return WorkspaceTree.CopyFiles(Path.GetFullPath(example.Directory),
                                       Path.GetFullPath(DestinationFor(example, parentDir)));
    }

    private sealed class IndexFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<Entry>? Examples { get; set; }
    }

    private sealed class Entry
    {
        public string? Folder  { get; set; }
        public string? Title   { get; set; }
        public string? Summary { get; set; }
    }
}
