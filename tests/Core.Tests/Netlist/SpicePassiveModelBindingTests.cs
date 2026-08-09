using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Binding a <c>.model</c> card of a passive type onto the circuitRF primitive that implements it,
/// and the two case-alignment rules that stand between a read netlist and one that elaborates.
///
/// <para><b>Every fixture here is synthetic.</b> Same rule as the reader's own tests: this is format
/// work, and the repository commits no third-party kit data.</para>
/// </summary>
public sealed class SpicePassiveModelBindingTests
{
    private static SpiceNetlistResult Read(string text) => SpiceNetlistReader.Read(text);

    private static Cell Cell(SpiceNetlistResult r, string name)
        => Assert.Single(r.Library.Cells, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static Instance Inst(Cell c, string name)
        => Assert.Single(c.Instances, i => i.InstanceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Over(Instance i, string name)
        => i.Overrides.FirstOrDefault(o => o.Name.Equals(name, StringComparison.Ordinal))?.Expression;

    // ── the capacitor card ────────────────────────────────────────────────────

    [Fact]
    public void B1_ACapacitorCardBecomesSemiCWithTheInstancesOwnGeometry()
    {
        var r = Read("""
            .model plate C (CJ=1.5f CJSW=40e-18 TC1=3.6u TC2=2n TNOM=27)
            .subckt part a b
            .param w=7 l=7
            C1 a b plate w=w l=l
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");

        Assert.Equal("SemiC", c1.Reference);
        Assert.Equal("1.5E-15", Over(c1, "Cj"));
        Assert.Equal("4E-17",   Over(c1, "Cjsw"));
        Assert.Equal("w",       Over(c1, "W"));
        Assert.Equal("l",       Over(c1, "L"));
        Assert.Equal("3.6E-06", Over(c1, "TC1"));
        Assert.Equal("2E-09",   Over(c1, "TC2"));
        Assert.Equal("27",      Over(c1, "Tnom"));

        // The names are circuitRF's, spelled the way circuitRF compares them — which is ordinally.
        Assert.All(c1.Overrides, o => Assert.DoesNotContain(o.Name, new[] { "cj", "CJ", "w", "l" }));

        Assert.Empty(r.IncompleteCells);
    }

    [Fact]
    public void B2_NarrowIsSubtractedFromBothDrawnDimensions()
    {
        var r = Read("""
            .model plate C (CJ=1f NARROW=0.2)
            .subckt part a b
            C1 a b plate w=10 l=20
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Equal("((10) - (0.2))", Over(c1, "W"));
        Assert.Equal("((20) - (0.2))", Over(c1, "L"));
    }

    [Fact]
    public void B3_ZeroNarrowLeavesTheDimensionExactlyAsWritten()
    {
        var r = Read("""
            .model plate C (CJ=1f NARROW=0)
            .subckt part a b
            C1 a b plate w=10 l=20
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Equal("10", Over(c1, "W"));
        Assert.Equal("20", Over(c1, "L"));
    }

    [Fact]
    public void B4_ScaleMultipliesTheCoefficients_NotTheGeometry()
    {
        // scale is documented to multiply the CAPACITANCE. Folding it into the coefficients says
        // that; folding it into W and L would square it, and at the scale=1 every real card uses the
        // two are indistinguishable — which is exactly why it is worth pinning.
        var r = Read("""
            .model plate C (CJ=2f CJSW=1e-18)
            .subckt part a b
            C1 a b plate w=10 l=20 scale=3
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Equal("((2E-15) * (3))", Over(c1, "Cj"));
        Assert.Equal("((1E-18) * (3))", Over(c1, "Cjsw"));
        Assert.Equal("10", Over(c1, "W"));
        Assert.Equal("20", Over(c1, "L"));
    }

    [Fact]
    public void B5_TheInstancesOwnCoefficientsOutrankTheCards()
    {
        var r = Read("""
            .model plate C (CJ=1f TC1=5u TC2=1n)
            .subckt part a b
            C1 a b plate w=1 l=1 tc1=9u
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Equal("9E-06", Over(c1, "TC1"));   // the instance's
        Assert.Equal("1E-09", Over(c1, "TC2"));   // the card's, untouched
    }

    [Fact]
    public void B6_ACoefficientWithNoGeometryIsReported_NotAppliedToNothing()
    {
        // The alternative is a capacitance of zero, which simulates perfectly and is not the part.
        var r = Read("""
            .model plate C (CJ=1f)
            .subckt part a b
            C1 a b plate w=10
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Equal("plate", c1.Reference);                    // left exactly as it was
        Assert.Contains("part", r.IncompleteCells);
        Assert.Contains(r.Notes, n => n.Message.Contains("length"));
    }

    [Fact]
    public void B7_AFixedCapacitanceOnTheCardNeedsNoGeometryAtAll()
    {
        var r = Read("""
            .model plate C (C=1p)
            .subckt part a b
            C1 a b plate
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Equal("SemiC", c1.Reference);
        Assert.Equal("1E-12", Over(c1, "C"));
        Assert.Null(Over(c1, "W"));
        Assert.Empty(r.IncompleteCells);
    }

    [Fact]
    public void B8_AnInitialConditionIsDroppedAndSaidSo()
    {
        var r = Read("""
            .model plate C (CJ=1f)
            .subckt part a b
            C1 a b plate w=1 l=1 ic=2.5
            .ends
            """);

        var c1 = Inst(Cell(r, "part"), "C1");
        Assert.Null(Over(c1, "ic"));
        Assert.Contains(r.Notes, n => n.Message.Contains("initial condition"));
    }

    // ── the resistor card ─────────────────────────────────────────────────────

    [Fact]
    public void B9_AResistorCardBecomesTheSheetResistanceRatio()
    {
        var r = Read("""
            .model sheet R (RSH=7 TC1=1m TNOM=27)
            .subckt part a b
            R1 a b sheet w=2 l=10
            .ends
            """);

        var r1 = Inst(Cell(r, "part"), "R1");
        Assert.Equal("R", r1.Reference);
        Assert.Equal("(7) * (10) / (2)", Over(r1, "R"));
        Assert.Equal("0.001", Over(r1, "TC1"));
        Assert.Equal("27",    Over(r1, "Tnom"));
    }

    [Fact]
    public void B10_AResistorCardWithNoSheetResistanceIsReported_NeverGivenAValue()
    {
        var r = Read("""
            .model sheet R (TC1=1m)
            .subckt part a b
            R1 a b sheet w=2 l=10
            .ends
            """);

        Assert.Equal("sheet", Inst(Cell(r, "part"), "R1").Reference);
        Assert.Contains("part", r.IncompleteCells);
        Assert.Contains(r.Notes, n => n.Message.Contains("sheet resistance"));
    }

    // ── what this pass must NOT touch ─────────────────────────────────────────

    [Fact]
    public void B11_ASemiconductorCardIsLeftEntirelyAlone()
    {
        // It is the parameter block of a device something ELSE supplies, and that something has to
        // still see it. Marking it incomplete would report the working case as broken.
        var r = Read("""
            .model nch nmos (level=54 vth0=0.4)
            .subckt part d g s b
            M1 d g s b nch w=1u l=100n
            .ends
            """);

        var m1 = Inst(Cell(r, "part"), "M1");
        Assert.Equal("nch", m1.Reference);
        Assert.Empty(r.IncompleteCells);
        Assert.Single(r.ModelCards);
    }

    [Fact]
    public void B12_ALetterThatDisagreesWithTheCardsTypeIsReported_NotReadAsSomethingElse()
    {
        var r = Read("""
            .model sheet R (RSH=7)
            .subckt part a b
            C1 a b sheet w=2 l=10
            .ends
            """);

        Assert.Equal("sheet", Inst(Cell(r, "part"), "C1").Reference);
        Assert.Contains("part", r.IncompleteCells);
        Assert.Contains(r.Notes, n => n.Message.Contains("'R' card"));
    }

    // ── the two case-alignment rules ──────────────────────────────────────────

    [Fact]
    public void B13_APassivesValueSpelledLowerCaseStillReachesCircuitRf()
    {
        // This dialect is case-insensitive; circuitRF compares parameter names ordinally. Passing
        // the spelling through verbatim gives a resistor with no value at all — measured on a real
        // kit, whose MIM capacitor's series resistance is written 'r=' and whose every part built on
        // it therefore failed to elaborate.
        var r = Read("""
            .subckt part a b c d
            R1 a b r=55m
            C2 b c c=1p tc1=3u
            L3 c d l=2n
            .ends
            """);

        var cell = Cell(r, "part");
        Assert.Equal("0.055", Over(Inst(cell, "R1"), "R"));
        Assert.Equal("1E-12", Over(Inst(cell, "C2"), "C"));
        Assert.Equal("3E-06", Over(Inst(cell, "C2"), "TC1"));
        Assert.Equal("2E-09", Over(Inst(cell, "L3"), "L"));
    }

    [Fact]
    public void B14_ASubcircuitCallIsAlignedToTheSpellingItsDefinitionDeclared()
    {
        var r = Read("""
            .subckt leaf a b
            .param w=1
            R1 a b w
            .ends
            .subckt part a b
            X1 a b leaf W=5 Bogus=2
            .ends
            """);

        var x1 = Inst(Cell(r, "part"), "X1");

        // Matched case-insensitively, rewritten to the declaration's own spelling…
        Assert.Equal("5", Over(x1, "w"));
        Assert.Null(Over(x1, "W"));

        // …and a name the definition does not declare is left EXACTLY as written, so a genuine typo
        // is still refused by the elaborator rather than quietly absorbed here.
        Assert.Equal("2", Over(x1, "Bogus"));
    }

    [Fact]
    public void B15_ADefinitionReadAfterTheCallThatUsesItStillAligns()
    {
        // The whole reason both of these are a separate pass: read order must not decide.
        var r = Read("""
            .subckt part a b
            X1 a b leaf W=5
            .ends
            .subckt leaf a b
            .param w=1
            R1 a b w
            .ends
            """);

        Assert.Equal("5", Over(Inst(Cell(r, "part"), "X1"), "w"));
    }

    // ── the subcircuit-local .param rule ──────────────────────────────────────

    [Fact]
    public void B16_ASubcircuitLocalParamIsOverridable_NotSealedShut()
    {
        var r = Read("""
            .subckt part a b
            .param w=7u l=7u
            C1 a b c={w*l}
            .ends
            """);

        var cell = Cell(r, "part");
        Assert.Empty(cell.Variables);
        Assert.Equal(["w", "l"], cell.Parameters.Select(p => p.Name));
        Assert.Equal("7E-06", cell.Parameters[0].DefaultExpression);
    }

    [Fact]
    public void B17_TheSubcktLinesOwnDeclarationWins_AndTheContradictionIsSaidOutLoud()
    {
        var r = Read("""
            .subckt part a b w=1
            .param w=2
            R1 a b w
            .ends
            """);

        var cell = Cell(r, "part");
        Assert.Equal("1", Assert.Single(cell.Parameters).DefaultExpression);
        Assert.Contains(r.Notes, n => n.Message.Contains("already declared"));
    }
}
