// Rendering overlay passed from LayoutEditorViewModel to LayoutCanvas/LayoutRenderer each frame.
// No Skia / Avalonia types — keeps the renderer re-skinnable (mirrors SymbolEditorOverlay's role).

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Transient interaction state for the layout canvas. L1b carries only the in-progress draw ghost —
/// no selection/handles yet (that's L1c).
/// </summary>
public sealed record class LayoutOverlay
{
    public static readonly LayoutOverlay Empty = new();

    /// <summary>The shape currently being drawn (in-progress), or null when no draw is active.
    /// Rendered by <c>LayoutRenderer</c> above every committed layer, in its own layer's color,
    /// with a dashed outline so it reads as provisional.</summary>
    public LayoutShape? InProgressPrimitive { get; init; }
}
