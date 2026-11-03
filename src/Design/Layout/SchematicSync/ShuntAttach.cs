namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-2 R-sdl2-3: a footprint pad attached side-on to a joint (D4, D5). The part is turned so that pad faces
/// the line, and placed so the pad's outer edge (<see cref="PadJoint"/>) touches the line's edge on the joint's centre
/// line — the line through the joint point, square to the direction of travel. The first pad at a joint goes on the
/// LEFT of the direction of travel, the second on the right; the graph has already set any third aside.
///
/// <para><b>The direction of travel</b> runs away from the chain's anchor — the part joined to the lowest-numbered
/// port — so "left" reads the way the schematic is read, from port 1. At a joint of two lines (D4) it is the facing of
/// the line end nearer the anchor; at a line's end that joins a pad (D5) it is the line's own facing when the line is
/// the nearer, and the reverse when the pad's part is.</para>
///
/// <para>It never moves the chain: it reads the poses of the parts already placed and returns a pose for the pad's
/// part alone.</para>
/// </summary>
public static class ShuntAttach
{
    /// <summary>Where <paramref name="attachment"/>'s pad joint must sit and which way it must face, from the poses
    /// of the joint's two ends. <paramref name="depth"/> is each placed part's distance from the anchor.</summary>
    public static (double X, double Y, double FacingDeg) Target(
        JointGraph.Attachment attachment,
        IReadOnlyDictionary<JointGraph.Part, ChainPose> poses,
        Func<JointGraph.Part, int> depth)
    {
        var (bx, by, bf) = poses[attachment.Base.Part].Apply(attachment.Base.Pin);
        bool baseFirst = depth(attachment.Base.Part) <= depth(attachment.Across.Part);
        double px = bx, py = by, travel, half;
        if (attachment.Across.Part.Kind == JointGraph.PartKind.Line)
        {
            // D4: two lines meet here; the pad goes against whichever is the wider, so it never sits on copper.
            var (cx, cy, cf) = poses[attachment.Across.Part].Apply(attachment.Across.Pin);
            if (!baseFirst) (px, py) = (cx, cy);
            travel = baseFirst ? bf : cf;
            half = Math.Max(attachment.Base.Pin.WidthDbu, attachment.Across.Pin.WidthDbu) / 2.0;
        }
        else
        {
            // D5: the line ends at a pad; the joint's centre line is the line's end edge.
            travel = baseFirst ? bf : bf + 180.0;
            half = attachment.Base.Pin.WidthDbu / 2.0;
        }

        double normal = travel + (attachment.Side == JointGraph.Side.Left ? 90.0 : -90.0);
        var (nx, ny) = LayoutAngle.CosSin(normal);
        return (px + nx * half, py + ny * half, LayoutAngle.Normalize(normal + 180.0));
    }

    /// <summary>The pose of the attached pad's part: its pad joint on <see cref="Target"/>, facing it, with the
    /// schematic's mirror (SDL-1 D2).</summary>
    public static ChainPose Place(
        JointGraph.Attachment attachment,
        IReadOnlyDictionary<JointGraph.Part, ChainPose> poses,
        Func<JointGraph.Part, int> depth,
        bool mirror)
    {
        var (tx, ty, facing) = Target(attachment, poses, depth);
        // The line's edge "faces" the pad: the opposite of the way the pad must face.
        return ChainPlacer.ChildAt(tx, ty, LayoutAngle.Normalize(facing + 180.0), attachment.Pad.Pin, mirror);
    }
}
