// ================================================================
//  TouchstoneNoiseBlockTests.cs
//
//  A measured two-port amplifier file carries a noise-parameter block after its network data,
//  whose start Touchstone 1.x marks only by a frequency that does not advance. Field report,
//  2026-09-28: such a file (tab separated, CRLF) was refused outright — its first noise row was
//  folded into the next network block and reported as "Token overflow: got 10 tokens, expected 9".
//  And the fixture-inference identity railRF now uses to read a vendor part file.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public sealed class TouchstoneNoiseBlockTests
{
    private const string AmplifierWithNoise =
        "! a measured two-port amplifier\r\n" +
        "# Hz S dB R 50\r\n" +
        "100000000\t-0.5\t-10\t15\t170\t-40\t80\t-1\t-20\r\n" +
        "200000000\t-0.6\t-20\t14\t160\t-39\t75\t-1.1\t-25\r\n" +
        "300000000\t-0.7\t-30\t13\t150\t-38\t70\t-1.2\t-30\r\n" +
        "\r\n" +
        "!Noise Parameters\r\n" +
        "!Freq  NFmin(dB)  Mag(GammaOpt)  Ang(GammaOpt)  rn\r\n" +
        "100000000   0.74   0.46   54.9   0.167\r\n" +
        "200000000   0.75   0.48   55.2   0.152\r\n";

    [Fact]
    public void ANoiseBlockAfterTheNetworkDataIsReadAsNoiseNotAsANetworkBlock()
    {
        var snp = TouchstoneIO.Read(new StringReader(AmplifierWithNoise), knownPorts: 2);

        Assert.Equal(new[] { 1e8, 2e8, 3e8 }, snp.Frequencies);
        Assert.Equal(15, 20 * Math.Log10(snp.Matrices[0][1, 0].Magnitude), 9);   // S21 of row 1

        Assert.Equal(2, snp.NoiseParameters.Count);
        var n = snp.NoiseParameters[0];
        Assert.Equal(1e8, n.FrequencyHz);
        Assert.Equal(0.74, n.NFminDb);
        Assert.Equal(0.46, n.GammaOpt.Magnitude, 12);
        Assert.Equal(54.9, n.GammaOpt.Phase * 180 / Math.PI, 9);
        Assert.Equal(0.167, n.RnNormalized);
    }

    [Fact]
    public void TheExplicitNoiseKeywordStartsTheBlockToo()
    {
        string text = AmplifierWithNoise.Replace("!Noise Parameters", "[Noise Data]");
        var snp = TouchstoneIO.Read(new StringReader(text), knownPorts: 2);
        Assert.Equal(3, snp.FrequencyCount);
        Assert.Equal(2, snp.NoiseParameters.Count);
    }

    [Theory]
    [InlineData(50.0, false)]
    [InlineData(25.0, false)]
    [InlineData(25.0, true)]    // strict 1.1: a complex Z0 is renormalised to its real part, the option line's R
    public void TheNoiseBlockIsWrittenAfterTheDataAndReadsBack(double r, bool strict)
    {
        double[] f = [1e9, 2e9, 3e9];
        var snp = new SNP(f, f.Select((_, k) => Amp(k)).ToArray(), z0: new Complex(r, strict ? 5 : 0));
        snp.NoiseParameters.AddRange(f.Select((hz, k) =>
            new NoiseParameterPoint(hz, 0.5 + 0.1 * k, Complex.FromPolarCoordinates(0.3 + 0.1 * k, k - 1.0), 0.2 + 0.05 * k)));

        var text = new StringWriter();
        TouchstoneIO.Write(snp, text, touchstone11Compatible: strict);
        var back = TouchstoneIO.Read(new StringReader(text.ToString()), knownPorts: 2);

        Assert.Equal(r, back.Z0.Real);
        Assert.Equal(f, back.Frequencies);
        Assert.Equal(snp.NoiseParameters.Count, back.NoiseParameters.Count);
        foreach (var (a, b) in snp.NoiseParameters.Zip(back.NoiseParameters))
        {
            Assert.Equal(a.FrequencyHz, b.FrequencyHz);
            Assert.Equal(a.NFminDb, b.NFminDb, 1e-9);
            Assert.True((a.GammaOpt - b.GammaOpt).Magnitude < 1e-9);
            Assert.Equal(a.RnNormalized, b.RnNormalized, 1e-9);
        }
        if (!strict)
            Assert.True((snp.Matrices[2][1, 0] - back.Matrices[2][1, 0]).Magnitude < 1e-8);
    }

    [Fact]
    public void ARunsNoiseCubesBecomeTheNoiseBlockReferencedToTheOptionLineR()
    {
        double[] f = [1e9, 2e9];
        var freq = new Axis("freq", f, "Hz");
        var pi = new Axis("i", [1.0, 2.0], "port");
        var pj = new Axis("j", [1.0, 2.0], "port");
        var s = f.SelectMany((_, k) => { var m = Amp(k); return new[] { m[0, 0], m[0, 1], m[1, 0], m[1, 1] }; }).ToArray();
        Complex[] gamma = [Complex.FromPolarCoordinates(0.4, 1.0), Complex.FromPolarCoordinates(0.5, 1.2)];

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube([freq, pi, pj], s));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube(new Complex[] { 25, 25 }));
        ds.AddToGroup("SP1", "NFmin", new DataCube([freq], new[] { 0.8, 0.9 }));
        ds.AddToGroup("SP1", "GammaOpt", new DataCube([freq], gamma));
        ds.AddToGroup("SP1", "Rn", new DataCube([freq], new[] { 10.0, 12.5 }));

        var snp = DataSetBuilder.ToSnp(ds, "SP1.S");
        Assert.Equal(new NoiseParameterPoint(1e9, 0.8, gamma[0], 10.0 / 25), snp.NoiseParameters[0]);
        Assert.Equal(new NoiseParameterPoint(2e9, 0.9, gamma[1], 0.5), snp.NoiseParameters[1]);

        var text = new StringWriter();
        TouchstoneIO.Write(snp, text);
        Assert.Contains("S RI R 25", text.ToString());
        Assert.Equal(2, TouchstoneIO.Read(new StringReader(text.ToString()), knownPorts: 2).NoiseParameters.Count);

        // Without all three cubes there is no noise block.
        ds.RemoveFromGroup("SP1", "Rn");
        Assert.Empty(DataSetBuilder.ToSnp(ds, "SP1.S").NoiseParameters);
    }

    private static Mat<Complex> Amp(int k)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = new Complex(0.3, -0.1 * k);
        m[0, 1] = new Complex(0.02, 0.01);
        m[1, 0] = Complex.FromPolarCoordinates(4 + k, 2.0 - k);
        m[1, 1] = new Complex(-0.2, 0.3);
        return m;
    }

    [Fact]
    public void TheFixtureIsReadOffTheSMatrix()
    {
        var z0 = new Complex(50, 0);
        double[] f = [1e6, 1e7, 1e8];
        Mat<Complex> M(Complex s11, Complex s21)
        {
            var m = new Mat<Complex>(2, 2);
            m[0, 0] = s11; m[0, 1] = s21; m[1, 0] = s21; m[1, 1] = s11;
            return m;
        }
        Complex Z(double hz) => new(0.01, -1.0 / (2 * Math.PI * hz * 100e-9));

        var series = new SNP(f, f.Select(hz => M(Z(hz) / (Z(hz) + 2 * z0), 2 * z0 / (Z(hz) + 2 * z0))).ToArray(), z0: z0);
        var shunt  = new SNP(f, f.Select(hz => M(-z0 / (2 * Z(hz) + z0), 2 * Z(hz) / (2 * Z(hz) + z0))).ToArray(), z0: z0);
        var amp    = TouchstoneIO.Read(new StringReader(AmplifierWithNoise), knownPorts: 2);

        Assert.Equal(PassiveExtraction.SeriesThrough, PassiveMetrics.InferExtraction(series));
        Assert.Equal(PassiveExtraction.ShuntThrough,  PassiveMetrics.InferExtraction(shunt));
        Assert.Null(PassiveMetrics.InferExtraction(amp));      // not a two-terminal part
    }
}
