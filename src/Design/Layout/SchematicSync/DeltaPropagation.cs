using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Schematic;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-4 §1: a re-run of Update Layout from Schematic starts from the layout exactly as the user left it and
/// applies only what the schematic changed — so the arrangement survives, and the copper stays joined.
///
/// <list type="number">
/// <item><b>D1 — every change is a change of pin poses.</b> A linked part whose pins sit differently in its new cell
/// (or under a turn carried from the schematic) than in its old one is a CHANGE; one whose pins did not move (an MLIN's
/// W) changes in place and propagates nothing.</item>
/// <item><b>D2 — the far side moves as one rigid body.</b> Only joints MADE in the layout as it is are followed: two
/// pins the schematic joins whose positions coincide within 1 DBU (whatever way the user turned them), a side-on pad
/// sitting against its joint, and copper contact with drawn copper or with a part no schematic names. From the side
/// that stays, each part is carried by the rigid motion that keeps every made joint made: across a joint
/// <c>T_V = T_U · D_U · C_V</c>, where <c>D_U</c> carries U's old pin onto its new one and <c>C_V</c> puts a changed
/// V's own pin back where it was. Inside a moving group nothing changes relative to anything else.</item>
/// <item><b>D3 — which side stays.</b> Locked parts and parts with no schematic link never move: when a connected group
/// holds any, they are where the walk starts. Otherwise it starts from the chain's recorded anchor
/// (<see cref="GeneratedPlacement.ChainAnchor"/>), else the part joined to the lowest-numbered port. Drawn copper joins
/// at no cost (a contact is no step further from the start), so copper drawn between two parts holds them together
/// — and a change between two held places cannot be absorbed: nothing on either side moves, and the gap is
/// reported naming what holds each side.</item>
/// <item><b>D4/D5 — several changes, and loops.</b> The walk is breadth-first in joints from the side that stays, so
/// changes apply nearest first, each through the layout as already moved. Where a loop is reached two ways that
/// disagree, the way carrying a change wins (only one arm of a branch-line got longer), then the way whose first
/// change is nearest the stay side; the other joint is left open and reported. Two arms that changed alike agree,
/// and nothing is reported.</item>
/// <item><b>D6</b> — a joint the user opened is not an edge, so nothing beyond it is reached. <b>D7</b> — drawn copper
/// reached from one moving group moves rigidly with it. <b>D8</b> — nothing avoids obstacles; SDL-3's overlap check
/// reads the layout as the run leaves it.</item>
/// </list>
///
/// <para>Pure: it reads the parts as the planner hands them and returns moves and report lines. The planner writes
/// the moves into its replacement instances, so the whole run stays one undoable step (D9).</para>
/// </summary>
public static class DeltaPropagation
{
    /// <summary>A rigid motion of the plane: rotate by <see cref="Deg"/> about the origin, then translate.</summary>
    public readonly record struct Rigid(double Deg, double Tx, double Ty)
    {
        public static readonly Rigid Identity = new(0, 0, 0);

        /// <summary>The frame of a pin at (<paramref name="x"/>, <paramref name="y"/>) facing <paramref name="deg"/>.</summary>
        public static Rigid Pose(double x, double y, double deg) => new(LayoutAngle.Normalize(deg), x, y);

        public (double X, double Y) Apply(double x, double y)
        {
            var (c, s) = LayoutAngle.CosSin(Deg);
            return (x * c - y * s + Tx, x * s + y * c + Ty);
        }

        /// <summary><c>a · b</c>: apply <paramref name="b"/> first.</summary>
        public static Rigid operator *(Rigid a, Rigid b)
        {
            var (x, y) = a.Apply(b.Tx, b.Ty);
            return new(LayoutAngle.Normalize(a.Deg + b.Deg), x, y);
        }

        public Rigid Inverse()
        {
            var (c, s) = LayoutAngle.CosSin(-Deg);
            return new(LayoutAngle.Normalize(-Deg), -(Tx * c - Ty * s), -(Tx * s + Ty * c));
        }

        public double SignedDeg => Deg > 180.0 ? Deg - 360.0 : Deg;

        public bool IsIdentity => Math.Abs(SignedDeg) <= ChainPlacer.CloseDeg && Math.Abs(Tx) < 1e-6 && Math.Abs(Ty) < 1e-6;
    }

    /// <summary>A placed part the schematic names: its index in the target, its instance before this run and as the
    /// run replaces it so far (a new cell, a carried turn), and its component.</summary>
    public sealed record LinkedPart(int Index, string Id, EditableComponent Comp, LayoutInstance Before, LayoutInstance After);

    /// <summary>What the planner hands over. <paramref name="Skip"/>: instance indices that take no part at all —
    /// deleted this run, or being re-snapped (R-sdl4-2).</summary>
    public sealed record Input(
        LayoutView Target, IReadOnlyList<LinkedPart> Parts, IReadOnlySet<int> Skip, string SchematicDir, string LayoutBaseDir,
        Technology? Technology, NetExtractor.ExtractionResult Extraction, Func<string, int?> PortOf);

    /// <summary>The moves, by instance index (the instance as it must finally be) and by shape index; the report;
    /// and the joints this run opened (<c>id:terminal|id:terminal</c>, lower id first), which the chain step leaves
    /// to this report.</summary>
    public sealed record Outcome(
        IReadOnlyDictionary<int, LayoutInstance> Instances, IReadOnlyDictionary<int, LayoutShape> Shapes,
        IReadOnlyList<ReportLine> Lines, IReadOnlySet<string> Opened)
    {
        public static readonly Outcome None = new(new Dictionary<int, LayoutInstance>(), new Dictionary<int, LayoutShape>(),
                                                  [], new HashSet<string>());
    }

    /// <summary>The key a joint between two linked parts is known by, either way round.</summary>
    public static string JointKey(string a, int ta, string b, int tb) =>
        JointGraph.CompareIds(a, b) <= 0 ? $"{a}:{ta}|{b}:{tb}" : $"{b}:{tb}|{a}:{ta}";

    private enum BodyKind { Linked, Unlinked, Drawn }

    private sealed class Body
    {
        public required BodyKind Kind;
        public required int Index;          // instance index, or shape index for drawn copper
        public string? Id;
        public int Order;
        public LayoutInstance? Before, After;
        public LayoutShape? Shape;
        public IReadOnlyList<LayoutPin?> OldPins = [], NewPins = [];
        public bool Locked, Changed, Anchor, HadRecord = true;
        public int? Port;
        public readonly List<Edge> Edges = [];
        public int Contacts;
        public Rigid? T;
        public Cand? By;
        public string Name => Id ?? Before?.DisplayRefDes
                              ?? (Before is { CellRef: { Length: > 0 } c } ? Path.GetFileNameWithoutExtension(c.TrimEnd('/', '\\')) : "a placement");
    }

    /// <summary>A made joint (<see cref="FromPin"/>/<see cref="ToPin"/> are 0-based terminals) or a copper contact
    /// (both −1).</summary>
    private sealed record Edge(Body From, int FromPin, Body To, int ToPin)
    {
        public bool Contact => FromPin < 0;
        public string Key => Contact ? "" : JointKey(From.Id!, FromPin + 1, To.Id!, ToPin + 1);
    }

    private sealed class Cand
    {
        public required Body Node;
        public required Rigid T;
        public required int Dist;
        public bool HasChange;
        public int FirstChangeDist = int.MaxValue, FirstChangeOrder = int.MaxValue;
        public int SourceOrder;
        public Edge? Via;
        public Body? LastChange;
        public required Body Root;
    }

    public static Outcome Run(Input input)
    {
        var tech = input.Technology;
        var target = input.Target;
        var linked = new List<Body>();
        var byInstance = new Dictionary<int, Body>();
        var cmp = Comparer<string>.Create(JointGraph.CompareIds);
        var graphParts = new List<JointGraph.Part>();
        var partBody = new Dictionary<JointGraph.Part, Body>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);

        foreach (var p in input.Parts.OrderBy(p => p.Id, cmp))
        {
            if (input.Skip.Contains(p.Index) || p.Before.Mag != 1.0 || p.Before.Rows * p.Before.Cols != 1) continue;
            var oldPins = PartTerminals(p.Comp, p.Before, input.SchematicDir, input.LayoutBaseDir, tech, out var kind, out _);
            var newPins = PartTerminals(p.Comp, p.After, input.SchematicDir, input.LayoutBaseDir, tech, out _, out _);
            if (oldPins is null || newPins is null) continue;
            var record = target.GeneratedPlacements.GetValueOrDefault(p.Id);
            var body = new Body
            {
                Kind = BodyKind.Linked, Index = p.Index, Id = p.Id, Before = p.Before, After = p.After,
                OldPins = oldPins, NewPins = newPins, Locked = p.Before.Locked,
                Anchor = record?.ChainAnchor == true, HadRecord = record is not null, Port = input.PortOf(p.Id),
            };
            body.Changed = Enumerable.Range(0, Math.Min(oldPins.Count, newPins.Count))
                                     .Any(k => oldPins[k] is not null && newPins[k] is not null && !Delta(body, k).IsIdentity);
            body.Order = linked.Count;
            linked.Add(body);
            byInstance[p.Index] = body;
            var part = new JointGraph.Part(p.Id, p.Before, oldPins, true, kind);
            graphParts.Add(part);
            partBody[part] = body;
        }
        if (!linked.Any(b => b.Changed)) return Outcome.None;

        // D2: the joints the schematic asks for, and of those the ones the layout has MADE.
        var graph = JointGraph.Build(input.Extraction, graphParts);
        foreach (var joint in graph.Joints)
        {
            var (a, b) = (partBody[joint.A.Part], partBody[joint.B.Part]);
            var (ax, ay, _) = ChainPose.Of(a.Before!).Apply(joint.A.Pin);
            var (bx, by, _) = ChainPose.Of(b.Before!).Apply(joint.B.Pin);
            if (Math.Abs(ax - bx) <= ChainPlacer.CloseDbu && Math.Abs(ay - by) <= ChainPlacer.CloseDbu)
                Link(a, joint.A.Terminal - 1, b, joint.B.Terminal - 1);
        }
        var poses = graph.Parts.ToDictionary(p => p, p => ChainPose.Of(p.Instance),
                                             ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        foreach (var at in graph.Attachments)
        {
            var (px, py, _) = poses[at.Pad.Part].Apply(at.Pad.Pin);
            bool made = false;
            foreach (bool baseFirst in new[] { true, false })
            {
                var (tx, ty, _) = ShuntAttach.Target(at, poses, part => ReferenceEquals(part, at.Base.Part) == baseFirst ? 0 : 1);
                made |= Math.Abs(px - tx) <= ChainPlacer.CloseDbu && Math.Abs(py - ty) <= ChainPlacer.CloseDbu;
            }
            if (!made) continue;
            var pad = partBody[at.Pad.Part];
            Link(partBody[at.Base.Part], at.Base.Terminal - 1, pad, at.Pad.Terminal - 1);
            Link(partBody[at.Across.Part], at.Across.Terminal - 1, pad, at.Pad.Terminal - 1);
        }

        // D3.2/D3.3/D7: copper contact with parts no schematic names and with copper drawn by hand.
        var others = new List<Body>();
        var linkedIndices = input.Parts.Select(p => p.Index).ToHashSet();
        for (int i = 0; i < target.Instances.Count; i++)
        {
            if (linkedIndices.Contains(i) || input.Skip.Contains(i)) continue;
            var inst = target.Instances[i];
            others.Add(new Body { Kind = BodyKind.Unlinked, Index = i, Before = inst, After = inst, Locked = inst.Locked, Order = int.MaxValue / 2 + i });
        }
        var conductors = tech is null ? [] : Conductors.Of(tech).SelectMany(c => c.DrawingLayers).ToHashSet();
        for (int i = 0; i < target.Shapes.Count; i++)
        {
            var s = target.Shapes[i];
            if (s.Generated is not null || s is LabelShape or BitmapShape || !conductors.Contains(s.Layer)) continue;
            others.Add(new Body { Kind = BodyKind.Drawn, Index = i, Shape = s, Order = int.MaxValue / 2 + target.Instances.Count + i });
        }
        if (others.Count > 0 && tech is not null) Contacts(linked, others, conductors, input.LayoutBaseDir, tech, target.DbuPerMicron);
        // Locked parts with no schematic link hold whether or not they touch anything; untouched ones are irrelevant.
        var bodies = linked.Concat(others.Where(o => o.Edges.Count > 0)).ToList();

        // D3, both sides held: a change whose pins split its group into a side holding a locked part (or a part with no
        // schematic link) and a far side holding another cannot be absorbed. Nothing on either side moves, whatever
        // the schematic-id order or wherever the walk's two waves would have met: the part keeps its origin, the joints
        // at its changed pins are opened, and the gap is reported at its own pin, naming what holds each side.
        var heldJoints = new List<(Body Part, Edge Across, string HoldHome, string HoldFar)>();
        foreach (var c in linked.Where(b => b.Changed && !b.Locked).ToList())
        {
            var cut = c.Edges.Where(e => !e.Contact && !Delta(c, e.FromPin).IsIdentity).ToList();
            if (cut.Count == 0) continue;
            var home = Side(c, c, cut);
            string? holdHome = NearestHold(c, home);
            var held = new List<(Edge Edge, string Hold)>();
            foreach (var e in cut)
            {
                if (home.Contains(e.To)) continue;   // a loop back to its own side: D4/D5's business
                if (NearestHold(e.To, Side(e.To, c, cut)) is { } holdFar) held.Add((e, holdFar));
            }
            // Held from its own side and beyond one changed pin, or (a part changed about its middle) beyond two.
            if (holdHome is null ? held.Count < 2 : held.Count == 0) continue;
            c.Changed = false;
            foreach (var (e, holdFar) in held)
            {
                c.Edges.Remove(e);
                e.To.Edges.RemoveAll(x => ReferenceEquals(x.To, c) && x.FromPin == e.ToPin && x.ToPin == e.FromPin);
                heldJoints.Add((c, e, holdHome ?? held.First(h => !ReferenceEquals(h.Edge, e)).Hold, holdFar));
            }
        }

        // D3: where each connected group's walk starts.
        var seeds = new List<Body>();
        var seen = new HashSet<Body>();
        foreach (var start in bodies)
        {
            if (!seen.Add(start)) continue;
            var group = new List<Body> { start };
            for (int q = 0; q < group.Count; q++)
                foreach (var e in group[q].Edges)
                    if (seen.Add(e.To)) group.Add(e.To);
            if (!group.Any(b => b.Changed)) continue;
            var hard = group.Where(b => b.Locked || b.Kind == BodyKind.Unlinked).ToList();
            if (hard.Count > 0) { seeds.AddRange(hard); continue; }
            var groupLinked = group.Where(b => b.Kind == BodyKind.Linked).ToList();
            seeds.Add(groupLinked.Where(b => b.Anchor).MinBy(b => b.Order)
                      ?? groupLinked.Where(b => b.Port is not null).OrderBy(b => b.Port).ThenBy(b => b.Order).FirstOrDefault()
                      ?? groupLinked.MinBy(b => b.Order)!);
        }

        // D2/D4/D5: the walk. Joints are one step, contacts none.
        var queue = new PriorityQueue<Cand, (int, int, int, int, int, long)>();
        long seq = 0;
        void Push(Cand c) => queue.Enqueue(c, (c.Dist, c.HasChange ? 0 : 1, c.FirstChangeDist, c.FirstChangeOrder, c.SourceOrder, seq++));
        foreach (var s in seeds) Push(new Cand { Node = s, T = Rigid.Identity, Dist = 0, Root = s, SourceOrder = s.Order });

        var conflicts = new List<(Cand Winner, Cand Loser)>();
        var conflictKeys = new HashSet<string>();
        while (queue.TryDequeue(out var cand, out _))
        {
            var node = cand.Node;
            if (node.T is { } had)
            {
                if (cand.Via is { } via && !Agree(had, cand.T, node, via))
                {
                    string key = via.Contact ? $"c{Math.Min(via.From.Order, via.To.Order)}-{Math.Max(via.From.Order, via.To.Order)}" : via.Key;
                    if (conflictKeys.Add(key)) conflicts.Add((node.By!, cand));
                }
                continue;
            }
            // Unlinked parts never move, whatever reaches them first.
            if (node.Kind == BodyKind.Unlinked && !cand.T.IsIdentity)
            {
                if (cand.Via is { } v && conflictKeys.Add($"c{Math.Min(v.From.Order, v.To.Order)}-{Math.Max(v.From.Order, v.To.Order)}"))
                    conflicts.Add((new Cand { Node = node, T = Rigid.Identity, Dist = 0, Root = node }, cand));
                node.T = Rigid.Identity;
                node.By = new Cand { Node = node, T = Rigid.Identity, Dist = cand.Dist, Root = node };
            }
            else
            {
                node.T = cand.T;
                node.By = cand;
            }
            foreach (var e in node.Edges)
            {
                var next = e.To;
                var t = node.T.Value;
                var c = new Cand
                {
                    Node = next, Dist = node.By!.Dist + (e.Contact ? 0 : 1), Via = e, Root = node.By.Root, SourceOrder = node.Order,
                    HasChange = node.By.HasChange, FirstChangeDist = node.By.FirstChangeDist,
                    FirstChangeOrder = node.By.FirstChangeOrder, LastChange = node.By.LastChange, T = t,
                };
                if (!e.Contact)
                {
                    var d = node.Changed ? Delta(node, e.FromPin) : Rigid.Identity;
                    if (!d.IsIdentity)
                    {
                        if (!c.HasChange) { c.HasChange = true; c.FirstChangeDist = node.By.Dist; c.FirstChangeOrder = node.Order; }
                        c.LastChange = node;
                    }
                    var back = next.Changed ? Delta(next, e.ToPin).Inverse() : Rigid.Identity;
                    if (!back.IsIdentity) c.LastChange = next;
                    c.T = t * d * back;
                }
                Push(c);
            }
        }

        // The moves.
        var movedInstances = new Dictionary<int, LayoutInstance>();
        var movedShapes = new Dictionary<int, LayoutShape>();
        var movedBy = new Dictionary<Body, List<Body>>();
        foreach (var b in bodies)
        {
            if (b.T is not { } t || t.IsIdentity || b.Kind == BodyKind.Unlinked) continue;
            if (b.Kind == BodyKind.Linked)
            {
                var moved = LayoutGeometry.Clone(b.After!);
                var (x, y) = t.Apply(b.After!.X, b.After.Y);
                moved.X = (long)Math.Round(x);
                moved.Y = (long)Math.Round(y);
                moved.RotationDegrees = b.After.RotationDegrees + t.Deg;
                if (moved.OrientationLink is { } link)
                {
                    double turned = SchematicLayoutOrientation.FromLayout(moved).Deg - SchematicLayoutOrientation.FromLayout(b.After).Deg;
                    moved.OrientationLink = link with { LayoutDeg = LayoutAngle.Normalize(link.LayoutDeg + turned) };
                }
                movedInstances[b.Index] = moved;
            }
            else movedShapes[b.Index] = MoveShape(b.Shape!, t);
            var by = b.By!.LastChange ?? b.By.Root;
            if (!movedBy.TryGetValue(by, out var list)) movedBy[by] = list = [];
            list.Add(b);
        }

        var lines = new List<ReportLine>();
        string u = LayoutUnits.Suffix(target.DisplayUnit);
        string Len(double dbu) => LayoutUnits.Format((long)Math.Round(dbu), target.DisplayUnit, target.DbuPerMicron);
        foreach (var (change, members) in movedBy.OrderBy(kv => kv.Key.Order))
        {
            var parts = members.Where(m => m.Kind == BodyKind.Linked).OrderBy(m => m.Order).Select(m => m.Id!).ToList();
            int drawn = members.Count(m => m.Kind == BodyKind.Drawn);
            var names = new List<string>(parts);
            if (drawn > 0) names.Add(drawn == 1 ? "1 shape of drawn copper" : $"{drawn} shapes of drawn copper");
            var (shift, turn) = Size(change);
            string how = Math.Abs(turn) > ChainPlacer.CloseDeg
                ? $"moved and turned {JoinNames(names)} by {turn.ToString("0.###", CultureInfo.InvariantCulture)}°"
                : $"moved {JoinNames(names)} by {Len(shift)} {u}";
            lines.Add(new ReportLine(change.Id ?? "", $"{change.Id} — {how} so the copper stays joined",
                                     ReportSeverity.Info) { Ids = [.. parts] });
        }
        var unrecorded = bodies.Where(b => b.Kind == BodyKind.Linked && !b.HadRecord && movedInstances.ContainsKey(b.Index))
                               .Select(b => b.Id!).ToList();
        if (unrecorded.Count > 0)
            lines.Add(new ReportLine("", $"{JoinNames(unrecorded)} {(unrecorded.Count == 1 ? "was" : "were")} placed before Update " +
                                         $"Layout kept a placement record; {(unrecorded.Count == 1 ? "it" : "they")} moved with the " +
                                         "change and are recorded from now on", ReportSeverity.Info) { Ids = unrecorded });

        var opened = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, e, holdHome, holdFar) in heldJoints)
        {
            opened.Add(e.Key);
            var w = NewWorld(p, e.FromPin);
            var (fx, fy) = e.To.T is { } ft ? FinalPin(e.To, e.ToPin) : (NewWorld(e.To, e.ToPin).Tx, NewWorld(e.To, e.ToPin).Ty);
            lines.Add(new ReportLine(p.Id!, $"{DescribeChange(p, Len, u)}; both sides are held ({holdHome}; {holdFar}). " +
                                            Opening(p, e.FromPin, w.Tx, w.Ty, w.Deg, e.To, fx, fy),
                                     ReportSeverity.Warning) { Kind = ReportKind.LoopGap, Ids = [p.Id!] });
        }
        foreach (var (winner, loser) in conflicts)
        {
            var via = loser.Via!;
            if (via.Contact)
            {
                var drawnBody = via.From.Kind == BodyKind.Drawn ? via.From : via.To.Kind == BodyKind.Drawn ? via.To : null;
                var part = via.From.Kind == BodyKind.Linked ? via.From : via.To;
                string what = drawnBody is not null ? DescribeDrawn(drawnBody) : $"{(via.From.Kind == BodyKind.Unlinked ? via.From : via.To).Name}, which has no schematic link,";
                lines.Add(new ReportLine(part.Id ?? "", $"{what} touched {part.Name}, which this run moves away from it; the " +
                                                        "copper was left where it was", ReportSeverity.Warning)
                    { Kind = ReportKind.LoopGap, Ids = part.Id is { } pid ? [pid] : [] });
                continue;
            }
            opened.Add(via.Key);
            // Where the joint ends up: the node's pin as the winner placed it, and the source's as the loser would.
            var node = loser.Node;
            var (nx, ny) = FinalPin(node, via.ToPin);
            var (sx, sy) = FinalPin(via.From, via.FromPin);
            double dx = sx - nx, dy = sy - ny, gap = Math.Sqrt(dx * dx + dy * dy);
            var changed = node.Changed && !Delta(node, via.ToPin).IsIdentity ? (node, via.ToPin)
                        : via.From.Changed && !Delta(via.From, via.FromPin).IsIdentity ? (via.From, via.FromPin)
                        : ((Body, int)?)null;
            string? holdA = Holder(winner.Via?.From ?? winner.Node, winner), holdB = Holder(via.From, via.From.By!);
            if (changed is var (p, pin) && holdA != holdB)
            {
                var pw = NewWorld(p, pin);
                var (px, py) = FinalPin(p, pin);
                var (other, ox, oy) = ReferenceEquals(p, node) ? (via.From, sx, sy) : (node, nx, ny);
                lines.Add(new ReportLine(p.Id!, $"{DescribeChange(p, Len, u)}; both sides are held ({holdA}; {holdB}). " +
                                                Opening(p, pin, px, py, LayoutAngle.Normalize(pw.Deg + p.T!.Value.Deg), other, ox, oy),
                                         ReportSeverity.Warning) { Kind = ReportKind.LoopGap, Ids = [p.Id!] });
                continue;
            }
            var loop = LoopOf(node, via.From);
            lines.Add(new ReportLine("",
                $"Loop {string.Join(", ", loop.Select(b => b.Id))} does not close after the schematic's change: " +
                $"{via.From.Id} pin {PinName(via.From, via.FromPin)} is {Len(gap)} {u} from {node.Id} pin {PinName(node, via.ToPin)} " +
                $"(x {Len(dx)}, y {Len(dy)} {u}).", ReportSeverity.Warning)
                { Kind = ReportKind.LoopGap, Ids = [.. loop.Select(b => b.Id!)] });
        }

        return new Outcome(movedInstances, movedShapes, lines, opened);

        // What a held change leaves at the changed pin: a gap when the joint's other pin is beyond the pin's face, and an
        // overlap when the changed part now runs into the other — a longer line between two held places.
        string Opening(Body p, int pin, double px, double py, double facingDeg, Body other, double ox, double oy)
        {
            double dist = Math.Sqrt((ox - px) * (ox - px) + (oy - py) * (oy - py));
            var (fx, fy) = LayoutAngle.CosSin(facingDeg);
            return (ox - px) * fx + (oy - py) * fy < -0.5
                ? $"{p.Id} pin {PinName(p, pin)} now overlaps {other.Id ?? other.Name} by {Len(dist)} {u}."
                : $"Left a {Len(dist)} {u} gap at {p.Id} pin {PinName(p, pin)}.";
        }

        // The bodies reachable from start without crossing the cut edges of the changed part c.
        static HashSet<Body> Side(Body start, Body c, List<Edge> cut)
        {
            var side = new HashSet<Body> { start };
            var todo = new Queue<Body>([start]);
            while (todo.TryDequeue(out var b))
                foreach (var e in b.Edges)
                {
                    if (ReferenceEquals(b, c) && cut.Contains(e)) continue;
                    if (ReferenceEquals(e.To, c) && cut.Any(x => ReferenceEquals(x.To, b) && x.FromPin == e.ToPin && x.ToPin == e.FromPin)) continue;
                    if (side.Add(e.To)) todo.Enqueue(e.To);
                }
            return side;
        }

        // What holds a side, said as Holder says it: the nearest locked part or part with no schematic link, breadth-first
        // from start in joints, schematic-id order breaking ties.
        static string? NearestHold(Body start, HashSet<Body> side)
        {
            var lastLinked = new Dictionary<Body, Body?> { [start] = start.Kind == BodyKind.Linked ? start : null };
            var todo = new Queue<Body>([start]);
            while (todo.TryDequeue(out var b))
            {
                if (b.Kind == BodyKind.Linked && b.Locked) return $"{b.Id} is locked";
                if (b.Kind == BodyKind.Unlinked)
                    return lastLinked[b] is { } ll ? $"{ll.Id} is joined to {b.Name}, which has no schematic link"
                         : b.Locked ? $"{b.Name} is locked" : $"{b.Name} has no schematic link";
                foreach (var e in b.Edges.OrderBy(e => e.To.Order))
                    if (side.Contains(e.To) && !lastLinked.ContainsKey(e.To))
                    {
                        lastLinked[e.To] = e.To.Kind == BodyKind.Linked ? e.To : lastLinked[b];
                        todo.Enqueue(e.To);
                    }
            }
            return null;
        }

        void Link(Body a, int pa, Body b, int pb)
        {
            if (ReferenceEquals(a, b)) return;
            a.Edges.Add(new Edge(a, pa, b, pb));
            b.Edges.Add(new Edge(b, pb, a, pa));
        }

        (double X, double Y) FinalPin(Body b, int k)
        {
            var w = NewWorld(b, k);
            return b.T!.Value.Apply(w.Tx, w.Ty);
        }

        string DescribeDrawn(Body d)
        {
            var box = LayoutGeometry.BboxOf(d.Shape!);
            string layer = tech?.Layers.FirstOrDefault(l => l.Key == d.Shape!.Layer)?.Name is { Length: > 0 } n ? n : $"{d.Shape!.Layer.Layer}/{d.Shape.Layer.Datatype}";
            return $"copper drawn on {layer} at {Len((box.MinX + box.MaxX) / 2.0)}, {Len((box.MinY + box.MaxY) / 2.0)} {u}";
        }

        // D3's reason a side stays, read along the walk from where the conflict was met back to where it started: the
        // nearest drawn copper joining two things, locked part or part with no schematic link, else the start itself.
        string Holder(Body from, Cand by)
        {
            Body? lastLinked = null;
            for (Body? b = from; b is not null; b = b.By?.Via?.From)
            {
                if (b.Kind == BodyKind.Linked && b.Locked) return $"{b.Id} is locked";
                if (b.Kind == BodyKind.Unlinked)
                    return lastLinked is not null ? $"{lastLinked.Id} is joined to {b.Name}, which has no schematic link"
                         : b.Locked ? $"{b.Name} is locked" : $"{b.Name} has no schematic link";
                if (b.Kind == BodyKind.Drawn && b.Contacts >= 2)
                    return $"{(lastLinked ?? b.Edges.Select(e => e.To).FirstOrDefault(o => o.Kind == BodyKind.Linked))?.Id} " +
                           $"is joined to {DescribeDrawn(b)}";
                if (b.Kind == BodyKind.Linked) lastLinked = b;
                if (b.By?.Via is null) return $"{b.Id ?? b.Name} anchors the chain";
            }
            return $"{by.Root.Id ?? by.Root.Name} anchors the chain";
        }
    }

    // ── Pins, before and after ───────────────────────────────────────────────────────────────────

    private static Rigid OldWorld(Body b, int k)
    {
        var (x, y, d) = ChainPose.Of(b.Before!).Apply(b.OldPins[k]!);
        return Rigid.Pose(x, y, d);
    }

    private static Rigid NewWorld(Body b, int k)
    {
        if (k >= b.NewPins.Count || b.NewPins[k] is null) return OldWorld(b, k);
        var (x, y, d) = ChainPose.Of(b.After!).Apply(b.NewPins[k]!);
        return Rigid.Pose(x, y, d);
    }

    /// <summary>The motion of a part's pin <paramref name="k"/> from where it was to where the change puts it.</summary>
    private static Rigid Delta(Body b, int k)
    {
        if (k < 0 || k >= b.OldPins.Count || b.OldPins[k] is null || k >= b.NewPins.Count || b.NewPins[k] is null) return Rigid.Identity;
        var d = NewWorld(b, k) * OldWorld(b, k).Inverse();
        // A pin rounded to the DBU on both sides is the same pin.
        return Math.Abs(d.SignedDeg) <= ChainPlacer.CloseDeg
               && Math.Abs(NewWorld(b, k).Tx - OldWorld(b, k).Tx) < 0.5 && Math.Abs(NewWorld(b, k).Ty - OldWorld(b, k).Ty) < 0.5
            ? Rigid.Identity : d;
    }

    /// <summary>Two motions of one body agree when they put the joint's pin (or, for a contact, the body's origin) in
    /// the same place within 1 DBU and turn it alike.</summary>
    private static bool Agree(Rigid a, Rigid b, Body node, Edge via)
    {
        if (Math.Abs(LayoutAngle.Normalize(a.Deg - b.Deg + 180.0) - 180.0) > ChainPlacer.CloseDeg) return false;
        double px, py;
        if (!via.Contact) { var w = NewWorld(node, via.ToPin); (px, py) = (w.Tx, w.Ty); }
        else if (node.After is { } inst) (px, py) = (inst.X, inst.Y);
        else { var box = LayoutGeometry.BboxOf(node.Shape!); (px, py) = ((box.MinX + box.MaxX) / 2.0, (box.MinY + box.MaxY) / 2.0); }
        var (ax, ay) = a.Apply(px, py);
        var (bx, by) = b.Apply(px, py);
        return Math.Abs(ax - bx) <= ChainPlacer.CloseDbu && Math.Abs(ay - by) <= ChainPlacer.CloseDbu;
    }

    /// <summary>How far a change moves its pins, and how far it turns them, at most.</summary>
    private static (double Shift, double Turn) Size(Body change)
    {
        double shift = 0, turn = 0;
        for (int k = 0; k < Math.Min(change.OldPins.Count, change.NewPins.Count); k++)
        {
            if (change.OldPins[k] is null || change.NewPins[k] is null) continue;
            var (o, n) = (OldWorld(change, k), NewWorld(change, k));
            shift = Math.Max(shift, Math.Sqrt((n.Tx - o.Tx) * (n.Tx - o.Tx) + (n.Ty - o.Ty) * (n.Ty - o.Ty)));
            double d = Delta(change, k).SignedDeg;
            if (Math.Abs(d) > Math.Abs(turn)) turn = d;
        }
        return (shift, turn);
    }

    /// <summary>"ML1 is 40.0 µm longer" — a two-pin part by its pin spacing, anything else by what its pins did.</summary>
    private static string DescribeChange(Body p, Func<double, string> len, string u)
    {
        if (p.OldPins.Count == 2 && p.NewPins.Count == 2 && p.OldPins.All(q => q is not null) && p.NewPins.All(q => q is not null))
        {
            double Span(IReadOnlyList<LayoutPin?> pins) => Math.Sqrt(Math.Pow(pins[0]!.X - pins[1]!.X, 2) + Math.Pow(pins[0]!.Y - pins[1]!.Y, 2));
            double d = Span(p.NewPins) - Span(p.OldPins);
            if (Math.Abs(d) >= 0.5) return $"{p.Id} is {len(Math.Abs(d))} {u} {(d > 0 ? "longer" : "shorter")}";
        }
        var (shift, turn) = Size(p);
        return Math.Abs(turn) > ChainPlacer.CloseDeg
            ? $"{p.Id} turns its pins by {turn.ToString("0.###", CultureInfo.InvariantCulture)}°"
            : $"{p.Id} moves a pin by {len(shift)} {u}";
    }

    private static string PinName(Body b, int k) =>
        (k < b.NewPins.Count ? b.NewPins[k] : null)?.Name is { Length: > 0 } n ? n : (k + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The cycle through <paramref name="a"/> and <paramref name="b"/> along the walk, linked parts only, from
    /// its first part in schematic-id order.</summary>
    private static List<Body> LoopOf(Body a, Body b)
    {
        List<Body> Up(Body x)
        {
            var path = new List<Body>();
            for (Body? q = x; q is not null && !path.Contains(q); q = q.By?.Via?.From) path.Add(q);
            return path;
        }
        var up = Up(a);
        var other = Up(b);
        int k = other.FindIndex(up.Contains);
        var cycle = k < 0 ? up.Concat(other).ToList()
                          : up.TakeWhile(q => !ReferenceEquals(q, other[k])).Append(other[k]).Concat(other.Take(k).Reverse()).ToList();
        cycle = cycle.Where(q => q.Kind == BodyKind.Linked).Distinct().ToList();
        if (cycle.Count == 0) return cycle;
        int start = cycle.IndexOf(cycle.MinBy(q => q.Order)!);
        return cycle.Skip(start).Concat(cycle.Take(start)).ToList();
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>A drawn shape carried by <paramref name="t"/>, unchanged in shape (D7). A box is promoted to a polygon
    /// first when the turn is not a quarter turn.</summary>
    private static LayoutShape MoveShape(LayoutShape shape, Rigid t)
    {
        var moved = LayoutGeometry.Clone(shape);
        bool cardinal = LayoutAngle.TryCardinal(t.Deg, out _);
        if (!cardinal && LayoutRotationPromotion.NeedsPromotion(moved)) moved = LayoutRotationPromotion.Promote(moved);
        LayoutCoordinateWalk.Transform(moved, new LayoutCoordinateTransform(
            (x, y) => { var (px, py) = t.Apply(x, y); return ((long)Math.Round(px), (long)Math.Round(py)); },
            m => m, RotatesAxes: !cardinal));
        return moved;
    }

    // ── Copper contact ───────────────────────────────────────────────────────────────────────────

    /// <summary>Contact edges: every unlinked placement or drawn shape whose conductor copper meets a linked part's, or
    /// a drawn shape's, within the partition's hairline. Two linked parts are never joined by contact — only by the
    /// joints the schematic names.</summary>
    private static void Contacts(List<Body> linked, List<Body> others, HashSet<LayerKey> conductors, string layoutBaseDir,
                                 Technology tech, int dbuPerMicron)
    {
        long reach = Math.Max(1, dbuPerMicron);
        var copper = new Dictionary<Body, Dictionary<LayerKey, Paths64>>();
        var boxes = new Dictionary<Body, Bbox>();
        foreach (var b in linked.Concat(others))
        {
            IEnumerable<LayoutShape> shapes = b.Shape is { } s ? [s]
                : LayoutFlatten.FlattenAllLevels(b.Before!, layoutBaseDir, tech).Shapes.Where(x => conductors.Contains(x.Layer));
            var byLayer = new Dictionary<LayerKey, Paths64>();
            var box = Bbox.Empty;
            foreach (var shape in shapes)
            {
                if (shape is LabelShape) continue;
                var paths = LayoutClipper.ToClipperPaths(shape, Math.Max(1, LayoutFlattener.ResolveTolDbu(shape, null)));
                if (!byLayer.TryGetValue(shape.Layer, out var acc)) byLayer[shape.Layer] = acc = [];
                acc.AddRange(paths);
                box = box.Union(LayoutGeometry.BboxOf(shape));
            }
            foreach (var key in byLayer.Keys.ToList())
                byLayer[key] = Clipper.InflatePaths(byLayer[key], reach / 2.0, JoinType.Miter, EndType.Polygon);
            copper[b] = byLayer;
            boxes[b] = box;
        }

        bool Touch(Body a, Body b)
        {
            var (ba, bb) = (boxes[a], boxes[b]);
            if (ba.IsEmpty || bb.IsEmpty || ba.MaxX + reach < bb.MinX || bb.MaxX + reach < ba.MinX
                || ba.MaxY + reach < bb.MinY || bb.MaxY + reach < ba.MinY) return false;
            foreach (var (layer, pa) in copper[a])
                if (copper[b].TryGetValue(layer, out var pb)
                    && Clipper.Intersect(pa, pb, FillRule.NonZero).Sum(p => Math.Abs(Clipper.Area(p))) > 0) return true;
            return false;
        }

        void Join(Body a, Body b)
        {
            a.Edges.Add(new Edge(a, -1, b, -1));
            b.Edges.Add(new Edge(b, -1, a, -1));
            a.Contacts++;
            b.Contacts++;
        }

        // Broad phase through the layout's spatial index, as SDL-3's overlap check reads it: an imported board's tens of
        // thousands of drawn shapes compared pairwise would be billions of box tests on every re-run that changes anything.
        // Candidates come back in ascending index, so the contacts are joined in the order the pairwise loop made them.
        var all = others.Concat(linked).ToList();
        var rects = all.Select(b => (LayoutShape)(boxes[b].IsEmpty
            ? new RectShape()
            : new RectShape { X1 = boxes[b].MinX, Y1 = boxes[b].MinY, X2 = boxes[b].MaxX, Y2 = boxes[b].MaxY })).ToList();
        var index = new LayoutSpatialIndex();
        for (int i = 0; i < others.Count; i++)
        {
            var box = boxes[others[i]];
            if (box.IsEmpty) continue;
            var near = new Bbox(box.MinX - reach, box.MinY - reach, box.MaxX + reach, box.MaxY + reach);
            var hits = index.QueryIntersecting(rects, near);
            foreach (int k in hits.Where(k => k >= others.Count))
                if (Touch(others[i], all[k])) Join(others[i], all[k]);
            foreach (int j in hits.Where(j => j > i && j < others.Count))
                if ((others[i].Kind == BodyKind.Drawn || others[j].Kind == BodyKind.Drawn) && Touch(others[i], others[j]))
                    Join(others[i], others[j]);
        }
    }
}
