namespace CircuitRF.Core.Matching;

/// <summary>Every "cannot" this library can reach (brief §9).</summary>
public enum MatchRefusalKind
{
    /// <summary>P_n(c) has no real root: no solution at this order for this Q.</summary>
    NoRealRoot,

    /// <summary>Q_far &lt; Q_actual — the termination's reactance exceeds what the network can take.</summary>
    FarEndNotAbsorbable,

    /// <summary>No member of the response family reaches the required far-end Q.</summary>
    ResponseInfeasible,

    /// <summary>The transform product cannot reach R_far_target / R_far_synthesised inside the ranges.</summary>
    TransformsCannotReachTarget,

    /// <summary>The ladder has no transformable pair at all.</summary>
    NoTransformablePairs,

    /// <summary>The order cannot absorb both ends (parity), or is outside 2..6.</summary>
    InvalidOrder,

    /// <summary>A termination is degenerate — a non-positive R, or a short/open reactance.</summary>
    InvalidTermination,
}

/// <summary>
/// A refusal is a <b>returned value</b>, never an exception and never a silent fallback. MN-3 renders
/// these verbatim, so <b>a refusal that does not carry its numbers is not finished</b> — every kind in
/// <see cref="MatchRefusalKind"/> populates <see cref="Numbers"/>.
/// </summary>
public sealed record MatchRefusal(MatchRefusalKind Kind, string Message)
{
    /// <summary>Which termination the refusal is about — 1, 2 or null when it is about neither.</summary>
    public int? End { get; init; }

    /// <summary>The numbers the message quotes, named, so a caller can re-render them its own way.</summary>
    public IReadOnlyDictionary<string, double> Numbers { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Builds a refusal with its numbers in one expression.</summary>
    public static MatchRefusal Create(
        MatchRefusalKind kind, string message, int? end = null, params (string Key, double Value)[] numbers)
        => new(kind, message)
        {
            End = end,
            Numbers = numbers.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        };
}
