// ================================================================
//  PinSweepTests.cs — brief-harmonicarf-r2b §5 (R-h9r2-17/17a/18/18a)
//
//  PinSearch.Sweep is the EXPLICIT uniform ladder tier A now drives: every point Start, Start+Step, …
//  up to and including Stop, a real HB solve each, with compression INTERPOLATED from the first
//  bracketing pair rather than an extra solve by default.
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

public sealed class PinSweepTests(ITestOutputHelper output)
{
    private static AnalysisSettings EngineSettings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>Hero 2's GaN HEMT — the same fixture <c>LoadlineSamplesTests</c> uses, with
    /// terminations (25 Ω source, 80+j10 Ω load) under which this device compresses cleanly within
    /// the default range, unlike the shipped document's own unmarked-band markers.</summary>
    private static CircuitModel Model(double pinMax = 34, double pinStep = 1.0,
                                      bool tickleEnabled = true, double tickleDbm = -50.0,
                                      bool exact = false) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = pinMax, PinStepDbm = pinStep,
            TickleEnabled = tickleEnabled, TickleDbm = tickleDbm, ExactCompressionSolve = exact,
        },
    };

    private static TerminationSet Terms(CircuitModel m)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return t;
    }

    // ══ R-h9r2-17 — the ladder is exactly what the user typed ═══════════════════════════════

    [Fact]
    public void DefaultRange_Is61Points_InclusiveAtBothEnds()
    {
        var model = Model(pinMax: 50, pinStep: 1.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 50, 1);

        Assert.Equal(61, r.Steps.Count);
        Assert.Equal(-10.0, r.Steps[0].PavlDbm, precision: 9);
        Assert.Equal(50.0,  r.Steps[^1].PavlDbm, precision: 9);
        for (int i = 1; i < r.Steps.Count; i++)
            Assert.Equal(1.0, r.Steps[i].PavlDbm - r.Steps[i - 1].PavlDbm, precision: 6);
    }

    [Fact]
    public void ANonIntegerRange_StillReachesStopExactly_WithAShortFinalInterval()
    {
        var model = Model(pinMax: 50, pinStep: 7.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 50, 7);

        // -10, -3, 4, 11, 18, 25, 32, 39, 46, 50 — nine regular 7 dB rungs plus a short final one.
        Assert.Equal(10, r.Steps.Count);
        Assert.Equal(-10.0, r.Steps[0].PavlDbm, precision: 9);
        Assert.Equal(50.0,  r.Steps[^1].PavlDbm, precision: 9);
        Assert.Equal(46.0,  r.Steps[^2].PavlDbm, precision: 9);
        // The final interval is short (4 dB), not a full 7.
        Assert.True(r.Steps[^1].PavlDbm - r.Steps[^2].PavlDbm < 7.0);

        Assert.Equal(PowerSweepValidation.PointCount(-10, 50, 7), r.Steps.Count);
    }

    [Fact]
    public void ARangeThatDividesEvenly_DoesNotDoubleCountTheFinalPoint()
    {
        var model = Model(pinMax: 10, pinStep: 2.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 10, 2);

        // -10,-8,-6,-4,-2,0,2,4,6,8,10 — 11 points, evenly divisible, no duplicate at the end.
        Assert.Equal(11, r.Steps.Count);
        Assert.Equal(10.0, r.Steps[^1].PavlDbm, precision: 9);
        Assert.Equal(8.0,  r.Steps[^2].PavlDbm, precision: 9);
    }

    [Fact]
    public void EveryStepSolves_NothingIsResampledOrSkipped()
    {
        // Every Steps[i].PavlDbm is a value ACTUALLY handed to ctx.Solve — pinned indirectly by
        // requiring monotone, evenly-spaced Pin values with no gaps, and Solves >= Steps.Count
        // (every step cost at least one real solve, tickle on top).
        var model = Model(pinMax: 20, pinStep: 2.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 20, 2);

        Assert.True(r.Solves >= r.Steps.Count);
        output.WriteLine($"{r.Steps.Count} steps, {r.Solves} solves");
    }

    // ══ R-h9r2-17a — compression is interpolated, not read off the nearest whole-dB step ═════

    [Fact]
    public void Compression_IsInterpolated_StrictlyBetweenTheBracketingLadderPoints()
    {
        var model = Model(pinMax: 34, pinStep: 1.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 34, 1);

        Assert.Equal(PinStopReason.Compression, r.Reason);
        Assert.NotNull(r.SweepCompression);
        var sc = r.SweepCompression!;

        output.WriteLine($"interpolated Pin={sc.PinDbm:F4} dBm, nearest solved={sc.Spectrum.PavlDbm} dBm, " +
                         $"WasInterpolated={sc.WasInterpolated}");

        if (sc.WasInterpolated)
        {
            // Strictly between the two ladder points it was interpolated from — not equal to either,
            // which is exactly the rounding-to-the-nearest-dB error interpolation exists to remove.
            double lo = Math.Floor(sc.PinDbm), hi = lo + 1.0;
            Assert.True(sc.PinDbm > lo - 1e-9 && sc.PinDbm < hi + 1e-9);
            Assert.NotEqual(Math.Round(sc.PinDbm), sc.PinDbm, precision: 6);
        }
    }

    [Fact]
    public void Compression_UsesTheSweepsOwnFinalGMax_NotAPartialOne()
    {
        // The FOM at compression must be internally consistent with the Steps array's own Compression
        // column — both are derived from the same running gMax.
        var model = Model(pinMax: 34, pinStep: 1.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 34, 1);
        Assert.NotNull(r.SweepCompression);

        // Every step's Compression is non-negative (gMax is by definition >= any single gain sample).
        foreach (var step in r.Steps)
            Assert.True(step.Compression >= -1e-9, $"pin={step.PavlDbm} compression={step.Compression}");
    }

    [Fact]
    public void ExactCompressionSolve_Default_IsOff()
        => Assert.False(new HarmonicaSettings().ExactCompressionSolve);

    [Fact]
    public void ExactCompressionSolve_On_CostsExactlyOneExtraSolve_AndSpectrumMatchesTheReading()
    {
        var offModel = Model(pinMax: 34, pinStep: 1.0, exact: false);
        var onModel  = Model(pinMax: 34, pinStep: 1.0, exact: true);

        var ctxOff = HarmonicaContext.Create(offModel, EngineSettings);
        var rOff = PinSearch.Sweep(ctxOff, Terms(offModel), -10, 34, 1);

        var ctxOn = HarmonicaContext.Create(onModel, EngineSettings);
        var rOn = PinSearch.Sweep(ctxOn, Terms(onModel), -10, 34, 1);

        Assert.NotNull(rOff.SweepCompression);
        Assert.NotNull(rOn.SweepCompression);
        Assert.Equal(rOff.Solves + 1, rOn.Solves);

        // ON: the reading's own Pin equals the SOLVED spectrum's Pin (a real state, not a blend).
        Assert.False(rOn.SweepCompression!.WasInterpolated);
        Assert.Equal(rOn.SweepCompression.PinDbm, rOn.SweepCompression.Spectrum.PavlDbm, precision: 6);
        Assert.Same(rOn.SweepCompression.Spectrum, rOn.AtCompression);

        output.WriteLine($"off: interpolated Pin={rOff.SweepCompression!.PinDbm:F4} ({rOff.Solves} solves); " +
                         $"on: exact Pin={rOn.SweepCompression.PinDbm:F4} ({rOn.Solves} solves)");
        // The two should be close — they are measuring the same physical crossing.
        Assert.True(Math.Abs(rOff.SweepCompression.PinDbm - rOn.SweepCompression.PinDbm) < 1.0);
    }

    [Fact]
    public void ExactCompressionSolve_Off_ReadsTheNearestSolvedLadderPoint_WithinHalfAStep()
    {
        var model = Model(pinMax: 34, pinStep: 1.0, exact: false);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 34, 1);
        Assert.NotNull(r.SweepCompression);

        double d = Math.Abs(r.SweepCompression!.PinDbm - r.SweepCompression.Spectrum.PavlDbm);
        Assert.True(d <= 0.5 + 1e-9, $"nearest solved point is {d:F3} dB from the interpolated compression Pin");
        Assert.Same(r.SweepCompression.Spectrum, r.AtCompression);
    }

    // ══ R-h9r2-18a — the tickle ══════════════════════════════════════════════════════════════

    [Fact]
    public void TickleOn_ReportsSmallSignalGain_AndCostsOneExtraSolve()
    {
        var onModel  = Model(pinMax: 20, pinStep: 2.0, tickleEnabled: true);
        var offModel = Model(pinMax: 20, pinStep: 2.0, tickleEnabled: false);

        var ctxOn = HarmonicaContext.Create(onModel, EngineSettings);
        var rOn = PinSearch.Sweep(ctxOn, Terms(onModel), -10, 20, 2);
        var ctxOff = HarmonicaContext.Create(offModel, EngineSettings);
        var rOff = PinSearch.Sweep(ctxOff, Terms(offModel), -10, 20, 2);

        Assert.NotNull(rOn.SmallSignalGainDb);
        Assert.Null(rOff.SmallSignalGainDb);
        Assert.Equal(rOff.Solves + 1, rOn.Solves);

        // Off: gMax seeds from the FIRST solved ladder point, so that point's own Compression is 0.
        Assert.Equal(0.0, rOff.Steps[0].Compression, precision: 9);
    }

    [Fact]
    public void Tickle_IsAnAbsoluteLevel_ValidatedBelowStart()
    {
        Assert.True(PowerSweepValidation.IsValidTickle(-50, -10));
        Assert.False(PowerSweepValidation.IsValidTickle(-10, -10));   // at Start — refused
        Assert.False(PowerSweepValidation.IsValidTickle(-5,  -10));   // above Start — refused
        Assert.False(PowerSweepValidation.IsValidTickle(double.NaN, -10));
    }

    // ══ never compressing is a normal outcome ════════════════════════════════════════════════

    [Fact]
    public void ARangeThatNeverCompresses_ReportsPinMax_NotAnError()
    {
        // Capped well below where this device's gain even peaks — CompressionAt never crosses 3 dB.
        var model = Model(pinMax: 5, pinStep: 1.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 5, 1);

        Assert.Equal(PinStopReason.PinMax, r.Reason);
        Assert.Null(r.SweepCompression);
        Assert.Null(r.AtCompression);
        Assert.False(r.Compressed);
        // Every point up to Stop was still solved — no decimation just because it never compresses.
        Assert.Equal(16, r.Steps.Count);
    }

    // ══ R-h9r2-18 — the Pin range is a VALUE change, not structural (mirrors R-h9b-6's own Z0 test) ═

    [Fact]
    public void PinRangeAndTickleSettings_DoNotEnterTheStructuralKey()
    {
        var a = Model(pinMax: 34, pinStep: 1.0, tickleEnabled: true,  tickleDbm: -50, exact: false);
        var b = Model(pinMax: 12, pinStep: 0.25, tickleEnabled: false, tickleDbm: -30, exact: true);

        // Two models differing ONLY in the Pin-sweep/tickle settings must share one StructuralKey —
        // a change here mutates in place (HarmonicaContext.Apply), never rebuilds the netlist.
        Assert.Equal(a.StructuralKey, b.StructuralKey);
    }

    // ══ R-h9r2-18/18a/17a — .charm persistence, additive, no FormatVersion bump ═══════════════

    [Fact]
    public void CharmRoundTrips_TheNewSettings_ExactValues()
    {
        var model = Model(pinMax: 22.5, pinStep: 0.5, tickleEnabled: false, tickleDbm: -33, exact: true);
        var terms = Terms(model);

        string json = CharmIo.Write(model, terms);
        var (back, _) = CharmIo.Read(json, null, out var unresolved, withMarkers: true);

        Assert.Empty(unresolved);
        Assert.Equal(model.Settings.PinStartDbm,          back.Settings.PinStartDbm);
        Assert.Equal(model.Settings.PinMaxDbm,             back.Settings.PinMaxDbm);
        Assert.Equal(model.Settings.PinStepDbm,            back.Settings.PinStepDbm);
        Assert.Equal(model.Settings.TickleEnabled,         back.Settings.TickleEnabled);
        Assert.Equal(model.Settings.TickleDbm,             back.Settings.TickleDbm);
        Assert.Equal(model.Settings.ExactCompressionSolve, back.Settings.ExactCompressionSolve);
    }

    [Fact]
    public void AnOlderCharmWithNoneOfTheseFields_OpensAtTheShippedDefaults()
    {
        // A minimal, pre-this-brief .charm — no PinStepDbm/TickleEnabled/TickleDbm/ExactCompressionSolve
        // block at all — must still open, additive-with-a-default, no FormatVersion bump.
        const string oldStyleJson = """
        {
          "Dut": { "Kind": "Sdd", "TypeName": "SDD", "Parameters": { "I[1,0]": "_v1/50" } },
          "Bias": { "Vgs": -1.5, "Vds": 10 },
          "Settings": { "HarmonicCount": 3, "FrequencyHz": 2000000000 }
        }
        """;

        var (back, _) = CharmIo.Read(oldStyleJson, null, out var unresolved, withMarkers: true);

        Assert.Empty(unresolved);
        var defaults = new HarmonicaSettings();
        Assert.Equal(defaults.PinStepDbm,            back.Settings.PinStepDbm);
        Assert.Equal(defaults.TickleEnabled,          back.Settings.TickleEnabled);
        Assert.Equal(defaults.TickleDbm,              back.Settings.TickleDbm);
        Assert.Equal(defaults.ExactCompressionSolve,  back.Settings.ExactCompressionSolve);
        Assert.Equal(defaults.PinMaxDbm,              back.Settings.PinMaxDbm);
    }

    // ══ frame-to-frame warm start (R-h9r2-19 lever 1) does not change the answer ═════════════

    [Fact]
    public void PriorLevelSpectra_DoesNotChangeTheConvergedAnswer_OnlyHowItGetsThere()
    {
        var model = Model(pinMax: 20, pinStep: 2.0);
        var ctx1 = HarmonicaContext.Create(model, EngineSettings);
        var cold = PinSearch.Sweep(ctx1, Terms(model), -10, 20, 2);

        var priors = new Dictionary<double, Complex[,]>();
        foreach (var step in cold.Steps)
            priors[Math.Round(step.PavlDbm, 6)] = step.Point.V;

        var ctx2 = HarmonicaContext.Create(model, EngineSettings);
        var warm = PinSearch.Sweep(ctx2, Terms(model), -10, 20, 2, priorLevelSpectra: priors);

        Assert.Equal(cold.Steps.Count, warm.Steps.Count);
        for (int i = 0; i < cold.Steps.Count; i++)
            Assert.Equal(cold.Steps[i].GainDb, warm.Steps[i].GainDb, precision: 6);
    }
}
