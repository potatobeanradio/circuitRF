using System;
using System.Linq;
using System.Numerics;
using RfCore.Propagation;
using RfCore.Statistics;
using Xunit;
using Xunit.Abstractions;

namespace RfCore.Tests.Propagation;

/// <summary>
/// Brief PCH-7's generator and profile gates, each at a fixed seed against a closed form:
///   1  Distributions: 10⁵ envelope samples (1,000 drops × 100 instants 10/f_D apart) pass a one-sample Kolmogorov-Smirnov
///      test at the 1 % level (D ≤ 1.628/√n = 0.00515) against Rayleigh, Rician at K = 3 and K = 10, and Nakagami at m = 2.
///   2  Correlation: one realization's time autocorrelation of the classical spectrum follows J0(2πf_Dτ) to 0.02 out to J0's
///      third zero.
///   3  Level crossings: the rate and average fade duration at −10 dB within 5 % of √(2π)f_Dρe^(−ρ²) and
///      (e^(ρ²) − 1)/(ρf_D√(2π)).
///   4  Profiles: every embedded profile's powers sum to 1 to 1e-12 and its RMS delay spread is the DelaySpread asked, to 1e-9.
/// </summary>
public class FadingTests(ITestOutputHelper output)
{
    private const double Fd = 100;

    private static FadingProcess Process(FadingKind kind, long drop, double k = 0, double m = 1, int n = FadingProcess.DefaultSinusoids,
                                         DopplerShape shape = DopplerShape.Classical)
        => new(new TapFading(kind, shape, Fd, k, m), 7, drop, "CH1:tap1", n);

    /// <summary>The Kolmogorov-Smirnov distance of <paramref name="samples"/> from the CDF <paramref name="cdf"/>.</summary>
    private static double Ks(double[] samples, Func<double, double> cdf)
    {
        var s = samples.OrderBy(x => x).ToArray();
        double d = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double f = cdf(s[i]);
            d = Math.Max(d, Math.Max(Math.Abs(f - (double)i / s.Length), Math.Abs((i + 1.0) / s.Length - f)));
        }
        return d;
    }

    private static double[] Envelopes(Func<long, FadingProcess> make)
    {
        var r = new double[100_000];
        for (int drop = 0; drop < 1000; drop++)
        {
            var p = make(drop + 1);
            for (int j = 0; j < 100; j++) r[drop * 100 + j] = p.Gain(j * 10 / Fd).Magnitude;
        }
        return r;
    }

    /// <summary>The Rician CDF at unit mean power: 1 − Q1(√(2K), r√(2(K+1))), the Marcum Q by its series.</summary>
    private static double RicianCdf(double k, double r)
    {
        double a = Math.Sqrt(2 * k), b = r * Math.Sqrt(2 * (k + 1));
        // Q1(a, b) = e^(−(a²+b²)/2) Σ_{n≥0} (a/b)^n I_n(ab); 1 − Q1 = e^(−(a²+b²)/2) Σ_{n≥1} (b/a)^n I_n(ab)
        double x = a * b, sum = 0;
        for (int n = 1; n < 400; n++)
        {
            double term = Math.Exp(n * Math.Log(b / a) + LogBesselI(n, x) - (a * a + b * b) / 2);
            sum += term;
            if (n > 10 && term < 1e-17) break;
        }
        return Math.Min(1, sum);
    }

    /// <summary>log I_n(x) by its power series, summed in logs.</summary>
    private static double LogBesselI(int n, double x)
    {
        if (x == 0) return n == 0 ? 0 : double.NegativeInfinity;
        double lx = Math.Log(x / 2), lead = n * lx - GammaFunction.LogGamma(n + 1), s = 0, t = 1;
        for (int k = 1; k < 2000; k++)
        {
            s += t;
            t *= x * x / 4 / (k * (double)(k + n));
            if (t < 1e-18 * s) { s += t; break; }
        }
        return lead + Math.Log(s);
    }

    [Fact]
    public void Gate1_EnvelopeDistributions_PassKolmogorovSmirnov()
    {
        const double critical = 1.628 / 316.227766;     // 1 % level, n = 10⁵
        double ray = Ks(Envelopes(d => Process(FadingKind.Rayleigh, d)), r => 1 - Math.Exp(-r * r));
        double k3 = Ks(Envelopes(d => Process(FadingKind.Rician, d, k: 3)), r => RicianCdf(3, r));
        double k10 = Ks(Envelopes(d => Process(FadingKind.Rician, d, k: 10)), r => RicianCdf(10, r));
        double m2 = Ks(Envelopes(d => Process(FadingKind.Nakagami, d, m: 2)), r => GammaFunction.P(2, 2 * r * r));
        output.WriteLine($"KS D (critical {critical:G4}): Rayleigh {ray:G4}, Rician K=3 {k3:G4}, K=10 {k10:G4}, Nakagami m=2 {m2:G4}");
        Assert.True(ray < critical, $"Rayleigh D {ray}");
        Assert.True(k3 < critical, $"Rician K=3 D {k3}");
        Assert.True(k10 < critical, $"Rician K=10 D {k10}");
        Assert.True(m2 < critical, $"Nakagami m=2 D {m2}");
    }

    /// <summary>J0 by its power series, independent of the generator's own.</summary>
    private static double J0(double x)
    {
        double s = 0, t = 1;
        for (int k = 1; k < 200; k++) { s += t; t *= -x * x / 4 / (k * (double)k); if (Math.Abs(t) < 1e-18) break; }
        return s;
    }

    [Fact]
    public void Gate2_ClassicalAutocorrelation_FollowsJ0_ThroughItsThirdZero()
    {
        // one realization, 2,000 fade periods at 50 samples a period; the lag runs to J0's third zero, 8.6537/(2π f_D)
        var p = Process(FadingKind.Rayleigh, 37);
        double dt = 1 / (50 * Fd);
        int n = 100_000, lags = (int)Math.Ceiling(8.6537 / (2 * Math.PI * Fd) / dt) + 1;
        var g = new Complex[n + lags];
        for (int i = 0; i < g.Length; i++) g[i] = p.Gain(i * dt);
        double worst = 0;
        for (int l = 0; l < lags; l++)
        {
            Complex acc = 0;
            for (int i = 0; i < n; i++) acc += g[i + l] * Complex.Conjugate(g[i]);
            worst = Math.Max(worst, Math.Abs(acc.Real / n - J0(2 * Math.PI * Fd * l * dt)));
        }
        output.WriteLine($"worst |R(τ) − J0| over {lags} lags: {worst:G3}");
        Assert.True(worst < 0.02, $"{worst}");
    }

    [Fact]
    public void Gate3_LevelCrossingRateAndFadeDuration_AtMinus10dB()
    {
        // six drops of 2,000 fade periods each at 250 samples a period (a fade at −10 dB lasts 0.13 periods: 33 samples)
        double rho = Math.Pow(10, -10 / 20.0), dt = 1 / (250 * Fd);
        int crossings = 0; double below = 0, span = 0;
        for (int drop = 1; drop <= 6; drop++)
        {
            var p = Process(FadingKind.Rayleigh, drop);
            int n = 2000 * 250;
            bool was = p.Gain(0).Magnitude < rho;
            for (int i = 1; i <= n; i++)
            {
                bool now = p.Gain(i * dt).Magnitude < rho;
                if (now) below += dt;
                if (now && !was) crossings++;          // downward crossings
                was = now;
            }
            span += n * dt;
        }
        double lcr = crossings / span, afd = below / crossings;
        double lcr0 = Math.Sqrt(2 * Math.PI) * Fd * rho * Math.Exp(-rho * rho);
        double afd0 = (Math.Exp(rho * rho) - 1) / (rho * Fd * Math.Sqrt(2 * Math.PI));
        output.WriteLine($"{crossings} crossings: rate {lcr:G5}/s against {lcr0:G5}/s ({lcr / lcr0 - 1:P2}); fade {afd:G5} s against {afd0:G5} s ({afd / afd0 - 1:P2})");
        Assert.True(Math.Abs(lcr / lcr0 - 1) < 0.05, $"LCR {lcr} against {lcr0}");
        Assert.True(Math.Abs(afd / afd0 - 1) < 0.05, $"AFD {afd} against {afd0}");
    }

    [Fact]
    public void Gate4_EveryProfile_SumsToOne_AndHasTheAskedSpread()
    {
        foreach (var p in TdlProfiles.All)
            foreach (double ds in new[] { 30e-9, 300e-9 })
            {
                var r = TdlProfiles.Resolve(p, ds, null);
                double sum = r.Taps.Sum(t => t.Power);
                double rms = TdlProfiles.Rms(r.Taps.Select(t => (t.DelayS, t.Power)));
                output.WriteLine($"{p.Name} at {ds * 1e9} ns: {r.Taps.Count} taps, Σp − 1 = {sum - 1:G3}, rms/DS − 1 = {rms / ds - 1:G3}, " +
                                 $"rescale {r.DelayRescale:G6}, printed powers {10 * Math.Log10(r.PowerSum):F3} dB");
                Assert.True(Math.Abs(sum - 1) < 1e-12, $"{p.Name}: {sum}");
                Assert.True(Math.Abs(rms / ds - 1) < 1e-9, $"{p.Name}: {rms} against {ds}");
            }
        // TDL-D's K moved to 6 dB by §7.7.6: the Rician tap's K changes and the spread is still the one asked
        var d = TdlProfiles.Resolve(TdlProfiles.Find("TDL-D")!, 100e-9, 6);
        Assert.True(Math.Abs(TdlProfiles.Rms(d.Taps.Select(t => (t.DelayS, t.Power))) / 100e-9 - 1) < 1e-9);
        Assert.Throws<PropagationRefusal>(() => TdlProfiles.Resolve(TdlProfiles.Find("TDL-A")!, 100e-9, 6));
        Assert.Throws<PropagationRefusal>(() => TdlProfiles.Resolve(TdlProfiles.Find("TDL-C")!, null, null));
        // a table in ns is used as printed when no spread is asked: EPA's last tap at 410 ns
        Assert.Equal(410e-9, TdlProfiles.Resolve(TdlProfiles.Find("EPA")!, null, null).Taps[^1].DelayS, 1e-21);
    }

    /// <summary>Stateless: a drop's gain is a function of its own draws, so drop 37 made alone equals drop 37 made after 36
    /// others, bit for bit, and another tap's stream does not move it.</summary>
    [Fact]
    public void Draws_AreStateless()
    {
        var alone = Process(FadingKind.Rayleigh, 37).Gain(0.0123);
        for (int d = 1; d < 37; d++) _ = Process(FadingKind.Rayleigh, d).Gain(0.0123);
        _ = new FadingProcess(new TapFading(FadingKind.Rayleigh, DopplerShape.Classical, Fd), 7, 37, "CH1:tap2").Gain(0.0123);
        Assert.Equal(alone, Process(FadingKind.Rayleigh, 37).Gain(0.0123));
    }

    /// <summary>The Nakagami quantile above m = 2: unguarded, Newton from a small r² at m ≥ 3 overshot to where dP/dx underflows and
    /// returned x ~1e14 (m = 4, r² = 0.1929: |g| of +135 dB). Over m = 3…20 and r² from 1e-4 to 30, P(m, x) = 1 − e^(−r²) to 1e-10
    /// (the upper tail read from Q). And J0 at fixed cost against its integral.</summary>
    [Fact]
    public void NakagamiQuantile_StaysBracketed_AtLargeM()
    {
        Assert.Equal(2.1733639776526843, FadingMath.NakagamiQuantileX(4, 0.1929), 9);
        foreach (double m in (double[])[3, 4, 5, 8, 10, 20])
            for (double r2 = 1e-4; r2 <= 30; r2 *= 1.7)
            {
                double x = FadingMath.NakagamiQuantileX(m, r2);
                Assert.True(double.IsFinite(x) && x < 200, $"m {m}, r² {r2}: x = {x}");
                double rel = r2 > 1 ? Math.Abs(GammaFunction.Q(m, x) / Math.Exp(-r2) - 1) : Math.Abs(GammaFunction.P(m, x) / (1 - Math.Exp(-r2)) - 1);
                Assert.True(rel < 1e-10, $"m {m}, r² {r2}: relative error {rel:E2}");
            }
        foreach (double z in (double[])[0, 2.404825557695773, 7.3, 11.99, 12.01, 40, 600])
        {
            int nq = 64 + 2 * (int)Math.Ceiling(z);
            double integral = Enumerable.Range(0, nq).Sum(k => Math.Cos(z * Math.Sin(2 * Math.PI * k / nq))) / nq;
            Assert.Equal(integral, FadingMath.J0(z), 11);
        }
    }
}
