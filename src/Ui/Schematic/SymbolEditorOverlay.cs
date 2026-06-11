// Rendering overlay passed from SymbolEditorViewModel to SymbolEditorCanvas each frame.
// No Skia / Avalonia types — keeps the renderer re-skinnable.

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Transient interaction state for the symbol editor canvas:
/// selection bboxes, live drag offset, rubber-band rect,
/// and pin-tool selection + live drag state (4c).
/// </summary>
public sealed record class SymbolEditorOverlay
{
    public static readonly SymbolEditorOverlay Empty = new();

    // ── Primitive selection / drag ────────────────────────────────────────────

    /// <summary>Indices into EditableSymbol.Primitives that are currently selected.</summary>
    public IReadOnlySet<int> SelectedIndices { get; init; } = s_emptyInts;

    /// <summary>
    /// Live drag delta in symbol-local units (snapped to p=5).
    /// Non-zero only while a primitive move-drag is in progress.
    /// </summary>
    public (double Dx, double Dy) LiveDragOffset { get; init; }

    /// <summary>
    /// Rubber-band rectangle in symbol-local coordinates while rubber-band-selecting,
    /// or null when not active.
    /// </summary>
    public (double X0, double Y0, double X1, double Y1)? RubberBand { get; init; }

    /// <summary>
    /// The primitive currently being drawn (in-progress), or null when no draw is active.
    /// Rendered as a ghost/preview via DrawSymbol before the user commits the placement.
    /// </summary>
    public SymbolPrimitive? InProgressPrimitive { get; init; }

    // ── Pin tool state ────────────────────────────────────────────────────────

    /// <summary>Indices into EditableSymbol.Pins that are currently selected (any count).</summary>
    public IReadOnlySet<int> SelectedPinIndices { get; init; } = s_emptyInts;

    /// <summary>
    /// Returns the single selected pin's index, or -1 when zero or more than one pin is selected.
    /// Used by the inspector and per-pin operations that require exactly one pin.
    /// </summary>
    public int SelectedPinIndex => SelectedPinIndices.Count == 1 ? SelectedPinIndices.First() : -1;

    /// <summary>
    /// Live drag delta for the selected pin (symbol-local, snapped to P=100).
    /// Non-zero only while a pin drag is in progress.
    /// </summary>
    public (double Dx, double Dy) PinLiveDragOffset { get; init; }

    /// <summary>
    /// Port indices (0-based) that currently have no pin assigned.
    /// Displayed informally so the author knows which ports are "open".
    /// Never an error — unmapped = open circuit (§3).
    /// </summary>
    public IReadOnlyList<int> UnmappedPortIndices { get; init; } = s_emptyPorts;

    // ── Resize gripper (single-selection only) ────────────────────────────────

    /// <summary>
    /// Position of the resize gripper handle (bottom-right of the selected primitive's bbox),
    /// in symbol-local coordinates.  Non-null only when exactly one resizable primitive is selected.
    /// </summary>
    public (double X, double Y)? ResizeHandle { get; init; }

    /// <summary>
    /// Live resize preview bbox in symbol-local coordinates.
    /// Non-null only while a resize drag is in progress.
    /// </summary>
    public (double X0, double Y0, double X1, double Y1)? ResizePreviewBb { get; init; }

    // ── Statics ───────────────────────────────────────────────────────────────

    private static readonly HashSet<int> s_emptyInts  = [];
    private static readonly List<int>    s_emptyPorts = [];
}
