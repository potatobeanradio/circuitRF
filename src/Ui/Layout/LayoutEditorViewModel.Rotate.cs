using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Rigid-body transforms of the selection — rotate 90° (<c>R</c> / <c>Shift+R</c>) and mirror
/// (<c>M</c> / <c>Shift+M</c>), with toolbar buttons for each. Same controls, icons and sense as the
/// Schematic Editor.
///
/// <para><b>A multi-selection rotates as ONE RIGID BODY — this is a deliberate departure from the
/// Schematic Editor, which rotates each selected component about its own origin.</b> The two are
/// right for different things: a schematic is a connectivity diagram, so spinning each symbol in
/// place is what the user means. Layout is physical artwork, where the relative positions of the
/// selected shapes ARE the design — rotating each about its own centre would scramble a via array,
/// a coupled pair, or a taper-plus-line into nonsense. So there is a single pivot for the whole
/// selection (its combined extent) and every shape keeps its position and orientation relative to
/// every other. Owner's explicit call. The same rule governs mirror.</para>
///
/// <para><b>Geometry is exact — the rotation never rounds a vertex.</b> A 90° rotation about the
/// ORIGIN is <c>(x,y) → (-y,x)</c>, pure integer arithmetic on DBU. Only the re-centering translation
/// can land off-grid, and it is rounded ONCE for the whole selection rather than per vertex — the
/// same "snap the delta, not each vertex" rule R-L1c-3 already established for Move, and for the same
/// reason: rounding every vertex independently would deform an off-grid shape (an imported outline, a
/// flattened arc) instead of moving it.</para>
///
/// <para><b>Arc bulge: untouched by rotation, flipped by mirror.</b> Bulge is <c>tan(sweep/4)</c> —
/// dimensionless and measured relative to the edge's own chord — so a rotation carries it unchanged.
/// A mirror is a reflection (determinant −1) and reverses which side of the chord the arc bulges
/// toward, so it MUST flip the sign (<c>LayoutFlatten.FlipBulgeSigns</c>). Getting this wrong is
/// silent: the shape still draws, just with its curves inverted.</para>
/// </summary>
public partial class LayoutEditorViewModel
{
    /// <summary>
    /// Rotate is enabled whenever there is anything rotatable selected. Unlike the boolean/offset
    /// family it is NOT shape-only — an instance rotates perfectly well (it carries its own
    /// <see cref="LayoutRotation"/>) — but a bitmap does not: <c>BitmapShape</c> is an axis-aligned
    /// image with no rotation of its own (R-bmp-3: a bitmap is not geometry), so it is excluded the
    /// same way the boolean operations exclude it.
    /// </summary>
    public LayoutCommandAvailability MirrorAvailability => RotateAvailability;

    /// <inheritdoc cref="MirrorAvailability"/>
    ///
    /// <para>A WIRE selection is enough on its own (owner, 2026-08-17). On a wirebond cell the wires
    /// are content of the same view and rotate with it, so a toolbar button greyed out with wires
    /// selected would be refusing the commonest thing on that cell.</para>
    public LayoutCommandAvailability RotateAvailability =>
        GeometricSelectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0
        && SelectedWireIndices.Count == 0
            ? new LayoutCommandAvailability(false, "Select geometry, an instance or a wire to rotate.")
            : new LayoutCommandAvailability(true, null);

    /// <summary>Rotates the selection 90°: counter-clockwise by default, clockwise with
    /// <paramref name="clockwise"/> — the same sense and keys as the Schematic Editor.</summary>
    public void RotateSelection(bool clockwise = false)
        => ApplyRigidBodyTransform(
            mapAboutOrigin: clockwise ? (x, y) => (y, -x) : (x, y) => (-y, x),
            flipsBulge:     false,                       // a rotation preserves handedness
            // R-L3d-11: ADVANCE by 90 deg, never snap to a cardinal. Rotating a 30 deg placement three
            // times gives 300 deg, not 270 deg — a snapping R key would make a non-cardinal placement
            // un-nudgeable, which is the opposite of what arbitrary angles are for.
            instanceRotDeg: d => d + (clockwise ? -90.0 : 90.0),
            togglesInstanceMirror: false,
            // A label's own angle advances by the same 90 deg, and for the same R-L3d-11 reason now
            // that it can be non-cardinal: R on a 33 deg imported annotation must give 123 deg, not
            // snap it to 90 and make it un-nudgeable.
            labelRotDeg:    d => d + (clockwise ? -90.0 : 90.0),
            portDirection:  r => AdvanceRotation(r, clockwise),
            description:    "Rotate");

    /// <summary>
    /// Mirrors the selection: about a VERTICAL axis by default (a horizontal flip, left↔right), or
    /// about a horizontal axis with <paramref name="horizontal"/> false — matching the Schematic
    /// Editor's own <c>M</c> / <c>Shift+M</c> split.
    ///
    /// <para>The instance bookkeeping is the subtle part, and is derived rather than guessed. An
    /// instance's transform is mirror-then-rotate, so pre-composing a world reflection <c>M</c> gives
    /// <c>M ∘ Rot(θ) ∘ Mx^m = Rot(−θ) ∘ Mx^(m+1)</c>: the rotation NEGATES and the mirror flag
    /// toggles. A vertical flip is <c>Rot(180) ∘ M</c>, so its rotation becomes <c>180 − θ</c>.
    /// Toggling the flag without also fixing the rotation silently mis-places every rotated
    /// instance.</para>
    /// </summary>
    public void MirrorSelection(bool horizontal = true)
        => ApplyRigidBodyTransform(
            mapAboutOrigin: horizontal ? (x, y) => (-x, y) : (x, y) => (x, -y),
            flipsBulge:     true,                        // a reflection reverses arc handedness
            //  horizontal: Rot(-θ).   vertical: Rot(180-θ), i.e. a half turn on top of the negation.
            instanceRotDeg: horizontal ? d => -d : d => 180.0 - d,
            togglesInstanceMirror: true,
            // A LabelShape has no mirror flag, so mirrored text is not representable — and reversed
            // text would be worse than none. The anchor moves with the geometry; the glyphs stay
            // readable at their existing rotation. Stated rather than silently dropped.
            labelRotDeg:    d => d,
            portDirection:  r => r,
            description:    "Mirror");

    /// <summary>
    /// The one implementation both rotate and mirror run through: pivot on the selection's combined
    /// extent, map about the origin exactly, re-centre with a SINGLE rounded translation, commit as
    /// one undo entry.
    /// </summary>
    private void ApplyRigidBodyTransform(
        Func<long, long, (long X, long Y)> mapAboutOrigin,
        bool flipsBulge,
        Func<double, double> instanceRotDeg,
        bool togglesInstanceMirror,
        Func<double, double> labelRotDeg,
        Func<LayoutRotation, LayoutRotation> portDirection,
        string description)
    {
        var shapeIndices    = GeometricSelectedIndices;
        var instanceIndices = _selectedInstanceIndices.ToList();

        // WB40f: the WIRES of a wirebond cell are content of this same view and are carried by the
        // same gesture. They also count on their own — a wires-only selection is a normal thing to
        // rotate on that cell.
        var wireIndices = SelectedWireIndices;

        if (shapeIndices.Count == 0 && instanceIndices.Count == 0 && wireIndices.Count == 0) return;

        var bbox = Bbox.Empty;
        foreach (var i in shapeIndices) bbox = bbox.Union(LayoutGeometry.BboxOf(Model.Shapes[i]));
        foreach (var i in instanceIndices)
            bbox = bbox.Union(CellHierarchy.InstanceBbox(Model.Instances[i], InstanceBaseDir));

        // …and the wires join the SAME bbox, so the pivot is the whole selection's centre. Anything
        // else would swing a wire off the pad it lands on when the two are rotated together, which is
        // the one outcome §6.3's "both selections at once" contract exists to make possible.
        bbox = bbox.Union(SelectedWireBbox(wireIndices));

        if (bbox.MaxX < bbox.MinX || bbox.MaxY < bbox.MinY) return;

        // Doubled centres keep the arithmetic integral until the single rounding step below.
        long cx2 = bbox.MinX + bbox.MaxX;
        long cy2 = bbox.MinY + bbox.MaxY;
        var (rcx2, rcy2) = mapAboutOrigin(cx2, cy2);
        long dx = (long)Math.Round((cx2 - rcx2) / 2.0, MidpointRounding.AwayFromZero);
        long dy = (long)Math.Round((cy2 - rcy2) / 2.0, MidpointRounding.AwayFromZero);

        // Magnitude is IDENTITY for both: neither a rotation nor a mirror changes a radius, width,
        // corner radius, pad/drill or label height. Only Scale has a magnitude factor.
        var transform = new LayoutCoordinateTransform(
            (x, y) => { var (mx, my) = mapAboutOrigin(x, y); return (mx + dx, my + dy); },
            m => m);

        var removed = new List<(int Index, LayoutShape Before)>(shapeIndices.Count);
        var result  = new List<LayoutShape>(shapeIndices.Count);
        foreach (var i in shapeIndices)
        {
            var clone = LayoutGeometry.Clone(Model.Shapes[i]);
            LayoutCoordinateWalk.Transform(clone, transform);
            if (flipsBulge) LayoutFlatten.FlipBulgeSigns(clone);
            NormalizeRectCorners(clone);

            // A PORT label rotates its DIRECTION and leaves its text upright; an ordinary label
            // rotates its text. Rotating a port's glyph too would leave a right-hand port (R180)
            // legible only upside down, and the arrow is the thing the gesture is actually aimed at
            // (owner report, 2026-08-09). A port whose direction is still null is being rotated for
            // the first time, so it adopts the inferred direction and advances from there — which is
            // also the moment "infer it" becomes "the user said so".
            if (clone is LabelShape { IsPort: true } port)
            {
                var current = port.PortDirection
                              ?? LayoutPortDirection.Resolve(Model.Shapes, port)?.Direction
                              ?? LayoutRotation.R0;
                port.PortDirection = portDirection(current);
            }
            else if (clone is LabelShape label)
            {
                label.RotationDegrees = labelRotDeg(label.RotationDegrees);
            }

            removed.Add((i, Model.Shapes[i]));
            result.Add(clone);
        }

        var instanceCommands = new List<IUiCommand>(instanceIndices.Count);
        foreach (var i in instanceIndices)
        {
            var before = Model.Instances[i];
            var after  = LayoutGeometry.Clone(before);
            var (nx, ny) = transform.Point(before.X, before.Y);
            after.X   = nx;
            after.Y   = ny;
            after.RotationDegrees = instanceRotDeg(before.RotationDegrees);
            if (togglesInstanceMirror) after.MirrorX = !before.MirrorX;
            instanceCommands.Add(new Commands.Layout.ReplaceInstanceCommand(Model, i, before, after));
        }

        IUiCommand? command = removed.Count > 0
            ? new Commands.Layout.ReplaceShapesCommand(Model, removed, result, description)
            : null;
        foreach (var c in instanceCommands)
            command = command is null ? c : new CompositeCommand(command, c);

        // The wire half rides in the SAME entry — see TransformWiresCommand for why one entry rather
        // than two. Its map is the layout's own, in the wires' units: nm and DBU are the same number
        // only at the 1,000 DBU/µm default, so the bridge is crossed explicitly here.
        if (wireIndices.Count > 0 && WireEditor is { } wireEditor)
        {
            int dbuPerMicron = Model.DbuPerMicron;
            (long X, long Y) WireMap(long xNm, long yNm)
            {
                var (dx2, dy2) = transform.Point(WBondSnap.ToDbu(xNm, dbuPerMicron),
                                                 WBondSnap.ToDbu(yNm, dbuPerMicron));
                return (WBondSnap.ToNm(dx2, dbuPerMicron), WBondSnap.ToNm(dy2, dbuPerMicron));
            }

            var wireCommand = new Commands.Layout.TransformWiresCommand(
                wireEditor, wireIndices, WireMap, description);

            command = command is null ? wireCommand : new CompositeCommand(command, wireCommand);
        }

        if (command is not null) Execute(command);
    }

    /// <summary>Next 90° step. CCW advances R0→R90→R180→R270; CW runs the other way.</summary>
    private static LayoutRotation AdvanceRotation(LayoutRotation r, bool clockwise) => clockwise
        ? r switch
        {
            LayoutRotation.R0   => LayoutRotation.R270,
            LayoutRotation.R270 => LayoutRotation.R180,
            LayoutRotation.R180 => LayoutRotation.R90,
            _                   => LayoutRotation.R0,
        }
        : r switch
        {
            LayoutRotation.R0   => LayoutRotation.R90,
            LayoutRotation.R90  => LayoutRotation.R180,
            LayoutRotation.R180 => LayoutRotation.R270,
            _                   => LayoutRotation.R0,
        };

    /// <summary>
    /// Restores the min/max corner convention after a transform that can invert it. Rect and
    /// RoundedRect store two opposite corners rather than an origin-plus-size, so a 90° rotation
    /// leaves (X1,Y1) holding what is now the maximum on one axis.
    /// </summary>
    private static void NormalizeRectCorners(LayoutShape shape)
    {
        switch (shape)
        {
            case RectShape r:
                if (r.X1 > r.X2) (r.X1, r.X2) = (r.X2, r.X1);
                if (r.Y1 > r.Y2) (r.Y1, r.Y2) = (r.Y2, r.Y1);
                break;
            case RoundedRectShape rr:
                if (rr.X1 > rr.X2) (rr.X1, rr.X2) = (rr.X2, rr.X1);
                if (rr.Y1 > rr.Y2) (rr.Y1, rr.Y2) = (rr.Y2, rr.Y1);
                break;
        }
    }
}
