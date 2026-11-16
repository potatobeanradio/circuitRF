using System;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Cells;
using Xunit;

namespace CircuitRF.Ui.Tests.Transient;

/// <summary>
/// brief-trn-3 gate 5 — the SPICE-dialect reader turns a voltage-controlled switch and its VSWITCH card into VSwitch with the
/// card's values, refuses a card parameter VSwitch cannot express by its name, and refuses a current-controlled switch by name.
/// Synthetic decks.
/// </summary>
public sealed class VSwitchImportTests
{
    private static SubcircuitTranslation Only(string text) =>
        SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text)).Single(t => t.Elements.Count > 0);

    [Fact]
    public void Gate5_AVoltageControlledSwitchReadsToVSwitchWithItsCardsValues()
    {
        var t = Only("""
            .subckt part a b c
            S1 a b c 0 swm OFF
            R1 a b 1k
            .model swm VSWITCH(RON=0.5 ROFF=1MEG VON=2 VOFF=1)
            .ends part
            """);
        Assert.Null(t.Refusal);
        var s = t.Elements.Single(e => e.InstanceName == "S1");
        Assert.Equal(SymbolKind.VSwitch, s.Symbol);
        Assert.Equal(["a", "b", "c", "0"], s.Nets);
        double P(string n) => double.Parse(s.Parameters.Single(p => p.Name == n).Expression, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0.5, P("Ron"));
        Assert.Equal(1e6, P("Roff"));
        Assert.Equal(2.0, P("Von"));
        Assert.Equal(1.0, P("Voff"));
    }

    [Fact]
    public void Gate5_ACardStatingWhatVSwitchCannotExpress_AndACurrentControlledSwitch_AreRefusedByName()
    {
        var unexpressible = Only("""
            .subckt part a b c
            S1 a b c 0 swm
            .model swm VSWITCH(RON=0.5 ROFF=1MEG VON=2 VOFF=1 TD=1n)
            .ends part
            """);
        Assert.Contains("TD", unexpressible.Elements.Single(e => e.InstanceName == "S1").Refusal);

        var abrupt = Only("""
            .subckt part a b c
            S1 a b c 0 swm
            .model swm SW(RON=1 ROFF=1MEG VT=1 VH=0.1)
            .ends part
            """);
        Assert.Contains("VT", abrupt.Elements.Single(e => e.InstanceName == "S1").Refusal);

        var r = SpiceNetlistReader.Read("""
            .subckt part a b
            W1 a b VSENSE csw
            R1 a b 1k
            .ends part
            """);
        Assert.Contains(r.Notes, n => n.Message.Contains("current-controlled switch", StringComparison.Ordinal));
        Assert.Contains("part", r.IncompleteCells);
    }
}
