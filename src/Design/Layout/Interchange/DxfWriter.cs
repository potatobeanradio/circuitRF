// DXF (ASCII, AC1015/R2000) writer (docs/sonnet-briefs/brief-L4b-dxf-interchange.md). Streams to a
// TextWriter — one HEADER/TABLES/BLOCKS/ENTITIES section, entities written as they're built.
// Format-specific: touches only text/groups, never CellFolder/Messages/dialogs — that orchestration
// lives in DxfExport. Written from the public DXF group-code specification — no DXF library
// dependency, never ingests GPL sources.
//
// §1.1 — the headline fact this brief opens with: LayoutEdge.Bulge and LWPOLYLINE's bulge (group 42)
// are the SAME quantity, tan(sweep/4). An arc edge exports by COPYING THE NUMBER — no conversion.
//
// Ring representation choice (this file's own design decision, since the brief leaves the exact
// entity choice per edge-kind combination to the implementer):
//   - No holes, no Cubic edge anywhere  -> LWPOLYLINE, bulge per vertex (0 for Line, tan(sweep/4) for
//     Arc) — exact for Line/Arc in any combination, per §1.1.
//   - No holes, has a Cubic edge, NO Arc edge -> one closed multi-segment SPLINE entity (a "Bezier
//     chain": degree 3, non-rational, clamped knot vector with each interior knot repeated 3x) — every
//     edge (Line degenerate-elevated, Cubic as-is) becomes one exact Bezier segment; §1.2's "a cubic
//     Bezier IS a degree-3 non-rational B-spline with knots [0,0,0,0,1,1,1,1]" extended to a chain of
//     segments sharing endpoints.
//   - No holes, has BOTH an Arc edge AND a Cubic edge in the SAME ring -> LWPOLYLINE with the Cubic
//     edge(s) flattened to line segments (an approximation, reported) — LWPOLYLINE cannot carry a
//     cubic vertex and a standalone SPLINE cannot carry a circular arc exactly either; this narrow
//     combination is the one case genuinely not representable by either single-entity form. Reported,
//     never silent.
//   - Holes present (§3.1a) -> HATCH, one boundary loop per ring (outer first, then each hole — Clipper2
//     tree order, mirrors GdsiiWriter's keyhole precedent but HATCH expresses holes NATIVELY, no
//     keyholing needed): a loop with any Arc/Cubic edge uses HATCH's "edge" boundary type (per-edge
//     Line(1)/Arc(2)/Spline(4) sub-edges — this is the one DXF mechanism that mixes edge kinds exactly
//     in one loop); an all-Line loop (always true for a hole) uses the lighter "polyline" boundary type.
//
// R-L4b-1 — arc edges must NEVER be flattened. R-L4b-2 — mirror is xscale=-1, not a flag; DXF's INSERT
// transform order (scale, then rotate, then translate) already matches LayoutInstanceTransform's own
// "negate local X before rotation" convention, so the mapping is DIRECT — no reflect-then-rotate-180
// trick like GDSII's STRANS needed (see DxfTransformCodec's own header comment). R-L4b-5 — bitmaps
// never contribute to extents. R-L4b-6 — fitting errs toward showing too much.
//
// ── Owner report, 2026-07-28: a real DXF exported by this writer would not open in QCAD, the AutoDesk
// web viewer, or eDrawings — confirmed directly, not guessed, using two independent real parsers run
// against the ORIGINAL (pre-fix) output of this file: `ezdxf.readfile` failed with "missing
// 'AcDbPolyline' subclass in LWPOLYLINE"; QCAD's own bundled ODA-based `dwginfo`/`dwg2svg` tools failed
// identically with "Bad Dxf sequence". Root cause: this file declared `$ACADVER = AC1015` (AutoCAD
// 2000/R2000) while writing entities in the STRUCTURE of the much older, simpler R12 format — no
// handles (group 5), no owner pointers (group 330), and no "subclass marker" groups (code 100,
// `AcDbEntity` / `AcDb<Class>`), all of which the R13+ file format MANDATES for every table record,
// block, and entity. This project's OWN `DxfReader` never noticed, because it dispatches purely by the
// leading `0 <TYPE>` token and ignores any group code it doesn't specifically look for (5/330/100
// included) — exactly the "correct by our own reader's standards" trap this brief's own completion note
// already names once for the HATCH boundary-flag bug. Fixed by writing every table, block, and entity
// with a real, unique handle, correct owner pointers, and the required subclass markers — verified
// directly against `ezdxf` and QCAD's `dwginfo`/`dwg2svg` (not merely against this project's own
// reader) before considering this closed. A `DxfHandles` counter (starting past the low,
// convention-reserved handle values) assigns every handle; `*Model_Space`/`*Paper_Space` are now real
// (empty) placeholder blocks, exactly as every AC1015+ file has them, with actual top-level content
// living in ENTITIES as before, owned by `*Model_Space`'s own `BLOCK_RECORD` handle.
//
// A SECOND, latent bug shared the same root cause and was caught by the same fix: `DxfReader`
// (unchanged by this fix) treats every `BLOCK` token in the BLOCKS section as an importable structure —
// but `*Model_Space`/`*Paper_Space` (and any anonymous `*U#`/`*D#`/`*X#` block AutoCAD itself generates
// for hatches, dimensions, and external references) are SYSTEM blocks, never user cells. Before this
// fix, our own writer never emitted them, so this never fired — but importing almost ANY real-world DXF
// (gate 12) would have created bogus empty "*Model_Space"/"*Paper_Space" cells. `DxfReader.
// ParseBlocksSection` now skips any block whose name starts with `*`, the universal DXF convention for
// an anonymous/system block.

using CircuitRF.Design.Theming;
using CircuitRF.WBond;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What actually happened during a write — mirrors <c>GdsiiExportSummary</c>'s shape so the
/// export dialog's pre-flight plan can show identical categories across both formats.</summary>
public sealed record DxfExportSummary(
    int CurvedShapesWritten,
    int HolesAsHatch,
    int BitmapsSkipped,
    int MixedArcCubicApproximated,
    int PathsFlattenedForCubic,
    bool SplineFlattenedToPolyline,
    int NonAsciiTextEscaped,
    IReadOnlyList<string> Diagnostics,
    /// <summary>brief-layout-testing-fixes.md item 6/R-fix-5: the number of TEXT records written —
    /// text a user did not knowingly place (an invisible, sub-pixel label authored by accident) is
    /// exactly what an export report should surface, never leave silent.</summary>
    int LabelRecordsWritten = 0,
    /// <summary>wbond.md §9.4: bond wires written as 3D polylines on <c>Wires_*</c> layers. Reported
    /// so an export that silently carried no wires — the design had none, or none were supplied — is
    /// distinguishable from one that did.</summary>
    int WiresWritten = 0,
    /// <summary>§4.3/R-via-9 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): a
    /// <see cref="ViaShape"/> part (barrel or pad) whose layer has no <c>.ctech</c>-known name (or, for
    /// the pad, no <see cref="ViaShape.LandingLayer"/> set at all) is skipped and named in
    /// <see cref="Diagnostics"/> — never silently exported on DXF's fallback layer "0".</summary>
    int ViaPartsSkipped = 0,
    /// <summary>docs/design/layout-view.md §9B.10 — in-design rulers written as aligned
    /// <c>DIMENSION</c> entities on the <c>RULER</c> layer. Reported so an export that carried none is
    /// distinguishable from one that did, and because R-rul-18b makes a Fixed ruler's exported text
    /// height a function of the drawing extents, which is a surprise the second time someone
    /// exports.</summary>
    int RulersWritten = 0);

public enum DxfViewMode { FitToExtents, MatchCurrentView }

/// <summary>brief-dxf-layer-colors.md §1.2/R-col-1 — the three write versions this exporter supports,
/// distinguished ONLY by <c>$ACADVER</c> and whether group 420 (24-bit true color) accompanies group 62
/// on every LAYER record. AC1015 (R2000) has no 420 at all — 62 (nearest-ACI) is the only option, so
/// colour is necessarily approximate. AC1018 (R2004) added 420 and AC1032 (R2018) carries the identical
/// capability — there is no further colour tier between them, so choosing AC1032 as the default (below)
/// is a product decision (newest header a modern reader is likeliest to expect), not a colour one; if a
/// compatibility complaint about AC1032 ever arrives, dropping the default to AC1018 changes nothing
/// about colour fidelity. R12 (AC1009) is deliberately never added here — see
/// docs/sonnet-briefs/brief-dxf-version-support.md's own reasoning against it (no LWPOLYLINE/ELLIPSE/
/// SPLINE/HATCH), unaffected by anything this brief changes.</summary>
public enum DxfAcadVersion { R2000, R2004, R2018 }

/// <summary>Export-time choices (§1.2's flatten-to-polyline fallback, §2A's two view modes,
/// R-col-1's version/colour-fidelity choice).</summary>
public sealed record DxfExportOptions(
    bool FlattenSplinesToPolyline = false,
    bool PathAsOutlinePolygon = false,
    DxfViewMode ViewMode = DxfViewMode.FitToExtents,
    LayoutViewport? MatchViewport = null,
    double CanvasAspect = 1.0,
    int InsUnits = DxfUnits.DefaultPromptUnits,
    DxfAcadVersion AcadVersion = DxfAcadVersion.R2018);

/// <summary>Assigns every handle (group 5) this writer needs — every table, table record, BLOCK/ENDBLK,
/// and entity in an AC1015+ file must carry one, and every one in the file must be unique. Starts past
/// the low single-digit values convention reserves for a few fixed system objects.</summary>
public sealed class DxfHandles
{
    private int _next = 0x10;
    public string Next() => (_next++).ToString("X");
}

public static partial class DxfWriter
{
    private const string SplinePatternName = "SOLID";

    /// <summary>The `$ACADVER` value this writer emits for <paramref name="version"/> — every version
    /// shares the SAME mandatory handle/owner/subclass-marker structure <c>DxfHandles</c> and every
    /// entity writer below implement (that requirement is an AC1015+ one, unaffected by which of the
    /// three post-R2000 versions is chosen). Exposed publicly (not a per-version constant) so any UI
    /// surface stating what this exporter produces — the export dialog, an about box, a support
    /// request — reads from this ONE table rather than a second hand-typed copy that could silently
    /// drift from what's actually written (brief-dxf-layer-colors.md R-col-1).</summary>
    public static string AcadVersionCode(DxfAcadVersion version) => version switch
    {
        DxfAcadVersion.R2000 => "AC1015",
        DxfAcadVersion.R2004 => "AC1018",
        DxfAcadVersion.R2018 => "AC1032",
        _ => "AC1032",
    };

    /// <summary>Human-readable description shown in the export dialog — states the colour-fidelity
    /// trade-off directly (R2000's is approximate; the other two are exact) so the dialog never needs a
    /// SEPARATE "colours are approximate" line for R2000 (gate 5).</summary>
    public static string FormatDescription(DxfAcadVersion version) => version switch
    {
        DxfAcadVersion.R2000 => $"AutoCAD 2000/R2000 ({AcadVersionCode(version)}) — indexed colour only, colours are approximate",
        DxfAcadVersion.R2004 => $"AutoCAD 2004/R2004 ({AcadVersionCode(version)}) — exact 24-bit colour",
        DxfAcadVersion.R2018 => $"AutoCAD 2018/R2018 ({AcadVersionCode(version)}) — exact 24-bit colour (default)",
        _ => AcadVersionCode(version),
    };

    /// <summary>R-col-1: only AC1015 (R2000) is limited to indexed colour (group 62 alone) — AC1018 and
    /// AC1032 both support group 420 (24-bit true colour) and this writer always emits it alongside 62
    /// on those two, never on R2000 (guardrail: "do not write group 420 into an AC1015 file").</summary>
    public static bool SupportsTrueColor(DxfAcadVersion version) => version != DxfAcadVersion.R2000;

    private const string ModelSpaceBlockName = "*Model_Space";
    private const string PaperSpaceBlockName = "*Paper_Space";

    /// <summary>Writes a hierarchy of structures: every structure becomes a BLOCK; the ROOT structure
    /// is ALSO instanced once, at identity transform, in ENTITIES — this is what makes the file open
    /// with the design on screen (§2A) rather than an empty model space plus unreferenced blocks.</summary>
    /// <param name="rulers">docs/design/layout-view.md §9B.10 — the ROOT cell's in-design ruler
    /// annotations, written as genuine aligned <c>DIMENSION</c> entities on their own <c>RULER</c>
    /// layer. Null or empty writes exactly what this method always did, including an empty DIMSTYLE
    /// table. <b>Root-only, deliberately</b>: rulers are cell-local (§9B.7) and do not render through
    /// an instance placement, so a sub-cell's working notes are not scattered across every design that
    /// reuses it.</param>
    /// <param name="displayUnit">The document's display unit — the readout in each dimension's picture
    /// block is formatted in it (R-rul-6: never a hard-coded unit, never a second formatter).</param>
    public static DxfExportSummary Write(
        TextWriter textWriter,
        IReadOnlyList<InterchangeStructure> structures,
        string rootStructureName,
        Technology? tech,
        int dbuPerMicron,
        DxfExportOptions options,
        WBondDesign? wires = null,
        IReadOnlyList<RulerAnnotation>? rulers = null,
        LayoutUnit displayUnit = LayoutUnit.Um)
    {
        double dbuToDrawingUnit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(options.InsUnits, dbuPerMicron);

        var counts = new Counts();
        var diagnostics = new List<string>();
        var byName = structures.ToDictionary(s => s.Name, s => s);
        var bbox = DxfExtents.ComputeStructureBbox(rootStructureName, byName);

        var blockNames = DxfNaming.MangleForExport(structures.Select(s => s.Name).ToList());
        var layerNames = ResolveLayerNames(structures, tech);

        // §9B.10 — one plan per ruler: its resolved world text height (R-rul-18b turns a Fixed ruler's
        // point size into a length against THESE extents) and which DIMSTYLE record it lands on.
        var rulerPlans = PlanRulers(rulers ?? [], bbox);
        var dimStyles = DistinctDimStyles(rulerPlans);
        var rulerBlockNames = Enumerable.Range(0, rulerPlans.Count).Select(RulerBlockName).ToList();

        var w = new DxfGroupWriter(textWriter);
        var handles = new DxfHandles();

        WriteHeader(w, bbox, dbuToDrawingUnit, options);

        // Wire layers are named after wBond ARRAYS, not technology layers, so they cannot come out of
        // layerNames — they are supplied alongside it or the LAYER table would be missing records the
        // wire entities reference, which a strict reader rejects the whole file over.
        var wireLayerNames = wires is null ? null : DxfWireIo.LayerNames(wires);

        // The RULER layer rides the SAME extraLayerNames seam, for the same reason: a layer a strict
        // reader sees referenced but not declared makes it reject the whole file.
        var extraLayers = new List<string>();
        if (wireLayerNames is not null) extraLayers.AddRange(wireLayerNames);
        if (rulerPlans.Count > 0) extraLayers.Add(RulerLayerName);

        var blockRecordHandles = WriteTablesSection(
            w, handles, layerNames, tech, blockNames.Values.Concat(rulerBlockNames), bbox, dbuToDrawingUnit,
            options, extraLayers.Count > 0 ? extraLayers : null, dimStyles);

        // ── BLOCKS ────────────────────────────────────────────────────────────
        WriteSectionStart(w, "BLOCKS");
        WriteEmptySpaceBlock(w, handles, ModelSpaceBlockName, blockRecordHandles[ModelSpaceBlockName]);
        WriteEmptySpaceBlock(w, handles, PaperSpaceBlockName, blockRecordHandles[PaperSpaceBlockName]);
        foreach (var s in structures)
        {
            string bname = blockNames[s.Name];
            string ownerHandle = blockRecordHandles[bname];
            WriteBlockHeader(w, handles, bname, ownerHandle);
            foreach (var shape in s.Shapes)
                WriteShape(w, shape, tech, layerNames, dbuToDrawingUnit, options, counts, diagnostics, handles, ownerHandle, s.Name);
            foreach (var inst in s.Instances)
                WriteInsert(w, inst, blockNames, dbuToDrawingUnit, handles, ownerHandle);
            WriteBlockFooter(w, handles, ownerHandle);
        }
        // One anonymous *D# block per ruler, through this same path unchanged.
        WriteRulerBlocks(w, handles, rulerPlans, blockRecordHandles, displayUnit, dbuPerMicron, dbuToDrawingUnit);
        WriteSectionEnd(w);

        // ── ENTITIES ──────────────────────────────────────────────────────────
        WriteSectionStart(w, "ENTITIES");
        string modelSpaceHandle = blockRecordHandles[ModelSpaceBlockName];
        WriteEntityHeader(w, "INSERT", handles, modelSpaceHandle, "0", "AcDbBlockReference");
        w.WriteEscapedString(2, blockNames[rootStructureName]);
        w.WriteCoord(10, 0, dbuToDrawingUnit);
        w.WriteCoord(20, 0, dbuToDrawingUnit);
        w.WriteDouble(41, 1.0);
        w.WriteDouble(42, 1.0);
        w.WriteDouble(50, 0.0);

        // Wires live in MODEL SPACE beside the root INSERT, not inside a block: they are absolute
        // geometry belonging to the assembly, not part of any cell's own definition, and an assembly
        // house opening the file expects to see them without descending into a block.
        int wiresWritten = wires is null
            ? 0
            : DxfWireIo.WriteWires(w, wires, dbuToDrawingUnit, dbuPerMicron, handles, modelSpaceHandle);

        // Rulers live in MODEL SPACE beside the root INSERT for the same reason the wires do: they are
        // statements about the assembly, not part of any cell's own definition, and a recipient
        // expects to see them without descending into a block.
        int rulersWritten = WriteRulerDimensions(w, handles, rulerPlans, blockRecordHandles, modelSpaceHandle,
                                                 displayUnit, dbuPerMicron, dbuToDrawingUnit);

        WriteSectionEnd(w);

        w.WriteString(0, "EOF");

        counts.WiresWritten = wiresWritten;

        if (rulersWritten > 0)
            diagnostics.Add(RulerExportNote(rulersWritten, rulerPlans.Any(p => p.Ruler.SizeMode == RulerSizeMode.Fixed)));

        return new DxfExportSummary(
            counts.CurveFlattened, counts.HolesAsHatch, counts.BitmapsSkipped,
            counts.MixedArcCubicApproximated, counts.PathsFlattenedForCubic,
            counts.SplineFlattenedToPolyline, w.EscapedTextCount, diagnostics,
            counts.LabelsWritten, counts.WiresWritten, counts.ViaPartsSkipped, rulersWritten);
    }

    private sealed class Counts
    {
        public int CurveFlattened;
        public int HolesAsHatch;
        public int BitmapsSkipped;
        public int MixedArcCubicApproximated;
        public int PathsFlattenedForCubic;
        public bool SplineFlattenedToPolyline;
        public int LabelsWritten;
        public int ViaPartsSkipped;
        public int WiresWritten;
    }

    // ── HEADER ───────────────────────────────────────────────────────────────

    private static void WriteHeader(DxfGroupWriter w, Bbox bbox, double dbuToDrawingUnit, DxfExportOptions options)
    {
        WriteSectionStart(w, "HEADER");

        WriteHeaderVar(w, "$ACADVER", 1, AcadVersionCode(options.AcadVersion));
        WriteHeaderVarInt(w, "$INSUNITS", 70, options.InsUnits);

        var (view, guard) = DxfViewCalc.Compute(bbox, options, dbuToDrawingUnit);

        w.WriteString(9, "$EXTMIN");
        w.WriteDouble(10, guard.ExtMinX); w.WriteDouble(20, guard.ExtMinY); w.WriteDouble(30, 0.0);
        w.WriteString(9, "$EXTMAX");
        w.WriteDouble(10, guard.ExtMaxX); w.WriteDouble(20, guard.ExtMaxY); w.WriteDouble(30, 0.0);
        w.WriteString(9, "$LIMMIN");
        w.WriteDouble(10, guard.ExtMinX); w.WriteDouble(20, guard.ExtMinY);
        w.WriteString(9, "$LIMMAX");
        w.WriteDouble(10, guard.ExtMaxX); w.WriteDouble(20, guard.ExtMaxY);

        w.WriteString(9, "$VIEWCTR");
        w.WriteDouble(10, view.CenterX); w.WriteDouble(20, view.CenterY);
        w.WriteString(9, "$VIEWSIZE");
        w.WriteDouble(40, view.Height);

        WriteSectionEnd(w);
    }

    private static void WriteHeaderVar(DxfGroupWriter w, string name, int code, string value)
    {
        w.WriteString(9, name);
        w.WriteString(code, value);
    }

    private static void WriteHeaderVarInt(DxfGroupWriter w, string name, int code, int value)
    {
        w.WriteString(9, name);
        w.WriteInt(code, value);
    }

    // ── TABLES (VPORT, LTYPE, LAYER, STYLE, VIEW, UCS, APPID, DIMSTYLE, BLOCK_RECORD) ────────────────
    //
    // Every table and table record needs a handle (5) + owner (330, the owning table's own handle for
    // records, 0 for the table itself) + the record's own pair of subclass markers (100
    // AcDbSymbolTableRecord, then 100 AcDb<SpecificTable>TableRecord) — this is the R13+ structural
    // requirement the original version of this file omitted entirely. VIEW/UCS carry zero records (this
    // codebase never creates named views/UCSs); STYLE/APPID/DIMSTYLE each carry exactly the one fixed
    // "Standard"/"ACAD" record real CAD files always have, since nothing in this codebase's own export
    // ever needs a second one.

    private static IReadOnlyDictionary<string, string> WriteTablesSection(
        DxfGroupWriter w, DxfHandles handles, IReadOnlyDictionary<LayerKey, string> layerNames, Technology? tech,
        IEnumerable<string> blockNames, Bbox bbox, double dbuToDrawingUnit, DxfExportOptions options,
        IReadOnlyList<string>? extraLayerNames = null,
        IReadOnlyList<(string Name, double TextHeightDbu)>? dimStyles = null)
    {
        WriteSectionStart(w, "TABLES");

        WriteVportTable(w, handles, bbox, dbuToDrawingUnit, options);
        WriteLtypeTable(w, handles);
        WriteLayerTable(w, handles, layerNames, tech, options, extraLayerNames);
        WriteStyleTable(w, handles);
        WriteEmptyTable(w, handles, "VIEW");
        WriteEmptyTable(w, handles, "UCS");
        WriteAppidTable(w, handles);
        WriteDimstyleTable(w, handles, dimStyles ?? [], dbuToDrawingUnit);
        var blockRecordHandles = WriteBlockRecordTable(w, handles, blockNames);

        WriteSectionEnd(w);
        return blockRecordHandles;
    }

    private static string WriteTableHeader(DxfGroupWriter w, DxfHandles handles, string name, int count, string? extraSubclass = null)
    {
        string tableHandle = handles.Next();
        w.WriteString(0, "TABLE");
        w.WriteString(2, name);
        w.WriteString(5, tableHandle);
        w.WriteString(330, "0");
        w.WriteString(100, "AcDbSymbolTable");
        w.WriteInt(70, count);
        if (extraSubclass is not null) w.WriteString(100, extraSubclass);
        return tableHandle;
    }

    private static void WriteTableFooter(DxfGroupWriter w) => w.WriteString(0, "ENDTAB");

    private static void WriteEmptyTable(DxfGroupWriter w, DxfHandles handles, string name)
    {
        WriteTableHeader(w, handles, name, 0);
        WriteTableFooter(w);
    }

    private static void WriteVportTable(DxfGroupWriter w, DxfHandles handles, Bbox bbox, double dbuToDrawingUnit, DxfExportOptions options)
    {
        string tableHandle = WriteTableHeader(w, handles, "VPORT", 1);
        var (view, _) = DxfViewCalc.Compute(bbox, options, dbuToDrawingUnit);

        w.WriteString(0, "VPORT");
        w.WriteString(5, handles.Next());
        w.WriteString(330, tableHandle);
        w.WriteString(100, "AcDbSymbolTableRecord");
        w.WriteString(100, "AcDbViewportTableRecord");
        w.WriteString(2, "*ACTIVE");
        w.WriteInt(70, 0);
        w.WriteDouble(10, 0.0); w.WriteDouble(20, 0.0);
        w.WriteDouble(11, 1.0); w.WriteDouble(21, 1.0);
        w.WriteDouble(12, view.CenterX); w.WriteDouble(22, view.CenterY);
        w.WriteDouble(40, view.Height);
        w.WriteDouble(41, view.Aspect);
        WriteTableFooter(w);
    }

    private static void WriteLtypeTable(DxfGroupWriter w, DxfHandles handles)
    {
        string tableHandle = WriteTableHeader(w, handles, "LTYPE", 3);
        foreach (var name in new[] { "ByBlock", "ByLayer", "Continuous" })
        {
            w.WriteString(0, "LTYPE");
            w.WriteString(5, handles.Next());
            w.WriteString(330, tableHandle);
            w.WriteString(100, "AcDbSymbolTableRecord");
            w.WriteString(100, "AcDbLinetypeTableRecord");
            w.WriteString(2, name);
            w.WriteInt(70, 0);
            w.WriteString(3, "");
            w.WriteInt(72, 65);
            w.WriteInt(73, 0);
            w.WriteDouble(40, 0.0);
        }
        WriteTableFooter(w);
    }

    /// <summary>brief-dxf-layer-colors.md §1 — every LAYER record now writes its own colour: group 62
    /// (nearest ACI index, always — AC1015's only option) and, when <paramref name="options"/>'s chosen
    /// version supports it (R-col-1/<see cref="SupportsTrueColor"/>), ALSO group 420 (exact 24-bit RGB)
    /// so a reader that understands true colour never needs the approximation. Every ENTITY omits both
    /// groups entirely (confirmed by inspection of every <c>Write*</c> shape method below — none writes
    /// 62/420), which is what makes them <c>ByLayer</c> and lets this table's colour actually take
    /// effect; writing an explicit per-entity colour here would make the layer table decorative, per
    /// the brief's own diagnosis.</summary>
    private static void WriteLayerTable(
        DxfGroupWriter w, DxfHandles handles, IReadOnlyDictionary<LayerKey, string> layerNames,
        Technology? tech, DxfExportOptions options, IReadOnlyList<string>? extraLayerNames = null)
    {
        var names = new List<string> { "0" };
        names.AddRange(layerNames.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => n != "0"));

        // Wire layers (Wires_<group>) carry no LayerKey — they are named after a wBond array, not after
        // a technology layer — so they cannot come from layerNames and are supplied alongside it. They
        // still need a real LAYER record or a strict reader rejects every entity that references them.
        if (extraLayerNames is not null)
            foreach (string extra in extraLayerNames)
                if (!names.Contains(extra, StringComparer.OrdinalIgnoreCase))
                    names.Add(extra);

        // Multiple LayerKeys can sanitize to the same DXF name (a rare collision) — the layer table has
        // only one record per name, so the FIRST key to claim a name supplies its colour; this mirrors
        // the pre-existing name-dedup behavior above, which already picked "whichever key got there
        // first" for the name itself.
        var firstKeyForName = new Dictionary<string, LayerKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in layerNames)
            firstKeyForName.TryAdd(name, key);

        bool trueColor = SupportsTrueColor(options.AcadVersion);
        string tableHandle = WriteTableHeader(w, handles, "LAYER", names.Count);
        foreach (var name in names)
        {
            w.WriteString(0, "LAYER");
            w.WriteString(5, handles.Next());
            w.WriteString(330, tableHandle);
            w.WriteString(100, "AcDbSymbolTableRecord");
            w.WriteString(100, "AcDbLayerTableRecord");
            w.WriteEscapedString(2, name);
            w.WriteInt(70, 0);

            Rgba color = ResolveLayerColorForWrite(name, firstKeyForName, tech);
            w.WriteInt(62, DxfAciPalette.NearestIndex(color));
            if (trueColor) w.WriteInt(420, PackTrueColor(color));

            w.WriteString(6, "CONTINUOUS");
        }
        WriteTableFooter(w);
    }

    /// <summary>Layer "0" is DXF's own universal default layer, not backed by any <see cref="LayerKey"/>
    /// — every real DXF file's own layer 0 is conventionally white, so that's what this writer emits for
    /// it. Every other name resolves through whichever <see cref="LayerKey"/> first claimed it: the
    /// technology's own colour when defined, else the SAME <see cref="FallbackPalette"/> gap-fill the
    /// renderer already uses for an undefined layer — so an exported file's colours match what the user
    /// actually sees on screen in circuitRF, technology-defined or not.</summary>
    private static Rgba ResolveLayerColorForWrite(string name, IReadOnlyDictionary<string, LayerKey> firstKeyForName, Technology? tech)
    {
        if (name == "0" || !firstKeyForName.TryGetValue(name, out var key))
            return new Rgba(255, 255, 255);

        var def = tech?.Layers.FirstOrDefault(l => l.Key == key);
        return def is not null ? def.Color : FallbackPalette.For(key).Color;
    }

    /// <summary>Group 420's own encoding: a plain 24-bit `0x00RRGGBB` integer — NOT a BGR order, and no
    /// alpha channel (DXF true colour is opaque only).</summary>
    private static int PackTrueColor(Rgba c) => (c.R << 16) | (c.G << 8) | c.B;

    private static void WriteStyleTable(DxfGroupWriter w, DxfHandles handles)
    {
        string tableHandle = WriteTableHeader(w, handles, "STYLE", 1);
        w.WriteString(0, "STYLE");
        w.WriteString(5, handles.Next());
        w.WriteString(330, tableHandle);
        w.WriteString(100, "AcDbSymbolTableRecord");
        w.WriteString(100, "AcDbTextStyleTableRecord");
        w.WriteString(2, "Standard");
        w.WriteInt(70, 0);
        w.WriteDouble(40, 0.0);
        w.WriteDouble(41, 1.0);
        w.WriteDouble(50, 0.0);
        w.WriteInt(71, 0);
        w.WriteDouble(42, 2.5);
        w.WriteString(3, "txt");
        w.WriteString(4, "");
        WriteTableFooter(w);
    }

    private static void WriteAppidTable(DxfGroupWriter w, DxfHandles handles)
    {
        string tableHandle = WriteTableHeader(w, handles, "APPID", 1);
        w.WriteString(0, "APPID");
        w.WriteString(5, handles.Next());
        w.WriteString(330, tableHandle);
        w.WriteString(100, "AcDbSymbolTableRecord");
        w.WriteString(100, "AcDbRegAppTableRecord");
        w.WriteString(2, "ACAD");
        w.WriteInt(70, 0);
        WriteTableFooter(w);
    }

    /// <summary>One record per BLOCK this file will define (plus the two system spaces) — every BLOCK
    /// entity's own owner (330) and every entity-inside-that-block's owner point at ITS BLOCK_RECORD's
    /// handle, never at the BLOCK_RECORD table's own handle. Returns block name -> record handle.</summary>
    private static IReadOnlyDictionary<string, string> WriteBlockRecordTable(
        DxfGroupWriter w, DxfHandles handles, IEnumerable<string> blockNames)
    {
        var allNames = new List<string> { ModelSpaceBlockName, PaperSpaceBlockName };
        allNames.AddRange(blockNames);

        string tableHandle = WriteTableHeader(w, handles, "BLOCK_RECORD", allNames.Count);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in allNames)
        {
            string recordHandle = handles.Next();
            result[name] = recordHandle;
            w.WriteString(0, "BLOCK_RECORD");
            w.WriteString(5, recordHandle);
            w.WriteString(330, tableHandle);
            w.WriteString(100, "AcDbSymbolTableRecord");
            w.WriteString(100, "AcDbBlockTableRecord");
            w.WriteEscapedString(2, name);
            w.WriteString(340, "0"); // no associated Layout object — OBJECTS section is not written
        }
        WriteTableFooter(w);
        return result;
    }

    private static IReadOnlyDictionary<LayerKey, string> ResolveLayerNames(
        IReadOnlyList<InterchangeStructure> structures, Technology? tech)
    {
        var keys = new HashSet<LayerKey>();
        foreach (var s in structures)
            foreach (var shape in s.Shapes)
            {
                if (shape is BitmapShape) continue;
                keys.Add(shape.Layer);
                // §4.3/R-via-9: a Via's PAD lives on LandingLayer, a layer no OTHER field on the shape
                // names — without this, a via whose pad layer isn't independently used by some other
                // shape in the design would never get a LAYER table entry at all, and WriteViaAsCircles
                // would report it "not known to this technology" even when the .ctech genuinely maps it.
                if (shape is ViaShape { LandingLayer: { } landing }) keys.Add(landing);
            }

        var result = new Dictionary<LayerKey, string>();
        foreach (var key in keys)
        {
            var def = tech?.Layers.FirstOrDefault(l => l.Key == key);
            string name = def?.Interchange?.DxfLayerName is { Length: > 0 } alias
                ? alias
                : def?.Name is { Length: > 0 } n
                    ? n
                    : $"L{key.Layer}_{key.Datatype}";
            result[key] = SanitizeLayerName(name);
        }
        return result;
    }

    private static string SanitizeLayerName(string name)
    {
        var chars = name.Select(c => c > 0x1F && c is not ('<' or '>' or '/' or '\\' or '"' or ':' or ';' or '?' or '*' or '|' or ',' or '=' or '`') ? c : '_').ToArray();
        var s = new string(chars);
        return s.Length == 0 ? "0" : s;
    }

    // ── Section framing ──────────────────────────────────────────────────────

    private static void WriteSectionStart(DxfGroupWriter w, string name)
    {
        w.WriteString(0, "SECTION");
        w.WriteString(2, name);
    }

    private static void WriteSectionEnd(DxfGroupWriter w) => w.WriteString(0, "ENDSEC");

    /// <summary>An empty (system) space block — the DXF analogue of GDSII having no notion of a "top"
    /// structure at all: real content lives in ENTITIES, owned by this block's own BLOCK_RECORD.</summary>
    private static void WriteEmptySpaceBlock(DxfGroupWriter w, DxfHandles handles, string name, string ownerHandle)
    {
        WriteBlockHeader(w, handles, name, ownerHandle);
        WriteBlockFooter(w, handles, ownerHandle);
    }

    private static void WriteBlockHeader(DxfGroupWriter w, DxfHandles handles, string blockName, string ownerHandle)
    {
        w.WriteString(0, "BLOCK");
        w.WriteString(5, handles.Next());
        w.WriteString(330, ownerHandle);
        w.WriteString(100, "AcDbEntity");
        w.WriteString(8, "0");
        w.WriteString(100, "AcDbBlockBegin");
        w.WriteEscapedString(2, blockName);
        w.WriteInt(70, 0);
        w.WriteDouble(10, 0.0); w.WriteDouble(20, 0.0); w.WriteDouble(30, 0.0);
        w.WriteEscapedString(3, blockName);
        w.WriteString(1, "");
    }

    private static void WriteBlockFooter(DxfGroupWriter w, DxfHandles handles, string ownerHandle)
    {
        w.WriteString(0, "ENDBLK");
        w.WriteString(5, handles.Next());
        w.WriteString(330, ownerHandle);
        w.WriteString(100, "AcDbEntity");
        w.WriteString(8, "0");
        w.WriteString(100, "AcDbBlockEnd");
    }

    /// <summary>The common entity preamble every AC1015+ entity needs: handle, owner (the containing
    /// block's own BLOCK_RECORD handle), the "AcDbEntity" subclass marker, layer, then the entity's OWN
    /// specific subclass marker — after this, only the entity-specific fields follow.</summary>
    /// <summary>
    /// The five groups every entity opens with. <c>internal</c> so <see cref="DxfWireIo"/> shares it —
    /// a second copy of the handle/owner/subclass preamble is exactly how an entity ends up missing
    /// one of them, which is the class of bug a strict reader rejects the whole file over.
    /// </summary>
    internal static void WriteEntityHeader(DxfGroupWriter w, string type, DxfHandles handles, string ownerHandle, string layer, string subclass)
    {
        w.WriteString(0, type);
        w.WriteString(5, handles.Next());
        w.WriteString(330, ownerHandle);
        w.WriteString(100, "AcDbEntity");
        w.WriteEscapedString(8, layer);
        w.WriteString(100, subclass);
    }

    // ── Shapes ────────────────────────────────────────────────────────────────

    private static void WriteShape(
        DxfGroupWriter w, LayoutShape shape, Technology? tech, IReadOnlyDictionary<LayerKey, string> layerNames,
        double dbuToDrawingUnit, DxfExportOptions options, Counts counts, List<string> diagnostics,
        DxfHandles handles, string ownerHandle, string structureName)
    {
        string LayerOf(LayoutShape s) => layerNames.TryGetValue(s.Layer, out var n) ? n : "0";

        switch (shape)
        {
            case BitmapShape:
                counts.BitmapsSkipped++;
                return;

            case RectShape rect:
                WriteLwPolylineFromRing(
                    w, LayerOf(rect),
                    Ring([rect.X1, rect.Y1, rect.X2, rect.Y1, rect.X2, rect.Y2, rect.X1, rect.Y2], null),
                    dbuToDrawingUnit, handles, ownerHandle);
                return;

            case LabelShape label:
                counts.LabelsWritten++; // item 6/R-fix-5: text a user did not knowingly place is
                                        // exactly what an export report should surface — count every
                                        // TEXT record, not just curve/hole/bitmap conversions.
                WriteText(w, label, LayerOf(label), dbuToDrawingUnit, handles, ownerHandle);
                return;

            case ViaShape via:
                WriteViaAsCircles(w, via, tech, layerNames, dbuToDrawingUnit, counts, diagnostics, handles, ownerHandle, structureName);
                return;

            case CircleShape c:
                WriteEntityHeader(w, "CIRCLE", handles, ownerHandle, LayerOf(c), "AcDbCircle");
                w.WriteCoord(10, c.Cx, dbuToDrawingUnit); w.WriteCoord(20, c.Cy, dbuToDrawingUnit);
                w.WriteDouble(40, c.R * dbuToDrawingUnit);
                return;

            case RoundedRectShape rr:
                counts.CurveFlattened++;
                WriteLwPolylineFromRing(w, LayerOf(rr), RoundedRectRing(rr), dbuToDrawingUnit, handles, ownerHandle);
                return;

            case PathShape path:
                WritePath(w, path, LayerOf(path), tech, dbuToDrawingUnit, options, counts, handles, ownerHandle);
                return;

            case PolygonShape poly:
                if (poly.Holes is { Count: > 0 })
                {
                    counts.HolesAsHatch++;
                    WriteHatch(w, LayerOf(poly), Ring(poly.Xy, null), poly.Holes, dbuToDrawingUnit, handles, ownerHandle);
                }
                else
                {
                    WriteLwPolylineFromRing(w, LayerOf(poly), Ring(poly.Xy, null), dbuToDrawingUnit, handles, ownerHandle);
                }
                return;

            case CurveShape curve:
                WriteCurve(w, curve, LayerOf(curve), dbuToDrawingUnit, options, counts, handles, ownerHandle);
                return;

            default:
                throw new NotSupportedException($"DXF export does not support shape type {shape.GetType().Name}.");
        }
    }

    /// <summary>§4.3/R-via-9: a <see cref="ViaShape"/> emits one exact CIRCLE per mapped layer it
    /// participates in — the barrel (<see cref="LayoutShape.Layer"/>, at <see cref="ViaShape.DrillSize"/>)
    /// and the pad (<see cref="ViaSpanResolver.PadLayer"/>, at <see cref="ViaShape.PadSize"/> — the
    /// shape's own <see cref="ViaShape.LandingLayer"/> when an importer set one, otherwise the TOP
    /// conductor of the span the stackup states). "Mapped"
    /// here means the layer has a real <see cref="LayerDef"/> in <paramref name="tech"/> — checked
    /// against the technology directly, NOT against <paramref name="layerNames"/> membership alone,
    /// since <c>ResolveLayerNames</c> always synthesizes SOME entry for a via's LandingLayer (so the
    /// LAYER table is well-formed whenever a part IS written) even when that key isn't genuinely
    /// declared. Unlike every OTHER shape kind, which silently falls back to DXF layer "0" when its key
    /// is unknown (<c>LayerOf</c>'s own default), a via part with an undeclared layer is SKIPPED and
    /// reported instead: falling back to "0" would put pad/barrel copper on an arbitrary layer, "an
    /// export that looks plausible and puts copper where the hole should be" (§4.3's own explicit
    /// warning). The pad is additionally skipped (and reported) when NEITHER the shape's landing layer
    /// nor its stackup via entry's span names a layer — nothing to draw it on at all.</summary>
    private static void WriteViaAsCircles(
        DxfGroupWriter w, ViaShape via, Technology? tech, IReadOnlyDictionary<LayerKey, string> layerNames,
        double dbuToDrawingUnit, Counts counts, List<string> diagnostics, DxfHandles handles, string ownerHandle,
        string structureName)
    {
        void WriteCircle(LayerKey layer, long diameterDbu)
        {
            if (tech?.Layers.Any(l => l.Key == layer) != true || !layerNames.TryGetValue(layer, out var name))
            {
                counts.ViaPartsSkipped++;
                diagnostics.Add($"{structureName}: via at ({via.X},{via.Y}) — layer ({layer.Layer},{layer.Datatype}) is not known to this technology; part skipped.");
                return;
            }
            WriteEntityHeader(w, "CIRCLE", handles, ownerHandle, name, "AcDbCircle");
            w.WriteCoord(10, via.X, dbuToDrawingUnit); w.WriteCoord(20, via.Y, dbuToDrawingUnit);
            w.WriteDouble(40, Math.Max(diameterDbu, 2) / 2.0 * dbuToDrawingUnit);
        }

        WriteCircle(via.Layer, via.DrillSize);

        if (ViaSpanResolver.PadLayer(via, tech) is { } landing)
            WriteCircle(landing, via.PadSize);
        else
        {
            counts.ViaPartsSkipped++;
            diagnostics.Add($"{structureName}: via at ({via.X},{via.Y}) has no pad layer — " +
                            (ViaSpanResolver.Explain(via.Layer, tech) ?? "no landing layer is set.") +
                            " Pad not exported.");
        }
    }

    // ── Curve (arc/cubic edge-list, closed) ──────────────────────────────────

    private readonly record struct RingEdge(long X0, long Y0, long X1, long Y1, EdgeKind Kind, double Bulge, long C1X, long C1Y, long C2X, long C2Y);

    private static List<RingEdge> Ring(long[] xy, List<LayoutEdge>? edges)
    {
        int n = xy.Length / 2;
        var result = new List<RingEdge>(n);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var e = edges is not null && i < edges.Count ? edges[i] : null;
            result.Add(new RingEdge(
                xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1],
                e?.Kind ?? EdgeKind.Line, e?.Bulge ?? 0.0,
                e?.C1X ?? 0, e?.C1Y ?? 0, e?.C2X ?? 0, e?.C2Y ?? 0));
        }
        return result;
    }

    private static List<RingEdge> RoundedRectRing(RoundedRectShape rr)
    {
        long x1 = Math.Min(rr.X1, rr.X2), x2 = Math.Max(rr.X1, rr.X2);
        long y1 = Math.Min(rr.Y1, rr.Y2), y2 = Math.Max(rr.Y1, rr.Y2);
        long cr = Math.Max(0, Math.Min(rr.CornerRadius, Math.Min(x2 - x1, y2 - y1) / 2));

        if (cr <= 0)
            return Ring([x1, y1, x2, y1, x2, y2, x1, y2], null);

        // tan(22.5deg) — the same constant §4's .clay example and the RoundedRect corner already use.
        const double kappa = 0.41421356237309515;
        var edges = new List<LayoutEdge>
        {
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = kappa },
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = kappa },
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = kappa },
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = kappa },
        };
        long[] xy =
        [
            x1 + cr, y1,  x2 - cr, y1,
            x2, y1 + cr,  x2, y2 - cr,
            x2 - cr, y2,  x1 + cr, y2,
            x1, y2 - cr,  x1, y1 + cr,
        ];
        return Ring(xy, edges);
    }

    private static void WriteCurve(DxfGroupWriter w, CurveShape curve, string layer, double dbuToDrawingUnit,
        DxfExportOptions options, Counts counts, DxfHandles handles, string ownerHandle)
    {
        var ring = Ring(curve.Xy, curve.Edges);
        bool hasArc = ring.Any(e => e.Kind == EdgeKind.Arc);
        bool hasCubic = ring.Any(e => e.Kind == EdgeKind.Cubic);

        if (curve.Holes is { Count: > 0 })
        {
            counts.HolesAsHatch++;
            WriteHatch(w, layer, ring, curve.Holes, dbuToDrawingUnit, handles, ownerHandle);
            return;
        }

        if (!hasCubic)
        {
            if (hasArc) counts.CurveFlattened++; // reported as a curved shape written (never flattened — see below)
            WriteLwPolylineFromRing(w, layer, ring, dbuToDrawingUnit, handles, ownerHandle);
            return;
        }

        if (hasArc)
        {
            // Both Arc and Cubic in one hole-free ring — neither LWPOLYLINE nor a standalone SPLINE
            // can carry both exactly. Flatten the Cubic edges to line segments (arcs stay bulges).
            counts.MixedArcCubicApproximated++;
            WriteLwPolylineFromRing(w, layer, FlattenCubicsInRing(ring), dbuToDrawingUnit, handles, ownerHandle);
            return;
        }

        if (options.FlattenSplinesToPolyline)
        {
            counts.SplineFlattenedToPolyline = true;
            WriteLwPolylineFromRing(w, layer, FlattenCubicsInRing(ring), dbuToDrawingUnit, handles, ownerHandle);
            return;
        }

        counts.CurveFlattened++;
        WriteClosedSplineChain(w, layer, ring, dbuToDrawingUnit, handles, ownerHandle);
    }

    private static List<RingEdge> FlattenCubicsInRing(List<RingEdge> ring)
    {
        var result = new List<RingEdge>();
        foreach (var e in ring)
        {
            if (e.Kind != EdgeKind.Cubic) { result.Add(e); continue; }
            var pts = new List<long> { e.X0, e.Y0 };
            AppendFlattenedCubic(pts, e.X0, e.Y0, e.C1X, e.C1Y, e.C2X, e.C2Y, e.X1, e.Y1, LayoutFlattener.DefaultTolDbu, 0);
            for (int i = 0; i + 3 < pts.Count; i += 2)
                result.Add(new RingEdge(pts[i], pts[i + 1], pts[i + 2], pts[i + 3], EdgeKind.Line, 0, 0, 0, 0, 0));
        }
        return result;
    }

    /// <summary>Local de Casteljau subdivision (mirrors <c>LayoutFlattener</c>'s own, private, algorithm)
    /// — used only for the narrow "cubic edge can't be represented exactly in this entity" fallback
    /// paths (mixed arc+cubic ring, cubic-bearing Path, or the flatten-to-polyline export option).</summary>
    private static void AppendFlattenedCubic(
        List<long> xy, double x0, double y0, double c1x, double c1y, double c2x, double c2y,
        double x1, double y1, long tolDbu, int depth)
    {
        if (depth >= 20 || IsFlatEnough(x0, y0, c1x, c1y, c2x, c2y, x1, y1, tolDbu))
        {
            xy.Add((long)Math.Round(x1)); xy.Add((long)Math.Round(y1));
            return;
        }

        double x01 = (x0 + c1x) / 2.0, y01 = (y0 + c1y) / 2.0;
        double x12 = (c1x + c2x) / 2.0, y12 = (c1y + c2y) / 2.0;
        double x23 = (c2x + x1) / 2.0, y23 = (c2y + y1) / 2.0;
        double x012 = (x01 + x12) / 2.0, y012 = (y01 + y12) / 2.0;
        double x123 = (x12 + x23) / 2.0, y123 = (y12 + y23) / 2.0;
        double xm = (x012 + x123) / 2.0, ym = (y012 + y123) / 2.0;

        AppendFlattenedCubic(xy, x0, y0, x01, y01, x012, y012, xm, ym, tolDbu, depth + 1);
        AppendFlattenedCubic(xy, xm, ym, x123, y123, x23, y23, x1, y1, tolDbu, depth + 1);
    }

    private static bool IsFlatEnough(double x0, double y0, double c1x, double c1y,
        double c2x, double c2y, double x1, double y1, long tolDbu)
    {
        double tol = Math.Max(1.0, tolDbu);
        return PointToLineDistance(c1x, c1y, x0, y0, x1, y1) <= tol
            && PointToLineDistance(c2x, c2y, x0, y0, x1, y1) <= tol;
    }

    private static double PointToLineDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double cross = dx * (py - ay) - dy * (px - ax);
        return Math.Abs(cross) / Math.Sqrt(lenSq);
    }

    // ── Path (open, width, end style) ────────────────────────────────────────

    private static void WritePath(
        DxfGroupWriter w, PathShape path, string layer, Technology? tech, double dbuToDrawingUnit,
        DxfExportOptions options, Counts counts, DxfHandles handles, string ownerHandle)
    {
        if (options.PathAsOutlinePolygon)
        {
            // §1.2's "offer an option to export paths as their outline polygon" — end-cap style is
            // baked into the outline itself here, unlike the parametric LWPOLYLINE-with-width form,
            // where flush/round/square/extended does not survive (DXF polyline width is a RENDERING
            // width, not a stroked outline).
            long tol = LayoutFlattener.ResolveTolDbu(path, tech);
            var outlinePaths = LayoutClipper.ToClipperPaths(path, tol);
            foreach (var p in outlinePaths)
            {
                var xy = new long[p.Count * 2];
                for (int i = 0; i < p.Count; i++) { xy[2 * i] = p[i].X; xy[2 * i + 1] = p[i].Y; }
                WriteLwPolylineFromRing(w, layer, Ring(xy, null), dbuToDrawingUnit, handles, ownerHandle);
            }
            return;
        }

        int n = path.Xy.Length / 2;
        var openEdges = new List<RingEdge>(Math.Max(0, n - 1));
        for (int i = 0; i < n - 1; i++)
        {
            var e = path.Edges is not null && i < path.Edges.Count ? path.Edges[i] : null;
            openEdges.Add(new RingEdge(
                path.Xy[2 * i], path.Xy[2 * i + 1], path.Xy[2 * (i + 1)], path.Xy[2 * (i + 1) + 1],
                e?.Kind ?? EdgeKind.Line, e?.Bulge ?? 0.0, e?.C1X ?? 0, e?.C1Y ?? 0, e?.C2X ?? 0, e?.C2Y ?? 0));
        }

        bool hasCubic = openEdges.Any(e => e.Kind == EdgeKind.Cubic);
        if (hasCubic)
        {
            counts.PathsFlattenedForCubic++;
            openEdges = FlattenCubicsOpen(openEdges);
        }

        WriteLwPolylineOpen(w, layer, openEdges, path.Xy, path.Width * dbuToDrawingUnit, dbuToDrawingUnit, handles, ownerHandle);
    }

    private static List<RingEdge> FlattenCubicsOpen(List<RingEdge> edges)
    {
        var result = new List<RingEdge>();
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) { result.Add(e); continue; }
            var pts = new List<long> { e.X0, e.Y0 };
            AppendFlattenedCubic(pts, e.X0, e.Y0, e.C1X, e.C1Y, e.C2X, e.C2Y, e.X1, e.Y1, LayoutFlattener.DefaultTolDbu, 0);
            for (int i = 0; i + 3 < pts.Count; i += 2)
                result.Add(new RingEdge(pts[i], pts[i + 1], pts[i + 2], pts[i + 3], EdgeKind.Line, 0, 0, 0, 0, 0));
        }
        return result;
    }

    // ── LWPOLYLINE ────────────────────────────────────────────────────────────

    private static void WriteLwPolylineFromRing(
        DxfGroupWriter w, string layer, List<RingEdge> ring, double dbuToDrawingUnit, DxfHandles handles, string ownerHandle)
    {
        WriteEntityHeader(w, "LWPOLYLINE", handles, ownerHandle, layer, "AcDbPolyline");
        w.WriteInt(90, ring.Count);
        w.WriteInt(70, 1); // closed
        foreach (var e in ring)
        {
            w.WriteCoord(10, e.X0, dbuToDrawingUnit);
            w.WriteCoord(20, e.Y0, dbuToDrawingUnit);
            if (e.Kind == EdgeKind.Arc && e.Bulge != 0)
                w.WriteDouble(42, e.Bulge);
        }
    }

    private static void WriteLwPolylineOpen(
        DxfGroupWriter w, string layer, List<RingEdge> edges, long[] originalXy, double width, double dbuToDrawingUnit,
        DxfHandles handles, string ownerHandle)
    {
        int n = originalXy.Length / 2;

        WriteEntityHeader(w, "LWPOLYLINE", handles, ownerHandle, layer, "AcDbPolyline");
        w.WriteInt(90, n);
        w.WriteInt(70, 0); // open
        if (width > 0) w.WriteDouble(43, width);

        if (edges.Count == 0)
        {
            for (int i = 0; i < n; i++)
            {
                w.WriteCoord(10, originalXy[2 * i], dbuToDrawingUnit);
                w.WriteCoord(20, originalXy[2 * i + 1], dbuToDrawingUnit);
            }
            return;
        }

        for (int i = 0; i < n; i++)
        {
            long x = i < edges.Count ? edges[i].X0 : edges[i - 1].X1;
            long y = i < edges.Count ? edges[i].Y0 : edges[i - 1].Y1;
            w.WriteCoord(10, x, dbuToDrawingUnit);
            w.WriteCoord(20, y, dbuToDrawingUnit);
            // Bulge at vertex i describes the edge LEAVING vertex i (LayoutEdge's own convention) —
            // the last vertex has no outgoing edge and so never carries a bulge.
            if (i < edges.Count && edges[i].Kind == EdgeKind.Arc && edges[i].Bulge != 0)
                w.WriteDouble(42, edges[i].Bulge);
        }
    }

    // ── SPLINE (closed multi-segment Bezier chain — §1.2, cubic-bearing ring, no holes) ────────────

    private static void WriteClosedSplineChain(
        DxfGroupWriter w, string layer, List<RingEdge> ring, double dbuToDrawingUnit, DxfHandles handles, string ownerHandle)
    {
        // Elevate every Line edge to a degenerate Cubic (control points at exact 1/3, 2/3 along the
        // chord) so the WHOLE ring becomes one uniform Bezier chain — exact for both kinds.
        var ctrl = new List<(double X, double Y)>();
        foreach (var e in ring)
        {
            if (ctrl.Count == 0) ctrl.Add((e.X0, e.Y0));
            if (e.Kind == EdgeKind.Cubic)
            {
                ctrl.Add((e.C1X, e.C1Y));
                ctrl.Add((e.C2X, e.C2Y));
            }
            else
            {
                double c1x = e.X0 + (e.X1 - e.X0) / 3.0, c1y = e.Y0 + (e.Y1 - e.Y0) / 3.0;
                double c2x = e.X0 + (e.X1 - e.X0) * 2.0 / 3.0, c2y = e.Y0 + (e.Y1 - e.Y0) * 2.0 / 3.0;
                ctrl.Add((c1x, c1y));
                ctrl.Add((c2x, c2y));
            }
            ctrl.Add((e.X1, e.Y1));
        }

        int segments = ring.Count;
        int numCtrl = ctrl.Count; // 3*segments + 1

        WriteEntityHeader(w, "SPLINE", handles, ownerHandle, layer, "AcDbSpline");
        w.WriteInt(70, 8); // planar; not closed/periodic/rational bits (closure is implicit: last == first)
        w.WriteInt(71, 3); // degree
        int numKnots = numCtrl + 3 + 1;
        w.WriteInt(72, numKnots);
        w.WriteInt(73, numCtrl);
        w.WriteInt(74, 0);

        // Clamped Bezier-chain knot vector: [0,0,0,0, 1,1,1, 2,2,2, ..., k,k,k, k? ]
        // For k segments: knots = 4 zeros, then for i in 1..k-1 three copies of i, then 4 copies of k.
        for (int i = 0; i < 4; i++) w.WriteDouble(40, 0.0);
        for (int seg = 1; seg < segments; seg++)
            for (int i = 0; i < 3; i++) w.WriteDouble(40, seg);
        for (int i = 0; i < 4; i++) w.WriteDouble(40, segments);

        foreach (var (x, y) in ctrl)
        {
            w.WriteDouble(10, x * dbuToDrawingUnit);
            w.WriteDouble(20, y * dbuToDrawingUnit);
        }
    }

    // ── HATCH (§3.1a holes; native multi-loop boundary) ──────────────────────

    private static void WriteHatch(
        DxfGroupWriter w, string layer, List<RingEdge> outer, List<long[]> holes, double dbuToDrawingUnit,
        DxfHandles handles, string ownerHandle)
    {
        WriteEntityHeader(w, "HATCH", handles, ownerHandle, layer, "AcDbHatch");
        w.WriteCoord(10, 0, dbuToDrawingUnit); w.WriteCoord(20, 0, dbuToDrawingUnit); w.WriteDouble(30, 0.0);
        w.WriteDouble(210, 0.0); w.WriteDouble(220, 0.0); w.WriteDouble(230, 1.0);
        w.WriteString(2, SplinePatternName);
        w.WriteInt(70, 1); // solid fill
        w.WriteInt(71, 0); // non-associative

        w.WriteInt(91, 1 + holes.Count);
        WriteHatchLoop(w, outer, isOuter: true, dbuToDrawingUnit);
        foreach (var hole in holes)
            WriteHatchLoop(w, Ring(hole, null), isOuter: false, dbuToDrawingUnit);

        w.WriteInt(75, 0); // hatch style: normal
        w.WriteInt(76, 1); // pattern type: predefined
        w.WriteInt(98, 0); // seed points
    }

    /// <summary>Boundary path type flag (group 92) bits — verified against the public DXF spec
    /// (Autodesk's own HATCH boundary-path-data reference, cross-checked against ezdxf's independent
    /// documentation): 0=Default, 1=External, 2=Polyline, 4=Derived, 8=Textbox, 16=Outermost. An
    /// earlier draft of this file used 2/4 instead of 1/2 (transposed) — since both this writer AND
    /// <c>DxfReader.ParseHatch</c> originally tested the SAME wrong bit, our own round-trip tests
    /// passed anyway (exactly the "correct by our own reader's standards" trap L4a's gate 12 warns
    /// about) while a real third-party HATCH reader — which correctly tests bit 2 for "Polyline" —
    /// would have misread every hole-free polyline-type loop as an edge-type boundary and desynced.
    /// Fixed in both files together; see <c>DxfReaderTests</c>/<c>LayoutDxfRoundTripTests</c> for the
    /// regression pin.</summary>
    private const int LoopFlagPolyline = 2;
    private const int LoopFlagExternal = 1;

    private static void WriteHatchLoop(DxfGroupWriter w, List<RingEdge> ring, bool isOuter, double dbuToDrawingUnit)
    {
        bool anyCurved = ring.Any(e => e.Kind != EdgeKind.Line);

        if (!anyCurved)
        {
            w.WriteInt(92, LoopFlagPolyline | (isOuter ? LoopFlagExternal : 0));
            w.WriteInt(72, 0); // no bulge
            w.WriteInt(73, 1); // closed
            w.WriteInt(93, ring.Count);
            foreach (var e in ring)
            {
                w.WriteCoord(10, e.X0, dbuToDrawingUnit);
                w.WriteCoord(20, e.Y0, dbuToDrawingUnit);
            }
            w.WriteInt(97, 0);
            return;
        }

        w.WriteInt(92, isOuter ? LoopFlagExternal : 0); // no polyline bit — edge-type boundary
        w.WriteInt(93, ring.Count);
        foreach (var e in ring)
        {
            switch (e.Kind)
            {
                case EdgeKind.Line:
                    w.WriteInt(72, 1);
                    w.WriteCoord(10, e.X0, dbuToDrawingUnit); w.WriteCoord(20, e.Y0, dbuToDrawingUnit);
                    w.WriteCoord(11, e.X1, dbuToDrawingUnit); w.WriteCoord(21, e.Y1, dbuToDrawingUnit);
                    break;

                case EdgeKind.Arc:
                    {
                        var arc = LayoutArc.FromBulge(e.X0, e.Y0, e.X1, e.Y1, e.Bulge);
                        double startDeg = arc.StartAngle * 180.0 / Math.PI;
                        double sweepDeg = arc.Sweep * 180.0 / Math.PI;
                        double endDeg = startDeg + sweepDeg;
                        w.WriteInt(72, 2);
                        w.WriteDouble(10, arc.Cx * dbuToDrawingUnit); w.WriteDouble(20, arc.Cy * dbuToDrawingUnit);
                        w.WriteDouble(40, arc.R * dbuToDrawingUnit);
                        w.WriteDouble(50, startDeg);
                        w.WriteDouble(51, endDeg);
                        w.WriteInt(73, sweepDeg >= 0 ? 1 : 0);
                    }
                    break;

                case EdgeKind.Cubic:
                    w.WriteInt(72, 4);
                    w.WriteInt(94, 3);
                    w.WriteInt(73, 0); // rational
                    w.WriteInt(74, 0); // periodic
                    w.WriteInt(95, 8);
                    w.WriteInt(96, 4);
                    for (int i = 0; i < 4; i++) w.WriteDouble(40, 0.0);
                    for (int i = 0; i < 4; i++) w.WriteDouble(40, 1.0);
                    w.WriteCoord(10, e.X0, dbuToDrawingUnit); w.WriteCoord(20, e.Y0, dbuToDrawingUnit);
                    w.WriteCoord(10, e.C1X, dbuToDrawingUnit); w.WriteCoord(20, e.C1Y, dbuToDrawingUnit);
                    w.WriteCoord(10, e.C2X, dbuToDrawingUnit); w.WriteCoord(20, e.C2Y, dbuToDrawingUnit);
                    w.WriteCoord(10, e.X1, dbuToDrawingUnit); w.WriteCoord(20, e.Y1, dbuToDrawingUnit);
                    w.WriteInt(97, 0);
                    break;
            }
        }
        w.WriteInt(97, 0);
    }

    // ── TEXT ──────────────────────────────────────────────────────────────────

    private static void WriteText(DxfGroupWriter w, LabelShape label, string layer, double dbuToDrawingUnit, DxfHandles handles, string ownerHandle)
    {
        WriteEntityHeader(w, "TEXT", handles, ownerHandle, layer, "AcDbText");
        w.WriteCoord(10, label.X, dbuToDrawingUnit);
        w.WriteCoord(20, label.Y, dbuToDrawingUnit);
        w.WriteDouble(40, label.Height * dbuToDrawingUnit);
        w.WriteEscapedString(1, label.Text);
        w.WriteDouble(50, label.RotationDegrees);
        w.WriteInt(70, label.IsPort ? 1 : 0); // not a real DXF TEXT field — our own port marker, mirrors
                                              // GdsiiWriter's TEXTTYPE convention for the same purpose.
        w.WriteString(100, "AcDbText"); // TEXT's own spec quirk: the AcDbText subclass marker repeats
                                        // after the base fields (normally bracketing alignment groups
                                        // this writer never emits) — omitting the repeat is itself
                                        // non-conformant for a real R13+ reader.
    }

    private static double DegreesOf(LayoutRotation r) => r switch
    {
        LayoutRotation.R90 => 90.0,
        LayoutRotation.R180 => 180.0,
        LayoutRotation.R270 => 270.0,
        _ => 0.0,
    };

    // ── INSERT (instances + arrays) ───────────────────────────────────────────

    private static void WriteInsert(
        DxfGroupWriter w, LayoutInstance inst, IReadOnlyDictionary<string, string> blockNames,
        double dbuToDrawingUnit, DxfHandles handles, string ownerHandle)
    {
        var (xscale, yscale, rotationDeg) = DxfTransformCodec.ToDxf(inst.MirrorX, inst.RotationDegrees, inst.Mag);
        bool isArray = inst.Rows > 1 || inst.Cols > 1;
        string targetName = blockNames.TryGetValue(inst.CellRef, out var mangled) ? mangled : inst.CellRef;

        // A row/column ARRAY insert uses the DXF's own distinct subclass name for the purpose
        // (AcDbMInsertBlock) — a plain single placement uses AcDbBlockReference.
        WriteEntityHeader(w, "INSERT", handles, ownerHandle, "0", isArray ? "AcDbMInsertBlock" : "AcDbBlockReference");
        w.WriteEscapedString(2, targetName);
        w.WriteCoord(10, inst.X, dbuToDrawingUnit);
        w.WriteCoord(20, inst.Y, dbuToDrawingUnit);
        w.WriteDouble(41, xscale);
        w.WriteDouble(42, yscale);
        w.WriteDouble(50, rotationDeg);
        if (isArray)
        {
            w.WriteInt(70, Math.Max(1, inst.Cols));
            w.WriteInt(71, Math.Max(1, inst.Rows));
            w.WriteDouble(44, inst.PitchX * dbuToDrawingUnit);
            w.WriteDouble(45, inst.PitchY * dbuToDrawingUnit);
        }
    }
}
