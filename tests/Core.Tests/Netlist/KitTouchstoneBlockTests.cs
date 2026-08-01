using System.Linq;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// The dialect writes an N-port Touchstone block by putting the port count INTO the type name
/// (<c>S15P</c>). circuitRF's own device is <c>SnP</c> with the count as a parameter, so the count
/// moves out of the name. Left alone it resolves as neither primitive nor cell and elaboration fails
/// with "Cell 'S15P' not found in libraries" — reported from a kit.
/// </summary>
public sealed class KitTouchstoneBlockTests
{
    [Theory]
    [InlineData("S15P", 15)]
    [InlineData("S2P",   2)]
    [InlineData("s4p",   4)]
    public void AnNPortBlock_BecomesSnPCarryingItsPortCount(string type, int ports)
    {
        var r = KitNetlistReader.Read($$"""
            define PART ( a b )
              {{type}}:SNP1  a b 0  File="net.s15p" Type="touchstone"
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal("SnP", inst.Reference);
        Assert.Equal(ports.ToString(),
                     inst.Overrides.Single(o => o.Name == "NumPorts").Expression);

        // The file the block reads is carried through untouched.
        // Quoted: a file name is TEXT, and everything the reader emits is later evaluated.
        Assert.Equal("\"net.s15p\"", inst.Overrides.Single(o => o.Name == "File").Expression);
    }

    [Fact]
    public void ThePortCountComesFromTheName_NotTheNetList()
    {
        // The last net is the reference node, so counting nets would give one port too many.
        var r = KitNetlistReader.Read("""
            define PART ( a b )
              S2P:SNP1  a b 0  File="x.s2p"
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal(3, inst.NetBindings.Count);
        Assert.Equal("2", inst.Overrides.Single(o => o.Name == "NumPorts").Expression);
    }

    [Theory]
    // Matched whole. A cell that merely starts with S and ends with P is an ordinary reference.
    [InlineData("SUB")]
    [InlineData("SHUNT_CAP")]
    [InlineData("STEP")]
    [InlineData("SP")]
    [InlineData("S0P")]
    public void AnOrdinaryTypeName_IsNotMistakenForATouchstoneBlock(string type)
    {
        var r = KitNetlistReader.Read($$"""
            define PART ( a )
              {{type}}:T1  a 0  W=1
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal(type, inst.Reference);
        Assert.DoesNotContain(inst.Overrides, o => o.Name == "NumPorts");
    }

    [Fact]
    public void AnExplicitPortCountFromTheKit_IsNotOverwritten()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
              S2P:SNP1  a 0  NumPorts=7 File="x.s2p"
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal("7", inst.Overrides.Single(o => o.Name == "NumPorts").Expression);
    }
}
