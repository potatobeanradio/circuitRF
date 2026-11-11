using System.Security.Cryptography;
using System.Text.Json;

namespace CircuitRF.Design.Plugins;

/// <summary>One downloadable build of a listed version: where it is and the SHA-256 it must have.</summary>
public sealed record PluginIndexAsset(string Url, string Sha256);

/// <summary>One vetted version of a listed plugin (<c>plugins.md</c> §10). A version is vetted, not a plugin.</summary>
public sealed record PluginIndexVersion(
    string Version,
    string? Tag,
    string? MinCircuitRF,
    int ApiVersion,
    IReadOnlyDictionary<string, PluginIndexAsset> Assets,
    string? Reviewed)
{
    /// <summary>This machine's asset: its RID's own, else <c>any</c>, else null.</summary>
    public PluginIndexAsset? AssetFor(string? rid = null)
    {
        rid ??= PluginPlatform.CurrentRid;
        return Assets.GetValueOrDefault(rid) ?? Assets.GetValueOrDefault(PluginIndex.AnyPlatform);
    }

    /// <summary>Why this version cannot be installed by this circuitRF on this machine, or null.</summary>
    public string? Unavailable(string? running = null, string? rid = null)
        => PluginVersions.NeedsNewer(MinCircuitRF, running)
           ?? (ApiVersion > PluginApi.Max ? "needs a newer circuitRF" : null)
           ?? (AssetFor(rid) is null ? $"no build for {rid ?? PluginPlatform.CurrentRid}" : null);
}

/// <summary>One plugin on the list, its versions newest first.</summary>
public sealed record PluginIndexEntry(string Id, string Name, string? Summary, string? Repo, IReadOnlyList<PluginIndexVersion> Versions)
{
    /// <summary>The newest version, installable or not — what the Available list shows.</summary>
    public PluginIndexVersion? Latest => Versions.Count > 0 ? Versions[0] : null;

    /// <summary>The newest version this circuitRF can install here.</summary>
    public PluginIndexVersion? LatestInstallable(string? running = null, string? rid = null)
        => Versions.FirstOrDefault(v => v.Unavailable(running, rid) is null);

    /// <summary>Every platform some version ships for, as the Available list's column shows it.</summary>
    public IReadOnlyList<string> Platforms => Latest?.Assets.Keys.Order(StringComparer.Ordinal).ToList() ?? [];
}

/// <summary>Where the list in use came from.</summary>
public enum PluginIndexOrigin { Shipped, Refreshed }

/// <summary>
/// The vetted list, <c>plugins/index.json</c> (<c>plugins.md</c> §10, brief-plg-6 §2). circuitRF ships the copy from the
/// release it was built from as an embedded resource; Refresh fetches <c>main</c>'s copy and its detached signature,
/// verified with the key the updater verifies releases with, and keeps it beside the installed plugins. A copy whose
/// signature does not verify is never used — the shipped one is.
/// </summary>
public sealed record PluginIndex(IReadOnlyList<PluginIndexEntry> Plugins, PluginIndexOrigin Origin = PluginIndexOrigin.Shipped)
{
    public const int Schema = 1;
    public const string AnyPlatform = "any";
    public const string FileName = "index.json";
    public const string SignatureName = FileName + ".sig";

    /// <summary>Where Refresh fetches from: this repository's <c>main</c>.</summary>
    public const string MainUrl = "https://raw.githubusercontent.com/potatobeanradio/circuitRF/main/plugins/" + FileName;

    private const string ResourceName = "CircuitRF.Design.plugins.index.json";

    /// <summary>The listed plugin called <paramref name="id"/>, or null.</summary>
    public PluginIndexEntry? Find(string id) => Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>The copy built into this circuitRF. Never throws: a broken resource is an empty list.</summary>
    public static PluginIndex Shipped()
    {
        using var s = typeof(PluginIndex).Assembly.GetManifestResourceStream(ResourceName);
        if (s is null) return new PluginIndex([]);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Read(ms.ToArray()).Index ?? new PluginIndex([]);
    }

    /// <summary>The list in use: the refreshed copy kept in <paramref name="cacheDir"/> when its signature verifies
    /// under <paramref name="publicKeySpkiBase64"/>, else the shipped one. Reads files only.</summary>
    public static PluginIndex Current(string? cacheDir, string? publicKeySpkiBase64)
    {
        if (cacheDir is not null)
            try
            {
                string file = Path.Combine(cacheDir, FileName), sig = Path.Combine(cacheDir, SignatureName);
                if (File.Exists(file) && File.Exists(sig))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    if (VerifySignature(bytes, File.ReadAllText(sig), publicKeySpkiBase64) && Read(bytes).Index is { } kept)
                        return kept with { Origin = PluginIndexOrigin.Refreshed };
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Shipped();
    }

    /// <summary>
    /// Refresh (brief-plg-6 §2): fetch <paramref name="url"/> and its <c>.sig</c>, verify, and keep both in
    /// <paramref name="cacheDir"/>. Returns the new list, or the refusal — a bad signature, an unreadable list, a request
    /// that failed — with the cache untouched, so the copy in use stays what it was.
    /// </summary>
    public static async Task<(PluginIndex? Index, string? Refusal)> RefreshAsync(
        IPluginHttp http, string cacheDir, string? publicKeySpkiBase64, string url = MainUrl, CancellationToken ct = default)
    {
        var list = await http.GetBytesAsync(url, ct);
        if (list.Failure is { } failed) return (null, failed);
        byte[]? bytes = list.Body, sig = null;
        if (bytes is not null)
        {
            var signature = await http.GetBytesAsync(url + ".sig", ct);
            if (signature.Failure is { } sigFailed) return (null, sigFailed);
            sig = signature.Body;
        }
        if (bytes is null) return (null, $"The plugin list was not found at {url}.");
        if (sig is null) return (null, "The plugin list has no signature, so it was not used. The list built into circuitRF is still in use.");

        string sigText = System.Text.Encoding.ASCII.GetString(sig);
        if (!VerifySignature(bytes, sigText, publicKeySpkiBase64))
            return (null, "The plugin list's signature does not verify, so it was not used. The list built into circuitRF is still in use.");

        var (index, errors) = Read(bytes);
        if (index is null) return (null, $"The plugin list could not be read: {string.Join("; ", errors)}");

        Directory.CreateDirectory(cacheDir);
        string tmp = Path.Combine(cacheDir, FileName + ".tmp"), tmpSig = Path.Combine(cacheDir, SignatureName + ".tmp");
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        await File.WriteAllTextAsync(tmpSig, sigText, ct);
        File.Move(tmp, Path.Combine(cacheDir, FileName), overwrite: true);
        File.Move(tmpSig, Path.Combine(cacheDir, SignatureName), overwrite: true);
        return (index with { Origin = PluginIndexOrigin.Refreshed }, null);
    }

    /// <summary>
    /// Whether <paramref name="signatureBase64"/> is an ECDSA P-256 / SHA-256 signature over exactly
    /// <paramref name="data"/> by <paramref name="publicKeySpkiBase64"/> — the updater's scheme
    /// (<c>tools/ReleaseSigner</c>). False for anything else, never a throw. No key is never valid.
    /// </summary>
    public static bool VerifySignature(ReadOnlySpan<byte> data, string? signatureBase64, string? publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) || string.IsNullOrWhiteSpace(signatureBase64) || signatureBase64.Length > 1024) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64.Trim()), out _);
            if (ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Reads and checks a list: the schema, required fields, a duplicate <c>id</c> or <c>id</c>/<c>version</c>, an asset
    /// without a SHA-256, and an asset URL not on <c>https://github.com</c> are each an error naming where. The list is
    /// null when there is any error. Unknown fields are ignored.
    /// </summary>
    public static (PluginIndex? Index, IReadOnlyList<string> Errors) Read(ReadOnlySpan<byte> json)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { return (null, [$"not JSON: {e.Message}"]); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, ["the list is not a JSON object"]);
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema)
                errors.Add($"schema: must be {Schema}");
            if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                return (null, [.. errors, "plugins: must be an array"]);

            var entries = new List<PluginIndexEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            foreach (var p in plugins.EnumerateArray())
            {
                string at = $"plugins[{i++}]";
                if (p.ValueKind != JsonValueKind.Object) { errors.Add($"{at}: not an object"); continue; }
                string? id = Str(p, "id"), name = Str(p, "name");
                if (string.IsNullOrWhiteSpace(id)) { errors.Add($"{at}.id: missing"); continue; }
                at = id;
                if (!ids.Add(id)) errors.Add($"{id}: listed twice");
                if (string.IsNullOrWhiteSpace(name)) errors.Add($"{id}.name: missing");
                string? repo = Str(p, "repo");
                if (repo is not null && !OnGitHub(repo)) errors.Add($"{id}.repo: {repo} is not a https://github.com address");

                var versions = new List<PluginIndexVersion>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (!p.TryGetProperty("versions", out var vs) || vs.ValueKind != JsonValueKind.Array || vs.GetArrayLength() == 0)
                    errors.Add($"{id}.versions: must be a non-empty array");
                else
                    foreach (var v in vs.EnumerateArray())
                    {
                        string? version = v.ValueKind == JsonValueKind.Object ? Str(v, "version") : null;
                        if (string.IsNullOrWhiteSpace(version)) { errors.Add($"{id}.versions: an entry has no version"); continue; }
                        string vat = $"{id} {version}";
                        if (!seen.Add(version)) errors.Add($"{vat}: listed twice");
                        int api = v.TryGetProperty("apiVersion", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                        if (api < 1) errors.Add($"{vat}.apiVersion: missing");

                        var assets = new Dictionary<string, PluginIndexAsset>(StringComparer.Ordinal);
                        if (!v.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Object || !assetsEl.EnumerateObject().Any())
                            errors.Add($"{vat}.assets: must name at least one platform");
                        else
                            foreach (var asset in assetsEl.EnumerateObject())
                            {
                                string aat = $"{vat}.assets.{asset.Name}";
                                if (asset.Name != AnyPlatform && !PluginPlatform.Rids.Contains(asset.Name))
                                    errors.Add($"{aat}: '{asset.Name}' is not a platform; it takes {AnyPlatform} or {string.Join(", ", PluginPlatform.Rids)}");
                                string? url = asset.Value.ValueKind == JsonValueKind.Object ? Str(asset.Value, "url") : null;
                                string? sha = asset.Value.ValueKind == JsonValueKind.Object ? Str(asset.Value, "sha256") : null;
                                if (url is null) errors.Add($"{aat}.url: missing");
                                else if (!OnGitHub(url)) errors.Add($"{aat}.url: {url} is not on https://github.com");
                                if (string.IsNullOrWhiteSpace(sha)) errors.Add($"{aat}.sha256: missing");
                                else if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) errors.Add($"{aat}.sha256: not a SHA-256 in hex");
                                if (url is not null && sha is not null) assets[asset.Name] = new PluginIndexAsset(url, sha.ToLowerInvariant());
                            }
                        versions.Add(new PluginIndexVersion(version, Str(v, "tag"), Str(v, "minCircuitRF"), api, assets, Str(v, "reviewed")));
                    }

                entries.Add(new PluginIndexEntry(id, name ?? id, Str(p, "summary"), repo,
                    versions.OrderByDescending(v => v.Version, PluginVersions.Comparer).ToList()));
            }
            return errors.Count == 0 ? (new PluginIndex(entries), errors) : (null, errors);
        }
    }

    /// <summary>Whether <paramref name="url"/> is an https address on <c>github.com</c> itself.</summary>
    public static bool OnGitHub(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
           && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
