namespace CircuitRF.Core.Matching;

/// <summary>One transform as the linkage sees it: a value, a lock, and the range it must stay in.</summary>
/// <param name="N">Its current turns ratio.</param>
/// <param name="Locked">Locked transforms are never written.</param>
/// <param name="Range">Recomputed by the caller before calling — never stored.</param>
public sealed record LinkSlot(double N, bool Locked, TransformRange Range);

/// <summary>What the redistribution settled on.</summary>
/// <param name="N">The new N of every transform, in the order they were given.</param>
/// <param name="Achieved">The product of N^2 those values reach.</param>
/// <param name="Required">The product they had to reach.</param>
/// <param name="Shortfall">
/// <c>Achieved / Required</c>. Away from 1 the far termination cannot be reached inside the ranges,
/// and MN-3 shows it in red.
/// </param>
/// <param name="AtLimit">Indices of the transforms sitting on a range bound.</param>
public sealed record LinkResult(
    IReadOnlyList<double> N, double Achieved, double Required, double Shortfall,
    IReadOnlyList<int> AtLimit)
{
    /// <summary>True when the product is on target to a relative 1e-9.</summary>
    public bool OnTarget => Math.Abs(Shortfall - 1.0) <= 1e-9;
}

/// <summary>
/// match.md §4.8's linkage rule as pure arithmetic, so MN-3 only binds to it.
/// </summary>
public static class MatchLinkage
{
    /// <summary>
    /// Moves one transform to <paramref name="requestedN"/> and, with link on, re-solves the unlocked
    /// others so <c>product(N^2)</c> stays on <paramref name="required"/>.
    /// </summary>
    /// <remarks>
    /// With exactly one transform and link on, N is fully determined — MN-3 disables the slider
    /// rather than letting the user move something that immediately snaps back.
    /// </remarks>
    public static LinkResult Redistribute(
        IReadOnlyList<LinkSlot> slots, int current, double requestedN, double required, bool link)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0) return new LinkResult([], 1.0, required, 1.0 / required, []);
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(current, slots.Count);

        var n = slots.Select(s => s.N).ToArray();
        n[current] = slots[current].Range.Clamp(requestedN);

        if (link)
        {
            double root = Math.Sqrt(required);
            if (slots.Count == 1)
            {
                n[0] = slots[0].Range.Clamp(root);
            }
            else
            {
                for (int t = 0; t < slots.Count; t++)
                {
                    if (t == current || slots[t].Locked) continue;

                    // sqrt(required) is the product every N must multiply to; the share left for t is
                    // that, divided by the one being dragged and by every other transform's N.
                    double others = 1.0;
                    for (int k = 0; k < slots.Count; k++)
                        if (k != current && k != t) others *= n[k];

                    double target = root / (n[current] * others);
                    // The brief writes this as t.N + (target - t.N); that is target, and saying so is
                    // clearer than reproducing an identity.
                    n[t] = slots[t].Range.Clamp(target);

                    if (Math.Abs(Product(n) / required - 1.0) <= 1e-9) break;
                }

                double rest = 1.0;
                for (int k = 0; k < slots.Count; k++) if (k != current) rest *= n[k] * n[k];
                n[current] = slots[current].Range.Clamp(Math.Sqrt(required / rest));
            }
        }

        double achieved = Product(n);
        var atLimit = new List<int>();
        for (int k = 0; k < slots.Count; k++)
        {
            double span = Math.Max(1e-30, slots[k].Range.Max - slots[k].Range.Min);
            if (Math.Abs(n[k] - slots[k].Range.Min) <= 1e-12 * span
                || Math.Abs(n[k] - slots[k].Range.Max) <= 1e-12 * span)
                atLimit.Add(k);
        }

        return new LinkResult(n, achieved, required, achieved / required, atLimit);
    }

    private static double Product(double[] n)
    {
        double p = 1.0;
        foreach (double x in n) p *= x * x;
        return p;
    }
}
