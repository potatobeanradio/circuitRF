// Traces to line elements — brief-artsch-5-traces-to-line-elements.md; overview D3, D13, D14, D16, D19;
// docs/design/artwork-to-schematic.md §6.
//
// Every signal island's copper between parts, ports and vias becomes a chain of line elements, connected node to
// node to what AS-3 and AS-4 found. The trace review is the READER (R-as5-1): its pieces, chains, end kinds,
// stations, corners and junctions are taken as they are, and nothing here walks the copper a second time.
//
//   1. the attachments — every modelled part's terminals, every kept via's ends, every port — each a node;
//   2. each trace segmented (LineSegmentation), its ends still placeholders;
//   3. each junction resolved (LineJunctions): an MTEE or MCROSS, or a plain node;
//   4. each remaining end attached: the nearest attachment on its island within reach — a via's line stops at
//      its land's edge — else the nearest other loose end within two widths, else an open end, listed;
//   4½. each pad one node: an island's copper that no trace piece covers is pad copper, and the line ends running into
//      one pad and the part terminals, via ends and ports on it are joined by an ideal short (PadNodes);
//   5. a line with no length left is absorbed (its nodes become one), and anything on an island no line
//      reached joins the nearest line end there, or the island's other attachments;
//   6. the elements written in SI, a TLIN's Z, εeff and loss from its cuts (R-as5-7);
//   7. coupled pairs (R-as5-8) and the report (R-as5-9).

using Clipper2Lib;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What line recognition reads.</summary>
internal sealed record LineRecognitionContext
{
    public required BoardGraph Board { get; init; }
    public required PartsTable Parts { get; init; }
    public required TraceImpedanceReport Review { get; init; }
    public required Technology Technology { get; init; }
    public required RecognitionOptions Options { get; init; }

    /// <summary>The top of the analysis range (D15) — a TLIN's <c>F</c> and the coupled-pair λ.</summary>
    public required double TopFrequencyHz { get; init; }

    public required RailLengthFormat Format { get; init; }
}

/// <summary>R-as5-1 … R-as5-9, written once.</summary>
public static class LineRecognition
{
    /// <summary>An end attaches to a part, port or via within this many of its widths plus
    /// <see cref="AttachReachMicrons"/>.</summary>
    public const double AttachReachWidths = 2;
    public const double AttachReachMicrons = 1000;

    /// <summary>Two loose ends on one island within this many widths are one node.</summary>
    public const double LooseEndWidths = 2;

    /// <summary>Two segments are a coupled pair within this angle, edge gap (in mean widths) and overlap
    /// (a fraction of the wavelength at the top frequency) — R-as5-8.</summary>
    public const double CoupledParallelDeg = 2;
    public const double CoupledGapWidths = 3;
    public const double CoupledOverlapWavelengths = 1.0 / 20;

    private const double C0 = 299_792_458.0;
    private const double Mu0 = 4e-7 * Math.PI;
    private const double DbPerNeper = 8.685889638065035;

    private sealed record Attachment(string Name, LineNodeKind Kind, int Island, long X, long Y, LayerKey? Layer)
    {
        public string? Refdes { get; init; }
        public int Terminal { get; init; } = -1;
        public int Via { get; init; } = -1;
        public int Port { get; init; }
        public double LandRadius { get; init; }
    }

    internal static LineRecognitionResult Recognize(LineRecognitionContext ctx, RecognitionReport report)
    {
        var board = ctx.Board;
        var options = ctx.Options;
        var tech = ctx.Technology;
        int dbu = board.DbuPerMicron;
        var fmt = ctx.Format;

        // ── 1. the attachments ───────────────────────────────────────────────────────────────────────
        var attachments = new List<Attachment>();
        foreach (var row in ctx.Parts.Rows.Where(r => r.IsModelled))
            for (int t = 0; t < row.Terminals.Count; t++)
                if (row.Terminals[t] is { Island: >= 0 } term)
                    attachments.Add(new Attachment($"{row.Refdes}.{t + 1}", LineNodeKind.PartTerminal, term.Island, term.X, term.Y, term.Layer)
                                    { Refdes = row.Refdes, Terminal = t });
        for (int v = 0; v < board.Vias.Count; v++)
        {
            var via = board.Vias[v];
            if (via.Element == ViaElement.None) continue;
            double r = 0.5 * Math.Max(via.PadDbu, via.DrillDbu);
            for (int i = 0; i < via.Islands.Count; i++)
                attachments.Add(new Attachment($"V{v + 1}.{i + 1}", LineNodeKind.Via, via.Islands[i], via.X, via.Y, null)
                                { Via = v, LandRadius = r });
        }
        foreach (var port in board.Ports)
            attachments.Add(new Attachment($"P{port.Number}", LineNodeKind.Port, port.Island, port.X, port.Y, port.Layer) { Port = port.Number });
        var attachmentOf = attachments.ToDictionary(a => a.Name);

        // ── 2. every trace on a signal island, segmented ─────────────────────────────────────────────
        var counts = new SegmentationCounts();
        var traces = new List<TraceLines>();
        var binders = new Dictionary<LayerKey, LineBinder>();
        foreach (var layer in ctx.Review.Layers)
        {
            string? conductor = tech.Stackup.Layers.FirstOrDefault(
                l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(layer.Layer))?.Name;
            if (conductor is null) continue;
            var binder = binders[layer.Layer] = new LineBinder(tech, conductor);
            foreach (var run in layer.Traces)
            {
                if (run.Pieces.Count == 0) continue;
                var p = run.Pieces[0];
                if (board.IslandAt((p.X0 + p.X1) / 2, (p.Y0 + p.Y1) / 2, run.Layer) is not { Kind: IslandKind.Signal } island) continue;
                var onIsland = attachments.Where(a => a.Island == island.Id).Select(a => (a.Name, a.X, a.Y)).ToList();
                traces.Add(LineSegmentation.Segment(run, island.Id, options, binder, dbu, counts, onIsland));
            }
        }

        // ── 3. the junctions ─────────────────────────────────────────────────────────────────────────
        var junctionElements = new List<LineDraft>();
        var plainJunctions = new List<(TraceJunction Junction, string Why)>();
        var coplanarJunctions = new List<TraceJunction>();
        var continuations = new List<(TraceJunction Junction, JunctionArm A, JunctionArm B)>();
        var resolved = new HashSet<(TraceLines, bool)>();
        var byId = traces.ToDictionary(t => t.Run.Id);
        var islandOfJunction = new Dictionary<string, int>();
        foreach (var junction in ctx.Review.Layers.SelectMany(l => l.Junctions))
        {
            var arms = new List<JunctionArm>();
            foreach (var arm in junction.Arms)
            {
                if (!byId.TryGetValue(arm.TraceId, out var t)) continue;
                arms.Add(new JunctionArm(arm.AtStart ? t.First : t.Last, arm.AtStart, arm.AtStart ? t.Start : t.End));
                resolved.Add((t, arm.AtStart));
                islandOfJunction[junction.Id] = t.Island;
            }
            if (arms.Count == 0) continue;
            var reading = LineJunctions.Resolve(junction, arms);
            if (reading.Element is { } e) junctionElements.Add(e);
            if (reading.Plain is { } why) plainJunctions.Add((junction, why));
            if (reading.Coplanar) coplanarJunctions.Add(junction);
            if (arms.Count == 2) continuations.Add((junction, arms[0], arms[1]));
        }

        // ── 4. the other ends ────────────────────────────────────────────────────────────────────────
        var parent = new Dictionary<string, string>();
        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p);
            return x;
        }
        void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (Rank(ra) <= Rank(rb)) parent[rb] = ra; else parent[ra] = rb;
        }
        int Rank(string name) => attachmentOf.TryGetValue(name, out var a) ? (int)a.Kind
                               : name.StartsWith('J') ? (int)LineNodeKind.Junction
                               : name.StartsWith('O') ? (int)LineNodeKind.Open : (int)LineNodeKind.Between;

        var reached = traces.SelectMany(t => t.Taps).ToHashSet();
        foreach (var (tap, same) in traces.SelectMany(t => t.TapAliases)) Union(tap, same);
        var loose = new List<(TraceLines T, bool AtStart, ChainEnd End, string Placeholder)>();
        (Attachment? Best, double D) Nearest(TraceLines t, ChainEnd end, Attachment? except)
        {
            double reach = AttachReachWidths * end.Width + AttachReachMicrons * dbu;
            Attachment? best = null;
            double bestD = double.MaxValue;
            foreach (var a in attachments)
            {
                if (a.Island != t.Island || ReferenceEquals(a, except)) continue;
                double d = Math.Sqrt(Sq(a.X - end.X) + Sq(a.Y - end.Y));
                double limit = a.Kind == LineNodeKind.Via ? a.LandRadius + end.Width : reach;
                if (d <= limit && d < bestD) { best = a; bestD = d; }
            }
            return (best, bestD);
        }

        foreach (var t in traces)
        {
            // Each end's nearest attachment — but never ONE attachment for both ends of a trace. A trace shorter than
            // the reach finds the part, port or via at its near end from its far end too, and became a line from that
            // node to itself (designer report, round 16: a 0.5 mm stub at a port label read as CPWG P3–P3). The nearer
            // end keeps it; the other takes its next nearest, or is loose.
            var start = resolved.Contains((t, true)) ? (Best: (Attachment?)null, D: double.MaxValue) : Nearest(t, t.Start, null);
            var finish = resolved.Contains((t, false)) ? (Best: (Attachment?)null, D: double.MaxValue) : Nearest(t, t.End, null);
            if (start.Best is { } shared && ReferenceEquals(shared, finish.Best))
            {
                if (start.D <= finish.D) finish = Nearest(t, t.End, shared);
                else start = Nearest(t, t.Start, shared);
            }

            foreach (var (atStart, end, placeholder, best) in new[]
                     { (true, t.Start, t.StartNode, start.Best), (false, t.End, t.EndNode, finish.Best) })
            {
                if (resolved.Contains((t, atStart))) continue;
                var line = atStart ? t.First : t.Last;
                if (best is null) { loose.Add((t, atStart, end, placeholder)); continue; }
                // A via owns its land: the line stops at the land's edge (R-as5-5).
                if (best.Kind == LineNodeKind.Via)
                    line.L += (best.X - end.X) * end.Dx + (best.Y - end.Y) * end.Dy - best.LandRadius;
                SetEnd(line, atStart, best.Name);
                reached.Add(best.Name);
            }
        }

        // Loose ends: two within two widths on one island are one node; the rest are open ends.
        var openEnds = new List<(string Node, ChainEnd End)>();
        int open = 0, joint = 0;
        var looseNode = new string?[loose.Count];
        for (int i = 0; i < loose.Count; i++)
        {
            if (looseNode[i] is not null) continue;
            var (ti, si, ei, _) = loose[i];
            for (int j = i + 1; j < loose.Count; j++)
            {
                var (tj, sj, ej, _) = loose[j];
                if (tj.Island != ti.Island || ReferenceEquals(ti, tj)) continue;
                if (Math.Sqrt(Sq(ej.X - ei.X) + Sq(ej.Y - ei.Y)) > LooseEndWidths * Math.Max(ei.Width, ej.Width)) continue;
                looseNode[i] ??= $"N{++joint}";
                looseNode[j] = looseNode[i];
            }
            if (looseNode[i] is null)
            {
                looseNode[i] = $"O{++open}";
                openEnds.Add((looseNode[i]!, ei));
            }
        }
        for (int i = 0; i < loose.Count; i++)
        {
            var (t, atStart, _, _) = loose[i];
            SetEnd(atStart ? t.First : t.Last, atStart, looseNode[i]!);
        }

        // ── 4½. pads: everything on one piece of pad copper is one node ──────────────────────────────
        // A line ends at a pad's edge and a part's terminal stands on the pad (R-as5-5), so step 4 meets them only within a
        // line end's reach. On a wide pad that is not enough: a device pad where the input line, two bias lines and two shunt
        // parts meet came out as one node per line end, none joined, with the pad itself not modelled. The pad is modelled as
        // an ideal short now; modelling it as a wide line or a junction is a later step.
        var pads = PadNodes(ctx, traces, attachments, dbu);
        var onPads = new HashSet<string>();
        foreach (var pad in pads)
        {
            for (int i = 1; i < pad.Members.Count; i++) Union(pad.Members[0], pad.Members[i]);
            onPads.UnionWith(pad.Members);
        }
        reached.UnionWith(onPads.Where(attachmentOf.ContainsKey));
        openEnds.RemoveAll(o => onPads.Contains(o.Node));

        // A "junction" of two arms is the review's reading of a sliver between two pieces — both ends of a
        // short piece near both of its neighbours. The piece is gone but its length is in the arms, run to the
        // centre; where they are one line type and one width they are ONE line, and the sliver is absorbed.
        var merged = new HashSet<LineDraft>();
        foreach (var (junction, a, b) in continuations)
        {
            // Unless a part's terminal, a via or a port is inside it: a shunt part's pad standing on a line and wider
            // than it is read by the review as two arms meeting there (brief-artsch-9's round trip). The arms stay two
            // lines and meet at the terminal, as a tap on one piece does (R-as5-3).
            if (islandOfJunction.TryGetValue(junction.Id, out int tapIsland) && TapBetween(a, b, attachments, tapIsland, dbu) is { } tap)
            {
                foreach (var arm in new[] { a, b })
                {
                    arm.Line.L += (tap.X - junction.X) * arm.End.Dx + (tap.Y - junction.Y) * arm.End.Dy;
                    SetEnd(arm.Line, arm.AtStart, tap.Name);
                }
                reached.Add(tap.Name);
                continue;
            }
            var (into, from) = (a.Line, b.Line);
            if (into == from || merged.Contains(into) || merged.Contains(from) || into.Kind != from.Kind
                || !LineSegmentation.SameClass(into.W, from.W, dbu)) continue;
            string far = b.AtStart ? from.Nodes[^1] : from.Nodes[0];
            if (a.AtStart) into.Nodes[0] = far; else into.Nodes[^1] = far;
            into.Absorb(from, a.AtStart, b.AtStart);
            merged.Add(from);
            counts.Slivers.Add((junction.X, junction.Y));
        }

        // ── 5. every draft, in order; a line with no length left is absorbed ───────────────────────────
        var drafts = traces.SelectMany(t => t.Drafts).Concat(junctionElements).Where(d => !merged.Contains(d)).ToList();
        var absorbed = new List<(long X, long Y)>();
        foreach (var d in drafts.Where(d => d.IsLine && d.L <= 0).ToList())
        {
            Union(d.Nodes[0], d.Nodes[1]);
            drafts.Remove(d);
            if (d.Anchor.Count > 0) absorbed.Add(d.Anchor[0]);
        }

        // Attachments no line reached: the nearest line end on their island, else the island's first attachment. A line end
        // farther than a line end reaches (step 4) is copper no line models — a trace the review did not hand over — and is
        // said: the join is an ideal short across it.
        var endsOnIsland = new Dictionary<int, List<(string Node, double X, double Y, double W)>>();
        foreach (var t in traces)
        {
            if (!endsOnIsland.TryGetValue(t.Island, out var list)) endsOnIsland[t.Island] = list = [];
            list.Add((t.First.Nodes[0], t.Start.X, t.Start.Y, t.Start.Width));
            list.Add((t.Last.Nodes[^1], t.End.X, t.End.Y, t.End.Width));
        }
        foreach (var j in junctionElements)
            if (islandOfJunction.TryGetValue(j.Source, out int ji))
                endsOnIsland[ji].Add((j.Nodes[0], j.Anchor[0].X, j.Anchor[0].Y, j.Widths.DefaultIfEmpty(0).Max()));
        var firstOnIsland = new Dictionary<int, string>();
        // Each: what was joined, where, what to, where — an attachment to a line end out of reach, or two members of a pad
        // its own copper does not make one pad (PadNodes).
        var farJoins = new List<(string A, long Ax, long Ay, LayerKey? Layer, string B, long Bx, long By)>();
        foreach (var pad in pads) farJoins.AddRange(pad.Split);
        foreach (var a in attachments.Where(a => !reached.Contains(a.Name)))
        {
            if (endsOnIsland.TryGetValue(a.Island, out var ends) && ends.Count > 0)
            {
                var e = ends.MinBy(e => Sq(e.X - a.X) + Sq(e.Y - a.Y));
                Union(a.Name, e.Node);
                double d = Math.Sqrt(Sq(e.X - a.X) + Sq(e.Y - a.Y));
                if (d > AttachReachWidths * e.W + AttachReachMicrons * dbu)
                    farJoins.Add((a.Name, a.X, a.Y, a.Layer, "the line end", (long)Math.Round(e.X), (long)Math.Round(e.Y)));
            }
            else if (firstOnIsland.TryGetValue(a.Island, out var first)) Union(a.Name, first);
            else firstOnIsland[a.Island] = a.Name;
        }

        // ── 6. the elements ─────────────────────────────────────────────────────────────────────────
        var conductorOf = binders.ToDictionary(kv => kv.Key, kv => kv.Value.SignalConductor);
        var layerName = ctx.Review.Layers.ToDictionary(l => l.Layer, l => l.Name);
        var elements = new List<LineElement>();
        var numbering = new Dictionary<LineElementType, int>();
        var lineDrafts = drafts.Where(d => d.IsLine).ToList();
        foreach (var d in drafts)
        {
            numbering[d.Type] = numbering.GetValueOrDefault(d.Type) + 1;
            var reference = d.ReferenceBelow ?? d.ReferenceAbove;
            var binder = binders.GetValueOrDefault(d.Layer);
            elements.Add(new LineElement
            {
                Name = $"{d.Type}{numbering[d.Type]}",
                Type = d.Type,
                Nodes = [.. d.Nodes.Select(Find)],
                Parameters = ParametersOf(d, ctx, binder, lineDrafts, dbu, out bool unsolved),
                Layer = d.Layer,
                LayerName = layerName.GetValueOrDefault(d.Layer, ""),
                SignalLayer = conductorOf.GetValueOrDefault(d.Layer),
                GroundReference = binder?.GroundOverride(reference),
                Source = d.Source,
                Anchor = [.. d.Anchor],
                GapLeft = d.Type is LineElementType.MLIN or LineElementType.CPWG ? Metres(d.GapLeft, dbu) : null,
                GapRight = d.Type is LineElementType.MLIN or LineElementType.CPWG ? Metres(d.GapRight, dbu) : null,
                Width = d.IsLine ? d.W / dbu * 1e-6 : null,
                Z0 = d.Z0,
                Eeff = d.Eeff,
                Fallback = d.Type == LineElementType.TLIN ? (unsolved ? LineTypeChoice.UnsolvedReason : d.Reason) : null,
                Unsolved = unsolved,
            });
        }
        // A CPWG whose gaps differ by more than 1.5× is a TLIN (D13) — decided on the segment's mean gaps.
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            if (e.Type != LineElementType.CPWG) continue;
            if (e.GapLeft is { } gl && e.GapRight is { } gr && Math.Max(gl, gr) <= LineTypeChoice.MaxGapRatio * Math.Min(gl, gr)) continue;
            var d = drafts[i];
            d.Type = LineElementType.TLIN;
            d.Reasons.Clear();
            d.Reasons[LineTypeChoice.AsymmetricReason] = 1;
            numbering[LineElementType.TLIN] = numbering.GetValueOrDefault(LineElementType.TLIN) + 1;
            elements[i] = e with
            {
                Name = $"TLIN{numbering[LineElementType.TLIN]}", Type = LineElementType.TLIN,
                Parameters = ParametersOf(d, ctx, binders.GetValueOrDefault(d.Layer), lineDrafts, dbu, out _),
                Fallback = LineTypeChoice.AsymmetricReason, GapLeft = null, GapRight = null,
            };
        }

        var nodes = new Dictionary<string, LineNode>();
        void AddNode(string name, LineNodeKind kind, int island, long x, long y, LayerKey? layer, Attachment? a = null)
        {
            string final = Find(name);
            if (nodes.ContainsKey(final)) return;
            if (final != name && attachmentOf.TryGetValue(final, out var rep))
                nodes[final] = Node(rep);
            else
                nodes[final] = a is null ? new LineNode(final, kind, island, x, y, layer) : Node(a);
        }
        LineNode Node(Attachment a) => new(a.Name, a.Kind, a.Island, a.X, a.Y, a.Layer)
        { Refdes = a.Refdes, Terminal = a.Terminal, Via = a.Via, Port = a.Port };
        foreach (var a in attachments) AddNode(a.Name, a.Kind, a.Island, a.X, a.Y, a.Layer, a);
        foreach (var t in traces)
            foreach (var d in t.Drafts)
                for (int i = 0; i < d.Nodes.Count; i++)
                {
                    var (x, y) = d.Anchor.Count == 0 ? (0L, 0L) : i == 0 ? d.Anchor[0] : d.Anchor[^1];
                    string name = d.Nodes[i];
                    var kind = name.StartsWith('O') ? LineNodeKind.Open : name.StartsWith('J') ? LineNodeKind.Junction : LineNodeKind.Between;
                    AddNode(name, kind, t.Island, x, y, t.Run.Layer);
                }
        foreach (var j in junctionElements)
            foreach (var name in j.Nodes)
                AddNode(name, LineNodeKind.Junction, islandOfJunction.GetValueOrDefault(j.Source, -1), j.Anchor[0].X, j.Anchor[0].Y, j.Layer);

        var joined = parent.Keys.Concat(nodes.Keys).Concat(attachments.Select(a => a.Name)).Distinct()
                           .ToDictionary(k => k, Find);
        var result = new LineRecognitionResult(elements, [.. nodes.Values])
        {
            Joined = joined, Attachments = [.. attachments.Select(Node)],
        };

        // ── 7. coupled pairs, and the report ──────────────────────────────────────────────────────────
        var coupled = CoupledPairs(elements, ctx.TopFrequencyHz, dbu);
        Report(result, report, options, counts, plainJunctions, coplanarJunctions, openEnds, absorbed, coupled, fmt);
        report.Add(RecognitionFindingClass.PadsJoinTheirMembers, pads.Count,
            $"{Plural(pads.Count, "pad joins its", "pads each join their")} line ends and part terminals into one node, the pad an " +
            "ideal short (it is not modelled as a line or a junction): " +
            string.Join("; ", pads.Take(8).Select(p => $"{fmt.Point((p.Box.MinX + p.Box.MaxX) / 2, (p.Box.MinY + p.Box.MaxY) / 2)}, " +
                                                    $"{(p.Box.MaxX - p.Box.MinX) / (1000.0 * dbu):0.##} × {(p.Box.MaxY - p.Box.MinY) / (1000.0 * dbu):0.##} mm, " +
                                                    $"{Plural(p.Ends, "line end", "line ends")} and {Plural(p.Terminals, "terminal", "terminals")}")) +
            (pads.Count > 8 ? "; …" : "") + ".",
            [.. pads.Select(p => new RecognitionAnchor((p.Box.MinX + p.Box.MaxX) / 2, (p.Box.MinY + p.Box.MaxY) / 2))]);
        report.Add(RecognitionFindingClass.CopperJoinedWithoutLine, farJoins.Count,
            $"{Plural(farJoins.Count, "join was", "joins were")} made across copper no line models — a trace the reading did not " +
            "take, shorted: " +
            string.Join("; ", farJoins.Take(8).Select(f => $"{f.A} at {fmt.Point(f.Ax, f.Ay)} and {f.B} at {fmt.Point(f.Bx, f.By)}, " +
                                                           $"{Math.Sqrt(Sq(f.Bx - f.Ax) + Sq(f.By - f.Ay)) / dbu / 1000:0.##} mm apart")) +
            (farJoins.Count > 8 ? "; …" : "") + ".",
            [.. farJoins.SelectMany(f => new[] { new RecognitionAnchor(f.Ax, f.Ay, f.Layer), new RecognitionAnchor(f.Bx, f.By, f.Layer) })]);
        return result;
    }

    /// <summary>
    /// The pads of the signal islands, each with what is on it: an island's copper that no trace piece's strip covers, split
    /// into its connected regions, and in each the node of every line end the trace review names a "pad" end whose step
    /// beyond it lands there, and every part terminal, via end and port inside it. A region with fewer than two members is
    /// no join. A junction's centre is not a pad: its arms end on it as "junction", never "pad".
    /// <para>The region is only what the review RETURNED taken away, so a trace it did not read (an arc, for one) is still in
    /// it. So each region is also cut to its cores: eroded by half of <see cref="TraceImpedanceAnalysis.PadWidthStep"/> times the
    /// widest line ending in it (else the widest trace on its layer), which takes away all copper no wider than the lines by the
    /// review's own pad-to-trace step, and grown back by as much. A member on no core, or on another core than the first, is
    /// still joined but named in <c>Split</c>, a join across copper no line models; a region with no core at all names a
    /// member farther than a line end reaches from the first. A distance alone could not tell a pad: a line in and a line
    /// out of a 4.5 mm device pad are 4.5 mm apart, past a 1.1 mm line end's reach.</para>
    /// </summary>
    private static List<(List<string> Members, int Ends, int Terminals, Bbox Box,
                         List<(string A, long Ax, long Ay, LayerKey? Layer, string B, long Bx, long By)> Split)> PadNodes(
        LineRecognitionContext ctx, List<TraceLines> traces, List<Attachment> attachments, int dbu)
    {
        var board = ctx.Board;
        var copper = board.Copper;
        var strips = new Dictionary<LayerKey, List<(Bbox Box, Path64 Path)>>();
        foreach (var layer in ctx.Review.Layers)
            foreach (var run in layer.Traces)
                foreach (var (x0, y0, x1, y1, w) in run.Pieces)
                {
                    double len = Math.Sqrt(Sq(x1 - x0) + Sq(y1 - y0));
                    if (len <= 0) continue;
                    // The strip a piece covers, a micron wider each side so the copper along its edges goes with it.
                    double h = 0.5 * w + dbu, nx = -(y1 - y0) / len * h, ny = (x1 - x0) / len * h;
                    var path = Clipper.MakePath(new[] { (long)(x0 + nx), (long)(y0 + ny), (long)(x1 + nx), (long)(y1 + ny),
                                                        (long)(x1 - nx), (long)(y1 - ny), (long)(x0 - nx), (long)(y0 - ny) });
                    long r = (long)Math.Ceiling(h);
                    var box = new Bbox(Math.Min(x0, x1) - r, Math.Min(y0, y1) - r, Math.Max(x0, x1) + r, Math.Max(y0, y1) + r);
                    (strips.TryGetValue(run.Layer, out var list) ? list : strips[run.Layer] = []).Add((box, path));
                }

        var widestOn = ctx.Review.Layers.Where(l => l.Traces.Count > 0).GroupBy(l => l.Layer)
                              .ToDictionary(g => g.Key, g => g.SelectMany(l => l.Traces).Max(t => t.WidthMax));
        var pads = new List<(List<string>, int, int, Bbox, List<(string, long, long, LayerKey?, string, long, long)>)>();
        for (int piece = 0; piece < copper.Count && piece < board.IslandOfPiece.Length; piece++)
        {
            int island = board.IslandOfPiece[piece];
            if (island < 0 || board.Islands[island].Kind != IslandKind.Signal) continue;
            var layer = copper.LayerOfPiece(piece);
            var ends = new List<(double X, double Y, string Node, double W)>();
            foreach (var t in traces.Where(t => t.Island == island && t.Run.Layer == layer))
                foreach (var (end, node) in new[] { (t.Start, t.First.Nodes[0]), (t.End, t.Last.Nodes[^1]) })
                    if (end.Kind == "pad")
                        ends.Add((end.X + 0.5 * end.Width * end.Dx, end.Y + 0.5 * end.Width * end.Dy, node, end.Width));
            var on = attachments.Where(a => a.Island == island && (a.Layer is null || a.Layer == layer)).ToList();
            if (ends.Count + on.Count < 2) continue;

            var bounds = copper.BoundsOfPiece(piece);
            var cut = new Paths64(strips.GetValueOrDefault(layer, []).Where(s => s.Box.Intersects(bounds)).Select(s => s.Path));
            var rest = cut.Count == 0 ? copper.PathsOfPiece(piece) : Clipper.Difference(copper.PathsOfPiece(piece), cut, LayoutClipper.Rule);
            foreach (var region in Drc.DrcRegions.Components(rest))
            {
                var endsIn = ends.Where(e => Regions.Contains(region, (long)Math.Round(e.X), (long)Math.Round(e.Y))).ToList();
                var onIt = on.Where(a => Regions.Contains(region, a.X, a.Y)).ToList();
                var members = endsIn.Select(e => e.Node).Concat(onIt.Select(a => a.Name)).Distinct().ToList();
                if (members.Count < 2) continue;
                var box = Bbox.Empty;
                foreach (var path in region) foreach (var pt in path) box = box.Union(new Bbox(pt.X, pt.Y, pt.X, pt.Y));

                var points = onIt.Select(a => (Name: a.Name, X: a.X, Y: a.Y))
                                 .Concat(endsIn.Select(e => (Name: "the line end", X: (long)Math.Round(e.X), Y: (long)Math.Round(e.Y)))).ToList();
                var split = new List<(string, long, long, LayerKey?, string, long, long)>();
                double wLine = endsIn.Count > 0 ? endsIn.Max(e => e.W) : widestOn.GetValueOrDefault(layer);
                if (wLine > 0)
                {
                    // Cut away what is no wider than the lines by the review's pad-to-trace step: what is left are the cores,
                    // each grown back by as much to the copper it stands for. A member on none of them, or on another core
                    // than the first, is joined to it across copper no line models.
                    double r = 0.5 * TraceImpedanceAnalysis.PadWidthStep * wLine;
                    var cores = Drc.DrcRegions.Components(Clipper.InflatePaths(region, -r, JoinType.Miter, EndType.Polygon))
                                    .Select(c => Clipper.InflatePaths(c, r + dbu, JoinType.Miter, EndType.Polygon)).ToList();
                    if (cores.Count == 0)
                    {
                        // No wider anywhere than its lines: one point to another past a line end's reach is not one pad.
                        double reach = AttachReachWidths * wLine + AttachReachMicrons * dbu;
                        var first = points[0];
                        foreach (var other in points.Skip(1).Where(o => Math.Sqrt(Sq(o.X - first.X) + Sq(o.Y - first.Y)) > reach))
                            split.Add((first.Name, first.X, first.Y, layer, other.Name, other.X, other.Y));
                    }
                    else
                    {
                        var coreOf = points.Select(p => cores.FindIndex(c => Regions.Contains(c, p.X, p.Y))).ToList();
                        int anchor = coreOf.FindIndex(c => c >= 0);
                        var first = points[Math.Max(0, anchor)];
                        var seen = new HashSet<int> { anchor >= 0 ? coreOf[anchor] : -2 };
                        for (int i = 0; i < points.Count; i++)
                            if (i != Math.Max(0, anchor) && (coreOf[i] < 0 || seen.Add(coreOf[i])))
                                split.Add((first.Name, first.X, first.Y, layer, points[i].Name, points[i].X, points[i].Y));
                    }
                }
                pads.Add((members, endsIn.Count, onIt.Count, box, split));
            }
        }
        return pads;
    }

    /// <summary>The attachment on <paramref name="island"/> between two arms' trace ends — within the wider arm's width
    /// of the line joining them, and past neither end — or null.</summary>
    private static Attachment? TapBetween(JunctionArm a, JunctionArm b, IReadOnlyList<Attachment> attachments, int island, int dbu)
    {
        double ux = b.End.X - a.End.X, uy = b.End.Y - a.End.Y;
        double len = Math.Sqrt(ux * ux + uy * uy);
        if (len <= 0) return null;
        (ux, uy) = (ux / len, uy / len);
        double w = Math.Max(a.Width, b.Width);
        // A terminal is its pad's centre, and a pad wider than the line — the very thing that split it here — puts that
        // well off the line: FB2's pad on a 1.2 mm supply rail, 1.7 mm off it, was not found, the two halves merged
        // into one line, and FB2 joined FB1's node (designer report, round 15). The reach is a line end's.
        double reach = Math.Max(w, AttachReachWidths * w + AttachReachMicrons * dbu);
        Attachment? best = null;
        double bestOff = double.MaxValue;
        foreach (var at in attachments)
        {
            if (at.Island != island) continue;
            double t = (at.X - a.End.X) * ux + (at.Y - a.End.Y) * uy;
            double off = Math.Abs(-(at.X - a.End.X) * uy + (at.Y - a.End.Y) * ux);
            if (t <= 0 || t >= len || off > reach || off >= bestOff) continue;
            best = at;
            bestOff = off;
        }
        return best;
    }

    private static void SetEnd(LineDraft line, bool atStart, string node)
    {
        if (atStart) line.Nodes[0] = node;
        else line.Nodes[^1] = node;
    }

    /// <summary>R-as5-7: an element's parameters, SI.</summary>
    private static Dictionary<string, double> ParametersOf(LineDraft d, LineRecognitionContext ctx, LineBinder? binder,
                                                           List<LineDraft> lines, int dbu, out bool unsolved)
    {
        unsolved = false;
        double M(double v) => v / dbu * 1e-6;
        switch (d.Type)
        {
            case LineElementType.MLIN:
            case LineElementType.SLIN:
                return new() { ["W"] = M(d.W), ["L"] = M(d.L) };
            case LineElementType.CPWG:
                double g = d.GapLeft is { } gl && d.GapRight is { } gr ? 0.5 * (gl + gr) : d.GapLeft ?? d.GapRight ?? 0;
                return new() { ["W"] = M(d.W), ["L"] = M(d.L), ["G"] = M(g) };
            case LineElementType.MBEND:
                return new() { ["W"] = M(d.W), ["Angle"] = d.Angle, ["Miter"] = (int)d.Miter };
            case LineElementType.MTAPER:
                return new() { ["W1"] = M(d.W), ["W2"] = M(d.W2), ["L"] = M(d.L) };
            case LineElementType.MTEE:
                return new() { ["W1"] = M(d.Widths[0]), ["W2"] = M(d.Widths[1]), ["W3"] = M(d.Widths[2]) };
            case LineElementType.MCROSS:
                return new() { ["W1"] = M(d.Widths[0]), ["W2"] = M(d.Widths[1]), ["W3"] = M(d.Widths[2]), ["W4"] = M(d.Widths[3]) };
        }

        // TLIN, physical form (AS-2): Z and εeff from the cuts — a neighbour's where none was solved.
        double? z = d.Z0, eeff = d.Eeff;
        if (z is null)
        {
            unsolved = true;
            var near = lines.Where(o => o.Source == d.Source && o.Z0 is not null)
                            .MinBy(o => Math.Abs(lines.IndexOf(o) - lines.IndexOf(d)))
                       ?? lines.Where(o => o.Layer == d.Layer && o.Z0 is not null).FirstOrDefault();
            z = near?.Z0 ?? 50;
            eeff = near?.Eeff ?? 1;
        }
        double f = ctx.TopFrequencyHz;
        double ad = 0, ac = 0;
        if (binder?.Substrate(d.ReferenceBelow, d.ReferenceAbove) is var (er, tanD, sigma))
        {
            double e = eeff!.Value;
            // The microstrip filling factor; 1 for a homogeneous line (Eeff = Er), where it is exact.
            double fill = Math.Abs(er - 1) < 1e-9 ? 1 : Math.Clamp((e - 1) / (er - 1) * er / e, 0, er);
            ad = Math.PI * f / C0 * Math.Sqrt(e) * fill * tanD * DbPerNeper;
            if (sigma > 0 && d.W > 0)
                ac = Math.Sqrt(Math.PI * f * Mu0 / sigma) / (z!.Value * M(d.W)) * DbPerNeper;   // Rs/(Z0·W): an estimate
        }
        return new() { ["Z"] = z!.Value, ["L"] = M(d.L), ["Eeff"] = eeff!.Value, ["F"] = f, ["Ac"] = ac, ["Ad"] = ad };
    }

    private static double? Metres(double? dbuValue, int dbu) => dbuValue is { } v ? v / dbu * 1e-6 : null;

    /// <summary>R-as5-8: pairs of straight line segments on one layer, parallel within 2°, their edges within 3
    /// mean widths, overlapping for more than λ/20 at the top frequency.</summary>
    private static List<(LineElement A, LineElement B, (long X, long Y) At)> CoupledPairs(
        List<LineElement> elements, double topHz, int dbu)
    {
        var lines = elements.Where(e => e.Type is LineElementType.MLIN or LineElementType.CPWG or LineElementType.SLIN or LineElementType.TLIN
                                        && e.Anchor.Count >= 2 && e.Parameters.ContainsKey("L")).ToList();
        double cosTol = Math.Cos(CoupledParallelDeg * Math.PI / 180);
        var pairs = new List<(LineElement, LineElement, (long, long))>();
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                var a = lines[i]; var b = lines[j];
                if (a.Layer != b.Layer || a.Nodes.Intersect(b.Nodes).Any()) continue;
                double wa = (a.Width ?? 0) * 1e6 * dbu, wb = (b.Width ?? 0) * 1e6 * dbu;
                double eeff = 0.5 * ((a.Eeff ?? 1) + (b.Eeff ?? 1));
                double minOverlap = CoupledOverlapWavelengths * C0 / (topHz * Math.Sqrt(eeff)) * 1e6 * dbu;
                bool found = false;
                for (int s = 0; s + 1 < a.Anchor.Count && !found; s++)
                    for (int t = 0; t + 1 < b.Anchor.Count && !found; t++)
                    {
                        var (ax0, ay0) = a.Anchor[s]; var (ax1, ay1) = a.Anchor[s + 1];
                        var (bx0, by0) = b.Anchor[t]; var (bx1, by1) = b.Anchor[t + 1];
                        double la = Math.Sqrt(Sq(ax1 - ax0) + Sq(ay1 - ay0)), lb = Math.Sqrt(Sq(bx1 - bx0) + Sq(by1 - by0));
                        if (la <= 0 || lb <= 0) continue;
                        double ux = (ax1 - ax0) / la, uy = (ay1 - ay0) / la;
                        if (Math.Abs(ux * (bx1 - bx0) / lb + uy * (by1 - by0) / lb) < cosTol) continue;
                        double gap = Math.Abs(-(bx0 - ax0) * uy + (by0 - ay0) * ux) - 0.5 * (wa + wb);
                        if (gap > CoupledGapWidths * 0.5 * (wa + wb)) continue;
                        double p0 = (bx0 - ax0) * ux + (by0 - ay0) * uy, p1 = (bx1 - ax0) * ux + (by1 - ay0) * uy;
                        double overlap = Math.Min(la, Math.Max(p0, p1)) - Math.Max(0, Math.Min(p0, p1));
                        if (overlap <= minOverlap) continue;
                        pairs.Add((a, b, ((ax0 + ax1) / 2, (ay0 + ay1) / 2)));
                        found = true;
                    }
            }
        return pairs;
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────────

    private static void Report(
        LineRecognitionResult result, RecognitionReport report, RecognitionOptions options, SegmentationCounts counts,
        List<(TraceJunction Junction, string Why)> plain, List<TraceJunction> coplanar, List<(string Node, ChainEnd End)> openEnds,
        List<(long X, long Y)> absorbed, List<(LineElement A, LineElement B, (long X, long Y) At)> coupled,
        RailLengthFormat fmt)
    {
        var elements = result.Elements;
        static RecognitionAnchor A(LineElement e) =>
            new(e.Anchor.Count == 0 ? 0 : (e.Anchor[0].X + e.Anchor[^1].X) / 2, e.Anchor.Count == 0 ? 0 : (e.Anchor[0].Y + e.Anchor[^1].Y) / 2, e.Layer);

        var byType = elements.GroupBy(e => e.Type).OrderBy(g => g.Key).ToList();
        report.Add(RecognitionFindingClass.LineElements, elements.Count,
            $"{Plural(elements.Count, "line element", "line elements")} read from the traces: " +
            string.Join(", ", byType.Select(g => $"{g.Count()} {g.Key}")) + ".");

        var fallbacks = elements.Where(e => e.Type == LineElementType.TLIN && !e.Unsolved).ToList();
        report.Add(RecognitionFindingClass.TlinFallbacks, fallbacks.Count,
            $"{Plural(fallbacks.Count, "line is a TLIN", "lines are TLINs")} because no circuit model covers the cross-section: " +
            string.Join("; ", fallbacks.GroupBy(e => e.Fallback ?? "").Select(g => $"{g.Count()} {g.Key}")) +
            ". Z and εeff are the solved cross-section's; the conductor loss is an estimate (Rs / (Z0·W)).",
            [.. fallbacks.Select(A)]);

        string under = options.Coplanar switch
        {
            CoplanarReading.Auto => $"under Auto (both side gaps within {options.CoplanarGapFactor:0.##}·H)",
            CoplanarReading.Microstrip => "with every coplanar line read as microstrip",
            _ => "with every line gapped on both sides read as GCPW",
        };
        var gcpw = elements.Where(e => e.Type == LineElementType.CPWG).ToList();
        report.Add(RecognitionFindingClass.LinesReadAsGcpw, gcpw.Count,
            $"{Plural(gcpw.Count, "segment reads", "segments read")} as grounded coplanar (CPWG) {under}.", [.. gcpw.Select(A)]);
        var mlin = elements.Where(e => e.Type == LineElementType.MLIN).ToList();
        int gapped = mlin.Count(e => e.GapLeft is not null && e.GapRight is not null);
        report.Add(RecognitionFindingClass.LinesReadAsMlin, mlin.Count,
            $"{Plural(mlin.Count, "segment reads", "segments read")} as microstrip (MLIN) {under}" +
            (gapped > 0 ? $"; {gapped} of them {(gapped == 1 ? "has" : "have")} ground on both sides, recorded for a swap to CPWG." : "."),
            [.. mlin.Select(A)]);

        var many = plain.Where(p => p.Why == LineJunctions.TooManyArms).ToList();
        report.Add(RecognitionFindingClass.JunctionsOverFourArms, many.Count,
            $"{Plural(many.Count, "junction has", "junctions have")} more than four arms and {(many.Count == 1 ? "is" : "are")} a plain node.",
            [.. many.Select(p => new RecognitionAnchor(p.Junction.X, p.Junction.Y, p.Junction.Layer))]);

        // R-dfr22-2-6: there is no coplanar tee model, so a coplanar junction is the microstrip tee or cross.
        report.Add(RecognitionFindingClass.CoplanarJunctionsAsMicrostrip, coplanar.Count,
            $"{Plural(coplanar.Count, "coplanar junction is", "coplanar junctions are")} modelled with the microstrip tee or cross " +
            "(no coplanar tee model), with the arms' widths.",
            [.. coplanar.Select(j => new RecognitionAnchor(j.X, j.Y, j.Layer))]);

        int bends = counts.UnmodelledBends.Count, steps = counts.UnmodelledSteps.Count;
        var notMs = plain.Where(p => p.Why == LineJunctions.NotMicrostrip).ToList();
        int unmodelled = bends + steps + notMs.Count;
        var parts = new List<string>();
        if (bends > 0) parts.Add($"{Plural(bends, "bend is", "bends are")} centre-line length");
        if (notMs.Count > 0) parts.Add($"{Plural(notMs.Count, "junction is a plain node", "junctions are plain nodes")}");
        if (steps > 0) parts.Add($"{Plural(steps, "width step abuts", "width steps abut")}");
        report.Add(RecognitionFindingClass.DiscontinuitiesNotModelled, unmodelled,
            $"Outside microstrip there are no discontinuity models: {string.Join(", ", parts)}.",
            [.. counts.UnmodelledBends.Concat(counts.UnmodelledSteps).Select(p => new RecognitionAnchor(p.X, p.Y))
               .Concat(notMs.Select(p => new RecognitionAnchor(p.Junction.X, p.Junction.Y, p.Junction.Layer)))]);

        report.Add(RecognitionFindingClass.OpenEnds, openEnds.Count,
            $"{Plural(openEnds.Count, "line ends", "lines end")} on nothing a part, a via or a port lands on, and " +
            $"{(openEnds.Count == 1 ? "is" : "are")} left open (there is no open-end model): " +
            string.Join(", ", openEnds.Take(6).Select(o => fmt.Point((long)Math.Round(o.End.X), (long)Math.Round(o.End.Y))))
            + (openEnds.Count > 6 ? ", …" : "") + ".",
            [.. openEnds.Select(o => new RecognitionAnchor((long)Math.Round(o.End.X), (long)Math.Round(o.End.Y)))]);

        int slivers = counts.Slivers.Count + absorbed.Count;
        report.Add(RecognitionFindingClass.SliversAbsorbed, slivers,
            $"{Plural(slivers, "short piece of line was", "short pieces of line were")} absorbed into a neighbour " +
            "(a sliver taking its neighbour's width, or a line left with no length past a bend, junction, pad or via); " +
            "none was dropped.",
            [.. counts.Slivers.Concat(absorbed).Select(p => new RecognitionAnchor(p.X, p.Y))]);

        report.Add(RecognitionFindingClass.CoupledPairs, coupled.Count,
            $"{Plural(coupled.Count, "pair of lines runs", "pairs of lines run")} close and parallel for more than λ/20 — " +
            "coupled, modelled uncoupled: " + string.Join(", ", coupled.Select(c => $"{c.A.Name} and {c.B.Name}")) + ".",
            [.. coupled.SelectMany(c => new[] { A(c.A), A(c.B) })]);

        var unsolved = elements.Where(e => e.Unsolved).ToList();
        report.Add(RecognitionFindingClass.UnsolvedSegments, unsolved.Count,
            $"{Plural(unsolved.Count, "segment has", "segments have")} no solved cross-section and {(unsolved.Count == 1 ? "is a TLIN" : "are TLINs")} " +
            "at a neighbour's Z and εeff.", [.. unsolved.Select(A)]);
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static double Sq(double v) => v * v;
}
