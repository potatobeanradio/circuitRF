using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// A cell whose name holds a space (<c>Filter Board</c>) placed in a schematic. Its <c>define</c> line always read, but the
/// instance line <c>Filter Board:X1 …</c> did not: the reader took <c>Filter</c> for half a statement and skipped the line
/// with a warning, so the instance dropped out of every simulation of the schematic. The writer now quotes such a type and
/// the reader takes the quoted form.
/// </summary>
public sealed class SpacedCellNameTests
{
    private const string Netlist = """
        define Filter Board (IN OUT)
          R:R1 IN OUT R=50 Ohm
        end Filter Board

        "Filter Board":X1  a  b
        Port:P1  a  0  Num=1  Z=50 Ohm
        Port:P2  b  0  Num=2  Z=50 Ohm
        """;

    [Fact]
    public void AQuotedCellType_IsRead_AndWrittenBackQuoted()
    {
        var (lib, tb) = new CnlReader().Read(Netlist, "tb");
        var x1 = Assert.Single(tb.Instances, i => i.InstanceName == "X1");
        Assert.Equal("Filter Board", x1.Reference);
        Assert.NotNull(lib.Find("Filter Board"));

        string written = CnlWriter.Write(tb, lib, null);
        Assert.Contains("\"Filter Board\":X1", written, StringComparison.Ordinal);
        var (lib2, tb2) = new CnlReader().Read(written, "tb");
        Assert.Equal(written, CnlWriter.Write(tb2, lib2, null));
    }
}
