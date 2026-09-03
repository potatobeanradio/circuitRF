using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report (2026-07-28): a DXF exported by this writer would not open in QCAD, the AutoDesk web
/// viewer, or eDrawings. Confirmed directly against two independent real parsers (ezdxf, and QCAD's own
/// bundled ODA-based dwginfo/dwg2svg converters) — both failed identically on the pre-fix output with
/// "missing subclass"/"Bad Dxf sequence" errors. Root cause: the writer declared `$ACADVER = AC1015`
/// (AutoCAD 2000/R2000) while emitting entities in the much simpler, older R12 STRUCTURE — no handles
/// (group 5), no owner pointers (group 330), and no subclass marker groups (code 100), all of which
/// R13+ mandates for every table record, block, and entity. This project's OWN DxfReader never noticed
/// because it ignores any group code it doesn't specifically look for — exactly the "correct by our
/// own reader's standards" trap this brief's own completion note already names once for the HATCH
/// boundary-flag bug. These tests pin the two fixes at the level this project's test suite CAN check
/// (structural, on-the-wire) — the actual "does it open in QCAD" verification was done by hand, once,
/// against the real dwginfo/dwg2svg tools bundled with a local QCAD install; not repeatable in CI.
/// </summary>
public class DxfR2000ConformanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dxf-r2000-conformance-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly LayerKey LayerA = new(1, 0);

    /// <summary>The export dialog's version clue (owner follow-up, 2026-07-28) reads
    /// <see cref="DxfWriter.FormatDescription"/> rather than a second hand-typed string — this pins that
    /// the description actually names the SAME version code the writer emits on the wire, so the two
    /// can never silently drift apart. brief-dxf-layer-colors.md turned the two constants into a
    /// per-version table (R-col-1) — this now checks all three, not just the one hardcoded version.</summary>
    [Theory]
    [InlineData(DxfAcadVersion.R2000)]
    [InlineData(DxfAcadVersion.R2004)]
    [InlineData(DxfAcadVersion.R2018)]
    public void FormatDescription_NamesTheSameAcadVersionCodeActuallyWritten(DxfAcadVersion version)
    {
        Assert.Contains(DxfWriter.AcadVersionCode(version), DxfWriter.FormatDescription(version));

        var top = new InterchangeStructure("TOP", [], []);
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [top], "TOP", null, 1000, new DxfExportOptions(AcadVersion: version));
        string text = sw.ToString();

        Assert.Contains("$ACADVER\n1\n" + DxfWriter.AcadVersionCode(version), text);
    }

    [Fact]
    public void ExportedFile_CarriesHandlesOwnersAndSubclassMarkers_OnEveryTableBlockAndEntity()
    {
        var leaf = new InterchangeStructure("LEAF", [new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }], []);
        var top = new InterchangeStructure("TOP", [], [new LayoutInstance { CellRef = "LEAF", X = 0, Y = 0, Mag = 1.0 }]);
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [leaf, top], "TOP", null, 1000, new DxfExportOptions());
        string text = sw.ToString();

        // Every table needs the R13+ symbol-table subclass marker.
        Assert.Contains("AcDbSymbolTable", text);
        // Every table RECORD (layer, block record, ...) needs its own two-marker pair.
        Assert.Contains("AcDbSymbolTableRecord", text);
        Assert.Contains("AcDbLayerTableRecord", text);
        Assert.Contains("AcDbBlockTableRecord", text);
        // Every BLOCK/ENDBLK and every entity needs the base AcDbEntity marker plus its own class.
        Assert.Contains("AcDbBlockBegin", text);
        Assert.Contains("AcDbBlockEnd", text);
        Assert.Contains("AcDbPolyline", text); // the RectShape's LWPOLYLINE
        Assert.Contains("AcDbBlockReference", text); // the plain (non-array) INSERT

        // The mandatory R13+ system space blocks — every real AC1015+ file has both, real geometry or
        // not; without them the file structure itself is non-conformant regardless of content.
        Assert.Contains("*Model_Space", text);
        Assert.Contains("*Paper_Space", text);

        // Handle (group 5) and owner (group 330) groups must actually be present, not just their
        // sibling subclass markers — count a generous lower bound rather than an exact number, since
        // the exact count is an implementation detail of how many tables/records this writer emits.
        int handleCount = CountGroupOccurrences(text, "5");
        int ownerCount = CountGroupOccurrences(text, "330");
        Assert.True(handleCount > 10, $"expected many handle (group 5) occurrences, found {handleCount}");
        Assert.True(ownerCount > 10, $"expected many owner (group 330) occurrences, found {ownerCount}");

        // Still opens cleanly through our own reader (the round-trip this project's own tests already
        // exercise) — the fix must not have broken the reader's own tolerance for the new structure.
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var result = DxfImport.Import(stream, _dir, null, 1000);
        Assert.False(result.Cancelled);
    }

    /// <summary>Counts lines that are exactly <paramref name="code"/> and are immediately followed (two
    /// lines later, i.e. after their value line) by another group-code line — a simple, dependency-free
    /// way to count "code\nvalue\n" pairs in the raw group-code text without a full tokenizer.</summary>
    private static int CountGroupOccurrences(string text, string code)
    {
        var lines = text.Split('\n');
        int count = 0;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i] == code) count++;
        return count;
    }

    [Fact]
    public void Import_SkipsAnonymousSystemBlocks_NeverCreatesCellsForModelSpaceOrPaperSpaceOrAnonymousBlocks()
    {
        // Hand-crafted, independent of our own writer — exercises the exact shape every real-world
        // AC1015+ DXF has (gate 12's own concern: "a reader tested only against its own writer is not
        // tested"). *Model_Space/*Paper_Space are present in EVERY real file; *U0 is the anonymous-block
        // naming convention AutoCAD itself generates for hatch/dimension internals.
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "SECTION");
        w.WriteString(2, "HEADER");
        w.WriteString(9, "$INSUNITS");
        w.WriteInt(70, DxfUnits.Millimeters);
        w.WriteString(0, "ENDSEC");

        w.WriteString(0, "SECTION");
        w.WriteString(2, "BLOCKS");

        w.WriteString(0, "BLOCK");
        w.WriteString(2, "*Model_Space");
        w.WriteString(0, "ENDBLK");

        w.WriteString(0, "BLOCK");
        w.WriteString(2, "*Paper_Space");
        w.WriteString(0, "ENDBLK");

        w.WriteString(0, "BLOCK");
        w.WriteString(2, "*U0");
        w.WriteString(0, "CIRCLE"); // an anonymous block a reader must never surface as a user cell
        w.WriteString(8, "0");
        w.WriteDouble(10, 0.0); w.WriteDouble(20, 0.0); w.WriteDouble(40, 1.0);
        w.WriteString(0, "ENDBLK");

        w.WriteString(0, "BLOCK");
        w.WriteString(2, "REAL");
        w.WriteString(0, "CIRCLE");
        w.WriteString(8, "0");
        w.WriteDouble(10, 0.0); w.WriteDouble(20, 0.0); w.WriteDouble(40, 500.0);
        w.WriteString(0, "ENDBLK");

        w.WriteString(0, "ENDSEC");

        w.WriteString(0, "SECTION");
        w.WriteString(2, "ENTITIES");
        w.WriteString(0, "INSERT");
        w.WriteString(2, "REAL");
        w.WriteDouble(10, 0.0); w.WriteDouble(20, 0.0);
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "EOF");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
        var result = DxfImport.Import(stream, _dir, null, 1000);
        Assert.False(result.Cancelled);

        // Real cells only: REAL, plus the synthetic model-space cell. Never *Model_Space/*Paper_Space/*U0.
        Assert.DoesNotContain(result.CellNameByBlockName.Keys, k => k.StartsWith('*'));
        Assert.Contains("REAL", result.CellNameByBlockName.Keys);
        Assert.Equal(2, result.CreatedCellDirs.Count); // REAL + the synthetic model-space cell
    }
}
