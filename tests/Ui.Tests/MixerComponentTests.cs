using CircuitRF.Design.Cells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The ideal mixer on the schematic side: two tiles over ONE engine component, their pin ORDER
/// (which is the engine contract), the glyphs, and what each extracts to.
///
/// <para>The pair follows the TermG pattern rather than the BJT one — nothing electrical differs
/// between <see cref="SymbolKind.Mixer"/> and <see cref="SymbolKind.MixerD"/>, only how many of the
/// engine's six nets the schematic exposes as pins. That is worth asserting, because the cheapest
/// wrong turn here is a second engine model that drifts from the first.</para>
/// </summary>
public class MixerComponentTests
{
    // ── Registry ──────────────────────────────────────────────────────────────

    [Fact]
    public void BothTiles_ResolveToTheSameEngineComponent()
    {
        Assert.Equal("Mixer", ComponentTypeRegistry.EngineReference(SymbolKind.Mixer));
        Assert.Equal("Mixer", ComponentTypeRegistry.EngineReference(SymbolKind.MixerD));

        Assert.Equal("Mixer",  ComponentTypeRegistry.DisplayName(SymbolKind.Mixer));
        Assert.Equal("MixerD", ComponentTypeRegistry.DisplayName(SymbolKind.MixerD));

        // Same instance prefix, so swapping one tile for the other does not renumber a schematic.
        Assert.Equal("MIX", ComponentTypeRegistry.Get(SymbolKind.Mixer).InstancePrefix);
        Assert.Equal("MIX", ComponentTypeRegistry.Get(SymbolKind.MixerD).InstancePrefix);
    }

    [Theory]
    [InlineData("Mixer",  SymbolKind.Mixer)]
    [InlineData("mix",    SymbolKind.Mixer)]
    [InlineData("MixerD", SymbolKind.MixerD)]
    [InlineData("mixd",   SymbolKind.MixerD)]
    public void TryParseCode_ResolvesBothCodes(string code, SymbolKind expected)
    {
        Assert.True(ComponentTypeRegistry.TryParseCode(code, out var kind, out _));
        Assert.Equal(expected, kind);
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [Fact]
    public void BothTiles_HaveIdenticalParameters_BecauseTheyAreTheSameComponent()
    {
        var a = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mixer, 0);
        var b = ComponentTypeRegistry.DefaultParameters(SymbolKind.MixerD, 0);
        Assert.Equal(a.Select(p => (p.Name, p.Expression, p.Unit, p.ShowOnSchematic)),
                     b.Select(p => (p.Name, p.Expression, p.Unit, p.ShowOnSchematic)));
    }

    // Every name here is a key CreateMixerModel reads. A typo in either place is silent — the
    // factory falls back to a default and the mixer runs at a gain nobody typed.
    [Fact]
    public void ParameterNames_AreTheKeysTheFactoryReads()
    {
        var names = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mixer, 0)
                                         .Select(p => p.Name).ToList();
        Assert.Equal(
            ["ConvGain", "Plo", "Zrf", "Zlo", "Zif", "IsoLO_RF", "IsoLO_IF", "IsoRF_IF", "IIP3"],
            names);
    }

    // The gain and the LO drive it holds at are one fact and belong on the schematic together: a
    // conversion gain quoted without its LO level is a number with no meaning, because the mixing
    // law is a product. Nothing else shows, or the symbol wears nine labels.
    [Fact]
    public void OnlyTheGainAndItsLoDrive_ShowOnTheSchematic()
    {
        var shown = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mixer, 0)
                                         .Where(p => p.ShowOnSchematic)
                                         .Select(p => p.Name);
        Assert.Equal(["ConvGain", "Plo"], shown);
    }

    // The non-idealities are OFF at a large number rather than at zero, and "off" has to survive
    // being read as a plain default: a reader meeting 200 dB in the parameter panel needs to be
    // told that is the ideal case, which is what ParameterDescription is for.
    [Fact]
    public void TheNonIdealitiesDefaultToOff_AndSayThatTheyDo()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mixer, 0)
                                      .ToDictionary(p => p.Name, p => p.Expression);
        Assert.Equal("200", ps["IsoLO_RF"]);
        Assert.Equal("200", ps["IsoLO_IF"]);
        Assert.Equal("200", ps["IsoRF_IF"]);
        Assert.Equal("100", ps["IIP3"]);

        // Each description has to say what ITS OWN default means, naming the number, or the table
        // reads as four suspiciously specific claims about a device advertised as ideal.
        foreach (var name in new[] { "IsoLO_RF", "IsoLO_IF", "IsoRF_IF", "IIP3" })
            Assert.Contains(ps[name],
                            ComponentTypeRegistry.ParameterDescription(SymbolKind.Mixer, name));
    }

    [Fact]
    public void EveryParameter_CarriesAMeaning_SoTheGeneratedTableIsSelfExplaining()
    {
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mixer, 0))
            Assert.False(string.IsNullOrWhiteSpace(
                ComponentTypeRegistry.ParameterDescription(SymbolKind.Mixer, p.Name)),
                $"'{p.Name}' has no description, so its row in the generated table would be blank");
    }

    [Fact]
    public void ThePortImpedances_AreDimensionedAsResistances()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mixer, 0)
                                      .ToDictionary(p => p.Name, p => p);
        foreach (var z in new[] { "Zrf", "Zlo", "Zif" })
        {
            Assert.Equal("50", ps[z].Expression);
            Assert.Equal(UnitDimension.Resistance, ps[z].Dimension);
        }
    }

    // ── Pin order is the engine contract ──────────────────────────────────────

    [Fact]
    public void Mixer_HasThreeSignalPins_RfLeftLoBottomIfRight()
    {
        var pins = SymbolPortDefs.For(SymbolKind.Mixer);
        Assert.Equal(["RF", "LO", "IF"], pins.Select(p => p.Name));
        Assert.Equal((-300f,   0f), (pins[0].LocalX, pins[0].LocalY));
        Assert.Equal((   0f, 300f), (pins[1].LocalX, pins[1].LocalY));
        Assert.Equal(( 300f,   0f), (pins[2].LocalX, pins[2].LocalY));
    }

    // MixerModel reads Nodes as [rf+, rf−, lo+, lo−, if+, if−]. Swapping a pair inverts that port's
    // voltage — a circuit that still solves and is wrong — so the order is asserted, not assumed.
    [Fact]
    public void MixerD_HasSixPins_InTheOrderTheModelReads()
    {
        var pins = SymbolPortDefs.For(SymbolKind.MixerD);
        Assert.Equal(["rf+", "rf-", "lo+", "lo-", "if+", "if-"], pins.Select(p => p.Name));
        Assert.Equal((-300f, -100f), (pins[0].LocalX, pins[0].LocalY));
        Assert.Equal((-300f,  100f), (pins[1].LocalX, pins[1].LocalY));
        Assert.Equal((-100f,  300f), (pins[2].LocalX, pins[2].LocalY));
        Assert.Equal(( 100f,  300f), (pins[3].LocalX, pins[3].LocalY));
        Assert.Equal(( 300f, -100f), (pins[4].LocalX, pins[4].LocalY));
        Assert.Equal(( 300f,  100f), (pins[5].LocalX, pins[5].LocalY));
    }

    [Fact]
    public void EveryPinLandsOnTheConnectionGrid()
    {
        foreach (var kind in new[] { SymbolKind.Mixer, SymbolKind.MixerD })
            foreach (var p in SymbolPortDefs.For(kind))
            {
                Assert.Equal(0f, p.LocalX % 100f);
                Assert.Equal(0f, p.LocalY % 100f);
            }
    }

    // ── The glyphs ────────────────────────────────────────────────────────────

    // A circle with a multiplication sign in it has meant "mixer" for sixty years, and it says the
    // one thing about the device a reader needs: what comes out is the PRODUCT of what goes in.
    [Fact]
    public void Mixer_Glyph_IsACircleWithACrossInIt()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Mixer);
        var body = Assert.Single(sym.Primitives.OfType<CirclePrimitive>());
        Assert.Equal(120.0, body.R);

        // The ✕: two lines through the centre, on opposite diagonals, inside the circle.
        var diagonals = sym.Primitives.OfType<LinePrimitive>()
            .Where(l => l.X1 != l.X2 && l.Y1 != l.Y2).ToList();
        Assert.Equal(2, diagonals.Count);
        Assert.All(diagonals, l =>
        {
            Assert.Equal(0.0, (l.X1 + l.X2) / 2, 9);       // centred on the body
            Assert.Equal(0.0, (l.Y1 + l.Y2) / 2, 9);
            Assert.True(Math.Abs(l.X1) < body.R, "the cross must stay inside the circle");
        });
        Assert.NotEqual(Math.Sign(diagonals[0].Y2 - diagonals[0].Y1),
                        Math.Sign(diagonals[1].Y2 - diagonals[1].Y1));
    }

    // The three leads are NOT interchangeable — RF·LO lands on IF and no other assignment does —
    // so each carries its name. A reader who guesses wrong gets a circuit that solves and is wrong.
    [Theory]
    [InlineData(SymbolKind.Mixer)]
    [InlineData(SymbolKind.MixerD)]
    public void BothGlyphs_NameTheirThreePorts(SymbolKind kind)
    {
        var text = BuiltInSymbols.Primitives(kind).Primitives
                                 .OfType<TextPrimitive>().Select(t => t.Content).ToList();
        Assert.Contains("RF", text);
        Assert.Contains("LO", text);
        Assert.Contains("IF", text);
    }

    [Fact]
    public void MixerD_Glyph_KeepsTheCross_ButOnABoxSixLeadsCanLandOn()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.MixerD);
        Assert.Single(sym.Primitives.OfType<RoundedRectPrimitive>());
        Assert.Empty(sym.Primitives.OfType<CirclePrimitive>());

        // The ✕ is the whole of the family resemblance, so it must be the SAME cross.
        var mine  = sym.Primitives.OfType<LinePrimitive>()
                       .Where(l => l.X1 != l.X2 && l.Y1 != l.Y2)
                       .Select(l => (l.X1, l.Y1, l.X2, l.Y2)).ToList();
        var theirs = BuiltInSymbols.Primitives(SymbolKind.Mixer).Primitives.OfType<LinePrimitive>()
                       .Where(l => l.X1 != l.X2 && l.Y1 != l.Y2)
                       .Select(l => (l.X1, l.Y1, l.X2, l.Y2)).ToList();
        Assert.Equal(theirs, mine);

        // Six ± marks, one per net, in the same SymbolPlus role the VCCS uses.
        var marks = sym.Primitives.OfType<TextPrimitive>()
                       .Where(t => t.Content is "+" or "−").ToList();
        Assert.Equal(3, marks.Count(t => t.Content == "+"));
        Assert.Equal(3, marks.Count(t => t.Content == "−"));
        Assert.All(marks.Where(t => t.Content == "+"),
                   t => Assert.Equal(SymbolColorRole.SymbolPlus, t.ColorRole));
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    // The single-ended tile shows three pins but the engine model declares three PORTS, i.e. six
    // nets in ± pair order. The ground returns are minted here, exactly as TermG's port 2 is —
    // a packaging convenience over the SAME engine component, never a parallel model.
    [Fact]
    public void Mixer_ExtractsSixNets_WithEachPortsMinusTiedToGround()
    {
        var inst = ExtractSingle(SymbolKind.Mixer);
        Assert.Equal("Mixer", inst.Reference);
        Assert.Equal(6, inst.NetBindings.Count);
        Assert.Equal("0", inst.NetBindings[1]);
        Assert.Equal("0", inst.NetBindings[3]);
        Assert.Equal("0", inst.NetBindings[5]);
        // …and the three signal nets are distinct, or the mixer would be shorted onto itself.
        var signal = new[] { inst.NetBindings[0], inst.NetBindings[2], inst.NetBindings[4] };
        Assert.Equal(3, signal.Distinct().Count());
        Assert.DoesNotContain("0", signal);
    }

    [Fact]
    public void MixerD_ExtractsItsOwnSixNets_WithNothingTiedToGround()
    {
        var inst = ExtractSingle(SymbolKind.MixerD);
        Assert.Equal("Mixer", inst.Reference);
        Assert.Equal(6, inst.NetBindings.Count);
        Assert.Equal(6, inst.NetBindings.Distinct().Count());
        Assert.DoesNotContain("0", inst.NetBindings);
    }

    [Fact]
    public void BothTiles_CarryTheirParametersThroughExtraction()
    {
        foreach (var kind in new[] { SymbolKind.Mixer, SymbolKind.MixerD })
        {
            var names = ExtractSingle(kind).Overrides.Select(o => o.Name).ToList();
            Assert.Contains("ConvGain", names);
            Assert.Contains("Plo", names);
            Assert.Contains("IsoLO_IF", names);
            Assert.Contains("IIP3", names);
        }
    }

    /// <summary>One placed, unwired mixer, extracted. Every pin gets its own auto-named net.</summary>
    private static CircuitRF.Core.Design.Instance ExtractSingle(SymbolKind kind)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MIX1", Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        model.Components.Add(comp);
        return Assert.Single(NetExtractor.Extract(model).TestBench.Instances);
    }

    // ── Palette ───────────────────────────────────────────────────────────────

    [Fact]
    public void BothAppearInThePalette_AndAreFoundBySearch()
    {
        var kinds = LibraryCatalog.AllItems.Select(i => i.Kind).ToList();
        Assert.Contains(SymbolKind.Mixer, kinds);
        Assert.Contains(SymbolKind.MixerD, kinds);

        Assert.Contains(LibraryCatalog.Search("mixer"),      i => i.Kind == SymbolKind.Mixer);
        Assert.Contains(LibraryCatalog.Search("downconvert"), i => i.Kind == SymbolKind.Mixer);
        Assert.Contains(LibraryCatalog.Search("MixerD"),     i => i.Kind == SymbolKind.MixerD);
    }
}
