using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// Statistical entries set at nominal ± k of their OWN σ — DOE's <c>levels=sigma:k</c> (brief-yield-14, R-ya15-9) and
/// sensitivity's ±kσ step (brief-sens-1 D3). One helper, so a level and a step are the same value for the same k.
///
/// <para>A level is z = k × coded through the entry's own distribution (<see cref="SampleValues.Apply"/>). A truncated
/// entry is set through its UNTRUNCATED distribution with z clipped at the truncation, because the truncated quantile at
/// z = k is not kσ (trunc=3 puts sigma:1 at 0.99σ) and a level is read as kσ.</para>
/// </summary>
internal sealed class SigmaLevels
{
    private readonly List<TunableEntry> _entries = [];
    private readonly List<TunableEntry> _levels = [];
    private readonly List<double> _trunc = [];

    public SigmaLevels(IEnumerable<TunableEntry> entries, double sigma)
    {
        Sigma = sigma;
        foreach (var e in entries)
        {
            var level = e.Clone();
            double? trunc = level.Spread?.Trunc is { } tr ? ResolvedSpread.Number(tr) : null;
            if (level.Spread is { } sp) sp.Trunc = null;
            _entries.Add(e);
            _levels.Add(level);
            _trunc.Add(trunc is > 0 ? trunc.Value : double.PositiveInfinity);
        }
    }

    /// <summary>k.</summary>
    public double Sigma { get; }

    /// <summary>The entries, as given.</summary>
    public IReadOnlyList<TunableEntry> Entries => _entries;

    /// <summary>Every entry at its coded level (−1, 0, +1 … in units of k σ).</summary>
    public SampledValues At(IReadOnlyList<Tunable> nominals, int run, IReadOnlyList<double> coded)
    {
        var z = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < _entries.Count; i++) z[_entries[i].Key] = Z(i, coded[i]);
        return SampleValues.Apply(_levels, nominals, new StatisticalSample(run, z));
    }

    /// <summary>Entry <paramref name="index"/> alone at its coded level; the value map holds only that entry's value,
    /// so every other value stays exactly as the design holds it.</summary>
    public SampledValues One(IReadOnlyList<Tunable> nominals, int run, int index, double coded)
        => SampleValues.Apply([_levels[index]], nominals,
               new StatisticalSample(run, new Dictionary<string, double>(StringComparer.Ordinal) { [_entries[index].Key] = Z(index, coded) }));

    private double Z(int index, double coded) => Math.Clamp(Sigma * coded, -_trunc[index], _trunc[index]);
}
