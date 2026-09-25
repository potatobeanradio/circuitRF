using System.Numerics;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-9 §7 gates 1-3 — the port transform, with no openEMS: every probe here is synthesised,
// and every expected value comes from a closed form or from RfCore, never from the transform.

public sealed class FdtdPortTransformTests(ITestOutputHelper output)
{
    // ── 1. A series R-L-C between two ports, sampled as openEMS samples ─────────────────────────

    /// <summary>
    /// Both runs of a series R-L-C between two 50 Ω ports, integrated in time (RK4, 0.125 ps, for 3 ns —
    /// thirty times the slower pole's 100 ps, since a record cut at 1.5 ns leaves e^−13.5 ≈ 1.4e-6 of it) from a
    /// Gaussian source. As in openEMS, the probes sample every 1 ps and the CURRENT probes half an FDTD
    /// step (0.25 ps) after the voltage probes. The transform, reading each probe's own time column,
    /// recovers the analytic S to 1e-6 — and the same numbers with the current's offset dropped do not,
    /// by a phase error that grows with frequency. That second half is what makes the first mean
    /// something (overview §1f). This network has no Z-matrix (I₁ = −I₂ in every run), which is why the
    /// transform never inverts I.
    /// </summary>
    [Fact]
    public void Gate1_SeriesRlc_TheTransformRecoversS_AndDroppingTheHalfStepFails()
    {
        const double R = 10, L = 1e-9, C = 1e-12, Z0 = 50;
        const double h = 0.125e-12, fdtdStep = 0.5e-12, tau = 30e-12, t0 = 150e-12, tEnd = 3e-9;
        const int stepsPerSample = 8, currentOffset = (int)(fdtdStep / 2 / h);     // 1 ps samples, current +0.25 ps
        double Vs(double t) => Math.Exp(-Math.Pow((t - t0) / tau, 2));

        // L i' = vs − (R + 2 Z0) i − vC ;  C vC' = i
        int n = (int)Math.Round(tEnd / h);
        var iL = new double[n + 1];
        double i = 0, vc = 0;
        (double, double) F(double t, double ii, double v) => ((Vs(t) - (R + 2 * Z0) * ii - v) / L, ii / C);
        for (int k = 0; k < n; k++)
        {
            double t = k * h;
            var (a1, b1) = F(t, i, vc);
            var (a2, b2) = F(t + h / 2, i + h / 2 * a1, vc + h / 2 * b1);
            var (a3, b3) = F(t + h / 2, i + h / 2 * a2, vc + h / 2 * b2);
            var (a4, b4) = F(t + h, i + h * a3, vc + h * b3);
            i  += h / 6 * (a1 + 2 * a2 + 2 * a3 + a4);
            vc += h / 6 * (b1 + 2 * b2 + 2 * b3 + b4);
            iL[k + 1] = i;
        }

        var tu = new List<double>(); var ti = new List<double>();
        var uExc = new List<double>(); var uPas = new List<double>();
        var iExc = new List<double>(); var iPas = new List<double>();
        for (int k = 0; k + currentOffset <= n; k += stepsPerSample)
        {
            double t = k * h;
            tu.Add(t);
            uExc.Add(Vs(t) - Z0 * iL[k]);          // the excited port: source minus its resistor's drop
            uPas.Add(Z0 * iL[k]);                  // the terminated port: the loop current through its resistor
            ti.Add((k + currentOffset) * h);
            iExc.Add(iL[k + currentOffset]);       // into the structure at the excited port
            iPas.Add(-iL[k + currentOffset]);      // ...and out of it at the other
        }
        FdtdProbe P(List<double> t, List<double> x) => new([.. t], [.. x]);
        var excited = new FdtdPortProbes(P(tu, uExc), P(ti, iExc));
        var passive = new FdtdPortProbes(P(tu, uPas), P(ti, iPas));
        // Run 1 excites port 1; the network is symmetric, so run 2 is the same records swapped.
        List<IReadOnlyList<FdtdPortProbes>> runs = [[excited, passive], [passive, excited]];

        double[] f = [.. Enumerable.Range(1, 10).Select(k => k * 1e9)];
        Complex[] z0 = [Z0, Z0];
        var r = FdtdPortTransform.Solve(runs, f, [1, 2], z0);
        Assert.Null(r.Error);

        double worst = 0;
        for (int k = 0; k < f.Length; k++)
        {
            double w = 2 * Math.PI * f[k];
            Complex zs = new Complex(R, w * L - 1 / (w * C));
            Complex s11 = zs / (zs + 2 * Z0), s21 = 2 * Z0 / (zs + 2 * Z0);
            foreach (var (got, want) in new[] { (r.S[k][0, 0], s11), (r.S[k][1, 0], s21), (r.S[k][0, 1], s21), (r.S[k][1, 1], s11) })
                worst = Math.Max(worst, (got - want).Magnitude);
        }
        output.WriteLine($"own time columns: worst |S − S_analytic| = {worst:E3}");
        Assert.True(worst < 1e-6, $"{worst}");

        // The same samples, the current read on the VOLTAGE's time column: the classic bug.
        FdtdPortProbes Shared(FdtdPortProbes p) => p with { Current = p.Current with { TimeS = p.Voltage.TimeS } };
        var naive = FdtdPortTransform.Solve(
            [[Shared(excited), Shared(passive)], [Shared(passive), Shared(excited)]], f, [1, 2], z0);
        var err = new double[f.Length];
        for (int k = 0; k < f.Length; k++)
        {
            double w = 2 * Math.PI * f[k];
            Complex zs = new Complex(R, w * L - 1 / (w * C));
            err[k] = (naive.S[k][1, 0] - 2 * Z0 / (zs + 2 * Z0)).Magnitude;
        }
        output.WriteLine("offset dropped, |ΔS21| by frequency: " + string.Join(" ", err.Select(e => e.ToString("E2"))));
        Assert.True(err[^1] > 1e-3, $"{err[^1]}");
        Assert.True(err[^1] > 4 * err[0], $"{err[0]} -> {err[^1]}");
    }

    // ── 2. Through RfCore, with a complex Z0 ────────────────────────────────────────────────────

    /// <summary>
    /// A T-network with a known Z, excited by two arbitrary independent current patterns, gives the
    /// S that RFNetwork.ZToS gives on that Z — with a different complex reference at each port. One
    /// definition of a wave: the transform's rearrangement and RfCore's agree to round-off.
    /// </summary>
    [Fact]
    public void Gate2_AComplexZ0_GivesRfCoresZToS()
    {
        Complex[] z0 = [new(50, 10), new(25, -5)];
        foreach (double f in new[] { 0.5e9, 3e9, 17e9 })
        {
            double w = 2 * Math.PI * f;
            Complex zc = 1 / new Complex(0, w * 0.4e-12);
            Complex z1 = new(12, w * 0.8e-9), z2 = new(7, w * 1.3e-9);
            var z = new Mat<Complex>(2, 2);
            z[0, 0] = z1 + zc; z[0, 1] = zc; z[1, 0] = zc; z[1, 1] = z2 + zc;

            Complex[,] current = { { new(1, 0.1), new(-0.2, 0.05) }, { new(0.3, -0.4), new(0.8, 0.2) } };
            var runs = new List<IReadOnlyList<FdtdPortProbes>>();
            for (int k = 0; k < 2; k++)
            {
                var ports = new List<FdtdPortProbes>();
                for (int i = 0; i < 2; i++)
                {
                    Complex u = z[i, 0] * current[0, k] + z[i, 1] * current[1, k];
                    ports.Add(new FdtdPortProbes(TwoSample(u, f, 0), TwoSample(current[i, k], f, 0.37e-12)));
                }
                runs.Add(ports);
            }
            var r = FdtdPortTransform.Solve(runs, [f], [1, 2], z0);
            Assert.Null(r.Error);
            var expected = RFNetwork.ZToS(z, z0);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((r.S[0][i, j] - expected[i, j]).Magnitude < 1e-12, $"S{i + 1}{j + 1} at {f / 1e9} GHz");
        }
    }

    // ── 3. A port that carries nothing ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_APortThatCarriesNothing_IsAnErrorNamingThePortAndTheFrequency()
    {
        const double f = 2.5e9;
        var live = new FdtdPortProbes(TwoSample(new Complex(40, 3), f, 0), TwoSample(new Complex(0.2, 0.01), f, 0));
        var dead = new FdtdPortProbes(TwoSample(Complex.Zero, f, 0), TwoSample(Complex.Zero, f, 0));
        var r = FdtdPortTransform.Solve([[live, dead], [live, dead]], [f], [3, 7], [50, 50]);
        Assert.NotNull(r.Error);
        Assert.Contains("Port 7", r.Error);
        Assert.Contains("2.5 GHz", r.Error);
        output.WriteLine(r.Error);
    }

    /// <summary>
    /// A probe record whose DFT at <paramref name="f"/> is exactly <paramref name="x"/>: two samples a
    /// quarter period apart, so Σ x(tₙ)e^(−jωtₙ)Δtₙ = (x₀ − j·x₁)·T/4 · e^(−jωt₀) — and the phase of
    /// the start time <paramref name="t0"/> is taken back out, so each probe can have its own column.
    /// </summary>
    private static FdtdProbe TwoSample(Complex x, double f, double t0)
    {
        double q = 0.25 / f;
        Complex v = x * Complex.FromPolarCoordinates(1, 2 * Math.PI * f * t0) / q;
        return new FdtdProbe([t0, t0 + q], [v.Real, -v.Imaginary]);
    }
}
