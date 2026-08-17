using System;
using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// WB40 — <b>a wirebond cell is an ordinary cell whose folder holds a <c>.wBond</c> beside its
/// <c>.clay</c>.</b>
///
/// <h3>Nothing new is invented</h3>
/// <para><see cref="LayoutEditorViewModel.WireDesign"/> was already the seam that puts a wire overlay
/// over a layout, and <c>WBondLayoutOverlay</c> already draws and edits through it — until now only
/// the wBond document ever set them. This partial is the other setter: a session loaded from a cell
/// that has a <c>.wBond</c> arrives with its wires attached, so pushing into that cell in the ordinary
/// Layout Editor shows them, editable, with no wBond-specific code in that view at all.</para>
///
/// <h3>WB23 is unchanged</h3>
/// <para>No 3D shape enters the <c>.clay</c>, the canvas stays 2D, no volumetric mesher is written,
/// and a wire drag still invalidates only the overlay. The wires live in their own file, which already
/// round-trips, already exports to DXF for the assembly house, and is already what the standalone
/// application reads.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// The wire layer over this layout, when this session came from a wirebond cell. Null for every
    /// ordinary layout, and null in the wBond editor — there the DOCUMENT owns the overlay, because
    /// the wires are the document rather than a property of the artwork.
    /// </summary>
    public WBondLayoutOverlay? WireOverlay { get; private set; }

    /// <summary>The editor behind <see cref="WireOverlay"/>, or null when there is none.</summary>
    public WBondViewModel? WireEditor { get; private set; }

    /// <summary>Absolute path of the <c>.wBond</c> this session's wires were read from, if any.</summary>
    public string? WireDesignPath { get; private set; }

    /// <summary>
    /// True after a wire edit that has not been written back. Kept beside <c>_prefsDirty</c> and for
    /// the same reason: a wire edit carries no entry on the LAYOUT's undo stack (it has its own), so
    /// without this the cell would look clean with unsaved wires in it.
    /// </summary>
    private bool _wireDirty;

    /// <summary>
    /// Attaches <paramref name="design"/> read from <paramref name="wBondPath"/> as this session's
    /// wire layer.
    ///
    /// <para>The design object itself, not a copy: the overlay mutates its wires in place, so a
    /// snapshot would leave the DRC checking geometry the user has since moved — the same rule the
    /// wBond document's own attachment follows.</para>
    /// </summary>
    public void AttachWireDesign(WBondDesign design, string wBondPath)
    {
        ArgumentNullException.ThrowIfNull(design);

        WireEditor = new WBondViewModel(design);
        WireDesignPath = wBondPath;

        WireOverlay = new WBondLayoutOverlay(WireEditor)
        {
            ReferenceLayout = Model,
            ReferenceTechnology = Technology,
            ReferenceBaseDir = InstanceBaseDir,
            GridPitchNm = GridPitchNm(),

            // An armed drawing tool takes the canvas — the overlay is offered every press first, so
            // without this arming Rectangle over a wirebond cell would start a wire marquee.
            LayoutToolArmed = () => ActiveTool != Tool.Select,

            // …and empty space stays the LAYOUT's marquee here. That is the opposite of the wBond
            // editor's default, and deliberately: there the wires are the subject and the artwork is
            // reference; in the Layout Editor it is the other way round.
            WireMarqueeEnabled = false,
        };

        WireEditor.DirtyChanged += () =>
        {
            _wireDirty = true;
            IsDirty = true;

            // Fires on an edit AND on an undo/redo (WBondViewModel.Republish raises it from both), which
            // is exactly the signal "this history moved" that the workspace's Undo command needs.
            WireHistoryChanged?.Invoke();
        };

        // ── The wires follow the LAYOUT's own snap and display unit ───────────────────────────────
        //
        // In the wBond editor WBondDocumentViewModel keeps these in step; a wirebond cell in the
        // ordinary Layout Editor had nothing doing it, so the wire grid was drawn at pitch 0 (no grid at
        // all) and the profile view's rulers stayed on the wBond default while the layout's Unit combo
        // said something else (owner, 2026-08-17). There is exactly one Snap box and one Unit box in
        // this editor, and they govern the wires too.
        PushLayoutSnapAndUnitToWires();

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SnapDbu) or nameof(DisplayUnit))
                PushLayoutSnapAndUnitToWires();
        };

        // A wire edit or SELECTION change made from somewhere other than the canvas — the docked
        // inductance panel's array double-click and its four settable rows are the reason this exists —
        // has to repaint the canvas showing those wires. The overlay itself was not touched, so nothing
        // else would.
        WireEditor.PropertyChanged += OnWireEditorPropertyChanged;
        WireEditor.ReadoutChanged += () => WireOverlay?.NotifyChanged();

        // LAST, and deliberately: this is the notification a view watches to attach the overlay to its
        // canvas, so everything it needs — the editor, the overlay, the path — has to be in place
        // before it fires. Attaching a session that is already ON SCREEN is the ordinary case now that
        // Update Layout from Schematic seeds the sidecar into an open document (wbond.md §9.5).
        WireDesign = design;
    }

    private long GridPitchNm() => SnapDbu > 0 ? WBondSnap.ToNm(SnapDbu, Model.DbuPerMicron) : 0;

    /// <summary>
    /// Gives the wires this layout's snap pitch and display unit — the grid both the overlay and the
    /// profile view draw, and the unit every wire readout is formatted in.
    /// </summary>
    private void PushLayoutSnapAndUnitToWires()
    {
        if (WireOverlay is { } overlay) overlay.GridPitchNm = GridPitchNm();

        if (WireEditor is { } editor
            && WBondDocumentViewModel.ToWBondUnit(DisplayUnit) is { } unit
            && editor.DisplayUnit != unit)
            editor.DisplayUnit = unit;

        WireGridPitchChanged?.Invoke();
    }

    /// <summary>Raised when the wire grid pitch changed, for a view that draws its own grid from it —
    /// the docked Wire Profile panel, which has no other way to learn this layout's snap.</summary>
    public event Action? WireGridPitchChanged;

    /// <summary>The wire grid pitch in nanometres, or 0 for no grid.</summary>
    public long WireGridPitchNm => GridPitchNm();

    private void OnWireEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WBondViewModel.Selection)
                           or nameof(WBondViewModel.PreviewSelection))
            WireOverlay?.NotifyChanged();
    }

    // ── Undo across TWO histories ─────────────────────────────────────────────
    //
    // Owner, 2026-08-17: "Undo does not work from the layout on a wire." It never could — the workspace
    // routes Undo to LayoutDocument.UndoRedo, which is this session's COMMAND stack, and a wire edit
    // lives in WireEditor's own SNAPSHOT stack. Nothing reached it from here.
    //
    // The two cannot be one stack (one replays commands, the other restores whole-design snapshots), and
    // neither can simply win: "layout first" would undo a shape move made ten minutes ago instead of the
    // wire the user just dragged. Every recorded entry carries an EditSequence stamp, so "what did I do
    // last" has one total answer — the same rule, and the same helper, the wBond editor's own Ctrl+Z uses.

    /// <summary>Raised whenever the WIRE history changed — an edit, an undo or a redo. What lets the
    /// workspace re-evaluate its Undo/Redo commands, which otherwise only watch the command stack.</summary>
    public event Action? WireHistoryChanged;

    /// <summary>True when this session has wires with anything to undo.</summary>
    private bool WireCanUndo => WireEditor?.CanUndo == true;

    private bool WireCanRedo => WireEditor?.CanRedo == true;

    /// <summary>Whether <see cref="UndoLast"/> would act on the WIRES rather than on the artwork.</summary>
    internal bool UndoTakesWires => EditSequence.UndoTakesFirst(
        WireCanUndo, WireEditor?.TopUndoStamp ?? 0, UndoRedo.CanUndo, UndoRedo.TopUndoStamp);

    internal bool RedoTakesWires => EditSequence.RedoTakesFirst(
        WireCanRedo, WireEditor?.TopRedoStamp ?? 0, UndoRedo.CanRedo, UndoRedo.TopRedoStamp);

    /// <summary>Undoes whichever of this session's two histories holds the more recent edit.</summary>
    public void UndoLast()
    {
        if (UndoTakesWires)
        {
            WireEditor!.Undo();
            WireHistoryChanged?.Invoke();
            return;
        }

        UndoRedo.Undo();
    }

    /// <summary>Redoes the oldest undone entry across both histories — the one the last undo produced.</summary>
    public void RedoLast()
    {
        if (RedoTakesWires)
        {
            WireEditor!.Redo();
            WireHistoryChanged?.Invoke();
            return;
        }

        UndoRedo.Redo();
    }

    /// <summary>Whether EITHER history has something to undo.</summary>
    public bool CanUndoLast => WireCanUndo || UndoRedo.CanUndo;

    public bool CanRedoLast => WireCanRedo || UndoRedo.CanRedo;

    /// <summary>
    /// What the menu should say. The wire history keeps no per-entry description — its entries are
    /// whole-design snapshots, not named commands — so it reports the kind of thing rather than
    /// inventing a name for it, and the artwork's own stack still names its command exactly.
    /// </summary>
    public string UndoLastDescription => UndoTakesWires ? "Undo wirebond edit" : UndoRedo.UndoDescription;

    public string RedoLastDescription => RedoTakesWires ? "Redo wirebond edit" : UndoRedo.RedoDescription;

    /// <summary>
    /// Writes the wire layer back beside the <c>.clay</c>, if it has one and it changed.
    ///
    /// <para>Called from <see cref="MarkSaved"/>, which every save path in the application funnels
    /// through — the workspace writes sub-cell sessions with a bare
    /// <c>LayoutPersistence.SaveToFile</c> rather than through this view model, so hooking any single
    /// writer would miss half of them. A failure is reported through the same
    /// <see cref="SaveError"/> seam a failed <c>.clay</c> write uses and does NOT clear the flag, so
    /// the next save tries again rather than the edits being quietly lost.</para>
    /// </summary>
    private void SaveWireDesignIfDirty()
    {
        if (!_wireDirty) return;
        if (WireDesign is not { } design || WireDesignPath is not { Length: > 0 } path) return;

        try
        {
            WBondIo.WriteFile(path, design);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save wirebonds to '{path}': {ex.Message}");
            return;
        }

        _wireDirty = false;
    }
}
