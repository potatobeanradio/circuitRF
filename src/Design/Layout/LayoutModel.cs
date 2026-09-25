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
using CircuitRF.Engine.Mom;   // PlanarPortKind — see LabelShape.PortKind for why the engine enum.

namespace CircuitRF.Design.Layout;

public readonly record struct LayerKey(int Layer, int Datatype);

/// <summary>
/// <b>RP-2b — what an EM port's NEGATIVE terminal is, when it is not the stackup's ground plane.</b>
/// Read only from <see cref="LabelShape.PortReference"/>, and <b>null there means the plane</b> —
/// which is what every port drawn before RP-2b is, and what a port placed on ordinary metal still
/// gets. There is deliberately no <c>GroundPlane</c> member: two spellings of one state is exactly
/// how the two get to disagree.
///
/// <para>The two members are ONE object as far as the kernel is concerned (RP-2a's R-rp2a-12 — two
/// cuts at one station, driven against each other) and differ in what the USER means: a ground strip
/// beside the line, or a second signal conductor. That difference is what the run's own note and a
/// future coplanar calibration read differently, which is why both members exist.</para>
///
/// <para>It is deliberately a LAYOUT enum rather than the engine's <c>PlanarPortReference</c>: this
/// file is the document model, its members are what a <c>.clay</c> spells on disk, and the engine's
/// enum carries a fourth member (<c>ViaBetweenLevels</c>) that names a refusal rather than anything
/// anyone can draw.</para>
/// </summary>
public enum LayoutPortReference { CoplanarGround, SecondConductor }

/// <summary>
/// <b>RP-2b — where the return conductor is cut, in the layout's own DBU frame.</b> The same
/// coordinate system <see cref="LabelShape.X"/>/<see cref="LabelShape.Y"/> are in, because it is the
/// same kind of thing: a point the user clicked on metal.
///
/// <para>A record STRUCT, on <see cref="LayerKey"/>'s terms — immutable, value-compared, and copied
/// by <c>LayoutGeometry</c>'s field-by-field clone with no chance of two labels sharing one mutable
/// point object.</para>
/// </summary>
public readonly record struct LayoutPortReturn(long X, long Y);

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
[JsonDerivedType(typeof(BitmapShape),      "Bitmap")]
public abstract class LayoutShape
{
    public LayerKey Layer { get; set; }

    /// <summary>Nullable net name (docs/design/layout-view.md §3.4 R10a). Unpopulated until L5.</summary>
    public string? Net { get; set; }

    /// <summary>
    /// The component reference this shape belongs to, when the artwork it came from DECLARED one
    /// (brief-L4g-gerber-import-orchestration.md R-L4g-12: Gerber's <c>%TO.C</c>).
    ///
    /// <para><b>Carried, not acted on.</b> circuitRF builds no hierarchy from it — see
    /// <c>GerberImport</c>'s own note at the site where footprint inference would otherwise go — but
    /// discarding data the file went to the trouble of declaring is not a neutral act, and this is the
    /// natural key for a later "group by component" editor action. Additive and nullable, so every
    /// existing <c>.clay</c> re-serializes byte-for-byte with no <c>FormatVersion</c> bump.</para>
    /// </summary>
    public string? Component { get; set; }

    /// <summary>The pad/pin name within <see cref="Component"/>, when declared (<c>%TO.P</c>). Same
    /// rules as <see cref="Component"/>: carried, unread, additive, nullable.</summary>
    public string? Pin { get; set; }
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

    /// <summary>Null inherits the technology default (docs/design/layout-view.md §3.2 R9b — every
    /// curved primitive carries a flatten tolerance; L1h added this field to close the gap where only
    /// Curve/Path had it).</summary>
    public long? FlattenTolDbu { get; set; }
}

public sealed class CircleShape : LayoutShape
{
    public long Cx { get; set; }
    public long Cy { get; set; }
    public long R  { get; set; }

    /// <summary>Null inherits the technology default (docs/design/layout-view.md §3.2 R9b — every
    /// curved primitive carries a flatten tolerance; L1h added this field to close the gap where only
    /// Curve/Path had it).</summary>
    public long? FlattenTolDbu { get; set; }
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

/// <summary>docs/sonnet-briefs/brief-via-primitive-and-stackup.md §1: a via is TWO things at one
/// coordinate — a copper pad (<see cref="PadSize"/>) and a drilled, plated barrel
/// (<see cref="DrillSize"/>, the EM/thermal parameter and the Excellon tool selector, R-via-1). No
/// per-via <c>Plated</c> flag: fill (Plated vs. Solid) is a PROCESS parameter carried on the matching
/// <see cref="StackupKind.Via"/> stackup entry (<see cref="StackupLayer.Fill"/>), not here — a fab
/// plates or fills a whole board to one specification, so a via never needs to override it.
///
/// <b>§4.3/R-via-9 — which field is which layer, pinned explicitly:</b> <see cref="LayoutShape.Layer"/>
/// (inherited) is the via/drill layer — the BARREL; <see cref="LandingLayer"/> is the pad's own copper
/// layer — the PAD. Getting this backwards produces a GDSII/DXF export that looks plausible and puts
/// copper where the hole should be (§4.3's own explicit warning) — read this doc comment before ever
/// touching either field in an interchange writer.
///
/// <b>R-via-7 forward hook (§5): changing PadSize/DrillSize must invalidate L6's mesh, never the
/// technology.</b> Barrel diameter is the swept design parameter (R-via-1) — a user tries 0.3 mm
/// against 0.5 mm and re-simulates — so this is a plain shape-field edit like any other; L6's mesh
/// cache is what must key its invalidation off THIS edit, not the other way around. Nothing here
/// builds L6; this is only the seam it must hook.</summary>
public sealed class ViaShape : LayoutShape
{
    public long X { get; set; }
    public long Y { get; set; }
    public long PadSize   { get; set; }
    public long DrillSize { get; set; }
    public LayerKey? LandingLayer { get; set; }
}

/// <summary>Mirrors the symbol editor's <c>SymbolFontStyle</c> PATTERN, deliberately not the type
/// (this file's header: "Layout borrows patterns from Schematic, not types") — same four options.</summary>
public enum LabelFontStyle { Regular, Bold, Italic, Condensed }

/// <summary>docs/design/layout-view.md §9B.3 — how a ruler's text (and its line weight) is sized.
/// <c>Fixed</c> is n screen POINTS at every zoom (the temporary-measurement mode); <c>Scaled</c> is a
/// physical height in the layout, exactly like <see cref="LabelShape.Height"/> (the annotation mode).
/// Both backing values are stored separately on <see cref="RulerAnnotation"/>, so switching modes and
/// switching back never destroys the other one's setting (R-rul-3).</summary>
public enum RulerSizeMode { Fixed, Scaled }

/// <summary>
/// A two-point measurement drawn IN the layout — docs/design/layout-view.md §9B. The line between
/// <c>(X1,Y1)</c> and <c>(X2,Y2)</c> plus the distance between them, rendered at its midpoint.
///
/// <para><b>D-ruler-1 (§9B.1): a ruler is NOT a <see cref="LayoutShape"/> and never enters
/// <see cref="LayoutView.Shapes"/>.</b> It lives in <see cref="LayoutView.Rulers"/>, its own top-level
/// collection. The tempting alternative — a <c>RulerShape : LayoutShape</c> inheriting selection,
/// marquee, move, undo, clipboard, hit-test and the inspector for free — is exactly the route
/// <see cref="BitmapShape"/> took, and paying for it meant excluding a bitmap BY HAND at every site
/// that walks <c>Shapes</c>: GDSII, DXF, Gerber, PCB, booleans, offset, flatten, repair, DRC, MoM
/// meshing, coordinate walk, rotation promotion, scaling and the snap-feature index. A missed
/// exclusion on a bitmap draws a placeholder box somewhere odd; <b>a missed exclusion on a ruler puts
/// an annotation into a manufacturing file</b>, and a Gerber with a dimension line etched in copper is
/// a scrapped board. A collection nothing walks cannot leak, and that guarantee is structural rather
/// than maintained.</para>
///
/// <para><b>No <c>Layer</c> field, and that is the point (R-rul-1).</b> Not "a Layer we ignore" —
/// absent. A ruler is not on a layer, does not obey layer visibility, never takes a layer colour, and
/// always paints above every layer. A <c>Layer</c> here would have made hiding M1 hide a ruler that
/// happens to measure something else, and would have left an inert field for someone to later wire up
/// wrongly.</para>
/// </summary>
public sealed class RulerAnnotation
{
    public long X1 { get; set; }
    public long Y1 { get; set; }
    public long X2 { get; set; }
    public long Y2 { get; set; }

    public RulerSizeMode SizeMode { get; set; } = RulerSizeMode.Fixed;

    /// <summary>Screen points — meaningful when <see cref="SizeMode"/> is
    /// <see cref="RulerSizeMode.Fixed"/>. R-rul-4: a newly-placed ruler defaults to Fixed, 11 pt.</summary>
    public double TextSizePt { get; set; } = 11.0;

    /// <summary>World height in DBU — meaningful when <see cref="SizeMode"/> is
    /// <see cref="RulerSizeMode.Scaled"/>. Persisted alongside <see cref="TextSizePt"/> so a mode
    /// switch is reversible (§9B.7).</summary>
    public long TextHeightDbu { get; set; }

    /// <summary>R-rul-2: the EXISTING <see cref="LabelFontStyle"/>, never a parallel enum — one
    /// typeface resolver serves both, and a ruler cannot acquire a face a label cannot.</summary>
    public LabelFontStyle Style { get; set; } = LabelFontStyle.Regular;

    /// <summary>Optional free text under the readout. Omitted from the file when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }

    /// <summary>
    /// <b>Where the readout is drawn, in world DBU — <c>null</c> means "wherever the ruler puts it"</b>
    /// (the dynamic midpoint-plus-normal-offset the renderer has always computed). Both coordinates
    /// move together: <see cref="HasTextPosition"/> is the one predicate everything asks, and a file
    /// carrying only one of the pair is treated as unpositioned rather than half-placed.
    ///
    /// <para><b>Absolute, not an offset from the midpoint</b> — the user asked to put the number
    /// somewhere in the LAYOUT, and an offset would silently re-aim it every time an endpoint moved.
    /// A whole-ruler move and a paste translate it with the endpoints (see
    /// <see cref="TranslateBy"/>), so the annotation still travels as one object; an ENDPOINT drag
    /// deliberately leaves it where it was put, because that is the position the user chose.</para>
    ///
    /// <para>Omitted from the file when null, so every <c>.clay</c> written before this field existed
    /// — and every ruler that never has its label moved — re-serializes byte for byte.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TextX { get; set; }

    /// <summary>The Y half of <see cref="TextX"/>. See that field's doc comment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TextY { get; set; }

    /// <summary>True when this ruler's readout has been placed by hand. The ONE predicate the
    /// renderer, the inspector, the context menu and the DXF writer all ask, so none of them can
    /// disagree about whether a half-written pair counts.</summary>
    [JsonIgnore]
    public bool HasTextPosition => TextX is not null && TextY is not null;

    /// <summary>
    /// Which point of the readout BLOCK <see cref="TextX"/>/<see cref="TextY"/> names, horizontally —
    /// <c>null</c> is <see cref="LabelHAlign.Center"/>, which is what a ruler has always drawn.
    ///
    /// <para><b>The existing <see cref="LabelHAlign"/>, never a parallel enum</b> — R-rul-2's rule for
    /// <see cref="LabelFontStyle"/>, applied for the same reason. <b>Nullable rather than defaulted</b>
    /// for <see cref="Decimals"/>'s reason: the enum's own default is <c>Left</c>, so a non-nullable
    /// field defaulting to <c>Center</c> would be written into every file that never touched it.</para>
    ///
    /// <para>It also sets how a multi-line readout JUSTIFIES inside its own block, which is why it is
    /// not inert on a ruler whose label has never been moved.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabelHAlign? TextHAlign { get; set; }

    /// <summary>The vertical half of <see cref="TextHAlign"/> — <c>null</c> is
    /// <see cref="LabelVAlign.Middle"/>. <c>Baseline</c> means the FIRST line's baseline, the only
    /// reading of it that is well defined for a block of several lines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabelVAlign? TextVAlign { get; set; }

    /// <summary>This ruler's effective horizontal anchor — its own override when it has one,
    /// otherwise <see cref="LabelHAlign.Center"/>. The ONE accessor every consumer goes through.</summary>
    [JsonIgnore]
    public LabelHAlign EffectiveTextHAlign => TextHAlign ?? LabelHAlign.Center;

    /// <summary>This ruler's effective vertical anchor — its own override when it has one, otherwise
    /// <see cref="LabelVAlign.Middle"/>.</summary>
    [JsonIgnore]
    public LabelVAlign EffectiveTextVAlign => TextVAlign ?? LabelVAlign.Middle;

    /// <summary>Clears the hand-placed readout position, returning it to the dynamic one the renderer
    /// computes. The anchor is deliberately LEFT ALONE — it is a separate property with its own
    /// control, and resetting a position the user asked to reset must not silently undo a second
    /// choice they did not mention.</summary>
    public void ResetTextPosition() { TextX = null; TextY = null; }

    /// <summary>Moves this ruler — both endpoints AND a hand-placed readout — by one integer delta.
    /// The single place that pairing is written down, so a move, a nudge, a paste and a duplicate
    /// cannot drift apart on whether the label comes along.</summary>
    public void TranslateBy(long dx, long dy)
    {
        X1 += dx; Y1 += dy; X2 += dx; Y2 += dy;
        if (TextX is { } tx) TextX = tx + dx;
        if (TextY is { } ty) TextY = ty + dy;
    }

    /// <summary>R-rul-7: the Delta-x / Delta-y line. Off by default and a per-ruler toggle, never an
    /// automatic angle test — auto-showing it "when angled" would silently change what a ruler SAYS
    /// when an endpoint is nudged one DBU off axis.</summary>
    public bool ShowComponents { get; set; }

    /// <summary>
    /// How many decimal places the readout shows — the distance AND the Delta-x/Delta-y line, since a
    /// ruler that reported those two at different precisions from the number above them would be
    /// stating one measurement three ways.
    ///
    /// <para><b>Null means "this display unit's own default"</b> (<see cref="DefaultDecimalsFor"/>),
    /// which is what keeps §1.3's "changing the display unit is free" true here: a document switched
    /// from mm to mil re-renders at mil's own precision with nothing stored changing. A non-null value
    /// is the user's override and travels with the ruler across a unit change, because at that point
    /// they have said what they want to see.</para>
    ///
    /// <para><b>It is a CEILING, not a fixed width</b> — it is passed straight to
    /// <see cref="LayoutUnits.Format"/>'s existing <c>maxDecimals</c> parameter, which trims trailing
    /// zeros. That is deliberate: R-rul-6 says every length renders through that one formatter, and a
    /// fixed-width variant would be a second one. So a 40 mil ruler at 1 decimal reads "40 mil", not
    /// "40.0 mil".</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Decimals { get; set; }

    /// <summary>
    /// The default decimal places for a display unit — the finest place a user working in that unit
    /// actually dimensions to, rather than a single number that is noise in one unit and coarse in
    /// another.
    ///
    /// <para>The metric three step one decade apart — nm, µm and mm resolve to 1 nm, 10 nm and
    /// 100 nm — so each unit reports a little coarser than the one below it, which is what someone
    /// choosing the bigger unit is asking for. <b>The imperial pair agrees by construction</b>: 1 mil
    /// is 0.001 inch, so mil at 1 decimal and inch at 4 are the same physical step (2.54 µm), and
    /// mil at 1 is the owner's own stated default.</para>
    ///
    /// <para>mm was briefly 3 (1 µm) and is 4 because 3 rounded a 25.4 µm measurement to
    /// <c>0.025 mm</c> — dropping real signal for the sake of a digit nobody had asked to lose. Since
    /// this is a CEILING and <see cref="LayoutUnits.Format"/> trims trailing zeros, the extra place
    /// costs nothing when it is not needed: a 3.59 mm ruler still reads "3.59 mm".</para>
    /// </summary>
    public static int DefaultDecimalsFor(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm   => 0,
        LayoutUnit.Um   => 2,
        LayoutUnit.Mm   => 4,
        LayoutUnit.Mil  => 1,
        LayoutUnit.Inch => 4,
        _               => 4,
    };

    /// <summary>This ruler's effective precision in <paramref name="unit"/> — its own override when it
    /// has one, otherwise that unit's default. The ONE accessor every readout goes through, so the
    /// canvas, the Properties Inspector, the status line and the DXF export cannot disagree.</summary>
    public int DecimalsFor(LayoutUnit unit) => Decimals is { } d ? Math.Clamp(d, 0, MaxDecimals) : DefaultDecimalsFor(unit);

    /// <summary>
    /// How the readout's numbers are SPELLED — General (the default), Fixed, or either exponential
    /// form. Works together with <see cref="Decimals"/>, which supplies the precision.
    ///
    /// <para><b>General is the default and is what a ruler has always done</b>: up to
    /// <see cref="Decimals"/> places, trailing zeros trimmed. <b>Fixed</b> keeps them, which is the
    /// one thing General cannot do and the reason to reach for it — ten rulers at "F1" all read
    /// "40.0 mil", "12.5 mil", and line up. The exponential forms are for a document whose features
    /// span decades.</para>
    ///
    /// <para>Omitted from the file at its default, so every <c>.clay</c> written before this field
    /// existed — and every ruler that never leaves General — re-serializes byte for byte.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LayoutUnits.LayoutNumberFormat NumberFormat { get; set; } = LayoutUnits.LayoutNumberFormat.General;

    /// <summary>This ruler's formatted length in <paramref name="unit"/> — precision AND spelling
    /// together. The ONE call every readout goes through (canvas, inspector, status line, DXF), so
    /// they cannot disagree about how a number looks.</summary>
    public string FormatLength(long dbu, LayoutUnit unit, int dbuPerMicron) =>
        LayoutUnits.Format(dbu, unit, dbuPerMicron, DecimalsFor(unit), NumberFormat);

    /// <summary>Upper bound on <see cref="Decimals"/>. Beyond this the digits are below one DBU at
    /// every resolution this editor supports, so they report rounding noise rather than a measurement.</summary>
    public const int MaxDecimals = 9;

    /// <summary>The measured distance, in DBU. R-rul-5: computed, never stored and never editable —
    /// a ruler whose number can be typed over is not a measurement.</summary>
    [JsonIgnore]
    public long DistanceDbu
    {
        get
        {
            double dx = (double)X2 - X1, dy = (double)Y2 - Y1;
            return (long)Math.Round(Math.Sqrt(dx * dx + dy * dy));
        }
    }

    public RulerAnnotation Clone() => new()
    {
        X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2,
        SizeMode = SizeMode, TextSizePt = TextSizePt, TextHeightDbu = TextHeightDbu,
        Style = Style, Caption = Caption, ShowComponents = ShowComponents,
        Decimals = Decimals, NumberFormat = NumberFormat,
        TextX = TextX, TextY = TextY, TextHAlign = TextHAlign, TextVAlign = TextVAlign,
    };
}

/// <summary>Which point of the text's own box <see cref="LabelShape.X"/> names, horizontally.</summary>
public enum LabelHAlign { Left, Center, Right }

/// <summary>Which point of the text's own box <see cref="LabelShape.Y"/> names, vertically.
/// <c>Baseline</c> is circuitRF's own historical anchor and stays the default.</summary>
public enum LabelVAlign { Baseline, Top, Middle, Bottom }

public sealed class LabelShape : LayoutShape
{
    public long X { get; set; }
    public long Y { get; set; }
    public string Text { get; set; } = "";
    public long Height { get; set; }

    /// <summary>
    /// <b>Serialization companion to <see cref="RotDeg"/> — never read this directly; read
    /// <see cref="RotationDegrees"/>.</b> Exactly the pairing <see cref="LayoutInstance.Rot"/> /
    /// <see cref="LayoutInstance.RotDeg"/> already uses, for the same reason and with the same
    /// guarantees: the cardinal case (which is nearly all text anyone draws) still serializes as
    /// <c>"Rotation": "R90"</c> with no <c>RotDeg</c> key and no <c>FormatVersion</c> bump, and a
    /// non-cardinal angle degrades here to the NEAREST cardinal rather than to zero.
    /// </summary>
    public LayoutRotation Rotation { get; set; } = LayoutRotation.R0;

    /// <summary>
    /// <b>Serialization companion to <see cref="Rotation"/> — never read this directly; read
    /// <see cref="RotationDegrees"/>.</b> Non-null ONLY for a non-cardinal angle, omitted from the file
    /// entirely when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RotDeg { get; set; }

    /// <summary>
    /// <b>The text's angle — the ONE accessor anything outside persistence may use.</b> Degrees,
    /// counter-clockwise, in the layout's Y-up DBU frame (<see cref="LayoutAngle"/> states the
    /// convention once); normalized to <c>[0, 360)</c> on set.
    ///
    /// <para>Four-way until 2026-08-25, when an owner report of imported board text ("the angled labels
    /// do not look right") made the cost visible: a real board's annotation is routinely at 45 deg, and
    /// snapping it to the nearest 90 leaves the text plausibly drawn and in the wrong place. Every
    /// format circuitRF reads — board, DXF, GDSII — carries an arbitrary text angle, so the snap was
    /// ours alone.</para>
    /// </summary>
    [JsonIgnore]
    public double RotationDegrees
    {
        get => RotDeg is { } d ? LayoutAngle.Normalize(d) : LayoutAngle.OfCardinal(Rotation);
        set
        {
            double n = LayoutAngle.Normalize(value);
            if (LayoutAngle.TryCardinal(n, out var cardinal)) { Rotation = cardinal; RotDeg = null; }
            else { Rotation = LayoutAngle.NearestCardinal(n); RotDeg = n; }
        }
    }

    public bool IsPort { get; set; }

    /// <summary>
    /// <b>An EM port's direction — the way current flows INTO the structure — when it is stated
    /// rather than inferred.</b> Meaningful only when <see cref="IsPort"/>; ignored otherwise.
    ///
    /// <para><c>R0</c> = +x̂, <c>R90</c> = +ŷ, <c>R180</c> = −x̂, <c>R270</c> = −ŷ, i.e. the usual
    /// counter-clockwise convention in layout's y-up world. <c>EmPortExtraction</c> maps that onto
    /// the conductor END the port names (a port whose current flows +x̂ sits on the conductor's
    /// low-x end), so this is the same quantity <c>PlanarPortSide</c> carries, expressed the way a
    /// user points at it.</para>
    ///
    /// <para><b>null means "infer it from the geometry", which is exactly what every port did before
    /// this field existed (owner report, 2026-08-09).</b> That is deliberate and is what makes the
    /// field additive in behaviour as well as in schema: every <c>.clay</c> written before today has
    /// null here and extracts precisely as it did, ambiguity refusals included. The Port tool seeds
    /// it at placement so a new port's direction is visible and editable from the moment it lands,
    /// and Rotate advances it — for a port label Rotate turns the ARROW and leaves the text upright,
    /// because a right-hand port would otherwise be legible only upside down.</para>
    ///
    /// <para>Additive: no <c>.clay</c> <c>FormatVersion</c> bump, and omitted from the file entirely
    /// when null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRotation? PortDirection { get; set; }

    /// <summary>
    /// <b>The layer of the conductor this port COMMITTED to</b>, stamped by the Port tool at
    /// placement and re-stamped whenever the port is dragged. Meaningful only when
    /// <see cref="IsPort"/>; null when the port sits on a placed instance's artwork rather than on a
    /// top-level shape (an instance answers with its own pin, which no top-level layer names).
    ///
    /// <para><b>It exists because two owner requirements are otherwise mutually exclusive</b>
    /// (2026-09-09). A placed port must never move on its own, which means the marker's geometry
    /// may not depend on which layers are switched on; and a drag must not be attracted to metal on
    /// a layer that is switched off. Resolving the
    /// conductor against visible artwork alone breaks the first; against all artwork breaks the
    /// second. Recording WHICH conductor layer the port committed to, at the moment the user could
    /// see it, satisfies both — <c>LayoutPortDirection.LookupFor</c>'s own summary states the rule
    /// once and is where it is enforced.</para>
    ///
    /// <para><b>Null still means "work it out", exactly as <see cref="PortDirection"/>'s null does</b>
    /// — that is what every <c>.clay</c> written before this field carries, and such a port resolves
    /// against the VISIBLE artwork (the safe half: a legacy port is never attracted to metal the user
    /// cannot see, and it commits the first time it is touched). Additive: no <c>FormatVersion</c>
    /// bump, omitted from the file entirely when null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerKey? PortLayer { get; set; }

    /// <summary>
    /// <b>RP-2b — what this port RETURNS THROUGH, when it is not the stackup's ground plane.</b>
    /// Meaningful only when <see cref="IsPort"/>; null means the plane, which is what every port
    /// drawn before this field carries and what a port placed on ordinary metal still gets. Naming a
    /// return is a deliberate act, on exactly <see cref="PortDirection"/>'s terms.
    ///
    /// <para><b>It lives on the LABEL and not in the <c>.cem</c>, and that is a decision rather than
    /// a convenience</b> (RP-2's R-rp2-8). A port is drawn in the layout and its return is part of
    /// what it IS — a coplanar port referenced to the strip beside it is a different port, not the
    /// same port analysed differently — so putting the reference in the setup would split one port's
    /// identity across two files and make the artwork non-portable between setups. The reference
    /// IMPEDANCE goes the other way for the mirror-image reason (<c>EmSetup.PortZ0s</c>): the same
    /// artwork can legitimately be analysed at two impedances.</para>
    ///
    /// <para>Set here, the port needs <see cref="PortReturn"/> as well — a conductor-referenced port
    /// is two cuts driven against each other, and there is no nearest-conductor search for the second
    /// one, because guessing it is guessing which loop the answer is about.</para>
    ///
    /// <para>Additive: no <c>.clay</c> <c>FormatVersion</c> bump, omitted from the file when null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutPortReference? PortReference { get; set; }

    /// <summary>
    /// <b>What KIND of EM port this is: an edge port, an interior cut of the metal, or a port to the
    /// ground plane.</b> Meaningful only when <see cref="IsPort"/>; ignored otherwise.
    ///
    /// <para><b>It lives on the LABEL, and until 2026-09-14 it lived in the <c>.cem</c>
    /// (<c>EmSetup.PortKinds</c>).</b> Moving it is a decision worth stating, because the note that
    /// used to be there argued the other way. The argument for the setup was the reference
    /// impedance's: the same artwork can be analysed two ways. The argument that won is
    /// <see cref="PortReturn"/>'s, and it is stronger for the TYPE than for the return — a port cut
    /// across the middle of a trace is not the same port as one driven from its end, it is a
    /// different port, and the drawing is where a user points at it. The split was also already
    /// incoherent: <see cref="PortReference"/> is on the shape, but <c>EmPortExtraction</c> refuses
    /// it unless the type says <c>InternalDeltaGap</c> — one file's field whose legality was decided
    /// by another file's.
    ///
    /// <para>The reference IMPEDANCE still goes the other way (<c>EmSetup.PortZ0s</c>) and that is
    /// not an inconsistency: 50 Ω versus 75 Ω really is one port measured twice.</para></para>
    ///
    /// <para><b>null means "infer it from the artwork", exactly as <see cref="PortDirection"/>'s null
    /// does</b> — an interior point of a conductor is an internal port, a point at a conductor's end
    /// face is an edge port (<c>LayoutPortDirection.KindOf</c>). That is what every <c>.clay</c>
    /// written before this field carries, and the Port tool stamps it at placement so that what the
    /// ghost drew is what the file records. <b>That is the bug this field fixes</b> (owner report,
    /// 2026-09-14): the tool drew an internal port under the cursor, and one frame after the click
    /// the open <c>.cem</c> republished the port as an edge port — because an empty <c>PortKinds</c>
    /// answered Edge for a port nobody had ever typed — so the marker jumped to the far end of the
    /// trace.</para>
    ///
    /// <para><b>The engine's enum, not a layout one</b> — unlike <see cref="LayoutPortReference"/>,
    /// whose note explains that the engine's <c>PlanarPortReference</c> carries a fourth member
    /// naming a refusal rather than anything anyone can draw. This enum's three members are exactly
    /// the three things a user can draw, so a parallel layout enum would be a mapping table and two
    /// places to add the fourth kind.</para>
    ///
    /// <para>Additive: no <c>.clay</c> <c>FormatVersion</c> bump, omitted from the file when null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanarPortKind? PortKind { get; set; }

    /// <summary>
    /// <b>RP-2b — the point on the RETURN conductor this port's negative terminal is cut at.</b>
    /// DBU, in the layout's own frame, exactly as <see cref="X"/>/<see cref="Y"/> are. Meaningful
    /// only when <see cref="PortReference"/> is set; ignored otherwise, and null there is refused by
    /// name rather than guessed at.
    ///
    /// <para>It resolves through the very same routine the positive terminal does
    /// (<c>EmPortExtraction</c>, then <c>PlanarPorts.TryResolveAgainstPlane</c>) — a second
    /// resolution rule would be a second chance for the two terminals to land on different levels
    /// with nobody noticing (RP-2's R-rp2-2, L9d's reason one level up).</para>
    ///
    /// <para>Additive: no <c>.clay</c> <c>FormatVersion</c> bump, omitted from the file when null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutPortReturn? PortReturn { get; set; }

    /// <summary>Additive (no <c>.clay</c> <c>FormatVersion</c> bump) — a newly-placed label always
    /// defaults to Regular; edited via the Properties Inspector.</summary>
    public LabelFontStyle Style { get; set; } = LabelFontStyle.Regular;

    /// <summary>
    /// <b>Which point of the text <see cref="X"/>/<see cref="Y"/> actually names.</b> circuitRF's own
    /// anchor has always been left-of-the-first-glyph, on the BASELINE, and that is what null means on
    /// both — so every <c>.clay</c> written before these fields existed loads and renders identically,
    /// and neither field is written when null (additive, no <c>FormatVersion</c> bump).
    ///
    /// <para>They exist because an imported format need not share that convention and usually does not:
    /// a board file anchors its text at the CENTRE of the box by default and states any other choice as
    /// its own justification token. Baking the difference into <see cref="X"/>/<see cref="Y"/> at import
    /// time was the alternative and is worse twice over — it needs the rendering font to measure the
    /// string (unavailable to a reader, which has no Avalonia host to load one through), and the offset
    /// it bakes in goes stale the moment the text or its height is edited.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabelHAlign? HAlign { get; set; }

    /// <inheritdoc cref="HAlign"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabelVAlign? VAlign { get; set; }
}

/// <summary>A reference image (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) — a
/// tracing underlay, ported from the symbol editor's <c>BitmapPrimitive</c>. Stored as a path
/// reference, never embedded bytes, matching the symbol/schematic convention exactly.
///
/// <b>Not geometry (§3):</b> <see cref="LayoutClipper"/>/<see cref="LayoutBooleans"/>/
/// <see cref="LayoutFlattener"/> never see a <c>BitmapShape</c> — Union/Intersect/Difference/XOR/
/// Offset/Flatten/Repair all exclude it from their operand set (disabled with a reason when a
/// selection is bitmap-only; silently skipped, never a crash, in a mixed selection). // L4: never
/// exported to GDSII/DXF/Gerber — one Messages note per export counting how many were skipped, since
/// a reference image is not manufacturable geometry. // L5b: skipped by DRC. // L6: skipped by MoM
/// meshing. It DOES participate in bbox/hit-test/select/move/scale/clipboard/undo, and — because it IS
/// a visual — is rendered into the clipboard's PDF/SVG/EMF graphic export (L1f).
///
/// <b><see cref="LayoutShape.Layer"/> governs visibility/selectability ONLY, never paint order
/// (R-bmp-2):</b> a bitmap ALWAYS renders behind every layer, regardless of that layer's
/// <c>ZOrder</c> — the use case is tracing over a reference photo, and a semi-transparent layer fill
/// reading on top of it is exactly what is wanted. This is a deliberate exception to every other
/// shape's "Layer determines both" rule; say so here because it is otherwise a surprise.</summary>
public sealed class BitmapShape : LayoutShape
{
    /// <summary>Path to the image file — absolute, or relative to the containing <c>.clay</c>, same
    /// convention (and same "may not resolve after a cross-workspace paste" limitation, fixed the
    /// same way — Resolve Path…) as <c>BitmapPrimitive.ImagePathRef</c>.</summary>
    public string ImagePathRef { get; set; } = "";

    /// <summary>Placement rect: minimum corner + size, in DBU — never doubles (§1.1 R1: every layout
    /// coordinate is integer DBU; the symbol editor's own <c>BitmapPrimitive</c> uses <c>double</c>
    /// only because symbol-local units already are one).</summary>
    public long X { get; set; }
    public long Y { get; set; }
    public long W { get; set; }
    public long H { get; set; }

    public double Opacity { get; set; } = 1.0;

    /// <summary>When locked, accidental click/drag does not move or scale the bitmap — exactly right
    /// for a tracing underlay the user does not want to disturb while drawing on top of it.</summary>
    public bool Locked { get; set; }
}

// ── Instance ────────────────────────────────────────────────────────────────

/// <summary>A cell-reference placement, optionally an array (rows/cols/pitch = GDSII AREF).</summary>
public sealed class LayoutInstance
{
    /// <summary>Relative path to the referenced cell folder.</summary>
    public string CellRef { get; set; } = "";

    public long X { get; set; }
    public long Y { get; set; }

    /// <summary>
    /// <b>Serialization companion to <see cref="RotDeg"/> — never read this directly; read
    /// <see cref="RotationDegrees"/></b> (brief-L3d-arbitrary-angle-instances.md R-L3d-5, held shut by
    /// <c>LayoutInstanceRotationAccessorTests</c>).
    ///
    /// <para>Carries the placement angle for the overwhelmingly common cardinal case, exactly as every
    /// <c>.clay</c> written before L3d already does, which is what makes an arbitrary angle an ADDITIVE
    /// change: a design that only ever rotates by 90 deg still serializes as <c>"Rot": "R90"</c> with no
    /// <c>RotDeg</c> key and no <c>FormatVersion</c> bump. For a non-cardinal angle this holds the
    /// NEAREST cardinal, so the field degrades to something sane rather than to zero.</para>
    /// </summary>
    public LayoutRotation Rot { get; set; }

    /// <summary>
    /// <b>Serialization companion to <see cref="Rot"/> — never read this directly; read
    /// <see cref="RotationDegrees"/></b> (R-L3d-5).
    ///
    /// <para>Non-null ONLY for a non-cardinal placement angle, and omitted from the file entirely when
    /// null — the same additive pattern <see cref="LabelShape.PortDirection"/> already established, and
    /// for the same reason: every <c>.clay</c> written before this field existed loads and re-saves
    /// byte-identically.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RotDeg { get; set; }

    /// <summary>
    /// <b>The placement angle — the ONE accessor anything outside persistence may use</b> (R-L3d-5).
    /// Degrees, counter-clockwise, in the layout's Y-up DBU frame (<see cref="LayoutAngle"/>'s header
    /// states the convention once); normalized to <c>[0, 360)</c> on set.
    ///
    /// <para><b>Why an accessor rather than two fields anyone may touch.</b> Two fields with one meaning
    /// drift — this repo has already paid for that once, with three copies of the version number
    /// disagreeing (<c>VersionSingleSourceTests</c> is the scar). Setting this keeps
    /// <see cref="Rot"/> and <see cref="RotDeg"/> consistent by construction: a cardinal angle writes
    /// the enum and clears <see cref="RotDeg"/>; anything else writes both.</para>
    /// </summary>
    [JsonIgnore]
    public double RotationDegrees
    {
        get => RotDeg is { } d ? LayoutAngle.Normalize(d) : LayoutAngle.OfCardinal(Rot);
        set
        {
            double n = LayoutAngle.Normalize(value);
            if (LayoutAngle.TryCardinal(n, out var cardinal)) { Rot = cardinal; RotDeg = null; }
            else { Rot = LayoutAngle.NearestCardinal(n); RotDeg = n; }
        }
    }

    public bool MirrorX { get; set; }
    public double Mag { get; set; } = 1.0;

    /// <summary>1×1 = a plain instance. R-via-8 forward hook (docs/sonnet-briefs/
    /// brief-via-primitive-and-stackup.md §5): via fences and thermal via arrays are exactly this —
    /// a regular grid whose Rows/Cols/PitchX/PitchY IS the design variable — which strengthens, but
    /// does not itself build, the case for L3c's own deferred "Create Array from selection" (an
    /// array instance built FROM an existing selection's pitch, the inverse of Explode Array). Still
    /// unbuilt; note it here again rather than silently losing the thread a second time.</summary>
    public int Rows { get; set; } = 1;
    public int Cols { get; set; } = 1;
    public long PitchX { get; set; }
    public long PitchY { get; set; }

    /// <summary>docs/design/layout-view.md §9 R16 re-run idempotency. Unused until L5.</summary>
    public string? SchematicId { get; set; }

    /// <summary>
    /// The content hash of the referenced cell's published INTERFACE at the moment this instance was
    /// placed (SL3 R-sl3-4) — pins, port count and declared parameter names, reduced by
    /// <c>CellInterfaceHash</c>. Recorded beside <see cref="CellRef"/>, in the file that holds the
    /// instance, because that is the only place the fact "this design was authored against THAT
    /// shape" can live.
    ///
    /// <para><b>Absent is not a warning</b> (R-sl3-5). Every <c>.clay</c> written before this field
    /// existed has no recorded hash, and so does every instance a user places by hand-editing a file.
    /// Absent means <i>never recorded</i> and renders exactly as it did before — <c>WhenWritingNull</c>
    /// and no <c>FormatVersion</c> bump, the same additive convention <see cref="RotDeg"/> beside it
    /// already follows. A feature whose first act is to mark every existing design as suspect is a
    /// feature that gets turned off.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CellInterfaceHash { get; set; }

    // ── The reference designator this placement draws on silkscreen ─────────────────────────────
    //
    // brief-footprint-4b-designators.md. THE DESIGNATOR BELONGS TO THE PLACEMENT, NOT TO THE CELL,
    // and the TEXT is derived while only its PLACEMENT is stored. Every field below is nullable and
    // omitted from the file when null — the same additive convention RotDeg and CellInterfaceHash
    // above already follow, so no FormatVersion bump and every .clay written before this existed
    // re-serializes byte-for-byte.

    /// <summary>
    /// The designator of an instance that corresponds to NO schematic component — a part placed by
    /// hand, or one read out of a board file. <b>Never read this directly; read
    /// <see cref="DisplayRefDes"/></b> (R-fp4b-1c), and never write it on an instance that has a
    /// <see cref="SchematicId"/>.
    ///
    /// <para>An instance the schematic owns takes its designator FROM <see cref="SchematicId"/> and
    /// stores nothing here (R-fp4b-1a): two fields with one meaning drift, which is the scar
    /// <see cref="RotationDegrees"/>'s own comment cites. Derived means a rename in the schematic
    /// arrives through Update Layout with no migration and no possibility of a board that disagrees
    /// with the drawing about what a part is called.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefDes { get; set; }

    /// <summary>
    /// <b>The component this placement IS</b>, for an instance that corresponds to no schematic
    /// component yet — brief-footprint-6 R-fp6-2d. A resistor dropped into a board straight from the
    /// Library palette is a PART, not a piece of copper, and this is the only honest place to say so:
    /// the land-pattern cell is shared by every 0402 on the board (a resistor and a capacitor at one
    /// case size are one cell, correctly), so what the part is cannot live on the cell.
    ///
    /// <para>Null on every instance that has a <see cref="SchematicId"/> — the schematic knows — and
    /// null on every land pattern placed by the Footprint tool, where nothing knows and nothing may
    /// invent one (R-fp6-1a). Update Schematic from Layout clears it as it links (R-fp6-3d), for the
    /// reason <see cref="RefDes"/> is cleared with it: two fields with one meaning drift.</para>
    ///
    /// <para>Stored as the kind's NAME, never the enum's number, and read back through
    /// <c>LayoutPartKind.Of</c> — which treats an unrecognised value as ABSENT rather than as an
    /// error, so a <c>.clay</c> written by a later version naming a part this one has never heard of
    /// still opens, and degrades to the bare-land-pattern case.</para>
    ///
    /// <para>Nullable and omitted when null, so no <c>FormatVersion</c> bump: the same additive
    /// convention every field in this block already follows.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartKind { get; set; }

    /// <summary>
    /// The orientations the schematic component and this placement had the last time Update Layout
    /// from Schematic or Update Schematic from Layout brought the two into step — the baseline that
    /// lets either command tell WHICH side was rotated since, and carry only that change across
    /// (<see cref="SchematicLayoutOrientation"/>). Null on every instance no sync has touched yet.
    ///
    /// <para>Only meaningful beside a <see cref="SchematicId"/>. Nullable and omitted when null, so
    /// no <c>FormatVersion</c> bump — the additive convention every field in this block follows.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrientationLink? OrientationLink { get; set; }

    /// <summary>
    /// Where the designator sits relative to this instance's own origin, <b>in the instance's placed
    /// frame and in the PARENT's DBU</b> (R-fp4b-2b) — so it moves and rotates with its part, which
    /// is what a user who dragged it there meant, and so a drag stores the delta in the frame the
    /// drag happened in.
    ///
    /// <para><b>Null means AUTO, and auto is RECOMPUTED, never frozen</b> (R-fp4b-2a). The default
    /// comes from the resolved cell's own silkscreen/courtyard extent, every time it is drawn, via
    /// the one function <c>FootprintLabel.AutoOffset</c>. Writing the computed default into the file
    /// at placement time would freeze it, so re-pointing a part from 0402 to 0805 — a supported
    /// gesture — would leave its designator sitting inside the bigger body: correct when written,
    /// wrong afterwards, and wrong silently.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LabelDx { get; set; }

    /// <inheritdoc cref="LabelDx"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LabelDy { get; set; }

    /// <summary>The designator's own angle when the user has set one. Null follows the placement,
    /// normalized readable — a designator is never drawn upside down (R-fp4b-3a).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LabelRotDeg { get; set; }

    /// <summary>The designator's cap height in the PARENT's DBU when the user has set one. Null is
    /// the board-wide default (<c>FootprintLabel.DefaultHeightMm</c>) — a FIXED size, never a
    /// fraction of the part, or a board carrying 0402s and a 7343-31 would print designators an
    /// order of magnitude apart and the small ones would be unreadable (R-fp4b-2d).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LabelHeight { get; set; }

    /// <summary>Per-instance visibility. <b>Null means SHOWN</b> (R-fp4b-7a, confirmed with the
    /// owner 2026-09-20): the report this came from is that a designer could not tell which part was
    /// which, and a designator behind a switch nobody finds does not answer it. Turning it off for a
    /// crowded corner of a board is a per-instance (and multi-select) edit; there is deliberately no
    /// global view-only toggle, because a switch that makes the screen disagree with the export is
    /// the defect R-fp4b-4a exists to prevent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowRefDes { get; set; }

    /// <summary><b>The designator — the ONE accessor anything outside persistence may use</b>
    /// (R-fp4b-1c), in the shape <see cref="RotationDegrees"/> already established. Null when this
    /// placement has no identity to draw, which is not the same as an empty one: an instance
    /// corresponding to no schematic component and given no designator must not be handed a
    /// fabricated one (R-fp4b-8c).</summary>
    [JsonIgnore]
    public string? DisplayRefDes
        => SchematicId is { Length: > 0 } s ? s : RefDes is { Length: > 0 } r ? r : null;

    /// <summary>Whether this placement's designator is drawn — <see cref="ShowRefDes"/> with its
    /// null default resolved once, here, rather than at each of the four call sites that ask.</summary>
    [JsonIgnore]
    public bool DesignatorShown => ShowRefDes ?? true;
}

/// <summary>
/// docs/design/pcell-contract.md R1/R9: marks a <see cref="LayoutView"/> as PCell-generated
/// rather than hand-drawn — the editor uses this to disable editing tools with a reason
/// (flatten is the escape hatch) and to know what to re-invoke on a parameter/technology change
/// (L3b's existing invalidation seam, not a new mechanism). <see cref="GeneratorId"/> is a key
/// into <c>CircuitRF.Design.Layout.PCells.PCellRegistry</c>; <see cref="Parameters"/> is the resolved
/// parameter snapshot (SI values) the generated geometry was last built from.
/// L5a's four built-ins are SymbolKind-registered (like TLIN/R/L/C), not on-disk cell folders, so
/// this marker — not a <c>.ccell</c> field — is what makes a <see cref="LayoutView"/> "generated"
/// for the read-only/regeneration gates in this phase; L5's own schematic→layout work is what
/// will attach this to a real placed instance's cell.
/// </summary>
public sealed record PCellOrigin(
    string GeneratorId,
    IReadOnlyDictionary<string, PCells.PCellValue> Parameters,
    IReadOnlyList<string>? ComputedParameters = null,
    IReadOnlyDictionary<string, PCells.PCellValue>? ComputedValues = null,
    IReadOnlyList<string>? UnreadParameters = null)
{
    /// <summary>Whether the generator DERIVES <paramref name="name"/> rather than reading it — a MIM
    /// cap's capacitance from its own w and l, a resistor's resistance from its own geometry.
    /// Editing such a parameter cannot change anything, so the parameter list shows it as text.</summary>
    public bool IsComputed(string name)
        => ComputedParameters is { } c && c.Contains(name, StringComparer.Ordinal);

    /// <summary>Whether the run that drew this artwork never READ <paramref name="name"/> — so
    /// nothing about the geometry depends on it. Not a claim that the parameter is inert or
    /// read-only: a netlist parameter (a model name, a multiplier) is unread here and still the
    /// user's to set. It is said on the row, and nothing branches on it.</summary>
    public bool IsUnread(string name)
        => UnreadParameters is { } u && u.Contains(name, StringComparer.Ordinal);
}

/// <summary>
/// brief-L5-followups-2.md §4.2/R-L5g-6: a per-generated-cell REGENERATION RECORD — the exact inputs
/// (a <c>PCellRegistry</c> generator id, resolved parameters, technology identity, layer overrides)
/// <c>GeneratedCellStore.GetOrCreate</c> needs to rebuild ONE generated cell folder byte-identically.
/// Keyed on <see cref="LayoutView.PCellSnapshots"/> by the generated cell's own FOLDER NAME — not by
/// an invented "instance identity" — because that folder name already IS a content hash of exactly
/// these same fields (<c>GeneratedCellStore.BuildCellName</c>), so the key is inherently stable and is
/// naturally shared by every instance referencing the same cell (R-L5-1), with nothing to track through
/// Duplicate/Paste/Undo. This is the record that makes "a generated cell is a pure, deletable,
/// rebuildable-from-the-layout cache — never authoritative" (§4.2) literally true: on a dangling
/// <see cref="LayoutInstance.CellRef"/> the last path segment is this dictionary's key, and calling
/// <c>GeneratedCellStore.GetOrCreate</c> with the recorded fields reproduces the identical folder.
///
/// Deliberately a SEPARATE table from <see cref="LayoutView.SchematicPCellSnapshots"/> — that one keys
/// on <see cref="LayoutInstance.SchematicId"/> for a DIFFERENT purpose (R-L5-9/10/11's overwrite
/// classification: "what did THIS SPECIFIC SCHEMATIC COMPONENT generate last time," which is
/// inherently schematic-id-scoped and has no meaning for a palette-dropped or layout-authored
/// instance) and must keep working exactly as before. <see cref="PCellSnapshots"/> is the generalized
/// mechanism the brief asks for, covering every PCell instance regardless of origin.
/// </summary>
public sealed record PCellSnapshot(
    string GeneratorId,
    IReadOnlyDictionary<string, PCells.PCellValue> Parameters,
    string? TechIdentity,
    string? SignalLayerNameOverride,
    string? GroundLayerNameOverride);

// ── Container ───────────────────────────────────────────────────────────────

/// <summary>
/// A connection point on a cell — name, where it is, how WIDE the connection is, and which way it
/// FACES (docs/design/layout-view.md §9, R3).
///
/// <para><b>Why this is its own type and not a <see cref="LabelShape"/> with <c>IsPort</c> set.</b>
/// Those two answer different questions and only look similar. A port label is TEXT the user sees;
/// it carries a name, a position and a layer, and nothing else — which is all a label needs. A pin
/// is CONNECTIVITY: a connection is an edge, not a point, so a width and an outward direction are
/// what make it usable by anything that has to join to it. Overloading the label would have meant
/// putting connectivity fields on a type used broadly for ordinary annotation, and would still have
/// left "which labels are pins" as a flag rather than a list.</para>
///
/// <para><b>This is the state whose absence made pins unpersistable.</b> Before it, a generated
/// cell's pins survived only as those name/position/layer labels, and the renderer recovered width
/// and direction by re-invoking the generator — exact for a PCell, and impossible for an imported
/// cell, which has no generator to invoke. A pin list on the view is what lets artwork that was
/// merely IMPORTED carry connectivity too.</para>
/// </summary>
public sealed class LayoutPin
{
    /// <summary>The terminal's name (<c>G</c>, <c>D</c>, <c>S</c>). May be empty when nothing named
    /// it — an unnamed pin is still a real connection point, just one the user must identify.</summary>
    public string Name { get; set; } = "";

    public long X { get; set; }
    public long Y { get; set; }

    /// <summary>How wide the connection is, across the direction it faces. Zero means unstated.</summary>
    public long WidthDbu { get; set; }

    /// <summary>Which way the pin faces, degrees counter-clockwise from +X — the direction a
    /// connection leaves the cell.</summary>
    public double OutwardDeg { get; set; }

    public LayerKey Layer { get; set; }
}

public sealed class LayoutView
{
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// How this document's lengths are SPELLED. Storage is DBU either way; this is the reading.
    /// </summary>
    /// <remarks>
    /// <b>It raises <see cref="DisplayUnitChanged"/> and deliberately NOT <see cref="Changed"/></b>.
    /// The two are different kinds of event: <c>Changed</c> says the GEOMETRY moved, and the spatial
    /// index, the path caches and the undo stack all hang off it — a unit is a document preference and
    /// belongs to none of them. But it is still shared state on a shared model, and a second window on
    /// this same view (railRF's board panel is one) has no other way to learn that the unit it is
    /// printing in is no longer the document's.
    /// </remarks>
    public LayoutUnit DisplayUnit
    {
        get => _displayUnit;
        set
        {
            if (_displayUnit == value) return;
            _displayUnit = value;
            DisplayUnitChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private LayoutUnit _displayUnit = LayoutUnit.Um;

    /// <summary>Raised when <see cref="DisplayUnit"/> changes — and only then. See its own note for
    /// why this is not <see cref="Changed"/>.</summary>
    public event EventHandler? DisplayUnitChanged;
    public long SnapDbu { get; set; }
    public AngleMode AngleMode { get; set; } = AngleMode.AnyAngle;

    /// <summary>Relative path to a .ctech.</summary>
    public string? TechRef { get; set; }

    /// <summary>
    /// What <see cref="LayoutPersistence"/> noticed while reading this view from a file and did NOT
    /// refuse it for — keys it ignored, shapes too small to be shapes (<see cref="LayoutLoadAudit"/>).
    /// Empty for a view built in memory or read cleanly. Never persisted: it describes the file as it
    /// was READ, and a save writes neither the unknown keys nor anything about them. The GUI posts it
    /// once per fresh load; <c>circuitrf check</c> reports it.
    /// </summary>
    public IReadOnlyList<LayoutLoadFinding> LoadFindings { get; set; } = [];

    /// <summary>
    /// This cell's connection points (§9, R3). Empty for a cell that declares none — which is the
    /// ordinary case for hand-drawn artwork and not a defect.
    ///
    /// <para>Populated by a PCell generator's own declared pins, or recovered from imported artwork
    /// by <c>PinInference</c>. Both routes land here, so nothing downstream needs to know which one
    /// produced them.</para>
    /// </summary>
    public List<LayoutPin> Pins { get; set; } = [];

    /// <summary>Non-null when this view's <see cref="Shapes"/> were produced by a PCell generator
    /// rather than drawn by hand. See <see cref="PCellOrigin"/>.</summary>
    public PCellOrigin? PCellOrigin { get; set; }

    /// <summary>
    /// L5, R-L5-11: the PCell parameter set last pushed onto a schematic-linked
    /// <see cref="LayoutInstance"/> by "Update Layout from Schematic", keyed by
    /// <see cref="LayoutInstance.SchematicId"/>. Deliberately NOT a field on <see cref="LayoutInstance"/>
    /// itself (the guardrail: an instance is a transform of a cell, nothing more) — this is a per-VIEW
    /// side table instead, because the value it needs to remember ("what did the schematic generate
    /// last time") is independent of which generated cell the instance currently references (that
    /// reference may have moved on since, via a direct layout-side parameter edit — R-L5-9). A re-run
    /// compares this snapshot against BOTH the current schematic value and the instance's currently-
    /// referenced cell's own <see cref="PCellOrigin"/>.Parameters to tell "the schematic changed"
    /// (informational) apart from "the layout was edited" (a warning — user work about to be
    /// discarded), per R-L5-11's three-row table. Absent entry = never schematic-generated (a
    /// palette-placed instance, R-L5-6's own exemption) or not yet run.
    /// </summary>
    public Dictionary<string, Dictionary<string, PCells.PCellValue>> SchematicPCellSnapshots { get; } = new(StringComparer.Ordinal);

    /// <summary>brief-L5-followups-2.md §4.2/R-L5g-6: the generalized regeneration record — see
    /// <see cref="Layout.PCellSnapshot"/>. Keyed by generated-cell FOLDER NAME (never an instance
    /// identity), populated at every site that calls <c>GeneratedCellStore.GetOrCreate</c> from a
    /// layout context. Covers every PCell instance this layout references, regardless of whether it
    /// arrived via schematic generation, a palette drop, or a layout-authored copy-on-write edit.</summary>
    public Dictionary<string, PCellSnapshot> PCellSnapshots { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// L5b (docs/design/layout-view.md §9A.1): deliberate, persisted exceptions to a design rule at a
    /// named place — see <see cref="Drc.DrcWaiver"/>. Lives on the LAYOUT, never on the technology: a
    /// waiver is a statement about this artwork, and a technology shared by twenty cells must not
    /// accumulate one cell's exceptions.
    ///
    /// <para><b>Not undoable, deliberately.</b> Waiving is a review judgement recorded against the
    /// design, not a geometry edit — putting it on the shape-editing undo stack would let Ctrl+Z after
    /// an unrelated edit silently revoke it. It does mark the document dirty so it is saved.</para>
    /// </summary>
    public List<Drc.DrcWaiver> DrcWaivers { get; } = [];

    /// <summary>
    /// brief-lvs-12-gui.md R-lvs12-4: deliberate, persisted exceptions to an LVS finding — see
    /// <see cref="Lvs.LvsWaiver"/>. Beside <see cref="DrcWaivers"/> and on the same terms: on the
    /// LAYOUT that was checked, still reported and merely not counted, dirty but not undoable.
    ///
    /// <para><b>The KEY is the one place the two differ</b>, deliberately (R-lvs12-4b). A DRC waiver
    /// names a place and stops applying when the shape moves; an LVS waiver names a relationship,
    /// survives every layout edit, and is invalidated by a schematic one. See
    /// <see cref="Lvs.LvsWaiverKey"/>.</para>
    ///
    /// <para><b>The RUN never writes this</b> (R-lvs12-4g) — it reads the list and hands back
    /// findings marked, which is what keeps an LVS run usable on a read-only tree.</para>
    /// </summary>
    public List<Lvs.LvsWaiver> LvsWaivers { get; } = [];

    public List<LayoutShape> Shapes { get; } = [];
    public List<LayoutInstance> Instances { get; } = [];

    /// <summary>
    /// docs/design/layout-view.md §9B: the in-design ruler annotations — a top-level collection
    /// beside <see cref="Shapes"/> and <see cref="Instances"/>, never inside <c>Shapes</c>
    /// (D-ruler-1; see <see cref="RulerAnnotation"/>'s own doc comment for why that boundary is
    /// structural rather than maintained).
    ///
    /// <para><b>Deliberately not in <see cref="SpatialIndex"/></b> (§9B.11): there are tens of
    /// rulers, not 500,000 — a linear scan for hit-test and paint is the right-sized tool. Mutations
    /// still go through <see cref="NotifyChanged"/> so the canvas repaints; pass
    /// <see cref="LayoutChangeInfo.Full"/> rather than inventing a ruler-specific change kind.</para>
    ///
    /// <para><b>Cell-local</b> (§9B.7): a ruler drawn here does NOT render when this cell is placed as
    /// an instance in a parent layout, and <c>LayoutFlattener</c> does not carry it up. An annotation
    /// is a statement its author made while working on THIS cell.</para>
    /// </summary>
    public List<RulerAnnotation> Rulers { get; } = [];

    /// <summary>The R-tree spatial index (L2b, docs/design/layout-view.md §5.2 R11) over
    /// <see cref="Shapes"/> — every query self-heals lazily (see its own doc comment), so it is always
    /// safe to read even for a <see cref="LayoutView"/> whose <see cref="Shapes"/> were populated by
    /// direct list mutation and never once ran through <see cref="NotifyChanged"/>.</summary>
    public LayoutSpatialIndex SpatialIndex { get; } = new();

    /// <summary>
    /// Held for the duration of one rendered FRAME and for the duration of one
    /// <see cref="NotifyChanged"/>, so a repaint and an edit-notification can never overlap.
    ///
    /// <para><b>Why a layout model has a lock at all:</b> Avalonia renders <c>LayoutCanvas</c> through
    /// an <c>ICustomDrawOperation</c>, which runs on the RENDER thread while the UI thread keeps
    /// editing this model. Two pieces of shared state made that a crash rather than a glitch: the
    /// spatial index (which self-heals — i.e. WRITES — from a query, and now takes its own internal
    /// lock for that reason), and <c>LayoutPathCache</c>, which <see cref="Changed"/> subscribers
    /// invalidate by DISPOSING native <c>SKPath</c> objects the render thread may be drawing at that
    /// instant. Raising <see cref="Changed"/> under this lock is what makes that invalidation wait for
    /// the frame using those paths to finish.</para>
    ///
    /// <para>This does not make arbitrary reads of <see cref="Shapes"/> atomic — a frame that starts
    /// while a command is midway through its own list mutation still sees a half-applied edit and
    /// simply draws it, one frame before the notification arrives. That is a stale pixel, not a crash;
    /// <c>LayoutRenderer</c> bounds-checks every index it takes from the spatial index so a list that
    /// shrank underneath a candidate set cannot throw on the render thread.</para>
    /// </summary>
    public object RenderLock { get; } = new();

    /// <summary>Raised after any mutation of <see cref="Shapes"/>/<see cref="Instances"/> —
    /// <c>LayoutCanvas</c> subscribes to repaint (mirrors <c>EditableSymbol.Changed</c>). Commands
    /// under <c>src/Ui/Commands/Layout/</c> call <see cref="NotifyChanged"/> after every mutation,
    /// in both Execute and Undo.</summary>
    public event EventHandler<LayoutChangeInfo>? Changed;

    /// <summary>
    /// <paramref name="info"/> describes what changed, for <see cref="SpatialIndex"/>'s incremental
    /// maintenance (L2b, R-L2b-2 — "one hook, not update calls sprinkled through a dozen commands":
    /// the index's own <see cref="LayoutSpatialIndex.Apply"/> is that one hook, called from here,
    /// before <see cref="Changed"/> fires, so every subscriber — including a future one — always sees
    /// a fresh index by the time it runs). Omit it (or pass <c>null</c>) for any mutation not worth
    /// classifying precisely — <see cref="LayoutChangeInfo.Full"/> is always correct, just triggers a
    /// full index rebuild instead of an incremental update.
    /// </summary>
    public void NotifyChanged(LayoutChangeInfo? info = null)
    {
        info ??= LayoutChangeInfo.Full;

        // Under RenderLock so the index update AND every subscriber's own invalidation (notably
        // LayoutCanvas disposing this model's cached SKPaths) are excluded from a frame in flight.
        // Safe to hold across the event: the render thread never waits on the UI thread inside it
        // (LayoutDrawOperation posts its frame result asynchronously, precisely so this cannot
        // deadlock), and every subscriber here is a cheap, non-blocking invalidate.
        lock (RenderLock)
        {
            if (info.Kind == LayoutChangeKind.InstancesChanged)
            {
                // L3a (R-L3a-4): an instances-only change never touches the shape side of the tree —
                // Apply() would do needless (if harmless) shape bookkeeping otherwise.
                SpatialIndex.MarkInstancesDirty();
            }
            else
            {
                SpatialIndex.Apply(Shapes, info);
                if (info.Kind == LayoutChangeKind.Full) SpatialIndex.MarkInstancesDirty();
            }
            Changed?.Invoke(this, info);
        }
    }
}
