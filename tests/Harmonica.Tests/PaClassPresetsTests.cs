using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// R9D §3.8 — the physics gate for the five PA-class presets. Source: Sharma, T. (2018). Modelling
/// and Design Methodology of Higher-Efficiency Harmonic Tuned Power Amplifiers for 5G Applications
/// (Doctoral thesis, University of Calgary). https://prism.ucalgary.ca/handle/1880/106695.
/// </summary>
public sealed class PaClassPresetsTests(ITestOutputHelper output)
{
    private const double F0 = 2e9;
    private const double Z0 = 80.0;

    private static CircuitModel Model(LumpedPackage package, DutCapacitances caps, int k = 5) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
            },
            Capacitances = caps,
        },
        Embedding = new EmbeddingStack { Package = package },
        Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings  = new HarmonicaSettings { HarmonicCount = k, FrequencyHz = F0, Z0 = Z0 },
    };

    // ── the five pinned tables, at Z0 = 80 ───────────────────────────────────

    /// <summary>Round 10 (owner): a preset's "short"/"open" bands are Z0/100 and Z0·100, not the
    /// absolute 1e-6 Ω / 1e6 Ω they used to be — "this helps with convergence for Class F and F⁻¹ and
    /// makes nice looking contours". Pinned explicitly because every other assertion in this file
    /// spells them through NearShort/NearOpen and would pass for any definition of those.</summary>
    [Fact]
    public void TheShortAndOpenBands_AreZ0OverAHundred_AndZ0TimesAHundred()
    {
        Assert.Equal(Z0 / 100.0, PaClassPresets.NearShort(Z0), precision: 12);
        Assert.Equal(Z0 * 100.0, PaClassPresets.NearOpen(Z0),  precision: 12);
        Assert.Equal(0.8,  PaClassPresets.IntrinsicLoad(PaClass.F, 2, Z0).Real, precision: 12);
        Assert.Equal(8000, PaClassPresets.IntrinsicLoad(PaClass.F, 3, Z0).Real, precision: 12);

        // An UNMARKED band is not written by a preset at all and keeps harmonicaRF's own 1e-6 —
        // the owner's own carve-out, and the reason that constant is untouched.
        Assert.Equal(1e-6, TerminationSet.UnmarkedBandOhms);
    }

    [Fact]
    public void ClassB_Z1IsZ0_AndEveryOtherBandIsNearShort()
    {
        Assert.Equal(new Complex(Z0, 0), PaClassPresets.IntrinsicLoad(PaClass.B, 1, Z0));
        for (int band = 2; band <= 5; band++)
            Assert.Equal(new Complex(PaClassPresets.NearShort(Z0), 0),
                         PaClassPresets.IntrinsicLoad(PaClass.B, band, Z0));
    }

    [Fact]
    public void ClassJ_Band1And2MatchTheClosedForm()
    {
        var z1 = PaClassPresets.IntrinsicLoad(PaClass.J, 1, Z0);
        Assert.Equal(80.0, z1.Real, precision: 9);
        Assert.Equal(-40.0, z1.Imaginary, precision: 9);

        var z2 = PaClassPresets.IntrinsicLoad(PaClass.J, 2, Z0);
        double expectedZ2Imag = 3.0 * Math.PI * 0.5 / 8.0 * Z0;
        Assert.Equal(0.0, z2.Real, precision: 9);
        Assert.Equal(expectedZ2Imag, z2.Imaginary, precision: 6);
        output.WriteLine($"Class J: ZL1={z1}, ZL2={z2} (expected imag ≈ 47.12)");
        Assert.Equal(47.1238898, z2.Imaginary, precision: 5);

        for (int band = 3; band <= 5; band++)
            Assert.Equal(new Complex(PaClassPresets.NearShort(Z0), 0),
                         PaClassPresets.IntrinsicLoad(PaClass.J, band, Z0));
    }

    [Fact]
    public void ClassJStar_IsTheComplexConjugateOfClassJ_BandByBand()
    {
        var z1 = PaClassPresets.IntrinsicLoad(PaClass.JStar, 1, Z0);
        Assert.Equal(80.0, z1.Real, precision: 9);
        Assert.Equal(40.0, z1.Imaginary, precision: 9);

        var z2 = PaClassPresets.IntrinsicLoad(PaClass.JStar, 2, Z0);
        Assert.Equal(0.0, z2.Real, precision: 9);
        Assert.Equal(-47.1238898, z2.Imaginary, precision: 5);

        for (int band = 1; band <= 5; band++)
        {
            var j     = PaClassPresets.IntrinsicLoad(PaClass.J,     band, Z0);
            var jStar = PaClassPresets.IntrinsicLoad(PaClass.JStar, band, Z0);
            Assert.Equal(Complex.Conjugate(j).Real,      jStar.Real,      precision: 9);
            Assert.Equal(Complex.Conjugate(j).Imaginary, jStar.Imaginary, precision: 9);
        }
    }

    [Fact]
    public void ClassF_Band1IsTwoZ0OverSqrt3_EvenNearShort_OddNearOpen()
    {
        var z1 = PaClassPresets.IntrinsicLoad(PaClass.F, 1, Z0);
        Assert.Equal(2.0 * Z0 / Math.Sqrt(3), z1.Real, precision: 3);
        output.WriteLine($"Class F: ZL1={z1} (expected ≈ 92.376)");
        Assert.Equal(92.376, z1.Real, precision: 3);
        Assert.Equal(0.0, z1.Imaginary, precision: 9);

        Assert.Equal(new Complex(PaClassPresets.NearShort(Z0), 0), PaClassPresets.IntrinsicLoad(PaClass.F, 2, Z0));
        Assert.Equal(new Complex(PaClassPresets.NearOpen(Z0),  0), PaClassPresets.IntrinsicLoad(PaClass.F, 3, Z0));
        Assert.Equal(new Complex(PaClassPresets.NearShort(Z0), 0), PaClassPresets.IntrinsicLoad(PaClass.F, 4, Z0));
        Assert.Equal(new Complex(PaClassPresets.NearOpen(Z0),  0), PaClassPresets.IntrinsicLoad(PaClass.F, 5, Z0));
    }

    [Fact]
    public void ClassFInverse_Band1MatchesTheClosedForm_EvenNearOpen_OddNearShort()
    {
        double expected = Math.Sqrt(2) * Z0 / 2 / (0.5 - 8.0 / 9.0 / Math.PI / Math.PI);
        var z1 = PaClassPresets.IntrinsicLoad(PaClass.FInverse, 1, Z0);
        output.WriteLine($"Class F-1: ZL1={z1} (expected ≈ 137.99)");
        Assert.Equal(expected, z1.Real, precision: 9);
        Assert.Equal(137.99, z1.Real, precision: 2);
        Assert.Equal(0.0, z1.Imaginary, precision: 9);

        Assert.Equal(new Complex(PaClassPresets.NearOpen(Z0),  0), PaClassPresets.IntrinsicLoad(PaClass.FInverse, 2, Z0));
        Assert.Equal(new Complex(PaClassPresets.NearShort(Z0), 0), PaClassPresets.IntrinsicLoad(PaClass.FInverse, 3, Z0));
        Assert.Equal(new Complex(PaClassPresets.NearOpen(Z0),  0), PaClassPresets.IntrinsicLoad(PaClass.FInverse, 4, Z0));
        Assert.Equal(new Complex(PaClassPresets.NearShort(Z0), 0), PaClassPresets.IntrinsicLoad(PaClass.FInverse, 5, Z0));
    }

    // ── the identity check: an empty package/no capacitors makes ExtrinsicFor the identity ─────────

    [Theory]
    [InlineData(PaClass.B)]
    [InlineData(PaClass.J)]
    [InlineData(PaClass.JStar)]
    [InlineData(PaClass.F)]
    [InlineData(PaClass.FInverse)]
    public void Identity_NoCapacitancesNoPackage_ExtrinsicEqualsIntrinsicTableExactly(PaClass paClass)
    {
        var model = Model(LumpedPackage.None, DutCapacitances.None);
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out string why), why);

        for (int band = 1; band <= model.Settings.HarmonicCount; band++)
        {
            var zIntr = PaClassPresets.IntrinsicLoad(paClass, band, Z0);
            var zExt  = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Load, band, zIntr);
            Assert.True(Complex.Abs(zExt - zIntr) < 1e-9,
                $"band {band}: intrinsic {zIntr}, extrinsic {zExt}");
        }
    }

    // ── the round trip: a real Cds and a real Ld, independently inverted ────────────────────────────

    [Fact]
    public void RoundTrip_RealCdsAndLd_ForwardFormRecoversTheIntrinsicValue()
    {
        double cds = 0.6e-12, rd = 1.2, ld = 0.25e-9, cpd = 0.0;
        var package = LumpedPackage.None with { Rd = rd, Ld = ld, Cpd = cpd };
        var caps = DutCapacitances.None with { Cds = new DutCapacitance { Farads = cds } };
        var model = Model(package, caps, k: 1);
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out string why), why);

        var zIntrTarget = PaClassPresets.IntrinsicLoad(PaClass.J, 1, Z0);
        var zExt = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Load, 1, zIntrTarget);

        // Independently derived (never calls IntrinsicAbcd.Chain): the same series/shunt combination
        // IntrinsicAbcdTests's own hand oracles use, applied to the LOAD side's Rd/Ld/Cds instead of
        // the source side's Rg/Lg/Cgs — this is the "(A·Z_ext + B)/(C·Z_ext + D)" forward form's
        // network-equivalent, computed from the raw model values rather than by re-calling the map
        // under test.
        double omega = 2.0 * Math.PI * model.Settings.FrequencyHz;
        var zSeries = new Complex(rd, omega * ld);
        var zAtDrainBeforeCds = zExt + zSeries;
        var zCds = Complex.One / new Complex(0, omega * cds);
        var zIntrRecovered = Complex.One / (Complex.One / zAtDrainBeforeCds + Complex.One / zCds);

        double residual = Complex.Abs(zIntrRecovered - zIntrTarget);
        output.WriteLine($"round-trip residual = {residual:E3}");
        Assert.True(residual < 1e-9, $"target {zIntrTarget}, recovered {zIntrRecovered}, residual {residual:E3}");
    }
}
