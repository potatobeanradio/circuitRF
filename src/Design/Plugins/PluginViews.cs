using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Schematic;
using RfCore.Data;

namespace CircuitRF.Design.Plugins;

/// <summary>Where a view's references resolve: the plugin's staging folder for its files, the workspace for a
/// technology id, and the manifest for what a bound marker or <c>"technology": "self"</c> names.</summary>
public sealed record PluginViewContext(PluginManifest Manifest, string? WorkspaceDir);

/// <summary>
/// One view of a plugin's reply, read (brief-plg-3, <c>plugins.md</c> §5). Every kind below is drawn in
/// <c>src/Ui/Plugins/Views</c> by a renderer circuitRF already has; this is the half that does not draw, so
/// <c>circuitRF plugin run</c> (brief 5) reads a view exactly as a panel does.
/// </summary>
public abstract record PluginView(string Name, string Kind)
{
    /// <summary>Why the view cannot be drawn. The panel shows an empty frame saying so and adds it to the messages.
    /// Null when it can be drawn.</summary>
    public string? Problem { get; init; }

    /// <summary>What the messages should also say about a view that IS drawn — a layer its technology lacks, a
    /// drawing note. Each once.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The staging folder of the session that sent it.</summary>
    public string StagingDir { get; init; } = "";
}

/// <summary>A view that could not be read: drawn as an empty frame with <see cref="PluginView.Problem"/>.</summary>
public sealed record PluginUnreadableView(string Name, string Kind) : PluginView(Name, Kind);

/// <summary>A marker a plugin places (<c>plugins.md</c> §5.1). <see cref="X"/> is base SI; a marker with
/// <see cref="Bind"/> sits at that parameter's value instead, and dragging it changes the parameter.</summary>
public sealed record PluginPlotMarker(int Index, string? Trace, double? X, string? Bind, bool Draggable);

/// <summary>A horizontal limit line: <see cref="Y"/> in the plot's y units, shaded on <see cref="Side"/>
/// (<c>above</c> or <c>below</c>, the Data Display's limit area).</summary>
public sealed record PluginPlotLine(double Y, string? Label, string Side, double? XStart, double? XEnd);

/// <summary>
/// A <c>plot</c> or <c>smith</c> view: the data as a <see cref="DataSet"/> — built here from inline axes and cubes, or
/// named as a file in the staging folder that the Data Display's own reader opens — and the plot spec, which is the
/// <c>.cdd</c> model's fields with <c>cube=</c>/<c>y=</c> meaning what they mean in <c>circuitRF plot --trace</c>. The
/// spec stays JSON here: it is composed by the plot verb's own parser (<c>PluginPlots</c> in <c>src/Cli</c>), never by
/// a second one.
/// </summary>
public sealed record PluginPlotView(
    string Name, string Kind, DataSet? Inline, string? File, JsonObject Plot,
    IReadOnlyList<PluginPlotMarker> Markers, IReadOnlyList<PluginPlotLine> Lines, JsonObject Params) : PluginView(Name, Kind)
{
    public bool IsSmith => Kind == "smith";

    /// <summary>A bound marker's x, base SI: its parameter's value in the parameters this reply answered.</summary>
    public double? BoundValue(PluginPlotMarker m)
        => m.Bind is { } p ? PluginViews.NumberOf(Params[p]) : m.X;
}

/// <summary>A <c>table</c> view (<c>plugins.md</c> §5.2), every cell already formatted in its column's unit by the
/// formatter a table readout uses.</summary>
public sealed record PluginTableView(string Name, PluginReadoutValue Table) : PluginView(Name, "table");

/// <summary>
/// A <c>layout</c> view (<c>plugins.md</c> §5.3): the PCell wire's generate reply decoded by
/// <see cref="PCellWireCodec.DecodeGenerateReply"/> into the layout model a generated cell becomes, with its ports
/// drawn as layout port labels, its editor-hint handles, and the technology it is drawn in.
/// </summary>
public sealed record PluginLayoutView(
    string Name, LayoutView Layout, Technology? Technology, IReadOnlyList<PCellHandle> Handles,
    IReadOnlyList<LabelShape> Ports, JsonObject Params) : PluginView(Name, "layout")
{
    /// <summary>The <c>.ctech</c> the view is drawn in, or null on the fallback palette.</summary>
    public string? TechnologyPath { get; init; }

    /// <summary>Metres per DBU in this view.</summary>
    public double MetresPerDbu => 1e-6 / Math.Max(1, Layout.DbuPerMicron);
}

/// <summary>A <c>schematic</c> view: a staged <c>.cnl</c>, drawn by <see cref="NetlistSchematic.FromFile"/> exactly as
/// <c>netlist --to-schematic</c> draws it.</summary>
public sealed record PluginSchematicView(string Name, string CnlPath, SchematicEditModel Schematic) : PluginView(Name, "schematic");

/// <summary>An <c>image</c> view: a staged PNG or SVG; with <see cref="Clickable"/>, a click is an <c>event</c>.</summary>
public sealed record PluginImageView(string Name, string Path, bool Clickable) : PluginView(Name, "image");

/// <summary>
/// Reads a view from a reply (brief-plg-3). Never throws: a view that cannot be read is a
/// <see cref="PluginUnreadableView"/> whose <see cref="PluginView.Problem"/> names what is wrong, so one bad view
/// leaves every other view of the same reply drawn.
/// </summary>
public static class PluginViews
{
    /// <summary>The first layer number given to a layer the technology does not have, so it is drawn on the
    /// fallback palette rather than refused.</summary>
    public const int UnknownLayerBase = 60000;

    public static PluginView Read(PluginViewBody body, string? declaredKind, PluginViewContext ctx)
    {
        string kind = declaredKind ?? PluginLayoutReader.KindOf(body.Body);
        return ReadKind(body, kind, ctx) with { StagingDir = body.StagingDir };
    }

    private static PluginView ReadKind(PluginViewBody body, string kind, PluginViewContext ctx)
    {
        try
        {
            return kind switch
            {
                "plot" or "smith" => Plot(body, kind, ctx),
                "table"           => Table(body),
                "layout"          => Layout(body, ctx),
                "schematic"       => Schematic(body, ctx),
                "image"           => Image(body, ctx),
                _                 => Unreadable(body.Name, kind, $"'{kind}' is not a view kind this circuitRF knows."),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Unreadable(body.Name, kind, e.Message);
        }
    }

    private static PluginUnreadableView Unreadable(string name, string kind, string why)
        => new(name, kind) { Problem = $"The {name} view cannot be drawn: {why}" };

    /// <summary>A file a view names, inside the staging folder — the session already refused one outside it.</summary>
    private static (string? Full, string? Problem) Staged(PluginViewBody body, string named)
    {
        var (full, refusal) = PluginStaging.Resolve(body.StagingDir, named);
        if (refusal is not null) return (null, refusal);
        if (!System.IO.File.Exists(full)) return (null, $"{named} is not in the plugin's staging folder.");
        return (full, null);
    }

    // ── plot / smith ────────────────────────────────────────────────────────────

    private static PluginView Plot(PluginViewBody body, string kind, PluginViewContext ctx)
    {
        if (body.Body is not JsonObject o) return Unreadable(body.Name, kind, "it is not an object.");
        var plot = o["plot"] as JsonObject ?? new JsonObject();

        DataSet? inline = null;
        string? file = null;
        switch (o["data"])
        {
            case JsonObject d when d["file"] is JsonValue fv && fv.TryGetValue<string>(out var named):
            {
                var (full, problem) = Staged(body, named);
                if (problem is not null) return Unreadable(body.Name, kind, problem);
                file = full;
                break;
            }
            case JsonObject d:
            {
                var (ds, problem) = InlineData(d);
                if (problem is not null) return Unreadable(body.Name, kind, problem);
                inline = ds;
                break;
            }
            default:
                return Unreadable(body.Name, kind, "it has no data (inline axes and cubes, or a file).");
        }

        var markers = new List<PluginPlotMarker>();
        if (plot["markers"] is JsonArray ma)
            for (int i = 0; i < ma.Count; i++)
            {
                if (ma[i] is not JsonObject m) return Unreadable(body.Name, kind, $"marker {i} is not an object.");
                string? bind = Str(m["bind"]);
                double? x = m["x"] is JsonValue xv && xv.TryGetValue<double>(out double xd) ? xd : null;
                // "x": "f0" names the parameter the marker sits at, as "bind" does without the drag.
                string? xParam = m["x"] is JsonValue xs && xs.TryGetValue<string>(out var xn) ? xn : null;
                bind ??= xParam;
                bool draggable = m["draggable"] is JsonValue dv && dv.TryGetValue<bool>(out bool db) ? db : m["bind"] is not null;
                if (bind is not null && BindRefusal(ctx.Manifest, bind, kind) is { } why)
                    return Unreadable(body.Name, kind, $"marker {i} {why}");
                if (bind is null && x is null) return Unreadable(body.Name, kind, $"marker {i} has no x.");
                markers.Add(new PluginPlotMarker(i, Str(m["trace"]), x, bind, draggable && bind is not null));
            }

        var lines = new List<PluginPlotLine>();
        if (plot["lines"] is JsonArray la)
            for (int i = 0; i < la.Count; i++)
            {
                if (la[i] is not JsonObject l || l["y"] is not JsonValue yv || !yv.TryGetValue<double>(out double y))
                    return Unreadable(body.Name, kind, $"line {i} has no y.");
                string side = Str(l["side"]) ?? "above";
                if (side is not ("above" or "below")) return Unreadable(body.Name, kind, $"line {i}'s side is '{side}'; it is above or below.");
                lines.Add(new PluginPlotLine(y, Str(l["label"]), side, NumberOf(l["xStart"]), NumberOf(l["xEnd"])));
            }

        return new PluginPlotView(body.Name, kind, inline, file, (JsonObject)plot.DeepClone(), markers, lines, body.Params);
    }

    /// <summary>
    /// Why <paramref name="param"/> cannot carry a marker, or null. A marker sits on the x axis, so it binds only a
    /// <c>quantity</c>; on a Smith chart that x is the frequency. Whether the unit is the plot's x unit is asked again
    /// when the data is in hand (<see cref="BindUnitRefusal"/>).
    /// </summary>
    public static string? BindRefusal(PluginManifest manifest, string param, string kind)
    {
        var p = manifest.Params.FirstOrDefault(x => x.Name == param);
        if (p is null) return $"is bound to '{param}', which is not one of the plugin's parameters.";
        if (p.Kind != PluginParamKind.Quantity) return $"is bound to '{param}', which is a {p.Kind.ToString().ToLowerInvariant()}; a marker binds a quantity.";
        if (kind == "smith" && p.Unit != "Hz") return $"is bound to '{param}' in {p.Unit}; a Smith chart's marker binds a frequency (Hz).";
        return null;
    }

    /// <summary>Why a marker bound to <paramref name="param"/> cannot sit on an x axis in <paramref name="xUnit"/>, or null.</summary>
    public static string? BindUnitRefusal(PluginManifest manifest, int marker, string param, string xUnit)
    {
        var p = manifest.Params.FirstOrDefault(x => x.Name == param);
        if (p is null || p.Unit == xUnit) return null;
        return $"Marker {marker} is bound to '{param}' in {p.Unit ?? "no unit"}, but the plot's x axis is in {(xUnit.Length == 0 ? "no unit" : xUnit)}.";
    }

    /// <summary>
    /// <c>{axes: {name: {unit, values}}, cubes: {name: {axes: [...], kind, re, im | values}}}</c> as a
    /// <see cref="DataSet"/> of single-kind cubes. A cube whose length is not the product of its axes' lengths is
    /// refused by name — a reshaped cube draws a believable wrong curve.
    /// </summary>
    public static (DataSet? Data, string? Problem) InlineData(JsonObject d)
    {
        var axes = new Dictionary<string, Axis>(StringComparer.Ordinal);
        if (d["axes"] is not JsonObject ao || ao.Count == 0) return (null, "its data has no axes.");
        foreach (var (name, node) in ao)
        {
            if (node is not JsonObject a || Doubles(a["values"]) is not { } values)
                return (null, $"axis '{name}' has no values.");
            axes[name] = new Axis(name, values, Str(a["unit"]) ?? "");
        }

        if (d["cubes"] is not JsonObject co || co.Count == 0) return (null, "its data has no cubes.");
        var ds = new DataSet();
        foreach (var (name, node) in co)
        {
            if (node is not JsonObject c) return (null, $"cube '{name}' is not an object.");
            var cubeAxes = new List<Axis>();
            if (c["axes"] is not JsonArray ca || ca.Count == 0) return (null, $"cube '{name}' names no axes.");
            foreach (var an in ca)
            {
                string? axisName = Str(an);
                if (axisName is null || !axes.TryGetValue(axisName, out var axis)) return (null, $"cube '{name}' names an axis '{axisName}' the data does not have.");
                cubeAxes.Add(axis);
            }
            long expect = cubeAxes.Aggregate(1L, (n, a) => n * a.Length);
            string kind = Str(c["kind"]) ?? (c.ContainsKey("re") ? "complex" : "real");
            if (kind == "complex")
            {
                var re = Doubles(c["re"]);
                var im = Doubles(c["im"]);
                if (re is null || im is null) return (null, $"cube '{name}' is complex and needs re and im.");
                if (re.Length != expect || im.Length != expect)
                    return (null, $"cube '{name}' has {re.Length} re and {im.Length} im values; its axes ({string.Join(" × ", cubeAxes.Select(a => $"{a.Name} {a.Length}"))}) need {expect}.");
                var z = new Complex[expect];
                for (int i = 0; i < expect; i++) z[i] = new Complex(re[i], im[i]);
                ds.Add(name, new DataCube([.. cubeAxes], z) { Unit = Str(c["unit"]) ?? "" });
            }
            else if (kind == "real")
            {
                var v = Doubles(c["values"]);
                if (v is null) return (null, $"cube '{name}' is real and needs values.");
                if (v.Length != expect)
                    return (null, $"cube '{name}' has {v.Length} values; its axes ({string.Join(" × ", cubeAxes.Select(a => $"{a.Name} {a.Length}"))}) need {expect}.");
                ds.Add(name, new DataCube([.. cubeAxes], v) { Unit = Str(c["unit"]) ?? "" });
            }
            else return (null, $"cube '{name}' is of kind '{kind}'; a cube is real or complex.");
        }
        return (ds, null);
    }

    // ── table ───────────────────────────────────────────────────────────────────

    private static PluginView Table(PluginViewBody body)
    {
        if (body.Body is not JsonObject o || o["columns"] is not JsonArray) return Unreadable(body.Name, "table", "it has no columns.");
        return new PluginTableView(body.Name, PluginValueFormat.Readout(o, null));
    }

    // ── layout ──────────────────────────────────────────────────────────────────

    private static PluginView Layout(PluginViewBody body, PluginViewContext ctx)
    {
        if (body.Body is not JsonObject o) return Unreadable(body.Name, "layout", "it is not an object.");
        var notes = new List<string>();

        var (tech, techPath, techProblem) = TechnologyOf(o["technology"], body.Params, ctx);
        if (techProblem is not null) notes.Add($"The {body.Name} view: {techProblem} It is drawn on the fallback palette.");

        // Layers by NAME are the technology's; the wire itself carries numbers, so a name is resolved here and the
        // codec decodes exactly what a PCell generator's reply would be.
        var missing = new List<string>();
        var invented = new Dictionary<string, LayerKey>(StringComparer.Ordinal);
        JsonNode? Resolve(JsonNode? layer)
        {
            if (layer is JsonValue v && v.TryGetValue<string>(out var name))
            {
                var key = tech?.Layers.FirstOrDefault(l => l.Name == name)?.Key;
                if (key is null)
                {
                    if (!invented.TryGetValue(name, out var k))
                    {
                        invented[name] = k = new LayerKey(UnknownLayerBase + invented.Count, 0);
                        missing.Add(name);
                    }
                    key = k;
                }
                return new JsonObject { ["layer"] = key.Value.Layer, ["datatype"] = key.Value.Datatype };
            }
            if (layer is JsonObject lo && tech is not null
                && lo["layer"] is JsonValue ln && ln.TryGetValue<int>(out int n)
                && !tech.Layers.Any(l => l.Key.Layer == n && l.Key.Datatype == (lo["datatype"] is JsonValue dn && dn.TryGetValue<int>(out int dt) ? dt : 0)))
            {
                string label = $"{n}/{(lo["datatype"] is JsonValue d2 && d2.TryGetValue<int>(out int dt2) ? dt2 : 0)}";
                if (!missing.Contains(label)) missing.Add(label);
            }
            return layer?.DeepClone();
        }

        var reply = new JsonObject { ["ok"] = true };
        foreach (var (key, list) in new[] { ("shapes", o["shapes"]), ("pins", o["ports"] ?? o["pins"]), ("handles", o["handles"]) })
        {
            if (list is null) continue;
            if (list is not JsonArray arr) return Unreadable(body.Name, "layout", $"its {key} is not a list.");
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                if (item is not JsonObject io) return Unreadable(body.Name, "layout", $"an entry of its {key} is not an object.");
                var c = (JsonObject)io.DeepClone();
                if (c.ContainsKey("layer")) c["layer"] = Resolve(io["layer"]);
                if (c.ContainsKey("landingLayer")) c["landingLayer"] = Resolve(io["landingLayer"]);
                copy.Add(c);
            }
            reply[key] = copy;
        }
        reply["shapes"] ??= new JsonArray();
        reply["pins"] ??= new JsonArray();

        PCellResult result;
        try { result = PCellWireCodec.DecodeGenerateReply(new PCellWireFrame(reply.ToJsonString(), body.Payload)); }
        catch (PCellWireException e) { return Unreadable(body.Name, "layout", e.Message.Replace("The PCell generator", "The plugin")); }

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var view = new LayoutView { DbuPerMicron = dbuPerMicron };
        if (tech is not null) view.DisplayUnit = tech.DefaultDisplayUnit;
        foreach (var s in result.Shapes) view.Shapes.Add(s);

        // Ports as layout port labels — the form SDL-5 writes into a .clay, so a .cem can name them once placed.
        var ports = new List<LabelShape>();
        foreach (var pin in result.Pins)
        {
            var label = new LabelShape
            {
                Layer = pin.Layer, PortLayer = pin.Layer, X = pin.X, Y = pin.Y, Text = pin.Name,
                Height = Math.Max(pin.WidthDbu / 2, dbuPerMicron), IsPort = true,
                PortDirection = LayoutPortDirection.FromPinOutward(pin.OutwardDirectionDeg),
            };
            ports.Add(label);
            view.Shapes.Add(label);
        }

        if (missing.Count > 0)
            notes.Add($"The {body.Name} view draws on {(missing.Count == 1 ? "a layer" : "layers")} the technology does not have, " +
                      $"on the fallback palette: {string.Join(", ", missing)}.");
        if (result.Diagnostics is { Count: > 0 } diag) notes.AddRange(diag.Select(x => $"The {body.Name} view: {x}"));

        return new PluginLayoutView(body.Name, view, tech, result.Handles ?? [], ports, body.Params) { Notes = notes, TechnologyPath = tech is null ? null : techPath };
    }

    /// <summary><c>"self"</c> is the technology the plugin's own <c>technology</c> or <c>substrate</c> parameter names;
    /// anything else is a technology path relative to the workspace. None is drawn on the fallback palette.</summary>
    private static (Technology? Tech, string? Path, string? Problem) TechnologyOf(JsonNode? node, JsonObject parameters, PluginViewContext ctx)
    {
        string? named = Str(node);
        if (named is null) return (null, null, null);
        string? path;
        if (named == "self")
        {
            var p = ctx.Manifest.Params.FirstOrDefault(x => x.Kind is PluginParamKind.Technology or PluginParamKind.Substrate);
            if (p is null) return (null, null, "\"technology\": \"self\" needs a technology or substrate parameter, and the plugin has none.");
            path = p.Kind == PluginParamKind.Technology ? Str(parameters[p.Name]?["path"]) : Str(parameters[p.Name]?["technology"]);
            if (path is null) return (null, null, $"its {p.Name} parameter names no technology.");
        }
        else
        {
            if (ctx.WorkspaceDir is null) return (null, null, $"there is no workspace to find the technology {named} in.");
            path = System.IO.Path.GetFullPath(named, ctx.WorkspaceDir);
        }
        return TechnologyResolver.LoadForPath(path) is { } tech ? (tech, path, null) : (null, null, $"{named} is not a technology circuitRF can read.");
    }

    // ── schematic ───────────────────────────────────────────────────────────────

    private static PluginView Schematic(PluginViewBody body, PluginViewContext ctx)
    {
        if (Str(body.Body["cnl"]) is not { } named) return Unreadable(body.Name, "schematic", "it names no .cnl.");
        var (full, problem) = Staged(body, named);
        if (problem is not null) return Unreadable(body.Name, "schematic", problem);
        var drawn = NetlistSchematic.FromFile(full!);
        if (drawn.Schematic is null) return Unreadable(body.Name, "schematic", string.Join(" ", drawn.Refusals));
        return new PluginSchematicView(body.Name, full!, drawn.Schematic) { Notes = drawn.Notes.Select(n => $"The {body.Name} view: {n}").ToList() };
    }

    // ── image ───────────────────────────────────────────────────────────────────

    private static PluginView Image(PluginViewBody body, PluginViewContext ctx)
    {
        if (Str(body.Body["file"]) is not { } named) return Unreadable(body.Name, "image", "it names no file.");
        string ext = System.IO.Path.GetExtension(named).ToLowerInvariant();
        if (ext is not (".png" or ".svg")) return Unreadable(body.Name, "image", $"{named} is not a PNG or an SVG.");
        var (full, problem) = Staged(body, named);
        if (problem is not null) return Unreadable(body.Name, "image", problem);
        bool clickable = body.Body["clickable"] is JsonValue cv && cv.TryGetValue<bool>(out bool c) && c;
        return new PluginImageView(body.Name, full!, clickable);
    }

    // ── small readers ───────────────────────────────────────────────────────────

    /// <summary>A number from a reply or a parameter: a bare number, or a quantity's <c>{value}</c>.</summary>
    public static double? NumberOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<double>(out double d) => d,
        JsonObject o when o["value"] is JsonValue v && v.TryGetValue<double>(out double d) => d,
        _ => null,
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double[]? Doubles(JsonNode? n)
    {
        if (n is not JsonArray a) return null;
        var r = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] is JsonValue v && v.TryGetValue<double>(out double d)) r[i] = d;
            else if (a[i] is null) r[i] = double.NaN;
            else return null;
        }
        return r;
    }

    /// <summary>The number text a parameter's control shows for <paramref name="value"/> in base SI — the shortest
    /// spelling that reads back to exactly that value, so a dragged marker sends the value it was dropped at.</summary>
    public static string QuantityText(PluginParam p, double value, PluginParamContext ctx)
    {
        string shown = PluginValueFormat.ControlText(p, JsonValue.Create(value));
        if (PluginParams.ResolveValue(p, shown, ctx).Node is { } back && NumberOf(back) == value) return shown;
        return value.ToString("R", CultureInfo.InvariantCulture) + (p.Unit is { Length: > 0 } u ? " " + u : "");
    }
}
