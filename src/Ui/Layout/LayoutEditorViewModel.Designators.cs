// The reference designator a placement draws, and moving it — brief-footprint-4b-designators.md §7.
//
// A THIRD SELECTABLE KIND (R-fp4b-6c), beside the shape channel and the instance channel. The
// closest precedent in this editor is the PCell parameter handle, and it is the right one: both are
// a draggable SUB-OBJECT attached to an instance, both need the "did the press hit the instance or
// the thing sitting on it" disambiguation, and both have to beat the instance-body move drag or
// grabbing one would move the whole part instead — which is the one interaction failure here a user
// cannot work around.
//
// THE DRAG RIDES THE EXISTING INSTANCE-OVERRIDE CHANNEL. A designator drag publishes a clone with
// nothing changed but LabelDx/LabelDy, so the renderer needed no preview path of its own: it already
// substitutes an overridden instance for the stored one, and it already draws the designator from
// whatever instance it is handed. Exactly the reuse R-pch-4b's anchor-pin translate makes.
//
// NOTHING HERE WRITES BACK TO THE SCHEMATIC (R-fp4b-1e, the rule R-fp3-6d already states). In
// practice that only ever arises for RefDes, because an instance with a SchematicId has no editable
// text at all — its designator IS the schematic's, derived, and a rename arrives through Update
// Layout.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Render;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    private readonly List<int> _selectedDesignatorIndices = [];

    /// <summary>Which placements' designators are selected — INSTANCE indices (R-fp4b-6c).</summary>
    public IReadOnlyList<int> SelectedDesignatorIndices => _selectedDesignatorIndices;

    /// <summary>Live while a designator is being dragged. <c>Offset</c> is the pending value of
    /// <see cref="LayoutInstance.LabelDx"/>/<c>LabelDy</c>, in the instance's own placed frame.</summary>
    private sealed record DesignatorDrag(int Index, long GrabDx, long GrabDy, long PressX, long PressY)
    {
        public long OffsetX { get; set; }
        public long OffsetY { get; set; }
        public bool Moved { get; set; }
    }

    private DesignatorDrag? _designatorDrag;

    internal bool DesignatorDragActive => _designatorDrag is not null;

    // ── Where a designator is, measured ONE way ─────────────────────────────────────────────────

    /// <summary>
    /// The silkscreen role this document's technology resolves to, and null when it declares none —
    /// in which case <b>no designator is drawn and none is hit-testable</b>, which is the same answer
    /// the renderer and every export give (R-fp4b-4c). Resolved through <c>LandPatternLayers</c>, the
    /// one function a land pattern's own body outline resolves through.
    /// </summary>
    private LandPatternRoles? DesignatorRoles()
    {
        var roles = LandPatternLayers.Resolve(Technology, PCellLayerSelection.Default, []);
        return roles.Silkscreen is null ? null : roles;
    }

    /// <summary>
    /// The <c>LabelShape</c> instance <paramref name="index"/> draws, or null when it draws none.
    /// <b>The one measurement the renderer, the hit test, the drag and Reset all read</b> — built out
    /// of <c>FootprintLabel</c> rather than re-derived here, because three of them agreeing by
    /// coincidence is not the same as one of them being right (R-fp4b-2c).
    /// </summary>
    private LabelShape? DesignatorLabelAt(int index, LandPatternRoles? roles)
    {
        if (roles is not { Silkscreen: { } silk }) return null;
        if (index < 0 || index >= Model.Instances.Count) return null;

        var inst = EffectiveInstanceAt(index);
        LayoutView? cellView = null;
        if (inst.LabelDx is null || inst.LabelDy is null)
            cellView = CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir).View;

        return FootprintLabel.ShapeFor(inst, cellView, roles, Model.DbuPerMicron, silk);
    }

    /// <summary>The world box of instance <paramref name="index"/>'s designator — what a click has to
    /// land in, and what the selection outline draws around.</summary>
    internal Bbox? DesignatorBboxAt(int index)
        => DesignatorLabelAt(index, DesignatorRoles()) is { } label
            ? LayoutRenderer.DesignatorWorldBbox(label)
            : null;

    /// <summary>
    /// Which placement's designator is under the press point — the TOPMOST, taken as the LAST in
    /// document order so the one painted over the others wins, mirroring how the pick stack reads
    /// paint order everywhere else in this editor.
    /// </summary>
    private int HitTestDesignator(long px, long py, long tolDbu)
    {
        var roles = DesignatorRoles();
        if (roles is null) return -1;

        for (int i = Model.Instances.Count - 1; i >= 0; i--)
        {
            if (DesignatorLabelAt(i, roles) is not { } label) continue;
            if (LayoutRenderer.DesignatorWorldBbox(label) is not { IsEmpty: false } bb) continue;
            if (px >= bb.MinX - tolDbu && px <= bb.MaxX + tolDbu &&
                py >= bb.MinY - tolDbu && py <= bb.MaxY + tolDbu)
                return i;
        }
        return -1;
    }

    // ── The drag (R-fp4b-6a) ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begins a designator drag if the press landed on one. Called from the pointer-press path BEFORE
    /// the instance-body move drag, for the reason a PCell grip is tested before it: a press on the
    /// thing sitting on an instance must not move the instance.
    /// </summary>
    private bool TryBeginDesignatorDrag(long px, long py, long tolDbu)
    {
        int index = HitTestDesignator(px, py, tolDbu);
        if (index < 0) return false;

        var inst = Model.Instances[index];
        // A drag that starts from AUTO has to start from where auto actually put it, or the label
        // would jump to the origin on the first pixel of the gesture. Resolving the current offset
        // here — rather than storing it at placement time — is what keeps auto recomputed
        // (R-fp4b-2a) right up to the moment the user makes it manual.
        var (dx, dy) = CurrentLabelOffset(inst);

        _designatorDrag = new DesignatorDrag(index, dx, dy, px, py) { OffsetX = dx, OffsetY = dy };
        _cycleCache.Clear();
        SetDesignatorSelection([index]);
        return true;
    }

    /// <summary>
    /// <paramref name="inst"/>'s designator offset as a number — its stored one, or the auto position
    /// resolved through <c>FootprintLabel</c>. The inverse of the frame transform
    /// <c>FootprintLabel.PlacementFor</c> applies, so a drag that does not move leaves the picture
    /// exactly where it was.
    /// </summary>
    private (long Dx, long Dy) CurrentLabelOffset(LayoutInstance inst)
    {
        if (inst.LabelDx is { } sx && inst.LabelDy is { } sy) return (sx, sy);

        var roles = DesignatorRoles();
        if (DesignatorLabelAt(Model.Instances.IndexOf(inst), roles) is not { } label) return (0, 0);

        var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(
            label.X, label.Y,
            new LayoutInstance { X = inst.X, Y = inst.Y, RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = 1.0 },
            0, 0);
        return ((long)Math.Round(lx), (long)Math.Round(ly));
    }

    /// <summary>Live update — publishes the pending offset through the instance-override channel, so
    /// the renderer draws it without a preview path of its own.</summary>
    private void UpdateDesignatorDrag(long px, long py)
    {
        if (_designatorDrag is not { } drag) return;
        if (drag.Index < 0 || drag.Index >= Model.Instances.Count) return;

        var inst = Model.Instances[drag.Index];
        // The cursor delta is in the PARENT's frame; the stored offset is in the instance's PLACED
        // frame (R-fp4b-2b), so the delta turns back through the placement's own rotation and mirror.
        // Unit magnification, because the stored offset is already in the parent's DBU.
        var unit = new LayoutInstance { RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = 1.0 };
        var (ldx, ldy) = LayoutInstanceTransform.InverseTransformPoint(px - drag.PressX, py - drag.PressY, unit, 0, 0);

        drag.OffsetX = drag.GrabDx + (long)Math.Round(ldx);
        drag.OffsetY = drag.GrabDy + (long)Math.Round(ldy);
        drag.Moved = px != drag.PressX || py != drag.PressY;
        RebuildOverlay();
    }

    /// <summary>ONE undo entry for the whole gesture (R-fp4b-6a), and nothing at all when the press
    /// turned out to be a click.</summary>
    private void CommitDesignatorDrag()
    {
        if (_designatorDrag is not { } drag) return;
        _designatorDrag = null;

        if (!drag.Moved || drag.Index < 0 || drag.Index >= Model.Instances.Count) { RebuildOverlay(); return; }

        var before = Model.Instances[drag.Index];
        var after = LayoutGeometry.Clone(before);
        after.LabelDx = drag.OffsetX;
        after.LabelDy = drag.OffsetY;
        Execute(new SetInstanceLabelsCommand(Model, [(drag.Index, before, after)], "Move Designator"));
        RebuildOverlay();
    }

    private void CancelDesignatorDrag()
    {
        _designatorDrag = null;
        RebuildOverlay();
    }

    /// <summary>The drag's live override, or null — read by <c>RebuildOverlay</c>.</summary>
    private KeyValuePair<int, LayoutInstance>? DesignatorDragOverride()
    {
        if (_designatorDrag is not { } drag) return null;
        if (drag.Index < 0 || drag.Index >= Model.Instances.Count) return null;
        var clone = LayoutGeometry.Clone(Model.Instances[drag.Index]);
        clone.LabelDx = drag.OffsetX;
        clone.LabelDy = drag.OffsetY;
        return new KeyValuePair<int, LayoutInstance>(drag.Index, clone);
    }

    // ── Selection ───────────────────────────────────────────────────────────────────────────────

    private void SetDesignatorSelection(IEnumerable<int> indices)
    {
        var distinct = new List<int>();
        foreach (int i in indices)
            if (i >= 0 && i < Model.Instances.Count && !distinct.Contains(i)) distinct.Add(i);

        _selectedDesignatorIndices.Clear();
        _selectedDesignatorIndices.AddRange(distinct);

        // Mutually exclusive with the other two channels, the way instance selection already is with
        // shapes: the gestures a designator offers (drag it, reset it) have nothing in common with
        // the ones a shape or a whole instance offers, and a mixed selection would advertise all of
        // them.
        if (distinct.Count > 0)
        {
            if (_selectedIndices.Count > 0) { _selectedIndices.Clear(); _pickedVertexIndex = null; }
            if (_selectedInstanceIndices.Count > 0) _selectedInstanceIndices.Clear();
            if (_selectedRulerIndices.Count > 0) _selectedRulerIndices.Clear();
        }

        SelectionStatusText = ComputeSelectionStatus();
        RebuildOverlay();
    }

    private string ComputeDesignatorSelectionStatus()
    {
        if (_selectedDesignatorIndices.Count == 0) return "";
        if (_selectedDesignatorIndices.Count == 1 &&
            Model.Instances[_selectedDesignatorIndices[0]].DisplayRefDes is { Length: > 0 } d)
            return $"Designator: {d}";
        return $"{_selectedDesignatorIndices.Count} designators";
    }

    // ── Getting it back (R-fp4b-6b) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Reset to auto — mandatory, not a nicety</b> (R-fp4b-6b). Clears
    /// <see cref="LayoutInstance.LabelDx"/>/<c>LabelDy</c>/<c>LabelRotDeg</c> back to null, i.e. back
    /// to DERIVED, not back to a remembered number: a dragged label with no way home is a trap, and
    /// it is the first thing a user hits after dragging one by accident.
    ///
    /// <para>Applies to whichever channel holds a selection — the designators if any are selected,
    /// otherwise the selected instances — so the command is reachable both ways a user might have
    /// arrived at it.</para>
    /// </summary>
    public void ResetSelectedDesignatorPositions()
    {
        var targets = _selectedDesignatorIndices.Count > 0
            ? _selectedDesignatorIndices
            : _selectedInstanceIndices;

        var edits = new List<(int, LayoutInstance, LayoutInstance)>();
        foreach (int i in targets)
        {
            if (i < 0 || i >= Model.Instances.Count) continue;
            var before = Model.Instances[i];
            if (before.LabelDx is null && before.LabelDy is null && before.LabelRotDeg is null) continue;
            var after = LayoutGeometry.Clone(before);
            after.LabelDx = null; after.LabelDy = null; after.LabelRotDeg = null;
            edits.Add((i, before, after));
        }
        if (edits.Count == 0) return;
        Execute(new SetInstanceLabelsCommand(Model, edits, "Reset Designator Position"));
        RebuildOverlay();
    }

    /// <summary>Whether Reset would do anything — what the menu item's enablement reads.</summary>
    public bool CanResetSelectedDesignatorPositions
        => (_selectedDesignatorIndices.Count > 0 ? _selectedDesignatorIndices : _selectedInstanceIndices)
            .Any(i => i >= 0 && i < Model.Instances.Count &&
                      (Model.Instances[i].LabelDx is not null || Model.Instances[i].LabelDy is not null ||
                       Model.Instances[i].LabelRotDeg is not null));

    /// <summary>
    /// Per-instance visibility (R-fp4b-6d) — a MULTI-SELECT edit, because turning designators off for
    /// a crowded corner of a board is inherently one. <b>There is deliberately no global toggle</b>:
    /// a view-only switch that makes the screen disagree with the export is precisely the defect
    /// R-fp4b-4a exists to prevent, and the silk layer's own visibility already hides designators on
    /// screen along with the body outlines they belong to.
    /// </summary>
    public void SetSelectedInstancesShowRefDes(bool shown)
    {
        var targets = _selectedInstanceIndices.Count > 0
            ? _selectedInstanceIndices
            : _selectedDesignatorIndices;

        var edits = new List<(int, LayoutInstance, LayoutInstance)>();
        foreach (int i in targets)
        {
            if (i < 0 || i >= Model.Instances.Count) continue;
            var before = Model.Instances[i];
            if (before.DesignatorShown == shown) continue;
            var after = LayoutGeometry.Clone(before);
            // Null MEANS shown (R-fp4b-7a), so "show" writes null rather than true — otherwise a
            // toggle off and on again would leave a field in a file that had none, and the additive
            // guarantee (an untouched .clay re-saves byte-for-byte) would hold only until someone
            // clicked twice.
            after.ShowRefDes = shown ? null : false;
            edits.Add((i, before, after));
        }
        if (edits.Count == 0) return;
        Execute(new SetInstanceLabelsCommand(
            Model, edits, shown ? "Show Designator" : "Hide Designator"));
        RebuildOverlay();
    }

    /// <summary>
    /// Sets the selected instance's OWN designator — the hand-placed case only. An instance with a
    /// <see cref="LayoutInstance.SchematicId"/> is refused silently rather than diverted into
    /// <see cref="LayoutInstance.RefDes"/>: nothing writes that field on an instance that has a
    /// schematic id (R-fp4b-1c), because two fields with one meaning drift.
    ///
    /// <para><b>It does not write back to the schematic</b> (R-fp4b-1e) — and cannot arise for one
    /// that would, since an instance the schematic owns has no editable text at all.</para>
    /// </summary>
    public void CommitSelectedInstanceRefDes(string text)
    {
        if (_selectedInstanceIndices.Count != 1) return;
        int index = _selectedInstanceIndices[0];
        var before = Model.Instances[index];
        if (before.SchematicId is { Length: > 0 }) return;

        string? value = text.Trim() is { Length: > 0 } t ? t : null;
        if (string.Equals(before.RefDes, value, StringComparison.Ordinal)) return;

        var after = LayoutGeometry.Clone(before);
        after.RefDes = value;
        Execute(new SetInstanceLabelsCommand(Model, [(index, before, after)], "Edit Designator"));

        // R-fp4b-8d — REPORTED, never renumbered. Two parts called C3 is a real condition a user
        // needs told about; silently renaming one of them is how a board stops matching its BOM.
        if (value is not null)
            foreach (string report in FootprintLabel.DuplicateReports(Model))
                if (report.Contains($"'{value}'", StringComparison.Ordinal))
                    _messageSink?.Warning(report);
    }

    /// <summary>
    /// Seeds a HAND-placed instance's designator from the part's own declared prefix (R-fp4b-8c) —
    /// <c>C</c> gives <c>C1</c>, then <c>C2</c>. <b>A part with no prefix gets no designator, not an
    /// invented one</b>: an instance corresponding to no schematic component must not be given a
    /// fabricated identity (R-fp3-6c).
    /// </summary>
    private void SeedDesignator(LayoutInstance inst)
    {
        if (inst.SchematicId is { Length: > 0 }) return;   // derived — it has one already
        string? cellDir = CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir).ResolvedCellDir;
        inst.RefDes = FootprintLabel.SeedDesignator(Model, FootprintLabel.PrefixOfCell(cellDir));
    }
}
