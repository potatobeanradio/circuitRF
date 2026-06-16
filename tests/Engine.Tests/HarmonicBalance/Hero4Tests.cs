using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Phase 4d — Hero 4: two-stage GaN HEMT PA (multi-device single-tone HB). Both FETs sit in the
/// nonlinear partition; the surrounding linear network (input match, interstage, output, bias) is
/// one multiport block interfacing all four nonlinear-facing nodes (n_gate, n_drain, n_gate2,
/// n_drain2). Drive-up test: confirm convergence across the sweep and monitor the composite gain
/// as Stage 2 is driven toward compression.
///
/// Per-stage / composite powers from the branch-current convention (HbEngine INl sign, §590):
///   Pin  into a stage  = +½·Re(V_gate  · conj(I:device:g))
///   Pout of a stage    = −½·Re(V_drain · conj(I:device:d))   (drain delivers into its external net)
/// </summary>
public class Hero4Tests(ITestOutputHelper output)
{
    private static string Hero4Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero4");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero4 not found");
    }

    [Fact]
    public void Hero4_TwoStage_DriveUp_CompositeGainCompresses()
    {
        var dir       = Hero4Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero4.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals);

        // The netlist default sweep (-40..-20) is deep small-signal (composite gain ≈ 32 dB).
        // Drive up to reach compression of the output stage.
        p = p with { SweepStart = -20, SweepStop = 22, SweepStep = 2 };

        var sw = new ParametricSweepAnalysis("SW_auto", p.SweepVarName!, p.SweepValues().ToArray(), hba.Name);
        var ds = ParametricSweepEngine.Run(sw, lib, tb);
        var sweepVals = ds["Converged"].Axes[0].Values;

        int g1 = NodeIdx(ds, "n_gate");
        int d1 = NodeIdx(ds, "n_drain");
        int g2 = NodeIdx(ds, "n_gate2");
        int d2 = NodeIdx(ds, "n_drain2");
        Assert.True(g1 >= 0 && d1 >= 0 && g2 >= 0 && d2 >= 0, "all four interface nodes present");

        // ── All sweep points converge ──
        for (int si = 0; si < sweepVals.Length; si++)
            Assert.True((double)ds["Converged"][si] > 0.5,
                $"Hero4 did not converge at Pavl={sweepVals[si]} dBm.");

        int n = sweepVals.Length;
        var compGainDb = new double[n];
        var g1Db = new double[n];
        var g2Db = new double[n];

        output.WriteLine("  Pavl   Pout    Gcomp   G1     G2      (dBm / dB)");
        for (int i = 0; i < n; i++)
        {
            double pavlDbm = sweepVals[i];
            double pavlW   = Math.Pow(10, (pavlDbm - 30) / 10);

            double pin1  = PinW (ds, i, "M1", g1);
            double pout1 = PoutW(ds, i, "M1", d1);
            double pin2  = PinW (ds, i, "M2", g2);
            double pout2 = PoutW(ds, i, "M2", d2);

            compGainDb[i] = 10 * Math.Log10(pout2 / pavlW);
            g1Db[i]       = 10 * Math.Log10(pout1 / pin1);
            g2Db[i]       = 10 * Math.Log10(pout2 / pin2);

            output.WriteLine($"  {pavlDbm,5:F0}  {10*Math.Log10(pout2)+30,6:F2}  " +
                             $"{compGainDb[i],6:F2}  {g1Db[i],5:F2}  {g2Db[i],6:F2}");
        }

        // Compression measured as gain drop from small-signal (index 0) to top drive (index n-1).
        // Positive = compression; negative = expansion.
        double g1Comp = g1Db[0] - g1Db[n - 1];
        double g2Comp = g2Db[0] - g2Db[n - 1];
        double compGainDrop = compGainDb[0] - compGainDb[n - 1];
        double poutSatDbm = 10 * Math.Log10(PoutW(ds, n - 1, "M2", d2)) + 30;
        output.WriteLine($"  small-signal: Gcomp={compGainDb[0]:F2} dB  G1={g1Db[0]:F2}  G2={g2Db[0]:F2}");
        output.WriteLine($"  @ top drive: composite drop={compGainDrop:F2} dB  " +
                         $"Stage1={g1Comp:F2} dB  Stage2={g2Comp:F2} dB  Pout_sat={poutSatDbm:F2} dBm");

        // ── Regression anchors (self-generated; modest windows to catch drift, not flake) ──
        Assert.InRange(compGainDb[0], 31.5, 32.5);   // small-signal composite gain ≈ 32.2 dB
        Assert.InRange(poutSatDbm,    50.5, 51.5);   // saturated output ≈ 51 dBm

        // ── The composite PA is driven into compression ──
        Assert.True(compGainDrop > 1.0,
            $"Composite gain did not reach compression (drop={compGainDrop:F2} dB from small-signal).");

        // ── Stage 2 (output stage) saturates first — it compresses hard while Stage 1 does not ──
        Assert.True(g2Comp > 3.0,
            $"Stage 2 did not reach compression (gain drop={g2Comp:F2} dB).");
        Assert.True(g2Comp > g1Comp + 2.0,
            $"Stage 2 should compress well before Stage 1 (Stage2 drop={g2Comp:F2} dB vs " +
            $"Stage1 drop={g1Comp:F2} dB) — the output stage must saturate first.");
    }

    private static int NodeIdx(DataSet ds, string name)
        => Array.FindIndex(ds["V"].Axes[1].Labels!, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));

    // Pin delivered into a stage at its gate: +½ Re(V_gate · conj(I:device:g)).
    private static double PinW(DataSet ds, int si, string devicePath, int gateNodeIdx)
    {
        var v = (Complex)ds["V"][si, gateNodeIdx, 1];
        var i = (Complex)ds[$"I:{devicePath}:g"][si, 1];
        return 0.5 * (v * Complex.Conjugate(i)).Real;
    }

    // Pout delivered by a stage at its drain into the external network: −½ Re(V_drain · conj(I:device:d)).
    private static double PoutW(DataSet ds, int si, string devicePath, int drainNodeIdx)
    {
        var v = (Complex)ds["V"][si, drainNodeIdx, 1];
        var i = (Complex)ds[$"I:{devicePath}:d"][si, 1];
        return -0.5 * (v * Complex.Conjugate(i)).Real;
    }
}
