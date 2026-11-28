// Owner: PCD-2 (brief-pcd-2 R-pcd2-1). Framework-free.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

// brief-pcd-2: the offset step — between the chains and the rows, a part no net joins is placed from another part's pin
// by its Offset (brief-pcd-0 §4.2), and the chain it is in moves with it as one rigid body.
public static partial class SchematicLayoutPlanner
{
    /// <summary>A chain, or a part in no chain, as the offset step moves it (D1). <see cref="Poses"/> are exact until the
    /// chain step writes them; <see cref="Root"/> is the unit whose row group this one rides in (D4).</summary>
    private sealed class PlacementUnit(IReadOnlyList<JointGraph.Part> parts, JointGraph.Part anchor,
                                       Dictionary<JointGraph.Part, ChainPose> poses, bool isChain)
    {
        public IReadOnlyList<JointGraph.Part> Parts { get; } = parts;
        public JointGraph.Part Anchor { get; } = anchor;
        public Dictionary<JointGraph.Part, ChainPose> Poses { get; } = poses;
        public bool IsChain { get; } = isChain;
        public PlacementUnit Root { get; set; } = null!;
        public bool ByOffset { get; set; }
        /// <summary>A lone part only gets its pose written when an offset moves it or is measured from it; otherwise the
        /// rows place it as before.</summary>
        public bool Involved { get; set; } = isChain;
    }

    /// <summary>
    /// R-pcd2-1: every unit with an <c>Offset</c> placed from its anchor's pin, in dependency order (D3), and the lines
    /// that say so or say why not. Sets each unit's <see cref="PlacementUnit.Root"/>; returns the root's id (D7).
    /// </summary>
    private static string? PlaceOffsets(List<PlacementUnit> units, Dictionary<string, EditableComponent> compById,
                                        Scope scope, Evaluator evaluator, int dbuPerMicron, List<ReportLine> lines)
    {
        var unitOf = new Dictionary<string, PlacementUnit>(StringComparer.Ordinal);
        var partOf = new Dictionary<string, JointGraph.Part>(StringComparer.Ordinal);
        foreach (var u in units)
        {
            u.Root = u;
            foreach (var p in u.Parts) { unitOf[p.SchematicId] = u; partOf[p.SchematicId] = p; }
        }

        // D2: the first part with an Offset drives its unit; every other one in it is said and ignored.
        var pending = new List<(PlacementUnit Unit, JointGraph.Part Part, OffsetPlacement Offset)>();
        foreach (var u in units)
        {
            JointGraph.Part? driver = null;
            foreach (var p in u.Parts.OrderBy(p => p.Order))
            {
                if (compById.GetValueOrDefault(p.SchematicId)?.Offset is not { } offset) continue;
                // D1: a part already in the layout stays where the layout has it.
                // ponytail: an offset unit already in the layout does not follow a parameter change on a re-run; the
                // designed cell always plans onto an empty layout, so only a hand-kept layout would need it.
                if (u.Parts.Any(q => q.Existing)) break;
                if (driver is not null)
                {
                    Refuse(p, $"its chain is already placed by {driver.SchematicId}'s offset");
                    continue;
                }
                driver = p;
                pending.Add((u, p, offset));
            }
        }

        var settled = new HashSet<PlacementUnit>(units.Where(u => pending.All(e => e.Unit != u)));
        for (bool progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            foreach (var entry in pending.OrderBy(e => e.Part.Order).ToList())
            {
                var (unit, part, offset) = entry;
                if (!ParseAnchor(offset.Anchor, out string anchorId, out int anchorPin))
                    Refuse(part, $"its offset anchor '{offset.Anchor}' is not a pin written <part>.<pin>");
                else if (!partOf.TryGetValue(anchorId, out var anchorPart))
                    Refuse(part, $"its offset anchor {offset.Anchor} names no part placed in this layout");
                else if (unitOf[anchorId] == unit)
                    Refuse(part, $"its anchor {anchorId} is in its own chain, which already places it");
                else if (!settled.Contains(unitOf[anchorId])) continue;   // its anchor's unit is placed first (D3)
                else if (PinOf(anchorPart, anchorPin) is not { } aPin)
                    Refuse(part, $"its offset anchor {offset.Anchor} is no pin of {anchorId}'s layout");
                else if (PinOf(part, offset.Pin) is not { } ownPin)
                    Refuse(part, $"its offset names pin {offset.Pin}, which {part.SchematicId}'s layout does not have");
                else
                {
                    var anchorUnit = unitOf[anchorId];
                    var (ax, ay, aDeg) = anchorUnit.Poses[anchorPart].Apply(aPin);
                    if (OffsetPlacer.At(offset, ax, ay, aDeg, scope, evaluator, dbuPerMicron, out string? refusal) is not { } at)
                        Refuse(part, $"offset not applied: {refusal}");
                    else
                    {
                        Move(unit, part, ChainPlacer.ChildAt(at.X, at.Y, at.Deg + 180.0, ownPin, unit.Poses[part].Mirror));
                        unit.ByOffset = unit.Involved = true;
                        unit.Root = anchorUnit.Root;
                        unit.Root.Involved = true;
                        lines.Add(new ReportLine(part.SchematicId,
                            $"{part.SchematicId} — placed by its offset from {anchorId} pin {anchorPin}",
                            ReportSeverity.Info) { Ids = [part.SchematicId, anchorId] });
                    }
                }
                settled.Add(unit);
                pending.Remove(entry);
                progress = true;
            }
        }
        foreach (var (_, part, offset) in pending.OrderBy(e => e.Part.Order))
            Refuse(part, $"its offset anchor {offset.Anchor} is placed by an offset that leads back to it");

        var root = units.Where(u => u.IsChain && !u.ByOffset).Concat(units.Where(u => !u.IsChain && !u.ByOffset))
                        .FirstOrDefault();
        return root?.Anchor.SchematicId;

        void Refuse(JointGraph.Part p, string why) =>
            lines.Add(new ReportLine(p.SchematicId, $"{p.SchematicId} — {why}; the offset was not applied",
                ReportSeverity.Warning) { Ids = [p.SchematicId] });
    }

    /// <summary>Every pose of <paramref name="unit"/> carried by the rigid motion that takes <paramref name="by"/> to
    /// <paramref name="to"/> (same mirror: only a turn and a shift).</summary>
    private static void Move(PlacementUnit unit, JointGraph.Part by, ChainPose to)
    {
        var from = unit.Poses[by];
        double turn = to.Deg - from.Deg;
        foreach (var p in unit.Parts)
        {
            var q = unit.Poses[p];
            var (rx, ry) = ChainPose.Rotate(q.X - from.X, q.Y - from.Y, turn);
            unit.Poses[p] = q with { X = to.X + rx, Y = to.Y + ry, Deg = LayoutAngle.Normalize(q.Deg + turn) };
        }
    }

    private static LayoutPin? PinOf(JointGraph.Part part, int pin) =>
        pin >= 1 && pin <= part.Terminals.Count ? part.Terminals[pin - 1] : null;

    private static bool ParseAnchor(string anchor, out string id, out int pin)
    {
        int dot = anchor.LastIndexOf('.');
        id = dot > 0 ? anchor[..dot].Trim() : "";
        pin = 0;
        return dot > 0 && int.TryParse(anchor[(dot + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pin);
    }
}
