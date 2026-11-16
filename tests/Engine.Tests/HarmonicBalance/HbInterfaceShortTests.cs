using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// brief PCH-12 — two interface nodes the linear network joins with an ideal inductor are ONE node at DC. Z_NN(0) is then
/// rank-deficient with no zero diagonal, and inverting it left the two decoupled: the DC solution had them at −2 V and 0 V.
/// Found in the SDD example's bias loop (a bias SDD feeding a FET's gate through a choke). Two cases: the pair with a DC path to
/// ground (inductance regularization cures it), and the pair with none but gmin (Z of order 1e12 Ω, so the 1 µΩ is lost in
/// double precision and Y_NN(0) is taken with the nodes held instead). In both, HB's DC equals the DC analysis.
/// </summary>
public class HbInterfaceShortTests(ITestOutputHelper output)
{
    private const string Grounded = """
        P1Tone:SRC src 0 Num=1 Pavl=-20 dBm Z=50 Ohm Freq=1e9
        C:CB src b C=100 pF
        SDD:A a 0 I[1,0]=(_v1+2)/1e-3
        L:L1 a b L=100 nH
        SDD:B b 0 I[1,0]=_v1/50+1e-3*_v1^2
        R:RL b 0 R=1000
        analysis HB1 type=hb Tone=1e9 MaxHarm=3
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoDeviceNodesJoinedByAnInductor_AreOneNodeAtDc(bool groundPath)
    {
        string cnl = groundPath ? Grounded : Grounded.Replace("R:RL b 0 R=1000\n", "", StringComparison.Ordinal);
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(netlist);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), netlist.ResolvedGlobals);
        var v = ((DataSet)new HbEngine(netlist, tb).Run(p))["V"];
        double At(string node) => ((Complex)v[Array.IndexOf(v.Axes[0].Labels!, node), 0]).Real;
        var vdc = DcResultPacker.Pack(dc, netlist)["V"];
        double dcA = vdc.RealValues[Array.IndexOf(vdc.Axes[0].Labels!, "a")];
        output.WriteLine($"ground path {groundPath}: HB V(a) {At("a"):G9}, V(b) {At("b"):G9}; DC V(a) {dcA:G9}");
        Assert.Equal(dcA, At("a"), 1e-6);
        Assert.Equal(dcA, At("b"), 1e-6);
    }
}
