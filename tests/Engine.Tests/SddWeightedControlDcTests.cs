using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests;

/// <summary>
/// brief PCH-12 — an SDD term on a user weight (w ≥ 2) that reads a control current: DC stamped the term's value and its
/// voltage derivatives but not ∂I[p,w]/∂_cn, so a bias loop written on a low-pass weight (H[2] = 1 at DC) was solved by a
/// Newton blind to the loop's gain and did not converge. With the derivative, DC equals the same loop written on w = 0.
/// </summary>
public class SddWeightedControlDcTests(ITestOutputHelper output)
{
    private const string Loop = """
        Vdc:VDD vdd 0 Vdc=5
        IProbe:IDD vdd x
        R:RD x d R=10
        SDD:FET g 0 d 0 I[1,0]=_v1/1e3 I[2,0]=0.05*(1+tanh(2*(_v1+1)))*tanh(_v2)
        L:LG vg g L=100 nH
        SDD:BIAS vg 0 TERM Rc=1e-3 Iset=0.03 K=200 C[1]=IDD
        analysis DC1 type=dc
        """;

    private static (bool Converged, double Vg) Run(string term)
    {
        var (lib, tb) = new CnlReader().Read(Loop.Replace("TERM", term, StringComparison.Ordinal));
        var netlist = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(netlist);
        var v = DcResultPacker.Pack(dc, netlist)["V"];
        return (dc.Converged, v.RealValues[Array.IndexOf(v.Axes[0].Labels!, "vg")]);
    }

    [Fact]
    public void AControlCurrentOnAUserWeight_IsInTheDcJacobian()
    {
        var plain = Run("I[1,0]=(_v1+1-tanh(K*(Iset-_c1)))/Rc");
        var weighted = Run("I[1,0]=(_v1+1)/Rc I[1,2]=-tanh(K*(Iset-_c1))/Rc H[2]=1/(1+j*2*pi*freq*1e-6)");
        output.WriteLine($"w=0: converged {plain.Converged}, Vg {plain.Vg:G9}; w=2: converged {weighted.Converged}, Vg {weighted.Vg:G9}");
        Assert.True(plain.Converged);
        Assert.True(weighted.Converged);
        Assert.Equal(plain.Vg, weighted.Vg, 1e-9);
    }
}
