using System.Linq;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

// Regression: ParseTunerLine must extract exactly the declared nets regardless of where Z[k]= appears
// on the line. The GUI emits the simple params (Zdefault=, Z0=, BiasTee=, Vbias=) BEFORE Z[1]=; the
// net section is everything before the first Z[k]=, so those params used to leak in as bogus net names.
// Two tuners sharing those param strings then cross-wired their bias-tee internals → non-convergence.
public sealed class TunerNetParseTests
{
    private static (System.Collections.Generic.List<string> nets, bool hasZ1, bool hasZdefault, bool hasZ0, bool hasBiasTee, bool hasVbias)
        ParseTuner(string line)
    {
        var (_, tb) = new CnlReader().Read(line);
        var inst = tb.Instances.Single(i => i.Reference == "Tuner");
        bool Has(string n) => inst.Overrides.Any(o => o.Name == n);
        return (inst.NetBindings.ToList(), Has("Z[1]"), Has("Zdefault"), Has("Z0"), Has("BiasTee"), Has("Vbias"));
    }

    [Fact]
    public void Z1First_TwoNets()
    {
        var r = ParseTuner("Tuner:T1  n_src  n1  Z[1]=50  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=Vgs");
        Assert.Equal(new[] { "n_src", "n1" }, r.nets);
        Assert.True(r.hasZ1 && r.hasZdefault && r.hasZ0 && r.hasBiasTee && r.hasVbias);
    }

    [Fact]
    public void Z1Last_StillTwoNets_NoBogusParamNets()   // the GUI emission order — the bug case
    {
        var r = ParseTuner("Tuner:T1  n_src  n1  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=Vgs  Z[1]=50");
        Assert.Equal(new[] { "n_src", "n1" }, r.nets);          // NOT 6 nets with "Zdefault=1e-6" etc.
        Assert.DoesNotContain(r.nets, n => n.Contains('='));     // no param leaked into the net list
        Assert.True(r.hasZ1 && r.hasZdefault && r.hasZ0 && r.hasBiasTee && r.hasVbias);
    }

    [Fact]
    public void Z1Last_LoadStyle_GroundRefNet()
    {
        var r = ParseTuner("Tuner:Load  n2  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VDD  Z[1]=50");
        Assert.Equal(new[] { "n2", "0" }, r.nets);
    }
}
