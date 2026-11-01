using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-sens-2 R-sens2-6: the entry, port and instance roll-ups come from the per-key Δs.</summary>
public sealed class SensitivityRollupTests
{
    [Fact]
    public void OneThreePortAndOneResistor_RollUpByEntryPortAndInstance()
    {
        string cnl = $"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            SnP:SNP1 a b c NumPorts=3 File="{SnpFiles.Reciprocal3}"
            R:R3 c 0 R=30 Ohm
            analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
            goal Thru = dB(SP1.S(2,1)) analysis=SP1 ge -30
            sensitivity factors=all
            """;
        var r = SensitivityRun.Create(PreparedCircuit.FromText(cnl, null, null)).Run();
        Assert.Equal(SensitivityOutcome.Finished, r.Outcome);
        var g = Assert.Single(r.Goals);
        Assert.Equal(13, g.Contributors.Count);   // 12 SnP keys (tied 3-port) and R3.R

        // instance: SNP1 and R3, shares summing to 1, each with its factor count.
        Assert.Equal(1.0, g.Instances.Sum(x => x.Share), 12);
        Assert.Equal(12, g.Instances.Single(x => x.Label == "SNP1").Factors);
        Assert.Equal(1, g.Instances.Single(x => x.Label == "R3").Factors);

        // entry: each SnP entry is its dB key plus its phase key.
        double Key(string key) => g.Contributors.Single(c => c.Factor.Key == key).Share;
        Assert.Equal(Key("SNP1.SdB_3_1") + Key("SNP1.Sdeg_3_1"), g.Entries.Single(e => e.Label == "SNP1 · S31").Share, 12);
        Assert.Equal(7, g.Entries.Count);

        // port: every key in row 1 or column 1 — S11, S21, S31.
        double row1 = g.Contributors.Where(c => c.Factor.Snp is { } s && (s.I == 1 || s.J == 1)).Sum(c => c.Share);
        Assert.Equal(row1, g.Ports.Single(p => p.Label == "SNP1 · port 1").Share, 12);
    }
}

/// <summary>brief-sens-2 R-sens2-5a: SnP blocks left out, by name or all at once, without moving anything else.</summary>
public sealed class SnpExcludeTests
{
    private static string Bench(string sensitivity) => $"""
        Port:P1 a 0 Num=1 Z=50 Ohm
        Port:P2 b 0 Num=2 Z=50 Ohm
        SnP:SNP1 a m NumPorts=2 File="{SnpFiles.Attenuator}"
        R:R1 m 0 R=200 Ohm
        SnP:SNP2 m n NumPorts=2 File="{SnpFiles.Attenuator}"
        R:R2 n b R=10 Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
        goal Thru = dB(SP1.S(2,1)) analysis=SP1 ge -30
        sensitivity {sensitivity}
        """;

    private static SensitivityResult Run(string sensitivity)
    {
        var run = SensitivityRun.Create(PreparedCircuit.FromText(Bench(sensitivity), null, null));
        Assert.Null(run.Refusal);
        return run.Run();
    }

    [Fact]
    public void ExcludingOneBlock_LeavesOnlyItsKeysUnstepped_AndMovesNoComponentsDelta()
    {
        var all = Run("factors=all");
        var less = Run("factors=all snpexclude=SNP2");

        // A tied 2-port offers 3 entries × (dB, phase) = 6 keys: twelve fewer simulations.
        Assert.Equal(all.Evaluations - 2 * 6, less.Evaluations);
        var g = Assert.Single(less.Goals);
        Assert.DoesNotContain(g.Contributors, c => c.Factor.Instance == "SNP2");
        Assert.Contains(g.Contributors, c => c.Factor.Instance == "SNP1");
        foreach (var key in new[] { "R1.R", "R2.R" })
            Assert.Equal(all.Goals[0].Contributors.Single(c => c.Factor.Key == key).Delta,
                         g.Contributors.Single(c => c.Factor.Key == key).Delta);
        Assert.Equal(new SensitivityExcluded("SNP2", 6), Assert.Single(less.Excluded));
    }

    [Fact]
    public void SnpOff_WithTheListSet_StepsNoSnpKey_AndTheLineKeepsTheList()
    {
        const string line = "sensitivity snp=off snpexclude=SNP2";   // factors=all is the default, never written
        var off = Run("factors=all snp=off snpexclude=SNP2");
        Assert.DoesNotContain(Assert.Single(off.Goals).Contributors, c => c.Factor.Snp is not null);
        Assert.Equal(["SNP1", "SNP2"], off.Excluded.Select(e => e.Instance));

        var (lib, tb) = new CnlReader().Read(Bench("factors=all snp=off snpexclude=SNP2"));
        Assert.Contains(line, CnlWriter.Write(tb, lib).Split('\n').Select(l => l.TrimEnd('\r')));
    }
}
