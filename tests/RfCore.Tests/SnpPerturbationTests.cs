// Gates for brief-sens-2 R-sens2-2 / R-sens2-3: the one function that applies an SnP block's entry offsets, and the
// reciprocity measurement that decides Reciprocal=auto.

using System;
using System.IO;
using System.Numerics;
using NumFlat;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public sealed class SnpPerturbationTests
{
    private static Mat<Complex> TwoPort()
    {
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = new Complex(0.1, 0.05);
        s[0, 1] = new Complex(0.7, -0.3);
        s[1, 0] = new Complex(0.7, -0.3);
        s[1, 1] = new Complex(-0.2, 0.1);
        return s;
    }

    // Mat<T> is a struct over its storage: "the same instance" is the same Memory, nothing copied.
    [Fact]
    public void ZeroOffsets_ReturnTheSameMatrixInstance()
    {
        var s = TwoPort();
        Assert.True(s.Memory.Equals(SnpPerturbation.Apply(s, null, 1e9).Memory));
        var zeros = SnpPerturbation.Build(2, tied: true, [("SdB_2_1", 0.0), ("Sdeg_2_1", 0.0)], out var refusal);
        Assert.Null(refusal);
        Assert.True(s.Memory.Equals(SnpPerturbation.Apply(s, zeros, 1e9).Memory));
        Assert.False(s.Memory.Equals(SnpPerturbation.Apply(s, SnpPerturbation.Build(2, true, [("SdB_2_1", 1.0)], out _), 1e9).Memory));
    }

    [Fact]
    public void OneDbAndTenDegreesOnS21_OfATiedTwoPort_MovesS12Identically_AndNothingElse()
    {
        var s = TwoPort();
        var o = SnpPerturbation.Build(2, tied: true, [("SdB_2_1", 1.0), ("Sdeg_2_1", 10.0)], out var refusal);
        Assert.Null(refusal);
        var r = SnpPerturbation.Apply(s, o, 2e9);

        var k = Complex.FromPolarCoordinates(Math.Pow(10, 1.0 / 20), 10 * Math.PI / 180);
        Assert.Equal(s[1, 0] * k, r[1, 0]);
        Assert.Equal(s[0, 1] * k, r[0, 1]);
        Assert.Equal(s[0, 0], r[0, 0]);
        Assert.Equal(s[1, 1], r[1, 1]);
    }

    [Fact]
    public void AtZeroHertz_OnlyTheMagnitudeOffsetApplies()
    {
        var s = TwoPort();
        var o = SnpPerturbation.Build(2, tied: true, [("SdB_2_1", 1.0), ("Sdeg_2_1", 10.0)], out _);
        var r = SnpPerturbation.Apply(s, o, 0);
        Assert.Equal(s[1, 0] * Math.Pow(10, 1.0 / 20), r[1, 0]);
    }
}

public sealed class SnpReciprocityTests
{
    private static string Example(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "examples", relative);
    }

    [Fact]
    public void TheShippedAmplifier_ResolvesUntied_AndAShippedSpiral_Tied()
    {
        var amp = TouchstoneCache.Get(Example("S-Parameters/Amplifier/schematic/potentially_unstable_amp.s2p"));
        Assert.False(SnpPerturbation.ResolveTie(amp, SnpReciprocal.Auto).Tied);

        var spiral = TouchstoneCache.Get(Example("Model Composer/passive-spiral/s/spiral_3.5t.s2p"));
        Assert.True(SnpPerturbation.ResolveTie(spiral, SnpReciprocal.Auto).Tied);
    }
}
