using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>The modifier keys a wBond gesture cares about (wbond.md §6.3, §6.2.1).</summary>
[Flags]
public enum WBondModifiers
{
    None = 0,

    /// <summary>Adds to the selection instead of replacing it.</summary>
    Shift = 1,

    /// <summary>Alt — proportional reshaping rather than moving one vertex (§6.2.1).</summary>
    Alt = 2,

    /// <summary>
    /// Promote a click to the whole wire.
    ///
    /// <para>No longer bound to a key: <c>w</c> is the Draw Wire tool (owner, 2026-08-16) and the
    /// promotion is a double-click. The flag stays because <see cref="WBondPointerController.ScopeFor"/>
    /// is the one place a click count and a modifier are reconciled, and both still arrive here.</para>
    /// </summary>
    WholeWire = 4,

    /// <summary>The <c>g</c> key: promote a click to the whole array group.</summary>
    WholeGroup = 8,
}

/// <summary>
/// Routes pointer and keyboard gestures onto the framework-free editing core.
///
/// <para><b>This class contains no geometry and no rules</b> — it decides <i>which</i> operation a
/// gesture means and hands off. Every rule that can be wrong lives in <c>SelectionResolver</c>,
/// <c>WireEdits</c> and <c>WireHitTest</c>, where it is tested against arithmetic rather than through
/// a canvas (brief-wbond-wbc §0.2). Keeping this layer thin is what makes that split worth
/// having.</para>
///
/// <para>It also owns the drag's <see cref="QualityLadder"/>, because the ladder is fed by measured
/// frame times and this is where a frame begins and ends.</para>
/// </summary>
public sealed class WBondPointerController
{
    private readonly WBondViewModel _vm;
    private readonly QualityLadder _ladder;
    private readonly Stopwatch _frameTimer = new();

    private bool _dragging;
    private long _pressX, _pressY;

    /// <param name="frameBudgetMs">
    /// The per-frame budget the <see cref="QualityLadder"/> degrades against — 60 fps by default,
    /// which is what the editor wants and what every real caller uses.
    ///
    /// <para><b>It is settable so a TEST can make the ladder inert</b>, and that is not a nicety.
    /// The ladder is fed measured wall-clock, so anything downstream of it is wall-clock-sensitive —
    /// including tests that look like pure counter assertions. <c>ADragFrame_UsesTheIncrementalPath</c>
    /// asserts only <c>RebuildCount</c> and <c>IncrementalUpdateCount</c>, yet it fails under a
    /// full-solution run: with 7,000 other tests on the cores a frame overruns 16.7 ms, the ladder
    /// drops to <see cref="DragQuality.FreezeAndSnap"/>, <see cref="DragFrame"/> stops calling
    /// <c>CommitPointMove</c> at all, and the incremental count stops rising. Handing such a test an
    /// unreachable budget lets it assert the thing it means — that a point move takes the incremental
    /// path — independently of how busy the machine is.</para>
    /// </param>
    public WBondPointerController(WBondViewModel viewModel,
                                  double frameBudgetMs = QualityLadder.FrameBudgetMs)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _ladder = new QualityLadder(frameBudgetMs);
    }

    /// <summary>The rung the current drag frame is running at.</summary>
    public DragQuality Quality => _ladder.Current;

    /// <summary>True when the readout on screen is approximate and must be labelled so (WB15).</summary>
    public bool ReadoutIsProvisional => _ladder.IsProvisional;

    public bool IsDragging => _dragging;

    /// <summary>
    /// A click, with its promotion decided by the modifiers or the click count.
    ///
    /// <para><b>Hit-tests in the LAYOUT plane only.</b> The profile view must use
    /// <see cref="Press(WireHitTest.Hit, long, long, WBondModifiers, int)"/> and pass the hit it
    /// already resolved — see that overload for why.</para>
    /// </summary>
    /// <param name="clickCount">1 = element, 2 = whole wire, 3 = whole array (§6.3).</param>
    public void Press(long worldX, long worldY, double toleranceNm, WBondModifiers modifiers,
                      int clickCount = 1, EditorView view = EditorView.Layout)
    {
        var hit = view == EditorView.Layout
            ? WireHitTest.HitTestLayout(_vm.Mesh, worldX, worldY, toleranceNm)
            : WireHitTest.HitTestProfile(_vm.Mesh, worldX, worldY, toleranceNm);

        Press(hit, worldX, worldY, modifiers, clickCount);
    }

    /// <summary>
    /// The same click, on a hit the CALLER already resolved.
    ///
    /// <h3>Why the profile view has to use this one (owner, 2026-08-18)</h3>
    /// <para><i>"In Wire Profile view, I cannot click and drag wire points or segments. I must first
    /// drag-select with marquee tool, then it allows me to drag."</i></para>
    ///
    /// <para><b>A profile hit depends on which PLANE the view is projecting onto, and this class does
    /// not know it.</b> The overload above called <c>HitTestProfile</c> with its own defaults — span
    /// mode Absolute and <c>azimuthRadians: null</c>, which means AUTO, each wire projected onto its
    /// own chord. The canvas draws in whatever plane its toolbar says, and the shipped default has
    /// been YZ since 2026-08-16. So the canvas found a wire under the pointer and this found nothing,
    /// cleared the selection, and the canvas then declined to arm a drag on an empty selection.</para>
    ///
    /// <para>Passing the hit rather than a second set of projection parameters is deliberate: the
    /// canvas has already resolved it one line earlier, and <b>two hit tests that must agree are two
    /// that can disagree</b>. With a selection present the bug hid completely, because a press on an
    /// already-selected element skips this call entirely — which is exactly why marquee-selecting
    /// first made dragging work.</para>
    /// </summary>
    public void Press(WireHitTest.Hit hit, long worldX, long worldY, WBondModifiers modifiers,
                      int clickCount = 1)
    {
        _pressX = worldX;
        _pressY = worldY;

        if (!hit.Found)
        {
            if (!modifiers.HasFlag(WBondModifiers.Shift)) _vm.Selection = new WireSelection();
            return;
        }

        var scope = ScopeFor(modifiers, clickCount);
        var resolved = SelectionResolver.Resolve(_vm.Mesh, hit.Wire, hit.Point, hit.IsSegment, scope);

        _vm.Selection = modifiers.HasFlag(WBondModifiers.Shift)
            ? SelectionResolver.Union(_vm.Selection, resolved)
            : resolved;
    }

    /// <summary>
    /// The promotion rule (§6.3). Modifiers win over click count, so a user holding <c>g</c> gets the
    /// group on the first click rather than having to triple-click as well.
    /// </summary>
    internal static SelectionScope ScopeFor(WBondModifiers modifiers, int clickCount)
    {
        if (modifiers.HasFlag(WBondModifiers.WholeGroup)) return SelectionScope.Array;
        if (modifiers.HasFlag(WBondModifiers.WholeWire)) return SelectionScope.Wire;

        return clickCount switch
        {
            >= 3 => SelectionScope.Array,
            2 => SelectionScope.Wire,
            _ => SelectionScope.Element,
        };
    }

    /// <summary>
    /// Begins a drag, and decides up front whether its fill is even worth attempting.
    ///
    /// <para><b>The size of the job is handed to the ladder here</b> (owner, 2026-08-18). Feedback
    /// alone has to PAY one catastrophic frame to discover that 500 moving wires cost seconds; the
    /// block count says so for free. See <see cref="QualityLadder.BeginDrag(int,int)"/> for why that
    /// is a bound rather than the cost model WB15 rejected.</para>
    /// </summary>
    /// <param name="subject">
    /// What this drag actually moves, when that is not simply the selection — the PROFILE view's plain
    /// drag and nudge move the whole group while leaving the selection alone
    /// (<c>WBondViewModel.ProfileGroupSubject</c>). It has to be known HERE and not only inside the
    /// per-frame lambda, because the ladder sizes the job from these wires, the incremental fill is
    /// told about these wires, and <see cref="EndDrag"/> recomputes the exact answer for these wires —
    /// a subject wider than the selection would otherwise move on screen and leave the inductance
    /// stale for every sibling.
    /// </param>
    public void BeginDrag(WireSelection? subject = null)
    {
        _dragging = true;
        _dragSubject = subject;
        _ladder.BeginDrag(MovingWires().Count, _vm.Mesh.WireCount);
        _vm.DeferFills = _ladder.Current != DragQuality.Exact;
    }

    /// <summary>What the open drag moves; null means "the selection", which is the ordinary case.</summary>
    private WireSelection? _dragSubject;

    /// <summary>The wires the open drag moves — its subject if it declared one, else the selection.</summary>
    private List<int> MovingWires() => [.. (_dragSubject ?? _vm.Selection).TouchedWires()];

    /// <summary>
    /// One drag frame: applies the move, times it, and feeds the ladder.
    /// </summary>
    /// <param name="apply">
    /// Mutates the geometry. Given the wire indices that are moving so a caller need not recompute
    /// them.
    /// </param>
    /// <returns>The rung the NEXT frame should run at.</returns>
    public DragQuality DragFrame(Action<IReadOnlyList<int>> apply, SelectionMotion motion = SelectionMotion.General)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (!_dragging) throw new InvalidOperationException("DragFrame called outside a drag; call BeginDrag first.");

        var moving = MovingWires();
        if (moving.Count == 0) return _ladder.Current;

        // THE PRIORITY, in two lines (owner, 2026-08-18: "dragging 500 wires must always be fast, it
        // should always take priority"). The geometry always moves and the canvas always redraws; the
        // FILL is the only thing that can be skipped, and at the frozen rung it is.
        bool fill = _ladder.Current == DragQuality.Exact;

        // Capacitance is strictly OPTIONAL work and is spent only out of measured leftover budget —
        // the last frame having used less than half of it. So it can never be what makes a drag slow:
        // on a 500-wire drag the ladder never reaches Exact and this is never true. See
        // WBondViewModel.RefreshCapacitanceDuringGesture for why it is not simply switched off.
        _vm.RefreshCapacitanceDuringGesture = fill && _ladder.HasHeadroom;
        _vm.DeferFills = !fill;

        _frameTimer.Restart();
        apply(moving);

        if (fill) _vm.CommitPointMove(moving, motion);

        _frameTimer.Stop();

        // Everything optional happened INSIDE the timed region, so its cost is part of what the ladder
        // just observed and the next frame's verdict already accounts for it.
        return _ladder.Observe(_frameTimer.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Ends the drag: restores exact geometry from any collapsed wires and recomputes the final,
    /// non-provisional answer.
    ///
    /// <para>This is the "snap" half of freeze-and-snap, and it must run even if the drag never
    /// degraded — a caller should not have to know which rungs were used.</para>
    /// </summary>
    public void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;

        // The final answer is exact and is allowed to cost whatever it costs — this is the one frame
        // nobody is waiting on, and on a big drag it is the only fill the whole gesture pays for.
        _vm.DeferFills = false;
        _vm.RefreshCapacitanceDuringGesture = true;

        var moving = MovingWires();
        if (moving.Count > 0) _vm.CommitPointMove(moving);

        _dragSubject = null;
        _ladder.BeginDrag();
    }

    /// <summary>
    /// An arrow-key nudge (§6.3 / WB25). Up is +z in the profile view and +y in the layout view.
    /// </summary>
    public void Nudge(int dx, int dyOrDz, bool shift, EditorView view) =>
        _vm.NudgeSelection(dx, dyOrDz, shift, view);

    /// <summary>
    /// A marquee, with its semantics decided by the drag direction (§6.3).
    ///
    /// <para>Right → left is <b>crossing</b> and catches whole wires; left → right is
    /// <b>enclose</b>. The direction is taken from the press and release x, which is what a user's
    /// hand actually did rather than a mode they had to select.</para>
    /// </summary>
    public void Marquee(long releaseX, long releaseY, WBondModifiers modifiers,
                        EditorView view = EditorView.Layout) =>
        _vm.Selection = ResolveMarquee(releaseX, releaseY, modifiers, _vm.Selection, view);

    /// <summary>
    /// What a marquee ending at the given point WOULD select, without committing it.
    ///
    /// <para>The live preview and the release both come through here, so the highlight a user watches
    /// while dragging cannot disagree with what they get when they let go — including the
    /// crossing-versus-enclose flip when the box is dragged back past its own start.</para>
    /// </summary>
    /// <param name="baseSelection">
    /// What a Shift-marquee adds to. For a live preview this must be the selection as it stood when
    /// the box began, not the previewed one, or the box would accumulate its own previews.
    /// </param>
    public WireSelection ResolveMarquee(long currentX, long currentY, WBondModifiers modifiers,
                                        WireSelection baseSelection,
                                        EditorView view = EditorView.Layout)
    {
        ArgumentNullException.ThrowIfNull(baseSelection);

        var direction = currentX < _pressX ? MarqueeDirection.RightToLeft : MarqueeDirection.LeftToRight;

        var resolved = SelectionResolver.ResolveMarquee(
            _vm.Mesh, _pressX, _pressY, currentX, currentY, direction, view);

        return modifiers.HasFlag(WBondModifiers.Shift)
            ? SelectionResolver.Union(baseSelection, resolved)
            : resolved;
    }
}
