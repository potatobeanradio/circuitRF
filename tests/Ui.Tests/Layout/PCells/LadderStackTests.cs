using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The ladder (MLAD) on the shipped die and board stacks: the same filter written for each stack's own substrate — a
/// quarter-wave shorted stub between two quarter-wave lines of Z0 — simulates on that substrate as the stack resolves it
/// (an open at F, so the line passes; a short at F/4 that it does not) and draws on its signal layer with its via pin.
/// </summary>
public class LadderStackTests(ITestOutputHelper output)
{
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("mmic-GaAs_2LM_100um", 10e9)]
    [InlineData("rfic-Si_3LM_TFR_100um", 30e9)]
    [InlineData("pcb-2layer_RO4350B_20mil_1oz", 2.4e9)]
    public void OnEachStack_TheLadderSimulatesAndDraws(string tech, double f)
    {
        var technology = ShippedTechnologies.Load(tech);
        var (r, _, _) = SubstrateResolver.ResolveElectrical(technology, PCellLayerSelection.Default);
        Assert.NotNull(r);
        var s = new MicrostripSubstrate(r.HeightMeters, r.ThicknessMeters, r.RelativePermittivity, r.ConductivitySPerM,
            r.LossTangent, r.RoughnessRmsMeters);

        double w = LadderPlan.Width(50.0, f, s);
        double quarter = PlanarLineAngle.LengthMeters(
            new MicrostripLineModel(w, 0.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "test").LineParameters(f).Eeff,
            Math.PI / 2.0, f);
        string sections = LadderPlan.Format([new LadderLine(w, quarter), new LadderStub(w, quarter, LadderStubEnd.Short),
                                             new LadderLine(w, quarter)]);

        string cnl = $"""
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            MLAD:X1 p1 p2 0 Sections="{sections}" Z0=50 Ohm F={F(f)} H={F(s.HMeters)} T={F(s.TMeters)} Er={F(s.EpsR)} Sigma={F(s.SigmaSPerM)} TanD={F(s.TanD)}
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var sp = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [f, f / 4.0])["S"];
        Complex S(int k, int i, int j) => (Complex)sp[k, i, j];
        double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);
        output.WriteLine($"{tech}: H {s.HMeters * 1e6:F1} um, er {s.EpsR}; {sections}; at F S21 {Db(S(0, 1, 0)):F2} dB, "
                       + $"S11 {Db(S(0, 0, 0)):F1} dB; at F/4 S21 {Db(S(1, 1, 0)):F2} dB");
        Assert.True(Db(S(0, 0, 0)) < -15.0, "matched at F, where the shorted quarter wave is an open");
        Assert.True(Db(S(1, 1, 0)) < -3.0, "at F/4 the sixteenth-wave short loads the line");

        var art = PassivePlanPCells.GenerateLadder(new Dictionary<string, PCellValue>
        {
            ["Sections"] = PCellValue.Text(sections), ["Z0"] = 50.0, ["F"] = f,
        }, technology, PCellLayerSelection.Default);
        Assert.Null(art.Diagnostics);
        Assert.Equal(["1", "2", "G1"], art.Pins.Select(p => p.Name));
        var signal = SubstrateResolver.ResolveSignalLayerKey(technology, PCellLayerSelection.Default, out _);
        Assert.All(art.Pins, p => Assert.Equal(signal, p.Layer));
        Assert.NotEmpty(art.Shapes);
    }
}
