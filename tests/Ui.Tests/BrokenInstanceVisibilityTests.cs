using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  A broken instance has to be FINDABLE, at PCB extents, in numbers.
//  (owner, 2026-09-05: working at board level, the Not Found marker could not be
//   seen, so there was no way to tell where the missing artwork was.)
//
//  The placeholder's stored extent is a fixed 50 um. On a 100 mm board that is
//  0.05% of the width — under one pixel — so the marker for missing artwork was
//  invisible exactly where it mattered most. These tests pin the screen-space
//  floor that fixes it, and the two things that keep it affordable when a
//  workspace has lost a whole library at once.
//
//  Pixel probes rather than a stopwatch: what is being asserted is "this is on
//  screen and this big", which is a property of the drawing, not of the machine.
// ──────────────────────────────────────────────────────────────────────────────

// One test here asserts an exact CellStat.Calls count, which is process-global — see
// CellStatGlobalsCollection's own note ("add a class here the moment it asserts on CellStat.Calls").
// It was missing from that collection, and a full-solution run duly reported "4040 calls for 2000
// placements" for a paint that resolves once per missing cell; it passes alone every time.
[Collection(CellStatGlobalsCollection.Name)]
public sealed class BrokenInstanceVisibilityTests(ITestOutputHelper Out)
{
    /// <summary>100 mm, in DBU at the default 1000 DBU/µm.</summary>
    private const long BoardDbu = 100_000_000;

    private const int PixelsWide = 1500, PixelsHigh = 1000;

    private static LayoutView BrokenLayout(int count)
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        var rng = new Random(7);
        for (int i = 0; i < count; i++)
            view.Instances.Add(new LayoutInstance
            {
                CellRef = $"../../Gone{i % 40}",   // a lost LIBRARY: many placements, few missing cells
                X = rng.NextInt64(0, BoardDbu),
                Y = rng.NextInt64(0, BoardDbu),
                Mag = 1.0,
            });
        return view;
    }

    /// <summary>Renders and returns (bitmap, stats). <paramref name="dbuAcross"/> is how much of the
    /// design the window spans — <see cref="BoardDbu"/> is "the whole board".</summary>
    private static (SKBitmap Bmp, LayoutRenderResult Stats) Render(LayoutView view, long dbuAcross)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "crf_brokenvis_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(baseDir);
        try
        {
            var vp = new LayoutViewport(0, 0, (double)PixelsWide / dbuAcross, PixelsWide, PixelsHigh);
            using var surface = SKSurface.Create(new SKImageInfo(PixelsWide, PixelsHigh));
            var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
            var stats = LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);
            using var img = surface.Snapshot();
            return (SKBitmap.FromImage(img), stats);
        }
        finally { try { Directory.Delete(baseDir, true); } catch { } }
    }

    /// <summary>Pixels that are not the background — the marker, whatever colour the theme gives it.</summary>
    private static int MarkedPixels(SKBitmap bmp)
    {
        SKColor background = bmp.GetPixel(0, 0);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
            if (bmp.GetPixel(x, y) != background) n++;
        return n;
    }

    [Fact]
    public void OneBrokenInstance_IsVisibleWithTheWHOLEBoardOnScreen()
    {
        // The bug, stated as a measurement. At this zoom the placeholder's own 50 um extent is
        // 0.75 px — so without a screen floor this count is a pixel or two, i.e. nothing. The floor
        // is 28 device pixels, so a filled-and-outlined box owes several hundred.
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "../../Gone", X = BoardDbu / 2, Y = BoardDbu / 2, Mag = 1.0 });

        var (bmp, stats) = Render(view, BoardDbu);
        using (bmp)
        {
            int marked = MarkedPixels(bmp);
            Out.WriteLine($"whole board: {marked} px marked, {stats.InstancesDrawn} placeholder(s)");

            Assert.Equal(1, stats.InstancesDrawn);
            // A 28 px box, filled and outlined; well over 400 px even allowing for the theme's alpha.
            Assert.True(marked > 400, $"a broken instance drew only {marked} px at board extent — it is invisible");
        }
    }

    [Fact]
    public void TheFloorDoesNotINFLATEAPlaceholderThatIsAlreadyBigEnough()
    {
        // The other side of the rule: zoomed in far enough that the stored 50 um extent is already
        // larger than the floor, the placeholder must be drawn at its own size — a floor that always
        // won would make every broken instance the same size regardless of zoom, which reads as the
        // marker having come loose from the design.
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "../../Gone", X = 100_000, Y = 100_000, Mag = 1.0 });

        // 400 um across 1500 px: the 100 um-wide placeholder is ~375 px, far above the 28 px floor.
        var (bmp, _) = Render(view, 400_000);
        using (bmp)
        {
            int marked = MarkedPixels(bmp);
            Out.WriteLine($"zoomed in: {marked} px marked");
            // Its own extent, not the floor: a 28 px box could never account for this many.
            Assert.True(marked > 20_000, $"a zoomed-in placeholder drew {marked} px — it was clamped to the floor");
        }
    }

    [Fact]
    public void ThousandsOfBrokenInstances_CostTwoDrawCallsEach_AndNothingPerFrameBeyondThat()
    {
        // "What if there are thousands of unreferenced cells — we still need to render fast so the
        // user can repair the references" (owner, 2026-09-05). A COUNTER, not a stopwatch: what has
        // to hold is that the per-placeholder work is a constant two marks, with no text and no
        // per-instance paint construction hiding behind it.
        //
        // What this pins is the shape that made the measured difference. At 5,000 broken instances
        // over a whole board the frame went from 382 ms to 15 ms, and essentially all of it was two
        // things the floor made pointless anyway: a DASHED stroke (Skia builds dash geometry per
        // rect) and a text label on a 28-pixel box. Both are now skipped exactly when the floor is
        // applied, which is also exactly when neither could be seen.
        var (bmp, stats) = Render(BrokenLayout(5000), BoardDbu);
        using (bmp)
        {
            Out.WriteLine($"{stats.InstancesDrawn} drawn, {stats.DrawCalls} draw calls");
            Assert.True(stats.InstancesDrawn > 1000, "the fixture should put thousands of them on screen");
            Assert.Equal(stats.InstancesDrawn * 2, stats.DrawCalls);
        }
    }

    [Fact]
    public void ASteadyStateRepaint_DoesNotReResolveEveryPLACEMENT_OnlyEveryMissingCell()
    {
        // A broken reference costs a real filesystem stat every time it is resolved — CellStat caches
        // a TRUE answer but deliberately never a FALSE one (R-sl4-8: a folder missing because a share
        // blinked must be re-asked on the very next resolve). That is right for RESOLUTION and wrong
        // for PAINTING, since the renderer re-resolves every visible instance on every frame. The
        // frame-scoped memo cannot show a stale answer — no folder appears halfway through a paint —
        // and it collapses a lost library's thousands of placements onto its handful of missing cells.
        //
        // 2,000 placements of 40 missing cells. Measured steady state: ~2,040 calls, of which the
        // paint contributes ~40. Without the memo the paint alone contributes one per PLACEMENT, so
        // the bound below separates the two decisively while leaving room for the constant.
        //
        // KNOWN AND NOT FIXED HERE: the remaining ~2,000 is a different path — the spatial index
        // measures each instance's bbox, and CellHierarchy.InstanceBbox resolves to do it. It is one
        // call per instance per frame and it is NOT what made the frame slow (the dash was), so it is
        // recorded rather than chased. This bound is deliberately loose enough to pass with it and
        // tight enough to fail without the memo.
        const int Placements = 2000;
        var view = BrokenLayout(Placements);

        Render(view, BoardDbu).Bmp.Dispose();            // warm the spatial index
        CellStat.ResetCalls();
        var (bmp, stats) = Render(view, BoardDbu);
        using (bmp)
        {
            long calls = CellStat.Calls;
            Out.WriteLine($"steady state: {stats.InstancesDrawn} placeholders drawn, {calls} CellStat calls "
                        + $"for {Placements} placements of 40 missing cells");

            Assert.True(calls < Placements * 1.5,
                $"{calls} filesystem calls for {Placements} placements — the paint is resolving per "
              + "placement rather than per missing cell");
        }
    }
}
