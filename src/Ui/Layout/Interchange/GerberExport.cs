// Gerber/Excellon export orchestrator (docs/sonnet-briefs/brief-L4c-gerber-export.md). Closes Phase L4
// — export only, no Gerber import/reader at all (§8, R-menu-5). Ties together LayoutDesignFlatten
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
    /// absolute cell directory, matching <see cref="LayoutDesignFlatten.FlattenResult.PendingCrossTechMappings"/>);
    /// pass null/empty on the first call. When <see cref="ExportPlan.RequiresMappingConfirmation"/> comes
    /// back true, the caller must resolve every pending entry (the SAME <c>LayerMappingDialog</c> paste/
    /// retarget/flatten already use) and call <see cref="Analyze"/> again with the merged dictionary.
    /// </summary>
    public static ExportPlan Analyze(
        string rootCellDir, Technology? rootTech, int dbuPerMicron, LayoutView rootView,
        Func<string?, string, TechResolution>? resolveTechAt,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>>? resolvedCrossTechMappings = null)
    {
        var flatten = LayoutDesignFlatten.Flatten(rootView, rootCellDir, rootTech, resolveTechAt, resolvedCrossTechMappings);

        var defaultFormat = new GerberFormat(GerberUnits.DefaultIntegerDigits, 6);

        if (flatten.ExceedsCeiling)
            return new ExportPlan(
                Shapes: [], Format: defaultFormat, CubicEdgesFlattened: 0, LabelsConvertedToGeometry: 0,
                PortLabelsOmitted: 0, BitmapsOmitted: 0, PathsAsStroke: 0, PathsAsRegion: 0,
                TopLevelInstancesFlattened: 0, ShapesContributedByFlatten: 0, UnresolvedInstances: [],
                PendingCrossTechMappings: flatten.PendingCrossTechMappings, ExceedsHierarchyCeiling: true,
                Diagnostics: [$"Flattening this design would exceed {LayoutDesignFlatten.HardCeiling:N0} shapes — refused. Nothing was changed."],
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

        var byLayer = GroupByLayer(geometry, rootTech);
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
        var drillLayers = DrillFunctionLayers(tech);
        if (drillLayers.Count == 0) return [];
        return geometry.OfType<CircleShape>().Where(c => drillLayers.Contains(c.Layer)).ToList();
    }

    /// <summary>Every drawing layer named in some <see cref="StackupKind.Via"/> entry — the one
    /// definition of "this layer means holes", shared by the bare-circle report and by the artwork
    /// grouping that must leave those circles out of the Gerber files.</summary>
    private static HashSet<LayerKey> DrillFunctionLayers(Technology? tech)
        => tech is null
            ? []
            : [.. tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).SelectMany(l => l.DrawingLayers)];

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

        // Two layers may name the same Gerber suffix — the technology's own problem, reported by
        // TechValidation — but the export must not silently write one layer's copper over another's.
        // A collision is disambiguated with the layer's own key and the file still written; both
        // files reach the fab, and the .gbrjob names both.
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The Excellon file's name is CLAIMED FIRST, before any layer can take it. It is not one more
        // suffix competing on equal terms: `.drl` is the conventional drill-file name, the .gbrjob
        // names it, and a fab looks for it — whereas a drawing layer whose GerberSuffix happens to be
        // "drl" has no such claim and can perfectly well be disambiguated. Reserving it also removes a
        // failure that was invisible on one platform and silent on the other: the layer loop wrote
        // `board.DRL`, the drill write then created `board.drl`, and on a case-insensitive filesystem
        // (macOS, Windows) the second CLOBBERED the first, so a whole layer's copper left the building
        // as a drill file. Found by L4h's round trip, which imported the surviving file and produced a
        // board missing one layer. (L4h §4: a writer change, named as such.)
        var vias = plan.Shapes.OfType<ViaShape>().ToList();
        var unpairedCircles = UnpairedDrillCircles(plan.Shapes, plan.Tech);
        bool writesDrill = vias.Count > 0 || unpairedCircles.Count > 0;
        string drillFileName = $"{cellName}.drl";
        if (writesDrill) usedFileNames.Add(drillFileName);

        foreach (var (key, shapes) in GroupByLayer(plan.Shapes, plan.Tech))
        {
            var layerDef = plan.Tech?.Layers.FirstOrDefault(l => l.Key == key);
            string suffix = layerDef?.Interchange?.GerberSuffix is { Length: > 0 } s ? s : $"G{key.Layer}_{key.Datatype}";
            string fileName = $"{cellName}.{suffix}";
            if (!usedFileNames.Add(fileName))
            {
                fileName = $"{cellName}.{suffix}_{key.Layer}_{key.Datatype}";
                usedFileNames.Add(fileName);
            }
            string path = Path.Combine(outputFolderDir, fileName);

            using (var stream = File.Create(path))
                GerberWriter.Write(stream, layerDef, shapes, plan.Format, plan.Tech, now);

            filesWritten.Add(path);
            jobEntries.Add(new GerberJobFile.FileAttribute(fileName, layerDef?.Interchange?.GerberFileFunction));
        }

        int tools = 0, hits = 0;
        if (writesDrill)
        {
            string drillPath = Path.Combine(outputFolderDir, drillFileName);
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

    /// <summary>
    /// One entry per layer that has artwork, <b>in the technology's own layer order</b> — layers the
    /// technology does not define come last, ordered by their key.
    ///
    /// <para><b>The order is the file set's order</b>: it is what the <c>.gbrjob</c> lists and the
    /// sequence the files are written in. It used to be <see cref="Dictionary{TKey,TValue}"/> insertion
    /// order, i.e. the order shapes happened to sit in the <c>.clay</c> — unspecified by contract, and
    /// in practice different for a design that was itself imported (an import adds shapes file by file,
    /// alphabetically). L4h's gate 17 is what named it: export2's job file has to match export1's, and
    /// with insertion order the same two-layer board listed its layers one way when drawn by hand and
    /// the other way after a round trip. Ordering by the technology is stable across that, and across
    /// any edit that reorders shapes.</para>
    /// </summary>
    private static List<(LayerKey Key, List<LayoutShape> Shapes)> GroupByLayer(
        IReadOnlyList<LayoutShape> shapes, Technology? tech)
    {
        // R-via-5's bare circles are HOLES, not copper, and belong only in the Excellon file. They used
        // to be written twice — a drill hit AND a filled disc in a Gerber file for the drill layer,
        // which a fab reads as copper to etch where the hole goes. L4h's round trip is what forced the
        // question: the disc came back as a pad, paired with its own hole into a via, and put a copper
        // landing on the top layer that the design never had, so the cycle did not close. Excluded here
        // rather than upstream so ExportPlan.Shapes still carries them and the fidelity dialog can go
        // on counting them (R-via-6's "Convert to Via" advice).
        var drillLayers = DrillFunctionLayers(tech);

        var map = new Dictionary<LayerKey, List<LayoutShape>>();
        foreach (var s in shapes)
        {
            if (s is CircleShape && drillLayers.Contains(s.Layer)) continue;
            var key = GerberLayerOf(s);
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(s);
        }

        var rank = new Dictionary<LayerKey, int>();
        if (tech is not null)
            for (int i = 0; i < tech.Layers.Count; i++) rank.TryAdd(tech.Layers[i].Key, i);

        return [.. map
            .OrderBy(kv => rank.TryGetValue(kv.Key, out int r) ? r : int.MaxValue)
            .ThenBy(kv => kv.Key.Layer)
            .ThenBy(kv => kv.Key.Datatype)
            .Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>
    /// Which layer's FILE a shape's artwork belongs in. Every shape but one answers with its own
    /// <see cref="LayoutShape.Layer"/>; a <see cref="ViaShape"/> answers with its
    /// <see cref="ViaShape.LandingLayer"/>, because what the writer emits for a via is its
    /// <see cref="ViaShape.PadSize"/> flash — copper — while <c>Layer</c> is the BARREL
    /// (<c>ViaShape</c>'s own doc comment, R-via-9), whose artwork is the drill file's hit and not a
    /// Gerber object at all.
    ///
    /// <para><b>Fixed here in L4h, on round-trip evidence (§4's "only with the failing cycle named").</b>
    /// Grouping the pad by <c>Layer</c> wrote copper into the DRILL layer's own Gerber file, which is
    /// wrong for fabrication on its face — a fab reading that file etches copper where the annular ring
    /// should be and the copper layer has no pad at all. The round trip is what made it undeniable:
    /// export wrote a copper file for the drill layer, the import identified that file as a second
    /// drill layer of its own, and export2 came back with one fewer file than export1 — so the file set
    /// was not closed after one cycle. Files exported before this change put via pads in the drill
    /// layer's file; files exported after it put them in the landing layer's, as
    /// brief-L4c-gerber-export.md's own §5 line ("a pad flash in copper") always said they should.</para>
    ///
    /// <para>A via carrying NO landing layer keeps the old answer, because there is then no copper
    /// layer to name — the pad has to go somewhere and the barrel's layer is the only one stated. The
    /// layout editor's Via tool always sets one; a hand-edited <c>.clay</c> need not.</para>
    /// </summary>
    private static LayerKey GerberLayerOf(LayoutShape shape)
        => shape is ViaShape { LandingLayer: { } landing } ? landing : shape.Layer;

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
