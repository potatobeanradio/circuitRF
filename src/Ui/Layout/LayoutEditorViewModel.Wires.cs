using System;
using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;

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

    // ── The wire tool, in the ordinary Layout Editor (owner, 2026-08-17) ──────────────────────────
    //
    // The overlay has had WireDrawArmed since WB-C; only the wBond editor ever set it, through its own
    // WBondTool enum. This is the Layout Editor's setter, and it is a property on the SESSION rather
    // than on the view because two things have to agree with it: the toolbar's own toggle state, and
    // Escape — and Escape is handled in the view model, where every other cancel already lives.

    /// <summary>
    /// Whether the draw-a-wire tool is armed (§6.4): click a start foot, click an end foot, with a
    /// live ghost of the generated loop in between.
    ///
    /// <para><b>Mutually exclusive with the LAYOUT tools, in both directions.</b> One canvas has two
    /// tool sets over it, and the overlay gives an armed layout tool first refusal on every press
    /// (<c>LayoutToolArmed</c>) — so arming this while Rectangle was armed would produce a tool that
    /// looks armed and silently never sees a click. Arming it selects the Select tool; arming a layout
    /// tool disarms it (<see cref="LayoutEditorViewModel.OnActiveToolChanged"/>).</para>
    ///
    /// <para>Refused, rather than merely inert, on a layout with no wire design: there is nowhere to
    /// put a wire, and a toggle that stays down having done nothing is worse than one that will not go
    /// down. The toolbar button is hidden in that case anyway (it is gated exactly like the two panel
    /// buttons), so this is the belt to that braces.</para>
    /// </summary>
    [ObservableProperty] private bool _wireDrawArmed;

    partial void OnWireDrawArmedChanged(bool value)
    {
        if (value && WireOverlay is null) { WireDrawArmed = false; return; }

        if (value)
        {
            ActiveTool = Tool.Select;
            WireRotateArmed = false;   // one armed thing at a time, across BOTH tool sets
        }

        if (WireOverlay is { } overlay)
        {
            overlay.WireDrawArmed = value;
            overlay.NotifyChanged();
        }
    }

    /// <summary>
    /// Drops the WIRE selection, if this session has wires. The layout's own shapes and instances are
    /// <see cref="LayoutEditorViewModel"/>'s to clear; this is the half it cannot reach.
    /// </summary>
    private void ClearWireSelection()
    {
        if (WireEditor is not { } editor || editor.Selection.IsEmpty) return;

        editor.ClearSelection();
        WireOverlay?.NotifyChanged();
    }

    /// <summary>
    /// True while the wire layer has something Escape should cancel — either wire tool armed, or a
    /// wire half-placed (one foot down, waiting for the second click).
    /// </summary>
    private bool WireOperationInProgress =>
        WireDrawArmed || WireRotateArmed || WireOverlay?.IsPlacingWire == true;

    /// <summary>
    /// The wires the layout's own Rotate and Mirror should carry — flat indices, in
    /// <c>WBondDesign.AllWires</c> order, and empty for every layout that has none.
    /// </summary>
    internal IReadOnlyList<int> SelectedWireIndices =>
        WireEditor is { } editor ? [.. editor.Selection.TouchedWires()] : [];

    /// <summary>
    /// The extent of those wires in the layout's own DBU, or <see cref="Bbox.Empty"/> for none — so a
    /// rigid transform can pivot about the WHOLE selection rather than about the artwork half of it.
    /// </summary>
    private Bbox SelectedWireBbox(IReadOnlyList<int> wireIndices)
    {
        if (wireIndices.Count == 0 || WireEditor is not { } editor) return Bbox.Empty;

        var wires = editor.Design.AllWires().ToList();
        var bbox = Bbox.Empty;

        foreach (int index in wireIndices)
        {
            if (index < 0 || index >= wires.Count) continue;

            foreach (var p in wires[index].Points)
            {
                long x = WBondSnap.ToDbu(p.X, Model.DbuPerMicron);
                long y = WBondSnap.ToDbu(p.Y, Model.DbuPerMicron);
                bbox = bbox.Union(new Bbox(x, y, x, y));
            }
        }

        return bbox;
    }

    /// <summary>
    /// Whether the ANGLE-WIRE tool is armed: grab a wire near one end and swing it, with the opposite
    /// end anchored (WB26a).
    ///
    /// <para><b>The implementation was already here and had no way in</b> (owner, 2026-08-17: "there
    /// is probably already code for this that has been implemented, but we just have no UI entry
    /// point") — <c>WBondLayoutOverlay.WireRotateArmed</c> and its whole swing, pivot and 45°-snap
    /// path, reachable until now only from the wBond editor's own tool enum.</para>
    ///
    /// <para>Distinct from the layout's R, which turns the selection 90° as a rigid body exactly as it
    /// turns a rectangle. This is the one that makes an ANGLED wire: the pivot is the end further from
    /// the grab, so the gesture needs no mode switch beyond being armed.</para>
    /// </summary>
    [ObservableProperty] private bool _wireRotateArmed;

    partial void OnWireRotateArmedChanged(bool value)
    {
        if (value && WireOverlay is null) { WireRotateArmed = false; return; }

        if (value)
        {
            ActiveTool = Tool.Select;   // …and the layout's own tools stand down
            WireDrawArmed = false;      // …as does the other wire tool: one armed thing at a time
        }

        if (WireOverlay is { } overlay)
        {
            overlay.WireRotateArmed = value;
            overlay.NotifyChanged();
        }
    }

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

            // Settings ▸ Wirebonds ▸ Wire z-height. The layout view has no z axis for the user to
            // have meant anything by, so a drawn wire's feet take the setting — the same z a new
            // wBond's own wires are created at, which is the whole point of it being one setting
            // (owner, 2026-08-17). Read at attach: a preference change applies to the next document
            // opened, like every other creation default.
            FootZNm = WBondDefaults.FootZNm,

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
            if (e.PropertyName is nameof(SnapDbu) or nameof(DisplayUnit)
                               or nameof(GeometrySnapEnabled) or nameof(IncludeIntersectionsEnabled))
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

    /// <summary>
    /// Tells this session that its wire design was changed by something OUTSIDE the wire editor —
    /// today, Update Layout from Schematic merging an array the schematic has gained
    /// (<see cref="WBond.WBondCellSeeding"/>).
    ///
    /// <para><b>Why the mutation happens in place and this exists instead of a re-attach.</b> Attaching
    /// builds a whole new <see cref="WBondViewModel"/>, which throws away the wire undo history and the
    /// selection, and hands the view a different overlay object to bind to. For an edit that only
    /// APPENDS arrays that is far too big a hammer — so the merge mutates the design the editor already
    /// holds, and this rebuilds what depends on its structure.</para>
    ///
    /// <para><b>The selection is cleared, and that is not tidiness.</b> A wire selection is a set of
    /// FLAT indices across the whole design; realigning the array order (which the merge does, because
    /// array order is pin order) moves every wire's flat index, so a surviving selection would point at
    /// different wires than the ones the user picked. <c>WBondViewModel.Restore</c> clears it on a
    /// structural undo for exactly this reason.</para>
    ///
    /// <para><see cref="WBondViewModel.CommitStructuralChange"/> rebuilds the mesh and the incremental
    /// fill — a wire count change invalidates both — and raises <c>DirtyChanged</c>, which is what marks
    /// the layout dirty so the merged design is actually written on the next save.</para>
    /// </summary>
    /// <summary>
    /// Gives this session a wire layer if it has none, and returns the editor either way.
    ///
    /// <para><b>Pasting wires into a layout that has never held any is the case this exists for</b>
    /// (owner, 2026-08-17: <i>"I copied wires and pcells from a hosted layout and pasted them into a
    /// fresh .clay, but the wires did not get pasted in"</i>). The paste path required a wire editor to
    /// already be there and silently dropped the wire half when it was not — which is every ordinary
    /// layout, since a cell only has one once something has put wires in it.</para>
    ///
    /// <para><b>An EMPTY design, not the shipped default one.</b> The caller is about to add the wires
    /// it is carrying; seeding a default wire first would leave a spare one nobody asked for, in a
    /// group nobody named.</para>
    ///
    /// <para>This turns an ordinary layout into a WIREBOND CELL (WB40) — on the next save it gains a
    /// <c>.wBond</c> beside its <c>.clay</c>, and with it the wire toolbar buttons and the two panels.
    /// That is the honest consequence of putting wires in a layout, and it is REPORTED rather than
    /// left to be discovered.</para>
    /// </summary>
    /// <param name="note">What to tell the user when a layer is actually created; null to say nothing.</param>
    internal WBondViewModel? EnsureWireLayer(string? note = null)
    {
        if (WireEditor is { } existing) return existing;

        // Empty when the layout has never been saved — RetargetWiresForSaveAs fills it in at the first
        // save, exactly as it does for a palette drop onto a scratch document.
        string path = CurrentLayoutPath is { Length: > 0 } clay
            ? Path.ChangeExtension(clay, ".wBond")
            : "";

        AttachWireDesign(new WBondDesign(), path);
        MarkWiresDirty();

        if (note is { Length: > 0 }) _messageSink?.Info(note);

        WireLayerAdded?.Invoke();
        return WireEditor;
    }

    /// <summary>
    /// Marks the wire layer as holding unsaved edits. Needed by a caller that ATTACHED a design it
    /// just built rather than one it read from disk (the palette drop, WB40b): attaching alone leaves
    /// the session clean, so the wires would be on screen and absent from the file after a save.
    /// </summary>
    internal void MarkWiresDirty()
    {
        if (WireDesign is null) return;
        _wireDirty = true;
        IsDirty = true;
    }

    public void NotifyWireDesignChangedExternally()
    {
        if (WireEditor is not { } editor) return;

        editor.ClearSelection();
        editor.CommitStructuralChange();
        WireOverlay?.NotifyChanged();
    }

    private long GridPitchNm() => SnapDbu > 0 ? WBondSnap.ToNm(SnapDbu, Model.DbuPerMicron) : 0;

    /// <summary>
    /// Gives the wires this layout's snap settings and display unit — the grid both the overlay and the
    /// profile view draw, the two geometry-snap toggles, and the unit every wire readout is formatted
    /// in.
    ///
    /// <para><b>The two toggles were missing entirely</b> (owner, 2026-08-17: <i>"geometry snap toggle
    /// is not respected in the wBond layout host"</i>). The overlay's own defaults are ON and
    /// intersections-off, and nothing here ever wrote them, so <c>S</c>/<c>F3</c> and the Include
    /// Intersections toggle governed every shape in the view except the wires. There is one snap
    /// control set in this editor and it governs the wires too — the same rule the Snap box and the
    /// Unit box already follow.</para>
    /// </summary>
    private void PushLayoutSnapAndUnitToWires()
    {
        if (WireOverlay is { } overlay)
        {
            overlay.GridPitchNm = GridPitchNm();
            overlay.GeometrySnapEnabled = GeometrySnapEnabled;
            overlay.IncludeIntersections = IncludeIntersectionsEnabled;

            // The layout recomputes its marker on the toggle rather than waiting for the next pointer
            // move (RecomputeSnapStateImmediate, R-snp-7) — so the wire layer has to repaint on it too,
            // or a stale snap glyph sits on screen saying the toggle did nothing.
            overlay.NotifyChanged();
        }

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
    /// Follows the wires to a new <c>.clay</c> name when the layout is saved somewhere else (Save As,
    /// or a scratch layout written for the first time).
    ///
    /// <para><b>Why this is needed, and why it was not before 2026-08-17.</b> A <c>.wBond</c> is now an
    /// ATTACHMENT: it is found by sharing its <c>.clay</c>'s stem (WB40, revised). Under the old
    /// cell-root placement, Save As happened to keep working by accident — the sidecar was found by
    /// looking one level UP from the artwork, so <c>amp_v2.clay</c> resolved to the same
    /// <c>&lt;cell&gt;.wBond</c> as <c>amp_v1.clay</c> did. Stem pairing removes that accident: without
    /// this, Save As would write the artwork to <c>amp_v2.clay</c> and the wires back into
    /// <c>amp_v1.wBond</c>, and the layout the user just created would open with **no wires at all**
    /// while their edits sat in the old file.</para>
    ///
    /// <para><b>Save As COPIES, so the wires are copied too</b> — the original <c>.clay</c> and its
    /// <c>.wBond</c> are both left alone, and the new pair is complete. That is why the dirty flag is
    /// forced: a Save As with no wire edits at all must still produce wires at the new name, or the
    /// copy is silently missing the thing the cell is about.</para>
    ///
    /// <para><b>An ordinary Save never retargets</b>, which is what keeps a legacy cell-root file where
    /// it is. Migrating it silently on save would leave a stale duplicate at the root that then loses
    /// resolution to the new one — the move is the user's to make, and it is reported to them.</para>
    /// </summary>
    private void RetargetWiresForSaveAs(string? previousClayPath, string newClayPath)
    {
        if (WireDesign is null) return;

        if (previousClayPath is { Length: > 0 } old &&
            string.Equals(Path.GetFullPath(old), Path.GetFullPath(newClayPath),
                          StringComparison.OrdinalIgnoreCase))
            return;   // ordinary Save — nothing moved

        WireDesignPath = Path.ChangeExtension(newClayPath, ".wBond");
        _wireDirty     = true;
    }

    /// <summary>
    /// Raised when a save DELETED the <c>.wBond</c> sidecar because the layout no longer has any
    /// wires — carries the path that is now gone, so the project tree can drop the node.
    /// </summary>
    public event Action<string>? WireSidecarRemoved;

    /// <summary>
    /// Writes the wire layer back beside the <c>.clay</c>, if it has one and it changed — or
    /// <b>deletes it when the layout has no wires left</b>.
    ///
    /// <para>Called from <see cref="MarkSaved"/>, which every save path in the application funnels
    /// through — the workspace writes sub-cell sessions with a bare
    /// <c>LayoutPersistence.SaveToFile</c> rather than through this view model, so hooking any single
    /// writer would miss half of them. A failure is reported through the same
    /// <see cref="SaveError"/> seam a failed <c>.clay</c> write uses and does NOT clear the flag, so
    /// the next save tries again rather than the edits being quietly lost.</para>
    ///
    /// <h3>Why the empty design deletes the file rather than writing an empty one</h3>
    /// <para>Owner, 2026-08-17: <i>"I recommend that the .wBond file gets removed. This helps keep the
    /// workspace clean and it also helps the .clay not have the wBond-specific buttons in the toolbar
    /// on its next document open."</i> A file stating "no wires" is exactly a file that should not
    /// exist: <see cref="WBond.WBondCell"/> resolves a layout's wires by the file's PRESENCE, so an
    /// empty one leaves the cell a wirebond cell for ever — three toolbar buttons, two panels
    /// following it, and a DRC assembly section, all about wires there are none of.</para>
    ///
    /// <h3>Undo still works, and that is why this is on the SAVE and not on the delete</h3>
    /// <para>The owner's own concern (<i>"my only concern with that is that user must be able to
    /// perform undo/redo"</i>). Deleting the last wire does not detach anything: the session keeps its
    /// <see cref="WireEditor"/>, its overlay and — the part that matters — its wire undo history, so
    /// Ctrl+Z brings the wires back in memory, raises <c>DirtyChanged</c>, and the next save writes the
    /// file again. The file mirrors the SAVED state of the layout, which is the only state it has ever
    /// claimed to mirror; nothing is lost that was not already saved as absent.</para>
    /// </summary>
    private void SaveWireDesignIfDirty()
    {
        if (!_wireDirty) return;
        if (WireDesign is not { } design || WireDesignPath is not { Length: > 0 } path) return;

        bool removed = false;

        try
        {
            if (design.WireCount > 0)
            {
                WBondIo.WriteFile(path, design);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }
        }
        catch (Exception ex)
        {
            SaveError?.Invoke(design.WireCount > 0
                ? $"Couldn't save wirebonds to '{path}': {ex.Message}"
                : $"Couldn't remove the now-empty '{path}': {ex.Message}");
            return;
        }

        _wireDirty = false;

        if (!removed) return;

        // Said, not silent: a file disappearing from the user's cell folder is a thing they should be
        // able to read about afterwards, and the sentence also names what changes at the next open.
        _messageSink?.Info(
            $"Removed '{Path.GetFileName(path)}' — this layout has no wirebond wires left. " +
            "Its wire tools and panels will be absent the next time it is opened.");

        WireSidecarRemoved?.Invoke(path);
    }
}
