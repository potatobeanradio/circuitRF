using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Design.Plugins;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Cli;

/// <summary>A plugin's <c>plot</c> or <c>smith</c> view as a Data Display plot, or the sentence saying why it is not.</summary>
/// <param name="Plot">The plot, its traces resolved, the plugin's markers on them — what the panel's <c>PlotControl</c>
/// draws and what <b>Open in Data Display</b> saves.</param>
/// <param name="Container">The document the plot was loaded from, as a <c>.cdd</c> holds it.</param>
/// <param name="Bound">Each bound marker the plugin placed, and the marker object standing for it on the plot.</param>
public sealed record PluginPlotComposition(
    Plot? Plot, PlotContainerConfig? Container, DataSet? Data, SNP? Snp, string SourceName,
    IReadOnlyList<(PluginPlotMarker Spec, Marker Marker, Trace Trace)> Bound, string? Problem);

/// <summary>
/// Composes a plugin's plot view (brief-plg-3 §1) with <c>circuitRF plot</c>'s own code. The plot object is spelled as
/// the verb's flags — each trace's fields as one <c>--trace</c>, so <c>cube=</c> and <c>y=</c> mean what they mean
/// there — and handed to <see cref="PlotVerb.ComposeArguments"/>; the document that comes back is loaded with
/// <see cref="PlotConfigLoader"/>, as a <c>.cdd</c> is. There is no second trace parser and no second <c>.cdd</c> reader
/// here, and a source scan holds that (gate 2).
/// </summary>
public static class PluginPlots
{
    /// <summary>The source name an inline data set is known by in the plot and in a saved <c>.cdd</c>: the view's own
    /// name, as the <c>.npy</c> <b>Open in Data Display</b> writes it under.</summary>
    public static string InlineSourceName(string view) => view + ".npy";

    private static readonly HashSet<string> PlotFields = new(StringComparer.Ordinal)
        { "title", "x", "y", "y2", "traces", "markers", "lines", "freqUnit", "legend" };

    public static PluginPlotComposition Compose(PluginPlotView view, PluginManifest manifest)
    {
        // ── the data: inline, or the file read the way the Data Display reads one ──
        DataSet? data = view.Inline;
        SNP? snp = null;
        string source = InlineSourceName(view.Name);
        if (view.File is { } file)
        {
            var (d, s, error) = PlotSourceFile.Load(file);
            if (error is not null || d is null) return Fail($"{Path.GetFileName(file)} could not be read ({error ?? "it holds no data"}).");
            (data, snp, source) = (d, s, Path.GetFileName(file));
        }
        if (data is null) return Fail("it has no data.");

        foreach (var (key, _) in view.Plot)
            if (!PlotFields.Contains(key)) return Fail($"'{key}' is not a plot field circuitRF reads ({string.Join(", ", PlotFields)}).");

        // ── the document, by the verb's own parse and composition ──
        var (args, argsProblem) = Arguments(view, xScale: null);
        if (argsProblem is not null) return Fail(argsProblem);
        var (config, refusal) = PlotVerb.ComposeArguments(args, data, source);
        if (refusal is not null) return Fail(refusal);

        var sources = new Source(source, data, snp);
        var pc = config!.Tabs[0].Plots[0];
        var plot = PlotConfigLoader.LoadPlot(pc, sources);
        if (plot.Traces.Count == 0) return Fail("none of its traces resolved against its data.");

        // A range on a frequency axis is given in hertz, as every value a plugin sends is; the plot's own window is in
        // its display unit. The axis is known only once a trace has resolved, so the range is applied in a second pass.
        string xUnit = XUnitOf(plot.Traces[0], data);
        if (view.Plot["x"] is JsonObject xo && (xo.ContainsKey("min") || xo.ContainsKey("max")))
        {
            double scale = xUnit == "Hz" ? plot.FreqUnits.Scale() : 1.0;
            (args, argsProblem) = Arguments(view, scale);
            if (argsProblem is not null) return Fail(argsProblem);
            (config, refusal) = PlotVerb.ComposeArguments(args, data, source);
            if (refusal is not null) return Fail(refusal);
            pc = config!.Tabs[0].Plots[0];
        }

        // ── limit lines: the Data Display's limit areas ──
        if (view.Lines.Count > 0)
        {
            double scale = xUnit == "Hz" ? pc.FreqUnit.Scale() : 1.0;
            pc.LimitAreas = [.. view.Lines.Select(l => new LimitAreaConfig
            {
                Side = l.Side == "below" ? LimitAreaSide.Below : LimitAreaSide.Above,
                Limit = l.Y, XStart = l.XStart * scale, XEnd = l.XEnd * scale,
            })];
        }
        plot = PlotConfigLoader.LoadPlot(pc, sources);
        if (plot.Traces.Count == 0) return Fail("none of its traces resolved against its data.");

        // ── the plugin's markers ──
        var bound = new List<(PluginPlotMarker, Marker, Trace)>();
        var cubes = view.Plot["traces"] is JsonArray ta ? ta.Select(t => (t?["cube"] as JsonValue)?.GetValue<string>()).ToList() : [];
        foreach (var m in view.Markers)
        {
            if (m.Bind is { } param && PluginViews.BindUnitRefusal(manifest, m.Index, param, xUnit) is { } why) return Fail(why);
            int ti = m.Trace is null ? 0 : cubes.IndexOf(m.Trace);
            if (ti < 0 || ti >= plot.Traces.Count) return Fail($"marker {m.Index} names the trace '{m.Trace}', which the plot does not have.");
            var trace = plot.Traces[ti];
            var marker = new Marker(trace, 0.0, isMulti: false, isDelta: false, index: m.Index + 1, plot.FreqUnits)
            {
                Name = m.Bind ?? $"m{m.Index + 1}",
            };
            if (!Place(trace, marker, view.BoundValue(m))) return Fail($"marker {m.Index} cannot be placed on '{m.Trace ?? "the first trace"}'.");
            trace.Markers.Add(marker);
            if (m.Bind is not null) bound.Add((m, marker, trace));
        }

        return new PluginPlotComposition(plot, pc, data, snp, source, bound, null);

        PluginPlotComposition Fail(string why)
            => new(null, null, data, snp, source, [], $"The {view.Name} view cannot be drawn: {why}");
    }

    /// <summary>
    /// <b>Open in Data Display</b>: the plot's data and a <c>.cdd</c> showing it, written into
    /// <paramref name="stagingDir"/> beside the plugin's own files — inline data as <c>&lt;view&gt;.npy</c>, a staged file
    /// as itself — with every marker on the plot. Returns the <c>.cdd</c>'s path. Placement moves neither.
    /// </summary>
    public static string WriteDataDisplay(PluginPlotComposition c, string stagingDir, string view)
    {
        if (c.Plot is null || c.Container is null || c.Data is null) throw new InvalidOperationException(c.Problem ?? "Nothing to open.");
        string dataPath = Path.Combine(stagingDir, c.SourceName);
        if (!File.Exists(dataPath))
            using (var stream = File.Create(dataPath)) RfCore.Export.DataSetExporter.ExportNpy(c.Data, stream);

        var pc = System.Text.Json.JsonSerializer.Deserialize<PlotContainerConfig>(
            System.Text.Json.JsonSerializer.Serialize(c.Container, DataDisplayJson.Options), DataDisplayJson.Options)!;
        for (int i = 0; i < pc.Traces.Count && i < c.Plot.Traces.Count; i++)
            pc.Traces[i].Markers = [.. c.Plot.Traces[i].Markers.Select(m => new MarkerConfig
            {
                Name = m.Name, Index = m.Index, Freq = m.Freq, FreqUnits = m.FreqUnits, MatrixFormat = m.MatrixFormat,
                PositionStaticX = m.PositionStatic.X, PositionStaticY = m.PositionStatic.Y,
            })];
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            SelectedDataSource = c.SourceName,
            Tabs = [new TabConfig { Name = view, Plots = [pc] }],
        };
        string cdd = Path.Combine(stagingDir, view + ".cdd");
        File.WriteAllText(cdd, System.Text.Json.JsonSerializer.Serialize(config, DataDisplayJson.Options), new System.Text.UTF8Encoding(false));
        return cdd;
    }

    /// <summary>Puts <paramref name="marker"/> on the sample of <paramref name="trace"/> nearest <paramref name="x"/>
    /// (raw axis units: hertz on a frequency axis). False when the trace has no samples to sit on.</summary>
    public static bool Place(Trace trace, Marker marker, double? x)
    {
        if (x is not { } want || trace.CubeXValues is not { Count: > 0 } xs) return false;
        int best = 0;
        for (int i = 1; i < xs.Count; i++) if (Math.Abs(xs[i] - want) < Math.Abs(xs[best] - want)) best = i;
        if (trace.CubeSamplePosition(best) is not { } pos) return false;
        marker.PositionStatic = pos;
        return true;
    }

    /// <summary>The raw axis value a marker sits on — exactly as the data's axis holds it — or null.</summary>
    public static double? XOf(Trace trace, Marker marker)
        => trace.CubeMarkerSampleIndex(marker) is { } i && trace.CubeXValues is { } xs && i < xs.Count ? xs[i] : null;

    /// <summary>The unit of the axis a trace runs along.</summary>
    private static string XUnitOf(Trace trace, DataSet data)
    {
        string? axis = trace.CubeXAxisName;
        if (axis is null) return "";
        foreach (var group in data.Groups)
            foreach (var (_, cube) in data.CubesIn(group))
                if (cube.Axes.FirstOrDefault(a => a.Name == axis) is { } a) return a.Unit;
        return "";
    }

    /// <summary>The plot spec as <c>circuitRF plot</c>'s flags. <paramref name="xScale"/> converts an x range from base
    /// SI to the plot's display unit; null leaves the x range out.</summary>
    internal static (List<string> Args, string? Problem) Arguments(PluginPlotView view, double? xScale)
    {
        var p = view.Plot;
        var args = new List<string> { "--type", view.IsSmith ? "smith" : "rect" };
        if (Text(p["title"]) is { } title) args.AddRange(["--title", title]);
        if (Text(p["freqUnit"]) is { } fu) args.AddRange(["--freq-unit", fu]);
        if (Text(p["legend"]) is { } lg) args.AddRange(["--legend", lg]);

        foreach (var (key, flag, label) in new[] { ("x", "--x", "--xlabel"), ("y", "--y", "--ylabel"), ("y2", "--y2", "--y2label") })
        {
            if (p[key] is not JsonObject axis) continue;
            if (Text(axis["label"]) is { } l) args.AddRange([label, l]);
            double? lo = PluginViews.NumberOf(axis["min"]), hi = PluginViews.NumberOf(axis["max"]);
            if (lo is null && hi is null) continue;
            if (lo is null || hi is null) return (args, $"its {key} range needs both min and max.");
            if (key == "x") { if (xScale is not { } s) continue; (lo, hi) = (lo * s, hi * s); }
            args.AddRange([flag, string.Create(CultureInfo.InvariantCulture, $"{lo:R}:{hi:R}")]);
        }

        if (p["traces"] is not JsonArray traces || traces.Count == 0) return (args, "its plot names no traces.");
        for (int i = 0; i < traces.Count; i++)
        {
            if (traces[i] is not JsonObject t) return (args, $"trace {i} is not an object.");
            var fields = new List<string>();
            foreach (var (k, v) in t)
            {
                // label= is the one field whose value may hold a comma; the verb reads it quoted.
                string? value = v switch
                {
                    JsonValue jv when jv.TryGetValue<string>(out var s) => k == "label" ? $"\"{s}\"" : s,
                    JsonValue jv when jv.TryGetValue<bool>(out bool b) => b ? "1" : "0",
                    JsonValue jv when jv.TryGetValue<double>(out double d) => d.ToString("R", CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (value is null) return (args, $"trace {i}'s {k} is not a string, a number or true/false.");
                fields.Add($"{k}={value}");
            }
            args.AddRange(["--trace", string.Join(",", fields)]);
        }
        return (args, null);
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>One data set as the plot's only source.</summary>
    public sealed class Source(string name, DataSet data, SNP? snp) : IPlotDataSources
    {
        public string? ResolveAbs(string? sourceRef) => sourceRef is null || sourceRef == name ? name : null;
        public bool Contains(string absPath) => absPath == name;
        public SNP? NetworkFor(string absPath) => absPath == name ? snp : null;
        public DataSet? DataFor(string absPath) => absPath == name ? data : null;
        public string? AliasFor(string absPath) => null;
        public string? DisplayNameFor(string absPath) => absPath == name ? Path.GetFileNameWithoutExtension(name) : null;
        public bool HasMultipleSources => false;
        public DataSet? SelectedData => data;
    }
}
