using System.Numerics;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Synthesis;

/// <summary>
/// Extraction (docs/design/filter-synthesis.md §6c) from the coupling matrix's own response: a matrix and a finite Qu
/// come back from the samples, and an ideal Chebyshev's band, ripple and return loss are its spec's.
/// </summary>
public sealed class FilterExtractionTests(ITestOutputHelper output)
{
    private const double F0 = 2.4e9;

    private static double Hz(double omega, double fbw)
    {
        double x = omega * fbw / 2.0;
        return F0 * (Math.Sqrt(x * x + 1.0) + x);
    }

    /// <summary>The spec the matrix stands for: its band at ω = ±1, its zeros in Hz.</summary>
    private static FilterSpec Spec(int order, double fbw, double rippleDb, params double[] zeros)
        => new(FilterResponse.Chebyshev, FilterBand.Bandpass, order, Hz(-1.0, fbw), Hz(1.0, fbw), rippleDb,
            TransmissionZerosHz: [.. zeros.Select(z => Hz(z, fbw))]);

    /// <summary>401 samples over ω = ±4, wider than the fit's window.</summary>
    private static (double[] F, Complex[] S11, Complex[] S21) Sweep(double[,] m, double fbw, double qu)
    {
        double[] w = [.. Enumerable.Range(0, 401).Select(i => -4.0 + 8.0 * i / 400.0)];
        var s = w.Select(x => CouplingMatrix.Response(m, x, qu, fbw)).ToArray();
        return ([.. w.Select(x => Hz(x, fbw))], [.. s.Select(v => v.S11)], [.. s.Select(v => v.S21)]);
    }

    public static TheoryData<string, double> RoundTrips => new() { { "Cameron 4-2", 500.0 }, { "detuned 5", 200.0 } };

    [Theory, MemberData(nameof(RoundTrips))]
    public void TheMatrixAndQu_ComeBackFromTheResponse(string which, double qu)
    {
        const double fbw = 0.05;
        double[] zeros = which == "Cameron 4-2" ? [1.3217, 1.8082] : [];
        double[,] m;
        if (zeros.Length > 0) m = CouplingMatrix.Synthesise(4, 22, zeros);
        else
        {
            // A 0.1 dB Chebyshev of order 5, three resonators off tune and the source coupled 10 % heavier: its reflection
            // zeros leave the axis, so F is complex in ω.
            m = CouplingMatrix.AllPole(LowpassPrototype.GValues(FilterResponse.Chebyshev, 5, 0.1));
            (m[1, 1], m[3, 3], m[5, 5]) = (0.1, -0.08, 0.05);
            m[0, 1] = m[1, 0] = 1.1 * m[0, 1];
        }
        var spec = Spec(m.GetLength(0) - 2, fbw, 0.1, zeros);
        var (f, s11, s21) = Sweep(m, fbw, qu);
        var x = FilterExtraction.Extract(spec, f, s11, s21, m, tolerance: 1e-6);

        double[,] t = FilterExtraction.Signed(m);
        double worst = 0.0;
        for (int i = 0; i < t.GetLength(0); i++)
            for (int j = 0; j < t.GetLength(0); j++) worst = Math.Max(worst, Math.Abs(x.Matrix[i, j] - t[i, j]));
        output.WriteLine($"{which}: worst |ΔM| {worst:E2}, Qu {x.UnloadedQ:F3} against {qu}");
        Assert.True(worst < 1e-6, $"worst |ΔM| = {worst}");
        Assert.Equal(1.0, x.UnloadedQ / qu, 0.01);
        Assert.All(x.Checks.Skip(4), c => Assert.True(c.Pass, $"{c.Name}: {c.Value} against {c.Target}"));
        Assert.Equal(spec.TransmissionZerosHz!, x.TransmissionZerosHz, (a, b) => Math.Abs(a - b) < 1.0);
    }

    [Fact]
    public void AnIdealChebyshev_ShowsItsSpec()
    {
        const double fbw = 0.1, ripple = 0.1;
        var spec = Spec(5, fbw, ripple);
        double[,] m = CouplingMatrix.AllPole(LowpassPrototype.GValues(FilterResponse.Chebyshev, 5, ripple));
        var (f, s11, s21) = Sweep(m, fbw, double.PositiveInfinity);
        var x = FilterExtraction.Extract(spec, f, s11, s21);
        double rl = -10.0 * Math.Log10(1.0 - Math.Pow(10.0, -ripple / 10.0));
        output.WriteLine($"f0 {x.CentreHz / 1e9:F6} GHz, BW {x.BandwidthHz / 1e6:F3} MHz, ripple {x.RippleDb:F4} dB, "
                         + $"RL {x.ReturnLossDb:F3} dB, IL {x.InsertionLossDb:F4} dB, Qu {x.UnloadedQ:G3}");

        Assert.Equal(1.0, x.CentreHz / spec.CentreHz, 1e-5);
        Assert.Equal(1.0, x.BandwidthHz / (spec.F2Hz - spec.F1Hz), 1e-3);
        Assert.Equal(ripple, x.RippleDb, 2e-3);
        Assert.Equal(rl, x.ReturnLossDb, 0.05);
        Assert.Equal(0.0, x.InsertionLossDb, 1e-3);
        Assert.True(x.UnloadedQ > 1e5);
        Assert.Equal(5, x.Order);
        Assert.Empty(x.TransmissionZerosHz);
        Assert.All(x.Checks, c => Assert.True(c.Pass, $"{c.Name}: {c.Value} against {c.Target}"));
    }
}
