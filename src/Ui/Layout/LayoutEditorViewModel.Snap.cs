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

    private static readonly long[] SnapLadderMultipliers = [1, 5, 10, 25, 50];

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
    private void RebuildSnapLadderOptions()
    {
        long baseDbu = Technology is { DefaultSnapDbu: > 0 } tech ? tech.DefaultSnapDbu : Model.SnapDbu;
        if (baseDbu <= 0) baseDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Um, Model.DbuPerMicron);

        SnapLadderOptions.Clear();
        foreach (var mult in SnapLadderMultipliers)
        {
            long dbu = baseDbu * mult;
            SnapLadderOptions.Add($"{LayoutUnits.Format(dbu, DisplayUnit, Model.DbuPerMicron)} {UnitSuffix(DisplayUnit)}");
        }
    }

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
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu) && dbu >= 0)
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
            RecomputeMoveDelta(last.X, last.Y, (_snapLastMods & KeyModifiers.Alt) != 0);
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

    private void UpdateSnapMarkerCore(long px, long py, KeyModifiers mods, long snapTolDbu, long pixelDbu)
    {
        _snapLastMods = mods;

        bool handleKindOutOfScope = _handleDragKind is HandleDragKind.Bulge or HandleDragKind.CubicControl
            or HandleDragKind.Radius or HandleDragKind.CornerRadius;
        if (handleKindOutOfScope || _scaleDragKind != ScaleDragKind.None)
        {
            _currentSnapCandidate = null;
            _snapCandidateIsRealTarget = false;
            return;
        }

        bool suppressed = (mods & KeyModifiers.Alt) != 0; // R-snp-11: Alt suppresses, never enables
        if (!GeometrySnapEnabled || suppressed || snapTolDbu <= 0)
        {
            _currentSnapCandidate = null;
            _snapCandidateIsRealTarget = false;
            _snapQueryLastComputedPoint = null;
            return;
        }

        // R-snp-16: skip the query entirely for a sub-device-pixel cursor move at an unchanged tolerance.
        long moveGuard = Math.Max(pixelDbu, 1);
        if (_snapQueryLastComputedPoint is { } last && snapTolDbu == _snapQueryLastTolDbu
            && Math.Abs(last.X - px) < moveGuard && Math.Abs(last.Y - py) < moveGuard)
            return;
        _snapQueryLastComputedPoint = (px, py);
        _snapQueryLastTolDbu = snapTolDbu;
        SnapQueryRunCount++;

        var (excludeShapes, excludeInstances) = ComputeSnapExclusions();

        var counters = new SnapQueryCounters();
        var candidates = LayoutSnapQuery.FindCandidates(
            Model, Technology, InstanceBaseDir, px, py, snapTolDbu, IncludeIntersectionsEnabled,
            excludeShapes, excludeInstances, ref counters);

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
            : _snapDragActive
                ? MakeGrabEcho(px, py)
                : null;
    }

    private SnapCandidate MakeGrabEcho(long px, long py)
    {
        var (gx, gy) = SnappedGrabPoint(px, py);
        return new SnapCandidate(_snapDragOwnerKind, gx, gy, _snapDragOwnerLayer,
                                 _snapDragOwnerIsInstance, _snapDragOwnerIndex);
    }

    /// <summary>R-snp-8: the click-through headline behaviour. Consumes the press for the top-priority
    /// snap candidate's owning shape/instance (R-snp-9 cycling on repeated near-identical presses,
    /// reusing <see cref="_snapCycleCache"/>) even when the raw click misses that shape's own
    /// hit-test. Returns false (letting the caller fall through to ordinary selection) when geometry
    /// snap is off, Alt-suppressed, or nothing is within tolerance.</summary>
    private bool TryBeginSnapMarkerDrag(long px, long py, bool shift, bool ctrl, bool alt, long snapTolDbu)
    {
        if (!GeometrySnapEnabled || alt || snapTolDbu <= 0) return false;

        SnapCandidate chosen;
        if (_snapCycleCache.Matches(px, py, snapTolDbu, bypassDistance: false))
        {
            chosen = _snapCycleCache.Advance(px, py);
        }
        else
        {
            var counters = new SnapQueryCounters();
            var candidates = LayoutSnapQuery.FindCandidates(
                Model, Technology, InstanceBaseDir, px, py, snapTolDbu, IncludeIntersectionsEnabled, null, null, ref counters);
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
