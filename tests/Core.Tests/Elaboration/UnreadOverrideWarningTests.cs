using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// An override on a sub-cell instance that the cell neither declares nor reads would be bound and never looked at: the
/// circuit would be built as if it were absent. Since brief-sdl-6 D7 that is an ERROR naming it, never ignored. A name
/// the cell does not declare can still be read by an expression inside it (scope lookup walks up), including one in a
/// cell it instantiates, so that is not refused.
/// </summary>
public class UnreadOverrideWarningTests
{
    private const string Cells = """
        define leaf (a b)
          C:C1 a b C=Kdeep*1 pF
        end leaf
        define cap (a b)
          parameters K=1
          C:C1 a b C=K*1 pF
          leaf:X1 a b
        end cap
        Port:P1 n 0 Num=1 Z=50 Ohm
        """;

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    [Fact]
    public void AnOverrideNothingReads_IsRefusedByName_AndOneThatIsDeclaredOrReadBelow_IsNot()
    {
        const string tail = "\nKdeep = 1\nanalysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz\n";
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(Cells + "\ncap:CB n 0 K=2 Kx=3 Kdeep=4" + tail));
        Assert.Contains("sets Kx,", ex.Message);

        using var nl = Elaborate(Cells + "\ncap:CB n 0 K=2 Kdeep=4" + tail);
        Assert.DoesNotContain(nl.Warnings, w => w.Contains(" sets K", StringComparison.Ordinal));
    }
}
