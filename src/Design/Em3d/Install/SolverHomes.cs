namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// Where the install assistant puts a solver: <c>&lt;root&gt;/&lt;tool&gt;/&lt;version&gt;/</c>, one
/// self-contained home per validated version (brief-em3d-24 §1, em-3d.md §7.2).
///
/// <para><b>A home is PUBLISHED when it holds <see cref="RecordFile"/></b>, and only then. Discovery's
/// <c>Installed</c> route reads nothing else, so a build that was cancelled, failed, or is still running
/// is invisible to it whatever it left on disk. A relocatable install (an upstream archive) is built in
/// <c>&lt;home&gt;.partial/</c> and renamed into place; a source build cannot be renamed, because Spack and
/// CMake write absolute library paths into every binary (measured on F0's Palace: every
/// <c>LC_RPATH</c> and install name is absolute), so it is built in place and the record is written
/// last. Either way, whatever an earlier attempt left — a <c>.partial</c>, or a home with no record —
/// is removed at the next attempt, and neither is ever a discovery location.</para>
///
/// <para><b>The root is the per-user state directory's <c>solvers/</c> — unless that path contains
/// whitespace.</b> On macOS it always does (<c>~/Library/Application Support/circuitRF</c>), and autotools
/// refuses to build there at all: <c>configure: error: unsafe srcdir value</c>, measured with Spack
/// v1.2.2 building <c>gmake</c> into a spaced prefix, 2026-09-25. Palace's dependency tree is mostly
/// autotools, so a Palace home under Application Support cannot be built. The fallback is
/// <c>~/.circuitRF/solvers</c>, which is still per user and still circuitRF's alone. A redirected state
/// directory (tests, the docs factory) is never escaped: there the root is the redirected one whatever it
/// contains, and a recipe that needs a space-free path refuses by saying so.</para>
/// </summary>
public static class SolverHomes
{
    /// <summary>The install record's file name inside a home.</summary>
    public const string RecordFile = "install.json";

    /// <summary>The suffix of a relocatable home under construction.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>The lock an install holds in its tool's directory for its whole length — and a removal
    /// takes, so the two exclude each other across processes.</summary>
    public const string InstallLockFile = ".install.lock";

    /// <summary>The suffix a home is renamed to before it is deleted (brief-em3d-25 R-em3d25-1d): a delete
    /// that stops part way leaves this, which is never a discovery location, and the next removal
    /// finishes it.</summary>
    public const string RemovingSuffix = ".removing";

    /// <summary>The directory holding every tool's homes, as described in the type's remarks.</summary>
    public static string DefaultRoot
    {
        get
        {
            string preferred = UserStateDirectory.SubDir("solvers");
            if (!HasWhitespace(preferred) || UserStateDirectory.IsRedirected
                || Environment.GetEnvironmentVariable(UserStateDirectory.EnvironmentVariable) is { Length: > 0 })
                return preferred;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Length == 0 ? preferred : Path.Combine(home, ".circuitRF", "solvers");
        }
    }

    /// <summary>Every root an install may have gone into, the one in use first: the space-free fallback
    /// and the state directory's own <c>solvers/</c> — so a record is found whichever one wrote it.</summary>
    public static IReadOnlyList<string> DefaultRoots
    {
        get
        {
            var roots = new List<string> { DefaultRoot };
            string preferred = UserStateDirectory.SubDir("solvers");
            if (!roots.Contains(preferred, StringComparer.Ordinal)) roots.Add(preferred);
            return roots;
        }
    }

    /// <summary>True when <paramref name="path"/> contains a space or any other whitespace.</summary>
    public static bool HasWhitespace(string path) => path.Any(char.IsWhiteSpace);

    /// <summary>The token a tool is filed under — also the recipe files' <c>tool</c> value.</summary>
    public static string ToolId(SolverTool tool) => tool switch
    {
        SolverTool.Palace => "palace",
        SolverTool.Gmsh   => "gmsh",
        _                 => "openems",
    };

    /// <summary>The tool named by <paramref name="id"/> (case-insensitive), or null.</summary>
    public static SolverTool? ToolFromId(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "palace"  => SolverTool.Palace,
        "gmsh"    => SolverTool.Gmsh,
        "openems" => SolverTool.OpenEms,
        _         => null,
    };

    public static string ToolDirectory(string root, SolverTool tool) => Path.Combine(root, ToolId(tool));

    public static string Home(string root, SolverTool tool, string version) => Path.Combine(ToolDirectory(root, tool), version);

    public static string Partial(string home) => home + PartialSuffix;

    /// <summary>Where an attempt's full log goes: beside the homes, not inside one, so it outlives the
    /// cleanup of a failed or cancelled attempt that it exists to explain.</summary>
    public static string LogDirectory(string root, SolverTool tool) => Path.Combine(ToolDirectory(root, tool), "logs");

    /// <summary>
    /// Every published home of <paramref name="tool"/> under <paramref name="roots"/>: a directory whose
    /// record reads and names that tool. Newest install first. A <c>.partial</c> or <c>.removing</c>
    /// directory is never returned, whatever it holds.
    /// </summary>
    public static IReadOnlyList<InstallRecord> Published(SolverTool tool, IEnumerable<string> roots)
    {
        var found = new List<InstallRecord>();
        foreach (string root in roots.Distinct(StringComparer.Ordinal))
        {
            string dir = ToolDirectory(root, tool);
            IEnumerable<string> homes;
            try { homes = Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string home in homes)
            {
                if (home.EndsWith(PartialSuffix, StringComparison.Ordinal) || home.EndsWith(RemovingSuffix, StringComparison.Ordinal)) continue;
                if (InstallRecord.TryRead(Path.Combine(home, RecordFile)) is { } record && record.Tool == tool)
                    found.Add(record);
            }
        }
        return found.OrderByDescending(r => r.InstalledAt).ThenBy(r => r.Home, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The published home <paramref name="program"/> lives in — the nearest ancestor holding
    /// <see cref="RecordFile"/> — or null for a program circuitRF did not install.
    /// </summary>
    public static string? HomeOf(string program)
    {
        DirectoryInfo? dir;
        try { dir = new FileInfo(Path.GetFullPath(program)).Directory; }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return null; }
        for (; dir is not null; dir = dir.Parent)
        {
            if (dir.Name.EndsWith(PartialSuffix, StringComparison.Ordinal) || dir.Name.EndsWith(RemovingSuffix, StringComparison.Ordinal)) return null;
            if (File.Exists(Path.Combine(dir.FullName, RecordFile))) return dir.FullName;
        }
        return null;
    }
}
