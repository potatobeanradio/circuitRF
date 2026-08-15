using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// R8C §3.3 — the intrinsic source impedance must pick up rgs for free, through the converged
/// Jacobian, with no hand-written expression for it anywhere. Gated, not asserted: a Tier-1 fixture
/// (linear Cgs, no package, no Cdg) where the closed form is available by hand:
/// <code>Z_S,intr = Z_source ∥ (rgs + 1/(jωCgs))</code>
/// </summary>
public sealed class IntrinsicRgsTests(ITestOutputHelper output)
{
    private const double F0 = 2e9;
    private const double CgsFarads = 1e-12;
    private const double ChokeH = 1e-6;
    private const double BlockF = 1e-9;

    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>The passive network the marker actually presents at the plane: choke in parallel with
    /// the blocked marker (matching IntrinsicPlaneTests' own oracle) — the raw termination impedance
    /// alone is not what the plane looks like.</summary>
    private static Complex PlaneImpedance(Complex z, double omega)
        => Complex.One / (Complex.One / new Complex(0, omega * ChokeH)
                         + Complex.One / (z + Complex.One / new Complex(0, omega * BlockF)));

    private static CircuitModel Model(double rgsOhms) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
            },
            Capacitances = new DutCapacitances
            {
                Cgs = new DutCapacitance { Farads = CgsFarads },
                RgsOhms = rgsOhms,
            },
        },
        Embedding = new EmbeddingStack { Package = LumpedPackage.None },
        Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings  = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = F0,
            BiasChokeHenries = ChokeH, DcBlockFarads = BlockF, Tol = 1e-9,
        },
    };

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.0)]
    [InlineData(25.0)]
    public void SourceImpedance_PicksUpRgs_ThroughTheJacobian_NoHandWrittenExpression(double rgsOhms)
    {
        var model = Model(rgsOhms);
        var zs = new Complex(40, 15);
        var zl = new Complex(30, -10);

        var terms = new TerminationSet(model.Settings.HarmonicCount);
        for (int h = 1; h <= model.Settings.HarmonicCount; h++)
        {
            terms.Set(TerminationSide.Source, h, zs);
            terms.Set(TerminationSide.Load,   h, zl);
        }

        var ctx = HarmonicaContext.Create(model, Settings);
        var pt  = ctx.Solve(terms, pavlDbm: -20);
        Assert.True(pt.Converged, $"‖F‖ = {pt.Residual:E3}");

        var iv = HarmonicaDataSet.Intrinsic(ctx, pt);
        var measured = iv.Z[(int)TerminationSide.Source, 1];

        double omega = 2.0 * Math.PI * F0;
        var zSourcePlane = PlaneImpedance(zs, omega);
        var zCgsBranch = new Complex(rgsOhms, 0) + Complex.One / new Complex(0, omega * CgsFarads);
        var oracle = Complex.One / (Complex.One / zSourcePlane + Complex.One / zCgsBranch);

        output.WriteLine($"rgs={rgsOhms} Ω: measured={measured}, oracle={oracle}, " +
                          $"diff={Complex.Abs(measured - oracle):E3}");
        Assert.True(Complex.Abs(measured - oracle) < 1e-9 * oracle.Magnitude + 1e-9,
            $"rgs={rgsOhms}: measured {measured} vs oracle {oracle}");
    }

    [Fact]
    public void Rgs_Zero_EmitsNoRgsElement_ReproducingThePreChangeNetlist()
    {
        string text = HarmonicaNetlist.Build(Model(0.0)).Text;
        Assert.DoesNotContain("RGS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("n_rgs", text, StringComparison.Ordinal);
        Assert.Contains("CGS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rgs_NonZero_EmitsTheSeriesResistorAndRewiresCgsThroughItsNode()
    {
        string text = HarmonicaNetlist.Build(Model(15.0)).Text;
        Assert.Contains("R:RGS", text, StringComparison.Ordinal);
        Assert.Contains("n_rgs", text, StringComparison.Ordinal);
        Assert.Contains("CGS  n_rgs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IntrinsicGatePort_IsUnchangedByRgs()
    {
        // R8C §3.2 — n_rgs is an internal node of a branch that sits in PARALLEL with the SDD's own
        // gate port, not between the SDD and the gate: IntrinsicPortMap locates the SDD's own ports,
        // which rgs must not move.
        var withoutRgs = HarmonicaContext.Create(Model(0.0), Settings);
        var withRgs    = HarmonicaContext.Create(Model(25.0), Settings);
        Assert.Equal(withoutRgs.IntrinsicPorts.GatePort, withRgs.IntrinsicPorts.GatePort);
    }

    // ── R8C §3.1 — .charm round trip ─────────────────────────────────────────

    [Fact]
    public void Rgs_Zero_RoundTripsAndWritesNoFieldAtAllByteForByte()
    {
        var model = Model(0.0);
        string before = CharmIo.Write(model);
        Assert.DoesNotContain("\"Rgs\"", before, StringComparison.Ordinal);

        var back = CharmIo.Read(before, null, out var unresolved);
        Assert.Empty(unresolved);
        Assert.Equal(0.0, back.Dut.Capacitances.RgsOhms);

        Assert.Equal(before, CharmIo.Write(back));
    }

    [Fact]
    public void Rgs_NonZero_RoundTrips()
    {
        var model = Model(17.5);
        string json = CharmIo.Write(model);
        var back = CharmIo.Read(json, null, out var unresolved);

        Assert.Empty(unresolved);
        Assert.Equal(17.5, back.Dut.Capacitances.RgsOhms);
    }

}
