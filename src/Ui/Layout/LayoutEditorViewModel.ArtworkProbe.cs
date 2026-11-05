using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Create Schematic from Artwork as the layout canvas sees it (brief-artsch-8-gui-command.md): the PROBE — the part
/// picked in the dialog's parts table, or the anchor a cross-probe followed, drawn over the artwork — the selection's
/// outline, which is the dialog's Selection scope, and the one-shot point pick its Ground option arms.
///
/// <para>Nothing here writes the layout (overview D5): the probe is overlay state, the pick hands a point back to
/// whoever armed it, and selecting the copper under an anchor is an ordinary selection change.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    private IReadOnlyList<long[]> _artworkProbe = [];

    /// <summary>The rings the probe draws, DBU; empty when nothing is probed.</summary>
    public IReadOnlyList<long[]> ArtworkProbe => _artworkProbe;

    /// <summary>
    /// Draws <paramref name="rings"/> over the artwork and brings <paramref name="focus"/> on screen: panned to at the
    /// current zoom when <paramref name="zoom"/> is false (the parts table — R-as8-3's "pans to it if it is off
    /// screen"), framed when it is true (a cross-probe — R-as8-5's "selects and zooms").
    /// </summary>
    public void ShowArtworkProbe(IReadOnlyList<long[]> rings, Bbox focus, bool zoom)
    {
        ArgumentNullException.ThrowIfNull(rings);
        _artworkProbe = rings;
        RebuildOverlay();
        if (focus.IsEmpty) return;
        if (zoom) RequestZoomToRegion(focus);
        else RequestRevealRegion(focus);
    }

    /// <summary>Takes the probe off the artwork.</summary>
    public void ClearArtworkProbe()
    {
        if (_artworkProbe.Count == 0) return;
        _artworkProbe = [];
        RebuildOverlay();
    }

    /// <summary>
    /// The selection as rings, DBU — the region Create Schematic from Artwork reads under its Selection scope (D11):
    /// a filled shape's own outline, anything else's box, and a placed cell's box. Empty when nothing is selected.
    /// </summary>
    public IReadOnlyList<long[]> SelectionOutline()
    {
        var rings = new List<long[]>();
        foreach (int i in _selectedIndices)
        {
            if (i < 0 || i >= Model.Shapes.Count) continue;
            var shape = Model.Shapes[i];
            if (shape is RectShape or PolygonShape or CircleShape or RoundedRectShape or CurveShape)
                rings.Add(LayoutFlattener.Flatten(shape, Math.Max(1, Model.DbuPerMicron))[0]);
            else
                rings.Add(Ring(LayoutGeometry.BboxOf(shape)));
        }
        foreach (int i in _selectedInstanceIndices)
            if (i >= 0 && i < Model.Instances.Count && CellHierarchy.InstanceBbox(Model.Instances[i], InstanceBaseDir) is { IsEmpty: false } box)
                rings.Add(Ring(box));
        return rings;
    }

    /// <summary>
    /// The layers the selection is on — each selected shape's, and the copper of each selected part as it is placed
    /// (a bottom-side part's pads are on the bottom). Create Schematic from Artwork reads copper on any other layer
    /// only where a via joins it to these (designer report, round 25).
    /// </summary>
    public IReadOnlyList<LayerKey> SelectionLayers()
    {
        var layers = new HashSet<LayerKey>();
        foreach (int i in _selectedIndices)
        {
            if (i < 0 || i >= Model.Shapes.Count) continue;
            layers.Add(Model.Shapes[i].Layer);
            if (Model.Shapes[i] is ViaShape { LandingLayer: { } landing }) layers.Add(landing);
        }
        foreach (int i in _selectedInstanceIndices)
            if (i >= 0 && i < Model.Instances.Count) AddPlacedLayers(Model.Instances[i], InstanceBaseDir, 0);
        return [.. layers];

        // A module's parts are placements inside it: their copper is the module's too. The resolved view of a
        // bottom placement is the side-swapped one, and its own placements have that side composed in already.
        void AddPlacedLayers(LayoutInstance inst, string baseDir, int depth)
        {
            if (depth >= CellHierarchy.MaxDepth) return;
            if (CellLayoutResolver.Resolve(inst, baseDir, Technology) is not
                { State: CellLayoutState.Resolved, View: { } cell, ResolvedCellDir: { } cellDir }) return;
            foreach (var shape in cell.Shapes)
            {
                layers.Add(shape.Layer);
                if (shape is ViaShape { LandingLayer: { } landing }) layers.Add(landing);
            }
            string subBaseDir = CellHierarchy.LayoutBaseDirOf(cellDir);
            foreach (var sub in cell.Instances) AddPlacedLayers(sub, subBaseDir, depth + 1);
        }
    }

    /// <summary>
    /// R-as8-5: selects the copper at (<paramref name="x"/>, <paramref name="y"/>) — the smallest top-level shape whose
    /// box holds the point, on <paramref name="layer"/> where one is named — and zooms to it. False when nothing there
    /// is a shape of this layout (copper inside a placed cell, say), in which case the selection is left alone.
    /// </summary>
    public bool SelectArtworkAt(long x, long y, LayerKey? layer = null)
    {
        int best = -1;
        double bestArea = double.MaxValue;
        for (int i = 0; i < Model.Shapes.Count; i++)
        {
            var shape = Model.Shapes[i];
            if (layer is { } l && shape.Layer != l) continue;
            var box = LayoutGeometry.BboxOf(shape);
            if (!box.Contains(x, y)) continue;
            double area = (double)(box.MaxX - box.MinX) * (box.MaxY - box.MinY);
            if (area < bestArea) { bestArea = area; best = i; }
        }
        if (best < 0) return false;
        _cycleCache.Clear();
        SetSelection([best]);
        RequestZoomToRegion(LayoutGeometry.BboxOf(Model.Shapes[best]));
        return true;
    }

    /// <summary>
    /// brief-dfr35-1 R-dfr35-1-8 — a place a mesher failure named, in micrometres: selects the smallest shape holding it and frames
    /// the place itself, since that shape is often a whole pour. The frame is several times what is there, so the place has context.
    /// </summary>
    public void ShowPlace(double xUm, double yUm, double spanUm)
    {
        long x = (long)Math.Round(xUm * Model.DbuPerMicron), y = (long)Math.Round(yUm * Model.DbuPerMicron);
        long reach = (long)Math.Round(Math.Max(spanUm * 2, 20) * Model.DbuPerMicron);
        SelectArtworkAt(x, y);
        RequestZoomToRegion(new Bbox(x - reach, y - reach, x + reach, y + reach));
    }

    // ── the one-shot point pick (the dialog's Ground ▸ Pick on layout) ──────────────────────────────

    private Action<long, long>? _artworkPointPick;

    /// <summary>True while a point pick is armed.</summary>
    public bool IsPickingArtworkPoint => _artworkPointPick is not null;

    /// <summary>
    /// Arms the pick: the next press on the canvas, whatever tool is active, is handed to <paramref name="onPicked"/>
    /// as a DBU point and disarms. RP-2b's return-conductor pick and the EM region drag are the precedent; Escape
    /// puts it back.
    /// </summary>
    public void ArmArtworkPointPick(string prompt, Action<long, long> onPicked)
    {
        ArgumentNullException.ThrowIfNull(onPicked);
        _artworkPointPick = onPicked;
        OnPropertyChanged(nameof(IsPickingArtworkPoint));
        ReportMessage(prompt);
    }

    /// <summary>Escape, or a completed pick.</summary>
    public void CancelArtworkPointPick()
    {
        if (_artworkPointPick is null) return;
        _artworkPointPick = null;
        OnPropertyChanged(nameof(IsPickingArtworkPoint));
    }

    private void ArtworkPointPress(double wx, double wy)
    {
        if (_artworkPointPick is not { } onPicked) return;
        CancelArtworkPointPick();
        onPicked((long)Math.Round(wx), (long)Math.Round(wy));
    }

    private static long[] Ring(Bbox b) => [b.MinX, b.MinY, b.MaxX, b.MinY, b.MaxX, b.MaxY, b.MinX, b.MaxY];
}
