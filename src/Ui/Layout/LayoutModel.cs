// Framework-free layout geometry model. No SKPath / Avalonia types.
// Coordinates are integer DBU (docs/design/layout-view.md §1.1 R1).
// No Schematic types are referenced here (SymbolRotation etc.) — layout borrows
// patterns from Schematic, not types.
//
// The promotion rule (docs/design/layout-view.md §3.2, brief-L1b-drawing-tools §4 — decided now,
// implemented in L1c): PolygonShape and CurveShape differ only in carrying an edge list. There is
// deliberately no "Curve" drawing tool in L1b — the interaction that actually creates a curved edge
// (drag a segment's midpoint to set its bulge) is the same interaction as L1c's bulge handle, so it
// is built once there and reused at draw time rather than implemented twice in ways that drift.
// A PolygonShape whose edge is converted to an Arc or Cubic (via that L1c bulge-handle / "convert
// edge" gesture) is REPLACED by an equivalent CurveShape carrying the same Xy plus the new edge
// list — Polygon is the "all edges are Line" special case of Curve, not a separate lineage.
// PathShape already carries an edge list from L0a, so it simply gains the curved edge in place;
// no promotion is needed there. This is a rule that gets decided twice, differently, if left
// implicit — do not add a Curve tool or an ad hoc Polygon->Curve conversion without reading this.

using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout;

public readonly record struct LayerKey(int Layer, int Datatype);

public enum LayoutRotation { R0, R90, R180, R270 }
public enum PathEndStyle   { Flush, Round, Square, Extended }
public enum AngleMode      { Manhattan, Deg45, AnyAngle }
public enum EdgeKind       { Line, Arc, Cubic }

/// <summary>
/// One edge of an edge list, parallel to the owning shape's vertex list: <c>Edges[i]</c>
/// describes the edge leaving vertex <c>i</c>. A null <c>Edges</c> list on the owning shape
/// means every edge is a straight line.
/// </summary>
public sealed class LayoutEdge
{
    public EdgeKind Kind  { get; set; } = EdgeKind.Line;

    /// <summary>Arc only: tan(sweep/4), signed. 0 = straight (unused when Kind != Arc).</summary>
    public double Bulge { get; set; }

    /// <summary>Cubic only: first control point.</summary>
    public long C1X { get; set; }
    public long C1Y { get; set; }

    /// <summary>Cubic only: second control point.</summary>
    public long C2X { get; set; }
    public long C2Y { get; set; }
}

// ── Shape base ──────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RectShape),        "Rect")]
[JsonDerivedType(typeof(PolygonShape),     "Poly")]
[JsonDerivedType(typeof(RoundedRectShape), "RRect")]
[JsonDerivedType(typeof(CircleShape),      "Circle")]
[JsonDerivedType(typeof(CurveShape),       "Curve")]
[JsonDerivedType(typeof(PathShape),        "Path")]
[JsonDerivedType(typeof(ViaShape),         "Via")]
[JsonDerivedType(typeof(LabelShape),       "Label")]
public abstract class LayoutShape
{
    public LayerKey Layer { get; set; }

    /// <summary>Nullable net name (docs/design/layout-view.md §3.4 R10a). Unpopulated until L5.</summary>
    public string? Net { get; set; }
}

/// <summary>Axis-aligned rectangle. Normalized so X1&lt;X2, Y1&lt;Y2.</summary>
public sealed class RectShape : LayoutShape
{
    public long X1 { get; set; }
    public long Y1 { get; set; }
    public long X2 { get; set; }
    public long Y2 { get; set; }
}

/// <summary>Flat vertex list, implicitly closed.</summary>
public sealed class PolygonShape : LayoutShape
{
    public long[] Xy { get; set; } = [];

    /// <summary>Inner rings (docs/design/layout-view.md §3.1a). Each is a flat, implicitly-closed
    /// vertex list, same convention as <see cref="Xy"/>. Null/absent means no holes — every existing
    /// hole-free <c>.clay</c> loads unchanged, no <c>FormatVersion</c> bump. R10b: a hole must lie
    /// inside <see cref="Xy"/> and must not intersect it or another hole — Clipper2's <c>PolyTree64</c>
    /// output satisfies this by construction; any other construction path is normalized through
    /// <c>LayoutClipper.EnsureValidHoles</c> rather than trusted (§3.1a R10b, R-L1e-0).</summary>
    public List<long[]>? Holes { get; set; }
}

/// <summary>Axis-aligned rounded rectangle. Normalized so X1&lt;X2, Y1&lt;Y2.</summary>
public sealed class RoundedRectShape : LayoutShape
{
    public long X1 { get; set; }
    public long Y1 { get; set; }
    public long X2 { get; set; }
    public long Y2 { get; set; }
    public long CornerRadius { get; set; }
}

public sealed class CircleShape : LayoutShape
{
    public long Cx { get; set; }
    public long Cy { get; set; }
    public long R  { get; set; }
}

/// <summary>Closed edge-list region — a filled boundary whose edges may be lines, arcs, or cubics.</summary>
public sealed class CurveShape : LayoutShape
{
    public long[] Xy { get; set; } = [];

    /// <summary>Parallel to <see cref="Xy"/>. Null means every edge is a straight line.</summary>
    public List<LayoutEdge>? Edges { get; set; }

    /// <summary>Null inherits the technology default (docs/design/layout-view.md §3.2 R9b).</summary>
    public long? FlattenTolDbu { get; set; }

    /// <summary>Inner rings — see <see cref="PolygonShape.Holes"/> for the full contract. A
    /// <c>Curve</c>'s holes are always plain flat vertex lists (never their own edge list): a hole cut
    /// by a boolean is Clipper2 output, which is polygonal by construction (§6.1).</summary>
    public List<long[]>? Holes { get; set; }
}

/// <summary>Open edge-list centerline with width — a parametric trace.</summary>
public sealed class PathShape : LayoutShape
{
    public long[] Xy { get; set; } = [];

    /// <summary>Parallel to <see cref="Xy"/>. Null means every edge is a straight line.</summary>
    public List<LayoutEdge>? Edges { get; set; }

    public long Width { get; set; }
    public PathEndStyle End { get; set; } = PathEndStyle.Flush;

    /// <summary>Null inherits the technology default (docs/design/layout-view.md §3.2 R9b).</summary>
    public long? FlattenTolDbu { get; set; }
}

public sealed class ViaShape : LayoutShape
{
    public long X { get; set; }
    public long Y { get; set; }
    public long PadSize   { get; set; }
    public long DrillSize { get; set; }
    public LayerKey? LandingLayer { get; set; }
}

public sealed class LabelShape : LayoutShape
{
    public long X { get; set; }
    public long Y { get; set; }
    public string Text { get; set; } = "";
    public long Height { get; set; }
    public LayoutRotation Rotation { get; set; } = LayoutRotation.R0;
    public bool IsPort { get; set; }
}

// ── Instance ────────────────────────────────────────────────────────────────

/// <summary>A cell-reference placement, optionally an array (rows/cols/pitch = GDSII AREF).</summary>
public sealed class LayoutInstance
{
    /// <summary>Relative path to the referenced cell folder.</summary>
    public string CellRef { get; set; } = "";

    public long X { get; set; }
    public long Y { get; set; }
    public LayoutRotation Rot { get; set; }
    public bool MirrorX { get; set; }
    public double Mag { get; set; } = 1.0;

    /// <summary>1×1 = a plain instance.</summary>
    public int Rows { get; set; } = 1;
    public int Cols { get; set; } = 1;
    public long PitchX { get; set; }
    public long PitchY { get; set; }

    /// <summary>docs/design/layout-view.md §9 R16 re-run idempotency. Unused until L5.</summary>
    public string? SchematicId { get; set; }
}

// ── Container ───────────────────────────────────────────────────────────────

public sealed class LayoutView
{
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;
    public long SnapDbu { get; set; }
    public AngleMode AngleMode { get; set; } = AngleMode.AnyAngle;

    /// <summary>Relative path to a .ctech.</summary>
    public string? TechRef { get; set; }

    public List<LayoutShape> Shapes { get; } = [];
    public List<LayoutInstance> Instances { get; } = [];

    /// <summary>Raised after any mutation of <see cref="Shapes"/>/<see cref="Instances"/> —
    /// <c>LayoutCanvas</c> subscribes to repaint (mirrors <c>EditableSymbol.Changed</c>). Commands
    /// under <c>src/Ui/Commands/Layout/</c> call <see cref="NotifyChanged"/> after every mutation,
    /// in both Execute and Undo.</summary>
    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
