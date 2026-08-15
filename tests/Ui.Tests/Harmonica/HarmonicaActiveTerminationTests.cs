// ================================================================
//  HarmonicaActiveTerminationTests.cs — R7A §1, §2/3 retired by R8B §2
//
//  Owner-reported (R7A): dragging a marker outside the Smith chart (an active termination, Re(Z) < 0)
//  made the Set Termination dialog and the marker fly menu report an incorrect Γ/Z. Root cause: the
//  compressed radial scale (IntrinsicGlyphScale), composed into the EXTRINSIC marker's own canvas
//  transform (MarkerToCanvas/CanvasToMarker), saturated its inverse at a `1 - 1e-9` clamp rather than
//  at a NAMED |Γ| ceiling, so every pointer position at or beyond drawn radius 1.25 collapsed to the
//  same Γ ≈ -1e9. §1's TrueRadius/DisplayRadius tests below still pin that fix — IntrinsicGlyphScale
//  itself is unchanged and still governs the INTRINSIC glyph.
//
//  R8B §2 removed the composition for the EXTRINSIC marker outright: MarkerToCanvas/CanvasToMarker
//  are gone, and an extrinsic marker now drags on the plain GammaToCanvas/CanvasToGamma affine map,
//  which has no saturation to fail — the whole class of bug this file's old §2/3 pinned is now
//  structurally impossible for a marker (see HarmonicaMarkerGammaTests for that map's own round-trip
//  coverage, including where it is now imperfect for a different, geometric reason). §4 is re-pointed
//  to the plain map rather than deleted, since "the marker and TerminationSet never disagree" is a
//  standing invariant regardless of which transform got it there.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaActiveTerminationTests(ITestOutputHelper output)
{
    private const double Z0 = 50.0;

    // ══ 1 — TrueRadius is the exact inverse of DisplayRadius, everywhere including the new ceiling ══

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.0001)]
    [InlineData(1.5)]
    [InlineData(3)]
    [InlineData(10)]
    public void TrueRadius_IsTheExactInverseOfDisplayRadius(double r)
    {
        double displayed = IntrinsicGlyphScale.DisplayRadius(r);
        double back = IntrinsicGlyphScale.TrueRadius(displayed);
        double rel = System.Math.Abs(back - r) / System.Math.Max(1.0, System.Math.Abs(r));
        output.WriteLine($"r={r} -> displayed={displayed} -> back={back} (rel err {rel:E3})");
        Assert.True(rel < 1e-9, $"TrueRadius(DisplayRadius({r})) = {back}, expected {r}");
    }

    [Theory]
    [InlineData(1.6)]   // past DisplayRadius(MaxTrueMagnitude) — used to collapse to Γ ≈ -1e9
    [InlineData(1.245)] // right at DisplayRadius(MaxTrueMagnitude)
    public void TrueRadius_AtOrPastTheAsymptote_SaturatesExactlyAtMaxTrueMagnitude(double displayRadius)
    {
        double ceilingDisplay = IntrinsicGlyphScale.DisplayRadius(IntrinsicGlyphScale.MaxTrueMagnitude);
        Assert.True(displayRadius >= ceilingDisplay - 1e-6,
            $"fixture radius {displayRadius} is not actually at or past the ceiling ({ceilingDisplay})");

        double back = IntrinsicGlyphScale.TrueRadius(displayRadius);
        Assert.Equal(IntrinsicGlyphScale.MaxTrueMagnitude, back, precision: 9);
    }

    // ══ 2/3 — RETIRED by R8B §2. An extrinsic marker's drag path is now the plain, uncompressed
    //    GammaToCanvas/CanvasToGamma affine map — there is no saturation left to fail, because there
    //    is no compression composed into it any more. See HarmonicaMarkerGammaTests for that map's
    //    own round-trip coverage.

    private static readonly (double W, double H) PanelSize = (400, 400);

    // ══ 4 — through the view model: the marker and the termination set can never disagree ══

    [Fact]
    public void SetMarkerGamma_ThenReadingTheTerminationBack_AgreesWithTheMarkersOwnGamma()
    {
        var vm = new HarmonicaViewModel();
        var marker = vm.Markers.FirstOrDefault(m => m.Side == TerminationSideKind.Load && m.Band == 1)
                     ?? throw new System.InvalidOperationException("no L1 marker on the default model");

        // A drawn radius well past the rim — an active termination, ordinary since R7A/R8B §2.
        var canvasPoint = HarmonicaPanelRenderer.GammaToCanvas(new Complex(-1.25, 0), PanelSize);
        var g = HarmonicaPanelRenderer.CanvasToGamma(canvasPoint, PanelSize);

        vm.SetMarkerGamma(marker, g);

        double z0 = vm.Model.Settings.Z0;
        var readBack = HarmonicaDataSet.GammaOf(vm.Terminations.Z(TerminationSide.Load, 1), z0);

        Assert.Equal(marker.Gamma.Real,      readBack.Real,      precision: 6);
        Assert.Equal(marker.Gamma.Imaginary, readBack.Imaginary, precision: 6);
    }
}
