using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Plugins;

/// <summary>The plugin contract versions this build speaks (<c>plugins.md</c> §12). A plugin written against a
/// newer one is listed dimmed, never started.</summary>
public static class PluginApi
{
    public const int Min = 1;
    public const int Max = 1;
}

/// <summary>What a plugin is shown as (<c>plugins.md</c> §3).</summary>
public enum PluginKind
{
    /// <summary>circuitRF draws a panel from the manifest's <c>ui</c> (brief 2).</summary>
    Panel,
    /// <summary>A parameter dialog, then one run — no panel.</summary>
    Action,
    /// <summary>The plugin opens its own window (brief 4).</summary>
    Window,
}

/// <summary>The kinds a parameter may be (<c>plugins.md</c> §3.1).</summary>
public enum PluginParamKind { Quantity, Int, Real, Bool, String, Choice, File, Substrate, Technology, Cell, Result, Readout }

/// <summary>One declared parameter. <see cref="Default"/>, <see cref="Min"/> and <see cref="Max"/> are kept as
/// the manifest wrote them — text for a quantity ("2.4 GHz"), a number for an int or real — and evaluated by
/// <see cref="PluginParams"/>. <see cref="Chars"/> is the author's size hint for a typed value: about how many
/// characters its box should hold. circuitRF turns it into a width from the current font and clamps it
/// (<see cref="PluginParam.CharsMin"/>–<see cref="PluginParam.CharsMax"/>), so a hint can never be a pixel size
/// that breaks a theme; without one the box takes the row's width.</summary>
public sealed record PluginParam(
    string Name,
    PluginParamKind Kind,
    string? Label,
    string? Unit,
    IReadOnlyList<string> Options,
    JsonElement? Default,
    JsonElement? Min,
    JsonElement? Max,
    IReadOnlyList<string> Extensions,
    int? Chars = null)
{
    public const int CharsMin = 2;
    public const int CharsMax = 80;

    /// <summary>The kinds typed into a box, which are the only ones a <c>chars</c> hint applies to — a picker is as
    /// wide as the row, so its choices are never cut short.</summary>
    public bool IsTyped => Kind is PluginParamKind.Quantity or PluginParamKind.Int or PluginParamKind.Real
                                 or PluginParamKind.String or PluginParamKind.File;

    /// <summary><see cref="Chars"/> within the limits, or null when the box should take the row's width.</summary>
    public int? ClampedChars => Chars is { } c ? Math.Clamp(c, CharsMin, CharsMax) : null;

    /// <summary>What a control is labelled with: the declared label, else the name.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label!;
}

/// <summary>One way to start the program: an executable relative to the plugin folder, or a Python entry.</summary>
public sealed record PluginRunEntry(string? Exe, string? PythonEntry, string? RequiresPython)
{
    public bool IsPython => PythonEntry is not null;
}

/// <summary>How the program starts on this machine, or why it cannot.</summary>
/// <param name="Platform">The RID that was looked for.</param>
/// <param name="Entry">The entry chosen, or null.</param>
/// <param name="Refusal">"no build for &lt;rid&gt;" when there is none.</param>
public sealed record PluginRunResolution(string Platform, PluginRunEntry? Entry, string? Refusal);

/// <summary>The manifest's deadline overrides, each in seconds; null keeps the default (<c>plugins.md</c> §6.2).</summary>
public sealed record PluginTimeouts(double? Update, double? Action, double? Commit);

/// <summary>A refusal or a note about one field of a manifest. Returned, never posted.</summary>
public sealed record PluginDiagnostic(string Field, string Message, bool IsError = true)
{
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>
/// A plugin's <c>plugin.json</c>, read (<c>plugins.md</c> §3, §3.1). Everything a list, a dialog, the command line
/// or MCP needs about a plugin without starting it.
/// </summary>
public sealed record PluginManifest
{
    public required int ApiVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Summary { get; init; }
    public string? Author { get; init; }
    public string? License { get; init; }
    public string? Homepage { get; init; }
    public string? MinCircuitRF { get; init; }
    /// <summary>RID (or <c>python</c>) → entry, as the manifest lists them.</summary>
    public required IReadOnlyDictionary<string, PluginRunEntry> Run { get; init; }
    public required PluginKind Kind { get; init; }
    public string? Menu { get; init; }
    public IReadOnlyList<string> Contexts { get; init; } = [];
    public IReadOnlyList<PluginParam> Params { get; init; } = [];
    public PluginTimeouts Timeouts { get; init; } = new(null, null, null);
    /// <summary>The panel's layout tree as written; <see cref="PluginLayoutReader"/> reads it.</summary>
    public string? UiJson { get; init; }

    /// <summary>The <c>id</c>'s last dotted part — the cell name a placement falls back to.</summary>
    public string IdTail => Id[(Id.LastIndexOf('.') + 1)..];

    /// <summary>Why this plugin cannot run on this build, or null. A newer contract is the reason here; a
    /// missing build for this machine is <see cref="ResolveRun"/>'s.</summary>
    public string? NeedsNewerCircuitRF => ApiVersion > PluginApi.Max ? "needs a newer circuitRF" : null;

    /// <summary>
    /// This machine's entry: the RID's own, else <c>python</c>, else the refusal "no build for &lt;rid&gt;". A compiled
    /// build for the platform wins over Python because it is what the author shipped for it.
    /// </summary>
    public PluginRunResolution ResolveRun(string? rid = null)
    {
        rid ??= PluginPlatform.CurrentRid;
        if (Run.TryGetValue(rid, out var native)) return new(rid, native, null);
        if (Run.TryGetValue(PluginManifestReader.PythonKey, out var python)) return new(rid, python, null);
        return new(rid, null, $"no build for {rid}");
    }
}

/// <summary>This machine's RID in the manifest's spelling.</summary>
public static class PluginPlatform
{
    /// <summary>The RIDs a manifest's <c>run</c> may name (<c>plugins.md</c> §3).</summary>
    public static readonly IReadOnlyList<string> Rids =
        ["win-x64", "win-arm64", "win-x86", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"];

    public static string CurrentRid
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86   => "x86",
                _                  => "x64",
            };
            return $"{os}-{arch}";
        }
    }
}

/// <summary>
/// Reads <c>plugin.json</c>. <b>Never throws and never posts</b> (<c>src/Design/CLAUDE.md</c>): every refusal is a
/// <see cref="PluginDiagnostic"/> naming its field, and the manifest is null when any of them is an error.
/// Unknown optional fields are ignored, so a manifest written for a later minor version still reads.
/// </summary>
public static partial class PluginManifestReader
{
    public const string FileName   = "plugin.json";
    public const string PythonKey  = "python";

    [GeneratedRegex("^[a-z0-9.-]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    public static (PluginManifest? Manifest, IReadOnlyList<PluginDiagnostic> Diagnostics) ReadFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, [new PluginDiagnostic(FileName, $"could not be read ({e.Message}).")]);
        }
        return Read(text);
    }

    public static (PluginManifest? Manifest, IReadOnlyList<PluginDiagnostic> Diagnostics) Read(string json)
    {
        var d = new List<PluginDiagnostic>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { return (null, [new PluginDiagnostic(FileName, $"is not valid JSON ({e.Message}).")]); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, [new PluginDiagnostic(FileName, "is not a JSON object.")]);

            int api = 0;
            if (!root.TryGetProperty("apiVersion", out var apiEl) || apiEl.ValueKind != JsonValueKind.Number || !apiEl.TryGetInt32(out api) || api < 1)
                d.Add(new("apiVersion", "is required, a whole number from 1."));

            string id = Str(root, "id") ?? "";
            if (id.Length == 0) d.Add(new("id", "is required."));
            else if (!IdPattern().IsMatch(id)) d.Add(new("id", $"'{id}' may hold only lower-case letters, digits, '.' and '-'."));

            string name = Str(root, "name") ?? "";
            if (name.Trim().Length == 0) d.Add(new("name", "is required."));
            string version = Str(root, "version") ?? "";
            if (version.Trim().Length == 0) d.Add(new("version", "is required."));

            PluginKind kind = PluginKind.Action;
            switch (Str(root, "kind"))
            {
                case "panel":  kind = PluginKind.Panel;  break;
                case "action": kind = PluginKind.Action; break;
                case "window": kind = PluginKind.Window; break;
                case null:     d.Add(new("kind", "is required: panel, action or window.")); break;
                case var other: d.Add(new("kind", $"'{other}' is not one of panel, action or window.")); break;
            }

            var run = ReadRun(root, d);
            var parameters = ReadParams(root, d);

            var contexts = new List<string>();
            if (root.TryGetProperty("contexts", out var ctx) && ctx.ValueKind == JsonValueKind.Array)
                foreach (var c in ctx.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cs) contexts.Add(cs);

            var timeouts = new PluginTimeouts(null, null, null);
            if (root.TryGetProperty("timeouts", out var t) && t.ValueKind == JsonValueKind.Object)
                timeouts = new PluginTimeouts(Seconds(t, "update", d), Seconds(t, "action", d), Seconds(t, "commit", d));

            string? ui = root.TryGetProperty("ui", out var uiEl) && uiEl.ValueKind == JsonValueKind.Object ? uiEl.GetRawText() : null;

            if (d.Any(x => x.IsError)) return (null, d);

            var manifest = new PluginManifest
            {
                ApiVersion   = api,
                Id           = id,
                Name         = name.Trim(),
                Version      = version.Trim(),
                Summary      = Str(root, "summary"),
                Author       = Str(root, "author"),
                License      = Str(root, "license"),
                Homepage     = Str(root, "homepage"),
                MinCircuitRF = Str(root, "minCircuitRF"),
                Run          = run,
                Kind         = kind,
                Menu         = Str(root, "menu"),
                Contexts     = contexts,
                Params       = parameters,
                Timeouts     = timeouts,
                UiJson       = ui,
            };

            // A panel's layout is part of the manifest: a tree that names an undeclared parameter is refused here, so
            // the list, the window and the command line all say so the same way (brief-plg-2 §1).
            if (ui is not null && kind == PluginKind.Panel)
            {
                d.AddRange(PluginLayoutReader.Read(ui, manifest).Diagnostics);
                if (d.Any(x => x.IsError)) return (null, d);
            }
            return (manifest, d);
        }
    }

    private static Dictionary<string, PluginRunEntry> ReadRun(JsonElement root, List<PluginDiagnostic> d)
    {
        var run = new Dictionary<string, PluginRunEntry>(StringComparer.Ordinal);
        if (!root.TryGetProperty("run", out var runEl) || runEl.ValueKind != JsonValueKind.Object)
        {
            d.Add(new("run", "is required: an entry for at least one platform, or python."));
            return run;
        }

        foreach (var p in runEl.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) { d.Add(new($"run.{p.Name}", "must be an object.")); continue; }
            if (p.Name == PythonKey)
            {
                if (Str(p.Value, "entry") is not { Length: > 0 } entry) { d.Add(new("run.python.entry", "is required.")); continue; }
                if (!Inside(entry)) { d.Add(new("run.python.entry", $"'{entry}' must be a path inside the plugin's folder.")); continue; }
                run[PythonKey] = new PluginRunEntry(null, entry, Str(p.Value, "requiresPython"));
            }
            else if (PluginPlatform.Rids.Contains(p.Name))
            {
                if (Str(p.Value, "exe") is not { Length: > 0 } exe) { d.Add(new($"run.{p.Name}.exe", "is required.")); continue; }
                if (!Inside(exe)) { d.Add(new($"run.{p.Name}.exe", $"'{exe}' must be a path inside the plugin's folder.")); continue; }
                run[p.Name] = new PluginRunEntry(exe, null, null);
            }
            // Any other key is a platform this build does not know: ignored, as every unknown optional field is.
        }

        if (run.Count == 0)
            d.Add(new("run", $"has no entry for any platform circuitRF knows ({string.Join(", ", PluginPlatform.Rids)}) and no python entry."));
        return run;

        static bool Inside(string relative)
            => !Path.IsPathRooted(relative)
               && !relative.Replace('\\', '/').Split('/').Any(part => part == "..");
    }

    private static List<PluginParam> ReadParams(JsonElement root, List<PluginDiagnostic> d)
    {
        var list = new List<PluginParam>();
        if (!root.TryGetProperty("params", out var ps)) return list;
        if (ps.ValueKind != JsonValueKind.Array) { d.Add(new("params", "must be a list.")); return list; }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var p in ps.EnumerateArray())
        {
            string where = $"params[{index++}]";
            if (p.ValueKind != JsonValueKind.Object) { d.Add(new(where, "must be an object.")); continue; }

            string pname = Str(p, "name") ?? "";
            if (pname.Length == 0) { d.Add(new($"{where}.name", "is required.")); continue; }
            where = $"params.{pname}";
            if (!NamePattern().IsMatch(pname)) d.Add(new($"{where}.name", $"'{pname}' is not an identifier."));
            if (!seen.Add(pname)) { d.Add(new($"{where}.name", $"'{pname}' is declared twice.")); continue; }

            string? kindText = Str(p, "kind");
            if (!TryKind(kindText, out var kind)) { d.Add(new($"{where}.kind", kindText is null ? "is required." : $"'{kindText}' is not a parameter kind.")); continue; }

            var options = new List<string>();
            if (p.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                foreach (var o in opts.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.String) options.Add(o.GetString()!);

            var extensions = new List<string>();
            if (p.TryGetProperty("extensions", out var exts) && exts.ValueKind == JsonValueKind.Array)
                foreach (var e in exts.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) extensions.Add(e.GetString()!);

            JsonElement? def = p.TryGetProperty("default", out var de) ? de.Clone() : null;
            JsonElement? min = p.TryGetProperty("min", out var mi) ? mi.Clone() : null;
            JsonElement? max = p.TryGetProperty("max", out var ma) ? ma.Clone() : null;
            string? unit = Str(p, "unit");

            if (kind == PluginParamKind.Choice)
            {
                if (options.Count == 0) d.Add(new($"{where}.options", "a choice needs at least one option."));
                if (def is { ValueKind: JsonValueKind.String } dv && !options.Contains(dv.GetString()!))
                    d.Add(new($"{where}.default", $"'{dv.GetString()}' is not one of its options ({string.Join(", ", options)})."));
            }

            if (kind == PluginParamKind.Quantity)
            {
                if (unit is null) d.Add(new($"{where}.unit", "a quantity needs a unit."));
                else if (PluginParams.UnitRefusal(unit) is { } why) d.Add(new($"{where}.unit", why));
                else
                {
                    foreach (var (field, value) in new[] { ("default", def), ("min", min), ("max", max) })
                        if (value is { } v && PluginParams.EvaluateQuantity(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText(), unit, null).Error is { } bad)
                            d.Add(new($"{where}.{field}", bad));
                }
            }

            if (kind is PluginParamKind.Int or PluginParamKind.Real)
                foreach (var (field, value) in new[] { ("default", def), ("min", min), ("max", max) })
                    if (value is { } v && (v.ValueKind != JsonValueKind.Number || (kind == PluginParamKind.Int && !v.TryGetInt64(out _))))
                        d.Add(new($"{where}.{field}", kind == PluginParamKind.Int ? "must be a whole number." : "must be a number."));

            int? chars = null;
            if (p.TryGetProperty("chars", out var ch))
            {
                if (ch.ValueKind == JsonValueKind.Number && ch.TryGetInt32(out int n) && n >= 1) chars = n;
                else d.Add(new($"{where}.chars", "must be a whole number of characters, from 1."));
            }

            list.Add(new PluginParam(pname, kind, Str(p, "label"), unit, options, def, min, max, extensions, chars));
        }
        return list;
    }

    private static bool TryKind(string? text, out PluginParamKind kind)
    {
        kind = default;
        if (text is null) return false;
        foreach (PluginParamKind k in Enum.GetValues<PluginParamKind>())
            if (string.Equals(k.ToString(), text, StringComparison.OrdinalIgnoreCase) && text == text.ToLowerInvariant())
            { kind = k; return true; }
        return false;
    }

    private static double? Seconds(JsonElement t, string key, List<PluginDiagnostic> d)
    {
        if (!t.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0) return v.GetDouble();
        d.Add(new($"timeouts.{key}", "must be a positive number of seconds."));
        return null;
    }

    private static string? Str(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
