using CircuitRF.Design.Layout.PCells.Wire;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.Plugins;

/// <summary>Who installed a plugin — recorded in <c>active.json</c>, so Settings ▸ Plugins can say an install was made
/// outside the window and is not yet allowed (brief-plg-6 §5).</summary>
public enum PluginInstallOrigin { Settings, Cli, Mcp }

/// <summary>
/// <c>&lt;id&gt;/active.json</c>: which installed version is active, the one before it, and where and when the install
/// came from (brief-plg-6 §1, §5).
/// </summary>
/// <param name="Active">The version folder in use.</param>
/// <param name="Previous">The version an update replaced, kept until the new one has started once; null after that.</param>
/// <param name="Origin"><c>settings</c>, <c>cli</c> or <c>mcp</c>.</param>
/// <param name="Installed">When the active version was installed (UTC).</param>
/// <param name="Source">Where it came from: <c>list</c>, an address and tag, or <c>folder &lt;path&gt;</c>.</param>
/// <param name="Started">Whether the active version has answered <c>hello</c> once.</param>
/// <param name="Environment">A Python plugin's environment: <c>ready</c> (no requirements — the shared Python),
/// <c>shared</c> (the shared plugin environment), <c>own</c> (its own <c>.venv</c>), <c>pending</c> (installed outside the
/// window, made when the person allows it) or the sentence that says why it could not be made. Null for a compiled
/// plugin.</param>
public sealed record PluginActiveRecord(
    string Active,
    string? Previous,
    string Origin,
    DateTimeOffset Installed,
    string Source,
    bool Started = false,
    string? Environment = null)
{
    /// <summary>No requirements: it runs on the shared Python as it is.</summary>
    public const string Ready = "ready";
    /// <summary>Its requirements are in the one shared plugin environment.</summary>
    public const string Shared = "shared";
    /// <summary>Its requirements conflict with another plugin's, so it has an environment of its own.</summary>
    public const string Own = "own";
    public const string Pending = "pending";

    /// <summary>Whether a Python plugin still needs its environment made before it may run.</summary>
    public bool NeedsEnvironment => Environment is not null and not (Ready or Shared or Own);

    /// <summary>Whether the install was made outside the window (the command line or MCP).</summary>
    public bool OutsideWindow => Origin is "cli" or "mcp";
}

/// <summary>Everything an install needs, as arguments (<c>src/Design/CLAUDE.md</c>): no preference is read here.</summary>
/// <param name="PluginsRoot">The per-user plugins folder (<see cref="PluginCatalogSources.DefaultRoot"/>).</param>
/// <param name="PreferencesPath">The <c>preferences.json</c> whose plugin keys an uninstall or link changes.</param>
/// <param name="Origin">Who is installing.</param>
/// <param name="Http">The only way anything here reaches a network; null refuses every download.</param>
/// <param name="Log">Each line of progress, pip's output included.</param>
/// <param name="CreatePythonEnvironment">Make a Python plugin's environment now. True only in the window: an install made
/// outside it runs nothing, and <c>pip</c> can run a package's build scripts (brief-plg-6 §5).</param>
/// <param name="PythonCommand">The interpreter to make environments with; null discovers one.</param>
public sealed record PluginInstallContext(
    string PluginsRoot,
    string PreferencesPath,
    PluginInstallOrigin Origin,
    IPluginHttp? Http = null,
    Action<string>? Log = null,
    bool CreatePythonEnvironment = false,
    PythonInterpreter? PythonCommand = null)
{
    internal void Say(string line) { try { Log?.Invoke(line); } catch { /* a log sink must not stop an install */ } }
}

/// <summary>What an install, update, rollback or uninstall did, or why it did nothing.</summary>
public sealed record PluginInstallResult(
    bool Ok,
    string? Id = null,
    string? Version = null,
    string? Directory = null,
    string? Refusal = null,
    bool Cancelled = false,
    string? Environment = null)
{
    public static PluginInstallResult Refused(string why) => new(false, Refusal: why);
    public static PluginInstallResult Stopped() => new(false, Refusal: "Cancelled; nothing was installed.", Cancelled: true);
}

/// <summary>A release on GitHub, as the address dialog's tag picker lists it.</summary>
public sealed record PluginRelease(string Tag, string? Name, bool Prerelease, IReadOnlyList<(string Name, string Url)> Assets);

/// <summary>
/// The install store (brief-plg-6 §1, <c>plugins.md</c> §9): <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/</c> plus
/// <c>&lt;id&gt;/active.json</c>. <b>Never a half-installed plugin</b>: every install unpacks into
/// <c>&lt;version&gt;.partial/</c>, validates the manifest against what was asked for, makes the Python environment when
/// asked to, and only then renames the folder and records it active. Any failure or a cancel deletes the
/// <c>.partial</c> folder and leaves the previous version active.
///
/// <para>Settings ▸ Plugins, <c>circuitRF plugin install</c> and the MCP <c>plugin</c> tool all call these functions; none
/// of them has a copy. <b>Nothing here allows a plugin to run</b> — an install is <c>not asked</c> until the person
/// answers in the window.</para>
/// </summary>
public static class PluginInstaller
{
    public const string PartialSuffix = ".partial";
    public const string ActiveFile = "active.json";
    public const string IndexCacheFolder = ".index";
    public const string VenvFolder = ".venv";

    /// <summary>Where Refresh keeps the signed list it fetched.</summary>
    public static string IndexCache(string pluginsRoot) => Path.Combine(pluginsRoot, IndexCacheFolder);

    // ── active.json ─────────────────────────────────────────────────────────────

    /// <summary><paramref name="idDir"/>'s record, or null when it has none or it does not read.</summary>
    public static PluginActiveRecord? ReadActive(string idDir)
    {
        try
        {
            string file = Path.Combine(idDir, ActiveFile);
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject o) return null;
            string? active = (string?)o["active"];
            if (string.IsNullOrEmpty(active)) return null;
            return new PluginActiveRecord(
                active, (string?)o["previous"], (string?)o["origin"] ?? "settings",
                DateTimeOffset.TryParse((string?)o["installed"], out var t) ? t : DateTimeOffset.MinValue,
                (string?)o["source"] ?? "", (bool?)o["started"] ?? false, (string?)o["environment"]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static void WriteActive(string idDir, PluginActiveRecord r)
    {
        var o = new JsonObject
        {
            ["active"] = r.Active, ["previous"] = r.Previous, ["origin"] = r.Origin,
            ["installed"] = r.Installed.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["source"] = r.Source,
            ["started"] = r.Started, ["environment"] = r.Environment,
        };
        string tmp = Path.Combine(idDir, ActiveFile + ".tmp");
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path.Combine(idDir, ActiveFile), overwrite: true);
    }

    /// <summary>The record of the installed plugin whose version folder is <paramref name="versionDir"/>, or null.</summary>
    public static PluginActiveRecord? RecordFor(string versionDir)
        => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(versionDir)) is { } idDir ? ReadActive(idDir) : null;

    /// <summary>
    /// The active version answered <c>hello</c> (brief-plg-6 §1): it has started once, so the version an update replaced
    /// is deleted. Called by <see cref="PluginSession"/>; anything that goes wrong here is ignored — a start is not
    /// failed by housekeeping.
    /// </summary>
    public static void ConfirmStarted(string versionDir)
    {
        try
        {
            string? idDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(versionDir));
            if (idDir is null || ReadActive(idDir) is not { } r || r.Started) return;
            if (!string.Equals(r.Active, Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir)), StringComparison.Ordinal)) return;
            if (r.Previous is { } prev && SafeName(prev)) TryDelete(Path.Combine(idDir, prev));
            WriteActive(idDir, r with { Started = true, Previous = null });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── sources ─────────────────────────────────────────────────────────────────

    /// <summary>Install from folder… (brief-plg-6 §2): copies <paramref name="folder"/> in.</summary>
    public static Task<PluginInstallResult> InstallFolderAsync(PluginInstallContext ctx, string folder, CancellationToken ct = default)
    {
        string from = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(from, PluginManifestReader.FileName)))
            return Task.FromResult(PluginInstallResult.Refused($"{from} holds no {PluginManifestReader.FileName}, so it is not a plugin."));
        var (manifest, refusal) = ReadManifest(File.ReadAllText(Path.Combine(from, PluginManifestReader.FileName)), from);
        if (manifest is null) return Task.FromResult(PluginInstallResult.Refused(refusal!));
        return InstallAsync(ctx, manifest, null, null, $"folder {from}", partial => CopyFolder(from, partial, ct), ct);
    }

    /// <summary>Installs a plugin archive. <paramref name="expectedId"/>/<paramref name="expectedVersion"/> are what the
    /// list or the caller asked for; a manifest that says otherwise is refused.</summary>
    public static Task<PluginInstallResult> InstallZipAsync(PluginInstallContext ctx, string zipPath, string source,
        string? expectedId = null, string? expectedVersion = null, CancellationToken ct = default)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        { return Task.FromResult(PluginInstallResult.Refused($"{Path.GetFileName(zipPath)} is not a readable zip archive: {e.Message}")); }

        return Run();

        async Task<PluginInstallResult> Run()
        {
            using (zip)
            {
                if (UnsafeEntry(zip) is { } bad) return PluginInstallResult.Refused(bad);
                string? prefix = ManifestPrefix(zip);
                if (prefix is null)
                    return PluginInstallResult.Refused($"{Path.GetFileName(zipPath)} holds no {PluginManifestReader.FileName}, at its top or in one folder at its top.");
                string json;
                using (var r = new StreamReader(zip.GetEntry(prefix + PluginManifestReader.FileName)!.Open())) json = await r.ReadToEndAsync(ct);
                var (manifest, refusal) = ReadManifest(json, Path.GetFileName(zipPath));
                if (manifest is null) return PluginInstallResult.Refused(refusal!);
                return await InstallAsync(ctx, manifest, expectedId, expectedVersion, source, partial => Extract(zip, prefix, partial, ct), ct);
            }
        }
    }

    /// <summary>
    /// Install from the vetted list: this platform's asset of <paramref name="version"/> (the newest installable when
    /// null), downloaded and <b>checked against the list's SHA-256 before anything is unpacked</b> (gate 3).
    /// </summary>
    public static async Task<PluginInstallResult> InstallFromListAsync(PluginInstallContext ctx, PluginIndex index, string id,
        string? version = null, CancellationToken ct = default)
    {
        if (index.Find(id) is not { } entry)
            return PluginInstallResult.Refused($"'{id}' is not on the plugin list." + (index.Plugins.Count == 0 ? " The list is empty."
                : $" It lists: {string.Join(", ", index.Plugins.Select(p => p.Id))}."));
        var chosen = version is null ? entry.LatestInstallable() : entry.Versions.FirstOrDefault(v => v.Version == version);
        if (chosen is null)
            return PluginInstallResult.Refused(version is null
                ? $"No version of {entry.Name} on the list installs here: {entry.Latest?.Unavailable() ?? "it lists none"}."
                : $"Version {version} of {entry.Name} is not on the list; it lists {string.Join(", ", entry.Versions.Select(v => v.Version))}.");
        if (chosen.Unavailable() is { } why) return PluginInstallResult.Refused($"{entry.Name} {chosen.Version} {why}.");
        var asset = chosen.AssetFor()!;

        return await WithDownload(ctx, asset.Url, async file =>
        {
            ctx.Say("Checking its SHA-256 against the list.");
            string actual = await Sha256Async(file, ct);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                return PluginInstallResult.Refused($"The download of {entry.Name} {chosen.Version} does not match the list: its SHA-256 is {actual}, and the list says {asset.Sha256}. Nothing was unpacked.");
            return await InstallZipAsync(ctx, file, "list", id, chosen.Version, ct);
        }, ct);
    }

    /// <summary>
    /// Install from address… (brief-plg-6 §2): a GitHub or GitLab repository link, as pasted — the repository's page, a
    /// <c>.git</c> clone address (https or <c>git@host:</c>), or a link to one release or tag, which names the tag itself.
    /// Installs <paramref name="tag"/>'s release asset whose name holds this platform's RID or <c>any</c>, else the tag's
    /// source archive. With no tag, the latest release, else the newest tag; a repository whose releases and tags cannot be
    /// read is then a refusal naming the tag, never a guess. No credential: a private repository is not found.
    /// </summary>
    public static async Task<PluginInstallResult> InstallFromAddressAsync(PluginInstallContext ctx, string address, string? tag,
        CancellationToken ct = default)
    {
        if (PluginRepository.Parse(address) is not { } parsed)
            return PluginInstallResult.Refused($"'{address}' is not a GitHub or GitLab repository link (https://github.com/<owner>/<repository> or https://gitlab.com/<group>/<project>).");
        var repo = parsed.Repository;
        tag ??= parsed.Tag;
        if (ctx.Http is null) return PluginInstallResult.Refused("This install cannot reach a network.");

        PluginRelease? release = null;
        var (releases, _) = await ListReleasesAsync(ctx.Http, address, ct);

        if (tag is null)
        {
            if (releases is null)
                return PluginInstallResult.Refused($"The releases of {repo.Path} could not be read, so which one to install is not known. Name it with a tag (--tag).");
            release = releases.FirstOrDefault(r => !r.Prerelease) ?? releases.FirstOrDefault();
            if (release is null)
                return PluginInstallResult.Refused($"{repo.Path} has no releases and no tags, so there is no version to install. Its author tags one.");
            tag = release.Tag;
        }
        else release = releases?.FirstOrDefault(r => r.Tag == tag);

        string rid = PluginPlatform.CurrentRid;
        var asset = release?.Assets.FirstOrDefault(a => a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? release?.Assets.FirstOrDefault(a => a.Name.Contains(PluginIndex.AnyPlatform, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        string url = asset?.Url ?? repo.SourceArchive(tag);
        string source = $"{repo.Web} {tag}";
        ctx.Say(asset is null ? $"Using the source archive of {tag}." : $"Using the release asset {asset.Value.Name}.");
        return await WithDownload(ctx, url, file => InstallZipAsync(ctx, file, source, ct: ct), ct);
    }

    /// <summary>The releases of <paramref name="address"/>, newest first — the address dialog's tag picker. A repository
    /// with no releases answers with its tags instead, each as a release with no assets. When neither can be read, the
    /// list is null and the sentence says why.</summary>
    public static async Task<(IReadOnlyList<PluginRelease>? Releases, string? Failure)> ListReleasesAsync(IPluginHttp http, string address, CancellationToken ct = default)
    {
        if (PluginRepository.Parse(address) is not { Repository: var repo })
            return (null, $"'{address}' is not a GitHub or GitLab repository link.");
        var answer = await http.GetBytesAsync(repo.ReleasesApi, ct);
        if (answer.Failure is { } failed) return (null, failed);
        if (answer.Body is not { } body) return (null, $"{repo.Path} was not found on {repo.HostName}. A private repository cannot be installed from.");
        var list = new List<PluginRelease>();
        try
        {
            if (JsonNode.Parse(body) is JsonArray releases)
                foreach (var r in releases.OfType<JsonObject>())
                {
                    if ((bool?)r["draft"] == true || (string?)r["tag_name"] is not { Length: > 0 } t) continue;
                    var assets = repo.Host == PluginRepositoryHost.GitHub
                        ? (r["assets"] as JsonArray ?? []).OfType<JsonObject>()
                            .Select(a => ((string?)a["name"] ?? "", (string?)a["browser_download_url"] ?? ""))
                        : (r["assets"]?["links"] as JsonArray ?? []).OfType<JsonObject>()
                            .Select(a => ((string?)a["name"] ?? "", (string?)a["direct_asset_url"] ?? (string?)a["url"] ?? ""));
                    list.Add(new PluginRelease(t, (string?)r["name"], (bool?)r["prerelease"] == true || (bool?)r["upcoming_release"] == true,
                        assets.Where(a => a.Item1.Length > 0 && a.Item2.Length > 0).ToList()));
                }
            if (list.Count > 0) return (list, null);

            // No releases: the tags are the versions there are, installed from their source archives.
            if ((await http.GetBytesAsync(repo.TagsApi, ct)).Body is { } tags && JsonNode.Parse(tags) is JsonArray tagList)
                foreach (var t in tagList.OfType<JsonObject>())
                    if ((string?)t["name"] is { Length: > 0 } name) list.Add(new PluginRelease(name, null, false, []));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { return (null, $"The releases of {repo.Path} did not read: {e.Message}"); }
        return (list, null);
    }

    // ── Link folder… ────────────────────────────────────────────────────────────

    /// <summary>
    /// Link folder… (brief-plg-6 §2): runs <paramref name="folder"/> in place — the manifest is re-read at every start, for
    /// an author. Recorded in <c>plugin_links</c>, merged into the preferences file.
    /// </summary>
    public static PluginInstallResult Link(PluginInstallContext ctx, string folder)
    {
        string full = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(full, PluginManifestReader.FileName)))
            return PluginInstallResult.Refused($"{full} holds no {PluginManifestReader.FileName}, so it is not a plugin.");
        var (manifest, refusal) = ReadManifest(File.ReadAllText(Path.Combine(full, PluginManifestReader.FileName)), full);
        if (manifest is null) return PluginInstallResult.Refused(refusal!);
        if (!PluginPreferences.Edit(ctx.PreferencesPath, root =>
            {
                var links = root[PluginPreferences.LinksKey] as JsonArray ?? [];
                if (!links.Any(l => string.Equals((string?)l, full, StringComparison.OrdinalIgnoreCase))) links.Add(full);
                root[PluginPreferences.LinksKey] = links.DeepClone();
            }))
            return PluginInstallResult.Refused($"{ctx.PreferencesPath} could not be written.");
        return new PluginInstallResult(true, manifest.Id, manifest.Version, full);
    }

    // ── update, roll back, uninstall ────────────────────────────────────────────

    /// <summary>
    /// Update (brief-plg-6 §1): installs the newer version from where the active one came from — the list, or the same
    /// address's latest release. The version it replaces stays until the new one has started once.
    /// </summary>
    public static async Task<PluginInstallResult> UpdateToNewestAsync(PluginInstallContext ctx, PluginIndex index, string id, CancellationToken ct = default)
    {
        if (NotAnId(id) is { } bad) return bad;
        string idDir = Path.Combine(ctx.PluginsRoot, id);
        if (ReadActive(idDir) is not { } r) return PluginInstallResult.Refused($"{id} is not installed here.");
        if (r.Source == "list")
        {
            var next = index.Find(id)?.LatestInstallable();
            if (next is null || PluginVersions.Compare(next.Version, r.Active) <= 0)
                return PluginInstallResult.Refused($"{id} {r.Active} is the newest version on the list that installs here.");
            return await InstallFromListAsync(ctx, index, id, next.Version, ct);
        }
        if (r.Source.StartsWith("https://", StringComparison.Ordinal))
        {
            string address = r.Source.Split(' ')[0];
            if (ctx.Http is null) return PluginInstallResult.Refused("This update cannot reach a network.");
            var (releases, failure) = await ListReleasesAsync(ctx.Http, address, ct);
            if (releases is null) return PluginInstallResult.Refused(failure!);
            var latest = releases.FirstOrDefault(x => !x.Prerelease);
            if (latest is null || r.Source.EndsWith(" " + latest.Tag, StringComparison.Ordinal))
                return PluginInstallResult.Refused($"{id} {r.Active} is from {address}'s latest release.");
            return await InstallFromAddressAsync(ctx, address, latest.Tag, ct);
        }
        return PluginInstallResult.Refused($"{id} was installed from a folder; install the new folder to update it.");
    }

    /// <summary>The version the list offers above the installed one, or null — the Installed list's Update available.</summary>
    public static string? UpdateAvailable(PluginIndex index, string id, string installedVersion, PluginActiveRecord? record)
        => record?.Source == "list" && index.Find(id)?.LatestInstallable() is { } v && PluginVersions.Compare(v.Version, installedVersion) > 0 ? v.Version : null;

    /// <summary>
    /// Roll back (brief-plg-6 §1, gate 2): the version an update replaced is active again, and the newer one — which has
    /// not started once — is deleted, with its permission.
    /// </summary>
    public static PluginInstallResult Rollback(PluginInstallContext ctx, string id)
    {
        if (NotAnId(id) is { } bad) return bad;
        string idDir = Path.Combine(ctx.PluginsRoot, id);
        if (ReadActive(idDir) is not { } r) return PluginInstallResult.Refused($"{id} is not installed here.");
        // active.json is a file on disk like any other: a version in it that is not a folder name is not followed.
        if (r.Previous is not { } prev || !SafeName(prev) || !SafeName(r.Active) || !Directory.Exists(Path.Combine(idDir, prev)))
            return PluginInstallResult.Refused($"{id} has no earlier version to go back to.");
        string failed = Path.Combine(idDir, r.Active);
        WriteActive(idDir, r with { Active = prev, Previous = null, Started = true, Environment = EnvironmentOf(Path.Combine(idDir, prev)) });
        TryDelete(failed);
        PluginPreferences.Edit(ctx.PreferencesPath, root => PluginPreferences.RemoveTrust(root, [failed]));
        return new PluginInstallResult(true, id, prev, Path.Combine(idDir, prev));
    }

    /// <summary>
    /// Uninstall (brief-plg-6 §1): removes the plugin's folder — or one version of it — its permission, and what was
    /// remembered for it (window size, last parameters). A linked plugin is unlinked; its folder is the author's and stays.
    /// </summary>
    public static PluginInstallResult Uninstall(PluginInstallContext ctx, string id, string? version = null, IReadOnlyList<string>? linkedFolders = null)
    {
        // An id and a version name folders under the plugins root and nothing else: '..', a separator or an absolute path
        // would make the delete below reach outside it.
        if (NotAnId(id) is { } bad) return bad;
        if (version is not null && !SafeName(version))
            return PluginInstallResult.Refused($"'{version}' is not a plugin version; a version is letters, digits, '.', '-', '_' and '+'.");
        string idDir = Path.Combine(ctx.PluginsRoot, id);
        if (!Directory.Exists(idDir) || ReadActive(idDir) is null && !Directory.EnumerateDirectories(idDir).Any())
        {
            var linked = linkedFolders?.FirstOrDefault(f => PluginCatalog.Read(f, PluginSource.Linked).Manifest?.Id == id);
            if (linked is null) return PluginInstallResult.Refused($"{id} is not installed here.");
            PluginPreferences.Edit(ctx.PreferencesPath, root =>
            {
                if (root[PluginPreferences.LinksKey] is JsonArray links)
                    root[PluginPreferences.LinksKey] = new JsonArray([.. links.Where(l => !string.Equals((string?)l, linked, StringComparison.OrdinalIgnoreCase)).Select(l => l?.DeepClone())]);
                PluginPreferences.RemoveTrust(root, [linked]);
                PluginPreferences.Forget(root, id);
            });
            return new PluginInstallResult(true, id, null, linked);
        }

        var r = ReadActive(idDir);
        var versions = Directory.GetDirectories(idDir).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList();
        if (version is not null)
        {
            string one = Path.Combine(idDir, version);
            if (!Directory.Exists(one)) return PluginInstallResult.Refused($"{id} {version} is not installed here; {string.Join(", ", versions.Select(Path.GetFileName))} is.");
            bool last = versions.Count(v => !v.EndsWith(PartialSuffix, StringComparison.Ordinal)) == 1;
            if (!last)
            {
                if (r is not null && r.Active == version)
                {
                    string? next = r.Previous ?? versions.Select(v => Path.GetFileName(v)).Where(v => v != version && !v.EndsWith(PartialSuffix, StringComparison.Ordinal)).OrderByDescending(v => v, PluginVersions.Comparer).FirstOrDefault();
                    if (next is not null) WriteActive(idDir, r with { Active = next, Previous = null, Environment = EnvironmentOf(Path.Combine(idDir, next)) });
                }
                else if (r is not null && r.Previous == version) WriteActive(idDir, r with { Previous = null });
                TryDelete(one);
                PluginPreferences.Edit(ctx.PreferencesPath, root => PluginPreferences.RemoveTrust(root, [one]));
                return new PluginInstallResult(true, id, version, one);
            }
        }

        if (!TryDelete(idDir)) return PluginInstallResult.Refused($"{idDir} could not be removed; a file in it may be in use.");
        PluginPreferences.Edit(ctx.PreferencesPath, root =>
        {
            PluginPreferences.RemoveTrust(root, versions);
            PluginPreferences.Forget(root, id);
        });
        return new PluginInstallResult(true, id, r?.Active, idDir);
    }

    // ── Python ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes <paramref name="versionDir"/>'s Python environment and records the outcome in <c>active.json</c> — what
    /// allowing a plugin installed outside the window does (brief-plg-6 §5). A failure leaves it installed, with the
    /// reason recorded, and is returned.
    /// </summary>
    public static async Task<string?> PrepareEnvironmentAsync(PluginInstallContext ctx, string versionDir, CancellationToken ct = default)
    {
        var entry = PluginCatalog.Read(versionDir, PluginSource.Installed);
        if (entry.Manifest is not { } m) return entry.DimReason;
        var outcome = await PluginPythonEnvironment.PrepareAsync(versionDir, ctx.PluginsRoot, m, ctx.PythonCommand, ctx.Say, ct);
        string? idDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(versionDir));
        if (idDir is not null && ReadActive(idDir) is { } r && r.Active == Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir)))
            try { WriteActive(idDir, r with { Environment = outcome.Kind ?? outcome.Refusal }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return outcome.Refusal;
    }

    // ── the one install path ────────────────────────────────────────────────────

    private static async Task<PluginInstallResult> InstallAsync(PluginInstallContext ctx, PluginManifest m, string? expectedId, string? expectedVersion,
        string source, Func<string, string?> fill, CancellationToken ct)
    {
        if (expectedId is not null && m.Id != expectedId)
            return PluginInstallResult.Refused($"The archive's manifest says it is {m.Id}, and {expectedId} was asked for. Nothing was installed.");
        if (expectedVersion is not null && m.Version != expectedVersion)
            return PluginInstallResult.Refused($"The archive's manifest says it is version {m.Version} of {m.Id}, and {expectedVersion} was asked for. Nothing was installed.");
        if (!SafeName(m.Id) || !SafeName(m.Version))
            return PluginInstallResult.Refused($"'{m.Id}' {m.Version} cannot name a folder; an id and a version are letters, digits, '.', '-' and '_'.");

        string idDir = Path.Combine(ctx.PluginsRoot, m.Id);
        string final = Path.Combine(idDir, m.Version), partial = final + PartialSuffix;
        if (Directory.Exists(final))
            return PluginInstallResult.Refused($"{m.Name} {m.Version} is already installed ({final}). Uninstall that version first, or link the folder to run it in place.");

        try
        {
            Directory.CreateDirectory(idDir);
            TryDelete(partial);
            Directory.CreateDirectory(partial);
            ctx.Say($"Unpacking {m.Name} {m.Version}.");
            if (fill(partial) is { } refused) { TryDelete(partial); Tidy(idDir); return PluginInstallResult.Refused(refused); }
            ct.ThrowIfCancellationRequested();

            // What was unpacked is what was asked for — the manifest read again from the folder that will run.
            var (check, why) = ReadManifest(File.ReadAllText(Path.Combine(partial, PluginManifestReader.FileName)), partial);
            if (check is null || check.Id != m.Id || check.Version != m.Version)
            { TryDelete(partial); Tidy(idDir); return PluginInstallResult.Refused(why ?? "The unpacked manifest is not the one that was read."); }

            bool python = m.ResolveRun().Entry?.IsPython == true;
            string? environment = python ? PluginActiveRecord.Pending : null;
            if (python && ctx.CreatePythonEnvironment)
            {
                var prepared = await PluginPythonEnvironment.PrepareAsync(partial, ctx.PluginsRoot, m, ctx.PythonCommand, ctx.Say, ct);
                if (prepared.Refusal is { } failed)
                { TryDelete(partial); Tidy(idDir); return PluginInstallResult.Refused(failed); }
                environment = prepared.Kind;
            }
            ct.ThrowIfCancellationRequested();

            Directory.Move(partial, final);
            var old = ReadActive(idDir);
            // One earlier version is kept, the one this replaces; an older one left behind by an update that never
            // started is removed now.
            if (old?.Previous is { } stale && stale != old.Active && SafeName(stale)) TryDelete(Path.Combine(idDir, stale));
            string? previous = old is not null && Directory.Exists(Path.Combine(idDir, old.Active)) ? old.Active : null;
            WriteActive(idDir, new PluginActiveRecord(m.Version, previous, OriginName(ctx.Origin), DateTimeOffset.UtcNow, source, false, environment));
            ctx.Say($"Installed {m.Name} {m.Version} in {final}.");
            return new PluginInstallResult(true, m.Id, m.Version, final, Environment: environment);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial); Tidy(idDir);
            return PluginInstallResult.Stopped();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial); Tidy(idDir);
            return PluginInstallResult.Refused($"{m.Name} {m.Version} could not be installed: {e.Message}");
        }
    }

    public static string OriginName(PluginInstallOrigin o) => o switch
    {
        PluginInstallOrigin.Cli => "cli", PluginInstallOrigin.Mcp => "mcp", _ => "settings",
    };

    /// <summary>A version gone back to: its own environment if it has one, else the shared one if it has requirements,
    /// else the shared Python. It ran before the update, so its packages were made then.</summary>
    private static string? EnvironmentOf(string versionDir)
        => PluginCatalog.Read(versionDir, PluginSource.Installed).Manifest?.ResolveRun().Entry?.IsPython != true ? null
         : File.Exists(PluginPythonEnvironment.Interpreter(versionDir)) ? PluginActiveRecord.Own
         : PluginPythonEnvironment.NeedsNetwork(versionDir) ? PluginActiveRecord.Shared
         : PluginActiveRecord.Ready;

    private static (PluginManifest? Manifest, string? Refusal) ReadManifest(string json, string where)
    {
        var (m, diagnostics) = PluginManifestReader.Read(json);
        return m is not null ? (m, null)
            : (null, $"The manifest in {where} is refused: {string.Join("; ", diagnostics.Where(d => d.IsError))}.");
    }

    /// <summary>The refusal for an id that cannot name a folder under the plugins root, or null.</summary>
    private static PluginInstallResult? NotAnId(string id)
        => SafeName(id) ? null : PluginInstallResult.Refused($"'{id}' is not a plugin id; an id is letters, digits, '.', '-' and '_'.");

    private static bool SafeName(string s)
        => s.Length is > 0 and < 128 && s != "." && s != ".." && !s.StartsWith('.') && !s.EndsWith(PartialSuffix, StringComparison.Ordinal)
           && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    private static async Task<PluginInstallResult> WithDownload(PluginInstallContext ctx, string url, Func<string, Task<PluginInstallResult>> then, CancellationToken ct)
    {
        if (ctx.Http is null) return PluginInstallResult.Refused("This install cannot reach a network.");
        string dir = Path.Combine(ctx.PluginsRoot, ".download-" + Guid.NewGuid().ToString("N")[..8]);
        string file = Path.Combine(dir, "plugin.zip");
        try
        {
            Directory.CreateDirectory(dir);
            ctx.Say($"Downloading {url}");
            if (await ctx.Http.DownloadAsync(url, file, ct) is { } failed) return PluginInstallResult.Refused(failed);
            return await then(file);
        }
        catch (OperationCanceledException) { return PluginInstallResult.Stopped(); }
        finally { TryDelete(dir); }
    }

    /// <summary>The SHA-256 of a file in lowercase hex.</summary>
    public static async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    // ── archives and copies ─────────────────────────────────────────────────────

    /// <summary>
    /// Zip safety (brief-plg-6 §1, gate 4): an entry with a <c>..</c> part, an absolute path or drive, or a link that
    /// leads outside the archive is refused by name, before anything is written.
    /// </summary>
    public static string? UnsafeEntry(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            string name = e.FullName;
            string norm = name.Replace('\\', '/');
            if (norm.StartsWith('/') || (norm.Length > 1 && norm[1] == ':') || Path.IsPathRooted(name))
                return $"The archive's entry '{name}' is an absolute path. Nothing was installed.";
            if (norm.Split('/').Any(p => p == ".."))
                return $"The archive's entry '{name}' leads outside the plugin's folder ('..'). Nothing was installed.";
            if (IsLink(e))
            {
                string target;
                using (var r = new StreamReader(e.Open())) target = r.ReadToEnd().Replace('\\', '/');
                string dir = Path.GetDirectoryName(norm)?.Replace('\\', '/') ?? "";
                string joined = target.StartsWith('/') ? target : (dir.Length > 0 ? dir + "/" : "") + target;
                if (target.StartsWith('/') || (target.Length > 1 && target[1] == ':') || Escapes(joined))
                    return $"The archive's entry '{name}' is a link to '{target}', outside the plugin's folder. Nothing was installed.";
            }
        }
        return null;

        static bool Escapes(string path)
        {
            int depth = 0;
            foreach (string p in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (p == ".") continue;
                depth += p == ".." ? -1 : 1;
                if (depth < 0) return true;
            }
            return false;
        }
    }

    private static bool IsLink(ZipArchiveEntry e) => ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>The folder inside the archive holding <c>plugin.json</c>: the top, or one folder at the top (a source
    /// archive's <c>repo-tag/</c>). Null when there is neither.</summary>
    private static string? ManifestPrefix(ZipArchive zip)
    {
        if (zip.GetEntry(PluginManifestReader.FileName) is not null) return "";
        var tops = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => n.Length > 0)
            .Select(n => n.Split('/')[0]).Distinct().ToList();
        if (tops.Count != 1) return null;
        string prefix = tops[0] + "/";
        return zip.GetEntry(prefix + PluginManifestReader.FileName) is not null ? prefix : null;
    }

    /// <summary>
    /// Unpacks the archive under <paramref name="into"/>. <b>Every link is made last</b>, after every file and folder: a
    /// link checked only by its text (<see cref="UnsafeEntry"/>) can still lead out once another link is followed — a link
    /// to <c>.</c> makes <c>d/..</c> the folder above — so nothing is ever written THROUGH a link the archive made, and
    /// a link whose own folder, or whose target, turns out to be outside is refused by name.
    /// </summary>
    private static string? Extract(ZipArchive zip, string prefix, string into, CancellationToken ct)
    {
        string root = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        string realRoot = PCellTrustStore.ResolvedDirectory(into) + Path.DirectorySeparatorChar;
        var links = new List<(ZipArchiveEntry Entry, string To)>();
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = e.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rel = name[prefix.Length..];
            if (rel.Length == 0) continue;
            string to = Path.GetFullPath(Path.Combine(into, rel));
            if (!to.StartsWith(root, StringComparison.Ordinal) && to + Path.DirectorySeparatorChar != root)
                return $"The archive's entry '{e.FullName}' leads outside the plugin's folder. Nothing was installed.";
            if (name.EndsWith('/')) { Directory.CreateDirectory(to); continue; }
            if (IsLink(e)) { links.Add((e, to)); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            e.ExtractToFile(to, overwrite: true);
            int mode = (e.ExternalAttributes >> 16) & 0x1FF;
            if (!OperatingSystem.IsWindows() && mode != 0)
                File.SetUnixFileMode(to, (UnixFileMode)mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        if (OperatingSystem.IsWindows()) return null;   // a link needs a privilege there; the archive's links are left out

        foreach (var (e, to) in links)
        {
            ct.ThrowIfCancellationRequested();
            string target;
            using (var r = new StreamReader(e.Open())) target = r.ReadToEnd();
            string parent = Path.GetDirectoryName(to)!;
            Directory.CreateDirectory(parent);
            string at = Path.Combine(PCellTrustStore.ResolvedDirectory(parent), Path.GetFileName(to));
            if (!at.StartsWith(realRoot, StringComparison.Ordinal))
                return $"The archive's entry '{e.FullName}' would be made outside the plugin's folder through another of its links. Nothing was installed.";
            var made = File.CreateSymbolicLink(at, target);
            string? resolved = null;
            try { resolved = made.ResolveLinkTarget(returnFinalTarget: true)?.FullName; } catch (IOException) { }
            resolved ??= Path.GetFullPath(target, Path.GetDirectoryName(at)!);
            if (!(PCellTrustStore.ResolvedDirectory(resolved) + Path.DirectorySeparatorChar).StartsWith(realRoot, StringComparison.Ordinal))
            {
                File.Delete(at);
                return $"The archive's entry '{e.FullName}' is a link that leads outside the plugin's folder once its other links are followed. Nothing was installed.";
            }
        }
        return null;
    }

    /// <summary>Copies a plugin folder, leaving out what is the author's and not the plugin's: version control, a
    /// development environment and Python's caches. A link leading outside the folder is refused by name.</summary>
    private static string? CopyFolder(string from, string into, CancellationToken ct)
    {
        // Links resolve to real paths, so the folder is compared by its real path too (a /tmp that is /private/tmp).
        string root = PCellTrustStore.ResolvedDirectory(from) + Path.DirectorySeparatorChar;
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if (name is ".git" or VenvFolder or "__pycache__" or ".DS_Store") continue;
                string rel = Path.GetRelativePath(from, path);
                var info = new FileInfo(path);
                string to = Path.Combine(into, rel);
                if (info.LinkTarget is not null)
                {
                    string? target = info.ResolveLinkTarget(true)?.FullName;
                    if (target is null || !(PCellTrustStore.ResolvedDirectory(target) + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal))
                        return $"'{rel}' is a link to '{info.LinkTarget}', outside the plugin's folder. Nothing was installed.";
                    // A link to a folder is copied as the link, never followed: one that points at a folder above it
                    // would otherwise be copied into itself without end.
                    if (Directory.Exists(path))
                    {
                        if (OperatingSystem.IsWindows())
                            return $"'{rel}' is a link to a folder, which an install does not copy on Windows. Nothing was installed.";
                        // An absolute target would still name the author's folder; relative, it names the copy's.
                        Directory.CreateSymbolicLink(to, Path.IsPathRooted(info.LinkTarget)
                            ? Path.GetRelativePath(PCellTrustStore.ResolvedDirectory(Path.GetDirectoryName(path)!), target) : info.LinkTarget);
                        continue;
                    }
                }
                if (Directory.Exists(path)) { Directory.CreateDirectory(to); stack.Push(path); }
                else
                {
                    File.Copy(path, to, overwrite: true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(path));
                }
            }
        }
        return null;
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            // A read-only file (a git object, a pip wheel) stops Directory.Delete on Windows.
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return !Directory.Exists(dir); }
    }

    /// <summary>Removes an id folder that a failed first install left empty.</summary>
    private static void Tidy(string idDir)
    {
        try { if (Directory.Exists(idDir) && !Directory.EnumerateFileSystemEntries(idDir).Any()) Directory.Delete(idDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
