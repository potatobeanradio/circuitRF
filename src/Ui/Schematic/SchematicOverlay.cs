namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Rendering overlay passed from SchematicViewModel to SchematicRenderer each frame.
/// Carries transient interaction state: selection affordances, live wire preview,
/// placement ghost, rubber-band rect, inline-edit highlight.
/// No Avalonia types — keeps renderer re-skinnable.
/// </summary>
public sealed record class SchematicOverlay
{
    public static readonly SchematicOverlay Empty = new();

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>IDs of selected components (draws selection outline + handles).</summary>
    public IReadOnlySet<string> SelectedComponentIds { get; init; } = HashSetEmpty;
    /// <summary>IDs of selected wires (whole-wire — rubber-band or endpoint click).</summary>
    public IReadOnlySet<string> SelectedWireIds      { get; init; } = HashSetEmpty;
    /// <summary>IDs of selected canvas objects.</summary>
    public IReadOnlySet<string> SelectedCanvasObjIds { get; init; } = HashSetEmpty;
    /// <summary>
    /// Selected wire segments from per-segment clicks.
    /// Empty when no segments are selected. Renderer highlights each listed segment.
    /// </summary>
    public IReadOnlySet<(string WireId, int SegmentIndex)> SelectedWireSegments { get; init; } = EmptySegments;

    internal static readonly HashSet<(string WireId, int SegmentIndex)> EmptySegments = new();

    // ── Rubber-band select ────────────────────────────────────────────────────

    /// <summary>World-space rubber-band rect, or null when not dragging.</summary>
    public (double X, double Y, double W, double H)? RubberBand { get; init; }

    /// <summary>True when the rubber-band was started right-to-left (crossing select). Renders as dashed outline.</summary>
    public bool RubberBandCrossing { get; init; }

    // ── Wire drawing preview ──────────────────────────────────────────────────

    /// <summary>Points in world coords of the wire currently being drawn, or null.</summary>
    public IReadOnlyList<(double X, double Y)>? WirePreview { get; init; }

    // ── Placement ghost ───────────────────────────────────────────────────────

    /// <summary>Component being placed (follows cursor), or null when not placing.</summary>
    public PlacementGhost? Ghost { get; init; }

    // ── Drag position overrides (live drag — bypasses full model rebuild) ──────

    /// <summary>
    /// Per-component world position during an active drag, keyed by component Id.
    /// Non-null only while a drag is in progress. The renderer uses these positions
    /// instead of SchematicModel's (stale) positions for moved components.
    /// </summary>
    public IReadOnlyDictionary<string, (double X, double Y)>? ComponentDragPositions { get; init; }

    /// <summary>
    /// Per-wire point list during an active drag (selected wires + follow-wires),
    /// keyed by wire Id. Non-null only while a drag is in progress.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(double X, double Y)>>? WireDragPoints { get; init; }

    /// <summary>
    /// Per-net-label world draw position during an active drag, keyed by label Id. Computed from the
    /// drag's live wire points (WireDragPoints) so anchored labels track their wire instead of lagging
    /// at their pre-drag spot. Non-null only while a drag that moves a labeled wire is in progress.
    /// </summary>
    public IReadOnlyDictionary<string, (double X, double Y)>? NetLabelDragPositions { get; init; }

    /// <summary>
    /// Synthetic wire routes drawn as live preview during a pin-on-pin separation drag.
    /// These wires do not exist in the model yet — they will be committed as real wires on
    /// drag-end. Non-null only while such a drag is in progress.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>>? PinOnPinPreviewWires { get; init; }

    /// <summary>
    /// Live connection dots during an active drag, recomputed from the moving geometry. When
    /// non-null the renderer draws these INSTEAD of SchematicModel.ConnectionDots, so junction
    /// dots (T-junctions, crossings) follow the drag instead of lagging at their pre-drag spots.
    /// Null when not dragging (or when the schematic is too large to recompute per tick).
    /// </summary>
    public IReadOnlyList<SchematicDot>? ConnectionDotsOverride { get; init; }

    // ── Move-Labels drag ──────────────────────────────────────────────────────

    /// <summary>
    /// Per-component (DX,DY) applied to ALL labels during an active Move-Labels drag.
    /// Keyed by component Id. Non-null only while a Move-Labels drag is in progress.
    /// </summary>
    public IReadOnlyDictionary<string, (double DX, double DY)>? LabelDragOffsets { get; init; }

    // ── Canvas-object drag / resize overrides ─────────────────────────────────

    /// <summary>
    /// Per-canvas-object live position and size during an active drag or resize, keyed by object Id.
    /// Values are (TopLeftX, TopLeftY, Width, Height) in world coords — same convention as SchematicBitmap.
    /// Non-null only while a drag or resize is in progress. DrawBitmaps reads these instead of the
    /// stale RenderModel values so bitmaps track the cursor live without a full BuildRenderModel per tick.
    /// </summary>
    public IReadOnlyDictionary<string, (double X, double Y, double W, double H)>? CanvasObjectDragPositions { get; init; }

    /// <summary>
    /// World position of the resize gripper handle (bottom-right corner) for the single selected bitmap.
    /// Non-null only when exactly one bitmap canvas object is selected and the Select tool is active.
    /// </summary>
    public (double X, double Y)? CanvasObjectGripperPos { get; init; }

    // ── R-dup-1: the Alt duplicate drag (docs/design/pcell-parameter-handles.md's sibling rule for
    //    the schematic; the layout editor's own half rides its paste-ghost channel) ────────────────

    /// <summary>
    /// The copy a duplicate drag is about to make, as ghost symbols at the dragged offset. Null when
    /// no duplicate drag is in flight.
    ///
    /// <para><b>Deliberately NOT <see cref="ComponentDragPositions"/>.</b> That channel means "draw
    /// this existing object somewhere else", which is the one thing a duplicate must not do — the
    /// original has to stay visibly put. These ghosts are extra geometry that is in no model yet,
    /// which is exactly what an uncommitted copy is, and they are drawn in the same ghost paint the
    /// placement ghost already uses so the two read as the same kind of "not yet real".</para>
    /// </summary>
    public IReadOnlyList<PlacementGhost>? DuplicateGhosts { get; init; }

    /// <summary>The wire half of that copy: world polylines, already offset.</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>>? DuplicateGhostWires { get; init; }

    /// <summary>The canvas-object half: top-left-anchored rectangles, already offset. Drawn as
    /// outlines — a bitmap ghost that painted its own image would be indistinguishable from the
    /// committed copy, which is the one thing the ghost has to say it is not.</summary>
    public IReadOnlyList<(double X, double Y, double W, double H)>? DuplicateGhostRects { get; init; }

    private static readonly HashSet<string> HashSetEmpty = new();
}

/// <summary>A component "ghost" shown during drag-placement.</summary>
public sealed record PlacementGhost(
    double        X,
    double        Y,
    SymbolKind    Symbol,
    SymbolRotation Rotation,
    bool          MirrorX,
    int           PortCount = 2,
    /// <summary>Non-null when dragging a resolved cell: draws the real symbol primitives instead of the Generic box.</summary>
    IReadOnlyList<SymbolPrimitive>? ResolvedPrimitives = null,
    /// <summary>Non-null when dragging a resolved cell: uses the real pins for port markers.</summary>
    IReadOnlyList<SymbolPin>?      ResolvedPins        = null);
