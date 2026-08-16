// ================================================================
//  HarmonicaDragTests.cs  —  M1's gate, brief-harmonicarf-h6
//
//  R-h6-1  one hit-test, resolved through GammaToCanvas / CanvasToGamma — never PlotRenderer's own
//          transform, which does not know about the annulus headroom.
//  R-h6-2  the grab radius is a DEVICE-PIXEL constant at every panel size.
//  R-h6-3  the drag writes nothing to the model beyond the marker itself.
//  R-h6-4  the frame loop is the SCHEDULER's; the gesture only says "dragging" or "not".
//  R-h6-5  StatusMessage reaches the strip.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica.Renderers;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaDragTests(ITestOutputHelper output)
{
    private sealed class Clock
    {
        private double _now;
        public double Read() => _now;
        public void Advance(double ms) => _now += ms;
    }

    /// <summary>Canvas coordinates of a Γ on the POWER Smith panel, at this canvas size — through the
    /// same layout arithmetic the gesture uses, so the fixture cannot drift from the code.</summary>
    private static (double X, double Y) OnPowerPanel(HarmonicaViewModel vm, Complex gamma,
                                                     double w, double h)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        // R8B §2 — an extrinsic termination marker is on the PLAIN chart map now, never the
        // compressed intrinsic one MarkerToCanvas used to compose in.
        var local = HarmonicaPanelRenderer.GammaToCanvas(gamma, (p.W * w, p.H * h));
        return (p.X * w + local.X, p.Y * h + local.Y);
    }

    // ══ R-h6-1 — ONE transform pair, and it round-trips ══════════════════════

    [Theory]
    [InlineData(300, 300)]
    [InlineData(900, 640)]
    [InlineData(421, 419)]
    public void Tier1_CanvasToGammaIsTheExactInverseOfGammaToCanvas(int w, int h)
    {
        Complex[] probes =
        [
            Complex.Zero, new(0.5, 0), new(-0.7, 0.2), new(0.31, -0.62), new(0.0, 0.85),
        ];

        foreach (var g in probes)
        {
            var p    = HarmonicaPanelRenderer.GammaToCanvas(g, (w, h));
            var back = HarmonicaPanelRenderer.CanvasToGamma(p, (w, h));
            Assert.Equal(g.Real,      back.Real,      precision: 4);
            Assert.Equal(g.Imaginary, back.Imaginary, precision: 4);
        }
    }

    [Fact]
    public void Tier1_TheInverseCarriesTheANNULUSHEADROOM_UnlikePlotRenderersOwnTransform()
    {
        // The trap R-h6-1 names: PlotRenderer's raw transform does not know about
        // HarmonicaPanelRenderer.AnnulusHeadroom, so inverting IT is off by that factor — visibly, at
        // the rim, which is exactly where markers sit. This measures the discrepancy rather than
        // asserting it is absent.
        const int W = 420, H = 420;
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz) { ShowWatermark = false };
        var raw = PlotRenderer.BuildTransforms(plot, (W, H));

        var rim = new Complex(0.95, 0.0);
        var correct = HarmonicaPanelRenderer.GammaToCanvas(rim, (W, H));
        var naive   = raw.PrimaryToCanvas(rim.Real, rim.Imaginary);

        double offsetPx = Math.Abs(naive.X - correct.X);
        output.WriteLine($"at |Γ| = 0.95 on a {W}px panel the raw transform is {offsetPx:F1} px away " +
                         $"from where the marker is actually drawn");

        // Bigger than a grab radius would forgive: the naive inverse would miss the marker entirely.
        Assert.True(offsetPx > HarmonicaHitTest.GrabRadiusDevicePixels * 0.8,
            $"the two transforms differ by only {offsetPx:F1} px at the rim — if that is no longer " +
            "true the fixture has stopped demonstrating why R-h6-1 exists");

        // And the pair this phase added agrees with the drawn position to the pixel.
        var back = HarmonicaPanelRenderer.CanvasToGamma(correct, (W, H));
        Assert.Equal(rim.Real, back.Real, precision: 4);
    }

    // ══ R-h6-2 — the grab radius is DEVICE PIXELS at every panel size ═════════

    [Fact]
    public void Tier2_TheGrabRadiusIsTheSameNumberOfPixelsOnA300pxPanelAndA900pxOne()
    {
        // Stripped to S1/L1: at 300px the default-set S2/L2/L3 markers can land within the 14-device-
        // pixel grab radius of a probe placed near L1's own default position, and R-h9r2-5's z-order
        // rank (not proximity) decides ties — the wrong marker can then win at d=0, before this test's
        // own walk-outward loop has anything to do with it.
        var vm = StripToS1L1(new HarmonicaViewModel());
        var marker = vm.Markers[1];
        marker.Gamma = new Complex(0.35, 0.20);

        double MeasureGrabRadiusPixels(double w, double h)
        {
            var (mx, my) = OnPowerPanel(vm, marker.Gamma, w, h);

            // Walk outward one pixel at a time until the hit test stops grabbing. The answer is in
            // CANVAS pixels by construction, which is the unit R-h6-2 is about.
            for (int d = 0; d < 200; d++)
            {
                var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, mx + d, my, w, h);
                if (!grab.IsGrab || !ReferenceEquals(grab.Marker, marker)) return d;
            }
            return double.NaN;
        }

        double small = MeasureGrabRadiusPixels(300, 300);
        double large = MeasureGrabRadiusPixels(900, 900);
        output.WriteLine($"grab radius: {small} px on a 300 px canvas, {large} px on a 900 px canvas " +
                         $"(declared {HarmonicaHitTest.GrabRadiusDevicePixels} device px)");

        Assert.Equal(small, large);
        Assert.InRange(small, HarmonicaHitTest.GrabRadiusDevicePixels - 1,
                              HarmonicaHitTest.GrabRadiusDevicePixels + 1);
    }

    [Fact]
    public void Tier2_TheRadiusIsDIVIDEDByRenderScaling_SoItStaysConstantInDEVICEPixels()
    {
        var vm = StripToS1L1(new HarmonicaViewModel());
        var marker = vm.Markers[1];
        marker.Gamma = new Complex(0.35, 0.20);

        const double W = 600, H = 600;
        var (mx, my) = OnPowerPanel(vm, marker.Gamma, W, H);

        // At 2× scaling one DIP is two device pixels, so a 14-device-pixel radius is 7 DIPs — the
        // pointer arrives in DIPs, so the test probes in DIPs.
        Assert.True(HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, mx + 6, my, W, H, renderScaling: 2.0).IsGrab);
        Assert.False(HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, mx + 10, my, W, H, renderScaling: 2.0).IsGrab);
        Assert.True(HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, mx + 10, my, W, H, renderScaling: 1.0).IsGrab);
    }

    [Fact]
    public void APointerDown200pxFromAnyMarker_GrabsNothing()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;

        var g = new HarmonicaGesture(vm);

        // 200 px from every marker AND every glyph, checked rather than assumed.
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var panelSize = (p.W * W, p.H * H);
        var probe = new SKPoint((float)(p.X * W + 10), (float)(p.Y * H + 10));

        foreach (var m in vm.Markers)
        foreach (var at in new[]
        {
            HarmonicaPanelRenderer.GammaToCanvas(m.Gamma, panelSize),
            HarmonicaPanelRenderer.GammaToCanvas(IntrinsicGlyphScale.DisplayPosition(m.GammaIntrinsic), panelSize),
        })
        {
            double d = Math.Sqrt(Math.Pow(at.X - (probe.X - p.X * W), 2) +
                                 Math.Pow(at.Y - (probe.Y - p.Y * H), 2));
            Assert.True(d > 200, $"the fixture's probe is only {d:F0} px from {m.Name} — move it");
        }

        Assert.False(g.PointerDown(probe.X, probe.Y, W, H));
        Assert.False(g.IsDragging);
        Assert.Equal(HarmonicaGrabKind.None, g.Grab.Kind);
    }

    [Fact]
    public void TheHitTestPrefersTheMARKER_BecauseTheMarkerIsDrawnOnTop()
    {
        // R-h45-4's z-order, enforced in the hit test: a hit test that disagreed with the z-order
        // would grab the thing the user cannot see.
        var vm = StripToS1L1(new HarmonicaViewModel());
        var m = vm.Markers[1];
        m.Gamma          = new Complex(0.20, 0.10);
        m.GammaIntrinsic = new Complex(0.20, 0.10);          // exactly underneath

        const double W = 900, H = 700;
        var (x, y) = OnPowerPanel(vm, m.Gamma, W, H);

        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, x, y, W, H);
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, grab.Kind);
        Assert.Same(m, grab.Marker);

        // Move the marker away and the glyph beneath becomes reachable.
        m.Gamma = new Complex(-0.6, -0.5);
        var glyph = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, x, y, W, H);
        Assert.Equal(HarmonicaGrabKind.IntrinsicGlyph, glyph.Kind);
        Assert.Same(m, glyph.Marker);
    }

    // ══ THE M1 GATE — down, 40 moves, up ═════════════════════════════════════

    [Fact]
    public async Task ASyntheticDrag_MovesTheMarkerToTheReleasePoint_AndSnapsWithONEFullFrame()
    {
        var clock = new Clock();
        // Stripped to S1/L1: the default S2/L2/L3 markers can otherwise collide with a probe placed
        // near L1's own position (R-h9r2-5's z-order rank, not proximity, decides who wins a grab
        // radius overlap) — see Tier2_TheGrabRadiusIsTheSameNumberOfPixelsOnA300pxPanelAndA900pxOne.
        var vm = StripToS1L1(new HarmonicaViewModel { Scheduler = new FrameScheduler(clock.Read, 33.3) });
        vm.Pool.Completed += (f, seq) => { vm.PublishFrame(f); vm.OnPoolSettled(seq); };

        const double W = 1200, H = 800;

        // The document solves a frame when it opens, and its measured cost is what puts the ladder
        // where a drag actually finds it. Doing that here rather than starting from a pristine
        // scheduler is the realistic case AND the deterministic one.
        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();
        var ladderAtStart = vm.Scheduler.Quality;
        output.WriteLine($"after the document's own first frame the ladder is at {ladderAtStart} " +
                         $"(that frame cost {vm.Scheduler.LastTiming.TotalMs:F0} ms)");
        Assert.NotEqual(FrameQuality.Full, ladderAtStart);

        // Now count only what the GESTURE publishes.
        var published = new List<FrameQuality>();
        vm.Pool.Completed += (f, _) => { lock (published) published.Add(f.Quality); };

        var marker = vm.Markers[1];
        var (sx, sy) = OnPowerPanel(vm, marker.Gamma, W, H);

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(sx, sy, W, H));
        Assert.Same(marker, g.Grab.Marker);

        int startedBeforeDrag = vm.Pool.StartedCount;

        // 40 moves along an arc, fired back-to-back with NO drain between them — the shape of a real
        // fast drag. Before brief-harmonicarf-r5 §3, every one of these reached SolvePool.Submit and
        // most were cancelled before finishing (latest-wins/D3); now conflate-and-pace holds at most
        // ONE mid-drag solve in flight at a time, so this loop must reach the pool far fewer than 40
        // times.
        var release = new Complex(0.55, -0.30);
        for (int i = 1; i <= 40; i++)
        {
            var target = marker.Gamma + (release - marker.Gamma) * (i / 40.0);
            var (mx, my) = OnPowerPanel(vm, target, W, H);
            g.PointerMoved(mx, my, W, H);
        }
        await vm.Pool.DrainAsync();

        var (ux, uy) = OnPowerPanel(vm, release, W, H);
        g.PointerUp(ux, uy, W, H);
        await vm.Pool.DrainAsync();

        // The marker landed on the RELEASE point, to within the pixel quantisation of the gesture.
        output.WriteLine($"released at Γ = {release}, marker at Γ = {marker.Gamma}");
        Assert.True((marker.Gamma - release).Magnitude < 0.01,
            $"the marker ended at {marker.Gamma}, not at the release point {release}");
        Assert.False(g.IsDragging);

        // Exactly ONE published frame at Full quality — the snap.
        List<FrameQuality> snapshot;
        lock (published) snapshot = [.. published];
        output.WriteLine($"published during the gesture: {string.Join(", ", snapshot)}");
        Assert.Equal(1, snapshot.Count(q => q == FrameQuality.Full));
        Assert.Equal(FrameQuality.Full, snapshot[^1]);

        // brief-harmonicarf-r5 §3 — conflate-and-pace collapsed the 40 moves at the SUBMISSION site
        // rather than by cancelling jobs after starting them: far fewer than 40 solves ever started
        // for the whole burst (the release's own submission is included in this count, so a bound of
        // half the move count is comfortably loose).
        int startedDuringDrag = vm.Pool.StartedCount - startedBeforeDrag;
        output.WriteLine($"pool: {startedDuringDrag} solves started across the whole gesture " +
                         $"(of 40 moves + 1 release), {vm.Pool.SupersededCount} superseded overall");
        Assert.True(startedDuringDrag < 20,
            $"{startedDuringDrag} solves started for a 40-move drag — conflate-and-pace is not " +
            "engaging (every move is reaching the pool, which is the starvation §3 exists to fix)");
    }

    [Fact]
    public async Task TheSameSequenceWithAnOverBudgetFrameTiming_WalksTheLadderDown_AndStillSnaps()
    {
        var clock = new Clock();
        // Stripped to S1/L1 — see Tier2_TheGrabRadiusIsTheSameNumberOfPixelsOnA300pxPanelAndA900pxOne
        // for why the default S2/L2/L3 markers cannot safely be left in for a fixture that grabs by
        // canvas position near L1's own default Γ.
        //
        // Dragged marker is S1 (Markers[0]), deliberately NOT L1: R-h9r2-3's default GridSide/
        // GridHarmonic is Load/1, i.e. L1 IS the swept band by default, and releasing a drag on the
        // swept band correctly SKIPS the grid re-solve (carrying the pre-drag grid forward instead of
        // paying for a re-solve that would publish the identical result) — which would leave
        // GridPoints empty here for a reason this test isn't about. S1 is never the swept band
        // (GridSide is a Load/Source discriminator), so it still exercises "release re-solves the
        // grid at Full quality" cleanly, which is this test's own actual subject.
        var vm = StripToS1L1(new HarmonicaViewModel { Scheduler = new FrameScheduler(clock.Read, 33.3) });
        const double W = 1200, H = 800;

        var marker = vm.Markers[0];
        var g = new HarmonicaGesture(vm);
        var (sx, sy) = OnPowerPanel(vm, marker.Gamma, W, H);
        Assert.True(g.PointerDown(sx, sy, W, H));

        var plans = new List<FrameQuality>();
        for (int i = 1; i <= 4; i++)
        {
            var (mx, my) = OnPowerPanel(vm, new Complex(0.1 * i, 0.05 * i), W, H);
            g.PointerMoved(mx, my, W, H);
            await vm.Pool.DrainAsync();
            plans.Add(vm.LastPlan!.Value.Quality);

            // A hopeless frame, recorded explicitly so the ladder's walk is deterministic rather than
            // dependent on this machine's speed.
            vm.RecordFrameCost(new FrameTiming(4, 900, 6, 90, 10));
            clock.Advance(50);
        }

        output.WriteLine($"4 over-budget drag frames → {string.Join(" → ", plans)}");
        Assert.Equal(FrameQuality.Full,           plans[0]);
        Assert.Equal(FrameQuality.CoarseRaster,   plans[1]);
        Assert.Equal(FrameQuality.CoarseGrid,     plans[2]);
        Assert.Equal(FrameQuality.FrozenContours, plans[3]);

        // Publishing is the VIEW's job and this test has deliberately not been playing that part —
        // an automatic RecordFrameCost per published frame would have raced the explicit over-budget
        // timings above and made the ladder's walk depend on this machine's speed. It plays the part
        // now, for the snap.
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);

        var (ux, uy) = OnPowerPanel(vm, new Complex(0.4, 0.2), W, H);
        g.PointerUp(ux, uy, W, H);
        await vm.Pool.DrainAsync();

        // Freeze-and-snap: the release is at FULL, whatever the ladder said mid-drag.
        Assert.Equal(FrameQuality.Full, vm.LastPlan!.Value.Quality);
        Assert.NotEmpty(vm.Frame.SmithPower.GridPoints);
        Assert.True((vm.Markers[0].Gamma - new Complex(0.4, 0.2)).Magnitude < 0.01);
    }

    // ══ R-h6-3 — the drag writes nothing but the marker ══════════════════════

    [Fact]
    public async Task Tier3_ADragWritesNothingToTheModelBeyondTheMarker()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1000, H = 700;

        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();

        int rebuildsBefore = vm.Pool.Workers[0].ContextRebuildCount;
        string structureBefore = vm.Model.StructuralKey;
        var untouched = vm.Markers[0].Gamma;

        var marker = vm.Markers[1];
        var g = new HarmonicaGesture(vm);
        var (sx, sy) = OnPowerPanel(vm, marker.Gamma, W, H);
        g.PointerDown(sx, sy, W, H);
        for (int i = 1; i <= 20; i++)
        {
            var (mx, my) = OnPowerPanel(vm, new Complex(0.02 * i, 0.01 * i), W, H);
            g.PointerMoved(mx, my, W, H);
        }
        await vm.Pool.DrainAsync();
        var (ux, uy) = OnPowerPanel(vm, new Complex(0.4, 0.2), W, H);
        g.PointerUp(ux, uy, W, H);
        await vm.Pool.DrainAsync();

        Assert.Equal(structureBefore, vm.Model.StructuralKey);
        Assert.Equal(rebuildsBefore,  vm.Pool.Workers[0].ContextRebuildCount);
        Assert.Equal(untouched,       vm.Markers[0].Gamma);           // the OTHER marker is untouched
        Assert.Equal(3, vm.Markers.Count);                            // R8B §3's default set (L1/L2/L3) — no marker was added or removed
    }

    /// <summary>R-h9b-14 strips a fresh document's marker set down to just S1/L1, for the many drag
    /// tests here that want exactly two markers and do not care which bands they are — so the added
    /// defaults cannot collide with a test-chosen probe position. R8B §3 changed the fresh-document
    /// default to L1/L2/L3 with no source marker at all, so S1 is added back explicitly (25 Ω, the
    /// pre-R8B default this file's fixtures were written against) rather than merely un-stripped.</summary>
    private static HarmonicaViewModel StripToS1L1(HarmonicaViewModel vm)
    {
        vm.SetMarkerImpedance(vm.AddMarkerBand(TerminationSideKind.Source, 1), new Complex(25, 0));
        vm.RemoveMarkerBand(TerminationSideKind.Load, 2);
        vm.RemoveMarkerBand(TerminationSideKind.Load, 3);
        return vm;
    }

    // ══ R-h6-5 — StatusMessage reaches the strip ════════════════════════════

    [Fact]
    public void Tier5_WhenTierAAloneMissesTheTarget_TierAHealthyLatchesFalse()
    {
        var clock = new Clock();
        var vm = new HarmonicaViewModel { Scheduler = new FrameScheduler(clock.Read, 33.3) };

        Assert.Null(vm.StatusMessage);

        // Tier A alone over budget — the one case no amount of tier-B degradation can fix.
        vm.RequestScheduledFrame(dragging: true);
        vm.RecordFrameCost(new FrameTiming(TierAMs: 120, GridSolveMs: 0, FitMs: 0, RasterMs: 0, RenderMs: 4));

        // The "running the coarsest contour grid" wording is retired (owner: harmonicaRF no longer
        // simulates a coarse grid to keep up, so the message no longer describes reality) — the
        // strip shows nothing for this case now, and TierAHealthy is the signal a caller reads.
        Assert.False(vm.Scheduler.TierAHealthy);
        Assert.Null(vm.StatusMessage);

        // And it is the SCHEDULER's message (or lack of one), not a copy this view model invented.
        Assert.Equal(vm.Scheduler.StatusMessage, vm.StatusMessage);
    }

    [Fact]
    public void Tier5_TheViewActuallyDisplaysIt_NotMerelyComputesIt()
    {
        // A message nothing displays is a message that does not exist. Ui.Tests has no headless
        // Avalonia host, so this asserts on the view's own source — the same route
        // HarmonicaPanelTests uses for "the renderer has no fill path".
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        // R9A §11 — the assignment now routes through MessageLineText, which still reads
        // h.StatusMessage as its own middle argument (gated on whether a gesture is live).
        Assert.Contains("MessageText.Text = MessageLineText(", src, StringComparison.Ordinal);
        Assert.Contains("h.StatusMessage,", src, StringComparison.Ordinal);
    }

    // ── §1/§2/§3 (R1C) — the toolbar is gone; the bottom message/progress line replaces it ────

    [Fact]
    public void Tier5b_TheToolbarIsGone_AndTheBottomMessageLineTakesItsPlace()
    {
        string axaml = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml");

        // The toolbar's own named controls must not exist anywhere in this document any more —
        // every one of them was either dropped for cause (§1's table) or moved to a menu command.
        foreach (var gone in new[] { "SolveButton", "PlaneToggle", "XUnitButton", "CursorModeButton",
                                     "EditDisplayToggle", "StatusText" })
            Assert.DoesNotContain($"x:Name=\"{gone}\"", axaml, StringComparison.Ordinal);

        // Its replacement: one line, selectable, at the bottom, with an inline progress bar per §3.
        Assert.Contains("x:Name=\"MessageBar\"", axaml, StringComparison.Ordinal);
        Assert.Contains("DockPanel.Dock=\"Bottom\"", axaml, StringComparison.Ordinal);
        Assert.Contains("<SelectableTextBlock x:Name=\"MessageText\"", axaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SolveProgressBar\" Width=\"75\"", axaml, StringComparison.Ordinal);

        // The code-behind's handlers for the removed toolbar buttons are gone with them.
        string cs = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        foreach (var gone in new[] { "OnSolveClick", "OnCycleXUnitClick", "OnToggleCursorSnap",
                                     "OnToggleEditDisplay" })
            Assert.DoesNotContain(gone, cs, StringComparison.Ordinal);

        // §2/§3 — the message and progress-bar roles are actually consumed, not merely projected.
        Assert.Contains("h.RenderTheme.Messages", cs, StringComparison.Ordinal);
        Assert.Contains("h.RenderTheme.ProgressBar", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void Tier5c_TheirCapabilitiesSurviveOnAMenu()
    {
        // §1's own guardrail: "do not silently drop a capability with the button." Solve (full grid)
        // and cursor snap-to-compression had no other affordance, so each needs a menu command now.
        string vmSrc = ReadSource("src", "Ui", "Harmonica", "HarmonicaMenuViewModel.cs");
        Assert.Contains("SolveNow", vmSrc, StringComparison.Ordinal);
        Assert.Contains("ToggleCursorSnap", vmSrc, StringComparison.Ordinal);

        string menuAxaml = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml");
        Assert.Contains("SolveNowCommand", menuAxaml, StringComparison.Ordinal);
        Assert.Contains("ToggleCursorSnapCommand", menuAxaml, StringComparison.Ordinal);
    }

    // ══ §5.4 (brief-harmonicarf-r4) — a mid-drag marker frame that hasn't moved costs no solve ══

    [Fact]
    public async Task MidDragMarkerFrame_WithinToleranceOfLastSubmitted_IsSkipped_GatedOnACounterNotAStopwatch()
    {
        // Same shape as HarmonicaGridPointDragTests' own
        // MidDragGridPointFrame_CostsZeroHbSolves_GatedOnACounterNotAStopwatch — a counter
        // (Pool.StartedCount / NoOpDragFrameSkipCount), never a stopwatch, proves the skip.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });

        var marker = vm.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);

        // One genuine move establishes a "last submitted" baseline.
        vm.SetMarkerGamma(marker, new Complex(0.10, 0.05));
        long seq1 = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
        Assert.NotEqual(-1, seq1);
        await vm.Pool.DrainAsync();

        int startedAfterFirst = vm.Pool.StartedCount;
        int skippedBefore     = vm.NoOpDragFrameSkipCount;

        // Sub-tolerance jitter — five moves, each landing within DragNoOpGammaTolerance of the last
        // SUBMITTED Γ (not of each other, so this also proves the comparison doesn't drift frame to
        // frame). None may reach the solve pool.
        for (int i = 1; i <= 5; i++)
        {
            vm.SetMarkerGamma(marker, new Complex(0.10 + 1e-6 * i, 0.05));
            long seq = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
            Assert.Equal(-1, seq);
        }
        Assert.Equal(startedAfterFirst, vm.Pool.StartedCount);
        Assert.Equal(skippedBefore + 5, vm.NoOpDragFrameSkipCount);

        // A real move — well past tolerance — DOES reach the pool. StartedCount increments when the
        // pool actually STARTS the job (async), not synchronously on Submit, so this needs a drain
        // before it can be read.
        vm.SetMarkerGamma(marker, new Complex(0.15, 0.05));
        long seq2 = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
        Assert.NotEqual(-1, seq2);
        await vm.Pool.DrainAsync();
        Assert.True(vm.Pool.StartedCount > startedAfterFirst,
            "a move well beyond the no-op tolerance must still reach the solve pool");
    }

    [Fact]
    public async Task MarkerReleaseAlwaysSolves_EvenWithinTheNoOpTolerance()
    {
        // §5.4's own carve-out: mid-drag is free, release is real — matching DragGridPoint's shape.
        // A release that lands within tolerance of the last mid-drag frame must still submit a real,
        // full-quality solve; skipping it would leave the document showing a degraded drag-quality
        // frame as its final, at-rest state.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });

        // S1 is never the swept band (default GridSide/GridHarmonic is Load/1) — see the sibling drag
        // test's own comment for why that matters to which code path a release takes. R8B §3 — a
        // fresh document has no source marker at all any more, so S1 is added explicitly.
        var marker = vm.AddMarkerBand(TerminationSideKind.Source, 1);

        vm.SetMarkerGamma(marker, new Complex(0.20, -0.10));
        long seq1 = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
        Assert.NotEqual(-1, seq1);
        await vm.Pool.DrainAsync();
        int startedBeforeRelease = vm.Pool.StartedCount;

        // Release at the SAME Γ (well within tolerance of the last mid-drag frame). StartedCount only
        // increments when the pool actually STARTS the job (async), so this needs a drain first.
        vm.SetMarkerGamma(marker, new Complex(0.20, -0.10));
        long seq2 = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: false);
        Assert.NotEqual(-1, seq2);
        await vm.Pool.DrainAsync();
        Assert.True(vm.Pool.StartedCount > startedBeforeRelease,
            "release must always submit a real solve, even when Γ has not moved since the last mid-drag frame");
    }

    // ══ brief-harmonicarf-r5 §3 — conflate-and-pace: latest-wins starvation ══════════════════

    [Fact]
    public async Task ConflateAndPace_AMoveThatArrivesWhileASolveIsInFlight_DoesNotReachThePool()
    {
        // §3.1's own mechanism, pinned directly rather than through a real pointer/timing race: a
        // mid-drag move that arrives before the PREVIOUS mid-drag solve has finished must conflate
        // (return -1, exactly like §5.4's no-op sentinel) instead of calling SolvePool.Submit — which
        // is what used to cancel that previous solve before it could publish (D3's own
        // cancel-before-submit). Deterministic because nothing here is drained until AFTER the second
        // call, so the first solve genuinely cannot have settled yet.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });

        var marker = vm.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);

        vm.SetMarkerGamma(marker, new Complex(0.10, 0.05));
        long seq1 = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
        Assert.NotEqual(-1, seq1);

        // A second, genuinely different move — arrives with the first solve's own sequence not yet
        // the pool's LastCompletedSequence (nothing has been drained), so it must conflate.
        vm.SetMarkerGamma(marker, new Complex(0.20, 0.05));
        long seq2 = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
        Assert.Equal(-1, seq2);

        // The glyph itself is UNPACED (R-h6-3) — it tracks the second move even though nothing new was
        // submitted for it yet.
        Assert.True((marker.Gamma - new Complex(0.20, 0.05)).Magnitude < 1e-6);

        await vm.Pool.DrainAsync();
        // Exactly the ONE mid-drag solve started — the conflated second move never reached the pool
        // at all (this test wires no Pool.Completed, so nothing resubmits it).
        Assert.Equal(1, vm.Pool.StartedCount);
    }

    [Fact]
    public async Task ConflateAndPace_OnceTheInFlightSolveSettles_ThePendingMoveResubmitsAutomatically()
    {
        // §3.3's "on completion, if the pending slot holds a newer Γ, submit that" — wired exactly as
        // the live view wires it (PublishFrame then OnPoolSettled, right after a pool completion).
        var vm = new HarmonicaViewModel();
        vm.Pool.Completed += (f, seq) => { vm.PublishFrame(f); vm.OnPoolSettled(seq); };
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });

        var marker = vm.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);

        vm.SetMarkerGamma(marker, new Complex(0.10, 0.05));
        Assert.NotEqual(-1, vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true));

        vm.SetMarkerGamma(marker, new Complex(0.25, 0.05));
        Assert.Equal(-1, vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true));

        int startedBeforeDrain = vm.Pool.StartedCount;

        // Draining lets the in-flight solve finish, which fires Pool.Completed → PublishFrame →
        // OnPoolSettled — and THAT is what submits the conflated move, with no further pointer event.
        await vm.Pool.DrainAsync();

        Assert.True(vm.Pool.StartedCount > startedBeforeDrain,
            "the conflated move never got resubmitted once the in-flight solve settled");
        Assert.True((marker.Gamma - new Complex(0.25, 0.05)).Magnitude < 1e-6,
            "the marker itself should still be at the latest conflated position (R-h6-3)");
    }

    [Fact]
    public async Task ConflateAndPace_ABurstOf30MovesWithNoDrainBetweenThem_StartsFarFewerThan30Solves()
    {
        // §3.4's own gate: N simulated moves during a drag produce at most the paced number of
        // submissions, the marker's own Γ still tracks the last move, and release still submits a
        // real full-quality solve.
        var vm = new HarmonicaViewModel();
        vm.Pool.Completed += (f, seq) => { vm.PublishFrame(f); vm.OnPoolSettled(seq); };
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });

        var marker = vm.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        int startedBefore = vm.Pool.StartedCount;

        for (int i = 1; i <= 30; i++)
        {
            vm.SetMarkerGamma(marker, new Complex(0.01 * i, 0.005 * i));
            vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: true);
        }
        await vm.Pool.DrainAsync();

        int startedDuringBurst = vm.Pool.StartedCount - startedBefore;
        output.WriteLine($"{startedDuringBurst} of 30 moves reached the solve pool " +
                         $"({vm.Pool.SupersededCount} superseded overall)");
        Assert.True(startedDuringBurst < 30,
            "every move reached the pool — conflate-and-pace is not engaging");

        // The marker's own Γ tracks the LAST move, unpaced.
        Assert.True((marker.Gamma - new Complex(0.30, 0.15)).Magnitude < 1e-6);

        // Release still submits a real, full-quality solve.
        vm.SetMarkerGamma(marker, new Complex(0.30, 0.15));
        long releaseSeq = vm.RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging: false);
        Assert.NotEqual(-1, releaseSeq);
        await vm.Pool.DrainAsync();
        Assert.Equal(FrameQuality.Full, vm.LastPlan!.Value.Quality);
    }

    private static string ReadSource(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine([dir!.FullName, .. parts]);
        Assert.True(System.IO.File.Exists(path), $"source not found at {path}");
        return System.IO.File.ReadAllText(path);
    }
}
