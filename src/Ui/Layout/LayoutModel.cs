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
[JsonDerivedType(typeof(BitmapShape),      "Bitmap")]
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

public sealed class LabelShape : LayoutShape
{
    public long X { get; set; }
    public long Y { get; set; }
    public string Text { get; set; } = "";
    public long Height { get; set; }
    public LayoutRotation Rotation { get; set; } = LayoutRotation.R0;
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

    /// <summary>Additive (no <c>.clay</c> <c>FormatVersion</c> bump) — a newly-placed label always
    /// defaults to Regular; edited via the Properties Inspector.</summary>
    public LabelFontStyle Style { get; set; } = LabelFontStyle.Regular;
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
    public LayoutRotation Rot { get; set; }
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
}

/// <summary>
/// docs/design/pcell-contract.md R1/R9: marks a <see cref="LayoutView"/> as PCell-generated
/// rather than hand-drawn — the editor uses this to disable editing tools with a reason
/// (flatten is the escape hatch) and to know what to re-invoke on a parameter/technology change
/// (L3b's existing invalidation seam, not a new mechanism). <see cref="GeneratorId"/> is a key
/// into <c>CircuitRF.Ui.Layout.PCells.PCellRegistry</c>; <see cref="Parameters"/> is the resolved
/// parameter snapshot (SI values) the generated geometry was last built from.
/// L5a's four built-ins are SymbolKind-registered (like TLIN/R/L/C), not on-disk cell folders, so
/// this marker — not a <c>.ccell</c> field — is what makes a <see cref="LayoutView"/> "generated"
/// for the read-only/regeneration gates in this phase; L5's own schematic→layout work is what
/// will attach this to a real placed instance's cell.
/// </summary>
public sealed record PCellOrigin(string GeneratorId, IReadOnlyDictionary<string, PCells.PCellValue> Parameters);

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
    public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;
    public long SnapDbu { get; set; }
    public AngleMode AngleMode { get; set; } = AngleMode.AnyAngle;

    /// <summary>Relative path to a .ctech.</summary>
    public string? TechRef { get; set; }

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

    public List<LayoutShape> Shapes { get; } = [];
    public List<LayoutInstance> Instances { get; } = [];

    /// <summary>The R-tree spatial index (L2b, docs/design/layout-view.md §5.2 R11) over
    /// <see cref="Shapes"/> — every query self-heals lazily (see its own doc comment), so it is always
    /// safe to read even for a <see cref="LayoutView"/> whose <see cref="Shapes"/> were populated by
    /// direct list mutation and never once ran through <see cref="NotifyChanged"/>.</summary>
    public LayoutSpatialIndex SpatialIndex { get; } = new();

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
