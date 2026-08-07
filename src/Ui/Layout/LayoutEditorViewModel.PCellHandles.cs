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
    private const double LivePreviewBudgetMs = 16.0;

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
    private IReadOnlyList<PCellHandleMarker> BuildPCellHandleMarkers()
    {
        var resolved = ResolveSelectedPCellHandles(out var inst, out _, out _, out _);
        if (resolved.Count == 0 || inst is null) return [];

        // While a pinned-anchor drag is live the cell is being drawn shifted, so the grips have to be
        // placed against the shifted instance or they would float off the artwork they belong to.
        if (_pcellHandleDrag is { } pinning) inst = PreviewInstance(pinning);

        var markers = new List<PCellHandleMarker>(resolved.Count);
        for (int i = 0; i < resolved.Count; i++)
        {
            var h = resolved[i];
            bool active = _pcellHandleDrag is { } d && d.HandleIndex == i;
            // While a grip is being dragged it renders where the SOLVER put it, which is where the
            // regenerated cell actually places it (R-pch-3) rather than where the cursor is.
            var use = active && _pcellHandleDrag!.PreviewHandle is { } moved ? moved : h;
            markers.Add(ToMarker(use, inst, active));
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
        public PCellValue? PendingValue;
        public PCellValue? PendingCrossValue;

        /// <summary>R-pch-4b: how far the INSTANCE must move so the grip's anchor keeps the world
        /// position it had. Zero on an ordinary grip, and zero on a pinned one whose anchor happens
        /// not to move.</summary>
        public long PendingDx, PendingDy;
        public PCellHandle? PreviewHandle;
        public LayoutView? PreviewView;
        public string Readout = "";
    }

    /// <summary>
    /// R-pch-8's entry point. Checked BEFORE the instance-body move drag — otherwise grabbing a grip
    /// would move the whole instance instead, which is the one interaction failure a user cannot
    /// work around.
    /// </summary>
    private bool TryBeginPCellHandleDrag(long px, long py, long tolDbu)
    {
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
        RebuildOverlay();
        return true;
    }

    /// <summary>
    /// One drag step. Snaps in WORLD space (the user is aligning to the grid they can see), then
    /// inverse-transforms into the cell's own frame, then projects — never the other way round.
    /// </summary>
    private void UpdatePCellHandleDrag(long px, long py, bool suspendSnap)
    {
        if (_pcellHandleDrag is not { } drag) return;

        var (sx, sy) = LayoutSnapping.SnapPoint(px, py, SnapDbu, suspendSnap);
        var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(sx, sy, drag.Instance, 0, 0);
        double target = drag.Handle.Project(lx, ly);

        PCellResult Generate(IReadOnlyDictionary<string, PCellValue> p)
        {
            PCellHandlePreviewGenerateCount++;
            return drag.Cache.GetOrGenerate(drag.Origin.GeneratorId, drag.Generator, p,
                                            Technology, PCellLayerSelection.Default);
        }

        // Time only a drag that is still deciding. A generator that DECLARED itself deferred is
        // believed without measuring — that is the whole saving.
        var stopwatch = drag.Moved || drag.Deferred ? null : Stopwatch.StartNew();
        var solved = PCellHandleSolver.Solve(Generate, drag.Origin.Parameters, drag.Handle,
                                             drag.HandleIndex, target, drag.ValuePerProjection);
        stopwatch?.Stop();

        drag.Moved = true;
        if (!solved.Ok) return;   // the design is unchanged; the grip simply does not follow

        drag.PendingValue = solved.Value;
        drag.Readout = $"{drag.Handle.DisplayLabel} = {PCellHandleSolver.FormatForReadout(solved.Value)}";

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
                                                      matchParameter: drag.Handle.Parameter);
            if (crossSolved.Ok)
            {
                crossAchieved = crossSolved.Achieved;
                drag.PendingCrossValue = crossSolved.Value;
                drag.Readout += $"   {cross.DisplayLabel} = {PCellHandleSolver.FormatForReadout(crossSolved.Value)}";
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
            drag.PreviewView = null;
            ApplyAnchorPin(drag);
            RebuildOverlay();
            return;
        }

        var merged = MergedValues(drag, solved.Value, drag.PendingCrossValue);
        PCellResult preview;
        try { preview = Generate(merged); }
        catch (Exception) { return; }

        // Where the generator ACTUALLY put the grip — the grip is drawn there, not under the cursor.
        drag.PreviewHandle = FindHandle(preview.Handles, drag.Handle, drag.HandleIndex);
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

    private void CommitPCellHandleDrag()
    {
        if (_pcellHandleDrag is not { } drag) { ResetPCellHandleDragState(); return; }

        // A press with no movement is a selection click on a grip, not an edit — nothing to commit
        // and nothing to undo.
        if (drag.Moved && drag.PendingValue is { } value)
        {
            // Both axes of a two-axis grip commit TOGETHER, in one edit and therefore one undo
            // entry. Committing them separately would make a single drag two undo steps, which is
            // not what the user did.
            var edit = new Dictionary<string, PCellValue>(StringComparer.Ordinal)
            {
                [drag.Handle.Parameter] = value,
            };
            if (drag.CrossHandle is { } cross && drag.PendingCrossValue is { } crossValue)
                edit[cross.Parameter] = crossValue;

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
