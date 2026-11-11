using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.Plugins;

/// <summary>
/// The plugin settings this installation has recorded, read from a <c>preferences.json</c> with no application running
/// (brief-plg-5): what <c>circuitRF plugin</c> and the MCP tool list, permit and start with. The keys are the ones
/// <c>AppPreferences</c> writes, so the command line and the window answer from one record. A missing or malformed file
/// is no settings, never a throw.
/// </summary>
/// <param name="Path">The preferences file these were read from — where <see cref="Trust"/> records a decision.</param>
public sealed record PluginPreferences(
    string Path,
    bool? Enabled,
    IReadOnlyList<string> LinkedFolders,
    IReadOnlyDictionary<string, string> ActiveVersions)
{
    public const string LinksKey  = "plugin_links";
    public const string ActiveKey = "plugin_active";

    /// <summary>The settings recorded in <paramref name="path"/> (this user's by default).</summary>
    public static PluginPreferences Read(string? path = null)
    {
        path ??= UserStateDirectory.PreferencesPath;
        bool? enabled = null;
        var links = new List<string>();
        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
            {
                if (root[PluginPolicy.PreferenceKey] is JsonValue e && e.TryGetValue<bool>(out bool on)) enabled = on;
                if (root[LinksKey] is JsonArray la)
                    foreach (var l in la)
                        if (l is JsonValue lv && lv.TryGetValue<string>(out var s) && s.Length > 0) links.Add(s);
                if (root[ActiveKey] is JsonObject ao)
                    foreach (var (id, v) in ao)
                        if (v is JsonValue vv && vv.TryGetValue<string>(out var ver)) active[id] = ver;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new PluginPreferences(path, enabled, links, active);
    }

    /// <summary>Where the plugins menu looks, with <c>CRF_PLUGIN_PATH</c> from <paramref name="environment"/>.</summary>
    public PluginCatalogSources Sources(Func<string, string?>? environment = null)
        => new(PluginCatalogSources.DefaultRoot, LinkedFolders,
               (environment ?? Environment.GetEnvironmentVariable)(PluginCatalogSources.EnvironmentVariable), ActiveVersions);

    /// <summary>The policy, the administrator's marker beside <paramref name="installRoot"/> first.</summary>
    public PluginPolicyState Policy(string? installRoot, Func<string, string?>? environment = null)
        => PluginPolicy.Resolve(installRoot, Enabled, environment);

    /// <summary>
    /// The decisions recorded in this file, READ-ONLY. Nothing below the window may grant a plugin permission — the
    /// person does that in circuitRF itself (owner decision, 2026-11-10) — so the store a headless caller gets writes
    /// nothing; <see cref="RecordDenial"/> is the one change it may make.
    /// </summary>
    public PluginTrustStore Trust() => PluginTrustStore.FromRecordedPreferences(Path);

    /// <summary>
    /// Takes a plugin's permission away: records <paramref name="pluginDirectory"/> as denied under
    /// <see cref="PluginTrustStore.PreferenceKey"/>, the key the window's prompt writes, merged so every other setting in
    /// the file is kept. Returns whether the file now says so. There is deliberately no counterpart that allows.
    /// </summary>
    public bool RecordDenial(string pluginDirectory)
    {
        var decisions = new Dictionary<string, bool>(PluginTrustStore.FromRecordedPreferences(Path).Decisions, StringComparer.OrdinalIgnoreCase)
        {
            [PluginTrustStore.Key(pluginDirectory)] = false,
        };
        try { WriteTrust(Path, decisions); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return PluginTrustStore.FromRecordedPreferences(Path).Decide(pluginDirectory) == PluginTrustDecision.Denied;
    }

    private static void WriteTrust(string path, IReadOnlyDictionary<string, bool> decisions)
    {
        JsonObject root = new();
        if (File.Exists(path))
        {
            // A file that is not a JSON object is left alone rather than replaced: it holds every other setting, and the
            // caller learns the decision was not recorded by reading it back.
            try { if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject read) root = read; else return; }
            catch (JsonException) { return; }
        }

        var table = new JsonObject();
        foreach (var (key, allowed) in decisions.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase)) table[key] = allowed;
        root[PluginTrustStore.PreferenceKey] = table;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>One headless run, asked for (<c>plugins.md</c> P-7). Every input is an argument.</summary>
/// <param name="Given">Parameter text by name — <c>--set name=value</c>. A parameter not given starts where the panel
/// starts it (<see cref="PluginParams.StartingText"/>).</param>
/// <param name="WorkspaceDir">The workspace parameters resolve against and the plugin is told about.</param>
/// <param name="ContextPath">What the plugin was opened on — a cell, a layout, a result — or null.</param>
/// <param name="PlaceInto">With a workspace, <c>commit</c> follows the <c>update</c> and the cells are placed there.</param>
/// <param name="CellName">The name to place under (one cell); null takes the plugin's.</param>
/// <param name="Replace">Write over an existing cell of that name.</param>
public sealed record PluginHeadlessRequest(
    IReadOnlyDictionary<string, string> Given,
    string? WorkspaceDir = null,
    string? ContextPath = null,
    string? PlaceInto = null,
    string? CellName = null,
    bool Replace = false);

/// <summary>What one headless run did. The session stays open — the staging folder its views name with it — until
/// this is disposed, so a caller can write the views out first.</summary>
public sealed class PluginHeadlessRun : IAsyncDisposable
{
    private readonly PluginSession? _session;

    internal PluginHeadlessRun(PluginSession? session) => _session = session;

    /// <summary>Why nothing was asked of the plugin: a parameter that does not resolve, or a plugin that has no headless
    /// form. Empty when it was asked.</summary>
    public IReadOnlyList<string> Refusals { get; init; } = [];
    /// <summary>The process host's refusal or failure — not permitted, disabled, would not start, exited, timed out.</summary>
    public PluginFailure? Failure { get; init; }
    /// <summary>The <c>params</c> the plugin was sent.</summary>
    public JsonObject? Values { get; init; }
    /// <summary>The <c>update</c>'s reply.</summary>
    public PluginReply? Reply { get; init; }
    /// <summary>Each view of the reply as a panel holds it, ready for <see cref="PluginViews.Read"/>.</summary>
    public IReadOnlyList<PluginViewBody> Views { get; init; } = [];
    /// <summary><c>commit</c>'s answer, when a placement was asked for and the update had no error.</summary>
    public PluginPlaced? Placed { get; init; }
    /// <summary>Each staged cell's placement, in order; the run stops at the first that was not placed.</summary>
    public IReadOnlyList<PluginPlacementResult> Placements { get; init; } = [];
    public string? StagingDirectory => _session?.StagingDirectory;
    public IReadOnlyList<string> StderrTail => _session?.StderrTail ?? Failure?.StderrTail ?? [];

    /// <summary>Whether anything stopped the run short of what was asked: a refusal, a failure, a plugin error, or a
    /// placement that did not happen.</summary>
    public bool Failed => Refusals.Count > 0 || Failure is not null || Reply?.HasError == true || Placed?.HasError == true
                          || Placements.Any(p => p.Outcome != PluginPlacementOutcome.Placed)
                          || (Placed is { Cells.Count: 0 });

    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.ByeAsync();
    }
}

/// <summary>
/// <c>circuitRF plugin run</c> and the MCP <c>plugin</c> tool (brief-plg-5): <c>hello</c> in <c>headless</c> mode, one
/// <c>update</c> with the panel's starting values overridden by what was given, and with a workspace to place into,
/// <c>commit</c> and <see cref="PluginPlacement.Place"/> — the function the panel's Place in workspace calls, handed the
/// layout view the same way (<see cref="PluginPlacement.LayoutViewFor"/>). Nothing here asks a question: an existing
/// cell comes back as <see cref="PluginPlacementOutcome.NameTaken"/>, which the caller turns into its own refusal.
/// </summary>
public static class PluginHeadless
{
    /// <summary>The plugin called <paramref name="id"/>, or null.</summary>
    public static PluginEntry? Find(IReadOnlyList<PluginEntry> entries, string id)
        => entries.FirstOrDefault(e => string.Equals(e.Manifest?.Id, id, StringComparison.Ordinal))
           ?? entries.FirstOrDefault(e => e.Manifest is null && string.Equals(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(e.Directory)), id, StringComparison.Ordinal));

    /// <summary>
    /// <c>hello</c>'s <c>context</c> for a run opened on <paramref name="path"/> (<c>plugins.md</c> §6.2): the path, and
    /// which of the manifest's <c>contexts</c> it is — a folder is a cell, a <c>.clay</c> a layout, a <c>.ctech</c> a
    /// technology, a Touchstone file or run <c>.npy</c> a result.
    /// </summary>
    public static JsonObject Context(string? workspaceDir, string? path)
    {
        var o = new JsonObject();
        if (workspaceDir is not null) o["workspace"] = workspaceDir;
        if (path is null) return o;
        string full = System.IO.Path.GetFullPath(path);
        string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        string? kind = Directory.Exists(full) ? "cell"
                     : ext == ".clay" ? "layout"
                     : ext == ".ctech" ? "technology"
                     : ext == ".npy" || System.Text.RegularExpressions.Regex.IsMatch(ext, @"^\.s\d+p$") ? "result"
                     : null;
        if (kind is not null) o[kind] = full;
        o["path"] = full;
        return o;
    }

    /// <summary>The parameters as the plugin receives them: each given text, else where the panel starts it.</summary>
    public static (JsonObject? Values, IReadOnlyList<string> Errors) Resolve(PluginManifest manifest, IReadOnlyDictionary<string, string> given, string? workspaceDir)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in manifest.Params.Where(p => p.Kind != PluginParamKind.Readout))
        {
            if (!given.TryGetValue(p.Name, out var g)) { texts[p.Name] = PluginParams.StartingText(p, null, workspaceDir); continue; }
            // A picker's value may be given by its short name — a conductor, a technology or cell folder's name — when
            // that names one choice; a value that names none is refused with every choice listed.
            if (PluginParams.ChoicesFor(p, workspaceDir) is { } choices && p.Kind != PluginParamKind.Choice)
            {
                g = Pick(choices, g) ?? g;
                if (PluginParams.ResolveValue(p, g, new PluginParamContext(workspaceDir)).Error is { } bad)
                {
                    errors.Add($"{p.DisplayLabel}: {bad}" + (workspaceDir is null ? ""
                        : choices.Count == 0 ? " The workspace offers none."
                        : $" The workspace offers: {string.Join("; ", choices)}."));
                    refused.Add(p.DisplayLabel);
                }
            }
            texts[p.Name] = g;
        }
        foreach (var (name, text) in given)
            if (!texts.ContainsKey(name) && !manifest.Params.Any(p => p.Name == name && p.Kind != PluginParamKind.Readout))
                texts[name] = text;   // unknown or a readout: Resolve refuses it by name
        var (values, more) = PluginParams.Resolve(manifest.Params, texts, new PluginParamContext(workspaceDir));
        errors.AddRange(more.Where(e => !refused.Any(label => e.StartsWith(label + ": ", StringComparison.Ordinal))));
        return (errors.Count == 0 ? values : null, errors);

        static string? Pick(IReadOnlyList<string> choices, string text)
        {
            string t = text.Trim();
            if (choices.Contains(t)) return t;
            var byName = choices.Where(c => string.Equals(ShortName(c), t, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        static string ShortName(string choice)
        {
            int sep = choice.IndexOf(" : ", StringComparison.Ordinal);
            if (sep >= 0) return choice[..sep].Trim();
            return System.IO.Path.GetFileNameWithoutExtension(choice.TrimEnd('/'));
        }
    }

    /// <summary>Why <paramref name="manifest"/> cannot be run with no display, or null.</summary>
    public static string? NoHeadlessForm(PluginManifest manifest)
        => manifest.HasHeadlessForm ? null
         : $"{manifest.Name} is a window plugin with no headless form: it declares no parameters, so there is nothing to run without its own window.";

    public static async Task<PluginHeadlessRun> RunAsync(PluginEntry entry, PluginHeadlessRequest request, PluginStartOptions options, CancellationToken ct = default)
    {
        // Policy and permission before anything else is judged, as the process host orders them.
        if (PluginSession.Refusal(entry, options) is { } refused0) return new PluginHeadlessRun(null) { Failure = refused0 };
        if (entry.Manifest is { } m && NoHeadlessForm(m) is { } windowOnly) return new PluginHeadlessRun(null) { Refusals = [windowOnly] };

        // Then the parameters: a value that does not resolve is refused before any process exists.
        JsonObject? values = null;
        if (entry.Manifest is { } manifest)
        {
            var (resolved, errors) = Resolve(manifest, request.Given, request.WorkspaceDir);
            if (errors.Count > 0) return new PluginHeadlessRun(null) { Refusals = errors };
            values = resolved;
        }

        var started = PluginSession.Start(entry, options with
        {
            Mode = "headless",
            WorkspaceDir = request.WorkspaceDir,
            Context = request.ContextPath is null ? options.Context : Context(request.WorkspaceDir, request.ContextPath),
        });
        if (started.Failure is { } refused) return new PluginHeadlessRun(null) { Failure = refused };
        var session = started.Value!;

        var hello = await session.HelloAsync(ct);
        if (hello.Failure is { } hf) return new PluginHeadlessRun(session) { Failure = hf, Values = values };

        var update = await session.UpdateAsync(values!, ct);
        if (update.Failure is { } uf) return new PluginHeadlessRun(session) { Failure = uf, Values = values };
        var reply = update.Value!;

        var bodies = new List<PluginViewBody>();
        foreach (var (name, body) in reply.Views)
            if (body is not null)
                bodies.Add(new PluginViewBody(name, body.DeepClone(), reply.Payload, (JsonObject)values!.DeepClone(), 1)
                { StagingDir = session.StagingDirectory });

        if (request.PlaceInto is not { } workspace || reply.HasError)
            return new PluginHeadlessRun(session) { Values = values, Reply = reply, Views = bodies };

        var placed = await session.CommitAsync(values!, ct);
        if (placed.Failure is { } cf) return new PluginHeadlessRun(session) { Failure = cf, Values = values, Reply = reply, Views = bodies };
        var answer = placed.Value!;

        var placements = new List<PluginPlacementResult>();
        if (!answer.HasError)
            foreach (var cell in answer.Cells)
            {
                var result = PluginPlacement.Place(new PluginPlacementRequest(workspace, session.Manifest, cell, answer.Messages,
                    CellName: answer.Cells.Count == 1 ? request.CellName : null,
                    Replace: request.Replace,
                    Layout: cell.LayoutView is { } lv
                        ? PluginPlacement.LayoutViewFor(bodies.FirstOrDefault(b => b.Name == lv), lv, session.Manifest, request.WorkspaceDir ?? workspace)
                        : null));
                placements.Add(result);
                if (result.Outcome != PluginPlacementOutcome.Placed) break;
            }

        return new PluginHeadlessRun(session)
        {
            Values = values, Reply = reply, Views = bodies, Placed = answer, Placements = placements,
        };
    }
}
