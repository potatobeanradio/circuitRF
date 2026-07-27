// Rendering overlay passed from LayoutEditorViewModel to LayoutCanvas/LayoutRenderer each frame.
// No Skia / Avalonia types — keeps the renderer re-skinnable (mirrors SymbolEditorOverlay's role).

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
}
