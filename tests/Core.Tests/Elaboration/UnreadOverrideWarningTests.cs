using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// An override on a sub-cell instance that the cell neither declares nor reads is bound and never looked at: the
/// circuit is built as if it were absent. It is said as a warning. A name the cell does not declare can still be read
/// by an expression inside it (scope lookup walks up), including one in a cell it instantiates, so that is not warned.
/// </summary>
public class UnreadOverrideWarningTests
{
    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static bool WarnsAbout(ElaboratedNetlist nl, string name)
        => nl.Warnings.Any(w => w.Contains($"sets {name},", StringComparison.Ordinal));

    [Fact]
    public void AnOverrideNothingReads_IsWarned_AndOneThatIsDeclaredOrReadBelow_IsNot()
    {
        using var nl = Elaborate("""
            define leaf (a b)
              C:C1 a b C=Kdeep*1 pF
            end leaf
            define cap (a b)
              parameters K=1
              C:C1 a b C=K*1 pF
              leaf:X1 a b
            end cap
            Port:P1 n 0 Num=1 Z=50 Ohm
            cap:CB n 0 K=2 Kx=3 Kdeep=4
            """ + "\nKdeep = 1\nanalysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz\n");

        Assert.True(WarnsAbout(nl, "Kx"));
        Assert.False(WarnsAbout(nl, "K"));
        Assert.False(WarnsAbout(nl, "Kdeep"));
    }
}
