using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.Plugins;

// brief-plg-2 §1 — the panel's layout tree (plugins.md §4.1–4.2), read from a manifest's `ui`. Below the firewall
// because brief 5 lists a plugin's views from it, and so the CLI can refuse a panel the window would refuse.

/// <summary>One node of a panel's layout tree. <see cref="Path"/> is the node's place in the tree ("0.1.2"), which
/// is what the window remembers a split's positions and a tab's selection under.</summary>
public abstract record PluginLayoutNode(string Path);

/// <summary>Children side by side (<c>row</c>) or stacked (<c>column</c>).</summary>
public sealed record PluginStackNode(string Path, bool Horizontal, IReadOnlyList<PluginLayoutNode> Children) : PluginLayoutNode(Path);

/// <summary>A titled box, like the analysis editor's groups.</summary>
public sealed record PluginGroupNode(string Path, string Title, IReadOnlyList<PluginLayoutNode> Children) : PluginLayoutNode(Path);

/// <summary>One tab: its title and what it holds.</summary>
public sealed record PluginTab(string Title, PluginLayoutNode Content);

/// <summary>Tabs. <see cref="FromFirstReply"/> marks the default panel's tabs, which are one per view the plugin's
/// first reply carries rather than any the manifest declared (<c>plugins.md</c> §4).</summary>
public sealed record PluginTabsNode(string Path, IReadOnlyList<PluginTab> Tabs, bool FromFirstReply = false) : PluginLayoutNode(Path);

/// <summary>A resizable split; <see cref="Sizes"/> are proportions, one per child.</summary>
public sealed record PluginSplitNode(string Path, bool Horizontal, IReadOnlyList<double> Sizes, IReadOnlyList<PluginLayoutNode> Children) : PluginLayoutNode(Path);

/// <summary>A parameter's label and control. <see cref="Label"/> and <see cref="Chars"/> override the declaration's
/// for this placement only.</summary>
public sealed record PluginParamLeaf(string Path, PluginParam Param, string? Label, int? Chars) : PluginLayoutNode(Path)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Param.DisplayLabel : Label!;
    /// <summary>The size hint in force here — this leaf's, else the declaration's — within the limits.</summary>
    public int? ClampedChars => (Chars ?? Param.Chars) is { } c ? Math.Clamp(c, PluginParam.CharsMin, PluginParam.CharsMax) : null;
}

/// <summary>How a readout is drawn.</summary>
public enum PluginReadoutStyle { Value, Table, Text }

/// <summary>A value the plugin fills in each reply.</summary>
public sealed record PluginReadoutLeaf(string Path, PluginParam Param, PluginReadoutStyle As, string? Label) : PluginLayoutNode(Path)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Param.DisplayLabel : Label!;
}

/// <summary>A button. <see cref="IsCommit"/> is the reserved placement action.</summary>
public sealed record PluginButtonLeaf(string Path, string Label, string Action) : PluginLayoutNode(Path)
{
    public const string CommitAction = "commit";
    public bool IsCommit => Action == CommitAction;
}

/// <summary>The plugin's messages for the current parameters, and circuitRF's own line about the process.</summary>
public sealed record PluginMessagesLeaf(string Path) : PluginLayoutNode(Path);

/// <summary>A view the plugin fills (brief 3 draws them).</summary>
public sealed record PluginViewLeaf(string Path, string Name, string Kind) : PluginLayoutNode(Path)
{
    /// <summary>The view kinds this build knows (<c>plugins.md</c> §4.2).</summary>
    public static readonly IReadOnlyList<string> Kinds = ["plot", "smith", "layout", "schematic", "image", "table"];
    public bool IsKnownKind => Kinds.Contains(Kind);
}

/// <summary>Fixed text, one or two lines.</summary>
public sealed record PluginNoteLeaf(string Path, string Text) : PluginLayoutNode(Path);

/// <summary>A node this build does not know — written for a newer contract. Drawn as an empty frame saying so, never a
/// refusal of the whole panel.</summary>
public sealed record PluginUnknownNode(string Path, string What) : PluginLayoutNode(Path)
{
    public const string Reason = "needs a newer circuitRF";
}

/// <summary>A panel: its title, its opening size, and its tree.</summary>
/// <param name="Size">The manifest's <c>ui.size</c>, or null for the default.</param>
/// <param name="IsDefault">True when the manifest had no <c>ui</c> and this is the default panel.</param>
public sealed record PluginPanelLayout(string Title, (double Width, double Height)? Size, PluginLayoutNode Root, bool IsDefault)
{
    public const double DefaultWidth = 960;
    public const double DefaultHeight = 640;

    /// <summary>Every node, depth first.</summary>
    public IEnumerable<PluginLayoutNode> Nodes => Walk(Root);

    /// <summary>The views the manifest places, by name — what brief 5 lists.</summary>
    public IReadOnlyList<PluginViewLeaf> Views => Nodes.OfType<PluginViewLeaf>().ToList();

    private static IEnumerable<PluginLayoutNode> Walk(PluginLayoutNode n)
    {
        yield return n;
        IEnumerable<PluginLayoutNode> children = n switch
        {
            PluginStackNode s => s.Children,
            PluginGroupNode g => g.Children,
            PluginSplitNode p => p.Children,
            PluginTabsNode t  => t.Tabs.Select(x => x.Content),
            _ => [],
        };
        foreach (var c in children)
            foreach (var d in Walk(c)) yield return d;
    }
}

/// <summary>
/// Reads <c>ui</c> into a <see cref="PluginPanelLayout"/> (<c>plugins.md</c> §4). <b>Never throws</b>: every refusal is
/// a <see cref="PluginDiagnostic"/> naming its place in the tree, and the layout is null when any is an error. A node
/// of a kind this build does not know is not an error — it is drawn as an empty frame that says it needs a newer
/// circuitRF, with a note.
/// </summary>
public static class PluginLayoutReader
{
    private static readonly string[] Containers = ["row", "column", "group", "tabs", "split"];
    private static readonly string[] Leaves = ["param", "readout", "button", "messages", "view", "note"];

    /// <summary>The manifest's panel: its <c>ui</c> read, or the default panel when it has none.</summary>
    public static (PluginPanelLayout? Layout, IReadOnlyList<PluginDiagnostic> Diagnostics) Read(PluginManifest manifest)
        => manifest.UiJson is null ? (Default(manifest), []) : Read(manifest.UiJson, manifest);

    /// <summary>
    /// The default panel for a manifest with no <c>ui</c>: the parameters in one column (a readout drawn as its value),
    /// the messages, a Place in workspace button, and beside them one tab per view the first reply carries.
    /// </summary>
    public static PluginPanelLayout Default(PluginManifest manifest)
    {
        var column = new List<PluginLayoutNode>();
        int i = 0;
        foreach (var p in manifest.Params)
        {
            string path = $"0.0.{i++}";
            column.Add(p.Kind == PluginParamKind.Readout
                ? new PluginReadoutLeaf(path, p, PluginReadoutStyle.Value, null)
                : new PluginParamLeaf(path, p, null, null));
        }
        column.Add(new PluginMessagesLeaf($"0.0.{i++}"));
        column.Add(new PluginButtonLeaf($"0.0.{i}", "Place in workspace", PluginButtonLeaf.CommitAction));
        var root = new PluginSplitNode("0", true, [1, 2],
        [
            new PluginStackNode("0.0", false, column),
            new PluginTabsNode("0.1", [], FromFirstReply: true),
        ]);
        return new PluginPanelLayout(manifest.Name, null, root, IsDefault: true);
    }

    /// <summary>The default panel's tabs, once the first reply names its views: one per view, in the reply's order,
    /// each kind taken from the view's own <c>kind</c> or else from its shape.</summary>
    public static IReadOnlyList<PluginTab> TabsFor(JsonObject views, string tabsPath)
    {
        var tabs = new List<PluginTab>();
        int i = 0;
        foreach (var (name, body) in views)
        {
            if (body is null) continue;
            tabs.Add(new PluginTab(name, new PluginViewLeaf($"{tabsPath}.{i++}", name, KindOf(body))));
        }
        return tabs;
    }

    /// <summary>A reply's view kind when no manifest declared it: its <c>kind</c>, else what its fields say it is.</summary>
    public static string KindOf(JsonNode body)
    {
        if (body is not JsonObject o) return "image";
        if (o["kind"] is JsonValue k && k.TryGetValue<string>(out var kind)) return kind;
        if (o.ContainsKey("shapes")) return "layout";
        if (o.ContainsKey("cnl")) return "schematic";
        if (o.ContainsKey("columns")) return "table";
        if (o.ContainsKey("plot") || o.ContainsKey("data")) return "plot";
        return "image";
    }

    public static (PluginPanelLayout? Layout, IReadOnlyList<PluginDiagnostic> Diagnostics) Read(string uiJson, PluginManifest manifest)
    {
        var d = new List<PluginDiagnostic>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(uiJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { return (null, [new PluginDiagnostic("ui", $"is not valid JSON ({e.Message}).")]); }

        using (doc)
        {
            var ui = doc.RootElement;
            if (ui.ValueKind != JsonValueKind.Object) return (null, [new PluginDiagnostic("ui", "must be an object.")]);

            string title = ui.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } ts
                ? ts : manifest.Name;

            (double, double)? size = null;
            if (ui.TryGetProperty("size", out var s))
            {
                if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() == 2
                    && s[0].ValueKind == JsonValueKind.Number && s[1].ValueKind == JsonValueKind.Number
                    && s[0].GetDouble() > 0 && s[1].GetDouble() > 0)
                    size = (s[0].GetDouble(), s[1].GetDouble());
                else d.Add(new("ui.size", "must be [width, height], two positive numbers."));
            }

            if (!ui.TryGetProperty("layout", out var layoutEl))
                return (null, [.. d, new PluginDiagnostic("ui.layout", "is required when ui is given; leave ui out for the default panel.")]);

            var reader = new Reader(manifest, d);
            var root = reader.Node(layoutEl, "0", "ui.layout");

            var views = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in reader.ViewsSeen)
                if (!views.TryAdd(v.Name, v.Where))
                    d.Add(new(v.Where, $"view '{v.Name}' is placed twice (also at {views[v.Name]}); each view has one name."));

            if (d.Any(x => x.IsError) || root is null) return (null, d);
            return (new PluginPanelLayout(title, size, root, IsDefault: false), d);
        }
    }

    private sealed class Reader(PluginManifest manifest, List<PluginDiagnostic> d)
    {
        private readonly Dictionary<string, string> _placed = new(StringComparer.Ordinal);
        public List<(string Name, string Where)> ViewsSeen { get; } = [];

        public PluginLayoutNode? Node(JsonElement e, string path, string where)
        {
            if (e.ValueKind != JsonValueKind.Object) { d.Add(new(where, "must be an object naming one container or leaf.")); return null; }

            string? key = Containers.Concat(Leaves).FirstOrDefault(k => e.TryGetProperty(k, out _));
            if (key is null)
            {
                string first = e.EnumerateObject().Select(p => p.Name).FirstOrDefault() ?? "{}";
                d.Add(new(where, $"'{first}' is not a panel element this circuitRF knows; it is drawn as an empty frame.", IsError: false));
                return new PluginUnknownNode(path, first);
            }
            var v = e.GetProperty(key);

            switch (key)
            {
                case "row" or "column":
                    return new PluginStackNode(path, key == "row", Children(v, path, $"{where}.{key}"));

                case "group":
                {
                    if (v.ValueKind != JsonValueKind.String) d.Add(new($"{where}.group", "is the group's title, a string."));
                    var kids = e.TryGetProperty("children", out var c) ? Children(c, path, $"{where}.children") : Missing($"{where}.children");
                    return new PluginGroupNode(path, v.ValueKind == JsonValueKind.String ? v.GetString()! : "", kids);
                }

                case "tabs":
                {
                    if (v.ValueKind != JsonValueKind.Array) { d.Add(new($"{where}.tabs", "must be a list of tabs.")); return null; }
                    var tabs = new List<PluginTab>();
                    int i = 0;
                    foreach (var tab in v.EnumerateArray())
                    {
                        string tw = $"{where}.tabs[{i}]";
                        string tp = $"{path}.{i++}";
                        if (tab.ValueKind != JsonValueKind.Object) { d.Add(new(tw, "must be an object.")); continue; }
                        string title = tab.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()! : "";
                        if (title.Trim().Length == 0) d.Add(new($"{tw}.title", "is required."));
                        // The tab's content is the same object without its title: a leaf or a container.
                        if (Node(tab, tp, tw) is { } content) tabs.Add(new PluginTab(title, content));
                    }
                    return new PluginTabsNode(path, tabs);
                }

                case "split":
                {
                    bool horizontal = true;
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is "horizontal" or "vertical") horizontal = v.GetString() == "horizontal";
                    else d.Add(new($"{where}.split", "must be horizontal or vertical."));
                    var kids = e.TryGetProperty("children", out var c) ? Children(c, path, $"{where}.children") : Missing($"{where}.children");
                    var sizes = new List<double>();
                    if (e.TryGetProperty("sizes", out var sz))
                    {
                        if (sz.ValueKind != JsonValueKind.Array || sz.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || x.GetDouble() <= 0))
                            d.Add(new($"{where}.sizes", "must be a list of positive proportions."));
                        else
                        {
                            sizes.AddRange(sz.EnumerateArray().Select(x => x.GetDouble()));
                            if (sizes.Count != kids.Count)
                                d.Add(new($"{where}.sizes", $"has {sizes.Count} {(sizes.Count == 1 ? "size" : "sizes")} for {kids.Count} {(kids.Count == 1 ? "child" : "children")}; give one per child."));
                        }
                    }
                    if (sizes.Count != kids.Count) sizes = [.. kids.Select(_ => 1.0)];
                    return new PluginSplitNode(path, horizontal, sizes, kids);
                }

                case "param":
                {
                    if (DeclaredParam(v, $"{where}.param") is not { } p) return null;
                    if (_placed.TryGetValue(p.Name, out var first)) d.Add(new($"{where}.param", $"'{p.Name}' is placed twice (also at {first}); a parameter has one control."));
                    else _placed[p.Name] = where;
                    int? chars = null;
                    if (e.TryGetProperty("chars", out var ch))
                    {
                        if (ch.ValueKind == JsonValueKind.Number && ch.TryGetInt32(out int n) && n >= 1) chars = n;
                        else d.Add(new($"{where}.chars", "must be a whole number of characters, from 1."));
                    }
                    return new PluginParamLeaf(path, p, Str(e, "label"), chars);
                }

                case "readout":
                {
                    if (DeclaredParam(v, $"{where}.readout") is not { } p) return null;
                    if (p.Kind != PluginParamKind.Readout)
                    {
                        d.Add(new($"{where}.readout", $"'{p.Name}' is a {p.Kind.ToString().ToLowerInvariant()} parameter, not a readout; place it with param."));
                        return null;
                    }
                    var style = PluginReadoutStyle.Value;
                    switch (Str(e, "as"))
                    {
                        case null or "value": break;
                        case "table": style = PluginReadoutStyle.Table; break;
                        case "text":  style = PluginReadoutStyle.Text; break;
                        case var other: d.Add(new($"{where}.as", $"'{other}' is not one of value, table or text.")); break;
                    }
                    return new PluginReadoutLeaf(path, p, style, Str(e, "label"));
                }

                case "button":
                {
                    if (v.ValueKind != JsonValueKind.String || v.GetString()!.Trim().Length == 0) { d.Add(new($"{where}.button", "is the button's label, a string.")); return null; }
                    if (Str(e, "action") is not { Length: > 0 } action) { d.Add(new($"{where}.action", "is required: the action the button sends, or commit.")); return null; }
                    return new PluginButtonLeaf(path, v.GetString()!.Trim(), action);
                }

                case "messages":
                    return new PluginMessagesLeaf(path);

                case "view":
                {
                    if (v.ValueKind != JsonValueKind.String || v.GetString()!.Trim().Length == 0) { d.Add(new($"{where}.view", "is the view's name, a string.")); return null; }
                    string name = v.GetString()!.Trim();
                    string kind = Str(e, "kind") ?? "";
                    if (kind.Length == 0) { d.Add(new($"{where}.kind", $"is required: one of {string.Join(", ", PluginViewLeaf.Kinds)}.")); return null; }
                    ViewsSeen.Add((name, where));
                    var leaf = new PluginViewLeaf(path, name, kind);
                    if (!leaf.IsKnownKind)
                        d.Add(new($"{where}.kind", $"'{kind}' is not a view kind this circuitRF knows; it is drawn as an empty frame.", IsError: false));
                    return leaf;
                }

                case "note":
                    if (v.ValueKind != JsonValueKind.String) { d.Add(new($"{where}.note", "is the note's text, a string.")); return null; }
                    return new PluginNoteLeaf(path, v.GetString()!);
            }
            return null;
        }

        private List<PluginLayoutNode> Children(JsonElement list, string path, string where)
        {
            var kids = new List<PluginLayoutNode>();
            if (list.ValueKind != JsonValueKind.Array) { d.Add(new(where, "must be a list.")); return kids; }
            int i = 0;
            foreach (var c in list.EnumerateArray())
            {
                if (Node(c, $"{path}.{i}", $"{where}[{i}]") is { } n) kids.Add(n);
                i++;
            }
            return kids;
        }

        private List<PluginLayoutNode> Missing(string where)
        {
            d.Add(new(where, "is required."));
            return [];
        }

        private PluginParam? DeclaredParam(JsonElement v, string where)
        {
            if (v.ValueKind != JsonValueKind.String) { d.Add(new(where, "names a parameter, a string.")); return null; }
            string name = v.GetString()!;
            var p = manifest.Params.FirstOrDefault(x => x.Name == name);
            if (p is null) d.Add(new(where, $"'{name}' is not a declared parameter."));
            return p;
        }

        private static string? Str(JsonElement o, string key)
            => o.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    }
}
