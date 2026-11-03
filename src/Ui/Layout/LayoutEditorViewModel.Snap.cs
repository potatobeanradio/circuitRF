// Geometry snap — toggles, the snap-distance control (§1), and the click-through/grab-target drag
// (§2.6) of docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md. A partial-class extension of
// LayoutEditorViewModel, kept in its own file per this codebase's convention for a large concern that
// deserves its own home (mirrors LayoutEditorViewModel.Scale.cs/.Booleans.cs/.Clipboard.cs).
//
// docs/sonnet-briefs/brief-snap-combobox-and-consistency.md §1: THIS FILE is where the snap-distance
// ladder is built (RebuildSnapLadderOptions, below) — not LayoutSnapping.cs, which holds only the L1b
// snap-value/snap-point math and knows nothing about the toolbar control.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    // ── §1 — Snap-distance control ──────────────────────────────────────────────

    /// <summary>The last non-zero <see cref="SnapDbu"/> — restored by <see cref="ToggleSnapDbuEnabled"/>
    /// (F9) when re-enabling snap after it was toggled off. Seeded from whatever <see cref="SnapDbu"/>
    /// already is at construction (a fresh document always starts with snap ON, per L0c's seeded
    /// default), so the very first F9 press has something sensible to restore even if the user never
    /// typed a value.</summary>
    private long _lastNonzeroSnapDbu;

    /// <summary>F9 (R-snp-1) — mirrors AutoCAD's grid-snap toggle: off sets <see cref="SnapDbu"/> to 0
    /// (LayoutSnapping's existing "SnapDbu &lt;= 0 means none" convention — no new off-state needed);
    /// on restores the last non-zero value.</summary>
    public void ToggleSnapDbuEnabled()
    {
        if (SnapDbu > 0)
        {
            _lastNonzeroSnapDbu = SnapDbu;
            SnapDbu = 0;
        }
        else
        {
            SnapDbu = _lastNonzeroSnapDbu > 0 ? _lastNonzeroSnapDbu : Model.SnapDbu;
        }
    }

    /// <summary>R-snp-2: the ladder is derived from the resolved technology's own
    /// <see cref="Technology.DefaultSnapDbu"/> (×1, ×5, ×10, ×25, ×50), each rendered through
    /// <see cref="LayoutUnits.Format"/> in the document's current <see cref="DisplayUnit"/> — never a
    /// fixed "1 · 5 · 10" list (the label-height defect again: one WHAT). Falls back to a µm-scale
    /// ladder when no technology resolves.
    /// <para/>
    /// docs/sonnet-briefs/brief-snap-ladder-crash.md R-crash-1 — load-bearing invariant, do not
    /// reintroduce a dependency on <see cref="SnapDbu"/> here: <b>an items collection must not be a
    /// function of the selection made from it.</b> A prior fix (brief-snap-combobox-and-consistency.md
    /// R-cmb-1) inserted the document's current <see cref="SnapDbu"/> into this list to keep the
    /// combobox from ever showing blank, and wired the rebuild to <c>OnSnapDbuChanged</c> — but
    /// selecting a ladder entry itself SETS <see cref="SnapDbu"/>, so that rebuild fired from inside
    /// Avalonia's own <c>SelectionChanged</c> notification, mutating the very
    /// <see cref="ObservableCollection{T}"/> the <c>SelectionModel</c> was mid-way through reading —
    /// `ArgumentOutOfRangeException`, reliably, on every selection. The real requirement was only ever
    /// "never blank," not "the list must contain the current value" — R-crash-2 satisfies "never
    /// blank" instead through <see cref="SnapDistanceText"/> alone: the combobox is
    /// <c>IsEditable="True"</c> and bound via <c>Text</c> (never <c>SelectedItem</c>), so it can
    /// display ANY value, on-ladder or not, while this list stays a pure, static function of
    /// <see cref="Technology"/>/<see cref="DisplayUnit"/> that a selection can never perturb.</summary>
    public ObservableCollection<string> SnapLadderOptions { get; } = [];

    // The rungs themselves (multiples of the technology's default snap, two of them sub-unit) are
    // SnapLadder.Multipliers — shared with the 3D editor's snap control (3D editor bugs round 2).

    /// <summary>Rebuilds <see cref="SnapLadderOptions"/> — called ONLY when the resolved technology
    /// changes (<c>OnTechnologyChanged</c>, which also covers a later retarget — R-cmb-2), the display
    /// unit changes (<c>OnDisplayUnitChanged</c> — R-cmb-3, since the same rungs must simply relabel,
    /// e.g. "1 mil" ↔ "25.4 µm"), and once directly from the constructor (a fresh document's
    /// <see cref="Technology"/> is seeded onto its backing field directly at construction, bypassing
    /// the property setter entirely so construction never dirties the document — which means
    /// <c>OnTechnologyChanged</c> never fires for that INITIAL value, and without an explicit call here
    /// the combobox would sit on an empty list until the workspace's own later
    /// <c>ApplyTechResolution</c> call happens to fire it — the "new workspace, new layout"
    /// binding-order gap brief-snap-combobox-and-consistency.md's own diagnosis named).
    /// <para/>
    /// brief-snap-ladder-crash.md R-crash-1: deliberately NEVER called from <c>OnSnapDbuChanged</c> or
    /// any selection path — see this method's own doc comment on <see cref="SnapLadderOptions"/> for
    /// why that specific wiring is what caused the reported crash.</summary>
    /// <summary>
    /// An explicit ladder base for a document with no resolved technology, or 0 for none.
    ///
    /// <para>Exists so a host can state the process step its ladder should describe INDEPENDENTLY of
    /// what the document's snap happens to be set to — the wBond editor ships a 0.1 mil default snap
    /// off a 1 mil ladder, and without this the fallback would re-derive the whole ladder from that
    /// 0.1 mil and offer a 0.01 mil finest rung.</para>
    ///
    /// <para>It is a base, never a selection, so R-crash-1's invariant is untouched: the list is still
    /// a pure function of things the user's choice of rung cannot move.</para>
    /// </summary>
    internal long SnapLadderBaseDbu { get; set; }

    private void RebuildSnapLadderOptions()
    {
        long baseDbu = Technology is { DefaultSnapDbu: > 0 } tech ? tech.DefaultSnapDbu
                     : SnapLadderBaseDbu > 0 ? SnapLadderBaseDbu
                     : Model.SnapDbu;

        if (baseDbu <= 0) baseDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Um, Model.DbuPerMicron);

        SnapLadderOptions.Clear();
        foreach (var rung in SnapLadder.Build(baseDbu, DisplayUnit, Model.DbuPerMicron))
            SnapLadderOptions.Add(rung);
    }

    /// <summary>
    /// Re-derives the ladder after the caller has changed what it is derived FROM — the resolved
    /// technology, or, when there is none, the document's own snap.
    ///
    /// <para>Exists for the wBond editor, which attaches a scratch reference layout and seeds its snap
    /// afterwards: the constructor's own rebuild ran against a snap of zero and produced a µm-scale
    /// fallback ladder that no longer described anything.</para>
    ///
    /// <para><b>Never call this from a selection path</b> — brief-snap-ladder-crash.md R-crash-1. See
    /// <see cref="SnapLadderOptions"/>'s own remarks for the crash that wiring caused.</para>
    /// </summary>
    internal void RefreshSnapLadder() => RebuildSnapLadderOptions();

    /// <summary>Staged typed-entry text for the snap-distance combo — same commit-on-LostFocus/Enter
    /// idiom as <see cref="CornerRadiusText"/>/<see cref="PathWidthText"/>. Bound to
    /// <see cref="SnapDbu"/>, never to the read-only <see cref="SnapText"/>.</summary>
    [ObservableProperty] private string _snapDistanceText = "";

    private void RefreshSnapDistanceDisplay() => SnapDistanceText = SnapText;

    /// <summary>Parses through <see cref="LayoutUnits.TryParse"/> (accepts a bare number or a
    /// unit-suffixed string, e.g. "2.5mil"/"0.1u" — same validation every other dimension field gets).
    /// Zero is accepted (equivalent to F9's "off" state); invalid text reverts to the canonical
    /// formatted value, never throws.</summary>
    public void CommitSnapDistanceText(string text)
    {
        if (SnapLadder.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu))
            SnapDbu = dbu;
        else
            RefreshSnapDistanceDisplay();
    }

    /// <summary>Selecting a ladder entry commits immediately (mirrors the Layer/Unit combos' own
    /// immediate-commit convention) — never re-seeds <see cref="Technology.DefaultSnapDbu"/>, only
    /// this document's own <see cref="SnapDbu"/>.</summary>
    public void CommitSnapLadderSelection(string? text)
    {
        if (text is null) return;
        CommitSnapDistanceText(text);
    }

    // ── §2.4 — Toggles (R-snp-6/7) ───────────────────────────────────────────────

    /// <summary>Geometry snap on/off — default ON (once the mode is on, the user wants it acting;
    /// R-snp-11 makes Alt the rare "place freely" escape hatch instead). Toggled by F3 or 's', or the
    /// toolbar button.</summary>
    [ObservableProperty] private bool _geometrySnapEnabled = true;

    /// <summary>Include-intersections — default OFF (R-snp-6: intersections are relational and
    /// unindexed, so leaving this off costs nothing when unused, and it is the one feature type dense
    /// enough to be noisy by default).</summary>
    [ObservableProperty] private bool _includeIntersectionsEnabled;

    partial void OnGeometrySnapEnabledChanged(bool value) => RecomputeSnapStateImmediate();
    partial void OnIncludeIntersectionsEnabledChanged(bool value) => RecomputeSnapStateImmediate();

    /// <summary>R-snp-7: toggling either flag recomputes the live marker/target at the LAST KNOWN
    /// cursor position immediately — never waits for the next pointer move, or a mode change would
    /// appear to do nothing until the mouse jiggles. When a Move drag is in progress, this must also
    /// re-run the drag's own delta computation (<see cref="RecomputeMoveDelta"/>) — otherwise only the
    /// rendered marker would update on toggle while the eventually-COMMITTED delta stayed whatever the
    /// last real pointer move left it at, which would make "recomputes immediately" a purely visual
    /// claim rather than a true one.</summary>
    private void RecomputeSnapStateImmediate()
    {
        if (_snapQueryLastComputedPoint is not { } last) return;
        _snapQueryLastComputedPoint = null; // force UpdateSnapMarker past its own sub-pixel-skip guard
        UpdateSnapMarker(last.X, last.Y, _snapLastMods, _snapQueryLastTolDbu, 0);
        if (_selectDragKind == SelectDragKind.Move)
            RecomputeMoveDelta(last.X, last.Y, (_snapLastMods & KeyModifiers.Shift) != 0);
        RebuildOverlay();
    }

    // ── §2.2/§2.6 — Query state, marker, and the click-through/grab-target drag ─────────────────

    /// <summary>The top-priority candidate at the current cursor position, or null. Consumed by
    /// <see cref="RebuildOverlay"/> (the rendered marker) and by the Move-drag target-attraction
    /// branch in <c>HandleSelectMove</c>.</summary>
    private SnapCandidate? _currentSnapCandidate;

    /// <summary>brief-snap-combobox-and-consistency.md R-cmb-4/5: true only when
    /// <see cref="_currentSnapCandidate"/> is a REAL <see cref="LayoutSnapQuery.FindCandidates"/> hit —
    /// never for the synthetic "keep the marker visible" echo <see cref="UpdateSnapMarker"/> falls back
    /// to during a grab-role drag with nothing genuinely nearby. Rendering (<c>RebuildOverlay</c>) reads
    /// <see cref="_currentSnapCandidate"/> alone — either value is fine to DRAW. Position computation
    /// (<c>RecomputeMoveDelta</c>) must gate on this flag too, or the always-visible marker fix
    /// silently defeats grid snap for the rest of any grab-role drag: geometry snap overrides grid snap
    /// only when it genuinely has a real feature to offer, never merely because the mode is enabled.</summary>
    private bool _snapCandidateIsRealTarget;

    /// <summary>
    /// Installs a snap marker computed by a CANVAS OVERLAY (the wBond wire layer, WB23) rather than by
    /// this view model's own query.
    ///
    /// <para><b>Why the marker cannot be left to this view model alone.</b> The overlay is offered every
    /// press and move before the layout editor sees it, and anything it consumes never reaches
    /// <see cref="OnPointerMoved"/> — the only place that refreshes or clears
    /// <see cref="_currentSnapCandidate"/>. So a wire drag left the last HOVER's glyph frozen on the
    /// vertex the wire had just been dragged away from, and drawing a wire showed no glyph at all
    /// despite both feet being snapped every frame (owner, 2026-08-17).</para>
    ///
    /// <para><b>Display only.</b> <see cref="_snapCandidateIsRealTarget"/> stays false, exactly as it
    /// does for the synthetic grab echo: the overlay has already applied its own snap to its own
    /// geometry, and letting this feed <c>RecomputeMoveDelta</c>'s absolute-position branch would move
    /// layout shapes to a point chosen for a wire.</para>
    /// </summary>
    internal void SetOverlaySnapMarker(SnapCandidate? candidate)
    {
        if (Equals(_currentSnapCandidate, candidate)) return;

        _currentSnapCandidate = candidate;
        _snapCandidateIsRealTarget = false;

        // The next hover this view model DOES see must re-query rather than be skipped by R-snp-16's
        // sub-pixel guard, which would otherwise compare against a point this marker never came from.
        _snapQueryLastComputedPoint = null;

        RebuildOverlay();
    }

    /// <summary>R-snp-9: reuses the SAME generic cycling primitive the shape-selection overlap cache
    /// uses (see <see cref="ClickCycleCache{T}"/>'s own header) — not a second mechanism.</summary>
    private readonly ClickCycleCache<SnapCandidate> _snapCycleCache = new();

    /// <summary>True while the current Move drag was initiated by grabbing a snap marker (§2.3's
    /// "grab" role) — gates the TARGET-ATTRACTION branch in <c>RecomputeMoveDelta</c> only. Exclusion
    /// (which geometry can never attract itself) is a SEPARATE, broader concern — see
    /// <see cref="ComputeSnapExclusions"/> — and applies to every drag that moves geometry, not just a
    /// marker-initiated one (brief-geometry-snap-followups.md R-snpf-4).</summary>
    private bool _snapDragActive;
    private bool _snapDragOwnerIsInstance;
    private int _snapDragOwnerIndex;

    /// <summary>Test/diagnostic-only — lets a test distinguish a marker-initiated (grab-role) drag from
    /// an ordinary body-drag, which matters for the "always visible during a grab" fallback below:
    /// only the former persists a marker when the cursor is nowhere near a real feature.</summary>
    internal bool SnapDragActiveForTests => _snapDragActive;

    /// <summary>Test/diagnostic-only: is a REAL snap target currently offered (as opposed to the
    /// synthetic marker-persistence echo, or nothing at all)? This is the flag every drag consults to
    /// decide whether geometry snap overrides grid snap, so a test can assert self-exclusion — "the
    /// dragged thing offered nothing to attract to" — without reaching into the query.</summary>
    internal bool HasSnapTargetForTests => _snapCandidateIsRealTarget && _currentSnapCandidate is not null;

    /// <summary>The ORIGINALLY-grabbed feature's own kind/layer, captured once at grab time — owner
    /// follow-up: "the glyph indicator should always be visible throughout the drag." While
    /// <see cref="_snapDragActive"/> and no OTHER feature is currently in range, <see cref="UpdateSnapMarker"/>
    /// falls back to a synthetic candidate of this same kind/layer, tracking the raw cursor (the grab
    /// point's own live position) — so the marker never disappears mid-drag; it only ever changes kind
    /// when a different real feature is found nearby (e.g. dragging a rect from its centroid shows the
    /// centroid circle throughout, until the cursor nears another shape's corner, at which point the
    /// glyph switches to the corner square).</summary>
    private SnapFeatureKind _snapDragOwnerKind;
    private LayerKey _snapDragOwnerLayer;

    /// <summary>R-snp-16: last point a snap query actually ran for, and at what tolerance — a
    /// sub-device-pixel move (or an unchanged tolerance-less call) is skipped entirely.</summary>
    private (long X, long Y)? _snapQueryLastComputedPoint;
    private long _snapQueryLastTolDbu;
    private KeyModifiers _snapLastMods;

    /// <summary>Test/diagnostic-only (mirrors <see cref="MarqueeRecomputeCount"/>'s established
    /// pattern) — counts how many times <see cref="UpdateSnapMarker"/> actually ran
    /// <see cref="LayoutSnapQuery.FindCandidates"/>, i.e. every time EXCEPT when R-snp-16's
    /// sub-device-pixel guard (or one of the disabled/suppressed/no-tolerance early-outs) skipped it.</summary>
    internal int SnapQueryRunCount { get; private set; }

    /// <summary>brief-geometry-snap-followups.md R-snpf-4/5/6: what geometry the CURRENT gesture is
    /// moving, and therefore must never let attract itself. Computed fresh every call from whichever
    /// drag is actually in progress — never from <see cref="_snapDragActive"/> (that flag gates only
    /// the separate TARGET-ATTRACTION concern in <see cref="RecomputeMoveDelta"/>, and is true only for
    /// a marker-initiated drag). A handle drag always targets exactly the one selected shape being
    /// reshaped; a Move drag excludes every currently-selected shape AND instance, however many there
    /// are and regardless of whether the drag started on a marker or a plain shape body — this is what
    /// makes self-exclusion work for an ordinary body-drag, not only a marker-grabbed one, and for a
    /// multi-shape/instance selection, not only a lone shape. Hover (no drag at all) excludes nothing.</summary>
    private (IReadOnlySet<int>? Shapes, IReadOnlySet<int>? Instances) ComputeSnapExclusions()
    {
        // A PCell grip drag REGENERATES the instance under the cursor on every tick, so its own
        // artwork is the one thing a grip must never attract to: the target would be the edge the
        // grip is currently dragging, and the solve would chase a feature that moves with it.
        if (PCellHandleDragInstanceIndex >= 0)
            return (null, new HashSet<int> { PCellHandleDragInstanceIndex });

        if (_handleDragKind != HandleDragKind.None)
            return (new HashSet<int> { _handleDragShapeIndex }, null);

        if (_selectDragKind == SelectDragKind.Move)
        {
            var shapes = _selectedIndices.Count > 0 ? (IReadOnlySet<int>)new HashSet<int>(_selectedIndices) : null;
            var instances = _selectedInstanceIndices.Count > 0 ? (IReadOnlySet<int>)new HashSet<int>(_selectedInstanceIndices) : null;
            return (shapes, instances);
        }

        return (null, null);
    }

    /// <summary>Recomputes <see cref="_currentSnapCandidate"/> for this pointer-move tick. Called from
    /// <c>HandleSelectMove</c>'s very top — BEFORE the handle/scale-drag dispatch — so a handle-drag's
    /// own <c>BuildHandleDragPreview</c> can consult the already-resolved candidate for THIS tick
    /// (brief-geometry-snap-followups.md R-snpf-1), and so an idle hover with no drag at all gets a
    /// live marker too (R-snpf-7/8/9) — this method no longer requires a drag to be in progress.
    /// <para/>
    /// R-snpf-2: a Scale drag has no single grab point to snap (many points move at once) and a
    /// Bulge/CubicControl/Radius/CornerRadius drag is a curvature or length control, not a shape-
    /// defining vertex a candidate could sensibly relocate — all five stay out of scope, narrowed from
    /// the ORIGINAL blanket "any handle or scale drag" early-out that disabled snap for every handle
    /// drag including Vertex/RectCorner/EdgeMidpoint/RectEdge, which is exactly what R-snpf-1 reports.</summary>
    private void UpdateSnapMarker(long px, long py, KeyModifiers mods, long snapTolDbu, long pixelDbu)
    {
        UpdateSnapMarkerCore(px, py, mods, snapTolDbu, pixelDbu);

        // The X:/Y: readout reports the SNAPPED point when snap is on, so it has to be recomputed
        // after the candidate for this tick is known. The canvas raises CursorWorldChanged (which
        // stores the raw point) BEFORE it hands the move to the view model, so refreshing only there
        // would leave the readout showing the PREVIOUS tick's candidate — visibly wrong on a fast
        // drag. Every early return inside the core counts, hence the wrapper rather than a call at
        // the bottom of it: two of those returns leave the candidate deliberately unchanged, and one
        // clears it, and all three still need the raw position re-rendered.
        RefreshCursorReadout();
    }

    // ── Snap tracking (LayoutSnapTracker) ─────────────────────────────────────────────────────

    /// <summary>The references a held snap point has acquired, and the tracking line the snap in force is on.</summary>
    internal LayoutSnapTracker Tracker { get; } = new();

    /// <summary>The clock the tracker's dwell and forgetting read, milliseconds — replaceable by a test.</summary>
    internal Func<long> TrackingClock { get; set; } = () => Environment.TickCount64;

    /// <summary>Tracking applies while a drawing tool is armed — the 3D view's rule. In Select, hovering about would
    /// acquire references nobody asked for.</summary>
    public bool SnapTrackingActive => ActiveTool != Tool.Select && GeometrySnapEnabled;

    /// <summary>Whether the canvas should keep the tracker's clock ticking (a dwell pending, a reference to forget).</summary>
    public bool SnapTrackingNeedsTick => SnapTrackingActive && Tracker.NeedsTick;

    /// <summary>The canvas's tick: a dwell completed (the cursor still) or a reference was forgotten — the marks and the
    /// snap follow at once.</summary>
    public void TickSnapTracking()
    {
        if (!SnapTrackingActive || !Tracker.Tick(TrackingClock())) return;
        RecomputeSnapStateImmediate();
        RebuildOverlay();
    }

    /// <summary>Every reference dropped — the drawing they were for is finished, or its tool put away.</summary>
    private void ClearSnapReferences()
    {
        if (Tracker.Clear()) RebuildOverlay();
    }

    /// <summary>The geometry snap's answer, tracked: a point starts a dwell; anything else may lock onto a reference's
    /// line, which then IS the snap — a drawing tool lands on it as on any real feature.</summary>
    private void ApplySnapTracking(long px, long py, long snapTolDbu)
    {
        if (!SnapTrackingActive) return;
        long now = TrackingClock();
        var raw = _snapCandidateIsRealTarget ? _currentSnapCandidate : null;
        Tracker.Observe(raw, now);
        Tracker.Tick(now);
        if (Tracker.References.Count == 0) return;
        var tracked = Tracker.Apply(raw, px, py, snapTolDbu, SnapDbu, box =>
        {
            var counters = new SnapQueryCounters();
            return LayoutSnapQuery.EdgesNear(Model, DrawTechnology, InstanceBaseDir, box, null, null, null, 256, ref counters);
        }, now);
        if (tracked is { Kind: SnapFeatureKind.Tracking })
        {
            _currentSnapCandidate = tracked;
            _snapCandidateIsRealTarget = true;
        }
    }

    /// <summary>What the overlay draws for tracking: every reference, and the line(s) from reference to tracked point.</summary>
    private LayoutSnapTrackingOverlay? BuildSnapTrackingOverlay()
    {
        var refs = Tracker.References;
        if (!SnapTrackingActive || refs.Count == 0) return null;
        var lines = new List<(long, long, long, long, bool)>();
        if (_currentSnapCandidate is { Kind: SnapFeatureKind.Tracking } at)
            foreach (var line in new[] { Tracker.LineA, Tracker.LineB })
                if (line is { } l && l.Reference < refs.Count)
                    lines.Add((refs[l.Reference].Point.X, refs[l.Reference].Point.Y, at.X, at.Y, l.Vertical));
        return new LayoutSnapTrackingOverlay([.. refs.Select(r => (r.Point.X, r.Point.Y))], lines);
    }

    private void UpdateSnapMarkerCore(long px, long py, KeyModifiers mods, long snapTolDbu, long pixelDbu)
    {
        var previousMods = _snapLastMods;
        _snapLastMods = mods;

        bool handleKindOutOfScope = _handleDragKind is HandleDragKind.Bulge or HandleDragKind.CubicControl
            or HandleDragKind.Radius or HandleDragKind.CornerRadius;

        // A marquee has nothing to snap TO and nothing that could consume the answer: its rectangle is
        // built from the raw pointer position (see the Marquee branch of HandleSelectMove), and the
        // commit path reads only ComputeMarqueeSelection. Running the query anyway made every pointer
        // move of a rubber-band drag pay for a snap search over whatever the cursor was sweeping
        // across — which, over a dense cell, is the most expensive place it could possibly run.
        //
        // R-dup-4: an ortho drag is NOT in this list. Geometry snap keeps working under Shift — see
        // the echo gate further down for the half of the owner's report that was real, and
        // RecomputeMoveDelta for how an attracted target is applied to a constrained move.
        if (handleKindOutOfScope || _scaleDragKind != ScaleDragKind.None
            || _selectDragKind == SelectDragKind.Marquee)
        {
            _currentSnapCandidate = null;
            _snapCandidateIsRealTarget = false;
            return;
        }

        // R-snp-11 USED to suppress the whole query on Alt. R-dup-2 retired that: Alt now arms a
        // duplicate drag, and suspending the snap that a duplicate is being aimed with would defeat
        // the gesture in exactly the way it defeated a grip drag (R-pch-12). Geometry snap is turned
        // off by its own toggle — S or F3 — which is persistent and visible in the toolbar, and grid
        // snap by F9. Momentary suppression is gone; nothing else here changes.
        if (!GeometrySnapEnabled || snapTolDbu <= 0)
        {
            _currentSnapCandidate = null;
            _snapCandidateIsRealTarget = false;
            _snapQueryLastComputedPoint = null;
            return;
        }

        // R-snp-16: skip the query entirely for a sub-device-pixel cursor move at an unchanged tolerance.
        //
        // R-dup-4: …and at UNCHANGED MODIFIERS. Pressing Shift without moving the mouse changes the
        // answer — it decides whether the grab echo below is offered at all — and this guard would
        // otherwise leave the previous tick's marker frozen on screen until the pointer moved again.
        // That is one of the ways the owner saw a marker with nothing to snap to: it was a stale
        // answer to a question that had since changed.
        long moveGuard = Math.Max(pixelDbu, 1);
        if (_snapQueryLastComputedPoint is { } last && snapTolDbu == _snapQueryLastTolDbu
            && mods == previousMods
            && Math.Abs(last.X - px) < moveGuard && Math.Abs(last.Y - py) < moveGuard)
            return;
        _snapQueryLastComputedPoint = (px, py);
        _snapQueryLastTolDbu = snapTolDbu;
        SnapQueryRunCount++;

        var (excludeShapes, excludeInstances) = ComputeSnapExclusions();

        var counters = new SnapQueryCounters();
        var candidates = LayoutSnapQuery.FindCandidates(
            Model, DrawTechnology, InstanceBaseDir, px, py, snapTolDbu, IncludeIntersectionsEnabled,
            excludeShapes, excludeInstances, ref counters);

        candidates = WithWireCandidates(candidates, px, py, snapTolDbu);

        _snapCandidateIsRealTarget = candidates.Count > 0;
        _currentSnapCandidate = _snapCandidateIsRealTarget
            ? candidates[0]
            // Owner follow-up: a grab-role drag keeps SOME marker showing throughout, even where the
            // cursor has moved away from every real feature — a synthetic echo of the ORIGINALLY-
            // grabbed feature's own kind, drawn where that feature has actually been moved to. Never
            // applies to a plain body-drag (_snapDragActive is only ever true for a marker-initiated
            // grab) and never applies here when snap is disabled/suppressed/out of tolerance — those
            // already returned above with the candidate explicitly nulled.
            // brief-snap-combobox-and-consistency.md R-cmb-4/5: this synthetic echo is DISPLAY ONLY —
            // _snapCandidateIsRealTarget stays false for it, so RecomputeMoveDelta's absolute-position
            // branch never fires off of it and grid snap still applies to the committed delta whenever
            // nothing real is in range.
            //
            // OWNER REPORT ("the glyph follows the mouse and is not in the centre when grid snapping
            // is on"): this used to be drawn at the RAW cursor, while the geometry under it moved by a
            // GRID-SNAPPED delta — so the marker drifted off its own shape by up to half a snap step
            // for the whole drag. Most visible on a via, whose one feature IS its centre, so the
            // offset reads directly as "the glyph is not in the middle of the via". It is drawn at the
            // grabbed feature's own snapped position now, computed by the same helper the commit uses.
            //
            // R-dup-4 (owner report, 2026-08-27: "holding Shift for ortho, the snap cursor is
            // sometimes on even though there's no geometry nearby"). THIS is the marker that was
            // on with nothing nearby — it is synthetic by definition, shown precisely when no real
            // feature is in range, and only ever during a marker-grabbed drag, which is the
            // "sometimes". Under an ortho constraint it is also drawn in the wrong place: it tracks
            // SnappedGrabPoint, computed from the UNCONSTRAINED delta, so it floats off the axis the
            // geometry is actually travelling on. Suppressed for the duration of a constrained move
            // rather than relocated: with the axis locked, "here is where the thing you grabbed has
            // got to" is already told by the geometry itself, and a marker whose whole job is to
            // stand in for an absent target is exactly what the report is about.
            //
            // REAL candidates are untouched by this — snapping to actual geometry within the
            // threshold keeps working under Shift, which is the owner's follow-up correction to a
            // first fix that stood the whole query down.
            : _snapDragActive && !(_selectDragKind == SelectDragKind.Move && (mods & KeyModifiers.Shift) != 0)
                ? MakeGrabEcho(px, py)
                : null;

        ApplySnapTracking(px, py, snapTolDbu);
    }

    /// <summary>
    /// Adds the WIRES riding over this layout to the snap answer — their vertices and their segments
    /// (owner, 2026-08-16: "this wire vertex/segment snapping will also be needed when moving regular
    /// geometry in the layout editor, so make sure it works for both editors").
    ///
    /// <para>Returns <paramref name="candidates"/> unchanged when there is no wire design, which is
    /// every layout that is not open under a wBond editor — so an ordinary layout's snap does exactly
    /// what it did before, with no extra work per pointer move.</para>
    ///
    /// <para><b>Wire points are stored in NANOMETRES and this query is in the layout's own database
    /// units</b> (<see cref="LayoutView.DbuPerMicron"/>). The two coincide only at the 1,000 DBU/µm
    /// default, which is exactly why the bridge is crossed explicitly here — see
    /// <c>WBondSnap</c>'s own note on the same conversion.</para>
    ///
    /// <para>Nothing is excluded: this path moves SHAPES, and a shape is never a wire, so there is no
    /// self-snap to guard against. (The wBond overlay, which moves wires, does exclude what it is
    /// dragging.) The result is re-sorted by the same (priority, distance) rule
    /// <see cref="LayoutSnapQuery.FindCandidates"/> uses, so a wire vertex and a pad corner compete on
    /// equal terms.</para>
    /// </summary>
    private IReadOnlyList<SnapCandidate> WithWireCandidates(
        IReadOnlyList<SnapCandidate> candidates, long px, long py, long snapTolDbu)
    {
        if (WireDesign is not { } design) return candidates;

        int dbuPerMicron = Model.DbuPerMicron;
        long tolNm = CircuitRF.Ui.WBond.WBondSnap.ToNm(snapTolDbu, dbuPerMicron);

        var hit = CircuitRF.WBond.WireSnap.Nearest(
            design,
            CircuitRF.Ui.WBond.WBondSnap.ToNm(px, dbuPerMicron),
            CircuitRF.Ui.WBond.WBondSnap.ToNm(py, dbuPerMicron),
            tolNm);

        if (!hit.Found) return candidates;

        // OwnerIndex −1 = "no owning shape", the same value a PCell pin already carries. Only the
        // marker's position and kind are read from a candidate on this path; the click-through GRAB
        // path runs its own query and never sees this one, so there is no owner to resolve.
        var wireCandidate = new SnapCandidate(
            hit.Kind == CircuitRF.WBond.WireSnapKind.Vertex
                ? SnapFeatureKind.CornerEndpoint
                : SnapFeatureKind.Nearest,
            CircuitRF.Ui.WBond.WBondSnap.ToDbu(hit.XNm, dbuPerMicron),
            CircuitRF.Ui.WBond.WBondSnap.ToDbu(hit.YNm, dbuPerMicron),
            default, false, -1);

        var merged = new List<SnapCandidate>(candidates.Count + 1);
        merged.AddRange(candidates);
        merged.Add(wireCandidate);

        double DistSq(SnapCandidate c)
        {
            double dx = c.X - px, dy = c.Y - py;
            return dx * dx + dy * dy;
        }
        merged.Sort((a, b) =>
        {
            int k = a.Kind.CompareTo(b.Kind);
            return k != 0 ? k : DistSq(a).CompareTo(DistSq(b));
        });

        return merged;
    }

    private SnapCandidate MakeGrabEcho(long px, long py)
    {
        var (gx, gy) = SnappedGrabPoint(px, py);
        return new SnapCandidate(_snapDragOwnerKind, gx, gy, _snapDragOwnerLayer,
                                 _snapDragOwnerIsInstance, _snapDragOwnerIndex);
    }

    /// <summary>
    /// The candidates this press may PICK UP, out of everything it may snap TO — owner-reported bug,
    /// 2026-09-09: "I set only 2 layers Selectable, select all, then click to drag, and some other
    /// layer moves."
    ///
    /// <para><b>The two roles are not the same set, and that is the whole point.</b>
    /// <see cref="LayoutSnapQuery.FindCandidates"/> gates on <c>Visible</c> alone and says so —
    /// "locked IS snappable" — which is right for the TARGET role: aligning new artwork to a locked
    /// board outline or keepout is exactly what locking a layer is for. But
    /// <see cref="TryBeginSnapMarkerDrag"/> reuses that same list for the GRAB role, where it
    /// <see cref="ApplyClickSelection"/>s the candidate's owner and starts moving it — so a marker
    /// belonging to a locked shape handed the user that shape instead of the selection they were
    /// reaching for, silently REPLACING it, and dragged the one thing the technology said could not
    /// be picked up. Marker grab is a click-through: it must obey the same
    /// <c>Visible &amp;&amp; Selectable</c> gate the click it stands in for obeys
    /// (<c>LayoutHitTest.HitStack</c>).</para>
    ///
    /// <para>Filtered BEFORE the cycle cache is built, not after it chooses, so R-snp-9 cycling still
    /// reaches every candidate a press is allowed to grab rather than stalling on a locked one; and
    /// an empty result falls through to ordinary selection exactly as "nothing within tolerance"
    /// already does.</para>
    ///
    /// <para><b>Instance owners pass through untouched.</b> An instance carries no layer of its own —
    /// its artwork is on the layers of the cell it places — and neither
    /// <c>LayoutHitTest.HitInstanceStack</c> nor Select All filters one by what is inside it. Gating
    /// here would invent a rule the click does not have.</para>
    /// </summary>
    private List<SnapCandidate> GrabbableOnly(IReadOnlyList<SnapCandidate> candidates)
    {
        var grabbable = new List<SnapCandidate>(candidates.Count);
        foreach (var c in candidates)
        {
            if (!c.OwnerIsInstance && c.OwnerIndex >= 0 && c.OwnerIndex < Model.Shapes.Count)
            {
                var def = ResolveLayerDef(Model.Shapes[c.OwnerIndex].Layer);
                if (!def.Visible || !def.Selectable) continue;
            }
            grabbable.Add(c);
        }
        return grabbable;
    }

    /// <summary>R-snp-8: the click-through headline behaviour. Consumes the press for the top-priority
    /// snap candidate's owning shape/instance (R-snp-9 cycling on repeated near-identical presses,
    /// reusing <see cref="_snapCycleCache"/>) even when the raw click misses that shape's own
    /// hit-test. Returns false (letting the caller fall through to ordinary selection) when geometry
    /// snap is off or nothing is within tolerance.
    /// <para/>
    /// R-dup-2: no longer refuses on Alt. A marker-grabbed drag is an ordinary Move drag, so Alt on it
    /// now means what it means on every other Move drag — duplicate — and refusing here would have made
    /// the one press most likely to be aimed at a feature the one press that could not be duplicated.</summary>
    private bool TryBeginSnapMarkerDrag(long px, long py, bool shift, bool ctrl, long snapTolDbu)
    {
        if (!GeometrySnapEnabled || snapTolDbu <= 0) return false;

        SnapCandidate chosen;
        if (_snapCycleCache.Matches(px, py, snapTolDbu, bypassDistance: false))
        {
            chosen = _snapCycleCache.Advance(px, py);
        }
        else
        {
            var counters = new SnapQueryCounters();
            var candidates = GrabbableOnly(LayoutSnapQuery.FindCandidates(
                Model, DrawTechnology, InstanceBaseDir, px, py, snapTolDbu, IncludeIntersectionsEnabled, null, null, ref counters));
            if (candidates.Count == 0) { _snapCycleCache.Clear(); return false; }
            chosen = _snapCycleCache.Rebuild(px, py, candidates);
        }

        if (chosen.OwnerIsInstance)
        {
            if (chosen.OwnerIndex < 0 || chosen.OwnerIndex >= Model.Instances.Count) return false;
            ApplyInstanceClickSelection(chosen.OwnerIndex, shift, ctrl);
        }
        else
        {
            if (chosen.OwnerIndex < 0 || chosen.OwnerIndex >= Model.Shapes.Count) return false;
            ApplyClickSelection(chosen.OwnerIndex, shift, ctrl);
        }

        if (!shift && !ctrl)
        {
            _snapDragActive = true;
            _snapDragOwnerIsInstance = chosen.OwnerIsInstance;
            _snapDragOwnerIndex = chosen.OwnerIndex;
            _snapDragOwnerKind = chosen.Kind;
            _snapDragOwnerLayer = chosen.Layer;
            // Grab role (§2.3): anchor the move-drag at the EXACT feature point, not the raw click —
            // every subsequent tick's delta is then measured from this point, so the grabbed feature
            // tracks the cursor exactly until a target attracts it.
            BeginMoveDrag(chosen.X, chosen.Y);
        }
        return true;
    }
}
