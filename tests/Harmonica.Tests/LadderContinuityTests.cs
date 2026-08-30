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
//
//  WHAT §2 NOW MEASURES, AND WHY IT IS LESS (HB-P3/HB-P4, 2026-08-30).
//  The nonphysical root is gone before the continuity guard is ever consulted. The HB Newton's
//  backtracking line search (HbNewton.Backtrack, which HarmonicaContext.Solve goes through) refuses
//  exactly the step that reached it: a full Newton step that increases ‖F‖. Measured over the whole
//  37-point default ring grid under Class F at K = 3, at PinStep 2, 3, 4, 6 and 8 dB, guard on and
//  guard off: zero steps with DE > 100%, zero continuations, zero holes, max DE 80.6–82.3% and max
//  Pout 40.4 dBm throughout. There is no step size at which this fixture still exhibits the defect,
//  so the vacuity assertion §2 opened with ("without the guard the ladder is supposed to land on the
//  nonphysical root") could not be restored by coarsening the ladder.
//
//  The guard is NOT thereby redundant and is deliberately left in place: a line search only refuses a
//  step that fails to reduce ‖F‖, and nothing about a monotone descent forbids descending onto a
//  different branch. Its predicate is still gated directly by IsDiscontinuous_MeasuresPoutAgainstItsOwnPinStep
//  below; what is now unexercised is the guard FIRING on a real fixture, which is a real loss of
//  coverage and is recorded here rather than hidden by a test that still reads as if it proved
//  something. A fixture that jumps branch under the line search would restore it; none is known.
//  This is the same supersession recorded for the Engine-side twin, LoadpullLadderContinuityTests.
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
    /// THE OWNER'S OWN CASE. One Γ point of the shipped default's 37-point grid under Class F at
    /// K = 3, walked at the contour grid's own 2 dB ladder. It used to converge onto a root drawing
    /// 353 kW from a 48 V supply and reporting DE = 251%, and the continuity guard was what pulled it
    /// back onto the physical branch.
    ///
    /// <para>SUPERSEDED BY HB-P3/HB-P4 (2026-08-30) — read the file header's "what §2 now measures"
    /// note. The nonphysical root is unreachable here now: the Newton line search refuses the step
    /// that got there, before the guard is consulted. So the two assertions this test was built on —
    /// that the unguarded ladder is WRONG, and that the guard FIRES — are both false today, and no
    /// PinStep from 2 to 8 dB restores them. What is asserted instead is the pair of facts that
    /// survive, and they are the ones a user cares about: the 2 dB ladder stays physical on its own,
    /// and it lands on the same branch the 1 dB ladder walks.</para>
    ///
    /// <para>Note what is asserted about the answer: not "it is close to some stored number" but that
    /// it obeys conservation of energy (DE &lt;= 100% — no more RF out than DC in, with no other source
    /// in the circuit). That is what keeps this a physics gate rather than a tuned threshold.</para>
    /// </summary>
    [Fact]
    public void TheClassFGridPointThatMadeTheContourIslands_StaysPhysicalAtTheGridsOwn2dBLadder()
    {
        var gamma = new Complex(-0.267, 0.462);

        var tOff = ClassF(3);
        SetLoadFundamental(tOff, gamma);
        var off = PinSearch.Sweep(HarmonicaContext.Create(Model(3, continuityMarginDb: 0.0)),
                                  tOff, -10, 34, 2.0);

        var tOn = ClassF(3);
        SetLoadFundamental(tOn, gamma);
        var on = PinSearch.Sweep(HarmonicaContext.Create(Model(3)), tOn, -10, 34, 2.0);

        Assert.Equal(PinStopReason.Compression, off.Reason);
        Assert.Equal(PinStopReason.Compression, on.Reason);

        // The unguarded 2 dB ladder is physical the whole way up — this is the half that the line
        // search now delivers on its own, and it is what the guard used to have to repair.
        foreach (var st in off.Steps)
        {
            Assert.True(st.De <= 1.0, $"DE {st.De:P1} at {st.PavlDbm:F0} dBm is more power out than in");
            Assert.True(PoutDbm(st) < 45, $"Pout {PoutDbm(st):F1} dBm at {st.PavlDbm:F0} dBm is not this device");
        }

        // …so the guard has nothing to remove here: it neither fires nor moves the answer nor costs a
        // solve. (Its predicate is gated directly by IsDiscontinuous_MeasuresPoutAgainstItsOwnPinStep.)
        Assert.Equal(0, off.Continuations);
        Assert.Equal(0, on.Continuations);
        Assert.Equal(off.Solves, on.Solves);
        Assert.Equal(off.Steps.Count, on.Steps.Count);
        for (int i = 0; i < off.Steps.Count; i++)
            Assert.Equal(PoutDbm(off.Steps[i]), PoutDbm(on.Steps[i]), precision: 12);

        // …and it is the SAME branch the 1 dB ladder walks, which is the independent check that the
        // coarse ladder is on the physical answer rather than merely a self-consistent wrong one.
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
