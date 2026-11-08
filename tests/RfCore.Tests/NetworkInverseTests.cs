// Gates for brief-de-1 §1: the network that undoes a 2K-port.

using System;
using System.Numerics;
using NumFlat;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public sealed class NetworkInverseTests
{
    /// <summary>A random complex 2K-port, symmetric (reciprocal) and scaled to be passive.</summary>
    internal static Mat<Complex> RandomNetwork(int ports, int seed)
    {
        var rng = new Random(seed);
        var a = new Mat<Complex>(ports, ports);
        for (int i = 0; i < ports; i++)
            for (int j = 0; j <= i; j++)
                a[i, j] = a[j, i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        // Divide by more than the largest singular value's bound so every |S| stays well inside the unit circle.
        double bound = 0;
        for (int i = 0; i < ports; i++) for (int j = 0; j < ports; j++) bound += a[i, j].Magnitude;
        for (int i = 0; i < ports; i++) for (int j = 0; j < ports; j++) a[i, j] /= 0.6 * bound / ports;
        return a;
    }

    internal static Mat<Complex> Through(int k)
    {
        var t = new Mat<Complex>(2 * k, 2 * k);
        for (int i = 0; i < k; i++) { t[i, i + k] = Complex.One; t[i + k, i] = Complex.One; }
        return t;
    }

    internal static double MaxDiff(Mat<Complex> x, Mat<Complex> y)
    {
        double m = 0;
        for (int i = 0; i < x.RowCount; i++)
            for (int j = 0; j < x.ColCount; j++) m = Math.Max(m, (x[i, j] - y[i, j]).Magnitude);
        return m;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheInverse_CascadedWithTheNetwork_InEitherOrder_IsAThroughConnection(int k)
    {
        var f = RandomNetwork(2 * k, seed: 17 + k);
        Assert.True(NetworkInverse.TryInvert(f, out var d, out var refusal), refusal);
        Assert.True(MaxDiff(NetworkInverse.Cascade(d, f), Through(k)) <= 1e-12);
        Assert.True(MaxDiff(NetworkInverse.Cascade(f, d), Through(k)) <= 1e-12);
    }

    [Fact]
    public void ANetworkWithAPortThatReachesNothing_IsRefused()
    {
        var f = RandomNetwork(4, seed: 5);
        for (int j = 0; j < 4; j++) { if (j != 0) { f[0, j] = 0; f[j, 0] = 0; } }   // port 1 is a one-port of its own
        Assert.False(NetworkInverse.TryInverseCascade(f, out _, out var refusal));
        Assert.Contains("does not reach the other", refusal);
    }

    // A series 100 Ω in a 50 Ω system: det S = 0. The series −100 Ω that undoes it has a cascade matrix but no S-matrix, because
    // it shorts the two reference terminations into each other. Why the stamp uses the cascade form (brief-de-2).
    [Fact]
    public void ASeries100OhmIn50Ohm_HasAnInverseCascade_ButNoInverseSMatrix()
    {
        var f = new Mat<Complex>(2, 2);
        f[0, 0] = f[1, 1] = 0.5;
        f[0, 1] = f[1, 0] = 0.5;
        Assert.True(NetworkInverse.TryInverseCascade(f, out var t, out _));
        Assert.True(NetworkInverse.TryToCascade(f, out var tf, out _));
        Assert.True(MaxDiff(t * tf, NetworkInverse.Identity(2)) <= 1e-12);
        Assert.False(NetworkInverse.TryInvert(f, out _, out var refusal));
        Assert.Contains("no S-matrix", refusal);
    }

    [Fact]
    public void AnOddPortCount_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => NetworkInverse.TryInvert(RandomNetwork(3, 1), out _, out _));
        Assert.Contains("even number of ports", ex.Message);
    }
}
