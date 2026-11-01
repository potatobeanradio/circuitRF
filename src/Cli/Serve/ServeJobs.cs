using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Engine;

namespace CircuitRF.Cli.Serve;

/// <summary>
/// EMB-5 R-emb5-1 — how <c>serve</c> schedules a call, and the jobs a long call can be started as.
///
/// <para><b>Two lanes.</b> A tool that writes nothing a design depends on (<see cref="ReadOnlyTools"/>) runs on a small
/// pool, so a sub-second <c>check</c> of a <c>.cnl</c> is never queued behind a sixteen-minute one of a board's
/// <c>.cem</c>, nor behind a run. Everything else keeps the one serial worker it always had: two runs writing the same
/// result files, or two meshers each taking most of the machine's memory, are not made safe by scheduling.</para>
///
/// <para><b>A job is a call that returns before it is done.</b> <c>run … async=true</c> (and <c>check … async=true</c>)
/// answers at once with an id; <c>job action=status|cancel|result id=…</c> then reports its stage and progress, stops it,
/// and returns the result the synchronous call would have returned — the same object, built by the same code, stored
/// rather than sent. Jobs live as long as the server does and no longer (§3: nothing persists across a restart).</para>
/// </summary>
internal sealed class ServeJobs
{
    public const string ToolName = "job";

    /// <summary>The tools that run on the pool. Each writes nothing a design reads back — <c>render</c> and <c>plot</c>
    /// write the one picture they are asked for, at a path the caller chose — and reaches no state another call can see:
    /// <c>JsonRun</c>, <c>RunHost</c> and the per-invocation flags are per call (EMB-5's completion note lists what was
    /// checked).</summary>
    public static readonly IReadOnlySet<string> ReadOnlyTools =
        new HashSet<string>(StringComparer.Ordinal) { "find", "check", "explain", "read", "reference", "solver", "render", "plot" };

    /// <summary>The tools a client may start as a job (<see cref="ToolCatalog.AsyncArgument"/>).</summary>
    public static readonly IReadOnlySet<string> JobTools = new HashSet<string>(StringComparer.Ordinal) { "run", "check" };

    internal sealed class Job(string id, string tool, string? analysis)
    {
        public string Id { get; } = id;
        public string Tool { get; } = tool;
        public string? Analysis { get; } = analysis;
        public CancellationTokenSource Cts { get; } = new();
        public Stopwatch Clock { get; } = new();

        public string State = "queued";
        public RunProgress? Last;
        public string? Estimate;
        public JsonObject? Result;
        public int? ExitCode;

        public void Observe(RunProgress p) { lock (this) Last = p; }
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private int _next;

    public Job Create(string tool, string? analysis)
    {
        var job = new Job("job-" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture), tool, analysis);
        _jobs[job.Id] = job;
        return job;
    }

    public void Started(Job job) { lock (job) { job.State = "running"; job.Clock.Start(); } }

    public void Finished(Job job, JsonObject result, int exitCode)
    {
        lock (job)
        {
            job.Clock.Stop();
            job.Result   = result;
            job.ExitCode = exitCode;
            job.State    = exitCode == 130 || job.Cts.IsCancellationRequested ? "cancelled" : "done";
        }
    }

    /// <summary>A client gone: every job still going is stopped, as an in-flight call is.</summary>
    public void CancelAll()
    {
        foreach (var job in _jobs.Values) { try { job.Cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// <summary>The <c>job</c> tool's schema — advertised beside the catalog's, as <c>batch</c>'s is, because it is not
    /// a command line: a job is state this server holds.</summary>
    public static JsonObject Advertise() => new()
    {
        ["name"] = ToolName,
        ["description"] =
            "A run or check started with async=true. status: its state, stage, progress, elapsed time and an estimate of "
          + "the time left. cancel: stops it at its next work boundary. result: the same result the call would have "
          + "returned had it not been started as a job. Jobs last as long as this server.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["enum"]        = new JsonArray("status", "cancel", "result"),
                    ["description"] = "status, cancel or result.",
                },
                ["id"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["description"] = "The job id the async call returned. Omit with status to list every job.",
                },
            },
            ["required"] = new JsonArray("action"),
        },
    };

    /// <summary>
    /// One <c>job</c> call. Returns the tool result to send, which for <c>result</c> on a finished job is the stored
    /// result itself, unchanged; or null with <paramref name="refusal"/> set.
    /// </summary>
    public JsonObject? Invoke(JsonObject? arguments, out CircuitRF.Diagnostics.Diagnostic? refusal)
    {
        refusal = null;
        string? action = Text(arguments, "action");
        string? id     = Text(arguments, "id");

        if (action is not ("status" or "cancel" or "result"))
        {
            refusal = action is null
                ? CliDiagnostics.ServeArgumentRequired(ToolName, "action")
                : CliDiagnostics.ServeArgumentUnknownValue(ToolName, "action", action, "status, cancel, result");
            return null;
        }

        if (id is null)
        {
            if (action != "status") { refusal = CliDiagnostics.ServeArgumentRequired(ToolName, "id"); return null; }
            var all = new JsonArray();
            foreach (var j in _jobs.Values.OrderBy(j => j.Id, StringComparer.Ordinal)) all.Add(Status(j));
            return Envelope(new JsonObject { ["jobs"] = all });
        }

        if (!_jobs.TryGetValue(id, out var job))
        {
            refusal = CliDiagnostics.ServeJobUnknown(id, string.Join(", ", _jobs.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            return null;
        }

        switch (action)
        {
            case "cancel":
                try { job.Cts.Cancel(); } catch (ObjectDisposedException) { /* finished and released */ }
                return Envelope(new JsonObject { ["job"] = Status(job) });

            case "result":
                lock (job)
                {
                    if (job.Result is { } done) return (JsonObject)done.DeepClone();
                }
                refusal = CliDiagnostics.ServeJobNotFinished(id, StateOf(job));
                return null;

            default:
                return Envelope(new JsonObject { ["job"] = Status(job) });
        }
    }

    private static string StateOf(Job job) { lock (job) return job.State; }

    /// <summary>What a status reports: state, stage, the run's own counters, elapsed time and, where the run counts
    /// its work, the time left at the rate so far.</summary>
    internal static JsonObject Status(Job job)
    {
        lock (job)
        {
            double elapsed = job.Clock.Elapsed.TotalSeconds;
            var o = new JsonObject
            {
                ["id"]             = job.Id,
                ["tool"]           = job.Tool,
                ["state"]          = job.State,
                ["elapsedSeconds"] = Math.Round(elapsed, 1),
            };
            if (job.Analysis is { } a) o["analysis"] = a;
            if (job.Cts.IsCancellationRequested && job.State == "running") o["state"] = "cancelling";
            if (job.Last is { } p)
            {
                o["stage"] = McpServer.ProgressMessage(p);
                o["completed"] = p.Completed;
                if (p.Total > 0)
                {
                    double f = (double)p.Completed / p.Total;
                    o["total"]   = p.Total;
                    o["percent"] = Math.Round(100 * f, 1);
                    if (f > 0 && f < 1 && job.State == "running")
                        o["remainingSeconds"] = Math.Round(elapsed * (1 - f) / f, 0);
                }
            }
            if (job.Estimate is { } e) o["estimate"] = e;
            if (job.ExitCode is { } x) o["exitCode"] = x;
            o["next"] = job.State is "queued" or "running"
                ? $"job action=status id={job.Id}, or job action=cancel id={job.Id}"
                : $"job action=result id={job.Id}";
            return o;
        }
    }

    /// <summary>The answer to an async call: the job it started, as a document.</summary>
    public static JsonObject Accepted(Job job) => Envelope(new JsonObject { ["job"] = Status(job) });

    /// <summary>A job document in the shape every other document has (<c>cli.md</c> §7A): header, status, exit code,
    /// no outputs and no diagnostics, and the answer under <c>result</c>. The text block and the structured content
    /// are the same JSON, as every tool result's are.</summary>
    private static JsonObject Envelope(JsonObject payload)
    {
        var document = new JsonObject
        {
            ["circuitrf"]   = new JsonObject { ["version"] = JsonRun.Version(), ["verb"] = ToolName },
            ["input"]       = new JsonObject { ["path"] = null, ["analysis"] = null },
            ["status"]      = "ok",
            ["exitCode"]    = 0,
            ["outputs"]     = new JsonArray(),
            ["diagnostics"] = new JsonArray(),
            ["result"]      = payload,
        };
        return new JsonObject
        {
            ["content"]           = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = document.ToJsonString() }),
            ["isError"]           = false,
            ["structuredContent"] = document,
        };
    }

    private static string? Text(JsonObject? o, string key)
        => o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
