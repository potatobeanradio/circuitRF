using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Transient;

/// <summary>
/// brief-trn-2 gate 7 — the SPICE-dialect reader turns each stimulus into its matching type (SIN into V_1Tone with Delay and
/// Damping), a DC current line into Idc, and keeps IC= on a capacitor and an inductor through to the elaborated netlist.
/// Synthetic deck; nothing here names a supplier.
/// </summary>
public sealed class TransientSourceImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-trn2-" + Guid.NewGuid().ToString("N")[..8]);

    public TransientSourceImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private const string Deck = """
        .subckt part a b
        V1 n1 0 PULSE(0 1 1n 2n 3n 10n 20n)
        V2 n2 0 PWL(0 0 1n 1 2n 0)
        V3 n3 0 EXP(0 1 1n 2n 10n 3n)
        V4 n4 0 SFFM(0 1 100Meg 2 1Meg)
        V5 n5 0 SIN(0.5 2 10Meg 5n 1e6 30)
        I1 n6 0 PULSE(0 1m 0 1n 1n 5n)
        I2 n7 0 DC 1m
        C1 a b 1u IC=0.5
        L1 a n8 1u IC=1m
        R1 n1 n2 1k
        R2 n3 n4 1k
        R3 n5 n6 1k
        R4 n7 0 1k
        R5 n8 b 1k
        .ends part
        """;

    [Fact]
    public void Gate7_EachStimulusIsItsType_ADcCurrentLineIsIdc_AndIcIsKept()
    {
        var t = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(Deck)).Single(x => x.Elements.Count > 0);
        Assert.Null(t.Refusal);
        SubcircuitElement E(string n) => t.Elements.Single(e => e.InstanceName == n);
        string P(SubcircuitElement e, string n) => e.Parameters.Single(p => p.Name == n).Expression;

        Assert.Equal(SymbolKind.VPulse, E("V1").Symbol);
        Assert.Equal(SymbolKind.VPwl, E("V2").Symbol);
        Assert.Equal(SymbolKind.VExp, E("V3").Symbol);
        Assert.Equal(SymbolKind.VSffm, E("V4").Symbol);
        Assert.Equal(SymbolKind.ToneSource, E("V5").Symbol);
        Assert.Equal(SymbolKind.IPulse, E("I1").Symbol);
        Assert.Equal(SymbolKind.Idc, E("I2").Symbol);

        // SIN(VO VA FREQ TD THETA PHASE): the offset, amplitude and frequency, Delay and Damping, and the sine's phase as a cosine's
        var sin = E("V5");
        foreach (var key in new[] { "Vdc", "V", "Freq", "Delay", "Damping", "Phase" }) Assert.Contains(sin.Parameters, p => p.Name == key);
        Assert.Contains("-90", P(sin, "Phase"));
        Assert.Contains("1", P(E("V2"), "Points"));

        Assert.Equal("0.5", P(E("C1"), "IC"));
        Assert.Contains(E("L1").Parameters, p => p.Name == "IC");
    }

    [Fact]
    public void Gate7_IcSurvivesIntoTheElaboratedNetlist()
    {
        File.WriteAllText(Path.Combine(_root, "part.sp"), Deck);
        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.SpiceModel, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        comp.Parameters.First(p => p.Name == "File").Expression = "part.sp";
        comp.Parameters.First(p => p.Name == "Name").Expression = "part";
        model.Components.Add(comp);

        var r = NetExtractor.Extract(model, "tb");
        var nl = new Elaborator(r.Library).Elaborate(r.TestBench);
        var c1 = nl.Components.Single(c => c.InstancePath.EndsWith("C1", StringComparison.Ordinal));
        var l1 = nl.Components.Single(c => c.InstancePath.EndsWith("L1", StringComparison.Ordinal));
        Assert.Equal(0.5, c1.Parameters["IC"].AsReal(), 15);
        Assert.Equal(1e-3, l1.Parameters["IC"].AsReal(), 15);
        Assert.Contains(nl.Components, c => c.ComponentType == "V_Pulse");
        Assert.Contains(nl.Components, c => c.ComponentType == "Idc");
    }

    /// <summary>The TRN review: a stimulus written without parentheses takes the numbers after it and stops at the first word
    /// that is not one — `AC 1` used to be swallowed as two more arguments, and a PWL's as a time-value pair.</summary>
    [Fact]
    public void AStimulusWithoutParentheses_StopsAtTheFirstWordThatIsNotANumber()
    {
        const string deck = """
            .subckt part a b
            V1 a 0 PWL 0 0 1n 1 AC 1
            R1 a b 1k
            .ends part
            """;
        var t = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(deck)).Single(x => x.Elements.Count > 0);
        Assert.Null(t.Refusal);
        var v1 = t.Elements.Single(e => e.InstanceName == "V1");
        Assert.Equal(SymbolKind.VPwl, v1.Symbol);
        string points = v1.Parameters.Single(p => p.Name == "Points").Expression;
        Assert.DoesNotContain("AC", points, StringComparison.OrdinalIgnoreCase);
    }
}
