using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// M2 — the behavioural and controlled sources, read into the shape an equation-defined device
/// takes. Every fixture is synthetic.
/// </summary>
public sealed class SpiceBehaviouralSourceTests
{
    private static SpiceNetlistResult Read(string text) => SpiceNetlistReader.Read(text);

    private static Instance Only(SpiceNetlistResult r, string cell, string instance)
        => r.Library.Cells.Single(c => c.Name.Equals(cell, StringComparison.OrdinalIgnoreCase))
            .Instances.Single(i => i.InstanceName.Equals(instance, StringComparison.OrdinalIgnoreCase));

    private static string Value(Instance i, string name)
        => i.Overrides.Single(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Expression;

    // ── the reader ────────────────────────────────────────────────────────────

    /// <summary>Both spellings of the behavioural form occur, and neither is the majority.</summary>
    [Theory]
    [InlineData("E1 out 0 VALUE = {2*V(a,b)}")]
    [InlineData("E1 out 0 VALUE {2*V(a,b)}")]
    [InlineData("E1 out 0 VALUE={2*V(a,b)}")]
    public void B1_BothSpellingsOfTheValueFormAreRead(string line)
    {
        var e1 = Only(Read($"""
            .subckt part out a b
            {line}
            .ends part
            """), "part", "E1");

        Assert.Equal("E", e1.Reference);
        Assert.Equal(["out", "0"], e1.NetBindings);
        Assert.Equal("2*V(a,b)", Value(e1, "VALUE"));
    }

    /// <summary>
    /// A positional gain is exactly <c>k*V(c+,c−)</c>, so it is written that way and the translation
    /// reads one shape instead of four.
    /// </summary>
    [Fact]
    public void B2_APositionalGainIsNormalisedToTheValueForm()
    {
        var e1 = Only(Read("""
            .subckt part out a b
            E1 out 0 a b 2.5
            .ends part
            """), "part", "E1");

        Assert.Equal(["out", "0"], e1.NetBindings);
        Assert.Equal("(2.5)*V(a,b)", Value(e1, "VALUE"));
    }

    /// <summary>A current-controlled source differs from a voltage-controlled one only in what it senses.</summary>
    [Fact]
    public void B3_ACurrentControlledSourceNamesItsSourcesBranch()
    {
        var r = Read("""
            .subckt part out a b
            Vs a b 0
            F1 out 0 Vs 3
            H1 out 0 Vs 4
            .ends part
            """);

        Assert.Equal("(3)*I(Vs)", Value(Only(r, "part", "F1"), "VALUE"));
        Assert.Equal("G", Only(r, "part", "F1").Reference);
        Assert.Equal("(4)*I(Vs)", Value(Only(r, "part", "H1"), "VALUE"));
        Assert.Equal("E", Only(r, "part", "H1").Reference);
    }

    /// <summary>The zero-volt sensor idiom — most of the V lines in these files are this.</summary>
    [Fact]
    public void B4_AZeroVoltSourceIsReadWithItsValue()
    {
        var v = Only(Read("""
            .subckt part a b
            V_sense a b 0
            .ends part
            """), "part", "V_sense");

        Assert.Equal("V", v.Reference);
        Assert.Equal("0", Value(v, "DC"));
    }

    [Theory]
    [InlineData("V1 a b DC 5",  "5")]
    [InlineData("V1 a b DC=5",  "5")]
    [InlineData("V1 a b 5",     "5")]
    [InlineData("I1 a b 2m",    "0.002")]
    public void B5_AnIndependentSourceKeepsItsDcValue(string line, string expected)
    {
        var r = Read($"""
            .subckt part a b
            {line}
            .ends part
            """);

        Assert.Equal(expected, Value(r.Library.Cells.Single().Instances.Single(), "DC"));
        Assert.Empty(r.IncompleteCells);
    }

    /// <summary>
    /// circuitRF drives a design from its own TestBench, so a stimulus inside a device definition
    /// contributes its DC level — and says so, because a waveform read as a number is
    /// indistinguishable from a source that never had one.
    /// </summary>
    [Fact]
    public void B6_AWaveformContributesItsDcLevelAndIsNoted()
    {
        var r = Read("""
            .subckt part a b
            V1 a b PULSE(3 5 0 1n 1n 10n 20n)
            .ends part
            """);

        Assert.Equal("3", Value(r.Library.Cells.Single().Instances.Single(), "DC"));
        Assert.Contains("PULSE", string.Join("\n", r.Notes.Select(n => n.Message)));
        Assert.Empty(r.IncompleteCells);
    }

    /// <summary>The transfer forms circuitRF has no analysis for stay refused, and refused BY NAME.</summary>
    [Theory]
    [InlineData("E1 out 0 TABLE {V(a,b)} = (0,0) (1,1)", "table")]
    [InlineData("E1 out 0 LAPLACE {V(a,b)} = {1/(1+s)}", "Laplace")]
    [InlineData("E1 out 0 FREQ {V(a,b)} = (1,0,0)",      "frequency response")]
    [InlineData("E1 out 0 POLY(1) a b 0 1",              "POLY")]
    public void B7_ATransferFormCircuitRfHasNoAnalysisForIsRefusedByName(string line, string mustSay)
    {
        var r = Read($"""
            .subckt part out a b
            {line}
            .ends part
            """);

        Assert.Contains("part", r.IncompleteCells);
        Assert.Contains(mustSay, string.Join("\n", r.Notes.Select(n => n.Message)),
                        StringComparison.OrdinalIgnoreCase);
    }

    // ── the transfer expression, as a device's equation ───────────────────────

    private static SpiceBehaviouralForm Form(string expr, string plus = "p", string minus = "m")
        => SpiceBehaviouralSource.Read(expr, plus, minus);

    /// <summary>
    /// A node the source is not connected to becomes an extra port that draws no current — the
    /// equation-defined device's own sense-port idiom.
    /// </summary>
    [Fact]
    public void B8_ASensedPairBecomesAPortAndTheSourcesOwnPairIsPortOne()
    {
        var f = Form("V(a,b)*2");

        Assert.Null(f.Refusal);
        Assert.Equal([new SpiceSensePair("p", "m"), new SpiceSensePair("a", "b")], f.Pairs);
        Assert.Equal("(_v2*2)", f.Equation);
    }

    /// <summary>
    /// A port is where the device ATTACHES. An expression that also reads the pair the source is
    /// connected across reads port 1 — a second port onto the same two nodes is an extra unknown for
    /// no extra physics.
    /// </summary>
    [Fact]
    public void B9_TheSourcesOwnPairIsNotOpenedTwice()
    {
        var f = Form("V(p,m) - 1", "p", "m");

        Assert.Single(f.Pairs);
        Assert.Equal("(_v1-1)", f.Equation);
    }

    /// <summary>The same pair written the other way round is that pair, negated.</summary>
    [Fact]
    public void B10_AReversedPairIsTheSamePortNegated()
    {
        var f = Form("V(a,b) + V(b,a)");

        Assert.Equal(2, f.Pairs.Count);
        Assert.Equal("(_v2+(-_v2))", f.Equation);
    }

    /// <summary>A one-argument <c>V()</c> is referenced to ground.</summary>
    [Fact]
    public void B11_AOneArgumentNodeVoltageIsReferencedToGround()
    {
        var f = Form("V(a)");
        Assert.Equal(new SpiceSensePair("a", "0"), f.Pairs[1]);
    }

    [Fact]
    public void B12_ABranchCurrentBecomesAControlCurrentReference()
    {
        var f = Form("-I(V_sense2)");

        Assert.Equal(["V_sense2"], f.ControlSources);
        Assert.Equal("(-_c1)", f.Equation);
        Assert.Single(f.Pairs);
    }

    // ── affine ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decided on the AST: these are the same element, and a gain that is a parameter is still an
    /// ideal source.
    /// </summary>
    [Theory]
    [InlineData("2*V(a,b)")]
    [InlineData("V(a,b)*2")]
    [InlineData("V(a,b)/0.5")]
    public void B13_AnIdealControlledSourceIsRecognisedWhicheverWayItIsWritten(string expr)
    {
        var f = Form(expr);

        Assert.True(f.IsAffine);
        Assert.Equal(1, f.AffineOf);
        Assert.False(f.AffineIsCurrent);
        Assert.Equal(2.0, new Evaluator().Eval(f.AffineGain!, new Scope("t")).AsReal(), 12);
    }

    [Fact]
    public void B14_AGainThatIsAParameterIsStillAnIdealSource()
    {
        var f = Form("k*V(a,b)");
        Assert.True(f.IsAffine);

        var scope = new Scope("t");
        scope.Bind("k", "7");
        Assert.Equal(7.0, new Evaluator().Eval(f.AffineGain!, scope).AsReal(), 12);
    }

    [Theory]
    [InlineData("V(a,b)*V(c,d)")]          // a product of two sensed quantities
    [InlineData("tanh(V(a,b))")]           // a function call
    [InlineData("V(a,b)^2")]               // a power
    [InlineData("V(a,b) + V(c,d)")]        // two terms — real, and not what circuitRF's VCVS states
    [InlineData("V(a,b) + 1")]             // an offset — likewise
    public void B15_AnythingElseIsNotAffine(string expr)
        => Assert.False(Form(expr).IsAffine);

    // ── refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public void B16_AnUnreadableExpressionIsRefusedRatherThanGuessedAt()
    {
        var f = Form("2 * * V(a,b)");
        Assert.NotNull(f.Refusal);
    }
}
