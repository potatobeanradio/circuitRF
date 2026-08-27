// Rendering overlay passed from LayoutEditorViewModel to LayoutCanvas/LayoutRenderer each frame.
// No Skia / Avalonia types — keeps the renderer re-skinnable (mirrors SymbolEditorOverlay's role).

using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Layout;

/// <summary>A world-space (DBU) rectangle for the marquee-select gesture. Not normalized — direction
/// (left-to-right vs right-to-left) is meaningful (enclose vs. crossing, §6.2).</summary>
public readonly record struct LayoutMarquee(long X1, long Y1, long X2, long Y2)
{
    public bool IsLeftToRight => X2 >= X1;
}

/// <summary>
/// Transient interaction state for the layout canvas. L1b carried only the in-progress draw ghost;
/// L1c adds selection, the live marquee rectangle, and a live move-drag preview (a per-index override
/// shape rendered translated in place of the original, so a drag never mutates the model until it
/// commits as one <c>MoveShapesCommand</c> — docs/design/layout-view.md §6.2/R-L1c-3).
/// </summary>
public sealed record class LayoutOverlay
{
    public static readonly LayoutOverlay Empty = new();

    /// <summary>The shape currently being drawn (in-progress), or null when no draw is active.
    /// Rendered by <c>LayoutRenderer</c> above every committed layer, in its own layer's color,
    /// with a dashed outline so it reads as provisional.</summary>
    public LayoutShape? InProgressPrimitive { get; init; }

    /// <summary>Currently selected shape indices — rendered with an accent outline above every
    /// layer. Empty when nothing is selected.</summary>
    public IReadOnlyList<int> SelectedIndices { get; init; } = [];

    /// <summary>L1h (R-L1h-5): true when the selection's bbox scale handles should render INSTEAD of
    /// L1d's single-shape vertex/edge/bulge handles — always true for a 2+ selection, or for a single
    /// selection with Scale mode toggled on.</summary>
    public bool ShowScaleHandles { get; init; }

    /// <summary>The live marquee rectangle while dragging on empty canvas with the Select tool, or
    /// null when no marquee is in progress.</summary>
    public LayoutMarquee? Marquee { get; init; }

    /// <summary>Live move-drag preview: shape index -&gt; a translated clone to render in that
    /// shape's place. The underlying model is untouched until the drag commits. Empty when no move
    /// is in progress.</summary>
    public IReadOnlyDictionary<int, LayoutShape> DragOverrides { get; init; } =
        new Dictionary<int, LayoutShape>();

    /// <summary>Live paste-ghost preview (L1f, docs/sonnet-briefs/brief-L1f-clipboard.md §3):
    /// the fragment's shapes, already translated to the current (snapped) cursor position, or null
    /// when no paste placement is in progress. Unlike <see cref="DragOverrides"/> these shapes have
    /// no index into <c>LayoutView.Shapes</c> yet — nothing is in the model until the placement
    /// commits.</summary>
    public IReadOnlyList<LayoutShape>? PastePreview { get; init; }

    /// <summary>
    /// One live ghost per INSTANCE in the fragment being pasted, already translated to the current
    /// (snapped) cursor position.
    ///
    /// <para><b>Owner report, 2026-08-09: "the ports are rendered live when moving the mouse, but my
    /// MLIN object is not."</b> L1f shipped the paste ghost as shapes-only and said so — an instance
    /// travelled with the paste and committed correctly, it just was not in the picture the user was
    /// aiming with. Which meant aiming a schematic-generated selection, whose metal is ALL instances,
    /// was aiming at two port glyphs and empty space.</para>
    ///
    /// <para><see cref="GhostInstance.BoxOnly"/> is the owner's own escape hatch — "if the geometry
    /// is too complicated for live rendering, then just render a box" — decided ONCE when the
    /// placement is armed, never per pointer move, so the ghost cannot flicker between the two
    /// treatments as the cursor moves.</para>
    /// </summary>
    public IReadOnlyList<GhostInstance>? PastePreviewInstances { get; init; }

    /// <summary>A pasted instance's live ghost: what to place, its array-expanded extent, and whether
    /// it is cheap enough to draw as real geometry.</summary>
    public readonly record struct GhostInstance(LayoutInstance Instance, Bbox Bbox, bool BoxOnly);

    // ── L3a additions (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md) ────────────────────────

    /// <summary>Currently selected INSTANCE indices — rendered with an accent outline, same visual
    /// treatment as <see cref="SelectedIndices"/> but a separate list (L3a keeps shape and instance
    /// selection mutually exclusive — see <c>LayoutEditorViewModel.Instances.cs</c>'s header).</summary>
    public IReadOnlyList<int> SelectedInstanceIndices { get; init; } = [];

    /// <summary>Live instance move-drag preview — instance index -&gt; a translated clone, mirroring
    /// <see cref="DragOverrides"/> exactly for instances.</summary>
    public IReadOnlyDictionary<int, LayoutInstance> InstanceDragOverrides { get; init; } =
        new Dictionary<int, LayoutInstance>();

    /// <summary>The Instance-place tool's live ghost (docs/sonnet-briefs/brief-L3a-instances-and-
    /// arrays.md §6 — "a live ghost following the cursor, snapped, click to place, Escape to cancel,"
    /// reusing L1f's paste-placement gesture vocabulary). A deliberately simplified placeholder-box
    /// ghost (not the resolved sub-cell's real geometry) — see the renderer's own doc comment for why.</summary>
    public (LayoutInstance Instance, Bbox Bbox)? PendingInstancePlacement { get; init; }

    /// <summary>L5, R-L5-7: the palette→layout PCell drag's live ghost — the generator's REAL output
    /// (a throwaway <see cref="LayoutView"/> wrapping <see cref="PCellResult.Shapes"/>, kept as the
    /// SAME reference across pointer-move ticks within one drag so <c>LayoutRenderer</c>'s
    /// reference-keyed compiled-cell cache actually hits) at the current (already-snapped) drag point.
    /// Distinct from <see cref="PendingInstancePlacement"/> — there is no on-disk cell to resolve yet
    /// (R-L5-7: generated once, cached, never written to disk until the drop actually commits).</summary>
    public (LayoutView GhostView, long X, long Y)? PendingPCellPlacement { get; init; }

    /// <summary>Geometry snap's top-priority candidate at the current cursor position (docs/sonnet-
    /// briefs/brief-snap-distance-and-geometry-snap.md §2.5) — null whenever no candidate is within
    /// tolerance, geometry snap is off, or the Alt modifier is suppressing it. Rendered as a fixed
    /// screen-space glyph colored by <see cref="SnapCandidate.Layer"/> (R-snp-4); only ever the single
    /// highest-priority candidate is shown, never the whole coincident stack (R-snp-9's cycling is a
    /// click-time concern, not a rendering one).</summary>
    public SnapCandidate? SnapMarker { get; init; }

    // ── Parameter handles (docs/design/pcell-parameter-handles.md) ──────────────────────────────

    /// <summary>
    /// The draggable parameter grips of the single selected PCell instance, already transformed into
    /// world DBU. Empty whenever the selection is not exactly one PCell-backed instance, or its
    /// generator declares none.
    ///
    /// <para><b>These are not L1d handles and must not look like them.</b> An L1d handle edits
    /// geometry; one of these edits a PARAMETER, and the artwork is regenerated around it. A user who
    /// confuses the two is surprised in a way that is hard to undo, so the renderer draws them in
    /// their own role with an axis hint showing which way each one travels.</para>
    /// </summary>
    public IReadOnlyList<PCellHandleMarker> PCellHandles { get; init; } = [];

    /// <summary>
    /// Live parameter-handle drag preview: the regenerated artwork to draw IN PLACE OF the named
    /// instance's own resolved cell, for the duration of the drag. Null when no drag is in progress
    /// or when the drag is running in deferred mode (R-pch-10) and the pre-drag artwork stands.
    ///
    /// <para>The model — and the generated cell on disk — is untouched until the drag commits, which
    /// is the same rule <see cref="DragOverrides"/> already follows for a shape move.</para>
    /// </summary>
    public (int InstanceIndex, LayoutView GhostView)? PCellHandlePreview { get; init; }

    /// <summary>
    /// L5b (docs/design/layout-view.md §9A.1): the DRC violation regions to draw over the artwork.
    /// A SYSTEM LAYER in the design doc's sense — superimposed on the geometry, never part of it: no
    /// <c>LayerKey</c>, never in <c>LayoutView.Shapes</c>, never reachable by an exporter, and never
    /// counted in <c>LayoutFrameCounters</c>. Empty when no check has run, when markers are toggled
    /// off, or when an edit has invalidated the last result.
    /// </summary>
    public IReadOnlyList<DrcMarker> DrcMarkers { get; init; } = [];
}

/// <summary>
/// One parameter grip, ready to draw — world DBU, with its travel direction already expressed in
/// world terms so the renderer never has to know what an instance transform is.
/// </summary>
/// <param name="X">Where the grip is.</param>
/// <param name="AnchorX">The fixed point it measures from — the axis hint is drawn between the two.</param>
/// <param name="AxisDx">Unit travel direction in world space. Already carries the instance's own
/// rotation and mirror, so a mirrored cell's grip hints the direction it will actually move.</param>
/// <param name="Label">What the readout calls it — the generator's own label, else the parameter name.</param>
/// <param name="Active">True for the grip currently being dragged.</param>
/// <param name="HasCrossAxis">True when this grip also drives a parameter ACROSS its axis (R-pch-4a)
/// — the renderer hints both directions so the second one is visible rather than discovered.</param>
/// <param name="IsAngular">The grip SWINGS about its anchor rather than sliding. The hint is drawn as
/// an arc through the grip rather than a straight line, because a straight tangent would read as
/// "drag this way and keep going" — which is exactly what an angular grip does not do.</param>
/// <param name="Hovered">R-pch-12: the pointer is within this grip's own grab radius RIGHT NOW, so a
/// press here edits this parameter rather than moving the instance. Drawn emphasised — the boundary
/// between the two gestures was otherwise a four-pixel disc with nothing on screen marking it.</param>
/// <param name="Armed">R-pch-12: grip-lock is engaged (Alt held over a selection that has grips), so
/// every grip is currently a large target and the instance cannot be moved at all. Drawn on EVERY
/// grip, not just the hovered one, because what the user needs to see is that the mode is on.</param>
public readonly record struct PCellHandleMarker(
    long   X,
    long   Y,
    long   AnchorX,
    long   AnchorY,
    double AxisDx,
    double AxisDy,
    string Label,
    bool   Active,
    bool   HasCrossAxis = false,
    bool   IsAngular = false,
    bool   Hovered = false,
    bool   Armed = false);

/// <summary>
/// What the pointer should look like over a parameter grip — a compass orientation, resolved by the
/// view model from the grip's own travel axis and mapped to a platform cursor by the canvas.
///
/// <para>Deliberately an orientation rather than an Avalonia <c>StandardCursorType</c>: which
/// platform cursor best says "this slides east-west" is the canvas's judgement, and every other
/// overlay type in this file is likewise a description of what to draw, never a drawing primitive.</para>
/// </summary>
public enum PCellGripCursor
{
    /// <summary>Not over a grip — the canvas keeps whatever cursor the active tool asks for.</summary>
    None,
    EastWest,
    NorthSouth,
    NorthEastSouthWest,
    NorthWestSouthEast,

    /// <summary>Travels in more than one direction: a two-axis grip (R-pch-4a) or an angular one.</summary>
    All,
}

/// <summary>
/// One violation's region, ready to draw. Deliberately a flat render-facing record rather than the
/// <c>DrcViolation</c> itself — the renderer has no business knowing what a rule is, and the panel
/// has no business knowing what a colour is.
/// </summary>
/// <param name="Rings">Flat, implicitly-closed DBU vertex lists, in world coordinates.</param>
/// <param name="Severity">Drives the marker colour.</param>
/// <param name="Waived">A waived violation still draws (§9A.1: waivers must stay VISIBLE), muted.</param>
/// <param name="Selected">The row the violations panel currently has selected.</param>
public readonly record struct DrcMarker(
    IReadOnlyList<long[]> Rings,
    DrcSeverity           Severity,
    bool                  Waived,
    bool                  Selected);
