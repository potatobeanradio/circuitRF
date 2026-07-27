using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L1h — Scale (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md §2). One operation, two
/// ways to drive it (a numeric dialog and bbox-handle mouse drags), sharing the same semantics via
/// <see cref="BuildScaledShapes"/>/<see cref="WouldCollapse"/> so the two surfaces can never disagree:
/// round to the nearest DBU (never snap to the grid — R-L1h trap 2), never scale arc bulge (dimensionless),
/// and promote Arc edges to Cubic first under a non-uniform scale (R-L1h-7 — cubics are closed under
/// affine transforms, circular arcs are not). Geometry lives in <see cref="LayoutCoordinateWalk"/>/
/// <see cref="LayoutArcPromotion"/> (pure, framework-free); this file is selection/drag-state plumbing
/// + <c>Commands.Layout.ReplaceShapesCommand</c> wiring + Messages, mirroring the rest of this VM.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    // ── Enablement ─────────────────────────────────────────────────────────────

    public LayoutCommandAvailability ScaleAvailability => ValidSelectedIndices.Count >= 1
        ? LayoutCommandAvailability.Enabled
        : LayoutCommandAvailability.Disabled(SelectAtLeastOneReason);

    public bool CanScaleSelection => ScaleAvailability.CanExecute;

    // ── Scale mode (R-L1h-5 row 3: single-shape L1d handles <-> bbox scale handles) ─────────────

    [ObservableProperty] private bool _scaleModeActive;

    public IRelayCommand ToggleScaleModeCommand { get; private set; } = null!;

    private void InitScaleCommands() => ToggleScaleModeCommand = new RelayCommand(() => ScaleModeActive = !ScaleModeActive);

    partial void OnScaleModeActiveChanged(bool value) => RebuildOverlay();

    /// <summary>R-L1h-5's table, extended by the layout-bitmaps brief: bbox scale handles show for a
    /// 2+ selection always, for a single selection while Scale mode is toggled on (temporarily
    /// replacing L1d's handles), OR for a single selected <see cref="BitmapShape"/> ALWAYS — a bitmap
    /// has no vertex list (<c>LayoutHandles.Build</c> already returns none for it), so there is no L1d
    /// mode for Scale mode to "temporarily replace"; bbox scale handles are simply its only handles,
    /// no toggle needed.</summary>
    public bool ShowScaleHandles => _selectedIndices.Count >= 2
        || (_selectedIndices.Count == 1 && ScaleModeActive)
        || (_selectedIndices.Count == 1 && IsSoleSelectionBitmap());

    private bool IsSoleSelectionBitmap()
    {
        int idx = _selectedIndices[0];
        return idx >= 0 && idx < Model.Shapes.Count && Model.Shapes[idx] is BitmapShape;
    }

    // ── Numeric path (the "Scale…" dialog) ────────────────────────────────────

    /// <summary>Scales every selected shape by <paramref name="factorX"/>/<paramref name="factorY"/>
    /// about <paramref name="anchorX"/>/<paramref name="anchorY"/>, as ONE undo entry. Equal factors
    /// are a uniform scale (arcs/circles stay arcs/circles); unequal factors trigger R-L1h-7's
    /// Arc→Cubic promotion first.</summary>
    public void ApplyScale(double factorX, double factorY, long anchorX, long anchorY)
    {
        var indices = MovableSelectedIndices(ValidSelectedIndices); // R-bmp: Locked blocks scale
        if (indices.Count == 0) return;

        if (factorX <= 0 || factorY <= 0)
        {
            _messageSink?.Error("Scale factor must be positive.");
            return;
        }

        var originals = indices.Select(i => Model.Shapes[i]).ToList();
        var (scaled, anyPromoted) = BuildScaledShapes(originals, factorX, factorY, anchorX, anchorY);

        if (scaled.Any(WouldCollapse))
        {
            _messageSink?.Error("Scale would collapse a shape below one DBU — cancelled.");
            return;
        }

        var removed = indices.Select(i => (i, Model.Shapes[i])).ToList();
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, scaled, "Scale"));
        SetSelection(Enumerable.Range(indices.Min(), scaled.Count));

        if (anyPromoted)
            _messageSink?.Warning("Scale: circular arc(s) were converted to cubic curves to scale non-uniformly.");
    }

    /// <summary>The current selection's bounding box, in world DBU — the numeric dialog's live-preview
    /// basis and the mouse handles' anchor geometry both read this.</summary>
    public Bbox SelectionBbox(IReadOnlyList<int>? indices = null)
    {
        var bb = Bbox.Empty;
        foreach (var i in indices ?? ValidSelectedIndices)
            if (i >= 0 && i < Model.Shapes.Count)
                bb = bb.Union(LayoutGeometry.BboxOf(Model.Shapes[i]));
        return bb;
    }

    // ── Shared scale semantics (numeric dialog AND mouse drag both call this) ────────────────────

    /// <summary>The one place that decides what a scale factor DOES to a shape (R-L1h-6/7): a uniform
    /// factor is the degenerate case where X/Y/Magnitude all coincide; a non-uniform one promotes any
    /// Arc edge (and a Circle's implicit one) to cubics FIRST, then transforms X/Y independently and
    /// Magnitude fields (radius, corner radius, width, pad/drill, label height, flatten tolerance) by
    /// the isotropic-equivalent <c>sqrt(fx*fy)</c> — see <see cref="LayoutCoordinateTransform"/>'s doc
    /// comment for why there is no exact answer for those fields under a non-uniform transform.
    /// Rounds to the nearest DBU (never snaps to the grid — trap 2); never touches bulge.</summary>
    private static (List<LayoutShape> Scaled, bool AnyPromoted) BuildScaledShapes(
        IReadOnlyList<LayoutShape> shapes, double factorX, double factorY, long anchorX, long anchorY)
    {
        bool uniform = Math.Abs(factorX - factorY) < 1e-9;
        double magnitudeFactor = uniform ? factorX : Math.Sqrt(Math.Abs(factorX) * Math.Abs(factorY));

        var transform = new LayoutCoordinateTransform(
            x => anchorX + (long)Math.Round((x - anchorX) * factorX, MidpointRounding.AwayFromZero),
            y => anchorY + (long)Math.Round((y - anchorY) * factorY, MidpointRounding.AwayFromZero),
            m => (long)Math.Round(m * magnitudeFactor, MidpointRounding.AwayFromZero));

        var result = new List<LayoutShape>(shapes.Count);
        bool anyPromoted = false;

        foreach (var shape in shapes)
        {
            var working = shape;
            if (!uniform)
            {
                var promoted = LayoutArcPromotion.PromoteArcsToCubics(shape);
                if (!ReferenceEquals(promoted, shape)) anyPromoted = true;
                working = promoted;
            }

            var clone = LayoutGeometry.Clone(working);
            LayoutCoordinateWalk.Transform(clone, transform);
            result.Add(clone);
        }

        return (result, anyPromoted);
    }

    /// <summary>Guard: a scale that would shrink a shape below one DBU in any meaningful dimension is
    /// rejected wholesale (§2.2 "Guards"), never silently produced.</summary>
    private static bool WouldCollapse(LayoutShape shape) => shape switch
    {
        RectShape r         => r.X2 - r.X1 < 1 || r.Y2 - r.Y1 < 1,
        RoundedRectShape rr => rr.X2 - rr.X1 < 1 || rr.Y2 - rr.Y1 < 1,
        CircleShape c       => c.R < 1,
        PathShape path      => path.Width < 1,
        ViaShape via        => via.PadSize < 1 || via.DrillSize < 1,
        LabelShape label    => label.Height < 1,
        BitmapShape bmp     => bmp.W < 1 || bmp.H < 1,
        PolygonShape or CurveShape => BboxDegenerate(LayoutGeometry.BboxOf(shape)),
        _ => false,
    };

    private static bool BboxDegenerate(Bbox bb) => bb.IsEmpty || bb.MaxX - bb.MinX < 1 || bb.MaxY - bb.MinY < 1;

    // ── Mouse path — bbox scale handles (R-L1h-4/R-L1h-5) ─────────────────────────────────────────
    // A THIRD, parallel gesture state machine alongside _selectDragKind (Move/Marquee) and
    // _handleDragKind (L1d single-shape reshape) — checked first in HandleSelectPress/Move/Release,
    // exactly like those two already are. Live preview reuses Overlay.DragOverrides (already
    // multi-shape-capable); commit is one ReplaceShapesCommand via BuildScaledShapes above, so the
    // mouse path and the numeric dialog can never compute a different answer for the same factors.

    private enum ScaleDragKind { None, Corner, Side }
    private ScaleDragKind _scaleDragKind = ScaleDragKind.None;
    private int _scaleDragHandleIndex;
    private long _scaleAnchorX, _scaleAnchorY;
    private long _scaleHandleOrigX, _scaleHandleOrigY;
    private IReadOnlyList<int> _scaleDragIndices = [];
    private IReadOnlyList<LayoutShape> _scaleDragOriginals = [];
    private double _scaleLiveFactorX = 1.0, _scaleLiveFactorY = 1.0;
    private bool _scaleDragMoved;

    [ObservableProperty] private string _scaleReadoutText = "";

    /// <summary>Hit-tests the CURRENT selection's bbox scale handles and begins a drag if one is hit.
    /// Returns false (does nothing) when no handle is under the point — the caller falls through to
    /// its normal click/marquee logic.</summary>
    private bool TryBeginScaleDrag(long px, long py, bool anchorAtCenter, long tolDbu)
    {
        if (!ShowScaleHandles) return false;
        var indices = ValidSelectedIndices;
        var bbox = SelectionBbox(indices);
        if (bbox.IsEmpty) return false;

        var handles = LayoutScaleHandles.Build(bbox);
        var hit = LayoutScaleHandles.HitTest(handles, px, py, tolDbu);
        if (hit is not { } h) return false;

        // R-bmp: Locked blocks scale (not selection) — a locked bitmap keeps showing handles as part
        // of the bbox that positioned them, but is excluded from what the drag actually scales. A
        // lone locked-bitmap selection has nothing left to scale, so the drag never begins.
        var movable = MovableSelectedIndices(indices);
        if (movable.Count == 0) return false;

        _scaleDragKind = h.Kind == ScaleHandleKind.Corner ? ScaleDragKind.Corner : ScaleDragKind.Side;
        _scaleDragHandleIndex = h.Index;
        _scaleHandleOrigX = h.X; _scaleHandleOrigY = h.Y;
        _scaleDragIndices = movable;
        _scaleDragOriginals = movable.Select(i => Model.Shapes[i]).ToList();
        _scaleDragMoved = false;
        _scaleLiveFactorX = 1.0; _scaleLiveFactorY = 1.0;

        if (anchorAtCenter)
        {
            (_scaleAnchorX, _scaleAnchorY) = MidPoint(bbox);
        }
        else
        {
            var opposite = LayoutScaleHandles.Opposite(handles, h);
            _scaleAnchorX = opposite.X; _scaleAnchorY = opposite.Y;
        }

        RebuildOverlay();
        return true;
    }

    private static (long X, long Y) MidPoint(Bbox bb) => ((bb.MinX + bb.MaxX) / 2, (bb.MinY + bb.MaxY) / 2);

    private void UpdateScaleDragPreview(long px, long py)
    {
        (_scaleLiveFactorX, _scaleLiveFactorY) = ComputeLiveScaleFactors(px, py);
        _scaleDragMoved = true;
        RebuildOverlay();
    }

    private (double Fx, double Fy) ComputeLiveScaleFactors(long px, long py)
    {
        const double minFactor = 0.01; // defensive floor for the LIVE preview only — the real guard runs at commit
        double dHandleX = _scaleHandleOrigX - _scaleAnchorX, dHandleY = _scaleHandleOrigY - _scaleAnchorY;
        double dCurX = px - _scaleAnchorX, dCurY = py - _scaleAnchorY;

        if (_scaleDragKind == ScaleDragKind.Corner)
        {
            double origDist = Math.Sqrt(dHandleX * dHandleX + dHandleY * dHandleY);
            double curDist = Math.Sqrt(dCurX * dCurX + dCurY * dCurY);
            double f = origDist > 1e-9 ? Math.Max(curDist / origDist, minFactor) : 1.0;
            return (f, f);
        }

        bool horizontal = _scaleDragHandleIndex is 1 or 3; // right/left side handles move along X
        if (horizontal)
        {
            double f = Math.Abs(dHandleX) > 1e-9 ? Math.Max(dCurX / dHandleX, minFactor) : 1.0;
            return (f, 1.0);
        }
        else
        {
            double f = Math.Abs(dHandleY) > 1e-9 ? Math.Max(dCurY / dHandleY, minFactor) : 1.0;
            return (1.0, f);
        }
    }

    /// <summary>Typed override mid-drag (§2.1, mirrors L1b's typed Rect W/H commit exactly) — text
    /// that parses as a bare number is a FACTOR; text that parses as a dimension (via
    /// <see cref="LayoutUnits.TryParse"/>) is the resulting SIZE along the dragged axis (both axes,
    /// for a corner drag, applied against the selection's larger original extent). Either commits the
    /// drag immediately at that exact value.</summary>
    public void CommitTypedScale(string text)
    {
        if (_scaleDragKind == ScaleDragKind.None) return;

        double fx = _scaleLiveFactorX, fy = _scaleLiveFactorY;
        string trimmed = text.Trim();

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) && factor > 0)
        {
            if (_scaleDragKind == ScaleDragKind.Corner) { fx = factor; fy = factor; }
            else if (_scaleDragHandleIndex is 1 or 3) fx = factor;
            else fy = factor;
        }
        else if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out long sizeDbu) && sizeDbu > 0)
        {
            var bbox = SelectionBbox(_scaleDragIndices);
            double origW = Math.Max(bbox.MaxX - bbox.MinX, 1.0), origH = Math.Max(bbox.MaxY - bbox.MinY, 1.0);
            if (_scaleDragKind == ScaleDragKind.Corner)
            {
                double f = origW >= origH ? sizeDbu / origW : sizeDbu / origH;
                fx = f; fy = f;
            }
            else if (_scaleDragHandleIndex is 1 or 3) fx = sizeDbu / origW;
            else fy = sizeDbu / origH;
        }
        else
        {
            return; // unparseable — leave the drag exactly as it was, never throw
        }

        CommitScaleDrag(fx, fy);
    }

    private void CommitScaleDrag(double fx, double fy)
    {
        if (_scaleDragIndices.Count == 0) { ResetScaleDragState(); return; }

        if (!_scaleDragMoved || (fx == 1.0 && fy == 1.0))
        {
            // A press-release with no real drag and no typed override — nothing to commit, matches
            // every other handle drag's "no-op click" contract.
            ResetScaleDragState();
            RebuildOverlay();
            return;
        }

        if (fx <= 0 || fy <= 0)
        {
            _messageSink?.Error("Scale factor must be positive.");
            ResetScaleDragState();
            RebuildOverlay();
            return;
        }

        var (scaled, anyPromoted) = BuildScaledShapes(_scaleDragOriginals, fx, fy, _scaleAnchorX, _scaleAnchorY);

        if (scaled.Any(WouldCollapse))
        {
            _messageSink?.Error("Scale would collapse a shape below one DBU — cancelled.");
            ResetScaleDragState();
            RebuildOverlay();
            return;
        }

        var removed = _scaleDragIndices.Select(i => (i, Model.Shapes[i])).ToList();
        int insertAt = _scaleDragIndices.Min();
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, scaled, "Scale"));
        SetSelection(Enumerable.Range(insertAt, scaled.Count));

        if (anyPromoted)
            _messageSink?.Warning("Scale: circular arc(s) were converted to cubic curves to scale non-uniformly.");

        ResetScaleDragState();
    }

    /// <summary>Called from <c>HandleSelectRelease</c> — commits at the live (pointer-derived) factor
    /// unless a typed override already committed the drag first.</summary>
    private void CommitScaleDragFromPointer()
    {
        if (_scaleDragKind == ScaleDragKind.None) return;
        CommitScaleDrag(_scaleLiveFactorX, _scaleLiveFactorY);
    }

    /// <summary>Escape mid-drag (§2.1: "Escape cancels with nothing pushed") — the model was never
    /// touched, only the live preview, so this just drops the drag state.</summary>
    private void ResetScaleDragState()
    {
        _scaleDragKind = ScaleDragKind.None;
        _scaleDragIndices = [];
        _scaleDragOriginals = [];
        _scaleLiveFactorX = 1.0; _scaleLiveFactorY = 1.0;
        _scaleDragMoved = false;
        ScaleReadoutText = "";
    }

    /// <summary>Live readout during a scale drag (§2.1: "factor plus resulting size, in display
    /// units") — recomputed by <c>RebuildOverlay</c> alongside the drag preview itself.</summary>
    private void UpdateScaleReadoutText()
    {
        if (_scaleDragKind == ScaleDragKind.None) { ScaleReadoutText = ""; return; }
        var bbox = SelectionBbox(_scaleDragIndices);
        long w = (long)Math.Round((bbox.MaxX - bbox.MinX) * _scaleLiveFactorX);
        long h = (long)Math.Round((bbox.MaxY - bbox.MinY) * _scaleLiveFactorY);
        string unit = LayoutUnits.Suffix(DisplayUnit);
        ScaleReadoutText = _scaleDragKind == ScaleDragKind.Corner
            ? $"×{_scaleLiveFactorX:0.###} — {LayoutUnits.Format(w, DisplayUnit, Model.DbuPerMicron)} × {LayoutUnits.Format(h, DisplayUnit, Model.DbuPerMicron)} {unit}"
            : $"×{(_scaleDragHandleIndex is 1 or 3 ? _scaleLiveFactorX : _scaleLiveFactorY):0.###} — " +
              $"{LayoutUnits.Format(w, DisplayUnit, Model.DbuPerMicron)} × {LayoutUnits.Format(h, DisplayUnit, Model.DbuPerMicron)} {unit}";
    }
}
