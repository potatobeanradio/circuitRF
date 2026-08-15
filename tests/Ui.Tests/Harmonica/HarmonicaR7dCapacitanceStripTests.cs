// ================================================================
//  HarmonicaR7dCapacitanceStripTests.cs  —  brief-harmonicarf-r7d-dut-capacitances-and-nonlinear-c
//
//  §3.1  Cgs/Cdg/Cds only appear (in HarmonicaInputs.Build) for an SDD DUT.
//  §3.2  the value text: "0.00" absent, "1.23" linear, "1.23 (linearized)"/"1.23 (at V=0)" nonlinear.
//  §3.3  HarmonicaSolver.LinearizedCapacitanceFarads reads V_intr, never re-solves.
//  §3.4  Apply refuses a negative value and refuses ANY edit while nonlinear; Locked mirrors that.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR7dCapacitanceStripTests(ITestOutputHelper output)
{
    private static CircuitModel SddModel(DutCapacitances? caps = null) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/1e6",
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
            },
            Capacitances = caps ?? DutCapacitances.None,
        },
        Bias     = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9,
                                           BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-9 },
    };

    private static CircuitModel NativeFetModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.NativeFet, TypeName = "FET_Angelov",
            Capacitances = DutCapacitances.None with { Cgs = new DutCapacitance { Farads = 1e-12 } },
        },
        Bias     = new BiasSpec { Vgs = -1.0, Vds = 5 },
        Settings = new HarmonicaSettings { HarmonicCount = 2, FrequencyHz = 1e9 },
    };

    private static HarmonicaInput Row(CircuitModel model, string key,
        double? linCgs = null, double? linCdg = null, double? linCds = null)
        => HarmonicaInputs.Build(model, null, linCgs, linCdg, linCds).Single(i => i.Key == key);

    // ── §3.1 — SDD only ──────────────────────────────────────────────────────

    [Fact]
    public void SddDut_EmitsAllThreeCapacitanceRows()
    {
        var inputs = HarmonicaInputs.Build(SddModel());
        Assert.Contains(inputs, i => i.Key == HarmonicaInputs.KeyCgs);
        Assert.Contains(inputs, i => i.Key == HarmonicaInputs.KeyCdg);
        Assert.Contains(inputs, i => i.Key == HarmonicaInputs.KeyCds);
    }

    [Fact]
    public void NonSddDut_EmitsNoCapacitanceRows()
    {
        var inputs = HarmonicaInputs.Build(NativeFetModel());
        Assert.DoesNotContain(inputs, i => i.Key == HarmonicaInputs.KeyCgs);
        Assert.DoesNotContain(inputs, i => i.Key == HarmonicaInputs.KeyCdg);
        Assert.DoesNotContain(inputs, i => i.Key == HarmonicaInputs.KeyCds);
    }

    // ── §3.2 — the value text ────────────────────────────────────────────────

    [Fact]
    public void Absent_ReadsAsZeroPointZeroZero_Unlocked()
    {
        var row = Row(SddModel(), HarmonicaInputs.KeyCgs);
        Assert.Equal("0.00", row.Text);
        Assert.Equal("pF", row.Unit);
        Assert.False(row.Locked);
        Assert.True(row.Structural);
    }

    [Fact]
    public void Linear_ReadsAsTwoDecimalPlaces_Unlocked()
    {
        var caps  = DutCapacitances.None with { Cds = new DutCapacitance { Farads = 1.234e-12 } };
        var row   = Row(SddModel(caps), HarmonicaInputs.KeyCds);
        Assert.Equal("1.23", row.Text);
        Assert.False(row.Locked);
    }

    [Fact]
    public void Nonlinear_WithNoLinearizedValue_ShowsC0AtVZero_Locked()
    {
        double[] coeffs = [2.5e-13, 1e-14];
        var caps = DutCapacitances.None with { Cdg = new DutCapacitance { Coefficients = coeffs } };
        var row  = Row(SddModel(caps), HarmonicaInputs.KeyCdg);

        Assert.Equal("0.25 (at V=0)", row.Text);
        Assert.True(row.Locked);
    }

    [Fact]
    public void Nonlinear_WithALinearizedValue_ShowsItInsteadOfC0()
    {
        double[] coeffs = [2.5e-13, 1e-14];
        var caps = DutCapacitances.None with { Cdg = new DutCapacitance { Coefficients = coeffs } };
        var row  = Row(SddModel(caps), HarmonicaInputs.KeyCdg, linCdg: 0.9e-12);

        Assert.Equal("0.90 (linearized)", row.Text);
        Assert.True(row.Locked);
    }

    // ── §3.4 — Apply refusals ────────────────────────────────────────────────

    [Fact]
    public void Apply_WritesFaradsFromTypedPicofarads()
    {
        var updated = HarmonicaInputs.Apply(SddModel(), HarmonicaInputs.KeyCgs, "2.5", out string? error);
        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.Equal(2.5e-12, updated!.Dut.Capacitances.Cgs.Farads);
        Assert.False(updated.Dut.Capacitances.Cgs.IsNonlinear);
    }

    [Fact]
    public void Apply_RefusesANegativeValue()
    {
        var updated = HarmonicaInputs.Apply(SddModel(), HarmonicaInputs.KeyCds, "-1", out string? error);
        Assert.Null(updated);
        Assert.NotNull(error);
        output.WriteLine(error);
    }

    [Fact]
    public void Apply_RefusesAnyEditWhileNonlinear()
    {
        var caps  = DutCapacitances.None with { Cgs = new DutCapacitance { Coefficients = [1e-13, 2e-14] } };
        var model = SddModel(caps);
        var updated = HarmonicaInputs.Apply(model, HarmonicaInputs.KeyCgs, "5", out string? error);

        Assert.Null(updated);
        Assert.NotNull(error);
        output.WriteLine(error);
        // The capacitor is untouched — refusing means the OLD value survives, this file's own
        // existing contract for a rejected edit.
        Assert.True(model.Dut.Capacitances.Cgs.IsNonlinear);
    }

    [Fact]
    public void StructuralKey_MovesWhenACapacitanceChanges_AndOnlyThen()
    {
        var a = SddModel();
        var b = SddModel(DutCapacitances.None with { Cgs = new DutCapacitance { Farads = 1e-13 } });
        Assert.NotEqual(a.StructuralKey, b.StructuralKey);

        // Two DIFFERENT DutCapacitances.None instances (default vs explicit) must still agree.
        var c = SddModel() with { Dut = SddModel().Dut with { Capacitances = DutCapacitances.None } };
        Assert.Equal(a.StructuralKey, c.StructuralKey);
    }

    // ── §3.3 — the linearized value, read from V_intr, never re-solved ─────────

    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    private static TerminationSet Terms(int k, Complex zs, Complex zl)
    {
        var t = new TerminationSet(k);
        for (int h = 1; h <= k; h++) { t.Set(TerminationSide.Source, h, zs); t.Set(TerminationSide.Load, h, zl); }
        return t;
    }

    [Fact]
    public void LinearizedCapacitanceFarads_MatchesTheHornerAtTheReadBiasVoltage()
    {
        double[] coeffs = [3e-13, 5e-14, -1e-15];
        var model = SddModel();
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(3, new Complex(50, 0), new Complex(50, 0));
        var pt    = ctx.Solve(terms, -10);
        Assert.True(pt.Converged);
        var ds = HarmonicaDataSet.Build(ctx, pt, terms);

        double vGate  = ReadComplex(ds, "V_intr", ctx.IntrinsicPorts.GatePort,  0).Real;
        double vDrain = ReadComplex(ds, "V_intr", ctx.IntrinsicPorts.DrainPort, 0).Real;

        double? cgs = HarmonicaSolver.LinearizedCapacitanceFarads(ctx, ds, coeffs, DutCapacitanceKind.Cgs);
        double? cds = HarmonicaSolver.LinearizedCapacitanceFarads(ctx, ds, coeffs, DutCapacitanceKind.Cds);
        double? cdg = HarmonicaSolver.LinearizedCapacitanceFarads(ctx, ds, coeffs, DutCapacitanceKind.Cdg);

        Assert.NotNull(cgs); Assert.NotNull(cds); Assert.NotNull(cdg);
        Assert.Equal(CircuitRF.Core.Devices.NonlinearCModel.CapacitanceAt(coeffs, vGate), cgs!.Value, 12);
        Assert.Equal(CircuitRF.Core.Devices.NonlinearCModel.CapacitanceAt(coeffs, vDrain), cds!.Value, 12);
        Assert.Equal(CircuitRF.Core.Devices.NonlinearCModel.CapacitanceAt(coeffs, vDrain - vGate), cdg!.Value, 12);
    }

    [Fact]
    public void LinearizedCapacitanceFarads_NullWhenNothingHasBeenSolved()
    {
        var model = SddModel();
        var ctx   = HarmonicaContext.Create(model, Settings);
        Assert.Null(HarmonicaSolver.LinearizedCapacitanceFarads(ctx, null, [1e-13], DutCapacitanceKind.Cgs));
    }

    private static Complex ReadComplex(RfCore.Data.DataSet ds, string cubeName, int sideIndex, int harmonic)
    {
        var cube = ds[cubeName];
        int harmonics = cube.Axes[1].Values.Length;
        return cube.ComplexValues[sideIndex * harmonics + harmonic];
    }

    // ── end-to-end through HarmonicaViewModel.Inputs ────────────────────────────

    [Fact]
    public void ViewModelInputs_ShowsALinearizedNonlinearCapacitorAfterASolve()
    {
        var caps = DutCapacitances.None with { Cds = new DutCapacitance { Coefficients = [2e-13, 4e-14] } };
        var vm = new HarmonicaViewModel(HarmonicaViewModel.DefaultModel() with
        {
            Dut = HarmonicaViewModel.DefaultModel().Dut with { Capacitances = caps },
        });
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6, SkipContours = true });
        Assert.Null(vm.SolveError);

        var row = vm.Inputs.Single(i => i.Key == HarmonicaInputs.KeyCds);
        output.WriteLine(row.Text);
        Assert.True(row.Locked);
        Assert.EndsWith("(linearized)", row.Text, StringComparison.Ordinal);
    }
}
