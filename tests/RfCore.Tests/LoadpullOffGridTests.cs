// ================================================================
//  LoadpullOffGridTests.cs — brief-ddl-3: the two LoadpullSurface
//  changes a read at an arbitrary load needs — the measured-hull test,
//  and the low-power clamp in GetPowerSweep that is only meaningful
//  when X is the drive itself.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class LoadpullOffGridTests
{
    /// <summary>
    /// A 7×7 grid of loads, Γ = −0.6 … 0.6 in both axes, each driven −5 … 30 dBm into a soft limiter whose small-signal
    /// gain and saturated power are smooth in Γ. Analytic, so a read between loads has a truth to be compared with.
    /// </summary>
    internal static DataSet Synthetic(out Func<Complex, double, double> poutTruth)
    {
        const int n = 7, nPin = 36;
        var gammas = new Complex[n * n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) gammas[i * n + k] = new Complex(-0.6 + 0.2 * i, -0.6 + 0.2 * k);
        double[] pins = [.. Enumerable.Range(0, nPin).Select(p => -5.0 + p)];

        static double Gain(Complex g) => 15 - 8 * Complex.Abs(g - new Complex(0.5, 0.4)) * Complex.Abs(g - new Complex(0.5, 0.4));
        static double Psat(Complex g) => 40 - 10 * Complex.Abs(g - new Complex(0.6, 0.0)) * Complex.Abs(g - new Complex(0.6, 0.0));
        poutTruth = (g, pin) =>
        {
            double lin = Math.Pow(10, (pin + Gain(g)) / 10), sat = Math.Pow(10, Psat(g) / 10);
            return 10 * Math.Log10(lin / Math.Pow(1 + Math.Pow(lin / sat, 4), 0.25));
        };

        var grid = new Axis("gridPoint", [.. Enumerable.Range(0, n * n).Select(i => (double)i)]);
        var pin  = new Axis("pinStep", pins, "dBm");
        double[] Cube(Func<Complex, double, double> f)
        {
            var v = new double[n * n * nPin];
            for (int g = 0; g < n * n; g++) for (int p = 0; p < nPin; p++) v[g * nPin + p] = f(gammas[g], pins[p]);
            return v;
        }
        var truth = poutTruth;
        const double pdcW = 20;
        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube([grid], gammas));
        ds.Add("ZLoad",     new DataCube([grid], [.. gammas.Select(g => 50 * (1 + g) / (1 - g))]));
        ds.Add("PavlDbm",   new DataCube([grid, pin], Cube((g, p) => p)));
        ds.Add("Pout_dBm",  new DataCube([grid, pin], Cube(truth)));
        ds.Add("Gt_dB",     new DataCube([grid, pin], Cube((g, p) => truth(g, p) - p)));
        ds.Add("Efficiency", new DataCube([grid, pin], Cube((g, p) => 100 * Math.Pow(10, truth(g, p) / 10) / 1000 / pdcW)));
        ds.Add("PAE",       new DataCube([grid, pin], Cube((g, p) =>
            100 * (Math.Pow(10, truth(g, p) / 10) - Math.Pow(10, p / 10)) / 1000 / pdcW)));
        return ds;
    }

    [Fact]
    public void IsInsideMeasured_IsTheConvexHullOfTheLoads()
    {
        var s = new LoadpullSurface(Synthetic(out _));
        Assert.True(s.IsInsideMeasured(0, new Complex(-0.3, -0.3)));    // between four loads
        Assert.True(s.IsInsideMeasured(0, new Complex(0.6, 0.6)));      // a corner load itself
        Assert.False(s.IsInsideMeasured(0, new Complex(0.8, 0.0)));
        Assert.Equal(0.2, s.DistanceOutsideMeasured(0, new Complex(0.8, 0.0)), 9);
    }

    /// <summary>
    /// The low-power clamp compares the first X with the drive's floor in dBm. With X = Pout — some 15 dB above the
    /// drive — it used to pull the curve's first point down to that floor. It applies only when X is the drive itself.
    /// </summary>
    [Fact]
    public void PowerSweep_WithPoutAsX_IsNotClampedToTheDriveFloor()
    {
        var s = new LoadpullSurface(Synthetic(out var truth));
        var node = new Complex(-0.2, 0.0);
        var ps = s.GetPowerSweep(0, node, "Pout_dBm", "Gt_dB", 3.0, SurfacePlane.Gamma)!;
        var drive = s.GetPowerSweep(0, node, "PavlDbm", "Pout_dBm", 3.0, SurfacePlane.Gamma)!;

        // The Pout-axis curve starts where the drive-axis curve's Pout starts, not at the drive floor.
        Assert.Equal(drive.Y[0], ps.X[0], 1);
        Assert.True(ps.X[0] > drive.X[0] + 5, $"first Pout {ps.X[0]:F2} dBm vs drive floor {drive.X[0]:F2} dBm");
    }
}
