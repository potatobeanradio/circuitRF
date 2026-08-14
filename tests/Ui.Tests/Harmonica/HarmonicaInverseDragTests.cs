// ================================================================
//  HarmonicaInverseDragTests.cs  —  M2/M3 through the DOCUMENT, brief-harmonicarf-h6
//
//  InverseSolveTests / ReachabilityTests gate the maths headlessly. This gates the WIRING: that a
//  pointer on a glyph reaches the solver, that the answer comes back on the frame and lands in the
//  terminations on the UI thread, that a refusal moves nothing and says so, and that the shading is
//  computed once per drag rather than once per frame.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaInverseDragTests(ITestOutputHelper output)
{
    // A big canvas on purpose: the Smith panel is a fraction of it (§7.1), and the whole point of
    // these tests is that the glyph and its marker are FAR ENOUGH APART IN PIXELS to be grabbed
    // separately. At 1200 × 800 the power panel is 390 px wide and this fixture's 0.05 Γ separation
    // is ~10 px — inside the 14 px grab radius, so the z-ordered hit test correctly took the marker
    // and the test was measuring the wrong gesture. GlyphIsSeparatelyGrabbable below pins that.
    private const double W = 2400, H = 1600;

    private static (double X, double Y) OnPowerPanel(HarmonicaViewModel vm, Complex gamma)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var local = HarmonicaPanelRenderer.MarkerToCanvas(gamma, (p.W * W, p.H * H));
        return (p.X * W + local.X, p.Y * H + local.Y);
    }

    /// <summary>
    /// A document with one solved frame in it, and the ladder already at freeze-and-snap.
    ///
    /// <para>The ladder is pushed there by recording hopeless frames DIRECTLY on the scheduler rather
    /// than by solving several — the point of these tests is the inverse-drag wiring, and on this
    /// model a contour-bearing frame is ~600 ms, so paying for four of them per test would buy
    /// nothing but wall-clock.</para>
    /// </summary>
    /// <summary>
    /// The default document's DUT with a real extrinsic package.
    ///
    /// <para><b>The package is not decoration, it is what makes an intrinsic drag testable at all.</b>
    /// §4.5 consequence 1: the glyph coincides with its marker when charge is off AND there is no
    /// extrinsic network — and the shipped default document is exactly that (a chargeless SDD, no
    /// package), so its glyph sits ~0.003 Γ from its marker and the z-ordered hit test correctly
    /// grabs the marker on top. A series Rd/Rs/Ls separates the two planes, which is both the
    /// realistic case and the only one where "grab the glyph" means anything.</para>
    ///
    /// <para><b>R-h9r2-19 note:</b> this fixture's own DUT equation GAIN-EXPANDS by ~3 dB before it
    /// ever compresses (rises smoothly from Pin −10 to a peak of ~14.8 dB around +22 dBm, then rolls
    /// over) — its own physics, unrelated to the package above. Before this brief, <c>PinSearch.Run</c>
    /// silently gave up its bracket search a few solves in and fell back to whatever it had (a mild,
    /// low-Pin point), which is why an inverse drag at "the compression point" here used to be
    /// benign. <c>PinSearch.Sweep</c> now finds the genuine 3 dB-down-from-its-own-peak crossing
    /// honestly — around +27 dBm, deep enough into this DUT's own saturation that a cold FD Jacobian
    /// for an 0.05-Γ intrinsic drag no longer converges there. <c>PinMaxDbm</c> is capped well below
    /// the peak so the sweep still (honestly) never reaches compression and falls back to its last
    /// solved, well-behaved point — mirroring the operating point this test always exercised, without
    /// relying on the old algorithm's silent early bail-out to get there.</para>
    /// </summary>
    private static CircuitModel ModelWithAPackage()
    {
        var m = HarmonicaViewModel.DefaultModel();
        return m with
        {
            Embedding = new EmbeddingStack
            {
                // Deliberately exaggerated — a real GaN HEMT's Rd is an ohm or two. The separation
                // between the two planes has to exceed the grab radius IN PIXELS for the gesture to be
                // exercisable at all, and a realistic package puts the glyph ~10 px from its marker.
                Package = new LumpedPackage { Rd = 20.0, Rs = 2.0, Ls = 100e-12 },
            },
            Settings = m.Settings with { PinMaxDbm = 15.0 },
        };
    }

    private static async Task<HarmonicaViewModel> DocumentAtTierAOnly()
    {
        var vm = new HarmonicaViewModel(ModelWithAPackage());
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);

        for (int i = 0; i < 4; i++)
            vm.Scheduler.RecordFrame(vm.Scheduler.NextPlan(dragging: true),
                                     new FrameTiming(4, 900, 6, 90, 10));
        Assert.Equal(FrameQuality.FrozenContours, vm.Scheduler.Quality);

        vm.RequestScheduledFrame(dragging: true);
        await vm.Pool.DrainAsync();
        return vm;
    }

    /// <summary>Pixel distance between a marker and its own glyph on the power panel.</summary>
    private static double GlyphSeparationPixels(HarmonicaViewModel vm, HarmonicaMarker m)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var a = HarmonicaPanelRenderer.MarkerToCanvas(m.Gamma,          (p.W * W, p.H * H));
        var b = HarmonicaPanelRenderer.MarkerToCanvas(m.GammaIntrinsic, (p.W * W, p.H * H));
        return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }

    // ══ M2 — the glyph drag reaches the solver and the answer lands ══════════

    [Fact]
    public async Task DraggingAnIntrinsicGlyph_MovesTheEXTRINSICTerminations_AndLandsTheGlyphOnTarget()
    {
        var vm = await DocumentAtTierAOnly();

        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 }); // L1
        Assert.NotEqual(Complex.Zero, marker.GammaIntrinsic);
        output.WriteLine($"before: {marker.Name} extrinsic Γ = {Fmt(marker.Gamma)}, " +
                         $"intrinsic Γ = {Fmt(marker.GammaIntrinsic)}");

        var extrinsicBefore = marker.Gamma;
        var glyphBefore     = marker.GammaIntrinsic;

        // The fixture must actually SEPARATE the two, or the z-ordered hit test takes the marker on
        // top and this test silently measures an extrinsic drag instead.
        double sep = GlyphSeparationPixels(vm, marker);
        output.WriteLine($"glyph is {sep:F0} px from its marker " +
                         $"(grab radius {HarmonicaHitTest.GrabRadiusDevicePixels})");
        Assert.True(sep > HarmonicaHitTest.GrabRadiusDevicePixels + 4,
            $"the glyph is only {sep:F0} px from its marker — this fixture cannot exercise an " +
            "intrinsic drag, because the marker is drawn on top and wins the hit test");

        // Grab the GLYPH. The marker sits elsewhere on the chart, so the hit test resolves to the
        // triangle rather than to the circle on top of it.
        var g = new HarmonicaGesture(vm);
        var (gx, gy) = OnPowerPanel(vm, glyphBefore);
        Assert.True(g.PointerDown(gx, gy, W, H));
        Assert.Equal(HarmonicaGrabKind.IntrinsicGlyph, g.Grab.Kind);
        Assert.Same(marker, g.Grab.Marker);
        Assert.True(vm.IsInverseDragging);

        // A short drag, then release.
        var target = glyphBefore + new Complex(0.04, -0.03);
        var (tx, ty) = OnPowerPanel(vm, target);
        g.PointerMoved(tx, ty, W, H);
        await vm.Pool.DrainAsync();
        g.PointerUp(tx, ty, W, H);
        await vm.Pool.DrainAsync();

        output.WriteLine($"after : {marker.Name} extrinsic Γ = {Fmt(marker.Gamma)}, " +
                         $"intrinsic Γ = {Fmt(marker.GammaIntrinsic)}   (target {Fmt(target)})");
        output.WriteLine($"status: {vm.StatusMessage ?? "(none)"}");

        Assert.Null(vm.InverseMessage);

        // The EXTRINSIC termination is what moved — that is the whole point of an inverse solve.
        Assert.True((marker.Gamma - extrinsicBefore).Magnitude > 1e-3,
            "the extrinsic termination did not move, so nothing was solved for");

        // …and the TerminationSet the engine reads moved with it, not just the marker.
        var z = vm.Terminations.Z(TerminationSide.Load, 1);
        var gammaOfZ = HarmonicaDataSet.GammaOf(z, vm.Model.Settings.Z0);
        Assert.Equal(marker.Gamma.Real,      gammaOfZ.Real,      precision: 9);
        Assert.Equal(marker.Gamma.Imaginary, gammaOfZ.Imaginary, precision: 9);

        // The GLYPH landed on the target. This is read off the published frame, which came from an
        // ordinary forward solve of the answer — so it is a round trip through the forward path, the
        // same oracle InverseSolveTests uses, taken here through the document.
        double err = (marker.GammaIntrinsic - target).Magnitude;
        output.WriteLine($"glyph landed {err:E3} from the target");
        Assert.True(err < 5e-3, $"the glyph landed {err:E3} from where it was dragged");

        Assert.False(vm.IsInverseDragging);
    }

    [Fact]
    public async Task AnUnreachableTargetThroughTheDocument_MovesNothing_AndSaysSo()
    {
        var vm = await DocumentAtTierAOnly();

        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 }); // L1
        var extrinsicBefore = marker.Gamma;
        var termsBefore     = vm.Terminations.Z(TerminationSide.Load, 1);
        var glyphBefore     = marker.GammaIntrinsic;

        var g = new HarmonicaGesture(vm);
        var (gx, gy) = OnPowerPanel(vm, glyphBefore);
        Assert.True(g.PointerDown(gx, gy, W, H));

        // Somewhere on the far side of the panel from anything this device can produce.
        vm.DragIntrinsicGlyph(marker, new Complex(-38.0, 44.0), dragging: true);
        await vm.Pool.DrainAsync();

        output.WriteLine($"status: {vm.StatusMessage}");
        Assert.NotNull(vm.InverseMessage);
        Assert.Contains("nothing moved", vm.InverseMessage!, StringComparison.Ordinal);

        // EXACTLY where they were.
        Assert.Equal(extrinsicBefore.Real,      marker.Gamma.Real);
        Assert.Equal(extrinsicBefore.Imaginary, marker.Gamma.Imaginary);
        Assert.Equal(termsBefore, vm.Terminations.Z(TerminationSide.Load, 1));

        g.Cancel();
    }

    [Fact]
    public async Task TheInverseSolveRunsOnTheWORKER_NotOnTheCallingThread()
    {
        // §6.7 / R-h6-4's standing constraint. The pool's own counters are the evidence: an inverse
        // frame is submitted like any other, so it is started by a worker and superseded like any
        // other.
        var vm = await DocumentAtTierAOnly();
        int startedBefore = vm.Pool.StartedCount;

        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 }); // L1
        var g = new HarmonicaGesture(vm);
        var (gx, gy) = OnPowerPanel(vm, marker.GammaIntrinsic);
        Assert.True(g.PointerDown(gx, gy, W, H));

        for (int i = 1; i <= 6; i++)
        {
            var t = marker.GammaIntrinsic + new Complex(0.006 * i, -0.004 * i);
            var (tx, ty) = OnPowerPanel(vm, t);
            g.PointerMoved(tx, ty, W, H);
        }
        await vm.Pool.DrainAsync();
        g.PointerUp(gx, gy, W, H);
        await vm.Pool.DrainAsync();

        output.WriteLine($"pool: {vm.Pool.StartedCount - startedBefore} inverse frames started, " +
                         $"{vm.Pool.SupersededCount} superseded overall");
        Assert.True(vm.Pool.StartedCount > startedBefore);
        Assert.True(vm.Pool.SupersededCount > 0,
            "a six-move inverse drag that superseded nothing is queueing");

        // Every worker that ran did so on its OWN context and never rebuilt the netlist: an inverse
        // drag is a VALUE change like any other.
        foreach (var w in vm.Pool.Workers)
            Assert.True(w.ContextRebuildCount <= 1,
                $"worker {w.Index} rebuilt its netlist {w.ContextRebuildCount} times during a drag");
    }

    // ══ M3 — the shading is computed ONCE per drag ═══════════════════════════

    [Fact]
    public async Task Tier12_TheReachableRegionIsSampledOncePerDrag_NotOncePerFrame()
    {
        var vm = await DocumentAtTierAOnly();
        Assert.True(vm.ShowReachableRegion);
        Assert.Equal(0, vm.ReachabilitySampleCount);

        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 }); // L1
        var g = new HarmonicaGesture(vm);
        var (gx, gy) = OnPowerPanel(vm, marker.GammaIntrinsic);
        Assert.True(g.PointerDown(gx, gy, W, H));

        const int Frames = 8;
        for (int i = 1; i <= Frames; i++)
        {
            var t = marker.GammaIntrinsic + new Complex(0.004 * i, 0.003 * i);
            var (tx, ty) = OnPowerPanel(vm, t);
            g.PointerMoved(tx, ty, W, H);
            await vm.Pool.DrainAsync();          // drain per move so every frame really runs
        }
        g.PointerUp(gx, gy, W, H);
        await vm.Pool.DrainAsync();

        output.WriteLine($"{Frames + 1} inverse frames → ReachabilitySampleCount = " +
                         $"{vm.ReachabilitySampleCount}");
        Assert.Equal(1, vm.ReachabilitySampleCount);

        // …and the region actually reached the panel, so "cached" is not "never computed".
        Assert.NotNull(vm.Frame.SmithPower.Reachable);
        Assert.False(vm.Frame.SmithPower.Reachable!.IsEmpty);
        Assert.Same(vm.Frame.SmithPower.Reachable, vm.Frame.SmithEfficiency.Reachable);
        output.WriteLine($"region: {vm.Frame.SmithPower.Reachable.Boundary.Count} boundary points, " +
                         $"area {vm.Frame.SmithPower.Reachable.Area:F4} Γ², " +
                         $"{vm.Frame.SmithPower.Reachable.Solves} solves, " +
                         $"{vm.Frame.SmithPower.Reachable.Dropped} dropped");
    }

    [Fact]
    public async Task Tier12_TurningTheShadingOff_SkipsTheSamplingEntirely()
    {
        // Open item 4's escape hatch: it is AUTOMATIC because it measured cheap, but a slow model can
        // still be told not to.
        var vm = await DocumentAtTierAOnly();
        vm.ShowReachableRegion = false;

        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 }); // L1
        var g = new HarmonicaGesture(vm);
        var (gx, gy) = OnPowerPanel(vm, marker.GammaIntrinsic);
        Assert.True(g.PointerDown(gx, gy, W, H));
        var (tx, ty) = OnPowerPanel(vm, marker.GammaIntrinsic + new Complex(0.02, 0.01));
        g.PointerMoved(tx, ty, W, H);
        await vm.Pool.DrainAsync();
        g.PointerUp(tx, ty, W, H);
        await vm.Pool.DrainAsync();

        Assert.Equal(0, vm.ReachabilitySampleCount);
        Assert.Null(vm.Frame.SmithPower.Reachable);
    }

    // ══ R-h6-10 — an out-of-circle EXTRINSIC marker is FLAGGED, not clamped ══

    [Fact]
    public void Tier10_AnActiveExtrinsicMarker_IsDrawnBeyondTheRimWithAHatchedOutline()
    {
        const int Size = 460;
        var theme = HarmonicaRenderTheme.Dark;
        SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
        try
        {
            // Two identical panels except for the marker's |Γ|: one passive, one active. The
            // difference between the two renders is the flag, by construction — the same differential
            // oracle H4–H5 had to invent when a colour probe could not separate an iso-line from
            // chart chrome.
            var passive = new HarmonicaMarker(TerminationSideKind.Load, 1)
            { Gamma = new Complex(0.60, 0.0), GammaIntrinsic = new Complex(0.60, 0.0) };
            var active = new HarmonicaMarker(TerminationSideKind.Load, 1)
            { Gamma = new Complex(1.60, 0.0), GammaIntrinsic = new Complex(0.60, 0.0) };

            Assert.False(passive.ExtrinsicIsOutsideUnitCircle);
            Assert.True(active.ExtrinsicIsOutsideUnitCircle);

            using var bmpP = Render(new SmithPanelData { Markers = [passive] }, theme, Size);
            using var bmpA = Render(new SmithPanelData { Markers = [active]  }, theme, Size);

            var rim    = HarmonicaPanelRenderer.GammaToCanvas(Complex.One, (Size, Size));
            var centre = HarmonicaPanelRenderer.GammaToCanvas(Complex.Zero, (Size, Size));
            var at     = HarmonicaPanelRenderer.MarkerToCanvas(active.Gamma, (Size, Size));

            output.WriteLine($"rim x = {rim.X:F1}, active marker drawn at x = {at.X:F1} " +
                             $"(centre {centre.X:F1})");

            // NOT CLAMPED: it is drawn strictly beyond the Γ = 1 rim…
            Assert.True(at.X > rim.X + 1,
                $"an |Γ| > 1 extrinsic marker was drawn at x = {at.X:F1}, at or inside the rim " +
                $"({rim.X:F1}) — R-h6-10 forbids clamping");
            // …and NOT HIDDEN: still on the panel, which is what the annulus headroom exists for.
            Assert.True(at.X < Size - 2, "the active marker was drawn off the panel");

            // FLAGGED: the hatched outline reaches further from the marker centre than the plain
            // outline does. Measured as the outermost differing pixel on the +x ray from each
            // marker's own centre.
            double plainReach  = OutermostMarkerPixel(bmpP, HarmonicaPanelRenderer.MarkerToCanvas(
                                     passive.Gamma, (Size, Size)), theme, Size);
            double hatchReach  = OutermostMarkerPixel(bmpA, at, theme, Size);
            output.WriteLine($"outline reach: plain {plainReach:F0} px, hatched {hatchReach:F0} px");
            Assert.True(hatchReach > plainReach + 2,
                $"the active marker's outline reaches {hatchReach:F0} px against the plain one's " +
                $"{plainReach:F0} — the hatch is not distinguishable");
        }
        finally
        {
            SkiaFonts.TestOverrideTypeface = null;
        }
    }

    /// <summary>How far from a marker's centre the outermost non-background pixel sits, sampled on
    /// eight rays so a dashed ring cannot be missed by landing between dashes on one of them.</summary>
    private static double OutermostMarkerPixel(SKBitmap bmp, SKPoint centre,
                                               HarmonicaRenderTheme theme, int size)
    {
        double best = 0;
        for (int k = 0; k < 8; k++)
        {
            double a = Math.PI * 2 * k / 8;
            for (int d = 1; d < 40; d++)
            {
                int x = (int)Math.Round(centre.X + Math.Cos(a) * d);
                int y = (int)Math.Round(centre.Y + Math.Sin(a) * d);
                if (x < 0 || x >= size || y < 0 || y >= size) break;
                var c = bmp.GetPixel(x, y);
                // The marker band and its outline are the only things painted out here; the chart's
                // own arcs are excluded by staying within 40 px of the marker AND requiring a strong
                // departure from the background.
                if (Math.Abs(c.Red - theme.Background.Red) > 40 ||
                    Math.Abs(c.Blue - theme.Background.Blue) > 40)
                    best = Math.Max(best, d);
            }
        }
        return best;
    }

    private static SKBitmap Render(SmithPanelData d, HarmonicaRenderTheme theme, int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (size, size), d, theme, darkMode: true);
        return SKBitmap.FromImage(surface.Snapshot());
    }

    private static string Fmt(Complex z)
        => $"{z.Real:F4}{(z.Imaginary < 0 ? "" : "+")}{z.Imaginary:F4}j";
}
