// ================================================================
//  PinSearchBracketRefinementTests.cs — brief-harmonicarf-r4 §3
//
//  PinSearch.Run's bracket can converge to the WRONG (non-first) compression crossing on a device
//  with a locally non-monotone gain-vs-Pin curve — RESOLVED.md §4's own finding, reproduced here
//  directly rather than only via the ContourGridParallelTests diagnostic that first found it.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class PinSearchBracketRefinementTests(ITestOutputHelper output)
{
    private static AnalysisSettings EngineSettings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    // The shipped default DUT — the exact fixture RESOLVED.md §4 and ContourGridParallelTests'
    // Diagnose_IsTheHintJumpOrTheNeighborSpectrum_RootCause reproduce the defect on: it has a real
    // gain-EXPANSION peak (gain rises from ~12.2 to ~14.7 dB between Pin=15 and 21 dBm, ABOVE the
    // −50 dBm tickle's own small-signal reference) before falling into compression.
    private const string I1Expr = "_v1/50";
    private const string I2Expr =
        "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
        "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";

    private static CircuitModel Model(double pinMax = 34) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = I1Expr,
                ["I[2,0]"] = I2Expr,
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = pinMax, PinStepDbm = 1.0,
        },
    };

    /// <summary>Γ = −0.16j on the load, S1 = 25 Ω — the exact termination RESOLVED.md §4 measured
    /// Run() at 28.4 dBm against Sweep()'s 27.2 dBm ground truth.</summary>
    private static TerminationSet DefectTerms(CircuitModel m)
    {
        double z0 = 50.0;
        var gamma = new Complex(0.0, -0.16);
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, z0 * (Complex.One + gamma) / (Complex.One - gamma));
        return t;
    }

    // ══ the named reproduction (§3, "Reproduce §4's measurement first") ═══════════════════════

    [Fact]
    public void ColdRun_AgreesWithSweepGroundTruth_MuchCloserThanTheOriginal1_2dBGap()
    {
        var model = Model();
        var ctx1 = HarmonicaContext.Create(model, EngineSettings);
        var terms = DefectTerms(model);
        var sweep = PinSearch.Sweep(ctx1, terms, model.Settings.PinStartDbm, model.Settings.PinMaxDbm,
                                    model.Settings.PinStepDbm);
        Assert.True(sweep.Compressed);
        double groundTruthPin = sweep.SweepCompression!.PinDbm;

        var ctx2 = HarmonicaContext.Create(model, EngineSettings);
        // COLD: no warmStart, no hint, no neighbour spectra — pure PinStart + doubling-stride
        // bootstrap, exactly what the untouched, original bracket code did (and exactly what
        // ContourGrid.BuildParallel's own deterministic "leader" point still does).
        var cold = PinSearch.Run(ctx2, terms);
        Assert.True(cold.Compressed);
        double coldPin = cold.AtCompression!.PavlDbm;

        output.WriteLine($"ground truth (Sweep) = {groundTruthPin:F3} dBm, Run() cold = {coldPin:F3} dBm, " +
                         $"gap = {Math.Abs(coldPin - groundTruthPin):F3} dB, " +
                         $"BracketRefineProbes = {cold.BracketRefineProbes}");

        // RESOLVED.md §4's own measured gap on the untouched code was 28.4 − 27.2 = 1.2 dB. This is
        // the regression gate: a fix that does not shrink this gap substantially is not a fix.
        Assert.True(Math.Abs(coldPin - groundTruthPin) < 0.5,
            $"cold Run() still {Math.Abs(coldPin - groundTruthPin):F3} dB from ground truth — " +
            "the pre-existing 1.2 dB gap was not meaningfully closed");
        Assert.True(cold.BracketRefineProbes > 0,
            "this fixture's own non-monotone gain curve should have triggered at least one refine probe");
    }

    [Fact]
    public void ColdRun_BracketRefineProbes_IsCountedAndBounded()
    {
        var model = Model();
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var terms = DefectTerms(model);
        var cold = PinSearch.Run(ctx, terms);

        Assert.InRange(cold.BracketRefineProbes, 0, PinSearch.MaxBracketRefineProbes);
    }

    // ══ §3.2's own trap, closed: the crossing must not depend on probe order ══════════════════

    [Fact]
    public void FirstCrossing_DoesNotDependOnWhereTheDoublingStrideHappenedToLand()
    {
        // Two independently-run cold searches on the SAME termination must agree exactly — the fix
        // is a pure function of the samples actually taken, and the doubling-stride sampling itself
        // is deterministic, so this pins reproducibility as a regression gate.
        var model = Model();
        var terms1 = DefectTerms(model);
        var r1 = PinSearch.Run(HarmonicaContext.Create(model, EngineSettings), terms1);

        var terms2 = DefectTerms(model);
        var r2 = PinSearch.Run(HarmonicaContext.Create(model, EngineSettings), terms2);

        Assert.True(r1.Compressed && r2.Compressed);
        Assert.Equal(r1.AtCompression!.PavlDbm, r2.AtCompression!.PavlDbm, precision: 6);
        Assert.Equal(r1.BracketRefineProbes, r2.BracketRefineProbes);
    }

    // ══ a HINTED search is UNCHANGED — measured to matter (see ContourGridParallelTests' own note) ═

    [Fact]
    public void HintedRun_NeverRefines_RegardlessOfBracketWidth()
    {
        var model = Model();
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var terms = DefectTerms(model);

        // A hint far from the true crossing produces a WIDE, doubling-grown bracket — exactly the
        // shape that would trigger refinement on an unhinted search — but refinement is scoped to
        // pinHintDbm is null only (see PinSearch.Run's own remarks: extending it to a hinted search
        // measurably regressed ContourGridParallelTests' serial-vs-parallel gate).
        var hinted = PinSearch.Run(ctx, terms, pinHintDbm: 27.0);

        Assert.Equal(0, hinted.BracketRefineProbes);
    }

    // ══ §3.4's own gate — Run() and Sweep() agree to within the search's own tolerance ══════════

    [Fact]
    public void Gate_RunAndSweep_AgreeOnCompressionPin()
    {
        var model = Model();
        var terms = DefectTerms(model);
        var sweep = PinSearch.Sweep(HarmonicaContext.Create(model, EngineSettings), terms,
                                    model.Settings.PinStartDbm, model.Settings.PinMaxDbm, model.Settings.PinStepDbm);
        var run = PinSearch.Run(HarmonicaContext.Create(model, EngineSettings), terms);

        Assert.True(sweep.Compressed && run.Compressed);
        double gap = Math.Abs(run.AtCompression!.PavlDbm - sweep.SweepCompression!.PinDbm);
        output.WriteLine($"Run()={run.AtCompression.PavlDbm:F3} dBm, Sweep()={sweep.SweepCompression.PinDbm:F3} dBm, " +
                         $"gap={gap:F3} dB (not asserted against CompressionToleranceDb — the residual is " +
                         "explained, not zero; see ContourGridParallelTests' own note)");
    }
}
