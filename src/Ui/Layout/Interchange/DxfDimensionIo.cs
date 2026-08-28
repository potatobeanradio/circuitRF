// Writing in-design RULER annotations to DXF as genuine ALIGNED DIMENSION entities —
// docs/design/layout-view.md §9B.10 / R-rul-18. A partial of the DXF write side, kept in its own file
// the way DxfWireIo keeps the bond-wire half of the same writer.
//
// WHY A DIMENSION AND NOT A LINE PLUS SOME TEXT. The cheap alternative renders identically in every
// viewer and is the wrong artifact: it arrives in the recipient's CAD as loose geometry — it does not
// report a measurement, it does not update if they stretch the drawing, it cannot be styled, and it
// does not appear as a dimension in anything that enumerates them. The reason to export a measurement
// at all is that the recipient wants a measurement, so it goes out as the object DXF has for exactly
// that.
//
// R-rul-18c — VALIDATE AGAINST A READER THAT IS NOT OURS. DIMENSION is the fussiest entity in this
// writer: three subclass markers, an anonymous block whose entities' owner handles must point at its
// own BLOCK_RECORD, and a DIMSTYLE reference that must resolve. A malformed one can make the whole
// file unreadable rather than merely drawing wrong. DxfWriter's own header note records the same rule
// and names the third-party readers the R13+ subclass-marker work was checked against (`ezdxf`, and
// QCAD's ODA-based `dwginfo`/`dwg2svg`) — apply it here rather than trusting a round-trip through our
// own DxfReader, which dispatches on the leading `0 <TYPE>` token and ignores every group code it does
// not specifically look for, and would therefore accept a file no other reader will open.

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>One ruler, resolved for export: its world text height and which DIMSTYLE it lands on.</summary>
internal readonly record struct DxfRulerPlan(RulerAnnotation Ruler, double TextHeightDbu, string StyleName);

public static partial class DxfWriter
{
    /// <summary>§9B.10: rulers go out on their own layer, through the existing <c>extraLayerNames</c>
    /// seam <c>DxfWireIo</c> already uses — so the recipient can freeze or delete every one of them at
    /// once.</summary>
    internal const string RulerLayerName = "RULER";

    /// <summary>
    /// R-rul-18b: <b>a <c>Fixed</c> ruler's point size has no meaning in DXF</b> — a DXF is a
    /// world-coordinate drawing with no screen and no zoom — so it is resolved ONCE, at export, to the
    /// height that occupies the same fraction of the drawing that its point size occupies of a nominal
    /// viewport: <c>extentsDiagonal × TextSizePt / NominalViewportDiagonalPt</c>. That makes it legible
    /// when the recipient zooms to extents, which is what the mode meant on screen.
    ///
    /// <para>The constant is stated HERE and nowhere else. A nominal viewport of 1024 × 768 device
    /// pixels at 96 dpi is 1280 px on the diagonal, which is 960 typographic points.</para>
    /// </summary>
    internal const double NominalViewportDiagonalPt = 960.0;

    /// <summary>The measured world text height a ruler exports at — <c>Scaled</c> uses its own stored
    /// height directly, <c>Fixed</c> resolves against the drawing extents per R-rul-18b.</summary>
    internal static double ResolveExportTextHeightDbu(RulerAnnotation ruler, Bbox extents)
    {
        if (ruler.SizeMode == RulerSizeMode.Scaled)
            return Math.Max(1.0, ruler.TextHeightDbu);

        double w = extents.IsEmpty ? 0 : (double)extents.MaxX - extents.MinX;
        double h = extents.IsEmpty ? 0 : (double)extents.MaxY - extents.MinY;
        double diagonal = Math.Sqrt(w * w + h * h);
        if (diagonal <= 0) return Math.Max(1.0, ruler.TextHeightDbu > 0 ? ruler.TextHeightDbu : 1.0);

        return Math.Max(1.0, diagonal * Math.Max(1.0, ruler.TextSizePt) / NominalViewportDiagonalPt);
    }

    /// <summary>
    /// One DIMSTYLE record per DISTINCT (text height, font style) pair actually used, named
    /// <c>CIRCUITRF_1</c>, <c>CIRCUITRF_2</c>, … Assigned in first-use order so the mapping is stable
    /// across two exports of the same document.
    ///
    /// <para><b>Per-entity <c>DSTYLE</c> XDATA overrides are the alternative and are rejected</b>: they
    /// are the fussiest corner of the format, and a handful of named styles is both conformant and
    /// legible to the recipient — who can restyle every ruler at once by editing one.</para>
    /// </summary>
    internal static List<DxfRulerPlan> PlanRulers(IReadOnlyList<RulerAnnotation> rulers, Bbox extents)
    {
        var plans = new List<DxfRulerPlan>(rulers.Count);
        var styleByKey = new Dictionary<(long HeightKey, LabelFontStyle Style), string>();

        foreach (var ruler in rulers)
        {
            // Degenerate rulers never reach the model (§9B.5 discards a zero-length one at placement),
            // but a hand-edited .clay could carry one and a DIMENSION with coincident extension-line
            // origins is not something to hand a stranger's CAD.
            if (ruler.X1 == ruler.X2 && ruler.Y1 == ruler.Y2) continue;

            double height = ResolveExportTextHeightDbu(ruler, extents);
            // Rounded to whole DBU for the grouping key only — two rulers whose resolved heights differ
            // in the tenth decimal place are one style, not two.
            var key = ((long)Math.Round(height), ruler.Style);
            if (!styleByKey.TryGetValue(key, out var name))
            {
                name = $"CIRCUITRF_{styleByKey.Count + 1}";
                styleByKey[key] = name;
            }
            plans.Add(new DxfRulerPlan(ruler, height, name));
        }
        return plans;
    }

    /// <summary>The distinct DIMSTYLE records <paramref name="plans"/> reference, in first-use
    /// order — name plus the text height that record carries.</summary>
    internal static List<(string Name, double TextHeightDbu)> DistinctDimStyles(IReadOnlyList<DxfRulerPlan> plans)
    {
        var result = new List<(string, double)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in plans)
            if (seen.Add(p.StyleName)) result.Add((p.StyleName, p.TextHeightDbu));
        return result;
    }

    /// <summary>The anonymous block name for the n-th exported ruler. <c>*D#</c> is DXF's own
    /// convention for a dimension's picture block, and <b>our importer already skips any block whose
    /// name starts with <c>*</c></b> (see <c>DxfReader.ParseBlocksSection</c>), which is what makes
    /// R-rul-19 true by construction rather than by a new rule.</summary>
    internal static string RulerBlockName(int index) => $"*D{index + 1}";

    /// <summary>
    /// The DIMENSION's own text override (group <c>1</c>), or null when there is nothing to add.
    ///
    /// <para><b>R-rul-18a: it ALWAYS begins with <c>&lt;&gt;</c></b> — DXF's placeholder for "the value
    /// you measured" — so the recipient's CAD still recomputes the number if they move an endpoint.
    /// Extra lines follow as MTEXT paragraph breaks. A ruler with neither caption nor Delta line omits
    /// group <c>1</c> entirely and gets the pure measured value. <b>Never write the formatted distance
    /// as literal text</b>: that dead number is precisely what made LINE + TEXT the wrong answer.</para>
    /// </summary>
    internal static string? RulerTextOverride(RulerAnnotation ruler, LayoutUnit unit, int dbuPerMicron)
    {
        var extra = new List<string>(2);
        if (ruler.ShowComponents)
        {
            long dx = Math.Abs(ruler.X2 - ruler.X1);
            long dy = Math.Abs(ruler.Y2 - ruler.Y1);
            extra.Add($"Δx {ruler.FormatLength(dx, unit, dbuPerMicron)}"
                      + $"  Δy {ruler.FormatLength(dy, unit, dbuPerMicron)}");
        }
        if (!string.IsNullOrWhiteSpace(ruler.Caption)) extra.Add(ruler.Caption!);

        return extra.Count == 0 ? null : "<>" + string.Concat(extra.Select(e => "\\P" + e));
    }

    /// <summary>The text midpoint (groups 11/21) — the same perpendicular offset the on-screen
    /// renderer uses, so the exported picture reads like the one the user placed.</summary>
    private static (double X, double Y) RulerTextMidpoint(RulerAnnotation r, double textHeight)
    {
        double dx = (double)r.X2 - r.X1, dy = (double)r.Y2 - r.Y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return (r.X1, r.Y1);

        double nx = -dy / len, ny = dx / len;
        if (ny < 0) { nx = -nx; ny = -ny; }

        double midX = (r.X1 + (double)r.X2) / 2.0;
        double midY = (r.Y1 + (double)r.Y2) / 2.0;
        double push = textHeight * 0.9;
        return (midX + nx * push, midY + ny * push);
    }

    private static void WriteDimstyleRecord(DxfGroupWriter w, DxfHandles handles, string tableHandle,
                                            string name, double textHeightDrawingUnits)
    {
        w.WriteString(0, "DIMSTYLE");
        // DIMSTYLE is the ONE table record whose handle group is 105, not 5 — a spec quirk, and getting
        // it wrong is one of the ways a strict reader rejects the whole file.
        w.WriteString(105, handles.Next());
        w.WriteString(330, tableHandle);
        w.WriteString(100, "AcDbSymbolTableRecord");
        w.WriteString(100, "AcDbDimStyleTableRecord");
        w.WriteEscapedString(2, name);
        w.WriteInt(70, 0);
        w.WriteDouble(140, textHeightDrawingUnits);          // DIMTXT — text height
        w.WriteDouble(141, textHeightDrawingUnits * 0.6);    // DIMCEN
        w.WriteDouble(144, 1.0);                             // DIMLFAC — measurement scale factor
        w.WriteInt(271, 4);                                  // DIMDEC — decimal places
        w.WriteInt(179, 0);                                  // DIMADEC
        w.WriteString(3, "");                                // DIMPOST — no prefix/suffix
        w.WriteString(340, "0");                             // DIMTXSTY — the "Standard" STYLE, unresolved
    }

    /// <summary>
    /// The DIMSTYLE table. <b>This method used to write zero records, and said so in a comment whose
    /// stated premise was "this codebase's own export never creates a dimension entity, so there is
    /// nothing for one to reference".</b> That premise is exactly what §9B.10 retires: it now writes
    /// one record per distinct (text height, font style) pair actually used, and an export with no
    /// rulers still writes an empty table — which is correct and is what every previous export
    /// produced.
    /// </summary>
    private static void WriteDimstyleTable(DxfGroupWriter w, DxfHandles handles,
                                           IReadOnlyList<(string Name, double TextHeightDbu)> styles,
                                           double dbuToDrawingUnit)
    {
        // The table itself (with its extra AcDbDimStyleTable subclass marker, unique to this one table
        // type per spec) must exist whether or not it has records.
        string tableHandle = WriteTableHeader(w, handles, "DIMSTYLE", styles.Count, extraSubclass: "AcDbDimStyleTable");
        foreach (var (name, height) in styles)
            WriteDimstyleRecord(w, handles, tableHandle, name, height * dbuToDrawingUnit);
        WriteTableFooter(w);
    }

    /// <summary>
    /// The anonymous <c>*D#</c> block for one ruler — the PICTURE a non-regenerating viewer draws.
    /// DXF's model is that the DIMENSION carries the semantics and this block carries the drawing;
    /// it holds the two extension lines, the dimension line with its end ticks, and the readout TEXT.
    ///
    /// <para>Every entity inside is owned by this block's OWN <c>BLOCK_RECORD</c> handle
    /// (R-rul-18c) — pointing them at the table's handle instead is one of the malformations that makes
    /// a whole file unreadable rather than merely drawing wrong.</para>
    /// </summary>
    private static void WriteRulerBlock(
        DxfGroupWriter w, DxfHandles handles, DxfRulerPlan plan, string blockName, string ownerHandle,
        LayoutUnit unit, int dbuPerMicron, double dbuToDrawingUnit)
    {
        var r = plan.Ruler;
        WriteBlockHeader(w, handles, blockName, ownerHandle);

        double dx = (double)r.X2 - r.X1, dy = (double)r.Y2 - r.Y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len;
        double nx = -uy, ny = ux;
        double tick = plan.TextHeightDbu * 0.5;

        void Line(double x1, double y1, double x2, double y2)
        {
            WriteEntityHeader(w, "LINE", handles, ownerHandle, RulerLayerName, "AcDbLine");
            w.WriteDouble(10, x1 * dbuToDrawingUnit);
            w.WriteDouble(20, y1 * dbuToDrawingUnit);
            w.WriteDouble(30, 0.0);
            w.WriteDouble(11, x2 * dbuToDrawingUnit);
            w.WriteDouble(21, y2 * dbuToDrawingUnit);
            w.WriteDouble(31, 0.0);
        }

        // The dimension line itself, then a tick across each end. Extension lines are zero-length here
        // (the dimension line runs between the two measured points themselves, which is what a ruler
        // is), so the ticks ARE the extension marks.
        Line(r.X1, r.Y1, r.X2, r.Y2);
        Line(r.X1 + nx * tick, r.Y1 + ny * tick, r.X1 - nx * tick, r.Y1 - ny * tick);
        Line(r.X2 + nx * tick, r.Y2 + ny * tick, r.X2 - nx * tick, r.Y2 - ny * tick);

        // The readout, as the picture only — the LIVE measurement is the DIMENSION's own group 42 plus
        // its `<>` text override. This TEXT exists solely so a viewer that does not regenerate
        // dimensions still shows something; a regenerating one replaces it.
        var (tx, ty) = RulerTextMidpoint(r, plan.TextHeightDbu);
        string readout = $"{r.FormatLength(r.DistanceDbu, unit, dbuPerMicron)} " + LayoutUnits.Suffix(unit);

        WriteEntityHeader(w, "TEXT", handles, ownerHandle, RulerLayerName, "AcDbText");
        w.WriteDouble(10, tx * dbuToDrawingUnit);
        w.WriteDouble(20, ty * dbuToDrawingUnit);
        w.WriteDouble(30, 0.0);
        w.WriteDouble(40, plan.TextHeightDbu * dbuToDrawingUnit);
        // Generated, not authored: this is a number we formatted plus a unit suffix we chose. A "µm"
        // here is still escaped (correctly — \U+00B5 is AutoCAD's own convention), but it is not a
        // fidelity note about the user's drawing and must not be reported as one.
        w.WriteGeneratedString(1, readout);
        w.WriteDouble(50, 0.0);   // §9B.4: always upright, whatever the ruler's angle
        w.WriteInt(72, 1);        // horizontal alignment: centre
        w.WriteDouble(11, tx * dbuToDrawingUnit);
        w.WriteDouble(21, ty * dbuToDrawingUnit);
        w.WriteDouble(31, 0.0);
        w.WriteString(100, "AcDbText"); // TEXT's own spec quirk — the subclass marker repeats

        WriteBlockFooter(w, handles, ownerHandle);
    }

    /// <summary>
    /// One <c>DIMENSION</c> entity per ruler, on the <c>RULER</c> layer, with subclass markers
    /// <c>AcDbDimension</c> + <c>AcDbAlignedDimension</c> — an ALIGNED dimension (measuring along its
    /// own axis) rather than a rotated/linear one, because a ruler's whole point is the distance
    /// between two endpoints at whatever angle they happen to lie.
    ///
    /// <para>Group <c>70</c> is <c>1 | 32</c>: aligned, and the block belongs to this dimension alone.</para>
    /// </summary>
    private static void WriteRulerDimension(
        DxfGroupWriter w, DxfHandles handles, DxfRulerPlan plan, string blockName, string ownerHandle,
        LayoutUnit unit, int dbuPerMicron, double dbuToDrawingUnit)
    {
        var r = plan.Ruler;
        var (tx, ty) = RulerTextMidpoint(r, plan.TextHeightDbu);
        double dx = (double)r.X2 - r.X1, dy = (double)r.Y2 - r.Y1;

        WriteEntityHeader(w, "DIMENSION", handles, ownerHandle, RulerLayerName, "AcDbDimension");
        w.WriteEscapedString(2, blockName);                    // the anonymous block that pictures it
        w.WriteCoord(10, r.X2, dbuToDrawingUnit);              // dimension-line definition point
        w.WriteCoord(20, r.Y2, dbuToDrawingUnit);
        w.WriteDouble(30, 0.0);
        w.WriteDouble(11, tx * dbuToDrawingUnit);              // text midpoint
        w.WriteDouble(21, ty * dbuToDrawingUnit);
        w.WriteDouble(31, 0.0);
        w.WriteInt(70, 1 | 32);
        w.WriteInt(71, 5);                                     // attachment point: middle-centre
        w.WriteDouble(42, r.DistanceDbu * dbuToDrawingUnit);   // the measured value

        if (RulerTextOverride(r, unit, dbuPerMicron) is { } text)
        {
            // The override MIXES our text with theirs: the `<>` placeholder and the Δx / Δy prefixes
            // are ours, the caption is the user's. Only the caption can make this a note they can act
            // on, so only a non-ASCII CAPTION counts. Either way the whole string is escaped
            // identically — this decides what is REPORTED, never what is written.
            bool captionIsUsersAndNonAscii =
                r.Caption is { } caption && !string.IsNullOrWhiteSpace(caption)
                && caption.Any(c => c > 0x7F);

            if (captionIsUsersAndNonAscii) w.WriteEscapedString(1, text);
            else w.WriteGeneratedString(1, text);
        }

        w.WriteEscapedString(3, plan.StyleName);               // the DIMSTYLE this resolves to

        w.WriteString(100, "AcDbAlignedDimension");
        w.WriteCoord(13, r.X1, dbuToDrawingUnit);              // first extension-line origin
        w.WriteCoord(23, r.Y1, dbuToDrawingUnit);
        w.WriteDouble(33, 0.0);
        w.WriteCoord(14, r.X2, dbuToDrawingUnit);              // second extension-line origin
        w.WriteCoord(24, r.Y2, dbuToDrawingUnit);
        w.WriteDouble(34, 0.0);

        // GROUP 50 — THE MEASUREMENT DIRECTION, AND IT IS NOT OPTIONAL. R-rul-18c earned its keep
        // here: a reader that is not ours (ezdxf 1.4.4) computes an aligned dimension's measurement
        // by PROJECTING the 13→14 vector onto the ray this angle names, defaulting to 0 — so a file
        // written without it reports the HORIZONTAL COMPONENT of every ruler, and a vertical ruler
        // measures exactly zero. The file opened, audited clean, and drew plausibly the whole time;
        // only asking a real reader for the number exposed it.
        w.WriteDouble(50, LayoutAngle.Normalize(Math.Atan2(dy, dx) * 180.0 / Math.PI));
    }

    /// <summary>Writes every ruler's DIMENSION into ENTITIES (model space) — beside the root INSERT,
    /// not inside a cell's block, because a ruler is a statement about the drawing rather than part of
    /// any cell's definition. Returns how many were written.</summary>
    private static int WriteRulerDimensions(
        DxfGroupWriter w, DxfHandles handles, IReadOnlyList<DxfRulerPlan> plans,
        IReadOnlyDictionary<string, string> blockRecordHandles, string modelSpaceHandle,
        LayoutUnit unit, int dbuPerMicron, double dbuToDrawingUnit)
    {
        for (int i = 0; i < plans.Count; i++)
            WriteRulerDimension(w, handles, plans[i], RulerBlockName(i), modelSpaceHandle,
                                unit, dbuPerMicron, dbuToDrawingUnit);
        _ = blockRecordHandles;
        return plans.Count;
    }

    private static void WriteRulerBlocks(
        DxfGroupWriter w, DxfHandles handles, IReadOnlyList<DxfRulerPlan> plans,
        IReadOnlyDictionary<string, string> blockRecordHandles,
        LayoutUnit unit, int dbuPerMicron, double dbuToDrawingUnit)
    {
        for (int i = 0; i < plans.Count; i++)
        {
            string name = RulerBlockName(i);
            WriteRulerBlock(w, handles, plans[i], name, blockRecordHandles[name],
                            unit, dbuPerMicron, dbuToDrawingUnit);
        }
    }

    /// <summary>One Messages note per export (§9B.10), stating how many rulers were written as
    /// dimensions AND — because R-rul-18b makes it a surprise the second time someone exports — that a
    /// <c>Fixed</c>-mode ruler's text height is resolved against the drawing's own extents.</summary>
    internal static string RulerExportNote(int count, bool anyFixed)
    {
        string head = count == 1
            ? "1 ruler written as an aligned DIMENSION on layer RULER."
            : $"{count} rulers written as aligned DIMENSIONs on layer RULER.";
        return anyFixed
            ? head + " Fixed-size ruler text has no meaning in a world-coordinate drawing, so its height"
                   + " was resolved once, here, against this drawing's own extents — a different extent"
                   + " gives a different height."
            : head;
    }
}
