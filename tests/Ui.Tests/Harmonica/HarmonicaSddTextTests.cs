// ================================================================
//  HarmonicaSddTextTests.cs  —  brief-harmonicarf-r7b §5
//
//  §5.2  every check in §3.6, §3.7's invisible-character trap, and a full round trip that is a
//        fixed point on its second pass.
//  §5.3  THE EQUIVALENCE GATE — the old folded-coefficient I[2,0] string (pasted here as the
//        oracle, and it must not be deleted from this test when it is deleted from the product)
//        agrees with the new variable form to 1e-12 relative across the device's operating range.
//  §5.4  the generated netlist: variables as global lines (with their spaces), equations on the
//        instance line (without), and the whole thing elaborates and solves.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSddTextTests(ITestOutputHelper output)
{
    // ══ §3.6.1 — duplicate name ═══════════════════════════════════════════════

    [Fact]
    public void DuplicateName_IsReported()
    {
        var r = HarmonicaSddText.Parse("a = 1\na = 2\nI[1,0] = _v1/50", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("already declared", StringComparison.Ordinal));
    }

    // ══ §3.6.2 — syntax ═══════════════════════════════════════════════════════

    [Fact]
    public void BadSyntax_IsReportedWithTheLineNumber()
    {
        var r = HarmonicaSddText.Parse("R = 50\nI[1,0] = _v1/(", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Line == 2);
    }

    // ══ §3.6.3 — variables must be constants ═════════════════════════════════

    [Fact]
    public void VariableCycle_IsReported()
    {
        var r = HarmonicaSddText.Parse("a = b\nb = a\nI[1,0] = _v1/50", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("ycl", StringComparison.Ordinal)); // Cycle/cyclic
        output.WriteLine(string.Join("\n", r.Problems.Select(p => p.Message)));
    }

    [Fact]
    public void VariableReferencingAnUndeclaredName_IsReported()
    {
        var r = HarmonicaSddText.Parse("a = zzz\nI[1,0] = _v1/50", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("zzz", StringComparison.Ordinal));
    }

    [Fact]
    public void VariableReferencingAPortVoltage_IsReportedWithASpecificReason()
    {
        var r = HarmonicaSddText.Parse("a = _v1 + 1\nI[1,0] = _v1/50", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p =>
            p.Message.Contains("_v1", StringComparison.Ordinal) &&
            p.Message.Contains("per bias point", StringComparison.Ordinal));
    }

    [Fact]
    public void VariableResolvingToComplex_IsReported()
    {
        var r = HarmonicaSddText.Parse("a = j*1\nI[1,0] = _v1/50", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("Complex", StringComparison.Ordinal));
    }

    // ══ §3.6.4 — equation free names ══════════════════════════════════════════

    [Fact]
    public void EquationReferencingAnUndeclaredName_IsReportedByName()
    {
        var r = HarmonicaSddText.Parse("I[1,0] = _v1/Rload", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("Rload", StringComparison.Ordinal));
    }

    [Fact]
    public void EquationReferencingAnOutOfRangePortVoltage_IsReported()
    {
        var r = HarmonicaSddText.Parse("I[1,0] = _v1/50 + _v3", portCount: 2);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("_v3", StringComparison.Ordinal));
    }

    // ══ §3.6.5 — port indices ═════════════════════════════════════════════════

    [Fact]
    public void AnEquationNamingAnOutOfRangePort_IsReported()
    {
        var r = HarmonicaSddText.Parse("I[1,0] = _v1/50\nI[3,0] = _v1/50", portCount: 2);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p =>
            p.Message.Contains("I[3,0]", StringComparison.Ordinal) &&
            p.Message.Contains("2 port", StringComparison.Ordinal));
    }

    // ══ §3.6.6 — at least one current equation ═══════════════════════════════

    [Fact]
    public void NoCurrentEquationAtAll_IsReported()
    {
        var r = HarmonicaSddText.Parse("Q[1] = 1e-12*_v1", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("current equation", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSingleIndexSugar_AlsoCountsAsACurrentEquation()
    {
        var r = HarmonicaSddText.Parse("I[1] = _v1/50", portCount: 1);
        Assert.True(r.IsValid, string.Join("; ", r.Problems.Select(p => p.Message)));
    }

    // ══ §3.6.7 — reserved names ═══════════════════════════════════════════════

    [Theory]
    [InlineData("_v1")]
    [InlineData("_c1")]
    [InlineData("freq")]
    public void AReservedName_CannotBeUsedAsAVariable(string name)
    {
        var r = HarmonicaSddText.Parse($"{name} = 1\nI[1,0] = _v1/50", portCount: 1);
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Message.Contains("reserved", StringComparison.Ordinal));
    }

    // ══ the well-formed case ══════════════════════════════════════════════════

    [Fact]
    public void WellFormedText_HasNoProblems_AndPartitionsCorrectly()
    {
        var r = HarmonicaSddText.Parse("R = 50\n\n; a comment\nI[1,0] = _v1/R", portCount: 1);
        Assert.True(r.IsValid, string.Join("; ", r.Problems.Select(p => p.Message)));
        Assert.Single(r.Variables);
        Assert.Single(r.Equations);
        Assert.Equal("R", r.Variables[0].Name);
        Assert.Equal("I[1,0]", r.Equations[0].Name);
    }

    // ══ §3.7 — the invisible-character trap ══════════════════════════════════

    [Fact]
    public void AnInvisibleLeftToRightMark_IsStrippedBeforeAnythingElseLooksAtTheText()
    {
        // U+200E glued onto the identifier, exactly as the owner's own default text carried it.
        var r = HarmonicaSddText.Parse("Periphery_mm‎ = 1.0\nI[1,0] = _v1/50", portCount: 1);
        Assert.True(r.IsValid, string.Join("; ", r.Problems.Select(p => p.Message)));
        Assert.Contains(r.Variables, v => v.Name == "Periphery_mm");
    }

    // ══ round trip — VarTextParser's own new overload ════════════════════════

    [Fact]
    public void SerializeLines_PreservesBlankAndCommentLines_AndIsAFixedPointOnTheSecondPass()
    {
        const string text = "; leading comment\nR = 50\n\n# another comment\nI[1,0] = _v1/R";
        var lines1 = CircuitRF.Ui.Schematic.VarTextParser.ParseLines(text);
        string out1 = CircuitRF.Ui.Schematic.VarTextParser.SerializeLines(lines1);

        var lines2 = CircuitRF.Ui.Schematic.VarTextParser.ParseLines(out1);
        string out2 = CircuitRF.Ui.Schematic.VarTextParser.SerializeLines(lines2);

        Assert.Contains("; leading comment", out1);
        Assert.Contains("# another comment", out1);
        Assert.Equal(out1, out2);   // fixed point on the second pass
    }

    // ══ round trip — reconstruction is idempotent, and CharmIo never invents SddText ═══

    [Fact]
    public void ReconstructedText_IsAFixedPointUnderParseAndToParameters()
    {
        var originalParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["R"] = "50",
            ["I[1,0]"] = "_v1/R",
        };

        string text1 = SddTextIo.Reconstruct(originalParams);
        var parsed1 = HarmonicaSddText.Parse(text1, portCount: 1);
        Assert.True(parsed1.IsValid, string.Join("; ", parsed1.Problems.Select(p => p.Message)));

        string text2 = SddTextIo.Reconstruct(HarmonicaSddText.ToParameters(parsed1));
        Assert.Equal(text1, text2);
    }

    [Fact]
    public void AnUntouchedPreR7BCharm_ReSerialisesByteForByte_AndTheDialogReconstructsOnOpen()
    {
        var dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["R"] = "50", ["I[1,0]"] = "_v1/R",
            },
            // SddText deliberately left null — a pre-R7B document.
        };
        var model = HarmonicaViewModel.DefaultModel() with { Dut = dut };

        string json = CharmIo.Write(model, new TerminationSet(model.Settings.HarmonicCount));
        Assert.DoesNotContain("SddText", json, StringComparison.Ordinal);

        var reloaded = CharmIo.ReadAll(json, baseDirectory: null);
        Assert.Null(reloaded.Model.Dut.SddText);

        string json2 = CharmIo.Write(reloaded.Model, reloaded.Terminations);
        Assert.Equal(json, json2);

        var editor = new HarmonicaDutEditor(reloaded.Model.Dut);
        Assert.NotNull(editor.SddText);
        Assert.Contains("R = 50", editor.SddText);
        Assert.Contains("I[1,0] = _v1/R", editor.SddText);
    }

    // ══ §5.3 — THE EQUIVALENCE GATE ═══════════════════════════════════════════

    [Fact]
    public void DefaultModelEquation_AgreesWithTheOldFoldedCoefficientForm_AcrossTheOperatingRange()
    {
        // The oracle: Hero 2's device exactly as HarmonicaViewModel.DefaultModel() spelled it before
        // R7B (git history, and the brief's own §3.8). Must not be deleted from this test when it is
        // deleted from the product.
        const string oldExpr =
            "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
            "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";

        var oldAst = Parser.Parse(oldExpr);

        var dutParams = HarmonicaViewModel.DefaultModel().Dut.Parameters;
        var newAst = Parser.Parse(dutParams["I[2,0]"]);
        var vars = dutParams
            .Where(p => !ComponentModelFactory.IsSddEquationName(p.Key))
            .ToDictionary(p => p.Key, p => double.Parse(p.Value, CultureInfo.InvariantCulture));

        int checkedPoints = 0;
        double maxRel = 0;
        for (double v1 = -6; v1 <= 0.0001; v1 += 0.25)
        {
            for (double v2 = 0; v2 <= 60.0001; v2 += 2.5)
            {
                double oldVal = SddEvaluator.EvalDouble(oldAst, new Dictionary<string, double>(), [v1, v2]);
                double newVal = SddEvaluator.EvalDouble(newAst, vars, [v1, v2]);
                double rel = oldVal == 0 ? Math.Abs(newVal) : Math.Abs((newVal - oldVal) / oldVal);
                maxRel = Math.Max(maxRel, rel);
                Assert.True(rel < 1e-12, $"v1={v1} v2={v2}: old={oldVal} new={newVal} rel={rel}");
                checkedPoints++;
            }
        }
        output.WriteLine($"{checkedPoints} grid points, max relative error {maxRel:E3}");
    }

    // ══ §5.4 — the generated netlist ══════════════════════════════════════════

    [Fact]
    public void DefaultModelNetlist_VariablesAreGlobalLines_EquationsAreWhitespaceFree_AndElaboratesAndSolves()
    {
        var model = HarmonicaViewModel.DefaultModel();
        string text = HarmonicaNetlist.Build(model).Text;
        var lines = text.Split('\n');

        int dutIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("SDD:", StringComparison.Ordinal));
        Assert.True(dutIdx > 0, "no SDD: line found in the generated netlist");

        // A variable, WITH its authored spacing, is a global line before the DUT line.
        Assert.Contains(lines.Take(dutIdx), l => l.Trim() == "B = 1130");

        // The equation on the instance line has had its authored spaces stripped.
        string dutLine = lines[dutIdx];
        Assert.DoesNotContain("TV0 - _v1", dutLine, StringComparison.Ordinal);
        Assert.Contains("TV0-_v1", dutLine, StringComparison.Ordinal);

        var ctx = HarmonicaContext.Create(model);
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(50, 0));
        terms.Set(TerminationSide.Load, 1, new Complex(50, 0));

        var point = ctx.Solve(terms, pavlDbm: -10);
        Assert.True(point.Converged, "the default SDD device (variable form) failed to converge");
    }
}
