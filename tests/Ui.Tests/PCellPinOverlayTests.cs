using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §6 (R-L5g-13/14/15): PCell pins render as a
/// screen-space overlay — a constant-pixel-size dot in a theme color distinct from every layer
/// color, and NOTHING ELSE (the outward-direction tick R-L5g-13 originally drew was removed on
/// 2026-08-09; it read as an EM port direction indicator — see
/// <c>LayoutRenderer.DrawPCellPinOverlay</c>'s own doc comment) — never as layer geometry, gated by
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

    /// <summary>
    /// Owner request, 2026-08-09: "change the pin rendering geometry from circle to square (for
    /// layout). This matches the pin shape for the symbols in the schematic editor."
    ///
    /// <para>Measured by COUNTING the strictly-interior painted pixels rather than probing one
    /// corner: at a 3 device-pixel half-size, a square's fully-covered pixels number 25 (|dx|,|dy| ≤ 2)
    /// while a circle's number ~17, so the two shapes separate cleanly with no reliance on how any
    /// single antialiased edge pixel resolves. Probing a single corner does NOT separate them —
    /// (2,2) is inside a radius-3 circle too, and every pixel that is not lies exactly on the
    /// square's own antialiased boundary.</para>
    /// </summary>
    [Fact]
    public void ThePinMarker_IsASquare_NotACircle()
    {
        var vmBaseDir = Path.Combine(_root, "doc-square");
        Directory.CreateDirectory(vmBaseDir);
        var (view, _, _) = BuildMlinInstance(vmBaseDir);

        var vp = new LayoutViewport(-1_000_000, -1_000_000, 3e-5, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPCellPins = true, BaseDir = vmBaseDir };
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);

        int cx = (int)vp.WorldToScreenX(0), cy = (int)vp.WorldToScreenY(0);

        int interior = 0;
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
                if (IsPinColor(PixelAt(surface, cx + dx, cy + dy))) interior++;

        // A circle of the same half-size cannot fill this 5x5 block; a square fills it entirely.
        Assert.True(interior >= 22,
            $"expected a filled square pin marker (>= 22 of 25 interior pixels painted), got {interior}");
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
    public void ShowPCellPins_True_DrawsNoOutwardDirectionTick_OnlyTheDot()
    {
        // Owner report (2026-08-09): the outward-direction line R-L5g-13 drew read as an EM PORT
        // direction indicator. It is gone. MLIN's two pins face 180° (pin 1, at 0,0) and 0° (pin 2,
        // at L,0) — both AWAY from the metal — so a tick would paint pin-coloured pixels in
        // otherwise-empty space just outside each end. Probe there: the dot's own 3 px radius must
        // not reach, and nothing else may.
        var vmBaseDir = Path.Combine(_root, "doc");
        Directory.CreateDirectory(vmBaseDir);
        var (view, _, lMeters) = BuildMlinInstance(vmBaseDir);
        long lDbu = (long)Math.Round(lMeters * 1_000_000_000);

        var vp = new LayoutViewport(-1_000_000, -1_000_000, 3e-5, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPCellPins = true, BaseDir = vmBaseDir };
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);

        int sx1 = (int)vp.WorldToScreenX(0), sy1 = (int)vp.WorldToScreenY(0);
        int sx2 = (int)vp.WorldToScreenX(lDbu), sy2 = (int)vp.WorldToScreenY(0);

        // Sanity: the dots themselves are still there (so a vacuous pass is impossible).
        Assert.True(PinColorNear(surface, sx1, sy1), "the pin dot itself must still be drawn at pin 1");
        Assert.True(PinColorNear(surface, sx2, sy2), "the pin dot itself must still be drawn at pin 2");

        // The old tick ran 9 device px outward from the dot centre; the dot's radius is 3.
        for (int d = 6; d <= 9; d++)
        {
            Assert.False(IsPinColor(PixelAt(surface, sx1 - d, sy1)),
                $"no outward tick may be drawn from pin 1 (found pin colour {d} px to its left)");
            Assert.False(IsPinColor(PixelAt(surface, sx2 + d, sy2)),
                $"no outward tick may be drawn from pin 2 (found pin colour {d} px to its right)");
        }
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
