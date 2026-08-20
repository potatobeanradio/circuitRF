// ================================================================
//  GroupDelayTests.cs  —  RFNetwork.GroupDelay / NetworkMetrics.GroupDelay
//
//  Group delay is tau(omega) = -d(phi)/d(omega) on the UNWRAPPED phase of one S element. The
//  oracle throughout is an IDEAL DELAY LINE, S21 = exp(-j*omega*tau0), whose group delay is
//  tau0 exactly, at every frequency, with no numerical derivative needed to know the answer.
//  A pure phase ramp is the only fixture that separates "the derivative is right" from "the
//  unwrapping is right" — because it is the case that wraps, over and over.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public class GroupDelayTests
{
    /// <summary>S21 of an ideal, lossless, matched delay line of <paramref name="tau0"/> seconds.</summary>
    private static Complex Line(double f, double tau0) =>
        Complex.Exp(new Complex(0, -2.0 * Math.PI * f * tau0));

    private static double[] Linspace(double a, double b, int n) =>
        [.. Enumerable.Range(0, n).Select(i => a + (b - a) * i / (n - 1.0))];

    /// <summary>
    /// <b>The headline.</b> A 1 ns line swept 1-11 GHz turns its phase through ten full cycles, so
    /// <c>Complex.Phase</c> wraps ten times inside the sweep. Group delay is 1 ns at every point.
    /// </summary>
    /// <remarks>
    /// Without unwrapping, each wrap contributes a spike of one grid spacing' worth of 2*pi — this
    /// test fails by orders of magnitude at ten scattered points, not by a tolerance.
    /// </remarks>
    [Fact]
    public void AnIdealDelayLine_ReadsItsOwnDelay_AcrossTenPhaseWraps()
    {
        const double tau0 = 1e-9;
        var f = Linspace(1e9, 11e9, 501);
        var s21 = f.Select(x => Line(x, tau0)).ToArray();

        var tau = RFNetwork.GroupDelay(s21, f);

        Assert.Equal(f.Length, tau.Length);
        for (int i = 0; i < tau.Length; i++)
            Assert.Equal(tau0, tau[i], 1e-15);
    }

    /// <summary>The grid need not be uniform — the difference is taken against the actual abscissae.</summary>
    [Fact]
    public void ANonUniformSweep_ReadsTheSameDelay()
    {
        const double tau0 = 2.5e-10;
        // Geometric spacing: adjacent steps differ by ~2x across the sweep.
        var f = Enumerable.Range(0, 200).Select(i => 1e9 * Math.Pow(1.01, i)).ToArray();
        var tau = RFNetwork.GroupDelay(f.Select(x => Line(x, tau0)).ToArray(), f);

        Assert.All(tau, t => Assert.Equal(tau0, t, 1e-16));
    }

    /// <summary>
    /// A NEGATIVE delay is a real, physical readout (an amplifier with phase lead in band), so it is
    /// reported as a negative number rather than clamped or made absolute.
    /// </summary>
    [Fact]
    public void APhaseLead_ReadsANegativeDelay()
    {
        var f = Linspace(1e9, 2e9, 101);
        var tau = RFNetwork.GroupDelay(f.Select(x => Line(x, -3e-10)).ToArray(), f);
        Assert.All(tau, t => Assert.Equal(-3e-10, t, 1e-16));
    }

    /// <summary>
    /// Degenerate inputs return zeros rather than infinities or exceptions: a sweep of one point has
    /// no derivative, and a repeated frequency is a defect in the FILE, not a reason to hand an
    /// infinity to an autoscale that will then take the whole plot with it.
    /// </summary>
    [Fact]
    public void DegenerateSweeps_ReturnZeros_NeverInfinities()
    {
        Assert.Empty(RFNetwork.GroupDelay(Array.Empty<Complex>(), Array.Empty<double>()));
        Assert.Equal([0.0], RFNetwork.GroupDelay([Complex.One], [1e9]));

        double[] repeated = [1e9, 1e9, 1e9];
        var tau = RFNetwork.GroupDelay(repeated.Select(x => Line(x, 1e-9)).ToArray(), repeated);
        Assert.All(tau, t => Assert.Equal(0.0, t));
    }

    /// <summary>
    /// The matrix overload reads the element it is asked for, and it is S21 (row 1, col 0) that a
    /// transmission delay lives in — reading S12 of an asymmetric network gives a different answer,
    /// which is what makes the index selection worth asserting.
    /// </summary>
    [Fact]
    public void TheMatrixOverload_ReadsTheElementItIsGiven()
    {
        var f = Linspace(1e9, 3e9, 201);
        var mats = f.Select(x =>
        {
            var m = new Mat<Complex>(2, 2);
            m[0, 0] = new Complex(0.1, 0.0);
            m[0, 1] = Line(x, 4e-9);        // reverse path: 4 ns
            m[1, 0] = Line(x, 1e-9);        // forward path: 1 ns
            m[1, 1] = new Complex(0.2, 0.0);
            return m;
        }).ToArray();

        Assert.All(RFNetwork.GroupDelay(mats, f, 1, 0), t => Assert.Equal(1e-9, t, 1e-15));
        Assert.All(RFNetwork.GroupDelay(mats, f, 0, 1), t => Assert.Equal(4e-9, t, 1e-15));

        Assert.Throws<ArgumentOutOfRangeException>(() => RFNetwork.GroupDelay(mats, f, 2, 0));
    }

    /// <summary>
    /// <b>The adapter renormalizes to a uniform REAL reference before reading the phase</b>, exactly
    /// like every other 2-port metric here (R-stb-2).
    /// </summary>
    /// <remarks>
    /// <b>Group delay is NOT reference-independent</b>, which is the whole reason this matters: S21's
    /// phase is defined against the terminations, so the same physical part read against 50 ohms and
    /// against 75 ohms has genuinely different group delays. What the adapter promises is narrower
    /// and checkable — a cube referenced PER-PORT, or to a complex impedance, is brought to the
    /// uniform real reference of its INPUT port first, so the phase it differentiates is a phase that
    /// means something. The oracle is doing that renormalization by hand and reading the result
    /// directly; the guard below proves the fixture actually needs it, since an adapter that skipped
    /// the step would agree with a fixture that was already uniform.
    /// </remarks>
    [Fact]
    public void TheCubeAdapter_RenormalizesToAUniformRealReference()
    {
        var f = Linspace(1e9, 4e9, 301);

        // A line, but expressed against genuinely mixed per-port references: port 1 real 50, port 2
        // COMPLEX. Nothing in the group-delay formula would work on this as it stands.
        Complex[] mixed = [new(50, 0), new(75, -20)];
        var raw = f.Select(x =>
        {
            var m = new Mat<Complex>(2, 2);
            // The reflections are frequency-DEPENDENT on purpose. Renormalization mixes S11/S22
            // into S21, so a fixture whose reflections are constants would come back with the same
            // group delay either way — a change of reference would add a constant phase and a
            // constant phase has no derivative.
            m[0, 0] = 0.30 * Line(x, 3e-10);
            m[0, 1] = 0.90 * Line(x, 8e-10);
            m[1, 0] = 0.90 * Line(x, 8e-10);
            m[1, 1] = 0.25 * Line(x, 5e-10);
            return m;
        }).ToArray();

        Complex[] uniform = [new(50, 0), new(50, 0)];
        var byHand = RFNetwork.GroupDelay(
            raw.Select(m => RFNetwork.SToS(m, mixed, uniform)).ToArray(), f, 1, 0);

        var viaAdapter = NetworkMetrics.GroupDelay(raw, mixed, f, 1, 2);

        for (int i = 0; i < viaAdapter.Length; i++)
            Assert.Equal(byHand[i], viaAdapter[i], 1e-18);

        // The fixture must actually need renormalizing, or the agreement above is vacuous.
        var unrenormalized = RFNetwork.GroupDelay(raw, f, 1, 0);
        Assert.True(unrenormalized.Zip(byHand).Any(p => Math.Abs(p.First - p.Second) > 1e-12),
                    "the fixture must differ before and after renormalization, or it proves nothing");
    }

    /// <summary>Swapping the ordered port pair reads the OTHER path's delay, on an asymmetric part.</summary>
    [Fact]
    public void ThePortPairIsOrdered()
    {
        var f = Linspace(1e9, 3e9, 201);
        var mats = f.Select(x =>
        {
            var m = new Mat<Complex>(2, 2);
            m[0, 0] = new Complex(0.05, 0.0);
            m[0, 1] = 0.5 * Line(x, 6e-10);
            m[1, 0] = 0.5 * Line(x, 2e-10);
            m[1, 1] = new Complex(0.05, 0.0);
            return m;
        }).ToArray();
        Complex[] z50 = [new(50, 0), new(50, 0)];

        Assert.All(NetworkMetrics.GroupDelay(mats, z50, f, 1, 2), t => Assert.Equal(2e-10, t, 1e-15));
        Assert.All(NetworkMetrics.GroupDelay(mats, z50, f, 2, 1), t => Assert.Equal(6e-10, t, 1e-15));
    }
}
