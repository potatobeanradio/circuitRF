using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// The EM SOLVE REGION as the layout canvas sees it: a one-shot drag that hands a rectangle back to
/// the <c>.cem</c> editor that asked for it, and the region of the setup last refreshed against this
/// layout, drawn as an outline. The region itself lives in the <c>.cem</c> (<see cref="EmSolveRegion"/>);
/// nothing here writes to the layout, so drawing one never dirties the <c>.clay</c>.
///
/// <para>The gesture follows RP-2b's return-conductor pick exactly: armed from outside the canvas, it
/// owns the next press whatever tool is active, and Escape puts it back. A drag rather than two
/// clicks, because a box is what the Select tool's marquee already taught every user to drag — and
/// the rubber band IS that marquee, drawn through the same overlay slot.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>Where a completed drag goes — the arming <c>.cem</c> editor's setter. Null when no
    /// pick is armed.</summary>
    private Action<long, long, long, long>? _emRegionPick;

    /// <summary>The drag's anchor, DBU, once the press has landed; null while armed but not pressed.</summary>
    private (long X, long Y)? _emRegionPress;

    private long _emRegionCurX, _emRegionCurY;

    /// <summary>True while a solve-region drag is armed or in progress.</summary>
    public bool IsPickingEmRegion => _emRegionPick is not null;

    /// <summary>The solve region of the EM setup last refreshed against this layout, in DBU, or null.
    /// Pushed by the workspace alongside the mesh; drawn as an outline over the artwork. Unlike the mesh
    /// it is NOT cleared by a layout edit: it is a statement in the <c>.cem</c>, not something derived
    /// from the geometry.</summary>
    [ObservableProperty] private LayoutMarquee? _emSolveRegionOutline;

    /// <summary>
    /// Arms the drag: the next press-drag-release on the canvas becomes a solve region, handed to
    /// <paramref name="onPicked"/> as two DBU corners.
    /// </summary>
    /// <param name="setupName">The <c>.cem</c>'s file name, so the prompt says which setup it is for.</param>
    public void ArmEmRegionPick(string setupName, Action<long, long, long, long> onPicked)
    {
        ArgumentNullException.ThrowIfNull(onPicked);
        _emRegionPick  = onPicked;
        _emRegionPress = null;
        OnPropertyChanged(nameof(IsPickingEmRegion));
        ReportMessage(
            $"Drag a box around the part of the layout '{setupName}' should solve. Only the geometry " +
            "inside it is meshed; anything crossing its edge is cut there. Escape cancels.");
    }

    /// <summary>Escape, or a completed drag. Leaves every document untouched.</summary>
    public void CancelEmRegionPick()
    {
        if (_emRegionPick is null) return;
        _emRegionPick  = null;
        _emRegionPress = null;
        OnPropertyChanged(nameof(IsPickingEmRegion));
        RebuildOverlay();
    }

    private void EmRegionPress(double wx, double wy)
    {
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
        _emRegionPress = (sx, sy);
        _emRegionCurX = sx; _emRegionCurY = sy;
        RebuildOverlay();
    }

    private void EmRegionMove(double wx, double wy, bool leftDown)
    {
        if (_emRegionPress is null || !leftDown) return;
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
        _emRegionCurX = sx; _emRegionCurY = sy;
        RebuildOverlay();
    }

    private void EmRegionRelease(double wx, double wy)
    {
        if (_emRegionPick is not { } onPicked || _emRegionPress is not { } p) return;
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);

        // A click without a drag encloses nothing — stay armed and say so, rather than handing the
        // setup a region that would clip away the whole layout.
        if (sx == p.X || sy == p.Y)
        {
            _emRegionPress = null;
            RebuildOverlay();
            ReportWarning("Solve region: drag a box — a click encloses no area. Escape cancels.");
            return;
        }

        CancelEmRegionPick();
        onPicked(p.X, p.Y, sx, sy);
    }

    /// <summary>The rubber band while the drag is live, for <see cref="RebuildOverlay"/>'s marquee slot.</summary>
    private LayoutMarquee? EmRegionRubberBand =>
        _emRegionPick is not null && _emRegionPress is { } p
            ? new LayoutMarquee(p.X, p.Y, _emRegionCurX, _emRegionCurY)
            : null;
}
