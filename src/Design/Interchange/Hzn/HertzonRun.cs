// HZN-8 R-hzn8-2 — running a .hzn design in Hertzon through its command line, and reading the answer back.
//
// `hertzon-cli describe` says what can be chosen (designs, setups, sweeps, ports, variables); `hertzon-cli solve` solves the
// FILE (never circuitRF's memory) and writes a Touchstone. Hertzon keeps its own solution in <project>.hznresults/ beside the
// project; circuitRF writes nothing there and reads only the Touchstone that -o names.
//
// This file owns the process: arguments, the stderr event stream, the exit-code sentences, cancel and a quiet-too-long notice.
// It decides nothing about the design, and the GUI and `em --solver hertzon` both call it.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

// ── describe ─────────────────────────────────────────────────────────────────────────────────

public sealed record HertzonPort(int Number, string Name, string Kind, int Modes, double? Z0Ohm);

/// <summary>One variable a run may override. <see cref="Name"/> keeps Hertzon's spelling (a project variable starts with <c>$</c>).</summary>
public sealed record HertzonVariable(string Name, string Expression, string Scope, double? Value, string Unit);

public sealed record HertzonSweep(string Name, bool Enabled, string Type, double? StartHz, double? StopHz, int? Points);

public sealed record HertzonSetup(string Name, bool Enabled, IReadOnlyList<HertzonSweep> Sweeps);

public sealed record HertzonDesign(string Name, string Kind, string Units, IReadOnlyList<HertzonPort> Ports,
                                   IReadOnlyList<HertzonVariable> Variables, IReadOnlyList<HertzonSetup> Setups)
{
    public bool IsThreeD => Kind == "3d";

    /// <summary>Rows of the Touchstone: a wave port with several modes has one row per mode.</summary>
    public int Rows => Ports.Sum(p => Math.Max(1, p.Modes));
}

public sealed record HertzonDescription(string Version, IReadOnlyList<HertzonDesign> Designs, string? Error)
{
    public bool Ok => Error is null;
}

// ── solve ────────────────────────────────────────────────────────────────────────────────────

/// <summary>What one solve is asked. Setup and sweep default to Hertzon's own (the first enabled).</summary>
public sealed record HertzonRunRequest(string Project, string Design, string? Setup, string? Sweep,
                                       IReadOnlyList<KeyValuePair<string, string>> Sets, string Output)
{
    /// <summary>Hertzon's model check before the solve, which is what makes exit code 4 mean something. On by default.</summary>
    public bool Validate { get; init; } = true;
}

public enum HertzonEventKind { Progress, Pass, Log, Quiet }

/// <summary>One line of the progress stream, or a notice that none has come for a while.</summary>
/// <param name="Percent">Progress and Pass: how far; null for the rest.</param>
/// <param name="QuietSeconds">Quiet: how long nothing has been printed.</param>
public sealed record HertzonEvent(HertzonEventKind Kind, string Message, double? Percent = null, int? Pass = null,
                                  long? Tetrahedra = null, double? DeltaS = null, string? Level = null, double? QuietSeconds = null);

public enum HertzonOutcome { Done, NotConverged, Cancelled, SolverFailed, BadInput, ModelCheckFailed, Failed }

/// <param name="Sentence">What to tell the person; null when the outcome is Done.</param>
/// <param name="TouchstonePath">The file written; set for Done and NotConverged.</param>
/// <param name="Summary">Hertzon's own <c>--json</c> object, when it printed one.</param>
public sealed record HertzonRunResult(HertzonOutcome Outcome, int ExitCode, string? Sentence, string? TouchstonePath,
                                      bool? Converged, int? Passes, long? Tetrahedra, double? Seconds, bool Cached,
                                      string? ResultsFolder, JsonObject? Summary, string Version, string? Setup, string? Solution)
{
    /// <summary>The Touchstone exists and is to be read.</summary>
    public bool HasResult => Outcome is HertzonOutcome.Done or HertzonOutcome.NotConverged && TouchstonePath is not null;
}

public static class HertzonRun
{
    /// <summary>How long Hertzon may print nothing before circuitRF says so (it may still be solving: one frequency of a
    /// large design takes minutes). Settable for tests.</summary>
    public static TimeSpan QuietAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a cancelled run is given to stop on its own interrupt before it is killed. Settable for tests.</summary>
    public static TimeSpan InterruptGrace { get; set; } = TimeSpan.FromSeconds(8);

    // ── describe ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs <c>describe</c> on the whole project.</summary>
    public static HertzonDescription Describe(HertzonInstallation cli, string project)
    {
        var info = Start(cli, "describe", Path.GetFullPath(project));
        try
        {
            using var p = Em3dProcessLauncher.Start(info, Em3dProcessKind.Probe);
            if (p is null) return new HertzonDescription(cli.Version, [], "hertzon-cli did not start.");
            var err = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return new HertzonDescription(cli.Version, [], "hertzon-cli describe did not answer in 60 s.");
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
                return new HertzonDescription(cli.Version, [], "Hertzon could not describe the project: " + Message(err.GetAwaiter().GetResult(), p.ExitCode));
            return Parse(cli.Version, outText.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new HertzonDescription(cli.Version, [], "hertzon-cli could not be started: " + ex.Message);
        }
    }

    internal static HertzonDescription Parse(string version, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var designs = new List<HertzonDesign>();
            if (doc.RootElement.TryGetProperty("designs", out var ds) && ds.ValueKind == JsonValueKind.Array)
                foreach (var d in ds.EnumerateArray())
                {
                    var ports = Items(d, "ports").Select(p => new HertzonPort(Int(p, "number") ?? 0, Str(p, "name") ?? "", Str(p, "kind") ?? "",
                                                                              Int(p, "modes") ?? 1, Num(p, "z0_ohm"))).ToList();
                    var vars = Items(d, "variables").Select(v => new HertzonVariable(Str(v, "name") ?? "", Str(v, "expression") ?? "",
                                                                                      Str(v, "scope") ?? "design", Num(v, "value"), Str(v, "unit") ?? "")).ToList();
                    var setups = Items(d, "setups").Select(s => new HertzonSetup(Str(s, "name") ?? "", Bool(s, "enabled") ?? true,
                        [.. Items(s, "sweeps").Select(w => new HertzonSweep(Str(w, "name") ?? "", Bool(w, "enabled") ?? true, Str(w, "type") ?? "",
                                                                              Num(w, "start_hz"), Num(w, "stop_hz"), Int(w, "points")))])).ToList();
                    designs.Add(new HertzonDesign(Str(d, "name") ?? "", Str(d, "kind") ?? "3d", Str(d, "units") ?? "", ports, vars, setups));
                }
            return new HertzonDescription(version, designs, null);
        }
        catch (JsonException ex) { return new HertzonDescription(version, [], "Hertzon's description could not be read: " + ex.Message); }
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string key)
        => e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];
    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static int? Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    private static bool? Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    // ── solve ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The argument list a request becomes: `solve file --design D [--setup S] [--sweep W] [--set k=v …] -o out
    /// --progress json --json`, plus the two switches that give exit codes 4 and 5 their meaning.</summary>
    public static IReadOnlyList<string> Arguments(HertzonRunRequest r)
    {
        var a = new List<string> { "solve", Path.GetFullPath(r.Project), "--design", r.Design };
        if (r.Setup is { Length: > 0 } s) { a.Add("--setup"); a.Add(s); }
        if (r.Sweep is { Length: > 0 } w) { a.Add("--sweep"); a.Add(w); }
        foreach (var kv in r.Sets) { a.Add("--set"); a.Add(kv.Key + "=" + kv.Value); }
        a.Add("-o"); a.Add(Path.GetFullPath(r.Output));
        a.Add("--progress"); a.Add("json");
        a.Add("--json");
        if (r.Validate) a.Add("--validate");
        a.Add("--require-convergence");
        return a;
    }

    private static ProcessStartInfo Start(HertzonInstallation cli, params string[] args)
    {
        var info = new ProcessStartInfo(cli.Path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        return info;
    }

    /// <summary>Solves. Never throws: every way it can end is a <see cref="HertzonRunResult"/>. Cancelling interrupts the
    /// process (the interrupt Hertzon's own Ctrl+C is) and kills it when it has not stopped within <see cref="InterruptGrace"/>.</summary>
    public static HertzonRunResult Solve(HertzonInstallation cli, HertzonRunRequest request, Action<HertzonEvent>? onEvent = null,
                                         CancellationToken cancel = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Output))!);
            var info = Start(cli, [.. Arguments(request)]);
            using var p = Em3dProcessLauncher.Start(info, Em3dProcessKind.Solver);
            if (p is null) return Failed(cli, "hertzon-cli did not start.");

            var errors = new List<string>();           // error events' messages, in order
            string? doneTouchstone = null;
            long lastLine = Stopwatch.GetTimestamp();
            int quietNotices = 0;
            var sync = new object();
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastLine, Stopwatch.GetTimestamp());
                Volatile.Write(ref quietNotices, 0);
                var ev = ParseEvent(e.Data, errors, ref doneTouchstone, sync);
                if (ev is not null) onEvent?.Invoke(ev);
            };
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            p.BeginErrorReadLine();

            bool interrupted = false;
            while (!p.WaitForExit(250))
            {
                if (cancel.IsCancellationRequested && !interrupted)
                {
                    interrupted = true;
                    Interrupt(p);
                }
                if (interrupted && !p.WaitForExit(InterruptGrace)) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } break; }
                double quiet = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastLine)).TotalSeconds;
                int notices = Volatile.Read(ref quietNotices);
                if (onEvent is not null && !interrupted && quiet >= QuietAfter.TotalSeconds * (notices + 1))
                {
                    Volatile.Write(ref quietNotices, notices + 1);
                    onEvent(new HertzonEvent(HertzonEventKind.Quiet,
                        $"Hertzon has printed nothing for {(quiet / 60.0).ToString("0.#", CultureInfo.InvariantCulture)} min. It may still be solving.",
                        QuietSeconds: quiet));
                }
            }
            p.WaitForExit();
            string stdout = stdoutTask.GetAwaiter().GetResult();
            JsonObject? summary = null;
            try { summary = JsonNode.Parse(stdout) as JsonObject; } catch (JsonException) { }

            string? lastError;
            lock (sync) lastError = errors.LastOrDefault();
            int code = cancel.IsCancellationRequested ? 130 : p.ExitCode;
            return Finish(cli, code, summary, lastError, request, doneTouchstone);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(cli, "hertzon-cli could not be run: " + ex.Message);
        }
    }

    private static HertzonRunResult Failed(HertzonInstallation cli, string sentence)
        => new(HertzonOutcome.Failed, -1, sentence, null, null, null, null, null, false, null, null, cli.Version, null, null);

    private static HertzonOutcome Outcome(int code) => code switch
    {
        0 => HertzonOutcome.Done,
        1 => HertzonOutcome.SolverFailed,
        3 => HertzonOutcome.BadInput,
        4 => HertzonOutcome.ModelCheckFailed,
        5 => HertzonOutcome.NotConverged,
        130 => HertzonOutcome.Cancelled,
        _ => HertzonOutcome.Failed,
    };

    private static void Interrupt(Process p)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { p.Kill(entireProcessTree: true); return; }
            // The interrupt Ctrl+C sends, so Hertzon stops its solver and exits 130 rather than being cut off mid-write.
            using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-INT", p.Id.ToString(CultureInfo.InvariantCulture) }, UseShellExecute = false, CreateNoWindow = true });
            kill?.WaitForExit(2000);
        }
        catch (Exception) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }
    }

    /// <summary>One stderr line as an event; error events' messages are recorded, and `done`'s Touchstone remembered.</summary>
    internal static HertzonEvent? ParseEvent(string line, List<string> errors, ref string? doneTouchstone, object sync)
    {
        if (line.Length == 0 || line[0] != '{') return new HertzonEvent(HertzonEventKind.Log, line, Level: "info");
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            string kind = Str(e, "event") ?? "";
            switch (kind)
            {
                case "progress":
                    return new HertzonEvent(HertzonEventKind.Progress, Str(e, "message") ?? "", Percent: Num(e, "percent"));
                case "pass":
                    return new HertzonEvent(HertzonEventKind.Pass, $"Adaptive pass {Int(e, "pass")}", Pass: Int(e, "pass"),
                                            Tetrahedra: e.TryGetProperty("tets", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null,
                                            DeltaS: Num(e, "delta_s"));
                case "log":
                    return new HertzonEvent(HertzonEventKind.Log, Str(e, "message") ?? "", Level: Str(e, "level") ?? "info");
                case "done":
                    lock (sync) doneTouchstone = Str(e, "touchstone");
                    return new HertzonEvent(HertzonEventKind.Progress, "Solution complete", Percent: 100);
                case "error":
                {
                    string m = Str(e, "message") ?? "";
                    lock (sync) errors.Add(m);
                    return new HertzonEvent(HertzonEventKind.Log, m, Level: "error");
                }
                default:
                    return new HertzonEvent(HertzonEventKind.Log, line, Level: "info");
            }
        }
        catch (JsonException) { return new HertzonEvent(HertzonEventKind.Log, line, Level: "info"); }
    }

    private static HertzonRunResult Finish(HertzonInstallation cli, int code, JsonObject? summary, string? errorMessage,
                                           HertzonRunRequest request, string? doneTouchstone)
    {
        string? touchstone = (summary?["touchstone"] as JsonValue)?.GetValue<string>() ?? doneTouchstone;
        bool exists = touchstone is not null && File.Exists(touchstone);
        bool? converged = (summary?["converged"] as JsonValue) is { } cv && cv.TryGetValue<bool>(out bool cb) ? cb : null;
        int? passes = (summary?["passes"] as JsonValue) is { } pv && pv.TryGetValue<int>(out int pi) ? pi : null;
        long? tets = (summary?["tetrahedra"] as JsonValue) is { } tv && tv.TryGetValue<long>(out long tl) ? tl : null;
        double? seconds = (summary?["seconds"] as JsonValue) is { } sv && sv.TryGetValue<double>(out double sd) ? sd : null;
        bool cached = (summary?["cached"] as JsonValue) is { } kv && kv.TryGetValue<bool>(out bool kb) && kb;
        string? results = (summary?["results"] as JsonValue)?.GetValue<string>();
        string? setup = (summary?["setup"] as JsonValue)?.GetValue<string>();
        string? solution = (summary?["solution"] as JsonValue)?.GetValue<string>();

        var outcome = Outcome(code);
        // Exit 0 or 5 with no file is not a result; exit 0 with converged:false is the not-converged result.
        if (outcome is HertzonOutcome.Done or HertzonOutcome.NotConverged && !exists) outcome = HertzonOutcome.Failed;
        if (outcome == HertzonOutcome.Done && converged == false) outcome = HertzonOutcome.NotConverged;

        string? sentence = Sentence(outcome, code, errorMessage, results ?? ResultsFolderGuess(request));
        if (outcome == HertzonOutcome.Failed && code is 0 or 5)
            sentence = $"Hertzon finished (exit code {code}) but wrote no Touchstone file at {touchstone ?? request.Output}.";
        return new HertzonRunResult(outcome, code, sentence, exists ? touchstone : null, converged, passes, tets, seconds, cached, results, summary,
                                    cli.Version, setup, solution);
    }

    private static string ResultsFolderGuess(HertzonRunRequest r)
        => Path.Combine(Path.GetFullPath(r.Project) is { } f ? Path.ChangeExtension(f, ".hznresults") : "", r.Design);

    /// <summary>The sentence for each way a run ends (exit codes 1, 3, 4 and 5 are Hertzon's documented ones).</summary>
    internal static string? Sentence(HertzonOutcome outcome, int code, string? message, string resultsFolder)
    {
        string said = string.IsNullOrWhiteSpace(message) ? "" : " Hertzon said: " + message.Trim();
        return outcome switch
        {
            HertzonOutcome.Done => null,
            HertzonOutcome.Cancelled => "The Hertzon run was cancelled; no result was read.",
            HertzonOutcome.SolverFailed => $"Hertzon's solver failed. Its solver.log is in the result folder under {resultsFolder}.{said}",
            HertzonOutcome.BadInput => $"Hertzon rejected the input.{said}",
            HertzonOutcome.ModelCheckFailed => $"Hertzon's model check failed.{said}",
            HertzonOutcome.NotConverged => "Hertzon's adaptive meshing did not converge. The Touchstone it wrote is read, and the run is marked not converged.",
            _ => code == 2 ? $"circuitRF called hertzon-cli with arguments it refused (exit code 2).{said}"
               : $"hertzon-cli ended with exit code {code}.{said}",
        };
    }

    private static string Message(string stderr, int code)
    {
        foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (t[0] == '{')
            {
                try { using var d = JsonDocument.Parse(t); if (Str(d.RootElement, "message") is { } m) return m; } catch (JsonException) { }
            }
            return t;
        }
        return $"exit code {code}.";
    }
}
