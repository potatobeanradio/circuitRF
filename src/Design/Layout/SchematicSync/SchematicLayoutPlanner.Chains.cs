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
        string schematicDir, string targetLayoutBaseDir, Technology? technology, List<ReportLine> lines)
    {
        var compById = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in physical) if (c.InstanceName is { Length: > 0 } n) compById.TryAdd(n, c);

        var slotOf = new Dictionary<LayoutInstance, int>(ReferenceEqualityComparer.Instance);
        var parts = new List<JointGraph.Part>();
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
            if (PartOf(inst, existing: true) is { } p) parts.Add(p);
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
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, inst.NetBindings);

        int joined = 0;
        var unstatedWidth = new HashSet<JointGraph.Part>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        var padNoted = new HashSet<JointGraph.Part>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        foreach (var chain in graph.Chains)
        {
            var anchor = ChainPlacer.ChooseAnchor(chain, part => LowestPort(part));
            // A new chain's anchor takes the facing its symbol gives it (the orientation link's, as a lone part would)
            // at the origin; the row packer then moves the chain as one. Its mirror is the schematic's either way (D2).
            var anchorPose = anchor.Existing
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

            if (chain.Parts.Any(p => p.Existing))
                foreach (var inst in members) fixedRegion = fixedRegion.Union(CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir));
            else if (members.Count > 0)
                groups.Add((members.Min(m => slotOf[m]), members.OrderBy(m => slotOf[m]).ToList()));

            foreach (var joint in chain.Joints)
                foreach (var end in new[] { joint.A, joint.B })
                    if (end.Pin.WidthDbu == 0 && unstatedWidth.Add(end.Part))
                        lines.Add(new ReportLine(end.Part.SchematicId,
                            $"{end.Part.SchematicId} — pin {PinName(end)} has no stated width; joined at its centre",
                            ReportSeverity.Info));

            // brief-sdl-2: a footprint with nothing to face away from (D1), and a pin joined with no copper under it (D2),
            // once per part each.
            foreach (var part in chain.Parts)
            {
                if (part.Kind != JointGraph.PartKind.Pad || !padSets.TryGetValue(part, out var set) || !padNoted.Add(part)) continue;
                if (set.SinglePad)
                    lines.Add(new ReportLine(part.SchematicId,
                        $"{part.SchematicId} — its pad has no other pad to face away from; joined facing the way its footprint " +
                        "stores it", ReportSeverity.Info));
                var used = chain.Joints.SelectMany(j => new[] { j.A, j.B })
                                .Concat(chain.Attachments.SelectMany(a => new[] { a.Pad, a.Across }))
                                .Where(e => ReferenceEquals(e.Part, part)).Select(e => e.Pin.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var point in set.Points.Where(q => !q.OnCopper && used.Contains(q.Pin.Name)).Take(1))
                    lines.Add(new ReportLine(part.SchematicId,
                        $"{part.SchematicId} — pin {(point.Pin.Name.Length > 0 ? point.Pin.Name : "?")} has no copper under it; " +
                        "joined at the pin itself", ReportSeverity.Info));
            }

            foreach (var gap in result.Gaps) lines.Add(new ReportLine("", GapLine(gap, target), ReportSeverity.Warning));
            ReportSideOnOverlaps(chain, result, padSets, bindings, lines);
        }

        // D4: a third or later pad at one joint is not placed against it.
        foreach (var (net, end) in graph.Overflow)
            if (!placed.Contains(end.Part.Instance) && !end.Part.Existing)
                lines.Add(new ReportLine(end.Part.SchematicId,
                    $"{end.Part.SchematicId} — pin {PinName(end)} is a third part at the joint on net {net}; at most two attach " +
                    "side-on at one joint, so it was placed in the row", ReportSeverity.Warning));

        foreach (var net in graph.Unjoined.Where(u => u.Reason != JointGraph.NetReason.OnePin))
            lines.Add(new ReportLine("", $"Net {net.Net} left as ratsnest: {ReasonText(net.Reason)} " +
                                         $"({string.Join(", ", net.Ends.Select(EndName))})", ReportSeverity.Info));

        int connections = graph.ConnectionCount;
        if (connections > 0)
            lines.Add(new ReportLine("", $"Joined {joined} of {connections} connection{(connections == 1 ? "" : "s")}; " +
                                         $"{connections - joined} left as ratsnest", ReportSeverity.Info));

        return new ChainOutcome(placed, groups, fixedRegion, anchors);

        JointGraph.Part? PartOf(LayoutInstance inst, bool existing)
        {
            if (inst.SchematicId is not { Length: > 0 } sid || !compById.TryGetValue(sid, out var comp)) return null;
            // A placement stretched or arrayed is not one part with one set of pins; leave it to the ratsnest.
            if (inst.Mag != 1.0 || inst.Rows * inst.Cols != 1) return null;
            if (CellLayoutResolver.Resolve(inst, targetLayoutBaseDir, technology) is not
                    { State: CellLayoutState.Resolved, View: { } cell }) return null;
            var pins = CellPins.Resolve(cell, technology);
            if (pins.Count == 0) return null;
            var symbol = comp.ExternalSymbolRef is { } symRef ? CellSymbolResolver.Resolve(symRef, schematicDir) : null;
            int drawn = comp.ToRenderComponent(null, symbol).Ports.Count;
            var terminals = Enumerable.Range(1, drawn).Select(k => SchematicLayoutOrientation.CellPinFor(k, pins)).ToList();

            // brief-sdl-2 R-sdl2-2: a line joins at its own pin; anything with pads — a footprint, an imported part, a
            // placed cell — joins at the middle of its pad's outer edge (D2), facing the pad's effective direction (D1;
            // only a footprint's file can store it backwards). A via's barrel joins where SDL-3 says.
            var kind = ViaSubstrateInjection.IsViaKind(comp.Symbol) ? JointGraph.PartKind.Via
                     : FootprintDefaults.IsOffered(comp.Symbol) ? JointGraph.PartKind.Pad
                     : JointGraph.PartKind.Line;
            if (kind == JointGraph.PartKind.Pad)
            {
                var set = PadJoint.Of(cell, technology, effectiveFacing: comp.Footprint is not null);
                terminals = terminals.Select(t => t is null ? null : set.For(t)?.Joint ?? t).ToList();
                var part = new JointGraph.Part(sid, inst, terminals, existing, kind);
                padSets[part] = set;
                return part;
            }
            return new JointGraph.Part(sid, inst, terminals, existing, kind);
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

    /// <summary>Each net a schematic Port's signal terminal sits on, with the lowest port number on it.</summary>
    private static Dictionary<string, int> PortNumbersByNet(SchematicEditModel model, NetExtractor.ExtractionResult extraction)
    {
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, inst.NetBindings);
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
        JointGraph.NetReason.LayerMismatch => "its pins are on different layers",
        JointGraph.NetReason.Via => "one end is a via, which this run does not join",
        JointGraph.NetReason.SideMismatch => "its parts are on different sides of the board, which only a via can join",
        _ => "it has one pin",
    };

    private static string PinName(JointGraph.End end) =>
        end.Pin.Name is { Length: > 0 } n ? n : end.Terminal.ToString(CultureInfo.InvariantCulture);

    private static string EndName(JointGraph.End end) => $"{end.Part.SchematicId} pin {PinName(end)}";

    /// <summary>
    /// R-sdl2-3: the parts placed beside one joint — its side-on pads and, at a line's end (D5), the pad it joins —
    /// whose pads on DIFFERENT nets overlap. Checked against the parts at the same joint only; the whole-layout overlap
    /// check is SDL-3's. Pad copper is compared box to box in the layout's coordinates, interiors only, so pads that
    /// merely touch are not reported.
    /// </summary>
    private static void ReportSideOnOverlaps(JointGraph.Chain chain, ChainPlacer.Result result,
                                             Dictionary<JointGraph.Part, PadJoint.Set> padSets,
                                             Dictionary<string, IReadOnlyList<string>> bindings, List<ReportLine> lines)
    {
        foreach (var group in chain.Attachments.GroupBy(a => a.Net))
        {
            var members = group.Select(a => a.Pad.Part)
                               .Concat(group.Select(a => a.Across.Part).Where(p => p.Kind == JointGraph.PartKind.Pad))
                               .Distinct(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>)
                               .Where(p => result.Poses.ContainsKey(p) && padSets.ContainsKey(p))
                               .OrderBy(p => p.Order).ToList();
            for (int i = 0; i < members.Count; i++)
                for (int k = i + 1; k < members.Count; k++)
                    if (PadsOverlap(members[i], members[k]))
                        lines.Add(new ReportLine(members[k].SchematicId,
                            $"{members[i].SchematicId} and {members[k].SchematicId}, both placed at the joint on net {group.Key}, " +
                            "overlap", ReportSeverity.Warning));
        }

        bool PadsOverlap(JointGraph.Part a, JointGraph.Part b)
        {
            foreach (var (pa, na) in Pads(a))
                foreach (var (pb, nb) in Pads(b))
                    if (na != nb && pa.MinX < pb.MaxX && pb.MinX < pa.MaxX && pa.MinY < pb.MaxY && pb.MinY < pa.MaxY) return true;
            return false;
        }

        IEnumerable<((double MinX, double MinY, double MaxX, double MaxY) Box, string Net)> Pads(JointGraph.Part part)
        {
            var nets = bindings.GetValueOrDefault(part.SchematicId) ?? [];
            var pose = result.Poses[part];
            for (int t = 0; t < part.Terminals.Count && t < nets.Count; t++)
            {
                if (part.Terminals[t] is not { } joint || padSets[part].Points.FirstOrDefault(q => q.Joint.Name == joint.Name)
                        is not { Ring: { } ring }) continue;
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                for (int v = 0; v + 1 < ring.Length; v += 2)
                {
                    var (x, y, _) = pose.Apply(new LayoutPin { X = ring[v], Y = ring[v + 1] });
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                }
                yield return ((x0, y0, x1, y1), nets[t]);
            }
        }
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
