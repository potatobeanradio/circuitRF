using System.Globalization;
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

            foreach (var gap in result.Gaps) lines.Add(new ReportLine("", GapLine(gap, target), ReportSeverity.Warning));
        }

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
            // A footprint's pads and a via's barrels join at points SDL-2 and SDL-3 define, not at their pin points.
            bool joinable = comp.Footprint is null && !ViaSubstrateInjection.IsViaKind(comp.Symbol);
            return new JointGraph.Part(sid, inst, terminals, existing, joinable);
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
        JointGraph.NetReason.LayerMismatch => "its two pins are on different layers",
        JointGraph.NetReason.PadOrVia => "one end is a footprint pad or a via, which this run does not join",
        _ => "it has one pin",
    };

    private static string PinName(JointGraph.End end) =>
        end.Pin.Name is { Length: > 0 } n ? n : end.Terminal.ToString(CultureInfo.InvariantCulture);

    private static string EndName(JointGraph.End end) => $"{end.Part.SchematicId} pin {PinName(end)}";

    /// <summary>D4's report: "Loop MT1, ML1, … does not close: MT3 pin 3 is 50 µm from ML4 pin 2 (x 0, y −50 µm),
    /// angle 0°."</summary>
    private static string GapLine(ChainPlacer.Gap gap, LayoutView target)
    {
        string u = LayoutUnits.Suffix(target.DisplayUnit);
        string Len(double dbu) => LayoutUnits.Format((long)Math.Round(dbu), target.DisplayUnit, target.DbuPerMicron);
        string where = $"{EndName(gap.From)} is {Len(Math.Sqrt(gap.Dx * gap.Dx + gap.Dy * gap.Dy))} {u} from " +
                       $"{EndName(gap.To)} (x {Len(gap.Dx)}, y {Len(gap.Dy)} {u}), " +
                       $"angle {gap.AngleDeg.ToString("0.######", CultureInfo.InvariantCulture)}°";
        return gap.Loop.Count > 0
            ? $"Loop {string.Join(", ", gap.Loop.Select(p => p.SchematicId))} does not close: {where}."
            : $"{EndName(gap.To)} and {EndName(gap.From)} are joined in the schematic but do not meet: {where}. " +
              "Both are fixed by parts already in the layout, which this run does not move.";
    }
}
