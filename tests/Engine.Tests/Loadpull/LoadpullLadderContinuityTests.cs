// ================================================================
//  LoadpullLadderContinuityTests.cs — Round 11
//
//  Three facts, and the first two pull in opposite directions on purpose:
//    1. The guard does not move the frozen heroes (it never fires at their 1 dB PinStep).
//    2. It is not therefore inert — on a Class F fixture at a coarse step it removes every GROSS
//       energy-violating grid point the unguarded ladder produces.
//    3. Against an ACTIVE (negative-real) termination the energy screen goes silent, because no
//       energy bound is computable there — while the continuity guard, which tests smoothness rather
//       than a budget, keeps working.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

public sealed class LoadpullLadderContinuityTests(ITestOutputHelper output)
{
    private static string Hero3Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero3");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero3 not found");
    }

    private readonly record struct Run(
        int Points, int ActivePoints, int EnergyViolatingPoints, int GrossViolations,
        int NonConvergent, int Continuations, int Solves, double MaxPae, int Warnings);

    /// <summary>Runs one loadpull directive over its whole grid and reports what the guard did.</summary>
    private static Run Sweep(string cnlFile, double? pinStepOverride = null, double? marginOverride = null)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero3Dir(), cnlFile));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();

        var p = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
        if (pinStepOverride is { } st) p = p with { PinStepDb = st };
        if (marginOverride  is { } m)  p = p with { ContinuityMarginDb = m };

        var eng = new LoadpullEngine(netlist, tb);
        var ctx = eng.PrepareContext(p);

        int bad = 0, gross = 0, nonconv = 0, cont = 0, solves = 0, active = 0;
        double maxPae = 0;
        for (int i = 0; i < p.Grid.Points.Count; i++)
        {
            var g = eng.RunOneTermination(p, ctx, p.Grid.Points[i].Z, i);
            cont   += g.Continuations;
            solves += g.PinSteps.Count;
            if (g.StopReason == "NonConvergence") nonconv++;
            if (g.HasActiveTermination) active++;

            // PAE, not DE. Pout ≤ Pdc + Pin_delivered + P_active; with every termination passive that
            // rearranges to PAE ≤ 1 exactly, whereas DE ≤ 1 does NOT follow — a low-gain stage driven
            // hard can legitimately put out more than its DC input.
            //
            // GROSS is a separate count on purpose: a runaway root reports efficiencies in the
            // THOUSANDS of percent, whereas a coarse ladder that merely drifts off the branch lands a
            // percent or two over. Different causes, and only one of them is a basin jump.
            var rungs = g.PinSteps.Where(s => s.Converged && !s.IsTickle && s.PdcW > 1e-9).ToList();
            if (rungs.Any(s => s.Pae > 1.0)) bad++;
            if (rungs.Any(s => s.Pae > 1.5)) gross++;
            foreach (var s in rungs) maxPae = Math.Max(maxPae, s.Pae);
        }
        return new Run(p.Grid.Points.Count, active, bad, gross, nonconv, cont, solves, maxPae,
                       netlist.Warnings.Count);
    }

    /// <summary>
    /// The goldens-are-safe gate. Hero 3 runs at <c>PinStep=1</c>, which is measured to follow the
    /// physical branch unaided — so the guard must never fire there, and the frozen golden cannot have
    /// moved. If this ever goes non-zero, the Hero 3 golden needs re-verifying and this test is the
    /// thing that says so.
    /// </summary>
    [Theory]
    [InlineData("hero3.cnl")]
    [InlineData("hero3_at_compression.cnl")]
    public void TheFrozenHeroes_NeverFireTheGuard(string cnlFile)
    {
        var r = Sweep(cnlFile);
        output.WriteLine($"{cnlFile}: continuations={r.Continuations} nonconvergent={r.NonConvergent} " +
                         $"PAE>100% points={r.EnergyViolatingPoints} solves={r.Solves}");

        Assert.Equal(0, r.Continuations);
        Assert.Equal(0, r.EnergyViolatingPoints);
        Assert.Equal(0, r.NonConvergent);
    }

    /// <summary>
    /// …and it is not inert. Class F puts a near-open at 3f₀, and at that fixture's 2 dB drive step the
    /// unguarded ladder converges — at a residual that passes the tolerance test — onto roots reporting
    /// efficiencies in the <i>thousands</i> of percent. Measured on its 20-point grid:
    /// <b>11 energy-violating points become 1, and every GROSS one is gone.</b>
    ///
    /// <para><b>The survivor is recorded rather than tuned away, because it is a different defect.</b>
    /// At Γ = 0.6 (Z = 200 Ω) the 2 dB ladder is perfectly continuous — Pout tracks Pin at 1:1 or less
    /// on every rung, so the guard correctly never fires — and it simply arrives at DE = 104.3% by the
    /// last rung. That is a coarse ladder drifting off the physical branch, not jumping to another one;
    /// the same fixture walked at a guarded 0.5 dB or 0.25 dB has none. <b>The continuity guard catches
    /// basin jumps and does not claim to catch drift</b>, and a gate that asserted zero here would be
    /// asserting something this mechanism was never built to deliver.</para>
    ///
    /// <para>The oracle is <b>energy conservation</b>, not agreement with a finer ladder: guarded 0.5 dB
    /// and guarded 0.25 dB walks of this fixture agree to 0.05 dB, but a coarse ladder's last rung
    /// legitimately differs from a fine one's, so "close to the reference" would be a tolerance to tune
    /// rather than a fact to assert.</para>
    /// </summary>
    [Fact]
    public void AClassFLoadAtACoarseStep_LandsOnNonphysicalRoots_UntilTheGuardRunsIt()
    {
        var off = Sweep("hero3_classF.cnl", marginOverride: 0.0);
        var on  = Sweep("hero3_classF.cnl");

        output.WriteLine($"guard OFF: PAE>100% points={off.EnergyViolatingPoints} (gross {off.GrossViolations}) " +
                         $"nonconv={off.NonConvergent} solves={off.Solves}");
        output.WriteLine($"guard ON : PAE>100% points={on.EnergyViolatingPoints} (gross {on.GrossViolations}) " +
                         $"nonconv={on.NonConvergent} continuations={on.Continuations} solves={on.Solves}");

        Assert.True(off.GrossViolations > 0,
            "the fixture is supposed to be WRONG without the guard — if it is not, it has stopped testing anything");

        // Every runaway root is gone…
        Assert.Equal(0, on.GrossViolations);
        // …and the guard did the work rather than the fixture having changed under it.
        Assert.True(on.Continuations > 0);
        // …and what is left is the single drift case above, not a regression back toward the 11.
        Assert.True(on.EnergyViolatingPoints <= 1,
            $"expected at most the one known drift point, got {on.EnergyViolatingPoints}");
        Assert.True(on.EnergyViolatingPoints < off.EnergyViolatingPoints);
    }

    /// <summary>
    /// <b>A NEGATIVE-REAL termination is a supported research capability, and every energy screen must
    /// go silent against one.</b> With an active termination the balance is
    /// <c>Pout ≤ Pdc + Pin_delivered + P_active</c>; the engine does not compute <c>P_active</c>, so
    /// there is no bound left to test and PAE above 100% is perfectly physical rather than a symptom.
    /// A screen that fired here would break exactly the negative-resistance PA work the capability
    /// exists for — the engine's warning and the pursuit's unscorable rule both read
    /// <see cref="GridPointResult.HasActiveTermination"/> for that reason.
    ///
    /// <para><b>The CONTINUITY guard is a separate question and still applies</b>, because it tests
    /// smoothness along a branch rather than an energy budget — nothing about it assumes passivity.
    /// Measured on this fixture at its 2 dB step: guard off, one point lands on a root reporting
    /// PAE = 33,729%; guard on, that is gone and the answer agrees with a guarded 0.5 dB walk to 0.1
    /// percentage point (82.1% against 82.2%).</para>
    /// </summary>
    [Fact]
    public void AnActiveTermination_SilencesTheEnergyScreen_ButNotTheContinuityGuard()
    {
        var off  = Sweep("hero3_classF_active.cnl", marginOverride: 0.0);
        var on   = Sweep("hero3_classF_active.cnl");
        var fine = Sweep("hero3_classF_active.cnl", pinStepOverride: 0.5);

        output.WriteLine($"guard OFF: active={off.ActivePoints}/{off.Points} PAE>100%={off.EnergyViolatingPoints} " +
                         $"maxPAE={off.MaxPae:P1} warnings={off.Warnings}");
        output.WriteLine($"guard ON : active={on.ActivePoints}/{on.Points} PAE>100%={on.EnergyViolatingPoints} " +
                         $"maxPAE={on.MaxPae:P1} continuations={on.Continuations} warnings={on.Warnings}");
        output.WriteLine($"fine 0.5 : maxPAE={fine.MaxPae:P1}");

        // Every point is recognised as active…
        Assert.Equal(off.Points, off.ActivePoints);
        // …so the engine stays silent even where PAE is far past 100%, which is the whole point.
        Assert.True(off.MaxPae > 1.0, "the fixture is supposed to reach PAE > 100% without the guard");
        Assert.Equal(0, off.Warnings);
        Assert.Equal(0, on.Warnings);

        // …and the continuity guard still does its job, judged against the fine guarded walk rather
        // than against any energy bound.
        Assert.True(on.Continuations > 0);
        Assert.True(on.MaxPae < 1.0,
            $"the guarded coarse walk should stay on the physical branch, but reached PAE {on.MaxPae:P1}");
        Assert.Equal(fine.MaxPae, on.MaxPae, precision: 2);
    }
}
