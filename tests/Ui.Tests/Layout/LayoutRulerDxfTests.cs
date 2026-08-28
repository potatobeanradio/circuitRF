using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B.10 — gates 4 and 5. A ruler exports as a genuine aligned
/// <c>DIMENSION</c>, not as a line and some text.
///
/// <para><b>R-rul-18c says validate against a reader that is NOT ours, and this file does not
/// substitute for that.</b> These assertions check the STRUCTURE a conformant reader requires — both
/// subclass markers, a resolvable group-3 DIMSTYLE reference, a <c>*D#</c> block whose entities are
/// owned by their own <c>BLOCK_RECORD</c>, and a <c>RULER</c> layer table record — parsed from the raw
/// group codes rather than through <c>DxfReader</c>, which dispatches on the leading <c>0 &lt;TYPE&gt;</c>
/// token and ignores every group code it does not specifically look for and would therefore accept a
/// file no other reader will open. The third-party check is recorded in <c>src/Ui/RESOLVED.md</c>.</para>
/// </summary>
public class LayoutRulerDxfTests : System.IDisposable
{
    public LayoutRulerDxfTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static readonly LayerKey Metal = new(1, 0);

    private static string WriteDxf(IEnumerable<RulerAnnotation> rulers, LayoutUnit unit = LayoutUnit.Um)
    {
        var structure = new InterchangeStructure(
            "TOP",
            [new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 20_000 }],
            []);

        var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", null, 1000, new DxfExportOptions(), null, [.. rulers], unit);
        return sw.ToString();
    }

    /// <summary>Every (code, value) pair, in file order — the raw group stream, so an assertion can
    /// talk about structure without going through our own reader.</summary>
    private static List<(int Code, string Value)> Groups(string dxf)
    {
        var lines = dxf.Split('\n');
        var result = new List<(int Code, string Value)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), out int code)) result.Add((code, lines[i + 1].TrimEnd('\r')));
        return result;
    }

    /// <summary>The group run for the n-th entity of <paramref name="type"/>, up to the next
    /// <c>0</c> group.</summary>
    private static List<(int Code, string Value)> Entity(List<(int Code, string Value)> groups, string type, int nth = 0)
    {
        int seen = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Code != 0 || groups[i].Value != type) continue;
            if (seen++ != nth) continue;
            var run = new List<(int Code, string Value)>();
            for (int j = i + 1; j < groups.Count && groups[j].Code != 0; j++) run.Add(groups[j]);
            return run;
        }
        return [];
    }

    private static RulerAnnotation Bare() => new()
    {
        X1 = 2_000, Y1 = 3_000, X2 = 22_000, Y2 = 3_000,
        SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 1_500,
    };

    // ── Gate 4: a real DIMENSION ──────────────────────────────────────────────────────────────────

    [Fact]
    public void OneDimensionPerRuler_WithBothSubclassMarkers()
    {
        string dxf = WriteDxf([Bare(), new RulerAnnotation
        {
            X1 = 0, Y1 = 10_000, X2 = 0, Y2 = 18_000, SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 1_500,
        }]);
        var groups = Groups(dxf);

        Assert.Equal(2, groups.Count(g => g.Code == 0 && g.Value == "DIMENSION"));

        var dim = Entity(groups, "DIMENSION");
        Assert.Contains((100, "AcDbEntity"), dim);
        Assert.Contains((100, "AcDbDimension"), dim);
        Assert.Contains((100, "AcDbAlignedDimension"), dim);
        Assert.Contains((8, DxfWriter.RulerLayerName), dim);

        // 70 = 1 | 32 — aligned, and the block belongs to this dimension alone.
        Assert.Contains((70, "33"), dim);

        // The measured value, in DXF DRAWING UNITS — this writer's default $INSUNITS is millimetres,
        // so 20,000 DBU at 1,000 DBU/µm (= 20 µm) is 0.02 units. Asserted through the same
        // dbuToDrawingUnit conversion every other coordinate here goes through, so the dimension can
        // never disagree with the geometry it measures.
        double unit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(DxfUnits.DefaultPromptUnits, 1000);
        Assert.Equal(20_000 * unit, ParseAt(dim, 42), 9);

        // Both extension-line origins are the ruler's own endpoints.
        Assert.Equal(2_000 * unit, ParseAt(dim, 13), 9);
        Assert.Equal(3_000 * unit, ParseAt(dim, 23), 9);
        Assert.Equal(22_000 * unit, ParseAt(dim, 14), 9);
        Assert.Equal(3_000 * unit, ParseAt(dim, 24), 9);
    }

    private static double ParseAt(List<(int Code, string Value)> run, int code) =>
        double.Parse(run.First(g => g.Code == code).Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(20_000, 0, 0.0)]
    [InlineData(0, 18_000, 90.0)]
    [InlineData(-20_000, 0, 180.0)]
    [InlineData(26_000, 11_000, 22.9321)]
    public void EveryDimension_CarriesGroup50_TheMeasurementDirection(long dx, long dy, double expectedDeg)
    {
        // <b>The defect a non-circuitRF reader found</b> (R-rul-18c, ezdxf 1.4.4). An ALIGNED
        // DIMENSION's measurement is computed by PROJECTING the 13→14 vector onto the ray group 50
        // names, and group 50 defaults to 0 — so a file written without it reports the HORIZONTAL
        // COMPONENT of every ruler, and a vertical ruler measures exactly zero. The file opened,
        // audited clean and drew plausibly the whole time; only asking a real reader for the NUMBER
        // exposed it, which is the whole reason R-rul-18c refuses a round-trip through our own reader.
        var r = new RulerAnnotation
        {
            X1 = 1_000, Y1 = 1_000, X2 = 1_000 + dx, Y2 = 1_000 + dy,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 1_500,
        };
        var dim = Entity(Groups(WriteDxf([r])), "DIMENSION");
        Assert.Equal(expectedDeg, ParseAt(dim, 50), 3);
    }

    [Fact]
    public void TheGroup3DimstyleReference_Resolves_ToARecordInTheDimstyleTable()
    {
        string dxf = WriteDxf([Bare()]);
        var groups = Groups(dxf);

        var dim = Entity(groups, "DIMENSION");
        string styleName = dim.First(g => g.Code == 3).Value;
        Assert.Equal("CIRCUITRF_1", styleName);

        var record = Entity(groups, "DIMSTYLE");
        Assert.Contains((100, "AcDbDimStyleTableRecord"), record);
        Assert.Contains((2, styleName), record);
        // DIMSTYLE is the one table record whose handle group is 105, not 5.
        Assert.Contains(record, g => g.Code == 105);
        // DIMTXT — the text height the record carries, in drawing units.
        double unit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(DxfUnits.DefaultPromptUnits, 1000);
        Assert.Equal(1_500 * unit, ParseAt(record, 140), 9);
    }

    [Fact]
    public void OneDimstyleRecord_PerDistinctHeightAndStylePair()
    {
        var a = Bare();
        var b = Bare(); b.Y1 = b.Y2 = 8_000;                       // same height, same style
        var c = Bare(); c.Y1 = c.Y2 = 12_000; c.TextHeightDbu = 3_000;  // different height
        var d = Bare(); d.Y1 = d.Y2 = 16_000; d.Style = LabelFontStyle.Bold;  // different style

        var groups = Groups(WriteDxf([a, b, c, d]));
        var names = groups.Where(g => g.Code == 0 && g.Value == "DIMSTYLE").Count();
        Assert.Equal(3, names);

        // Rulers a and b share one style; c and d each get their own.
        var refs = new List<string>();
        for (int i = 0; i < 4; i++) refs.Add(Entity(groups, "DIMENSION", i).First(g => g.Code == 3).Value);
        Assert.Equal(refs[0], refs[1]);
        Assert.NotEqual(refs[0], refs[2]);
        Assert.NotEqual(refs[0], refs[3]);
        Assert.NotEqual(refs[2], refs[3]);
    }

    [Fact]
    public void NoRulers_StillWritesAnEmptyDimstyleTable_AndNoDimension()
    {
        var groups = Groups(WriteDxf([]));
        Assert.Contains(groups, g => g.Code == 2 && g.Value == "DIMSTYLE");
        Assert.DoesNotContain(groups, g => g.Code == 0 && g.Value == "DIMENSION");
        Assert.DoesNotContain(groups, g => g.Code == 0 && g.Value == "DIMSTYLE");
    }

    [Fact]
    public void EachRuler_GetsAnAnonymousBlock_WhoseEntitiesAreOwnedByItsOwnBlockRecord()
    {
        string dxf = WriteDxf([Bare()]);
        var groups = Groups(dxf);

        var dim = Entity(groups, "DIMENSION");
        string blockName = dim.First(g => g.Code == 2).Value;
        Assert.Equal("*D1", blockName);

        // The BLOCK_RECORD for it, and its handle.
        string? recordHandle = null;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Code != 0 || groups[i].Value != "BLOCK_RECORD") continue;
            var run = new List<(int Code, string Value)>();
            for (int j = i + 1; j < groups.Count && groups[j].Code != 0; j++) run.Add(groups[j]);
            if (run.Any(g => g.Code == 2 && g.Value == blockName))
            { recordHandle = run.First(g => g.Code == 5).Value; break; }
        }
        Assert.NotNull(recordHandle);

        // Every entity inside the *D1 BLOCK…ENDBLK run must be owned (330) by that record handle.
        int start = IndexOfBlockStart(groups, blockName);
        Assert.True(start >= 0, "the *D1 block must be written");
        int count = 0;
        for (int i = start; i < groups.Count; i++)
        {
            if (groups[i].Code == 0 && groups[i].Value == "ENDBLK")
            {
                Assert.Equal(recordHandle, groups[i + 2].Value);   // 5 handle, 330 owner
                break;
            }
            if (groups[i].Code != 0) continue;
            if (groups[i].Value is "LINE" or "TEXT")
            {
                count++;
                Assert.Equal(330, groups[i + 2].Code);
                Assert.Equal(recordHandle, groups[i + 2].Value);
                Assert.Equal(DxfWriter.RulerLayerName, groups[i + 4].Value);
            }
        }
        // The dimension line plus one tick at each end, and the readout.
        Assert.Equal(4, count);
    }

    private static int IndexOfBlockStart(List<(int Code, string Value)> groups, string blockName)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Code != 0 || groups[i].Value != "BLOCK") continue;
            for (int j = i + 1; j < groups.Count && groups[j].Code != 0; j++)
                if (groups[j].Code == 2 && groups[j].Value == blockName) return i;
        }
        return -1;
    }

    [Fact]
    public void TheRulerLayer_HasATableRecord()
    {
        var groups = Groups(WriteDxf([Bare()]));
        bool found = false;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Code != 0 || groups[i].Value != "LAYER") continue;
            for (int j = i + 1; j < groups.Count && groups[j].Code != 0; j++)
                if (groups[j].Code == 2 && groups[j].Value == DxfWriter.RulerLayerName) found = true;
        }
        Assert.True(found, "a strict reader rejects a file that references an undeclared layer");
    }

    // ── R-rul-18a: the caption/Δ ride in group 1, and the measurement stays live ──────────────────

    [Fact]
    public void ARulerWithACaption_CarriesAGroup1_BeginningWithThePlaceholder()
    {
        var r = Bare();
        r.Caption = "min trace gap";
        r.ShowComponents = true;

        var dim = Entity(Groups(WriteDxf([r])), "DIMENSION");
        string text = dim.First(g => g.Code == 1).Value;

        Assert.StartsWith("<>", text);
        Assert.Contains("\\P", text);
        Assert.Contains("min trace gap", text);
        // NEVER the formatted distance as literal text — that dead number is what made LINE + TEXT the
        // wrong answer, and it is what an override without `<>` would freeze in.
        Assert.DoesNotContain("20 µm", text);
    }

    [Fact]
    public void ABareRuler_CarriesNoGroup1AtAll()
    {
        var dim = Entity(Groups(WriteDxf([Bare()])), "DIMENSION");
        Assert.DoesNotContain(dim, g => g.Code == 1);
    }

    // ── R-rul-18b: Fixed is resolved once, against the drawing extents ────────────────────────────

    [Fact]
    public void FixedSizing_ResolvesAgainstTheDrawingExtents_NotToAScreenQuantity()
    {
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, SizeMode = RulerSizeMode.Fixed, TextSizePt = 12.0,
        };

        var small = new Bbox(0, 0, 40_000, 20_000);
        var large = new Bbox(0, 0, 400_000, 200_000);

        double hSmall = DxfWriter.ResolveExportTextHeightDbu(r, small);
        double hLarge = DxfWriter.ResolveExportTextHeightDbu(r, large);

        // Ten times the extents, ten times the height — the same fraction of the drawing either way,
        // which is what makes it legible when the recipient zooms to extents.
        Assert.InRange(hLarge / hSmall, 9.99, 10.01);

        // The constant, stated once: diagonal × pt / 960.
        double diag = System.Math.Sqrt(40_000.0 * 40_000 + 20_000.0 * 20_000);
        Assert.InRange(hSmall, diag * 12.0 / 960.0 - 0.5, diag * 12.0 / 960.0 + 0.5);
    }

    [Fact]
    public void ScaledSizing_ExportsItsOwnWorldHeight_Unchanged()
    {
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 2_750,
        };
        Assert.Equal(2_750, DxfWriter.ResolveExportTextHeightDbu(r, new Bbox(0, 0, 40_000, 20_000)));
        Assert.Equal(2_750, DxfWriter.ResolveExportTextHeightDbu(r, new Bbox(0, 0, 400_000, 200_000)));
    }

    [Fact]
    public void TheExportSummary_CountsRulers_AndSaysHowFixedWasResolved()
    {
        var structure = new InterchangeStructure(
            "TOP", [new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 20_000 }], []);
        var fixedRuler = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, SizeMode = RulerSizeMode.Fixed };

        var summary = DxfWriter.Write(TextWriter.Null, [structure], "TOP", null, 1000,
                                      new DxfExportOptions(), null, [fixedRuler], LayoutUnit.Um);

        Assert.Equal(1, summary.RulersWritten);
        Assert.Contains(summary.Diagnostics, d => d.Contains("DIMENSION") && d.Contains("extents"));
    }

    // ── R-rul-6: the readout is formatted in the DOCUMENT's display unit ──────────────────────────

    [Fact]
    public void ThePictureBlocksReadout_UsesTheDocumentsDisplayUnit()
    {
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 25_400, Y2 = 0, SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 2_000,
        };
        Assert.Contains("1 mil", WriteDxf([r], LayoutUnit.Mil));
        // µ is non-ASCII, and this writer escapes it as AutoCAD's own \U+XXXX (R-dxf-2) rather than
        // emitting a raw code-page byte that would only round-trip for a reader sharing that page.
        Assert.Contains(@"25.4 \U+00B5m", WriteDxf([r], LayoutUnit.Um));
    }

    // ── Gate 5: import does NOT reconstruct rulers ────────────────────────────────────────────────

    [Fact]
    public void ImportingThatSameFile_ProducesNoRulers_AndNoStrayAnonymousBlocks()
    {
        var r = Bare();
        r.Caption = "keep";
        string dxf = WriteDxf([r]);

        var read = DxfReader.Read(new StringReader(dxf));

        // R-rul-19: a DIMENSION read back in is skipped, and its *D# block is already skipped by the
        // importer's existing anonymous-block rule — verified, not re-implemented.
        Assert.DoesNotContain(read.Structures, st => st.Name.StartsWith('*'));
        Assert.All(read.Structures, st => Assert.DoesNotContain(
            st.Shapes, sh => sh.Shape is LabelShape { Text: "keep" }));

        // Nothing in the interchange model can carry a ruler in the first place — that is the shape of
        // the guarantee, not a filter someone has to remember.
        var view = new LayoutView();
        foreach (var st in read.Structures) foreach (var sh in st.Shapes) view.Shapes.Add(sh.Shape);
        Assert.Empty(view.Rulers);
    }

    // ── Rulers come last in ENTITIES, so they are above the bond wires ────────────────────────────

    /// <summary>
    /// Owner, 2026-08-27, alongside the on-screen fix: rulers must export above the wBond wires too.
    /// A DXF with no <c>SORTENTSTABLE</c> is drawn in ENTITIES order (and, for a reader that sorts by
    /// handle, in handle order — which is the same order here, since handles are issued as entities
    /// are written). So "above" is a position in the file, and this pins it.
    /// </summary>
    [Fact]
    public void EveryRulerIsWrittenAfterEveryWire_SoItDrawsOnTopOfThem()
    {
        var design = new CircuitRF.WBond.WBondDesign();
        var array = new CircuitRF.WBond.WireArray { Name = "G1" };
        array.Wires.Add(CircuitRF.WBond.LoopShape.CreateSeedWire(
            new CircuitRF.WBond.Point3(0, 0, 0),
            new CircuitRF.WBond.Point3(20_000_000, 0, 0),
            CircuitRF.Ui.WBond.WBondDefaults.DiameterNm, CircuitRF.Ui.WBond.WBondDefaults.Material,
            2_000_000, CircuitRF.Ui.WBond.WBondDefaults.Points));
        design.Arrays.Add(array);

        var structure = new InterchangeStructure(
            "TOP",
            [new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 20_000 }],
            []);

        var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", null, 1000, new DxfExportOptions(), design,
                        [Bare()], LayoutUnit.Um);

        var groups = Groups(sw.ToString());

        // Index of the LAST wire entity and of the FIRST ruler DIMENSION, in the ENTITIES section.
        int entitiesAt = groups.FindIndex(g => g.Code == 2 && g.Value == "ENTITIES");
        Assert.True(entitiesAt >= 0);

        int lastWire = -1, firstDim = -1;
        for (int i = entitiesAt; i < groups.Count; i++)
        {
            if (groups[i].Code == 8 && groups[i].Value.StartsWith(DxfWireIo.LayerPrefix, System.StringComparison.Ordinal))
                lastWire = i;
            if (firstDim < 0 && groups[i].Code == 0 && groups[i].Value == "DIMENSION")
                firstDim = i;
        }

        Assert.True(lastWire >= 0, "the fixture must actually export a wire");
        Assert.True(firstDim >= 0, "the fixture must actually export a ruler");
        Assert.True(firstDim > lastWire,
                    $"the ruler DIMENSION (at {firstDim}) must come after every wire (last at {lastWire})");
    }
}
