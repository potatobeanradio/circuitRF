// ================================================================
//  ServeLongWorkTests.cs — brief EMB-5 part A: the MCP server under long work.
//
//  The server is driven IN PROCESS over two anonymous pipe pairs, with its `Runner` seam — the function an
//  argument vector is handed to — replaced where a test needs a verb whose duration it controls. That is what
//  lets these gates assert ORDER ("the find was answered while the slow call was still blocked") with a latch
//  instead of a stopwatch: nothing here measures the machine. `initialize` is deliberately never sent, because
//  it sets the kit-trust globals other test classes read.
//
//  What each test pins, one claim each:
//    R-emb5-1  a slow call on either lane does not delay a concurrent `find`; a job's status/cancel/result
//              round trip; a job's result is the synchronous call's document.
//    R-emb5-2  `em --dry-run` carries the numbers `check` (planar) and `explain` (3D) report; a real em run's
//              first progress notification is the estimate.
//    R-emb5-3  each long verb, cancelled at its first stage observation, comes back as stopped (130) and the
//              lane it ran on answers the next call.
//    R-emb5-4  a progress message names the stage's own size and its live figure.
//    R-emb5-5  the root refusal names the root and a scratch folder that exists.
// ================================================================

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using CircuitRF.Cli.Serve;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Cli;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ServeLongWorkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emb5-" + Guid.NewGuid().ToString("N")[..10]);

    public ServeLongWorkTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(2);

    // ══ R-emb5-1 — lanes ═════════════════════════════════════════════════════════════════════════

    /// <summary>A call held open by a latch, on the serial worker (a run) or on the read-only pool (a check), does not
    /// hold up a <c>find</c> sent after it: the find is answered while the slow call is still blocked.</summary>
    [Theory]
    [InlineData("run")]
    [InlineData("check")]
    public void ASlowCall_DoesNotDelayAConcurrentFind(string slowTool)
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string slowVerb = slowTool == "run" ? "sparam" : "check";

        using var server = new InProcessServe(_root, argv =>
        {
            if (argv[0] != slowVerb) return CliEntry.Run(argv);
            started.Set();
            release.Wait(RunHost.Cancellation);
            return 0;
        });

        var slowArgs = slowTool == "run"
            ? new JsonObject { ["analysis"] = "sparam", ["path"] = "slow.cnl" }
            : new JsonObject { ["path"] = "slow.cnl" };
        int slow = server.Send("tools/call", new JsonObject { ["name"] = slowTool, ["arguments"] = slowArgs });
        Assert.True(started.Wait(Generous), "the slow call never started");

        int find = server.Send("tools/call", new JsonObject { ["name"] = "find", ["arguments"] = new JsonObject { ["path"] = "." } });
        string found = InProcessServe.DocumentOf(server.Await(find));

        Assert.Equal(0, JsonDocument.Parse(found).RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(server.Answered(slow), "the slow call finished before the find was answered — nothing was pinned");

        release.Set();
        server.Await(slow);
    }

    /// <summary>A job: <c>async=true</c> answers at once with an id; <c>status</c> reports the run's own stage and
    /// counters; <c>cancel</c> stops it at its next boundary; <c>result</c> is then the stopped call's document (130).
    /// And a job run to the end returns, as its result, the very document the synchronous call returns.</summary>
    [Fact]
    public void AJob_ReportsCancelsAndReturnsTheSynchronousResult()
    {
        string cnl = Attenuator("pad.cnl", points: 11);
        using var inStage = new ManualResetEventSlim();

        using var server = new InProcessServe(_root, argv =>
        {
            if (!argv.Contains(Path.Combine(_root, "slow.cnl"))) return CliEntry.Run(argv);
            var control = RunHost.Control!;
            control.BeginStage("a stage that never ends", 1000, "step(s)");
            control.TickStage();
            inStage.Set();
            while (true) { Thread.Sleep(5); control.TickStage(); }   // TickStage throws once cancelled
        });

        // Cancelled mid-stage.
        string slowId = JobId(server.Call("run", new JsonObject { ["analysis"] = "sparam", ["path"] = "slow.cnl", ["async"] = true }));
        Assert.True(inStage.Wait(Generous), "the job never started");
        var running = JobOf(server.Call(ServeJobs.ToolName, new JsonObject { ["action"] = "status", ["id"] = slowId }));
        Assert.Equal("running", running["state"]!.GetValue<string>());
        Assert.StartsWith("a stage that never ends", running["stage"]!.GetValue<string>());
        Assert.Contains("of 1,000 step(s)", running["stage"]!.GetValue<string>());

        server.Call(ServeJobs.ToolName, new JsonObject { ["action"] = "cancel", ["id"] = slowId });
        var stopped = WaitFor(server, slowId, "cancelled");
        Assert.Equal(130, stopped["exitCode"]!.GetValue<int>());
        var stoppedResult = JsonNode.Parse(server.CallRaw(ServeJobs.ToolName, new JsonObject { ["action"] = "result", ["id"] = slowId }))!;
        Assert.Equal(130, JsonDocument.Parse(InProcessServe.TextOf(stoppedResult)).RootElement.GetProperty("exitCode").GetInt32());

        // Run to the end: the stored result is the synchronous call's result.
        string synchronous = InProcessServe.TextOf(JsonNode.Parse(server.CallRaw("run", new JsonObject { ["analysis"] = "sparam", ["path"] = cnl }))!);
        string jobId = JobId(server.Call("run", new JsonObject { ["analysis"] = "sparam", ["path"] = cnl, ["async"] = true }));
        WaitFor(server, jobId, "done");
        string viaJob = InProcessServe.TextOf(JsonNode.Parse(server.CallRaw(ServeJobs.ToolName, new JsonObject { ["action"] = "result", ["id"] = jobId }))!);

        Assert.Equal(0, JsonDocument.Parse(synchronous).RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(synchronous, viaJob);
    }

    // ══ R-emb5-2 — the estimate, from the functions check and explain use ════════════════════════

    /// <summary>A planar setup: the dry run's mesh figures are check's, field for field, it carries the estimate line,
    /// and it wrote nothing.</summary>
    [Fact]
    public void ADryRun_OfAPlanarSetup_CarriesTheNumbersCheckReports()
    {
        string cem = PlanarFixture(points: 21);
        using var server = new InProcessServe(_root);

        var dry   = JsonNode.Parse(server.Call("run", new JsonObject { ["analysis"] = "em", ["path"] = cem, ["dryRun"] = true }))!;
        var check = JsonNode.Parse(server.Call("check", new JsonObject { ["path"] = cem }))!;

        var dryEm   = EmBlock(dry);
        var checkEm = EmBlock(check);
        Assert.Equal(checkEm.ToJsonString(), dryEm.ToJsonString());
        Assert.True(dryEm["unknowns"]!.GetValue<int>() > 0);
        Assert.Equal(21, dryEm["points"]!.GetValue<int>());
        Assert.NotNull(dryEm["memoryBytes"]);
        Assert.NotNull(dryEm["factorSecondsTotal"]);

        string estimate = Message(dry, "em.dry-run.estimate");
        Assert.Contains($"{dryEm["unknowns"]!.GetValue<int>():N0} unknowns", estimate);
        Assert.Empty(dry["outputs"]!.AsArray());
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(cem)!, "results")), "a dry run wrote results");
    }

    /// <summary>A 3D setup: the dry run's size rows are explain's, byte for byte, and the estimate line quotes
    /// Palace's own sentence. No mesher runs (none is needed to get it).</summary>
    [Fact]
    public void ADryRun_OfA3DSetup_CarriesTheNumbersExplainPrints()
    {
        string c3d = ThreeDFixture();
        using var server = new InProcessServe(_root);

        var dry     = JsonNode.Parse(server.Call("run", new JsonObject { ["analysis"] = "em", ["path"] = c3d, ["dryRun"] = true }))!;
        var explain = JsonNode.Parse(server.Call("explain", new JsonObject { ["path"] = c3d }))!;

        var drySize     = dry["result"]!["explain"]!["em3d"]!["size"]!;
        var explainSize = explain["result"]!["explain"]!["em3d"]!["size"]!;
        Assert.Equal(explainSize.ToJsonString(), drySize.ToJsonString());

        // The estimate restates the rows of the setup's OWN solver only (an EMerge setup quoting Palace's figures named a run
        // no one would see), so the row compared is that solver's.
        string solver = explain["result"]!["explain"]!["em3d"]!["solver"]!.GetValue<string>();
        var own = drySize.AsArray().First(r => solver.Contains(r!["backend"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))!;
        string backend = own["backend"]!.GetValue<string>(), note = own["note"]!.GetValue<string>();
        Assert.Contains(backend + ": " + note.TrimEnd('.'), Message(dry, "em.dry-run.estimate"));
    }

    /// <summary>A real em run with a progress token: its FIRST progress notification is the estimate, before any
    /// stage of the run. The run is then cancelled — the claim is about what comes first, not about a solve.</summary>
    [Fact]
    public void AnEmRun_StatesItsEstimateFirst()
    {
        string cem = PlanarFixture(points: 3);
        using var server = new InProcessServe(_root);

        int id = server.Send("tools/call", new JsonObject
        {
            ["name"] = "run",
            ["arguments"] = new JsonObject { ["analysis"] = "em", ["path"] = cem },
            ["_meta"] = new JsonObject { ["progressToken"] = "p" },
        });
        for (int i = 0; i < 12_000 && server.Notifications("notifications/progress").Count == 0 && !server.Answered(id); i++)
            Thread.Sleep(10);
        server.Notify("notifications/cancelled", new JsonObject { ["requestId"] = id });
        server.Await(id);

        var first = server.Notifications("notifications/progress").First();
        Assert.StartsWith("estimate: ", first["params"]!["message"]!.GetValue<string>());
        Assert.Contains("unknowns", first["params"]!["message"]!.GetValue<string>());
    }

    // ══ R-emb5-3 — every long verb stops at its next boundary ════════════════════════════════════

    /// <summary>
    /// Each long verb, cancelled at the first progress observation it makes — that is, inside its first stage — is
    /// answered as stopped (130, or <c>serve.tool.cancelled</c>), and the lane it ran on then answers a call of its
    /// own: the worker is free. The cancellation is made by the observer itself, synchronously, so which stage it lands
    /// in is decided by the verb and not by a race.
    /// </summary>
    [Theory]
    [InlineData("check-cem")]
    [InlineData("check-c3d")]
    [InlineData("explain-c3d")]
    [InlineData("run-em")]
    public void ALongVerb_CancelledMidStage_StopsAndFreesItsLane(string which)
    {
        int observed = 0;
        using var server = new InProcessServe(_root, argv =>
        {
            var outer = RunHost.Observer;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(RunHost.Cancellation);
            using var _ = RunHost.Install(cts.Token, p =>
            {
                outer?.Invoke(p);
                if (Interlocked.Increment(ref observed) == 1) cts.Cancel();
            });
            return CliEntry.Run(argv);
        });

        var (tool, arguments) = which switch
        {
            "check-cem"   => ("check", new JsonObject { ["path"] = PlanarFixture(points: 3) }),
            "check-c3d"   => ("check", new JsonObject { ["path"] = ThreeDFixture() }),
            "explain-c3d" => ("explain", new JsonObject { ["path"] = ThreeDFixture() }),
            // The planar kernel: its first observation is a stage start, which is always delivered and comes before any
            // solve. The cross-section kernel reports only throttled ticks, and once the code is JIT-compiled a 400-point
            // run fits inside one throttle window, so its first observation is the final tick — after the last check.
            // The S-parameter engine is the cross-section kernel's case: throttled ticks only, and a warm process (late in a
            // full run) finished a 20,001-point attenuator inside one throttle window, observing nothing. Not a case here.
            "run-em"      => ("run", new JsonObject { ["analysis"] = "em", ["path"] = PlanarFixture(points: 3) }),
            _             => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        var stopped = JsonDocument.Parse(server.Call(tool, arguments)).RootElement;
        Assert.True(observed >= 1, $"{which} reported no progress, so nothing could cancel it mid-stage");
        Assert.Equal(130, stopped.GetProperty("exitCode").GetInt32());
        Assert.Empty(stopped.GetProperty("outputs").EnumerateArray());

        // The same lane, free: a second call of the same tool is answered (the observer only ever cancels once).
        var next = tool == "run"
            ? new JsonObject { ["analysis"] = "sparam", ["path"] = Attenuator("after.cnl", points: 3) }
            : new JsonObject { ["path"] = Attenuator("after.cnl", points: 3) };
        string answered = server.Call(tool, next);
        Assert.NotEqual(130, JsonDocument.Parse(answered).RootElement.GetProperty("exitCode").GetInt32());
    }

    // ══ R-emb5-4 — the stage's own size ═════════════════════════════════════════════════════════

    [Fact]
    public void AProgressMessage_NamesTheStagesOwnSizeAndItsLiveFigure()
    {
        string s = McpServer.ProgressMessage(new RunProgress(
            "solving the structure", 3, 10, 9_731, 23_734, "column(s)", "factoring the near field: 9,731 / 23,734 columns"));
        Assert.Equal("solving the structure — 9,731 of 23,734 column(s); factoring the near field: 9,731 / 23,734 columns", s);
    }

    // ══ R-emb5-5 — where to write instead ════════════════════════════════════════════════════════

    [Fact]
    public void TheRootRefusal_NamesTheRootAndAScratchFolderThatExists()
    {
        using var server = new InProcessServe(_root);
        var doc = JsonNode.Parse(server.Call("read", new JsonObject { ["path"] = "../elsewhere/x.s2p" }))!;

        string message = Message(doc, "serve.path.outside-root");
        string scratch = Path.Combine(Path.GetFullPath(_root), PathRoot.ScratchFolderName);
        Assert.Contains(Path.GetFullPath(_root), message);
        Assert.Contains(scratch, message);
        Assert.True(Directory.Exists(scratch));
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════════

    private string Attenuator(string name, int points)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path,
            "Port:P1 in  0 Num=1 Z=50 Ohm\n" +
            "Port:P2 out 0 Num=2 Z=50 Ohm\n" +
            "R:R1 in  mid R=96 Ohm\n" +
            "R:R2 mid 0   R=71.2 Ohm\n" +
            "R:R3 mid out R=96 Ohm\n" +
            $"analysis SP1 type=sparam start=1 stop=6 npts={points} Unit=GHz\n");
        return path;
    }

    /// <summary>A 20 mm line on the two-layer starter board with a port at each end — ServeProtocolAdapterTests' own
    /// fixture, in a folder of its own per call so two fixtures never share a results folder.</summary>
    private string PlanarFixture(int points, bool planar = true)
    {
        string root = Path.Combine(_root, "em-" + Guid.NewGuid().ToString("N")[..6]);
        string cellLayoutDir = Path.Combine(root, "Line", "layout");
        Directory.CreateDirectory(cellLayoutDir);

        TechPersistence.SaveToFile(Path.Combine(root, "pcb.ctech"), StarterTechnologies.Pcb2Layer());
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        view.Shapes.Add(new LabelShape { Layer = new(1, 0), X = 0,          Y = 1_450_000, Text = "1", Height = 500_000, IsPort = true });
        view.Shapes.Add(new LabelShape { Layer = new(1, 0), X = 20_000_000, Y = 1_450_000, Text = "2", Height = 500_000, IsPort = true });
        LayoutPersistence.SaveToFile(Path.Combine(cellLayoutDir, "Line.clay"), view);

        string cem = Path.Combine(root, "line.cem");
        EmSetupPersistence.SaveToFile(cem, new EmSetup
        {
            Name      = "line",
            LayoutRef = Path.Combine("Line", "layout", "Line.clay"),
            Frequency = new FrequencySpec("1", "10", points, SweepKind.Linear, "GHz", "GHz"),
            // The planar kernel, so check meshes it and the estimate has a mesh to state; the cross-section kernel (what
            // Auto picks for a plain line) has none and is a thousand times cheaper.
            AnalysisKind = planar ? EmAnalysisKind.Planar : EmAnalysisKind.Auto,
        });
        return cem;
    }

    /// <summary>The shipped "3D Wave Ports" example workspace, copied under the root: its Pair view embeds one driven
    /// setup on two strips with two wave ports.</summary>
    private string ThreeDFixture()
    {
        string source = Path.Combine(RepoRoot(), "examples", "3D Wave Ports");
        string dest = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return Path.Combine(dest, "Pair", "3d", "Pair.c3d");
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    // ══ reading documents ════════════════════════════════════════════════════════════════════════

    private static JsonNode EmBlock(JsonNode document) =>
        document["result"]!["check"]!["documents"]!.AsArray().First(d => d!["em"] is not null)!["em"]!;

    private static string Message(JsonNode document, string id) =>
        document["diagnostics"]!.AsArray().First(d => d!["id"]!.GetValue<string>() == id)!["message"]!.GetValue<string>();

    private static string JobId(string document) => JobOf(document)["id"]!.GetValue<string>();

    private static JsonNode JobOf(string document) => JsonNode.Parse(document)!["result"]!["job"]!;

    /// <summary>Polls a job until it reaches <paramref name="state"/> — bounded by a generous count, never a clock
    /// assertion.</summary>
    private static JsonNode WaitFor(InProcessServe server, string id, string state)
    {
        for (int i = 0; i < 12_000; i++)
        {
            var job = JobOf(server.Call(ServeJobs.ToolName, new JsonObject { ["action"] = "status", ["id"] = id }));
            if (job["state"]!.GetValue<string>() == state) return job;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException($"job {id} never reached '{state}'");
    }
}

/// <summary>
/// The MCP server in this process, over two anonymous pipe pairs. Every frame it writes is collected; a request is
/// answered by the first frame carrying its id.
/// </summary>
internal sealed class InProcessServe : IDisposable
{
    private readonly AnonymousPipeServerStream _toServer   = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromServer = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _serverIn;
    private readonly AnonymousPipeClientStream _serverOut;
    private readonly StreamWriter _writer;
    private readonly Thread _serve, _pump;
    private readonly List<JsonObject> _frames = [];
    private readonly ConcurrentDictionary<string, JsonObject> _answers = new();
    private int _nextId;

    public InProcessServe(string root, Func<string[], int>? runner = null)
    {
        _serverIn  = new AnonymousPipeClientStream(PipeDirection.In,  _toServer.ClientSafePipeHandle);
        _serverOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle);
        _writer = new StreamWriter(_toServer, new UTF8Encoding(false)) { AutoFlush = true };

        var rpc  = new JsonRpc(_serverIn, _serverOut);
        var path = PathRoot.Open(root, out _)!;
        var server = runner is null
            ? new McpServer(rpc, path, new InstallationGuard(null))
            : new McpServer(rpc, path, new InstallationGuard(null)) { Runner = runner };

        _serve = new Thread(() => { server.Serve(); rpc.Dispose(); _serverOut.Dispose(); }) { IsBackground = true, Name = "test-serve" };
        _serve.Start();

        _pump = new Thread(() =>
        {
            using var reader = new StreamReader(_fromServer, new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (JsonNode.Parse(line) is not JsonObject frame) continue;
                lock (_frames)
                {
                    _frames.Add(frame);
                    if (frame["id"] is JsonValue id && frame["method"] is null) _answers[id.ToJsonString()] = frame;
                    Monitor.PulseAll(_frames);
                }
            }
            lock (_frames) Monitor.PulseAll(_frames);
        }) { IsBackground = true, Name = "test-serve-pump" };
        _pump.Start();
    }

    public int Send(string method, JsonObject parameters)
    {
        int id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
        lock (_writer) _writer.WriteLine(message.ToJsonString());
        return id;
    }

    public void Notify(string method, JsonObject parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        lock (_writer) _writer.WriteLine(message.ToJsonString());
    }

    public bool Answered(int id) => _answers.ContainsKey(id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public JsonObject Await(int id)
    {
        string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        lock (_frames)
        {
            while (!_answers.ContainsKey(key))
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new Xunit.Sdk.XunitException($"no answer to request {id}");
                Monitor.Wait(_frames, left);
            }
        }
        return _answers[key];
    }

    /// <summary>A tool call's whole result object, as JSON text.</summary>
    public string CallRaw(string tool, JsonObject arguments)
        => Await(Send("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }))["result"]!.ToJsonString();

    /// <summary>A tool call's document.</summary>
    public string Call(string tool, JsonObject arguments) => TextOf(JsonNode.Parse(CallRaw(tool, arguments))!);

    public IReadOnlyList<JsonObject> Notifications(string method)
    {
        lock (_frames) return [.. _frames.Where(f => f["method"]?.GetValue<string>() == method)];
    }

    public static string TextOf(JsonNode result) => result["content"]![0]!["text"]!.GetValue<string>();

    public static string DocumentOf(JsonObject frame) => TextOf(frame["result"]!);

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { /* gone */ }
        _serve.Join(TimeSpan.FromSeconds(60));
        _pump.Join(TimeSpan.FromSeconds(10));
        _serverIn.Dispose();
        _toServer.Dispose();
        _fromServer.Dispose();
    }
}
