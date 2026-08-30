// ================================================================
//  HbDriveSteppingTests.cs — HB-P3 M2: DriveStepping does something.
//
//  `DriveStepping=` was parsed into HbAnalysisParams and read by nothing: the knob the design
//  reserved "for Phase-4 bring-up" was never wired. It now walks every tone source's drive up to the
//  requested level in 2 dB rungs, warm-starting each from the one below.
//
//  The fixture below needs a point the LINE SEARCH cannot rescue, since M1 catches nearly everything
//  the ramp was written for. Capping HbMaxIter is what does it: hero2_convergence at 20 dBm converges
//  cold in 6 iterations, so MaxIter=4 fails cold (‖F‖ = 1.31e-4) while every warm-started rung of the
//  ramp still fits inside 4. MaxIter=3 fails BOTH, which is the third case worth pinning — a ramp
//  that cannot reach the top must leave the cold result standing rather than publish a rung.
// ================================================================

using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public sealed class HbDriveSteppingTests(ITestOutputHelper output)
{
    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    private readonly record struct RunOut(
        bool Converged, IReadOnlyList<double> RungOffsets, Complex[,]? V,
        double MaxDriveScale, IReadOnlyList<string> Warnings);

    private static RunOut Run(double pavlDbm, int maxIter, DcBiasSteppingMode mode)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero2Dir(), "hero2_convergence.cnl"));
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int idx = tb.GlobalVariables.FindIndex(v => v.Name == "Pavl_dbm");
        tb.GlobalVariables[idx] =
            new Variable("Pavl_dbm", pavlDbm.ToString("G17", CultureInfo.InvariantCulture), null);

        using var netlist = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit)
                with { MaxIter = maxIter, DriveStepping = mode };
        var rr = new HbEngine(netlist, tb).Run(p);

        double scale = netlist.Components
            .Where(c => c.Model is IDriveScalable)
            .Select(c => ((IDriveScalable)c.Model).DriveScale)
            .DefaultIfEmpty(double.NaN).Max();

        return new RunOut(rr.Converged,
            rr.Trace!.Steps.Select(s => s.Pin_dBm).ToList(),
            rr.InterfaceV, scale, netlist.Warnings.ToList());
    }

    /// <summary>The manual ladder the ramp is supposed to reproduce: the drive expressed in the
    /// netlist's own dBm, chained warm. hero2's <c>|Vs| = √(8·P_avl·Re Z_s)</c> makes a d dB Pavl
    /// offset exactly the ramp's <c>10^(d/20)</c> voltage scale, so the two are the same walk taken
    /// through two different doors — which is what makes this an oracle rather than a re-run.</summary>
    private static Complex[,] ManualLadder(double targetDbm, int maxIter, double spanDb = 20.0,
        double stepDb = 2.0)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero2Dir(), "hero2_convergence.cnl"));
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int idx = tb.GlobalVariables.FindIndex(v => v.Name == "Pavl_dbm");

        Complex[,]? seed = null;
        foreach (double off in HbDriveRampOffsets(spanDb, stepDb))
        {
            tb.GlobalVariables[idx] = new Variable("Pavl_dbm",
                (targetDbm + off).ToString("G17", CultureInfo.InvariantCulture), null);
            using var netlist = new Elaborator(lib).Elaborate(tb);
            var p  = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit)
                     with { MaxIter = maxIter };
            var sp = new HbEngine(netlist, tb).RunSinglePoint(p, seed);
            Assert.True(sp.Converged, $"manual ladder rung {targetDbm + off} dBm did not converge");
            seed = sp.V;
        }
        return seed!;
    }

    private static double[] HbDriveRampOffsets(double spanDb, double stepDb)
    {
        int n = (int)Math.Ceiling(spanDb / stepDb);
        var o = new double[n + 1];
        for (int i = 0; i <= n; i++) o[i] = i == n ? 0.0 : -spanDb + i * (spanDb / n);
        return o;
    }

    private static double MaxAbsDiff(Complex[,] a, Complex[,] b)
    {
        double m = 0;
        for (int n = 0; n < a.GetLength(0); n++)
            for (int k = 0; k < a.GetLength(1); k++)
                m = Math.Max(m, (a[n, k] - b[n, k]).Magnitude);
        return m;
    }

    // ── 6. IfNecessary ramps only on failure ─────────────────────────────────

    [Fact]
    public void IfNecessary_RampsOnlyWhenTheColdSolveFails_AndLandsWhereAManualLadderDoes()
    {
        // Converges cold → exactly one step, at the requested drive, and no ramp at all.
        var easy = Run(pavlDbm: 20.0, maxIter: 100, DcBiasSteppingMode.IfNecessary);
        output.WriteLine($"cold-OK   : converged={easy.Converged} steps=[{string.Join(",", easy.RungOffsets)}]");
        Assert.True(easy.Converged);
        Assert.Equal([0.0], easy.RungOffsets);

        // Same point, MaxIter capped below what a cold solve needs → the ramp runs and rescues it.
        var never  = Run(20.0, maxIter: 4, DcBiasSteppingMode.Never);
        var ramped = Run(20.0, maxIter: 4, DcBiasSteppingMode.IfNecessary);
        output.WriteLine($"Never     : converged={never.Converged}");
        output.WriteLine($"IfNecessary: converged={ramped.Converged} " +
                         $"steps={ramped.RungOffsets.Count} rungs=[{string.Join(",", ramped.RungOffsets)}]");

        Assert.False(never.Converged);
        Assert.True(ramped.Converged, "the ramp should have reached the requested drive");

        // Step 0 is the cold attempt that failed; the rungs follow; the last one is the requested drive.
        Assert.Equal(0.0,  ramped.RungOffsets[0]);
        Assert.Equal(-20.0, ramped.RungOffsets[1]);
        Assert.Equal(0.0,  ramped.RungOffsets[^1]);
        Assert.True(ramped.RungOffsets.Count > 2);

        // The sources are back at the drive that was asked for.
        Assert.Equal(1.0, ramped.MaxDriveScale);

        // And it landed where walking the netlist's own Pavl_dbm to the same place lands.
        // The oracle walks the same ladder WITHOUT the artificial iteration cap, which exists only to
        // force the engine's ramp to fire at all: at MaxIter=4 the manual walk's own −6 dB rung fails
        // and the engine recovers it by subdividing (visible in the rung list above as the −7 … −6.25
        // run), so capping the oracle too would be comparing two different ladders. Both ends
        // converge to ‖F‖ < 1e-7, so they agree to convergence noise rather than bit-identically.
        double diff  = MaxAbsDiff(ramped.V!, ManualLadder(20.0, maxIter: 100));
        double scale = Math.Max(1.0, Enumerable.Range(0, ramped.V!.GetLength(0))
            .SelectMany(n => Enumerable.Range(0, ramped.V.GetLength(1)).Select(k => ramped.V[n, k].Magnitude))
            .Max());
        output.WriteLine($"ramp vs manual ladder: max |ΔV| = {diff:E3} V ({diff / scale:E2} relative)");
        Assert.True(diff / scale < 1e-6,
            $"ramp and manual ladder differ by {diff:E3} V ({diff / scale:E2} relative)");
    }

    /// <summary>
    /// A ramp that cannot reach the top publishes nothing of its own. At <c>MaxIter = 3</c> no rung
    /// converges either, and the point must report the COLD result's non-convergence rather than the
    /// spectrum of whatever rung it got to — design §11.1's rule that a wrong branch is never
    /// smuggled in as an answer.
    /// </summary>
    [Fact]
    public void ARampThatNeverReachesTheRequestedDrive_LeavesTheColdResultStanding()
    {
        var r = Run(20.0, maxIter: 3, DcBiasSteppingMode.IfNecessary);
        output.WriteLine($"converged={r.Converged} steps={r.RungOffsets.Count} finalScale={r.MaxDriveScale}");

        Assert.False(r.Converged);
        Assert.Equal(1.0, r.MaxDriveScale);
        Assert.Contains(r.Warnings, w => w.Contains("HB did not converge", StringComparison.Ordinal));
    }

    // ── 7. Always / Never ────────────────────────────────────────────────────

    [Fact]
    public void Always_RampsEvenWhenTheColdSolveWouldHaveWorked()
    {
        var r = Run(pavlDbm: 0.0, maxIter: 100, DcBiasSteppingMode.Always);
        output.WriteLine($"Always @0 dBm: converged={r.Converged} rungs=[{string.Join(",", r.RungOffsets)}]");

        Assert.True(r.Converged);
        Assert.True(r.RungOffsets.Count >= 2, "Always must ramp, not solve once");
        // No cold attempt precedes it: the first step IS the bottom rung.
        Assert.Equal(-20.0, r.RungOffsets[0]);
        Assert.Equal(0.0,   r.RungOffsets[^1]);
        Assert.Equal(1.0,   r.MaxDriveScale);
    }

    [Fact]
    public void Never_ReportsTheFailureExactlyAsItAlwaysDid()
    {
        var r = Run(20.0, maxIter: 4, DcBiasSteppingMode.Never);
        output.WriteLine($"Never: converged={r.Converged} steps=[{string.Join(",", r.RungOffsets)}] " +
                         $"warnings={r.Warnings.Count}");

        Assert.False(r.Converged);
        Assert.Equal([0.0], r.RungOffsets);          // one solve, no rungs
        Assert.Contains(r.Warnings, w =>
            w.StartsWith("HB did not converge", StringComparison.Ordinal) &&
            w.Contains("stored best-available result", StringComparison.Ordinal));
    }

    // ── 8. The drive is always put back ──────────────────────────────────────

    /// <summary>
    /// <see cref="HbDriveRamp.Walk{T}"/> deliberately does NOT restore the drive itself — it leaves
    /// the sources wherever the last rung it tried put them, so the caller's <c>finally</c> is the
    /// only thing that puts them back. This pins that division of labour, which is what makes the
    /// engine's <c>finally</c> load-bearing rather than decorative: a rung that throws propagates out
    /// of <c>Walk</c> with the sources still scaled.
    /// </summary>
    [Fact]
    public void WalkLeavesTheDriveWhereItWas_AndRestoreIsWhatPutsItBack()
    {
        var src = new[] { new FakeDrive(), new FakeDrive() };

        var boom = Assert.Throws<InvalidOperationException>(() =>
            HbDriveRamp.Walk<object>(src,
                (offsetDb, _) => offsetDb > -18.5 ? throw new InvalidOperationException("rung 2") : new object(),
                _ => new Complex[1, 1]));
        Assert.Equal("rung 2", boom.Message);

        // Mid-ramp: still scaled down, because Walk does not clean up after itself.
        Assert.True(src[0].DriveScale < 1.0, $"expected a rung scale, got {src[0].DriveScale}");
        Assert.Equal(src[0].DriveScale, src[1].DriveScale);

        HbDriveRamp.Restore(src);
        Assert.All(src, d => Assert.Equal(1.0, d.DriveScale));
    }

    /// <summary>The rung ladder: −span … 0 in fixed steps, ending on exactly 0 rather than a sum.</summary>
    [Fact]
    public void TheRungLadder_EndsExactlyAtTheRequestedDrive()
    {
        var offsets = HbDriveRamp.Offsets(spanDb: 20.0, stepDb: 2.0);
        output.WriteLine(string.Join(", ", offsets));

        Assert.Equal(11, offsets.Length);
        Assert.Equal(-20.0, offsets[0]);
        Assert.Equal(0.0, offsets[^1]);
        for (int i = 1; i < offsets.Length; i++)
            Assert.Equal(2.0, offsets[i] - offsets[i - 1], precision: 12);
    }

    /// <summary>A drive-scale multiplier applies to the TONE and never to the DC bias.</summary>
    [Fact]
    public void ScalingTheDrive_DoesNotMoveTheDcBias()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero2Dir(), "hero2_convergence.cnl"));
        using var netlist = new Elaborator(lib).Elaborate(tb);

        // Vgate/Vdrain are Vdc-only tone sources; Vdrive carries the RF tone.
        var bias = netlist.Components.First(c => c.InstancePath.EndsWith("Vdrain", StringComparison.Ordinal));
        var drv  = netlist.Components.First(c => c.InstancePath.EndsWith("Vdrive",  StringComparison.Ordinal));

        var probe = new StampProbe();
        double omega0 = 2.0 * Math.PI * 2e9;

        bias.Stamp(probe, 0.0);          double vddFull = probe.Last;
        drv .Stamp(probe, omega0);       double rfFull  = probe.Last;

        HbDriveRamp.SetOffset([(IDriveScalable)bias.Model, (IDriveScalable)drv.Model], -20.0);

        bias.Stamp(probe, 0.0);          double vddDown = probe.Last;
        drv .Stamp(probe, omega0);       double rfDown  = probe.Last;

        output.WriteLine($"Vdd {vddFull:F3} → {vddDown:F3} V; RF drive {rfFull:F4} → {rfDown:F4} V");

        Assert.Equal(vddFull, vddDown, precision: 12);              // bias untouched
        Assert.Equal(0.1, rfDown / rfFull, precision: 9);           // −20 dB = 0.1× in voltage
    }

    private sealed class FakeDrive : IDriveScalable
    {
        public double DriveScale { get; set; } = 1.0;
    }

    /// <summary>Captures the source value a Group-2 stamp writes, so a test can read the excitation
    /// a model presents without standing up an MNA solve.</summary>
    private sealed class StampProbe : IMnaContext
    {
        public double Last { get; private set; }
        private int _branches;

        public int  AddBranch() => _branches++;
        public void AddAdmittance(int a, int b, Complex y) { }
        public void AddBlockAdmittance(int rowNode, int colNode, Complex y) { }
        public void AddBranchCurrent(int br, int a, int b) { }
        public void AddConstraint(int br, int node, Complex c) { }
        public void AddBranchConstraint(int br, int other, Complex c) { }
        public void AddCurrentInjection(int node, Complex i) => Last = i.Magnitude;
        public void AddSourceValue(int br, Complex v) => Last = v.Magnitude;
        public void AddNodeBranchCoupling(int node, int br, Complex c) { }
    }
}
