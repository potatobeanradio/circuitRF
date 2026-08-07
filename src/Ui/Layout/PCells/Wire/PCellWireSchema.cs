using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// Version of the BYTE FORMAT, deliberately separate from <see cref="PCellContractVersion"/>.
///
/// <para>They version different things and can move independently: the contract describes what a
/// generator receives (kinded parameters, R5's guarantees), the wire describes how that crosses a
/// process boundary. A byte-layout change need not change the semantics and a semantic change need
/// not change the bytes. Conflating them means a host that only speaks a new layout claims to
/// implement a new contract.</para>
///
/// <para><b>A mismatch is refused with both numbers named, never negotiated.</b> Negotiation means N
/// code paths of which the rare ones are wrong; refusing is one path and the message says exactly
/// what to update. Adding a shape kind, a dimension or a value kind is a bump — see
/// <c>docs/design/pcell-wire-schema.md</c> §7.</para>
/// </summary>
public static class PCellWireVersion
{
    /// <summary>
    /// Version 6 (2026-08-06) added <see cref="PCellWireGenerateReply.Handles"/> — the optional
    /// draggable parameter grips of <c>docs/design/pcell-parameter-handles.md</c>.
    ///
    /// <para><b>The bump is required even though the field is purely additive</b>, and the reason is
    /// this file's own refusal rule: versions are compared for equality and a mismatch is refused
    /// rather than negotiated, so any change to what a reply may contain is a bump by definition.
    /// <see cref="PCellContractVersion"/> is untouched — <c>Generate</c>'s signature has not changed,
    /// only what a generator may optionally include in its result, which is exactly the kind of
    /// independent movement the two numbers exist to allow.</para>
    ///
    /// <para>Version 5 (2026-08-04) added <see cref="PCellWireOp.Offset"/> — grow/shrink, asked of the host
    /// for the same reason the booleans are (§8): it is Clipper2 offset, which circuitRF already owns,
    /// and a second implementation would disagree invisibly.
    ///
    /// <para>Version 4 (2026-08-04) added <see cref="PCellWireParameterDecl.Default"/>, so a host can PLACE
    /// a script's cell without being told its parameters — a generator states what its own defaults
    /// are, which is what makes a placed vendor cell editable rather than fixed at whatever the
    /// script fell back to.</para>
    ///
    /// <para>Version 3 (2026-08-04) let a frame travel the OTHER WAY: a script may, mid-generate, ask the
    /// host to perform a layer boolean (<see cref="PCellWireOp.Clip"/>) rather than implementing a
    /// second clipper on its own side. Nothing already on the wire changed shape — see
    /// <c>docs/design/pcell-wire-schema.md</c> §8.</para>
    ///
    /// <para>Version 2 (2026-08-04) added <see cref="PCellWireGenerateRequest.DbuPerMicron"/>, so a
    /// generator can express an ABSOLUTE PHYSICAL CONSTANT — a process dimension it holds itself,
    /// rather than one circuitRF passed in. Schema §1 records why version 1 deliberately withheld
    /// it.</para>
    /// </summary>
    public const int Current = 6;
}

/// <summary>
/// The commands. The first three match the device worker's shape so the two hosts read alike;
/// <see cref="Clip"/> is the one that travels script→host (schema §8).
/// </summary>
public static class PCellWireOp
{
    public const string Describe = "describe";
    public const string Generate = "generate";
    public const string Shutdown = "shutdown";

    /// <summary>Script→host: perform a layer boolean with circuitRF's own clipper.</summary>
    public const string Clip = "clip";

    /// <summary>Script→host: grow or shrink a region with circuitRF's own clipper.</summary>
    public const string Offset = "offset";
}

// ── clip (script → host) ─────────────────────────────────────────────────────

/// <summary>Which boolean, named as the script's own generators name them rather than as Clipper2
/// does — the mapping to <c>ClipType</c> lives in one place, in the service.</summary>
public enum PCellWireClipRule
{
    And,
    Or,
    Not,
    Xor,
}

/// <summary>
/// A layer boolean, asked of the host mid-generate.
///
/// <para>The two operands are described only by their RING VERTEX COUNTS; the coordinates ride in
/// the frame's int64 payload, subject rings first and then clip rings, x and y interleaved. That is
/// the same division of labour as every other message here — JSON says what, the payload carries how
/// much — and it keeps a several-thousand-vertex operand off the JSON parser.</para>
/// </summary>
public sealed class PCellWireClipRequest
{
    public string Op { get; set; } = PCellWireOp.Clip;
    public PCellWireClipRule Rule { get; set; } = PCellWireClipRule.Or;
    public List<int> Subject { get; set; } = [];
    public List<int> Clip { get; set; } = [];
}

/// <summary>One resulting region: an outer ring and the rings that are holes in it. An island inside
/// a hole is a further entry in its own right, not a nesting level here.</summary>
public sealed class PCellWireClipPolygon
{
    public int Outer { get; set; }
    public List<int> Holes { get; set; } = [];
}

/// <summary>
/// Grow (positive) or shrink (negative) a region, by <see cref="DeltaDbu"/> database units.
///
/// <para>Same shape as a clip request and for the same reason: the rings' vertex counts are here, the
/// coordinates ride in the payload. There is one operand, so there is one count list.</para>
/// </summary>
public sealed class PCellWireOffsetRequest
{
    public string Op { get; set; } = PCellWireOp.Offset;
    public long DeltaDbu { get; set; }
    public List<int> Subject { get; set; } = [];
}

/// <summary>
/// The regions an operation produced. Shared by <see cref="PCellWireOp.Clip"/> and
/// <see cref="PCellWireOp.Offset"/> — both answer with regions-and-their-holes, and giving them two
/// identical reply types would be two things to keep in step for no gain.
/// </summary>
public sealed class PCellWireRegionReply
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public List<PCellWireClipPolygon> Polygons { get; set; } = [];
}

/// <summary>
/// What a parameter's value MEANS dimensionally — the only thing that changes what crosses the wire.
///
/// <para><b>Three, not circuitRF's full <c>UnitDimension</c>, and that is deliberate.</b> Length and
/// Angle are the two that change the representation (length → int64 DBU, angle → degrees); offering
/// Resistance or Capacitance would invite a script to declare one and expect a unit-aware conversion
/// that does not exist, which is a promise the wire cannot keep.</para>
/// </summary>
public enum PCellWireDimension
{
    None,
    Length,
    Angle,
}

// ── describe ─────────────────────────────────────────────────────────────────

/// <summary>
/// One parameter a generator declares. <see cref="Dimension"/> is what makes the host's
/// metre→DBU conversion possible at all (schema §1) — the host cannot convert what it has not been
/// told is a length, which is precisely why <c>describe</c> is load-bearing rather than a handshake
/// pleasantry.
/// </summary>
public sealed class PCellWireParameterDecl
{
    public string Name { get; set; } = "";
    public PCellValueKind Kind { get; set; } = PCellValueKind.Real;
    public PCellWireDimension Dimension { get; set; } = PCellWireDimension.None;

    /// <summary>
    /// What this parameter is when nobody has said otherwise (wire version 4). Null when the
    /// generator states none.
    ///
    /// <para><b>Why the host needs it rather than just letting the script fall back.</b> A script
    /// can always substitute its own default for a parameter it was not sent — and that alone is
    /// enough to GENERATE. It is not enough to PLACE: circuitRF records a generated cell's parameter
    /// set (<c>PCellOrigin</c>) and edits it from there, so a cell placed with an empty set has
    /// nothing to show in the parameter list and nothing to edit. Declared defaults are what make a
    /// placed cell adjustable instead of frozen at whatever the script chose.</para>
    ///
    /// <para><b>Declared, never inferred.</b> A default the host guessed — zero, or the first value
    /// of a range — would be a number the generator never sanctioned, and a wrong one draws
    /// perfectly.</para>
    /// </summary>
    public PCellValue? Default { get; set; }
}

public sealed class PCellWireGeneratorDecl
{
    public string Id { get; set; } = "";
    public List<PCellWireParameterDecl> Parameters { get; set; } = [];
}

public sealed class PCellWireDescribeReply
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public int WireVersion { get; set; } = PCellWireVersion.Current;
    public int ContractVersion { get; set; } = PCellContractVersion.Current;
    public List<PCellWireGeneratorDecl> Generators { get; set; } = [];
}

// ── generate ─────────────────────────────────────────────────────────────────

/// <summary>A <see cref="LayerKey"/> on the wire. Its own type rather than the model's record struct
/// so the JSON property names are part of THIS schema and cannot drift when the model changes.</summary>
public sealed class PCellWireLayer
{
    public int Layer { get; set; }
    public int Datatype { get; set; }

    public LayerKey ToKey() => new(Layer, Datatype);
    public static PCellWireLayer From(LayerKey k) => new() { Layer = k.Layer, Datatype = k.Datatype };
}

public sealed class PCellWireLayerDef
{
    public int Layer { get; set; }
    public int Datatype { get; set; }
    public string Name { get; set; } = "";
    public string? Purpose { get; set; }
}

/// <summary>
/// The RESOLVED layer choice, never the question. circuitRF's own substrate resolution — "topmost
/// conductor, nearest ground-designated conductor beneath", plus any per-instance override — runs
/// host-side and only its answer crosses. A script re-deriving it would be a second implementation
/// of a rule whose failure is silent: geometry on a plausible but wrong layer.
/// </summary>
public sealed class PCellWireLayers
{
    public PCellWireLayer? Signal { get; set; }
    public PCellWireLayer? Ground { get; set; }
    public List<PCellWireLayerDef> Table { get; set; } = [];
}

/// <summary>One stackup entry. <see cref="Thickness"/> is DBU (schema §1) — as it already is in
/// circuitRF's own <c>StackupLayer.ThicknessDbu</c>; SI metres are a derived view for the electrical
/// models, not the stored form.</summary>
public sealed class PCellWireStackupLayer
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public long Thickness { get; set; }

    public double? Epsr { get; set; }
    public double? Tand { get; set; }
    public double? Mur { get; set; }

    /// <summary>Siemens per metre. <b>Not a length</b>, which is why it may cross as a double: the
    /// wire stays scale-free, so a script cannot turn a physical constant into a coordinate.</summary>
    public double? Sigma { get; set; }

    public bool IsGroundReference { get; set; }
    public List<PCellWireLayer> DrawingLayers { get; set; } = [];
}

public sealed class PCellWireStackup
{
    public string Top { get; set; } = "open";
    public string Bottom { get; set; } = "ground";
    public List<PCellWireStackupLayer> Layers { get; set; } = [];
}

public sealed class PCellWireGenerateRequest
{
    public string Op { get; set; } = PCellWireOp.Generate;
    public string GeneratorId { get; set; } = "";

    /// <summary>Kinded values, encoded exactly as <c>.clay</c> encodes them (schema §3). Length
    /// values are already in DBU.</summary>
    public Dictionary<string, PCellValue> Parameters { get; set; } = new(StringComparer.Ordinal);

    public PCellWireLayers Layers { get; set; } = new();

    /// <summary>Null when no technology resolved — a generator still produces geometry in that case
    /// (pcell-contract.md §2); only the ELECTRICAL stamp refuses without one.</summary>
    public PCellWireStackup? Stackup { get; set; }

    /// <summary>
    /// Database units per micrometre in the layout being drawn into (wire version 2).
    ///
    /// <para><b>This is NOT permission to convert metres, and the distinction is the whole reason
    /// version 1 withheld it.</b> Length PARAMETERS still arrive already converted, by circuitRF's
    /// single rounding rule — there are still no metres in any message, so there is still nothing for
    /// a script to convert and still one rounding rule across the boundary. What this adds is the
    /// ability to express a constant the GENERATOR itself holds: a process dimension out of a kit's
    /// own data, which is a physical length in micrometres and has no other way of becoming DBU.
    /// That case was named as a version bump in version 1's own text, and this is it.</para>
    ///
    /// <para>It is a property of the TARGET LAYOUT, not of the process — a generator asked to draw
    /// into a finer layout must be told the finer number — which is why it rides on the request
    /// rather than on the technology.</para>
    /// </summary>
    public int DbuPerMicron { get; set; }
}

// ── the reply ────────────────────────────────────────────────────────────────

/// <summary>
/// A run of coordinates in the binary payload: <paramref name="At"/> and <paramref name="Count"/>
/// are both in int64 ELEMENTS, and <paramref name="Count"/> is even (x,y pairs).
///
/// <para>Every coordinate is addressed this way and none ever appears in the JSON — which is what
/// makes "a fractional coordinate is unrepresentable" structural rather than a validation rule
/// somebody could forget to apply. There is nowhere to write one.</para>
/// </summary>
public sealed class PCellWireSpan
{
    public int At { get; set; }
    public int Count { get; set; }
}

/// <summary>One edge of an edge list, parallel to the owning shape's vertex span.</summary>
public sealed class PCellWireEdge
{
    public string Kind { get; set; } = "line";

    /// <summary>Arc only: signed tan(sweep/4) — the same quantity <c>LayoutEdge.Bulge</c> stores, so
    /// a curve crosses as a curve. A script that flattened its own would bake a tolerance into the
    /// geometry, and flattening is a RENDERING decision made at screen resolution (§3.2 R9c).</summary>
    public double Bulge { get; set; }

    /// <summary>Cubic only: the two control points, as a 4-element span (c1x, c1y, c2x, c2y).</summary>
    public PCellWireSpan? Control { get; set; }
}

/// <summary>
/// One emitted shape. A single flat type across every kind, discriminated by <see cref="Kind"/>,
/// rather than a polymorphic hierarchy: this is a frozen third-party format, and a flat object with
/// documented per-kind fields is what a script in any language can produce without a serialization
/// library that agrees with .NET about discriminators.
/// </summary>
public sealed class PCellWireShape
{
    public string Kind { get; set; } = "";
    public PCellWireLayer Layer { get; set; } = new();
    public string? Net { get; set; }

    /// <summary>poly / curve / path: the outer vertex run. rect / rrect: two corners (4 elements).
    /// circle / via / label: the single anchor point (2 elements).</summary>
    public PCellWireSpan? Xy { get; set; }

    /// <summary>poly / curve only.</summary>
    public List<PCellWireSpan>? Holes { get; set; }

    /// <summary>curve / path only. Null means every edge is a straight line.</summary>
    public List<PCellWireEdge>? Edges { get; set; }

    // Scalar lengths. In the JSON because a span each would be noise for a single value — and
    // rejected on decode if they arrive with a fractional part, which is what keeps them honest.
    public long? Width { get; set; }
    public long? Radius { get; set; }
    public long? CornerRadius { get; set; }
    public long? PadSize { get; set; }
    public long? DrillSize { get; set; }
    public long? Height { get; set; }
    public long? FlattenTol { get; set; }

    public string? End { get; set; }
    public string? Text { get; set; }
    public string? Rotation { get; set; }
    public bool? IsPort { get; set; }
    public PCellWireLayer? LandingLayer { get; set; }
}

public sealed class PCellWirePin
{
    public string Name { get; set; } = "";
    public long X { get; set; }
    public long Y { get; set; }
    public PCellWireLayer Layer { get; set; } = new();
    public long Width { get; set; }
    public double OutwardDeg { get; set; }
}

/// <summary>
/// One draggable parameter grip (wire version 6, <c>pcell-parameter-handles.md</c> §3).
///
/// <para><b>The four coordinates ride in the binary payload, as one 4-element span</b> (anchorX,
/// anchorY, x, y) — not as JSON numbers, however few of them there are. §2's rule that no coordinate
/// ever appears in the JSON is what makes a fractional coordinate structurally unrepresentable, and
/// an exception made for brevity here would cost exactly that guarantee.</para>
///
/// <para><see cref="Min"/> and <see cref="Max"/> ARE plain JSON numbers, and that is not an
/// inconsistency: they are parameter VALUES, not coordinates, and §3 already encodes a value that
/// way. A fractional bound (a minimum impedance of 20.5 Ω) is legitimate where a fractional
/// coordinate is not.</para>
/// </summary>
public sealed class PCellWireHandle
{
    public string Parameter { get; set; } = "";

    /// <summary>"linear" or "angular". A kind this host does not implement drops THAT HANDLE and is
    /// reported once — never the whole generate, and never the cell's other handles. That is what
    /// lets a further kind be added without the next bump becoming a cliff.</summary>
    public string Kind { get; set; } = "linear";

    /// <summary>anchorX, anchorY, x, y — exactly 4 elements.</summary>
    public PCellWireSpan? Span { get; set; }

    public double AxisDeg { get; set; }
    public string? Label { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>The parameter this grip drives when dragged ACROSS its own axis (R-pch-4a). Absent
    /// on an ordinary one-degree-of-freedom grip, which is the common case.</summary>
    public string? CrossParameter { get; set; }
    public string? CrossLabel { get; set; }
    public double? CrossMin { get; set; }
    public double? CrossMax { get; set; }

    /// <summary>R-pch-4b: hold this grip's ANCHOR still in world space while it is dragged. Absent
    /// reads as false — the pre-existing behaviour, so a wire-version-6 generator written before this
    /// field existed keeps working unchanged.</summary>
    public bool KeepAnchorFixed { get; set; }
}

/// <summary>The handle-kind vocabulary, in one place so the encoder and decoder cannot disagree.</summary>
public static class PCellWireHandleKind
{
    public const string Linear  = "linear";
    public const string Angular = "angular";
}

public sealed class PCellWireGenerateReply
{
    public bool Ok { get; set; } = true;

    /// <summary>Set when <see cref="Ok"/> is false. The host surfaces it NAMING THE CELL — a script
    /// can only say what went wrong, and the host is the only side that knows which instance asked.</summary>
    public string? Error { get; set; }

    public List<PCellWireShape> Shapes { get; set; } = [];
    public List<PCellWirePin> Pins { get; set; } = [];

    /// <summary>A generator that DID produce geometry and has something to say about it. Not an error
    /// channel: a non-empty list with <see cref="Ok"/> true must not be treated as failure.</summary>
    public List<string>? Diagnostics { get; set; }

    /// <summary>Optional draggable parameter grips (wire version 6). Absent — the case for every
    /// generator written before this existed — simply means the cell is not draggable.</summary>
    public List<PCellWireHandle>? Handles { get; set; }

    /// <summary>
    /// "auto" (or absent) / "deferred" — how eagerly a drag on this cell should redraw its artwork
    /// (R-pch-10). A generator that already knows it is too expensive to redraw per frame says
    /// "deferred" and is believed, saving the one full regeneration Auto spends finding out.
    ///
    /// <para>An unrecognised value reads as "auto", deliberately: the field is a performance HINT,
    /// and refusing a generate over one would trade a working cell for a preference.</para>
    /// </summary>
    public string? Preview { get; set; }
}

/// <summary>The preview-mode vocabulary, in one place so the encoder and decoder cannot disagree.</summary>
public static class PCellWirePreviewMode
{
    public const string Auto     = "auto";
    public const string Deferred = "deferred";
}

/// <summary>The shape-kind vocabulary, in one place so the encoder and decoder cannot disagree.</summary>
public static class PCellWireShapeKind
{
    public const string Rect   = "rect";
    public const string Poly   = "poly";
    public const string RRect  = "rrect";
    public const string Circle = "circle";
    public const string Curve  = "curve";
    public const string Path   = "path";
    public const string Via    = "via";
    public const string Label  = "label";

    /// <summary>
    /// <b>Bitmap is absent permanently, not pending.</b> A bitmap is a tracing underlay, not artwork —
    /// already excluded from booleans, flatten, DRC and every export (R-bmp-3). A generator emitting
    /// one would be emitting something that is not geometry, and admitting it would oblige every
    /// future consumer to know to ignore it.
    /// </summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Rect, Poly, RRect, Circle, Curve, Path, Via, Label };
}

/// <summary>Shared JSON settings. camelCase on the wire because this is a third-party-facing format
/// and every other language's convention for it is camelCase; circuitRF's own files stay PascalCase.</summary>
internal static class PCellWireJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy        = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase),
                                        new PCellValueJsonConverter() },
    };
}
