// The compiled-instance half of the drill-map defect (2026-09-04) — see src/Ui/RESOLVED.md,
// "The drill map rendered differently at every zoom step" and its instance follow-up.
//
// LayoutRenderer.DrawLayer's merge tier was given IsRingGeometry so a CLOSED PathShape could not be
// batched into a shared NonZero-filled path. CompileCell folds every primitive of a chunk into one
// path for exactly the same reason and was NOT given the same guard, so a PLACEMENT of the same
// artwork still cancelled its rings — worse than the top-level case, because a compiled chunk always
// merges and so the corruption did not even vary with zoom.
//
// The oracle throughout is the same geometry drawn TOP-LEVEL: a placement of a cell must render what
// the cell renders. That is a property no amount of batching may trade away, and it is what these
// tests assert.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutInstanceRingGeometryTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);
    private const int Size = 400;

    public LayoutInstanceRingGeometryTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfRingInst_" + Guid.NewGuid().ToString("N")[..8]);
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

    // Opaque, so a batched fill and a per-shape fill are genuinely comparable — a translucent layer
    // composites an overlap differently and would blur the assertion (the same reasoning
    // LayoutInstanceChunkCullingTests states for its own fixture).
    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A closed rectangular centreline, wound CW or CCW as asked — the two orientations an
    /// importer emits interchangeably, and the pair that cancels once merged into one path.</summary>
    private static PathShape ClosedRect(long x1, long y1, long x2, long y2, long width, bool clockwise)
    {
        long[] ccw = [x1, y1, x2, y1, x2, y2, x1, y2, x1, y1];
        if (!clockwise) return new PathShape { Layer = LayerA, Xy = ccw, Width = width, End = PathEndStyle.Flush };
        long[] cw = new long[ccw.Length];
        int n = ccw.Length / 2;
        for (int i = 0; i < n; i++) { cw[2 * i] = ccw[2 * (n - 1 - i)]; cw[2 * i + 1] = ccw[2 * (n - 1 - i) + 1]; }
        return new PathShape { Layer = LayerA, Xy = cw, Width = width, End = PathEndStyle.Flush };
    }

    /// <summary>The drill chart in miniature: a border and a cell rule nested inside it, wound against
    /// each other, plus an ordinary filled polygon so the chunk has something to merge them WITH.</summary>
    private static void Chart(LayoutView v)
    {
        v.Shapes.Add(ClosedRect(10_000, 10_000, 190_000, 190_000, 4_000, clockwise: true));
        v.Shapes.Add(ClosedRect(60_000, 60_000, 140_000, 140_000, 4_000, clockwise: false));
        v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = [20_000, 150_000, 50_000, 150_000, 50_000, 180_000, 20_000, 180_000] });
    }

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private LayoutView PlaceInstance(string cellDir)
    {
        var top = MakeView();
        top.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1, PitchX = 0, PitchY = 0,
        });
        return top;
    }

    private static SKColor[] Render(LayoutView view, LayoutViewport vp, string? baseDir)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, MakeTech(), vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir,
        });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Pixels;
    }

    private static int Painted(SKColor[] px, SKColor background)
    {
        int n = 0;
        foreach (var c in px)
            if (Math.Abs(c.Red - background.Red) > 6 || Math.Abs(c.Green - background.Green) > 6
                || Math.Abs(c.Blue - background.Blue) > 6) n++;
        return n;
    }

    private static LayoutViewport Viewport() => new(0, 0, Size / 200_000.0, Size, Size);

    [Fact]
    public void APlacedCellsRings_RenderWhatTheCellItselfRenders()
    {
        // The whole property in one assertion. Top-level draws each shape on its own; a compiled
        // instance folds a chunk into one path. Unguarded, the two nested rings cancel under NonZero
        // and the placement comes out as a solid slab with the figure gone — the owner's own 10x10
        // array of an imported board did exactly that.
        var cellDir = CreateCell("chart", Chart);
        var placed = PlaceInstance(cellDir);

        var flat = MakeView();
        Chart(flat);

        var vp = Viewport();
        var flatPx = Render(flat, vp, null);
        var placedPx = Render(placed, vp, _workspaceDir);

        var bg = flatPx[0];
        int flatLit = Painted(flatPx, bg), placedLit = Painted(placedPx, bg);
        Assert.True(flatLit > 0, "the top-level render drew nothing — the fixture is wrong, not the renderer");

        // Anti-aliasing along shared edges differs between one batched fill and several separate ones,
        // so this is a tolerance and not an equality. A cancelled ring is not a few percent: the
        // unguarded render of this fixture floods the whole 180k-DBU square solid.
        Assert.InRange(placedLit, (int)(flatLit * 0.95), (int)(flatLit * 1.05));
    }

    [Fact]
    public void APlacedCell_KeepsItsRingsHoles()
    {
        // Stated directly, because the ratio above could in principle be met by the wrong pixels: the
        // interior of both rings is a HOLE and must stay background. This is "the table cells appear
        // to be filled", seen through a placement.
        var cellDir = CreateCell("chart", Chart);
        var placed = PlaceInstance(cellDir);

        var vp = Viewport();
        var px = Render(placed, vp, _workspaceDir);

        var bg = px[0];
        var centre = px[Size / 2 * Size + Size / 2];   // inside both rings, touching neither wall
        Assert.True(
            Math.Abs(centre.Red - bg.Red) <= 6 && Math.Abs(centre.Green - bg.Green) <= 6
            && Math.Abs(centre.Blue - bg.Blue) <= 6,
            $"the compiled instance filled the rings' interior: centre {centre} vs background {bg}");
    }

    [Fact]
    public void ARingInsideANestedCell_SurvivesBothCompiles()
    {
        // A cell inside a cell: CompileCell folds a child chunk's geometry into the parent's, so rings
        // have to stay individual through BOTH levels. Merging them one level up reintroduces exactly
        // the cancellation they were kept out of at the level below.
        var inner = CreateCell("inner", Chart);
        // A NESTED reference resolves against the referring cell's own layout folder
        // (CellHierarchy.LayoutBaseDirOf), not against the workspace the top-level view resolves in.
        string outerLayoutDir = CellFolder.SubFolderPath(Path.Combine(_workspaceDir, "outer"), ViewType.Layout);
        var outer = CreateCell("outer", v => v.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(outerLayoutDir, inner),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1, PitchX = 0, PitchY = 0,
        }));
        var placed = PlaceInstance(outer);

        var flat = MakeView();
        Chart(flat);

        var vp = Viewport();
        var flatPx = Render(flat, vp, null);
        var px = Render(placed, vp, _workspaceDir);

        var bg = flatPx[0];
        var centre = px[Size / 2 * Size + Size / 2];
        Assert.True(
            Math.Abs(centre.Red - bg.Red) <= 6 && Math.Abs(centre.Green - bg.Green) <= 6
            && Math.Abs(centre.Blue - bg.Blue) <= 6,
            $"a ring two cells deep lost its hole: centre {centre} vs background {bg}");

        // The centre pixel alone is not enough here — two nested rings can cancel in a way that leaves
        // the innermost hole intact while flooding the band between them, which is exactly what the
        // unguarded nested compile does. The area is what catches that.
        int flatLit = Painted(flatPx, bg), placedLit = Painted(px, bg);
        Assert.True(flatLit > 0, "the top-level render drew nothing — the fixture is wrong");
        Assert.InRange(placedLit, (int)(flatLit * 0.95), (int)(flatLit * 1.05));
    }

    [Fact]
    public void WithOutlinesOff_AnInstancesSubstitutedChunk_DoesNotOutshineItsNeighbours()
    {
        // The other half of the same commit, which the instance path had also never been given: a LOD
        // substitution reproduces what the frame's own outline decision would have produced, never
        // something brighter. Here that is the per-chunk visibility floor and the elision/coarse
        // paints, which painted SOLID while ordinary chunks kept the layer's partial fill alpha.
        var tech = new Technology
        {
            Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers =
            [
                new LayerDef
                {
                    Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 128, 96),
                    FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true,
                },
            ],
        };

        var cellDir = CreateCell("mixed", v =>
        {
            // A thick bar (drawn from its real geometry) and, well away from it, a hairline one thin
            // enough that its chunk trips the visibility floor.
            v.Shapes.Add(new PathShape { Layer = LayerA, Xy = [20_000, 140_000, 180_000, 140_000], Width = 3_000, End = PathEndStyle.Flush });
            v.Shapes.Add(new PathShape { Layer = LayerA, Xy = [20_000, 60_000, 180_000, 60_000], Width = 200, End = PathEndStyle.Flush });
        });
        var placed = PlaceInstance(cellDir);

        var vp = Viewport();
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size));
        LayoutRenderer.Draw(surface.Canvas, placed, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir,
            OutlineVertexBudget = 1,      // the frame refuses outlines
        });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        var bg = bmp.GetPixel(2, 2);
        var thin = bmp.GetPixel(Size / 2, Size - Size * 60_000 / 200_000);
        var thick = bmp.GetPixel(Size / 2, Size - Size * 140_000 / 200_000);

        Assert.True(Math.Abs(thick.Green - bg.Green) > 6, "the thick path did not draw — the fixture is wrong");
        Assert.True(Math.Abs(thin.Green - bg.Green) > 6, "the hairline path did not draw — the fixture is wrong");

        // Same layer, same colour, same (absent) outline. Solid-against-35% is a ~100-level gap here.
        Assert.InRange(thin.Green, thick.Green - 24, thick.Green + 24);
        Assert.InRange(thin.Blue, thick.Blue - 24, thick.Blue + 24);
    }
}
