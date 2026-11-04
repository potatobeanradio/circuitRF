namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// A part's placement as the chain carries it: origin in DBU and angle in degrees, both EXACT doubles, and the
/// mirror (brief-sdl-1 D3). Rounded to DBU once, by <see cref="WriteTo"/>, when the instance is written —
/// rounding at every link would add up to a DBU per part, which on an any-angle chain shows within twenty parts.
/// The transform is <see cref="LayoutInstanceTransform.TransformPoint"/>'s, without its rounding: mirror (negate
/// local X), then rotate counter-clockwise, then translate.
/// </summary>
public readonly record struct ChainPose(double X, double Y, double Deg, bool Mirror)
{
    public static ChainPose Of(LayoutInstance inst) => new(inst.X, inst.Y, inst.RotationDegrees, inst.MirrorX);

    /// <summary>Where <paramref name="pin"/> lands, and which way it faces, under this pose.</summary>
    public (double X, double Y, double Deg) Apply(LayoutPin pin)
    {
        var (x, y) = Rotate(Mirror ? -pin.X : pin.X, pin.Y, Deg);
        return (X + x, Y + y, Facing(pin.OutwardDeg));
    }

    /// <summary>A local outward direction as this pose turns it: a mirror reflects it about the local Y axis.</summary>
    public double Facing(double localDeg) => LayoutAngle.Normalize((Mirror ? 180.0 - localDeg : localDeg) + Deg);

    public void WriteTo(LayoutInstance inst)
    {
        inst.X = (long)Math.Round(X);
        inst.Y = (long)Math.Round(Y);
        inst.RotationDegrees = Deg;
        inst.MirrorX = Mirror;
    }

    internal static (double X, double Y) Rotate(double x, double y, double deg)
    {
        var (c, s) = LayoutAngle.CosSin(deg);
        return (x * c - y * s, x * s + y * c);
    }
}

/// <summary>
/// brief-sdl-1 R-sdl1-2: lays one <see cref="JointGraph.Chain"/> out so every pin the schematic joins meets its
/// partner — same point, facing each other — and reports the joints that cannot meet (series rule S1: never
/// distort to close).
///
/// <list type="number">
/// <item><b>Tree.</b> Breadth-first from the anchor, neighbours in schematic-id order. Each tree edge places the
/// child: its mirror is the schematic's (D2: chirality is the drawing's, never the pins'), its angle
/// <c>θ = θ_parent_out + 180° − θ_child_local_out</c> with the mirror applied to the local direction first, and its
/// origin whatever puts its pin exactly on the parent's.</item>
/// <item><b>Closing joints.</b> Every edge that is not a tree edge is checked: within 1 DBU in each axis and
/// 1e-6° of facing it is joined (D4); anything more is a <see cref="Gap"/>. Nothing moves to close it.</item>
/// <item><b>Existing parts are fixed</b> (D5). They keep the pose they have; an edge reaching one is a closing
/// joint, and parts beyond it chain from it.</item>
/// </list>
/// </summary>
public static class ChainPlacer
{
    /// <summary>D4's tolerance: a closing joint within this in each axis…</summary>
    public const double CloseDbu = 1.0;

    /// <summary>…and facing within this of exactly opposite, is joined.</summary>
    public const double CloseDeg = 1e-6;

    /// <summary>A closing joint that does not close. <paramref name="Dx"/>/<paramref name="Dy"/> are
    /// <c>From − To</c> in DBU; <paramref name="AngleDeg"/> is how far <c>From</c>'s facing is from exactly
    /// opposite <c>To</c>'s, signed, in (−180°, 180°]. <paramref name="Loop"/> is the cycle the joint closes, from
    /// its first part in schematic-id order — empty when the two ends hang from different fixed parts, so the
    /// joint closes no loop of this run's making. <paramref name="Joint"/> is null for a side-on pad that does not sit
    /// against its joint (<paramref name="Attachment"/>; <c>From</c> is the pad, <c>To</c> the line end it attaches
    /// to). <paramref name="Bridge"/> is set when the misfit is a bridging part's (brief-sdl-2 D6).</summary>
    public sealed record Gap(JointGraph.Joint? Joint, JointGraph.End From, JointGraph.End To,
                             double Dx, double Dy, double AngleDeg, IReadOnlyList<JointGraph.Part> Loop)
    {
        public JointGraph.Attachment? Attachment { get; init; }
        public BridgeFit? Bridge { get; init; }
    }

    /// <summary>A part both of whose pads join line ends the chain had already placed (brief-sdl-2 D6), whose pads do
    /// not span the gap between those ends: <paramref name="RequiredDbu"/> between <paramref name="First"/> and
    /// <paramref name="Second"/> (the line ends), <paramref name="ActualDbu"/> between its own pad edges. It is centred
    /// between them, so each joint is out by half the difference — and nothing is stretched (series rule S1).</summary>
    public sealed record BridgeFit(JointGraph.Part Part, JointGraph.End First, JointGraph.End Second,
                                   double RequiredDbu, double ActualDbu);

    public sealed record Result(
        JointGraph.Part Anchor,
        IReadOnlyDictionary<JointGraph.Part, ChainPose> Poses,
        int JoinedCount,
        IReadOnlyList<Gap> Gaps);

    /// <summary>
    /// The anchor (R-sdl1-2 step 1), first match wins: an existing part (the first in schematic-id order); the part joined to the
    /// lowest-numbered schematic Port (<paramref name="portNumberOf"/> names the port on a part's terminal's net,
    /// null when there is none); the first part in schematic-id order. A part held in the chain only by a side-on
    /// attachment is never a new chain's anchor: it is placed against a joint, so the joint has to be placed first.
    /// </summary>
    public static JointGraph.Part ChooseAnchor(JointGraph.Chain chain, Func<JointGraph.Part, int?> portNumberOf)
    {
        var jointed = chain.Parts.Where(p => chain.Joints.Any(j => ReferenceEquals(j.A.Part, p) || ReferenceEquals(j.B.Part, p)))
                                 .ToList();
        // An existing part with a joint first: a side-on part (a shunt pad) has none, so a walk started from it would
        // reach nothing.
        if ((jointed.FirstOrDefault(p => p.Existing) ?? chain.Parts.FirstOrDefault(p => p.Existing)) is { } existing)
            return existing;
        if (jointed.Count == 0) jointed = [.. chain.Parts];
        JointGraph.Part? best = null;
        int bestNumber = int.MaxValue;
        foreach (var part in jointed)
            if (portNumberOf(part) is { } n && n < bestNumber) { best = part; bestNumber = n; }
        return best ?? jointed[0];
    }

    /// <summary>
    /// Every part of <paramref name="chain"/> placed from <paramref name="anchor"/> at <paramref name="anchorPose"/>.
    /// <paramref name="mirrorOf"/> is a NEW part's mirror from the schematic (D2); an existing part's pose is its
    /// instance's. The poses returned are for every part, existing ones included, so the caller can measure the chain.
    ///
    /// <para>Joints are walked first, breadth-first. A side-on attachment (brief-sdl-2 D4/D5) places its pad's part
    /// once both ends of its joint are placed — and the walk then carries on from that part, so anything joined beyond
    /// it chains from where it went. An attachment whose part was already placed by another route is checked as a
    /// closing joint is.</para>
    /// </summary>
    public static Result Place(JointGraph.Chain chain, JointGraph.Part anchor, ChainPose anchorPose,
                               Func<JointGraph.Part, bool> mirrorOf)
    {
        var cmp = ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>;
        var poses = new Dictionary<JointGraph.Part, ChainPose>(cmp);
        var parent = new Dictionary<JointGraph.Part, JointGraph.Part?>(cmp);
        var depth = new Dictionary<JointGraph.Part, int>(cmp);
        foreach (var p in chain.Parts)
            if (p.Existing && !ReferenceEquals(p, anchor)) { poses[p] = ChainPose.Of(p.Instance); parent[p] = null; depth[p] = 0; }
        poses[anchor] = anchorPose;
        parent[anchor] = null;
        depth[anchor] = 0;

        var edgesOf = chain.Parts.ToDictionary(p => p, p => chain.Joints
            .Where(j => ReferenceEquals(j.A.Part, p) || ReferenceEquals(j.B.Part, p))
            .OrderBy(j => j.Other(p).Order).ThenBy(j => j.EndOf(p).Terminal).ToList(), cmp);

        var used = new HashSet<JointGraph.Joint>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Joint>);
        var closing = new List<JointGraph.Joint>();
        var queued = new HashSet<JointGraph.Part>(cmp) { anchor };
        var queue = new Queue<JointGraph.Part>([anchor]);
        var treeChild = new HashSet<JointGraph.Part>(cmp);   // parts something was placed FROM
        int joined = 0;
        int Depth(JointGraph.Part p) => depth.GetValueOrDefault(p);

        var pending = chain.Attachments.ToList();
        var attachClosing = new List<JointGraph.Attachment>();
        while (true)
        {
            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                foreach (var joint in edgesOf[u])
                {
                    if (!used.Add(joint)) continue;
                    var v = joint.Other(u);
                    if (poses.ContainsKey(v))
                    {
                        closing.Add(joint);
                        if (queued.Add(v)) queue.Enqueue(v);   // a fixed part reached: parts beyond it chain from it
                        continue;
                    }
                    poses[v] = Child(poses[u], joint.EndOf(u).Pin, joint.EndOf(v).Pin, mirrorOf(v));
                    parent[v] = u;
                    depth[v] = Depth(u) + 1;
                    treeChild.Add(u);
                    joined++;
                    if (queued.Add(v)) queue.Enqueue(v);
                }
            }

            // The next attachment whose joint is placed, in schematic-id order: place its pad's part, or — when that part
            // is already placed — check it.
            int next = pending.FindIndex(a => poses.ContainsKey(a.Base.Part) && poses.ContainsKey(a.Across.Part));
            if (next < 0)
            {
                // An existing part the walk has not reached — held to the rest only through a side-on part it already
                // placed, say: walk on from it, so the parts beyond it are placed and its joints checked.
                if (chain.Parts.FirstOrDefault(p => p.Existing && !queued.Contains(p)) is not { } unreached) break;
                queued.Add(unreached);
                queue.Enqueue(unreached);
                continue;
            }
            var attach = pending[next];
            pending.RemoveAt(next);
            var pad = attach.Pad.Part;
            if (poses.ContainsKey(pad))
            {
                attachClosing.Add(attach);
                if (queued.Add(pad)) queue.Enqueue(pad);
                continue;
            }
            poses[pad] = ShuntAttach.Place(attach, poses, Depth, mirrorOf(pad));
            parent[pad] = attach.Base.Part;
            depth[pad] = Math.Max(Depth(attach.Base.Part), Depth(attach.Across.Part)) + 1;
            treeChild.Add(attach.Base.Part);
            joined++;
            if (queued.Add(pad)) queue.Enqueue(pad);
        }

        var gaps = new List<Gap>();
        foreach (var joint in closing)
        {
            // Measured from the end later in schematic-id order to the earlier one, whichever way the walk met them.
            var (from, to) = (joint.B, joint.A);
            var (dx, dy, angle) = Miss(poses, from, to);
            if (Closes(dx, dy, angle)) { joined++; continue; }

            // D6: a part whose two pads join two line ends both placed before it, and from which nothing else was placed,
            // is centred between them, so the misfit is split rather than all at one end.
            var bridge = BridgeOf(joint, from, to, parent, treeChild, edgesOf, anchor, chain);
            if (bridge is { } b)
            {
                var (part, first, second) = b;
                double sign = ReferenceEquals(part, from.Part) ? -0.5 : 0.5;
                var pose = poses[part];
                poses[part] = pose with { X = pose.X + sign * dx, Y = pose.Y + sign * dy };
                joined--;   // the joint it was placed from is now out by half the misfit too: neither of its joints is made
                var (ax, ay, _) = poses[first.Part].Apply(first.Pin);
                var (cx, cy, _) = poses[second.Part].Apply(second.Pin);
                var pins = edgesOf[part].Select(j => j.EndOf(part).Pin).ToList();
                double actual = Math.Sqrt(Sq(pins[0].X - pins[1].X) + Sq(pins[0].Y - pins[1].Y));
                gaps.Add(new Gap(joint, from, to, dx, dy, angle, Loop(from.Part, to.Part, parent))
                {
                    Bridge = new BridgeFit(part, first, second, Math.Sqrt(Sq(ax - cx) + Sq(ay - cy)), actual),
                });
                continue;
            }
            gaps.Add(new Gap(joint, from, to, dx, dy, angle, Loop(from.Part, to.Part, parent)));
        }

        foreach (var attach in attachClosing.Concat(pending.Where(a => poses.ContainsKey(a.Pad.Part))))
        {
            if (!poses.ContainsKey(attach.Base.Part) || !poses.ContainsKey(attach.Across.Part)) continue;
            var (px, py, pf) = poses[attach.Pad.Part].Apply(attach.Pad.Pin);
            (double Dx, double Dy, double Angle) MissFrom((double X, double Y, double F) t)
            {
                double a = LayoutAngle.Normalize(pf - t.F);
                return (px - t.X, py - t.Y, a > 180.0 ? a - 360.0 : a);
            }
            var (dx, dy, angle) = MissFrom(ShuntAttach.Target(attach, poses, Depth));
            if (Closes(dx, dy, angle)) { joined++; continue; }
            // A pad an earlier run placed was placed with ITS walk's direction of travel, which a walk started from another
            // part (a re-run's anchor is an existing part) can read the other way: it sits against its joint either way.
            if (attach.Pad.Part.Existing)
            {
                bool baseFirst = Depth(attach.Base.Part) <= Depth(attach.Across.Part);
                var other = MissFrom(ShuntAttach.Target(attach, poses, part => ReferenceEquals(part, attach.Base.Part) == baseFirst ? 1 : 0));
                if (Closes(other.Dx, other.Dy, other.Angle)) { joined++; continue; }
            }
            gaps.Add(new Gap(null, attach.Pad, attach.Base, dx, dy, angle, Loop(attach.Pad.Part, attach.Base.Part, parent))
            {
                Attachment = attach,
            });
        }

        return new Result(anchor, poses, joined, gaps);

        static double Sq(double v) => v * v;
    }

    private static bool Closes(double dx, double dy, double angle) =>
        Math.Abs(dx) <= CloseDbu && Math.Abs(dy) <= CloseDbu && Math.Abs(angle) <= CloseDeg;

    private static (double Dx, double Dy, double AngleDeg) Miss(
        IReadOnlyDictionary<JointGraph.Part, ChainPose> poses, JointGraph.End from, JointGraph.End to)
    {
        var (fx, fy, fd) = poses[from.Part].Apply(from.Pin);
        var (tx, ty, td) = poses[to.Part].Apply(to.Pin);
        double angle = LayoutAngle.Normalize(fd - td - 180.0);
        if (angle > 180.0) angle -= 360.0;
        return (fx - tx, fy - ty, angle);
    }

    /// <summary>D6: the bridging part of a closing joint, with the line ends its two pads join (the tree edge's first,
    /// this joint's second) — a new part with pads, exactly two joints, placed from a line end and placing nothing.</summary>
    private static (JointGraph.Part Part, JointGraph.End First, JointGraph.End Second)? BridgeOf(
        JointGraph.Joint joint, JointGraph.End from, JointGraph.End to,
        Dictionary<JointGraph.Part, JointGraph.Part?> parent, HashSet<JointGraph.Part> treeChild,
        Dictionary<JointGraph.Part, List<JointGraph.Joint>> edgesOf, JointGraph.Part anchor, JointGraph.Chain chain)
    {
        foreach (var (mine, other) in new[] { (from, to), (to, from) })
        {
            var part = mine.Part;
            if (part.Kind != JointGraph.PartKind.Pad || part.Existing || ReferenceEquals(part, anchor)) continue;
            if (treeChild.Contains(part) || edgesOf[part].Count != 2 || other.Part.Kind == JointGraph.PartKind.Pad) continue;
            if (parent.GetValueOrDefault(part) is not { } up || up.Kind == JointGraph.PartKind.Pad) continue;
            if (chain.Attachments.Any(a => ReferenceEquals(a.Pad.Part, part) || ReferenceEquals(a.Across.Part, part))) continue;
            var tree = edgesOf[part].Single(j => !ReferenceEquals(j, joint));
            if (!ReferenceEquals(tree.Other(part), up)) continue;
            return (part, tree.EndOf(up), other);
        }
        return null;
    }

    /// <summary>The child's pose that puts <paramref name="childPin"/> on <paramref name="parentPin"/>, facing it.</summary>
    public static ChainPose Child(ChainPose parentPose, LayoutPin parentPin, LayoutPin childPin, bool childMirror)
    {
        var (px, py, pDeg) = parentPose.Apply(parentPin);
        return ChildAt(px, py, pDeg, childPin, childMirror);
    }

    /// <summary>The child's pose that puts <paramref name="childPin"/> on the point (<paramref name="x"/>,
    /// <paramref name="y"/>), facing opposite <paramref name="partnerFacingDeg"/> — the facing of what it meets there.</summary>
    public static ChainPose ChildAt(double x, double y, double partnerFacingDeg, LayoutPin childPin, bool childMirror)
    {
        double localOut = childMirror ? 180.0 - childPin.OutwardDeg : childPin.OutwardDeg;
        double deg = LayoutAngle.Normalize(partnerFacingDeg + 180.0 - localOut);
        var (rx, ry) = ChainPose.Rotate(childMirror ? -childPin.X : childPin.X, childPin.Y, deg);
        return new ChainPose(x - rx, y - ry, deg, childMirror);
    }

    /// <summary>The cycle a closing joint between <paramref name="a"/> and <paramref name="b"/> closes, walked from its
    /// first part in schematic-id order toward its lower neighbour; empty when the two hang from different roots.</summary>
    private static List<JointGraph.Part> Loop(JointGraph.Part a, JointGraph.Part b,
                                              Dictionary<JointGraph.Part, JointGraph.Part?> parent)
    {
        List<JointGraph.Part> PathUp(JointGraph.Part p)
        {
            var path = new List<JointGraph.Part>();
            for (JointGraph.Part? q = p; q is not null; q = parent.GetValueOrDefault(q)) path.Add(q);
            return path;
        }
        var up = PathUp(a);
        var other = PathUp(b);
        if (!ReferenceEquals(up[^1], other[^1])) return [];

        var onA = new HashSet<JointGraph.Part>(up, ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>);
        int k = other.FindIndex(onA.Contains);
        var lca = other[k];
        var cycle = up.TakeWhile(p => !ReferenceEquals(p, lca)).ToList();
        cycle.Add(lca);
        cycle.AddRange(other.Take(k).Reverse());
        if (cycle.Count < 2) return cycle;

        int start = cycle.IndexOf(cycle.MinBy(p => p.Order)!);
        var rotated = cycle.Skip(start).Concat(cycle.Take(start)).ToList();
        return rotated.Count > 2 && rotated[^1].Order < rotated[1].Order
            ? [rotated[0], .. Enumerable.Reverse(rotated.Skip(1))]
            : rotated;
    }
}
