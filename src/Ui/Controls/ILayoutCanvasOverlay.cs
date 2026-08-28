using System.Collections.Generic;
using Avalonia;
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

    /// <summary>
    /// True when the gesture the overlay currently has in flight will produce a COPY rather than move
    /// what it grabbed — so the host can show the copy cursor, exactly as it already does for the
    /// layout editor's own R-dup-1 duplicate drag.
    ///
    /// <para>Defaulted to false: an overlay with no copy gesture of its own says nothing and the host
    /// falls through to the layout editor's answer.</para>
    /// </summary>
    bool DuplicateDragArmed => false;

    // ── The COMPANION move (owner, 2026-08-27) ───────────────────────────────────────────────────
    //
    // An overlay holds a selection of its own beside the layout editor's, and §6.3 of wbond.md makes
    // holding both at once the point: "select the pads and the wires landing on them" is one gesture.
    // Dragging that selection moved only the half that owned the press.
    //
    // The HOST mediates, exactly as it already does for the companion marquee, because it is the one
    // object that holds both halves. Whichever half owns the press drives; the host pushes that
    // half's OWN delta into the other. One delta from one snap decision — re-deriving it on the far
    // side is how the two halves of a selection end up a step apart.

    /// <summary>The live delta of a drag this overlay OWNS, in the host's world units (DBU), absolute
    /// from its own press — or null when it is not driving one. The host pushes this into the layout
    /// editor so what the layout has selected comes along.</summary>
    (long Dx, long Dy)? CompanionDragDelta => null;

    /// <summary>Whether the most recent press RESOLVED a new selection in this overlay rather than
    /// picking up the one that was already there — the host reads it to decide whether the LAYOUT's
    /// own selection should travel with this drag.</summary>
    bool LastPressResolvedNewSelection => true;

    /// <summary>Set by the host before <see cref="BeginCompanionMove"/>: whether the press arming this
    /// companion resolved a new selection in the half that owns it. A companion refuses when it did —
    /// a plain click means "just the thing I clicked", on both sides of the seam.</summary>
    bool CompanionPressResolvedNewSelection { get; set; }

    /// <summary>Arms a move of this overlay's own selection, driven by a gesture the HOST owns. No-op
    /// when the overlay has nothing selected.</summary>
    void BeginCompanionMove() { }

    /// <summary>The host's own delta, in DBU, absolute from its press. Applied verbatim — it is
    /// already the snapped answer that half committed to.</summary>
    void CompanionMoveTo(long dxDbu, long dyDbu) { }

    /// <summary>Commits the companion move as one edit of this overlay's own.</summary>
    void CommitCompanionMove() { }

    /// <summary>Abandons it with nothing committed.</summary>
    void CancelCompanionMove() { }

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

    /// <summary>
    /// The overlay's own right-click items, prepended to the canvas's (WB39a).
    ///
    /// <para><b>This exists because the canvas is now SHARED and its context menu is built once.</b>
    /// The wBond editor hosts <c>LayoutEditorView</c> rather than transcribing it, so there is exactly
    /// one <c>ContextMenu</c> over the layout canvas and exactly one <c>Opening</c> handler — and the
    /// wire commands (Select All Wires, Group Wires As…, Delete Vertex/Segment/Wire) have to reach it
    /// from here rather than from a second menu the host declares. It is also what gives a wirebond
    /// CELL (WB40) its wire menu in the ordinary Layout Editor, with no wBond-specific code in that
    /// view at all.</para>
    ///
    /// <para><paramref name="host"/> is the canvas itself, offered only so an item that opens a dialog
    /// can resolve the owning window (<c>TopLevel.GetTopLevel</c>). <paramref name="tolDbu"/> is the
    /// canvas's own hit tolerance at the current zoom, handed down rather than re-derived so the menu
    /// cannot offer to delete a vertex the click did not land on.</para>
    ///
    /// <para>Defaulted to nothing, so an overlay with no menu of its own needs no code.</para>
    /// </summary>
    IReadOnlyList<object> BuildContextMenuItems(
        double worldX, double worldY, long tolDbu, LayoutEditorViewModel? layout, Visual host) => [];

    /// <summary>
    /// The overlay's OWN snap answer for the gesture it is currently running, or null when it is
    /// running none or nothing is in range.
    ///
    /// <para><b>This exists because a consumed gesture stops the layout editor's snap marker dead.</b>
    /// <see cref="LayoutCanvas"/> offers the overlay every press and move first, and anything it
    /// consumes never reaches <c>LayoutEditorViewModel.OnPointerMoved</c> — so the marker the layout
    /// editor computed on the last HOVER is neither updated nor cleared. Two owner-visible bugs come
    /// straight out of that: the glyph freezes at the vertex a wire was grabbed by while the wire moves
    /// away from it, and no glyph appears at all mid-way through drawing a wire, even though the wire's
    /// own feet are being snapped the whole time.</para>
    ///
    /// <para>The canvas pushes this into the layout editor as a DISPLAY-ONLY marker, so one glyph
    /// mechanism serves both gestures and the answer on screen is always the answer the geometry
    /// actually used.</para>
    /// </summary>
    SnapCandidate? SnapMarker => null;

    /// <summary>
    /// Whether the press the overlay just consumed landed on nothing of its own.
    ///
    /// <para>The canvas clears the LAYOUT's selection for it. A click on empty space means "deselect"
    /// whichever selection the user was holding, and in the wBond editor that press is the wire
    /// marquee's — so without this, clicking empty space cleared the wire selection and left the layout
    /// selection standing, with nothing on screen explaining why (owner, 2026-08-17).</para>
    ///
    /// <para>Only ever true for a press the overlay CONSUMED: one it declined already reaches the
    /// layout editor, which clears its own selection exactly as it always did.</para>
    /// </summary>
    bool ConsumedPressWasEmptySpace => false;
}
