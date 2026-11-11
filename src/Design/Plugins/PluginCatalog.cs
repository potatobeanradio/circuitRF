namespace CircuitRF.Design.Plugins;

/// <summary>Where a listed plugin came from.</summary>
public enum PluginSource
{
    /// <summary>Under the plugins root: <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/</c>.</summary>
    Installed,
    /// <summary>A folder run in place — Link folder…, or <c>CRF_PLUGIN_PATH</c>.</summary>
    Linked,
}

/// <summary>
/// One plugin as a list shows it. <see cref="Manifest"/> is null when the manifest was refused, and then
/// <see cref="Diagnostics"/> says why; <see cref="DimReason"/> is the one sentence a dimmed row shows.
/// </summary>
public sealed record PluginEntry(
    string Directory,
    PluginSource Source,
    PluginManifest? Manifest,
    IReadOnlyList<PluginDiagnostic> Diagnostics)
{
    /// <summary>Why this entry is listed but cannot run on this build and machine, or null when it can.</summary>
    public string? DimReason => Manifest is null
        ? (Diagnostics.FirstOrDefault(d => d.IsError)?.ToString() ?? "its manifest could not be read")
        : Manifest.NeedsNewerCircuitRF ?? Manifest.ResolveRun().Refusal;

    /// <summary>The folder's own name when the manifest gave no usable name.</summary>
    public string DisplayName => Manifest?.Name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
}

/// <summary>Where <see cref="PluginCatalog.Discover"/> looks. Every source is an argument: a preference is passed in,
/// never read here (<c>src/Design/CLAUDE.md</c>).</summary>
/// <param name="PluginsRoot">The per-user plugins folder, or null for none.</param>
/// <param name="LinkedFolders">Folders run in place, from preferences.</param>
/// <param name="EnvironmentPath">The value of <c>CRF_PLUGIN_PATH</c>, <c>;</c>- or <c>:</c>-separated.</param>
/// <param name="ActiveVersions">Plugin id → the version recorded as active.</param>
public sealed record PluginCatalogSources(
    string? PluginsRoot,
    IReadOnlyList<string>? LinkedFolders = null,
    string? EnvironmentPath = null,
    IReadOnlyDictionary<string, string>? ActiveVersions = null)
{
    public const string EnvironmentVariable = "CRF_PLUGIN_PATH";

    /// <summary>The per-user plugins folder (<c>plugins.md</c> §9.1).</summary>
    public static string DefaultRoot => UserStateDirectory.SubDir("plugins");
}

/// <summary>
/// The plugins this installation can offer (brief-plg-1 §2). <b>It reads manifests and nothing else</b>: no process
/// is started, no interpreter is probed and nothing is imported, so listing a plugin never runs it (series gate 2).
/// </summary>
public static class PluginCatalog
{
    public static IReadOnlyList<PluginEntry> Discover(PluginCatalogSources sources)
    {
        var entries = new List<PluginEntry>();

        if (sources.PluginsRoot is { } root && Directory.Exists(root))
            foreach (string idDir in SafeDirectories(root))
            {
                // A '.partial' folder is an install in progress or one that failed: never listed (brief-plg-6 §1).
                var versions = SafeDirectories(idDir)
                    .Where(v => !v.EndsWith(PluginInstaller.PartialSuffix, StringComparison.Ordinal))
                    .Where(v => File.Exists(Path.Combine(v, PluginManifestReader.FileName)))
                    .Select(v => Read(v, PluginSource.Installed))
                    .ToList();
                if (versions.Count == 0) continue;

                string id = Path.GetFileName(idDir);
                // active.json beside the versions is the record (brief-plg-6 §1); the preference is the older one.
                string? active = PluginInstaller.ReadActive(idDir)?.Active ?? sources.ActiveVersions?.GetValueOrDefault(id);
                var chosen = versions.FirstOrDefault(v => active is not null && string.Equals(Path.GetFileName(v.Directory), active, StringComparison.Ordinal))
                             ?? versions.OrderByDescending(v => v.Manifest?.Version ?? Path.GetFileName(v.Directory), PluginVersions.Comparer).First();
                entries.Add(chosen);
            }

        var linked = new List<string>();
        if (sources.LinkedFolders is { } folders) linked.AddRange(folders);
        if (sources.EnvironmentPath is { Length: > 0 } env)
            linked.AddRange(env.Split(Path.PathSeparator == ';' ? [';'] : [';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (string folder in linked.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(Path.Combine(folder, PluginManifestReader.FileName)))
                entries.Add(Read(Path.GetFullPath(folder), PluginSource.Linked));

        // A linked folder is for a plugin under development and wins over the installed copy of the same id.
        return entries
            .GroupBy(e => e.Manifest?.Id ?? e.Directory, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(e => e.Source == PluginSource.Linked) ?? g.First())
            .OrderBy(e => e.Manifest?.Menu ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One folder's manifest, read — the same answer a Link folder… or a re-read before a start gets.</summary>
    public static PluginEntry Read(string directory, PluginSource source)
    {
        var (manifest, diagnostics) = PluginManifestReader.ReadFile(Path.Combine(directory, PluginManifestReader.FileName));
        return new PluginEntry(directory, source, manifest, diagnostics);
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
