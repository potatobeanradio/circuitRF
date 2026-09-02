using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// M2–M4 — what circuitRF builds from a source line, and what a file's <c>.func</c> definitions
/// become.
///
/// <para><b>Every fixture is synthetic.</b> The repository commits no third-party kit data, so
/// nothing here names a supplier, a product or a part.</para>
/// </summary>
public class SpiceSourceImportTests
{
    private static SubcircuitTranslation Only(string text)
    {
        var all = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        return all.Single(t => t.Elements.Count > 0);
    }

    private static SubcircuitElement E(SubcircuitTranslation t, string name)
        => t.Elements.Single(e => e.InstanceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string P(SubcircuitElement e, string name)
        => e.Parameters.Single(p => p.Name.Equals(name, StringComparison.Ordinal)).Expression;

    // ── independent sources ───────────────────────────────────────────────────

    /// <summary>
    /// The zero-volt sensor idiom is a current PROBE, which is what a behavioural source can name.
    /// A zero-volt supply would be electrically identical and would read as a mistake.
    /// </summary>
    [Fact]
    public void S1_AZeroVoltSourceBecomesAnIProbe()
    {
        var e = E(Only("""
            .subckt part a b
            V_sense a b 0
            R1 a b 1k
            .ends part
            """), "V_sense");

        Assert.Equal(SymbolKind.IProbe, e.Symbol);
        Assert.Empty(e.Parameters);
        Assert.Contains(e.Notes, n => n.Contains("IProbe", StringComparison.Ordinal));
    }

    [Fact]
    public void S2_ADcSourceBecomesAVdcAtItsValue()
    {
        var e = E(Only("""
            .subckt part a b
            V1 a b DC 5
            R1 a b 1k
            .ends part
            """), "V1");

        Assert.Equal(SymbolKind.Vdc, e.Symbol);
        Assert.Equal("5", P(e, "Vdc"));
    }

    /// <summary>
    /// circuitRF has no DC-only current source. What it places instead is stated as a note rather
    /// than left for someone to discover from the schematic.
    /// </summary>
    [Fact]
    public void S3_ADcCurrentSourceBecomesAToneSourceCarryingOnlyItsOffset()
    {
        var e = E(Only("""
            .subckt part a b
            I1 a b 2m
            R1 a b 1k
            .ends part
            """), "I1");

        Assert.Equal(SymbolKind.CurrentToneSource, e.Symbol);
        Assert.Equal("0.002", P(e, "Idc"));
        Assert.Equal("0", P(e, "I"));
        Assert.Contains(e.Notes, n => n.Contains("no DC-only current source", StringComparison.Ordinal));
    }

    // ── the ideal controlled sources ──────────────────────────────────────────

    /// <summary>
    /// One sensed quantity and one coefficient is an IDEAL controlled source, which circuitRF has as
    /// a linear element. Building an equation-defined device for it instead would make a linear
    /// macromodel nonlinear — and an S-parameter run on it would start needing an operating point it
    /// has no reason to need.
    /// </summary>
    [Theory]
    [InlineData("E1 out 0 in 0 2.5",              SymbolKind.Vcvs, "E")]
    [InlineData("E1 out 0 VALUE={2.5*V(in,0)}",   SymbolKind.Vcvs, "E")]
    [InlineData("G1 out 0 in 0 2.5",              SymbolKind.Vccs, "G")]
    [InlineData("G1 out 0 VALUE={2.5*V(in,0)}",   SymbolKind.Vccs, "G")]
    public void S4_AnIdealControlledSourceIsALinearElement(string line, SymbolKind kind, string gain)
    {
        var e = E(Only($"""
            .subckt part out in
            {line}
            R1 in 0 1k
            .ends part
            """), "E1".Equals(line[..2], StringComparison.OrdinalIgnoreCase) ? "E1" : "G1");

        Assert.Equal(kind, e.Symbol);
        Assert.Equal(["out", "0", "in", "0"], e.Nets);
        Assert.Equal(2.5, Eval(P(e, gain)), 12);
    }

    private static double Eval(string expr)
        => new CircuitRF.Core.Expressions.Evaluator()
               .Eval(expr, new CircuitRF.Core.Expressions.Scope("t")).AsReal();

    // ── the equation-defined device ───────────────────────────────────────────

    /// <summary>
    /// A behavioural CURRENT source states the current it delivers, which is the equation-defined
    /// device's own <c>I[1,0]</c>. The node pair it senses becomes a second port that draws nothing.
    /// </summary>
    [Fact]
    public void S5_ABehaviouralCurrentSourceBecomesACurrentEquation()
    {
        var e = E(Only("""
            .subckt part out a b
            G1 out 0 VALUE={tanh(V(a,b))}
            R1 a b 1k
            .ends part
            """), "G1");

        Assert.Equal(SymbolKind.Sdd, e.Symbol);
        Assert.Equal("2", P(e, "NumPorts"));
        Assert.Equal(["out", "0", "a", "b"], e.Nets);
        Assert.Equal("tanh(_v2)", P(e, "I[1,0]"));
    }

    /// <summary>
    /// A behavioural VOLTAGE source HOLDS its own pair, which is a branch equation — and the whole
    /// reason the milestone exists, since 123 of the 234 controlled-source lines measured are this.
    /// </summary>
    [Fact]
    public void S6_ABehaviouralVoltageSourceBecomesABranchEquation()
    {
        var e = E(Only("""
            .subckt part out a b
            E1 out 0 VALUE={tanh(V(a,b))}
            R1 a b 1k
            .ends part
            """), "E1");

        Assert.Equal(SymbolKind.Sdd, e.Symbol);
        Assert.Equal("tanh(_v2)", P(e, "V[1]"));
        Assert.DoesNotContain(e.Parameters, p => p.Name.StartsWith("I[", StringComparison.Ordinal));
    }

    /// <summary>A sensed branch current becomes a control-current reference to the probe it names.</summary>
    [Fact]
    public void S7_ASensedBranchCurrentBecomesAControlReference()
    {
        var e = E(Only("""
            .subckt part out a b
            V_sense a b 0
            E1 out 0 VALUE={-I(V_sense)}
            .ends part
            """), "E1");

        Assert.Equal("V_sense", P(e, "C[1]"));
        Assert.Equal("(-_c1)", P(e, "V[1]"));
        Assert.Equal("1", P(e, "NumPorts"));
    }

    // ── .func ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A function is substituted at its call site, so the written cell is SELF-CONTAINED: there is
    /// nowhere in a cell folder for a function definition to live, and a design that happened to
    /// declare a function of the same name would otherwise decide what an imported part computes.
    /// </summary>
    [Fact]
    public void S8_ANestedChainOfFunctionsIsInlinedAndEvaluates()
    {
        var e = E(Only("""
            .func f1(x) {2*x}
            .func f2(x) {f1(x)+1}
            .func f3(x) {f2(x)*f2(x)}
            .subckt part out a b
            G1 out 0 VALUE={f3(V(a,b))}
            R1 a b 1k
            .ends part
            """), "G1");

        // f3(v) = (2v+1)²; at v = 3 that is 49.
        var scope = new CircuitRF.Core.Expressions.Scope("t");
        scope.Bind("_v2", "3");
        Assert.Equal(49.0, new CircuitRF.Core.Expressions.Evaluator()
                              .Eval(P(e, "I[1,0]"), scope).AsReal(), 12);
        Assert.DoesNotContain("f1", P(e, "I[1,0]"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Two files declaring the same function name do not collide, and cannot.</b> The name never
    /// enters a namespace at all — each definition is substituted into its own file's equations. A
    /// scheme that hoisted them into the design's one flat function table would have first-one-wins
    /// behaviour with nothing said anywhere.
    /// </summary>
    [Fact]
    public void S9_TwoFilesDeclaringTheSameFunctionNameDoNotCollide()
    {
        string A = Body("""
            .func k(x) {2*x}
            .subckt partA out a b
            G1 out 0 VALUE={k(V(a,b))}
            R1 a b 1k
            .ends partA
            """, "G1", "I[1,0]");

        string B = Body("""
            .func k(x) {100*x}
            .subckt partB out a b
            G1 out 0 VALUE={k(V(a,b))}
            R1 a b 1k
            .ends partB
            """, "G1", "I[1,0]");

        var scope = new CircuitRF.Core.Expressions.Scope("t");
        scope.Bind("_v2", "1");
        var ev = new CircuitRF.Core.Expressions.Evaluator();
        Assert.Equal(2.0,   ev.Eval(A, scope).AsReal(), 12);
        Assert.Equal(100.0, ev.Eval(B, scope).AsReal(), 12);

        static string Body(string text, string inst, string param)
            => Only(text).Elements.Single(e => e.InstanceName == inst)
                   .Parameters.Single(p => p.Name == param).Expression;
    }

    /// <summary>
    /// A file's global <c>.param</c>s become the CELL's own variables, so two imported parts each
    /// keep their own. They used to be read correctly and then discarded by both consumers.
    /// </summary>
    [Fact]
    public void S10_AFilesGlobalParametersTravelWithTheCellThatUsesThem()
    {
        var t = Only("""
            .param Rd = 47
            .subckt part a b
            R1 a b {Rd*2}
            .ends part
            """);

        var v = Assert.Single(t.Definition.Variables, x => x.Name == "Rd");
        Assert.Equal("47", v.Expression);
    }

    /// <summary>A global must never bind over a parameter the definition declares — that seals it shut.</summary>
    [Fact]
    public void S11_AGlobalDoesNotShadowTheDefinitionsOwnParameter()
    {
        var t = Only("""
            .param Rd = 47
            .subckt part a b  Rd=10
            R1 a b {Rd}
            .ends part
            """);

        Assert.DoesNotContain(t.Definition.Variables, x => x.Name == "Rd");
        Assert.Equal("10", t.Definition.Parameters.Single(p => p.Name == "Rd").DefaultExpression);
    }

    // ── charge, written directly ──────────────────────────────────────────────

    /// <summary>
    /// <c>ddt</c> is a charge MARKER, not a function to evaluate — there is no time axis to
    /// differentiate along. The expression inside it is the charge.
    /// </summary>
    [Fact]
    public void S12_ADdtInACurrentSourceIsAChargeEquation()
    {
        var e = E(Only("""
            .subckt part out a b
            G1 out 0 VALUE={ddt(1e-12*V(a,b))}
            R1 a b 1k
            .ends part
            """), "G1");

        Assert.Equal("I[1,1]", e.Parameters.Single(p => p.Name.StartsWith("I[", StringComparison.Ordinal)).Name);
        Assert.Equal("(1E-12*_v2)", P(e, "I[1,1]"));
    }

    /// <summary>
    /// A capacitor may state its stored CHARGE instead of its capacitance. That is the spelling with
    /// no conversion to get wrong: a capacitance is the derivative of the charge, so the charge is
    /// its integral and NOT <c>C(v)·v</c>.
    /// </summary>
    [Fact]
    public void S13_ACapacitorStatingAChargeBecomesAChargeEquation()
    {
        var e = E(Only("""
            .subckt part a b
            C1 a b Q={1e-12*V(a,b)+2e-13*V(a,b)^3}
            R1 a b 1k
            .ends part
            """), "C1");

        Assert.Equal(SymbolKind.Sdd, e.Symbol);
        Assert.Equal("1", P(e, "NumPorts"));
        Assert.Equal(["a", "b"], e.Nets);
        Assert.Contains("_v1", P(e, "I[1,1]"), StringComparison.Ordinal);
    }

    // ── the definition still refuses as a whole where it must ─────────────────

    /// <summary>
    /// <b>With the file's own functions substituted, any call left is one circuitRF does not have —
    /// and this is the last moment it can be said usefully.</b> Left alone it parses, elaborates,
    /// and throws "unknown function" from inside the solver at simulate time, in a message naming
    /// neither the file nor the line nor the element.
    /// </summary>
    [Fact]
    public void S15_ACallToAFunctionCircuitRfDoesNotHaveIsRefusedByName()
    {
        var t = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read("""
            .subckt part out a b
            G1 out 0 VALUE={mystery(V(a,b))}
            R1 a b 1k
            .ends part
            """)).Single();

        Assert.False(t.IsSupported);
        Assert.Contains("mystery", t.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A table inside an expression is the same table, and is refused as one.</summary>
    [Fact]
    public void S16_ATableInsideAnExpressionIsRefusedAsATable()
    {
        var t = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read("""
            .subckt part out a b
            G1 out 0 VALUE={TABLE(V(a,b), 0,0, 1,1)}
            R1 a b 1k
            .ends part
            """)).Single();

        Assert.False(t.IsSupported);
        Assert.Contains("piecewise-linear table", t.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A function the file DOES define is inlined, so it never reaches that refusal.</summary>
    [Fact]
    public void S17_AFunctionTheFileDefinesIsNotMistakenForAMissingOne()
    {
        var t = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read("""
            .func mystery(x) {x*3}
            .subckt part out a b
            G1 out 0 VALUE={mystery(V(a,b))}
            R1 a b 1k
            .ends part
            """)).Single();

        Assert.True(t.IsSupported, t.Refusal);
    }

    // ── the drawing, read back the way a run reads it ─────────────────────────

    /// <summary>
    /// <b>The oracle is extraction, not the drawing.</b> An equation-defined device is VARIADIC — it
    /// reads its own port count off a parameter — so a behavioural source whose expression senses a
    /// second node pair is drawn with six pins, not four. Asking for the pins before the parameters
    /// are on the component gives it the two-port default, and the extra nets are then bound
    /// nowhere: a wiring failure that looks like nothing on screen.
    /// </summary>
    [Fact]
    public void S18_ABehaviouralSourceIsDrawnWithEveryPinItSensesAndExtractsBackOntoTheRightNets()
    {
        var t = Only("""
            .subckt part in out
            R1 in n1 100
            E1 n1 out VALUE={tanh(V(in,0))}
            .ends part
            """);
        Assert.True(t.IsSupported, t.Refusal);

        var model = SubcircuitCellBuilder.BuildSchematic(t, "Part", n => "../../" + n, new List<string>());
        var e1    = model.Components.Single(c => c.InstanceName == "E1");
        Assert.Equal(SymbolKind.Sdd, e1.Symbol);
        Assert.Equal(2, e1.PortCount);
        Assert.Equal(4, SymbolPortDefs.For(e1.Symbol, e1.PortCount).Length);

        var tb = NetExtractor.Extract(model, "tb").TestBench;
        var extracted = tb.Instances.Single(i => i.InstanceName == "E1");
        var r1        = tb.Instances.Single(i => i.InstanceName == "R1");

        // Four nets: the source's own pair first, then the pair its expression senses.
        Assert.Equal(4, extracted.NetBindings.Count);

        // Port 1 is (n1, out) — n1 is the net R1's second terminal is on, and `out` is a cell port.
        Assert.Equal(r1.NetBindings[1], extracted.NetBindings[0]);

        // Port 2 is (in, 0) — `in` is the net R1's FIRST terminal is on, and the reference is ground.
        Assert.Equal(r1.NetBindings[0], extracted.NetBindings[2]);
        Assert.Equal("0", extracted.NetBindings[3]);

        // …and the sensed pair is a different pair from the constrained one, which is the whole
        // point of the second port existing.
        Assert.NotEqual(extracted.NetBindings[0], extracted.NetBindings[2]);
    }

    [Fact]
    public void S14_ASubcircuitWhoseSourceIsUnreadableIsRefusedWhole()
    {
        var all = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read("""
            .subckt part out a b
            E1 out 0 TABLE {V(a,b)} = (0,0) (1,1)
            R1 a b 1k
            .ends part
            """));

        var t = all.Single();
        Assert.False(t.IsSupported);
    }

    // ── M4b — the charge pair collapses ───────────────────────────────────────

    /// <summary>
    /// The idiom this whole family of models writes a nonlinear capacitance with: a behavioural
    /// voltage source driving a linear capacitor, nothing else on the node between them. The pair
    /// IS one charge, and it is placed as one — which is the only form harmonic balance can carry,
    /// because a branch-current unknown is not one of its unknowns.
    /// </summary>
    [Fact]
    public void M1_AChargePairBecomesOneChargeEquation()
    {
        var t = Only("""
            .subckt part g d
            .param Cdg = 1e-12
            E_Edg d ox VALUE {-(V(g,d) - 2e-12*V(g,d)*V(g,d)/Cdg)}
            C_Cdg ox g {Cdg}
            .ends part
            """);

        Assert.Null(t.Refusal);
        var e = Assert.Single(t.Elements);
        Assert.Equal("E_Edg", e.InstanceName);
        Assert.Equal(SymbolKind.Sdd, e.Symbol);

        // The branch equation is gone; what is left is a CHARGE.
        Assert.DoesNotContain(e.Parameters, q => q.Name == "V[1]");
        Assert.Contains(e.Parameters, q => q.Name == "I[1,1]");

        // Port 1 now spans the pair's two OUTER terminals, and the interior node is gone entirely.
        Assert.Equal("d", e.Nets[0]);
        Assert.Equal("g", e.Nets[1]);
        Assert.DoesNotContain("ox", e.Nets);
        Assert.Contains(e.Notes, n => n.Contains("charge", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>"Nothing else on the interior node" is the entire correctness condition.</b> A third
    /// element there makes the source and the capacitor no longer in series, so the collapse would
    /// be a different circuit — the pair stays the general branch-row device, which still solves at
    /// DC and in S-parameters.
    /// </summary>
    [Fact]
    public void M2_AThirdElementOnTheInteriorNodePreventsTheCollapse()
    {
        var t = Only("""
            .subckt part g d
            E_Edg d ox VALUE {-(V(g,d) - 1e-12*V(g,d)*V(g,d))}
            C_Cdg ox g 1p
            R_leak ox 0 1meg
            .ends part
            """);

        var e = E(t, "E_Edg");
        Assert.Contains(e.Parameters, q => q.Name == "V[1]");
        Assert.DoesNotContain(e.Parameters, q => q.Name == "I[1,1]");
        Assert.Equal(3, t.Elements.Count);
    }

    /// <summary>A definition's PORT is wired by the call site, so it is never an interior node.</summary>
    [Fact]
    public void M3_AnInteriorNodeThatIsAlsoAPortPreventsTheCollapse()
    {
        var t = Only("""
            .subckt part g d ox
            E_Edg d ox VALUE {-(V(g,d) - 1e-12*V(g,d)*V(g,d))}
            C_Cdg ox g 1p
            .ends part
            """);

        Assert.Contains(E(t, "E_Edg").Parameters, q => q.Name == "V[1]");
        Assert.Equal(2, t.Elements.Count);
    }

    /// <summary>
    /// A source that reads the pair it constrains is implicit in its own output, and that pair is
    /// exactly what the collapse dissolves — so there is nothing to write it into.
    /// </summary>
    [Fact]
    public void M4_ASourceThatSensesItsOwnPairIsNotCollapsed()
    {
        var t = Only("""
            .subckt part g d
            E_Edg d ox VALUE {tanh(V(d,ox)) + V(g,d)}
            C_Cdg ox g 1p
            .ends part
            """);

        Assert.Contains(E(t, "E_Edg").Parameters, q => q.Name == "V[1]");
    }

    // ── a function the evaluator that will run it does not have ───────────────

    /// <summary>
    /// The rounding family exists for a PARAMETER expression and not for a device equation, which
    /// carries a derivative alongside every value. Read from one list, <c>INT(…)</c> imported
    /// cleanly and threw "unknown function" from inside the solver.
    /// </summary>
    [Fact]
    public void M5_ARoundingFunctionInADeviceEquationIsRefusedByName()
    {
        var t = Only("""
            .subckt part a b
            G1 a b VALUE={INT(V(a,b))*TANH(V(a,b))}
            .ends part
            """);

        Assert.NotNull(t.Refusal);
        Assert.Contains("int", t.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And the same call in a value that an ordinary evaluator resolves is fine.</summary>
    [Fact]
    public void M6_ARoundingFunctionInAPassiveValueIsKept()
    {
        var t = Only("""
            .subckt part a b
            .param n = 3.7
            R1 a b {1k*INT(n)}
            .ends part
            """);

        Assert.Null(t.Refusal);
        Assert.Contains("int(", P(E(t, "R1"), "R"), StringComparison.Ordinal);
    }

    // ── the transient time variable, one hop away from the element ────────────

    /// <summary>
    /// The reader refuses a <c>time</c> written on an element line. Through a <c>.param</c> the
    /// element's own text names no time at all, so nothing on the line can see it — and left alone
    /// it became a cell carrying a variable that fails to evaluate with an unbound name.
    /// </summary>
    [Fact]
    public void M7_ATimeDependentParameterRefusesTheElementThatReadsIt()
    {
        var t = Only("""
            .param tr = time*2
            .subckt part a b
            R1 a b {tr}
            .ends part
            """);

        Assert.NotNull(t.Refusal);
        Assert.Contains("tr", t.Refusal!, StringComparison.Ordinal);
        Assert.Contains("transient", t.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a definition that never reads it is untouched — a file declares globals for the whole
    /// library, and refusing every definition over one would refuse definitions that work.
    /// </summary>
    [Fact]
    public void M8_ADefinitionThatDoesNotReadItIsUnaffected()
    {
        var t = Only("""
            .param tr = time*2
            .param rd = 1k
            .subckt part a b
            R1 a b {rd}
            .ends part
            """);

        Assert.Null(t.Refusal);
        Assert.DoesNotContain(t.Definition.Variables, v => v.Name == "tr");
    }

    // ── M4c — a capacitance that varies with its own voltage ──────────────────

    /// <summary>
    /// <c>C = f(v)</c> declares the small-signal CAPACITANCE, so the stored charge is ∫C dv and
    /// never C(v)·v. A polynomial has that integral exactly, and <c>NonlinearC</c> is the component
    /// that performs it.
    /// </summary>
    [Fact]
    public void M9_APolynomialCapacitanceBecomesTheIntegratingComponent()
    {
        var e = E(Only("""
            .subckt part a b
            C1 a b C={1p + 0.5p*V(a,b) + 0.25p*V(a,b)*V(a,b)}
            .ends part
            """), "C1");

        Assert.Equal(SymbolKind.NonlinearC, e.Symbol);
        Assert.Equal(3, e.Parameters.Count);
        // Compared as ratios: these are picofarad-scale numbers, and an absolute decimal-place
        // assertion on them says nothing about whether the coefficient is right.
        Assert.Equal(1.0, Evaluate(P(e, "C0")) / 1e-12,    12);
        Assert.Equal(1.0, Evaluate(P(e, "C1")) / 0.5e-12,  12);
        Assert.Equal(1.0, Evaluate(P(e, "C2")) / 0.25e-12, 12);
    }

    /// <summary>
    /// A capacitance with no symbolic integral available is REFUSED, by name, with the spelling
    /// that has no conversion to get wrong. A wrong charge law converges and looks plausible.
    /// </summary>
    [Fact]
    public void M10_ANonPolynomialCapacitanceIsRefusedByName()
    {
        var t = Only("""
            .subckt part a b
            C1 a b C={1p/(1+tanh(V(a,b)))}
            .ends part
            """);

        Assert.NotNull(t.Refusal);
        Assert.Contains("C1", t.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Q={", t.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A constant-valued capacitor is untouched by any of it.</summary>
    [Fact]
    public void M11_AConstantCapacitanceIsStillAnOrdinaryCapacitor()
    {
        var e = E(Only("""
            .subckt part a b
            C1 a b C={2p}
            .ends part
            """), "C1");

        Assert.Equal(SymbolKind.Capacitor, e.Symbol);
    }

    private static double Evaluate(string expression)
        => new CircuitRF.Core.Expressions.Evaluator()
               .Eval(expression, new CircuitRF.Core.Expressions.Scope("test")).AsReal();
}
