using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// R8C §5.4 — the closed-form ABCD back-calculation <see cref="IntrinsicAbcd"/> replaces the intrinsic
/// drag's inverse solve with. Every oracle here is HAND-DERIVED, never computed with another
/// circuitRF path (§5.4's own rule): the whole point of item 3 is to check the ABCD chain and the
/// netlist agree about what the circuit is, which a shared derivation could not do.
/// </summary>
public sealed class IntrinsicAbcdTests(ITestOutputHelper output)
{
    private const double F0 = 2e9;

    private static double Omega(int band) => 2.0 * Math.PI * F0 * band;

    private static CircuitModel Model(LumpedPackage package, DutCapacitances caps) => new()
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
        Settings  = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = F0,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-9,
        },
    };

    // ── 1. Identity ──────────────────────────────────────────────────────────

    [Fact]
    public void Identity_NoPackageNoCapacitorsNoRgs_ChainIsIdentity()
    {
        var model = Model(LumpedPackage.None, DutCapacitances.None);
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out _));

        foreach (var z in new[] { new Complex(50, 0), new Complex(12, -30), new Complex(1, 500) })
        {
            var ext = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, z);
            Assert.True(Complex.Abs(ext - z) < 1e-12, $"source: {ext} vs {z}");
            var extL = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Load, 1, z);
            Assert.True(Complex.Abs(extL - z) < 1e-12, $"load: {extL} vs {z}");
        }
    }

    // ── 2. Source, one element at a time ────────────────────────────────────

    [Fact]
    public void Source_CgsOnly_MatchesHandExpression()
    {
        double cgs = 1e-12;
        var model = Model(LumpedPackage.None,
            DutCapacitances.None with { Cgs = new DutCapacitance { Farads = cgs } });

        double omega = Omega(1);
        var zExt = new Complex(60, 20);
        // Z_intr = Z_ext ∥ 1/(jωCgs)
        var zIntrExpected = Complex.One /
            (Complex.One / zExt + new Complex(0, omega * cgs));

        var zExtBack = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, zIntrExpected);
        Assert.True(Complex.Abs(zExtBack - zExt) < 1e-9 * zExt.Magnitude,
            $"{zExtBack} vs {zExt}");
    }

    [Fact]
    public void Source_RgsPlusCgs_MatchesHandExpression()
    {
        double cgs = 1e-12, rgs = 15.0;
        var model = Model(LumpedPackage.None,
            DutCapacitances.None with { Cgs = new DutCapacitance { Farads = cgs }, RgsOhms = rgs });

        double omega = Omega(1);
        var zExt = new Complex(60, 20);
        var zBranch = new Complex(rgs, 0) + Complex.One / new Complex(0, omega * cgs);
        // Z_intr = Z_ext ∥ (rgs + 1/(jωCgs))
        var zIntrExpected = Complex.One / (Complex.One / zExt + Complex.One / zBranch);

        var zExtBack = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, zIntrExpected);
        Assert.True(Complex.Abs(zExtBack - zExt) < 1e-9 * zExt.Magnitude,
            $"{zExtBack} vs {zExt}");
    }

    [Theory]
    [InlineData(true, false, false)]   // + Rg/Lg (Rg only)
    [InlineData(true, true, false)]    // + Rg/Lg + Lg
    [InlineData(true, true, true)]     // + Rg/Lg + Lg + Cpg
    public void Source_AddingRgLgCpg_MatchesHandExpression(bool withRg, bool withLg, bool withCpg)
    {
        double cgs = 1e-12, rgs = 15.0;
        double rg = withRg ? 3.0 : 0.0;
        double lg = withLg ? 0.5e-9 : 0.0;
        double cpg = withCpg ? 0.3e-12 : 0.0;

        var package = LumpedPackage.None with { Rg = rg, Lg = lg, Cpg = cpg };
        var model = Model(package,
            DutCapacitances.None with { Cgs = new DutCapacitance { Farads = cgs }, RgsOhms = rgs });
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out _));

        double omega = Omega(1);
        var zExt = new Complex(60, 20);
        var zSeries = new Complex(rg, omega * lg);
        var zBranch = new Complex(rgs, 0) + Complex.One / new Complex(0, omega * cgs);

        // HarmonicaNetlist.Build's own node order: Cpg shunts the TERMINATION PLANE itself, before
        // the Rg/Lg series lead — so Cpg combines with Z_ext directly (same node), the series lead
        // then moves the plane inward, and only THEN does the Cgs/rgs branch shunt (at the gate
        // terminal, a DIFFERENT node from Cpg's). Z_intr = ((Z_ext ∥ Z_Cpg) + Z_series) ∥ Z_branch.
        var zAtPlane = zExt;
        if (withCpg)
        {
            var zCpg = Complex.One / new Complex(0, omega * cpg);
            zAtPlane = Complex.One / (Complex.One / zExt + Complex.One / zCpg);
        }
        var zAtGate = zAtPlane + zSeries;
        var zIntrExpected = Complex.One / (Complex.One / zAtGate + Complex.One / zBranch);

        var zExtBack = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, zIntrExpected);
        output.WriteLine($"rg={rg} lg={lg} cpg={cpg}: back={zExtBack}, expected={zExt}");
        Assert.True(Complex.Abs(zExtBack - zExt) < 1e-9 * zExt.Magnitude,
            $"{zExtBack} vs {zExt}");
    }

    // ── 3. Round trip against the real solver ───────────────────────────────

    private static AnalysisSettings SolverSettings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    [Fact]
    public void RoundTrip_DragToTarget_RealSolverReportsTheSameIntrinsicGamma()
    {
        double cgs = 1e-12, rgs = 10.0, cds = 0.5e-12;
        var package = LumpedPackage.None with { Rg = 2.0, Lg = 0.3e-9, Cpg = 0.2e-12,
                                                 Rd = 1.0, Ld = 0.2e-9, Cpd = 0.15e-12 };
        var caps = new DutCapacitances
        {
            Cgs = new DutCapacitance { Farads = cgs },
            Cds = new DutCapacitance { Farads = cds },
            RgsOhms = rgs,
        };
        var model = Model(package, caps);
        Assert.True(CircuitModel.IntrinsicDragAllowed(model, out string why), why);

        double z0 = model.Settings.Z0;
        var targetGammaIntr = new Complex(0.3, 0.2);
        var targetZIntr = HarmonicaDataSet.ImpedanceOf(targetGammaIntr, z0);

        // IntrinsicAbcd's chain (per harmonicarf.md §4.1/§4.4) ends at the NETLIST's own termination
        // plane node (n_srcterm) — the ideal bias choke and the closure-time DC block sit OUTSIDE
        // that chain (the brief's own 3-element list does not carry them; they are the document's
        // "ideal" bias tee, negligible at the operating frequency for realistic values). This fixture
        // deliberately uses NON-ideal choke/block (the repo's own test convention, exercising real
        // physics), so the raw MARKER impedance this test feeds the solver has to be the inverse of
        // that one extra stage — Y_plane = Y_choke + 1/(Z_marker + Z_dcblock) — applied ONLY to
        // recover what a marker drag must set, never inside IntrinsicAbcd itself.
        var zPlane = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, targetZIntr);
        double omega1 = 2.0 * Math.PI * model.Settings.FrequencyHz;
        var yChoke = Complex.One / new Complex(0, omega1 * model.Settings.BiasChokeHenries);
        var zDcBlock = Complex.One / new Complex(0, omega1 * model.Settings.DcBlockFarads);
        var ySeriesNeeded = Complex.One / zPlane - yChoke;
        var zMarker = Complex.One / ySeriesNeeded - zDcBlock;

        var terms = new TerminationSet(model.Settings.HarmonicCount);
        for (int h = 1; h <= model.Settings.HarmonicCount; h++)
        {
            terms.Set(TerminationSide.Source, h, h == 1 ? zMarker : new Complex(40, 0));
            terms.Set(TerminationSide.Load,   h, new Complex(35, -10));
        }

        var ctx = HarmonicaContext.Create(model, SolverSettings);
        var pt  = ctx.Solve(terms, pavlDbm: -20);
        Assert.True(pt.Converged, $"‖F‖ = {pt.Residual:E3}");

        var iv = HarmonicaDataSet.Intrinsic(ctx, pt);
        var measuredGammaIntr = iv.Gamma[(int)TerminationSide.Source, 1];

        double residual = Complex.Abs(measuredGammaIntr - targetGammaIntr);
        output.WriteLine($"round-trip residual = {residual:E3}");
        Assert.True(residual < 1e-9, $"target {targetGammaIntr}, measured {measuredGammaIntr}, residual {residual:E3}");
    }

    // ── 4. Both sides are independent ───────────────────────────────────────

    [Fact]
    public void BothSides_AreIndependent_ChangingLoadDoesNotMoveSourceExtrinsicFor()
    {
        var package = LumpedPackage.None with { Rg = 2.0, Lg = 0.3e-9 };
        var caps = new DutCapacitances { Cgs = new DutCapacitance { Farads = 1e-12 }, RgsOhms = 5.0 };
        var model = Model(package, caps);

        var zIntr = new Complex(30, 10);
        var a = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, zIntr);

        // The load side's own package/caps do not enter Source's chain at all — ExtrinsicFor(Source,
        // ...) does not even take a load-side argument, so bit-equality across two calls proves the
        // independence directly rather than by construction alone.
        var b = IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Source, 1, zIntr);
        Assert.Equal(a, b);
    }

    // ── 5. The predicate itself ──────────────────────────────────────────────

    [Fact]
    public void Predicate_FalseForNonlinearCgs()
    {
        var caps = DutCapacitances.None with
        {
            Cgs = new DutCapacitance { Coefficients = [1e-12, 1e-14] },
        };
        Assert.False(CircuitModel.IntrinsicDragAllowed(Model(LumpedPackage.None, caps), out _));
    }

    [Fact]
    public void Predicate_FalseForNonAbsentCdg()
    {
        var caps = DutCapacitances.None with { Cdg = new DutCapacitance { Farads = 1e-13 } };
        Assert.False(CircuitModel.IntrinsicDragAllowed(Model(LumpedPackage.None, caps), out _));
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0)]   // Rs != 0
    [InlineData(0.0, 1e-9, 0.0)]  // Ls != 0
    [InlineData(0.0, 0.0, 1e-13)] // CgdExt != 0
    public void Predicate_FalseForPackageCoupling(double rs, double ls, double cgdExt)
    {
        var package = LumpedPackage.None with { Rs = rs, Ls = ls, CgdExt = cgdExt };
        Assert.False(CircuitModel.IntrinsicDragAllowed(Model(package, DutCapacitances.None), out _));
    }

    [Fact]
    public void Predicate_FalseForNativeFetAndExternal()
    {
        var nativeFet = new CircuitModel
        {
            Dut = new DutSpec { Kind = DutKind.NativeFet, TypeName = "FET_Angelov" },
        };
        Assert.False(CircuitModel.IntrinsicDragAllowed(nativeFet, out _));

        var external = new CircuitModel
        {
            Dut = new DutSpec { Kind = DutKind.External, TypeName = "SomeKit" },
        };
        Assert.False(CircuitModel.IntrinsicDragAllowed(external, out _));
    }

    [Fact]
    public void Predicate_TrueForTheShippedDefaultDocument()
    {
        // H6's own brief recorded that the shipped default document cannot exercise an intrinsic
        // drag at all (it is a bare SDD, no package, no capacitances — nothing to distinguish
        // intrinsic from extrinsic). §5.4 item 5 asks this to be checked explicitly.
        var shippedDefault = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
                },
            },
            Embedding = new EmbeddingStack { Package = LumpedPackage.None },
            Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
            Settings  = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = F0 },
        };

        bool allowed = CircuitModel.IntrinsicDragAllowed(shippedDefault, out string reason);
        output.WriteLine($"shipped default: allowed={allowed}, reason='{reason}'");
        Assert.True(allowed, reason);
    }
}
