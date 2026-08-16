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

    /// <summary>The <c>w</c> key: promote a click to the whole wire.</summary>
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
    private readonly QualityLadder _ladder = new();
    private readonly Stopwatch _frameTimer = new();

    private readonly Dictionary<int, Point3[]> _collapsed = [];
    private bool _dragging;
    private long _pressX, _pressY;

    public WBondPointerController(WBondViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>The rung the current drag frame is running at.</summary>
    public DragQuality Quality => _ladder.Current;

    /// <summary>True when the readout on screen is approximate and must be labelled so (WB15).</summary>
    public bool ReadoutIsProvisional => _ladder.IsProvisional;

    public bool IsDragging => _dragging;

    /// <summary>
    /// A click, with its promotion decided by the modifiers or the click count.
    /// </summary>
    /// <param name="clickCount">1 = element, 2 = whole wire, 3 = whole array (§6.3).</param>
    public void Press(long worldX, long worldY, double toleranceNm, WBondModifiers modifiers,
                      int clickCount = 1, EditorView view = EditorView.Layout)
    {
        _pressX = worldX;
        _pressY = worldY;

        var hit = view == EditorView.Layout
            ? WireHitTest.HitTestLayout(_vm.Mesh, worldX, worldY, toleranceNm)
            : WireHitTest.HitTestProfile(_vm.Mesh, worldX, worldY, toleranceNm);

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
    /// Begins a drag. Collapses the moving wires to their chords if the ladder is already degraded,
    /// and resets the ladder so every drag starts optimistic.
    /// </summary>
    public void BeginDrag()
    {
        _dragging = true;
        _ladder.BeginDrag();
        _collapsed.Clear();
    }

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

        var moving = _vm.Selection.TouchedWires().ToList();
        if (moving.Count == 0) return _ladder.Current;

        ApplyQualityToGeometry(moving);

        _frameTimer.Restart();
        apply(moving);

        if (_ladder.Current != DragQuality.FreezeAndSnap)
            _vm.CommitPointMove(moving, motion);

        _frameTimer.Stop();

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

        RestoreCollapsed();

        var moving = _vm.Selection.TouchedWires().ToList();
        if (moving.Count > 0) _vm.CommitPointMove(moving);

        _ladder.BeginDrag();
    }

    private void ApplyQualityToGeometry(IReadOnlyList<int> moving)
    {
        if (_ladder.Current == DragQuality.Chord)
        {
            var wires = _vm.Design.AllWires().ToList();
            foreach (int index in moving)
            {
                if (_collapsed.ContainsKey(index)) continue;
                if (index < 0 || index >= wires.Count) continue;

                _collapsed[index] = QualityLadder.CollapseToChord(wires[index]);
            }

            // A chord has a different POINT COUNT, so the flat filament layout no longer matches —
            // this is the one place the degraded rung has to pay for a rebuild. It pays once, at the
            // moment the ladder steps down, not per frame.
            if (_collapsed.Count > 0) _vm.CommitStructuralChange();
        }
        else if (_ladder.Current == DragQuality.Exact && _collapsed.Count > 0)
        {
            RestoreCollapsed();
        }
    }

    private void RestoreCollapsed()
    {
        if (_collapsed.Count == 0) return;

        var wires = _vm.Design.AllWires().ToList();
        foreach (var (index, original) in _collapsed)
        {
            if (index < 0 || index >= wires.Count) continue;
            QualityLadder.RestoreFromChord(wires[index], original);
        }

        _collapsed.Clear();
        _vm.CommitStructuralChange();
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
