using System.Diagnostics;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Parameter handles — dragging a PCell instance's artwork to edit the parameter that produced it.
/// See <c>docs/design/pcell-parameter-handles.md</c>.
///
/// <para><b>A handle drag edits the INSTANCE's parameters, never the generated cell (R-pch-7).</b> It
/// commits through <see cref="EditInstancePCellParameters"/>, which is copy-on-write by construction:
/// the new parameter set hashes to a different cell folder and the instance is repointed at it in one
/// <c>ReplaceInstanceCommand</c>. The old cell — and every sibling instance still referencing it — is
/// untouched, so dragging one instance can never change another. R9 is not weakened either: the
/// generated artwork is still read-only; what is being dragged is a grip on a parameter that happens
/// to be drawn where the artwork is.</para>
///
/// <para><b>The cursor is inverse-transformed into cell-local space; the handle is never
/// forward-transformed and projected in world (R-pch-8).</b> Doing it this way makes rotation,
/// mirroring and magnification correct for free — including the one that is easy to get wrong: with
/// <c>Mag = 2</c>, dragging two millimetres on screen is one millimetre in the cell. Projecting in
/// world would need that division written by hand, in a place where getting it wrong is a silent
/// factor of two.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>R-pch-10. Above this, a drag stops regenerating artwork per pointer move and falls
    /// back to grip-plus-readout, regenerating once on release. One frame at 60 Hz: past that the
    /// drag would visibly stutter, and a 743-shape vendor cell issuing a hundred boolean round trips
    /// per generate cannot be asked to keep up. Not an error and not reported — the readout still
    /// tracks, because the sensitivity was measured before the first correction.</summary>
    private const double DefaultLivePreviewBudgetMs = 16.0;

    /// <summary>
    /// The live-preview budget this editor actually uses. One frame at 60 Hz for every real caller.
    ///
    /// <para><b>Settable so a TEST can make the fallback unreachable</b>, and that is not a nicety.
    /// The budget is measured wall-clock, so it silently decides what a test OBSERVES: once a drag
    /// defers, <c>PreviewHandles</c> is null and only the dragged grip moves. That is what broke
    /// <c>MKlopfGripAndProfileTests.DraggingTheFarMiddleGrip_MovesTheFarEndCapGripsLive…</c> under a
    /// full-solution run (2026-08-16, twice in five runs) while it passed in isolation — with 7,000
    /// other tests on the cores the FIRST solve of the gesture overran 16 ms, the drag deferred, and
    /// the end-cap grip correctly stopped following. The behaviour that test exists to pin — every
    /// grip on a cell moves when the cell regenerates — is not a statement about machine speed, so it
    /// hands itself an unreachable budget rather than being tagged out of the routine gate.</para>
    ///
    /// <para><b>Instance-scoped on purpose</b>: a process-wide switch would leak between the tests
    /// running in parallel beside it — including <c>PCellHandleDegradationTests</c>, whose whole
    /// subject is a genuinely slow cell hitting this budget for real.</para>
    /// </summary>
    internal double LivePreviewBudgetMs { get; set; } = DefaultLivePreviewBudgetMs;

    // The grab radius is the caller's `tolDbu` — the SAME several-device-pixel tolerance L1d's own
    // handle hit-test uses, computed fresh per query from the live zoom by LayoutCanvas.HitTolDbu()
    // and never cached, never derived from SnapDbu. Reusing it rather than declaring a second
    // constant is what makes a grip and a vertex handle feel identical under the cursor.

    private PCellHandleDragState? _pcellHandleDrag;

    /// <summary>Reported once per (generator, parameter, reason) per session — a cell whose
    /// declaration is wrong would otherwise say so on every repaint.</summary>
    private readonly HashSet<string> _reportedHandleRejections = new(StringComparer.Ordinal);

    /// <summary>Test/diagnostic instrumentation: how many times a generator has been invoked for a
    /// live preview. Gate 11's mechanism — a deferred drag must regenerate on release, not per
    /// move — and, like every other cost claim in this codebase, a counter rather than a clock.</summary>
    internal int PCellHandlePreviewGenerateCount { get; private set; }

    /// <summary>True while the active drag is running in deferred mode (R-pch-10).</summary>
    internal bool PCellHandleDragIsDeferred => _pcellHandleDrag?.Deferred ?? false;

    /// <summary>True while the active drag was started under grip-lock (R-pch-12) — the state in
    /// which Alt has been spent and no longer suspends snap.</summary>
    internal bool PCellHandleDragIsLocked => _pcellHandleDrag?.Locked ?? false;

    // ── Grip-lock and hover (R-pch-12) ────────────────────────────────────────

    /// <summary>
    /// R-pch-12. True while Alt is held over a selection that HAS grips — the state in which the next
    /// press can only talk to a grip and can never move the instance.
    ///
    /// <para>Kept as state rather than derived per frame because the canvas learns about Alt from two
    /// unrelated places: a pointer move (which carries modifiers) and a bare Alt key-down with the
    /// pointer stationary. Both write here; <see cref="ClearGripLockArmed"/> is the LostFocus escape
    /// hatch, and it is not optional — a held-key latch whose key-up goes to whatever took focus is
    /// exactly the bug the Space-to-pan latch already caused once.</para>
    /// </summary>
    internal bool GripLockArmed { get; private set; }

    /// <summary>The grip under the cursor, or −1. Drawn emphasised and reported to the canvas as a
    /// cursor change, so which of the two gestures a press will get is visible BEFORE the press.</summary>
    internal int HoveredPCellHandleIndex { get; private set; } = -1;

    /// <summary>What the canvas should show the pointer as, for the grip currently under it.</summary>
    internal PCellGripCursor HoveredPCellHandleCursor { get; private set; } = PCellGripCursor.None;

    /// <summary>Called by the canvas on a bare Alt key-down/up, and on LostFocus. Arming is refused
    /// when the selection has no grips, so Alt keeps every other meaning it has everywhere else in
    /// this editor.</summary>
    internal void SetGripLockArmed(bool armed)
    {
        bool next = armed && !ScaleModeActive && _pcellHandleDrag is null && SelectionHasPCellHandles();
        if (next == GripLockArmed) return;
        GripLockArmed = next;
        RebuildOverlay();
    }

    /// <summary>Drops the armed latch. See <see cref="GripLockArmed"/> for why a LostFocus path has
    /// to exist at all.</summary>
    internal void ClearGripLockArmed()
    {
        if (!GripLockArmed && HoveredPCellHandleIndex < 0) return;
        GripLockArmed = false;
        HoveredPCellHandleIndex = -1;
        HoveredPCellHandleCursor = PCellGripCursor.None;
        RebuildOverlay();
    }

    /// <summary>Whether a press right now could reach a grip at all — the gate on grip-lock, and the
    /// reason Alt's other meanings (cycling, scale-about-centre, suspend-snap) are untouched
    /// everywhere else.</summary>
    private bool SelectionHasPCellHandles()
        => ResolveSelectedPCellHandles(out _, out _, out _, out _).Count > 0;

    /// <summary>
    /// Recomputes what the pointer is over, on an idle hover. This is the whole of the "the user can
    /// see which gesture they are about to get" half: the grip under the cursor is drawn emphasised
    /// and the canvas swaps in an axis cursor, so the 4-pixel boundary between "edit this parameter"
    /// and "move the whole cell" stops being invisible.
    ///
    /// <para>Uses the GRIP-LOCK radius while Alt is held, because that is the radius a press would
    /// actually use — the highlight has to promise exactly what the press will deliver.</para>
    /// </summary>
    private void UpdatePCellHandleHover(long px, long py, long tolDbu, long lockTolDbu, bool alt)
    {
        int previousIndex = HoveredPCellHandleIndex;
        var previousCursor = HoveredPCellHandleCursor;
        bool previousArmed = GripLockArmed;

        HoveredPCellHandleIndex = -1;
        HoveredPCellHandleCursor = PCellGripCursor.None;

        var handles = ResolveSelectedPCellHandles(out var inst, out _, out _, out _);
        GripLockArmed = alt && !ScaleModeActive && handles.Count > 0;

        if (handles.Count > 0 && inst is not null)
        {
            long tol = Math.Max(GripLockArmed ? Math.Max(lockTolDbu, tolDbu) : tolDbu, 1);
            double bestDistance = double.MaxValue;
            for (int i = 0; i < handles.Count; i++)
            {
                var m = ToMarker(handles[i], inst, active: false);
                double d = Math.Sqrt((double)(m.X - px) * (m.X - px) + (double)(m.Y - py) * (m.Y - py));
                if (d > tol || d >= bestDistance) continue;
                bestDistance = d;
                HoveredPCellHandleIndex = i;
                HoveredPCellHandleCursor = CursorFor(m);
            }
        }

        if (HoveredPCellHandleIndex != previousIndex || HoveredPCellHandleCursor != previousCursor
            || GripLockArmed != previousArmed)
            RebuildOverlay();
    }

    /// <summary>
    /// Which pointer shape announces this grip. A two-axis grip (R-pch-4a) and an angular one both
    /// travel in more than one direction, so both get the omnidirectional cursor rather than a lie
    /// about a single axis; everything else quantises its travel direction to the nearest of four.
    ///
    /// <para>No Y flip here, and that is deliberate rather than an oversight: world +Y is Y-UP and it
    /// is drawn as screen NORTH, so a world axis pointing up-and-right is a NE-SW cursor directly.
    /// The renderer negates Y because it works in Y-DOWN path space; a compass direction does not.</para>
    /// </summary>
    private static PCellGripCursor CursorFor(PCellHandleMarker m)
    {
        if (m.IsAngular || m.HasCrossAxis) return PCellGripCursor.All;
        if (m.AxisDx == 0 && m.AxisDy == 0) return PCellGripCursor.All;

        double deg = Math.Atan2(m.AxisDy, m.AxisDx) * (180.0 / Math.PI);
        if (deg < 0) deg += 180.0;          // an axis has no sense of direction, only orientation
        if (deg >= 180.0) deg -= 180.0;

        return deg switch
        {
            < 22.5  => PCellGripCursor.EastWest,
            < 67.5  => PCellGripCursor.NorthEastSouthWest,
            < 112.5 => PCellGripCursor.NorthSouth,
            < 157.5 => PCellGripCursor.NorthWestSouthEast,
            _       => PCellGripCursor.EastWest,
        };
    }

    /// <summary>
    /// The instance a grip drag is currently in flight on, or −1. Paired with
    /// <see cref="PCellHandleDragParameters"/> so a reader can tell WHICH instance the in-flight
    /// values belong to rather than assuming it is whatever happens to be selected.
    /// </summary>
    internal int PCellHandleDragInstanceIndex => _pcellHandleDrag?.InstanceIndex ?? -1;

    /// <summary>
    /// The parameter set the grip drag has solved to SO FAR — the committed set with the dragged
    /// parameter (and its cross axis, if any) replaced by the values the solver has reached. Null
    /// when no drag is in flight, and null until the first pointer move has actually solved
    /// something, so a press with no movement shows the committed values unchanged.
    ///
    /// <para>This exists so the Properties Inspector can show the value the user is dragging TO
    /// rather than the one they started from. It deliberately reports the SOLVED value, not the
    /// cursor's — R-pch-3 makes regeneration authoritative, so a clamped or quantized parameter must
    /// read the same in the panel as the grip's own position on screen. A panel that counted past a
    /// clamp while the grip sat still would be the panel lying about the design.</para>
    /// </summary>
    internal IReadOnlyDictionary<string, PCellValue>? PCellHandleDragParameters =>
        _pcellHandleDrag is { PendingValue: { } v } d ? MergedValues(d, v, d.PendingCrossValue) : null;

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// The grips to draw for the current selection, in world DBU. Empty unless exactly one instance
    /// (and no shape) is selected and its resolved cell is PCell-backed with declared handles.
    ///
    /// <para><b>Handles are resolved by invoking the generator, never read from the <c>.clay</c></b> —
    /// they are functions of the parameter values, so persisting them would be a second copy to go
    /// stale. A cell whose generator cannot be invoked (an untrusted kit, a missing interpreter) has
    /// no grips, which is the correct degradation: its parameters are still editable in the
    /// Properties Inspector.</para>
    ///
    /// <para>Drawn on the BASE (0,0) array placement only. An arrayed instance is one object with one
    /// parameter set, so every placement would show the same grips driving the same values — 2,500
    /// copies of them on a 50×50 array, all doing the same thing.</para>
    /// </summary>
    private IReadOnlyList<PCellHandleMarker> BuildPCellHandleMarkers(
        IReadOnlyDictionary<int, LayoutInstance> instanceDragOverrides)
    {
        // OWNER REPORT: the grip glyphs are drawn ON TOP of the snap glyph (LayoutRenderer.Draw's own
        // order), and a grip that has locked onto a feature sits exactly where that feature's marker
        // is — so the amber square hid the one mark saying WHICH feature is being snapped to, at
        // precisely the moment it is the only thing worth reading. While a grip drag has a snap glyph
        // showing, the grips yield to it entirely.
        //
        // Gated on the candidate being non-null rather than on _snapCandidateIsRealTarget, because
        // that field is what decides whether a marker is DRAWN at all (RebuildOverlay assigns
        // SnapMarker = _currentSnapCandidate) — which is the condition the report is about. The two
        // agree during a grip drag regardless: the synthetic grab echo needs _snapDragActive, which
        // only a marker-initiated grab sets, never a grip drag.
        //
        // Scoped to an ACTIVE grip drag, not to "a snap marker exists": hovering near a feature with a
        // PCell merely selected must keep showing its grips, or they would flicker away under the
        // cursor with nothing being dragged.
        if (_pcellHandleDrag is not null && _currentSnapCandidate is not null) return [];

        var resolved = ResolveSelectedPCellHandles(out var inst, out _, out _, out int instIdx);
        if (resolved.Count == 0 || inst is null) return [];

        // Whenever the instance is being DRAWN somewhere other than where the model has it, the grips
        // have to be placed against that same preview or they float off the artwork they belong to.
        // Two independent cases arrive through the one channel: an ordinary whole-instance move drag,
        // and a pinned-anchor grip drag's own R-pch-4b translate. Reading the override rather than
        // re-deriving either is what keeps the grips and the geometry in step by construction.
        if (instanceDragOverrides.TryGetValue(instIdx, out var previewInstance)) inst = previewInstance;
        else if (_pcellHandleDrag is { } pinning) inst = PreviewInstance(pinning);

        // EVERY grip is re-read from the drag's own regenerated cell, not just the one being dragged.
        // A cell's grips are all functions of the same parameter set — MKlopf's end-cap grips sit on
        // the outline whose width its impedances decide, and its far-end grips move with `L` — so
        // drawing the dragged one against the preview and the rest against the COMMITTED parameters
        // leaves the others sitting on artwork that is no longer under them, snapping into place only
        // on release. Filtered the same way `resolved` was, so the two lists stay index-parallel; a
        // count mismatch (a generator emitting a different number of grips at the dragged value)
        // falls back to the committed positions rather than pairing grips up by luck.
        var live = _pcellHandleDrag is { PreviewHandles: { } ph } d2
            ? UsableHandles(ph, d2.Origin.Parameters)
            : null;
        if (live is not null && live.Count != resolved.Count) live = null;

        var markers = new List<PCellHandleMarker>(resolved.Count);
        for (int i = 0; i < resolved.Count; i++)
        {
            bool active = _pcellHandleDrag is { } d && d.HandleIndex == i;
            bool hovered = _pcellHandleDrag is null && HoveredPCellHandleIndex == i;
            // While a grip is being dragged everything renders where the SOLVER put it, which is where
            // the regenerated cell actually places it (R-pch-3) rather than where the cursor is.
            var use = live?[i]
                      ?? (active && _pcellHandleDrag!.PreviewHandle is { } moved ? moved : resolved[i]);
            markers.Add(ToMarker(use, inst, active) with { Hovered = hovered, Armed = GripLockArmed });
        }
        return markers;
    }

    private PCellHandleMarker ToMarker(PCellHandle h, LayoutInstance inst, bool active)
    {
        var (wx, wy) = LayoutInstanceTransform.TransformPoint(h.X, h.Y, inst, 0, 0);
        var (ax, ay) = LayoutInstanceTransform.TransformPoint(h.AnchorX, h.AnchorY, inst, 0, 0);

        double dx, dy;
        if (h.Kind == PCellHandleKind.Angular)
        {
            // An Angular grip's AxisDeg is a REFERENCE direction, not a direction of travel — the grip
            // moves TANGENTIALLY, along the circle it swings on. Transforming a point along AxisDeg
            // (correct for Linear) would point the hint at a fixed compass bearing that has nothing to
            // do with where this grip goes. The tangent is derived from the ALREADY-TRANSFORMED radius,
            // so mirror and rotation are carried without a second rule.
            dx = -(wy - ay); dy = wx - ax;
        }
        else
        {
            // The travel direction in WORLD terms — obtained by transforming a point one step along the
            // declared axis and differencing, so the instance's own rotation and mirror are carried
            // without any second transform rule to keep in step with LayoutInstanceTransform.
            const long probe = 1_000;
            double rad = h.AxisDeg * (Math.PI / 180.0);
            var (px, py) = LayoutInstanceTransform.TransformPoint(
                h.AnchorX + (long)Math.Round(probe * Math.Cos(rad)),
                h.AnchorY + (long)Math.Round(probe * Math.Sin(rad)), inst, 0, 0);
            dx = px - ax; dy = py - ay;
        }

        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 0) { dx /= len; dy /= len; }

        return new PCellHandleMarker(wx, wy, ax, ay, dx, dy, h.DisplayLabel, active,
                                     h.Cross is not null, h.Kind == PCellHandleKind.Angular);
    }

    /// <summary>
    /// Resolves the selected instance's declared handles, dropping (and reporting once) any that
    /// cannot be dragged. Returns an empty list for every "not applicable" case — no selection, a
    /// multi-selection, a non-PCell cell, an unresolvable generator — none of which is a problem to
    /// report.
    /// </summary>
    private IReadOnlyList<PCellHandle> ResolveSelectedPCellHandles(
        out LayoutInstance? instance, out PCellOrigin? origin,
        out PCellGenerator? generator, out int instanceIndex)
    {
        instance = null; origin = null; generator = null; instanceIndex = -1;

        if (_selectedInstanceIndices.Count != 1 || _selectedIndices.Count != 0) return [];
        int idx = _selectedInstanceIndices[0];
        if ((uint)idx >= (uint)Model.Instances.Count) return [];

        var inst = Model.Instances[idx];
        var res = CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir);
        if (res.State != CellLayoutState.Resolved || res.View!.PCellOrigin is not { } o) return [];
        if (!PCellRegistry.TryGet(o.GeneratorId, out var gen)) return [];

        PCellResult result;
        try { result = _pcellHandleCache.GetOrGenerate(o.GeneratorId, gen, o.Parameters, Technology, PCellLayerSelection.Default); }
        catch (Exception) { return []; }   // a broken generator costs its grips, never the frame

        if (result.Handles is not { Count: > 0 }) return [];

        var usable = new List<PCellHandle>(result.Handles.Count);
        foreach (var h in result.Handles)
        {
            var why = PCellHandleSolver.Validate(h, o.Parameters);
            if (why == PCellHandleRejection.None) { usable.Add(h); continue; }
            ReportHandleRejection(o.GeneratorId, h, why);
        }

        instance = inst; origin = o; generator = gen; instanceIndex = idx;
        return usable;
    }

    /// <summary>
    /// The draggable subset of a handle list, by the SAME rule
    /// <see cref="ResolveSelectedPCellHandles"/> applies — so a list regenerated mid-drag stays
    /// index-parallel with the one the grips were built from. Silent: a declaration that is going to
    /// be rejected has already been reported once by the resolution path, and repeating it per
    /// pointer move would be noise.
    /// </summary>
    private static List<PCellHandle> UsableHandles(
        IReadOnlyList<PCellHandle> handles, IReadOnlyDictionary<string, PCellValue> parameters)
    {
        var usable = new List<PCellHandle>(handles.Count);
        foreach (var h in handles)
            if (PCellHandleSolver.Validate(h, parameters) == PCellHandleRejection.None) usable.Add(h);
        return usable;
    }

    private void ReportHandleRejection(string generatorId, PCellHandle handle, PCellHandleRejection why)
    {
        if (!_reportedHandleRejections.Add($"{generatorId}|{handle.Parameter}|{why}")) return;
        _messageSink?.Warning(PCellHandleSolver.Explain(why, generatorId, handle));
    }

    /// <summary>Long-lived cache for the RESOLUTION path only (drawing the grips), keyed on the
    /// unchanging parameter set of the selected instance. The drag has its own short-lived cache —
    /// see <see cref="PCellHandleDragState"/>.</summary>
    private readonly PCellGeometryCache _pcellHandleCache = new();

    // ── Drag ──────────────────────────────────────────────────────────────────

    private sealed class PCellHandleDragState
    {
        public required int InstanceIndex;
        public required int HandleIndex;
        public required PCellHandle Handle;
        public required PCellOrigin Origin;
        public required PCellGenerator Generator;
        public required LayoutInstance Instance;
        public required double ValuePerProjection;

        /// <summary>The perpendicular axis, when this grip declares one (R-pch-4a) — null on an
        /// ordinary one-degree-of-freedom grip, and null too when the cross axis was declared but
        /// could not be measured, in which case the primary still drags normally.</summary>
        public PCellHandle? CrossHandle;
        public double CrossValuePerProjection;

        /// <summary>Short-lived, and deliberately so: a drag produces a new parameter set per pointer
        /// move, so a long-lived cache would grow without bound over a session. Dropped with the
        /// drag.</summary>
        public readonly PCellGeometryCache Cache = new();

        public bool Deferred;
        public bool Moved;

        /// <summary>R-pch-12: this drag was started under GRIP-LOCK (Alt held at press). Alt is
        /// SPENT by that press — for the rest of the gesture it no longer means suspend-snap, so
        /// holding it to guarantee the grip does not silently cost the geometry snapping the grip was
        /// grabbed to use.</summary>
        public bool Locked;
        public PCellValue? PendingValue;
        public PCellValue? PendingCrossValue;

        /// <summary>R-pch-4b: how far the INSTANCE must move so the grip's anchor keeps the world
        /// position it had. Zero on an ordinary grip, and zero on a pinned one whose anchor happens
        /// not to move.</summary>
        public long PendingDx, PendingDy;
        public PCellHandle? PreviewHandle;

        /// <summary>EVERY grip the regenerated cell emitted, so the ones that are not being dragged
        /// still follow the artwork they sit on. Null in deferred mode (R-pch-10 buys its saving by
        /// not regenerating, so there is nothing to read the other grips from) and null before the
        /// first solve — both fall back to the committed positions.</summary>
        public IReadOnlyList<PCellHandle>? PreviewHandles;
        public LayoutView? PreviewView;
        public string Readout = "";
    }

    /// <summary>
    /// R-pch-8's entry point. Checked BEFORE the instance-body move drag — otherwise grabbing a grip
    /// would move the whole instance instead, which is the one interaction failure a user cannot
    /// work around.
    /// </summary>
    /// <param name="locked">R-pch-12: this press was made under grip-lock — the caller passes the
    /// larger lock radius as <paramref name="tolDbu"/>. Marks the resulting drag as locked.</param>
    /// <param name="claimedPress">True when a grip was within <paramref name="tolDbu"/> and therefore
    /// OWNS this press, whether or not a drag actually began. The two answers differ exactly once: a
    /// grip whose sensitivity cannot be measured refuses, and under grip-lock that refusal must stay a
    /// refusal rather than becoming whatever the press would otherwise have done (R-dup-1 made that a
    /// duplicate drag, so "false means nothing was here" would silently turn a dead grip into a copy).
    /// A press with no grip in range claims nothing and falls through.</param>
    private bool TryBeginPCellHandleDrag(long px, long py, long tolDbu, bool locked, out bool claimedPress)
    {
        claimedPress = false;
        var handles = ResolveSelectedPCellHandles(out var inst, out var origin, out var gen, out int instIdx);
        if (handles.Count == 0 || inst is null || origin is null || gen is null) return false;


        long tol = Math.Max(tolDbu, 1);
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < handles.Count; i++)
        {
            var m = ToMarker(handles[i], inst, active: false);
            double d = Math.Sqrt((double)(m.X - px) * (m.X - px) + (double)(m.Y - py) * (m.Y - py));
            if (d <= tol && d < bestDistance) { bestDistance = d; best = i; }
        }
        if (best < 0) return false;
        claimedPress = true;

        var handle = handles[best];
        var localCache = new PCellGeometryCache();
        PCellResult Generate(IReadOnlyDictionary<string, PCellValue> p)
            => localCache.GetOrGenerate(origin.GeneratorId, gen, p, Technology, PCellLayerSelection.Default);

        // R-pch-2: measure how much this parameter moves its own grip, by asking the generator. Once
        // per gesture — not per pointer move.
        if (!PCellHandleSolver.MeasureSensitivity(Generate, origin.Parameters, handle, best,
                out double valuePerProjection, out var why))
        {
            ReportHandleRejection(origin.GeneratorId, handle, why);
            return false;
        }

        var drag = new PCellHandleDragState
        {
            InstanceIndex = instIdx,
            HandleIndex = best,
            Handle = handle,
            Origin = origin,
            Generator = gen,
            Instance = inst,
            ValuePerProjection = valuePerProjection,
            Locked = locked,
        };

        // R-pch-4a: a grip may drive a second parameter across its own axis. Measured the same way
        // and through the same solver — the cross axis is simply this handle turned 90°, so nothing
        // below needs a second code path. A cross axis that cannot be measured is dropped on its own
        // and REPORTED, leaving the primary axis dragging normally: losing one direction of a
        // two-axis grip should not cost the other.
        if (handle.Cross is not null)
        {
            var crossHandle = handle.AsCrossHandle();
            // The generator's own list still names this grip by its PRIMARY parameter, so that is
            // the name the solver has to look it up by.
            if (PCellHandleSolver.MeasureSensitivity(Generate, origin.Parameters, crossHandle, best,
                    out double crossVpp, out var crossWhy, matchParameter: handle.Parameter))
            {
                drag.CrossHandle = crossHandle;
                drag.CrossValuePerProjection = crossVpp;
            }
            else
            {
                ReportHandleRejection(origin.GeneratorId, crossHandle, crossWhy);
            }
        }

        // R-pch-10: a generator that already knows it is too expensive to redraw per frame says so,
        // and is believed without spending a regeneration to find out. Auto still measures.
        if (PreviewModeOf(origin, gen) == PCellPreviewMode.Deferred) drag.Deferred = true;

        _pcellHandleDrag = drag;

        // The armed highlight has done its job the moment the grip is caught: from here the ACTIVE
        // grip is the thing to look at, and leaving every other grip lit would say the press is still
        // open when it is not.
        GripLockArmed = false;
        HoveredPCellHandleIndex = -1;
        HoveredPCellHandleCursor = PCellGripCursor.None;

        RebuildOverlay();
        return true;
    }

    /// <summary>The ordinary, unlocked entry point — a press only ever claims a grip by starting a
    /// drag on it, so the two answers coincide and the caller has nothing to distinguish.</summary>
    private bool TryBeginPCellHandleDrag(long px, long py, long tolDbu)
        => TryBeginPCellHandleDrag(px, py, tolDbu, locked: false, out _);

    /// <summary>
    /// One drag step. Snaps in WORLD space (the user is aligning to the grid they can see), then
    /// inverse-transforms into the cell's own frame, then projects — never the other way round.
    /// </summary>
    private void UpdatePCellHandleDrag(long px, long py, bool suspendSnap)
    {
        if (_pcellHandleDrag is not { } drag) return;

        // Geometry snap overrides grid snap, and ONLY when a real feature is in range — the same rule
        // every other drag in this editor follows (R-cmb-4/5). Without this a grip could only ever
        // land on the grid, so lining a microstrip's end up with the pad or trace it has to meet was
        // a matter of zooming in and eyeballing it. The synthetic "keep the marker visible" echo is
        // deliberately NOT a target: _snapCandidateIsRealTarget is false for it, so a grip dragged
        // through open space still snaps to the grid rather than freezing on its own grab point.
        bool geometryTarget = !suspendSnap && _snapCandidateIsRealTarget && _currentSnapCandidate is not null;

        var (sx, sy) = geometryTarget
            ? (_currentSnapCandidate!.Value.X, _currentSnapCandidate!.Value.Y)
            : LayoutSnapping.SnapPoint(px, py, SnapDbu, suspendSnap);

        // R-pch-11 quantizes a LENGTH parameter onto the snap lattice so a committed value is a whole
        // number of the document's own units. That is right for a grid-snapped drag and wrong for a
        // geometry-snapped one: a real feature is under no obligation to sit on the grid, so rounding
        // the solved length afterwards would drag the grip back off the very edge it was aimed at.
        bool suspendQuantize = suspendSnap || geometryTarget;
        var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(sx, sy, drag.Instance, 0, 0);
        double target = drag.Handle.Project(lx, ly);

        PCellResult Generate(IReadOnlyDictionary<string, PCellValue> p)
        {
            PCellHandlePreviewGenerateCount++;
            return drag.Cache.GetOrGenerate(drag.Origin.GeneratorId, drag.Generator, p,
                                            Technology, PCellLayerSelection.Default);
        }

        // Where this tick backs out to if the value it reaches folds the artwork through itself
        // (see the overlap guard below) — the last value that did not.
        var priorValue   = drag.PendingValue;
        var priorCross   = drag.PendingCrossValue;
        var priorReadout = drag.Readout;

        // Time only a drag that is still deciding. A generator that DECLARED itself deferred is
        // believed without measuring — that is the whole saving.
        var stopwatch = drag.Moved || drag.Deferred ? null : Stopwatch.StartNew();
        var solved = PCellHandleSolver.Solve(Generate, drag.Origin.Parameters, drag.Handle,
                                             drag.HandleIndex, target, drag.ValuePerProjection,
                                             quantize: MakeParameterQuantizer(drag.Handle.Quantity, suspendQuantize));
        stopwatch?.Stop();

        drag.Moved = true;
        if (!solved.Ok) return;   // the design is unchanged; the grip simply does not follow

        drag.PendingValue = solved.Value;
        drag.Readout = $"{drag.Handle.DisplayLabel} = {FormatHandleValue(drag.Handle.Quantity, solved.Value)}";

        // R-pch-4a: the perpendicular axis, solved SECOND and against the parameters the first solve
        // already settled. Sequential rather than simultaneous because the two are only independent
        // as scalars — the geometry a cell draws for one may well depend on the other, and solving
        // the cross axis against a stale primary would chase a target that has already moved.
        double crossTarget = drag.Handle.ProjectCross(lx, ly);
        PCellHandle? crossAchieved = null;
        var baseForCross = drag.Origin.Parameters;
        if (drag.CrossHandle is { } cross)
        {
            baseForCross = MergedParameters(drag.Origin.Parameters, drag.Handle.Parameter, solved.Value);
            var crossSolved = PCellHandleSolver.Solve(Generate, baseForCross, cross,
                                                      drag.HandleIndex, crossTarget, drag.CrossValuePerProjection,
                                                      matchParameter: drag.Handle.Parameter,
                                                      quantize: MakeParameterQuantizer(cross.Quantity, suspendQuantize));
            if (crossSolved.Ok)
            {
                crossAchieved = crossSolved.Achieved;
                drag.PendingCrossValue = crossSolved.Value;
                drag.Readout += $"   {cross.DisplayLabel} = {FormatHandleValue(cross.Quantity, crossSolved.Value)}";
            }
        }

        // R-pch-10: the FIRST solve of a gesture decides whether this drag can afford live artwork.
        // Measured once, held for the rest of the gesture — re-deciding per move would make the
        // preview flicker between modes on a cell that sits near the budget.
        if (stopwatch is not null && stopwatch.Elapsed.TotalMilliseconds > LivePreviewBudgetMs)
            drag.Deferred = true;

        if (drag.Deferred)
        {
            // R-pch-10: no ghost artwork, and — the part that actually saves the work — no further
            // regeneration to find out where the grip went. The solver already regenerated to reach
            // this value and hands back the handle the generator emitted, so the grip's real
            // position (and its real anchor) come for free rather than being reconstructed.
            drag.PreviewHandle = (drag.CrossHandle is not null ? crossAchieved : null) ?? solved.Achieved;
            drag.PreviewHandles = null;
            drag.PreviewView = null;
            ApplyAnchorPin(drag);
            RebuildOverlay();
            return;
        }

        var merged = MergedValues(drag, solved.Value, drag.PendingCrossValue);
        PCellResult preview;
        try { preview = Generate(merged); }
        catch (Exception) { return; }

        // OWNER REPORT (MKlopf, shortening L by its grip): the drag must not push the geometry through
        // itself. Shrink an offset taper's length far enough and the centreline's curvature exceeds
        // what its own trace width can turn through, so the inner edge crosses the outer one and the
        // outline folds — a shape that is not manufacturable and does not mean anything electrically.
        //
        // The grip STOPS at the last value that did not fold, rather than refusing the whole gesture:
        // a drag is a continuous search for a value, and the useful answer at the boundary is the
        // boundary itself. Deliberately scoped to the DRAG — a value typed into the Properties
        // Inspector or the parameter dialog still goes through, because that is a deliberate,
        // reviewable act on a named number, and this editor's standing rule is to report a bad
        // parameter rather than to forbid one.
        if (PreviewFoldsThroughItself(preview))
        {
            drag.PendingValue      = priorValue;
            drag.PendingCrossValue = priorCross;
            drag.Readout           = priorReadout;
            return;   // previous preview and grip position stand; the grip simply stops here
        }

        // Where the generator ACTUALLY put the grips — they are drawn there, not under the cursor.
        // The whole list is kept, not only the dragged one: every grip on this cell moved when the
        // cell regenerated, and BuildPCellHandleMarkers redraws all of them from it.
        drag.PreviewHandle = FindHandle(preview.Handles, drag.Handle, drag.HandleIndex);
        drag.PreviewHandles = preview.Handles;
        ApplyAnchorPin(drag);

        var ghost = new LayoutView
        {
            DbuPerMicron = Model.DbuPerMicron,
            DisplayUnit  = Model.DisplayUnit,
            SnapDbu      = Model.SnapDbu,
        };
        ghost.Shapes.AddRange(preview.Shapes);
        drag.PreviewView = ghost;

        RebuildOverlay();
    }

    /// <summary>
    /// Vertices this guard is willing to test in one pointer move. The sweep behind
    /// <see cref="LayoutSelfIntersection.Test"/> is O(n²) per shape, which is nothing on a microstrip
    /// outline (a couple of hundred vertices) and real money on a vendor cell with hundreds of shapes.
    /// Past the budget the check is SKIPPED and the move accepted: a grip that stuttered would be a
    /// worse failure than one that lets an exotic cell reach a self-overlapping value, and a generator
    /// that big is not one whose overlap a per-frame geometric test was going to catch cheaply.
    /// </summary>
    private const int SelfOverlapVertexBudget = 20_000;

    /// <summary>
    /// True when any shape in <paramref name="preview"/> crosses itself. Reuses L1d's own
    /// <see cref="LayoutSelfIntersection"/> rather than asking the generator, so it works for every
    /// PCell — the question "did this outline fold through itself" is about the geometry that came
    /// out, not about which parameter produced it.
    /// </summary>
    private bool PreviewFoldsThroughItself(PCellResult preview)
    {
        int budget = SelfOverlapVertexBudget;
        foreach (var shape in preview.Shapes)
        {
            budget -= OutlineVertexCount(shape);
            if (budget < 0) return false;   // too much geometry to test per frame — accept the move
            if (LayoutSelfIntersection.Test(shape, Technology)) return true;
        }
        return false;
    }

    /// <summary>Vertices in a shape's own outline, for the budget above. Rect/Circle/RoundedRect/Via/
    /// Label cannot self-intersect by construction and cost nothing to skip.</summary>
    private static int OutlineVertexCount(LayoutShape shape) => shape switch
    {
        PolygonShape p => p.Xy.Length / 2,
        CurveShape   c => c.Xy.Length / 2,
        PathShape    p => p.Xy.Length / 2,
        _              => 0,
    };

    // ── Units: the two things the host needs a declared quantity for ───────────
    //
    // R-pch-2 keeps the SENSITIVITY unit-free by measuring it, and that stays true. These two are a
    // different question: what to PRINT, and which lattice the committed value must land on. Neither
    // is derivable from a measurement, so both key off the generator's own PCellHandleQuantity and
    // both degrade to the previous behaviour when it says nothing.

    /// <summary>
    /// One snap step expressed in the parameter's own SI units. A length parameter is metres
    /// (R-pc-6) and one DBU is 1e-6/DbuPerMicron metres, so the layout's own <see cref="SnapDbu"/>
    /// converts directly — no DBU round trip, and therefore no second rounding rule to disagree with
    /// <see cref="PCellUnits.MetresToDbu"/>.
    /// </summary>
    private double SnapStepMetres => SnapDbu * 1e-6 / Math.Max(1, Model.DbuPerMicron);

    /// <summary>
    /// The lattice a solved value must land on, or null for "any value will do".
    ///
    /// <para>Only a LENGTH is quantized, and only when the user actually has snapping on and is not
    /// holding Alt to suspend it — the same two conditions every other snapped gesture in this editor
    /// already answers to. An angle is deliberately excluded: a degree is not a distance, and rounding
    /// one onto a length grid would be arithmetic with no meaning behind it.</para>
    /// </summary>
    private Func<double, double>? MakeParameterQuantizer(PCellHandleQuantity quantity, bool suspendSnap)
    {
        if (quantity != PCellHandleQuantity.Length || suspendSnap) return null;
        double step = SnapStepMetres;
        if (step <= 0 || !double.IsFinite(step)) return null;
        return v => Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
    }

    /// <summary>
    /// The drag readout's value, in terms a person reading a layout recognises: a length in the
    /// document's own display unit (mil on a board, µm on a die), an angle in degrees, anything
    /// undeclared exactly as before.
    /// </summary>
    private string FormatHandleValue(PCellHandleQuantity quantity, PCellValue value)
    {
        if (value.Kind != PCellValueKind.Real) return PCellHandleSolver.FormatForReadout(value);
        double v = value.AsReal();

        switch (quantity)
        {
            case PCellHandleQuantity.Length:
            {
                long dbu = PCellUnits.MetresToDbu(v, Model.DbuPerMicron);
                return $"{LayoutUnits.Format(dbu, DisplayUnit, Model.DbuPerMicron)} {LayoutUnits.Suffix(DisplayUnit)}";
            }
            case PCellHandleQuantity.Angle:
                return $"{v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}°";
            default:
                return PCellHandleSolver.FormatForReadout(value);
        }
    }

    private void CommitPCellHandleDrag()
    {
        if (_pcellHandleDrag is not { } drag) { ResetPCellHandleDragState(); return; }

        // A press with no movement is a selection click on a grip, not an edit — nothing to commit
        // and nothing to undo.
        if (drag.Moved && drag.PendingValue is { } value)
        {
            // Owner report, 2026-08-12: a grip could be dragged past zero and LEFT there, persisting a
            // negative width or arm length — which on MTee/MCross draws an arm back over the one that
            // belongs on that side and reads as the render glitching out. A negative value is fine
            // WHILE dragging (the owner's own call, and the generator stays sign-transparent so the
            // grip keeps following the cursor); it is normalised here, at mouse up, which is the one
            // moment a value stops being transient. PCellDimensionSign owns which parameters this
            // applies to and why a width recovers exactly while a length only recovers its magnitude.
            var normalizedValue = PCellDimensionSign.Normalize(
                drag.Origin.GeneratorId, drag.Handle.Parameter, value);

            // Both axes of a two-axis grip commit TOGETHER, in one edit and therefore one undo
            // entry. Committing them separately would make a single drag two undo steps, which is
            // not what the user did.
            var edit = new Dictionary<string, PCellValue>(StringComparer.Ordinal)
            {
                [drag.Handle.Parameter] = normalizedValue,
            };
            bool normalized = normalizedValue != value;

            if (drag.CrossHandle is { } cross && drag.PendingCrossValue is { } crossValue)
            {
                var normalizedCross = PCellDimensionSign.Normalize(
                    drag.Origin.GeneratorId, cross.Parameter, crossValue);
                edit[cross.Parameter] = normalizedCross;
                normalized |= normalizedCross != crossValue;
            }

            // The pinning translate was measured against the geometry the PREVIEW drew, which is the
            // geometry of the value we just changed. Re-measure it, or a pinned grip whose value
            // flipped sign lands the instance twice the anchor's own travel away from where it should
            // — the anchor moved to -a during the drag and to +a at commit, and the stale delta
            // corrects for the wrong one of the two.
            if (normalized) RemeasureAnchorPin(drag, edit);

            // R-pch-4b: the parameter edit and the anchor-pinning translate are ONE command, so a
            // pinned drag is still one undo entry. Two commands would let Undo put the geometry back
            // while leaving the instance moved.
            EditInstancePCellParameters(drag.InstanceIndex, edit, drag.PendingDx, drag.PendingDy);
        }

        ResetPCellHandleDragState();
    }

    private void ResetPCellHandleDragState()
    {
        _pcellHandleDrag = null;
        DrawReadoutText = "";
    }

    /// <summary>
    /// Re-reads where the dragged grip's ANCHOR lands for a set of values that is no longer the one
    /// the preview was drawn from, and recomputes the R-pch-4b pinning translate from it.
    ///
    /// <para>Only reached when <see cref="PCellDimensionSign"/> changed something at commit. Reuses
    /// the drag's own preview cache and the same <see cref="ApplyAnchorPin"/> the move handler uses,
    /// so the pin is measured exactly one way — reading the regenerated anchor, never predicting it
    /// (see that method's own note on why prediction cannot work).</para>
    ///
    /// <para>A generator that throws, or a handle that vanished, leaves the existing translate in
    /// place: a slightly-off pin is a far better outcome than losing the edit, and the value itself is
    /// already correct either way.</para>
    /// </summary>
    private void RemeasureAnchorPin(PCellHandleDragState drag, IReadOnlyDictionary<string, PCellValue> edit)
    {
        if (!drag.Handle.KeepAnchorFixed) return;

        var merged = new Dictionary<string, PCellValue>(drag.Origin.Parameters, StringComparer.Ordinal);
        foreach (var (k, v) in edit) merged[k] = v;

        try
        {
            var regenerated = drag.Cache.GetOrGenerate(
                drag.Origin.GeneratorId, drag.Generator, merged, Technology, PCellLayerSelection.Default);
            if (FindHandle(regenerated.Handles, drag.Handle, drag.HandleIndex) is { } moved)
            {
                drag.PreviewHandle = moved;
                ApplyAnchorPin(drag);
            }
        }
        catch (Exception) { /* keep the translate we have — the parameter edit still stands */ }
    }

    private static IReadOnlyDictionary<string, PCellValue> MergedParameters(
        IReadOnlyDictionary<string, PCellValue> baseParameters, string name, PCellValue value)
    {
        var merged = new Dictionary<string, PCellValue>(baseParameters, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return merged;
    }

    /// <summary>
    /// R-pch-4b. Works out how far the INSTANCE must move so the grip's anchor keeps the world
    /// position it had before the drag — which is what "drag this end and hold the other still"
    /// means once you notice that a generator cannot move its own origin (R4 pins pin 1 at (0,0)).
    ///
    /// <para>Read from the regenerated handle's own anchor rather than predicted: how far an anchor
    /// moves is not a fixed function of the parameter (MLIN's left-edge anchor moves by the whole
    /// length change, its top-edge anchor by half the width change), so any rule for guessing it
    /// would be wrong for some cell. The generator re-emits the anchor on every generate; the host
    /// just reads it.</para>
    /// </summary>
    private void ApplyAnchorPin(PCellHandleDragState drag)
    {
        drag.PendingDx = drag.PendingDy = 0;
        if (!drag.Handle.KeepAnchorFixed || drag.PreviewHandle is not { } moved) return;

        var (beforeX, beforeY) = LayoutInstanceTransform.TransformPoint(
            drag.Handle.AnchorX, drag.Handle.AnchorY, drag.Instance, 0, 0);
        var (afterX, afterY) = LayoutInstanceTransform.TransformPoint(
            moved.AnchorX, moved.AnchorY, drag.Instance, 0, 0);

        drag.PendingDx = beforeX - afterX;
        drag.PendingDy = beforeY - afterY;
    }

    /// <summary>The instance as it should currently be DRAWN — translated when a pinned anchor is
    /// holding still. Also what the grips are placed against, so they track the shifted cell rather
    /// than floating away from it mid-drag.</summary>
    private LayoutInstance PreviewInstance(PCellHandleDragState drag)
    {
        if (drag.PendingDx == 0 && drag.PendingDy == 0) return drag.Instance;
        var shifted = LayoutGeometry.Clone(drag.Instance);
        LayoutGeometry.TranslateBy(shifted, drag.PendingDx, drag.PendingDy);
        return shifted;
    }

    /// <summary>The generator's declared preview mode for this cell's own parameter set, resolved
    /// through the resolution cache so it costs nothing on a drag that has already drawn grips.
    /// Any failure means Auto — a broken generator must not be read as a preference.</summary>
    private PCellPreviewMode PreviewModeOf(PCellOrigin origin, PCellGenerator generator)
    {
        try
        {
            return _pcellHandleCache
                .GetOrGenerate(origin.GeneratorId, generator, origin.Parameters, Technology, PCellLayerSelection.Default)
                .Preview;
        }
        catch (Exception) { return PCellPreviewMode.Auto; }
    }

    /// <summary>Both axes of a two-axis grip folded into one parameter set.</summary>
    private static IReadOnlyDictionary<string, PCellValue> MergedValues(
        PCellHandleDragState drag, PCellValue value, PCellValue? crossValue)
    {
        var merged = new Dictionary<string, PCellValue>(drag.Origin.Parameters, StringComparer.Ordinal)
        {
            [drag.Handle.Parameter] = value,
        };
        if (drag.CrossHandle is { } cross && crossValue is { } cv) merged[cross.Parameter] = cv;
        return merged;
    }

    /// <summary>Same slot first, then the first handle naming the same parameter — a cell may declare
    /// several grips for one parameter and the list may legitimately change length.</summary>
    private static PCellHandle? FindHandle(IReadOnlyList<PCellHandle>? handles, PCellHandle wanted, int index)
    {
        if (handles is null || handles.Count == 0) return null;
        if ((uint)index < (uint)handles.Count &&
            string.Equals(handles[index].Parameter, wanted.Parameter, StringComparison.Ordinal))
            return handles[index];
        foreach (var h in handles)
            if (string.Equals(h.Parameter, wanted.Parameter, StringComparison.Ordinal)) return h;
        return null;
    }
}
