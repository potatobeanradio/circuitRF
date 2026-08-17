using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Something drawn on top of, and given first refusal on input to, a <see cref="LayoutCanvas"/>
/// without being part of the layout it is showing.
///
/// <h3>Why a seam and not a second control on top</h3>
/// <para>A transparent sibling control layered over the canvas would swallow every pointer event it
/// did not want: an unhandled event bubbles to ANCESTORS, never sideways to a sibling underneath, so
/// pan, zoom, marquee and every layout tool would have to be forwarded by hand. A seam inverts that —
/// the overlay is asked first and says whether it consumed the gesture; anything it declines reaches
/// the layout editor's own state machine untouched.</para>
///
/// <h3>What an implementation must not do</h3>
/// <para><b>Never touch the layout model.</b> The overlay's whole justification (wbond.md WB23/WB17)
/// is that its own edits cost the layout nothing: no shape enters <c>.clay</c>, and a drag must not
/// invalidate <see cref="LayoutPathCache"/> — which is exactly what mutating <c>LayoutView.Shapes</c>
/// would do, turning a cheap overlay repaint into a 500k-shape rebuild. Redraw by calling
/// <see cref="LayoutCanvas.InvalidateOverlay"/>, which repaints without disturbing the cache.</para>
///
/// <para>Coordinates are the canvas's own world units — the layout's <b>database units</b>, not
/// nanometres and not microns. An overlay that stores its geometry in some other unit converts at
/// this boundary (see <c>WBondSnap</c> for the one that does).</para>
/// </summary>
public interface ILayoutCanvasOverlay
{
    /// <summary>
    /// Draws the overlay, after the layout itself and inside the same Skia lease.
    /// </summary>
    /// <param name="theme">
    /// The SAME theme object the layout underneath was just drawn with. Handed down rather than
    /// re-derived so shared visual language — the selection accent above all — cannot drift between
    /// the layout and whatever is drawn over it.
    /// </param>
    void Draw(SKCanvas canvas, LayoutViewport viewport, LayoutRenderTheme theme);

    /// <summary>
    /// The overlay's own extent in the canvas's world units (the layout's DBU), or
    /// <see cref="Bbox.Empty"/> when it has nothing to show.
    ///
    /// <para><b>Zoom to Fit has to include this or it frames the wrong thing.</b> The canvas fits the
    /// union of the layout's shapes and instances; an overlay's content is in neither, so a wBond
    /// document on an empty scratch layout fitted to an EMPTY extent and landed at an arbitrary
    /// default — with every wire off screen, which is exactly what the owner saw.</para>
    /// </summary>
    Bbox ContentBounds();

    /// <summary>Returns true when the press was consumed and must not reach the layout editor.</summary>
    bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount);

    /// <summary>
    /// Returns true when the move was consumed (a drag, marquee or creation gesture the overlay owns
    /// is in progress). <paramref name="modifiers"/> is live rather than captured at press because a
    /// constraint the user applies MID-gesture — Shift for ortho while placing a wire — has to take
    /// effect while they are still moving the pointer.
    /// </summary>
    bool OnPointerMoved(long worldX, long worldY, long tolDbu, bool leftButtonDown, KeyModifiers modifiers);

    /// <summary>Returns true when the release was consumed.</summary>
    bool OnPointerReleased(long worldX, long worldY);

    /// <summary>Returns true when the key was consumed.</summary>
    bool OnKeyDown(Key key, KeyModifiers modifiers);

    /// <summary>
    /// Key release. Exists because a HELD key can be a modifier in its own right — wbond.md §6.3's
    /// hold-<c>g</c> (promote a click to the whole array) is exactly that, and a modifier that is set
    /// on press and never cleared is worse than one that was never offered. Return value is advisory;
    /// a release is never "consumed" the way a press is.
    /// </summary>
    void OnKeyUp(Key key, KeyModifiers modifiers);

    /// <summary>
    /// Keyboard focus has left the canvas: drop every HELD-key latch, whether or not its release was
    /// ever seen.
    ///
    /// <para><b>A key-up is not guaranteed to arrive.</b> Press a promotion key over the canvas, then
    /// click a toolbar button or a combo before letting go, and the release is delivered to whatever
    /// took focus — never here. The latch then stays set for the rest of the session, silently
    /// changing what every later click means, with nothing on screen saying why. That is not a
    /// hypothetical: the same shape on <c>LayoutCanvas</c>'s own space-to-pan latch turns every
    /// subsequent left-drag into a pan, which reads exactly as "marquee select stopped working".</para>
    ///
    /// <para>Defaulted, so an overlay that latches nothing needs no code.</para>
    /// </summary>
    void OnFocusLost() { }
}
