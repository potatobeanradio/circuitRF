using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>What one drag on the cross-section MEANS. Resolved at the movement threshold and fixed
/// for the rest of the gesture (R-stk5-10).</summary>
internal enum StackupDragKind
{
    /// <summary>No gesture, or one that never passed the threshold.</summary>
    None,
    /// <summary>A band moving in z (R-stk5-1).</summary>
    Reorder,
    /// <summary>A barrel moving as a unit, both ends together (R-stk5-4).</summary>
    ViaSpan,
    /// <summary>A barrel sliding sideways — cosmetic, and persisted (R-stk5-10).</summary>
    ViaLane,
    /// <summary>One end of a span, from its gripper (R-stk5-5).</summary>
    ViaGripTop,
    /// <summary>The other end.</summary>
    ViaGripBottom,
    /// <summary>A resistive film: to the other face of its band, or onto another conductor (R-stk9-7).</summary>
    Film,
}

/// <summary>
/// <b>The three drags of brief-stackup-render-5-drag.md, as a state machine over a
/// <see cref="StackupScene"/>.</b>
///
/// <h3>It reads the scene and writes through the view model. It owns no geometry and no undo</h3>
/// <para>Every rect it reasons about — band, barrel, gripper, band column — came out of the scene
/// the renderer drew, which is R-stk1-1: a second copy of the placement arithmetic would answer
/// "where would this land?" about a picture nobody is looking at. And every commit is one call to
/// <c>TechEditorViewModel</c> or <c>StackupLayerRowViewModel</c>, so undo, live validation, the
/// dirty mark, the live-tech push and the card list all follow (series overview §0).</para>
///
/// <h3>ONE undo entry, on release (R-stk5-2, R-stk5-6)</h3>
/// <para>Nothing is written while the pointer is down. What moves is the OVERLAY — a ghost of the
/// thing being dragged and, for a reorder, the line it would drop on — and the model is touched
/// exactly once, by <see cref="Release"/>. Dragging the top copper of a nine-entry stack to the
/// bottom crosses four bands; four single-step calls would push four undo entries and one Ctrl-Z
/// would put it back one place, which reads as a broken undo.</para>
///
/// <h3>The stack does not re-flow under the pointer</h3>
/// <para>The scene is rebuilt on <see cref="Release"/> and not before. Re-laying the drawing out on
/// every pointer move would slide the drop target out from under the hand that is aiming at it.</para>
///
/// <h3>Framework-free, which is what makes it testable</h3>
/// <para>Floats and a scene in, a decision out. There is no input device in this project's tests and
/// no application host, so the canvas forwards its pointer events here and the gate drives the same
/// methods directly.</para>
/// </summary>
internal sealed class StackupDragController
{
    /// <summary>
    /// How far the pointer must move before a press becomes a drag rather than a click.
    ///
    /// <para>R-stk5-3: without it every click nudges the stack, because a press and a release at the
    /// "same" place differ by a pixel or two on any real pointing device. Below this the gesture is
    /// brief 3's selection and nothing else.</para>
    /// </summary>
    public const float Threshold = 4f;

    /// <summary>
    /// How many lanes the lateral drag snaps to across the band column (R-stk5-10).
    ///
    /// <para>"Nice looking stackups" means ALIGNED ones, and two vias land on the same lane only if
    /// something quantises them — a free-drag-only control makes alignment a matter of eyesight.
    /// Twelve is enough columns to separate the handful of via entries a real stackup carries without
    /// making the grid itself visible; <c>Alt</c> turns it off for the case the grid does not
    /// serve.</para>
    ///
    /// <para>A fixed count rather than one derived from the pane's width, because the SNAPPED value is
    /// what gets persisted: a grid whose spacing came from the pane would store a different number
    /// for the same gesture after a resize, and two vias aligned at one width would separate at
    /// another.</para>
    /// </summary>
    public const int LaneSnapDivisions = 12;

    // ── State ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the press ARMED. <see cref="StackupDragKind.None"/> when nothing is pressed.
    /// For a barrel this is <see cref="StackupDragKind.ViaSpan"/> as a placeholder — the axis has not
    /// been decided yet and <see cref="Kind"/> reports nothing until it has.</summary>
    private StackupDragKind _armed;
    private bool  _barrelAxisUndecided;
    private bool  _started;
    private StackupDragKind _kind;

    /// <summary>The entry being dragged, held by NAME — R-stk3-1's rule. A drag spans a commit (its
    /// own), and every object identity in the stackup is destroyed by one.</summary>
    private string? _layer;
    private float _x0, _y0;

    // What a release would do, recomputed on every move.
    private int     _targetBandIndex = -1;
    private float?  _insertY;
    private SKRect? _ghost;
    private StackupBarrel? _dragVia;
    private StackupBarrel? _dragViaSource;
    private string? _spanFrom;
    private string? _spanTo;
    private double? _lane;

    // A film drag: which film, the technology CanPlaceOn is asked about, and what a release would do.
    private LayerKey? _film;
    private Technology? _tech;
    private ConductorSheetSurface? _filmFace;
    private string? _filmTarget;
    private string? _refusal;

    /// <summary>Why the film under the pointer would not land here, in the binding's own words, or null.</summary>
    public string? RefusalReason => _started && _kind == StackupDragKind.Film ? _refusal : null;

    /// <summary>The face a film drag would set, or null.</summary>
    public ConductorSheetSurface? PreviewFilmFace => _started && _kind == StackupDragKind.Film ? _filmFace : null;

    /// <summary>The conductor a film drag would move to, or null.</summary>
    public string? PreviewFilmTarget => _started && _kind == StackupDragKind.Film ? _filmTarget : null;

    /// <summary>True once the pointer has passed <see cref="Threshold"/> — i.e. once this is a drag
    /// and not a click.</summary>
    public bool IsDragging => _started;

    /// <summary>True when a press landed on something a drag could move — before the threshold, and
    /// therefore before it is known whether this is a drag at all. What the canvas takes pointer
    /// CAPTURE on: a press on the background or on a label arms nothing and captures nothing.</summary>
    public bool IsArmed => _armed != StackupDragKind.None;

    /// <summary>What the live gesture is, or <see cref="StackupDragKind.None"/>.</summary>
    public StackupDragKind Kind => _started ? _kind : StackupDragKind.None;

    /// <summary>The entry the live gesture is moving, for the gate and for nobody else.</summary>
    public string? DragLayer => _started ? _layer : null;

    /// <summary>Which band position a reorder would drop on, or -1.</summary>
    public int TargetBandIndex => _started && _kind == StackupDragKind.Reorder ? _targetBandIndex : -1;

    /// <summary>The span a via drag would commit, or nulls.</summary>
    public (string? From, string? To) PreviewSpan =>
        _started && _kind is StackupDragKind.ViaSpan or StackupDragKind.ViaGripTop or StackupDragKind.ViaGripBottom
            ? (_spanFrom, _spanTo)
            : (null, null);

    /// <summary>The lane a lateral drag would commit, or null.</summary>
    public double? PreviewLane => _started && _kind == StackupDragKind.ViaLane ? _lane : null;

    /// <summary>This frame's drag chrome, laid over the overlay the canvas already built from hover
    /// and selection. Nothing here is state of its own — it is a projection of the fields above, so
    /// there is no third copy to keep in step.</summary>
    public StackupOverlay Decorate(StackupOverlay overlay)
    {
        if (!_started) return overlay;
        return overlay with
        {
            DragGhost = _ghost is { } g ? (g.Left, g.Top, g.Right, g.Bottom) : null,
            DragVia = _dragVia,
            DragViaSource = _dragViaSource,
            DragInsertY = _insertY,
            DragRefused = _refusal is not null,
        };
    }

    // ── Press ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Arms a gesture on what the press landed on. Nothing is a drag yet — <see cref="Update"/>
    /// decides that at the threshold.
    ///
    /// <para>A band arms a reorder, a barrel arms a barrel gesture whose axis is still open, and a
    /// gripper arms that end. <b>A via's BAND-like hit is never a reorder</b> (R-stk5-1: a via's list
    /// position is not z), a label arms nothing — it is there to be clicked and double-clicked — and
    /// neither does the background.</para>
    /// </summary>
    public void Arm(StackupScene scene, float x, float y, Technology? tech = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Reset();
        _tech = tech;

        if (scene.HitTest(x, y) is not { } hit) return;

        switch (hit.Kind)
        {
            case StackupHitKind.Band:
                _armed = StackupDragKind.Reorder;
                break;

            case StackupHitKind.Film:
                _armed = StackupDragKind.Film;
                _film = hit.FilmLayer;
                break;

            case StackupHitKind.ViaBarrel:
                _armed = StackupDragKind.ViaSpan;
                _barrelAxisUndecided = true;
                break;

            case StackupHitKind.ViaGripTop:
                _armed = StackupDragKind.ViaGripTop;
                break;

            case StackupHitKind.ViaGripBottom:
                _armed = StackupDragKind.ViaGripBottom;
                break;

            default:
                return;
        }

        _layer = hit.LayerName;
        _x0 = x;
        _y0 = y;
    }

    // ── Move ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the armed gesture. Returns true when the overlay changed and the canvas should
    /// repaint.
    /// </summary>
    /// <param name="freeLane">Alt is held: a lateral drag positions freely rather than snapping
    /// (R-stk5-10).</param>
    public bool Update(StackupScene scene, float x, float y, bool freeLane = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_armed == StackupDragKind.None || _layer is null) return false;

        float dx = x - _x0, dy = y - _y0;

        if (!_started)
        {
            if (Math.Abs(dx) < Threshold && Math.Abs(dy) < Threshold) return false;

            // R-stk5-10's dominant-axis lock, decided ONCE and never revisited. A barrel that
            // re-spans itself because the hand drifted upward while sliding it sideways is the
            // defect this rule exists to prevent, so the axis is read at the moment the gesture
            // becomes a drag and the rest of the drag obeys it.
            _kind = _barrelAxisUndecided
                ? (Math.Abs(dx) > Math.Abs(dy) ? StackupDragKind.ViaLane : StackupDragKind.ViaSpan)
                : _armed;
            _barrelAxisUndecided = false;
            _started = true;
        }

        return _kind switch
        {
            StackupDragKind.Reorder                                   => UpdateReorder(scene, y, dy),
            StackupDragKind.ViaLane                                   => UpdateLane(scene, dx, freeLane),
            StackupDragKind.ViaSpan                                   => UpdateSpan(scene, dy),
            StackupDragKind.ViaGripTop or StackupDragKind.ViaGripBottom => UpdateGrip(scene, y),
            StackupDragKind.Film                                      => UpdateFilm(scene, x, y, dy),
            _                                                         => false,
        };
    }

    // ── Release and cancel ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Commits the gesture — <b>one</b> <c>CommitEdit</c>, through the function the card already
    /// calls. Returns true when something was written.
    ///
    /// <para>A gesture that never passed the threshold writes nothing: the press has already
    /// selected (R-stk5-3), and that is the whole of what a click means here.</para>
    /// </summary>
    public bool Release(TechEditorViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (!_started || _layer is null) { Reset(); return false; }

        var row = vm.StackupLayers.FirstOrDefault(
            r => string.Equals(r.Layer.Name, _layer, StringComparison.Ordinal));
        var kind = _kind;
        int target = _targetBandIndex;
        var (from, to) = (_spanFrom, _spanTo);
        double? lane = _lane;
        var (film, face, filmTarget, refused) = (_film, _filmFace, _filmTarget, _refusal);
        Reset();

        if (kind == StackupDragKind.Film)
        {
            if (film is not { } key || refused is not null) return false;
            if (filmTarget is not null) return vm.MoveFilm(key, filmTarget);
            return face is { } f && vm.SetFilmFace(key, f);
        }

        if (row is null) return false;

        switch (kind)
        {
            case StackupDragKind.Reorder when target >= 0:
                // R-stk5-1: the SAME function the card's own arrows call, so the drag cannot acquire
                // a reorder rule of its own — the via refusal and the via-skipping walk are stated
                // once, there.
                vm.MoveStackupLayerTo(row, target);
                return true;

            case StackupDragKind.ViaSpan:
            case StackupDragKind.ViaGripTop:
            case StackupDragKind.ViaGripBottom:
                if (from is null || to is null) return false;
                row.CommitSpanPair(from, to, $"Span {row.Layer.Name} {from}→{to}");
                return true;

            case StackupDragKind.ViaLane:
                vm.SetViaDrawLane(row, lane);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// R-stk5-3's <c>Esc</c>, and the release that happened outside the canvas: no move, no undo
    /// entry, nothing written. Returns true when there was a gesture to abandon — which is what tells
    /// the key handler to stop here rather than fall through to brief 3's clear-selection.
    /// </summary>
    public bool Cancel()
    {
        bool had = _armed != StackupDragKind.None || _started;
        Reset();
        return had;
    }

    private void Reset()
    {
        _armed = StackupDragKind.None;
        _kind = StackupDragKind.None;
        _barrelAxisUndecided = false;
        _started = false;
        _layer = null;
        _targetBandIndex = -1;
        _insertY = null;
        _ghost = null;
        _dragVia = _dragViaSource = null;
        _spanFrom = _spanTo = null;
        _lane = null;
        _film = null;
        _tech = null;
        _filmFace = null;
        _filmTarget = null;
        _refusal = null;
    }

    // ── The four gestures ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-stk5-1/R-stk5-3. Where a reorder would drop, as a BAND position.
    ///
    /// <para>The insertion line snaps to non-via band boundaries only, which is the same rule the
    /// reorder itself obeys — a via has no position in z to insert beside. The slot is chosen by the
    /// bands whose MIDDLE the pointer has passed, so the boundary the line takes is the one the
    /// pointer is nearest.</para>
    /// </summary>
    private bool UpdateReorder(StackupScene scene, float y, float dy)
    {
        var bands = scene.Bands;
        if (bands.Count == 0) return false;

        int from = IndexOfBand(bands, _layer!);
        if (from < 0) return false;

        int slot = 0;
        while (slot < bands.Count && bands[slot].Rect.MidY < y) slot++;

        int target = slot > from ? slot - 1 : slot;
        float insertY = slot == 0 ? bands[0].Rect.Top : bands[slot - 1].Rect.Bottom;

        var ghost = bands[from].Rect;
        ghost.Offset(0, dy);

        bool changed = target != _targetBandIndex || _insertY != insertY || _ghost != ghost;
        _targetBandIndex = target;
        _insertY = insertY;
        _ghost = ghost;
        // A BAND is what is moving, and a translucent rectangle is what a band looks like. Only a via
        // has detail worth previewing — see PreviewBarrel.
        _dragVia = _dragViaSource = null;
        return changed;
    }

    /// <summary>
    /// R-stk5-4. Both ends step together and the number of conductors spanned is CONSTANT — the span
    /// stops at the ends of the stack rather than clamping one end and stretching, because a barrel
    /// that silently changes length when it hits the top copper is a via the user did not ask for.
    /// </summary>
    private bool UpdateSpan(StackupScene scene, float dy)
    {
        if (!Ends(scene, out var conductors, out var barrel, out int lo, out int hi, out bool fromIsUpper))
            return false;

        float targetTop = barrel.Rect.Top + dy;
        int nearest = NearestConductorTo(conductors, targetTop);

        int shift = Math.Clamp(nearest - lo, -lo, conductors.Count - 1 - hi);
        return SetSpanPreview(conductors, barrel, lo + shift, hi + shift, fromIsUpper);
    }

    /// <summary>
    /// R-stk5-5. One end, snapped to the nearest CONDUCTOR band under the pointer.
    ///
    /// <para>Two clamps, both of which have to be clamps rather than refusals at release — a gesture
    /// that ends in a message is a gesture the user has to undo by reading. The ends may not cross,
    /// and they may not coincide: a via spanning one conductor connects nothing. So the minimum is
    /// adjacent conductors.</para>
    ///
    /// <para>Snapping to conductors ONLY is not a convenience. A via terminating on a dielectric is
    /// not expressible in the model and never should become so.</para>
    /// </summary>
    private bool UpdateGrip(StackupScene scene, float y)
    {
        if (!Ends(scene, out var conductors, out var barrel, out int lo, out int hi, out bool fromIsUpper))
            return false;

        int nearest = NearestConductorTo(conductors, y, byEdge: false);

        if (_kind == StackupDragKind.ViaGripTop) lo = Math.Clamp(nearest, 0, hi - 1);
        else                                     hi = Math.Clamp(nearest, lo + 1, conductors.Count - 1);

        return SetSpanPreview(conductors, barrel, lo, hi, fromIsUpper);
    }

    /// <summary>
    /// R-stk5-10. The lane, as a fraction of the band column the scene itself measured
    /// (<see cref="StackupScene.BandColumn"/>) — never a re-derivation of it, or the barrel would
    /// not come back under the pointer.
    ///
    /// <para>Clamped so the whole barrel stays inside the column: a fraction that put its centre on
    /// the edge would hang half of it over the label column.</para>
    /// </summary>
    private bool UpdateLane(StackupScene scene, float dx, bool freeLane)
    {
        var col = scene.BandColumn;
        if (col.Width <= 0) return false;

        var barrel = scene.Barrels.FirstOrDefault(
            b => string.Equals(b.Name, _layer, StringComparison.Ordinal));
        if (barrel is null) return false;

        float half = StackupScene.BarrelWidth * 0.5f;
        double f = (barrel.Rect.MidX + dx - col.Left) / col.Width;
        if (!freeLane) f = Math.Round(f * LaneSnapDivisions) / LaneSnapDivisions;

        double loF = Math.Clamp((half + 1f) / col.Width, 0d, 1d);
        double hiF = Math.Clamp((col.Width - half - 1f) / col.Width, 0d, 1d);
        f = Math.Clamp(f, Math.Min(loF, hiF), Math.Max(loF, hiF));

        float cx = col.Left + (float)f * col.Width;
        var rect = new SKRect(cx - half, barrel.Rect.Top, cx + half, barrel.Rect.Bottom);

        bool changed = !Nullable.Equals(_lane, f) || _dragVia?.Rect != rect;
        _lane = f;
        PreviewBarrel(barrel, rect, barrel.SpanFromLayer, barrel.SpanToLayer);
        _insertY = null;
        return changed;
    }

    /// <summary>
    /// R-stk9-7. Over its own band a film snaps to the nearer face; over another conductor it would move
    /// there, if <see cref="ResistorFilmBinding.CanPlaceOn"/> says so; over anything else it is refused, and
    /// the reason is the binding's own sentence. Nothing is written until the release, and a refused release
    /// writes nothing.
    /// </summary>
    private bool UpdateFilm(StackupScene scene, float x, float y, float dy)
    {
        if (_film is not { } key || scene.FilmRectOf(key) is not { } source) return false;
        var strip = scene.Films.First(f => f.Layer == key);

        var (face, target, refusal) = ((ConductorSheetSurface?)null, (string?)null, (string?)null);
        SKRect? ghost = null;

        var via = scene.Barrels.FirstOrDefault(b => b.Rect.Contains(x, y));
        var band = scene.Bands.FirstOrDefault(b => y >= b.Rect.Top && y <= b.Rect.Bottom
                                                   && x >= b.Rect.Left && x <= b.Rect.Right);
        if (band is { } over && string.Equals(over.Name, strip.ConductorName, StringComparison.Ordinal))
        {
            face = y < over.Rect.MidY ? ConductorSheetSurface.Top : ConductorSheetSurface.Bottom;
            ghost = PlaceOn(over.Rect, source, face.Value);
        }
        else if (via is not null) refusal = _tech is null ? "" : ReasonFor(key, via.Name);
        else if (band is { } other)
        {
            if (_tech is { } tech && ResistorFilmBinding.CanPlaceOn(tech, key, other.Name, out string reason))
            {
                target = other.Name;
                ghost = PlaceOn(other.Rect, source, ResistorFilmBinding.LandingFace(tech, key, other.Name));
            }
            else refusal = _tech is null ? "" : ReasonFor(key, other.Name);
        }
        else refusal = "";

        ghost ??= new SKRect(source.Left, source.Top + dy, source.Right, source.Bottom + dy);

        bool changed = face != _filmFace || target != _filmTarget || refusal != _refusal || ghost != _ghost;
        (_filmFace, _filmTarget, _refusal, _ghost) = (face, target, refusal, ghost);
        _insertY = null;
        _dragVia = _dragViaSource = null;
        return changed;

        string ReasonFor(LayerKey film, string name)
        {
            ResistorFilmBinding.CanPlaceOn(_tech!, film, name, out string why);
            return why;
        }

        static SKRect PlaceOn(SKRect bandRect, SKRect from, ConductorSheetSurface surface) => surface == ConductorSheetSurface.Top
            ? new SKRect(from.Left, bandRect.Top, from.Right, bandRect.Top + from.Height)
            : new SKRect(from.Left, bandRect.Bottom - from.Height, from.Right, bandRect.Bottom);
    }

    // ── Shared span arithmetic ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conductor bands, the barrel being dragged, and its span as a pair of indices into them.
    ///
    /// <para><paramref name="fromIsUpper"/> is what preserves the entry's own spelling: the model
    /// holds <c>SpanFromLayer</c>/<c>SpanToLayer</c> and neither is required to be the upper one, so
    /// a drag that normalised them would rewrite a field the user did not touch and dirty the
    /// editor for it.</para>
    /// </summary>
    private bool Ends(
        StackupScene scene, out IReadOnlyList<StackupBand> conductors, out StackupBarrel barrel,
        out int lo, out int hi, out bool fromIsUpper)
    {
        conductors = [];
        barrel = null!;
        lo = hi = -1;
        fromIsUpper = true;

        var found = scene.Barrels.FirstOrDefault(
            b => string.Equals(b.Name, _layer, StringComparison.Ordinal));
        if (found is null) return false;
        barrel = found;

        var list = scene.Bands.Where(b => b.Kind == StackupKind.Conductor).ToList();
        if (list.Count < 2) return false;
        conductors = list;

        int a = IndexOfBand(list, barrel.SpanFromLayer);
        int b2 = IndexOfBand(list, barrel.SpanToLayer);
        if (a < 0 || b2 < 0) return false;

        fromIsUpper = a <= b2;
        lo = Math.Min(a, b2);
        hi = Math.Max(a, b2);
        return true;
    }

    private bool SetSpanPreview(
        IReadOnlyList<StackupBand> conductors, StackupBarrel barrel, int lo, int hi, bool fromIsUpper)
    {
        string upper = conductors[lo].Name, lower = conductors[hi].Name;
        string from = fromIsUpper ? upper : lower;
        string to   = fromIsUpper ? lower : upper;

        var rect = new SKRect(
            barrel.Rect.Left, conductors[lo].Rect.Top, barrel.Rect.Right, conductors[hi].Rect.Bottom);

        bool changed = !string.Equals(from, _spanFrom, StringComparison.Ordinal) ||
                       !string.Equals(to,   _spanTo,   StringComparison.Ordinal) ||
                       _dragVia?.Rect != rect;
        _spanFrom = from;
        _spanTo = to;
        PreviewBarrel(barrel, rect, from, to);
        _insertY = null;
        return changed;
    }

    /// <summary>
    /// <b>The live preview of a via drag: the barrel the release would produce, as a real
    /// <c>StackupBarrel</c>.</b>
    ///
    /// <para>Owner, 2026-09-13: the drag showed "a non-detailed ghost", and most of what a via IS
    /// lives in the detail — a plated barrel's two walls, the bore cut through the material, the
    /// outline that binds them into one object, the grippers at each end. So the preview is not a
    /// rectangle the renderer fills: it is the same record type the scene emits, carrying the same
    /// <c>Look</c>, <c>WallPx</c> and <c>Fill</c> as the barrel being dragged, and
    /// <c>StackupRenderer</c> draws it through the SAME method it draws a real one with. A preview
    /// painted by a second piece of code is one that can come to differ from what it previews.</para>
    ///
    /// <para>The grippers are re-derived from the new rect rather than carried over, by the same
    /// arithmetic <c>StackupScene.Build</c> uses — a preview whose handles stayed at the old ends
    /// would be showing the user the wrong thing to aim at.</para>
    ///
    /// <para><see cref="_dragViaSource"/> is the ORIGINAL, which the renderer then omits: a via is
    /// being moved, not copied, and a retracting gripper drag would otherwise leave the old barrel's
    /// tail sticking out past the new end.</para>
    /// </summary>
    private void PreviewBarrel(StackupBarrel source, SKRect rect, string? from, string? to)
    {
        float g = StackupScene.GripGlyphHalf + StackupScene.GripHitSlop;

        _dragViaSource = source;
        _dragVia = source with
        {
            Rect          = rect,
            GripTop       = new SKRect(rect.MidX - g, rect.Top    - g, rect.MidX + g, rect.Top    + g),
            GripBottom    = new SKRect(rect.MidX - g, rect.Bottom - g, rect.MidX + g, rect.Bottom + g),
            SpanFromLayer = from,
            SpanToLayer   = to,
        };
        _ghost = null;
    }

    private static int IndexOfBand(IReadOnlyList<StackupBand> bands, string? name)
    {
        for (int i = 0; i < bands.Count; i++)
            if (string.Equals(bands[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The conductor nearest a scene y — by its TOP edge when a whole barrel is stepping
    /// (the top edge is what the barrel's own top sits on), and by DISTANCE TO THE BAND when one
    /// gripper is being aimed at a band the pointer is inside.</summary>
    private static int NearestConductorTo(IReadOnlyList<StackupBand> conductors, float y, bool byEdge = true)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < conductors.Count; i++)
        {
            var r = conductors[i].Rect;
            float d = byEdge
                ? Math.Abs(r.Top - y)
                : y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0f;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
