using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// PnTone — the convenient multi-tone power source — authors a two-tone HB from a single component
/// (replacing the V_nTone + Z_Port:Zsource + bias-tee chain). This drives BOTH fundamentals through a
/// nonlinear SDD and verifies the hallmark of a real two-tone sim: equal carriers + intermodulation.
/// </summary>
public class PnToneTwoToneTests(ITestOutputHelper output)
{
    // One PnTone (2 tones, 0.9/1.1 GHz) drives a cubic SDD transconductance into a 50 Ω load.
    private const string Cnl = @"
PnTone:Pd   n1 0   Freq[1]=0.9e9  Pavl[1]=20  Freq[2]=1.1e9  Pavl[2]=20  Z=50
SDD:X1      n1 0  n2 0   I[1,0]=_v1/50   I[2,0]=0.02*_v1 + 0.01*_v1^2 + 0.006*_v1^3
R:Rl        n2 0   R=50
analysis HB1 type=hb NumFreqs=2 Tone[1]=0.9e9 Tone[2]=1.1e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-8
";

    // A circuit shaped like the user's two-tone setup: the source drives a LINEAR-only node (vin)
    // through an IProbe (Iin) into the SDD gate (n1). Both vin and Iin must appear in the result
    // (they don't live on the nonlinear interface), or measurements like V("vin",1)/I("Iin",1) fail.
    private const string CnlLinearNodeProbe = @"
PnTone:Pd   vin 0   Freq[1]=0.9e9  Pavl[1]=20  Freq[2]=1.1e9  Pavl[2]=20  Z=50
IProbe:Iin  vin n1
SDD:X1      n1 0  n2 0   I[1,0]=_v1/50   I[2,0]=0.02*_v1 + 0.01*_v1^2 + 0.006*_v1^3
R:Rl        n2 0   R=50
analysis HB1 type=hb NumFreqs=2 Tone[1]=0.9e9 Tone[2]=1.1e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-8
";

    private static (DataSet ds, ElaboratedNetlist nl) Run() => RunCnl(Cnl);

    private static (DataSet ds, ElaboratedNetlist nl) RunCnl(string cnl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pntone_{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, cnl);
        try
        {
            var (lib, tb) = CnlReader.ReadFile(path);
            var nl  = new Elaborator(lib).Elaborate(tb);
            var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
            var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
            Assert.True(p.IsMultiTone, "PnTone test must resolve to a two-tone HB");
            var ds = (DataSet)new HbEngine(nl, tb).Run(p);
            return (ds, nl);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Two-tone result completeness: the V cube spans LINEAR-only user nodes (vin), and the I cube
    // carries IProbe branch currents (Iin) — recovered per mixing product by the back-solve.
    [Fact]
    public void TwoTone_VCube_HasLinearNodes_AndICube_HasIProbeCurrents()
    {
        var (ds, _) = RunCnl(CnlLinearNodeProbe);
        Assert.True(ds["Converged"].RealValues[0] > 0.5);

        // V node axis now includes the linear-only source node "vin" (not just interface n1/n2).
        var nodeNames = ds["V"].Axes.First(a => a.Name == "node").Labels!;
        Assert.Contains("vin", nodeNames);
        Assert.Contains("n1",  nodeNames);
        Assert.Contains("n2",  nodeNames);

        // I branch axis includes the IProbe "Iin".
        Assert.True(ds.Contains("I"), "two-tone result must have an I cube");
        var branchNames = ds["I"].Axes.First(a => a.Name == "branch").Labels!;
        Assert.Contains("Iin", branchNames);

        // vin is driven at the carriers (so V("vin",1) resolves to a real value, not a missing node).
        double vinCarrier = TwoToneMeasurements.Tone(ds, 0, "vin", 1, 0).Magnitude;
        double iinCarrier = TwoToneMeasurements.Tone(ds, 0, "vin", 0, 1).Magnitude;
        Assert.True(vinCarrier > 1e-3 && iinCarrier > 1e-3, "both carriers present at the linear node");
    }

    [Fact]
    public void Elaboration_ProducesPnToneModel_WithTwoTones()
    {
        var (_, nl) = Run();
        var pn = nl.Components.Select(c => c.Model).OfType<PnToneModel>().Single();
        Assert.Equal(new[] { 0.9e9, 1.1e9 }, pn.ToneFreqsHz);
    }

    [Fact]
    public void PnTone_DrivesBothCarriers_AndProducesIM3()
    {
        var (ds, _) = Run();
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "two-tone solve must converge");

        // PnTone drives BOTH fundamentals at the source node, equally (same Pavl). (k1,k2) = carriers.
        double c1 = TwoToneMeasurements.Tone(ds, 0, "n1", 1, 0).Magnitude;
        double c2 = TwoToneMeasurements.Tone(ds, 0, "n1", 0, 1).Magnitude;
        output.WriteLine($"|V(n1)| carrier1={c1:E3}  carrier2={c2:E3}");
        Assert.True(c1 > 1e-3 && c2 > 1e-3, "both fundamentals must be driven");
        Assert.True(Math.Abs(c1 - c2) < 0.05 * Math.Max(c1, c2), "equal-Pavl tones → near-equal carriers");

        // The nonlinearity mixes them: IM3 (2,−1) and (−1,2) appear at the drain, below the carriers.
        double drainCarrier = TwoToneMeasurements.Tone(ds, 0, "n2", 1, 0).Magnitude;
        double im3Lo = TwoToneMeasurements.Tone(ds, 0, "n2", 2, -1).Magnitude;
        double im3Hi = TwoToneMeasurements.Tone(ds, 0, "n2", -1, 2).Magnitude;
        output.WriteLine($"|V(n2)| carrier={drainCarrier:E3}  IM3lo={im3Lo:E3}  IM3hi={im3Hi:E3}");
        Assert.True(im3Lo > 1e-9 && im3Hi > 1e-9, "IM3 products must be present (two real tones mixed)");
        Assert.True(im3Lo < drainCarrier && im3Hi < drainCarrier, "IM3 must be below the carrier");

        // IM3 frequencies are 2f1−f2 = 0.7 GHz and 2f2−f1 = 1.3 GHz.
        Assert.Equal(0.7e9, TwoToneMeasurements.FrequencyOf(ds, 2, -1), precision: 0);
        Assert.Equal(1.3e9, TwoToneMeasurements.FrequencyOf(ds, -1, 2), precision: 0);
    }
}
