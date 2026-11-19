// brief-emi-2 R-emi2-3b — what EMerge's probe answered (EmergeSolver.ProbeSource), and the speed tier it implies.

using System.Text.Json;

namespace CircuitRF.Design.Em3d;

/// <summary>How fast the sparse direct solver an EMerge run would use is, as the Settings row says it.</summary>
public enum EmergeSpeedTier
{
    /// <summary>The probe could not tell which solver EMerge would pick.</summary>
    Unknown,

    /// <summary>EMerge picks its single-threaded default (SuperLU, from SciPy).</summary>
    Baseline,

    /// <summary>EMerge picks a faster solver than its default.</summary>
    Fast,
}

/// <summary>
/// One answer of EMerge's probe. <see cref="Emerge"/> is the version discovery validates; the rest is what the Settings
/// row, <c>solver list</c> and the MCP <c>solver</c> tool report.
/// </summary>
/// <param name="SparseSolvers">The optional sparse solvers EMerge reports importable, by its own flag names, lower case.
/// Reported, never displayed: the names are the packages', and what matters to a user is the tier.</param>
/// <param name="DirectSolver">The class EMerge's own automatic routine picks for a large driven system, or null when the
/// probe could not ask it.</param>
public sealed record EmergeProbe(
    string Emerge, string? Python, string? Platform, string? Machine, int? Cores, string? Gmsh,
    IReadOnlyList<string> SparseSolvers, string? DirectSolver)
{
    /// <summary>The class name of EMerge's single-threaded default (SciPy's SuperLU).</summary>
    internal const string BaselineSolverClass = "SolverSuperLU";

    /// <summary>The first characters of the probe's own line, which is how it is told apart from EMerge's log lines.</summary>
    private const string Marker = "{\"probe\"";

    /// <summary>brief-emi-9 — EMerge's flag for its GPU sparse solver, as the probe lists it.</summary>
    internal const string GpuSolverFlag = "sparta";

    /// <summary>brief-emi-9 — EMerge can load its GPU sparse solver and it has a GPU (the probe lists SPARTA only then). A run asks
    /// for the GPU (<see cref="EmergeDevicePolicy"/>); on the CPU SPARTA is EMerge's own first choice since 3.0.8.</summary>
    public bool Gpu => SparseSolvers.Contains(GpuSolverFlag, StringComparer.Ordinal);

    public EmergeSpeedTier Tier => DirectSolver switch
    {
        null                => EmergeSpeedTier.Unknown,
        BaselineSolverClass => EmergeSpeedTier.Baseline,
        _                   => EmergeSpeedTier.Fast,
    };

    /// <summary>
    /// The tier in two or three words: <c>solver list --json</c>'s <c>tier</c>. What a person reads, with what would
    /// make it faster, is <see cref="EmergeAccelerator.Sentence(SolverInstallation?)"/> (brief-emi-3).
    /// </summary>
    public string TierText => Tier switch
    {
        EmergeSpeedTier.Fast     => "fast sparse solver",
        EmergeSpeedTier.Baseline => "baseline sparse solver (one core)",
        _                        => "sparse solver not reported",
    } + (Gpu ? ", GPU sparse solver" : "");

    /// <summary>The banner line: <c>EMerge 3.0.0b2, Python 3.13.16, Gmsh 4.15.2</c>.</summary>
    public string Banner => $"EMerge {Emerge}" + (Python is { } p ? $", Python {p}" : "") + (Gmsh is { } g ? $", Gmsh {g}" : "");

    /// <summary>
    /// The probe's answer out of what the process printed (EMerge's log lines included), or null with
    /// <paramref name="why"/> saying what was wrong: no probe line (its first line quoted), a line that does not parse,
    /// or an EMerge that would not import (its own error quoted).
    /// </summary>
    public static EmergeProbe? Read(string output, out string? why)
    {
        why = null;
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string? line = lines.LastOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null)
        {
            string first = lines.FirstOrDefault() ?? "";
            if (first.Length > 200) first = first[..200];
            why = first.Length == 0 ? "it started but printed nothing" : $"it did not answer the probe; it printed '{first}'";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Text(root, "error") is { } error)
            {
                why = $"the Python there could not import EMerge ({error})";
                return null;
            }
            if (Text(root, "emerge") is not { Length: > 0 } version)
            {
                why = "EMerge imported but did not say which version it is";
                return null;
            }
            var solvers = root.TryGetProperty("sparseSolvers", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            int? cores = root.TryGetProperty("cores", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) ? n : null;
            return new EmergeProbe(version, Text(root, "python"), Text(root, "platform"), Text(root, "machine"), cores,
                                   Text(root, "gmsh"), solvers, Text(root, "directSolver"));
        }
        catch (JsonException)
        {
            why = $"its answer did not parse: '{(line.Length > 200 ? line[..200] : line)}'";
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
