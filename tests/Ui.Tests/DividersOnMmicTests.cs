using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Gysel, Bagley and tee dividers on the shipped MMIC and RFIC technologies (mmic-GaAs_2LM_100um, 100 µm of εr 12.9
/// under gold; rfic-Si_3LM_TFR_100um). The stackup's substrate, as the generator and the model read it, plans each part
/// at 10 GHz; the cell draws with no refusal, and the circuit model divides from a matched input.
/// </summary>
public class DividersOnMmicTests
{
    [Theory]
    [InlineData("mmic-GaAs_2LM_100um", "MGPD", 3)]
    [InlineData("mmic-GaAs_2LM_100um", "MBPD", 4)]
    [InlineData("mmic-GaAs_2LM_100um", "MTPD", 3)]
    [InlineData("rfic-Si_3LM_TFR_100um", "MGPD", 3)]
    [InlineData("rfic-Si_3LM_TFR_100um", "MBPD", 4)]
    [InlineData("rfic-Si_3LM_TFR_100um", "MTPD", 3)]
    public void TheDivider_PlansDrawsAndDividesOnAChip(string technology, string kind, int ports)
    {
        var tech = ShippedTechnologies.Load(technology);
        var (s, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.NotNull(s);

        Assert.True(PCellRegistry.TryGet(kind, out var generate));
        var art = generate(new Dictionary<string, PCellValue> { ["Z0"] = 50.0, ["F"] = 10e9 }, tech, PCellLayerSelection.Default);
        Assert.NotEmpty(art.Shapes);
        Assert.Equal(kind == "MGPD" ? 5 : ports, art.Pins.Count);

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string sub = $"H={F(s!.HeightMeters)} T={F(s.ThicknessMeters)} Er={F(s.RelativePermittivity)} "
                   + $"Sigma={F(s.ConductivitySPerM)} TanD={F(s.LossTangent)}";
        string pins = string.Join(" ", Enumerable.Range(1, ports).Select(k => $"p{k}")) + (kind == "MGPD" ? " r2 r3" : "");
        string cnl = string.Concat(Enumerable.Range(1, ports).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm\n"))
                   + $"{kind}:D1 {pins} Z0=50 Ohm F=10 GHz {sub}\n"
                   + (kind == "MGPD" ? "R:RA r2 0 R=50 Ohm\nR:RB r3 0 R=50 Ohm\n" : "");
        var (lib, tb) = new CnlReader().Read(cnl);
        var sp = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [10e9])["S"];
        double Db(int i, int j) => 20.0 * Math.Log10(((Complex)sp[0, i, j]).Magnitude);

        Assert.True(Db(0, 0) < -20.0, $"|S11| {Db(0, 0):F2} dB");
        // Each output's share less the line loss: 3 µm of gold, so the Bagley's far corner, three quarter waves round the
        // ring, arrives 0.6 dB down.
        double share = 10.0 * Math.Log10(1.0 / (ports - 1));
        for (int k = 1; k < ports; k++) Assert.InRange(Db(k, 0), share - 0.8, share);
    }
}
