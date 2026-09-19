namespace CircuitRF.Design.RailRf;

/// <summary>
/// The answer <see cref="RailOrder.Resolve"/> gives: an order, or a refusal. Never both, and never
/// an exception — the caller is a window that has to put the sentence on its status strip and a verb
/// that has to print it, and neither of them wants a stack trace.
/// </summary>
/// <param name="Order">The rail names, upstream first. Empty when <paramref name="Refusal"/> is set.</param>
/// <param name="Refusal">The sentence, or null when there is an order.</param>
public sealed record RailOrderResult(IReadOnlyList<string> Order, string? Refusal)
{
    public bool Ok => Refusal is null;
}

/// <summary>
/// The dependency order over a document's rail set (railrf.md §2.2).
///
/// <para><b>What the two rows of one regulator buy.</b> A regulator is a load on its input rail and a
/// source on its output rail — <b>two rows naming the same refdes</b>, a <see cref="RailLoad"/> on
/// one rail's list and a <see cref="RailSource"/> on another's — and nothing in the model links them
/// except the refdes. This class reads that: <b>rail B depends on rail A when some refdes is a LOAD
/// on A and a SOURCE on B</b>, and the order it returns is what makes a regulator's input voltage the
/// UPSTREAM ANSWER rather than a nominal.</para>
///
/// <para><b>The cycle refusal is load-bearing and it is not a limitation to be lifted.</b> Note §9:
/// solving the rails together — a regulator as a two-port with a forward transfer and a PSRR — is a
/// different model, it needs exactly the data §8.2 records as frequently impossible to obtain, and it
/// would be entered by accident the first time someone asked for a cycle in the order to be
/// supported. So the refusal names both rails and the refdes that closes the loop, and stops.</para>
/// </summary>
public static class RailOrder
{
    /// <summary>One rail depending on another, and the part that makes it so.</summary>
    /// <param name="Upstream">The rail that must be solved first — the one the refdes LOADS.</param>
    /// <param name="Downstream">The rail the same refdes SOURCES.</param>
    /// <param name="Refdes">The part in both rows.</param>
    public readonly record struct RailEdge(string Upstream, string Downstream, string Refdes);

    /// <summary>
    /// The order the rails must be solved in, or the refusal.
    ///
    /// <para>Rails with no dependency between them keep their declaration order — a solve order that
    /// reshuffled an independent set would make two runs of one document report their rails in
    /// different orders for no reason anyone could see.</para>
    /// </summary>
    public static RailOrderResult Resolve(RailDocument doc)
    {
        var rails = doc.Rails.Select(r => r.Name).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Two rails of one name are a REFUSAL here and not an exception. RailDocumentIo already
        // refuses such a document on read and on write, so this is reachable only from a document
        // being edited in memory — and this method's own contract is that it never throws, because
        // its callers are a window that has to put the sentence on a status strip and a verb that
        // has to print it. Keying the Kahn dictionaries below on a duplicate name would throw
        // instead, from inside a method documented not to.
        for (int i = 0; i < rails.Count; i++)
            if (!index.TryAdd(rails[i], i))
                return new RailOrderResult([],
                    $"Two rails are both called '{rails[i]}'. A rail's name is how --rail picks one " +
                    "and how the solve order names one, so they are distinct.");

        if (Edges(doc, out var edges) is { } bad) return new RailOrderResult([], bad);

        // Kahn's, with declaration order as the tiebreak among ready rails.
        var incoming = rails.ToDictionary(r => r, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = rails.ToDictionary(r => r, _ => new List<RailEdge>(), StringComparer.OrdinalIgnoreCase);

        foreach (var e in edges)
        {
            outgoing[e.Upstream].Add(e);
            incoming[e.Downstream]++;
        }

        var ready = new List<string>(rails.Where(r => incoming[r] == 0));
        var order = new List<string>(rails.Count);

        while (ready.Count > 0)
        {
            // The earliest-declared of the rails that are ready.
            int pick = 0;
            for (int i = 1; i < ready.Count; i++)
                if (index[ready[i]] < index[ready[pick]]) pick = i;

            string next = ready[pick];
            ready.RemoveAt(pick);
            order.Add(next);

            foreach (var e in outgoing[next])
                if (--incoming[e.Downstream] == 0) ready.Add(e.Downstream);
        }

        if (order.Count == rails.Count) return new RailOrderResult(order, null);

        // Everything left carries an incoming edge from something else that is left: a cycle.
        var stuck = rails.Where(r => !order.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        return new RailOrderResult([], CycleRefusal(stuck, edges));
    }

    /// <summary>
    /// Every dependency the document states, or the refusal for a part that is a load and a source on
    /// ONE rail — a self-edge, which is a cycle of length one and would otherwise be reported as a
    /// rail depending on itself.
    /// </summary>
    public static string? Edges(RailDocument doc, out IReadOnlyList<RailEdge> edges)
    {
        var found = new List<RailEdge>();

        foreach (var downstream in doc.Rails)
        foreach (string refdes in Refdeses(downstream.Sources.Select(s => s.Anchor)))
        foreach (var upstream in doc.Rails)
        {
            if (!Refdeses(upstream.Loads.Select(l => l.Anchor))
                    .Contains(refdes, StringComparer.OrdinalIgnoreCase)) continue;

            if (string.Equals(upstream.Name, downstream.Name, StringComparison.OrdinalIgnoreCase))
            {
                edges = [];
                return $"'{refdes}' is both a load and a source on rail '{upstream.Name}'. A part " +
                       "that feeds the rail it draws from is a rail that depends on itself, and there " +
                       "is no order that solves it — a regulator's two rows name two DIFFERENT rails.";
            }

            found.Add(new RailEdge(upstream.Name, downstream.Name, refdes));
        }

        edges = found;
        return null;
    }

    private static IReadOnlyCollection<string> Refdeses(IEnumerable<RailPortAnchor> anchors) =>
        [.. anchors.Where(a => a.Refdes is { Length: > 0 }).Select(a => a.Refdes!).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The sentence for a cycle: the rails in it, and the refdes that closes the loop.
    ///
    /// <para>It walks one actual cycle rather than listing the whole stuck set, because "these four
    /// rails could not be ordered" does not say which row to change and "U2 is a load on '+3V3' and a
    /// source on '+1V8'" does.</para>
    /// </summary>
    private static string CycleRefusal(List<string> stuck, IReadOnlyList<RailEdge> edges)
    {
        var within = new HashSet<string>(stuck, StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, RailEdge>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
            if (within.Contains(e.Upstream) && within.Contains(e.Downstream))
                next.TryAdd(e.Upstream, e);

        // Walk forward from the first stuck rail until a rail repeats; that repeat IS the loop.
        var path = new List<RailEdge>();
        var onPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string at = stuck[0];

        while (next.TryGetValue(at, out var e))
        {
            if (onPath.TryGetValue(at, out int from))
            {
                path = path.Skip(from).ToList();
                break;
            }
            onPath[at] = path.Count;
            path.Add(e);
            at = e.Downstream;
        }

        if (path.Count == 0)
            return $"The rails {Quote(stuck)} cannot be put in a solve order — each of them waits on " +
                   "another, so none can be solved first.";

        var closing = path[^1];
        string loop = string.Join(" → ", path.Select(p => $"'{p.Upstream}'").Append($"'{closing.Downstream}'"));

        return $"The rails cannot be put in a solve order: {loop}. " +
               $"'{closing.Refdes}' is a load on rail '{closing.Upstream}' and a source on rail " +
               $"'{closing.Downstream}', which closes the loop. railRF solves the rails one at a time " +
               "in dependency order, so that a regulator's input voltage is the upstream answer " +
               "rather than a nominal; solving them together is a different model and railRF does not " +
               "have it. Break the loop by removing one of those two rows.";
    }

    private static string Quote(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => $"'{n}'"));
}
