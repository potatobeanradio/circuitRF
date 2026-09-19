using System.Numerics;
using CircuitRF.Render.DataDisplay;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.Controls;

/// <summary>
/// Transient chrome drawn over a <see cref="PlotControl"/>'s scene, with a gesture of its own —
/// the Smith Chart tool's grippers, and brief 9's grab handle for its constant-Q arcs
/// (<c>brief-smith-5-chart.md</c> <c>R-smith5-6</c>, <c>docs/design/smith-chart.md</c> §5.4).
/// </summary>
/// <remarks>
/// <b>It is the shape <see cref="PlotControl"/> already uses, not a new control-extension
/// mechanism.</b> The control exposes four <c>Func&lt;&gt;</c> hooks a host fills in
/// (<c>NextMarkerIndexProvider</c>, <c>FindMarkerInfoBoxVmProvider</c>, <c>ContainerProvider</c>,
/// <c>SelectedMarkersProvider</c>); this is a fifth seam of the same kind — a draw callback in
/// canvas space, a hit test that returns a handle, and press / move / release — and it is
/// deliberately the same seam <c>ILayoutCanvasOverlay</c> gives the layout canvas to wBond and
/// railRF.
///
/// <para><b>Four rules, each of which is a defect somewhere else in this repository.</b></para>
///
/// <para><b>1. The canvas is an argument, never an assumption.</b> <c>ContourRenderer</c> once drew
/// every contour on every Smith plot to the first target it had been handed, because the target was
/// remembered rather than passed. Nothing here may cache a canvas, a surface or a transform between
/// calls.</para>
///
/// <para><b>2. The overlay never mutates the <c>Plot</c>.</b> It draws transient chrome and calls
/// back into whatever owns the model; that owner rebuilds the plot. This is
/// <c>ILayoutCanvasOverlay</c>'s own rule and it is why that seam has held — an overlay that edited
/// the scene it is drawn over would be a second, invisible author of the picture.</para>
///
/// <para><b>3. Hit-test order is overlay first, then the control's own markers.</b> A gripper under
/// a marker is unreachable otherwise, and the marker is the thing the user can move out of the
/// way.</para>
///
/// <para><b>4. Null means "not mine".</b> <see cref="HitTest"/> returning null leaves the press to
/// the control, so pan, zoom and marker drag are unaffected wherever no handle is under the cursor.
/// With <c>PlotControl.Overlay</c> null the control behaves exactly as it did before this seam
/// existed.</para>
/// </remarks>
public interface IPlotOverlay
{
    /// <summary>
    /// Draws the overlay, inside the same Skia lease and the same viewport clip as the traces
    /// underneath — <b>above the trajectories and below the markers</b>, which is harmonicaRF's own
    /// z-order rule and the reason the hook is inside <c>PlotRenderer.Draw</c> rather than after it.
    /// </summary>
    /// <param name="tf">The transform the frame beneath was drawn with, handed down rather than
    /// rebuilt, so the chrome cannot land a pixel away from the curve it belongs to.</param>
    /// <param name="theme">The same theme object the plot underneath was just drawn with.</param>
    void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme);

    /// <summary>
    /// The handle under a canvas-space point, or <b>null when the overlay does not want the
    /// event</b> — see rule 4. The returned object is opaque to the control and is handed straight
    /// back to <see cref="DragBegin"/>.
    /// </summary>
    object? HitTest(double canvasX, double canvasY, TransformSet tf);

    /// <summary>A press landed on <paramref name="handle"/>. The before-state for the gesture's
    /// single undo entry is captured here (<c>R-smith5-8</c>).</summary>
    void DragBegin(object handle);

    /// <summary>The pointer moved, in the plot's own world coordinates — Γ on a Smith plot.</summary>
    void DragTo(Complex gammaWorld);

    /// <summary>
    /// The same, with the shift modifier as the pointer event that carried the move reported it —
    /// the constant-Q arcs' quarter-step (<c>brief-smith-9-q-and-sweep.md</c> <c>R-smith9-2</c>).
    /// </summary>
    /// <remarks>
    /// <b>READ FROM THE EVENT, never latched.</b> Brief 9 asks for the modifier latch to be released
    /// on <c>LostFocus</c>, after the layout view's "marquee select stopped working" defect — a held
    /// -key flag that was never cleared because the key-up went to whatever took focus
    /// (<c>src/Ui/RESOLVED.md</c>). <b>There is no latch here to release</b>: the flag arrives with
    /// the move it applies to, so it cannot outlive it, cannot be missed on the way up and cannot be
    /// left set by a window change. That is the stronger form of the same guarantee, and it is why
    /// this is a parameter rather than a property.
    ///
    /// <para>Defaulted to the unmodified call, so an overlay with no modifier of its own says
    /// nothing and <c>PlotControl</c> has one call site rather than two.</para>
    /// </remarks>
    void DragTo(Complex gammaWorld, bool shift) => DragTo(gammaWorld);

    /// <summary>
    /// The gesture ended. <paramref name="cancelled"/> true is an Escape mid-drag: the before-state
    /// is restored and <b>nothing is pushed</b>.
    /// </summary>
    void DragEnd(bool cancelled);

    /// <summary>
    /// The handle the pointer is hovering, or null. Defaulted to a no-op so an overlay with no hover
    /// state of its own says nothing; the Smith grippers brighten on it (§5.4's <i>subtle</i>).
    /// </summary>
    /// <returns>True when the overlay's appearance changed and the control should redraw.</returns>
    bool Hover(object? handle) => false;
}
