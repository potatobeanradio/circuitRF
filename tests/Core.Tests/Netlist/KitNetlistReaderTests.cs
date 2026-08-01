using System;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Reading the netlist a kit ships. The point of the whole reader: a vendor kit is read-only and
/// self-contained, so importing one must produce a working part with no file placed anywhere
/// afterwards — and the three facts a part needs (that it offers a choice of formulation, which one
/// is buildable, what circuit it is) are all sitting in this file.
///
/// <para>Nothing here names a supplier, a library or a part: these fixtures exercise the FORMAT.</para>
/// </summary>
public sealed class KitNetlistReaderTests
{
    // ── Structure ─────────────────────────────────────────────────────────────

    [Fact]
    public void ADefineBecomesACell_WithItsPortsInOrder()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a b c )
              R:R1  a b  R=50 Ohm
            end PART
            """);

        var cell = Assert.Single(r.Library.Cells);
        Assert.Equal("PART", cell.Name);
        Assert.Equal(["a", "b", "c"], cell.Ports);
    }

    [Fact]
    public void AMismatchedEnd_IsAnError_NamingTheLine()
    {
        // Every cell after this one would be attributed to the wrong define, so this is worth
        // stopping for rather than carrying on with a plausible-looking library.
        var ex = Assert.Throws<KitNetlistException>(() => KitNetlistReader.Read("""
            define PART ( a )
            end OTHER
            """));

        Assert.Equal(2, ex.Line);
        Assert.Contains("OTHER", ex.Message);
    }

    [Fact]
    public void ADefineWithNoEnd_IsAnError()
        => Assert.Throws<KitNetlistException>(() => KitNetlistReader.Read("define PART ( a )\nR:R1 a 0 R=1"));

    [Fact]
    public void AContinuedLine_IsOneLine_ReportedAtTheLineItStartedOn()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a b )
              R:R1  a b \
                    R=50 Ohm  Noise=no
              !!! not a construct
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal(["a", "b"], inst.NetBindings);
        Assert.Equal("50", inst.Overrides.Single(o => o.Name == "R").Expression);
        Assert.Equal(4, Assert.Single(r.Notes).Line);
    }

    [Theory]
    [InlineData("; a comment")]
    [InlineData("#uselib \"ckt\" , \"S15P\"")]
    public void BothCommentMarkers_AreComments(string comment)
    {
        var r = KitNetlistReader.Read($"define PART ( a )\n{comment}\nend PART");

        Assert.Empty(Assert.Single(r.Library.Cells).Instances);
        Assert.Empty(r.Notes);
    }

    // ── Instances, nets and the unit rule ─────────────────────────────────────

    [Fact]
    public void AnInstanceSplitsIntoTypeName_NetsThenParameters()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a b )
              SUB:T1  a b _n1 _n2  Gate_Fingers=26  FS="PROC1"
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal("SUB", inst.Reference);
        Assert.Equal("T1",  inst.InstanceName);
        Assert.Equal(["a", "b", "_n1", "_n2"], inst.NetBindings);
        Assert.Equal("26",     inst.Overrides.Single(o => o.Name == "Gate_Fingers").Expression);
        Assert.Equal("\"PROC1\"", inst.Overrides.Single(o => o.Name == "FS").Expression);
    }

    [Fact]
    public void ABareWordAfterAValue_IsThatValuesUnit()
    {
        // The rule that silently corrupts if it is wrong: R=1 TOhm read as R=1 is a resistor a
        // thousand billion times too small, and everything downstream still runs.
        var r = KitNetlistReader.Read("""
            define PART ( a )
              R:R1  a 0  R=1 TOhm  Noise=no
              R:R2  a 0  R=0.001 Ohm
              SUB:T1  a 0  Gate_Periphery=15.6 mm  RTH=NEW_A
            end PART
            """);

        var cell = Assert.Single(r.Library.Cells);

        var r1 = cell.Instances[0].Overrides.Single(o => o.Name == "R");
        Assert.Equal("1",    r1.Expression);
        Assert.Equal("TOhm", r1.Unit);
        // Still the point of this line: "no" is a VALUE, not a unit, so it is not eaten by the unit
        // rule. It now arrives as a string literal because "no" is one of the dialect's boolean
        // words — bare, it would reach the expression engine as a variable name.
        Assert.Equal("\"no\"", cell.Instances[0].Overrides.Single(o => o.Name == "Noise").Expression);

        Assert.Equal("Ohm", cell.Instances[1].Overrides.Single(o => o.Name == "R").Unit);

        var per = cell.Instances[2].Overrides.Single(o => o.Name == "Gate_Periphery");
        Assert.Equal("15.6", per.Expression);
        Assert.Equal("mm",   per.Unit);
        Assert.Null(cell.Instances[2].Overrides.Single(o => o.Name == "RTH").Unit);
    }

    [Fact]
    public void SimulatorOptions_ContributeNoDevice_AndSayTheyWereSkipped()
    {
        // The one Type:Name line that is not a device. Reported rather than special-cased into
        // silence, so a reader of the report sees everything the file held.
        var r = KitNetlistReader.Read("""
            define PART ( a )
              Options:Options1 TopologyCheck=yes MaxSpectralSize=512
              R:R1 a 0 R=1
            end PART
            """);

        Assert.Equal("R", Assert.Single(Assert.Single(r.Library.Cells).Instances).Reference);
        Assert.Contains(r.Notes, n => n.Message.Contains("Options"));
    }

    [Fact]
    public void SomethingNotUnderstood_IsReportedByName_AndTheRestStillReads()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
              %%% whatever this is
              R:R1 a 0 R=1
            end PART
            """);

        Assert.Single(Assert.Single(r.Library.Cells).Instances);
        var note = Assert.Single(r.Notes);
        Assert.Equal(2, note.Line);
        Assert.Contains("%%%", note.Message);
    }

    // ── Parameters and variables ──────────────────────────────────────────────

    [Fact]
    public void ParametersBecomeTheCellsInterface_AQuotedValueIsText()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
            parameters  TAMB=-1  RTH1=-1  DataPath="Sub\"
              R:R1 a 0 R=1
            end PART
            """);

        var cell = Assert.Single(r.Library.Cells);
        Assert.Equal(["TAMB", "RTH1", "DataPath"], cell.Parameters.Select(p => p.Name));
        Assert.Equal("-1",  cell.Parameters[0].DefaultExpression);
        // A backslash in a quoted value is a directory separator, not an escape: a kit writes a
        // folder as `Path="Data\"`. Normalised, so joining it to a filename produces a path rather
        // than a run-on word — and so the path works on the platform it is read on.
        Assert.Equal("\"Sub/\"", cell.Parameters[2].DefaultExpression);
    }

    [Fact]
    public void AnAssignmentBecomesACellVariable_InDeclarationOrder()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
              FIRST = 1
              SECOND = FIRST * 2
              R:R1 a 0 R=SECOND
            end PART
            """);

        var cell = Assert.Single(r.Library.Cells);
        Assert.Equal(["FIRST", "SECOND"], cell.Variables.Select(v => v.Name));
        Assert.Equal("FIRST * 2", cell.Variables[1].Expression);
    }

    // ── Conditionals ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("if(A==-1) then ((1.0e-6)*1) else (A*1) endif", -1.0, 1.0e-6)]
    [InlineData("if(A==-1) then ((1.0e-6)*1) else (A*1) endif",  4.0, 4.0)]
    [InlineData("if(A==-1) then ((1.0e-7)/2) else (A/2) endif", -1.0, 5.0e-8)]
    public void AConditional_EvaluatesToTheSameNumberThroughCircuitRfsOwnEngine(
        string source, double a, double expected)
    {
        // The rewrite is purely syntactic, so the proof is that the kit's own arithmetic survives it.
        string rewritten = KitNetlistReader.RewriteConditionals(source);

        var scope = new Scope("test");
        scope.Bind("A", a.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var value = new Evaluator().Eval(rewritten, scope);

        Assert.Equal(expected, value.AsReal(), 12);
    }

    [Fact]
    public void AnExpressionWithNoConditional_IsUntouched()
        => Assert.Equal("A * 2 + B", KitNetlistReader.RewriteConditionals("A * 2 + B"));

    [Fact]
    public void AMalformedConditional_IsLeftExactlyAsWritten()
    {
        // Better an expression that fails to evaluate with the kit's own text in the message than a
        // half-rewritten one that evaluates to something nobody wrote.
        const string broken = "if(A==-1) then ((1.0)*1)";
        Assert.Equal(broken, KitNetlistReader.RewriteConditionals(broken));
    }

    [Fact]
    public void AnElseIfChain_BecomesNestedIfs_AndEvaluatesTheSame()
    {
        // Kits write both bracketed and bare branches; both have to survive, because a branch read
        // wrongly evaluates to something nobody wrote.
        string rewritten = KitNetlistReader.RewriteConditionals(
            """if(t==0) then "m1" elseif(t==1) then "m2" else "m1m2" endif""");

        foreach (var (t, expected) in new[] { (0.0, "m1"), (1.0, "m2"), (2.0, "m1m2") })
        {
            var scope = new Scope("test");
            scope.Bind("t", t.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(expected, new Evaluator().Eval(rewritten, scope).AsString());
        }
    }

    // ── Declarations outside any subcircuit ───────────────────────────────────

    [Fact]
    public void ProcessConstantsAndFunctions_AreCarried()
    {
        // The cells reference these by bare name, so a definition read without them does not resolve.
        var r = KitNetlistReader.Read("""
            AREA(w,s,nf) = w*s*nf
            CAP_M2 = 7.498e-6
            define PART ( a )
              R:R1 a 0 R=CAP_M2
            end PART
            """);

        Assert.Equal("CAP_M2", Assert.Single(r.Variables).Name);
        var f = Assert.Single(r.Functions);
        Assert.Equal("AREA", f.Name);
        Assert.Equal(["w", "s", "nf"], f.Parameters);
    }

    [Fact]
    public void ADeclarationInsideASubcircuit_StaysWithThatCell_NotAtTopLevel()
    {
        var r = KitNetlistReader.Read("""
            GLOBAL = 1
            define PART ( a )
              LOCAL = 2
              R:R1 a 0 R=LOCAL
            end PART
            """);

        Assert.Equal("GLOBAL", Assert.Single(r.Variables).Name);
        Assert.Equal("LOCAL",  Assert.Single(Assert.Single(r.Library.Cells).Variables).Name);
    }

    // ── strcat ────────────────────────────────────────────────────────────────

    [Fact]
    public void StrcatResolvesAgainstTheCellsOwnParameterDefaults()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
            parameters  DataPath="Kit_Data/"
              SUB:T1  a 0  File=strcat(DataPath,"model.mdl")
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        // A resolved strcat produced a PATH — text, so it is emitted as a literal rather than as a
        // bare word the expression engine would read as a variable name.
        Assert.Equal("\"Kit_Data/model.mdl\"", inst.Overrides.Single(o => o.Name == "File").Expression);
    }

    [Fact]
    public void StrcatOverSomethingUnresolvable_IsLeftAsTheKitWroteIt()
    {
        // A half-built path is worse than the expression that produced it: one fails with the kit's
        // own text in the message, the other silently points somewhere that does not exist.
        var r = KitNetlistReader.Read("""
            define PART ( a )
              SUB:T1  a 0  File=strcat(Unknown,"model.mdl")
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Contains("strcat", inst.Overrides.Single(o => o.Name == "File").Expression);
    }

    // ── Operator dialect: ** is the kit's exponentiation ──────────────────────

    /// <summary>
    /// The kit spells exponentiation <c>**</c>; circuitRF spells it <c>^</c>. Both are
    /// right-associative and bind tighter than the arithmetic operators, so this is a spelling
    /// change — but an untranslated one does not merely look odd, it fails to parse, and the
    /// failure surfaces from the elaborator naming a character position in a generated file.
    /// </summary>
    [Fact]
    public void ThePowerOperator_IsTranslated_SoTheExpressionParses()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
              GLINE = (Gy*(TL_FREQ*1.0e-9)**GLE_val)
            end PART
            """);

        var v = Assert.Single(Assert.Single(r.Library.Cells).Variables);
        Assert.Equal("(Gy*(TL_FREQ*1.0e-9)^GLE_val)", v.Expression);
        Parser.Parse(v.Expression);          // the property that actually matters
    }

    [Theory]
    [InlineData("2**3", "2^3")]
    [InlineData("Drat**2+1.0", "Drat^2+1.0")]
    [InlineData("a**b**c", "a^b^c")]                     // right-assoc in both dialects
    [InlineData("(RFS0/(W*1.0e6-RFS1))**(RFS2)", "(RFS0/(W*1.0e6-RFS1))^(RFS2)")]
    [InlineData("a*b", "a*b")]                           // a single '*' is untouched
    [InlineData("a * b * c", "a * b * c")]
    [InlineData("", "")]
    public void RewritePowerOperator_TranslatesPairsOnly(string input, string expected)
        => Assert.Equal(expected, KitNetlistReader.RewritePowerOperator(input));

    [Fact]
    public void APowerOperatorInsideQuotedText_IsLeftAlone()
    {
        // The same values carry file paths and enum names. A '**' inside one is data, and
        // rewriting it would corrupt a path rather than translate an operator.
        Assert.Equal("\"a**b\"", KitNetlistReader.RewritePowerOperator("\"a**b\""));
        Assert.Equal("x^2+\"n**m\"", KitNetlistReader.RewritePowerOperator("x**2+\"n**m\""));
    }

    [Fact]
    public void ThePowerOperator_IsTranslatedInAnInstanceOverrideToo()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
              R:R1  a 0  R=Rsh**2
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal("Rsh^2", inst.Overrides.Single(o => o.Name == "R").Expression);
    }

    // ── Units: a kit writes them glued as readily as spaced ───────────────────

    /// <summary>
    /// A kit writes both spellings on ONE line (<c>CLINE=1 pF  LLINE=1pH</c>), so the two must
    /// mean the same thing. Unsplit, the glued one reaches the expression engine as <c>1pH</c> and
    /// fails to parse.
    /// </summary>
    [Fact]
    public void AGluedUnit_IsSplit_LikeASpacedOne()
    {
        var r = KitNetlistReader.Read("""
            define PART ( a )
              parameters CLINE=1 pF  LLINE=1pH
            end PART
            """);

        var ps = Assert.Single(r.Library.Cells).Parameters;

        var spaced = ps.Single(p => p.Name == "CLINE");
        Assert.Equal("1", spaced.DefaultExpression);
        Assert.Equal("pF", spaced.Unit);

        var glued = ps.Single(p => p.Name == "LLINE");
        Assert.Equal("1", glued.DefaultExpression);
        Assert.Equal("pH", glued.Unit);
        Parser.Parse(glued.DefaultExpression);
    }

    /// <summary>
    /// The guards are the reason splitting is safe: a scientific literal is not a value plus a
    /// unit, and an identifier is not a number at all.
    /// </summary>
    [Theory]
    [InlineData("1.0e-9")]
    [InlineData("Gate_Periphery")]
    [InlineData("Rsh")]
    public void AValueThatOnlyLooksGlued_IsLeftWhole(string value)
    {
        var r = KitNetlistReader.Read($$"""
            define PART ( a )
              parameters X={{value}}
            end PART
            """);

        var p = Assert.Single(Assert.Single(r.Library.Cells).Parameters);
        Assert.Equal(value, p.DefaultExpression);
        Assert.True(string.IsNullOrEmpty(p.Unit));
    }
}
