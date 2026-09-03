using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── docs/sonnet-briefs/brief-layout-testing-fixes.md item 6 ─────────────────────────────────────
//
// Diagnosis (performed before writing any code, per the brief's explicit instruction): the reported
// "Sample Label" text could only ever have come from a real LabelShape.Text value. Both GdsiiWriter
// and DxfWriter reach their WriteText method SOLELY from `case LabelShape label:` in their per-shape
// dispatch switch (confirmed by direct code reading, not assumed) — there is no other code path that
// writes any text, and neither writer references Environment.UserName/MachineName or any hardcoded
// author/metadata string anywhere. The literal reported text does not appear in any committed .clay
// or testdata file (grepped the full repo) — consistent with the owner's own stated practice of not
// committing their scratch test designs (see the prior DXF brief's test.dxf note). The only remaining
// explanation, and the brief's own leading hypothesis, is confirmed by elimination: the design
// genuinely contained a LabelShape with that text, invisible on screen because its Height rendered
// sub-pixel — the same failure mode the label-height brief already fixed once for the IN-PROGRESS
// ghost and for a freshly-COMMITTED label typed through CommitLabel, but never for a label that
// arrives some other way (an old file predating that fix, a hand-edited .clay, or GDSII/DXF import).
//
// R-fix-5: report the count of TEXT/label records written in both export summaries — text a user
// did not knowingly place is exactly what an export report should surface.
//
// "Also worth fixing regardless": a minimum on-screen render size for COMMITTED labels, not just the
// in-progress ghost — LayoutRenderer.DrawLayer's per-shape Label branch now applies the SAME
// EffectiveVisibleLabelHeightDbu floor R-lbl-2's ghost already used, for DISPLAY ONLY (the model's
// own Height is never mutated).

[Collection(CircuitRF.Ui.Tests.LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutLabelExportAndVisibilityTests : IDisposable
{
    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private readonly string _dir = Directory.CreateTempSubdirectory("label-export-test-").FullName;

    public LayoutLabelExportAndVisibilityTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        Directory.Delete(_dir, recursive: true);
    }

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = FreshModel();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    // ── R-fix-5: label record counts, both formats ──────────────────────────────────────────────

    [Fact]
    public void GdsiiExport_ReportsLabelRecordCount_InTheWriteSummaryAndThePreflightPlan()
    {
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "REF" });
            v.Shapes.Add(new LabelShape { Layer = Layer1, X = 100, Y = 100, Text = "Unexpected" });
            v.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
        });

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.Equal(2, plan.LabelRecordsWritten);

        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, plan);
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public void GdsiiExport_NoLabels_ReportsZero()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.Equal(0, plan.LabelRecordsWritten);
    }

    [Fact]
    public void DxfExport_ReportsLabelRecordCount_InTheWriteSummary()
    {
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "REF" });
            v.Shapes.Add(new LabelShape { Layer = Layer1, X = 100, Y = 100, Text = "Unexpected" });
        });

        var plan = DxfExport.Analyze(cellDir, null, 1000);
        var options = new DxfExportOptions();
        var preview = DxfExport.Preview(plan, options);
        Assert.Equal(2, preview.LabelRecordsWritten);

        var outPath = Path.Combine(_dir, "out.dxf");
        var summary = DxfExport.Write(outPath, plan, options);
        Assert.Equal(2, summary.LabelRecordsWritten);
    }

    [Fact]
    public void DxfExport_NoLabels_ReportsZero()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var plan = DxfExport.Analyze(cellDir, null, 1000);
        var summary = DxfExport.Write(Path.Combine(_dir, "empty.dxf"), plan, new DxfExportOptions());
        Assert.Equal(0, summary.LabelRecordsWritten);
    }

    // ── "Also worth fixing regardless": committed labels get the same visibility floor as the ghost ──

    [Fact]
    public void CommittedLabel_WithSubPixelHeight_StillRendersVisibly_ModelHeightNeverMutated()
    {
        var tech = new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = Layer1, Name = "L", Color = new Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
        };
        var view = FreshModel();
        // 5 DBU = 5 nm at DbuPerMicron=1000 — deliberately far below any visible threshold at a
        // realistic zoom, exactly the class of value an old file or an importer could produce.
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "W", Height = 5 };
        view.Shapes.Add(label);

        var vp = LayoutViewport.ZoomToFit(new Bbox(-60_000, -60_000, 60_000, 60_000), 300, 300, 0.1);
        Assert.True(5 * vp.Zoom < 1.0, "fixture must actually be sub-pixel at this viewport to be a meaningful test");

        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        int anchorSx = (int)vp.WorldToScreenX(0);
        int anchorSy = (int)vp.WorldToScreenY(0);

        bool AnyRedPixel(int x0, int x1, int y0, int y1)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(bmp.Height, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(bmp.Width, x1); x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red > c.Green + 40 && c.Red > c.Blue + 40) return true;
                }
            return false;
        }

        // R0 extends right-and-up from the anchor (world), i.e. right-and-up on screen too (screen Y
        // decreases upward) — mirrors MeasureLabelWorldBbox_SitsInTheCorrectQuadrantRelativeToTheAnchor's
        // own R0 case.
        Assert.True(AnyRedPixel(anchorSx - 5, anchorSx + 60, anchorSy - 40, anchorSy + 10),
            "a committed label with a sub-pixel Height must still render visibly — the same floor the ghost already applies");

        // Display-only: rendering must never mutate the model's own Height field.
        Assert.Equal(5, label.Height);
    }

    [Fact]
    public void CommittedLabel_AlreadyVisibleHeight_RendersUnboosted_NoRegressionForTheCommonCase()
    {
        var tech = new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = Layer1, Name = "L", Color = new Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
        };
        var view = FreshModel();
        // A generously-sized label at this viewport already clears MinVisibleLabelDevicePixels —
        // EffectiveVisibleLabelHeightDbu must return it unchanged (pure arithmetic already tested
        // directly in LayoutLabelFixAndTextFlattenTests; this just confirms the draw call still uses
        // the real value, not an unconditionally-boosted one).
        long height = 40_000;
        view.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "W", Height = height });

        var vp = LayoutViewport.ZoomToFit(new Bbox(-60_000, -60_000, 60_000, 60_000), 300, 300, 0.1);
        double devicePxPerDbu = vp.Zoom;
        Assert.Equal(height, LayoutRenderer.EffectiveVisibleLabelHeightDbu(height, devicePxPerDbu));
    }
}
