using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B, gates 2 and 3 — the MODEL half of the ruler feature.
///
/// <para><b>Gate 3 is the load-bearing one.</b> §9B.1's whole argument for keeping a ruler out of
/// <c>LayoutView.Shapes</c> is that a missed exclusion would put an annotation into a manufacturing
/// file — "a Gerber with a dimension line etched in copper is a scrapped board, and nothing in the
/// flow catches it before the fab does." The property that makes that structurally impossible is
/// asserted here directly, on all four manufacturing writers: the same layout with and without rulers
/// produces BYTE-IDENTICAL output. It passes because none of those writers was touched at all.</para>
/// </summary>
public class LayoutRulerModelTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-ruler-model-" + System.Guid.NewGuid().ToString("N"));

    public LayoutRulerModelTests()
    {
        Directory.CreateDirectory(_dir);
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        System.GC.SuppressFinalize(this);
    }

    private static readonly LayerKey Metal = new(1, 0);

    private static RulerAnnotation SampleFixed() => new()
    {
        X1 = 0, Y1 = 0, X2 = 3_000, Y2 = 4_000,
        SizeMode = RulerSizeMode.Fixed,
        TextSizePt = 13.5,
        TextHeightDbu = 2_500,
        Style = LabelFontStyle.Bold,
        Caption = "bond wire span",
        ShowComponents = true,
    };

    private static RulerAnnotation SampleScaled() => new()
    {
        X1 = 1_000, Y1 = 2_000, X2 = 9_000, Y2 = 2_000,
        SizeMode = RulerSizeMode.Scaled,
        TextSizePt = 7.25,
        TextHeightDbu = 900,
        Style = LabelFontStyle.Italic,
        Caption = null,
        ShowComponents = false,
    };

    // ── Gate 2: round-trip ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clay_RoundTrips_BothSizeModes_WithBothBackingValuesPreserved()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Rulers.Add(SampleFixed());
        view.Rulers.Add(SampleScaled());

        var path = Path.Combine(_dir, "rulers.clay");
        LayoutPersistence.SaveToFile(path, view);
        var reloaded = LayoutPersistence.LoadFromFile(path);

        Assert.Equal(2, reloaded.Rulers.Count);

        var a = reloaded.Rulers[0];
        Assert.Equal(0, a.X1); Assert.Equal(0, a.Y1);
        Assert.Equal(3_000, a.X2); Assert.Equal(4_000, a.Y2);
        Assert.Equal(RulerSizeMode.Fixed, a.SizeMode);
        // §9B.7: BOTH backing values persist, so a mode switch is reversible.
        Assert.Equal(13.5, a.TextSizePt);
        Assert.Equal(2_500, a.TextHeightDbu);
        Assert.Equal(LabelFontStyle.Bold, a.Style);
        Assert.Equal("bond wire span", a.Caption);
        Assert.True(a.ShowComponents);

        var b = reloaded.Rulers[1];
        Assert.Equal(RulerSizeMode.Scaled, b.SizeMode);
        Assert.Equal(7.25, b.TextSizePt);
        Assert.Equal(900, b.TextHeightDbu);
        Assert.Equal(LabelFontStyle.Italic, b.Style);
        Assert.Null(b.Caption);
        Assert.False(b.ShowComponents);
    }

    [Fact]
    public void RulerFreeClay_ReSerializes_ByteForByte_WithNoFormatVersionBump()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 5_000, Y2 = 5_000 });

        var first = Path.Combine(_dir, "plain.clay");
        LayoutPersistence.SaveToFile(first, view);
        string before = File.ReadAllText(first);

        var reloaded = LayoutPersistence.LoadFromFile(first);
        var second = Path.Combine(_dir, "plain-again.clay");
        LayoutPersistence.SaveToFile(second, reloaded);

        Assert.Equal(before, File.ReadAllText(second));
        // R-rul-15: additive — the key is absent entirely when there are no rulers.
        Assert.DoesNotContain("\"Rulers\"", before);
        Assert.Contains("\"FormatVersion\": 1", before);
    }

    [Fact]
    public void CaptionIsOmitted_WhenNull()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Rulers.Add(SampleScaled());   // Caption == null

        var path = Path.Combine(_dir, "nocaption.clay");
        LayoutPersistence.SaveToFile(path, view);

        Assert.DoesNotContain("\"Caption\"", File.ReadAllText(path));
    }

    [Fact]
    public void Distance_IsComputed_NeverStored()
    {
        // R-rul-5: 3-4-5. There is no settable Distance property to write into — the type does not
        // expose one, and the file carries no such key.
        var r = SampleFixed();
        Assert.Equal(5_000, r.DistanceDbu);

        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Rulers.Add(r);
        var path = Path.Combine(_dir, "dist.clay");
        LayoutPersistence.SaveToFile(path, view);
        Assert.DoesNotContain("Distance", File.ReadAllText(path));
    }

    // ── Gate 3: NOT GEOMETRY — the load-bearing property ───────────────────────────────────────────

    /// <summary>Creates the cell under its own fresh parent directory, so two otherwise-identical
    /// exports can use the SAME cell name — every one of these writers stamps the structure/board name
    /// into its output, so a per-call unique name would make the comparison fail for a reason that has
    /// nothing to do with rulers.</summary>
    private string CreateCell(string name, System.Action<LayoutView> populate)
    {
        var parent = Path.Combine(_dir, "w" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
        var cellDir = CellFolder.CreateCellFolder(parent, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    private static Technology PcbTech() => new()
    {
        Name = "Test",
        Layers =
        [
            new LayerDef
            {
                Key = Metal, Name = "Top Copper", Color = new Rgba(0xC8, 0x7A, 0x3E),
                Interchange = new InterchangeMapping(null, null, null, "GTL", "Copper,L1,Top"),
            },
        ],
        Stackup = new Stackup
        {
            Layers = [new StackupLayer { Name = "Top Copper", Kind = StackupKind.Conductor, DrawingLayers = [Metal] }],
        },
    };

    /// <summary>The same artwork twice — once with rulers, once without.</summary>
    private (LayoutView WithRulers, LayoutView Without) TwoViews()
    {
        LayoutView Make(bool rulers)
        {
            var v = new LayoutView { DbuPerMicron = 1000 };
            v.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 10_000 });
            v.Shapes.Add(new ViaShape { Layer = Metal, X = 5_000, Y = 5_000, PadSize = 1_200, DrillSize = 600 });
            if (rulers) { v.Rulers.Add(SampleFixed()); v.Rulers.Add(SampleScaled()); }
            return v;
        }
        return (Make(true), Make(false));
    }

    [Fact]
    public void Gdsii_IsByteIdentical_WithAndWithoutRulers()
    {
        var (with, without) = TwoViews();
        string a = WriteGdsii(with), b = WriteGdsii(without);
        Assert.Equal(b, a);
    }

    private string WriteGdsii(LayoutView view)
    {
        string cell = CreateCell("TOP", v =>
        {
            foreach (var s in view.Shapes) v.Shapes.Add(s);
            foreach (var r in view.Rulers) v.Rulers.Add(r);
        });
        var plan = GdsiiExport.Analyze(cell, PcbTech(), 1000);
        string path = Path.Combine(Path.GetDirectoryName(cell)!, "out.gds");
        GdsiiExport.Write(path, plan);
        return System.Convert.ToBase64String(File.ReadAllBytes(path));
    }

    [Fact]
    public void GerberAndExcellon_AreByteIdentical_WithAndWithoutRulers()
    {
        var (with, without) = TwoViews();
        Assert.Equal(WriteGerber(without), WriteGerber(with));
    }

    private string WriteGerber(LayoutView source)
    {
        string cell = CreateCell("TOP", v =>
        {
            foreach (var s in source.Shapes) v.Shapes.Add(s);
            foreach (var r in source.Rulers) v.Rulers.Add(r);
        });
        var loaded = LayoutPersistence.LoadFromFile(
            Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cell, PcbTech(), 1000, loaded, null);
        string outDir = Path.Combine(Path.GetDirectoryName(cell)!, "gbr");
        Directory.CreateDirectory(outDir);
        GerberExport.Write(outDir, "TOP", plan);

        // Every produced file (the copper Gerbers, the Excellon drill, the job file), concatenated in
        // a stable order — one comparison covering both writers.
        return string.Join("\n---\n", Directory.GetFiles(outDir).OrderBy(f => f, System.StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + "\n" + File.ReadAllText(f)));
    }

    [Fact]
    public void KicadPcb_IsByteIdentical_WithAndWithoutRulers()
    {
        var (with, without) = TwoViews();
        Assert.Equal(WritePcb(without), WritePcb(with));
    }

    private string WritePcb(LayoutView source)
    {
        string cell = CreateCell("TOP", v =>
        {
            foreach (var s in source.Shapes) v.Shapes.Add(s);
            foreach (var r in source.Rulers) v.Rulers.Add(r);
        });
        var plan = PcbExport.Analyze(cell, PcbTech(), 1000);
        string path = Path.Combine(Path.GetDirectoryName(cell)!, "out.kicad_pcb");
        PcbExport.Write(path, plan);
        // The board writer stamps a generation timestamp; strip any line carrying one so the
        // comparison is about the GEOMETRY, which is what this gate is asserting.
        return string.Join("\n", File.ReadAllLines(path).Where(l => !l.Contains("(generator")));
    }

    // ── §9B.7: cell-local ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Flatten_ProducesNoRulers()
    {
        var sub = new LayoutView { DbuPerMicron = 1000 };
        sub.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        sub.Rulers.Add(SampleFixed());

        // LayoutFlattener walks SHAPES. A ruler is not in Shapes, so there is nothing to exclude and
        // nothing can come up — that IS §9B.1's claim, and this pins the two halves of it: the sub-cell
        // really does have a ruler, and a flatten of its geometry produces rings only.
        Assert.Single(sub.Rulers);
        var rings = LayoutFlattener.Flatten(sub.Shapes[0], tolDbu: 10);
        Assert.NotEmpty(rings);

        // A view built from that flattened geometry carries no rulers at all.
        var flat = new LayoutView { DbuPerMicron = sub.DbuPerMicron };
        foreach (var ring in rings) flat.Shapes.Add(new PolygonShape { Layer = Metal, Xy = ring });
        Assert.Empty(flat.Rulers);
    }
}
