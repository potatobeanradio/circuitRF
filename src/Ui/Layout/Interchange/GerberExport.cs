// Gerber/Excellon export orchestrator (docs/sonnet-briefs/brief-L4c-gerber-export.md). Closes Phase L4
// — export only, no Gerber import/reader at all (§8, R-menu-5). Ties together GerberHierarchyFlatten
// (R-L4c-6 — the whole design flattens, since Gerber has no hierarchy), label-to-geometry conversion
// (R-L4c-5, reusing the SAME stroked-font pipeline the label-flatten feature already built), per-layer
// grouping, and GerberWriter/ExcellonWriter/GerberJobFile. Analyze runs the exact same write path into
// Stream.Null (mirrors GdsiiExport/DxfExport's own "the dialog IS the real write, run as a dry run"),
// so the pre-flight fidelity dialog can never disagree with what Write actually produces.

using System.Linq;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Layout.Interchange;

public static class GerberExport
{
    /// <summary>R-L4c-7's pre-flight report — every count the export dialog states before anything is
    /// written. Deliberately does NOT include hole or arc counts: both are native to Gerber (a hole
    /// becomes a clear region, an arc becomes G02/G03) so neither is a lossy conversion worth flagging —
    /// only genuinely lossy/structural changes are listed, per the brief's own explicit item list.</summary>
    public sealed record ExportPlan(
        IReadOnlyList<LayoutShape> Shapes,
        GerberFormat Format,
        int CubicEdgesFlattened,
        int LabelsConvertedToGeometry,
        int PortLabelsOmitted,
        int BitmapsOmitted,
        int PathsAsStroke,
        int PathsAsRegion,
        int TopLevelInstancesFlattened,
        int ShapesContributedByFlatten,
        IReadOnlyList<string> UnresolvedInstances,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>> PendingCrossTechMappings,
        bool ExceedsHierarchyCeiling,
        IReadOnlyList<string> Diagnostics,
        Technology? Tech,
        /// <summary>R-via-5 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.2): bare
        /// <c>Circle</c>s drawn directly on a drill-function layer (a layer named in some
        /// <see cref="StackupKind.Via"/> entry's <c>DrawingLayers</c>) — the intuitive, and for MMIC
        /// genuinely correct (§1), way to draw a via. Each still contributes an Excellon hit (never
        /// refused, never silently dropped — R13a) but is unpaired: no matching pad, no annular-ring
        /// data. Reported here so the dialog can suggest <c>Convert to Via</c> (R-via-6).</summary>
        int UnpairedDrillCircles = 0)
    {
        public bool RequiresMappingConfirmation => PendingCrossTechMappings.Count > 0;

        public bool CanWrite => !ExceedsHierarchyCeiling && !RequiresMappingConfirmation;

        /// <summary>True when the format widened past the plain default (4 integer + 6 decimal digits,
        /// the DbuPerMicron=1000 case) — worth a line in the dialog per §6, silent otherwise.</summary>
        public bool FormatIsNonDefault => Format.IntegerDigits != GerberUnits.DefaultIntegerDigits || Format.DecimalDigits != 6;

        /// <summary>R-L4c-7: "when every count is zero, show nothing" — mirrors GDSII/DXF's identical
        /// rule so a dialog that always says something never trains users to dismiss it unread.</summary>
        public bool HasNothingToReport =>
            CanWrite &&
            CubicEdgesFlattened == 0 && TopLevelInstancesFlattened == 0 &&
            LabelsConvertedToGeometry == 0 && PortLabelsOmitted == 0 && BitmapsOmitted == 0 &&
            PathsAsRegion == 0 && UnresolvedInstances.Count == 0 && !FormatIsNonDefault &&
            UnpairedDrillCircles == 0;
    }

    public sealed record WriteResult(IReadOnlyList<string> FilesWritten, int DrillToolsDefined, int DrillHitsWritten);

    /// <summary>
    /// Walks and flattens <paramref name="rootView"/>'s whole hierarchy, converts labels to geometry,
    /// and computes the fidelity plan — no bytes written yet. <paramref name="resolvedCrossTechMappings"/>
    /// carries any prior <see cref="LayerMappingRow"/> confirmations (keyed by the resolved sub-cell's
    /// absolute cell directory, matching <see cref="GerberHierarchyFlatten.FlattenResult.PendingCrossTechMappings"/>);
    /// pass null/empty on the first call. When <see cref="ExportPlan.RequiresMappingConfirmation"/> comes
    /// back true, the caller must resolve every pending entry (the SAME <c>LayerMappingDialog</c> paste/
    /// retarget/flatten already use) and call <see cref="Analyze"/> again with the merged dictionary.
    /// </summary>
    public static ExportPlan Analyze(
        string rootCellDir, Technology? rootTech, int dbuPerMicron, LayoutView rootView,
        Func<string?, string, TechResolution>? resolveTechAt,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>>? resolvedCrossTechMappings = null)
    {
        var flatten = GerberHierarchyFlatten.Flatten(rootView, rootCellDir, rootTech, resolveTechAt, resolvedCrossTechMappings);

        var defaultFormat = new GerberFormat(GerberUnits.DefaultIntegerDigits, 6);

        if (flatten.ExceedsCeiling)
            return new ExportPlan(
                Shapes: [], Format: defaultFormat, CubicEdgesFlattened: 0, LabelsConvertedToGeometry: 0,
                PortLabelsOmitted: 0, BitmapsOmitted: 0, PathsAsStroke: 0, PathsAsRegion: 0,
                TopLevelInstancesFlattened: 0, ShapesContributedByFlatten: 0, UnresolvedInstances: [],
                PendingCrossTechMappings: flatten.PendingCrossTechMappings, ExceedsHierarchyCeiling: true,
                Diagnostics: [$"Flattening this design would exceed {GerberHierarchyFlatten.HardCeiling:N0} shapes — refused. Nothing was changed."],
                Tech: rootTech);

        if (flatten.PendingCrossTechMappings.Count > 0)
            return new ExportPlan(
                Shapes: [], Format: defaultFormat, CubicEdgesFlattened: 0, LabelsConvertedToGeometry: 0,
                PortLabelsOmitted: 0, BitmapsOmitted: 0, PathsAsStroke: 0, PathsAsRegion: 0,
                TopLevelInstancesFlattened: flatten.TopLevelInstancesFlattened,
                ShapesContributedByFlatten: flatten.ShapesContributedByInstances,
                UnresolvedInstances: flatten.UnresolvedInstances,
                PendingCrossTechMappings: flatten.PendingCrossTechMappings, ExceedsHierarchyCeiling: false,
                Diagnostics: [], Tech: rootTech);

        int labelsConverted = 0, portLabelsOmitted = 0, bitmapsOmitted = 0;
        var geometry = new List<LayoutShape>(flatten.Shapes.Count);

        foreach (var shape in flatten.Shapes)
        {
            switch (shape)
            {
                case BitmapShape:
                    bitmapsOmitted++; // §3.1b R10e
                    break;

                case LabelShape label when label.IsPort:
                    portLabelsOmitted++; // R-L4c-5: port labels are markers, not artwork
                    break;

                case LabelShape label:
                {
                    var contours = LayoutTextOutline.BuildGlyphContours(label);
                    long tol = LayoutFlattener.ResolveTolDbu(label, rootTech);
                    var polygons = LayoutTextFlatten.FlattenContoursToPolygons(contours, tol, label.Layer, label.Net);
                    if (polygons.Count > 0) labelsConverted++;
                    geometry.AddRange(polygons);
                    break;
                }

                default:
                    geometry.Add(shape);
                    break;
            }
        }

        long maxAbsCoord = MaxAbsCoordinateDbu(geometry);
        GerberFormat format;
        try
        {
            format = GerberUnits.Resolve(dbuPerMicron, maxAbsCoord);
        }
        catch (GerberUnitsException ex)
        {
            return new ExportPlan(
                Shapes: [], Format: defaultFormat, CubicEdgesFlattened: 0, LabelsConvertedToGeometry: labelsConverted,
                PortLabelsOmitted: portLabelsOmitted, BitmapsOmitted: bitmapsOmitted, PathsAsStroke: 0, PathsAsRegion: 0,
                TopLevelInstancesFlattened: flatten.TopLevelInstancesFlattened,
                ShapesContributedByFlatten: flatten.ShapesContributedByInstances,
                UnresolvedInstances: flatten.UnresolvedInstances,
                PendingCrossTechMappings: flatten.PendingCrossTechMappings, ExceedsHierarchyCeiling: true,
                Diagnostics: [ex.Message], Tech: rootTech);
        }

        var byLayer = GroupByLayer(geometry);
        var now = DateTime.UtcNow;
        int cubics = 0, strokes = 0, regions = 0;
        foreach (var (key, shapes) in byLayer)
        {
            var layerDef = rootTech?.Layers.FirstOrDefault(l => l.Key == key);
            var result = GerberWriter.Write(Stream.Null, layerDef, shapes, format, rootTech, now);
            cubics += result.CubicEdgesFlattened;
            strokes += result.PathsAsStroke;
            regions += result.PathsAsRegion;
        }

        var vias = geometry.OfType<ViaShape>().ToList();
        var unpairedCircles = UnpairedDrillCircles(geometry, rootTech);
        if (vias.Count > 0 || unpairedCircles.Count > 0)
            ExcellonWriter.Write(Stream.Null, vias, format, unpairedCircles);

        return new ExportPlan(
            geometry, format, cubics, labelsConverted, portLabelsOmitted, bitmapsOmitted, strokes, regions,
            flatten.TopLevelInstancesFlattened, flatten.ShapesContributedByInstances,
            flatten.UnresolvedInstances, flatten.PendingCrossTechMappings, false,
            [], rootTech, unpairedCircles.Count);
    }

    /// <summary>R-via-5: a drill-function layer is any layer named in a <see cref="StackupKind.Via"/>
    /// entry's <c>DrawingLayers</c> — the same identity the Via tool's own enablement check
    /// (<c>LayoutEditorViewModel.ViaToolAvailability</c>) uses, applied here to find bare Circles that
    /// were drawn on it instead of placed as a real <see cref="ViaShape"/>.</summary>
    private static List<CircleShape> UnpairedDrillCircles(IReadOnlyList<LayoutShape> geometry, Technology? tech)
    {
        if (tech is null) return [];
        var drillLayers = new HashSet<LayerKey>(
            tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).SelectMany(l => l.DrawingLayers));
        if (drillLayers.Count == 0) return [];
        return geometry.OfType<CircleShape>().Where(c => drillLayers.Contains(c.Layer)).ToList();
    }

    /// <summary>Writes the real file set into <paramref name="outputFolderDir"/> — one Gerber file per
    /// layer (named <c>{cellName}.{GerberSuffix}</c>, falling back to a synthetic suffix when a layer
    /// carries no <c>.ctech</c> Gerber mapping), one Excellon drill file when the design has any
    /// <see cref="ViaShape"/> (§5: a single plated file, since <c>InterchangeMapping</c> carries no
    /// plated/non-plated distinction to split on), and one <c>.gbrjob</c> (R-L4c-2).</summary>
    public static WriteResult Write(string outputFolderDir, string cellName, ExportPlan plan)
    {
        if (!plan.CanWrite)
            throw new InvalidOperationException("Gerber export is blocked — resolve the pending item(s) first.");

        Directory.CreateDirectory(outputFolderDir);
        var filesWritten = new List<string>();
        var jobEntries = new List<GerberJobFile.FileAttribute>();
        var now = DateTime.UtcNow;

        foreach (var (key, shapes) in GroupByLayer(plan.Shapes))
        {
            var layerDef = plan.Tech?.Layers.FirstOrDefault(l => l.Key == key);
            string suffix = layerDef?.Interchange?.GerberSuffix is { Length: > 0 } s ? s : $"G{key.Layer}_{key.Datatype}";
            string fileName = $"{cellName}.{suffix}";
            string path = Path.Combine(outputFolderDir, fileName);

            using (var stream = File.Create(path))
                GerberWriter.Write(stream, layerDef, shapes, plan.Format, plan.Tech, now);

            filesWritten.Add(path);
            jobEntries.Add(new GerberJobFile.FileAttribute(fileName, layerDef?.Interchange?.GerberFileFunction));
        }

        int tools = 0, hits = 0;
        var vias = plan.Shapes.OfType<ViaShape>().ToList();
        var unpairedCircles = UnpairedDrillCircles(plan.Shapes, plan.Tech);
        if (vias.Count > 0 || unpairedCircles.Count > 0)
        {
            string drillPath = Path.Combine(outputFolderDir, $"{cellName}.drl");
            using (var stream = File.Create(drillPath))
            {
                var r = ExcellonWriter.Write(stream, vias, plan.Format, unpairedCircles);
                tools = r.ToolsDefined;
                hits = r.HitsWritten;
            }
            filesWritten.Add(drillPath);
        }

        string jobPath = Path.Combine(outputFolderDir, $"{cellName}.gbrjob");
        using (var stream = File.Create(jobPath))
            GerberJobFile.Write(stream, jobEntries, now, GerberWriter.Version);
        filesWritten.Add(jobPath);

        return new WriteResult(filesWritten, tools, hits);
    }

    private static Dictionary<LayerKey, List<LayoutShape>> GroupByLayer(IReadOnlyList<LayoutShape> shapes)
    {
        var map = new Dictionary<LayerKey, List<LayoutShape>>();
        foreach (var s in shapes)
        {
            if (!map.TryGetValue(s.Layer, out var list)) map[s.Layer] = list = [];
            list.Add(s);
        }
        return map;
    }

    /// <summary>The largest coordinate MAGNITUDE across every shape's own defining fields — mirrors
    /// <c>GdsiiCoordinateValidation</c>'s per-shape field walk, but computes a max rather than validating
    /// a fixed range, since Gerber's integer-digit count simply widens to fit (R-L4c-1) rather than
    /// refusing. By the time this runs, <c>geometry</c> never contains a <c>LabelShape</c> (already
    /// converted to polygons) or <c>BitmapShape</c> (already filtered).</summary>
    private static long MaxAbsCoordinateDbu(IReadOnlyList<LayoutShape> shapes)
    {
        long max = 0;
        void Check(long v) { long a = Math.Abs(v); if (a > max) max = a; }

        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case RectShape r: Check(r.X1); Check(r.Y1); Check(r.X2); Check(r.Y2); break;
                case PolygonShape p:
                    foreach (var v in p.Xy) Check(v);
                    if (p.Holes is not null) foreach (var h in p.Holes) foreach (var v in h) Check(v);
                    break;
                case RoundedRectShape rr: Check(rr.X1); Check(rr.Y1); Check(rr.X2); Check(rr.Y2); break;
                case CircleShape c: Check(c.Cx - c.R); Check(c.Cx + c.R); Check(c.Cy - c.R); Check(c.Cy + c.R); break;
                case CurveShape curve:
                    foreach (var v in curve.Xy) Check(v);
                    CheckCubicControls(curve.Edges, Check);
                    if (curve.Holes is not null) foreach (var h in curve.Holes) foreach (var v in h) Check(v);
                    break;
                case PathShape path:
                    foreach (var v in path.Xy) Check(v);
                    CheckCubicControls(path.Edges, Check);
                    Check(path.Width);
                    break;
                case ViaShape via: Check(via.X); Check(via.Y); Check(via.PadSize); break;
            }
        }
        return max;
    }

    private static void CheckCubicControls(List<LayoutEdge>? edges, Action<long> check)
    {
        if (edges is null) return;
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) continue;
            check(e.C1X); check(e.C1Y); check(e.C2X); check(e.C2Y);
        }
    }
}
