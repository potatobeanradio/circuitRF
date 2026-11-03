using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-1 R-sdl1-1: which placed parts the schematic joins pin to pin, and why every other net keeps its
/// ratsnest. Nodes are placed parts with at least one resolved pin; an edge is a <b>joint</b> — a net holding
/// exactly two layout pins, on one layer, off the ground net (D1). The connected components of the joint edges
/// are the <see cref="Chain"/>s <see cref="ChainPlacer"/> lays out.
///
/// <para>Pins are matched to schematic terminals by <see cref="SchematicLayoutOrientation.CellPinFor"/>, the rule
/// the orientation alignment and the ground vias already read — the caller resolves them, so there is no second
/// matcher here. Everything is ordered by schematic id (series rule S5): parts by <see cref="CompareIds"/>, nets
/// by their first end, joints by their first end.</para>
/// </summary>
public sealed class JointGraph
{
    /// <summary>Why a net with layout pins is not a joint.</summary>
    public enum NetReason
    {
        /// <summary>One layout pin: nothing to join and no ratsnest line, so never reported.</summary>
        OnePin,
        /// <summary>The ground net ("0"): its pins return through vias and the pour, not through each other.</summary>
        Ground,
        /// <summary>Three or more layout pins: no single pairing is the schematic's (SDL-3).</summary>
        ThreeOrMorePins,
        /// <summary>The two pins are on different layers: joining them needs a via (SDL-3).</summary>
        LayerMismatch,
        /// <summary>One end is a footprint pad or a via, whose joint point is SDL-2's and SDL-3's to define.</summary>
        PadOrVia,
    }

    /// <summary>
    /// One placed part. <paramref name="Terminals"/>[k − 1] is the cell pin schematic terminal k lands on, null where
    /// none does. <paramref name="Existing"/>: already in the layout before this run, and so an anchor (D5).
    /// <paramref name="Joinable"/>: a distributed part this brief chains; a footprint or a via is not.
    /// <paramref name="Order"/> is the part's position in schematic-id order, filled in by <see cref="Build"/>.
    /// </summary>
    public sealed record Part(string SchematicId, LayoutInstance Instance, IReadOnlyList<LayoutPin?> Terminals,
                              bool Existing, bool Joinable)
    {
        public int Order { get; internal set; }
    }

    /// <summary>One end of a net: a part's terminal (1-based) and the cell pin it lands on.</summary>
    public sealed record End(Part Part, int Terminal, LayoutPin Pin);

    /// <summary>A joint: <paramref name="A"/> is the end first in schematic-id order.</summary>
    public sealed record Joint(string Net, End A, End B)
    {
        public End EndOf(Part part) => ReferenceEquals(A.Part, part) ? A : B;
        public Part Other(Part part) => ReferenceEquals(A.Part, part) ? B.Part : A.Part;
    }

    /// <summary>A net with layout pins that is not a joint, with why.</summary>
    public sealed record UnjoinedNet(string Net, NetReason Reason, IReadOnlyList<End> Ends);

    /// <summary>A connected component of the joints: its parts and its joints, both in schematic-id order.</summary>
    public sealed record Chain(IReadOnlyList<Part> Parts, IReadOnlyList<Joint> Joints);

    public IReadOnlyList<Part> Parts { get; private init; } = [];
    public IReadOnlyList<Joint> Joints { get; private init; } = [];
    public IReadOnlyList<UnjoinedNet> Unjoined { get; private init; } = [];
    public IReadOnlyList<Chain> Chains { get; private init; } = [];

    /// <summary>The nets a ratsnest line can sit on: every joint, and every other net with two or more pins.</summary>
    public int ConnectionCount => Joints.Count + Unjoined.Count(u => u.Reason != NetReason.OnePin);

    /// <summary>
    /// The graph over <paramref name="parts"/>, whose terminals sit on the nets <paramref name="extraction"/> bound
    /// them to (the extraction the planner already made — the schematic is never re-traversed, §9 step 1).
    /// </summary>
    public static JointGraph Build(NetExtractor.ExtractionResult extraction, IEnumerable<Part> parts)
    {
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, inst.NetBindings);

        var ordered = parts.OrderBy(p => p.SchematicId, Comparer<string>.Create(CompareIds)).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;

        // Nets in the order of their first end — parts in schematic-id order, terminals in order.
        var ends = new Dictionary<string, List<End>>(StringComparer.Ordinal);
        var netOrder = new List<string>();
        foreach (var part in ordered)
        {
            if (!bindings.TryGetValue(part.SchematicId, out var nets)) continue;
            for (int k = 1; k <= Math.Min(part.Terminals.Count, nets.Count); k++)
            {
                if (part.Terminals[k - 1] is not { } pin || nets[k - 1] is not { Length: > 0 } net) continue;
                if (!ends.TryGetValue(net, out var list)) { ends[net] = list = []; netOrder.Add(net); }
                list.Add(new End(part, k, pin));
            }
        }

        var joints = new List<Joint>();
        var unjoined = new List<UnjoinedNet>();
        foreach (string net in netOrder)
        {
            var list = ends[net];
            NetReason? reason =
                list.Count == 1 ? NetReason.OnePin
                : net == "0" ? NetReason.Ground
                : list.Count > 2 ? NetReason.ThreeOrMorePins
                : !list[0].Part.Joinable || !list[1].Part.Joinable ? NetReason.PadOrVia
                : list[0].Pin.Layer != list[1].Pin.Layer ? NetReason.LayerMismatch
                : null;
            if (reason is { } r) unjoined.Add(new UnjoinedNet(net, r, list));
            else joints.Add(new Joint(net, list[0], list[1]));
        }

        return new JointGraph
        {
            Parts = ordered,
            Joints = joints,
            Unjoined = unjoined,
            Chains = Components(ordered, joints),
        };
    }

    /// <summary>The connected components of <paramref name="joints"/>, each listed from its first part. A part
    /// with no joint is in no chain.</summary>
    private static List<Chain> Components(List<Part> parts, List<Joint> joints)
    {
        var root = parts.ToDictionary(p => p, p => p, ReferenceEqualityComparer.Instance as IEqualityComparer<Part>);
        Part Find(Part p)
        {
            while (!ReferenceEquals(root[p], p)) p = root[p] = root[root[p]];
            return p;
        }
        foreach (var j in joints)
        {
            Part a = Find(j.A.Part), b = Find(j.B.Part);
            if (ReferenceEquals(a, b)) continue;
            if (a.Order <= b.Order) root[b] = a; else root[a] = b;
        }

        var chains = new List<Chain>();
        foreach (var group in joints.GroupBy(j => Find(j.A.Part), ReferenceEqualityComparer.Instance as IEqualityComparer<Part>)
                                    .OrderBy(g => g.Key.Order))
        {
            var members = new HashSet<Part>(group.SelectMany(j => new[] { j.A.Part, j.B.Part }),
                                            ReferenceEqualityComparer.Instance as IEqualityComparer<Part>);
            chains.Add(new Chain(members.OrderBy(p => p.Order).ToList(), group.ToList()));
        }
        return chains;
    }

    /// <summary>
    /// Schematic-id order: the letters, then the number as a NUMBER, so ML2 comes before ML10 — the order a reader
    /// expects in a report line. Falls back to ordinal comparison, so the order is total and never depends on culture.
    /// </summary>
    public static int CompareIds(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }
            if (a[i] != b[j]) return a[i].CompareTo(b[j]);
            i++; j++;
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }
}
