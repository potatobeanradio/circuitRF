// railRF's maps on the layout editor's own canvas (docs/sonnet-briefs/brief-railrf-8-board-view.md;
// railrf.md §11.6).
//
// ── WHAT THIS FILE IS NOT ──────────────────────────────────────────────────────────────────────
//
// It contains NO NAVIGATION CODE. Not a pan, not a zoom, not a marquee, not a fit. §11.6's whole
// instruction is that railRF's board pans and zooms exactly as the layout editor does — "not
// 'similarly'" — and the way to get that is to BE that control rather than to re-implement it:
//
//     Someone who has learned one of these windows has learned the other, and a near-miss is worse
//     than an absence because it is discovered by being wrong.
//
// So this overlay DECLINES every NAVIGATION gesture. OnKeyDown returns false unconditionally, and a
// pointer event is consumed in exactly two states, neither of which the canvas has a meaning for:
// the pour pick of §2.3 step 2 (armed by the window, and only while nothing named a net), and a
// drag of the legend plate, which starts only on a press INSIDE that plate. Everything else
// reaches the canvas's own state machine untouched — its marquee, its pan and its hit test go on
// working over the map, which is §11.6's whole instruction. What the overlay does with an ordinary
// pointer move is read a value out and publish it.
//
// The one thing railRF adds is R-rail8-4's WIDER keyboard gate, and it is deliberately not here:
// it is LayoutCanvas.NavigationKeysSuppressed, a predicate the window supplies. A gate belongs on
// the thing that owns the gestures; a copy of the gestures in an overlay is the near-miss above.
//
// ── AND IT NEVER TOUCHES THE LAYOUT MODEL ──────────────────────────────────────────────────────
//
// R-rail8-2, which is the overlay seam's own contract. Nothing railRF draws enters the .clay, and a
// map repaint goes through LayoutCanvas.InvalidateOverlay rather than invalidating LayoutPathCache —
// which on a real board is the difference between a repaint and a rebuild of half a million shapes.
// The only write this class makes anywhere is the classification override of R-rail8-11, and that
// goes to the RailDocument through a callback, never to LayoutView.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The drop, |Z| and class maps drawn over the board, through the seam wBond already uses.
/// </summary>
/// <remarks>
/// <b>It draws and reads out; it decides nothing and navigates nothing.</b> The picture is
/// <see cref="RailMapScene"/>'s, below the firewall, so the window and a headless report draw alike
/// (R-rail8-13); the voltage under the cursor is <see cref="RailDcResult.VoltageAt"/>'s, so the
/// readout and the report cannot disagree at the same coordinate (R-rail5-2).
/// </remarks>
public sealed class RailLayoutOverlay : ILayoutCanvasOverlay
{
    /// <summary>Raised when the picture or the readout changed and the host should repaint. The host
    /// answers with <see cref="LayoutCanvas.InvalidateOverlay"/> and nothing else — see this file's
    /// header.</summary>
    public event Action? OverlayChanged;

    /// <summary>Raised when <see cref="Readout"/> changed, so a status strip can follow it without
    /// polling.</summary>
    public event Action<string?>? ReadoutChanged;

    /// <summary>
    /// Asked to force a region either way, or to clear the override (a null class).
    /// </summary>
    /// <remarks>
    /// A callback rather than a document reference, because the OVERRIDE is a document edit and a
    /// re-solve, and both belong to the view model. What is owned here is only which region was
    /// clicked, which is the half only the picture knows.
    /// </remarks>
    public Action<PdnRegionRef, PdnCopperClass?>? ForceRegion { get; set; }

    private RailDcResult? _result;
    private PdnPlaneAnswer? _plane;
    private RailMapKind _kind = RailMapKind.Copper;
    private int _dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private RailMapTheme _theme = RailMapTheme.Fallback;

    private RailMapScene? _scene;
    private string? _readout;

    /// <summary>The rail result the maps are drawn from, or null before one exists.</summary>
    public RailDcResult? Result
    {
        get => _result;
        set { if (!ReferenceEquals(_result, value)) { _result = value; Invalidate(); } }
    }

    /// <summary>
    /// The plane pair's own answer — §4.5's modes and §2.4's |Z| map — or null before one exists.
    /// </summary>
    /// <remarks>
    /// <b>A second result beside <see cref="Result"/>, and it has to be.</b> §4.1's shunt branch
    /// vanishes at ω = 0, so the DC netlist holds no cavity and the map is of its own extraction at
    /// its own frequency (<c>RailPlaneRun</c>'s header carries the argument). What the DC result
    /// still supplies is the copper to clip to and the markers to draw, which is why the |Z| scene
    /// reads both.
    /// </remarks>
    public PdnPlaneAnswer? Plane
    {
        get => _plane;
        set { if (!ReferenceEquals(_plane, value)) { _plane = value; Invalidate(); } }
    }

    /// <summary>Which tab is showing. <b>Switching it rebuilds the SCENE and nothing else</b>
    /// (R-rail8-12): one canvas, one <c>LayoutView</c>, four tab states — a tab that rebuilt the
    /// canvas would drop the viewport and undo R-rail8-8.</summary>
    public RailMapKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; Invalidate(); } }
    }

    /// <summary>The artwork's resolution — everything the metres↔DBU bridge needs, and the only
    /// place railRF supplies it.</summary>
    public int DbuPerMicron
    {
        get => _dbuPerMicron;
        set { if (_dbuPerMicron != value) { _dbuPerMicron = value; Invalidate(); } }
    }

    /// <summary>railRF's own colours, projected from the active theme by the host.</summary>
    public RailMapTheme Theme
    {
        get => _theme;
        set
        {
            _theme = value ?? RailMapTheme.Fallback;
            OverlayChanged?.Invoke();       // colours only: the SCENE is unchanged, so it is not rebuilt
        }
    }

    /// <summary>
    /// What the cursor is currently over, or null when it is over nothing this overlay knows about.
    /// </summary>
    /// <remarks>
    /// <b>A latch, and therefore dropped in <see cref="OnFocusLost"/>.</b> It outlives the pointer
    /// move that set it by design — a readout that cleared itself on the next frame would be
    /// unreadable — which is exactly what makes it something that has to be cleared explicitly.
    /// </remarks>
    public string? Readout => _readout;

    private bool _emptyNoteShownByHost;

    /// <summary>
    /// Set by a host that prints an empty tab's sentence itself, so the renderer does not centre a
    /// second copy of it underneath.
    /// </summary>
    /// <remarks>
    /// <b>The window puts the |Z| tab's frequency box and Find button over the canvas</b>
    /// (owner, 2026-09-19), and the sentence belongs with the button that answers it. The
    /// renderer's centred note is what a clipboard copy, a report page and a headless render still
    /// get — none of those has a button — so the note is not removed from the SCENE BUILDER, only
    /// from the scene this overlay hands the canvas. It is the same collision the board's own
    /// "No board yet" placeholder already had with the map note.
    ///
    /// <para>Only an EMPTY tab's note is suppressed. A refusal is a note too, and the host prints
    /// that one in the same place for the same reason.</para>
    /// </remarks>
    public bool EmptyNoteShownByHost
    {
        get => _emptyNoteShownByHost;
        set { if (_emptyNoteShownByHost != value) { _emptyNoteShownByHost = value; Invalidate(); } }
    }

    /// <summary>The scene currently being drawn. Built on demand and cached until an input changes,
    /// because a pan must not re-run the field sampling.</summary>
    public RailMapScene Scene => _scene ??= BuildScene();

    private RailMapScene BuildScene()
    {
        var scene = RailMapScene.Build(_result, _kind, _dbuPerMicron, _plane)
                                .WithLegendMovedBy(_legendDx, _legendDy);

        if (_emptyNoteShownByHost && _kind == RailMapKind.Impedance && scene.Tiles.Count == 0)
            scene = RailMapScene.Empty(RailMapKind.Impedance);

        return scene;
    }

    // ── the legend is DRAGGABLE (owner, 2026-09-19) ────────────────────────────────────────────

    /// <summary>
    /// How far the user has dragged the legend plate from where the scene put it, in DBU.
    /// </summary>
    /// <remarks>
    /// <b>It is VIEW STATE and it is not persisted</b>, exactly as the pan and the zoom of the
    /// panel it sits in are not. The default position is a fact about the map — the scene derives
    /// it from the content's own bbox — and where a reader has since pushed the plate to see what
    /// is under it is a fact about this session's look at it. Writing it into the <c>.crail</c>
    /// would make the document dirty on a gesture that changed no input and no number.
    ///
    /// <para>Applied by <see cref="RailMapScene.WithLegendMovedBy"/>, which is below the firewall,
    /// so the window's own copy-to-clipboard and report pages draw the plate where the window has
    /// it rather than where it started.</para>
    /// </remarks>
    public (long X, long Y) LegendOffset
    {
        get => (_legendDx, _legendDy);
        set
        {
            if (_legendDx == value.X && _legendDy == value.Y) return;
            (_legendDx, _legendDy) = value;
            _scene = null;                 // the BOX moved; nothing else about the scene changed
            OverlayChanged?.Invoke();
        }
    }

    private long _legendDx, _legendDy;

    /// <summary>Where the drag started, in DBU, or null while no drag is in flight.</summary>
    private (long X, long Y)? _legendGrab;

    /// <summary>The offset the drag started from, so a cancel or a jitter cannot accumulate.</summary>
    private (long X, long Y) _legendGrabOffset;

    /// <summary>True while the pointer is dragging the plate — read by a test, and by nothing else.</summary>
    public bool IsDraggingLegend => _legendGrab is not null;

    private void Invalidate()
    {
        _scene = null;
        SetReadout(null);
        OverlayChanged?.Invoke();
    }

    // ── draw ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The drawing layers the technology says are not visible — <b>supplied by the host</b>, so the
    /// map comes off a layer at the same moment the copper under it does (owner, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// Held rather than derived: this overlay has no technology and is not given one, for the reason
    /// this file's header states — it draws what the result produced and decides nothing. What layer
    /// visibility IS is a property of the frame's technology, which is the canvas's own input.
    /// </remarks>
    public IReadOnlySet<LayerKey> HiddenLayers
    {
        get => _hiddenLayers;
        set
        {
            var next = value ?? (IReadOnlySet<LayerKey>)new HashSet<LayerKey>();
            if (_hiddenLayers.SetEquals(next)) return;
            _hiddenLayers = next;
            OverlayChanged?.Invoke();       // visibility only: the SCENE is unchanged, so it is kept
        }
    }

    private IReadOnlySet<LayerKey> _hiddenLayers = new HashSet<LayerKey>();

    /// <summary>
    /// The part selected in the parts table, marked on the board — or null for none.
    /// </summary>
    /// <remarks>
    /// <b>It does NOT rebuild the scene</b>, which is the whole reason it is held here rather than
    /// handed to <see cref="RailMapScene.Build"/>: arrowing down a thirteen-row parts table would
    /// otherwise re-sample the drop field thirteen times, and a selection is not a result (see
    /// <see cref="RailPartHighlight"/>). Like <see cref="Theme"/> and <see cref="HiddenLayers"/>, it
    /// repaints and keeps the scene.
    ///
    /// <para>Resolved by the view model, because which pads belong to <c>C7</c> is a question about
    /// the board netlist and this overlay has neither one nor any business reading one.</para>
    /// </remarks>
    public RailPartHighlight? PartHighlight
    {
        get => _partHighlight;
        set
        {
            if (_partHighlight == value) return;       // a record: equal by value, so re-selecting the
            _partHighlight = value;                    // same row does not repaint
            OverlayChanged?.Invoke();
        }
    }

    private RailPartHighlight? _partHighlight;

    /// <summary>
    /// The net highlighted in the pick list and not yet made a rail, or null — R-rail19-2's preview.
    /// </summary>
    /// <remarks>
    /// <b>Pushed, like <see cref="PartHighlight"/>, and it does NOT rebuild the scene.</b> Arrowing
    /// down a list of two hundred nets would otherwise re-sample the drop field two hundred times,
    /// and a pick-list selection is not a result (R-rail8-13). The WALK behind it is not paid for
    /// twice either — that cache is the view model's, keyed by net name, because the walk is a
    /// question about the board netlist and this overlay has no business reading one.
    /// </remarks>
    public RailNetPreview? NetPreview
    {
        get => _netPreview;
        set
        {
            if (ReferenceEquals(_netPreview, value)) return;
            _netPreview = value;
            OverlayChanged?.Invoke();
        }
    }

    private RailNetPreview? _netPreview;

    /// <summary>
    /// Paints the map for the WINDOW, which is the one caller that asks for the blitted form.
    /// </summary>
    /// <remarks>
    /// <b><c>batchTiles: true</c>, and it is the whole of the frame-rate fix.</b> An accurate
    /// reading's drop map is one tile per extraction cell — 64,907 of them on a four-layer sensor
    /// board — and each was a separate <c>DrawRect</c> on every frame and on every pointer move,
    /// which is a render thread pegged at 100% and about one frame a second. The renderer's own
    /// header carries the measurement, why it is a triangle list and not an image, and the reason
    /// the default is off: the report and the clipboard go to SVG and PDF, where the map stays
    /// vector.
    /// </remarks>
    public void Draw(SKCanvas canvas, LayoutViewport viewport, LayoutRenderTheme theme) =>
        RailMapRenderer.Draw(canvas, Scene, viewport, _theme, _hiddenLayers, _partHighlight,
                             batchTiles: true, netPreview: _netPreview);

    /// <summary>
    /// The union of the map, the legend, the source and load markers and the via callouts — <b>not
    /// the copper's bbox</b>, which the canvas already has.
    /// </summary>
    /// <remarks>
    /// §11.6 trap 4. A drop map is co-extensive with the copper, so including it looks harmless until
    /// a legend, a source marker or a flagged-via callout sits outside the copper's own bbox and Zoom
    /// to Fit cuts it off. The seam's own doc comment records the wBond case this rule comes from: a
    /// document on an empty scratch layout fitted to an EMPTY extent and landed at an arbitrary
    /// default, with every wire off screen.
    /// </remarks>
    public Bbox ContentBounds() => Scene.Bounds;

    // ── pointer: every gesture is DECLINED, and a move reads a value out ───────────────────────

    /// <summary>
    /// The pour pick, and NOTHING else — every other press is declined.
    /// </summary>
    /// <remarks>
    /// <b>Why there is a press here at all now.</b> §2.3 step 2's second route is "pick the rail by
    /// clicking its pour on the board", and the window SAYS SO in the specification column whenever
    /// nothing named a net. It was never wired: the sentence was on screen, the click did nothing,
    /// and a user reasonably read the whole card as broken (owner, 2026-09-19). A sentence telling
    /// someone to perform a gesture that does not exist is worse than no sentence.
    ///
    /// <para><b>It is armed by the window, not by this overlay</b> — <see cref="PourPick"/> is null
    /// except in exactly the state that sentence describes. A press that is not that state, or that
    /// carries a modifier, or is a double-click, is DECLINED, because anything consumed here never
    /// reaches the canvas's own marquee, pan and hit test, and §11.6's whole rule is that those keep
    /// working. The pick itself also declines when the point is not on copper: a rail anchored at a
    /// coordinate with nothing under it is a rail whose connectivity walk seeds from nowhere.</para>
    /// </remarks>
    public bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount)
    {
        if (clickCount != 1 || modifiers != KeyModifiers.None) return false;

        // THE PLATE FIRST, because it is drawn on top of whatever is under it and a press that
        // lands on it is unambiguously about it. Consuming the press is what keeps the canvas's
        // marquee and pan from starting underneath the drag — the same reason the pour pick below
        // consumes its own.
        if (Scene.Legend is { } legend && legend.Box.Contains(worldX, worldY))
        {
            _legendGrab = (worldX, worldY);
            _legendGrabOffset = (_legendDx, _legendDy);
            return true;
        }

        if (PourPick is null) return false;
        return PourPick(worldX, worldY, tolDbu);
    }

    /// <summary>
    /// What a left-click on the copper does, or null when clicking the pour means nothing here.
    /// </summary>
    /// <remarks>
    /// Set by the view model, and only while <c>HasNoPickableNets</c> — the exact condition the
    /// "pick the rail by clicking its pour" sentence is shown under. Returns true when it made a
    /// rail, which is what consumes the press.
    /// </remarks>
    public Func<long, long, long, bool>? PourPick { get; set; }

    /// <summary>
    /// Reads the value under the cursor and declines the gesture.
    /// </summary>
    /// <remarks>
    /// <b>Returning false is load-bearing, not a default.</b> Anything an overlay consumes never
    /// reaches the layout editor's own state machine — which here would mean the marquee, the pan and
    /// the hit test all stopping while the pointer is over the map, and §11.6's whole instruction is
    /// that they do not.
    /// </remarks>
    public bool OnPointerMoved(long worldX, long worldY, long tolDbu, bool leftButtonDown, KeyModifiers modifiers)
    {
        // A DRAG IN FLIGHT OWNS THE MOVE. It is measured from where the press landed rather than
        // from the last move, so a frame the canvas coalesced away costs nothing, and it is added
        // to the offset the press STARTED from, so the plate cannot creep by accumulating its own
        // rounding. The readout is not published while dragging: the value under the cursor is
        // whatever the plate is covering, which is not what the reader is pointing at.
        if (_legendGrab is { } grab)
        {
            if (!leftButtonDown) { _legendGrab = null; return true; }

            LegendOffset = (_legendGrabOffset.X + worldX - grab.X,
                            _legendGrabOffset.Y + worldY - grab.Y);
            return true;
        }

        SetReadout(ReadoutAt(worldX, worldY, tolDbu));
        return false;
    }

    public bool OnPointerReleased(long worldX, long worldY)
    {
        if (_legendGrab is null) return false;
        _legendGrab = null;
        return true;
    }

    /// <summary>Declines every key, so every navigation gesture reaches the canvas. railRF's addition
    /// to the keyboard is a GATE on the canvas, not a handler here — see this file's header.</summary>
    public bool OnKeyDown(Key key, KeyModifiers modifiers) => false;

    public void OnKeyUp(Key key, KeyModifiers modifiers) { }

    /// <summary>
    /// Part of the seam's companion-move contract, and inert here: railRF holds no selection of its
    /// own, so there is nothing for the layout editor's drag to bring along. Settable because the
    /// interface declares it settable, and the host sets it before every companion move.
    /// </summary>
    public bool CompanionPressResolvedNewSelection { get; set; }

    /// <summary>
    /// Focus has left the canvas: drop every latch, whether or not its release was ever seen.
    /// </summary>
    /// <remarks>
    /// R-rail8-6. The seam's own header states the rule and the bug behind it — a held key released
    /// over a button never reaches the canvas, the latch stays set, and from that moment every
    /// left-drag is a pan with nothing on screen explaining it. The recorded symptom named the wrong
    /// subsystem entirely: <i>"marquee select stopped working in the layout view"</i>. This overlay
    /// latches no KEY, but it does latch a readout, and a readout left standing describes copper the
    /// pointer is no longer anywhere near.
    /// </remarks>
    public void OnFocusLost() => SetReadout(null);

    private void SetReadout(string? value)
    {
        if (_readout == value) return;
        _readout = value;
        ReadoutChanged?.Invoke(value);
    }

    /// <summary>
    /// What the cursor is over, as the strip says it.
    /// </summary>
    /// <remarks>
    /// <b>The voltage comes from <see cref="RailDcResult.VoltageAt"/> and not from the tile under the
    /// pointer</b> (R-rail5-2, R-rail8-9): §2.4 asks for "the absolute voltage anywhere on the net",
    /// and brief 5 put the interpolation rule in exactly one place precisely so the window and the
    /// headless report cannot differ at the same coordinate. The picture decides only WHICH LAYER is
    /// being read — the nearest tile's — because the layer the user believes they are pointing at is
    /// the one they can see.
    /// </remarks>
    private string? ReadoutAt(long x, long y, long tolDbu)
    {
        var scene = Scene;

        // A marker first: it is the smallest target and it carries the most specific sentence.
        long reach = Math.Max(tolDbu, RailMapScene.MarkerReachDbu(_dbuPerMicron));
        RailMapMarker? nearestMarker = null;
        double nearestMarkerD2 = double.MaxValue;
        foreach (var m in scene.Markers)
        {
            double dx = m.X - x, dy = m.Y - y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= (double)reach * reach && d2 < nearestMarkerD2) { nearestMarkerD2 = d2; nearestMarker = m; }
        }
        if (nearestMarker is { } marker) return marker.Readout;

        if (scene.Kind == RailMapKind.Drop) return DropReadoutAt(scene, x, y);
        if (scene.Kind == RailMapKind.Impedance) return ImpedanceReadoutAt(scene, x, y);

        // The class tab: the SMALLEST region whose extent covers the point, so a small piece sitting
        // inside a pour's bounding box is what answers rather than the pour.
        RailMapRegion? best = null;
        long bestArea = long.MaxValue;
        foreach (var r in scene.Regions)
        {
            if (!r.Bounds.Contains(x, y)) continue;
            long area = Math.Max(1, r.Bounds.MaxX - r.Bounds.MinX) * Math.Max(1, r.Bounds.MaxY - r.Bounds.MinY);
            if (area < bestArea) { bestArea = area; best = r; }
        }
        return best?.Readout;
    }

    private string? DropReadoutAt(RailMapScene scene, long x, long y)
    {
        if (_result is not { } result || scene.Tiles.Count == 0) return null;

        LayerKey? layer = null;
        double bestD2 = double.MaxValue;
        foreach (var tile in scene.Tiles)
        {
            double dx = tile.CentreX - x, dy = tile.CentreY - y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; layer = tile.Layer; }
        }

        if (layer is not { } l) return null;
        if (result.VoltageAt(l, isReference: false, x, y) is not { } v) return null;

        string drop = result.SourceVoltageV is { } src
            ? $", {(src - v) * 1e3:0.###} mV below the source"
            : "";

        return $"{RailMapScene.Volts(v)} on layer {l.Layer}/{l.Datatype}{drop}";
    }

    /// <summary>
    /// What the |Z| map reads under the cursor, and <b>what the modes say about that place</b>.
    /// </summary>
    /// <remarks>
    /// §2.4's whole question about the cavity is a question about a PLACE — <i>"a mode whose maximum
    /// sits on the load pin field is a problem; the same mode with its maximum in a corner is
    /// not"</i> — so the readout names the mode with the largest field here rather than only the
    /// ohms. The ohms come out of the ANSWER rather than back out of the tile: the tile carries
    /// decibels because the ramp is logarithmic, and converting them back would be a second
    /// arithmetic on a number that is already held exactly.
    /// </remarks>
    private string? ImpedanceReadoutAt(RailMapScene scene, long x, long y)
    {
        if (_plane is not { Refusal: null } plane || plane.ImpedanceMap.Count == 0) return null;

        int best = -1;
        double bestD2 = double.MaxValue;
        for (int i = 0; i < plane.ImpedanceMap.Count; i++)
        {
            var cell = plane.ImpedanceMap[i].Cell;
            double dx = cell.CentreX - x, dy = cell.CentreY - y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }

        if (best < 0) return null;

        // Off the copper entirely: the map is clipped to the artwork, so a cursor a long way from
        // every cell is over nothing this overlay drew.
        long reach = Math.Max(1, RailMapScene.MetresToDbu(plane.CellSizeMetres, _dbuPerMicron));
        if (bestD2 > (double)reach * reach) return null;

        var hit = plane.ImpedanceMap[best];

        // ── ZERO IS "NO CONNECTION", NOT "NO IMPEDANCE" ──────────────────────────────────────
        //
        // A cell on a galvanically separate piece of the rail has no path to the drive, so its
        // node carries no injected current and solves to exactly zero volts. Printing that as
        // "0 Ω" reads as a dead short to the very place a reader is trying to understand — it is
        // the opposite of what is true. These cells are uncoloured on the map for the same
        // reason; the readout says why rather than leaving the blank patch unexplained.
        if (!(hit.OhmsMagnitude > 0))
            return $"Not reachable from {plane.MapPortName}. This copper is on one of the " +
                   $"{plane.Pieces} galvanically separate pieces of this rail, and no current " +
                   "from that drive flows in it — so it has no impedance to it, rather than a " +
                   "low one.";

        string where = $"{RailMapScene.Ohms(hit.OhmsMagnitude)} at " +
                       $"{PdnMask.Hertz(plane.MapFrequencyHz)}" +
                       (plane.MapPortName.Length > 0 ? $" from {plane.MapPortName}" : "");

        var mode = plane.Modes
            .Where(m => best < m.Field.Count)
            .OrderByDescending(m => Math.Abs(m.Field[best]))
            .FirstOrDefault();

        return mode is null || !(Math.Abs(mode.Field[best]) > 0)
            ? where
            : where + $" · the {PdnMask.Hertz(mode.FrequencyHz)} mode is at " +
                      $"{Math.Abs(mode.Field[best]):0.00} of its own peak here";
    }

    // ── R-rail8-11: forcing a region, from the class tab ───────────────────────────────────────

    /// <summary>
    /// The overlay's own right-click rows, prepended to the canvas's.
    /// </summary>
    /// <remarks>
    /// <b>Why a MENU and not a click.</b> §2.9 rule 2 requires that a region can be forced either way,
    /// and the obvious spelling — click the region — would mean consuming a press, which stops the
    /// layout editor's own marquee and hit test dead for as long as the class tab is showing (see
    /// <see cref="OnPointerMoved"/>). A context row costs the user one extra gesture and costs the
    /// canvas nothing.
    ///
    /// <para>Three rows and not two, because "put it back" has to be reachable: an override the user
    /// cannot clear is a document they cannot return to the measured answer, and the measured answer
    /// is the one the extraction will keep agreeing with as the artwork changes.</para>
    /// </remarks>
    public IReadOnlyList<object> BuildContextMenuItems(
        double worldX, double worldY, long tolDbu, LayoutEditorViewModel? layout, Avalonia.Visual host)
    {
        var items = new List<object>();

        // ── WHAT IS HERE, AND WHAT IS NOT (owner, 2026-09-19) ─────────────────────────────────
        //
        // The copper-class rows and nothing else. Copy, Copy Coordinate, Place Source and Place
        // Load are the WINDOW's rows: each needs either the clipboard (which needs an anchor
        // control this overlay is not) or the document (which this overlay must never touch — see
        // this file's header). The window composes the one menu out of both halves, which is also
        // what keeps it to a single separator.
        if (Kind != RailMapKind.Class || ForceRegion is null) return items;

        long x = (long)Math.Round(worldX), y = (long)Math.Round(worldY);

        RailMapRegion? best = null;
        long bestArea = long.MaxValue;
        foreach (var r in Scene.Regions)
        {
            if (!r.Bounds.Contains(x, y)) continue;
            long area = Math.Max(1, r.Bounds.MaxX - r.Bounds.MinX) * Math.Max(1, r.Bounds.MaxY - r.Bounds.MinY);
            if (area < bestArea) { bestArea = area; best = r; }
        }
        if (best is not { } region) return items;

        MenuItem Row(string header, PdnCopperClass? forced, bool ticked) => new()
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = ticked,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(
                () => ForceRegion?.Invoke(region.Region, forced)),
        };

        items.Add(Row("Copper: treat as a trace", PdnCopperClass.Trace,
                      region.Forced && region.Class == PdnCopperClass.Trace));
        items.Add(Row("Copper: treat as spreading", PdnCopperClass.Spreading,
                      region.Forced && region.Class == PdnCopperClass.Spreading));
        items.Add(Row("Copper: use the measured classification", null, !region.Forced));
        return items;
    }
}
