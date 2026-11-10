using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Plugins;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Plugins.Views;
using CircuitRF.Ui.Tests.Layout.PCells;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-3 gates 1–4: a plugin's views read, composed and placed with circuitRF's own code. Gate 5 is
/// <c>NetlistToSchematicTests</c> (the move kept its output byte for byte); gate 6 is UiSnapshot's.
/// </summary>
public class PluginViewTests
{
    private static readonly PluginManifest Manifest = PluginManifestReader.Read("""
        { "apiVersion": 1, "id": "t.views", "name": "Views", "version": "1",
          "run": { "python": { "entry": "x.py" } }, "kind": "panel",
          "params": [
            { "name": "f0", "kind": "quantity", "unit": "Hz", "default": "2.4 GHz" },
            { "name": "n",  "kind": "int", "default": 1 }
          ] }
        """).Manifest!;

    private static PluginViewBody Body(string name, JsonNode body, JsonObject? parameters = null, long serial = 1)
        => new(name, body, default, parameters ?? new JsonObject { ["f0"] = new JsonObject { ["value"] = 2.4e9, ["unit"] = "Hz" } }, serial);

    private static string RepoRoot()
    {
        for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "VERSION")) && Directory.Exists(Path.Combine(d, "src"))) return d;
        throw new DirectoryNotFoundException("the repository root");
    }

    /// <summary>An inline cube from re/im arrays: one axis, one complex cube.</summary>
    private static JsonObject Inline(double[] freqs, Complex[] values, string cube = "S21") => new()
    {
        ["axes"] = new JsonObject { ["freq"] = new JsonObject { ["unit"] = "Hz", ["values"] = new JsonArray([.. freqs.Select(f => (JsonNode)f)]) } },
        ["cubes"] = new JsonObject
        {
            [cube] = new JsonObject
            {
                ["axes"] = new JsonArray("freq"), ["kind"] = "complex",
                ["re"] = new JsonArray([.. values.Select(v => (JsonNode)v.Real)]),
                ["im"] = new JsonArray([.. values.Select(v => (JsonNode)v.Imaginary)]),
            },
        },
    };

    // ── gate 1: inline data is the data ────────────────────────────────────────

    [Fact]
    public void Gate1_AnInlineS21_IsTheS21TheDataDisplayReadsFromTheS2p_ElementForElement()
    {
        string s2p = Path.Combine(RepoRoot(), "examples", "Model Composer", "passive-spiral", "s", "spiral_1.5t.s2p");
        var snp = TouchstoneIO.ReadFile(s2p);
        var freqs = snp.Frequencies.ToArray();
        var s21 = snp.Matrices.Select(m => m[1, 0]).ToArray();

        // The plugin sends what it has as JSON numbers; the round trip through text is the one a reply makes.
        var json = JsonNode.Parse(Inline(freqs, s21).ToJsonString())!.AsObject();
        var (inline, problem) = PluginViews.InlineData(json);
        Assert.Null(problem);

        var (fromFile, _, error) = PlotSourceFile.Load(s2p);
        Assert.Null(error);
        var cube = inline!["S21"];
        var s = fromFile!["S"];
        Assert.Equal(RfCore.Data.DataKind.Complex, cube.DataKind);
        Assert.Equal(freqs.Length, cube.ComplexValues.Length);
        for (int f = 0; f < freqs.Length; f++)
        {
            Assert.Equal(s.Axes[0].Values[f], cube.Axes[0].Values[f]);
            Assert.Equal(s.ComplexValues[f * 4 + 1 * 2 + 0], cube.ComplexValues[f]);   // [freq, i=2, j=1]
        }
    }

    [Fact]
    public void Gate1_ACubeWhoseLengthIsNotItsAxes_IsRefusedByName()
    {
        var json = Inline([1e9, 2e9, 3e9], [Complex.One, Complex.One]);
        var (data, problem) = PluginViews.InlineData(json);
        Assert.Null(data);
        Assert.Contains("'S21'", problem);
        Assert.Contains("need 3", problem);

        var view = PluginViews.Read(Body("response", new JsonObject { ["data"] = json, ["plot"] = new JsonObject() }), "plot",
                                    new PluginViewContext(Manifest, null));
        Assert.IsType<PluginUnreadableView>(view);
        Assert.Contains("'S21'", view.Problem);
    }

    // ── gate 2: one parser ─────────────────────────────────────────────────────

    [Fact]
    public void Gate2_NoSecondTraceParserOrCddReader_UnderThePluginFolders()
    {
        string root = RepoRoot();
        var files = new[] { "src/Ui/Plugins", "src/Design/Plugins" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories)).ToList();
        Assert.NotEmpty(files);
        string[] forbidden =
        [
            "case \"cube\"", "\"cube=\"", "\"y=\"", "new TraceConfig", "Deserialize<DataDisplayConfig", "Deserialize<PlotContainerConfig",
            "CubeTraceSpecParser", "new AxesConfig", "new MarkerConfig",
        ];
        foreach (string f in files)
        {
            string code = Regex.Replace(Regex.Replace(File.ReadAllText(f), @"//.*", ""), @"/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (string bad in forbidden) Assert.False(code.Contains(bad), $"{Path.GetFileName(f)} contains {bad}");
        }

        // …and the composition is the verb's own, loaded as a .cdd is.
        string plots = File.ReadAllText(Path.Combine(root, "src", "Cli", "PluginPlots.cs"));
        Assert.Contains("PlotVerb.ComposeArguments(", plots);
        Assert.Contains("PlotConfigLoader.LoadPlot(", plots);
        Assert.DoesNotContain("SplitFields", plots);
    }

    [Fact]
    public void APlotSpec_IsComposedByThePlotVerb_SoItsRefusalsAreTheVerbs()
    {
        var data = Inline([1e9, 2e9, 3e9], [new(0.5, 0), new(0.1, 0.1), new(0.4, -0.2)]);
        PluginPlotComposition Compose(JsonObject trace)
        {
            var body = new JsonObject { ["data"] = data.DeepClone(), ["plot"] = new JsonObject { ["traces"] = new JsonArray(trace) } };
            return PluginPlots.Compose((PluginPlotView)PluginViews.Read(Body("response", body), "plot", new PluginViewContext(Manifest, null)), Manifest);
        }

        var ok = Compose(new JsonObject { ["cube"] = "S21", ["y"] = "db20" });
        Assert.Null(ok.Problem);
        Assert.Single(ok.Plot!.Traces);
        Assert.Equal(20 * Math.Log10(0.5), ok.Plot.Traces[0].Points[0].Y, 4);

        // y=db is the verb's own refusal: 10·log10 here and 20·log10 in a measure line draw at half depth.
        Assert.Contains("db", Compose(new JsonObject { ["cube"] = "S21", ["y"] = "db" }).Problem);
        Assert.Contains("S99", Compose(new JsonObject { ["cube"] = "S99" }).Problem);
    }

    // ── gate 3: a bound marker ─────────────────────────────────────────────────

    private sealed class Session : IPluginPanelSession
    {
        public readonly List<(string Type, JsonObject Params)> Sent = [];
        public JsonObject? View;
        public event Action<PluginProgress>? Progress { add { } remove { } }
        public event Action<PluginFailure>? Ended { add { } remove { } }
        public PluginFailure? Fatal => null;
        public string StagingDirectory => Path.GetTempPath();
        public Task<PluginOutcome<PluginHello>> HelloAsync(CancellationToken ct = default) => Task.FromResult(PluginOutcome<PluginHello>.Of(new PluginHello(1)));
        private Task<PluginOutcome<PluginReply>> Answer(string type, JsonObject p)
        {
            lock (Sent) Sent.Add((type, (JsonObject)p.DeepClone()));
            return Task.FromResult(PluginOutcome<PluginReply>.Of(new PluginReply(new JsonObject(),
                new JsonObject { ["response"] = View!.DeepClone() }, [], new JsonObject(), null)));
        }
        public Task<PluginOutcome<PluginReply>> UpdateAsync(JsonObject parameters, CancellationToken ct = default) => Answer("update", parameters);
        public Task<PluginOutcome<PluginReply>> ActionAsync(string name, JsonObject parameters, CancellationToken ct = default) => Answer("action", parameters);
        public Task<PluginOutcome<PluginReply>> EventAsync(string view, string kind, JsonObject fields, JsonObject parameters, CancellationToken ct = default)
            => Answer($"event:{view}:{kind}:{fields["marker"]}:{fields["x"]}", parameters);
        public Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SettledAsync(TimeSpan within) => Task.FromResult(true);
        public Task ByeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task Gate3_DroppingABoundMarkerAt2p35GHz_SendsOneEventAndOneUpdate_AndTheReplyDrawsItThere()
    {
        // A 10 MHz grid, so 2.35 GHz is a sample: a marker sits on samples, and a drag lands on one.
        double[] freqs = [.. Enumerable.Range(0, 121).Select(k => (200 + k) * 1e7)];
        var view = new JsonObject
        {
            ["data"] = Inline(freqs, [.. freqs.Select(f => new Complex(f / 4e9, 0))]),
            ["plot"] = new JsonObject
            {
                ["traces"] = new JsonArray(new JsonObject { ["cube"] = "S21", ["y"] = "db20" }),
                ["markers"] = new JsonArray(new JsonObject { ["trace"] = "S21", ["bind"] = "f0", ["draggable"] = true }),
            },
        };
        var session = new Session { View = view };
        await using var c = new PluginPanelController(Manifest, () => PluginOutcome<IPluginPanelSession>.Of(session),
                                                      new PluginPanelOptions { Debounce = TimeSpan.FromMilliseconds(10) });
        c.Open();
        await Until(() => c.State.Reply is not null && !c.State.InFlight, "the first reply");

        // The marker as the panel draws it, then the drop: the sample it lands on, as the axis holds it.
        var ctx = new PluginViewContext(Manifest, null);
        var drawn = PluginPlots.Compose((PluginPlotView)PluginViews.Read(c.State.ViewBodies["response"], "plot", ctx), Manifest);
        var (spec, marker, trace) = Assert.Single(drawn.Bound);
        Assert.Equal(2.4e9, PluginPlots.XOf(trace, marker));
        Assert.True(PluginPlots.Place(trace, marker, 2.35e9));
        double x = PluginPlots.XOf(trace, marker)!.Value;
        Assert.Equal(2.35e9, x);

        int before = session.Sent.Count;
        var f0 = Manifest.Params.First(p => p.Name == "f0");
        c.SendEvent("response", "marker", new JsonObject { ["marker"] = spec.Index, ["x"] = x }, "f0",
                    PluginViews.QuantityText(f0, x, new PluginParamContext(null)));
        await Until(() => session.Sent.Count == before + 2 && !c.State.InFlight, "the event and its update");
        await Task.Delay(100);   // nothing further may follow

        var after = session.Sent.Skip(before).ToList();
        Assert.Equal(2, after.Count);
        Assert.StartsWith("event:response:marker:0:", after[0].Type);
        Assert.Equal("update", after[1].Type);
        foreach (var (_, p) in after) Assert.Equal(2.35e9, PluginViews.NumberOf(p["f0"]));
        Assert.Equal("2.35 GHz", c.Value("f0"));

        // After the reply, the marker is drawn at the parameter's new value.
        var redrawn = PluginPlots.Compose((PluginPlotView)PluginViews.Read(c.State.ViewBodies["response"], "plot", ctx), Manifest);
        var (_, m2, t2) = Assert.Single(redrawn.Bound);
        Assert.Equal(2.35e9, PluginPlots.XOf(t2, m2));
    }

    [Fact]
    public void Gate3_AMarkerBoundToAnythingButAQuantityInTheXUnit_IsRefusedNamingIt()
    {
        var data = Inline([1e9, 2e9], [Complex.One, Complex.One]);
        PluginView Read(string bind) => PluginViews.Read(Body("response", new JsonObject
        {
            ["data"] = data.DeepClone(),
            ["plot"] = new JsonObject
            {
                ["traces"] = new JsonArray(new JsonObject { ["cube"] = "S21", ["y"] = "db20" }),
                ["markers"] = new JsonArray(new JsonObject { ["trace"] = "S21", ["x"] = 1e9 }, new JsonObject { ["trace"] = "S21", ["bind"] = bind }),
            },
        }), "plot", new PluginViewContext(Manifest, null));

        var intBound = Read("n");
        Assert.Contains("marker 1", intBound.Problem);
        Assert.Contains("'n'", intBound.Problem);

        // A quantity whose unit is not the x axis's: refused when the data is in hand, naming the marker.
        var ohms = PluginManifestReader.Read("""
            { "apiVersion": 1, "id": "t.o", "name": "O", "version": "1", "run": { "python": { "entry": "x.py" } }, "kind": "panel",
              "params": [ { "name": "r", "kind": "quantity", "unit": "Ohm", "default": "50 Ohm" } ] }
            """).Manifest!;
        var view = (PluginPlotView)PluginViews.Read(Body("response", new JsonObject
        {
            ["data"] = data.DeepClone(),
            ["plot"] = new JsonObject
            {
                ["traces"] = new JsonArray(new JsonObject { ["cube"] = "S21", ["y"] = "db20" }),
                ["markers"] = new JsonArray(new JsonObject { ["trace"] = "S21", ["bind"] = "r" }),
            },
        }, new JsonObject { ["r"] = new JsonObject { ["value"] = 50.0 } }), "plot", new PluginViewContext(ohms, null));
        var composed = PluginPlots.Compose(view, ohms);
        Assert.Contains("Marker 0", composed.Problem);
        Assert.Contains("Hz", composed.Problem);
    }

    // ── gate 4: the layout, previewed and placed ───────────────────────────────

    /// <summary>The stub example, run for real against a copy of the Patch Antenna workspace: its first reply.</summary>
    private static async Task<(PluginSession Session, JsonObject Values, PluginReply Reply, string Workspace)> Stub(string scratch)
    {
        string ws = Path.Combine(scratch, "ws");
        CopyDir(Path.Combine(RepoRoot(), "examples", "Patch Antenna"), ws);
        var entry = PluginTestKit.Example("stub");
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, scratch) with { WorkspaceDir = ws });
        Assert.True(started.Ok, started.Failure?.Sentence);
        var session = started.Value!;
        Assert.True((await session.HelloAsync()).Ok);
        var texts = entry.Manifest!.Params.Where(p => p.Kind != PluginParamKind.Readout)
            .ToDictionary(p => p.Name, p => PluginParams.StartingText(p, null, ws));
        var (values, errors) = PluginParams.Resolve(entry.Manifest.Params, texts, new PluginParamContext(ws));
        Assert.Empty(errors);
        var reply = await session.UpdateAsync(values!);
        Assert.True(reply.Ok, reply.Failure?.Sentence);
        return (session, values!, reply.Value!, ws);
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    private static PluginLayoutView Artwork(PluginSession s, JsonObject values, PluginReply reply, string ws)
    {
        var body = new PluginViewBody("artwork", reply.Views["artwork"]!, reply.Payload, values, 1) { StagingDir = s.StagingDirectory };
        var view = PluginViews.Read(body, "layout", new PluginViewContext(s.Manifest, ws));
        Assert.Null(view.Problem);
        return Assert.IsType<PluginLayoutView>(view);
    }

    [PythonFact]
    public async Task Gate4_TheStagedShapesPlaced_GiveAClayWhoseGeometryIsThePreviews_ShapeForShape()
    {
        string scratch = PluginTestKit.NewScratch();
        var (session, values, reply, ws) = await Stub(scratch);
        await using var _ = session;
        var preview = Artwork(session, values, reply, ws);
        Assert.NotNull(preview.Technology);
        Assert.Empty(preview.Notes);                                    // every layer it names, the technology has
        Assert.Equal(2, preview.Ports.Count);
        Assert.All(preview.Ports, p => Assert.True(p.IsPort));

        var placed = await session.CommitAsync(values);
        Assert.True(placed.Ok, placed.Failure?.Sentence);
        var cell = Assert.Single(placed.Value!.Cells);
        Assert.Equal("artwork", cell.LayoutView);
        var result = PluginPlacement.Place(new PluginPlacementRequest(ws, session.Manifest, cell, placed.Value.Messages, Layout: preview));
        Assert.Equal(PluginPlacementOutcome.Placed, result.Outcome);

        string clay = Path.Combine(result.CellDir!, "layout", "artwork.clay");
        var back = LayoutPersistence.LoadFromFile(clay);
        Assert.Equal(preview.Layout.Shapes.Count, back.Shapes.Count);
        for (int i = 0; i < back.Shapes.Count; i++)
            Assert.Equal(Shape(preview.Layout.Shapes[i]), Shape(back.Shapes[i]));
        Assert.Equal(2, back.Shapes.OfType<LabelShape>().Count(l => l.IsPort));   // the ports, as port labels
        Assert.Contains(result.Written, w => w.EndsWith("stub.cnl"));

        static string Shape(LayoutShape s)
        {
            var one = new LayoutView();
            one.Shapes.Add(s);
            return LayoutPersistence.Serialize(one);
        }
    }

    [PythonFact]
    public async Task Gate4_AHandleDrag_SendsTheValueThePCellHandleSolverSolves()
    {
        string scratch = PluginTestKit.NewScratch();
        var (session, values, reply, ws) = await Stub(scratch);
        await using var _ = session;
        var preview = Artwork(session, values, reply, ws);
        var h = Assert.Single(preview.Handles);
        Assert.Equal("trim", h.Parameter);
        var trim = session.Manifest.Params.First(p => p.Name == "trim");
        double perUnit = PluginHandles.PerUnit(preview, h, trim)!.Value;
        Assert.Equal(1e9, perUnit);   // 1000 DBU per µm: a metre of trim moves the grip 1e9 DBU

        // Drag the grip 2 mm along its axis: the solver lands on a trim of 2 mm.
        double d = 2e-3 * perUnit, r = h.AxisDeg * Math.PI / 180;
        double? solved = PluginHandles.Solve(preview, h, perUnit, h.X + d * Math.Cos(r), h.Y + d * Math.Sin(r));
        Assert.NotNull(solved);
        Assert.Equal(2e-3, solved!.Value, 12);

        // The drag goes through PCellHandleSolver and nowhere else: no second solver in the plugin views.
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Plugins", "Views", "PluginLayoutViewControl.cs"));
        Assert.Contains("PCellHandleSolver.MeasureSensitivity(", src);
        Assert.Contains("PCellHandleSolver.Solve(", src);
        Assert.Single(Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Ui", "Plugins"), "*.cs", SearchOption.AllDirectories),
                      f => File.ReadAllText(f).Contains("PCellHandleSolver."));
    }

    // ── the other kinds ────────────────────────────────────────────────────────

    [PythonFact]
    public async Task TheStubsSchematicView_IsTheNetlistDrawnAsNetlistToSchematicDrawsIt()
    {
        string scratch = PluginTestKit.NewScratch();
        var (session, values, reply, ws) = await Stub(scratch);
        await using var _ = session;
        var body = new PluginViewBody("circuit", reply.Views["circuit"]!, default, values, 1) { StagingDir = session.StagingDirectory };
        var view = Assert.IsType<PluginSchematicView>(PluginViews.Read(body, "schematic", new PluginViewContext(session.Manifest, ws)));
        var direct = CircuitRF.Design.Schematic.NetlistSchematic.FromFile(view.CnlPath);
        Assert.Equal(CircuitRF.Design.Schematic.SchematicPersistence.Serialize(direct.Schematic!, "circuit"),
                     CircuitRF.Design.Schematic.SchematicPersistence.Serialize(view.Schematic, "circuit"));
    }

    [Fact]
    public void AViewNamingAFileOutsideItsStagingFolder_OrNotThere_IsNotDrawn()
    {
        string staging = PluginTestKit.NewScratch();
        var ctx = new PluginViewContext(Manifest, null);
        PluginView Read(string kind, JsonObject b) => PluginViews.Read(Body("v", b) with { StagingDir = staging }, kind, ctx);

        Assert.Contains("not in the plugin's staging folder", Read("image", new JsonObject { ["file"] = "missing.png" }).Problem);
        Assert.Contains("PNG or an SVG", Read("image", new JsonObject { ["file"] = "chart.gif" }).Problem);
        Assert.NotNull(Read("schematic", new JsonObject { ["cnl"] = "../../etc/x.cnl" }).Problem);

        File.WriteAllText(Path.Combine(staging, "chart.svg"), "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>");
        var image = Assert.IsType<PluginImageView>(Read("image", new JsonObject { ["file"] = "chart.svg", ["clickable"] = true }));
        Assert.True(image.Clickable);
    }

    [Fact]
    public void ALayoutLayerTheTechnologyLacks_IsDrawnOnTheFallbackPalette_AndNamedOnce()
    {
        string tech = Path.Combine(RepoRoot(), "examples", "Patch Antenna", "tech", "pcb-2layer_RO4350B_30mil_1oz.ctech");
        var payload = new long[] { 0, 0, 1000, 1000, 0, 0, 500, 500, 2000, 2000, 3000, 3000 };
        var body = new JsonObject
        {
            ["technology"] = Path.GetRelativePath(Path.Combine(RepoRoot(), "examples", "Patch Antenna"), tech),
            ["shapes"] = new JsonArray(
                new JsonObject { ["kind"] = "rect", ["layer"] = "Top Copper", ["xy"] = new JsonObject { ["at"] = 0, ["count"] = 4 } },
                new JsonObject { ["kind"] = "rect", ["layer"] = "Resistor Film", ["xy"] = new JsonObject { ["at"] = 4, ["count"] = 4 } },
                new JsonObject { ["kind"] = "rect", ["layer"] = "Resistor Film", ["xy"] = new JsonObject { ["at"] = 8, ["count"] = 4 } }),
        };
        var view = PluginViews.Read(new PluginViewBody("art", body, payload, new JsonObject(), 1), "layout",
                                    new PluginViewContext(Manifest, Path.Combine(RepoRoot(), "examples", "Patch Antenna")));
        var layout = Assert.IsType<PluginLayoutView>(view);
        Assert.Equal(3, layout.Layout.Shapes.Count);
        Assert.Equal(layout.Technology!.Layers.First(l => l.Name == "Top Copper").Key, layout.Layout.Shapes[0].Layer);
        Assert.Equal(PluginViews.UnknownLayerBase, layout.Layout.Shapes[1].Layer.Layer);
        Assert.Equal(layout.Layout.Shapes[1].Layer, layout.Layout.Shapes[2].Layer);
        Assert.Contains("Resistor Film", Assert.Single(layout.Notes));

        // A shape kind the wire does not know is refused by the codec, by name, never skipped.
        body["shapes"]!.AsArray().Add(new JsonObject { ["kind"] = "blob", ["layer"] = "Top Copper", ["xy"] = new JsonObject { ["at"] = 0, ["count"] = 2 } });
        var refused = PluginViews.Read(new PluginViewBody("art", body, payload, new JsonObject(), 2), "layout",
                                       new PluginViewContext(Manifest, Path.Combine(RepoRoot(), "examples", "Patch Antenna")));
        Assert.Contains("'blob'", refused.Problem);
    }

    [Fact]
    public void ATableView_IsTheReadoutFormattersTable_AndCopiesAsTabSeparatedText()
    {
        var table = PluginViews.Read(Body("dims", new JsonObject
        {
            ["columns"] = new JsonArray("Section", "L"), ["units"] = new JsonArray("", "metre"),
            ["rows"] = new JsonArray(new JsonArray("Stub", 0.0187)),
        }), "table", new PluginViewContext(Manifest, null));
        var t = Assert.IsType<PluginTableView>(table).Table;
        Assert.Equal(["Section", "L"], t.Columns!);
        Assert.Equal("18.7 mm", t.Rows![0][1]);
        Assert.Equal("Section\tL\nStub\t18.7 mm\n", PluginTableViewControl.TabSeparated(t));
    }
}
