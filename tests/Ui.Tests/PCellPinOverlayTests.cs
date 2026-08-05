using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §6 (R-L5g-13/14/15): PCell pins render as a
/// screen-space overlay — a constant-pixel-size dot plus an outward-direction tick, in a theme
/// color distinct from every layer color — never as layer geometry, gated by
/// <see cref="LayoutRenderOptions.ShowPCellPins"/> (default off at the render-options layer, per
/// its own doc comment; the interactive canvas opts in via <see cref="LayoutEditorViewModel.
/// ShowPCellPins"/>, default ON, R-L5g-15).
///
/// Uses MLIN — a real, already-registered PCell generator (<see cref="MlinPCell.Generate"/>) whose
/// two pins are at deterministic cell-local positions (0,0)/(L,0) with known outward directions
/// (180°/0°) — a real generator, not a hand-authored fixture, so this exercises the actual
/// <see cref="PCellRegistry"/> lookup + generator invocation the renderer uses in production.
/// </summary>
public sealed class PCellPinOverlayTests : IDisposable
{
    private readonly string _root;

    public PCellPinOverlayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-pin-overlay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (LayoutView View, LayoutInstance Inst, double LMeters) BuildMlinInstance(string vmBaseDir)
    {
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vmBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };
        view.Instances.Add(inst);
        return (view, inst, defaults.Real("L"));
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(Math.Clamp(x, 0, bmp.Width - 1), Math.Clamp(y, 0, bmp.Height - 1));
    }

    private static bool IsPinColor(SKColor c)
    {
        var pin = LayoutRenderTheme.Light.PCellPin;
        return Math.Abs(c.Red - pin.Red) < 40 && Math.Abs(c.Green - pin.Green) < 40 && Math.Abs(c.Blue - pin.Blue) < 40;
    }

    private static bool PinColorNear(SKSurface surface, int sx, int sy, int radius = 3)
    {
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                if (IsPinColor(PixelAt(surface, sx + dx, sy + dy))) return true;
        return false;
    }

    [Fact]
    public void ShowPCellPins_True_PaintsADotAtEachResolvedInstancePin()
    {
        var vmBaseDir = Path.Combine(_root, "doc");
        Directory.CreateDirectory(vmBaseDir);
        var (view, _, lMeters) = BuildMlinInstance(vmBaseDir);
        long lDbu = (long)Math.Round(lMeters * 1_000_000_000); // metres -> nm (DbuPerMicron=1000)

        var vp = new LayoutViewport(-1_000_000, -1_000_000, 3e-5, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPCellPins = true, BaseDir = vmBaseDir };
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);

        int sx1 = (int)vp.WorldToScreenX(0), sy1 = (int)vp.WorldToScreenY(0);
        int sx2 = (int)vp.WorldToScreenX(lDbu), sy2 = (int)vp.WorldToScreenY(0);

        Assert.True(PinColorNear(surface, sx1, sy1), "expected a pin marker at MLIN's pin 1 (0,0)");
        Assert.True(PinColorNear(surface, sx2, sy2), "expected a pin marker at MLIN's pin 2 (L,0)");
    }

    [Fact]
    public void ShowPCellPins_False_PaintsNoPinMarkers()
    {
        var vmBaseDir = Path.Combine(_root, "doc");
        Directory.CreateDirectory(vmBaseDir);
        var (view, _, lMeters) = BuildMlinInstance(vmBaseDir);
        long lDbu = (long)Math.Round(lMeters * 1_000_000_000);

        var vp = new LayoutViewport(-1_000_000, -1_000_000, 3e-5, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPCellPins = false, BaseDir = vmBaseDir };
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);

        int sx1 = (int)vp.WorldToScreenX(0), sy1 = (int)vp.WorldToScreenY(0);
        int sx2 = (int)vp.WorldToScreenX(lDbu), sy2 = (int)vp.WorldToScreenY(0);

        Assert.False(PinColorNear(surface, sx1, sy1), "ShowPCellPins=false must paint no pin marker at pin 1");
        Assert.False(PinColorNear(surface, sx2, sy2), "ShowPCellPins=false must paint no pin marker at pin 2");
    }

    [Fact]
    public void LayoutRenderOptions_Default_ShowPCellPinsIsFalse_ExportsNeverOptIn()
    {
        // R-L5g-14: every one-shot export render (which never sets ShowPCellPins explicitly, exactly
        // like every existing export call site never sets ShowGrid/Overlay) draws no pins, by
        // construction of the struct's own default(bool) — this is what the export code paths rely
        // on without needing to know this option exists at all.
        Assert.False(default(LayoutRenderOptions).ShowPCellPins);
    }

    [Fact]
    public void LayoutRenderOptions_DefaultFactory_ShowPCellPinsIsTrue()
    {
        // The one full "everything on" factory (used by ad-hoc/interactive-equivalent renders)
        // matches the interactive canvas's own default-on toggle (R-L5g-15).
        Assert.True(LayoutRenderOptions.Default(LayoutRenderTheme.Light).ShowPCellPins);
    }

    [Fact]
    public void PinOverlay_NeverContributesToGeometryCounters()
    {
        var vmBaseDir = Path.Combine(_root, "doc");
        Directory.CreateDirectory(vmBaseDir);
        var (view, _, _) = BuildMlinInstance(vmBaseDir);

        var vp = new LayoutViewport(-1_000_000, -1_000_000, 3e-5, 400, 400);
        using var surfaceOn = SKSurface.Create(new SKImageInfo(400, 400));
        var resultOn = LayoutRenderer.Draw(surfaceOn.Canvas, view, null, vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPCellPins = true, BaseDir = vmBaseDir });

        using var surfaceOff = SKSurface.Create(new SKImageInfo(400, 400));
        var resultOff = LayoutRenderer.Draw(surfaceOff.Canvas, view, null, vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPCellPins = false, BaseDir = vmBaseDir });

        // PathsConstructed/DrawCalls are deliberately NOT compared here — CompileCell's own
        // ConditionalWeakTable cache is shared (keyed by the resolved sub-cell LayoutView reference)
        // across these two back-to-back Draw() calls against the SAME view, so the second call is a
        // cache hit regardless of ShowPCellPins and would show a lower PathsConstructed no matter what
        // — a caching artifact of this test's own two-calls-one-view shape, not evidence about the pin
        // overlay. The real invariant (DrawPCellPinOverlay never touches any LayoutFrameCounters field
        // at all) is true by direct construction — confirmed by reading the method, not asserted here.
        Assert.Equal(resultOff.ShapesExamined, resultOn.ShapesExamined);
        Assert.Equal(resultOff.ShapesDrawn, resultOn.ShapesDrawn);
    }

    [Fact]
    public void LayoutEditorViewModel_ShowPCellPins_DefaultsTrue()
    {
        string clayPath = Path.Combine(_root, "vmdoc", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
        Assert.True(vm.ShowPCellPins);
    }
}
