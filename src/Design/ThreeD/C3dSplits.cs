// brief-hzn-21 R-hzn21-3 — making a Split: the one function the editor's Split at Plane and anything headless call.

using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.ThreeD;

public static class C3dSplits
{
    /// <summary>
    /// <paramref name="target"/> wrapped in a Split at <paramref name="plane"/>, at <paramref name="at"/> DBU along its normal, keeping
    /// <paramref name="keep"/>. The Split takes the target's name and drawing state, as every operation does; the target inside carries none.
    /// </summary>
    public static C3dSplit Make(C3dObject target, C3dSplitPlane plane, long at, C3dSplitKeep keep)
    {
        var t = C3dBooleans.Copy(target);
        string name = t.Name;
        t.Name = "";
        t.Hidden = false;
        t.Group = null;
        t.Transparency = null;
        t.Appearance = null;
        t.Model = true;
        return new C3dSplit
        {
            Name = name, Hidden = target.Hidden, Group = target.Group, Transparency = target.Transparency, Appearance = target.Appearance,
            Model = target.Model, Plane = plane, At = at, Keep = keep, Target = t,
        };
    }

    /// <summary>
    /// Moves <paramref name="split"/> to <paramref name="plane"/> keeping the cut where it was where the two can say the same thing: a named
    /// plane becomes a Custom one through its point on the axis with that axis as normal; a Custom one becomes a named plane at its Base's
    /// coordinate along the new axis.
    /// </summary>
    public static void SetPlane(C3dSplit split, C3dSplitPlane plane)
    {
        if (split.Plane == plane) return;
        if (plane == C3dSplitPlane.Custom)
        {
            int a = split.Axis;
            long[] b = [0, 0, 0];
            b[a] = split.At;
            split.Base = new C3dPoint3(b[0], b[1], b[2]);
            if (C3dBindings.GetExpr(split, nameof(C3dSplit.At), 0) is { } atExpr)
                C3dBindings.SetExpr(split, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.Base))!, a, atExpr);
            split.Normal = [a == 0 ? 1 : 0, a == 1 ? 1 : 0, a == 2 ? 1 : 0];
            C3dBindings.SetExpr(split, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.At))!, 0, null);
            split.At = 0;
        }
        else
        {
            int a = plane switch { C3dSplitPlane.YZ => 0, C3dSplitPlane.XZ => 1, _ => 2 };
            if (split.Plane == C3dSplitPlane.Custom)
            {
                split.At = a switch { 0 => split.Base.X, 1 => split.Base.Y, _ => split.Base.Z };
                if (C3dBindings.GetExpr(split, nameof(C3dSplit.Base), a) is { } baseExpr)
                    C3dBindings.SetExpr(split, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.At))!, 0, baseExpr);
                for (int k = 0; k < 3; k++) C3dBindings.SetExpr(split, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.Base))!, k, null);
                split.Base = default;
                split.Normal = null;
            }
        }
        split.Plane = plane;
    }

    /// <summary>Why <paramref name="o"/> cannot be split, or null when it can: a solid whose extent circuitRF knows.</summary>
    public static string? Refusal(C3dObject o, int dbuPerMicron)
    {
        if (!C3dOperands.IsSolid(o)) return $"'{o.Name}' is not a solid: a Split cuts a solid.";
        if (C3dOperands.SelfAndDescendants(o).Any(x => x is C3dStep))
            return $"'{o.Name}' is or holds a Step part, which cannot be split yet: a Split is sized from its target's own extent, and a " +
                   "Step part's is known only to the geometry worker.";
        return GeometryKernelTree.BoundsUm(o, dbuPerMicron) is null ? $"'{o.Name}' has an extent circuitRF cannot bound, so it cannot be split." : null;
    }

    /// <summary>The coordinate (DBU) of <paramref name="o"/>'s centre along <paramref name="plane"/>'s normal: where a new Split cuts it in
    /// half. Null when its extent is unknown.</summary>
    public static long? Centre(C3dObject o, C3dSplitPlane plane, int dbuPerMicron)
    {
        if (GeometryKernelTree.BoundsUm(o, dbuPerMicron) is not { } b) return null;
        double um = plane switch { C3dSplitPlane.YZ => (b.Lo.X + b.Hi.X) / 2, C3dSplitPlane.XZ => (b.Lo.Y + b.Hi.Y) / 2, _ => (b.Lo.Z + b.Hi.Z) / 2 };
        return (long)Math.Round(um * dbuPerMicron, MidpointRounding.AwayFromZero);
    }
}
