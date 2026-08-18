using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Dropping a <b>wBond</b> out of the Library Palette onto a layout (owner, 2026-08-17: <i>"Cannot
/// drag and drop a wBond component from the Library Palette into a layout. User should be able to do
/// that and then start editing wires in layout."</i>).
///
/// <h3>Why it needed its own path at all</h3>
/// <para>Every other palette drop resolves a PCELL GENERATOR and places the generated cell as an
/// instance (<see cref="LayoutEditorViewModel.CommitPaletteDrop"/>). A wBond has no generator and
/// never will — WB23 is explicit that no wire enters a <c>.clay</c> — so the drag-over handler
/// correctly said "no" and there was nothing behind it to say "yes" to instead. What a dropped wBond
/// produces is not a shape: it is this session's WIRE LAYER, the same one a cell with a <c>.wBond</c>
/// beside its <c>.clay</c> arrives with (WB40).</para>
///
/// <h3>What it produces</h3>
/// <list type="bullet">
///   <item>On a layout with no wires: the shipped one-array, one-wire design
///     (<see cref="WBondEmbedding.DefaultDesign"/> — the same definition a freshly-placed schematic
///     component and a blank wBond editor both start from), moved so its input foot lands where the
///     drop did, attached to this session.</item>
///   <item>On a layout that ALREADY carries wires: one more array with one wire in it, at the drop
///     point. A second wire LAYER is not a thing a cell can have (one <c>.wBond</c> per <c>.clay</c>),
///     so the honest reading of "drop another wBond here" is another group of wires — and it is never
///     a refusal of a gesture the cursor had already accepted.</item>
/// </list>
///
/// <h3>The file is written by the ordinary save, not here</h3>
/// <para>Attaching marks the session dirty and <c>SaveWireDesignIfDirty</c> writes the sidecar beside
/// the <c>.clay</c> on the next save, exactly as it does for a wire the user drew. That is also what
/// makes the drop work on a SCRATCH layout, which has no path yet: the wires ride along to whatever
/// name the first save gives it (<c>RetargetWiresForSaveAs</c>), rather than the drop having to refuse
/// an unsaved document.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// Raised when this session has just GAINED a wire layer it did not have — a palette drop, or a
    /// paste that carried wires — so the shell can show the two wBond panels, the same courtesy
    /// Update Layout from Schematic already does on a first write.
    ///
    /// <para>Never for a layout that simply OPENED with wires: a panel that reappears every time you
    /// open a cell is one you have to close every time.</para>
    /// </summary>
    public event Action? WireLayerAdded;

    /// <summary>
    /// True when a wBond tile can be dropped on this layout — which is always. The gesture needs no
    /// generator, no workspace and no saved path; see the class remarks for the last of those.
    /// </summary>
    public bool CanDropWBond() => true;

    /// <summary>
    /// Commits a wBond drop at (<paramref name="xDbu"/>, <paramref name="yDbu"/>), the canvas's own
    /// already-snapped world point.
    /// </summary>
    /// <returns>True when wires were added.</returns>
    public bool CommitWBondDrop(long xDbu, long yDbu)
    {
        CancelPaletteDragGhost();

        long xNm = WBondSnap.ToNm(xDbu, Model.DbuPerMicron);
        long yNm = WBondSnap.ToNm(yDbu, Model.DbuPerMicron);

        return WireEditor is null ? AttachDroppedWBond(xNm, yNm) : AddDroppedArray(xNm, yNm);
    }

    /// <summary>The first drop: build the design, move it under the cursor, attach it.</summary>
    private bool AttachDroppedWBond(long xNm, long yNm)
    {
        var design = WBondEmbedding.DefaultDesign(WBondDefaults.FootZNm);
        TranslateInPlace(design, xNm, yNm);

        // Empty rather than guessed when the layout has never been saved. WireDesignPath is what
        // SaveWireDesignIfDirty writes to, and RetargetWiresForSaveAs fills it in at the first save —
        // inventing a path for a scratch document would write wires into a folder the artwork is not
        // going to land in.
        string path = CurrentLayoutPath is { Length: > 0 } clay
            ? Path.ChangeExtension(clay, ".wBond")
            : "";

        AttachWireDesign(design, path);

        // Attaching a design read from disk is not an edit; attaching one the user just made is.
        // Without this the wires would be on screen and absent from the file after a save.
        MarkWiresDirty();

        _messageSink?.Info(
            "Wirebond wires added to this layout. Draw more with the Wire tool (W); they are saved " +
            "beside the layout as its .wBond when you save.");

        WireLayerAdded?.Invoke();
        return true;
    }

    /// <summary>A later drop: one more array, one more wire, at the point.</summary>
    private bool AddDroppedArray(long xNm, long yNm)
    {
        if (WireEditor is not { } editor) return false;

        long footZ = WBondDefaults.FootZNm;
        var start = WBondEmbedding.DefaultWire.StartAt(footZ);
        var end = WBondEmbedding.DefaultWire.EndAt(footZ);

        // An array name no existing array uses, which is what makes this "another group of wires"
        // rather than "one more wire in the group you already had". This used to be expressed by
        // inventing a uniquely-named throwaway LOOP PROFILE, because profile identity was what
        // decided which array a new wire joined; asking for the array by name is what it always meant.
        int index = editor.AddWire(
            new Point3(start.X + xNm, start.Y + yNm, start.Z),
            new Point3(end.X + xNm, end.Y + yNm, end.Z),
            WBondUnits.ToNm(WBondEmbedding.DefaultWire.DiameterMils, WBondUnit.Mil),
            WireMaterials.Default.Name,
            arrayName: editor.NextArrayName(),
            points: WBondDefaults.Points,
            loopHeightNm: WBondUnits.ToNm(WBondEmbedding.DefaultWire.LoopHeightMils, WBondUnit.Mil));

        if (index < 0) return false;

        WireOverlay?.NotifyChanged();
        _messageSink?.Info($"Added wire array '{editor.GroupNameOfWire(index)}' at the drop point.");

        WireLayerAdded?.Invoke();
        return true;
    }

    /// <summary>Moves every wire in <paramref name="design"/> by (dx, dy), leaving z alone.</summary>
    private static void TranslateInPlace(WBondDesign design, long dxNm, long dyNm)
    {
        foreach (var wire in design.AllWires())
            for (int i = 0; i < wire.Points.Count; i++)
            {
                var p = wire.Points[i];
                wire.Points[i] = new Point3(p.X + dxNm, p.Y + dyNm, p.Z);
            }
    }
}
