using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-1 R-sdl1-1, extended by brief-sdl-2: which placed parts the schematic joins pin to pin, which footprint
/// pads attach side-on to a joint, and why every other net keeps its ratsnest. Nodes are placed parts with at least
/// one resolved pin; an edge is a <b>joint</b> — a net whose two layout pins meet end to end — or an
/// <see cref="Attachment"/>, a footprint pad laid against the side of a joint (SDL-2 D4/D5). The connected components
/// of both are the <see cref="Chain"/>s <see cref="ChainPlacer"/> lays out.
///
/// <para><b>Two kinds of pin</b> (SDL-2 R-sdl2-2). A line's pin is its own point, mid-way across its end edge. A
/// footprint's pin stands in the graph as its <see cref="PadJoint"/> — the middle of the pad's outer edge, facing the
/// pad's effective direction — so the placer joins a pad exactly as it joins a line, with no case of its own.</para>
///
/// <para>Pins are matched to schematic terminals by <see cref="SchematicLayoutOrientation.CellPinFor"/>, the rule
/// the orientation alignment and the ground vias already read — the caller resolves them, so there is no second
/// matcher here. Everything is ordered by schematic id (series rule S5): parts by <see cref="CompareIds"/>, nets
/// by their first end, joints by their first end.</para>
/// </summary>
public sealed class JointGraph
{
    /// <summary>What a part is to the chain: a line drawn from its own geometry, a part with pads (a footprint, an
    /// imported part, a placed cell), or a via, whose barrel joins at a point SDL-3 defines.</summary>
    public enum PartKind { Line, Pad, Via }

    /// <summary>Why a net with layout pins is not a joint.</summary>
    public enum NetReason
    {
        /// <summary>One layout pin: nothing to join and no ratsnest line, so never reported.</summary>
        OnePin,
        /// <summary>The ground net ("0"): its pins return through vias and the pour, not through each other.</summary>
        Ground,
        /// <summary>Three or more pins with no defined answer: three line ends, or three pads and no line (SDL-3).</summary>
        ThreeOrMorePins,
        /// <summary>The pins are on different layers: joining them needs a via (SDL-3).</summary>
        LayerMismatch,
        /// <summary>One end is a via, whose joint point is SDL-3's to define.</summary>
        Via,
        /// <summary>Parts on different sides of the board (SDL-2 D8): no joint without a via.</summary>
        SideMismatch,
    }

    /// <summary>Which side of the direction of travel a side-on pad goes (SDL-2 D4).</summary>
    public enum Side { Left, Right }

    /// <summary>
    /// One placed part. <paramref name="Terminals"/>[k − 1] is the pin schematic terminal k lands on, null where none
    /// does — for a <see cref="PartKind.Pad"/> part, its <see cref="PadJoint"/> pin. <paramref name="Existing"/>: already
    /// in the layout before this run, and so an anchor (SDL-1 D5). <paramref name="Order"/> is the part's position in
    /// schematic-id order, filled in by <see cref="Build"/>.
    /// </summary>
    public sealed record Part(string SchematicId, LayoutInstance Instance, IReadOnlyList<LayoutPin?> Terminals,
                              bool Existing, PartKind Kind)
    {
        public int Order { get; internal set; }
    }

    /// <summary>One end of a net: a part's terminal (1-based) and the pin it lands on.</summary>
    public sealed record End(Part Part, int Terminal, LayoutPin Pin);

    /// <summary>A joint: <paramref name="A"/> is the end first in schematic-id order.</summary>
    public sealed record Joint(string Net, End A, End B)
    {
        public End EndOf(Part part) => ReferenceEquals(A.Part, part) ? A : B;
        public Part Other(Part part) => ReferenceEquals(A.Part, part) ? B.Part : A.Part;
    }

    /// <summary>
    /// A footprint pad laid against the side of a joint, its outer edge on the line's edge, on the joint's centre line
    /// (SDL-2 D4, D5). <paramref name="Base"/> is a line end of the joint. <paramref name="Across"/> is the joint's
    /// other end: a second line end (D4, a shunt part where two lines meet) or the pad the line joins (D5).
    /// </summary>
    public sealed record Attachment(string Net, End Pad, End Base, End Across, Side Side);

    /// <summary>A net with layout pins that is not a joint, with why.</summary>
    public sealed record UnjoinedNet(string Net, NetReason Reason, IReadOnlyList<End> Ends);

    /// <summary>A connected component of the joints and attachments: its parts, joints and attachments, each in
    /// schematic-id order.</summary>
    public sealed record Chain(IReadOnlyList<Part> Parts, IReadOnlyList<Joint> Joints, IReadOnlyList<Attachment> Attachments)
    {
        public Chain(IReadOnlyList<Part> parts, IReadOnlyList<Joint> joints) : this(parts, joints, []) { }
    }

    public IReadOnlyList<Part> Parts { get; private init; } = [];
    public IReadOnlyList<Joint> Joints { get; private init; } = [];
    public IReadOnlyList<Attachment> Attachments { get; private init; } = [];

    /// <summary>A third or later pad at one joint (D4): not placed by the chain.</summary>
    public IReadOnlyList<(string Net, End Pad)> Overflow { get; private init; } = [];

    public IReadOnlyList<UnjoinedNet> Unjoined { get; private init; } = [];
    public IReadOnlyList<Chain> Chains { get; private init; } = [];

    /// <summary>The nets a ratsnest line can sit on: every joint, and every other net with two or more pins. A pad
    /// attached side-on is its own connection, joined when it is placed against its joint.</summary>
    public int ConnectionCount => Joints.Count + Attachments.Count + Overflow.Count
                                  + Unjoined.Count(u => u.Reason != NetReason.OnePin);

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
        var netsOf = new Dictionary<Part, List<string>>(ReferenceEqualityComparer.Instance as IEqualityComparer<Part>);
        foreach (var part in ordered)
        {
            netsOf[part] = [];
            if (!bindings.TryGetValue(part.SchematicId, out var nets)) continue;
            for (int k = 1; k <= Math.Min(part.Terminals.Count, nets.Count); k++)
            {
                if (part.Terminals[k - 1] is not { } pin || nets[k - 1] is not { Length: > 0 } net) continue;
                if (!ends.TryGetValue(net, out var list)) { ends[net] = list = []; netOrder.Add(net); }
                list.Add(new End(part, k, pin));
                netsOf[part].Add(net);
            }
        }

        var joints = new List<Joint>();
        var attachments = new List<Attachment>();
        var overflow = new List<(string, End)>();
        var unjoined = new List<UnjoinedNet>();
        foreach (string net in netOrder)
        {
            var list = ends[net];
            NetReason? reason =
                list.Count == 1 ? NetReason.OnePin
                : net == "0" ? NetReason.Ground
                : list.Any(e => e.Part.Kind == PartKind.Via) ? NetReason.Via
                // D8 before the layers: a part flipped to the bottom has its pins on the bottom copper too, and
                // the side is the reason.
                : list.Any(e => e.Part.Instance.Side != list[0].Part.Instance.Side) ? NetReason.SideMismatch
                : list.Any(e => e.Pin.Layer != list[0].Pin.Layer) ? NetReason.LayerMismatch
                : null;
            if (reason is { } r) { unjoined.Add(new UnjoinedNet(net, r, list)); continue; }

            if (list.Count == 2) { joints.Add(new Joint(net, list[0], list[1])); continue; }

            var lines = list.Where(e => e.Part.Kind == PartKind.Line).ToList();
            var pads = list.Where(e => e.Part.Kind == PartKind.Pad).ToList();
            End baseEnd, across;
            List<End> side;
            if (lines.Count == 2)
            {
                // D4: the two lines join each other; every pad on the net goes against the joint's side.
                (baseEnd, across) = (lines[0], lines[1]);
                joints.Add(new Joint(net, lines[0], lines[1]));
                side = pads;
            }
            else if (lines.Count == 1)
            {
                // D5: the line joins the pad whose part carries the chain on to more distributed parts — the first such
                // in schematic-id order, else the first pad — and the rest go against the side of that joint.
                var joined = pads.FirstOrDefault(p => LeadsToALine(p.Part, net)) ?? pads[0];
                (baseEnd, across) = (lines[0], joined);
                joints.Add(new Joint(net, lines[0].Part.Order <= joined.Part.Order ? lines[0] : joined,
                                     lines[0].Part.Order <= joined.Part.Order ? joined : lines[0]));
                side = pads.Where(p => !ReferenceEquals(p, joined)).ToList();
            }
            else { unjoined.Add(new UnjoinedNet(net, NetReason.ThreeOrMorePins, list)); continue; }

            for (int i = 0; i < side.Count; i++)
            {
                if (i < 2) attachments.Add(new Attachment(net, side[i], baseEnd, across, i == 0 ? Side.Left : Side.Right));
                else overflow.Add((net, side[i]));
            }
        }

        return new JointGraph
        {
            Parts = ordered,
            Joints = joints,
            Attachments = attachments,
            Overflow = overflow,
            Unjoined = unjoined,
            Chains = Components(ordered, joints, attachments),
        };

        // Whether one of the part's OTHER nets reaches a line through parts and nets that do not pass back through
        // this one — "its other pad is on a net that leads to more distributed parts" (D5).
        bool LeadsToALine(Part start, string through)
        {
            var seenNets = new HashSet<string>(StringComparer.Ordinal) { through, "0" };
            var seenParts = new HashSet<Part>(ReferenceEqualityComparer.Instance as IEqualityComparer<Part>) { start };
            var queue = new Queue<string>(netsOf[start].Where(n => seenNets.Add(n)));
            while (queue.Count > 0)
            {
                foreach (var e in ends[queue.Dequeue()])
                {
                    if (!seenParts.Add(e.Part)) continue;
                    if (e.Part.Kind == PartKind.Line) return true;
                    foreach (string n in netsOf[e.Part]) if (seenNets.Add(n)) queue.Enqueue(n);
                }
            }
            return false;
        }
    }

    /// <summary>The connected components of <paramref name="joints"/> and <paramref name="attachments"/>, each listed
    /// from its first part. A part with neither is in no chain.</summary>
    private static List<Chain> Components(List<Part> parts, List<Joint> joints, List<Attachment> attachments)
    {
        var cmp = ReferenceEqualityComparer.Instance as IEqualityComparer<Part>;
        var root = parts.ToDictionary(p => p, p => p, cmp);
        Part Find(Part p)
        {
            while (!ReferenceEquals(root[p], p)) p = root[p] = root[root[p]];
            return p;
        }
        void Union(Part x, Part y)
        {
            Part a = Find(x), b = Find(y);
            if (ReferenceEquals(a, b)) return;
            if (a.Order <= b.Order) root[b] = a; else root[a] = b;
        }
        foreach (var j in joints) Union(j.A.Part, j.B.Part);
        foreach (var a in attachments) Union(a.Pad.Part, a.Base.Part);

        var chains = new List<Chain>();
        var jointsBy = joints.ToLookup(j => Find(j.A.Part), cmp);
        var attachBy = attachments.ToLookup(a => Find(a.Base.Part), cmp);
        foreach (var key in jointsBy.Select(g => g.Key).Concat(attachBy.Select(g => g.Key))
                                    .Distinct(cmp).OrderBy(k => k.Order))
        {
            var members = new HashSet<Part>(cmp);
            foreach (var j in jointsBy[key]) { members.Add(j.A.Part); members.Add(j.B.Part); }
            foreach (var a in attachBy[key]) { members.Add(a.Pad.Part); members.Add(a.Base.Part); }
            chains.Add(new Chain(members.OrderBy(p => p.Order).ToList(), jointsBy[key].ToList(), attachBy[key].ToList()));
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
