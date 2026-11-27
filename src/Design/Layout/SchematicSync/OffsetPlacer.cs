// Owner: PCD-2 (brief-pcd-0 §5, seam 9; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>A pin as placed: position in DBU and its outward direction in degrees.</summary>
public readonly record struct PinPose(long X, long Y, double OutwardDeg);

/// <summary>brief-pcd-0 §4.2: the geometry of an offset placement.</summary>
public static class OffsetPlacer
{
    /// <summary>Where the offset part's pin goes and which way it faces, given its anchor pin as placed. Null with refusal
    /// when an expression does not evaluate.</summary>
    public static PinPose? Pose(OffsetPlacement offset, PinPose anchor, Scope scope, Evaluator evaluator,
                                int dbuPerMicron, out string? refusal)
        => throw new NotImplementedException("PCD-2");

    /// <summary>The offset after dragging the part's pin from now to (toX, toY): Dx and Dy keep their parameter terms and
    /// gain the change as a literal in offset.Unit (S1: an expression, never a coordinate).</summary>
    public static OffsetPlacement Drag(OffsetPlacement offset, PinPose anchor, PinPose now, long toX, long toY,
                                       int dbuPerMicron)
        => throw new NotImplementedException("PCD-2");
}
