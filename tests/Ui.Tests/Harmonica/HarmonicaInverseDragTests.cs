// ================================================================
//  HarmonicaInverseDragTests.cs  —  M2/M3 through the DOCUMENT, brief-harmonicarf-h6;
//  rewritten for R8C §5, which retires the wiring this file used to gate.
//
//  InverseSolveTests / ReachabilityTests still gate the (retired, kept-in-tree) inverse solve's own
//  maths headlessly. IntrinsicAbcdTests gates the CLOSED FORM's own maths headlessly. This file gates
//  the WIRING: that a pointer on a glyph reaches IntrinsicAbcd, that the answer lands in the
//  terminations on the UI thread with no solve pool involvement for the position itself, that a pole
//  target moves nothing and says so, and that the glyph is not grabbable at all when
//  CircuitModel.IntrinsicDragAllowed is false.
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

    /// <summary>Every call site in this file drives an INTRINSIC glyph drag — the glyph is the one
    /// thing still on IntrinsicGlyphScale's compressed radial map after R8B §2 re-pointed the
    /// extrinsic marker onto the plain chart transform.</summary>
    private static (double X, double Y) OnPowerPanel(HarmonicaViewModel vm, Complex gamma)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var local = HarmonicaPanelRenderer.GammaToCanvas(IntrinsicGlyphScale.DisplayPosition(gamma), (p.W * W, p.H * H));
        return (p.X * W + local.X, p.Y * H + local.Y);
    }

    /// <summary>
    /// A document with one solved frame in it, and the ladder already at freeze-and-snap.
    ///
    /// <para>The ladder is pushed there by recording hopeless frames DIRECTLY on the scheduler rather
    /// than by solving several — the point of these tests is the intrinsic-drag wiring, and on this
    /// model a contour-bearing frame is ~600 ms, so paying for four of them per test would buy
    /// nothing but wall-clock.</para>
    /// </summary>
    /// <summary>
    /// The default document's DUT with a real extrinsic package on the LOAD side only.
    ///
    /// <para><b>The package is not decoration, it is what makes an intrinsic drag testable at all.</b>
    /// §4.5 consequence 1: the glyph coincides with its marker when charge is off AND there is no
    /// extrinsic network — and the shipped default document is exactly that (a chargeless-package SDD,
    /// no package), so its glyph sits ~0.003 Γ from its marker and the z-ordered hit test correctly
    /// grabs the marker on top. A series Rd/Ld separates the two planes.
    ///
    /// <para><b>R8C §5.2 — Rd/Ld, never Rs/Ls.</b> The fixture this file used before R8C used Rs/Ls to
    /// get the same pixel separation; Rs/Ls is exactly <c>LumpedPackage.CouplesInputAndOutput</c>,
    /// which now makes <c>IntrinsicDragAllowed</c> false and the glyph ungrabbable. Rd/Ld is a series
    /// lead on the LOAD side alone — it moves <c>Z_L,intr</c> away from the marker just as well and
    /// leaves the predicate true.</para>
    ///
    /// <para><b>R-h9r2-19 note:</b> this fixture's own DUT equation GAIN-EXPANDS by ~3 dB before it
    /// ever compresses (rises smoothly from Pin −10 to a peak of ~14.8 dB around +22 dBm, then rolls
    /// over) — its own physics, unrelated to the package above. <c>PinMaxDbm</c> is capped well below
    /// the peak so a sweep never reaches compression and falls back to its last solved, well-behaved
    /// point.</para>
    /// </summary>
    private static CircuitModel ModelWithAPackage()
    {
        var m = HarmonicaViewModel.DefaultModel();
        var model = m with
        {
            Embedding = new EmbeddingStack
            {
                Package = new LumpedPackage { Rd = 20.0, Ld = 2e-9 },
            },
            Settings = m.Settings with { PinMaxDbm = 15.0 },
        };
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out string reason), reason);
        return model;
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
        var a = HarmonicaPanelRenderer.GammaToCanvas(m.Gamma, (p.W * W, p.H * H));
        var b = HarmonicaPanelRenderer.GammaToCanvas(IntrinsicGlyphScale.DisplayPosition(m.GammaIntrinsic), (p.W * W, p.H * H));
        return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }

    // ══ R8C §5.3 — the closed-form glyph drag reaches IntrinsicAbcd and the answer lands ══════════

    [Fact]
    public async Task DraggingAnIntrinsicGlyph_MovesTheEXTRINSICTerminations_AndLandsTheGlyphOnTarget()
    {
        var vm = await DocumentAtTierAOnly();
        Assert.True(CircuitModel.IntrinsicDragAllowed(vm.Model, out string reason), reason);

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

        // A short drag, then release. R8C §5.1 — IsInverseDragging is retired FROM the drag path
        // (the field it reads is never assigned any more); the closed form needs no "am I dragging"
        // state of its own, since every frame is independently computed.
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

        // The EXTRINSIC termination is what moved — that is the whole point of the ABCD back-calc.
        Assert.True((marker.Gamma - extrinsicBefore).Magnitude > 1e-3,
            "the extrinsic termination did not move, so nothing was computed for");

        // …and the TerminationSet the engine reads moved with it, not just the marker.
        var z = vm.Terminations.Z(TerminationSide.Load, 1);
        var gammaOfZ = HarmonicaDataSet.GammaOf(z, vm.Model.Settings.Z0);
        Assert.Equal(marker.Gamma.Real,      gammaOfZ.Real,      precision: 9);
        Assert.Equal(marker.Gamma.Imaginary, gammaOfZ.Imaginary, precision: 9);

        // The GLYPH landed on the target. This is read off the published frame, which came from an
        // ordinary forward solve of the ABCD-computed termination — so it is a round trip through the
        // real solver, not merely IntrinsicAbcd re-checking its own arithmetic. The residual is the
        // ideal-bias-tee approximation IntrinsicAbcd makes (the chain does not model the document's
        // own non-ideal BiasChokeHenries/DcBlockFarads — see IntrinsicAbcd's own header), measured at
        // ~0.2% relative for this document's choke/block values — well under the tolerance below.
        double err = (marker.GammaIntrinsic - target).Magnitude;
        output.WriteLine($"glyph landed {err:E3} from the target");
        Assert.True(err < 5e-3, $"the glyph landed {err:E3} from where it was dragged");
    }

    /// <summary>The load-side chain's own analytic pole: <c>Z_intr = A/C</c>, where <c>-C·Z_intr + A
    /// = 0</c> exactly. Only a chain with a genuine SHUNT element (C ≠ 0) has one — the Rd/Ld-only
    /// fixture above is affine (C = 0, no pole at all), so this uses a dedicated model with a bare
    /// shunt Cds and no series lead, chosen so the pole is exactly representable in double precision
    /// (a pure-imaginary Y divides itself to exactly 1.0, so the denominator is exactly
    /// <c>Complex.Zero</c>, not merely close to it).</summary>
    private static CircuitModel ModelWithACleanPole()
    {
        var m = HarmonicaViewModel.DefaultModel();
        var model = m with
        {
            Dut = m.Dut with
            {
                Capacitances = new DutCapacitances
                {
                    Cds = new DutCapacitance { Farads = 1e-12 },
                },
            },
            Settings = m.Settings with { PinMaxDbm = 15.0 },
        };
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out string reason), reason);
        return model;
    }

    [Fact]
    public async Task ThePolesTarget_MovesNothing_AndSaysSo()
    {
        var vm = new HarmonicaViewModel(ModelWithACleanPole());
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);
        for (int i = 0; i < 4; i++)
            vm.Scheduler.RecordFrame(vm.Scheduler.NextPlan(dragging: true),
                                     new FrameTiming(4, 900, 6, 90, 10));
        vm.RequestScheduledFrame(dragging: true);
        await vm.Pool.DrainAsync();

        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 }); // L1
        var extrinsicBefore = marker.Gamma;
        var termsBefore     = vm.Terminations.Z(TerminationSide.Load, 1);
        var glyphBefore     = marker.GammaIntrinsic;

        double sep = GlyphSeparationPixels(vm, marker);
        output.WriteLine($"glyph is {sep:F0} px from its marker (grab radius " +
                         $"{HarmonicaHitTest.GrabRadiusDevicePixels})");
        Assert.True(sep > HarmonicaHitTest.GrabRadiusDevicePixels + 4,
            $"the glyph is only {sep:F0} px from its marker — this fixture cannot exercise a drag");

        var g = new HarmonicaGesture(vm);
        var (gx, gy) = OnPowerPanel(vm, glyphBefore);
        Assert.True(g.PointerDown(gx, gy, W, H));
        Assert.Equal(HarmonicaGrabKind.IntrinsicGlyph, g.Grab.Kind);

        // Z_intr = 1/(jωCds), the chain's own pole, computed the SAME way IntrinsicAbcd's Chain
        // builds it (a bare shunt Cds, Rd = Ld = Cpd = 0) — not re-derived by a different route.
        double omega = 2.0 * Math.PI * vm.Model.Settings.FrequencyHz;
        var zPole = Complex.One / new Complex(0, omega * vm.Model.Dut.Capacitances.Cds.Farads);
        var gammaPole = HarmonicaDataSet.GammaOf(zPole, vm.Model.Settings.Z0);

        // A drag target essentially never lands EXACTLY on the pole in floating point (one ULP off the
        // true zero denominator still blows the quotient up to ~1e17 Ω here, measured) — which is
        // exactly why HarmonicaViewModel.PoleMagnitudeOhms is a magnitude bound, not a literal
        // double.IsFinite check. This asserts the fixture actually produces a value past that bound.
        var zExtCheck = IntrinsicAbcd.ExtrinsicFor(vm.Model, TerminationSide.Load, 1, zPole);
        output.WriteLine($"Z_pole = {zPole}, IntrinsicAbcd.ExtrinsicFor there = {zExtCheck}");
        Assert.True(zExtCheck.Magnitude > HarmonicaViewModel.PoleMagnitudeOhms,
            "the fixture's own pole did not actually land near IntrinsicAbcd's pole — the test fixture " +
            "needs revisiting, not the production refusal path");

        var (tx, ty) = OnPowerPanel(vm, IntrinsicGlyphScale.DisplayPosition(gammaPole));
        g.PointerMoved(tx, ty, W, H);
        await vm.Pool.DrainAsync();

        output.WriteLine($"status: {vm.StatusMessage}");
        Assert.NotNull(vm.InverseMessage);
        Assert.Contains("not reachable", vm.InverseMessage!, StringComparison.Ordinal);

        // EXACTLY where they were — R-h6-9's precedent, still enforced.
        Assert.Equal(extrinsicBefore.Real,      marker.Gamma.Real);
        Assert.Equal(extrinsicBefore.Imaginary, marker.Gamma.Imaginary);
        Assert.Equal(termsBefore, vm.Terminations.Z(TerminationSide.Load, 1));

        g.Cancel();
    }

    // ══ R8C §5.2 — the glyph is not grabbable at all when dragging is disallowed ═══════════════════

    [Fact]
    public async Task IntrinsicGlyph_IsNotGrabbable_WhenNonlinearCgsDisallowsTheDrag()
    {
        var m = HarmonicaViewModel.DefaultModel();
        var model = m with
        {
            Embedding = new EmbeddingStack { Package = new LumpedPackage { Rd = 20.0, Ld = 2e-9 } },
            Dut = m.Dut with
            {
                Capacitances = new DutCapacitances
                {
                    Cgs = new DutCapacitance { Coefficients = [1e-12, 1e-14] },
                },
            },
        };
        Assert.False(CircuitModel.IntrinsicDragAllowed(model, out string reason));
        output.WriteLine($"disallowed: {reason}");

        var vm = new HarmonicaViewModel(model);
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);
        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();

        var marker = vm.Markers.Single(mk => mk is { Side: TerminationSideKind.Load, Band: 1 });
        var (gx, gy) = OnPowerPanel(vm, marker.GammaIntrinsic);

        var g = new HarmonicaGesture(vm);
        Assert.False(g.PointerDown(gx, gy, W, H));
        Assert.NotEqual(HarmonicaGrabKind.IntrinsicGlyph, g.LastGrabKind);

        // The click fell through Pass 2 — the reason is surfaced anyway, so the click is not silent.
        Assert.Equal(reason, vm.InverseMessage);
    }

    // ══ R8C §5.3 — ShowReachableRegion defaults OFF now the inverse solve no longer drives a drag ══

    [Fact]
    public void ShowReachableRegion_DefaultsFalse()
    {
        var vm = new HarmonicaViewModel();
        Assert.False(vm.ShowReachableRegion);
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
            // R8B §2 — an active marker is no longer compressed onto IntrinsicGlyphScale's annulus;
            // it draws at its TRUE position on the plain chart map, which means it can leave the
            // panel entirely once |Γ| is far enough out (§2.3: "the largest Γ a pointer can express
            // is whatever the panel extent reaches, ~1.3 at the chart margins"). 1.15 stays just
            // inside that extent, so this fixture can still exercise "on the panel, past the rim".
            var passive = new HarmonicaMarker(TerminationSideKind.Load, 1)
            { Gamma = new Complex(0.60, 0.0), GammaIntrinsic = new Complex(0.60, 0.0) };
            var active = new HarmonicaMarker(TerminationSideKind.Load, 1)
            { Gamma = new Complex(1.15, 0.0), GammaIntrinsic = new Complex(0.60, 0.0) };

            Assert.False(passive.ExtrinsicIsOutsideUnitCircle);
            Assert.True(active.ExtrinsicIsOutsideUnitCircle);

            using var bmpP = Render(new SmithPanelData { Markers = [passive] }, theme, Size);
            using var bmpA = Render(new SmithPanelData { Markers = [active]  }, theme, Size);

            var rim    = HarmonicaPanelRenderer.GammaToCanvas(Complex.One, (Size, Size));
            var centre = HarmonicaPanelRenderer.GammaToCanvas(Complex.Zero, (Size, Size));
            var at     = HarmonicaPanelRenderer.GammaToCanvas(active.Gamma, (Size, Size));

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
            double plainReach  = OutermostMarkerPixel(bmpP, HarmonicaPanelRenderer.GammaToCanvas(
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
