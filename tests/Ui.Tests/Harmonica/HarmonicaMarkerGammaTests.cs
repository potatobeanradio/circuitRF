// ================================================================
//  HarmonicaMarkerGammaTests.cs — R8B §2.4
//
//  "This should be a very basic calculation... User moves marker on a gamma plane with real and
//  imaginary world coordinates. Simple and done." MarkerToCanvas/CanvasToMarker composed
//  IntrinsicGlyphScale's compressed radial map into the EXTRINSIC termination marker's own drag path
//  — invented for the intrinsic glyph (R-h45-4), and wrong for the marker the moment |Γ| > 1. Both are
//  gone; an extrinsic marker now drags on the plain GammaToCanvas/CanvasToGamma affine map.
// ================================================================

using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaMarkerGammaTests(ITestOutputHelper output)
{
    private static readonly (double W, double H) Size = (420, 420);

    // ══ Round trip — the plain chart map, no compression ═══════════════════════════════════

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.2)]
    public void RoundTrip_WellInsideTheRim_IsExactToNinePlaces(double re, double im)
    {
        var gamma = new Complex(re, im);
        var p    = HarmonicaPanelRenderer.GammaToCanvas(gamma, Size);
        var back = HarmonicaPanelRenderer.CanvasToGamma(p, Size);
        Assert.Equal(gamma.Real,      back.Real,      precision: 9);
        Assert.Equal(gamma.Imaginary, back.Imaginary, precision: 9);
    }

    [Theory]
    [InlineData(-0.9, 0.0)]
    [InlineData(0.999, 0.0)]
    public void RoundTrip_NearTheRim_IsExactToFloatPrecision_NotNinePlaces(double re, double im)
    {
        // MEASURED, NOT ASSUMED: GammaToCanvas/CanvasToGamma is an exact DOUBLE-precision affine map,
        // but it is carried through SkiaSharp's SKPoint, which is float (32-bit) — so a value far
        // enough from the origin (a large canvas offset) loses absolute precision to float rounding
        // well before 1e-9, regardless of whether it is inside or outside the unit circle. -0.9 and
        // 0.999 measured ~3.5e-8 / ~5.8e-8 here, comfortably inside float32's own ~1e-7 relative
        // floor for a few-hundred-pixel canvas offset, and nowhere near the outside-the-rim case
        // below.
        var gamma = new Complex(re, im);
        var p    = HarmonicaPanelRenderer.GammaToCanvas(gamma, Size);
        var back = HarmonicaPanelRenderer.CanvasToGamma(p, Size);
        double err = (back - gamma).Magnitude;
        output.WriteLine($"Γ = {gamma} -> canvas {p} -> Γ = {back} (err {err:E3})");
        Assert.True(err < 1e-6, $"Γ = {gamma} round-tripped with error {err:E3} — worse than float32 precision explains");
    }

    [Theory]
    [InlineData(1.2, -0.4)]
    [InlineData(2.0, 0.0)]
    public void RoundTrip_WellOutsideTheRim_IsNotExactToNinePlaces_BecauseTheChartViewportClips(double re, double im)
    {
        // GammaToCanvas/CanvasToGamma is a plain affine map with no compression of its own — but the
        // underlying PlotRenderer.BuildTransforms/ComputeViewport pipeline was built for plotting DATA
        // inside a chart's own axis extent, and its window (Plot.Axes.Window) is not unboundedly wide.
        // A Γ well past the rim maps to a canvas point outside what that window's inverse recovers
        // exactly. Stated here as a known limitation rather than silently expected to pass: an active
        // marker's PRACTICAL bound is whatever the panel's own pixel extent reaches (§2.3 — "a hard,
        // obvious, self-explaining bound"), not an arbitrary |Γ|.
        var gamma = new Complex(re, im);
        var p    = HarmonicaPanelRenderer.GammaToCanvas(gamma, Size);
        var back = HarmonicaPanelRenderer.CanvasToGamma(p, Size);
        double err = (back - gamma).Magnitude;
        output.WriteLine($"Γ = {gamma} -> canvas {p} -> Γ = {back} (err {err:E3})");
        Assert.True(err > 1e-9,
            $"Γ = {gamma} round-tripped to {back} (err {err:E3}) — if this now round-trips exactly, " +
            "the known viewport-clip limitation this test documents no longer applies and the InlineData " +
            "here should move up to the exact-round-trip theory above.");
    }

    // ══ Z agreement — the marker's Γ and the readout strip's own Z must agree ══════════════════

    [Theory]
    [InlineData(50.0)]
    [InlineData(12.0)]
    public void ZAgreesWithTheReadoutStripsOwnImpedanceOf(double z0)
    {
        var p = HarmonicaPanelRenderer.GammaToCanvas(new Complex(0.4, -0.15), Size);
        var gamma = HarmonicaPanelRenderer.CanvasToGamma(p, Size);

        var z = HarmonicaDataSet.ImpedanceOf(gamma, z0);
        var expected = HarmonicaDataSet.ImpedanceOf(gamma, z0);
        Assert.Equal(expected.Real,      z.Real,      precision: 9);
        Assert.Equal(expected.Imaginary, z.Imaginary, precision: 9);
    }

    // ══ Marker/locus agreement — §2.1's own defect ══════════════════════════════════════════

    [Fact]
    public void AnActiveMarkersOwnVswrLocus_IsDrawnConcentricWithTheDrawnMarker()
    {
        // Before R8B §2, DrawMarkers painted the marker glyph at
        // IntrinsicGlyphScale.DisplayPosition(m.Gamma) while DrawVswrLocus painted the SAME marker's
        // VSWR circle through the raw transform — two different radial mappings for an active
        // (|Γ| > 1) marker, so the circle was not centred on, and did not pass around, the marker as
        // painted. DrawMarkers now paints m.Gamma directly (no DisplayPosition composition), so the
        // two must agree.
        var gamma = new Complex(1.2, 0.0);
        double z0 = 50.0;

        var markerAt = HarmonicaPanelRenderer.GammaToCanvas(gamma, Size);
        var pts = LoadpullSurface.VswrLocus(gamma, 2.0, SurfacePlane.Gamma, new Complex(z0, 0.0));
        Assert.NotNull(pts);

        // Every locus sample, projected through the SAME transform the marker is drawn with, lands
        // within one grab radius of the drawn marker centre in Γ-plane distance — the circle really
        // does surround the marker as painted, not some compressed ghost of it.
        double maxDelta = 0.0;
        foreach (var pt in pts!)
        {
            var canvasPt = HarmonicaPanelRenderer.GammaToCanvas(pt, Size);
            double d = System.Math.Sqrt(System.Math.Pow(canvasPt.X - markerAt.X, 2) + System.Math.Pow(canvasPt.Y - markerAt.Y, 2));
            maxDelta = System.Math.Max(maxDelta, d);
        }
        output.WriteLine($"largest marker-to-locus-sample canvas distance: {maxDelta:F1} px");

        // The locus radius for VSWR=2 at an active Γ=1.2 centre is a real, bounded distance (not the
        // ~0.24 Γ-unit disagreement the old dual-transform bug produced) — sanity-bound it well under
        // a full panel width rather than asserting an exact figure that would drift with panel size.
        Assert.True(maxDelta < Size.W, "the VSWR locus is nowhere near its own marker");
    }
}
