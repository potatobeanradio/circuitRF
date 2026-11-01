using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// A line the reader does not know is skipped with a read warning — but that warning travels on the
/// elaborated netlist, so when the skipped line was the one that defined a name, the run fails with
/// only "Unresolved name" and the cause is gone. The error must name the skipped line.
/// </summary>
public class SkippedLineCauseTests
{
    [Fact]
    public void UnresolvedName_NamesTheSkippedLineThatMentionsIt()
    {
        var (lib, tb) = new CnlReader().Read(
            "var x=1\nR:R1 a 0 R=x\nPort:P1 a 0 Num=1 Z=50 Ohm\n");

        var ex = Assert.Throws<UnresolvedNameException>(() => new Elaborator(lib).Elaborate(tb));

        Assert.Equal("x", ex.Name);
        Assert.Contains("line 1: 'var x=1'", ex.Message);
        Assert.Contains("'x = <value>'", ex.Message);
    }
}
