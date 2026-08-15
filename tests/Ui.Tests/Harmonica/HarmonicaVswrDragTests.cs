// ================================================================
//  HarmonicaVswrDragTests.cs — R8B §7.3
//
//  "I can't drag the VSWR circle outside the Smith Chart." Two findings, not a bug in the drag path
//  itself: (1) a THEOREM — a passive marker's whole VSWR family stays strictly inside |Γ| = 1 for
//  every finite VSWR, so it literally cannot be dragged outside the chart; (2) a saturation that hid
//  (1) badly — VswrThrough silently returned the clamped MaxVswr the instant a drag point fell
//  outside the loosest circle in its search bracket, which read as "the number stopped moving".
//  VswrThroughEx reports the clamp instead of hiding it.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Ui.Harmonica;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaVswrDragTests(ITestOutputHelper output)
{
    private const double Z0 = 50.0;

    [Fact]
    public void APassiveCentres_WholeFamily_StaysStrictlyInsideTheUnitCircle_EvenAtAnExtremeVswr()
    {
        var center = new Complex(0.3, -0.2);
        var pts = LoadpullSurface.VswrLocus(center, HarmonicaVswrHandle.MaxVswr, SurfacePlane.Gamma,
                                            new Complex(Z0, 0.0), nPoints: 360);
        Assert.NotNull(pts);

        double maxMag = pts!.Max(p => p.Magnitude);
        output.WriteLine($"passive centre {center}, VSWR = {HarmonicaVswrHandle.MaxVswr}: max |Γ| on locus = {maxMag:F6}");
        Assert.All(pts, p => Assert.True(p.Magnitude < 1.0, $"|Γ| = {p.Magnitude} is not < 1"));
    }

    [Fact]
    public void ADragPointJustOutsideTheRim_FromAPassiveCentre_IsSaturated()
    {
        var center = new Complex(0.3, -0.2);
        var dragGamma = new Complex(1.5, 0.0);

        var (vswr, saturated) = HarmonicaVswrHandle.VswrThroughEx(center, dragGamma, Z0);
        output.WriteLine($"drag Γ = {dragGamma} from passive centre {center} -> VSWR = {vswr}, saturated = {saturated}");
        Assert.True(saturated);
        Assert.Equal(HarmonicaVswrHandle.MaxVswr, vswr);
    }

    [Fact]
    public void AnActiveCentres_WholeFamily_StaysStrictlyOutsideTheUnitCircle()
    {
        var center = new Complex(1.4, 0.0);
        var pts = LoadpullSurface.VswrLocus(center, 5.0, SurfacePlane.Gamma, new Complex(Z0, 0.0), nPoints: 360);
        Assert.NotNull(pts);

        double minMag = pts!.Min(p => p.Magnitude);
        output.WriteLine($"active centre {center}, VSWR = 5: min |Γ| on locus = {minMag:F6}");
        Assert.All(pts, p => Assert.True(p.Magnitude > 1.0, $"|Γ| = {p.Magnitude} is not > 1"));
    }

    [Fact]
    public void ADragPointJustOutsideAnActiveCentresOwnLocus_IsNOTSaturated()
    {
        // R8B §2's other half: an active marker draws — and can be dragged — genuinely outside the
        // chart now, unclipped. A drag just beyond a modest-VSWR active locus still has plenty of
        // bracket room left; it must NOT report saturated.
        var center = new Complex(1.4, 0.0);
        var (ctr, rad) = CircleParamsFor(center, 5.0, Z0);
        var direction = (center - ctr).Magnitude > 1e-9
            ? (center - ctr) / (center - ctr).Magnitude
            : new Complex(1.0, 0.0);
        var justOutside = ctr + direction * (rad * 1.1);

        var (vswr, saturated) = HarmonicaVswrHandle.VswrThroughEx(center, justOutside, Z0);
        output.WriteLine($"drag Γ = {justOutside} from active centre {center} -> VSWR = {vswr}, saturated = {saturated}");
        Assert.False(saturated);
    }

    [Fact]
    public void FormatVswr_Saturated_ReportsTheBound_NotTheClampedNumber()
    {
        Assert.Equal("VSWR: > 10⁶", HarmonicaReadoutFormatting.FormatVswr(HarmonicaVswrHandle.MaxVswr, saturated: true));
        Assert.Equal("VSWR: 2", HarmonicaReadoutFormatting.FormatVswr(2.0, saturated: false));
    }

    private static (Complex Ctr, double Rad) CircleParamsFor(Complex center, double vswr, double z0)
    {
        var pts = LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma, new Complex(z0, 0.0), nPoints: 2);
        return ((pts![0] + pts[1]) / 2.0, (pts[0] - pts[1]).Magnitude / 2.0);
    }
}
