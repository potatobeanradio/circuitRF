// L5b: joining a process's rule deck to the technology circuitRF builds from that same process.
// Every fixture is SYNTHETIC, matching the rest of this folder — the repository commits no
// third-party process data.

using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.TechImport;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.TechImport;

public class RuleDeckIntoTechnologyTests
{
    /// <summary>Binds the same stream numbers <see cref="LayerPropertiesReaderTests.Table"/> uses,
    /// which is the only thing a deck and a layer table ever agree on.</summary>
    private const string LayersDef = """
        metaltop_drw = get_polygons(10, 0)
        metallow_drw = get_polygons(5, 0)
        """;

    private const string Deck = """
        if TABLES.include?('metaltop')
          mt_a_value = drc_rules['MT_a'].to_f
          mt_a_l = metaltop_drw.width(mt_a_value.um, euclidian)
          mt_a_l.output('MT.a', "Min. MetalTop width: #{mt_a_value} um")

          mt_b_value = drc_rules['MT_b'].to_f
          mt_b_l = metaltop_drw.space(mt_b_value.um, euclidian)
          mt_b_l.output('MT.b', "Min. MetalTop space: #{mt_b_value} um")

          mt_c_value = drc_rules['MT_c'].to_f
          mt_c_l = metaltop_drw.enclosed(metallow_drw, mt_c_value.um, euclidian)
          mt_c_l.output('MT.c', "enclosure")

          # A shape circuitRF still cannot express, so the report has both halves to state.
          mt_d_l = metaltop_drw.area(mt_c_value.um)
          mt_d_l.output('MT.d', "min area")
        end
        """;

    private const string ValueTable = """
        {
          "drc_rules": {
            "MT_a": 3.0, "MT_b": 4.0, "MT_c": 0.5,
            "p1": 1, "p2": 2, "p3": 3, "p4": 4, "p5": 5, "p6": 6
          }
        }
        """;

    private static ProcessRuleDeck ReadDeck() =>
        RuleDeckReader.Read([LayersDef, Deck], RuleDeckReader.ReadRuleValues(ValueTable));

    private static TechnologyImportResult Build(ProcessRuleDeck? deck) =>
        ProcessTechnologyBuilder.Build(
            ProcessStackReaderTests.Read(),
            LayerPropertiesReader.Read(LayerPropertiesReaderTests.Table),
            "fallback",
            deck);

    [Fact]
    public void DeckRules_LandInTheTechnology_MatchedByStreamNumber()
    {
        var tech = Build(ReadDeck()).Technology;

        var top = new LayerKey(10, 0);

        var width = Assert.Single(tech.DrcRules.Where(r => r.Kind == DrcRuleKind.MinWidth && r.Layer == top));
        Assert.Equal("MT.a", width.Name);
        Assert.Equal(3000, width.ValueDbu);   // 3.0 µm at 1000 DBU/µm

        var space = Assert.Single(tech.DrcRules.Where(r => r.Kind == DrcRuleKind.MinSpacing && r.Layer == top));
        Assert.Equal("MT.b", space.Name);
        Assert.Equal(4000, space.ValueDbu);
    }

    /// <summary>
    /// A deck is the document a fab signs off against; a stack description's own min width/spacing is
    /// a summary written for an electrical model. Where both state a rule, the deck wins.
    /// </summary>
    [Fact]
    public void WhereBothStateARule_TheDeckWins_AndTheStackStillFillsInWhereItDoesNot()
    {
        var withDeck = Build(ReadDeck()).Technology;
        var without  = Build(null).Technology;

        var top = new LayerKey(10, 0);

        // The stack states MetalTop WMIN=1.0 / SMIN=1.2; the deck states 3.0 / 4.0.
        Assert.Equal(1000, Assert.Single(without.DrcRules.Where(r => r.Kind == DrcRuleKind.MinWidth && r.Layer == top)).ValueDbu);
        Assert.Equal(3000, Assert.Single(withDeck.DrcRules.Where(r => r.Kind == DrcRuleKind.MinWidth && r.Layer == top)).ValueDbu);

        // MetalLow is in the stack but not in the deck — its stack-derived rules survive.
        var low = new LayerKey(5, 0);
        Assert.Contains(withDeck.DrcRules, r => r.Layer == low && r.Kind == DrcRuleKind.MinWidth);
        Assert.Contains(withDeck.DrcRules, r => r.Layer == low && r.Kind == DrcRuleKind.MinSpacing);
    }

    /// <summary>
    /// The report is the point: a user who imports a process with hundreds of rules and gets a
    /// handful needs to SEE that number, not discover it by trusting a checker that only ever looked
    /// at a fraction of the deck.
    /// </summary>
    [Fact]
    public void EverythingCircuitRfCannotCheck_IsCounted_AndSaidPlainlyAtImport()
    {
        var notes = Build(ReadDeck()).Notes;

        // Updated for v2, not loosened. The fixture's `enclosed` statement is now READ rather than
        // counted as unreadable, so the note reports a different mix — what the test still guards is
        // the property that matters: the import states how many rules it read, by kind, AND how many
        // it will not enforce. A user who imports a process stating hundreds of rules and gets a
        // handful needs to see both numbers, not discover the second by trusting a checker that only
        // ever looked at a fraction of the deck.
        Assert.Contains(notes, n => n.Contains("rule(s) from the process's design-rule deck"));
        Assert.Contains(notes, n => n.Contains("MinEnclosure"));           // read, and named by kind
        Assert.Contains(notes, n => n.Contains("cannot check yet") && n.Contains("area"));
        Assert.Contains(notes, n => n.Contains("checked against the rules listed above and no others"));
    }

    [Fact]
    public void ADeckRuleNamingALayerTheTableDoesNotDefine_IsNotImported_AndIsReported()
    {
        const string strayLayers = "stray_drw = get_polygons(999, 7)";
        const string strayDeck   = """
            v = drc_rules['MT_a'].to_f
            l = stray_drw.width(v.um, euclidian)
            l.output('S.a', "w")
            """;

        var deck   = RuleDeckReader.Read([strayLayers, strayDeck], RuleDeckReader.ReadRuleValues(ValueTable));
        var result = Build(deck);

        Assert.DoesNotContain(result.Technology.DrcRules, r => r.Layer == new LayerKey(999, 7));
        Assert.Contains(result.Notes, n => n.Contains("the layer table does not define"));
    }

    [Fact]
    public void NoDeck_LeavesTheStackDerivedRulesExactlyAsTheyWere()
    {
        var a = Build(null).Technology.DrcRules;
        var b = ProcessTechnologyBuilder.Build(
            ProcessStackReaderTests.Read(),
            LayerPropertiesReader.Read(LayerPropertiesReaderTests.Table),
            "fallback").Technology.DrcRules;

        Assert.Equal(a.Select(r => (r.Name, r.Kind, r.Layer, r.ValueDbu)),
                     b.Select(r => (r.Name, r.Kind, r.Layer, r.ValueDbu)));
    }
}
