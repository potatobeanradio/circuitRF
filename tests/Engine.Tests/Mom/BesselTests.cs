using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// Tier −1: the special functions, before anything that uses them.
///
/// <para>Kernel A needed no Bessel functions, so <see cref="Bessel"/> is new code with no
/// established behaviour to regress against. It is gated the same way every other new formulation
/// in this area is: against a <b>second, independent</b> expression of the same quantity, never
/// against itself.</para>
/// <list type="bullet">
///   <item>J₀/J₁ against the integral representations <c>J₀(x) = (1/π)∫₀^π cos(x sin θ)dθ</c> and
///     <c>J₁(x) = (1/π)∫₀^π cos(θ − x sin θ)dθ</c>, evaluated by Gauss-Legendre with enough nodes
///     to resolve the oscillation. These share no code and no series with the implementation.</item>
///   <item>Y₀/Y₁ against the Wronskian identity, which no wrong pair of functions satisfies.</item>
///   <item>Bessel's differential equation by finite difference across the series/asymptotic
///     crossover, where half the stencil comes from each branch — so it checks both the value and
///     the continuity of the handover, at the one place both formulas are at their worst.</item>
/// </list>
/// </summary>
public sealed class BesselTests
{
    private readonly ITestOutputHelper _out;
    public BesselTests(ITestOutputHelper output) => _out = output;

    /// <summary>Node counts chosen once so the Gauss-Legendre table is built twice, not 300 times.</summary>
    private static int NodesFor(double x) => x <= 60 ? 700 : 1600;

    /// <summary>J₀(x) = (1/π)∫₀^π cos(x sin θ) dθ — an integral representation, not a series.</summary>
    private static double J0Integral(double x) =>
        Quadrature.Integrate(t => Math.Cos(x * Math.Sin(t)), 0, Math.PI, NodesFor(x)) / Math.PI;

    /// <summary>J₁(x) = (1/π)∫₀^π cos(θ − x sin θ) dθ.</summary>
    private static double J1Integral(double x) =>
        Quadrature.Integrate(t => Math.Cos(t - x * Math.Sin(t)), 0, Math.PI, NodesFor(x)) / Math.PI;

    [Fact]
    public void TB1_J0AndJ1_MatchTheirIntegralRepresentations_AcrossBothBranches()
    {
        double worst0 = 0, worst1 = 0, atX0 = 0, atX1 = 0;
        for (double x = 0.05; x <= 200.0; x *= 1.05)
        {
            double e0 = Math.Abs(Bessel.J0(x).Real - J0Integral(x));
            double e1 = Math.Abs(Bessel.J1(x).Real - J1Integral(x));
            if (e0 > worst0) { worst0 = e0; atX0 = x; }
            if (e1 > worst1) { worst1 = e1; atX1 = x; }
            Assert.Equal(0.0, Bessel.J0(x).Imaginary);   // a real argument gives a real value
        }
        _out.WriteLine($"worst |ΔJ₀| = {worst0:E3} at x = {atX0:F3}");
        _out.WriteLine($"worst |ΔJ₁| = {worst1:E3} at x = {atX1:F3}");
        Assert.True(worst0 < 1e-10, $"J₀ worst error {worst0:E3} at x = {atX0}");
        Assert.True(worst1 < 1e-10, $"J₁ worst error {worst1:E3} at x = {atX1}");
    }

    [Fact]
    public void TB2_TheWronskian_HoldsForComplexArgument_AcrossBothBranches()
    {
        double worst = 0;
        Complex atZ = 0;
        foreach (double re in new[] { 0.2, 1.0, 3.0, 7.0, 12.0, 13.5, 20.0, 50.0, 150.0 })
        foreach (double im in new[] { -6.0, -1.0, 0.0, 1.0, 6.0 })
        {
            var z = new Complex(re, im);
            double e = (Bessel.WronskianResidual(z) / (2.0 / (Math.PI * z))).Magnitude;
            if (e > worst) { worst = e; atZ = z; }
        }
        _out.WriteLine($"worst relative Wronskian residual = {worst:E3} at z = {atZ}");
        Assert.True(worst < 1e-9, $"Wronskian residual {worst:E3} at z = {atZ}");
    }

    [Fact]
    public void TB3_BesselsEquation_HoldsAcrossTheSeriesAsymptoticCrossover()
    {
        // z²y″ + zy′ + z²y = 0.  The crossover is |z| = 13, so a stencil centred there is half
        // series and half asymptotic; a discontinuous handover shows up immediately in y″.
        const double dz = 1e-3;
        double worst = 0, atX = 0;
        for (double x = 12.0; x <= 14.0; x += 0.02)
        {
            double ym = Bessel.J0(x - dz).Real, y0 = Bessel.J0(x).Real, yp = Bessel.J0(x + dz).Real;
            double d1 = (yp - ym) / (2 * dz);
            double d2 = (yp - 2 * y0 + ym) / (dz * dz);
            double residual = Math.Abs(x * x * d2 + x * d1 + x * x * y0);
            if (residual > worst) { worst = residual; atX = x; }
        }
        _out.WriteLine($"worst ODE residual across the crossover = {worst:E3} at x = {atX:F3}");
        // The gate is set by the FINITE DIFFERENCE, not by Bessel: a second difference amplifies
        // the function's own ~1e-12 evaluation noise by x²/dz² ≈ 1.6e8, which is the 2.7e-4 seen.
        // The sensitivity that matters is the other direction — a discontinuous handover of size δ
        // registers at 1.6e8·δ, so this gate still catches any jump above ~1e-11, i.e. below the
        // accuracy TB1 measures for either branch on its own.
        Assert.True(worst < 1e-3, $"ODE residual {worst:E3} at x = {atX}");
    }

    [Fact]
    public void TB4_H02_IsJ0MinusJY0_AndDecaysBelowTheRealAxis()
    {
        foreach (var z in new[] { new Complex(2, -0.3), new Complex(9, -1), new Complex(30, -2), new Complex(80, -0.05) })
        {
            var direct = Bessel.J0(z) - Complex.ImaginaryOne * Bessel.Y0(z);
            var h = Bessel.H02(z);
            Assert.True((h - direct).Magnitude <= 1e-9 * Math.Max(1, direct.Magnitude),
                $"H₀⁽²⁾ vs J₀ − jY₀ at z = {z}: {h} vs {direct}");
        }

        // e^{jωt} ⇒ H₀⁽²⁾ is the OUTGOING wave: |H₀⁽²⁾(z)| must shrink as Im z goes negative.
        Assert.True(Bessel.H02(new Complex(30, -3)).Magnitude < Bessel.H02(new Complex(30, 0)).Magnitude);
    }
}
