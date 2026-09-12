using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  A layer's ZOrder decides paint order INSIDE a placed cell too.
//
//  Top-level shapes were always sorted (LayoutRenderer buckets them by layer and sorts the buckets
//  by ZOrder), but a compiled sub-cell was not: CompileCell collects geometry into a Dictionary
//  keyed by LayerKey, so its Layers came back in the order the cell's shapes happened to be stored
//  in the .clay. A cell whose bottom-copper shape sat after its top-copper one therefore painted
//  the BOTTOM layer on top — the "my top layer is being masked by the layer below" report — and
//  swapping two lines in the file silently changed the picture.
//
//  Both fixtures below are the SAME two overlapping opaque rectangles, differing only in which line
//  of the file comes first. Whichever way round they are written, the ZOrder-10 layer must win.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutInstanceLayerOrderTests : IDisposable
{
    private readonly string _workspaceDir;

    // Opaque, and full red against full blue: a partially transparent fill blends the two and there
    // would then be no pixel that names a winner.
    private static readonly LayerKey Upper = new(1, 0);   // ZOrder 10 — must paint on top
    private static readonly LayerKey Lower = new(2, 0);   // ZOrder  1 — must paint beneath

    public LayoutInstanceLayerOrderTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfZOrderTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef { Key = Upper, Name = "Upper", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 10, Visible = true, Selectable = true },
            new LayerDef { Key = Lower, Name = "Lower", Color = new CircuitRF.Design.Theming.Rgba(0, 0, 255), FillOpacity = 1.0, ZOrder = 1,  Visible = true, Selectable = true },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static void AddOverlappingRects(LayoutView v, bool upperFirst)
    {
        var first  = upperFirst ? Upper : Lower;
        var second = upperFirst ? Lower : Upper;
        v.Shapes.Add(new RectShape { Layer = first,  X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 20_000 });
        v.Shapes.Add(new RectShape { Layer = second, X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 20_000 });
    }

    /// <summary>The colour of the pixel at the centre of the overlap. Read through SKBitmap.GetPixel
    /// rather than the raw byte buffer, whose channel order is platform-dependent.</summary>
    private static SKColor CentreOfOverlap(LayoutView view, Technology tech, string? baseDir)
    {
        var vp = new LayoutViewport(-2_000, -2_000, 400.0 / 24_000, 400, 400);
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(200, 200);
    }

    private LayoutView PlaceCellContaining(string cellName, bool upperFirst)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, cellName);
        var sub = MakeView();
        AddOverlappingRects(sub, upperFirst);
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), sub);

        var top = MakeView();
        top.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1,
        });
        return top;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TopLevelShapes_HighestZOrderPaintsLast_WhicheverOrderTheyAreStoredIn(bool upperFirst)
    {
        var view = MakeView();
        AddOverlappingRects(view, upperFirst);

        var c = CentreOfOverlap(view, MakeTech(), null);
        Assert.True(c.Red > c.Blue, $"expected the ZOrder-10 layer on top, got {c}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlacedInstance_HighestZOrderPaintsLast_WhicheverOrderTheyAreStoredIn(bool upperFirst)
    {
        var top = PlaceCellContaining("Cell" + (upperFirst ? "A" : "B"), upperFirst);

        var c = CentreOfOverlap(top, MakeTech(), _workspaceDir);
        Assert.True(c.Red > c.Blue, $"expected the ZOrder-10 layer on top, got {c}");
    }

    /// <summary>The point of the pair: a placement must look like the same geometry drawn flat. This
    /// is what actually failed — the flat render and the placed render disagreed about which layer
    /// was on top, for one of the two storage orders only.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlacedInstance_AgreesWithTheSameGeometryDrawnFlat(bool upperFirst)
    {
        var flat = MakeView();
        AddOverlappingRects(flat, upperFirst);
        var tech = MakeTech();

        Assert.Equal(CentreOfOverlap(flat, tech, null),
                     CentreOfOverlap(PlaceCellContaining("Cell" + (upperFirst ? "C" : "D"), upperFirst), tech, _workspaceDir));
    }
}
