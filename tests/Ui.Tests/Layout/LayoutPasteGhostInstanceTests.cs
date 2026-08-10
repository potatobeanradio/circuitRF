// Owner report, 2026-08-09: "When I paste the geometry back into a layout, before committing the
// paste's position, the ports are rendered live (when moving the mouse), but my MLIN object is not
// rendered live. For small amounts of geometry it should render live. If the geometry is too
// complicated for live rendering, then just render a box for the geometry (but keep the port
// rendering live)."
//
// L1f shipped the paste ghost as shapes-only and said so in its own completion note — an instance
// travelled with the paste and committed correctly, it just was never in the picture the user was
// aiming with. Which meant aiming a schematic-generated selection, whose metal is ALL instances, was
// aiming at two port glyphs and empty space.

using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPasteGhostInstanceTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private readonly string _root;

    public LayoutPasteGhostInstanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crfPasteGhost_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary><paramref name="shapeCount"/> rectangles in one cell — the knob that decides whether a
    /// pasted instance is cheap enough to ghost live.</summary>
    private string CreateCell(string name, int shapeCount)
    {
        var dir = CellFolder.CreateCellFolder(_root, name);
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        for (int i = 0; i < shapeCount; i++)
            view.Shapes.Add(new RectShape
            {
                Layer = TopCopper,
                X1 = i * 2_000, Y1 = 0, X2 = i * 2_000 + 1_000, Y2 = 1_000_000,
            });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(dir, ViewType.Layout), "main.clay"), view);
        return dir;
    }

    private (LayoutEditorViewModel Vm, LayoutView View) Editor()
    {
        var parentDir = CellFolder.CreateCellFolder(_root, "Top");
        string clay = Path.Combine(CellFolder.SubFolderPath(parentDir, ViewType.Layout), "main.clay");
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        LayoutPersistence.SaveToFile(clay, view);
        return (new LayoutEditorViewModel(view, clay), view);
    }

    private static (IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<LayoutInstance> Instances) Fragment(string cellRef)
        => ([new LabelShape
             {
                 Layer = TopCopper, X = 0, Y = 0, Text = "P1", Height = 1_000_000,
                 IsPort = true, PortDirection = LayoutRotation.R0,
             }],
            [new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 1 }]);

    [Fact]
    public void APastedInstance_GhostsLive_AndFollowsTheCursorWithTheShapes()
    {
        CreateCell("Small", 4);
        var (vm, _) = Editor();
        var (shapes, instances) = Fragment(Path.Combine("..", "..", "Small"));

        vm.BeginPastePlacement(shapes, 0, 0, instances);
        vm.OnPointerMoved(5_000_000, 3_000_000, leftDown: false, default);

        var ghosts = vm.Overlay.PastePreviewInstances;
        Assert.NotNull(ghosts);
        var g = Assert.Single(ghosts!);

        Assert.False(g.BoxOnly, "a four-shape cell is comfortably inside the live-render budget");
        Assert.Equal(5_000_000, g.Instance.X);       // moved with the cursor…
        Assert.Equal(3_000_000, g.Instance.Y);
        Assert.False(g.Bbox.IsEmpty, "the ghost must resolve its cell to know how big it is");

        // …by the SAME delta the shape half moved, so the two cannot drift apart mid-gesture.
        var shapeGhost = Assert.Single(vm.Overlay.PastePreview!);
        Assert.Equal(5_000_000, ((LabelShape)shapeGhost).X);
    }

    [Fact]
    public void AHeavyInstance_DegradesToABox_WhileThePortsStayLive()
    {
        // Deliberately over the budget: the owner's own rule is "just render a box for the geometry,
        // but keep the port rendering live", so the two halves must degrade independently.
        CreateCell("Heavy", 1);
        var heavyDir = CellFolder.CreateCellFolder(_root, "HeavyArray");
        var heavyView = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        heavyView.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "Heavy"), X = 0, Y = 0, Mag = 1.0,
            Rows = 200, Cols = 200, PitchX = 2_000, PitchY = 2_000,   // 40,000 shapes
        });
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(heavyDir, ViewType.Layout), "main.clay"), heavyView);

        var (vm, _) = Editor();
        var (shapes, instances) = Fragment(Path.Combine("..", "..", "HeavyArray"));

        vm.BeginPastePlacement(shapes, 0, 0, instances);
        vm.OnPointerMoved(1_000_000, 1_000_000, leftDown: false, default);

        var g = Assert.Single(vm.Overlay.PastePreviewInstances!);
        Assert.True(g.BoxOnly, "40,000 shapes is well over the live-render budget");

        // The port ghost is untouched by the instance's cost.
        Assert.Single(vm.Overlay.PastePreview!);
    }

    [Fact]
    public void TheGhostActuallyPaints_AndTheInstanceHalfIsWhatAddsThePixels()
    {
        CreateCell("Small2", 4);
        var (vm, view) = Editor();
        var (shapes, instances) = Fragment(Path.Combine("..", "..", "Small2"));

        var vp = new LayoutViewport(-1_000_000, -1_000_000, 3e-5, 400, 400);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            BaseDir = Path.GetDirectoryName(vm.CurrentLayoutPath!)!,
        };

        // Ports only — what the ghost used to be.
        vm.BeginPastePlacement(shapes, 0, 0, null);
        vm.OnPointerMoved(2_000_000, 1_000_000, leftDown: false, default);
        int portsOnly = PaintedPixels(view, vp, opts with { Overlay = vm.Overlay });
        vm.OnKeyDown(Key.Escape, default);   // the ordinary way a placement is abandoned

        // Ports AND the instance.
        vm.BeginPastePlacement(shapes, 0, 0, instances);
        vm.OnPointerMoved(2_000_000, 1_000_000, leftDown: false, default);
        int withInstance = PaintedPixels(view, vp, opts with { Overlay = vm.Overlay });

        Assert.True(withInstance > portsOnly,
            $"the instance ghost must add pixels ({withInstance} vs {portsOnly})");
    }

    private static int PaintedPixels(LayoutView view, LayoutViewport vp, LayoutRenderOptions opts)
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        var bg = LayoutRenderTheme.Light.Background;
        int n = 0;
        for (int x = 0; x < 400; x++)
            for (int y = 0; y < 400; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red != bg.Red || c.Green != bg.Green || c.Blue != bg.Blue) n++;
            }
        return n;
    }
}
