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

    private static readonly HashSet<string> HashSetEmpty = new();
}

/// <summary>A component "ghost" shown during drag-placement.</summary>
public sealed record PlacementGhost(
    double        X,
    double        Y,
    SymbolKind    Symbol,
    SymbolRotation Rotation,
    bool          MirrorX,
    int           PortCount = 2);
