using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.TechImport;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// L5b: reading a process's own design-rule deck.
///
/// <para>Every fixture here is SYNTHETIC — the repository commits no third-party process data, and a
/// test keyed to one kit on one machine fails on a fresh clone. What the fixtures reproduce is the
/// deck LANGUAGE's own grammar (layer bindings, a rule-value table, loops over a list of layers,
/// interpolated rule names), which is what the reader actually keys on.</para>
/// </summary>
public class RuleDeckReaderTests
{
    private const string LayersDef = """
        # comment line
        metal1_drw = get_polygons(8, 0)
        metal2_drw = get_polygons(10, 0)
        metal3_drw = get_polygons(30, 0)
        via1_drw   = get_polygons(19, 0)
        """;

    private const string ValueTable = """
        {
          "drc_rules": {
            "M1_a": 0.16,
            "M1_b": 0.18,
            "Mn_a": 0.20,
            "Mn_b": 0.21,
            "V1_a": 0.19,
            "V1_b": 0.22,
            "X_1": 1.0,
            "X_2": 2.0,
            "X_3": 3.0
          }
        }
        """;

    private static IReadOnlyDictionary<string, double> Values() => RuleDeckReader.ReadRuleValues(ValueTable);

    // ── Recognition ──────────────────────────────────────────────────────────

    [Fact]
    public void LooksLikeRuleDeck_RecognisesTheGrammar_NotAFileName()
    {
        Assert.True(RuleDeckReader.LooksLikeRuleDeck(LayersDef));
        Assert.True(RuleDeckReader.LooksLikeRuleDeck("  v = drc_rules['M1_a'].to_f"));
        Assert.False(RuleDeckReader.LooksLikeRuleDeck("this is just prose"));
        Assert.False(RuleDeckReader.LooksLikeRuleDeck(""));
    }

    [Fact]
    public void LooksLikeRuleValueTable_IsStructural()
    {
        Assert.True(RuleDeckReader.LooksLikeRuleValueTable(ValueTable));
        Assert.False(RuleDeckReader.LooksLikeRuleValueTable("{ \"a\": 1 }"));       // too few to be a table
        Assert.False(RuleDeckReader.LooksLikeRuleValueTable("not json at all"));
    }

    [Fact]
    public void ReadRuleValues_ReadsTheMap_AndNeverThrowsOnRubbish()
    {
        Assert.Equal(0.16, Values()["M1_a"]);
        Assert.Empty(RuleDeckReader.ReadRuleValues("{ broken"));
    }

    // ── The two rule shapes circuitRF can express ────────────────────────────

    [Fact]
    public void Width_And_Space_OnOneLayer_AreRead_WithTheDecksOwnNameAndValue()
    {
        const string deck = """
            if TABLES.include?('metal1')
              m1_a_value = drc_rules['M1_a'].to_f
              m1_a_l = metal1_drw.width(m1_a_value.um, euclidian)
              m1_a_l.output('M1.a', "Min. Metal1 width: #{m1_a_value} um")

              m1_b_value = drc_rules['M1_b'].to_f
              m1_b_l = metal1_drw.space(m1_b_value.um, euclidian)
              m1_b_l.output('M1.b', "Min. Metal1 space: #{m1_b_value} um")
            end
            """;

        var deckResult = RuleDeckReader.Read([LayersDef, deck], Values());

        Assert.Equal(2, deckResult.Rules.Count);

        var width = deckResult.Rules.Single(r => r.Kind == DrcRuleKind.MinWidth);
        Assert.Equal("M1.a", width.Name);
        Assert.Equal(8, width.StreamLayer);
        Assert.Equal(0, width.StreamDatatype);
        Assert.Equal(0.16, width.ValueUm);
        Assert.Contains("Metal1 width", width.Description);

        var space = deckResult.Rules.Single(r => r.Kind == DrcRuleKind.MinSpacing);
        Assert.Equal("M1.b", space.Name);
        Assert.Equal(0.18, space.ValueUm);
    }

    /// <summary>
    /// The comment stripper has to be quote-aware: a deck states each rule's own description as an
    /// interpolated string, so cutting at the first '#' loses every rule NAME and description — the
    /// two things that let a violation be traced back to the process's own documentation.
    /// </summary>
    [Fact]
    public void AnInterpolatedDescription_DoesNotSwallowTheRulesOwnName()
    {
        const string deck = """
            v = drc_rules['M1_a'].to_f
            l = metal1_drw.width(v.um, euclidian)   # a real trailing comment
            l.output('M1.a', "width is #{v} um")
            """;

        var r = Assert.Single(RuleDeckReader.Read([LayersDef, deck], Values()).Rules);
        Assert.Equal("M1.a", r.Name);
    }

    // ── Loops ────────────────────────────────────────────────────────────────

    [Fact]
    public void ARuleStatedInsideALoop_IsReadOncePerLayerInTheList()
    {
        const string deck = """
            if TABLES.include?('metaln')
              mets_lay = [metal2_drw, metal3_drw]

              mets_lay.each_with_index do |met_lay, index|
                mn_a_value = drc_rules['Mn_a'].to_f
                mn_a_l = met_lay.width(mn_a_value.um, euclidian)
                mn_a_l.output("M#{index}.a", "Min. Metal#{index} width: #{mn_a_value} um")
              end
            end
            """;

        var rules = RuleDeckReader.Read([LayersDef, deck], Values()).Rules;

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r is { StreamLayer: 10, ValueUm: 0.20 });
        Assert.Contains(rules, r => r is { StreamLayer: 30, ValueUm: 0.20 });

        // Each copy names the layer it landed on, so the imported rules stay distinguishable even
        // though the deck wrote one interpolated name for all of them.
        Assert.Contains(rules, r => r.Name.Contains("metal2_drw"));
        Assert.Contains(rules, r => r.Name.Contains("metal3_drw"));
    }

    /// <summary>
    /// The bug this cost real rules on a real process: two files of one deck bind the SAME ordinary
    /// name to different lists, because each is a local variable in its own program. Collecting array
    /// bindings globally lets the last file read win, and every earlier file's rules then resolve
    /// against another file's list.
    /// </summary>
    [Fact]
    public void ATwoFileDeck_BindingTheSameListNameDifferently_KeepsEachFilesOwnList()
    {
        const string beol = """
            mets_lay = [metal2_drw, metal3_drw]
            mets_lay.each do |met_lay|
              v = drc_rules['Mn_a'].to_f
              l = met_lay.width(v.um, euclidian)
              l.output('Mn.a', "w")
            end
            """;

        // A later file binding the same name to symbols that resolve to nothing at all.
        const string density = """
            mets_lay = [metal1, metal2, metal3]
            """;

        var rules = RuleDeckReader.Read([LayersDef, beol, density], Values()).Rules;

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.StreamLayer == 10);
        Assert.Contains(rules, r => r.StreamLayer == 30);
    }

    // ── Everything else is REPORTED, never silently dropped ──────────────────

    [Fact]
    public void RuleShapesCircuitRfCannotCheck_AreCountedByOperation()
    {
        const string deck = """
            v = drc_rules['V1_a'].to_f
            a = via1_drw.enclosed(metal1_drw, v.um, euclidian)
            b = metal1_drw.sep(metal2_drw, v.um)
            c = metal1_drw.enclosed(metal2_drw, v.um)
            """;

        var result = RuleDeckReader.Read([LayersDef, deck], Values());

        Assert.Empty(result.Rules);
        Assert.Equal(3, result.UnsupportedTotal);
        Assert.Contains(result.Unsupported, u => u is { Operation: "enclosed", Count: 2 });
        Assert.Contains(result.Unsupported, u => u.Operation == "sep");
    }

    /// <summary>
    /// A rule on a DERIVED layer expression (one layer minus another) is real, but the layer it
    /// applies to is not one circuitRF draws on. Mapping it onto the base layer would widen the rule
    /// silently, so it is reported instead.
    /// </summary>
    [Fact]
    public void ARuleOnADerivedLayer_IsReportedRatherThanMappedOntoTheBaseLayer()
    {
        const string deck = """
            via1_nseal = via1_drw.not(edgeseal_drw)
            v = drc_rules['V1_b'].to_f
            l = via1_nseal.space(v.um, euclidian)
            l.output('V1.b', "space")
            """;

        var result = RuleDeckReader.Read([LayersDef, deck], Values());

        Assert.Empty(result.Rules);
        Assert.Contains(result.Unsupported, u => u.Operation.Contains("derived"));
    }

    [Fact]
    public void ARuleWhoseValueTheTableDoesNotState_IsReportedRatherThanGuessedAt()
    {
        const string deck = """
            v = drc_rules['NOT_IN_TABLE'].to_f
            l = metal1_drw.width(v.um, euclidian)
            l.output('X.a', "w")
            """;

        var result = RuleDeckReader.Read([LayersDef, deck], Values());

        Assert.Empty(result.Rules);
        Assert.Contains(result.Unsupported, u => u.Operation.Contains("value not stated"));
    }

    [Fact]
    public void ADeckThatBindsNoLayer_ImportsNothing_AndSaysWhy()
    {
        const string deck = """
            v = drc_rules['M1_a'].to_f
            l = metal1_drw.width(v.um, euclidian)
            """;

        var result = RuleDeckReader.Read([deck], Values());

        Assert.Empty(result.Rules);
        Assert.NotEmpty(result.Notes);
    }

    // ── Choosing among several value tables ─────────────────────────────────

    /// <summary>
    /// A kit ships one value table per corner AND unrelated configuration that is structurally a map
    /// of numbers. Picking the first candidate found would read the deck against a table answering
    /// none of its keys — every rule would fall out as "value not stated" and the import would look
    /// like the deck was unreadable. Coverage settles it with no knowledge of any file's name.
    /// </summary>
    [Fact]
    public void TheValueTable_IsChosenByWhichOneTheDecksOwnKeysResolveAgainst()
    {
        const string deck = """
            v = drc_rules['M1_a'].to_f
            l = metal1_drw.width(v.um, euclidian)
            l.output('M1.a', "w")
            """;

        var unrelated = RuleDeckReader.ReadRuleValues("""
            { "grid_x": 1, "grid_y": 2, "pitch": 3, "a": 4, "b": 5, "c": 6, "d": 7, "e": 8, "f": 9 }
            """);

        // Unrelated table FIRST — the position it would have been chosen from.
        var result = RuleDeckReader.Read([LayersDef, deck], [unrelated, Values()]);

        var r = Assert.Single(result.Rules);
        Assert.Equal(0.16, r.ValueUm);
        Assert.Equal(1, result.ChosenValueTable);
    }

    [Fact]
    public void NoTableAnsweringAnything_IsReported_AndALiteralValueStillReads()
    {
        const string deck = """
            l = metal1_drw.width(0.25.um, euclidian)
            l.output('M1.lit', "literal")
            """;

        var result = RuleDeckReader.Read([LayersDef, deck], ruleValues: null);

        var r = Assert.Single(result.Rules);
        Assert.Equal(0.25, r.ValueUm);
    }
}
