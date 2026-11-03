using System.Collections.Concurrent;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// The draws one Monte Carlo trial hands an elaboration (<see cref="CircuitRF.Core.Elaboration.Elaborator.Statistics"/>),
/// so the distribution calls in a kit's models and in a design's VARs vary (docs/design/yield.md §7).
///
/// <para><b>A stream's z is a pure function of (seed, trial, stream)</b> (yield overview D5), drawn at
/// <see cref="StatStreams"/> slot k = 1 — slot 0 belongs to the setup's statistical entries, so a stream named
/// like an entry's key never shares its draw. A caller that has planned the streams already (it elaborated
/// nominally first and read <c>StatisticalCalls</c>) passes their z in <c>planned</c>, and those win: that is how
/// <c>lhs</c> and <c>sobol</c> reach expression streams too.</para>
///
/// <para>A statistical corner's replay sets <c>unplannedAtNominal</c>: a stream absent from <c>planned</c> (the
/// recorded z-vector) evaluates at its nominal rather than drawing — it did not exist when the trial was drawn.</para>
///
/// <para><c>process=0</c> / <c>mismatch=0</c> switch a kind off, which evaluates it at its nominal;
/// <c>sigmascale</c> multiplies every spread.</para>
/// </summary>
public sealed class ExpressionDraws : IStatisticalDraws
{
    /// <summary>The <see cref="StatStreams"/> slot expression streams draw from.</summary>
    public const int StreamSlot = 1;

    private readonly ulong _seed;
    private readonly IReadOnlyDictionary<string, double>? _planned;
    private readonly bool _unplannedAtNominal;
    private readonly string[] _held;
    private readonly ConcurrentDictionary<string, (StatisticalKind Kind, double Z)> _drawn = new(StringComparer.Ordinal);

    public ExpressionDraws(int seed, int trial, bool process = true, bool mismatch = true, double sigmaScale = 1.0,
                           IReadOnlyDictionary<string, double>? planned = null, bool unplannedAtNominal = false,
                           IReadOnlyList<string>? held = null)
    {
        _held = held is { Count: > 0 } ? [.. held.Select(h => h + ".")] : [];
        if (trial < 1) throw new ArgumentOutOfRangeException(nameof(trial), "Trials are numbered from 1.");
        _seed      = unchecked((ulong)seed);
        Trial      = trial;
        Process    = process;
        Mismatch   = mismatch;
        SigmaScale = sigmaScale;
        _planned   = planned;
        _unplannedAtNominal = unplannedAtNominal;
    }

    /// <summary>The draws of <paramref name="trial"/> under a setup's <c>statistics</c> settings.</summary>
    public static ExpressionDraws For(StatisticsSettings? settings, int trial,
                                      IReadOnlyDictionary<string, double>? planned = null, bool unplannedAtNominal = false,
                                      IReadOnlyList<string>? held = null)
    {
        var s = settings ?? new StatisticsSettings();
        return new ExpressionDraws(s.EffectiveSeed, trial, s.Process ?? true, s.Mismatch ?? true,
                                   s.SigmaScale ?? 1.0, planned, unplannedAtNominal, held);
    }

    /// <summary>
    /// Whether <paramref name="stream"/> is inside a held instance (brief vendorlib-13): a library part unticked in the
    /// Yield panel's Library parts group, or one a corner sets the corner of. Its draws evaluate at their nominal.
    /// </summary>
    public bool IsHeld(string stream)
    {
        foreach (var h in _held) if (stream.StartsWith(h, StringComparison.Ordinal)) return true;
        return false;
    }

    public int    Trial      { get; }
    public bool   Process    { get; }
    public bool   Mismatch   { get; }
    public double SigmaScale { get; }

    /// <summary>Every stream this trial drew, with its kind and z — what a result records per stream.</summary>
    public IReadOnlyDictionary<string, (StatisticalKind Kind, double Z)> Drawn => _drawn;

    public StatisticalDraw? Draw(StatisticalKind kind, string stream)
    {
        if (kind == StatisticalKind.Process ? !Process : !Mismatch) return null;
        if (_held.Length > 0 && IsHeld(stream)) return null;
        // A replayed trial (brief-yield-6 R-ya6-4): a stream the recording has no draw for is one the run never had.
        if (_unplannedAtNominal && (_planned is null || !_planned.ContainsKey(stream))) return null;

        double z = _planned is not null && _planned.TryGetValue(stream, out double p)
            ? p
            : StatStreams.Normal(_seed, Trial, StatStreams.Id(stream), StreamSlot);
        _drawn[stream] = (kind, z);
        return new StatisticalDraw(z, SpecialFunctions.NormalCdf(z), SigmaScale);
    }
}
