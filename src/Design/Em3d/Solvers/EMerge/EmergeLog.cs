// brief-emi-5 R-emi5-2 — progress from EMerge's own log, the way PalaceLogProgress reads Palace's.
//
// Two pieces, kept apart so each is tested alone:
//
//   EmergeLog            one line in, zero or one event out. No process, no file, no clock: the gate feeds it a canned log.
//                        Every pattern is anchored on what EMerge 3.0.0b2 prints (src/Design/RESOLVED.md, EMI-5, lists the
//                        lines), and on the driver's own JSON lines.
//   EmergeStageTracker   events -> RunControl: the stage a user reads, and the run's facts from the same events.
//
// THE RULE, as Palace's: every figure shown is one EMerge printed, and a fraction is shown only where EMerge stated a total
// (a plain sweep's job count). The adaptive sweep has no total: it shows its estimated error against its tolerance. A line
// that is not recognised is ignored; a run whose log says nothing recognisable still runs and still reads its results.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d;

/// <summary>One thing EMerge's log, or circuitRF's driver, said.</summary>
public abstract record EmergeLogEvent;

/// <summary><c>Calling GMSH mesher</c>: meshing begins.</summary>
public sealed record EmergeMeshing : EmergeLogEvent;

/// <summary><c>GMSH Meshing complete! (◮N, ▲M)</c>: the tetrahedra and triangles of the mesh.</summary>
public sealed record EmergeMeshDone(long Tetrahedra, long Triangles) : EmergeLogEvent;

/// <summary>brief-emw-3 — the driver's <c>{"stage": "gpu-stalled", …}</c> line: the adaptive sweep on the GPU stopped improving
/// and is being solved again on the CPU.</summary>
public sealed record EmergeGpuStalled(int Samples, bool Unusable = false) : EmergeLogEvent;

/// <summary>The driver's <c>{"stage": "meshed", …}</c> line: the mesh's exact counts, before any solve.</summary>
public sealed record EmergeMeshCounts(long Tetrahedra, long Edges, long Triangles) : EmergeLogEvent;

/// <summary>Designer round 36 — a step of the minutes before and after Gmsh, which EMerge names only in passing: the driver's
/// <c>{"stage": "geometry"}</c>, and EMerge's boundary-condition and mesh-data lines.</summary>
public sealed record EmergeSetupStep(string Words) : EmergeLogEvent;

/// <summary><c>Starting frequency domain simulation (#tets = N)</c>: assembling the matrices of the first frequencies.</summary>
public sealed record EmergeAssembling : EmergeLogEvent;

/// <summary><c>Pass k: …</c> — a pass of EMerge's adaptive mesh refinement.</summary>
public sealed record EmergeRefinementPass(int Pass) : EmergeLogEvent;

/// <summary><c>Adaptive sweep: solving at ['9.66 GHz', …]</c>: the frequencies the next batch solves, as EMerge wrote them.</summary>
public sealed record EmergeSweepBatch(IReadOnlyList<string> Frequencies) : EmergeLogEvent;

/// <summary><c>Adaptive sweep: N samples, estimated error E (tol T)</c>.</summary>
public sealed record EmergeSweepError(int Samples, double Error, double Tolerance) : EmergeLogEvent;

/// <summary><c>Adaptive sweep converged with N samples.</c>, or <c>… stopped at max_samples=N with estimated error E.</c></summary>
public sealed record EmergeSweepDone(int Samples, bool Converged, double? Error) : EmergeLogEvent;

/// <summary><c>Starting serial solve of N jobs.</c>: N frequencies are about to be solved.</summary>
public sealed record EmergeJobs(int Count) : EmergeLogEvent;

/// <summary><c>[…-jNNN] Calling … Solver.</c>: one factorisation and solve begins.</summary>
public sealed record EmergeFactorising : EmergeLogEvent;

/// <summary><c>Pre-assembling matrices of N frequency points.</c> in an eigenmode run: the eigensolve follows.</summary>
public sealed record EmergeEigenAssembly : EmergeLogEvent;

/// <summary><c>Post Processing simulation frequency = f GHz</c>.</summary>
public sealed record EmergePostProcessing(double FrequencyGHz) : EmergeLogEvent;

public static class EmergeLog
{
    // EMerge's terminal format (logsettings INFO_FORMAT): "<elapsed>  <LEVEL>  : <message>". Its import banner uses loguru's
    // default format instead ("… | INFO     | emerge:<module>:60 - EMerge v3.0.0b2"), which carries nothing the run reads.
    // A number as EMerge formats one ("1.35e+00", "1.0e-03"), without the full stop that may end its sentence.
    private const string Number = @"([0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)";

    private static readonly Regex Line = new(@"^\s*\d+:\d\d:\d\d\.\d+\s+([A-Z]+)\s*:\s?(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex MeshDone = new(@"^GMSH Meshing complete! \(◮(\d+), ▲(\d+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex Pass = new(@"^Pass (\d+):", RegexOptions.CultureInvariant);
    private static readonly Regex Batch = new(@"^Adaptive sweep: solving at \[(.*)\]\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Error = new(@"^Adaptive sweep: (\d+) samples, estimated error " + Number + @" \(tol " + Number + @"\)",
                                              RegexOptions.CultureInvariant);
    private static readonly Regex Converged = new(@"^Adaptive sweep converged with (\d+) samples", RegexOptions.CultureInvariant);
    private static readonly Regex Stopped = new(@"^Adaptive sweep stopped at max_samples=(\d+) with estimated error " + Number,
                                                RegexOptions.CultureInvariant);
    private static readonly Regex Jobs = new(@"^Starting serial solve of (\d+) jobs?\.", RegexOptions.CultureInvariant);
    private static readonly Regex Calling = new(@"^\[[^\]]*\] Calling .+ Solver\b", RegexOptions.CultureInvariant);
    private static readonly Regex Post = new(@"^Post Processing simulation frequency = " + Number + " GHz", RegexOptions.CultureInvariant);

    /// <summary>What <paramref name="line"/> says, or null when it says nothing the run reads.</summary>
    public static EmergeLogEvent? Read(string line)
    {
        string t = line.TrimEnd('\r');
        if (t.StartsWith('{')) return Driver(t);
        var m = Line.Match(t);
        if (!m.Success) return null;
        string said = m.Groups[2].Value.Trim();
        if (said.StartsWith("Calling GMSH mesher", StringComparison.Ordinal)) return new EmergeMeshing();
        if (said.StartsWith("Generating default boundary conditions", StringComparison.Ordinal)) return new EmergeSetupStep("preparing the boundary conditions");
        if (said.StartsWith("Generating internal mesh data", StringComparison.Ordinal)) return new EmergeSetupStep("building its mesh data");
        if (said.StartsWith("Starting frequency domain simulation", StringComparison.Ordinal)) return new EmergeAssembling();
        if (MeshDone.Match(said) is { Success: true } done) return new EmergeMeshDone(Long(done.Groups[1]), Long(done.Groups[2]));
        if (Pass.Match(said) is { Success: true } pass) return new EmergeRefinementPass(int.Parse(pass.Groups[1].Value, CultureInfo.InvariantCulture));
        if (Batch.Match(said) is { Success: true } batch)
            return new EmergeSweepBatch([.. batch.Groups[1].Value.Split(',').Select(s => s.Trim().Trim('\'', '"')).Where(s => s.Length > 0)]);
        if (Error.Match(said) is { Success: true } e)
            return new EmergeSweepError(int.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture), Num(e.Groups[2]), Num(e.Groups[3]));
        if (Converged.Match(said) is { Success: true } c)
            return new EmergeSweepDone(int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture), true, null);
        if (Stopped.Match(said) is { Success: true } s)
            return new EmergeSweepDone(int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture), false, Num(s.Groups[2]));
        if (Jobs.Match(said) is { Success: true } j) return new EmergeJobs(int.Parse(j.Groups[1].Value, CultureInfo.InvariantCulture));
        if (Calling.IsMatch(said)) return new EmergeFactorising();
        if (said.StartsWith("Pre-assembling matrices of", StringComparison.Ordinal)) return new EmergeEigenAssembly();
        if (Post.Match(said) is { Success: true } p) return new EmergePostProcessing(Num(p.Groups[1]));
        return null;
    }

    /// <summary>The driver's own JSON lines: <c>{"stage": "meshed"}</c> and <c>{"stage": "gpu-stalled"}</c> are events; its
    /// last line is read from <c>run.json</c> instead.</summary>
    private static EmergeLogEvent? Driver(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("stage", out var geo) && geo.GetString() == "geometry")
                return new EmergeSetupStep("building the geometry");
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("stage", out var st) && st.GetString() == "gpu-stalled")
                return new EmergeGpuStalled(r.TryGetProperty("samples", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
                                            r.TryGetProperty("unusable", out var u) && u.ValueKind == JsonValueKind.True);
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("stage", out var stage) && stage.GetString() == "meshed"
                   && r.TryGetProperty("tets", out var tets) && r.TryGetProperty("edges", out var edges)
                   && r.TryGetProperty("triangles", out var tris)
                ? new EmergeMeshCounts(tets.GetInt64(), edges.GetInt64(), tris.GetInt64())
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The driver's refusal, from the <c>{"error": …}</c> line it prints before exiting 2; null when there is none.</summary>
    public static string? DriverError(IEnumerable<string> lines)
    {
        foreach (string line in lines.Reverse())
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e))
                    return e.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>brief-emw-3 — the driver's refusal (an error line marked <c>"refused"</c>: a terminal face whose modes cannot be
    /// converted), or null when the run failed otherwise or not at all.</summary>
    public static string? DriverRefusal(IEnumerable<string> lines) => DriverRefusal(lines, out _);

    /// <summary>The driver's refusal and its <c>"cause"</c> (brief-dfr28-3: <see cref="NonFiniteSolveCause"/>), null for a
    /// terminal face's, which names none.</summary>
    public static string? DriverRefusal(IEnumerable<string> lines, out string? cause)
    {
        cause = null;
        foreach (string line in lines.Reverse())
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e))
                {
                    if (!r.TryGetProperty("refused", out var refused) || refused.ValueKind != JsonValueKind.True) return null;
                    if (r.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.String) cause = c.GetString();
                    return e.GetString();
                }
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>brief-dfr28-3 R-dfr28-3-1 — the cause the driver names when a solve's matrix or solution is not finite
    /// (<c>NON_FINITE</c> in <c>circuitrf_emerge.py</c>).</summary>
    public const string NonFiniteSolveCause = "non-finite-solve";

    private static long Long(Group g) => long.Parse(g.Value, CultureInfo.InvariantCulture);
    private static double Num(Group g) => double.Parse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>
/// R-emi5-2 — EMerge's events as the stage a user reads, and the facts a run reports from them: the tetrahedra, the
/// adaptive sweep's samples and its last error, and whether it converged.
/// </summary>
public sealed class EmergeStageTracker(RunControl? control, bool eigenmode)
{
    public const string StartingLabel = "Starting EMerge";
    public const string MeshingLabel = "Meshing (EMerge)";
    public const string SolvingLabel = "Solving (EMerge)";
    public const string EigenLabel = "Solving the eigenmode problem (EMerge, one core)";
    public const string PostLabel = "Post-processing (EMerge)";
    public const string CpuAgainLabel = "Solving again on the CPU (EMerge)";

    /// <summary>The mesh's tetrahedra, as EMerge last reported them (after any refinement pass).</summary>
    public long? Tetrahedra { get; private set; }

    /// <summary>brief EMG-1 — the driver moved this run off the GPU because a solve could not be trusted.</summary>
    public bool LeftTheGpuUnusable { get; private set; }

    /// <summary>The driver's exact counts, before any solve.</summary>
    public EmergeMeshCounts? Counts { get; private set; }

    /// <summary>Frequencies the adaptive sweep has solved, as it last said.</summary>
    public int? Samples { get; private set; }

    /// <summary>The adaptive sweep's last estimated error, and its tolerance.</summary>
    public (double Error, double Tolerance)? SweepError { get; private set; }

    /// <summary>Whether the adaptive sweep said it converged; null when it said neither.</summary>
    public bool? Converged { get; private set; }

    /// <summary>Called with the driver's mesh counts the moment they arrive, on the run's thread. Throwing
    /// <see cref="OperationCanceledException"/> stops the run (the memory gate's refusal).</summary>
    public Action<EmergeMeshCounts>? OnMeshed { get; init; }

    private int _jobs, _batchDone;
    private bool _solving, _adaptive, _meshing;
    private IReadOnlyList<string> _batch = [];

    /// <summary>A batch's frequencies, and which of them is being solved: a factorisation here can take many minutes.</summary>
    private string BatchText()
        => $"{SolvingLabel}: {(_batchDone > 0 ? $"frequency {_batchDone} of {_batch.Count} in " : "")}{string.Join(", ", _batch)}{ErrorText()}";

    public void Begin() => control?.BeginStage(StartingLabel);

    public void Line(string line)
    {
        switch (EmergeLog.Read(line))
        {
            case EmergeMeshing:
                _meshing = true;
                control?.BeginStage(MeshingLabel);
                break;
            case EmergeSetupStep step:
                control?.SetStageLabel($"{(_meshing ? MeshingLabel : StartingLabel)}: {step.Words}");
                break;
            case EmergeAssembling when !_solving:
                control?.SetStageLabel($"{SolvingLabel}: assembling the matrices");
                break;
            case EmergeMeshDone d:
                Tetrahedra = d.Tetrahedra;
                control?.SetStageDetail($"{d.Tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra");
                break;
            case EmergeMeshCounts c:
                Counts = c;
                Tetrahedra = c.Tetrahedra;
                OnMeshed?.Invoke(c);
                break;
            case EmergeGpuStalled g:
                // brief-emw-3 — the sweep starts again, on the CPU: what the user sees while it does
                _adaptive = false;
                Samples = null;
                SweepError = null;
                LeftTheGpuUnusable = g.Unusable;
                control?.BeginStage(CpuAgainLabel);
                control?.SetStageDetail(g.Unusable
                    ? "the GPU's single-precision factorisation was unusable"
                    : $"the GPU's sweep stopped improving after {g.Samples} frequencies");
                break;
            case EmergeRefinementPass p:
                control?.BeginStage($"Refining the mesh (EMerge), pass {p.Pass}");
                break;
            case EmergeEigenAssembly when eigenmode:
                control?.BeginStage(EigenLabel);
                break;
            case EmergeSweepBatch b:
                _adaptive = true;
                Solving(0);
                _batch = b.Frequencies;
                _batchDone = 0;
                control?.SetStageLabel(BatchText());
                break;
            case EmergeSweepError e:
                Samples = e.Samples;
                SweepError = (e.Error, e.Tolerance);
                control?.SetStageLabel($"{SolvingLabel}{ErrorText()}");
                break;
            case EmergeSweepDone done:
                Samples = done.Samples;
                Converged = done.Converged;
                if (done.Error is { } err && SweepError is { } se) SweepError = (err, se.Tolerance);
                break;
            case EmergeJobs j when !_adaptive && !_solving:
                // A plain sweep states its total; the adaptive one solves a batch at a time and does not.
                _jobs = j.Count;
                Solving(j.Count);
                break;
            case EmergeFactorising when _jobs > 0:
                control?.TickStage();
                break;
            case EmergeFactorising when _adaptive && _batchDone < _batch.Count:
                _batchDone++;
                control?.SetStageLabel(BatchText());
                break;
            case EmergePostProcessing when !_adaptive:
                control?.SetStageLabel(PostLabel);
                break;
        }
    }

    private void Solving(int total)
    {
        if (_solving) return;
        _solving = true;
        control?.BeginStage(SolvingLabel, total, total > 0 ? "frequencies" : "");
    }

    private string ErrorText()
        => SweepError is { } e
            ? $" · {Samples} samples, {PalaceStageTracker.ModelChangeWords} {PalaceStageTracker.Sci(e.Error)} " +
              $"(done below {PalaceStageTracker.Sci(e.Tolerance)})"
            : "";
}
