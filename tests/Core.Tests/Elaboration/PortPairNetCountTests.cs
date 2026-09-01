using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// The one refusal the 2N-net components share (brief-sys-2): a netlist line that does not carry
/// two nets per port is refused during elaboration, NAMING the instance.
///
/// <para>Without it the failure is an index-out-of-range thrown from inside a stamp or a Newton
/// iteration, at a point where nothing left on the stack can say which component it was. The
/// schematic tiles all emit the right count — the ground-referenced ones append their own "0"
/// returns at extraction — so a wrong count only ever arrives from a hand-written netlist, which is
/// exactly the reader the sentence is for.</para>
/// </summary>
public class PortPairNetCountTests
{
    private static string Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var ex = Record.Exception(() => new Elaborator(lib).Elaborate(tb));
        return ex?.Message ?? "";
    }

    [Fact]
    public void AnAttenuatorWantsFourNets()
    {
        string msg = Elaborate("Atten:A1  a 0 b  Loss=10\n");
        Assert.Contains("Atten 'A1'", msg);
        Assert.Contains("expected 4 nets", msg);
        Assert.Contains("got 3", msg);
    }

    [Fact]
    public void AnSpstSwitchWantsFour_AndAnSpdtSix()
    {
        string spst = Elaborate("Switch:SW1  a 0 b 0 c 0  Throws=1\n");
        Assert.Contains("Switch 'SW1'", spst);
        Assert.Contains("expected 4 nets", spst);

        string spdt = Elaborate("Switch:SW2  a 0 b 0  Throws=2\n");
        Assert.Contains("Switch 'SW2'", spdt);
        Assert.Contains("expected 6 nets", spdt);
        Assert.Contains("com+, com−, 1+, 1−, 2+, 2−", spdt);
    }

    [Fact]
    public void TheExpectedCountFollowsTheThrowsParameter_NotTheReferenceName()
    {
        // One engine component serves both tiles, so the count cannot be a constant per reference.
        // A four-throw switch is ten nets, and nothing about it is a special case.
        string msg = Elaborate("Switch:SW1  a 0 b 0  Throws=4\n");
        Assert.Contains("expected 10 nets", msg);
        Assert.Equal("", Elaborate("Switch:SW1  a 0 b 0 c 0 d 0 e 0  Throws=4\n"));
    }

    [Fact]
    public void TheMixersOwnRefusal_StillSaysWhatItAlwaysSaid()
    {
        // The mixer's check was the first of these and is now the general one; its sentence has to
        // survive the generalisation, because it is the one a user has already seen.
        string msg = Elaborate("Mixer:X1  rf 0 lo 0 if\n");
        Assert.Contains("Mixer 'X1'", msg);
        Assert.Contains("expected 6 nets (rf+, rf−, lo+, lo−, if+, if−)", msg);
        Assert.Contains("got 5", msg);
    }

    [Fact]
    public void TheThreePortSystemBlocksWantSix_AndTheCouplerFamilyEight()
    {
        string circ = Elaborate("Circulator:C1  a 0 b 0 c\n");
        Assert.Contains("Circulator 'C1'", circ);
        Assert.Contains("expected 6 nets (1+, 1−, 2+, 2−, 3+, 3−)", circ);
        Assert.Contains("got 5", circ);

        string bal = Elaborate("Balun:B1  a 0 b 0\n");
        Assert.Contains("Balun 'B1'", bal);
        Assert.Contains("expected 6 nets (unb+, unb−, bal++, bal+−, bal−+, bal−−)", bal);

        // ONE reference for three tiles — the directional coupler and both hybrids — so the count
        // is the same eight whichever one was placed.
        string cpl = Elaborate("Coupler:CPL1  a 0 b 0 c 0\n");
        Assert.Contains("Coupler 'CPL1'", cpl);
        Assert.Contains("expected 8 nets (in+, in−, thru+, thru−, cpl+, cpl−, iso+, iso−)", cpl);
        Assert.Contains("got 6", cpl);
    }

    [Fact]
    public void TheAmplifierWantsFour_AndItsPortsAreNAMED()
    {
        // Named rather than numbered, because the amplifier is UNILATERAL: a line with its two
        // ports the wrong way round is a 20 dB pad, and these four words are the only warning of it.
        string msg = Elaborate("Amp:A1  a 0 b  Gain=20\n");
        Assert.Contains("Amp 'A1'", msg);
        Assert.Contains("expected 4 nets (in+, in−, out+, out−)", msg);
        Assert.Contains("got 3", msg);
    }

    [Fact]
    public void Ip3RefIsAnEnumName_AndNeverReachesTheExpressionEvaluator()
    {
        // The amplifier's IP3Ref joins the Switch's OffState and the Circulator's Direction on the
        // same list, for the same reason: a bare identifier either fails to parse or resolves
        // against a global that happens to share its spelling.
        Assert.Equal("", Elaborate("Amp:A1  a 0 b 0  IP3Ref=Input\n"));
        Assert.Equal("", Elaborate("Input = 3\nAmp:A1  a 0 b 0  IP3Ref=Input\n"));
    }

    [Fact]
    public void DirectionIsAnEnumName_AndNeverReachesTheExpressionEvaluator()
    {
        // The Circulator's Direction is the Switch's OffState by another name, and it needs the same
        // rule for the same reason: a bare identifier either fails to parse or, worse, resolves
        // against a global that happens to share its spelling.
        Assert.Equal("", Elaborate("Circulator:C1  a 0 b 0 c 0  Direction=CCW\n"));
        Assert.Equal("", Elaborate("CCW = 3\nCirculator:C1  a 0 b 0 c 0  Direction=CCW\n"));
    }

    [Fact]
    public void ARightCountIsNotRefused()
    {
        Assert.Equal("", Elaborate("Atten:A1  a 0 b 0  Loss=10\n"));
        Assert.Equal("", Elaborate("Switch:SW1  a 0 b 0  Throws=1\n"));
        Assert.Equal("", Elaborate("Switch:SW2  a 0 b 0 c 0  Throws=2\n"));
        Assert.Equal("", Elaborate("Mixer:X1  rf 0 lo 0 if 0\n"));
        Assert.Equal("", Elaborate("Circulator:C1  a 0 b 0 c 0\n"));
        Assert.Equal("", Elaborate("Balun:B1  a 0 b 0 c 0\n"));
        Assert.Equal("", Elaborate("Coupler:CPL1  a 0 b 0 c 0 d 0\n"));
        Assert.Equal("", Elaborate("Amp:A1  a 0 b 0  Gain=20\n"));
    }

    [Fact]
    public void OffStateIsAnEnumName_AndNeverReachesTheExpressionEvaluator()
    {
        // A bare identifier is either a parse failure or, worse, resolves against a global that
        // happens to share its spelling. Match's Response has the same rule for the same reason.
        Assert.Equal("", Elaborate("Switch:SW1  a 0 b 0  Throws=1 OffState=Absorptive\n"));
        Assert.Equal("", Elaborate("Reflective = 3\nSwitch:SW1  a 0 b 0  Throws=1 OffState=Reflective\n"));
    }
}
