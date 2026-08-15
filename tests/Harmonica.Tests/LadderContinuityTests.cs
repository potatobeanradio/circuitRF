// ================================================================
//  LadderContinuityTests.cs — Round 11 §1/§2
//
//  Two defects, both of them a drive-up ladder taking a step it could not take:
//
//   §1  A prior-FRAME level spectrum solved at a different harmonic order has the same Pin-level
//       keys and a different SHAPE. PinSearch.Sweep handed it to HarmonicaContext.Solve anyway,
//       Solve silently fell back to the cold DC seed, and the ladder lost its rung-to-rung warm
//       start for EVERY rung — which is what made the owner's K = 3 → 5 → 3 round trip land on a
//       different drive-up from the K = 3 it started at.
//
//   §2  The contour grid's own 2 dB ladder converged, at four Γ points of the shipped default under
//       the Class F preset, onto a NONPHYSICAL root of the same residual (Pout 89 dBm, Pdc 353 kW,
//       DE 251%) in one step. ‖F‖ ≈ 2e-9, so it was reported converged and entered the contour fit
//       as ordinary data — the "contour islands" the owner saw.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

public sealed class LadderContinuityTests
{
    // The shipped default document's own device and bias (HarmonicaViewModel.DefaultModel), in the
    // folded-coefficient form LoadpullHoleDiagnosticTests already uses for the same device —
    // transcribed rather than referenced, because this project may not see src/Ui.
    private const string I1Expr = "_v1/50";
    private const string I2Expr =
        "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
        "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";

    private const double Z0 = 80.0;

    private static CircuitModel Model(int k, double continuityMarginDb = 3.0) => new()
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
            HarmonicCount = k, FrequencyHz = 2e9, Tol = 1e-8, Z0 = Z0,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
            LadderContinuityMarginDb = continuityMarginDb,
        },
    };

    /// <summary>The Class F preset's own load terminations at this Z0 — band 1 at 2·Z0/√3, then
    /// alternating near-open / near-short. Written straight in: the preset's arithmetic is
    /// <c>PaClassPresetsTests</c>' business, and this file is about the ladder.</summary>
    private static TerminationSet ClassF(int k)
    {
        var t = new TerminationSet(k);
        t.Set(TerminationSide.Source, 1, new Complex(50, 0));
        for (int band = 1; band <= k; band++)
            t.Set(TerminationSide.Load, band, PaClassPresets.IntrinsicLoad(PaClass.F, band, Z0));
        return t;
    }

    private static void SetLoadFundamental(TerminationSet t, Complex gamma)
        => t.Set(TerminationSide.Load, 1, Z0 * (Complex.One + gamma) / (Complex.One - gamma));

    private static double PoutDbm(PinStep s) => 10 * Math.Log10(s.PoutW) + 30;

    // ══ §1 — a prior-frame spectrum of the WRONG harmonic count ═════════════════════════════════

    /// <summary>
    /// The defect in one assertion. A K = 5 frame's level spectra keyed by Pin are a perfect lookup
    /// hit at every rung of a K = 3 ladder and a useless seed at all of them; before the fix, every
    /// rung silently cold-started and the sweep truncated on a non-convergent rung at 12 dBm where the
    /// warm-started identical circuit reaches 26.
    /// </summary>
    [Fact]
    public void APriorFrameSpectrumAtADifferentK_IsIgnored_AndTheLadderStillWarmStarts()
    {
        var s = ClassF(5);
        var wrongShape = new Dictionary<double, Complex[,]>();
        var ctx5 = HarmonicaContext.Create(Model(5));
        foreach (var st in PinSearch.Sweep(ctx5, s, -10, 34, 1.0).Steps)
            wrongShape[Math.Round(st.PavlDbm, 6)] = st.Point.V;
        Assert.NotEmpty(wrongShape);

        var t3   = ClassF(3);
        var ctx3 = HarmonicaContext.Create(Model(3));
        var clean = PinSearch.Sweep(ctx3, t3, -10, 34, 1.0);

        // A second context, so neither run can inherit the other's cached DC seed.
        var seeded = PinSearch.Sweep(HarmonicaContext.Create(Model(3)), t3, -10, 34, 1.0,
                                     priorLevelSpectra: wrongShape);

        Assert.Equal(clean.Reason, seeded.Reason);
        Assert.Equal(clean.Steps.Count, seeded.Steps.Count);
        for (int i = 0; i < clean.Steps.Count; i++)
            Assert.Equal(PoutDbm(clean.Steps[i]), PoutDbm(seeded.Steps[i]), precision: 6);
    }

    [Fact]
    public void AcceptsWarmStart_IsShapeExact_OnBothAxes()
    {
        var ctx = HarmonicaContext.Create(Model(3));
        int n = ctx.Interface.InterfaceCount;

        Assert.True(ctx.AcceptsWarmStart(new Complex[n, 4]));      // N × (K+1)
        Assert.False(ctx.AcceptsWarmStart(null));
        Assert.False(ctx.AcceptsWarmStart(new Complex[n, 6]));     // a K = 5 spectrum
        Assert.False(ctx.AcceptsWarmStart(new Complex[n, 3]));
        Assert.False(ctx.AcceptsWarmStart(new Complex[n + 1, 4]));
    }

    // ══ §2 — the continuity guard ═══════════════════════════════════════════════════════════════

    [Theory]
    // ΔPin = 2 dB. Pout moving 2 dB with it is an ordinary rung; 5.1 dB is past the 3 dB margin.
    [InlineData(20.0, 22.0, 3.0, false)]
    [InlineData(20.0, 25.0, 3.0, false)]
    [InlineData(20.0, 25.1, 3.0, true)]
    [InlineData(20.0, 14.9, 3.0, true)]   // a collapse is just as impossible as an expansion
    [InlineData(20.0, 90.0, 0.0, false)]  // margin 0 disables the guard entirely
    public void IsDiscontinuous_MeasuresPoutAgainstItsOwnPinStep(
        double poutLoDbm, double poutHiDbm, double marginDb, bool expected)
    {
        var lo = FakeStep(20.0, poutLoDbm);
        var hi = FakeStep(22.0, poutHiDbm);
        Assert.Equal(expected, PinSearch.IsDiscontinuous(lo, hi, marginDb));
    }

    /// <summary>
    /// THE OWNER'S OWN CASE, as a gate. One Γ point of the shipped default's 37-point grid under
    /// Class F at K = 3, walked at the contour grid's own 2 dB ladder. Without the guard the ladder
    /// converges onto a root drawing 353 kW from a 48 V supply and reporting DE = 251%; with it, the
    /// same 2 dB ladder reaches the same answer the 1 dB ladder does.
    ///
    /// <para>Note what is asserted about the BAD answer: not "it is far from the good one" but that
    /// it violates conservation of energy (DE &gt; 100% — more RF out than DC in, with no other source
    /// in the circuit). That is what makes this a physics gate rather than a tuned threshold.</para>
    /// </summary>
    [Fact]
    public void TheClassFGridPointThatMadeTheContourIslands_IsRecoveredByContinuation()
    {
        var gamma = new Complex(-0.267, 0.462);

        var tOff = ClassF(3);
        SetLoadFundamental(tOff, gamma);
        var off = PinSearch.Sweep(HarmonicaContext.Create(Model(3, continuityMarginDb: 0.0)),
                                  tOff, -10, 34, 2.0);

        Assert.Equal(PinStopReason.Compression, off.Reason);
        var worstOff = off.Steps[^1];
        Assert.True(worstOff.De > 1.0,
            $"the unguarded 2 dB ladder is expected to land on the nonphysical root, but DE was {worstOff.De:P1}");
        Assert.True(PoutDbm(worstOff) > 60,
            $"...and its Pout with it, but it was {PoutDbm(worstOff):F1} dBm");
        Assert.Equal(0, off.Continuations);

        var tOn = ClassF(3);
        SetLoadFundamental(tOn, gamma);
        var on = PinSearch.Sweep(HarmonicaContext.Create(Model(3)), tOn, -10, 34, 2.0);

        Assert.Equal(PinStopReason.Compression, on.Reason);
        Assert.True(on.Continuations > 0, "the guard is expected to fire on this point");
        foreach (var st in on.Steps)
        {
            Assert.True(st.De <= 1.0, $"DE {st.De:P1} at {st.PavlDbm:F0} dBm is more power out than in");
            Assert.True(PoutDbm(st) < 45, $"Pout {PoutDbm(st):F1} dBm at {st.PavlDbm:F0} dBm is not this device");
        }

        // …and it is the SAME branch the 1 dB ladder walks unaided, which is the independent check
        // that the guard recovers the physical answer rather than merely a different wrong one.
        var tFine = ClassF(3);
        SetLoadFundamental(tFine, gamma);
        var fine = PinSearch.Sweep(HarmonicaContext.Create(Model(3, continuityMarginDb: 0.0)),
                                   tFine, -10, 34, 1.0);
        Assert.Equal(PinStopReason.Compression, fine.Reason);
        Assert.Equal(PoutDbm(fine.SweepCompression!.Spectrum), PoutDbm(on.SweepCompression!.Spectrum), precision: 0);
    }

    /// <summary>An ordinary drive-up pays nothing: no continuation fires, and the answer is
    /// bit-identical to the same sweep with the guard switched off.</summary>
    [Fact]
    public void AnOrdinaryDriveUp_NeitherFiresTheGuardNorMovesTheAnswer()
    {
        var t = ClassF(3);
        var on  = PinSearch.Sweep(HarmonicaContext.Create(Model(3)), ClassF(3), -10, 34, 1.0);
        var off = PinSearch.Sweep(HarmonicaContext.Create(Model(3, continuityMarginDb: 0.0)), t, -10, 34, 1.0);

        Assert.Equal(0, on.Continuations);
        Assert.Equal(off.Solves, on.Solves);
        Assert.Equal(off.Steps.Count, on.Steps.Count);
        for (int i = 0; i < off.Steps.Count; i++)
            Assert.Equal(PoutDbm(off.Steps[i]), PoutDbm(on.Steps[i]), precision: 12);
    }

    private static PinStep FakeStep(double pinDbm, double poutDbm)
    {
        double poutW = Math.Pow(10, (poutDbm - 30) / 10);
        return new PinStep(pinDbm, 0.0, null!)
        {
            Foms  = new CircuitRF.Engine.Loadpull.LoadpullEngine.FomResult(
                        PavlW: 0, PinDeliveredW: 0, PoutW: poutW, GtDb: 0, GpDb: 0),
            PdcW  = 1.0,
            GainDb = poutDbm - pinDbm,
        };
    }
}
