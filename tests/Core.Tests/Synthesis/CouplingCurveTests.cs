using System.Numerics;
using CircuitRF.Core.Synthesis.Filters;
using Xunit;

namespace CircuitRF.Core.Tests.Synthesis;

/// <summary>The EM curves' extraction (docs/design/filter-synthesis.md §6b) on synthetic S-parameters over the 21 points a
/// bench is swept at: a lightly fed pair's two resonances give k, a singly loaded resonator's reflection gives Qe.</summary>
public class CouplingCurveTests
{
    [Fact]
    public void KAndQe_ComeBackFromTwentyOnePoints()
    {
        double fc = 2.4e9;
        var f = Enumerable.Range(0, CouplingCurves.Points).Select(i => fc * (0.85 + 0.3 * i / 20.0)).ToArray();

        // Two modes at f1 and f2, Q = 300 each, of opposite sign, far narrower than the 1.5 % step.
        double f1 = 2.31e9, f2 = 2.47e9, q = 300.0;
        Complex Mode(double fx, double fr) => 0.9 / (1.0 + Complex.ImaginaryOne * q * (fx / fr - fr / fx));
        var p = CouplingCurves.Poles(f, [.. f.Select(x => Mode(x, f1) - Mode(x, f2))], 2);
        Assert.Equal(CouplingCurves.K(f1, f2), CouplingCurves.K(p[0].Real, p[1].Real), 1e-4);

        // A singly loaded resonator, Qe = 15, behind 30 ps of line: its ±90° points are f0 ± f0/(2·Qe).
        double f0 = 2.43e9, qe = 15.0, tau = 30e-12;
        var s11 = f.Select(x =>
        {
            var y = Complex.ImaginaryOne * qe * (x / f0 - f0 / x);
            return Complex.Exp(-Complex.ImaginaryOne * 2.0 * Math.PI * x * tau) * (1.0 - y) / (1.0 + y);
        }).ToArray();
        Assert.Equal(qe, CouplingCurves.ExternalQ(f, s11), qe * 0.01);

        // The curve read backwards, in ln k between its points.
        var curve = new CouplingCurve([1e-4, 2e-4, 4e-4], [0.08, 0.04, 0.02]);
        Assert.Equal(3e-4, curve.Where(Math.Sqrt(0.04 * 0.02), "k"), 1e-12);
        Assert.Equal("synthesis.em.out-of-curve",
            Assert.Throws<CircuitRF.Core.Devices.ParameterRefusalException>(() => curve.Where(0.1, "k")).Diagnostic!.Id);
    }
}
