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
                                      bool exact = false, double sweepOverdriveDb = 0.0) => new()
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
            SweepOverdriveDb = sweepOverdriveDb,
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
    public void DefaultRange_Is61Points_InclusiveAtBothEnds_WhenOverdriveNeverCatchesUp()
    {
        // brief-harmonicarf-r4 §1: since this fixture DOES cross compression well before 50 dBm
        // (~27 dBm, see LoadpullHoleDiagnosticTests / RESOLVED.md §3), a huge overdrive margin is what
        // keeps the early stop from firing here — this test is about R-h9r2-17's ladder CONSTRUCTION
        // (inclusive at both ends, no decimation), not about §1's new stop behaviour, which has its
        // own tests below.
        var model = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 1000.0);
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
        // sweepOverdriveDb: 1000 — this is a ladder-CONSTRUCTION test (R-h9r2-17), not a §1 early-stop
        // test; see DefaultRange_...WhenOverdriveNeverCatchesUp's own remark.
        var model = Model(pinMax: 50, pinStep: 7.0, sweepOverdriveDb: 1000.0);
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
    public void ExactCompressionSolve_Default_IsOn()
        // R9A §8 — flipped from off to on.
        => Assert.True(new HarmonicaSettings().ExactCompressionSolve);

    [Fact]
    public void ExactCompressionSolve_RoundTripsExplicitlyOff_EvenThoughTheNewDefaultIsOn()
    {
        // R9A §8 — the persisted value must win over the new C# default: a document saved with it
        // explicitly off (pre-R9A, or an owner who turned it off) must not silently flip on at load.
        var model = Model(pinMax: 22.5, pinStep: 0.5, exact: false);
        var terms = Terms(model);

        string json = CharmIo.Write(model, terms);
        var (back, _) = CharmIo.Read(json, null, out var unresolved, withMarkers: true);

        Assert.Empty(unresolved);
        Assert.False(back.Settings.ExactCompressionSolve);
    }

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
        var model = Model(pinMax: 22.5, pinStep: 0.5, tickleEnabled: false, tickleDbm: -33, exact: true,
                          sweepOverdriveDb: 2.5);
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
        Assert.Equal(model.Settings.SweepOverdriveDb,      back.Settings.SweepOverdriveDb);
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
        Assert.Equal(defaults.SweepOverdriveDb,       back.Settings.SweepOverdriveDb);
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

    // ══ brief-harmonicarf-r4 §1 — the sweep stops at the compression target ═══════════════════

    [Fact]
    public void DefaultOverdrive_IsZero()
        => Assert.Equal(0.0, new HarmonicaSettings().SweepOverdriveDb, precision: 9);

    [Fact]
    public void WithZeroOverdrive_TheLadderStopsOnTheFirstRungThatReachesTarget()
    {
        var model = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 0.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 50, 1);

        Assert.Equal(PinStopReason.Compression, r.Reason);
        Assert.True(r.Steps.Count < 61, "the ladder should have stopped short of Stop=50 dBm");
        // The last solved rung is the FIRST one whose compression reached the target — every rung
        // before it must be strictly below.
        Assert.True(r.Steps[^1].Compression >= model.Settings.CompressionDb - 1e-9);
        for (int i = 0; i < r.Steps.Count - 1; i++)
            Assert.True(r.Steps[i].Compression < model.Settings.CompressionDb + 1e-9);
        output.WriteLine($"stopped at {r.Steps.Count} of 61 rungs, last Pin={r.Steps[^1].PavlDbm} dBm, " +
                         $"compression={r.Steps[^1].Compression:F3} dB");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void OverdriveMargin_ExtendsTheLadderByExactlyThatMuchCompression(double overdriveDb)
    {
        var model = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: overdriveDb);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 50, 1);

        Assert.Equal(PinStopReason.Compression, r.Reason);
        double lastCompression = r.Steps[^1].Compression;
        Assert.True(lastCompression >= model.Settings.CompressionDb + overdriveDb - 1e-9,
                    $"last rung's compression {lastCompression:F3} dB did not reach the overdrive target");
        // Never overshoots by a whole rung's worth (1 dB Pin step) more than necessary — the SECOND
        // to last rung must still be short of the overdrive target, or the ladder stopped later than
        // it needed to.
        if (r.Steps.Count > 1)
            Assert.True(r.Steps[^2].Compression < model.Settings.CompressionDb + overdriveDb + 1e-9);
        output.WriteLine($"overdrive={overdriveDb} dB: {r.Steps.Count} rungs, last compression=" +
                         $"{lastCompression:F3} dB");
    }

    [Fact]
    public void LargerOverdrive_NeverSolvesFewerRungsThanASmallerOne()
    {
        var m0 = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 0.0);
        var m2 = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 2.0);
        var m3 = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 3.0);

        var r0 = PinSearch.Sweep(HarmonicaContext.Create(m0, EngineSettings), Terms(m0), -10, 50, 1);
        var r2 = PinSearch.Sweep(HarmonicaContext.Create(m2, EngineSettings), Terms(m2), -10, 50, 1);
        var r3 = PinSearch.Sweep(HarmonicaContext.Create(m3, EngineSettings), Terms(m3), -10, 50, 1);

        Assert.True(r0.Steps.Count <= r2.Steps.Count);
        Assert.True(r2.Steps.Count <= r3.Steps.Count);
        output.WriteLine($"0dB={r0.Steps.Count} 2dB={r2.Steps.Count} 3dB={r3.Steps.Count} rungs " +
                         $"(of 61 full-range); solves 0dB={r0.Solves} 2dB={r2.Solves} 3dB={r3.Solves}");
    }

    [Fact]
    public void ANeverCrossingSweep_IgnoresOverdrive_StillRunsToStop()
    {
        // Mirrors ARangeThatNeverCompresses_ReportsPinMax_NotAnError but with a non-zero overdrive
        // margin, to prove the "never crosses -> full range" guarantee is independent of the margin.
        var model = Model(pinMax: 5, pinStep: 1.0, sweepOverdriveDb: 3.0);
        var ctx = HarmonicaContext.Create(model, EngineSettings);
        var r = PinSearch.Sweep(ctx, Terms(model), -10, 5, 1);

        Assert.Equal(PinStopReason.PinMax, r.Reason);
        Assert.Equal(16, r.Steps.Count);
    }

    [Fact]
    public void EarlyStop_DoesNotMoveTheCrossingBracketOrTheInterpolatedReading()
    {
        // The crossing pair (and therefore SweepCompression's interpolated figures) must be identical
        // whether the ladder stops right after it (overdrive 0) or keeps going (overdrive large) — the
        // early stop happens STRICTLY AFTER the crossing is recorded and must not perturb it.
        var stopModel = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 0.0);
        var fullModel = Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: 1000.0);

        var rStop = PinSearch.Sweep(HarmonicaContext.Create(stopModel, EngineSettings), Terms(stopModel), -10, 50, 1);
        var rFull = PinSearch.Sweep(HarmonicaContext.Create(fullModel, EngineSettings), Terms(fullModel), -10, 50, 1);

        Assert.NotNull(rStop.SweepCompression);
        Assert.NotNull(rFull.SweepCompression);
        Assert.Equal(rFull.SweepCompression!.PinDbm,  rStop.SweepCompression!.PinDbm,  precision: 9);
        Assert.Equal(rFull.SweepCompression.PoutDbm, rStop.SweepCompression.PoutDbm, precision: 9);
        Assert.Equal(rFull.SweepCompression.GainDb,  rStop.SweepCompression.GainDb,  precision: 9);
        Assert.Equal(rFull.SweepCompression.De,      rStop.SweepCompression.De,      precision: 9);
        Assert.Equal(rFull.SweepCompression.Pae,     rStop.SweepCompression.Pae,     precision: 9);
        Assert.True(rStop.Steps.Count < rFull.Steps.Count);
    }

    /// <summary>§1.4's own gate, on the SHIPPED DEFAULT document (<c>HarmonicaViewModel.DefaultModel</c>
    /// — same DUT/bias this class's own <c>Model()</c> factory mirrors — with <c>PinMaxDbm</c> forced
    /// to <c>HarmonicaSettings</c>' own default of 50, exactly as brief-harmonicarf-r4's own preamble
    /// specifies). Reports before/after solve counts at 0/2/3 dB overdrive for the completion note.</summary>
    [Fact]
    public void Gate_ShippedDefault_BeforeAfterSolveCounts()
    {
        CircuitModel At(double overdriveDb) => Model(pinMax: 50, pinStep: 1.0, sweepOverdriveDb: overdriveDb);

        var before = At(1000.0);   // stands in for "never stops early" — the pre-§1 behaviour
        var r0 = PinSearch.Sweep(HarmonicaContext.Create(before, EngineSettings), Terms(before), -10, 50, 1);

        var m0 = At(0.0);
        var r1 = PinSearch.Sweep(HarmonicaContext.Create(m0, EngineSettings), Terms(m0), -10, 50, 1);
        var m2 = At(2.0);
        var r2 = PinSearch.Sweep(HarmonicaContext.Create(m2, EngineSettings), Terms(m2), -10, 50, 1);
        var m3 = At(3.0);
        var r3 = PinSearch.Sweep(HarmonicaContext.Create(m3, EngineSettings), Terms(m3), -10, 50, 1);

        output.WriteLine("§1.4 gate — shipped default, PinMaxDbm=50, CompressionDb=3:");
        output.WriteLine($"  BEFORE (runs to Stop, R-h9r2-19 as originally written): {r0.Steps.Count} rungs, {r0.Solves} solves, last Pin={r0.Steps[^1].PavlDbm} dBm, compression at last rung={r0.Steps[^1].Compression:F3} dB");
        output.WriteLine($"  AFTER, overdrive=0dB: {r1.Steps.Count} rungs, {r1.Solves} solves, last Pin={r1.Steps[^1].PavlDbm} dBm, compression={r1.Steps[^1].Compression:F3} dB, SweepCompression.PinDbm={r1.SweepCompression?.PinDbm:F3}");
        output.WriteLine($"  AFTER, overdrive=2dB: {r2.Steps.Count} rungs, {r2.Solves} solves, last Pin={r2.Steps[^1].PavlDbm} dBm, compression={r2.Steps[^1].Compression:F3} dB");
        output.WriteLine($"  AFTER, overdrive=3dB: {r3.Steps.Count} rungs, {r3.Solves} solves, last Pin={r3.Steps[^1].PavlDbm} dBm, compression={r3.Steps[^1].Compression:F3} dB");

        Assert.True(r1.Solves < r0.Solves);
        Assert.Equal(r0.SweepCompression!.PinDbm, r1.SweepCompression!.PinDbm, precision: 6);
    }

    [Fact]
    public void MaxSweepPointsValidation_IsUnaffectedByEarlyStop_ValidatesTheFullRequestedRange()
    {
        // §1.2's own rule: a range that would exceed MaxSweepPoints must be refused BY NAME before
        // any solving starts — the validator never even sees a "would stop early" concept, it only
        // ever sees Start/Stop/Step.
        Assert.False(PowerSweepValidation.IsValidRange(-10, 50, 0.001, out int count));
        Assert.True(count > HarmonicaSettings.MaxSweepPoints);
    }
}
