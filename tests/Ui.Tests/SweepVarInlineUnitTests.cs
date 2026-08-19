using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A VAR row whose unit is written INLINE in the expression — "RFfreq = 2 GHz", which is exactly
/// how a <c>.cnl</c> spells it and how the schematic renders the row back — must mean the same
/// thing it means in a netlist.
///
/// <para>It did not. The expression grammar has no unit-suffix production, so
/// <c>Parser.Parse("2 GHz")</c> is a parse error at the 'GHz'; <c>Elaborator</c> skips a global it
/// cannot resolve and does so silently, so the variable simply was not there. Nothing reported it:
/// downstream, a frequency field referencing it fell back to its own default (the loadpull engines
/// use 1 GHz) and the sweep row inherited no unit to scale its range by.</para>
/// </summary>
public class SweepVarInlineUnitTests
{
    private static EditableComponent Var(params (string Name, string Expr, string Unit)[] rows)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Var, InstanceName = "VAR1" };
        foreach (var (n, e, u) in rows)
            c.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = u });
        return c;
    }

    private static Variable Extract(string expr, string unitColumn = "")
    {
        var model = new SchematicEditModel();
        model.Components.Add(Var(("RFfreq", expr, unitColumn)));
        return NetExtractor.Extract(model).TestBench.GlobalVariables.Single();
    }

    [Fact]
    public void AnInlineUnit_BecomesTheVariablesUnit_AndTheVariableResolves()
    {
        var v = Extract("2 GHz");

        Assert.Equal("2",   v.Expression);
        Assert.Equal("GHz", v.Unit);

        // The whole point: it now has a value at all, and that value is 2 GHz in Hz.
        var tb = new TestBench("tb");
        tb.GlobalVariables.Add(v);
        var nl = new Elaborator().Elaborate(tb);
        Assert.Equal(2e9, nl.ResolvedGlobals["RFfreq"].AsReal(), 3);
        Assert.Contains("RFfreq", nl.GlobalsWithExplicitUnit);
    }

    /// <summary>An identity unit carries no multiplier, but lifting it is still what keeps the
    /// variable alive — "48 V" used to vanish exactly as "2 GHz" did.</summary>
    [Fact]
    public void AnInlineIdentityUnit_IsLiftedToo()
    {
        var v = Extract("48 V");
        Assert.Equal("48", v.Expression);
        Assert.Equal("V",  v.Unit);
    }

    /// <summary>An editor glyph is normalized to the engine spelling on the way in — the schematic
    /// writes "Ω" and "µ", the <c>Units</c> table is ASCII-keyed.</summary>
    [Theory]
    [InlineData("50 \u03A9", "50", "Ohm")]
    [InlineData("2.5 \u00B5H", "2.5", "uH")]
    public void AnInlineGlyphUnit_IsNormalized(string expr, string expectExpr, string expectUnit)
    {
        var v = Extract(expr);
        Assert.Equal(expectExpr, v.Expression);
        Assert.Equal(expectUnit, v.Unit);
    }

    /// <summary>The row's own unit column still wins — it is the canonical place to put one.</summary>
    [Fact]
    public void TheUnitColumnWins_WhenBothAreSet()
    {
        var v = Extract("2 GHz", unitColumn: "MHz");
        Assert.Equal("2 GHz", v.Expression);   // untouched; the column already said what to do
        Assert.Equal("MHz",   v.Unit);
    }

    /// <summary>
    /// The guard that makes the lift safe. Every bare SI prefix is a unit name, so a token-only rule
    /// would tear "2 * f" into "2 *" + femto and "R * m" into "R *" + milli. An expression that
    /// already parses is never touched — which is every expression that was working before.
    /// </summary>
    [Theory]
    [InlineData("2 * f")]
    [InlineData("R * m")]
    [InlineData("a + b")]
    [InlineData("50")]
    [InlineData("if(x > 1, n, p)")]
    public void AnExpressionThatAlreadyParses_IsLeftAlone(string expr)
    {
        var v = Extract(expr);
        Assert.Equal(expr, v.Expression);
        Assert.Null(v.Unit);
    }

    /// <summary>A trailing token that is not a unit leaves the row exactly as authored, so the
    /// existing error surfaces where it always did rather than being disguised.</summary>
    [Fact]
    public void ATrailingNonUnit_IsNotLifted()
    {
        var v = Extract("2 gigahertz");
        Assert.Equal("2 gigahertz", v.Expression);
        Assert.Null(v.Unit);
    }

    /// <summary>
    /// The sweep editor inherits the same unit the run does, wherever it was written. A row that
    /// showed a blank inherited unit while the run inherited GHz would be the worse half of the bug:
    /// the preview would say "3 pts: 2 … 3" for a sweep that runs at 2 … 3 GHz.
    /// </summary>
    [Theory]
    [InlineData("2 GHz", "")]     // written inline
    [InlineData("2", "GHz")]      // written in the unit column
    public void TheSweepRow_InheritsTheUnit_WhereverItWasWritten(string expr, string unitColumn)
    {
        var model = new SchematicEditModel();
        model.Components.Add(Var(("RFfreq", expr, unitColumn)));

        var row = new ViewModels.SweepAxisRowViewModel(model)
        {
            VarName = "RFfreq", StartExpr = "2", StopExpr = "3", StepOrCountExpr = "0.5",
        };

        Assert.Equal("GHz", row.EffectiveUnit);
        Assert.Equal([2e9, 2.5e9, 3e9], row.BuildValues()!);
    }
}
