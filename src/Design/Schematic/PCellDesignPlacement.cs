// Owner: PCD-1 (brief-pcd-0 §5, seam 2). Framework-free.
//
// brief-pcd-0 §4.2 — the two .csch component fields a designed cell adds: where a part not joined by a net goes, and
// which MSHAPE vertices a parameter moves. Records: shared between clones, replaced and never mutated in place.

namespace CircuitRF.Design.Schematic;

/// <summary>brief-pcd-0 §4.2: where a part not joined by a net goes, relative to another part's pin.</summary>
public sealed record OffsetPlacement
{
    /// <summary><c>"&lt;InstanceName&gt;.&lt;pin&gt;"</c>, the pin a 1-based port number: <c>"TL1.2"</c>.</summary>
    public string Anchor { get; init; } = "";
    /// <summary>This part's pin that is placed at the offset point.</summary>
    public int    Pin    { get; init; } = 1;
    /// <summary>Along the anchor pin's outward direction.</summary>
    public string Dx     { get; init; } = "0";
    /// <summary>90° counter-clockwise from it.</summary>
    public string Dy     { get; init; } = "0";
    /// <summary>The unit Dx and Dy are in, as <c>EditableParameter.Unit</c>; "" = metres.</summary>
    public string Unit   { get; init; } = "";
    /// <summary>Degrees; 0 = this pin faces the anchor pin, as at a joint.</summary>
    public string Angle  { get; init; } = "0";
}

/// <summary>
/// brief-pcd-0 §4.3: an MSHAPE parameter that moves some of its vertices. The expressions are already written into
/// X[i]/Y[i]; this records what made them, for the drag handle.
/// </summary>
public sealed record StretchBinding
{
    /// <summary>A parameter of the designed cell.</summary>
    public string Parameter { get; init; } = "";
    /// <summary>The vertex (X[i]/Y[i]) that stays put.</summary>
    public int    Anchor    { get; init; } = 1;
    /// <summary>The vertices that travel.</summary>
    public int[]  Moving    { get; init; } = [];
    /// <summary>Which way they travel as Parameter grows, in the part's own frame.</summary>
    public double AxisDeg   { get; init; }
}
