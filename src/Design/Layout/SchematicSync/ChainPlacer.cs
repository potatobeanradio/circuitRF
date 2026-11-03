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
    /// joint closes no loop of this run's making.</summary>
    public sealed record Gap(JointGraph.Joint Joint, JointGraph.End From, JointGraph.End To,
                             double Dx, double Dy, double AngleDeg, IReadOnlyList<JointGraph.Part> Loop);

    public sealed record Result(
        JointGraph.Part Anchor,
        IReadOnlyDictionary<JointGraph.Part, ChainPose> Poses,
        int JoinedCount,
        IReadOnlyList<Gap> Gaps);

    /// <summary>
    /// The anchor (R-sdl1-2 step 1), first match wins: an existing part (the first in schematic-id order); the part joined to the
    /// lowest-numbered schematic Port (<paramref name="portNumberOf"/> names the port on a part's terminal's net,
    /// null when there is none); the first part in schematic-id order.
    /// </summary>
    public static JointGraph.Part ChooseAnchor(JointGraph.Chain chain, Func<JointGraph.Part, int?> portNumberOf)
    {
        if (chain.Parts.FirstOrDefault(p => p.Existing) is { } existing) return existing;
        JointGraph.Part? best = null;
        int bestNumber = int.MaxValue;
        foreach (var part in chain.Parts)
            if (portNumberOf(part) is { } n && n < bestNumber) { best = part; bestNumber = n; }
        return best ?? chain.Parts[0];
    }

    /// <summary>
    /// Every part of <paramref name="chain"/> placed from <paramref name="anchor"/> at <paramref name="anchorPose"/>.
    /// <paramref name="mirrorOf"/> is a NEW part's mirror from the schematic (D2); an existing part's pose is its
    /// instance's. The poses returned are for every part, existing ones included, so the caller can measure the chain.
    /// </summary>
    public static Result Place(JointGraph.Chain chain, JointGraph.Part anchor, ChainPose anchorPose,
                               Func<JointGraph.Part, bool> mirrorOf)
    {
        var cmp = ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Part>;
        var poses = new Dictionary<JointGraph.Part, ChainPose>(cmp);
        var parent = new Dictionary<JointGraph.Part, JointGraph.Part?>(cmp);
        foreach (var p in chain.Parts)
            if (p.Existing && !ReferenceEquals(p, anchor)) { poses[p] = ChainPose.Of(p.Instance); parent[p] = null; }
        poses[anchor] = anchorPose;
        parent[anchor] = null;

        var edgesOf = chain.Parts.ToDictionary(p => p, p => chain.Joints
            .Where(j => ReferenceEquals(j.A.Part, p) || ReferenceEquals(j.B.Part, p))
            .OrderBy(j => j.Other(p).Order).ThenBy(j => j.EndOf(p).Terminal).ToList(), cmp);

        var used = new HashSet<JointGraph.Joint>(ReferenceEqualityComparer.Instance as IEqualityComparer<JointGraph.Joint>);
        var closing = new List<JointGraph.Joint>();
        var queued = new HashSet<JointGraph.Part>(cmp) { anchor };
        var queue = new Queue<JointGraph.Part>([anchor]);
        int joined = 0;

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
                joined++;
                if (queued.Add(v)) queue.Enqueue(v);
            }
        }

        var gaps = new List<Gap>();
        foreach (var joint in closing)
        {
            // Measured from the end later in schematic-id order to the earlier one, whichever way the walk met them.
            var (from, to) = (joint.B, joint.A);
            var (fx, fy, fd) = poses[from.Part].Apply(from.Pin);
            var (tx, ty, td) = poses[to.Part].Apply(to.Pin);
            double dx = fx - tx, dy = fy - ty;
            double angle = LayoutAngle.Normalize(fd - td - 180.0);
            if (angle > 180.0) angle -= 360.0;
            if (Math.Abs(dx) <= CloseDbu && Math.Abs(dy) <= CloseDbu && Math.Abs(angle) <= CloseDeg) { joined++; continue; }
            gaps.Add(new Gap(joint, from, to, dx, dy, angle, Loop(from.Part, to.Part, parent)));
        }

        return new Result(anchor, poses, joined, gaps);
    }

    /// <summary>The child's pose that puts <paramref name="childPin"/> on <paramref name="parentPin"/>, facing it.</summary>
    public static ChainPose Child(ChainPose parentPose, LayoutPin parentPin, LayoutPin childPin, bool childMirror)
    {
        var (px, py, pDeg) = parentPose.Apply(parentPin);
        double localOut = childMirror ? 180.0 - childPin.OutwardDeg : childPin.OutwardDeg;
        double deg = LayoutAngle.Normalize(pDeg + 180.0 - localOut);
        var (rx, ry) = ChainPose.Rotate(childMirror ? -childPin.X : childPin.X, childPin.Y, deg);
        return new ChainPose(px - rx, py - ry, deg, childMirror);
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
