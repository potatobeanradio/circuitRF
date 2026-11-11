using System.Text.Json.Nodes;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitRF plugin list | describe | run | deny</c> — a plugin with no display (brief-plg-5,
/// <c>plugins.md</c> §11 and P-7), for a script, a build machine or an agent.
///
/// <para><b>It owns no plugin logic.</b> Discovery is <see cref="PluginCatalog"/>, the settings
/// <see cref="PluginPreferences"/>, the run <see cref="PluginHeadless.RunAsync"/> — the process host, the parameters
/// the panel's own way, and <see cref="PluginPlacement.Place"/>, which the panel's Place in workspace calls — and the
/// views are written by <see cref="PluginViewFiles"/>. What is here is argument parsing, refusals and reporting, on
/// <c>Authoring.cs</c>' terms, and a source scan holds it there.</para>
///
/// <para><b>There is no <c>allow</c>, on purpose.</b> Permission to run a plugin — someone else's program, with the
/// user's own permissions — is granted only by the person, in the window: the prompt when they open it from Tools ▸
/// Plugins, or Settings ▸ Plugins. Anything that can type a command line can be an agent, so a command line can only
/// take permission away (<c>deny</c>). Owner decision, 2026-11-10.</para>
///
/// <para><b>Install and uninstall are not here yet</b>; they arrive with brief-plg-6.</para>
/// </summary>
internal static class Plugin
{
    /// <summary>The installation's root, beside which an administrator's <c>no-plugins</c> marker sits: what the installed
    /// executable said (<see cref="CliEntry.InstallRoot"/>), else beside this assembly's own folder.</summary>
    private static string? InstallRoot() => CliEntry.InstallRoot?.Invoke() ?? AppContext.BaseDirectory;

    private const string Nouns = "list, describe, run or deny";

    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage($"name what to do: {Nouns}.");
        string noun = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        return noun switch
        {
            "list"     => List(rest),
            "describe" => Describe(rest),
            "run"      => RunPlugin(rest),
            "allow"    => JsonRun.Fail(CliDiagnostics.PluginAllowOnlyInTheWindow()),
            "deny"     => Deny(rest),
            "install" or "uninstall" => JsonRun.Fail(CliDiagnostics.PluginUsage(
                $"'{noun}' is not available in this build. A plugin in a folder of its own runs in place when {PluginCatalogSources.EnvironmentVariable} names the folder.")),
            _ => Usage($"'{args[0]}' is not something plugin does; it takes {Nouns}."),
        };
    }

    private static int Usage(string why)
    {
        JsonRun.Report(CliDiagnostics.PluginUsage(why));
        Console.Error.WriteLine(
            "Usage: circuitrf plugin list\n" +
            "       circuitrf plugin describe <id>\n" +
            "       circuitrf plugin run <id> [--set name=value]... [--workspace <dir>] [--context <path>]\n" +
            "                            [-o <dir>] [--place <workspace> [--cell <name>] [--replace | --new-name <name>]]\n" +
            "       circuitrf plugin deny <id>");
        return 1;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    private static int List(string[] args)
    {
        if (args.Length > 0) return JsonRun.Fail(CliDiagnostics.PluginUnknownOption("list", args[0], "nothing but --json"));
        var prefs = PluginPreferences.Read();
        var policy = prefs.Policy(InstallRoot());
        var trust = prefs.Trust();
        var entries = PluginCatalog.Discover(prefs.Sources());

        var rows = new JsonArray();
        foreach (var e in entries)
        {
            var m = e.Manifest;
            string permitted = Permitted(trust, e);
            string? dim = e.DimReason;
            rows.Add(new JsonObject
            {
                ["id"] = m?.Id, ["name"] = e.DisplayName, ["version"] = m?.Version, ["kind"] = KindName(m),
                ["source"] = SourceName(e), ["directory"] = e.Directory, ["permitted"] = permitted,
                ["available"] = dim is null, ["reason"] = dim,
            });
            Console.WriteLine($"{m?.Id ?? e.DisplayName,-32} {m?.Version ?? "-",-10} {KindName(m) ?? "-",-7} {SourceName(e),-9} {permitted,-11}"
                              + (dim is null ? "" : $" dimmed: {dim}"));
        }
        if (entries.Count == 0) Console.WriteLine($"No plugins are installed or linked. ({PluginCatalogSources.DefaultRoot}, {PluginCatalogSources.EnvironmentVariable})");
        if (!policy.Allowed) JsonRun.Report(CliDiagnostics.PluginDisabled(policy.Reason));

        JsonRun.Plugin = new JsonObject
        {
            ["action"] = "list", ["plugins"] = rows,
            ["policy"] = new JsonObject { ["allowed"] = policy.Allowed, ["reason"] = policy.Reason.Length > 0 ? policy.Reason : null },
        };
        return 0;
    }

    // ── describe ──────────────────────────────────────────────────────────────

    private static int Describe(string[] args)
    {
        if (args.Length != 1 || args[0].StartsWith('-'))
            return args.Length == 0 ? Usage("describe names a plugin.")
                 : JsonRun.Fail(CliDiagnostics.PluginUnknownOption("describe", args[^1], "one plugin id"));
        var prefs = PluginPreferences.Read();
        if (Locate(prefs, args[0]) is not { } entry) return NotFound(prefs, args[0]);
        if (entry.Manifest is not { } m)
        {
            foreach (var d in entry.Diagnostics) JsonRun.Report(CliDiagnostics.CheckPluginManifest(entry.Directory, d.Field, d.Message, d.IsError));
            return 1;
        }

        var trust = prefs.Trust();
        var panel = PluginLayoutReader.Read(m).Layout;
        var run = m.ResolveRun();

        Console.WriteLine($"{m.Name} {m.Version} ({m.Id}) — a {KindName(m)} plugin");
        if (m.Summary is { } summary) Console.WriteLine($"  {summary}");
        Console.WriteLine($"  from {SourceName(entry)} {entry.Directory}; {Permitted(trust, entry)}");
        if (m.Author is not null || m.License is not null) Console.WriteLine($"  by {m.Author ?? "-"}, licence {m.License ?? "-"}");
        Console.WriteLine($"  runs here as: {(run.Entry is { } how ? How(how) : run.Refusal)}");
        Console.WriteLine("  parameters:");
        foreach (var p in m.Params)
            Console.WriteLine($"    {p.Name,-16} {p.Kind.ToString().ToLowerInvariant(),-10} {Facts(p)}");
        if (panel?.Views is { Count: > 0 } views)
        {
            Console.WriteLine("  views:");
            foreach (var v in views) Console.WriteLine($"    {v.Name,-16} {v.Kind}");
        }

        var platforms = new JsonObject();
        foreach (var (rid, entryHow) in m.Run)
            platforms[rid] = entryHow.IsPython
                ? new JsonObject { ["python"] = entryHow.PythonEntry, ["requiresPython"] = entryHow.RequiresPython }
                : new JsonObject { ["exe"] = entryHow.Exe };

        JsonRun.Plugin = new JsonObject
        {
            ["action"] = "describe",
            ["manifest"] = new JsonObject
            {
                ["apiVersion"] = m.ApiVersion, ["id"] = m.Id, ["name"] = m.Name, ["version"] = m.Version,
                ["summary"] = m.Summary, ["author"] = m.Author, ["license"] = m.License, ["homepage"] = m.Homepage,
                ["minCircuitRF"] = m.MinCircuitRF, ["kind"] = KindName(m), ["menu"] = m.Menu,
                ["contexts"] = new JsonArray([.. m.Contexts.Select(c => (JsonNode)c)]),
                ["params"] = new JsonArray([.. m.Params.Select(p => (JsonNode)Param(p))]),
                ["views"] = new JsonArray([.. (panel?.Views ?? []).Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["kind"] = v.Kind })]),
                ["run"] = platforms,
                ["timeouts"] = new JsonObject { ["update"] = m.Timeouts.Update, ["action"] = m.Timeouts.Action, ["commit"] = m.Timeouts.Commit },
            },
            ["directory"] = entry.Directory, ["source"] = SourceName(entry), ["permitted"] = Permitted(trust, entry),
            ["headless"] = m.HasHeadlessForm,
            ["thisMachine"] = new JsonObject
            {
                ["platform"] = run.Platform,
                ["runs"] = run.Entry is { } e2 ? How(e2) : null,
                ["reason"] = entry.DimReason,
            },
        };
        return 0;
    }

    // ── run ───────────────────────────────────────────────────────────────────

    private static int RunPlugin(string[] args)
    {
        const string takes = "--set name=value, --workspace, --context, -o, --place, --cell, --replace and --new-name";
        string? id = null, outDir = null, place = null, cell = null, contextPath = null;
        bool replace = false, newName = false;
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--set" when i + 1 < args.Length:
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.PluginUsage($"--set takes name=value, and '{kv}' is not."));
                    given[kv[..eq].Trim()] = kv[(eq + 1)..];
                    continue;
                }
                case "-o" or "--output" when i + 1 < args.Length: outDir = args[++i]; continue;
                case "--place" when i + 1 < args.Length: place = args[++i]; continue;
                case "--cell" when i + 1 < args.Length: cell = args[++i]; continue;
                case "--new-name" when i + 1 < args.Length: cell = args[++i]; newName = true; continue;
                case "--context" when i + 1 < args.Length: contextPath = args[++i]; continue;
                case "--replace": replace = true; continue;
            }
            if (args[i].StartsWith('-') || id is not null) return JsonRun.Fail(CliDiagnostics.PluginUnknownOption("run", args[i], takes));
            id = args[i];
        }
        if (id is null) return Usage("run names a plugin.");
        if (replace && newName) return JsonRun.Fail(CliDiagnostics.PluginUsage("--replace and --new-name are two answers to one question; give one."));
        if (place is null && (cell is not null || replace))
            return JsonRun.Fail(CliDiagnostics.PluginUsage("--cell, --replace and --new-name name where --place puts a cell; give --place too."));

        string? placeInto = place is null ? null : WorkspaceRoot(place);
        if (place is not null && placeInto is null)
            return JsonRun.Fail(CliDiagnostics.PluginUsage($"--place takes a workspace (a folder holding a .cws), and '{place}' is not one."));
        if (contextPath is not null && !File.Exists(contextPath) && !Directory.Exists(contextPath))
            return JsonRun.Fail(CliDiagnostics.PluginUsage($"--context names '{contextPath}', which does not exist."));

        var prefs = PluginPreferences.Read();
        if (Locate(prefs, id) is not { } entry) return NotFound(prefs, id);
        string label = entry.Manifest?.Id ?? id;
        JsonRun.InputPath = entry.Directory;

        string? workspace = CliEntry.HostWorkspace ?? placeInto;
        var options = new PluginStartOptions
        {
            Trust = prefs.Trust(), Policy = prefs.Policy(InstallRoot()), Verbs = PluginVerbs.Instance,
        };
        var request = new PluginHeadlessRequest(given, workspace, contextPath, placeInto, cell, replace);

        PluginHeadlessRun run;
        try { run = PluginHeadless.RunAsync(entry, request, options, RunHost.Cancellation).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { JsonRun.Report(CliDiagnostics.PluginCancelled()); return 130; }

        try { return Report(run, entry, label, workspace, outDir, placeInto, options.Trust.Decide(entry.Directory)); }
        finally { run.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static int Report(PluginHeadlessRun run, PluginEntry entry, string label, string? workspace, string? outDir, string? placeInto,
                              PluginTrustDecision decision)
    {
        foreach (string why in run.Refusals) JsonRun.Report(CliDiagnostics.PluginRefused(label, why));

        if (run.Failure is { } f)
        {
            if (f.Kind == PluginFailureKind.Cancelled) { JsonRun.Report(CliDiagnostics.PluginCancelled()); return 130; }
            JsonRun.Report(f.Kind switch
            {
                PluginFailureKind.NotPermitted     => CliDiagnostics.PluginNotPermitted(label, entry.DisplayName,
                                                          decision == PluginTrustDecision.Denied ? "denied" : "not-asked", entry.Directory),
                PluginFailureKind.DisabledByPolicy => CliDiagnostics.PluginDisabled(f.Sentence),
                _                                  => CliDiagnostics.PluginFailed(label, f.Kind.ToString(), f.Sentence),
            });
            if (f.Kind is PluginFailureKind.Exited or PluginFailureKind.Protocol or PluginFailureKind.Timeout or PluginFailureKind.StartFailed)
                foreach (string line in run.StderrTail.TakeLast(5)) JsonRun.Report(CliDiagnostics.PluginStderr(line));
        }

        var manifest = entry.Manifest;
        var views = new JsonArray();
        if (run.Reply is { } reply)
        {
            foreach (var msg in reply.Messages) JsonRun.Report(CliDiagnostics.PluginMessage(label, msg.Severity, msg.Text));
            foreach (var p in manifest!.Params.Where(p => p.Kind == PluginParamKind.Readout))
                if (reply.Readouts[p.Name] is { } value)
                {
                    var shown = PluginValueFormat.Readout(value, p.Unit);
                    Console.WriteLine(shown.IsTable
                        ? $"{p.DisplayLabel}:\n" + string.Join("\n", shown.Rows!.Select(r => "  " + string.Join("  ", r)))
                        : $"{p.DisplayLabel}: {shown.Text}");
                }

            if (outDir is not null)
            {
                string dir = Path.GetFullPath(outDir);
                foreach (var file in PluginViewFiles.Write(run.Views, manifest, workspace, dir))
                {
                    if (file.Problem is { } problem) JsonRun.Report(CliDiagnostics.PluginViewNotWritten(file.Name, problem));
                    foreach (string note in file.Notes) JsonRun.Report(CliDiagnostics.PluginMessage(label, "warning", note));
                    foreach (string path in file.Files) { Console.WriteLine(path); JsonRun.AddOutput(JsonRun.KindOf(path), path); }
                    views.Add(new JsonObject
                    {
                        ["name"] = file.Name, ["kind"] = file.Kind,
                        ["files"] = new JsonArray([.. file.Files.Select(x => (JsonNode)x)]), ["problem"] = file.Problem,
                    });
                }
                string readouts = PluginViewFiles.WriteReadouts(reply.Readouts, dir);
                Console.WriteLine(readouts);
                JsonRun.AddOutput("json", readouts);
            }
            else
                foreach (var b in run.Views) views.Add(new JsonObject { ["name"] = b.Name });
        }

        var placed = new JsonArray();
        if (run.Placed is { } answer)
        {
            foreach (var msg in answer.Messages) JsonRun.Report(CliDiagnostics.PluginMessage(label, msg.Severity, msg.Text));
            if (answer.Cells.Count == 0) JsonRun.Report(CliDiagnostics.PluginPlacedNothing(label));
            foreach (var result in run.Placements)
            {
                switch (result.Outcome)
                {
                    case PluginPlacementOutcome.Placed:
                        Console.WriteLine(result.CellDir);
                        JsonRun.AddOutput("cell", result.CellDir!);
                        placed.Add(new JsonObject
                        {
                            ["cell"] = result.CellName, ["directory"] = result.CellDir,
                            ["written"] = new JsonArray([.. result.Written.Select(w => (JsonNode)w)]),
                            ["restorePoint"] = result.Checkpoint?.ToString(),
                        });
                        break;
                    case PluginPlacementOutcome.NameTaken:
                        JsonRun.Report(CliDiagnostics.PluginCellExists(result.CellName, placeInto!));
                        break;
                    default:
                        foreach (string why in result.Refusals) JsonRun.Report(CliDiagnostics.PluginPlacementRefused(why));
                        break;
                }
            }
        }

        JsonRun.Plugin = new JsonObject
        {
            ["action"] = "run", ["id"] = label, ["version"] = manifest?.Version,
            ["params"] = run.Values?.DeepClone(),
            ["readouts"] = run.Reply?.Readouts.DeepClone(),
            ["views"] = views,
            ["placed"] = placeInto is null ? null : placed,
        };
        return run.Failed ? 1 : 0;
    }

    // ── deny ──────────────────────────────────────────────────────────────────

    /// <summary>Takes a plugin's permission away, recorded where the window's prompt records a decision. The one
    /// permission change a command line may make.</summary>
    private static int Deny(string[] args)
    {
        if (args.Length != 1 || args[0].StartsWith('-'))
            return args.Length == 0 ? Usage("deny names a plugin.")
                 : JsonRun.Fail(CliDiagnostics.PluginUnknownOption("deny", args[^1], "one installed plugin's id"));
        var prefs = PluginPreferences.Read();
        if (Locate(prefs, args[0]) is not { } entry) return NotFound(prefs, args[0]);
        string label = entry.Manifest?.Id ?? args[0];

        if (!prefs.RecordDenial(entry.Directory))
            return JsonRun.Fail(CliDiagnostics.PluginPermissionNotRecorded(label, $"{prefs.Path} could not be written."));

        Console.WriteLine($"{label} will not run on this machine ({entry.Directory}). Only Settings ▸ Plugins in circuitRF allows it again.");
        JsonRun.Plugin = new JsonObject
        {
            ["action"] = "deny", ["id"] = label, ["directory"] = entry.Directory, ["permitted"] = "denied",
        };
        return 0;
    }

    // ── shared ────────────────────────────────────────────────────────────────

    /// <summary>The installed or linked plugin an id names. Only ever the catalogue's: a path is not an id, so nothing
    /// here reaches a plugin that is not already installed.</summary>
    private static PluginEntry? Locate(PluginPreferences prefs, string id)
        => PluginHeadless.Find(PluginCatalog.Discover(prefs.Sources()), id);

    private static int NotFound(PluginPreferences prefs, string id)
    {
        var ids = PluginCatalog.Discover(prefs.Sources()).Select(e => e.Manifest?.Id ?? e.DisplayName).ToList();
        return JsonRun.Fail(CliDiagnostics.PluginNotFound(id, ids.Count == 0
            ? "None is; 'circuitRF plugin list' says where it looks."
            : $"These are: {string.Join(", ", ids)}."));
    }

    private static string? WorkspaceRoot(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full) && Path.GetFileName(full) == DocumentKinds.CwsFileName) return Path.GetDirectoryName(full);
        return Directory.Exists(full) && File.Exists(Path.Combine(full, DocumentKinds.CwsFileName)) ? full : null;
    }

    private static string Permitted(PluginTrustStore trust, PluginEntry e) => trust.Decide(e.Directory) switch
    {
        PluginTrustDecision.Allowed => "allowed",
        PluginTrustDecision.Denied  => "denied",
        _                           => "not asked",
    };

    private static string SourceName(PluginEntry e) => e.Source == PluginSource.Linked ? "linked" : "installed";

    private static string? KindName(PluginManifest? m) => m?.Kind.ToString().ToLowerInvariant();

    private static string How(PluginRunEntry how)
        => how.IsPython ? $"Python, {how.PythonEntry}" + (how.RequiresPython is { } v ? $" ({v})" : "") : how.Exe!;

    private static string Facts(PluginParam p)
    {
        var parts = new List<string>();
        if (p.Unit is { } u) parts.Add(u);
        if (p.Default is not null) parts.Add($"default {PluginParams.DefaultText(p)}");
        if (p.Min is { } lo) parts.Add($"min {Raw(lo)}");
        if (p.Max is { } hi) parts.Add($"max {Raw(hi)}");
        if (p.Options.Count > 0) parts.Add($"one of {string.Join(", ", p.Options)}");
        if (p.Extensions.Count > 0) parts.Add(string.Join(" ", p.Extensions));
        if (!string.IsNullOrWhiteSpace(p.Label)) parts.Add($"\"{p.Label}\"");
        return string.Join("; ", parts);
    }

    private static JsonObject Param(PluginParam p) => new()
    {
        ["name"] = p.Name, ["label"] = p.Label, ["kind"] = p.Kind.ToString().ToLowerInvariant(), ["unit"] = p.Unit,
        ["default"] = p.Default is { } d ? JsonValue.Create(d) : null,
        ["min"] = p.Min is { } lo ? JsonValue.Create(lo) : null,
        ["max"] = p.Max is { } hi ? JsonValue.Create(hi) : null,
        ["options"] = p.Options.Count > 0 ? new JsonArray([.. p.Options.Select(o => (JsonNode)o)]) : null,
        ["extensions"] = p.Extensions.Count > 0 ? new JsonArray([.. p.Extensions.Select(o => (JsonNode)o)]) : null,
    };

    private static string Raw(System.Text.Json.JsonElement e)
        => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : e.GetRawText();
}
