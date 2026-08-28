namespace CircuitRF.Core.Matching;

/// <summary>Which of the two element kinds a ladder element is.</summary>
public enum ElementType
{
    /// <summary>An inductance, henries.</summary>
    L,

    /// <summary>A capacitance, farads.</summary>
    C,
}

/// <summary>One L or one C of the bandpass ladder. An arm has two of them.</summary>
public sealed class MatchElement
{
    /// <summary>L1, C1, L2, ... numbered left to right in the final (Term1-first) order.</summary>
    public required string Name { get; set; }

    /// <summary>L or C.</summary>
    public required ElementType Type { get; init; }

    /// <summary>True when this element sits between a node and ground; false when it is in the through path.</summary>
    public required bool IsShunt { get; set; }

    /// <summary>Henries or farads, per <see cref="Type"/>.</summary>
    public required double Value { get; set; }

    /// <summary>
    /// 1 or 2 when the external termination at that end supplies this element, 0 when it is ours.
    /// <b>The flag, not the name, is what decides what MN-2 must not stamp</b> — absorbed elements
    /// keep ordinary names.
    /// </summary>
    public int AbsorbedEnd { get; set; }

    /// <summary>True when an external termination supplies this element.</summary>
    public bool IsAbsorbed => AbsorbedEnd != 0;

    /// <summary>True for the CFano/LFano excess element of match.md §4.5.</summary>
    public bool IsExcess { get; set; }

    /// <summary>True for the CDetune/LDetune element of match.md §4.6.</summary>
    public bool IsDetune { get; set; }

    /// <summary>Which basis arm this came from, or -1 for a Norton product.</summary>
    public int ArmIndex { get; init; } = -1;

    /// <summary>A copy — the transform pipeline works on clones so a caller's network is never mutated.</summary>
    public MatchElement Clone() => new()
    {
        Name = Name,
        Type = Type,
        IsShunt = IsShunt,
        Value = Value,
        AbsorbedEnd = AbsorbedEnd,
        IsExcess = IsExcess,
        IsDetune = IsDetune,
        ArmIndex = ArmIndex,
    };
}

/// <summary>
/// The ladder: an ordered element list between two port resistances, always in <b>Term1-first</b>
/// order regardless of which end drove the synthesis.
/// </summary>
/// <remarks>
/// <b>Nets are derived from the list, never stored.</b> Order plus orientation determines the topology
/// completely (see <see cref="AssignNets"/>), which is what lets match.md §4.7's "swap nets with the
/// neighbour it displaced" reduce to a list swap. The swap is response-preserving only between
/// like-oriented neighbours — two series elements of one arm, or two shunt elements of one arm — and
/// <c>NortonTransform</c> asserts exactly that rather than trusting the offset table.
/// </remarks>
public sealed class MatchNetwork
{
    /// <summary>Port resistance on the Term1 side, ohms.</summary>
    public double R1 { get; set; }

    /// <summary>Port resistance on the Term2 side, ohms.</summary>
    public double R2 { get; set; }

    /// <summary>The elements, left (Term1) to right (Term2).</summary>
    public List<MatchElement> Elements { get; init; } = [];

    /// <summary>A deep copy.</summary>
    public MatchNetwork Clone() => new()
    {
        R1 = R1,
        R2 = R2,
        Elements = [.. Elements.Select(e => e.Clone())],
    };

    /// <summary>Finds an element by name, or null.</summary>
    public MatchElement? Find(string name) =>
        Elements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    /// <summary>Index of an element by name, or -1.</summary>
    public int IndexOf(string name) =>
        Elements.FindIndex(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The two node names each element connects, derived by walking the list: a series element steps
    /// the through node forward, a shunt element hangs off the current node to ground.
    /// </summary>
    /// <returns>One (a, b) pair per element, in element order. "0" is ground.</returns>
    public IReadOnlyList<(string A, string B)> AssignNets()
    {
        var nets = new List<(string, string)>(Elements.Count);
        string current = "p1";
        int mint = 0;
        foreach (var e in Elements)
        {
            if (e.IsShunt)
            {
                nets.Add((current, "0"));
            }
            else
            {
                string next = $"n{++mint}";
                nets.Add((current, next));
                current = next;
            }
        }
        return nets;
    }

    /// <summary>The node the Term2-side port attaches to, after <see cref="AssignNets"/>'s walk.</summary>
    public string RightPortNet()
    {
        string current = "p1";
        int mint = 0;
        foreach (var e in Elements)
            if (!e.IsShunt) current = $"n{++mint}";
        return current;
    }
}

/// <summary>Q, C_eq and the excess split — the three places allowed to know about L versus C.</summary>
public static class MatchQ
{
    /// <summary>C_eq of a raw (kind, value) pair; see <see cref="Termination.CeqAt"/> for the record form.</summary>
    public static double Ceq(ReactanceKind kind, double value, double omega0) => kind switch
    {
        ReactanceKind.C => value,
        ReactanceKind.L => value > 0 ? 1.0 / (omega0 * omega0 * value) : double.PositiveInfinity,
        _ => 0.0,
    };

    /// <summary>Q from a C_eq, a resistance and a topology.</summary>
    public static double Q(TerminationTopology topology, double r, double ceq, double omega0) =>
        ceq <= 0.0 ? 0.0
        : topology == TerminationTopology.Parallel ? omega0 * r * ceq
        : 1.0 / (omega0 * r * ceq);

    /// <summary>
    /// Splits an end arm's absorbed reactance into the termination's own value plus a real added
    /// element (match.md §4.5). <b>Worked entirely in C_eq</b>, which is what makes one formula serve
    /// all four (series|parallel) x (C|L) combinations: in raw element values the parallel-inductor
    /// case combines reciprocally and the capacitive one does not.
    /// </summary>
    /// <param name="topology">The end's topology.</param>
    /// <param name="ceqTotal">The end arm's C_eq — for every arm this is simply the arm's C value.</param>
    /// <param name="qSynthesised">Q the synthesis wants at this end.</param>
    /// <param name="qActual">Q the real termination provides.</param>
    /// <param name="r">The end's port resistance <i>as it stands now</i>.</param>
    /// <param name="omega0">Band centre, rad/s.</param>
    /// <returns>(C_eq the termination keeps, C_eq of the added element).</returns>
    public static (double CeqKept, double CeqAdded) SplitExcess(
        TerminationTopology topology, double ceqTotal, double qSynthesised, double qActual,
        double r, double omega0)
    {
        double dq = qSynthesised - qActual;
        if (topology == TerminationTopology.Parallel)
        {
            double added = dq / (r * omega0);
            return (ceqTotal - added, added);
        }
        else
        {
            double added = 1.0 / (dq * r * omega0);
            return (ceqTotal * added / (added - ceqTotal), added);
        }
    }

    /// <summary>Converts a C_eq back to a raw element value of the given kind.</summary>
    public static double FromCeq(ReactanceKind kind, double ceq, double omega0) => kind switch
    {
        ReactanceKind.C => ceq,
        ReactanceKind.L => ceq > 0 ? 1.0 / (omega0 * omega0 * ceq) : double.PositiveInfinity,
        _ => 0.0,
    };
}

/// <summary>Order parity (match.md §4.2) — what the Designer's order picker is driven from.</summary>
public static class MatchOrders
{
    /// <summary>The lowest order the synthesis accepts.</summary>
    public const int MinOrder = 2;

    /// <summary>The highest order the closed forms are tabulated for.</summary>
    public const int MaxOrder = 6;

    /// <summary>
    /// The orders that can absorb both ends. An end absorbing a series reactance needs a series arm
    /// there and an end absorbing a shunt reactance needs a shunt arm; arms alternate, so a MIXED
    /// pair forces even n and a LIKE pair forces odd n. Either end resistive frees the parity.
    /// </summary>
    public static IReadOnlyList<int> ValidOrders(Termination term1, Termination term2)
        => ValidOrders(term1, term2, NetworkForm.Bandpass);

    /// <inheritdoc cref="ValidOrders(Termination, Termination)"/>
    /// <param name="term1">The port-1 end.</param>
    /// <param name="term2">The port-2 end.</param>
    /// <param name="form">Which network form the orders are being asked about (match.md §16.4).</param>
    /// <remarks>
    /// <b>The parity argument is a BANDPASS argument.</b> Its arms hold two elements each, so an end
    /// absorbing a series reactance needs a series arm and the alternation forces the parity above.
    ///
    /// <para>A lowpass or highpass ladder is single elements, so it alternates every position rather
    /// than every arm, and the count is 2n whatever n is (match.md §16.2) — with 2n even, the two END
    /// elements are always of OPPOSITE orientation. So every order serves a mixed pair, and a LIKE
    /// pair (shunt-C to shunt-C, the classic interstage) needs an odd element count, which §16.3's
    /// closed form does not have: it would need a weighted Chebyshev polynomial and a Remez exchange.
    /// v1 says so by offering no order at all for that pair rather than by producing a network that
    /// only absorbs one of the two ends.</para>
    /// </remarks>
    public static IReadOnlyList<int> ValidOrders(Termination term1, Termination term2, NetworkForm form)
    {
        ArgumentNullException.ThrowIfNull(term1);
        ArgumentNullException.ThrowIfNull(term2);

        if (form != NetworkForm.Bandpass)
        {
            bool bothReactive = term1.HasReactance && term2.HasReactance;
            return bothReactive && term1.Topology == term2.Topology ? [] : [2, 3, 4, 5, 6];
        }

        if (!term1.HasReactance || !term2.HasReactance)
            return [2, 3, 4, 5, 6];

        bool like = term1.Topology == term2.Topology;
        return like ? [3, 5] : [2, 4, 6];
    }
}
