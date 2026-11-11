using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Plugins;

/// <summary>
/// circuitRF's own command-line verbs, run in THIS process (brief-plg-4 §1) — what a host call that is "the same run
/// <c>circuitRF sparam</c> performs" or "as <c>explain --json</c> spells it" is handed to. The verbs live in
/// <c>src/Cli</c>, above this project, so the caller that starts a session supplies them (<c>PluginVerbs</c> there);
/// a session given none answers those calls with a refusal.
/// </summary>
public interface IPluginVerbs
{
    /// <summary>
    /// Runs <paramref name="argv"/> exactly as <c>circuitRF &lt;argv&gt; --json --result summary</c> would, with state
    /// of its own, and returns its exit code and its document. Cancelling <paramref name="ct"/> stops the run at its
    /// next work boundary (exit 130). <paramref name="exportDataTo"/>, when given, also writes the run's data set there
    /// through the exporter <c>-o out.npy</c> writes with.
    /// </summary>
    PluginVerbRun Run(IReadOnlyList<string> argv, string? exportDataTo, CancellationToken ct);
}

/// <summary>A verb's exit code and its <c>--json</c> document (null only when it wrote none).</summary>
public sealed record PluginVerbRun(int ExitCode, JsonObject? Document);

/// <summary>
/// The four host calls (brief-plg-4 §2, <c>plugins.md</c> §7): each answers a <c>host</c> message with the body of its
/// <c>hostReply</c> — the fields, or <c>error</c> with a sentence — and holds no logic of its own. <c>simulate</c>, a
/// <c>reference</c> and an expression in a document are the CLI's verbs; a technology, a substrate and a cell are the
/// objects a parameter of that kind delivers (<see cref="PluginParams.ResolveValue"/>); a line is
/// <see cref="LineCalculator.ModelSweep"/>, the elaborated part. A source scan holds that shut.
/// </summary>
public sealed class PluginHostServices(string stagingDir, string? workspaceDir, IPluginVerbs? verbs)
{
    /// <summary>The refusal for an EM analysis or a <c>.cem</c>: a plugin update must not start a run measured in minutes.</summary>
    public const string EmRefused = "EM runs are placed and run by the user.";
    public const string LoadpullRefused = "Loadpull runs are placed and run by the user.";
    public const string SimulatingText = "circuitRF is simulating…";
    public static readonly IReadOnlyList<string> Calls = ["simulate", "resolve", "evaluate", "lineModel"];

    /// <summary>The 20·log10(e) a neper is in decibels.</summary>
    private const double DbPerNeper = 8.685889638065035;

    /// <summary>
    /// Answers <paramref name="call"/>. <paramref name="budget"/> bounds a <c>simulate</c> (the deadline of the request it
    /// arrived inside); past it the run is stopped and the reply says so. Cancelling <paramref name="ct"/> — the request
    /// was cancelled — stops it too. Never throws for anything a plugin can send.
    /// </summary>
    public JsonObject Answer(JsonObject call, TimeSpan? budget, CancellationToken ct)
    {
        string? name = Text(call, "call");
        try
        {
            return name switch
            {
                "simulate"  => Simulate(call, budget, ct),
                "resolve"   => Resolve(call, ct),
                "evaluate"  => Evaluate(call, ct),
                "lineModel" => LineModel(call),
                null        => Error($"A host message names its call: {string.Join(", ", Calls)}."),
                _           => Error($"'{name}' is not a host call this circuitRF answers; it answers {string.Join(", ", Calls)}."),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Error($"The {name} call failed: {e.Message}");
        }
    }

    // ── simulate ────────────────────────────────────────────────────────────────

    private JsonObject Simulate(JsonObject call, TimeSpan? budget, CancellationToken ct)
    {
        if (Text(call, "cnl") is not { } named) return Error("simulate names its staged netlist as cnl.");
        var (path, outside) = PluginStaging.Resolve(stagingDir, named);
        if (outside is not null) return Error(Capital(outside));
        string ext = Path.GetExtension(path!).ToLowerInvariant();
        if (ext is ".cem" or ".c3d") return Error(EmRefused);
        if (ext != ".cnl") return Error($"simulate runs a staged .cnl; {Path.GetFileName(path)} is not one.");
        if (!File.Exists(path)) return Error($"{named} is not in the staging folder.");
        if (verbs is null) return Error("This circuitRF cannot simulate for a plugin here.");

        TestBench tb;
        try { (_, tb) = CnlTechnologyBinding.ReadFile(path!, out _); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Error($"{Path.GetFileName(path)} could not be read: {e.Message}"); }

        // The analysis: the one named, else the file's only chain. A named inner analysis runs its sweep, as every
        // run verb runs it.
        var tops = AnalysisChain.RunnableTops(tb);
        Analysis top;
        if (Text(call, "analysis") is { } wanted)
        {
            if (tb.Analyses.FirstOrDefault(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is not { } a)
                return Error($"{Path.GetFileName(path)} has no analysis named {wanted}; it has {Names(tb.Analyses)}.");
            top = AnalysisChain.PromoteToRunnableTop(a, tb);
        }
        else if (tops.Count == 1) top = tops[0];
        else return Error(tops.Count == 0
            ? $"{Path.GetFileName(path)} has no analysis to run."
            : $"{Path.GetFileName(path)} has {tops.Count} analyses ({Names(tops)}); name one as analysis.");

        if (tb.Analyses.Any(a => ChainSelector.ChainContains(top, a.Name, tb) && a.Model == AnalysisModel.Em)) return Error(EmRefused);
        var base_ = ChainSelector.BaseOfChain(top, tb);
        string? verb = base_ switch
        {
            SParameterAnalysis       => "sparam",
            DcAnalysis               => "dc",
            HarmonicBalanceAnalysis  => "hb",
            _                        => null,
        };
        if (base_ is LoadpullAnalysis or LoadpullPursuitAnalysis) return Error(LoadpullRefused);
        if (verb is null) return Error($"simulate runs S-parameter, DC and harmonic-balance analyses; {top.Name} is none of them.");

        // sparam and dc take no analysis name: they run the file's first chain of their kind, so that must be this one.
        if (verb is not "hb")
        {
            Func<Analysis, bool> isBase = verb == "sparam" ? a => a is SParameterAnalysis : a => a is DcAnalysis;
            var chosen = ChainSelector.Select(tb, null, isBase, verb, "").Selected;
            if (!ReferenceEquals(chosen, top))
                return Error($"circuitRF {verb} runs a file's first {verb} analysis, which in {Path.GetFileName(path)} is {chosen?.Name}; stage {top.Name} in a file of its own.");
        }

        string stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "." + top.Name);
        string npy = stem + ".npy";
        // A plain S-parameter run writes its Touchstone file beside the netlist, as `circuitRF sparam x.cnl` does; a
        // swept one has no Touchstone form. Everything else is written by -o.
        bool touchstone = verb == "sparam" && top is SParameterAnalysis;
        var argv = new List<string> { verb, path! };
        if (verb == "hb") argv.AddRange(["--analysis", top.Name]);
        if (!touchstone) argv.AddRange(["-o", npy]);
        if (workspaceDir is not null && Directory.Exists(workspaceDir)) argv.AddRange(["--workspace", workspaceDir]);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget is { } b && b > TimeSpan.Zero && b != Timeout.InfiniteTimeSpan) limit.CancelAfter(b);
        var run = verbs.Run(argv, touchstone ? npy : null, limit.Token);

        var messages = Diagnostics(run.Document);
        if (run.ExitCode == 130 || (run.ExitCode != 0 && limit.IsCancellationRequested))
            return Error(ct.IsCancellationRequested
                ? "The simulation was stopped."
                : $"The simulation did not finish within {Describe(budget!.Value)}, so it was stopped. A run this long belongs behind an action.",
                messages);
        if (run.ExitCode != 0)
            return Error(messages.OfType<JsonObject>().Select(m => Text(m, "text")).FirstOrDefault(t => t is not null)
                         ?? $"circuitRF {verb} failed (exit code {run.ExitCode}).", messages);

        var reply = new JsonObject { ["result"] = npy };
        if (touchstone && Outputs(run.Document, "touchstone").FirstOrDefault() is { } snp) reply["touchstone"] = snp;
        reply["cubes"] = Cubes(run.Document);
        reply["messages"] = messages;
        return reply;
    }

    // ── resolve ─────────────────────────────────────────────────────────────────

    private JsonObject Resolve(JsonObject call, CancellationToken ct)
    {
        string? what = Text(call, "what");
        string? name = Text(call, "name");
        switch (what)
        {
            case "technology":
            {
                string? text = name ?? DefaultTechnologyPath();
                if (text is null) return Error("The workspace names no default technology; name one.");
                return Param(PluginParamKind.Technology, text);
            }
            case "substrate":
            {
                string? text = SubstrateText(call["name"]);
                if (text is null) return Error("resolve substrate names a conductor, as \"conductor : technology\" or the substrate a parameter delivered.");
                return Param(PluginParamKind.Substrate, text);
            }
            case "cell":
                return name is null ? Error("resolve cell names the cell.") : Param(PluginParamKind.Cell, name);
            case "reference":
            {
                if (Text(call, "ref") is not { } reference) return Error("resolve reference names the reference as ref.");
                if (verbs is null) return Error("This circuitRF cannot resolve a reference for a plugin here.");
                string? from = Text(call, "from") is { } f ? Document(f) : WorkspaceCws();
                if (from is null) return Error("resolve reference resolves from a document in the workspace or the staging folder, named as from.");
                var run = verbs.Run(["explain", from, "--ref", reference], null, ct);
                if (run.Document?["result"]?["explain"] is not JsonObject explained)
                    return Error(Diagnostics(run.Document).OfType<JsonObject>().Select(m => Text(m, "text")).FirstOrDefault(t => t is not null)
                                 ?? "The reference could not be resolved.");
                return new JsonObject { ["resolved"] = explained.DeepClone(), ["messages"] = Diagnostics(run.Document) };
            }
            default:
                return Error("resolve resolves a technology, a substrate, a cell or a reference, named as what.");
        }

        JsonObject Param(PluginParamKind kind, string text)
        {
            var (node, error) = PluginParams.ResolveValue(Synthetic(kind), text, new PluginParamContext(workspaceDir));
            return error is not null ? Error(Capital(error)) : new JsonObject { ["resolved"] = node };
        }
    }

    // ── evaluate ────────────────────────────────────────────────────────────────

    private JsonObject Evaluate(JsonObject call, CancellationToken ct)
    {
        if (Text(call, "expr") is not { } expr) return Error("evaluate names its expression as expr.");
        string unit = Text(call, "unit") ?? "";
        if (unit.Length > 0 && PluginParams.UnitRefusal(unit) is { } bad) return Error(bad);

        // In a document: the expression engine in that document's resolved scope, as `explain --expr` evaluates it.
        if (Text(call, "path") is { } named)
        {
            if (verbs is null) return Error("This circuitRF cannot evaluate in a document for a plugin here.");
            if (Document(named) is not { } path) return Error($"{named} is not a document in the workspace or the staging folder.");
            var run = verbs.Run(["explain", path, "--expr", expr], null, ct);
            if (run.Document?["result"]?["explain"]?["expression"] is not JsonObject value)
                return Error(Diagnostics(run.Document).OfType<JsonObject>().Select(m => Text(m, "text")).FirstOrDefault(t => t is not null)
                             ?? $"'{expr}' could not be evaluated.");
            return new JsonObject
            {
                ["value"] = (value["real"] ?? value["complex"] ?? value["bool"])?.DeepClone(),
                ["unit"]  = unit,
                ["kind"]  = value["kind"]?.DeepClone(),
                ["text"]  = value["text"]?.DeepClone(),
            };
        }

        // Otherwise as a quantity parameter's text is: a workspace has no variables of its own, so numbers, SI prefixes,
        // units and functions.
        var (result, error) = PluginParams.EvaluateQuantity(expr, unit, null);
        return error is not null ? Error(Capital(error)) : new JsonObject { ["value"] = result, ["unit"] = unit };
    }

    // ── lineModel ───────────────────────────────────────────────────────────────

    private JsonObject LineModel(JsonObject call)
    {
        // "line", not the brief's "type": every message's "type" is its envelope's, so a host message cannot carry another.
        string? component = Text(call, "line") switch
        {
            "microstrip" => "MLIN",
            "stripline"  => "SLIN",
            "cpwg"       => "CPWG",
            _            => null,
        };
        if (Text(call, "line") == "cpw")
            return Error("circuitRF has no coplanar line without a ground plane below it; cpwg is the coplanar line it models.");
        if (component is null) return Error("lineModel names its line as microstrip, stripline or cpwg.");
        if (SubstrateText(call["substrate"]) is not { } text) return Error("lineModel names its substrate, as \"conductor : technology\" or the substrate a parameter delivered.");
        if (Number(call, "width") is not { } width) return Error("lineModel names its width in metres.");
        double? gap = Number(call, "gap");
        var freqs = call["freq"] switch
        {
            JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<double>(out double d) ? d : double.NaN).ToList(),
            JsonValue v when v.TryGetValue<double>(out double d) => [d],
            _ => new List<double>(),
        };
        if (freqs.Count == 0) return Error("lineModel names its frequencies in hertz, as freq.");

        int sep = text.IndexOf(" : ", StringComparison.Ordinal);
        string conductor = text[..sep].Trim(), techText = text[(sep + 3)..].Trim();
        if (TechnologyResolver.LoadForPath(Path.GetFullPath(techText, workspaceDir ?? stagingDir)) is not { } tech)
            return Error($"{techText} is not a technology circuitRF can read.");

        var (points, warnings, refusal) = LineCalculator.ModelSweep(tech, conductor, component, width, gap, freqs);
        if (points is null) return Error(refusal!);
        return new JsonObject
        {
            ["z0"]          = Array(points.Select(p => p.Z0)),
            ["eeff"]        = Array(points.Select(p => p.Eeff)),
            ["alphaDbPerM"] = Array(points.Select(p => p.AlphaNpPerM * DbPerNeper)),
            ["messages"]    = new JsonArray([.. warnings.Select(w => (JsonNode)new JsonObject { ["severity"] = "warning", ["text"] = w })]),
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A substrate as the picker spells it: the text itself, the object a <c>substrate</c> parameter delivered,
    /// or a conductor alone on the workspace's default technology.</summary>
    private string? SubstrateText(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o when Text(o, "signal") is { } signal && Text(o, "technology") is { } tech:
                return $"{signal} : {tech}";
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Trim().Length > 0:
                if (s.Contains(" : ", StringComparison.Ordinal)) return s.Trim();
                return DefaultTechnologyPath() is { } t ? $"{s.Trim()} : {t}" : null;
            default:
                return null;
        }
    }

    private string? WorkspaceCws()
        => workspaceDir is null ? null : CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(workspaceDir);

    /// <summary>The workspace's default technology, as <see cref="TechnologyResolver"/> resolves it for the workspace.</summary>
    private string? DefaultTechnologyPath()
        => WorkspaceCws() is { } cws
            ? TechnologyResolver.ResolveForDocument(null, null, cws, new TechnologyCache()).Resolution.ResolvedPath
            : null;

    /// <summary>A document a plugin may ask about or open: in its staging folder, or in the workspace. Null for anything
    /// else, or for nothing there.</summary>
    public string? Document(string named)
    {
        if (PluginStaging.Resolve(stagingDir, named) is { Path: { } staged } && (File.Exists(staged) || Directory.Exists(staged))) return staged;
        if (workspaceDir is null) return null;
        string full = Path.GetFullPath(named, workspaceDir);
        string root = Path.GetFullPath(workspaceDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (full + Path.DirectorySeparatorChar).StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
               && (File.Exists(full) || Directory.Exists(full)) ? full : null;
    }

    private static PluginParam Synthetic(PluginParamKind kind)
        => new(kind.ToString().ToLowerInvariant(), kind, null, null, [], null, null, null, []);

    /// <summary>A verb's diagnostics as a plugin's messages.</summary>
    private static JsonArray Diagnostics(JsonObject? document)
    {
        var list = new JsonArray();
        if (document?["diagnostics"] is JsonArray d)
            foreach (var n in d.OfType<JsonObject>())
                list.Add(new JsonObject
                {
                    ["severity"] = Text(n, "severity") is "error" or "warning" ? Text(n, "severity") : "info",
                    ["text"]     = Text(n, "message") ?? "",
                });
        return list;
    }

    private static IEnumerable<string> Outputs(JsonObject? document, string kind)
        => (document?["outputs"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Text(o, "kind") == kind).Select(o => Text(o, "path")).OfType<string>()
            .Select(p => Path.GetFullPath(p));

    /// <summary>The run's cube names, group-qualified when they are in a group, as the document's shape lists them.</summary>
    private static JsonArray Cubes(JsonObject? document)
    {
        var names = new JsonArray();
        if (document?["result"]?["shape"]?["groups"] is JsonObject groups)
            foreach (var (group, cubes) in groups)
                if (cubes is JsonObject c)
                    foreach (var (cube, _) in c) names.Add(group.Length == 0 ? cube : $"{group}/{cube}");
        return names;
    }

    private static JsonObject Error(string sentence, JsonArray? messages = null)
    {
        var o = new JsonObject { ["error"] = sentence };
        if (messages is { Count: > 0 }) o["messages"] = messages;
        return o;
    }

    private static string? Text(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static double? Number(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<double>(out double d) && double.IsFinite(d) ? d : null;

    private static JsonArray Array(IEnumerable<double> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    private static string Names(IEnumerable<Analysis> analyses) => string.Join(", ", analyses.Select(a => a.Name));

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Describe(TimeSpan t)
        => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalSeconds:0.#} s";
}
