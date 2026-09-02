using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// M1 — the reader reads what these files actually write, and a refusal names the real reason.
///
/// <para><b>No new capability is added here and the importable count does not move</b> (the design
/// note's own measurement: reader hygiene alone takes it from 1 of 45 to 1 of 45). What moves is
/// what a refusal SAYS. A subcircuit refused because the reader took the separator <c>PARAMS:</c>
/// for the name of a missing definition is refused for a reason that is not true, and the person
/// holding the file cannot act on it.</para>
///
/// <para><b>Every fixture is synthetic.</b> The repository commits no third-party kit data, so
/// nothing here names a supplier, a product or a part.</para>
/// </summary>
public sealed class SpiceReaderHygieneTests
{
    private static SpiceNetlistResult Read(string text) => SpiceNetlistReader.Read(text);

    private static Cell Cell(SpiceNetlistResult r, string name)
        => Assert.Single(r.Library.Cells, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Notes(SpiceNetlistResult r)
        => string.Join("\n", r.Notes.Select(n => n.Message));

    private static double Value(string rewritten, params (string Name, string Expr)[] bindings)
    {
        var scope = new Scope("test");
        foreach (var (n, e) in bindings) scope.Bind(n, e);
        return new Evaluator().Eval(rewritten, scope).AsReal();
    }

    // ── `PARAMS:` on a call line ──────────────────────────────────────────────

    /// <summary>
    /// The reader takes the name of what implements an element from the END of the bare-word run,
    /// which is what lets one rule cover a three- and a four-terminal device. <c>PARAMS:</c> is a
    /// bare word, so it took the separator for the name — and the refusal then named a subcircuit
    /// the file never mentions.
    /// </summary>
    [Fact]
    public void H1_ParamsSeparatorOnACallLineIsNotTheSubcircuitName()
    {
        var r = Read("""
            .subckt core a b  k=1
            R1 a b {k}
            .ends core
            .subckt part in out
            X1 in out core PARAMS: k=2
            .ends part
            """);

        var x1 = Cell(r, "part").Instances.Single();
        Assert.Equal("core", x1.Reference);
        Assert.Equal(["in", "out"], x1.NetBindings);
        Assert.Equal("2", x1.Overrides.Single(o => o.Name == "k").Expression);
        Assert.Empty(r.IncompleteCells);
    }

    // ── `limit`, by arity ─────────────────────────────────────────────────────

    /// <summary>
    /// Three arguments is an ordinary clamp. Read as a distribution it is reduced to its first
    /// argument and the clamp is simply gone — the expression still parses, still evaluates, and no
    /// longer bounds anything.
    /// </summary>
    [Theory]
    [InlineData("limit(a,0,1)", 2.0, 1.0)]     // above the ceiling
    [InlineData("limit(a,0,1)", -3.0, 0.0)]    // below the floor
    [InlineData("limit(a,0,1)", 0.25, 0.25)]   // inside, untouched
    public void H2_ThreeArgumentLimitIsAClamp(string written, double a, double expected)
    {
        var stats = new List<SpiceStatisticalUse>();
        string expr = SpiceExpression.Rewrite(written, stats);

        Assert.Equal(expected, Value(expr, ("a", a.ToString(System.Globalization.CultureInfo.InvariantCulture))), 12);
        Assert.Empty(stats);
    }

    /// <summary>Two arguments is the distribution, and stays reduced to its nominal AND reported.</summary>
    [Fact]
    public void H3_TwoArgumentLimitIsStillADistributionAndIsStillReported()
    {
        var stats = new List<SpiceStatisticalUse>();
        string expr = SpiceExpression.Rewrite("limit(2.5, 0.1)", stats);

        Assert.Equal(2.5, Value(expr), 12);
        var use = Assert.Single(stats);
        Assert.Equal("limit", use.Function);
        Assert.Equal("2.5", use.Nominal);
    }

    /// <summary>A clamp wrapped round a distribution keeps both readings, each at its own arity.</summary>
    [Fact]
    public void H4_ClampAndDistributionNest()
    {
        var stats = new List<SpiceStatisticalUse>();
        string expr = SpiceExpression.Rewrite("limit(gauss(4,1), 0, 3)", stats);

        Assert.Equal(3.0, Value(expr), 12);
        Assert.Equal("gauss", Assert.Single(stats).Function);
    }

    // ── sgn, inner braces ─────────────────────────────────────────────────────

    [Fact]
    public void H5_SgnIsCircuitRfsSign()
    {
        Assert.Equal(-1.0, Value(SpiceExpression.Rewrite("sgn(a)"), ("a", "-7")), 12);

        // A word it is merely a substring of is left alone.
        Assert.Equal(5.0, Value(SpiceExpression.Rewrite("sgnal"), ("sgnal", "5")), 12);
    }

    /// <summary>
    /// A parameter written in braces INSIDE a larger expression. <see cref="SpiceExpression.Unwrap"/>
    /// only strips a pair spanning the whole value, so the inner pair used to reach circuitRF's
    /// parser — which has no brace in its grammar and stopped at the character.
    /// </summary>
    [Fact]
    public void H6_ANestedBraceIsGroupingAndParses()
    {
        string expr = SpiceExpression.Rewrite("{limit((a*(b/300)**{c}),-1e12,1e12)}");
        Assert.Equal(2.0 * Math.Pow(3.0 / 300.0, 4.0), Value(expr, ("a", "2"), ("b", "3"), ("c", "4")), 12);
    }

    // ── a passive's initial condition ─────────────────────────────────────────

    /// <summary>
    /// The comma keeps the value and the condition in one word, so nothing read as a value and the
    /// capacitor was refused for having none — losing a component over a setting circuitRF has no
    /// analysis for anyway.
    /// </summary>
    [Theory]
    [InlineData("CQB b 0 1u,IC=0")]
    [InlineData("CQB b 0 1u,IC = 0")]
    public void H7_ACapacitorKeepsItsValueAndNotesTheInitialCondition(string line)
    {
        var r = Read($"""
            .subckt part b
            {line}
            .ends part
            """);

        var c = Cell(r, "part").Instances.Single();
        Assert.Equal("C", c.Reference);
        Assert.Equal("1E-06", c.Overrides.Single(o => o.Name == "C").Expression);
        Assert.DoesNotContain(c.Overrides, o => o.Name.Equals("IC", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("initial condition", Notes(r), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.IncompleteCells);
    }

    /// <summary>A comma inside an argument list is not a suffix — <c>if(a,b,c)</c> is one word.</summary>
    [Fact]
    public void H8_ACommaInsideBracketsDoesNotSplitAValue()
    {
        var r = Read("""
            .subckt part a b  k=1
            R1 a b {if(k>0,10,20)}
            .ends part
            """);

        var r1 = Cell(r, "part").Instances.Single();
        Assert.Equal(["a", "b"], r1.NetBindings);
        Assert.Equal("if(k>0,10,20)", r1.Overrides.Single(o => o.Name == "R").Expression);
    }

    // ── element letters circuitRF will not implement ──────────────────────────

    [Theory]
    [InlineData("S1 a b c d SWMOD", "switch")]
    [InlineData("W1 a b VSENSE SWMOD", "switch")]
    [InlineData("T1 a 0 b 0 Z0=50 TD=1n", "transmission line")]
    [InlineData("O1 a 0 b 0 LMOD", "transmission line")]
    [InlineData("U1 a b UMOD", "circuitRF does not have")]
    public void H9_AnUnimplementedElementIsRefusedByName(string line, string mustSay)
    {
        var r = Read($"""
            .subckt part a b c d
            {line}
            .ends part
            """);

        Assert.Contains("part", r.IncompleteCells);
        Assert.Contains(mustSay, Notes(r), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a kind this reader does not read", Notes(r));
    }

    // ── TEMP ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>TEMP</c> is the simulator's own temperature variable, not a <c>.param</c>. circuitRF holds
    /// the ambient under the lower-case name its elaborator reserves, so the rewrite is a spelling
    /// change onto a name that actually resolves.
    /// </summary>
    [Fact]
    public void H10_TempIsRewrittenToTheAmbientsOwnName()
    {
        var r = Read("""
            .subckt part a b
            R1 a b {TEMP*1k}
            .ends part
            """);

        Assert.Equal("temp*1000", Cell(r, "part").Instances.Single()
                                      .Overrides.Single(o => o.Name == "R").Expression);
    }

    /// <summary>A file that also DECLARES the name is aligned to the same spelling — circuitRF's scopes are ordinal.</summary>
    [Fact]
    public void H11_ADeclaredTempIsAlignedToTheSameSpelling()
    {
        var r = Read("""
            .param TEMP = 40
            .subckt part a b
            R1 a b {TEMP}
            .ends part
            """);

        Assert.Equal("temp", Assert.Single(r.Variables).Name);
        Assert.Equal("temp", Cell(r, "part").Instances.Single()
                                .Overrides.Single(o => o.Name == "R").Expression);
    }

    // ── time ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// circuitRF solves in steady state, where any elapsed time has passed, so a lower bound on the
    /// time variable holds. That is an interpretation of a transient construct and has to say so out
    /// loud.
    /// </summary>
    [Fact]
    public void H12_TimeInsideAConditionIsReadAsSteadyStateAndNoted()
    {
        var notes = new List<string>();
        string expr = SpiceExpression.Rewrite("if(time > 0, a*2, 0)", null, notes);

        Assert.Equal(4.0, Value(expr, ("a", "2")), 12);
        Assert.False(SpiceExpression.ReferencesTime(expr));
        Assert.Contains("steady state", Assert.Single(notes));
    }

    /// <summary>
    /// <b>The comparison is what is read, not the conditional around it.</b> An <c>if</c> whose
    /// condition merely MENTIONS time cannot be replaced by its then-branch: an upper bound on time
    /// is FALSE in steady state, and a real file writes exactly this shape. Replacing the whole
    /// conditional here would stick the output at 0 forever — a different circuit that converges.
    /// </summary>
    [Theory]
    [InlineData("if(a > 1 || time < 1n, 7, 9)", 7.0)]   // the time half is FALSE, so a > 1 decides
    [InlineData("if(a > 9 || time < 1n, 7, 9)", 9.0)]   // …and with both false, the else-branch
    [InlineData("if(a > 9 || time > 0,  7, 9)", 7.0)]   // the time half is TRUE, and that is enough
    [InlineData("if(a > 1 && time > 0,  7, 9)", 7.0)]
    [InlineData("if(a > 1 && time < 1n, 7, 9)", 9.0)]
    public void H12b_OnlyTheComparisonIsRead_NotTheConditionalAroundIt(string written, double expected)
    {
        var notes = new List<string>();
        string expr = SpiceExpression.Rewrite(written, null, notes);

        Assert.Equal(expected, Value(expr, ("a", "2")), 12);
        Assert.Single(notes);
    }

    /// <summary>
    /// Both spellings of the logical connectives. This dialect writes them with one character;
    /// circuitRF writes them with two, and there is nothing else either character could mean in its
    /// grammar. Measured worth 28 of one library's 34 subcircuits.
    /// </summary>
    [Theory]
    [InlineData("if(a > 1 & b > 1, 7, 9)",  7.0)]
    [InlineData("if(a > 9 & b > 1, 7, 9)",  9.0)]
    [InlineData("if(a > 9 | b > 1, 7, 9)",  7.0)]
    [InlineData("if(a > 9 | b > 9, 7, 9)",  9.0)]
    [InlineData("if(a > 1 && b > 1, 7, 9)", 7.0)]
    [InlineData("if(a > 9 || b > 9, 7, 9)", 9.0)]
    public void H12c_SingleCharacterLogicalOperatorsAreCircuitRfsDoubled(string written, double expected)
        => Assert.Equal(expected, Value(SpiceExpression.Rewrite(written), ("a", "2"), ("b", "3")), 12);

    /// <summary>The ternary spelling of the same thing, since that is how half of them are written.</summary>
    [Fact]
    public void H13_TheTernarySpellingIsReadTheSameWay()
    {
        var notes = new List<string>();
        string expr = SpiceExpression.Rewrite("time > 0 ? a : 0", null, notes);

        Assert.Equal(2.0, Value(expr, ("a", "2")), 12);
        Assert.Single(notes);
    }

    /// <summary>A ramp has no steady-state value at all, so it stays refused — and refused by name.</summary>
    [Fact]
    public void H14_TimeOutsideAConditionIsRefusedByName()
    {
        var r = Read("""
            .subckt part a b
            R1 a b {time*1k}
            .ends part
            """);

        Assert.Contains("part", r.IncompleteCells);
        Assert.Contains("transient time variable", Notes(r));
        Assert.Contains("no transient analysis", Notes(r));
    }

    // ── the two dialects' spellings of the same functions ─────────────────────

    /// <summary>
    /// This dialect is case-insensitive; circuitRF's parser matches <c>if</c> as a keyword and its
    /// evaluators match a function name ordinally. So <c>IF(…)</c> used to parse cleanly as a call
    /// to an unknown function and fail at SIMULATE time, in a message that named neither the file
    /// nor the element.
    /// </summary>
    [Theory]
    [InlineData("IF(a>1,7,9)",   7.0)]
    [InlineData("MAX(a,b)",      3.0)]
    [InlineData("Min(a,b)",      2.0)]
    [InlineData("TANH(0)",       0.0)]
    [InlineData("ABS(0-a)",      2.0)]
    public void H16_AFunctionCallIsSpelledTheWayCircuitRfSpellsIt(string written, double expected)
        => Assert.Equal(expected, Value(SpiceExpression.Rewrite(written), ("a", "2"), ("b", "3")), 12);

    /// <summary>
    /// Only a NAME FOLLOWED BY A BRACKET is a function call. A parameter or a net that happens to be
    /// called <c>MAX</c> is neither, and re-spelling it would rename it.
    /// </summary>
    [Fact]
    public void H17_ANameThatIsNotACallIsLeftAlone()
        => Assert.Equal("MAX*2", SpiceExpression.Rewrite("MAX*2"));

    /// <summary>
    /// Names this dialect spells differently, each a pure spelling change onto a function circuitRF
    /// already implements with the same meaning and arity.
    /// </summary>
    [Theory]
    [InlineData("arctan(a)")]
    [InlineData("ARCTAN(a)")]
    public void H18_TheDialectsOwnNameForAnInverseTangent(string written)
        => Assert.Equal(Math.Atan(2.0), Value(SpiceExpression.Rewrite(written), ("a", "2")), 12);

    // ── a `.model` card declared inside a `.subckt` ───────────────────────────

    /// <summary>
    /// This dialect scopes a card declared inside a <c>.subckt</c> to that subcircuit; circuitRF
    /// holds one card list. The collision is already reported — what the note has to say is that the
    /// two were LOCAL to different definitions, because "already defined" reads like a duplicated
    /// line in a file where nothing is duplicated.
    /// </summary>
    [Fact]
    public void H15_ARedefinedLocalCardSaysWhichSubcircuitsItWasLocalTo()
    {
        var r = Read("""
            .subckt one a b
            .model DBODY D(IS=1e-14)
            D1 a b DBODY
            .ends one
            .subckt two a b
            .model DBODY D(IS=3e-14)
            D2 a b DBODY
            .ends two
            """);

        string notes = Notes(r);
        Assert.Contains("already defined", notes);
        Assert.Contains("local to a subcircuit", notes);
        Assert.Contains("'one'", notes);
        Assert.Contains("'two'", notes);
    }

    // ── a source line that states BOTH a waveform and a DC value ──────────────

    /// <summary>
    /// A line may state a stimulus circuitRF drops AND the DC value it should keep, in either
    /// order. Reading whichever came first made a bias supply silently become a short.
    /// </summary>
    [Theory]
    [InlineData("V1 a b AC 1 DC 5")]
    [InlineData("V1 a b DC 5 AC 1")]
    [InlineData("V1 a b PULSE(0 12 0 1n 1n 1u 2u) DC 5")]
    public void H10_AStatedDcValueSurvivesAWaveformOnTheSameLine(string line)
    {
        var r = Read($".subckt part a b\n{line}\nR1 a b 1k\n.ends part\n");
        var inst = Cell(r, "part").Instances.Single(i => i.InstanceName == "V1");

        Assert.Equal("5", inst.Overrides.Single(o => o.Name == "DC").Expression);
    }

    /// <summary>An AC-only source really does sit at zero volts — the fallback still applies.</summary>
    [Fact]
    public void H11_AnAcOnlySourceIsZeroVolts()
    {
        var r = Read(".subckt part a b\nV1 a b AC 1\nR1 a b 1k\n.ends part\n");
        var inst = Cell(r, "part").Instances.Single(i => i.InstanceName == "V1");

        Assert.Equal("0", inst.Overrides.Single(o => o.Name == "DC").Expression);
        Assert.Contains("AC stimulus", Notes(r), StringComparison.Ordinal);
    }

    // ── a comma that is not an initial condition ──────────────────────────────

    /// <summary>
    /// The <c>,IC=</c> split must not reach a temperature-coefficient list, which is ONE assignment
    /// with a comma in it. Splitting every top-level comma left a bare <c>0.2</c> that the element
    /// rule then reported as an unexpected extra word.
    /// </summary>
    [Fact]
    public void H12_ACommaThatIsNotAnInitialConditionIsNotSplit()
    {
        var r = Read(".subckt part a b\nR1 a b 1k TC=0.1,0.2\n.ends part\n");
        var inst = Cell(r, "part").Instances.Single(i => i.InstanceName == "R1");

        Assert.DoesNotContain("extra word", Notes(r), StringComparison.Ordinal);
        Assert.Equal("0.1,0.2", inst.Overrides.Single(o => o.Name == "TC").Expression);
    }

    /// <summary>And the spelling it exists for still splits.</summary>
    [Fact]
    public void H13_AnInitialConditionGluedToAValueStillSplits()
    {
        var r = Read(".subckt part a b\nC1 a b 1u,IC=0\n.ends part\n");
        var inst = Cell(r, "part").Instances.Single(i => i.InstanceName == "C1");

        Assert.Equal("1E-06", inst.Overrides.Single(o => o.Name == "C").Expression);
        Assert.Contains("initial condition", Notes(r), StringComparison.Ordinal);
    }
}
