using System.Text.Json;

namespace CircuitRF.Design.Em3d;

/// <summary>
/// What Spack has installed, read from its own install database — so a Palace built the way Palace's
/// documentation builds it is found with no setup at all.
///
/// <para><b>Why this exists.</b> Palace's own macOS/Linux recipe is a Spack environment with
/// <c>view: false</c> and <c>install_tree: root: $HOME/opt/spack</c>, <c>padded_length: 256</c>
/// (F0's install, <c>docs/design/em-3d-f0-findings.md</c>). Nothing from it ever lands on <c>PATH</c>:
/// the program sits at <c>~/opt/spack/__spack_path_placeholder__/…/darwin-m3/palace-&lt;version&gt;-&lt;hash&gt;/bin/palace</c>
/// and the user is expected to <c>spack load</c> it in each shell. A Finder-launched application has
/// no shell at all, so every route <see cref="SolverDiscovery"/> had before missed it.</para>
///
/// <para><b>The database, not a directory walk.</b> Every install tree keeps
/// <c>&lt;root&gt;/.spack-db/index.json</c>: one record per installed spec with its name, version,
/// prefix and dependencies by hash. The projection from spec to directory is configurable, and so is
/// the padding, so guessing a directory layout would find Palace on one machine and miss it on the
/// next. The database also answers the question a directory cannot: <b>which MPI this Palace was
/// linked against</b> — the only <c>mpirun</c> that is certain to launch it
/// (<see cref="MpiLauncherFor"/>).</para>
///
/// <para>Read-only, and never fatal: a database that is missing, locked mid-write or of a shape this
/// does not recognise yields nothing, and discovery carries on to its next route.</para>
/// </summary>
public static class SpackInstalls
{
    /// <summary>One installed spec.</summary>
    public sealed record Install(string Hash, string Name, string Version, string Prefix, long InstalledAt,
                                 IReadOnlyList<Dependency> Dependencies);

    /// <summary>A link to another installed spec, with the virtual packages it provides here
    /// (<c>mpi</c>, <c>blas</c>, …).</summary>
    public sealed record Dependency(string Name, string Hash, IReadOnlyList<string> Virtuals);

    /// <summary>
    /// The install-tree roots looked in, in order: <c>$SPACK_ROOT/opt/spack</c>, then Spack's own
    /// default beside a clone in the home directory (<c>~/spack/opt/spack</c>), then Palace's recipe's
    /// <c>~/opt/spack</c>, then the system-wide <c>/opt/spack</c>. Empty on Windows, where Spack does
    /// not build Palace. Every lookup takes its roots as an argument, so a test points one lookup at a
    /// tree of its own without touching what every other caller in the process reads.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots { get; } = MakeDefaultRoots();

    /// <summary>Installed specs named <paramref name="package"/> whose prefix still exists, most
    /// recently installed first.</summary>
    public static IReadOnlyList<Install> Find(string package, IReadOnlyList<string>? roots = null)
        => Databases(roots ?? DefaultRoots).SelectMany(d => d.Values)
                      .Where(i => string.Equals(i.Name, package, StringComparison.Ordinal) && Directory.Exists(i.Prefix))
                      .OrderByDescending(i => i.InstalledAt)
                      .ThenBy(i => i.Prefix, StringComparer.Ordinal)
                      .ToList();

    /// <summary>
    /// The <c>mpirun</c> of the MPI that the Spack install containing <paramref name="program"/> was
    /// linked against, or null when <paramref name="program"/> is not in a Spack prefix, the prefix has
    /// no MPI dependency, or that MPI has no <c>bin/mpirun</c>. A different MPI's launcher — Homebrew's,
    /// say — may start the ranks and then fail to wire them together, so this outranks <c>PATH</c>.
    /// </summary>
    public static string? MpiLauncherFor(string program, IReadOnlyList<string>? roots = null)
    {
        string full;
        try { full = Path.GetFullPath(program); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException) { return null; }

        foreach (var db in Databases(roots ?? DefaultRoots))
        {
            var owner = db.Values.FirstOrDefault(i => IsUnder(full, i.Prefix));
            if (owner is null) continue;
            var mpi = owner.Dependencies.FirstOrDefault(d => d.Virtuals.Contains("mpi", StringComparer.Ordinal));
            if (mpi is null || !db.TryGetValue(mpi.Hash, out var provider)) return null;
            string launcher = Path.Combine(provider.Prefix, "bin", "mpirun");
            return File.Exists(launcher) ? launcher : null;
        }
        return null;
    }

    // ── reading the databases ────────────────────────────────────────────────────────────────

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, Install> Installs)> Cache = new(StringComparer.Ordinal);

    /// <summary>Every database under <paramref name="roots"/>, parsed once per change of the file.</summary>
    private static IEnumerable<Dictionary<string, Install>> Databases(IReadOnlyList<string> roots)
    {
        foreach (string root in roots)
            foreach (string file in DatabaseFiles(root))
                if (Load(file) is { } installs) yield return installs;
    }

    /// <summary>
    /// <c>.spack-db/index.json</c> at <paramref name="root"/>, or below it through Spack's path padding —
    /// a chain of directories named <c>__spack_path_placeholder__</c> whose last link is truncated
    /// (<c>__spack_pat</c>), so only the <c>__spack_p</c> prefix is relied on. Bounded: padding is at most
    /// a few hundred characters, so 64 levels is far past any real tree.
    /// </summary>
    internal static IEnumerable<string> DatabaseFiles(string root)
    {
        var level = new List<string> { root };
        for (int depth = 0; depth < 64 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (string dir in level)
            {
                string db = Path.Combine(dir, ".spack-db", "index.json");
                if (FileExists(db)) { yield return db; continue; }
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir, "__spack_p*")
                                           .Where(d => new DirectoryInfo(d).LinkTarget is null)
                                           .Order(StringComparer.Ordinal));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            level = next;
        }
    }

    private static Dictionary<string, Install>? Load(string file)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        lock (Gate)
        {
            if (Cache.TryGetValue(file, out var hit) && hit.Stamp == stamp) return hit.Installs;
        }

        Dictionary<string, Install>? installs;
        try { installs = Parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        if (installs is not null)
            lock (Gate) Cache[file] = (stamp, installs);
        return installs;
    }

    /// <summary>
    /// The installed records of one <c>index.json</c> (database version 7/8: <c>database.installs</c>
    /// keyed by hash, each with <c>spec</c>, <c>path</c> and <c>installed</c>). Null when the text is not
    /// that shape. A record that is not installed, has no prefix, or is an external (<c>path</c> null) is
    /// skipped — an external's prefix is not Spack's to answer for.
    /// </summary>
    internal static Dictionary<string, Install>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("database", out var database)
                || !database.TryGetProperty("installs", out var installs)
                || installs.ValueKind != JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, Install>(StringComparer.Ordinal);
            foreach (var entry in installs.EnumerateObject())
            {
                var r = entry.Value;
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (!r.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.True) continue;
                if (!r.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                if (!r.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object) continue;

                string name    = Str(spec, "name") ?? "";
                string version = Str(spec, "version") ?? "";
                long at = r.TryGetProperty("installation_time", out var t) && t.ValueKind == JsonValueKind.Number
                          && t.TryGetDouble(out double seconds) ? (long)seconds : 0;

                var deps = new List<Dependency>();
                if (spec.TryGetProperty("dependencies", out var dl) && dl.ValueKind == JsonValueKind.Array)
                    foreach (var d in dl.EnumerateArray())
                    {
                        if (Str(d, "hash") is not { } hash) continue;
                        var virtuals = new List<string>();
                        if (d.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object
                            && ps.TryGetProperty("virtuals", out var vs) && vs.ValueKind == JsonValueKind.Array)
                            virtuals.AddRange(vs.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                                                .Select(v => v.GetString()!));
                        deps.Add(new Dependency(Str(d, "name") ?? "", hash, virtuals));
                    }

                result[entry.Name] = new Install(entry.Name, name, version, path.GetString()!, at, deps);
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsUnder(string file, string prefix)
    {
        string p = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return file.StartsWith(p, StringComparison.Ordinal);
    }

    private static bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IReadOnlyList<string> MakeDefaultRoots()
    {
        if (OperatingSystem.IsWindows()) return [];
        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable("SPACK_ROOT")?.Trim() is { Length: > 0 } spackRoot && Path.IsPathRooted(spackRoot))
            roots.Add(Path.Combine(spackRoot, "opt", "spack"));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            roots.Add(Path.Combine(home, "spack", "opt", "spack"));
            roots.Add(Path.Combine(home, "opt", "spack"));
        }
        roots.Add("/opt/spack");
        return roots.Distinct(StringComparer.Ordinal).ToList();
    }
}
