// The bottom-side view of a cell — brief-bsp-1 R-bsp1-6 (series overview D3).
//
// A bottom-side placement is resolved to a CLONE of the cell's view whose surface-role layers are
// swapped. The clone is cached per resolved view, so it is invalidated exactly when the resolver's own
// cache is (a reload hands back a new view, and the old one's entry goes with it), and the renderer's
// compiled-geometry cache — keyed on the view instance — gives the bottom view its own entry with no
// renderer change.
//
// Layers only. Geometry is never touched here: the reflection is the instance's MirrorX, applied by
// LayoutInstanceTransform as for any mirror, and putting a layer map into the transform would reach
// every coordinate walk in the repo.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Design.Layout;

internal static class PlacementSideViews
{
    private sealed record Entry(LayoutView View, IReadOnlyList<SurfaceRole> Omitted);

    /// <summary>Per source view, one bottom clone per distinct swap (<see cref="SurfaceRoles.Signature"/>)
    /// — keyed on the swap's VALUE rather than the technology instance, so the layout editor's draw
    /// technology (re-cloned whenever a layer is hidden) and the technology it came from share one
    /// clone instead of recompiling every bottom part on a visibility toggle.</summary>
    private static readonly ConditionalWeakTable<LayoutView, ConcurrentDictionary<string, Entry>> _bottom = new();

    /// <summary>Drops every bottom clone of <paramref name="view"/> — for the one view that is edited
    /// IN PLACE, a push-in session's live view (<see cref="CellLayoutResolver.SetLive"/>).</summary>
    public static void Invalidate(LayoutView view) => _bottom.Remove(view);

    public static CellLayoutResolution Bottom(LayoutInstance inst, CellLayoutResolution top, Technology? tech)
    {
        var source = top.View!;
        string who = inst.DisplayRefDes is { } rd ? rd : $"the placement of '{inst.CellRef}'";

        if (tech is null)
            return new CellLayoutResolution
            {
                State = CellLayoutState.Resolved, View = source, ResolvedCellDir = top.ResolvedCellDir,
                SideNotes = [$"{Capitalise(who)} is on the bottom, but no technology was resolved to find the bottom layers in, " +
                             "so it is drawn on the layers its cell draws on."],
            };

        var roles = SurfaceRoles.For(tech);
        if (!roles.HasBottomCopper)
            return new CellLayoutResolution
            {
                State = CellLayoutState.SideRefused, ResolvedCellDir = top.ResolvedCellDir, SourceView = source,
                Refusal = $"{Capitalise(who)} is on the bottom, but '{tech.Name}' has no bottom copper — " +
                          "there is nothing to solder it to.",
            };

        var entry = _bottom.GetOrCreateValue(source).GetOrAdd(roles.Signature, _ => Build(source, roles, tech));
        return new CellLayoutResolution
        {
            State = CellLayoutState.Resolved, View = entry.View, ResolvedCellDir = top.ResolvedCellDir,
            SourceView = source, OmittedRoles = entry.Omitted,
            SideNotes = [.. entry.Omitted.Select(r =>
                $"{Capitalise(who)} is on the bottom, but '{tech.Name}' has no bottom {SurfaceRoles.Describe(r)} layer; " +
                $"its {SurfaceRoles.Describe(r)} was omitted.")],
        };
    }

    private static Entry Build(LayoutView source, SurfaceRoles roles, Technology tech)
    {
        var clone = new LayoutView
        {
            DbuPerMicron = source.DbuPerMicron,
            DisplayUnit  = source.DisplayUnit,
            SnapDbu      = source.SnapDbu,
            AngleMode    = source.AngleMode,
            TechRef      = source.TechRef,
            PCellOrigin  = source.PCellOrigin,
            LoadFindings = source.LoadFindings,
        };
        foreach (var key in source.GroundViaKeys) clone.GroundViaKeys.Add(key);
        foreach (var kv in source.PCellSnapshots) clone.PCellSnapshots[kv.Key] = kv.Value;
        foreach (var kv in source.SchematicPCellSnapshots) clone.SchematicPCellSnapshots[kv.Key] = kv.Value;
        foreach (var kv in source.GeneratedPlacements) clone.GeneratedPlacements[kv.Key] = kv.Value;

        var omitted = new List<SurfaceRole>();
        void Omit(LayerKey key)
        {
            if (roles.OmittedRole(key) is { } r && !omitted.Contains(r)) omitted.Add(r);
        }

        foreach (var shape in source.Shapes)
        {
            if (roles.Swap(shape.Layer) is not { } layer) { Omit(shape.Layer); continue; }
            var c = LayoutGeometry.Clone(shape);
            c.Layer = layer;
            // A via's second layer and a port's committed conductor are layers too; a role with no
            // other side leaves them where they were rather than dropping the via or the port.
            if (c is ViaShape { LandingLayer: { } landing } via) via.LandingLayer = roles.Swap(landing) ?? landing;
            if (c is LabelShape { PortLayer: { } port } lbl) lbl.PortLayer = roles.Swap(port) ?? port;
            clone.Shapes.Add(c);
        }

        // A generated cell stores no pins and has its generator name them on demand — on the TOP
        // roles. Asked here, once, and stored swapped, or a bottom placement's pins would report the
        // top copper (R-bsp1-11).
        foreach (var pin in CellPins.Resolve(source, tech))
        {
            if (roles.Swap(pin.Layer) is not { } layer) { Omit(pin.Layer); continue; }
            clone.Pins.Add(new LayoutPin
            {
                Name = pin.Name, X = pin.X, Y = pin.Y, WidthDbu = pin.WidthDbu, OutwardDeg = pin.OutwardDeg, Layer = layer,
            });
        }

        // R-bsp1-2, applied where the walk will find it: the child is resolved in its turn, against
        // THIS clone, so its side here is already the composed one.
        foreach (var child in source.Instances)
        {
            var c = LayoutGeometry.Clone(child);
            c.Side = child.Side == PlacementSide.Top ? PlacementSide.Bottom : PlacementSide.Top;
            clone.Instances.Add(c);
        }

        omitted.Sort();
        return new Entry(clone, omitted);
    }

    private static string Capitalise(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
