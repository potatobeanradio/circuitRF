// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 D9 — the Design implementation of Core's IParametricModelProvider: an instance named EmCell=<cell folder>
// is the EM result of that cell's own layout at the values its scope holds. Each host registers it once (the GUI at
// startup, the CLI before a run verb); Core never references this project.

using CircuitRF.Core.Devices.External;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>One EM evaluation an elaboration asked for, as the run that asked records it.</summary>
/// <param name="InstancePath">The instance simulated by EM.</param>
/// <param name="Cell">Its cell's name.</param>
/// <param name="Key">The geometry's cache key; null when refused before a geometry existed.</param>
/// <param name="Cached">True when the cache held it.</param>
/// <param name="Refusal">Why it has no result, or null.</param>
/// <param name="Estimate">For a geometry still to solve, what solving it costs.</param>
/// <param name="Unreached">D4's sentences for this cell.</param>
/// <param name="Connectivity">brief-sdl-7 D9 — the geometry's connectivity signature; null when refused or unmeasured.</param>
public sealed record EmEvaluationRecord(
    string InstancePath, string Cell, string? Key, bool Cached, string? Refusal, EmSolveEstimate? Estimate,
    IReadOnlyList<string> Unreached, string? Setup = null)
{
    public CircuitRF.Design.Layout.SchematicSync.ConnectivitySignature? Connectivity { get; init; }

    /// <summary>brief-sdl-8 — true when the cell is solved through a setup of its 3D view.</summary>
    public bool ThreeD { get; init; }

    /// <summary>brief-sdl-8 D7 — the smallest element the held 3D mesh makes, metres; null for a planar setup.</summary>
    public double? SmallestElementM { get; init; }

    /// <summary>brief-sdl-8 D2/D7 — what the 3D view says about this evaluation: each flattened group, what is held.</summary>
    public IReadOnlyList<string> ThreeDNotes { get; init; } = [];

    /// <summary>What the re-plan of the layout reported at this geometry that a reader of the EM result should know — a
    /// joint it could not close, a part it placed apart (<see cref="EmCellGeometry.Notes"/>).</summary>
    public IReadOnlyList<string> PlanNotes { get; init; } = [];

    /// <summary>The values that made this geometry differ from the schematic's own, as <c>L90 = 0.005</c>; empty at the
    /// schematic's own values.</summary>
    public string At { get; init; } = "";
}

/// <summary>
/// The run an elaboration belongs to, as far as EM evaluations are concerned: whether it only counts (D5), the run's
/// control (cancellation reaches a solve), the solver's core cap, and the record of every evaluation it asked for —
/// what the result is stamped with (R-sdl6-3) and what the count reports. Flows with the call (an
/// <see cref="AsyncLocal{T}"/>), so a sweep's per-point elaboration, deep inside the engine, belongs to the run that
/// started it without the engine knowing.
/// </summary>
public sealed class EmEvaluationScope : IDisposable
{
    private static readonly AsyncLocal<EmEvaluationScope?> _current = new();
    private readonly EmEvaluationScope? _previous;
    private readonly List<EmEvaluationRecord> _records = [];
    private readonly Lock _gate = new();

    private EmEvaluationScope(bool countOnly, RunControl? control, int? maxCores, bool estimate = true)
    {
        CountOnly = countOnly;
        Estimate  = estimate;
        Control   = control;
        MaxCores  = maxCores;
        _previous = _current.Value;
        _current.Value = this;
    }

    /// <summary>The scope the calling flow is in, or null outside any run (an evaluation then solves, uncounted).</summary>
    public static EmEvaluationScope? Current => _current.Value;

    /// <summary>Starts a scope for the calling flow; disposing it restores the one before.</summary>
    public static EmEvaluationScope Begin(bool countOnly = false, RunControl? control = null, int? maxCores = null,
                                          bool estimate = true) =>
        new(countOnly, control, maxCores, estimate);

    public bool CountOnly { get; }

    /// <summary>brief-sdl-7 — counting, whether an unsolved geometry's cost is estimated (false for a connectivity probe).</summary>
    public bool Estimate { get; }
    public RunControl? Control { get; }
    public int? MaxCores { get; }

    public IReadOnlyList<EmEvaluationRecord> Records
    {
        get { lock (_gate) return [.. _records]; }
    }

    internal void Record(EmEvaluationRecord r)
    {
        lock (_gate) _records.Add(r);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current.Value, this)) _current.Value = _previous;
    }
}

public sealed class ParametricEmProvider : IParametricModelProvider
{
    public static readonly ParametricEmProvider Instance = new();

    private ParametricEmProvider() { }

    /// <summary>Registers <see cref="Instance"/> with Core's registry; idempotent. Every host calls it before a run.</summary>
    public static void Register() => ParametricModelRegistry.Register(Instance);

    public string Describe => "EM of a cell's own layout — a cell folder whose layout Update Layout from Schematic made, with an EM setup";

    public ParametricModelAnswer? Resolve(ParametricModelRequest request)
    {
        string cellDir;
        try
        {
            cellDir = Path.IsPathRooted(request.Cell) || request.BaseDirectory is not { Length: > 0 } b
                ? Path.GetFullPath(request.Cell)
                : Path.GetFullPath(Path.Combine(b, request.Cell));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (request.Cell.Length == 0 || !Directory.Exists(cellDir)) return null;   // not a cell this provider serves

        var scope = EmEvaluationScope.Current;
        string cellName = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir));
        if (EmCapability.Resolve(cellDir, request.EmSetup, request.BaseDirectory, out var refusal) is not { } target)
        {
            scope?.Record(new EmEvaluationRecord(request.InstancePath, cellName, null, false, refusal, null, []));
            return ParametricModelAnswer.Refused(refusal!);
        }

        var result = EmEvaluation.Evaluate(target, request.Values, scope?.CountOnly ?? false,
                                           scope?.Control?.Child(), scope?.MaxCores, scope?.Estimate ?? true);
        var unreached = result.Geometry?.Unreached ?? [];
        string setupName = target.SetupName;
        scope?.Record(new EmEvaluationRecord(request.InstancePath, target.CellName, result.Key, result.Cached, result.Refusal,
                                             result.Estimate, unreached, setupName)
        {
            Connectivity = result.Geometry?.Connectivity,
            ThreeD = target.Is3D,
            SmallestElementM = result.Geometry?.ThreeD?.SmallestElementM,
            ThreeDNotes = result.Geometry?.ThreeD?.Notes ?? [],
            PlanNotes = result.Geometry?.Notes ?? [],
            At = result.Geometry is { } g ? string.Join(", ", g.Applied.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                                                .Select(kv => $"{kv.Key} = {EmCellCache.Format(kv.Value)}")) : "",
        });
        if (result.Refusal is { } why) return ParametricModelAnswer.Refused(why);

        string d4 = unreached.Count == 0 ? "" : " " + string.Join(" ", unreached);
        if (result.Geometry?.ThreeD is { Notes.Count: > 0 } three) d4 += " " + string.Join(" ", three.Notes);
        if (result.SnpPath is not { } snp)
            return ParametricModelAnswer.Counted(unreached.Count == 0 ? null : d4.Trim());

        int ports = PortsOf(snp);
        if (ports != request.Ports)
            return ParametricModelAnswer.Refused(
                $"its EM result has {ports} port(s) but '{request.InstancePath}' connects {request.Ports} net(s). The EM ports " +
                $"are the schematic's Ports, numbered by Num; every Port needs its label in the layout (Update Layout from " +
                "Schematic places them).");

        return ParametricModelAnswer.Snp(snp,
            $"'{request.InstancePath}' is the EM result of '{target.CellName}' ({setupName}), " +
            (result.Cached ? "read from the EM cache" : "solved") + $" at geometry {result.Key}.{d4}");
    }

    private static int PortsOf(string snp)
    {
        string ext = Path.GetExtension(snp);
        return ext.Length > 3 && int.TryParse(ext[2..^1], out int n) ? n : -1;
    }
}
