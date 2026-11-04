using System.Globalization;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

// brief-sdl-1: connected placement — the run's chain step. JointGraph says which pins the schematic joins and
// ChainPlacer lays each chain out; this is where the planner hands them its parts and takes back poses and lines.
public static partial class SchematicLayoutPlanner
{
    /// <summary>What the chain step did. <see cref="Placed"/>: every NEW part a chain placed, which the rows and the
    /// artwork placement then leave alone. <see cref="Groups"/>: the chains joined to nothing already placed, each a
    /// rigid group for the row packer at its first part's slot. <see cref="FixedRegion"/>: where the parts chained
    /// onto existing ones went. <see cref="Anchors"/>: the new parts that anchored their chain (the placement record's
    /// <c>ChainAnchor</c>).</summary>
    private sealed record ChainOutcome(
        IReadOnlySet<LayoutInstance> Placed,
        IReadOnlyList<(int Slot, IReadOnlyList<LayoutInstance> Members)> Groups,
        Bbox FixedRegion,
        IReadOnlySet<LayoutInstance> Anchors)
    {
        public static readonly ChainOutcome None = new(
            new HashSet<LayoutInstance>(), [], Bbox.Empty, new HashSet<LayoutInstance>());
    }

    /// <summary>
    /// brief-sdl-1 R-sdl1-1 to R-sdl1-4. Builds the joint graph over every part the layout will hold once the plan is
    /// applied — the parts already placed (as this run replaces them) and the ones it adds — places each chain, writes
    /// the new parts' poses and orientation links, and adds the run's report lines: one per loop or joint that does not
    /// close, one per part joined through a pin of unstated width, one per net left as ratsnest, and the summary.
    /// </summary>
    private static ChainOutcome ConnectChains(
        NetExtractor.ExtractionResult extraction, SchematicEditModel model, List<EditableComponent> physical,
        List<(int Slot, LayoutInstance Instance)> newInstances,
        Dictionary<LayoutInstance, (LayoutInstance Instance, LayoutView Cell, LayoutPin Pin)> beside,
        Dictionary<LayoutInstance, VisualOrientation> schematicFacing,
        LayoutView target, Dictionary<string, (int Index, LayoutInstance Instance)> existingBySchematicId,
        Dictionary<int, LayoutInstance> replacedAt, HashSet<int> deleted,
        string schematicDir, string targetLayoutBaseDir, Technology? technology, List<ReportLine> lines,
        IReadOnlyList<EditableComponent> noLayout, IReadOnlySet<string> resnap, IReadOnlySet<string> opened)
    {
        var compById = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in physical) if (c.InstanceName is { Length: > 0 } n) compById.TryAdd(n, c);

        var slotOf = new Dictionary<LayoutInstance, int>(ReferenceEqualityComparer.Instance);
        var parts = new List<JointGraph.Part>();
        var notChained = new HashSet<string>(StringComparer.Ordinal);
        var padSets = new Dictionary<JointGraph.Part, PadJoint.Set>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        foreach (var (slot, inst) in newInstances)
        {
            slotOf[inst] = slot;
            if (!beside.ContainsKey(inst) && PartOf(inst, existing: false) is { } p) parts.Add(p);
        }
        foreach (var (sid, e) in existingBySchematicId)
        {
            if (deleted.Contains(e.Index) || !compById.ContainsKey(sid)) continue;   // an orphan joins nothing
            var inst = replacedAt.GetValueOrDefault(e.Index, e.Instance);
            if (inst.SchematicId is { Length: > 0 } now && now != sid) continue;   // adopted under a new name below
            // brief-sdl-4 R-sdl4-2: a part being re-snapped is placed by the chain against its neighbours, as a new one is.
            if (PartOf(inst, existing: !resnap.Contains(sid)) is { } p) parts.Add(p);
        }
        foreach (var (index, after) in replacedAt)   // a rename adopted this run: listed under its new name
            if (after.SchematicId is { Length: > 0 } sid && !existingBySchematicId.ContainsKey(sid)
                && compById.ContainsKey(sid) && !deleted.Contains(index) && PartOf(after, existing: true) is { } p)
                parts.Add(p);

        var graph = JointGraph.Build(extraction, parts);
        var placed = new HashSet<LayoutInstance>(ReferenceEqualityComparer.Instance);
        var anchors = new HashSet<LayoutInstance>(ReferenceEqualityComparer.Instance);
        var groups = new List<(int Slot, IReadOnlyList<LayoutInstance> Members)>();
        var fixedRegion = Bbox.Empty;
        var portNumbers = PortNumbersByNet(model, extraction);
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, TerminalNets.Of(inst));

        int joined = 0;
        var unstatedWidth = new HashSet<JointGraph.Part>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        var padNoted = new HashSet<JointGraph.Part>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        foreach (var chain in graph.Chains)
        {
            var anchor = ChainPlacer.ChooseAnchor(chain, part => LowestPort(part));
            // A new chain's anchor takes the facing its symbol gives it (the orientation link's, as a lone part would)
            // at the origin; the row packer then moves the chain as one. Its mirror is the schematic's either way (D2).
            // A re-snapped chain with nothing fixed in it is placed from where its anchor already is.
            var anchorPose = anchor.Existing || resnap.Contains(anchor.SchematicId)
                ? ChainPose.Of(anchor.Instance)
                : new ChainPose(0, 0, anchor.Instance.RotationDegrees, MirrorOf(anchor));
            var result = ChainPlacer.Place(chain, anchor, anchorPose, MirrorOf);
            joined += result.JoinedCount;

            var members = new List<LayoutInstance>();
            foreach (var part in chain.Parts)
            {
                if (part.Existing) continue;
                result.Poses[part].WriteTo(part.Instance);
                var comp = compById[part.SchematicId];
                part.Instance.OrientationLink = SchematicLayoutOrientation.Link(
                    (int)comp.Rotation, comp.MirrorX, SchematicLayoutOrientation.FromLayout(part.Instance));
                placed.Add(part.Instance);
                members.Add(part.Instance);
            }
            if (!anchor.Existing) anchors.Add(anchor.Instance);

            if (chain.Parts.Any(p => p.Existing || resnap.Contains(p.SchematicId)))
                foreach (var inst in members) fixedRegion = fixedRegion.Union(CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir));
            else if (members.Count > 0)
                groups.Add((members.Min(m => slotOf[m]), members.OrderBy(m => slotOf[m]).ToList()));

            foreach (var joint in chain.Joints)
                foreach (var end in new[] { joint.A, joint.B })
                    if (end.Pin.WidthDbu == 0 && unstatedWidth.Add(end.Part))
                        lines.Add(new ReportLine(end.Part.SchematicId,
                            $"{end.Part.SchematicId} — pin {PinName(end)} has no stated width; joined at its centre",
                            ReportSeverity.Info) { Kind = ReportKind.FootprintNote, Ids = [end.Part.SchematicId] });

            // brief-sdl-2: a footprint with nothing to face away from (D1), and a pin joined with no copper under it (D2),
            // once per part each.
            foreach (var part in chain.Parts)
            {
                if (part.Kind != JointGraph.PartKind.Pad || !padSets.TryGetValue(part, out var set) || !padNoted.Add(part)) continue;
                if (set.SinglePad)
                    lines.Add(new ReportLine(part.SchematicId,
                        $"{part.SchematicId} — its pad has no other pad to face away from; joined facing the way its footprint " +
                        "stores it", ReportSeverity.Info) { Kind = ReportKind.FootprintNote, Ids = [part.SchematicId] });
                var used = chain.Joints.SelectMany(j => new[] { j.A, j.B })
                                .Concat(chain.Attachments.SelectMany(a => new[] { a.Pad, a.Across }))
                                .Where(e => ReferenceEquals(e.Part, part)).Select(e => e.Pin.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var point in set.Points.Where(q => !q.OnCopper && used.Contains(q.Pin.Name)).Take(1))
                    lines.Add(new ReportLine(part.SchematicId,
                        $"{part.SchematicId} — pin {(point.Pin.Name.Length > 0 ? point.Pin.Name : "?")} has no copper under it; " +
                        "joined at the pin itself", ReportSeverity.Info) { Kind = ReportKind.FootprintNote, Ids = [part.SchematicId] });
            }

            foreach (var gap in result.Gaps)
            {
                var gapIds = Ids(new[] { gap.To.Part, gap.From.Part }.Concat(gap.Bridge is { } b ? new[] { b.Part } : Array.Empty<JointGraph.Part>()));
                if (gap.From.Part.Existing && gap.To.Part.Existing && gap.Bridge is null)
                {
                    // brief-sdl-4: between two parts already placed, a joint whose pins coincide is made however the user
                    // turned them (D2); one this run opened is reported by DeltaPropagation; any other the user opened,
                    // and it stays open (D6) — said every run, as Info, because it is the user's choice.
                    if (Math.Abs(gap.Dx) <= ChainPlacer.CloseDbu && Math.Abs(gap.Dy) <= ChainPlacer.CloseDbu) { joined++; continue; }
                    if (gap.Joint is { } j
                        && opened.Contains(DeltaPropagation.JointKey(j.A.Part.SchematicId, j.A.Terminal, j.B.Part.SchematicId, j.B.Terminal)))
                        continue;
                    lines.Add(new ReportLine("", OpenJointLine(gap, target), ReportSeverity.Info) { Kind = ReportKind.LoopGap, Ids = gapIds });
                    continue;
                }
                lines.Add(new ReportLine("", GapLine(gap, target), ReportSeverity.Warning) { Kind = ReportKind.LoopGap, Ids = gapIds });
            }
        }

        // D4: a third or later pad at one joint is not placed against it.
        foreach (var (net, end) in graph.Overflow)
            if (!placed.Contains(end.Part.Instance) && !end.Part.Existing && !resnap.Contains(end.Part.SchematicId))
                lines.Add(new ReportLine(end.Part.SchematicId,
                    $"{end.Part.SchematicId} — pin {PinName(end)} is a third part at the joint on net {net}; at most two attach " +
                    "side-on at one joint, so it was placed in the row", ReportSeverity.Warning)
                    { Kind = ReportKind.FootprintNote, Ids = [end.Part.SchematicId] });

        foreach (var net in graph.Unjoined.Where(u => u.Reason != JointGraph.NetReason.OnePin))
            lines.Add(UnjoinedLine(net, technology));

        ReportJoinedThroughNoLayout(graph, noLayout, bindings, lines);

        int connections = graph.ConnectionCount;
        if (connections > 0)
            lines.Add(new ReportLine("", $"Joined {joined} of {connections} connection{(connections == 1 ? "" : "s")}; " +
                                         $"{connections - joined} left as ratsnest", ReportSeverity.Info));

        return new ChainOutcome(placed, groups, fixedRegion, anchors);

        JointGraph.Part? PartOf(LayoutInstance inst, bool existing)
        {
            if (inst.SchematicId is not { Length: > 0 } sid || !compById.TryGetValue(sid, out var comp)) return null;
            // A placement stretched or arrayed is not one part with one set of pins: it is left to the ratsnest, and
            // said once (brief-sdl-3).
            if (inst.Mag != 1.0 || inst.Rows * inst.Cols != 1)
            {
                if (notChained.Add(sid))
                    lines.Add(new ReportLine(sid, inst.Rows * inst.Cols != 1
                            ? $"{sid} — placed as an array of {inst.Rows} × {inst.Cols}; an array is not joined to its neighbours"
                            : $"{sid} — placed magnified ×{inst.Mag.ToString("0.###", CultureInfo.InvariantCulture)}; " +
                              "a magnified part is not joined to its neighbours",
                        ReportSeverity.Info) { Kind = ReportKind.Unjoined, Ids = [sid] });
                return null;
            }
            if (PartTerminals(comp, inst, schematicDir, targetLayoutBaseDir, technology, out var kind, out var set) is not { } terminals)
                return null;
            var part = new JointGraph.Part(sid, inst, terminals, existing, kind);
            if (set is not null) padSets[part] = set;
            return part;
        }

        bool MirrorOf(JointGraph.Part part) =>
            part.Existing ? part.Instance.MirrorX
            : schematicFacing.TryGetValue(part.Instance, out var f) ? f.Mirror : part.Instance.MirrorX;

        int? LowestPort(JointGraph.Part part)
        {
            if (!bindings.TryGetValue(part.SchematicId, out var nets)) return null;
            int? best = null;
            foreach (string net in nets)
                if (portNumbers.TryGetValue(net, out int n) && (best is null || n < best)) best = n;
            return best;
        }
    }

    /// <summary>
    /// The pins a placed part joins by, one per schematic terminal (null where none lands): a line's own pins; for a
    /// part with pads, each pad's <see cref="PadJoint"/> (SDL-2 D1/D2); for a via, its two pins faced apart
    /// (brief-sdl-3). Null when the cell does not resolve or has no pins. Shared by the chain step and by
    /// <see cref="DeltaPropagation"/>, which reads the same pins before and after a change.
    /// </summary>
    internal static IReadOnlyList<LayoutPin?>? PartTerminals(EditableComponent comp, LayoutInstance inst, string schematicDir,
                                                            string layoutBaseDir, Technology? technology,
                                                            out JointGraph.PartKind kind, out PadJoint.Set? padSet)
    {
        kind = JointGraph.PartKind.Line;
        padSet = null;
        if (CellLayoutResolver.Resolve(inst, layoutBaseDir, technology) is not
                { State: CellLayoutState.Resolved, View: { } cell }) return null;
        var pins = CellPins.Resolve(cell, technology);
        if (pins.Count == 0) return null;
        var symbol = comp.ExternalSymbolRef is { } symRef ? CellSymbolResolver.Resolve(symRef, schematicDir) : null;
        int drawn = comp.ToRenderComponent(null, symbol).Ports.Count;
        var terminals = Enumerable.Range(1, drawn).Select(k => SchematicLayoutOrientation.CellPinFor(k, pins)).ToList();

        // brief-sdl-2 R-sdl2-2: a line joins at its own pin; anything with pads — a footprint, an imported part, a
        // placed cell — joins at the middle of its pad's outer edge (D2), facing the pad's effective direction (D1;
        // only a footprint's file can store it backwards).
        kind = ViaSubstrateInjection.IsViaKind(comp.Symbol) ? JointGraph.PartKind.Via
             : FootprintDefaults.IsOffered(comp.Symbol) ? JointGraph.PartKind.Pad
             : JointGraph.PartKind.Line;
        if (kind == JointGraph.PartKind.Pad)
        {
            var set = PadJoint.Of(cell, technology, effectiveFacing: comp.Footprint is not null);
            padSet = set;
            return terminals.Select(t => t is null ? null : set.For(t)?.Joint ?? t).ToList();
        }
        if (kind == JointGraph.PartKind.Via)
        {
            // brief-sdl-3: both of a via's pins sit at the barrel's centre and its generator faces them alike, so
            // the joint gives them opposite facings — A back the way the line came, B onward — and a line on
            // either layer that meets the via ends at its centre with the line beyond carrying straight on. The
            // via has no handedness, so its solved rotation is whatever turns A to face the incoming line.
            return terminals.Select((t, i) => t is null ? null : new LayoutPin
            {
                Name = t.Name, X = t.X, Y = t.Y, Layer = t.Layer, WidthDbu = t.WidthDbu,
                OutwardDeg = i == 0 ? 180.0 : 0.0,
            }).ToList();
        }
        return terminals;
    }

    /// <summary>Each net a schematic Port's signal terminal sits on, with the lowest port number on it.</summary>
    private static Dictionary<string, int> PortNumbersByNet(SchematicEditModel model, NetExtractor.ExtractionResult extraction)
    {
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, TerminalNets.Of(inst));
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var comp in model.Components)
        {
            if (comp.Symbol is not (SymbolKind.Term or SymbolKind.ZPort)) continue;
            if (!int.TryParse(comp.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression?.Trim(),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out int num)) continue;
            if (!bindings.TryGetValue(comp.InstanceName, out var nets) || nets.Count == 0 || nets[0] is not { Length: > 0 } net
                || net == "0") continue;
            if (!result.TryGetValue(net, out int had) || num < had) result[net] = num;
        }
        return result;
    }

    /// <summary>R-sdl1-3: a part made from artwork that a chain placed rather than its artwork anchor, once per run.</summary>
    private static void ReportChainedOverArtwork(List<(int Slot, LayoutInstance Instance)> newInstances,
                                                 List<EditableComponent> physical, ChainOutcome chained,
                                                 SchematicEditModel model, List<ReportLine> lines)
    {
        if (model.ArtworkSource is not { Layout.Length: > 0 }) return;
        int count = newInstances.Count(n => chained.Placed.Contains(n.Instance)
                                            && physical[n.Slot] is { FromArtwork: true, ArtworkAnchor.Count: > 0 });
        if (count == 0) return;
        lines.Add(new ReportLine("", count == 1
                ? "1 part drawn from artwork was placed where the schematic joins it to its neighbours, not where the artwork had it"
                : $"{count} parts drawn from artwork were placed where the schematic joins them to their neighbours, " +
                  "not where the artwork had them",
            ReportSeverity.Info));
    }

    private static string ReasonText(JointGraph.NetReason reason) => reason switch
    {
        JointGraph.NetReason.Ground => "the ground net returns through vias and the pour, not pin to pin",
        JointGraph.NetReason.ThreeOrMorePins => "three or more pins meet on it, and no one pairing is the schematic's",
        JointGraph.NetReason.SideMismatch => "its parts are on different sides of the board, which only a via can join",
        _ => "it has one pin",
    };

    /// <summary>The report line for a net the graph does not join (brief-sdl-3): three or more line ends name the
    /// junction part that is missing, and pins on different layers name both layers; every other reason is the
    /// ratsnest line SDL-1 wrote.</summary>
    private static ReportLine UnjoinedLine(JointGraph.UnjoinedNet net, Technology? technology)
    {
        var ids = Ids(net.Ends.Select(e => e.Part));
        string ends = string.Join(", ", net.Ends.Select(EndName));
        switch (net.Reason)
        {
            case JointGraph.NetReason.LineEnds:
                return new ReportLine("",
                    $"Net {net.Net} joins {CountWord(net.Ends.Count)} line ends " +
                    $"({string.Join(", ", net.Ends.Select(e => $"{e.Part.SchematicId}.{PinName(e)}"))}); " +
                    "a junction part (MTEE, MCROSS) is needed to place it", ReportSeverity.Warning)
                    { Kind = ReportKind.Unjoined, Ids = ids };
            case JointGraph.NetReason.LayerMismatch:
                var layers = net.Ends.Select(e => LayerName(technology, e.Pin.Layer)).Distinct(StringComparer.Ordinal).ToList();
                return new ReportLine("",
                    $"Net {net.Net} left as ratsnest: its pins are on {string.Join(" and ", layers)}, and no via joins " +
                    $"them ({ends})", ReportSeverity.Warning) { Kind = ReportKind.Unjoined, Ids = ids };
            default:
                return new ReportLine("", $"Net {net.Net} left as ratsnest: {ReasonText(net.Reason)} ({ends})",
                    ReportSeverity.Info) { Kind = ReportKind.Unjoined, Ids = ids };
        }
    }

    /// <summary>
    /// brief-sdl-3: parts the schematic joins only through a part with no layout (an ideal resistor between two
    /// lines) are on different nets as far as the layout is concerned, so they are placed as separate chains, and
    /// nothing on the board shows they belong together. One line per such part, naming the first part with a layout
    /// on each of its nets — said only when those parts did end up in different chains.
    /// </summary>
    private static void ReportJoinedThroughNoLayout(JointGraph graph, IReadOnlyList<EditableComponent> noLayout,
                                                    Dictionary<string, IReadOnlyList<string>> bindings,
                                                    List<ReportLine> lines)
    {
        var cmp = ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>;
        var chainOf = new Dictionary<JointGraph.Part, int>(cmp);
        for (int c = 0; c < graph.Chains.Count; c++)
            foreach (var p in graph.Chains[c].Parts) chainOf[p] = c;
        int Group(JointGraph.Part p) => chainOf.TryGetValue(p, out int c) ? c : -1 - p.Order;

        // The first part with a layout on each net, in schematic-id order (graph.Parts is in that order).
        var firstOn = new Dictionary<string, JointGraph.Part>(StringComparer.Ordinal);
        foreach (var part in graph.Parts)
        {
            if (!bindings.TryGetValue(part.SchematicId, out var nets)) continue;
            for (int k = 0; k < part.Terminals.Count && k < nets.Count; k++)
                if (part.Terminals[k] is not null && nets[k] is { Length: > 0 } net && net != "0") firstOn.TryAdd(net, part);
        }

        foreach (var comp in noLayout.OrderBy(c => c.InstanceName, Comparer<string>.Create(JointGraph.CompareIds)))
        {
            if (comp.InstanceName is not { Length: > 0 } id || !bindings.TryGetValue(id, out var nets)) continue;
            var sides = nets.Where(n => n is { Length: > 0 } && n != "0").Distinct(StringComparer.Ordinal)
                            .Select(n => firstOn.GetValueOrDefault(n)).OfType<JointGraph.Part>()
                            .Distinct(cmp).OrderBy(p => p.Order).ToList();
            if (sides.Count < 2 || sides.Select(Group).Distinct().Count() < 2) continue;
            string names = sides.Count == 2 ? $"{sides[0].SchematicId} and {sides[1].SchematicId}"
                : string.Join(", ", sides.Take(sides.Count - 1).Select(p => p.SchematicId)) + $" and {sides[^1].SchematicId}";
            lines.Add(new ReportLine(id, $"{names} are joined through {id}, which has no layout; they are placed apart",
                ReportSeverity.Info) { Kind = ReportKind.Unjoined, Ids = [.. sides.Select(p => p.SchematicId), id] });
        }
    }

    /// <summary>A drawing layer by its technology name, else as layer/datatype.</summary>
    private static string LayerName(Technology? technology, LayerKey key) =>
        technology?.Layers.FirstOrDefault(l => l.Key == key)?.Name is { Length: > 0 } name
            ? name : $"{key.Layer}/{key.Datatype}";

    private static string CountWord(int n) => n switch
    {
        3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The schematic ids of <paramref name="parts"/>, once each, in the order given.</summary>
    private static IReadOnlyList<string> Ids(IEnumerable<JointGraph.Part> parts) =>
        parts.Select(p => p.SchematicId).Distinct(StringComparer.Ordinal).ToList();

    private static string PinName(JointGraph.End end) =>
        end.Pin.Name is { Length: > 0 } n ? n : end.Terminal.ToString(CultureInfo.InvariantCulture);

    private static string EndName(JointGraph.End end) => $"{end.Part.SchematicId} pin {PinName(end)}";

    /// <summary>brief-sdl-4 D6: a joint the user opened in the layout, as the run leaves it.</summary>
    private static string OpenJointLine(ChainPlacer.Gap gap, LayoutView target)
    {
        string u = LayoutUnits.Suffix(target.DisplayUnit);
        string Len(double dbu) => LayoutUnits.Format((long)Math.Round(dbu), target.DisplayUnit, target.DbuPerMicron);
        return $"{EndName(gap.To)} and {EndName(gap.From)} are {Len(Math.Sqrt(gap.Dx * gap.Dx + gap.Dy * gap.Dy))} {u} apart " +
               $"(x {Len(gap.Dx)}, y {Len(gap.Dy)} {u}): the joint is open in the layout, so Update Layout leaves it open " +
               "and moves nothing across it. Re-snap joins it again.";
    }

    /// <summary>D4's report: "Loop MT1, ML1, … does not close: MT3 pin 3 is 50 µm from ML4 pin 2 (x 0, y −50 µm),
    /// angle 0°."</summary>
    private static string GapLine(ChainPlacer.Gap gap, LayoutView target)
    {
        string u = LayoutUnits.Suffix(target.DisplayUnit);
        string Len(double dbu) => LayoutUnits.Format((long)Math.Round(dbu), target.DisplayUnit, target.DbuPerMicron);
        string where = $"{EndName(gap.From)} is {Len(Math.Sqrt(gap.Dx * gap.Dx + gap.Dy * gap.Dy))} {u} from " +
                       $"{EndName(gap.To)} (x {Len(gap.Dx)}, y {Len(gap.Dy)} {u}), " +
                       $"angle {gap.AngleDeg.ToString("0.######", CultureInfo.InvariantCulture)}°";
        string loop = string.Join(", ", gap.Loop.Select(p => p.SchematicId));
        if (gap.Bridge is { } b)
        {
            // D6: the part is centred between the two line ends, so the misfit is the difference of two spacings.
            string fit = $"{b.Part.SchematicId} bridges {EndName(b.First)} and {EndName(b.Second)}, which are " +
                         $"{Len(b.RequiredDbu)} {u} apart, and its pads span {Len(b.ActualDbu)} {u}";
            return gap.Loop.Count > 0 ? $"Loop {loop} does not close: {fit}." : $"{fit}.";
        }
        if (gap.Attachment is { } a)
            return $"{EndName(gap.From)} does not sit against the side of {EndName(a.Base)}: " +
                   $"{Len(Math.Sqrt(gap.Dx * gap.Dx + gap.Dy * gap.Dy))} {u} out (x {Len(gap.Dx)}, y {Len(gap.Dy)} {u}), " +
                   $"angle {gap.AngleDeg.ToString("0.######", CultureInfo.InvariantCulture)}°.";
        return gap.Loop.Count > 0
            ? $"Loop {loop} does not close: {where}."
            : $"{EndName(gap.To)} and {EndName(gap.From)} are joined in the schematic but do not meet: {where}. " +
              "Both are fixed by parts already in the layout, which this run does not move.";
    }
}
