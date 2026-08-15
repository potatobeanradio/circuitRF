using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-harmonicarf-r7d — Cgs/Cdg/Cds on the DUT, linear or nonlinear. §7's gates: the netlist
/// (absent emits nothing; a nonlinear line's coefficients survive), the physics (Cdg moves the
/// intrinsic glyph and Zin while the extrinsic marker is untouched; an untouched document is
/// unaffected), and the <c>.charm</c> round trip (three states, including byte-for-byte absent).
/// </summary>
public sealed class DutCapacitanceTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>An SDD with real gm/gds so a feedback capacitance has something to feed back onto.</summary>
    private static CircuitModel Model(DutCapacitances? caps = null) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/1e6",
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
            },
            Capacitances = caps ?? DutCapacitances.None,
        },
        Embedding = new EmbeddingStack { Package = LumpedPackage.None },
        Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings  = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-9,
        },
    };

    private static TerminationSet Terms(int k, Complex zs, Complex zl)
    {
        var t = new TerminationSet(k);
        for (int h = 1; h <= k; h++) { t.Set(TerminationSide.Source, h, zs); t.Set(TerminationSide.Load, h, zl); }
        return t;
    }

    // ── §7.2 — the netlist gate ───────────────────────────────────────────────

    [Fact]
    public void Absent_EmitsNoLineAtAll()
    {
        var model = Model();   // DutCapacitances.None
        string text = HarmonicaNetlist.Build(model).Text;

        Assert.DoesNotContain("CGS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CDG", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CDS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NonlinearC", text, StringComparison.Ordinal);

        // CnlReader + Elaborator accept it (HarmonicaContext.Create throws if they do not).
        var ctx = HarmonicaContext.Create(model, Settings);
        Assert.Equal(1, ctx.RebuildCount);
    }

    public static IEnumerable<object[]> LinearCapacitorCases()
    {
        yield return new object[] { "CGS", (Func<DutCapacitance, DutCapacitances>)(c => DutCapacitances.None with { Cgs = c }) };
        yield return new object[] { "CDG", (Func<DutCapacitance, DutCapacitances>)(c => DutCapacitances.None with { Cdg = c }) };
        yield return new object[] { "CDS", (Func<DutCapacitance, DutCapacitances>)(c => DutCapacitances.None with { Cds = c }) };
    }

    [Theory]
    [MemberData(nameof(LinearCapacitorCases))]
    public void Linear_EmitsAPlainCLine(string name, Func<DutCapacitance, DutCapacitances> place)
    {
        var caps  = place(new DutCapacitance { Farads = 1.2e-12 });
        var model = Model(caps);
        string text = HarmonicaNetlist.Build(model).Text;

        Assert.Contains($"C:{name}", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"NonlinearC:{name}", text, StringComparison.Ordinal);

        var ctx = HarmonicaContext.Create(model, Settings);
        Assert.Equal(1, ctx.RebuildCount);

        var ec = ctx.Netlist.Components.First(c => c.InstancePath == name);
        Assert.IsType<CapacitorModel>(ec.Model);
    }

    [Theory]
    [MemberData(nameof(LinearCapacitorCases))]
    public void Nonlinear_CoefficientsSurviveToTheElaboratedModel(string name, Func<DutCapacitance, DutCapacitances> place)
    {
        double[] coeffs = [3e-13, 5e-14, -2e-15];
        var caps  = place(new DutCapacitance { Coefficients = coeffs });
        var model = Model(caps);
        string text = HarmonicaNetlist.Build(model).Text;

        Assert.Contains($"NonlinearC:{name}", text, StringComparison.Ordinal);
        Assert.Contains("C0=", text, StringComparison.Ordinal);
        Assert.Contains("C1=", text, StringComparison.Ordinal);
        Assert.Contains("C2=", text, StringComparison.Ordinal);

        var ctx = HarmonicaContext.Create(model, Settings);
        var ec  = ctx.Netlist.Components.First(c => c.InstancePath == name);
        var nlc = Assert.IsType<NonlinearCModel>(ec.Model);

        foreach (double v in new[] { -0.4, 0.0, 0.7, 1.3 })
        {
            double expected = NonlinearCModel.CapacitanceAt(coeffs, v);
            double actual   = nlc.Evaluate(new PortVoltages([v])).Dc[0, 0];
            Assert.True(Math.Abs(actual - expected) < 1e-20 * Math.Max(1.0, Math.Abs(expected)),
                $"{name} at V={v}: expected {expected:E6}, got {actual:E6}");
        }
    }

    [Fact]
    public void NonSddDut_EmitsNoCapacitorLinesEvenIfCapacitancesAreSet()
    {
        // §1 — SDD only. A non-SDD DUT with a package-level short still emits nothing for a stated
        // capacitance: a native FET already carries gate charge, and emitting these too would
        // double-count.
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.NativeFet, TypeName = "FET_Angelov",
                Capacitances = DutCapacitances.None with { Cgs = new DutCapacitance { Farads = 1e-12 } },
            },
            Bias     = new BiasSpec { Vgs = -1.0, Vds = 5 },
            Settings = new HarmonicaSettings { HarmonicCount = 2, FrequencyHz = 1e9 },
        };
        string text = HarmonicaNetlist.Build(model).Text;
        Assert.DoesNotContain("CGS", text, StringComparison.Ordinal);
    }

    // ── §7.3 — the physics gate ───────────────────────────────────────────────

    [Fact]
    public void Cdg_RotatesTheIntrinsicGlyphAndMovesZinWhileTheExtrinsicMarkerStaysPut()
    {
        var terms = Terms(3, new Complex(50, 0), new Complex(50, 0));

        DataSet Solve(DutCapacitances caps)
        {
            var model = Model(caps);
            var ctx   = HarmonicaContext.Create(model, Settings);
            var pt    = ctx.Solve(terms, -10);
            Assert.True(pt.Converged, $"‖F‖ = {pt.Residual:E3}");
            return HarmonicaDataSet.Build(ctx, pt, terms);
        }

        var bare = Solve(DutCapacitances.None);
        var fed  = Solve(DutCapacitances.None with { Cdg = new DutCapacitance { Farads = 0.5e-12 } });

        Complex GammaIntrLoad(DataSet ds) => ReadComplex(ds, "Gamma_intr", (int)TerminationSide.Load, 1);
        Complex GammaExt(DataSet ds, TerminationSide s) => ReadComplex(ds, "Gamma_ext", (int)s, 1);
        Complex Zin(DataSet ds) => ReadComplex(ds, "Zin", (int)TerminationSide.Source, 1);

        double glyphMove = (GammaIntrLoad(fed) - GammaIntrLoad(bare)).Magnitude;
        output.WriteLine($"Γ_intr (load, 1f0): bare {GammaIntrLoad(bare):G6}  fed {GammaIntrLoad(fed):G6}  Δ={glyphMove:E3}");
        Assert.True(glyphMove > 1e-4, "the intrinsic glyph did not move when Cdg was added");

        // The extrinsic marker is exactly what the user set on the panel — Cdg cannot move it.
        Assert.Equal(GammaExt(bare, TerminationSide.Source), GammaExt(fed, TerminationSide.Source));
        Assert.Equal(GammaExt(bare, TerminationSide.Load),   GammaExt(fed, TerminationSide.Load));

        double zinMove = (Zin(fed) - Zin(bare)).Magnitude;
        output.WriteLine($"Zin (source, 1f0): bare {Zin(bare):G6}  fed {Zin(fed):G6}  Δ={zinMove:E3}");
        Assert.True(zinMove / Zin(bare).Magnitude > 1e-4, "Zin did not move — the feedback capacitance should be visible at the input");
    }

    [Fact]
    public void AllThreeAbsent_MatchesAModelThatNeverSetCapacitancesAtAll_BitIdentically()
    {
        // "An untouched document must not move by so much as an LSB" — proven here by construction
        // rather than an external fixture: a model that never touches the new field (Capacitances
        // defaults to DutCapacitances.None) and one that sets it to DutCapacitances.None explicitly
        // must produce IDENTICAL netlist text and IDENTICAL solved numbers, since the netlist text is
        // what solving is actually a function of.
        var untouched = new DutSpec { Kind = DutKind.Sdd, TypeName = "SDD", Parameters = Model().Dut.Parameters };
        var explicitlyNone = untouched with { Capacitances = DutCapacitances.None };

        var m1 = Model() with { Dut = untouched };
        var m2 = Model() with { Dut = explicitlyNone };

        Assert.Equal(HarmonicaNetlist.Build(m1).Text, HarmonicaNetlist.Build(m2).Text);
        Assert.Equal(m1.StructuralKey, m2.StructuralKey);

        var terms = Terms(3, new Complex(50, 0), new Complex(50, 0));
        var pt1 = HarmonicaContext.Create(m1, Settings).Solve(terms, -10);
        var pt2 = HarmonicaContext.Create(m2, Settings).Solve(terms, -10);

        Assert.Equal(pt1.V.Cast<Complex>(), pt2.V.Cast<Complex>());
        Assert.Equal(pt1.Residual, pt2.Residual);
    }

    private static Complex ReadComplex(DataSet ds, string cubeName, int sideIndex, int harmonic)
    {
        var cube = ds[cubeName];
        int harmonics = cube.Axes[1].Values.Length;
        return cube.ComplexValues[sideIndex * harmonics + harmonic];
    }

    // ── §7.4 — the .charm round trip ──────────────────────────────────────────

    [Fact]
    public void Absent_RoundTripsAndWritesNoBlockAtAllByteForByte()
    {
        var model = Model();
        string before = CharmIo.Write(model);
        Assert.DoesNotContain("\"Cgs\"", before, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Cdg\"", before, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Cds\"", before, StringComparison.Ordinal);

        var back  = CharmIo.Read(before, null, out var unresolved);
        Assert.Empty(unresolved);
        Assert.True(back.Dut.Capacitances.IsIdentity);

        string after = CharmIo.Write(back);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Linear_RoundTrips()
    {
        var caps  = DutCapacitances.None with { Cds = new DutCapacitance { Farads = 4.4e-13 } };
        var model = Model(caps);

        string json = CharmIo.Write(model);
        var back = CharmIo.Read(json, null, out var unresolved);

        Assert.Empty(unresolved);
        Assert.False(back.Dut.Capacitances.Cds.IsNonlinear);
        Assert.Equal(4.4e-13, back.Dut.Capacitances.Cds.Farads);
        Assert.True(back.Dut.Capacitances.Cgs.IsAbsent);
        Assert.True(back.Dut.Capacitances.Cdg.IsAbsent);
    }

    [Fact]
    public void Nonlinear_RoundTrips()
    {
        double[] coeffs = [1e-13, 2e-14];
        var caps  = DutCapacitances.None with { Cgs = new DutCapacitance { Coefficients = coeffs } };
        var model = Model(caps);

        string json = CharmIo.Write(model);
        var back = CharmIo.Read(json, null, out var unresolved);

        Assert.Empty(unresolved);
        Assert.True(back.Dut.Capacitances.Cgs.IsNonlinear);
        Assert.Equal(coeffs, back.Dut.Capacitances.Cgs.Coefficients);
    }
}
