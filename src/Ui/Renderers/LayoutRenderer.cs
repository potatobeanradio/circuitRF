using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using SkiaSharp;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Renderers;

/// <summary>Render-time knobs for <see cref="LayoutRenderer.Draw"/>.</summary>
public readonly struct LayoutRenderOptions
{
    public LayoutRenderTheme Theme { get; init; }
    public bool ShowGrid { get; init; }

    /// <summary>The in-progress draw ghost (L1b), or null. Drawn above every committed layer in its
    /// own layer's resolved color, with a dashed outline — never contributes to
    /// <see cref="LayoutRenderResult.UnknownLayers"/> (it is provisional, not committed geometry).</summary>
    public LayoutOverlay? Overlay { get; init; }

    /// <summary>
    /// Export mode (L1f, R-L1f-5): when true, the background fill is skipped entirely instead of
    /// painting <see cref="LayoutRenderTheme.Background"/> — the destination surface (a fresh
    /// <c>SKBitmap</c> the caller has already erased to transparent, or a fresh PDF/SVG canvas,
    /// which start blank) is left as-is. Combined with <c>ShowGrid = false</c> and
    /// <c>Overlay = null</c> (which alone already suppresses grid/ghost/selection/handles/marquee —
    /// see the checks below), this produces a clean geometry-only render: a dark-theme layout pasted
    /// onto a white document page must not paint an opaque dark rectangle. Rulers are a separate
    /// control (<c>LayoutRulerRenderer</c>) and are never part of this call regardless.
    /// </summary>
    public bool TransparentBackground { get; init; }

    /// <summary>L2c §1/§2 (docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md) — the ONE aggregated,
    /// batched-per-layer-fill mechanism that serves both LOD (R-L2c-1: a shape whose on-screen bbox
    /// falls below this many device pixels contributes a minimal clamped rect instead of its full
    /// geometry) and the R8b merge tier (below). 0 (the default) means "expose it, tune from
    /// measurement" already happened — see the L2c completion note for the value and the data behind
    /// it; a caller may still override for testing/tuning. <b>Never cached, never derived from zoom —
    /// see <see cref="LayoutRenderer.DrawLayer"/> for why.</b></summary>
    public double LodPixelThreshold { get; init; }

    /// <summary>L2c §2 (R-L2c-2) — the OTHER trigger for the same batched-fill mechanism: a layer whose
    /// VISIBLE (candidate) shape count exceeds this switches every shape on it into the batched path,
    /// same as a sub-pixel shape would individually. 0 means "use <see cref="LayoutRenderer.
    /// DefaultMergeShapeCountThreshold"/>" — see that constant's doc comment for the reasoning and the
    /// measurement behind the chosen value.</summary>
    public int MergeShapeCountThreshold { get; init; }

    /// <summary>Stroke-elision engagement size, in device pixels — a compiled instance chunk whose
    /// largest primitive is smaller than this on screen draws as one solid grown fill instead of a
    /// fill pass plus a per-primitive outline pass. 0 (the default) means
    /// <see cref="LayoutRenderer.DefaultStrokeElisionDevicePixels"/>; a NEGATIVE value disables the
    /// tier outright, which is how a test pins the exact-geometry output the tier has to match.
    /// See that constant for the measurement this exists because of.</summary>
    public double StrokeElisionPixelThreshold { get; init; }

    /// <summary>On-screen WIDTH, in device pixels, at or under which a <see cref="PathShape"/> is drawn
    /// as one widened fill instead of a fill plus an outline stroke — see
    /// <see cref="LayoutRenderer.DefaultHairlineWidthDevicePixels"/> for what sets the value and why it
    /// is a much smaller number than <see cref="StrokeElisionPixelThreshold"/>, which measures a
    /// different thing (a compiled instance chunk's total extent). 0 (the default) means that constant;
    /// a NEGATIVE value disables the tier outright, which is how a test pins the exact
    /// fill-plus-outline output the tier has to match. Kept separate from
    /// <see cref="StrokeElisionPixelThreshold"/> on purpose: the two tiers engage on different
    /// quantities at different sizes, and a test that pins one must be able to leave the other
    /// alone.</summary>
    public double HairlineFillPixelThreshold { get; init; }

    /// <summary>Coverage at or above which a compiled instance chunk already on the stroke-elision
    /// tier stops drawing its primitives and contributes ONE rect to a per-layer batch. 0 (the default)
    /// means <see cref="LayoutRenderer.DefaultCoarseCoverageThreshold"/>; a NEGATIVE value disables the
    /// tier outright, which is how a test pins the elided output the tier has to match. See that
    /// constant for why the substitution is exact on a uniform field.</summary>
    public double CoarseCoverageThreshold { get; init; }

    /// <summary>§2.3 R8b's "a preference forces merge permanently for anyone who prefers it" — when
    /// true, EVERY layer uses the batched-fill path regardless of shape count or size. The mechanism
    /// exists here; wiring an actual Settings toggle to it is a small follow-up, not required to close
    /// the full-extent perf gap this phase targets.</summary>
    public bool ForceMergeTier { get; init; }

    /// <summary>L2c §3 (R-L2c-3/4) — the per-shape path cache, or <c>null</c> to disable caching
    /// entirely (every existing test and one-shot export render passes <c>null</c> and gets exactly
    /// L2b's behavior — paths built fresh in current path space every call). Owned by the CALLER
    /// (<c>LayoutCanvas</c>, for the lifetime of one document) since it must persist across frames to
    /// do any good at all — <see cref="LayoutRenderer.Draw"/> is a stateless static method and cannot
    /// own it itself.</summary>
    public LayoutPathCache? PathCache { get; init; }

    /// <summary>L3a (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md) — the absolute directory a
    /// relative <see cref="LayoutInstance.CellRef"/> resolves against: the directory of the currently
    /// open <c>.clay</c>. Null for a not-yet-saved (scratch) document — instances simply cannot resolve
    /// relative paths in that state and render as their "not found" placeholder, exactly as a scratch
    /// schematic cannot resolve a cell-ref symbol either (both are the same "no stable base yet"
    /// limitation, not a bug).</summary>
    public string? BaseDir { get; init; }

    /// <summary>brief-L5-followups-2.md §6 (R-L5g-13/15): draws each resolved top-level PCell
    /// instance's pins as a screen-space overlay (constant-pixel-size dot + outward-direction tick,
    /// <see cref="LayoutRenderTheme.PCellPin"/>) — never layer geometry, never contributes to any
    /// <see cref="LayoutFrameCounters"/> geometry count, never reachable by any exporter (pins are
    /// resolved live from the PCell generator, not stored as shapes). Defaults to <c>false</c> so
    /// every export/one-shot render (which never sets this) draws no pins by construction, exactly
    /// like <see cref="Overlay"/> being null already suppresses every other interactive-only overlay;
    /// the interactive canvas opts in via its own view-toggle VM property (default ON there, R-L5g-15
    /// — the toggle default lives at the VM layer, not here).</summary>
    public bool ShowPCellPins { get; init; }

    /// <summary>
    /// SL3 R-sl3-9 — the cell references in this document whose published interface no longer matches
    /// what the instances referencing them were placed against. Every instance carrying one of these
    /// references is marked; the geometry itself is untouched (R36 without exception — the librarian's
    /// new artwork is the truth and renders exactly as drawn).
    ///
    /// <para>A SET of references rather than a per-instance flag because <see cref="LayoutInstance"/>
    /// is a persisted model with no runtime state, and because the answer is a property of the CELL:
    /// forty instances of one changed cell are forty marks of one fact. Null — the case for every
    /// export and one-shot render, which never sets this — marks nothing.</para>
    /// </summary>
    public IReadOnlySet<string>? InterfaceChangedCellRefs { get; init; }

    /// <summary>
    /// docs/design/layout-view.md §9B / R-rul-1 — the per-document <b>Show Rulers</b> view toggle.
    /// Defaults to TRUE, unlike every other overlay flag here, because a ruler is DOCUMENT CONTENT
    /// rather than overlay state: it is in the <c>.clay</c>, it comes out in a slide, and an export
    /// path that quietly dropped it would contradict §9B.9's whole point. Export mode therefore leaves
    /// this ON — the grid, the ghost and the selection overlay are what <c>Overlay = null</c>
    /// suppresses, and rulers are none of those.
    /// </summary>
    /// <remarks><b>Stored inverted, deliberately.</b> This is a <c>readonly struct</c>, so a field
    /// initializer would force an explicit parameterless constructor — and <c>default(...)</c> bypasses
    /// one, which would make a defaulted options value silently DROP every ruler. Backing the property
    /// with the negation keeps <c>default</c> and <c>new { }</c> agreeing that rulers show.</remarks>
    public bool ShowRulers
    {
        get => !_rulersSuppressed;
        init => _rulersSuppressed = !value;
    }

    private readonly bool _rulersSuppressed;

    /// <summary>brief-L6-L7-em-ui.md R-em-15 — draws the EM cross-section mesh as a screen-space
    /// inset (see <c>LayoutRenderer.Mesh.cs</c>). Copies <see cref="ShowPCellPins"/>' contract
    /// exactly: never layer geometry, never contributes to any <see cref="LayoutFrameCounters"/>
    /// geometry count, never reachable by any exporter, and <b>defaulting to false</b> so every
    /// export/one-shot render draws no mesh by construction. The toggle default lives at the VM
    /// layer, not here.</summary>
    public bool ShowEmMesh { get; init; }

    /// <summary>The mesh to draw when <see cref="ShowEmMesh"/> is set. Null draws nothing — R-em-17:
    /// an edited layout CLEARS this rather than leaving a stale mesh on screen.</summary>
    public EmMeshReport? EmMesh { get; init; }

    /// <summary>brief-L8b D5 — draws the PLAN-VIEW surface mesh over the artwork (see
    /// <c>LayoutRenderer.PlanarMesh.cs</c>). Same contract as <see cref="ShowEmMesh"/>, including the
    /// default of false; the two are independent, and which one a document actually shows follows
    /// from which mesh was computed rather than from a mode.</summary>
    public bool ShowPlanarMesh { get; init; }

    /// <summary>The surface mesh to draw when <see cref="ShowPlanarMesh"/> is set. Null draws nothing
    /// — R-em-17 applies here MORE strongly than to the inset: a plan-view mesh drawn over EDITED
    /// artwork is worse than no mesh.</summary>
    public PlanarMeshReport? PlanarMesh { get; init; }

    /// <summary>L8e/D5 — the per-cell |J| map to shade the surface mesh with. Null takes the plain
    /// cell-boundary path; this IS L8b's own one-per-cell-scalar provision, now wired.</summary>
    public PlanarCurrentDensityMap? PlanarCurrentDensity { get; init; }

    /// <summary>§10.6 — the resolved ports whose de-embedding reference planes to draw over the
    /// artwork. Null or empty draws none. (Nullable rather than defaulted to <c>[]</c> because this
    /// is a readonly struct, which may not carry field initialisers.)</summary>
    public IReadOnlyList<PlanarPortResolution>? PlanarPorts { get; init; }

    /// <summary>
    /// <b>Which port labels the active EM setup drives as INTERNAL DELTA GAPS</b>, by the label's own
    /// DBU anchor. Null or empty means every port is an edge port, which is what every caller that
    /// predates the second port type passes and what a layout with no EM setup open means.
    ///
    /// <para><b>The port TYPE is not on the shape, and must not be.</b> It lives in the <c>.cem</c>
    /// (<c>EmSetup.PortKinds</c>) because a layout is geometry — the same artwork can be analysed with
    /// a gap in the middle of a trace in one setup and driven from its ends in another. So the
    /// renderer is TOLD, by the same channel that already hands it the mesh, the current-density map
    /// and the reference planes, rather than reading it off the label.</para>
    ///
    /// <para>Each entry carries the port's KIND as well as its anchor: an internal delta gap and an
    /// internal port draw different marks, and a port absent from this list draws the edge
    /// port's own reference-plane bar and arrow.</para>
    ///
    /// <para><b>Matched on the anchor, which is an exact pair of longs</b>, rather than on a port
    /// number: a label whose text names no number is auto-numbered in document order by
    /// <c>EmPortExtraction</c>, and reproducing that ordering here would be a second copy of it —
    /// free to drift, and silently wrong when it did.</para>
    /// </summary>
    public IReadOnlyList<(long X, long Y, PlanarPortKind Kind)>? InternalPortMarks { get; init; }

    /// <summary>
    /// L5b export (owner request: "copy/paste the DRC markers, just like the mesh"). Copies
    /// <see cref="ShowPlanarMesh"/>'s own contract: false by default, so every export/one-shot render
    /// draws no markers unless a caller explicitly opts in. The interactive canvas never sets this —
    /// it already draws markers via <see cref="Overlay"/>'s own <c>DrcMarkers</c>; this is the second,
    /// Overlay-independent path an exporter (which always passes <c>Overlay = null</c>) can use.
    /// </summary>
    public bool ShowDrcMarkers { get; init; }

    /// <summary>The markers to draw when <see cref="ShowDrcMarkers"/> is set. Null draws nothing.</summary>
    public IReadOnlyList<DrcMarker>? DrcMarkers { get; init; }

    /// <summary>
    /// Skip the geometry-snap glyph here, because the CALLER will draw it itself once everything else
    /// is on the canvas — via <see cref="LayoutRenderer.DrawSnapMarkerOnTop"/>.
    ///
    /// <para><b>Set by the interactive canvas and by nothing else.</b> A host that paints an OVERLAY
    /// after this call (<c>LayoutCanvas</c> hands the Skia lease to <c>ILayoutCanvasOverlay.Draw</c>
    /// straight afterwards — that is how wBond's wires reach a layout view) draws that overlay on top
    /// of everything <see cref="LayoutRenderer.Draw"/> produced, the snap glyph included. The glyph is
    /// a fixed ~8 device pixels while a wire and its vertex dots scale with zoom without limit, so
    /// past a certain zoom the glyph is drawn UNDER a wire wide enough to cover it completely —
    /// owner, 2026-08-19: "the geometry snap glyphs do not render if the zoom level is too high. I
    /// believe the glyphs are there but are hidden behind the wire point and segment renderings."</para>
    ///
    /// <para><b>Why not simply make the glyph bigger</b> (the owner's own suggestion in the same
    /// sentence): the thing hiding it has no size limit. A dot that keeps growing with zoom will cover
    /// any FIXED screen-space glyph at some zoom, so a size bump moves the zoom at which the glyph
    /// disappears without removing the zoom at which it does. Order is the property that has to hold,
    /// and it is one the size cannot buy. The size constants are untouched.</para>
    ///
    /// <para>Defaults to false, so every export, thumbnail and one-shot test render is unchanged: they
    /// draw nothing after the call, so there is nothing for the glyph to be under.</para>
    /// </summary>
    public bool DeferSnapMarker { get; init; }

    /// <summary>
    /// Same deal as <see cref="DeferSnapMarker"/>, for the RULER annotations, and for exactly the same
    /// reason (owner, 2026-08-27: rulers were rendering underneath the wBond wires). §9B.1's whole
    /// point is that a ruler "always paints above every layer" — but a host that paints an OVERLAY
    /// after <see cref="LayoutRenderer.Draw"/> returns paints it over everything that call produced,
    /// rulers included, so on a wirebond layout the annotation ended up under the wires it was
    /// measuring. When set, <c>Draw</c> skips its ruler pass and the host calls
    /// <see cref="LayoutRenderer.DrawRulersOnTop"/> after the overlay instead.
    ///
    /// <para><b>On that path rulers land above the layout's own selection chrome too</b> (outlines,
    /// handles, marquee), where ordinarily they paint below it. That is the honest cost of there
    /// being only one seam: everything that must be above the overlay has to move past the chrome
    /// with it. Content over thin dashed chrome is the right way round of the two.</para>
    ///
    /// <para>Defaults to false, so every export, thumbnail and one-shot test render is unchanged —
    /// they paint nothing after the call, so there is nothing for a ruler to be under.</para>
    /// </summary>
    public bool DeferRulers { get; init; }

    public static LayoutRenderOptions Default(LayoutRenderTheme theme) => new() { Theme = theme, ShowGrid = true, ShowPCellPins = true };
}

/// <summary>
/// Layer keys encountered during this <see cref="LayoutRenderer.Draw"/> call that a resolved
/// <see cref="Technology"/> did not define (docs/design/layout-view.md §2.4 gap-fill). Empty when
/// there is no technology at all — that is the normal "everything is fallback" case, not a warning.
/// The caller (the canvas / view model) is responsible for deduping against what has already been
/// warned about "once per layer per load" and posting to Messages — this is a pure render call and
/// never posts anything itself.
///
/// <b>The trailing fields (L2a, docs/sonnet-briefs/brief-L2a-performance-harness.md §2) are counters,
/// not timings</b> — deterministic, machine-independent work counts the CI benchmark gate asserts
/// against, since a millisecond assertion flakes on a shared runner and a shape/draw-call count does
/// not. All default to 0 so the two pre-existing single-argument call sites need no change.
/// <list type="bullet">
/// <item><see cref="ShapesExamined"/> — shapes considered for this frame: the candidate count the
/// spatial index's viewport-rect query returns (L2b), not <c>view.Shapes.Count</c> — at a zoomed-in
/// view this is O(visible), proving culling actually happened; at full extent it equals the total,
/// since a query rect covering everything returns everything.</item>
/// <item><see cref="ShapesDrawn"/> — of those candidates, the ones that actually issued a fill/text/
/// bitmap draw call (a hidden/non-selectable layer's candidates are examined but never drawn — see
/// <c>Counters_HiddenLayer_ExaminedButNotDrawn</c>).</item>
/// <item><see cref="PathsConstructed"/> — <c>SKPath</c> objects allocated this frame building shape
/// geometry (<see cref="BuildShapePath"/>'s own path, plus <see cref="BuildPathOutline"/>'s three —
/// centerline, stroke-to-fill outline, and the <c>Simplify</c> destination — for every <c>PathShape</c>).
/// Excludes the ghost/selection/handle/marquee overlay paths, which are editor-interaction geometry, not
/// per-frame committed-layer cost, and are never present in a benchmark frame (no live selection).</item>
/// <item><see cref="DrawCalls"/> — <c>canvas.Draw*</c> calls issued for committed geometry: one per
/// drawn shape's fill, one per layer for its batched hairline stroke (only when the batch is
/// non-empty), one per drawn label's text, one per drawn bitmap. Background/grid draws are excluded —
/// they are O(viewport), not O(shapes), and already have their own separate accounting (grid pitch).</item>
/// <item><see cref="LayersVisited"/> — resolved layers whose <see cref="LayerDef.Visible"/> was true
/// and which therefore actually entered <see cref="DrawLayer"/> this frame.</item>
/// <item><see cref="InstancesExamined"/> / <see cref="InstancesDrawn"/> (L3a) — instance PLACEMENTS
/// considered / actually drawn this frame. "Considered" is the spatial-index candidate count for
/// <see cref="LayoutView.Instances"/> (off-screen instances never appear here, per R-L3a's culling
/// requirement); "drawn" counts every ARRAY CELL actually painted (in full OR as a single LOD mark —
/// see <see cref="LayoutRenderer"/>'s instance-drawing file), so a 50x50 array contributes 2,500 to
/// <see cref="InstancesDrawn"/> from ONE spatial-index candidate.</item>
/// </list>
/// </summary>
public readonly record struct LayoutRenderResult(
    IReadOnlyList<LayerKey> UnknownLayers,
    int ShapesExamined = 0,
    int ShapesDrawn = 0,
    int PathsConstructed = 0,
    int DrawCalls = 0,
    int LayersVisited = 0,
    int InstancesExamined = 0,
    int InstancesDrawn = 0,
    /// <summary>L3a R-L3a-1 — distinct <see cref="LayoutInstance.CellRef"/> strings that failed to
    /// resolve (NotFound/PrimaryMissing/Cyclic/DepthExceeded) among this frame's candidates. Mirrors
    /// <see cref="UnknownLayers"/>'s contract exactly: the caller (canvas/view-model) dedupes against
    /// what has already been warned about for this open document and posts to Messages "once per
    /// distinct CellRef per load" — this is a pure render call and never posts anything itself.</summary>
    IReadOnlyList<string>? MissingInstanceCellRefs = null,
    /// <summary>How many layer fill paints this frame built. One per VISIBLE LAYER, never one per
    /// shape — see <see cref="LayerFillPaint"/>. Surfaced because the difference between those two is
    /// invisible in the rendered image and shows up only as a frame time on a file too big to
    /// bisect.</summary>
    int FillPaintsBuilt = 0);

/// <summary>Plain-field, no-dictionary per-frame work counters (L2a) — threaded through the private
/// draw helpers below by reference. A class (not a struct) so passing it around never copies; fields
/// are incremented directly with no logging/formatting, per the L2a brief's "must not itself cost
/// measurable time" guardrail (gate 6).</summary>
internal sealed class LayoutFrameCounters
{
    public int ShapesExamined;
    public int ShapesDrawn;
    public int PathsConstructed;
    public int DrawCalls;
    public int LayersVisited;
    public int InstancesExamined;
    public int InstancesDrawn;
    public int FillPaintsBuilt;
}

/// <summary>
/// Separable Skia renderer for the layout canvas (docs/design/layout-view.md §2.3/§3.2, L1a brief).
/// No Avalonia types. Draws 10³–10⁶ shapes; <see cref="SchematicRenderer.DrawSymbol"/> is NOT reused
/// here — see the brief's §0 for why (per-primitive path construction does not scale to layout counts).
///
/// <b>Coordinate convention (R-L1a-1/2 — read before touching this file):</b> Layout coordinates are
/// 64-bit integer DBU; <c>SKPath</c> is float32 (24-bit mantissa, ~16.7M distinct values), so feeding
/// raw DBU straight into a path quantizes badly far from the origin. Instead:
/// <list type="bullet">
/// <item>Paths are built in "path space": <c>(dbu - origin) * dbuToUm</c> (<see cref="PathSpace"/>),
/// where <c>origin</c> is a per-frame anchor near the viewport centre (quantized to a coarse step so
/// it changes rarely — see <see cref="ComputeOrigin"/>). Magnitudes are then bounded by the visible
/// extent in micrometres, not by absolute position — small at every zoom level, however far from
/// (0,0) the design sits.</item>
/// <item>Path space is built Y-DOWN (screen sense) even though the layout's own coordinate system is
/// Y-UP (physical/GDSII convention, <see cref="LayoutViewport"/>) — the flip happens once, per
/// vertex, at path-space construction (<see cref="PathSpace.Y"/>). <b>Arc parameters are always
/// derived from the ORIGINAL (Y-up, DBU) endpoints via <see cref="LayoutArc.FromBulge(long,long,long,long,double)"/>,
/// never re-derived from the already-flipped path-space floats</b> — a flip is a reflection
/// (determinant -1), which reverses an arc's sweep sense; re-deriving center/radius/angle from
/// flipped points with the same signed bulge silently fits a DIFFERENT arc (same two endpoints, same
/// sweep magnitude, wrong center) rather than the mirrored version of the original one. The fix is to
/// negate the world-computed start angle and sweep once when converting to Skia's arc-degrees
/// convention (see <see cref="AppendEdge"/>) — this is covered by a regression test
/// (<c>ClosedCurve_OfFourQuarterArcs_FillsLikeACircle</c>) precisely because the bug is silent: it
/// still draws *a* curve, just not the right one.</item>
/// <item>Pan and zoom are then just a plain positive-scale <c>SKMatrix</c> (<see cref="SKMatrix.CreateScaleTranslation"/>)
/// applied to the whole path-space geometry — panning never rebuilds a path, only changes the matrix.</item>
/// </list>
///
/// <b>The compositing contract (§2.3 R8a):</b> fills are drawn per-shape (so same-layer overlap
/// composites darker — this is the owner's decision, see the design doc); strokes are fully opaque
/// and a CONSTANT device-pixel width at any zoom (<see cref="GeometryStrokeDevicePixels"/> — a
/// scale-compensated width, <see cref="DevicePixelsToPathSpace"/>, rather than Skia's <c>StrokeWidth
/// = 0</c> hairline special case, which can only ever mean exactly 1 device pixel) and are batched
/// into one path per layer, since opaque-stroke overlap is idempotent. One <see cref="SKPaint"/> per
/// layer per role (fill/stroke), reused across every shape on that layer.
///
/// <b>Curves render natively</b> — <c>Line</c>→<c>LineTo</c>, <c>Arc</c>→<c>ArcTo</c>, <c>Cubic</c>→
/// <c>CubicTo</c>, <c>Circle</c>→<c>AddCircle</c>, <c>RoundedRect</c>→<c>AddRoundRect</c>. No
/// flattener is written in this phase — Skia tessellates adaptively at the current transform, which
/// already is §3.2 R9c's "rendering flattens adaptively at screen resolution."
///
/// <b><c>LayoutView.Instances</c></b> is rendered by the L3a partial-class extension in
/// <c>LayoutRenderer.Instances.cs</c> — see that file for the compiled-cell-geometry caching that
/// makes an array cost one path build and N matrix draws (R-L3a-3).
/// </summary>
public static partial class LayoutRenderer
{
    private const double MinGridPixelSpacing = 8.0;

    /// <summary>Maps DBU (world, Y-up) coordinates to path-space floats (Y-down/screen-sense),
    /// bounded by the visible extent rather than absolute position (R-L1a-1). The X axis is not
    /// flipped; only Y is, to convert layout's physical Y-up convention to Skia's Y-down one.</summary>
    /// <summary>Internal (not private) so <c>LayoutPathOutlineSeamTests</c> can construct one directly
    /// and call <see cref="BuildPathOutline"/> for a precise, isolated regression test of the
    /// GetFillPath-seam fix — see that method's doc comment.</summary>
    internal readonly struct PathSpace(long originX, long originY, double dbuToUm)
    {
        public double DbuToUm { get; } = dbuToUm;

        public float X(long dbu) => (float)((dbu - originX) * DbuToUm);
        public float Y(long dbu) => (float)(-(dbu - originY) * DbuToUm);

        public float X(double dbu) => (float)((dbu - originX) * DbuToUm);
        public float Y(double dbu) => (float)(-(dbu - originY) * DbuToUm);

        /// <summary>A world-space length (radius, width, corner radius — no origin offset) to path space.</summary>
        public float Len(double worldLen) => (float)(worldLen * DbuToUm);
    }

    // Reused across frames on the calling thread — Avalonia's ICustomDrawOperation hands us the whole
    // render-surface canvas (Bounds is for invalidation/hit-testing only, it does NOT clip Skia), so
    // every draw here must clip itself instead of relying on a caller-supplied clip. [ThreadStatic]
    // keeps this safe if multiple canvases ever render concurrently on different threads.
    [System.ThreadStatic]
    private static SKPaint? _backgroundPaint;

    private static SKPaint BackgroundPaint(SKColor color)
    {
        var paint = _backgroundPaint ??= new SKPaint { Style = SKPaintStyle.Fill };
        paint.Color = color;
        return paint;
    }

    public static LayoutRenderResult Draw(SKCanvas canvas, LayoutView? view, Technology? tech, LayoutViewport vp, LayoutRenderOptions opts)
    {
        var theme = opts.Theme;

        // Clip + explicit fill instead of canvas.Clear(...): Clear fills the ENTIRE current clip
        // region, and with no clip in force that is the whole render surface — wiping every sibling
        // control already painted this frame (toolbar, rulers, metadata bar). See the L1 fix note in
        // src/Ui/CLAUDE.md for the full story (this was the toolbar-invisible-until-hover bug).
        canvas.Save();
        try
        {
            var clipRect = SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height);
            canvas.ClipRect(clipRect);
            if (!opts.TransparentBackground)
                canvas.DrawRect(clipRect, BackgroundPaint(theme.Background));

            if (view is not null && opts.ShowGrid)
                DrawGrid(canvas, view, vp, theme);

            // Note: do NOT early-return on an empty Shapes list — the in-progress draw ghost (below)
            // must still render even when the layout has no committed geometry yet (drawing the very
            // first shape).
            if (view is null || vp.Width < 1 || vp.Height < 1 || vp.Zoom <= 0)
                return new LayoutRenderResult([]);

            var counters = new LayoutFrameCounters();
            var missingCellRefs = new HashSet<string>();
            var dragOverrides = opts.Overlay?.DragOverrides ?? EmptyDragOverrides;

            // ── L2b render culling — query the index once for the whole frame ───
            // The stored bbox is exact for every shape kind (LayoutSpatialIndex.ConservativeBboxOf ==
            // LayoutGeometry.BboxOf except for labels — see that method's doc comment), but the drawn
            // PIXELS extend a little beyond it: the batched per-layer hairline stroke
            // (GeometryStrokeDevicePixels) plus antialiasing softening. Expanding the QUERY rect (never
            // the stored bbox — that stays exact and shared with hit-test/marquee) by a device-pixel
            // margin, converted to world units at the CURRENT zoom, is what keeps a shape whose fill is
            // just outside the viewport but whose stroke would still paint a pixel or two into it from
            // being wrongly culled — the gate-4 "pixel-identical output" requirement depends on this.
            double marginDbu = RenderCullMarginDevicePixels / vp.Zoom;
            var viewportRect = new Bbox(
                (long)System.Math.Floor(vp.VisibleMinX - marginDbu), (long)System.Math.Floor(vp.VisibleMinY - marginDbu),
                (long)System.Math.Ceiling(vp.VisibleMaxX + marginDbu), (long)System.Math.Ceiling(vp.VisibleMaxY + marginDbu));
            var candidates = view.SpatialIndex.QueryIntersecting(view.Shapes, viewportRect);

            // R-L2b-2: "drags do not churn the index" — a live move/scale/handle-drag preview
            // (Overlay.DragOverrides) never calls NotifyChanged, so the index still reflects each
            // dragged shape's PRE-drag position for the whole gesture. A shape dragged from off-screen
            // into view would be wrongly culled if candidates came from the index query alone. Drag
            // selections are always small (bounded by what the user selected), so force-including them
            // unconditionally is cheap; a dragged shape whose LIVE position is off-screen is still
            // correctly invisible — the canvas clip rect (set above) discards it, exactly as it always
            // would have.
            if (dragOverrides.Count > 0)
            {
                var withDrag = new HashSet<int>(candidates);
                withDrag.UnionWith(dragOverrides.Keys);
                var merged = new List<int>(withDrag);
                merged.Sort();
                candidates = merged;
            }
            counters.ShapesExamined = candidates.Count;

            // ── Group candidates by layer, resolve each layer once ──────────────
            // Carries the shape's own index so a live move-drag can substitute a translated clone
            // (opts.Overlay.DragOverrides) without ever mutating view.Shapes mid-drag (L1c).
            // BitmapShape is excluded here — R-bmp-2: a bitmap's Layer governs visibility/
            // selectability only, never paint order, so it is never part of the per-layer,
            // ZOrder-sorted draw below. It is drawn separately, always first (see DrawBitmapShapes).
            // ── Candidate indices → shapes, resolved ONCE and tolerantly ────────
            //
            // The spatial index hands out POSITIONS, and the list those positions name can shrink on
            // the UI thread while this frame is being drawn on the render thread. A bounds CHECK does
            // not close that window — the list can shrink between the check and the read, which is
            // precisely the ArgumentOutOfRangeException that reaches a thread with nothing to catch
            // it. Taking the backing span once fixes the length this frame reads against, so no index
            // can throw: a position that has since gone yields either nothing or a stale shape for
            // one frame, which is a frame of visual lag rather than a torn process.
            //
            // Resolved once and shared, rather than re-indexed here and again in DrawBitmapShapes —
            // two independent reads of a list that is moving underneath is two chances to disagree,
            // and it was the second one that actually crashed.
            var shapesNow = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(view.Shapes);
            var live = new List<(int Index, LayoutShape Shape)>(candidates.Count);
            foreach (var i in candidates)
            {
                // Null-checked as well as bounds-checked: List<T>.RemoveAt clears the vacated slot,
                // so a span captured before a delete can hand back a null where a shape used to be.
                if ((uint)i < (uint)shapesNow.Length && shapesNow[i] is { } s) live.Add((i, s));
            }

            var byLayer = new Dictionary<LayerKey, List<(int Index, LayoutShape Shape)>>();
            foreach (var (i, shape) in live)
            {
                if (shape is BitmapShape) continue;
                if (!byLayer.TryGetValue(shape.Layer, out var list))
                    byLayer[shape.Layer] = list = [];
                list.Add((i, shape));
            }

            var layerMap = tech?.Layers.ToDictionary(l => l.Key);
            var unknownLayers = new HashSet<LayerKey>();
            var resolved = new List<(LayerDef Def, List<(int Index, LayoutShape Shape)> Shapes)>(byLayer.Count);
            foreach (var (key, shapes) in byLayer)
            {
                LayerDef def;
                if (layerMap is not null && layerMap.TryGetValue(key, out var found))
                    def = found;
                else
                {
                    if (tech is not null) unknownLayers.Add(key);   // tech resolved but this key is absent — a real gap
                    def = FallbackPalette.For(key);
                }
                resolved.Add((def, shapes));
            }
            resolved.Sort(static (a, b) => a.Def.ZOrder.CompareTo(b.Def.ZOrder));

            // ── Path-space origin + transform (R-L1a-1/2) ───────────────────────
            double centerX = vp.PanX + vp.Width  / (2.0 * vp.Zoom);
            double centerY = vp.PanY + vp.Height / (2.0 * vp.Zoom);
            double spanX   = vp.Width  / vp.Zoom;
            double spanY   = vp.Height / vp.Zoom;
            var (originX, originY) = ComputeOrigin(centerX, centerY, spanX, spanY);

            double dbuToUm = 1.0 / System.Math.Max(1, view.DbuPerMicron);
            var ps = new PathSpace(originX, originY, dbuToUm);

            double scaleUm = vp.Zoom / dbuToUm;                          // device px per micron
            double transX  = (originX - vp.PanX) * vp.Zoom;
            double transY  = vp.Height - (originY - vp.PanY) * vp.Zoom;
            var matrix = SKMatrix.CreateScaleTranslation((float)scaleUm, (float)scaleUm, (float)transX, (float)transY);

            canvas.Save();
            try
            {
                canvas.Concat(in matrix);

                // R-bmp-2: bitmaps ALWAYS render first — beneath every layer, regardless of the
                // layer's own ZOrder. This is the one deliberate exception to "Layer determines both
                // visibility and paint order" every other shape follows.
                DrawBitmapShapes(canvas, live, layerMap, unknownLayers, tech, dragOverrides, ps, theme, counters);

                // Built once per frame, not per port: an EM port's marker needs the conductor it sits
                // on, and that conductor may be a placed INSTANCE's artwork rather than a top-level
                // shape (a schematic-generated layout has no top-level shapes at all).
                var conductorAt = LayoutPortDirection.LookupFor(view, tech, opts.BaseDir ?? "");

                foreach (var (def, shapes) in resolved)
                {
                    if (!def.Visible) continue;
                    counters.LayersVisited++;
                    DrawLayer(canvas, def, shapes, conductorAt, ps, dragOverrides, scaleUm, opts, counters,
                              tech?.FindFillPattern(def.FillPattern), view.DbuPerMicron);
                }

                // L3a — instances (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md). Culled the
                // same way shapes are: the combined spatial-index query already excludes off-screen
                // placements (R-L3a §4 "culling and LOD apply to instances too").
                Bbox InstanceBboxFor(LayoutInstance inst) => CellHierarchy.InstanceBbox(inst, opts.BaseDir ?? "");
                var instanceCandidates = view.SpatialIndex.QueryIntersecting(
                    view.Shapes, view.Instances, InstanceBboxFor, CellLayoutResolver.Generation, viewportRect);
                counters.InstancesExamined = instanceCandidates.Count(e => e.Kind == SpatialEntryKind.Instance);
                var instanceDragOverrides = opts.Overlay?.InstanceDragOverrides ?? EmptyInstanceDragOverrides;
                if (counters.InstancesExamined > 0)
                {
                    // The same margin-expanded viewport the spatial-index query above used, in PATH
                    // space — DrawInstances maps it back through each placement's own matrix to cull
                    // inside a compiled cell. Computed here, from the one viewport already resolved
                    // for this frame, rather than re-derived down there from a second copy of the rule
                    // that decides what is on screen.
                    var visiblePathRect = NormalizedRect(
                        ps.X(vp.VisibleMinX - marginDbu), ps.Y(vp.VisibleMinY - marginDbu),
                        ps.X(vp.VisibleMaxX + marginDbu), ps.Y(vp.VisibleMaxY + marginDbu));
                    DrawInstances(canvas, view, tech, instanceCandidates, instanceDragOverrides, opts, ps,
                                  scaleUm, visiblePathRect, counters, missingCellRefs);
                }

                // L8b D5 — the plan-view surface mesh. Drawn INSIDE the path-space transform (it is
                // in the same (x, y) plane as the artwork, which is the whole reason this overlay can
                // exist at all) and above the layers, so cell boundaries read against the metal. The
                // cross-section inset below is drawn AFTER the transform is restored, because it is
                // screen-space; the two are deliberately different and both are correct.
                if (opts.ShowPlanarMesh && opts.PlanarMesh is { } planarMesh)
                {
                    // L8e/D5 — the heat map IS the per-cell-scalar path L8b left provisioned, and it
                    // is one argument, not a second overlay.
                    var density = opts.PlanarCurrentDensity;
                    DrawPlanarMeshOverlay(canvas, planarMesh, theme, ps, view.DbuPerMicron, scaleUm,
                                          density is null ? null : density.Normalised);

                    // §10.6 — the de-embedding reference planes, over the engine's own coordinates.
                    if (opts.PlanarPorts is { Count: > 0 } refPlanes)
                        DrawPlanarReferencePlanes(canvas, refPlanes, theme, ps,
                                                  view.DbuPerMicron, scaleUm);
                }

                if (opts.Overlay?.InProgressPrimitive is { } ghost)
                    DrawGhostShape(canvas, ghost, layerMap, tech, conductorAt, ps, scaleUm, theme.Background);

                if (opts.Overlay?.PastePreview is { Count: > 0 } pastePreview)
                    foreach (var previewShape in pastePreview)
                        DrawGhostShape(canvas, previewShape, layerMap, tech, conductorAt, ps, scaleUm, theme.Background);

                // The instance half of the paste ghost. Reuses the Instance tool's OWN ghost drawing
                // (real resolved geometry at reduced opacity, with the labelled placeholder fallback
                // for a reference that does not resolve) rather than a second ghost renderer — a
                // pasted instance and a placed one are the same thing and must look the same.
                if (opts.Overlay?.PastePreviewInstances is { Count: > 0 } pasteInstances)
                    foreach (var g in pasteInstances)
                    {
                        if (g.BoxOnly) DrawGhostInstanceBox(canvas, g.Bbox, theme, ps, scaleUm, counters);
                        else DrawPendingInstancePlacement(canvas, (g.Instance, g.Bbox), tech,
                                                          opts.BaseDir ?? "", theme, ps, scaleUm, counters);
                    }

                if (opts.Overlay?.PendingInstancePlacement is { } pendingInstance)
                    DrawPendingInstancePlacement(canvas, pendingInstance, tech, opts.BaseDir ?? "", theme, ps, scaleUm, counters);

                if (opts.Overlay?.PendingPCellPlacement is { } pendingPCell)
                    DrawPendingPCellPlacement(canvas, pendingPCell, tech, theme, ps, scaleUm, counters);

                // docs/design/layout-view.md §9B: rulers paint AFTER every layer and after
                // instances — they are not on a layer, obey no layer's visibility, and always sit on
                // top — but BEFORE the transient interaction overlay (selection outlines, handles,
                // marquee, snap marker), which is chrome about the current gesture rather than
                // content. Default-true and NOT gated on Overlay, so an export carries them.
                // Unless the host has taken it on itself to draw them LAST, above whatever it paints
                // after this call — see LayoutRenderOptions.DeferRulers.
                if (!opts.DeferRulers && opts.ShowRulers
                    && (view.Rulers.Count > 0 || opts.Overlay?.RulerPreview is not null
                        || opts.Overlay?.RulerPastePreview is { Count: > 0 }))
                    DrawRulers(canvas, view.Rulers,
                               opts.Overlay?.SelectedRulerIndices ?? [],
                               opts.Overlay?.RulerDragOverrides,
                               opts.Overlay?.RulerPreview,
                               opts.Overlay?.RulerPastePreview,
                               opts.Overlay?.ShowRulerEndpointHandles == true,
                               view.DisplayUnit, view.DbuPerMicron, theme, ps, scaleUm);

                if (opts.Overlay?.SelectedIndices is { Count: > 0 } selected)
                {
                    DrawSelectionOutlines(canvas, view, selected, dragOverrides, theme, ps, scaleUm,
                                          conductorAt, opts.InternalPortMarks);

                    // L1h R-L1h-5: bbox scale handles replace L1d's single-shape handles when showing
                    // (always for a 2+ selection; for a single shape, only while Scale mode is on).
                    if (opts.Overlay?.ShowScaleHandles == true)
                        DrawScaleHandles(canvas, view, selected, dragOverrides, theme, ps, scaleUm);
                    else if (selected.Count == 1)
                        DrawHandles(canvas, view, selected[0], dragOverrides, theme, ps, scaleUm);
                }

                if (opts.Overlay?.SelectedInstanceIndices is { Count: > 0 } selectedInstances)
                    DrawInstanceSelectionOutlines(canvas, view, selectedInstances, instanceDragOverrides, opts, theme, ps, scaleUm);

                if (opts.Overlay?.Marquee is { } marquee)
                    DrawMarquee(canvas, marquee, theme, ps);

                // Unless the host has taken it on itself to draw the glyph LAST, above whatever it
                // paints after this call — see LayoutRenderOptions.DeferSnapMarker.
                if (!opts.DeferSnapMarker && opts.Overlay?.SnapMarker is { } snapMarker)
                    DrawSnapMarker(canvas, snapMarker, layerMap, ps, scaleUm, theme);

                // pcell-parameter-handles.md §4.2 — above the instance's own artwork and its selection
                // outline, because a grip the user cannot see is a feature that did not ship. Never
                // layer geometry: no LayerKey, never in LayoutView.Shapes, never counted, never
                // reachable by an exporter (every export path passes Overlay = null).
                if (opts.Overlay?.PCellHandles is { Count: > 0 } pcellHandles)
                    DrawPCellHandles(canvas, pcellHandles, theme, ps, scaleUm);

                // L5b: §9A.1's "system layer over the geometry" — drawn LAST inside the path-space
                // transform so a violation is never hidden behind the metal it is about, and above
                // the selection outline so a selected shape's own violation stays visible.
                // Export mode has no Overlay (every exporter passes Overlay = null) so it opts in via
                // ShowDrcMarkers/DrcMarkers instead — same shape as ShowPlanarMesh/PlanarMesh above.
                var drcMarkers = opts.Overlay?.DrcMarkers
                    ?? (opts.ShowDrcMarkers ? opts.DrcMarkers : null);
                if (drcMarkers is { Count: > 0 })
                    DrawDrcMarkers(canvas, drcMarkers, theme, ps, scaleUm);
            }
            finally
            {
                canvas.Restore();
            }

            // R-em-15: drawn AFTER the path-space transform has been restored — the mesh overlay is
            // screen-space, and it must never be swept up by anything that walks layer geometry.
            if (opts.ShowEmMesh && opts.EmMesh is { } meshReport)
                DrawEmMeshOverlay(canvas, meshReport, theme, vp.Width, vp.Height);

            return new LayoutRenderResult(
                unknownLayers.Count == 0 ? [] : unknownLayers.ToArray(),
                ShapesExamined: counters.ShapesExamined,
                ShapesDrawn: counters.ShapesDrawn,
                PathsConstructed: counters.PathsConstructed,
                DrawCalls: counters.DrawCalls,
                LayersVisited: counters.LayersVisited,
                InstancesExamined: counters.InstancesExamined,
                InstancesDrawn: counters.InstancesDrawn,
                MissingInstanceCellRefs: missingCellRefs.Count == 0 ? [] : missingCellRefs.ToArray(),
                FillPaintsBuilt: counters.FillPaintsBuilt);
        }
        finally
        {
            canvas.Restore();
        }
    }

    // ── Origin quantization ──────────────────────────────────────────────────

    /// <summary>
    /// Anchors path space near the viewport centre, quantized to a power-of-two step derived from
    /// the current view span — so the origin changes only roughly once per screen's worth of
    /// panning (relevant once L2 adds path caching on top of this convention; L1a rebuilds every
    /// frame regardless, per the brief's scope fence).
    /// </summary>
    internal static (long OriginX, long OriginY) ComputeOrigin(double centerX, double centerY, double spanX, double spanY)
    {
        double span = System.Math.Max(System.Math.Max(System.Math.Abs(spanX), System.Math.Abs(spanY)), 1.0);
        long step = (long)System.Math.Pow(2, System.Math.Ceiling(System.Math.Log2(span)));
        if (step <= 0) step = 1;
        long ox = (long)System.Math.Round(centerX / step) * step;
        long oy = (long)System.Math.Round(centerY / step) * step;
        return (ox, oy);
    }

    // ── Grid (screen-space — never touches the path-space float32 path) ────────

    private static void DrawGrid(SKCanvas canvas, LayoutView view, LayoutViewport vp, LayoutRenderTheme theme)
    {
        var pitch = LayoutGridMath.ComputeGridPitch(view.SnapDbu, vp.Zoom, MinGridPixelSpacing);
        if (pitch is null) return;

        long minorPitch = pitch.Value;
        long majorPitch = minorPitch * LayoutGridMath.MajorGridStepCount;

        long iStart = (long)System.Math.Floor(vp.VisibleMinX / minorPitch);
        long iEnd   = (long)System.Math.Ceiling(vp.VisibleMaxX / minorPitch);
        long jStart = (long)System.Math.Floor(vp.VisibleMinY / minorPitch);
        long jEnd   = (long)System.Math.Ceiling(vp.VisibleMaxY / minorPitch);

        const long safetyCap = 4096;
        if (iEnd - iStart > safetyCap || jEnd - jStart > safetyCap) return;

        var minorPts = new List<SKPoint>();
        var majorPts = new List<SKPoint>();

        for (long i = iStart; i <= iEnd; i++)
        {
            long wx = i * minorPitch;
            float sx = (float)vp.WorldToScreenX(wx);
            bool iMajor = wx % majorPitch == 0;
            for (long j = jStart; j <= jEnd; j++)
            {
                long wy = j * minorPitch;
                float sy = (float)vp.WorldToScreenY(wy);
                bool jMajor = wy % majorPitch == 0;
                (iMajor && jMajor ? majorPts : minorPts).Add(new SKPoint(sx, sy));
            }
        }

        using var minorPaint = new SKPaint { IsAntialias = true, Color = theme.GridMinor, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round };
        using var majorPaint = new SKPaint { IsAntialias = true, Color = theme.GridMajor, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round };

        if (minorPts.Count > 0) canvas.DrawPoints(SKPointMode.Points, minorPts.ToArray(), minorPaint);
        if (majorPts.Count > 0) canvas.DrawPoints(SKPointMode.Points, majorPts.ToArray(), majorPaint);
    }

    // ── Bitmaps (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md, R-bmp-2) ─────────────
    // Always drawn first — beneath every layer, regardless of the resolved layer's own ZOrder. A
    // bitmap's Layer governs visibility/selectability only; see the call site in Draw() and
    // BitmapShape's own doc comment for why.

    /// <param name="live">This frame's candidates, already resolved to shapes by <see cref="Draw"/>.
    /// Takes the resolved pairs rather than the indices and the view, so it cannot re-read a list that
    /// is being edited on another thread — see the resolution in <c>Draw</c> for why that read is not
    /// safe to repeat.</param>
    private static void DrawBitmapShapes(
        SKCanvas canvas, IReadOnlyList<(int Index, LayoutShape Shape)> live, Dictionary<LayerKey, LayerDef>? layerMap,
        HashSet<LayerKey> unknownLayers, Technology? tech,
        IReadOnlyDictionary<int, LayoutShape> dragOverrides, PathSpace ps, LayoutRenderTheme theme,
        LayoutFrameCounters counters)
    {
        foreach (var (i, shape) in live)
        {
            if (shape is not BitmapShape) continue;
            if ((dragOverrides.TryGetValue(i, out var ov) ? ov : shape) is not BitmapShape bmp) continue;

            LayerDef def;
            if (layerMap is not null && layerMap.TryGetValue(bmp.Layer, out var found))
                def = found;
            else
            {
                if (tech is not null) unknownLayers.Add(bmp.Layer);
                def = FallbackPalette.For(bmp.Layer);
            }
            if (!def.Visible) continue;

            var rect = BitmapPlacementRect(bmp, ps);
            if (rect.Width < 0.5f || rect.Height < 0.5f) continue;

            counters.ShapesDrawn++;
            counters.DrawCalls++;
            var skBmp = BitmapCache.Load(bmp.ImagePathRef);
            if (skBmp is null)
            {
                BitmapCache.DrawBrokenPlaceholder(canvas, rect.Left, rect.Top, rect.Width, rect.Height, theme.Warning);
            }
            else
            {
                byte alpha = (byte)System.Math.Clamp(bmp.Opacity * 255, 0, 255);
                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(alpha) };
                canvas.DrawBitmap(skBmp, rect, paint);
            }
        }
    }

    // ── In-progress draw ghost (L1b) ────────────────────────────────────────────

    /// <summary>Device-pixel floor for a label's on-screen font size — both the in-progress ghost
    /// (R-lbl-2) AND, since the owner report that a committed label could still "disappear" the
    /// instant Enter was pressed (a technology-appropriate height, R-lbl-1, can still be well under
    /// one device pixel at a zoomed-out view), the COMMITTED shape's height at the moment it's placed
    /// (<c>LayoutEditorViewModel.CommitLabel</c>). Never retroactively applied to an existing label —
    /// only at the moment of typing/placement, using the zoom captured when typing started.</summary>
    internal const float MinVisibleLabelDevicePixels = 8f;

    /// <summary>The pure-arithmetic visibility-floor computation — split out from
    /// <see cref="DrawGhostShape"/>'s Label branch (and reused directly by
    /// <c>LayoutEditorViewModel.CommitLabel</c>) specifically so it is headlessly testable: drawing the
    /// actual glyph touches <c>SkiaFonts.PlexRegular</c>, which cannot load without a live Avalonia app
    /// host (confirmed empirically — <c>Avalonia.Platform.AssetLoader</c> throws
    /// <c>InvalidOperationException</c> with no app host, exactly as this project's other font-touching
    /// renderer tests already document), but this arithmetic needs no font at all. <paramref
    /// name="zoomPxPerDbu"/> is device pixels per DBU (<c>LayoutViewport.Zoom</c>/<c>LayoutCanvas</c>'s
    /// own <c>_zoom</c> field directly — path-space's <c>dbuToUm</c> cancels out of the on-screen-pixel
    /// computation entirely, so it is deliberately not a parameter here). Returns
    /// <paramref name="heightDbu"/> unchanged when it would already render at or above
    /// <see cref="MinVisibleLabelDevicePixels"/>, or when <paramref name="zoomPxPerDbu"/> is unknown
    /// (0 or less — never boosts on a "don't know the zoom" caller).</summary>
    internal static long EffectiveVisibleLabelHeightDbu(long heightDbu, double zoomPxPerDbu)
    {
        double pixelHeight = heightDbu * zoomPxPerDbu;
        if (pixelHeight <= 0 || pixelHeight >= MinVisibleLabelDevicePixels) return heightDbu;
        return (long)System.Math.Ceiling(MinVisibleLabelDevicePixels / zoomPxPerDbu);
    }

    /// <summary>Draws a not-yet-committed shape above every layer, in its own resolved layer
    /// color, with a dashed outline so it reads as provisional. Reuses
    /// <see cref="BuildShapePath"/> — no second geometry path for the ghost. Never touches
    /// <c>unknownLayers</c>: an uncommitted shape's layer choice isn't a gap to warn about — if it
    /// is placed, the very next frame's normal per-shape resolution will do that. Shared by the L1b
    /// in-progress draw ghost (one shape) and the L1f paste-ghost preview (a whole fragment).
    ///
    /// <b>docs/sonnet-briefs/brief-drag-fill-reopened.md, R-dgf-3:</b> the fill uses the layer's OWN
    /// <see cref="LayerDef.FillOpacity"/> (the same alpha <c>DrawLayer</c> computes for the committed
    /// shape), not a fixed low alpha — a prior fixed alpha=60 measured at a consistent ~0.67-0.69× the
    /// committed shape's contrast against the canvas background regardless of layer color (muted or
    /// saturated) or theme (light or dark), i.e. the cause was opacity, not color as first suspected;
    /// the dashed outline (unchanged) is what marks a ghost as provisional, so the fill does not need
    /// to be faint to carry that meaning.</summary>
    private static void DrawGhostShape(SKCanvas canvas, LayoutShape ghost, Dictionary<LayerKey, LayerDef>? layerMap,
        Technology? ghostTech,
        LayoutPortDirection.ConductorLookup? conductorAt, PathSpace ps, double scaleUm, SKColor background)
    {
        LayerDef def = layerMap is not null && layerMap.TryGetValue(ghost.Layer, out var found)
            ? found
            : FallbackPalette.For(ghost.Layer);
        var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);

        if (ghost is LabelShape label)
        {
            long effectiveHeight = EffectiveVisibleLabelHeightDbu(label.Height, ps.DbuToUm * scaleUm);
            LabelShape effective = effectiveHeight == label.Height ? label : new LabelShape
            {
                Layer = label.Layer, X = label.X, Y = label.Y, Text = label.Text,
                Height = effectiveHeight, RotationDegrees = label.RotationDegrees, IsPort = label.IsPort,
                PortDirection = label.PortDirection, Style = label.Style,
                HAlign = label.HAlign, VAlign = label.VAlign,
            };
            DrawLabelText(canvas, effective, ps, color, centred: label.IsPort);
            // A port ghost carries its own marker, so what the user is placing looks like what
            // lands. It resolves against the SAME per-frame conductor lookup a committed port uses
            // (owner request, 2026-08-09: "the ghost's snapping and sizes also need to render live"),
            // so the width bar spans the real metal and the arrow's length is clamped by the real
            // conductor — not the no-conductor stand-in a null lookup would fall back to.
            if (label.IsPort)
                DrawPortMarker(canvas, effective, conductorAt, ps, scaleUm, color, background,
                               new LayoutFrameCounters(), PlanarPortKind.Edge);
            return;
        }

        // Bitmaps have no BuildShapePath entry (R-bmp-3: not geometry) — a paste-ghost preview
        // containing a bitmap still needs to show SOMETHING at its placement rect, even though the
        // L1b draw-ghost path never reaches this case (there is no Bitmap drawing tool).
        using var shapePath = ghost is BitmapShape bmp ? BuildBitmapPlacementRectPath(bmp, ps) : BuildShapePath(ghost, ps);
        if (shapePath is null || shapePath.IsEmpty) return;

        using var fillPaint = LayerFillPaint.Create(def, ghostTech?.FindFillPattern(def.FillPattern), color, scaleUm);
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0, Color = color.WithAlpha(220),
            PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };

        canvas.DrawPath(shapePath, fillPaint);
        canvas.DrawPath(shapePath, strokePaint);
    }

    // ── Per-layer draw: per-shape fills, one batched hairline stroke ────────────

    private static readonly IReadOnlyDictionary<int, LayoutShape> EmptyDragOverrides = new Dictionary<int, LayoutShape>();

    /// <summary>L3a — the instance-move-drag analogue of <see cref="EmptyDragOverrides"/>.</summary>
    internal static readonly IReadOnlyDictionary<int, LayoutInstance> EmptyInstanceDragOverrides = new Dictionary<int, LayoutInstance>();

    /// <summary>Device-pixel target for the per-shape outline stroke — doubled from the plain
    /// Skia hairline (which is exactly 1 device pixel) per owner feedback, 2026-07-26.</summary>
    private const double GeometryStrokeDevicePixels = 2.0;

    /// <summary>
    /// On-screen WIDTH, in device pixels, at or under which a <see cref="PathShape"/> stops being drawn
    /// as fill-plus-outline and is drawn as one widened fill instead (see <c>DrawLayer</c>'s hairline
    /// tier and <see cref="LayoutPathCache.GetOrBuildWidened"/>).
    ///
    /// <para><b>One pixel, and the value was measured rather than assumed — a bigger one is visibly
    /// wrong.</b> The substitution is exact in FOOTPRINT at any width (fill-then-outline and
    /// fill-at-Width-plus-the-pen cover the identical region), but not in ALPHA: the widened fill is
    /// painted at the stroke's solid opacity throughout, where the real pair paints a solid rim around
    /// an interior at the layer's own (usually partial) fill opacity. Below one device pixel that
    /// interior cannot be resolved and the two are indistinguishable. Above it they are not, and the
    /// error grows fast — swept on the owner's 4-up panel at Zoom-to-Fit, admitting the 10-mil traces
    /// (1.03 px wide there) moved 41% of the board's pixels and dropped its mean red from 145 to 104,
    /// which reads as copper flooding its own clearances. At 0.5 px the same sweep moves 2.8% of pixels
    /// and the mean by 0.4% — antialiasing-level differences.</para>
    ///
    /// <para>This is deliberately NOT <see cref="DefaultStrokeElisionDevicePixels"/> (4.0), which
    /// measures a different quantity — a compiled instance chunk's TOTAL extent, where a whole
    /// primitive being four pixels across is what makes its interior sub-pixel. Here the shape is long
    /// and the width alone is what is small, so the width alone is what may be tested.</para>
    /// </summary>
    internal const double DefaultHairlineWidthDevicePixels = 1.0;

    /// <summary>L2b render-culling query-rect margin, in device pixels — generously covers
    /// <see cref="GeometryStrokeDevicePixels"/>'s half-width (the stroke straddles the fill boundary)
    /// plus antialiasing softening, so a shape whose fill bbox sits just outside the viewport but whose
    /// stroke would still paint a pixel or two into it is never wrongly culled.</summary>
    private const double RenderCullMarginDevicePixels = 8.0;

    /// <summary>Device-pixel target for the selection accent outline — also doubled, so a selected
    /// shape reads unmistakably as selected next to its now-thicker geometry outline.</summary>
    private const double SelectionStrokeDevicePixels = 2.0;

    /// <summary>Converts a desired ON-SCREEN stroke width (device pixels) to the equivalent width in
    /// path space, given the current frame's device-pixels-per-micron scale — this is what keeps a
    /// stroke's apparent thickness constant across zoom levels without relying on Skia's <c>StrokeWidth
    /// = 0</c> hairline special case (which can only ever mean exactly 1 device pixel, not N).</summary>
    private static float DevicePixelsToPathSpace(double scaleUm, double devicePixels)
        => (float)(devicePixels / System.Math.Max(scaleUm, 1e-12));

    // ── L2c §1/§2 — LOD aggregation and the R8b merge tier, ONE mechanism, two triggers ──────────
    // docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md. R-L2c-1: a shape whose on-screen bbox is
    // under LodPixelThreshold contributes a MINIMAL RECT (never dropped — a dense cluster of sub-pixel
    // shapes must still read as filled, not empty, at full extent) to a single batched-per-layer fill
    // path instead of building its real geometry. R-L2c-2: a layer whose candidate count exceeds
    // MergeShapeCountThreshold (or ForceMergeTier) sends every one of its NON-sub-pixel shapes into the
    // SAME batched path too, with their real geometry (still built once, just not drawn/composited
    // individually) — gate 6 requires both triggers route through the identical aggregate, and they do
    // by construction: there is exactly one `aggregate` SKPath per layer, filled once, below.

    /// <summary>Default LOD engagement threshold, device pixels — §5.3 item 3's own starting guess,
    /// confirmed (not just assumed) by the LOD-only measurement in the L2c completion note before the
    /// merge tier or path cache were built at all (gate 2's explicit ordering).</summary>
    internal const double DefaultLodPixelThreshold = 2.0;

    /// <summary>The minimal rect a sub-pixel shape contributes is clamped to at least this many device
    /// pixels per side (not the LOD threshold itself, which only decides WHETHER to aggregate) — small
    /// enough to stay visually negligible individually, large enough that Skia does not silently drop a
    /// truly-zero-area rect.</summary>
    private const double MinimalRectDevicePixels = 1.0;

    /// <summary>Default R8b merge-tier shape-count threshold. L2a's own single-layer sweep (§5 of that
    /// phase's data) found merged fills cheaper than per-shape darkening at EVERY density tested (500 to
    /// 100,000 shapes/layer, ratio never below 1.0×) — there is no performance cliff to tune to, per L2c
    /// §2's framing. The remaining question is purely R8a's UX trade-off (same-layer overlap stops
    /// reading as darker once merged), so the threshold is set where a genuinely dense layer's
    /// individual-shape darkening feedback has already become visual noise rather than information —
    /// see the L2c completion note for the measured full-extent numbers this value was chosen against.</summary>
    internal const int DefaultMergeShapeCountThreshold = 2_000;

    private static void DrawLayer(SKCanvas canvas, LayerDef def, List<(int Index, LayoutShape Shape)> shapes,
        LayoutPortDirection.ConductorLookup? conductorAt,
        PathSpace ps, IReadOnlyDictionary<int, LayoutShape> dragOverrides, double scaleUm,
        LayoutRenderOptions opts, LayoutFrameCounters counters, FillPattern? fillPattern = null,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);

        using var fillPaint = LayerFillPaint.Create(def, fillPattern, color, scaleUm, counters);
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels),
            Color = color.WithAlpha(255),
        };
        using var strokeBatch = new SKPath();
        using var aggregate = new SKPath();

        // ── The stroke-elision tier's own aggregate (see ElideStrokeFor) ─────────────────────────
        // Separate from `aggregate` because it is painted differently: the elided fill carries the
        // STROKE's solid alpha, not the layer's (usually partial) fill alpha, and it is never fed to
        // strokeBatch. That is not a new convention — it is the one the instance elision tier already
        // states in LayoutRenderer.Instances.cs, for the same reason: at the few-device-pixel sizes
        // this engages at the outline IS essentially the whole visible shape, so carrying the fill's
        // partial opacity across would visibly dim geometry that reads solid today.
        using var elided = new SKPath();

        double lodThreshold = opts.LodPixelThreshold > 0 ? opts.LodPixelThreshold : DefaultLodPixelThreshold;
        int mergeThreshold = opts.MergeShapeCountThreshold > 0 ? opts.MergeShapeCountThreshold : DefaultMergeShapeCountThreshold;
        bool layerMerges = opts.ForceMergeTier || shapes.Count > mergeThreshold;
        double devicePxPerDbu = scaleUm * ps.DbuToUm;

        // A NEGATIVE threshold disables the tier outright — how a test pins the exact fill-plus-outline
        // output this tier has to match.
        double hairlineThreshold = opts.HairlineFillPixelThreshold != 0
            ? opts.HairlineFillPixelThreshold : DefaultHairlineWidthDevicePixels;
        long widenDbu = devicePxPerDbu > 0
            ? (long)System.Math.Ceiling(GeometryStrokeDevicePixels / devicePxPerDbu) : 0;

        foreach (var (index, original) in shapes)
        {
            // A shape being live-move-dragged (Select tool) renders at its translated preview
            // position instead of its stored one — the model itself is untouched until the drag
            // commits as one MoveShapesCommand (R-L1c-3).
            var shape = dragOverrides.TryGetValue(index, out var ov) ? ov : original;

            if (shape is LabelShape label)
            {
                if (string.IsNullOrEmpty(label.Text)) continue;
                counters.ShapesDrawn++;
                counters.DrawCalls++;

                // item 6, "also worth fixing regardless": a committed label authored (or imported)
                // with a sub-pixel Height is a trap — invisible on screen yet real model data, exactly
                // the failure mode this brief's diagnosis traced. R-lbl-2's ghost already applies this
                // floor for an in-progress label; this applies the SAME EffectiveVisibleLabelHeightDbu
                // arithmetic to a COMMITTED one, for DISPLAY ONLY — the model's own Height is never
                // touched, only the drawn clone, mirroring DrawGhostShape's Label branch exactly.
                long effectiveHeight = EffectiveVisibleLabelHeightDbu(label.Height, devicePxPerDbu);
                LabelShape effective = effectiveHeight == label.Height ? label : new LabelShape
                {
                    Layer = label.Layer, X = label.X, Y = label.Y, Text = label.Text,
                    Height = effectiveHeight, RotationDegrees = label.RotationDegrees, IsPort = label.IsPort,
                    PortDirection = label.PortDirection, Style = label.Style,
                    HAlign = label.HAlign, VAlign = label.VAlign,
                };
                // ── THE KIND IS ASKED OF `original`, NEVER OF THE DRAG OVERRIDE ──────────
                //
                // Owner report, 2026-08-25: "the internal port rendering is messed up during a drag
                // — it reverts to edge port rendering." MarkKindOf matches on the label's exact DBU
                // anchor, and a live move drag renders `shape` as a translated CLONE while the model
                // stays untouched until commit (R-L1c-3). So from the first pixel of the drag the
                // anchor no longer matched any mark, every internal port fell through to Edge, and
                // the gap glyph was replaced by an edge port's bar-and-arrow for the whole gesture.
                //
                // `original` is the shape the .cem's marks were computed FROM, and moving a port does
                // not retype it — the type lives in the .cem and no drag can touch it. Asking the
                // stored shape is therefore not a workaround for the coordinate key; it is the
                // correct question, and it stays correct if the key ever changes.
                var portKind = label.IsPort && original is LabelShape stored
                    ? MarkKindOf(opts.InternalPortMarks, stored)
                    : PlanarPortKind.Edge;

                DrawLabelText(canvas, effective, ps, color, centred: label.IsPort);
                if (label.IsPort)
                    DrawPortMarker(canvas, effective, conductorAt, ps, scaleUm, color,
                                   opts.Theme.Background, counters, portKind,
                                   opts.PlanarMesh, dbuPerMicron);
                continue;
            }

            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;

            // R-L2c-1: sub-pixel-size is a PER-SHAPE, per-frame decision (never cached, never derived
            // from anything but the current zoom) — it can engage even on a layer well under the merge
            // count threshold, and must, or a dense small-feature layer viewed zoomed-out would still
            // pay full per-shape path-construction cost for geometry nobody can see the shape of anyway.
            double screenW = (bb.MaxX - bb.MinX) * devicePxPerDbu;
            double screenH = (bb.MaxY - bb.MinY) * devicePxPerDbu;
            if (System.Math.Max(screenW, screenH) < lodThreshold)
            {
                AddMinimalRect(aggregate, bb, ps, scaleUm);
                counters.ShapesDrawn++;
                continue;
            }

            // ── Stroke elision for a HAIRLINE-WIDTH path ────────────────────────────────────────
            //
            // The LOD tier above catches a shape that is small in BOTH dimensions. It cannot catch the
            // shape that actually dominates an imported PCB: a trace that is long (so its bbox is
            // nowhere near sub-pixel) and, on screen, a small fraction of a pixel WIDE. That shape's
            // 2-device-pixel outline is not describing its silhouette — it is forty times the width of
            // the thing it outlines, so what the user sees IS the outline. Two consequences, and the
            // second is the expensive one:
            //
            //   * every such shape's outline goes into strokeBatch, and Skia's stroker then walks
            //     tens of thousands of sub-paths in one DrawPath. On the owner's 4-up panel — a 2014
            //     Gerber whose copper pours are painted as 41,824 abutting one-mil raster scanlines —
            //     that single stroke pass was ~235 ms of a ~292 ms frame, measured by removing it.
            //   * a pour painted that way saturates to solid outline colour at full extent, hiding
            //     the silkscreen and the traces underneath it.
            //
            // Filling the same centreline at Width + one stroke-width instead is not an approximation:
            // a PathShape's fill IS its centreline stroked at Width, so fill-then-outline and
            // fill-at-Width-plus-the-pen cover the identical region (see
            // LayoutPathCache.GetOrBuildWidened). One filled path replaces a fill plus a stroker pass.
            //
            // The FOOTPRINT is exact at any width; the ALPHA is only indistinguishable below about one
            // device pixel, which is what DefaultHairlineWidthDevicePixels is set from and why it is
            // not the instance tier's 4.0 — read that constant before widening this gate.
            //
            // Scoped to PathShape deliberately. It is the only shape kind that HAS a width to be
            // hairline in, and the only one this substitution is exact for — a thin polygon would need
            // a real offset, and inventing an approximation for it here would be a different claim
            // than the one this tier can actually make.
            if (hairlineThreshold > 0 && widenDbu > 0 && shape is PathShape thinPath
                && thinPath.Width * devicePxPerDbu < hairlineThreshold
                && IsOpenCentreline(thinPath)
                && opts.PathCache is { } elisionCache && !dragOverrides.ContainsKey(index))
            {
                var (widenedLocal, wRefX, wRefY) =
                    elisionCache.GetOrBuildWidened(index, thinPath, widenDbu, ps.DbuToUm, counters);
                if (widenedLocal is null || widenedLocal.IsEmpty) continue;
                elided.AddPath(widenedLocal, ps.X(wRefX), ps.Y(wRefY));
                counters.ShapesDrawn++;
                continue;
            }

            if (layerMerges)
            {
                // Full geometry, same as the individual tier below — just added to the shared
                // aggregate instead of drawn/composited on its own (R-L2c-2's "same mechanism").
                //
                // ── THE CACHE APPLIES HERE TOO, and leaving it out was the whole cost of a
                // stroke-per-segment import ────────────────────────────────────────────────────
                //
                // R-L2c-3's own note said the cache "applies to the individual tier only", and while
                // the merge tier existed purely as a full-extent LOD fallback that was harmless: at
                // full extent nearly everything is sub-pixel and never reaches this branch at all.
                // A Gerber written by a tool that strokes every trace segment separately breaks that
                // assumption head on — 46,000 one-mil draws on a single copper layer, each one LONG
                // (so never sub-pixel by bbox) and each one on a layer far past
                // MergeShapeCountThreshold. Every frame rebuilt every outline: 219,556 SKPaths per
                // frame on the owner's board, ~370 ms a frame, for geometry that had not changed
                // since the file was opened.
                //
                // The cached path is in shape-LOCAL space, so it is added under the same
                // (ps.X(refX), ps.Y(refY)) offset the individual tier applies via canvas.Translate —
                // SKPath.AddPath's offset overload is the identical arithmetic, and it is already
                // what strokeBatch below has always used for a cached path. The drag bypass is the
                // same one the individual tier documents: a cache entry is keyed by INDEX and knows
                // nothing of the translated preview clone, so a drag-previewed shape must build
                // fresh or it paints at its pre-drag position.
                if (opts.PathCache is { } mergeCache && !dragOverrides.ContainsKey(index))
                {
                    var (cachedLocal, mRefX, mRefY) = mergeCache.GetOrBuild(index, shape, ps.DbuToUm, counters, out _);
                    if (cachedLocal.IsEmpty) continue;
                    aggregate.AddPath(cachedLocal, ps.X(mRefX), ps.Y(mRefY));
                    counters.ShapesDrawn++;
                    continue;
                }

                using var mergedPath = BuildShapePath(shape, ps, counters);
                if (mergedPath is null || mergedPath.IsEmpty) continue;
                aggregate.AddPath(mergedPath);
                counters.ShapesDrawn++;
                continue;
            }

            // ── Individual per-shape darkening tier — R-L2c-3's path cache applies HERE only ──────
            //
            // brief-drag-fill-still-outline-only.md: LayoutPathCache is keyed by shape INDEX only — a
            // cache hit returns the (LocalPath, RefX, RefY) built the LAST time that index was drawn,
            // never comparing the `shape` argument against what produced the cached entry. During a
            // live move-drag, `shape` above is the translated preview clone (`dragOverrides[index]`),
            // but its geometry is translation-invariant in shape-LOCAL space, so a cache hit silently
            // reused the PRE-drag RefX/RefY — painting the fill+geometry-stroke at the shape's original,
            // undragged position while `DrawSelectionOutlines` (never cached, rebuilt fresh every frame
            // from the same translated shape) correctly tracked the cursor. Net effect: a stationary
            // filled shape left behind, with only its accent SELECTION outline visibly moving — exactly
            // "the ghost is still an outline during dragging," and it explains why instances (whose own
            // cache, `_cellCompileCache` in LayoutRenderer.Instances.cs, caches only translation-
            // invariant COMPILED GEOMETRY and always recomputes the placement matrix fresh per frame)
            // never showed this. Fix: bypass the cache for a shape currently being drag-previewed — a
            // drag selection is always small, so this costs nothing, and it matches this codebase's
            // existing "drags never touch a cache" rule already applied to the L2b spatial index.
            if (opts.PathCache is { } cache && !dragOverrides.ContainsKey(index))
            {
                var (localPath, refX, refY) = cache.GetOrBuild(index, shape, ps.DbuToUm, counters, out _);
                if (localPath.IsEmpty) continue;

                float dx = ps.X(refX), dy = ps.Y(refY);
                counters.ShapesDrawn++;
                counters.DrawCalls++;
                canvas.Save();
                canvas.Translate(dx, dy);
                canvas.DrawPath(localPath, fillPaint);
                canvas.Restore();
                strokeBatch.AddPath(localPath, dx, dy);
            }
            else
            {
                using var shapePath = BuildShapePath(shape, ps, counters);
                if (shapePath is null || shapePath.IsEmpty) continue;

                counters.ShapesDrawn++;
                counters.DrawCalls++;
                canvas.DrawPath(shapePath, fillPaint);
                strokeBatch.AddPath(shapePath);
            }
        }

        if (!aggregate.IsEmpty)
        {
            counters.DrawCalls++;
            canvas.DrawPath(aggregate, fillPaint);
            strokeBatch.AddPath(aggregate);
        }

        // One fill for every hairline path on this layer, at the stroke's own solid alpha, and never
        // fed to strokeBatch — the widened geometry already covers what the outline would have.
        if (!elided.IsEmpty)
        {
            using var elidedPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(255) };
            counters.DrawCalls++;
            canvas.DrawPath(elided, elidedPaint);
        }

        if (!strokeBatch.IsEmpty)
        {
            counters.DrawCalls++;
            canvas.DrawPath(strokeBatch, strokePaint);
        }
    }

    /// <summary>
    /// Whether a <see cref="PathShape"/>'s centreline starts and ends at different points — the gate on
    /// the hairline tier, and it is about HOLES, not about tidiness.
    ///
    /// <para>An OPEN centreline strokes to a capsule: one contour, no interior. Any number of those can
    /// be merged into a single batched path and filled once, because under the non-zero winding rule a
    /// union of same-wound simple contours is exactly their union.</para>
    ///
    /// <para>A CLOSED centreline strokes to a RING — an outer contour plus a hole. Batched, the shared
    /// path is filled NonZero, so contour ORIENTATION across independently built shapes starts to
    /// matter and one shape's hole is cancelled by another's oppositely-wound contour. (Measured, not
    /// assumed: one ring alone, two nested rings wound the same way, two coincident rings and three
    /// nested rings are all fine — two NESTED rings of OPPOSITE winding are not, and a Gerber traces
    /// each outline in whatever direction its source tool emitted.) That is not hypothetical. Every
    /// board outline in the owner's panel is a closed 5-point path one mil wide (the largest is the
    /// 194 x 115 mm panel border), and batching them turned each board into a solid black rectangle
    /// covering everything inside it. A closed path therefore stays on the ordinary fill-plus-outline
    /// route, where it is drawn on its own and its hole is safe. There are a handful of them per file
    /// against tens of thousands of open segments, so nothing measurable is given up.</para>
    /// </summary>
    private static bool IsOpenCentreline(PathShape p) =>
        p.Xy.Length >= 4 && (p.Xy[0] != p.Xy[^2] || p.Xy[1] != p.Xy[^1]);

    /// <summary>R-L2c-1's minimal rect — the shape's real (sub-pixel) bbox, clamped up to at least
    /// <see cref="MinimalRectDevicePixels"/> per side so it survives rasterization, centered on the
    /// shape's own bbox center so a clamp never shifts a cluster's apparent position.</summary>
    private static void AddMinimalRect(SKPath aggregate, Bbox bb, PathSpace ps, double scaleUm)
    {
        var rect = NormalizedRect(ps.X(bb.MinX), ps.Y(bb.MinY), ps.X(bb.MaxX), ps.Y(bb.MaxY));
        float halfMin = (float)(0.5 * MinimalRectDevicePixels / System.Math.Max(scaleUm, 1e-12));
        float cx = (rect.Left + rect.Right) / 2f, cy = (rect.Top + rect.Bottom) / 2f;
        float w = System.Math.Max(rect.Width, halfMin * 2f);
        float h = System.Math.Max(rect.Height, halfMin * 2f);
        aggregate.AddRect(new SKRect(cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f));
    }

    // ── Selection outline + marquee (L1c) ───────────────────────────────────────

    /// <summary>Accent outline for every selected shape, drawn above every layer, batched into one
    /// stroked path. Never touches fill — the layer color stays the information the user reads.</summary>
    private static void DrawSelectionOutlines(SKCanvas canvas, LayoutView view, IReadOnlyList<int> selected,
        IReadOnlyDictionary<int, LayoutShape> dragOverrides, LayoutRenderTheme theme, PathSpace ps, double scaleUm,
        LayoutPortDirection.ConductorLookup? conductorAt = null,
        IReadOnlyList<(long X, long Y, PlanarPortKind Kind)>? internalPortMarks = null)
    {
        using var batch = new SKPath();
        foreach (var idx in selected)
        {
            if (idx < 0 || idx >= view.Shapes.Count) continue;
            var original = view.Shapes[idx];
            var shape = dragOverrides.TryGetValue(idx, out var ov) ? ov : original;

            // A port's outline follows its MARK, and the mark's position follows its TYPE — asked of
            // `original` for the same reason DrawLayer does: a drag override is the same port
            // previewed elsewhere, and moving a port cannot retype it.
            bool internalMark = shape is LabelShape { IsPort: true } && original is LabelShape stored
                                && MarkKindOf(internalPortMarks, stored) != PlanarPortKind.Edge;

            using var outline = BuildOutlinePathForSelection(shape, ps, conductorAt, internalMark);
            if (outline is null || outline.IsEmpty) continue;
            batch.AddPath(outline);
        }
        if (batch.IsEmpty) return;

        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, SelectionStrokeDevicePixels),
            Color = theme.Selection,
        };
        canvas.DrawPath(batch, paint);
    }

    /// <summary>
    /// Owner report: the label selection box was not centered on R0/R180 text and rendered in the
    /// completely wrong spot for R90/R270 — <see cref="LayoutHitTest"/>'s hand-derived approximate
    /// footprint (duplicated here, since neither layer originally had font metrics) had BOTH a rotation
    /// sign bug (R90/R270's local "top-right" corner landed on the wrong side of the anchor — see
    /// <c>LayoutHitTest.LabelHitBbox</c>'s fix for the corrected corner table) and a loose fit (a fixed
    /// 0.62-of-height-per-character estimate, not the real rendered width). Unlike hit-testing (which
    /// must stay framework-free), this file already has SkiaSharp — so the selection box now measures
    /// the REAL font metrics via <see cref="SKFont.MeasureText(string, out SKRect)"/> (cheap: one
    /// measurement call, not a full glyph-outline extraction like <c>LayoutTextOutline</c> uses for
    /// flattening) and transforms all four corners through the exact same rotation
    /// <c>DrawLabelText</c>/<c>LayoutTextOutline</c> use, so it can never drift from what's actually
    /// rendered and is correct for every rotation by construction, not by a re-derived formula.
    /// </summary>
    /// <remarks>
    /// <b>PORTS no longer come through here for their selection outline</b> (2026-08-25) — that is
    /// <c>LayoutHitTest.PortPickBbox</c> now, so the hit region and the highlight cannot disagree.
    /// This measures where the GLYPHS are, which is still what an ordinary label's outline wants and
    /// what the clipboard's painted-bounds pass needs for every label including a port.
    /// </remarks>
    /// <param name="centred">Mirror <see cref="DrawLabelText"/>'s centring — pass
    /// <c>label.IsPort</c>. A port's name is drawn centred on its anchor, so measuring it
    /// left-anchored puts the box half a text-width off, which for the clipboard's painted bounds is
    /// a port cropped at the edge of the page.</param>
    internal static Bbox? MeasureLabelWorldBbox(LabelShape label, bool centred = false)
    {
        if (string.IsNullOrEmpty(label.Text) || label.Height <= 0) return null;

        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(label.Style), label.Height);
        float advance = font.MeasureText(label.Text, out SKRect bounds);
        if (bounds.Width <= 0 && bounds.Height <= 0) return null;

        // DrawLabelText's own anchor offsets, in its own (pre-rotation, Y-down) local frame, taken from
        // the SAME resolver it draws with. A text aligner shifts the origin by a fraction of the ADVANCE
        // width (not of the tight bounds, which is only what the glyphs happen to ink).
        var (align, baselineDy) = LayoutTextOutline.ResolveLabelAnchor(label, font, centred);
        float alignDx = align switch
        {
            SKTextAlign.Center => -advance / 2f,
            SKTextAlign.Right  => -advance,
            _                  => 0f,
        };
        if (alignDx != 0f || baselineDy != 0f) bounds.Offset(alignDx, baselineDy);

        // Mirrors DrawLabelText's rotation exactly (see that method's own Y-down-path-space
        // rotation-sign comment) — all four corners, since a rotated rect's world-space bbox isn't
        // just its two "opposite" corners once rotation is involved.
        float rotationDeg = -(float)label.RotationDegrees;
        var m = SKMatrix.CreateRotationDegrees(rotationDeg);
        SKPoint[] corners =
        [
            new SKPoint(bounds.Left, bounds.Top), new SKPoint(bounds.Right, bounds.Top),
            new SKPoint(bounds.Right, bounds.Bottom), new SKPoint(bounds.Left, bounds.Bottom),
        ];

        Bbox bb = Bbox.Empty;
        foreach (var c in corners)
        {
            var r = m.MapPoint(c);
            long dbuX = label.X + (long)System.Math.Round(r.X);
            long dbuY = label.Y - (long)System.Math.Round(r.Y); // the one Y-flip: path space is Y-down, DBU is Y-up
            bb = bb.Union(new Bbox(dbuX, dbuY, dbuX, dbuY));
        }
        return bb;
    }

    /// <summary>Same geometry every other draw call uses, plus the two shapes that have no direct
    /// <see cref="BuildShapePath"/> entry: <c>Label</c> (real font metrics — see
    /// <see cref="MeasureLabelWorldBbox"/>) and <c>Via</c> (a circle at its pad radius).</summary>
    private static SKPath? BuildOutlinePathForSelection(LayoutShape shape, PathSpace ps,
        LayoutPortDirection.ConductorLookup? conductorAt = null, bool internalMark = false)
    {
        switch (shape)
        {
            // A PORT's outline is its PICK REGION — the MARK it draws, plus padding — taken from the
            // hit test itself rather than measured again here. See LayoutHitTest.PortPickBbox for the
            // report that forced this. Every other label keeps the real-font-metrics glyph box below.
            case LabelShape { IsPort: true } port:
            {
                var pb = LayoutHitTest.PortPickBbox(port, LayoutPortDirection.Resolve(conductorAt, port),
                                                    atAnchor: internalMark);
                if (pb.IsEmpty) return null;
                var portPath = new SKPath();
                portPath.AddRect(NormalizedRect(ps.X(pb.MinX), ps.Y(pb.MinY), ps.X(pb.MaxX), ps.Y(pb.MaxY)));
                return portPath;
            }

            case LabelShape label:
            {
                if (MeasureLabelWorldBbox(label) is not { IsEmpty: false } bb) return null;
                var path = new SKPath();
                path.AddRect(NormalizedRect(ps.X(bb.MinX), ps.Y(bb.MinY), ps.X(bb.MaxX), ps.Y(bb.MaxY)));
                return path;
            }

            case ViaShape via:
            {
                var path = new SKPath();
                path.AddCircle(ps.X(via.X), ps.Y(via.Y), ps.Len(via.PadSize / 2.0));
                return path;
            }

            // Bitmaps have no BuildShapePath entry (they are not geometry — R-bmp-3) but must still
            // show a selection outline (§3: full participation in select/move/scale).
            case BitmapShape bmp:
                return BuildBitmapPlacementRectPath(bmp, ps);

            default:
                return BuildShapePath(shape, ps);
        }
    }

    /// <summary>L1i R-L1i-4: solid = enclose (left-to-right), dashed = crossing (right-to-left) — the
    /// standard CAD affordance. Now that the highlight updates live (<c>Overlay.SelectedIndices</c> is
    /// the marquee preview while a drag is active — see <c>LayoutEditorViewModel.RebuildOverlay</c>),
    /// dragging back across the press point flips mode AND highlight mid-gesture; the rectangle style
    /// is the visible cue that makes that flip legible instead of mysterious.</summary>
    private static void DrawMarquee(SKCanvas canvas, LayoutMarquee m, LayoutRenderTheme theme, PathSpace ps)
    {
        var rect = NormalizedRect(ps.X(m.X1), ps.Y(m.Y1), ps.X(m.X2), ps.Y(m.Y2));

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection.WithAlpha(50) };
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0, Color = theme.Selection.WithAlpha(255),
            PathEffect = m.IsLeftToRight ? null : SKPathEffect.CreateDash([6f, 4f], 0),
        };

        canvas.DrawRect(rect, fillPaint);
        canvas.DrawRect(rect, strokePaint);
    }

    // ── Shape-reshape handles (L1d, docs/design/layout-view.md §6.3) ───────────────────────────

    /// <summary>Device-pixel target for a handle's on-screen size — computed per query from the
    /// current zoom (never cached, never derived from SnapDbu — the exact class of bug the brief's
    /// "Read first" section calls out), via the same <see cref="DevicePixelsToPathSpace"/> helper
    /// the doubled geometry/selection strokes already use.</summary>
    private const double HandleSizeDevicePixels = 8.0;

    /// <summary>
    /// The "grab this point" glyph — L1d's own filled square, in ONE place so the two things that
    /// draw it cannot drift apart.
    ///
    /// <para>A PCell parameter grip reuses it deliberately rather than inventing a shape of its own.
    /// The editor already has a visual language for "this is draggable" and a second one would be a
    /// second thing to learn; the grip's own difference — that it edits a parameter rather than
    /// geometry — is carried by its colour role and by an axis hint that no L1d handle has. An
    /// earlier draft used a hollow diamond and was wrong for a concrete reason worth remembering:
    /// that shape is already L1d's BULGE handle.</para>
    /// </summary>
    private static void DrawGrabSquare(SKCanvas canvas, float cx, float cy, float half, SKPaint paint)
        => canvas.DrawRect(new SKRect(cx - half, cy - half, cx + half, cy + half), paint);

    private static void DrawHandles(SKCanvas canvas, LayoutView view, int shapeIndex,
        IReadOnlyDictionary<int, LayoutShape> dragOverrides, LayoutRenderTheme theme, PathSpace ps, double scaleUm)
    {
        if (shapeIndex < 0 || shapeIndex >= view.Shapes.Count) return;
        var shape = dragOverrides.TryGetValue(shapeIndex, out var ov) ? ov : view.Shapes[shapeIndex];
        var handles = LayoutHandles.Build(shape);
        if (handles.Count == 0) return;

        float half = DevicePixelsToPathSpace(scaleUm, HandleSizeDevicePixels) / 2f;
        float hairline = DevicePixelsToPathSpace(scaleUm, 1.5);

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection };
        using var strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = hairline, Color = theme.Selection };

        foreach (var h in handles)
        {
            float cx = ps.X(h.X), cy = ps.Y(h.Y);
            switch (h.Kind)
            {
                case LayoutHandleKind.Vertex:
                case LayoutHandleKind.Radius:
                case LayoutHandleKind.CornerRadius:
                    // Filled square. Shared with the PCell parameter grip — see
                    // DrawGrabSquare's own note on why that reuse is deliberate.
                    DrawGrabSquare(canvas, cx, cy, half, fillPaint);
                    break;

                case LayoutHandleKind.EdgeMidpoint:
                    // Hollow circle.
                    canvas.DrawCircle(cx, cy, half, strokePaint);
                    break;

                case LayoutHandleKind.Bulge:
                {
                    // Hollow diamond.
                    using var diamond = new SKPath();
                    diamond.MoveTo(cx, cy - half);
                    diamond.LineTo(cx + half, cy);
                    diamond.LineTo(cx, cy + half);
                    diamond.LineTo(cx - half, cy);
                    diamond.Close();
                    canvas.DrawPath(diamond, strokePaint);
                    break;
                }

                case LayoutHandleKind.CubicControl:
                {
                    // Small filled circle, with a thin tangent line to its anchor vertex.
                    var (ax, ay) = CubicControlAnchorWorld(shape, h.Index, h.SubIndex);
                    canvas.DrawLine(ps.X(ax), ps.Y(ay), cx, cy, strokePaint);
                    canvas.DrawCircle(cx, cy, half * 0.6f, fillPaint);
                    break;
                }
            }
        }
    }

    /// <summary>L1h (R-L1h-4/5) — 8 square handles (4 corners + 4 side midpoints) at the selection's
    /// combined bbox, drawn instead of L1d's per-shape handles whenever they're showing.</summary>
    private static void DrawScaleHandles(SKCanvas canvas, LayoutView view, IReadOnlyList<int> selected,
        IReadOnlyDictionary<int, LayoutShape> dragOverrides, LayoutRenderTheme theme, PathSpace ps, double scaleUm)
    {
        var bb = Bbox.Empty;
        foreach (var idx in selected)
        {
            if (idx < 0 || idx >= view.Shapes.Count) continue;
            var shape = dragOverrides.TryGetValue(idx, out var ov) ? ov : view.Shapes[idx];
            bb = bb.Union(LayoutGeometry.BboxOf(shape));
        }
        if (bb.IsEmpty) return;

        var handles = LayoutScaleHandles.Build(bb);
        float half = DevicePixelsToPathSpace(scaleUm, HandleSizeDevicePixels) / 2f;

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection };
        foreach (var h in handles)
        {
            float cx = ps.X(h.X), cy = ps.Y(h.Y);
            canvas.DrawRect(new SKRect(cx - half, cy - half, cx + half, cy + half), fillPaint);
        }
    }

    /// <summary>The vertex a Cubic edge's control point is anchored to — C1 (SubIndex 0) anchors to
    /// the edge's start vertex, C2 (SubIndex 1) to its end vertex.</summary>
    private static (long X, long Y) CubicControlAnchorWorld(LayoutShape shape, int edgeIndex, int subIndex)
    {
        var xy = LayoutShapeEditing.XyOf(shape);
        int n = xy.Length / 2;
        bool closed = LayoutShapeEditing.IsClosed(shape);
        int vertexIndex = subIndex == 0 ? edgeIndex : (closed ? (edgeIndex + 1) % n : edgeIndex + 1);
        return (xy[2 * vertexIndex], xy[2 * vertexIndex + 1]);
    }

    // ── Shape -> path-space SKPath ───────────────────────────────────────────────

    /// <summary>Internal (not private) so <see cref="LayoutPathCache"/> can build a shape's path in
    /// LOCAL space (a <see cref="PathSpace"/> whose origin is the shape's own bbox min, not the
    /// per-frame one) — see that type's doc comment for why (R-L2c-3).</summary>
    internal static SKPath? BuildShapePath(LayoutShape shape, PathSpace ps, LayoutFrameCounters? counters = null)
    {
        // Path (a trace) needs its own outline construction (centerline -> GetFillPath), not the
        // generic per-shape builder below.
        if (shape is PathShape trace)
            return BuildPathOutline(trace, ps, counters);

        var path = new SKPath();
        if (counters is not null) counters.PathsConstructed++;
        switch (shape)
        {
            case RectShape r:
                path.AddRect(NormalizedRect(ps.X(r.X1), ps.Y(r.Y1), ps.X(r.X2), ps.Y(r.Y2)));
                break;

            case PolygonShape p:
                AddPolygonPath(path, p.Xy, ps);
                AddHoleRings(path, p.Xy, p.Holes, ps);
                break;

            case RoundedRectShape rr:
            {
                var rect = NormalizedRect(ps.X(rr.X1), ps.Y(rr.Y1), ps.X(rr.X2), ps.Y(rr.Y2));
                float radius = ps.Len(rr.CornerRadius);
                path.AddRoundRect(rect, radius, radius);
                break;
            }

            case CircleShape c:
                path.AddCircle(ps.X(c.Cx), ps.Y(c.Cy), ps.Len(c.R));
                break;

            case CurveShape curve:
                AddEdgeListPath(path, curve.Xy, curve.Edges, closed: true, ps);
                AddHoleRings(path, curve.Xy, curve.Holes, ps);
                break;

            case ViaShape via:
                // docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.1: render as an ANNULUS, pad
                // filled in the layer colour with the barrel punched out via an opposite-winding inner
                // circle (the same "hole" technique AddHoleRings already uses for Polygon/Curve, just
                // via SKPathDirection instead of a reversed vertex list, since a circle has no vertex
                // list of its own) — a solid disc would hide exactly the pad/drill relationship that
                // matters (R-via-1's whole reason PadSize and DrillSize are two independent fields).
                path.AddCircle(ps.X(via.X), ps.Y(via.Y), ps.Len(via.PadSize / 2.0), SKPathDirection.Clockwise);
                if (via.DrillSize > 0)
                    path.AddCircle(ps.X(via.X), ps.Y(via.Y), ps.Len(via.DrillSize / 2.0), SKPathDirection.CounterClockwise);
                break;

            default:
                path.Dispose();
                return null;
        }

        return NormalizeOuterWinding(path, shape);
    }

    /// <summary>R-fix-1 (docs/sonnet-briefs/brief-layout-testing-fixes.md, item 1) — every OUTER ring this
    /// builder produces is normalized to the SAME absolute winding direction before it can ever reach a
    /// BATCHED path (the instance-compiled aggregate in <c>LayoutRenderer.Instances.cs</c>'s
    /// <c>CompileCell</c>, and the L2c LOD/merge-tier aggregate in <c>DrawLayer</c> — both call THIS
    /// method, so normalizing here fixes both by construction, per the brief's own diagnosis). Without
    /// this, a shape's vertex order is whatever the user drew, a boolean produced, or an importer
    /// emitted — two overlapping outer contours with OPPOSITE winding cancel to nothing under Skia's
    /// default Winding fill rule once merged into one <c>SKPath</c>.
    ///
    /// A single shape drawn on its own (the individual, non-aggregated tier) never showed this bug —
    /// one simple contour fills identically whether CW or CCW — which is why normalizing here is a
    /// pure no-op for that tier and only matters once paths from DIFFERENT shapes are combined.
    ///
    /// <c>Polygon</c>/<c>Curve</c> are the only shape kinds whose outer-ring winding is DATA-driven (a
    /// user/boolean/import vertex order); <c>Rect</c>/<c>RoundedRect</c>/<c>Circle</c>/<c>Via</c> are
    /// built via Skia's own primitives, whose winding is a FIXED Skia-internal convention independent of
    /// any data here — empirically confirmed (a real pixel-oracle test against a Rect/Polygon overlap,
    /// not assumed) to already agree with a Polygon/Curve ring normalized to
    /// "<c>SignedArea(xy) &lt; 0</c> in DBU space" (reverse whenever it is NOT), so only Polygon/Curve
    /// need an explicit check.
    ///
    /// Reversing the WHOLE path (via Skia's own <c>AddPathReverse</c>, not a hand-rolled vertex/bulge
    /// reversal) rather than re-deriving the outer ring's vertex order is deliberate: it is correct for
    /// curved edges (arc bulge, cubic control points) and holes with ZERO extra logic, and preserves
    /// whatever relative outer-vs-hole relationship <see cref="AddHoleRings"/> already established —
    /// reversing every contour in a path together leaves their RELATIVE winding relationship unchanged,
    /// only the absolute direction flips.</summary>
    private static SKPath NormalizeOuterWinding(SKPath path, LayoutShape shape)
    {
        long[]? outerXy = shape switch
        {
            PolygonShape p => p.Xy,
            CurveShape c => c.Xy,
            _ => null, // Rect/RoundedRect/Circle/Via: fixed Skia winding, already consistent (see above)
        };
        if (outerXy is null) return path;
        if (LayoutGeometry.SignedArea(outerXy) < 0) return path;

        var reversed = new SKPath();
        reversed.AddPathReverse(path);
        path.Dispose();
        return reversed;
    }

    private static SKRect NormalizedRect(float x1, float y1, float x2, float y2) =>
        new(System.Math.Min(x1, x2), System.Math.Min(y1, y2), System.Math.Max(x1, x2), System.Math.Max(y1, y2));

    /// <summary>A bitmap's placement rect in path space — the one place this is computed, shared by
    /// <see cref="DrawBitmapShapes"/> (pixel draw), the ghost preview, and the selection outline.</summary>
    private static SKRect BitmapPlacementRect(BitmapShape bmp, PathSpace ps) =>
        NormalizedRect(ps.X(bmp.X), ps.Y(bmp.Y), ps.X(bmp.X + bmp.W), ps.Y(bmp.Y + bmp.H));

    private static SKPath BuildBitmapPlacementRectPath(BitmapShape bmp, PathSpace ps)
    {
        var path = new SKPath();
        path.AddRect(BitmapPlacementRect(bmp, ps));
        return path;
    }

    private static void AddPolygonPath(SKPath path, long[] xy, PathSpace ps)
    {
        int n = xy.Length / 2;
        if (n < 2) return;
        path.MoveTo(ps.X(xy[0]), ps.Y(xy[1]));
        for (int i = 1; i < n; i++)
            path.LineTo(ps.X(xy[2 * i]), ps.Y(xy[2 * i + 1]));
        path.Close();
    }

    /// <summary>Appends every hole ring (§3.1a) to <paramref name="path"/>, in the SAME contour path
    /// as the outer shape so Skia's default <c>Winding</c> fill rule can cut them out — which requires
    /// each hole to be wound OPPOSITE the outer ring. Rather than trust that whatever produced
    /// <paramref name="holes"/> already stored them with the opposite winding, this compares signed
    /// area and reverses on the fly — cheap, and correct regardless of the construction path (normal
    /// Clipper2 output, a hand-edited file, or a future paste/import). <paramref name="outerRef"/> is
    /// the outer ring's own vertex list (its polygon-area sign is a reliable proxy for a Curve's
    /// overall winding sense even when some edges are curved).</summary>
    private static void AddHoleRings(SKPath path, long[] outerRef, List<long[]>? holes, PathSpace ps)
    {
        if (holes is not { Count: > 0 }) return;
        bool outerCcw = LayoutGeometry.SignedArea(outerRef) >= 0;
        foreach (var hole in holes)
        {
            bool holeCcw = LayoutGeometry.SignedArea(hole) >= 0;
            AddPolygonPath(path, holeCcw == outerCcw ? ReverseRing(hole) : hole, ps);
        }
    }

    private static long[] ReverseRing(long[] xy)
    {
        int n = xy.Length / 2;
        var result = new long[xy.Length];
        for (int i = 0; i < n; i++)
        {
            result[2 * i]     = xy[2 * (n - 1 - i)];
            result[2 * i + 1] = xy[2 * (n - 1 - i) + 1];
        }
        return result;
    }

    /// <summary>Builds an open or closed edge-list path in path space — shared by <c>Curve</c> and
    /// the centerline of <c>Path</c> (docs/design/layout-view.md §3.2 R9a, "one edge vocabulary").</summary>
    private static void AddEdgeListPath(SKPath path, long[] xy, List<LayoutEdge>? edges, bool closed, PathSpace ps)
    {
        int n = xy.Length / 2;
        if (n == 0) return;
        if (n == 1) { path.MoveTo(ps.X(xy[0]), ps.Y(xy[1])); return; }

        path.MoveTo(ps.X(xy[0]), ps.Y(xy[1]));

        int edgeCount = closed ? n : n - 1;
        for (int i = 0; i < edgeCount; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            long wx0 = xy[2 * i], wy0 = xy[2 * i + 1];
            long wx1 = xy[2 * j], wy1 = xy[2 * j + 1];
            var edge = edges is not null && i < edges.Count ? edges[i] : null;
            AppendEdge(path, wx0, wy0, wx1, wy1, edge, ps);
        }

        if (closed) path.Close();
    }

    /// <summary>Appends one edge to <paramref name="path"/>. <paramref name="wx0"/>/<paramref name="wy0"/>/
    /// <paramref name="wx1"/>/<paramref name="wy1"/> are the ORIGINAL DBU (Y-up, world) endpoints —
    /// arc parameters must be derived from these, not from already-flipped path-space floats (see the
    /// type-level doc comment for why). Line and Cubic edges have no orientation sensitivity and are
    /// transformed directly.</summary>
    private static void AppendEdge(SKPath path, long wx0, long wy0, long wx1, long wy1, LayoutEdge? edge, PathSpace ps)
    {
        float bx = ps.X(wx1), by = ps.Y(wy1);

        switch (edge?.Kind ?? EdgeKind.Line)
        {
            case EdgeKind.Line:
                path.LineTo(bx, by);
                break;

            case EdgeKind.Arc:
            {
                var arc = LayoutArc.FromBulge(wx0, wy0, wx1, wy1, edge!.Bulge);   // world space (Y-up)
                if (arc.R <= 0) { path.LineTo(bx, by); break; }

                float pcx = ps.X(arc.Cx), pcy = ps.Y(arc.Cy);
                float pr  = ps.Len(arc.R);
                var rect = new SKRect(pcx - pr, pcy - pr, pcx + pr, pcy + pr);

                // Y was flipped going from world to path space (a reflection), which reverses the
                // sense of "increasing angle" — negate both angles once here, at the single point
                // that converts to Skia's own (path-space-native) degrees/clockwise convention.
                float startDeg = (float)(-arc.StartAngle * 180.0 / System.Math.PI);
                float sweepDeg = (float)(-arc.Sweep      * 180.0 / System.Math.PI);
                path.ArcTo(rect, startDeg, sweepDeg, forceMoveTo: false);
                break;
            }

            case EdgeKind.Cubic:
            {
                float c1x = ps.X(edge!.C1X), c1y = ps.Y(edge.C1Y);
                float c2x = ps.X(edge.C2X),  c2y = ps.Y(edge.C2Y);
                path.CubicTo(c1x, c1y, c2x, c2y, bx, by);
                break;
            }
        }
    }

    // ── PathShape (trace): centerline -> outline via GetFillPath (§1.5 of the L1a brief) ────────

    /// <summary>
    /// Builds a <c>PathShape</c>'s DISPLAY outline — curves stay curves, via Skia's own stroker plus
    /// <see cref="SKPath.Simplify"/>. <c>GetFillPath</c> does not produce a single merged contour: Skia's
    /// stroker emits one contour per segment plus a wedge per join, all overlapping at every bend. That
    /// is invisible when FILLING (the default Winding fill rule composites the overlaps exactly once,
    /// which is why nothing looked wrong for a solid trace) and very visible when hairline-STROKING the
    /// same path (<c>DrawLayer</c>'s batched outline stroke traces every contour edge in the path,
    /// including the internal boundaries where segment quads and join wedges abut one another — those
    /// internal boundaries are the seam artifacts a bent trace showed at each vertex). <c>Simplify</c>
    /// unions the overlapping contours into the real silhouette (plus any genuine holes), so both the
    /// fill and the (now correctly seam-free) stroke are built from the SAME single-contour path — do
    /// not keep an unsimplified copy for the fill and a simplified one for the stroke.
    ///
    /// <b>This is deliberately a SEPARATE outline from L1e's Clipper2 geometry offset, and must stay
    /// that way.</b> Clipper2 operates on flattened (polygonal) geometry, so a curved trace's Clipper2
    /// outline is polygonal — correct for booleans/DRC/Gerber export, wrong for display, which needs
    /// the adaptive, zoom-correct curve tessellation §3.2 R9c specifies. Two outlines, two purposes:
    /// display (here, Skia stroker + Simplify, curves stay curves) vs. geometry (L1e, Clipper2 offset
    /// on the flattened centerline, exact and integer). Do not "unify" them later.
    ///
    /// <c>// L2: cache with the shape path</c> — <c>Simplify</c> is an <c>SkPathOps</c> call, meaningfully
    /// more expensive than plain path construction; fine at L1 scale (paths rebuild every frame anyway),
    /// but it must ride along with L2's per-shape path cache rather than recompute every frame.
    /// </summary>
    internal static SKPath? BuildPathOutline(PathShape trace, PathSpace ps, LayoutFrameCounters? counters = null)
    {
        int n = trace.Xy.Length / 2;
        if (n < 2) return null;

        var xy = trace.End == PathEndStyle.Extended ? ExtendedCenterline(trace.Xy, trace.Width) : trace.Xy;

        using var centerline = new SKPath();
        if (counters is not null) counters.PathsConstructed++;
        AddEdgeListPath(centerline, xy, trace.Edges, closed: false, ps);

        var cap = trace.End switch
        {
            PathEndStyle.Round  => SKStrokeCap.Round,
            PathEndStyle.Square => SKStrokeCap.Square,
            _                   => SKStrokeCap.Butt,   // Flush, and Extended (handled via the pre-extended centerline above)
        };

        using var strokeForFill = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = ps.Len(trace.Width),
            StrokeCap   = cap,
            StrokeJoin  = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        var outline = new SKPath();
        if (counters is not null) counters.PathsConstructed++;
        strokeForFill.GetFillPath(centerline, outline);

        // L2: cache with the shape path — Simplify is an SkPathOps call, not free.
        var simplified = new SKPath();
        if (counters is not null) counters.PathsConstructed++;
        if (outline.Simplify(simplified))
        {
            outline.Dispose();
            return simplified;
        }
        simplified.Dispose();
        return outline;   // degenerate input (e.g. zero-width / duplicate-point) — fall back rather than dropping the trace
    }

    /// <summary>Extends the first/last vertex of a centerline outward by <c>width/2</c> along the
    /// tangent to its neighbor — the DBU-space equivalent of an "Extended" end cap, done before any
    /// transform so the extension length is exact in world units regardless of zoom.</summary>
    private static long[] ExtendedCenterline(long[] xy, long width)
    {
        int n = xy.Length / 2;
        if (n < 2) return xy;
        long half = width / 2;
        var result = (long[])xy.Clone();
        ExtendVertexTowardOutward(result, 0, 1, half);
        ExtendVertexTowardOutward(result, n - 1, n - 2, half);
        return result;
    }

    private static void ExtendVertexTowardOutward(long[] xy, int vertexIdx, int neighborIdx, long amount)
    {
        double vx = xy[2 * vertexIdx], vy = xy[2 * vertexIdx + 1];
        double nx = xy[2 * neighborIdx], ny = xy[2 * neighborIdx + 1];
        double dx = vx - nx, dy = vy - ny;
        double len = System.Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return;
        double ux = dx / len, uy = dy / len;
        xy[2 * vertexIdx]     = (long)System.Math.Round(vx + ux * amount);
        xy[2 * vertexIdx + 1] = (long)System.Math.Round(vy + uy * amount);
    }

    // ── Label (annotation / port marker) — rendered as text, not fill+stroke ────

    /// <summary>Stroke width of a port marker, in DEVICE pixels — constant on screen at any zoom, like
    /// every other affordance in this editor, and deliberately heavier than the geometry hairline so a
    /// port reads as an annotation rather than as more metal.</summary>
    internal const float PortMarkerStrokeDevicePixels = 2.0f;

    /// <summary>
    /// How long the direction arrow is, as a fraction of the port's own width — its PREFERRED length,
    /// before the conductor gets a say.
    ///
    /// <para><b>Shorter than it was, because the arrow moved.</b> It used to run from the reference
    /// plane INTO the metal, where two thirds of the port width was a proportion of the thing it was
    /// lying on. It now approaches the plane from OUTSIDE and its head lands on the bar, so the same
    /// fraction put a long tail out in the empty space beside the part — longest, in the way this
    /// most shows, exactly where the port is widest.</para>
    /// </summary>
    private const double PortArrowLengthOverWidth = LayoutPortDirection.ArrowLengthOverWidth;

    /// <summary>
    /// The hard ceiling on the arrow's reach, as a fraction of the conductor's own extent ALONG the
    /// direction. Owner report, 2026-08-09: <i>"the arrow head can sometimes extend beyond the metal
    /// shape that the port is connected to... can the arrow never extend beyond the metal in the
    /// direction it's pointing to?"</i> — the reach is <c>min(width-preferred, length × this)</c>,
    /// so a short conductor clamps the arrow rather than the arrow dwarfing the conductor.
    ///
    /// <para>The arrow now approaches the plane from outside rather than running into the metal, so
    /// this no longer governs whether it OVERRUNS anything — it governs proportion: a small part does
    /// not get a long tail, which is the same judgement applied to the mark's new position.</para>
    /// </summary>
    private const double PortArrowMaxLengthOverConductorLength = LayoutPortDirection.ArrowMaxLengthOverConductorLength;

    /// <summary>
    /// The arrowhead's barb length as a fraction of the arrow's FINAL reach — not of the port width.
    /// That distinction is the second half of the same report (<i>"the size of the arrow head seems
    /// to be a function of the port width; this makes the arrow appear too big for short but wide
    /// edge shapes"</i>): tying the head to the reach means the whole arrow shrinks together the
    /// moment a short conductor clamps it, instead of a stub with an enormous head on it.
    /// </summary>
    private const double PortArrowBarbOverReach = LayoutPortDirection.ArrowBarbOverReach;

    /// <summary>
    /// A second, independent ceiling on the barb, as a fraction of the port WIDTH. The reach-based
    /// rule alone still grows without bound on a conductor that is long AND wide, where the head can
    /// get comparable to the reference-plane bar it sits against — and a head that rivals the bar
    /// competes with the one mark that is load-bearing.
    /// </summary>
    private const double PortArrowMaxBarbOverWidth = LayoutPortDirection.ArrowMaxBarbOverWidth;

    /// <summary>Length of the serif turned back from each end of the reference-plane bar, as a
    /// fraction of the port width. Small, and turned AWAY from the metal, so the bar reads as a
    /// plane cutting the conductor rather than as one more piece of geometry lying on it.</summary>
    private const double PortPlaneSerifOverWidth = LayoutPortDirection.PlaneSerifOverWidth;

    /// <summary>How far a port marker's colour is pushed away from the canvas background, on top of
    /// its layer's own colour (owner request, 2026-08-09: "make it darker than its layer color in
    /// light mode and lighter than its layer color in dark mode"). Deliberately stronger than the
    /// snap marker's own tint — a snap marker is transient and a port is permanent artwork the user
    /// has to pick out from the metal it sits on.</summary>
    private const double PortMarkerContrastTintAmount = 0.45;

    /// <summary>
    /// How long the direction arrow is, and how long its barbs are, in DBU.
    ///
    /// <para><b>The arrow is bounded by the metal it points into, never by its own preferred size.</b>
    /// A wide, short pad used to get an arrow two thirds of its WIDTH long — which on a pad shorter
    /// than it is wide runs straight out the far end, with a head sized to match (owner report,
    /// 2026-08-09). <c>reach ≤ LengthDbu × PortArrowMaxLengthOverConductorLength &lt; LengthDbu</c>,
    /// so overrunning the conductor is arithmetically impossible rather than merely unlikely.</para>
    ///
    /// <para>Pure and separate from the drawing so the claim can be asserted directly, not inferred
    /// from pixels — though there is a pixel oracle for it too.</para>
    /// </summary>
    internal static (double Reach, double BarbLen) PortArrowGeometry(LayoutPortDirection.PortHint hint)
        => LayoutPortDirection.ArrowGeometry(hint);

    /// <summary>
    /// An EM port's marker: a bar ACROSS the conductor (how wide the port is) and an arrow along the
    /// direction current flows INTO the structure. Both are the answer to a question the port label's
    /// text cannot carry, and before this a port drew as text alone (owner report, 2026-08-09).
    ///
    /// <para>Drawn in world/path space, so it pans and zooms with the artwork — unlike the PCell pin
    /// overlay, which is a constant-pixel screen-space dot. A port's width IS a physical dimension
    /// the user is judging; a pin's position is not.</para>
    ///
    /// <para>A port sitting on no conductor at all, with no direction stated, draws no marker — there
    /// is nothing to be a width of and nothing to point along, and <c>EmPortExtraction</c> will refuse
    /// it by name at run time. Drawing a guessed arrow there would be the one thing worse than
    /// drawing none.</para>
    /// </summary>
    /// <summary>
    /// Half-width of the internal gap's own break, as a fraction of the port width. The two bars sit
    /// this far either side of the cut, so the break between them is <c>2 ×</c> this — wide enough to
    /// read as a deliberate discontinuity at a glance and narrow enough that the pair still reads as
    /// ONE mark rather than as two separate ports.
    /// </summary>
    private const double InternalGapHalfOverWidth = LayoutPortDirection.GapHalfOverWidth;

    /// <summary>
    /// How far the internal gap's bars are turned back along the conductor at each end, as a fraction
    /// of the port width. The bracket shape — a bar with BOTH ends turned toward its own metal — is
    /// what distinguishes a gap from an edge port's plane, whose single serif turns AWAY.
    ///
    /// <para><b>Sized by looking at it, not by taste.</b> At the edge port's own 0.12 the flanges were
    /// invisible in a rendered figure and the pair read as two plain parallel lines — which is the
    /// one thing this mark must not be, since two plain lines is what a reader would take for two
    /// reference planes. Large enough that the brackets are the first thing seen.</para>
    /// </summary>
    private const double InternalGapFlangeOverWidth = LayoutPortDirection.GapFlangeOverWidth;

    /// <summary>
    /// How far each bar runs PAST the conductor at both ends, as a fraction of the port width.
    ///
    /// <para><b>This is what makes the brackets visible at all, and it is a fix rather than a
    /// flourish.</b> A gap's bars are drawn mid-conductor, so their ends sit exactly ON the metal's
    /// own outline — and a flange turned along the conductor from a point on that outline is drawn
    /// on top of the outline and cannot be seen. Measured, in a rendered figure: at zero overhang the
    /// mark read as two plain parallel lines however long the flanges were made. An edge port has no
    /// such problem, because its bar's ends are at the end of the structure with background around
    /// them.</para>
    /// </summary>
    private const double InternalGapOverhangOverWidth = LayoutPortDirection.GapOverhangOverWidth;

    /// <summary>The internal port's ring, as a fraction of the conductor width — large enough to clear
    /// the port's own centred label text, which is why it is sized off the conductor.</summary>
    private const double InternalPortRingOverWidth = LayoutPortDirection.RingOverWidth;

    /// <summary>Ring to first ground bar.</summary>
    private const double InternalPortStemOverWidth = 0.16;

    /// <summary>Half-widths of the three ground bars, widest first.</summary>
    private static readonly double[] InternalPortGroundBarsOverWidth = [0.34, 0.21, 0.10];

    /// <summary>Spacing between those bars.</summary>
    private const double InternalPortGroundPitchOverWidth = 0.13;

    /// <summary>
    /// <b>The half-width of the gap the SOLVER will actually use</b>, in DBU, or null when there is no
    /// mesh to ask.
    ///
    /// <para>A delta gap is cut on a mesh GRIDLINE and drives the rooftop spanning the pair of cells
    /// either side of it — so the length of conductor the excitation occupies is those two cells, and
    /// it is set by the mesh settings rather than by anything drawn. Once a mesh exists, drawing the
    /// break at the fixed fraction would be drawing a number that means nothing next to a number that
    /// does, with the mesh's own gridlines visible underneath it.</para>
    ///
    /// <para><b>Half-width per SIDE, not one number for both</b>, because a graded mesh's two cells
    /// need not be the same size — and at a gap near a conductor edge they routinely are not. Each
    /// bracket lands on its own cell's outer gridline, so the mark is verifiable against the overlay
    /// rather than merely proportional to it.</para>
    ///
    /// <para><b>This finds the nearest usable gridline the way the resolver does, and it is not the
    /// resolver.</b> <c>PlanarPorts</c> additionally requires the pair to be paired into a ROOFTOP,
    /// which depends on the conformal cut and is not reconstructible from a mesh report alone. Where
    /// the two disagree the mark still sits on a real gridline and the run's own note remains the
    /// authority on where the cut landed; it is a picture of the mesh, not a second resolution.</para>
    /// </summary>
    internal static (double Cut, double Back, double Fwd)? MeshGapHalfWidth(
        PlanarMeshReport? report, LabelShape label, LayoutPortDirection.PortHint hint, int dbuPerMicron)
    {
        if (report?.Mesh is not { } mesh || mesh.Cells.Count == 0) return null;

        // R-mom-2: the engine is in metres and the canvas in DBU, and PlanarExtractor neither
        // translates nor centres — so the mapping is one scalar, exactly as the mesh overlay's own
        // header states. Do not add a centring step to the extractor without revisiting both.
        double toDbu = dbuPerMicron * 1e6;
        bool alongX  = hint.Direction is LayoutRotation.R0 or LayoutRotation.R180;

        var gLong = alongX ? mesh.GridX : mesh.GridY;
        var gTran = alongX ? mesh.GridY : mesh.GridX;
        if (gLong.Count < 3 || gTran.Count < 2) return null;

        double lDbu = alongX ? label.X : label.Y;
        double tDbu = alongX ? label.Y : label.X;

        int t = IndexOfCoord(gTran, tDbu / toDbu);
        if (t < 0) return null;

        // The nearest INTERIOR gridline whose two flanking cells both carry metal on the port's own
        // transverse line — an interior cut, which is what makes it a gap rather than an end.
        var present = new bool[gLong.Count - 1];
        foreach (var c in mesh.Cells)
        {
            int cl = alongX ? c.IX : c.IY, ct = alongX ? c.IY : c.IX;
            if (ct == t && cl >= 0 && cl < present.Length) present[cl] = true;
        }

        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i + 1 < present.Length; i++)
        {
            if (!present[i] || !present[i + 1]) continue;
            double d = System.Math.Abs(gLong[i + 1] * toDbu - lDbu);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return null;

        double cut   = gLong[best + 1] * toDbu;
        double lower = cut - gLong[best] * toDbu;
        double upper = gLong[best + 2] * toDbu - cut;

        // Returned in the PORT's own frame — behind the cut along −û, ahead along +û — not in world
        // order. R180 and R270 point down-coordinate, so their two extents swap. Getting this wrong
        // is invisible on a uniform mesh (the two are equal) and shows up only on a graded one, which
        // is exactly where the mark is worth drawing at all.
        bool up = hint.Direction is LayoutRotation.R0 or LayoutRotation.R90;
        return up ? (cut, lower, upper) : (cut, upper, lower);
    }

    /// <summary>
    /// <b>The via footprint an INTERNAL port actually drives, once there is a mesh to measure.</b>
    /// Bounding box in DBU, or null when nothing under the label attaches to the ground plane.
    ///
    /// <para>The same idea as <see cref="MeshGapHalfWidth"/> and for the same reason: before a mesh
    /// exists the mark is a legibility glyph and must not be measured, and once one exists the honest
    /// size is the one the excitation will use. An internal port drives the ground-attachment bases
    /// of the via under it — every cell of that footprint — so the mark can be drawn at the extent of
    /// exactly those cells, against the mesh gridlines the overlay is already drawing.</para>
    ///
    /// <para><b>It measures the FOOTPRINT the mesh resolved, which is the number that surprises
    /// people</b>: a via's own four edges are hard gridlines, so a coarse mesh does not shrink it —
    /// but a footprint smaller than the mesh can carry vanishes to a single cell, and drawing that is
    /// how a user sees it. The cells are collected by 4-connectivity from the label's own cell,
    /// exactly as the resolver walks them.</para>
    /// </summary>
    internal static (double X0, double Y0, double X1, double Y1)? MeshViaFootprint(
        PlanarMeshReport? report, LabelShape label, int dbuPerMicron)
    {
        if (report?.Mesh is not { } mesh || mesh.Bases.Count == 0) return null;

        double toDbu = dbuPerMicron * 1e6;
        int nx = mesh.GridX.Count - 1, ny = mesh.GridY.Count - 1;
        if (nx < 1 || ny < 1) return null;

        // Attachment cells only — a horizontal rooftop at the same (x, y) is a different port.
        var at = new int[nx * ny];
        System.Array.Fill(at, -1);
        foreach (var b in mesh.Bases)
        {
            if (!b.AttachesToGround) continue;
            var cell = mesh.Cells[b.CellB];
            if (cell.IX >= 0 && cell.IX < nx && cell.IY >= 0 && cell.IY < ny)
                at[cell.IY * nx + cell.IX] = b.CellB;
        }

        int ix = IndexOfCoord(mesh.GridX, label.X / toDbu);
        int iy = IndexOfCoord(mesh.GridY, label.Y / toDbu);
        if (ix < 0 || iy < 0 || at[iy * nx + ix] < 0) return null;

        var stack = new System.Collections.Generic.Stack<(int X, int Y)>();
        var seen  = new bool[nx * ny];
        stack.Push((ix, iy));
        seen[iy * nx + ix] = true;

        int x0 = ix, x1 = ix, y0 = iy, y1 = iy;
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            x0 = System.Math.Min(x0, cx); x1 = System.Math.Max(x1, cx);
            y0 = System.Math.Min(y0, cy); y1 = System.Math.Max(y1, cy);

            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int qx = cx + dx, qy = cy + dy;
                if (qx < 0 || qx >= nx || qy < 0 || qy >= ny) continue;
                if (seen[qy * nx + qx] || at[qy * nx + qx] < 0) continue;
                seen[qy * nx + qx] = true;
                stack.Push((qx, qy));
            }
        }

        return (mesh.GridX[x0] * toDbu, mesh.GridY[y0] * toDbu,
                mesh.GridX[x1 + 1] * toDbu, mesh.GridY[y1 + 1] * toDbu);
    }

    /// <summary>Which cell of a gridline array a coordinate falls in; −1 when it is outside.</summary>
    private static int IndexOfCoord(System.Collections.Generic.IReadOnlyList<double> grid, double v)
    {
        for (int i = 0; i + 1 < grid.Count; i++)
            if (v >= grid[i] && v <= grid[i + 1]) return i;
        return -1;
    }

    /// <summary>
    /// An INTERNAL DELTA-GAP port's marker, and it is deliberately a different mark from an edge
    /// port's rather than the same one moved.
    ///
    /// <para><b>Where.</b> At the label's own anchor, across the conductor — because for an internal
    /// port the cut IS where the user put it. An edge port's bar is snapped to the conductor END
    /// (that is what <c>PortHint.PlaneX/Y</c> is for); doing that here would draw the cut at the far
    /// end of the trace and be actively misleading, which is why the two branches exist at all.</para>
    ///
    /// <para><b>What.</b> Two bars facing each other across a visible break, each with both ends
    /// turned back toward its own side's metal — a conductor cut open, which is exactly what the port
    /// is. The arrow runs THROUGH the break rather than starting at a plane and heading inward, so it
    /// reads as "positive current crosses the gap this way" rather than "current enters here".</para>
    ///
    /// <para>An edge port's mark says <i>a boundary, and which way in</i>; this one says <i>a break,
    /// and which way across</i>. At a glance, and at any zoom, they are not each other.</para>
    /// </summary>
    /// <summary>
    /// The hint re-measured AT THE GAP.
    ///
    /// <para><see cref="LayoutPortDirection.Resolve"/> measures a port's width at the conductor's END
    /// FACE, which is the right station for an edge port and the wrong one for an interior cut: on a
    /// taper the two differ by the whole taper ratio, and the gap would draw bars sized to metal that
    /// is nowhere near it. The direction, and everything else the hint carries, is unchanged — only
    /// the station moves.</para>
    ///
    /// <para>Falls back to the hint as resolved when there is nothing to measure (a conductor that is
    /// an instance, or a shape the flattener declines), which is the same fallback the width
    /// measurement itself takes.</para>
    /// </summary>
    private static LayoutPortDirection.PortHint GapHint(
        LayoutPortDirection.ConductorLookup? conductorAt, LabelShape label,
        LayoutPortDirection.PortHint hint)
    {
        if (conductorAt?.Invoke(label.X, label.Y) is not { Shape: { } shape } info) return hint;

        bool alongX = hint.Direction is LayoutRotation.R0 or LayoutRotation.R180;
        var span = LayoutPortDirection.SpanAt(
            shape, info.Box, hint.Direction,
            acrossAt: alongX ? label.Y : label.X,
            alongAt:  alongX ? label.X : label.Y);

        return span is { } s ? hint with { WidthDbu = s.Width } : hint;
    }

    /// <param name="meshHalfWidth">
    /// The gap the solver will actually use, per side, in DBU — from the computed mesh. Null falls
    /// back to <see cref="InternalGapHalfOverWidth"/>, which is a legibility fraction and not a
    /// dimension. <b>So the break is a real measurement whenever there is a mesh to measure, and
    /// reverts to a glyph the moment the mesh is invalidated</b>, which is the honest behaviour in
    /// both directions: an edit that changes the mesh must not leave a stale width on screen looking
    /// like a live one.
    /// </param>
    /// <summary>
    /// <b>The SHUNT port's mark: a ring around the via, with a ground symbol hanging off it.</b>
    ///
    /// <para>The other two marks are statements about a plane the current crosses IN the layout —
    /// an edge port's bar across the conductor end, a gap's pair of brackets either side of its
    /// break — and both are oriented by the direction current flows. An internal port has no such
    /// direction: its current leaves the metal vertically, out of the plane the layout draws. So the
    /// mark is deliberately NOT oriented by the conductor: a ring says "the port is here", the
    /// ground symbol says "and its other terminal is the plane", and both read the same whichever
    /// way the trace runs.</para>
    ///
    /// <para>The ground symbol hangs DOWNWARD ON SCREEN rather than along any layout axis, for the
    /// same reason: there is no layout direction it could honestly point along, and the one thing it
    /// has to say — that this terminal is the ground plane — is a convention every reader of a
    /// schematic already has.</para>
    /// </summary>
    private static void DrawInternalPortMarker(SKCanvas canvas, LabelShape label,
        LayoutPortDirection.PortHint hint, PathSpace ps, double scaleUm,
        SKColor layerColor, SKColor background, LayoutFrameCounters counters,
        (double X0, double Y0, double X1, double Y1)? meshFootprint = null)
    {
        var color = TintForContrast(layerColor, background, PortMarkerContrastTintAmount);

        // ── WHERE AND HOW BIG: the mesh's own footprint when there is a mesh ─────────────────
        //
        // The same rule the delta gap's break follows. Without a mesh there is nothing to measure and
        // the ring is a fixed fraction of the conductor — a legible glyph, not a dimension. WITH one,
        // the port drives the ground-attachment cells under the label and the ring is drawn round
        // exactly those, centred on them rather than on the label, so it can be read against the
        // gridlines underneath. An edit invalidates the mesh and it reverts to the glyph, which is
        // the honest behaviour in both directions.
        double ax = label.X, ay = label.Y;
        double cxW = ax, cyW = ay;
        double rxW = hint.WidthDbu * InternalPortRingOverWidth, ryW = rxW;

        if (meshFootprint is { } fp)
        {
            cxW = 0.5 * (fp.X0 + fp.X1);
            cyW = 0.5 * (fp.Y0 + fp.Y1);
            rxW = 0.5 * (fp.X1 - fp.X0);
            ryW = 0.5 * (fp.Y1 - fp.Y0);
        }

        float cx = ps.X(cxW), cy = ps.Y(cyW);
        float DX(double d) => ps.Len(d);

        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, PortMarkerStrokeDevicePixels),
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            Color = color.WithAlpha(255),
        };
        using var path = new SKPath();

        // The ring: a circle on the glyph path, and the footprint's own ellipse once measured — so a
        // rectangular via reads as the rectangle it is rather than as a circle of some average size.
        float rx = DX(rxW), ry = DX(ryW);
        path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));

        // …and the ground symbol below it: a stem, then three bars narrowing away from the ring.
        float y = cy + ry + DX(hint.WidthDbu * InternalPortStemOverWidth);
        path.MoveTo(cx, cy + ry);
        path.LineTo(cx, y);

        foreach (double halfBar in InternalPortGroundBarsOverWidth)
        {
            float h = DX(hint.WidthDbu * halfBar);
            path.MoveTo(cx - h, y);
            path.LineTo(cx + h, y);
            y += DX(hint.WidthDbu * InternalPortGroundPitchOverWidth);
        }

        canvas.DrawPath(path, paint);
        counters.DrawCalls++;

        // The snap, drawn — the same leader an edge port and a gap already use. Only when the mesh
        // put the footprint somewhere other than where the label sits, so a port over its own via
        // carries no extra ink.
        float lx = ps.X(ax) - cx, ly = ps.Y(ay) - cy;
        if (lx * lx + ly * ly > 1e-6f)
        {
            using var leader = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = DevicePixelsToPathSpace(scaleUm, PortMarkerStrokeDevicePixels * 0.5f),
                Color = color.WithAlpha(150),
                PathEffect = SKPathEffect.CreateDash([DevicePixelsToPathSpace(scaleUm, 3f),
                                                      DevicePixelsToPathSpace(scaleUm, 3f)], 0),
            };
            using var leaderPath = new SKPath();
            leaderPath.MoveTo(ps.X(ax), ps.Y(ay));
            leaderPath.LineTo(cx, cy);
            canvas.DrawPath(leaderPath, leader);
            counters.DrawCalls++;
        }
    }

    private static void DrawInternalGapMarker(SKCanvas canvas, LabelShape label,
        LayoutPortDirection.PortHint hint, PathSpace ps, double scaleUm,
        SKColor layerColor, SKColor background, LayoutFrameCounters counters,
        (double Cut, double Back, double Fwd)? meshHalfWidth = null)
    {
        var color = TintForContrast(layerColor, background, PortMarkerContrastTintAmount);

        var (ux, uy) = LayoutPortDirection.UnitVector(hint.Direction);
        var (px, py) = LayoutPortDirection.PerpendicularVector(hint.Direction);

        double half   = hint.WidthDbu * (0.5 + InternalGapOverhangOverWidth);
        double flange = hint.WidthDbu * InternalGapFlangeOverWidth;

        // The break: the mesh's own cells when there is a mesh, a legibility fraction when there is
        // not. Asymmetric in the first case, because a graded mesh's two cells need not match.
        double gapBack = meshHalfWidth?.Back ?? hint.WidthDbu * InternalGapHalfOverWidth;
        double gapFwd  = meshHalfWidth?.Fwd  ?? hint.WidthDbu * InternalGapHalfOverWidth;

        // ── WHERE THE MARK GOES: the CUT, which is not always the label ──────────────────────
        //
        // The cut can only be a mesh gridline, and the label may sit up to half a cell from the
        // nearest one. Without a mesh there is nothing to snap to and the label's own position is the
        // best statement available. WITH one, the cut is known — so the mark goes there, which is
        // both the honest position and the only one whose brackets can land on the gridlines drawn
        // underneath them. The snap is then visible rather than only reported in the notes: a leader
        // is drawn from the label to the cut when the two differ, the same idiom an edge port already
        // uses for the same reason.
        //
        // Path space is Y-DOWN while DBU space is Y-up, so every world +y offset is subtracted — the
        // same convention DrawPortMarker uses.
        bool alongX = hint.Direction is LayoutRotation.R0 or LayoutRotation.R180;
        double cutX = meshHalfWidth is { } mw && alongX  ? mw.Cut : label.X;
        double cutY = meshHalfWidth is { } mh && !alongX ? mh.Cut : label.Y;

        float cx = ps.X(cutX), cy = ps.Y(cutY);
        float ax = ps.X(label.X), ay = ps.Y(label.Y);
        float DX(double d) => ps.Len(d);

        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, PortMarkerStrokeDevicePixels),
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            Color = color.WithAlpha(255),
        };
        using var path = new SKPath();

        // One bar per side of the break, each flanged back toward its own metal.
        foreach (int side in new[] { -1, 1 })
        {
            double gap = side < 0 ? gapBack : gapFwd;
            float ox = cx + DX(ux * gap * side), oy = cy - DX(uy * gap * side);

            float b1x = ox + DX(px * half), b1y = oy - DX(py * half);
            float b2x = ox - DX(px * half), b2y = oy + DX(py * half);
            path.MoveTo(b1x, b1y);
            path.LineTo(b2x, b2y);

            // Both ends turned back ALONG the conductor, AWAY from the break — the bracket an edge
            // port's outward-turned single serif is not. Drawn from the overhung ends, which is the
            // only place they are against background rather than on the conductor's own outline.
            path.MoveTo(b1x, b1y);
            path.LineTo(b1x + DX(ux * flange * side), b1y - DX(uy * flange * side));
            path.MoveTo(b2x, b2y);
            path.LineTo(b2x + DX(ux * flange * side), b2y - DX(uy * flange * side));
        }

        // ── NO ARROW ON A GAP ────────────────────────────────────────────────────────────────
        //
        // The brackets are the whole mark. An arrowhead here pointed at nothing — an edge port's
        // head lands on its reference plane, and a gap has no single plane for one to land on — and
        // a headless shaft through the break is indistinguishable from a stray line. The port's
        // polarity is a number rather than a picture: the run's own note names which way positive
        // current crosses, and the EM Setup panel is where it is set.
        canvas.DrawPath(path, paint);
        counters.DrawCalls++;

        // The snap, drawn: a leader from the label's own anchor to the cut the mesh put it on. Only
        // when they genuinely differ, so a gap that landed where it was placed carries no extra ink.
        float lx = ax - cx, ly = ay - cy;
        if (lx * lx + ly * ly > 1e-6f)
        {
            using var leader = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = DevicePixelsToPathSpace(scaleUm, PortMarkerStrokeDevicePixels * 0.5f),
                Color = color.WithAlpha(150),
                PathEffect = SKPathEffect.CreateDash([DevicePixelsToPathSpace(scaleUm, 3f),
                                                      DevicePixelsToPathSpace(scaleUm, 3f)], 0),
            };
            using var leaderPath = new SKPath();
            leaderPath.MoveTo(ax, ay);
            leaderPath.LineTo(cx, cy);
            canvas.DrawPath(leaderPath, leader);
            counters.DrawCalls++;
        }
    }

    /// <summary>What the active EM setup drives this label as. Exact longs — see
    /// <see cref="LayoutRenderOptions.InternalPortMarks"/> for why not a port number. A label the
    /// setup says nothing about is an edge port, which is what every port is unless something says
    /// otherwise.</summary>
    private static PlanarPortKind MarkKindOf(IReadOnlyList<(long X, long Y, PlanarPortKind Kind)>? marks,
                                             LabelShape label)
    {
        if (marks is null) return PlanarPortKind.Edge;
        foreach (var (x, y, kind) in marks) if (x == label.X && y == label.Y) return kind;
        return PlanarPortKind.Edge;
    }

    private static void DrawPortMarker(SKCanvas canvas, LabelShape label,
        LayoutPortDirection.ConductorLookup? conductorAt,
        PathSpace ps, double scaleUm, SKColor layerColor, SKColor background,
        LayoutFrameCounters counters, PlanarPortKind kind,
        PlanarMeshReport? mesh = null, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        if (LayoutPortDirection.Resolve(conductorAt, label) is not { } hint) return;

        if (kind == PlanarPortKind.Internal)
        {
            DrawInternalPortMarker(canvas, label, hint, ps, scaleUm, layerColor, background, counters,
                                   MeshViaFootprint(mesh, label, dbuPerMicron));
            return;
        }

        if (kind == PlanarPortKind.InternalDeltaGap)
        {
            DrawInternalGapMarker(canvas, label, GapHint(conductorAt, label, hint),
                                  ps, scaleUm, layerColor, background, counters,
                                  MeshGapHalfWidth(mesh, label, hint, dbuPerMicron));
            return;
        }

        var color = TintForContrast(layerColor, background, PortMarkerContrastTintAmount);

        var (ux, uy) = LayoutPortDirection.UnitVector(hint.Direction);
        var (px, py) = LayoutPortDirection.PerpendicularVector(hint.Direction);

        double half = hint.WidthDbu / 2.0;
        var (reach, barbLen) = PortArrowGeometry(hint);
        double serif = hint.WidthDbu * PortPlaneSerifOverWidth;

        // The bar sits at the CONDUCTOR END, not at the label's own anchor — that is the whole point
        // of hint.PlaneX/Y. See PortHint's own note: with the bar drawn wherever the user happened to
        // click, "where is the reference plane" had no readable answer (owner report, 2026-08-09).
        float bx0 = ps.X(hint.PlaneX), by0 = ps.Y(hint.PlaneY);
        float ax = ps.X(label.X), ay = ps.Y(label.Y);

        // Path space is Y-DOWN while DBU space is Y-up, so every world +y offset is SUBTRACTED here.
        // Lengths themselves are unsigned and go through ps.Len; the flip lives in the call sites'
        // signs, exactly as DrawLabelText's own rotation table handles it.
        float DX(double d) => ps.Len(d);

        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, PortMarkerStrokeDevicePixels),
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            Color = color.WithAlpha(255),
        };
        using var path = new SKPath();

        // The reference plane: a bar across the conductor at the end the port names, with a short
        // serif turned back OUT of the metal at each end so the plane reads as a cut, not a trace.
        float e1x = bx0 + DX(px * half), e1y = by0 - DX(py * half);
        float e2x = bx0 - DX(px * half), e2y = by0 + DX(py * half);
        path.MoveTo(e1x, e1y);
        path.LineTo(e2x, e2y);
        path.MoveTo(e1x, e1y);
        path.LineTo(e1x - DX(ux * serif), e1y + DX(uy * serif));
        path.MoveTo(e2x, e2y);
        path.LineTo(e2x - DX(ux * serif), e2y + DX(uy * serif));

        // ── The arrow ARRIVES AT the plane; it does not set off from it ──────────────────────
        //
        // The arrowhead lands exactly on the bar, with the shaft running back along the direction
        // current comes from. The pair then reads as one statement — "current arrives HERE, flowing
        // THAT way" — with the head pointing at the one line that is load-bearing, instead of an
        // arrow laid across the metal whose tip means nothing in particular.
        //
        // It also stops the marker covering the conductor. An arrow drawn INTO the metal is longest
        // exactly where the port is widest, so on a wide port it ran a third of the way across the
        // part and sat on top of whatever else was there.
        float tipX = bx0, tipY = by0;
        path.MoveTo(bx0 - DX(ux * reach), by0 + DX(uy * reach));
        path.LineTo(tipX, tipY);
        foreach (int s in new[] { 1, -1 })
        {
            double abx = -ux * barbLen + s * px * barbLen * 0.6;
            double aby = -uy * barbLen + s * py * barbLen * 0.6;
            path.MoveTo(tipX, tipY);
            path.LineTo(tipX + DX(abx), tipY - DX(aby));
        }

        canvas.DrawPath(path, paint);
        counters.DrawCalls++;

        // A leader from the label's own anchor to the plane, drawn only when the two genuinely differ
        // — otherwise the text would look unattached to the marker it names.
        float dx = ax - bx0, dy = ay - by0;
        if (dx * dx + dy * dy > 1e-6f)
        {
            using var leader = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = DevicePixelsToPathSpace(scaleUm, PortMarkerStrokeDevicePixels * 0.5f),
                Color = color.WithAlpha(150),
                PathEffect = SKPathEffect.CreateDash([DevicePixelsToPathSpace(scaleUm, 3f),
                                                      DevicePixelsToPathSpace(scaleUm, 3f)], 0),
            };
            using var leaderPath = new SKPath();
            leaderPath.MoveTo(ax, ay);
            leaderPath.LineTo(bx0, by0);
            canvas.DrawPath(leaderPath, leader);
            counters.DrawCalls++;
        }
    }

    /// <summary>
    /// A label's text.
    /// </summary>
    /// <param name="centred">
    /// Centre the text ON the anchor instead of running it rightward from it.
    ///
    /// <para><b>True for EVERY port, and it is safe there for a specific reason rather than a general
    /// one.</b> Every other label in a layout is left-anchored, and a label's drawn position is not
    /// purely cosmetic in general — <c>Flatten to Polygon</c> turns text into real geometry. A PORT
    /// label is the exception the exception rests on: it is excluded from Flatten to Polygon and from
    /// every boolean (<c>LayoutEditorViewModel.Booleans</c> returns an empty result for
    /// <c>IsPort</c>), so nothing downstream can disagree with where it was drawn.</para>
    ///
    /// <para><b>It was true for an internal delta gap ONLY, on the grounds that "an edge port's anchor
    /// is its end FACE, where running the text inward is right". That reasoning was about the TEXT and
    /// missed what the text has to sit inside</b> (owner, 2026-08-25). A port's selection highlight
    /// and its pick region are one centred square about the anchor
    /// (<c>LayoutHitTest.PortPickBbox</c>), and left-anchored glyphs run straight out of it — so the
    /// box selected the port without covering its name, which is what the owner reported. Centring
    /// every port's name puts the glyphs back inside the box that selects them, and the square's own
    /// size (half the larger text extent) is what guarantees they fit.</para>
    ///
    /// <para><b>The reference-plane bar and arrow do NOT move</b> — they are drawn about the
    /// conductor end (<c>hint.PlaneX/PlaneY</c>), which is the whole point of them, and a leader
    /// joins them to the anchor when the two differ. This changes where the NAME sits, nothing
    /// else.</para>
    /// </param>
    private static void DrawLabelText(SKCanvas canvas, LabelShape label, PathSpace ps, SKColor color,
                                      bool centred = false)
    {
        if (string.IsNullOrEmpty(label.Text)) return;

        float sizeUm = System.Math.Max(0.001f, ps.Len(label.Height));
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(label.Style), sizeUm);
        using var paint = new SKPaint { IsAntialias = true, Color = color };

        canvas.Save();
        canvas.Translate(ps.X(label.X), ps.Y(label.Y));
        // Path space is Y-down — negate the DBU-space (Y-up) counter-clockwise angle.
        float rotationDeg = -(float)label.RotationDegrees;
        if (rotationDeg != 0f) canvas.RotateDegrees(rotationDeg);

        var (align, dy) = LayoutTextOutline.ResolveLabelAnchor(label, font, centred);
        canvas.DrawText(label.Text, 0, dy, align, font, paint);
        canvas.Restore();
    }

    /// <summary>
    /// The ONE place a label's <see cref="LabelShape.HAlign"/>/<see cref="LabelShape.VAlign"/> become
    /// a Skia text-aligner and a baseline offset, in the label's own pre-rotation, Y-DOWN local frame.
    /// Shared by <see cref="DrawLabelText"/> and <see cref="MeasureLabelWorldBbox"/> so what is drawn
    /// and what is measured (selection outline, hit test) can never disagree.
    ///
    /// <para><paramref name="centred"/> is the PORT override and still wins outright — a port's mark is
    /// centred on its anchor regardless of what the label itself says, which is what every port in every
    /// existing <c>.clay</c> already did.</para>
    /// </summary>
    // ResolveLabelAnchor moved to LayoutTextOutline (CircuitRF.Design) when the interchange stack
    // crossed the UI firewall: the flattener needs the SAME anchor arithmetic the renderer draws with,
    // and `circuitrf convert` runs the flattener with no Avalonia in the process. One copy, in the
    // project both callers can reach — the property being protected is that these two never disagree.
}
