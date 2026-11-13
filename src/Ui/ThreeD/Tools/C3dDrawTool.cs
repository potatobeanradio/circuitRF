// brief-em3d-45 R-em3d45-3 / -4 — what every drawing tool is: a small state machine fed clicks and typed values,
// which shows a rubber band while it runs and hands back ONE object when it finishes.
//
// A TOOL NEVER TOUCHES THE DOCUMENT. It is given points (C3dDrawInput, resolved by the editor from the snap and the
// cursor's ray) and returns what it made; the editor inserts that as one undo entry. So a cancelled gesture adds no
// entry (gate 8), and the preview is the overlay's, never the scene's.
//
// EVERY TYPED DIMENSION LANDS IN EXACTLY ONE STORED FIELD (R-em3d45-4a): a box's Size component, a rectangle's Size,
// a cylinder's Radius or Length, a prism's Height — the fields brief 51 will bind an expression to. The typed
// field's parser is one function answering "an exact DBU value" or "this is an expression" (C3dDimension.Parse).

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

/// <summary>The tools (owner decision D6 adds the cylinder), and Extrude — a gesture on an existing object (§5) — and
/// brief 46's Move and Rotate, which are gestures on the selection (Duplicate is a Move that keeps the original).</summary>
/// <para>brief-em3d-47 — the face and vertex gestures: Move Along Normal, a face's Move, a vertex's Move, Extrude to New
/// Solid and Align to Face.</para>
/// <para>brief-em3d-48 — Place: an instance of a cell, following the cursor until the click.</para>
/// <para>brief-em3d-50 — Wire: a bond wire from pad to pad, then its loop height.</para>
/// <para>brief-em3d-102 — Sphere, appended so no stored or compared value moves.</para>
/// <para>brief-sar-3 — Ellipsoid and LumpedElement, appended for the same reason.</para>
/// <para>brief-dfr34-3 — SectionThrough, Section Through…'s two-point pick, appended for the same reason.</para>
/// <para>brief-em3d-75 — the thermal places (a heat source, as a rectangle or a polygon; a point, spot or line probe; a mesh
/// region) and Temperature Along's two-point pick.</para>
public enum C3dToolKind { Box, Sheet, Polygon, Polyline, Cylinder, Extrude, Move, Rotate, PushPull, FaceMove, VertexMove, ExtrudeFace, AlignFace, Place, Port, Wire,
                          HeatSource, HeatSourcePolygon, ProbePoint, ProbeSpot, ProbeLine, MeshRegion, TemperatureAlong,
                          EffectiveBlock, Sphere, Ellipsoid, LumpedElement, SectionThrough, CoaxialPort }

/// <summary>
/// Where the cursor is, as a tool reads it: the snap in force (a DBU point, and whether it is exact and on geometry
/// rather than the grid), and the ray through the cursor, world metres. Either may be absent.
/// </summary>
/// <para>brief-em3d-46 — <paramref name="Free"/> is Shift held: a rotation turns freely rather than by 15° steps.</para>
/// <para>brief-em3d-48 — <paramref name="Command"/> is Ctrl/Cmd held: a placement's handle is the child's bottom-centre.</para>
public readonly record struct C3dDrawInput(C3dPoint3? Snap, bool SnapExact, bool SnapOnGeometry, Point3? RayOrigin, Point3? RayDirection,
                                           bool Free = false, bool Command = false)
{
    public bool HasRay => RayOrigin is not null && RayDirection is not null;
}

/// <summary>What a click or a typed step did: advanced (or finished with <see cref="Result"/>), or was refused.</summary>
/// <para>brief-em3d-46 — an operation finishes with <paramref name="Finished"/> and no object: the editor commits it.</para>
public readonly record struct C3dToolStep(bool Advanced, C3dObject? Result = null, string? Refusal = null,
                                          (int EdgeA, int EdgeB)? Crossing = null, bool Finished = false)
{
    public static C3dToolStep Next => new(true);
    public static C3dToolStep Refuse(string why) => new(false, Refusal: why);
    public static C3dToolStep Done(C3dObject o) => new(true, o);
    public static C3dToolStep Finish => new(true, Finished: true);
}

/// <summary>The services a tool needs from the editor: the plane, the points, and the new object's name and material.</summary>
public interface IC3dDrawHost
{
    DrawingPlane Plane { get; }
    int DbuPerMicron { get; }

    /// <summary>The cursor's point ON the plane: a snap moved along the normal onto it, or the ray's hit. Null, with
    /// the reason in <paramref name="refusal"/>, when the plane is seen edge-on or the ray misses it.</summary>
    C3dPoint3? PlanePoint(in C3dDrawInput input, out string? refusal);

    /// <summary>The cursor's point anywhere in 3D: a geometry snap as it is, or else the plane point.</summary>
    C3dPoint3? FreePoint(in C3dDrawInput input, out string? refusal);

    /// <summary>R-em3d45-3a step 3 — how far along <paramref name="direction"/> (a world axis, ±) from
    /// <paramref name="through"/> the cursor says: a geometry snap projected onto that line, or the point of the line
    /// closest to the cursor's ray, on the grid. Null when neither says anything (the ray runs along the line).</summary>
    long? Along(C3dPoint3 through, C3dAxis axis, in C3dDrawInput input);

    /// <summary>The cursor's point at <paramref name="through"/>'s depth: a geometry snap as it is, or else the point of the
    /// cursor's ray nearest <paramref name="through"/>, its step from there on the grid. Null when the ray says nothing.</summary>
    C3dPoint3? DepthPoint(C3dPoint3 through, in C3dDrawInput input) => null;

    string NextName(string prefix);
    string? CurrentMaterial { get; }

    /// <summary>A conductor sheet's thickness from the technology, µm, or null when it states none.</summary>
    double? ThicknessUmFor(string? material);

    /// <summary>brief-em3d-75 — the cursor's point ON a face (a geometry snap, or the surface under the cursor) and that face
    /// as the document names it (<c>object/face</c>); null, with the reason, off every face.</summary>
    C3dPoint3? SurfacePoint(in C3dDrawInput input, out string? face, out string? refusal);
}

public abstract class C3dDrawTool(IC3dDrawHost host)
{
    protected IC3dDrawHost Host { get; } = host;

    public abstract C3dToolKind Kind { get; }

    /// <summary>The tool's name as the menu and the status line say it.</summary>
    public virtual string Name => Kind.ToString();

    /// <summary>True for a tool that takes the drawing plane at its first click (Box, Sheet, Cylinder, Sphere, Ellipsoid,
    /// Polygon, Polyline). The editor moves the plane through a geometry snap there before the click, so the object starts
    /// at the snap glyph and not at its shadow on the plane.</summary>
    public virtual bool TakesPlaneAtFirstClick => false;

    /// <summary>The gesture's first point while everything placed so far lies on the plane taken at the first click, so a plane
    /// chosen now can still be taken (<see cref="RetakePlane"/>); null when the gesture has none or has left the plane.</summary>
    public virtual C3dPoint3? PlaneAnchor => null;

    /// <summary>Takes <paramref name="plane"/> for the rest of the gesture, the first point moved onto it along its normal.
    /// Called only while <see cref="PlaneAnchor"/> is not null.</summary>
    public virtual void RetakePlane(DrawingPlane plane) { }

    /// <summary>0 before the first click; the gesture is in progress from then until it finishes or is cancelled.</summary>
    public int Step { get; protected set; }

    public bool InProgress => Step > 0;

    /// <summary>The status line's prompt for the next click.</summary>
    public abstract string Prompt { get; }

    /// <summary>The dimensions the next click would fix, in Tab order — empty when the step has none to type.</summary>
    public abstract IReadOnlyList<string> Dimensions { get; }

    /// <summary>Those dimensions' values at the cursor now (DBU), for the field's prefill; null where the cursor says nothing.</summary>
    public abstract long?[] Current(in C3dDrawInput input);

    /// <summary>A click at the cursor.</summary>
    public abstract C3dToolStep Click(in C3dDrawInput input);

    /// <summary>The typed field's Enter: the step as if clicked, each typed dimension exact, the rest the cursor's.</summary>
    public abstract C3dToolStep Typed(long?[] values, in C3dDrawInput input);

    /// <summary>Enter with no field open: a polygon or polyline finishes; any other tool clicks.</summary>
    public virtual C3dToolStep Enter(in C3dDrawInput input) => Click(input);

    /// <summary>The second click of a double-click: a polygon or polyline finishes; elsewhere it is ignored (the first
    /// click already placed the point).</summary>
    public virtual C3dToolStep DoubleClick(in C3dDrawInput input) => new(false);

    /// <summary>Backspace: a polygon or polyline takes back its last vertex. True when it did something.</summary>
    public virtual bool Backspace() => false;

    /// <summary>The rubber band for the cursor where it is, and the points already fixed.</summary>
    public abstract void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints);

    /// <summary>Esc: the gesture ends, nothing made.</summary>
    public virtual void Reset() => Step = 0;

    /// <summary>
    /// 3D round 1 — Esc mid-gesture goes back ONE stage, not to the start: a box at its height goes back to the
    /// sheet's second corner, then to its first point; only then does Esc disarm. True when it stepped back (the
    /// gesture may now be at step 0, the tool still armed); false when the gesture has no earlier stage and ends.
    /// A stage's state is simply overwritten when the stage is done again, so stepping back is a decrement here.
    /// </summary>
    public virtual bool StepBack()
    {
        if (Step == 0) return false;
        Step--;
        return true;
    }

    /// <summary>brief-em3d-46 — the typed field's parse of dimension <paramref name="index"/>: a length, unless the tool
    /// types something else (a rotation's angle).</summary>
    public virtual C3dDimension ParseField(int index, string text, LayoutUnit unit, int dbuPerMicron) => C3dDimension.Parse(text, unit, dbuPerMicron);

    /// <summary>The field's prefill of dimension <paramref name="index"/>, parsed back by <see cref="ParseField"/>.</summary>
    public virtual string SpellField(int index, long value, LayoutUnit unit, int dbuPerMicron) => C3dDimension.Spell(value, unit, dbuPerMicron);

    /// <summary>The unit the field's label names for dimension <paramref name="index"/>, or null for the display unit.</summary>
    public virtual string? FieldSuffix(int index) => null;

    /// <summary>
    /// brief-em3d-51 R-em3d51-1b — the stored field that dimension <paramref name="dim"/> of step <paramref name="step"/>
    /// lands in, in the finished object (<c>Size[2]</c>, <c>Rect.Size[0]</c>, <c>Radius</c>) — where a typed expression
    /// is BOUND. Null when it lands in no named field (a point list, a displacement): an expression there is evaluated
    /// once and stored as a number, and the field says so as the user types.
    /// </summary>
    public virtual string? FieldFor(int step, int dim) => null;

    /// <summary>Whether that field stores the typed value's MAGNITUDE (a size): a negative typed expression is then bound
    /// negated, so the stored size stays positive.</summary>
    public virtual bool FieldIsMagnitude(int step, int dim) => false;

    /// <summary>brief-em3d-51 R-em3d51-3b — the rubber band for the values the typed field previews (a null is the
    /// cursor's). By default the cursor's own.</summary>
    public virtual void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => Preview(input, rubber, fixedPoints);

    /// <summary>World axis index (x 0, y 1, z 2) of a plane's u, v and normal.</summary>
    protected static (int U, int V, int N) AxesOf(C3dPlane plane) => plane switch
    {
        C3dPlane.YZ => (1, 2, 0),
        C3dPlane.XZ => (0, 2, 1),
        _ => (0, 1, 2),
    };

    protected Point3 M(C3dPoint3 p) => DrawGeometry.Metres(p, Host.DbuPerMicron);

    protected static long Or(long? typed, long? cursor) => typed ?? cursor ?? 0;
}

/// <summary>What the typed field's text is (R-em3d45-4a).</summary>
public enum C3dDimensionKind { Value, Expression, Invalid }

/// <summary>The parse of one typed dimension.</summary>
public readonly record struct C3dDimension(C3dDimensionKind Kind, long Dbu, string? Why)
{
    /// <summary>
    /// THE one parser of a typed dimension: an exact DBU value — a bare number is the display unit, a suffix
    /// (<c>25um</c>, <c>10mil</c>) is honoured, all in decimal arithmetic (LayoutUnits) — or "this is an
    /// expression" (<c>w</c>, <c>2*w</c>), which the editor evaluates and binds (brief 51); or neither, with the reason.
    /// </summary>
    public static C3dDimension Parse(string? text, LayoutUnit unit, int dbuPerMicron)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return new(C3dDimensionKind.Invalid, 0, "Type a length.");
        if (LayoutUnits.TryParse(t, unit, dbuPerMicron, out long dbu)) return new(C3dDimensionKind.Value, dbu, null);
        // `2m` is two MILLI to the engine (m is the SI prefix everywhere), and in a length a reader very likely meant metres.
        // As a whole typed value it is refused with both spellings; inside an expression the resolver warns instead.
        if (Units.TrySplitGluedNumber(t, out var n, out var suffix) && suffix == "m")
            return new(C3dDimensionKind.Invalid, 0, $"'{t}': a bare 'm' is MILLI, not the metre. Write {n}mm or {n}metre.");
        // Anything the engine reads is an expression — including a sum of unit literals, `10um + 1mil`, which the word
        // test below would otherwise take for a length with an unknown unit.
        try
        {
            Parser.Parse(t);
            return new(C3dDimensionKind.Expression, 0, null);
        }
        catch (Exception ex) when (ex is ExpressionException or FormatException or ArgumentException) { }
        // A number with a word after it is a length with a unit this build does not know — not an expression.
        if (char.IsDigit(t[0]) || t[0] is '.' or ',' || (t.Length > 1 && t[0] is '+' or '-' && char.IsDigit(t[1])))
            if (t.All(c => char.IsLetterOrDigit(c) || c is '.' or ',' or '+' or '-' or ' ' or 'µ' or 'μ'))
                return new(C3dDimensionKind.Invalid, 0, $"'{t}' is not a length: the units are nm, um, mm, mil and in.");
        return new(C3dDimensionKind.Invalid, 0, $"'{t}' is not a length.");
    }

    /// <summary>A length as the field is prefilled with it: the display unit's number, lossless (it parses back to
    /// the same DBU), with no suffix — a bare number is the display unit.</summary>
    public static string Spell(long dbu, LayoutUnit unit, int dbuPerMicron)
        => LayoutUnits.Format(dbu, unit, dbuPerMicron, LayoutUnits.SpellDecimals(unit, dbuPerMicron));
}
