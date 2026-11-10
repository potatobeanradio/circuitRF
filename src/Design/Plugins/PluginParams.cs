using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Plugins;

/// <summary>Where a parameter's value is resolved: the workspace a dialog or a <c>plugin run</c> is in, and the
/// variables an expression may name. Everything is an argument — nothing here reads a preference.</summary>
/// <param name="WorkspaceDir">The workspace folder, or null outside one (then a substrate, technology or cell
/// has nothing to resolve against).</param>
/// <param name="Scope">The scope a quantity's expression evaluates in; null is a scope of no variables, in
/// which numbers, SI prefixes and arithmetic still work.</param>
public sealed record PluginParamContext(string? WorkspaceDir, Scope? Scope = null);

/// <summary>
/// A parameter as the user gives it and as the plugin receives it (<c>plugins.md</c> §3.1). One function for the
/// dialog, <c>circuitRF plugin run --set</c> and MCP, so all three send the same message for the same text.
/// </summary>
public static class PluginParams
{
    /// <summary>
    /// Null when <paramref name="unit"/> is one a quantity may be declared in, otherwise why not. A plugin
    /// receives base SI (§3.1), so the unit is a base one circuitRF knows — <c>Hz</c>, not <c>GHz</c>; and the
    /// metre is <c>metre</c>, because <c>m</c> is milli everywhere in circuitRF.
    /// </summary>
    public static string? UnitRefusal(string unit)
    {
        if (unit == "m") return "'m' is milli in circuitRF; a length in metres is declared as 'metre'.";
        if (!Units.IsRecognizedUnit(unit)) return $"'{unit}' is not a unit circuitRF knows.";
        if (Units.Scale(unit) is { } s && s != 1.0)
        {
            string b = Units.BaseUnit(unit);
            return b != unit
                ? $"'{unit}' is scaled; declare the base unit '{b}' (a plugin receives base SI, and a user may still type {unit})."
                : $"'{unit}' is a prefix, not a unit.";
        }
        return null;
    }

    /// <summary>
    /// <paramref name="text"/> as a value in <paramref name="unit"/>'s base SI: a number with an SI prefix or a
    /// unit ("2.4 GHz", "2.4GHz", "2.4e9"), or an expression ("2*fc") evaluated by circuitRF's one expression
    /// engine in <paramref name="scope"/>. A unit typed with it must be of the declared kind — "2 nH" for a
    /// frequency is refused, never reinterpreted.
    /// </summary>
    public static (double? Value, string? Error) EvaluateQuantity(string text, string unit, Scope? scope)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return (null, "a value is required.");

        var (expression, typed) = Units.SplitTrailingUnit(t, includeIdentityUnits: true);
        // "2 * f" ends in the femto prefix: split only when the unsplit text does not read on its own.
        if (typed is not null && Parses(t)) (expression, typed) = (t, null);
        if (typed is not null && Units.BaseUnit(typed) != unit && typed != unit)
            return (null, $"'{typed}' is not a {unit} unit.");

        try
        {
            var v = new Evaluator().Eval(expression, scope ?? new Scope("plugin"), typed);
            if (v.Kind != ValueKind.Real) return (null, $"'{t}' is not a real number.");
            double r = v.AsReal();
            return double.IsFinite(r) ? (r, null) : (null, $"'{t}' is not finite.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, $"'{t}' could not be read ({e.Message}).");
        }

        static bool Parses(string s)
        {
            try { Parser.Parse(s); return true; } catch { return false; }
        }
    }

    /// <summary>The text a control starts with for <paramref name="p"/>: its default as written, or empty.</summary>
    public static string DefaultText(PluginParam p) => p.Default switch
    {
        null => "",
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.True }  => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { } other => other.GetRawText(),
    };

    /// <summary>
    /// The <c>params</c> object a message carries, from the text the user gave for each parameter (a missing
    /// entry takes the default). Every refusal names its parameter; the object is null when there is any. A
    /// <c>readout</c> is filled by the plugin and never sent.
    /// </summary>
    public static (JsonObject? Values, IReadOnlyList<string> Errors) Resolve(
        IReadOnlyList<PluginParam> declared, IReadOnlyDictionary<string, string> given, PluginParamContext context)
    {
        var values = new JsonObject();
        var errors = new List<string>();

        foreach (var p in declared)
        {
            if (p.Kind == PluginParamKind.Readout) continue;
            string text = given.TryGetValue(p.Name, out var g) ? g : DefaultText(p);
            var (node, error) = ResolveOne(p, text, context);
            if (error is not null) errors.Add($"{p.DisplayLabel}: {error}");
            else values[p.Name] = node;
        }

        foreach (string name in given.Keys)
            if (!declared.Any(p => p.Name == name))
                errors.Add($"{name}: the plugin declares no such parameter.");

        return (errors.Count == 0 ? values : null, errors);
    }

    private static (JsonNode? Node, string? Error) ResolveOne(PluginParam p, string text, PluginParamContext ctx)
    {
        string t = text.Trim();
        switch (p.Kind)
        {
            case PluginParamKind.Quantity:
            {
                var (v, error) = EvaluateQuantity(t, p.Unit!, ctx.Scope);
                if (error is not null) return (null, error);
                if (Bound(p.Min, p.Unit!) is { } lo && v < lo) return (null, $"{t} is below the minimum {Text(p.Min)}.");
                if (Bound(p.Max, p.Unit!) is { } hi && v > hi) return (null, $"{t} is above the maximum {Text(p.Max)}.");
                return (new JsonObject { ["value"] = v, ["unit"] = p.Unit, ["text"] = t }, null);
            }
            case PluginParamKind.Int:
            {
                if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return (null, $"'{t}' is not a whole number.");
                if (p.Min is { ValueKind: JsonValueKind.Number } mi && n < mi.GetDouble()) return (null, $"{n} is below the minimum {mi.GetRawText()}.");
                if (p.Max is { ValueKind: JsonValueKind.Number } ma && n > ma.GetDouble()) return (null, $"{n} is above the maximum {ma.GetRawText()}.");
                return (JsonValue.Create(n), null);
            }
            case PluginParamKind.Real:
            {
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) || !double.IsFinite(r)) return (null, $"'{t}' is not a number.");
                if (p.Min is { ValueKind: JsonValueKind.Number } mi && r < mi.GetDouble()) return (null, $"{t} is below the minimum {mi.GetRawText()}.");
                if (p.Max is { ValueKind: JsonValueKind.Number } ma && r > ma.GetDouble()) return (null, $"{t} is above the maximum {ma.GetRawText()}.");
                return (JsonValue.Create(r), null);
            }
            case PluginParamKind.Bool:
                return t.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on"   => (JsonValue.Create(true), null),
                    "false" or "0" or "no" or "off" or "" => (JsonValue.Create(false), null),
                    _ => (null, $"'{t}' is not true or false."),
                };
            case PluginParamKind.String:
                return (JsonValue.Create(text), null);
            case PluginParamKind.Choice:
                return p.Options.Contains(t) ? (JsonValue.Create(t), null) : (null, $"'{t}' is not one of {string.Join(", ", p.Options)}.");
            case PluginParamKind.File:
            {
                if (t.Length == 0) return (null, "a file is required.");
                string full = Path.GetFullPath(t, ctx.WorkspaceDir ?? Directory.GetCurrentDirectory());
                if (!File.Exists(full)) return (null, $"{full} does not exist.");
                if (p.Extensions.Count > 0 && !p.Extensions.Any(e => full.EndsWith(e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase)))
                    return (null, $"{Path.GetFileName(full)} is not a {string.Join(" or ", p.Extensions)} file.");
                return (JsonValue.Create(full), null);
            }
            case PluginParamKind.Technology: return Technology(t, ctx);
            case PluginParamKind.Substrate:  return Substrate(t, ctx);
            case PluginParamKind.Cell:       return Cell(t, ctx);
            case PluginParamKind.Result:     return Result(t, ctx);
            default: return (null, "this kind is not sent.");
        }

        double? Bound(JsonElement? e, string unit)
            => e is { } b ? EvaluateQuantity(b.ValueKind == JsonValueKind.String ? b.GetString()! : b.GetRawText(), unit, null).Value : null;
        static string Text(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString()! : e?.GetRawText() ?? "";
    }

    // ── The workspace kinds ─────────────────────────────────────────────────────

    /// <summary>The technologies a picker offers: every <c>.ctech</c> in the workspace, as a path relative to it.
    /// Bounded and dot-folder-free, like every other workspace walk.</summary>
    public static IReadOnlyList<string> TechnologyChoices(string? workspaceDir)
        => workspaceDir is null ? [] : Enumerate(workspaceDir, "*.ctech", 4)
            .Select(p => Path.GetRelativePath(workspaceDir, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The substrates a picker offers: each technology's conductors, as "conductor : technology" — the
    /// conductor first, because it is the part that differs between rows and a narrow picker truncates the end.</summary>
    public static IReadOnlyList<string> SubstrateChoices(string? workspaceDir)
    {
        var list = new List<string>();
        foreach (string tech in TechnologyChoices(workspaceDir))
            if (TechnologyResolver.LoadForPath(Path.Combine(workspaceDir!, tech)) is { } t)
                foreach (var layer in t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference))
                    list.Add($"{layer.Name} : {tech}");
        return list;
    }

    /// <summary>The cells a picker offers: every folder holding a <c>.ccell</c>, relative to the workspace.</summary>
    public static IReadOnlyList<string> CellChoices(string? workspaceDir)
        => workspaceDir is null ? [] : Enumerate(workspaceDir, "*" + CircuitRF.Design.Cells.CellFolder.CcellFileName, 4)
            .Select(p => Path.GetRelativePath(workspaceDir, Path.GetDirectoryName(p)!).Replace('\\', '/'))
            .Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The results a picker offers: every Touchstone file and run <c>.npy</c> in the workspace.</summary>
    public static IReadOnlyList<string> ResultChoices(string? workspaceDir)
        => workspaceDir is null ? [] : Enumerate(workspaceDir, "*", 5)
            .Where(p => IsTouchstone(p) || p.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(workspaceDir, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    private static (JsonNode?, string?) Technology(string t, PluginParamContext ctx)
    {
        if (ctx.WorkspaceDir is null) return (null, "there is no workspace to take a technology from.");
        string full = Path.GetFullPath(t, ctx.WorkspaceDir);
        if (TechnologyResolver.LoadForPath(full) is not { } tech) return (null, $"{t} is not a technology circuitRF can read.");
        return (new JsonObject
        {
            ["id"]           = tech.Name,
            ["path"]         = full,
            ["dbuPerMicron"] = LayoutUnits.DefaultDbuPerMicron,
            ["layers"]       = new JsonArray([.. tech.Layers.Select(l => (JsonNode)new JsonObject
                                   { ["name"] = l.Name, ["layer"] = l.Key.Layer, ["datatype"] = l.Key.Datatype, ["purpose"] = l.Purpose })]),
            ["stackup"]      = Stackup(tech),
        }, null);
    }

    private static (JsonNode?, string?) Substrate(string t, PluginParamContext ctx)
    {
        if (ctx.WorkspaceDir is null) return (null, "there is no workspace to take a substrate from.");
        int sep = t.IndexOf(" : ", StringComparison.Ordinal);
        if (sep < 0) return (null, $"'{t}' does not name a conductor and a technology.");
        string conductor = t[..sep].Trim();
        string techText = t[(sep + 3)..].Trim();
        string techPath = Path.GetFullPath(techText, ctx.WorkspaceDir);
        if (TechnologyResolver.LoadForPath(techPath) is not { } tech) return (null, $"{techText} is not a technology circuitRF can read.");

        var (sub, failure, _) = SubstrateResolver.ResolveElectrical(tech, new PCellLayerSelection(conductor, null));
        if (sub is null) return (null, failure?.Reason ?? $"{conductor} does not resolve to a substrate.");
        return (new JsonObject
        {
            ["technology"]   = techPath,
            ["signal"]       = sub.SignalConductorName,
            ["ground"]       = sub.GroundConductorName,
            ["h"]            = sub.HeightMeters,
            ["er"]           = sub.RelativePermittivity,
            ["tanD"]         = sub.LossTangent,
            ["t"]            = sub.ThicknessMeters,
            ["sigma"]        = sub.ConductivitySPerM,
            ["roughness"]    = sub.RoughnessRmsMeters,
            ["stripline"]    = sub.IsStripline,
            ["stackup"]      = Stackup(tech),
        }, null);
    }

    /// <summary>Every stackup entry in base SI: thickness in metres, and only the fields its kind gives meaning to.</summary>
    private static JsonArray Stackup(Technology tech)
    {
        double metresPerDbu = 1e-6 / LayoutUnits.DefaultDbuPerMicron;
        var a = new JsonArray();
        foreach (var l in tech.Stackup.Layers)
        {
            var o = new JsonObject
            {
                ["kind"] = l.Kind.ToString().ToLowerInvariant(), ["name"] = l.Name, ["thickness"] = l.ThicknessDbu * metresPerDbu,
            };
            if (l.Kind == StackupKind.Dielectric) { o["er"] = l.Epsr; o["tanD"] = l.TanD; o["mur"] = l.Mur; }
            if (l.Kind == StackupKind.Conductor)
            {
                o["sigma"] = l.SigmaSm; o["isGround"] = l.IsGroundReference;
                if (l.RoughnessRmsM is { } r) o["roughness"] = r;
            }
            a.Add(o);
        }
        return a;
    }

    private static (JsonNode?, string?) Cell(string t, PluginParamContext ctx)
    {
        if (ctx.WorkspaceDir is null) return (null, "there is no workspace to take a cell from.");
        string dir = Path.GetFullPath(t, ctx.WorkspaceDir);
        if (!File.Exists(Path.Combine(dir, CircuitRF.Design.Cells.CellFolder.CcellFileName))) return (null, $"{t} is not a cell.");
        var views = new JsonObject();
        foreach (var type in Enum.GetValues<CircuitRF.Design.Cells.ViewType>())
        {
            string sub = CircuitRF.Design.Cells.CellFolder.SubFolderPath(dir, type);
            if (!Directory.Exists(sub)) continue;
            var files = Directory.EnumerateFiles(sub, "*" + CircuitRF.Design.Cells.CellFolder.ViewExtension(type)).Order(StringComparer.Ordinal).ToList();
            if (files.Count > 0) views[CircuitRF.Design.Cells.CellFolder.SubFolderName(type)] = new JsonArray([.. files.Select(f => (JsonNode)JsonValue.Create(f)!)]);
        }
        return (new JsonObject { ["path"] = dir, ["name"] = Path.GetFileName(dir), ["views"] = views }, null);
    }

    private static (JsonNode?, string?) Result(string t, PluginParamContext ctx)
    {
        string full = Path.GetFullPath(t, ctx.WorkspaceDir ?? Directory.GetCurrentDirectory());
        if (!File.Exists(full)) return (null, $"{t} does not exist.");
        var o = new JsonObject { ["path"] = full };
        if (IsTouchstone(full))
        {
            try
            {
                var snp = RfCore.TouchstoneIO.ReadFile(full, readComments: false);
                o["ports"] = snp.Matrices.Length > 0 ? snp.Matrices[0].RowCount : 0;
                if (snp.Frequencies.Length > 0) { o["fmin"] = snp.Frequencies[0]; o["fmax"] = snp.Frequencies[^1]; }
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
            {
                return (null, $"{Path.GetFileName(full)} could not be read ({e.Message}).");
            }
        }
        return (o, null);
    }

    private static bool IsTouchstone(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Length >= 4 && ext[1] is 's' or 'S' && ext[^1] is 'p' or 'P' && int.TryParse(ext[2..^1], out _);
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, int depth)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (dir, d) = pending.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir, pattern); dirs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string f in files) yield return f;
            if (d >= depth) continue;
            foreach (string sub in dirs)
                if (!Path.GetFileName(sub).StartsWith('.') && new DirectoryInfo(sub).LinkTarget is null) pending.Push((sub, d + 1));
        }
    }
}
