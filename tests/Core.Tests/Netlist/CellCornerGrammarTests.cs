using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Brief vendorlib-13 §3.4: <c>corner</c> inside a <c>define</c> round-trips through the reader and the writer, and each
/// R-vl13-2a refusal is refused naming its line.
/// </summary>
public sealed class CellCornerGrammarTests
{
    private const string Model = """
        define TSTFET100 (g d s)
          part TSTFET100
          parameters Cg=gauss(1, 0.05, 3) pF  Rd=5 Ohm
          corner min Cg=0.9 pF Rd=4
          corner max Cg=1.1 pF
          C:C1 g s C=Cg
          R:R1 d s R=Rd
        end TSTFET100
        """;

    [Fact]
    public void CornerInsideDefine_RoundTripsThroughReaderAndWriter()
    {
        var (lib, tb) = new CnlReader().Read(Model, "model");
        var cell = lib.Find("TSTFET100")!;
        Assert.Equal("TSTFET100", cell.Part);
        Assert.Equal("gauss(1, 0.05, 3)", cell.Parameters[0].DefaultExpression);   // a spaced argument list stays one value
        Assert.Equal("pF", cell.Parameters[0].Unit);
        Assert.Equal(["min", "max"], cell.Corners.Select(c => c.Name));
        var min = cell.Corners[0].Bindings;
        Assert.Equal(("Cg", "0.9", "pF"), (min[0].Name, min[0].Expression, min[0].Unit));
        Assert.Equal(("Rd", "4", (string?)null), (min[1].Name, min[1].Expression, min[1].Unit));

        string written = CnlWriter.Write(tb, lib, null);
        var (lib2, tb2) = new CnlReader().Read(written, "model");
        Assert.Equal(written, CnlWriter.Write(tb2, lib2, null));
        Assert.Contains("  corner min Cg=0.9 pF Rd=4", written);
        Assert.Contains("  part TSTFET100", written);
    }

    [Theory]
    [InlineData("corner hot temp=85", "temp")]
    [InlineData("corner typ Lg=1 nH", "not a parameter")]
    [InlineData("corner min Cg=0.8 pF", "second corner")]
    [InlineData("corner nominal Cg=1 pF", "reserved")]
    public void EachRefusal_IsRefused_NamingTheLine(string line, string says)
    {
        string text = Model.Replace("  C:C1 g s C=Cg", "  " + line + "\n  C:C1 g s C=Cg");
        var ex = Assert.Throws<CnlReadException>(() => new CnlReader().Read(text, "model"));
        Assert.Contains(says, ex.Message);
        Assert.Contains(line, ex.Message);
    }
}
