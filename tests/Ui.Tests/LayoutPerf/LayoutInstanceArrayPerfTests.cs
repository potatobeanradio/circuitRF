// Phase L3a gate 5 (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md): "record frame time for a
// 50x50 array of a 20-shape cell and compare against 50,000 flat shapes. The whole point is that they
// should not be comparable." Benchmark-tagged (real timing, not a CI-gated assertion — matches every
// other wall-clock number in this project's L2 benchmark suite). Was "Nightly" — that category is
// retired (docs/sonnet-briefs/brief-test-default-fast.md R-tst-B): Category=Benchmark is now the one
// tag for "should never run in a routine pass," excluded by default via circuitrf.runsettings.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.LayoutPerf;

public sealed class LayoutInstanceArrayPerfTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly ITestOutputHelper _out;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutInstanceArrayPerfTests(ITestOutputHelper output)
    {
        _out = output;
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstArrayPerf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void Array50x50Of20ShapeCell_FrameTime_NotComparableTo50kFlatShapes()
    {
        const int subCellShapeCount = 20;
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, "Via");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var subView = new LayoutView { DbuPerMicron = 1000 };
        for (int i = 0; i < subCellShapeCount; i++)
            subView.Shapes.Add(new RectShape { Layer = LayerA, X1 = i * 10, Y1 = 0, X2 = i * 10 + 5, Y2 = 5 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), subView);

        var arrayView = new LayoutView { DbuPerMicron = 1000 };
        arrayView.Instances.Add(new LayoutInstance { CellRef = "Via", X = 0, Y = 0, Mag = 1.0, Rows = 50, Cols = 50, PitchX = 1000, PitchY = 1000 });
        var tech = new Technology { Name = "Test", Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = 0.5, ZOrder = 0, Visible = true, Selectable = true }] };
        var arrayVp = new LayoutViewport(-2000, -2000, 0.01, 800, 800);

        using var surface = SKSurface.Create(new SKImageInfo(800, 800));
        var arrayOpts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };
        // Warm the compile cache once (mirrors every other benchmark in this suite — R-L2a-4: warm up
        // then discard, measure only steady-state frames).
        LayoutRenderer.Draw(surface.Canvas, arrayView, tech, arrayVp, arrayOpts);
        var arrayTiming = BenchmarkHarness.Measure(1, 5, () => LayoutRenderer.Draw(surface.Canvas, arrayView, tech, arrayVp, arrayOpts));

        var flatView = SyntheticLayoutGenerator.Generate(50_000, 1, seed: 3001, GeneratorProfile.Manhattan);
        var flatTech = SyntheticLayoutGenerator.GenerateTechnology(1);
        var flatVp = LayoutViewport.ZoomToFit(
            flatView.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s))), 800, 800);
        var flatOpts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var flatTiming = BenchmarkHarness.Measure(1, 3, () => LayoutRenderer.Draw(surface.Canvas, flatView, flatTech, flatVp, flatOpts));

        _out.WriteLine($"[L3a array]  50x50 of a {subCellShapeCount}-shape cell (2,500 placements): {arrayTiming}");
        _out.WriteLine($"[L3a flat]   50,000 unique flat shapes:                                    {flatTiming}");
        _out.WriteLine($"Ratio (flat / array): {flatTiming.MedianMs / Math.Max(arrayTiming.MedianMs, 0.001):F1}x");

        // Not a strict CI gate (wall-clock is inherently noisy — R-L2a-3) — a loose catastrophe
        // backstop matching this project's own established convention: the array must be MEANINGFULLY
        // cheaper, not merely "not slower."
        Assert.True(arrayTiming.MedianMs < flatTiming.MedianMs,
            $"expected the array ({arrayTiming.MedianMs}ms) to be cheaper than 50k flat shapes ({flatTiming.MedianMs}ms) — R-L3a-3's whole point");
    }
}
