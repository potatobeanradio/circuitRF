using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Palette wiring for the RLC family — SRLC and PRLC, and the six two-element members added
/// beside them (SRL, SRC, SLC, PRL, PRC, PLC; owner, 2026-09-20).
///
/// <para><b>The load-bearing test is R2, the pin contract.</b> The stated reason these parts are
/// drawn small enough to share R/L/C's 400-unit span is that a designer can replace a plain R, L or
/// C with one of them and not touch a wire. Nothing else enforces that: the glyph geometry and the
/// pin table are edited in two different files, and a symbol redraw that nudged a pin would leave a
/// part that still places, still saves and still simulates — while every schematic it was dropped
/// into came apart at the wires. So the pins are asserted against R/L/C's own, by reading both,
/// rather than against copied literals that would move together with a mistake.</para>
///
///   R1 — both are placeable palette items, under Lumped.
///   R2 — pin positions are IDENTICAL to R, L and C's (the swap contract).
///   R3 — each tile places its own engine component, and that component exists in the factory.
///   R4 — every registry parameter name reaches the model: perturb it, the stamp must move.
///   R5 — a freshly placed part is a working part: the shipped defaults stamp.
///   R6 — the seven members that carry an inductor expose the branch a Mutual references, and the
///        two that do not (SRC, PRC) deliberately do not.
///
/// <para><b>Every one of them is one row of the same arrays.</b> The family is nine parts over two
/// pieces of arithmetic, so a test that named SRLC and PRLC and left the other seven to a later
/// round would be the shape of the defect it is meant to catch.</para>
/// </summary>
public class RlcPaletteWiringTests
{
    /// <summary>Every member of the family, in the order the palette lists them.</summary>
    private static readonly SymbolKind[] Rlc =
    [
        SymbolKind.Srlc, SymbolKind.Prlc,
        SymbolKind.Srl,  SymbolKind.Src,  SymbolKind.Slc,
        SymbolKind.Prl,  SymbolKind.Prc,  SymbolKind.Plc,
    ];

    /// <summary>The seven that carry an inductor, and can therefore be one end of a Mutual.</summary>
    private static readonly SymbolKind[] Inductive =
    [
        SymbolKind.Srlc, SymbolKind.Prlc, SymbolKind.Srl, SymbolKind.Slc,
        SymbolKind.Prl,  SymbolKind.Plc,
    ];

    /// <summary>
    /// The parameter names a member carries, read off its own engine reference: "SRL" holds an R
    /// and an L, "PLC" an L and a C. Derived rather than tabulated, so a new member needs no row
    /// here and a member whose reference and parameter set disagree is caught rather than copied.
    /// </summary>
    private static string[] ElementsOf(SymbolKind kind)
    {
        string r = ComponentTypeRegistry.EngineReference(kind);
        return new[] { "R", "L", "C" }.Where(e => r[1..].Contains(e)).ToArray();
    }

    // ── R1 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void R1_BothArePlaceablePaletteItemsUnderLumped()
    {
        foreach (var kind in Rlc)
        {
            var item = LibraryCatalog.AllItems.FirstOrDefault(i => i.Kind == kind);
            Assert.True(item is not null, $"{kind} is missing from the palette catalog");
            Assert.Equal(ComponentCategory.Lumped, item!.Category);
            Assert.Contains(kind, LibraryCatalog.ByCategory(ComponentCategory.Lumped).Select(i => i.Kind));
        }

        // Typing the code into the palette's search box has to land on the part — and case does
        // not matter, which is what the lower-cased round below says.
        foreach (var kind in Rlc)
        {
            string code = ComponentTypeRegistry.EngineReference(kind);
            Assert.True(ComponentTypeRegistry.TryParseCode(code, out var k1, out _), code);
            Assert.Equal(kind, k1);
            Assert.True(ComponentTypeRegistry.TryParseCode(code.ToLowerInvariant(), out var k2, out _), code);
            Assert.Equal(kind, k2);
        }
    }

    // ── R2: the swap contract ─────────────────────────────────────────────────

    [Fact]
    public void R2_PinPositionsAreIdenticalToTheStandaloneRLandC()
    {
        var reference = SymbolPortDefs.For(SymbolKind.Resistor);

        // The premise: R, L and C already agree with each other. If they ever stop, the comparison
        // below is measuring the wrong thing, so it is checked rather than assumed.
        Assert.Equal(reference, SymbolPortDefs.For(SymbolKind.Inductor));
        Assert.Equal(reference, SymbolPortDefs.For(SymbolKind.Capacitor));

        foreach (var kind in Rlc)
            Assert.Equal(reference, SymbolPortDefs.For(kind));

        // And what the RENDERER draws — the symbol's own pins, not just the port table, since those
        // are two separate code paths and a schematic connects to what it draws.
        var refPins = BuiltInSymbols.Primitives(SymbolKind.Resistor).Pins
            .Select(p => (p.LocalX, p.LocalY, p.Name)).ToList();
        foreach (var kind in Rlc)
            Assert.Equal(refPins, BuiltInSymbols.Primitives(kind).Pins
                .Select(p => (p.LocalX, p.LocalY, p.Name)).ToList());
    }

    /// <summary>
    /// The glyphs are supposed to be SMALLER than the standalone parts they borrow from — that is
    /// what buys the room to stack three of them inside one 400-unit span. Asserted as "the body
    /// fits between the leads", which is the property that actually matters: any primitive
    /// straying past y = ±200 would collide with the wire attached to the pin.
    /// </summary>
    [Fact]
    public void R2b_EveryDrawnPrimitiveStaysInsideTheLeadSpan()
    {
        foreach (var kind in Rlc)
        {
            var sym = BuiltInSymbols.Primitives(kind);
            var ys  = sym.Primitives.SelectMany(YsOf).ToList();

            Assert.True(ys.Min() >= -200.0, $"{kind}: geometry reaches y={ys.Min():F1}, past the top pin");
            Assert.True(ys.Max() <= +200.0, $"{kind}: geometry reaches y={ys.Max():F1}, past the bottom pin");

            // And it actually REACHES both pins — a glyph whose leads stopped short would draw a
            // visible gap between the symbol and the wire, which no pin assertion would catch.
            Assert.Equal(-200.0, ys.Min(), 6);
            Assert.Equal(+200.0, ys.Max(), 6);
        }
    }

    /// <summary>Every y a primitive touches. Curves are bounded by their control points, which is
    /// an over-estimate and therefore safe for a "stays inside" claim.</summary>
    private static IEnumerable<double> YsOf(SymbolPrimitive p) => p switch
    {
        LinePrimitive l      => [l.Y1, l.Y2],
        PolylinePrimitive pl => pl.Points.Select(pt => pt[1]),
        ArcPrimitive a       => [a.Cy - a.R, a.Cy + a.R],
        CirclePrimitive c    => [c.Cy - c.R, c.Cy + c.R],
        QuadCurvePrimitive q => [q.P0Y, q.CtrlY, q.P2Y],
        _ => [],
    };

    // ── R3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void R3_EachTilePlacesItsOwnEngineComponentAndTheFactoryKnowsIt()
    {
        // The reference IS the kind's own name, which is what makes ElementsOf derivable.
        Assert.Equal(
            ["SRLC", "PRLC", "SRL", "SRC", "SLC", "PRL", "PRC", "PLC"],
            Rlc.Select(ComponentTypeRegistry.EngineReference).ToArray());

        foreach (var kind in Rlc)
        {
            string code = ComponentTypeRegistry.EngineReference(kind);
            Assert.True(ComponentModelFactory.IsPrimitive(code), $"the factory does not know '{code}'");

            // Series members stamp one branch, parallel members stamp admittances — a tile wired to
            // the wrong half of the family would still place and still solve, as something else.
            var model = ComponentModelFactory.TryCreate(code);
            if (code[0] == 'S') Assert.IsAssignableFrom<SeriesRlcBranchModel>(model);
            else                Assert.IsAssignableFrom<ParallelRlcBranchModel>(model);
        }
    }

    // ── R4: every offered parameter reaches the model ─────────────────────────

    [Theory]
    [InlineData(SymbolKind.Srlc)]
    [InlineData(SymbolKind.Prlc)]
    [InlineData(SymbolKind.Srl)]
    [InlineData(SymbolKind.Src)]
    [InlineData(SymbolKind.Slc)]
    [InlineData(SymbolKind.Prl)]
    [InlineData(SymbolKind.Prc)]
    [InlineData(SymbolKind.Plc)]
    public void R4_EveryRegistryParameterNameReachesTheStamp(SymbolKind kind)
    {
        var names = ComponentTypeRegistry.DefaultParameters(kind, 2).Select(p => p.Name).ToList();

        // EXACTLY the elements the part has — the whole reason to place an SRL rather than an SRLC
        // with a value nobody meant. An extra row here is a number on the schematic that does
        // nothing, and a missing one is a value the user cannot reach.
        Assert.Equal(ElementsOf(kind), names);

        var seed = new Dictionary<string, double>
        {
            ["R"] = 3.0, ["L"] = 4e-9, ["C"] = 5e-12,
        };
        var baseline = names.ToDictionary(n => n, n => new Value(seed[n]));
        var reference = Fingerprint(kind, baseline);

        foreach (var name in names)
        {
            var perturbed = new Dictionary<string, Value>(baseline)
            {
                // A factor of 7 rather than 2: it cannot be reached by any other parameter's own
                // scaling, so a stamp that moved for the wrong reason still reads as wrong.
                [name] = new Value(baseline[name].AsReal() * 7.0),
            };
            var moved = Fingerprint(kind, perturbed);
            Assert.True(reference.Zip(moved).Any(t => System.Math.Abs(t.First - t.Second) > 1e-18),
                $"{kind}: parameter '{name}' is offered by the registry but changes nothing in the stamp");
        }
    }

    // ── R5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void R5_TheShippedDefaultsStampWithoutThrowing()
    {
        foreach (var kind in Rlc)
        {
            // Exactly what a freshly placed part carries, expressions and units and all, run
            // through the same evaluation the elaborator does.
            var defaults = ComponentTypeRegistry.DefaultParameters(kind, 2);
            var vals = defaults.ToDictionary(
                p => p.Name,
                p => new Value(double.Parse(p.Expression) * UnitScale(p.Unit)));

            var fp = Fingerprint(kind, vals);
            Assert.All(fp, v => Assert.False(double.IsNaN(v) || double.IsInfinity(v)));
        }
    }

    /// <summary>R = 1 Ω, L = 1 nH, C = 1 pF — the values every member of the family ships with,
    /// so a swap between any two of them reads the same.</summary>
    [Fact]
    public void R5b_ShippedDefaultsAreOneOhmOneNanohenryOnePicofarad()
    {
        var expected = new Dictionary<string, (string Expr, string Unit)>
        {
            ["R"] = ("1", "Ω"), ["L"] = ("1", "nH"), ["C"] = ("1", "pF"),
        };

        foreach (var kind in Rlc)
        {
            var d = ComponentTypeRegistry.DefaultParameters(kind, 2).ToDictionary(p => p.Name);
            foreach (var name in ElementsOf(kind))
                Assert.Equal(expected[name], (d[name].Expression, d[name].Unit));

            // Every value shows on the schematic: the point of these parts is that the numbers are
            // on the page, so hiding one would defeat the part.
            Assert.All(d.Values, p => Assert.True(p.ShowOnSchematic, $"{kind}.{p.Name} is hidden"));
        }
    }

    // ── R6: a Mutual can reference either ─────────────────────────────────────

    /// <remarks>
    /// <b>The negative half is the load-bearing one.</b> An SRC or a PRC that implemented
    /// <see cref="IInductiveBranch"/> would let a Mutual name it and stamp −jωM onto a diagonal
    /// that is not an inductance — a PRC does not even allocate a branch — and both give a number
    /// that solves. The refusal only happens because the interface is absent.
    /// </remarks>
    [Fact]
    public void R6_OnlyTheMembersThatCarryAnInductorExposeABranchAMutualCanCoupleTo()
    {
        // The kind the family was modelled on, so they all stay one family.
        Assert.IsAssignableFrom<IInductiveBranch>(ComponentModelFactory.TryCreate("L"));

        foreach (var kind in Inductive)
            Assert.IsAssignableFrom<IInductiveBranch>(
                ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind)));

        foreach (var kind in new[] { SymbolKind.Src, SymbolKind.Prc })
            Assert.IsNotAssignableFrom<IInductiveBranch>(
                ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind)));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every matrix entry the model stamps, at two frequencies, flattened. Two frequencies because
    /// a reactive term that only appears at one of them would otherwise be invisible.
    /// </summary>
    private static double[] Fingerprint(SymbolKind kind, IReadOnlyDictionary<string, Value> parameters)
    {
        var model = ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind))!;
        var ec = new CircuitRF.Core.Elaboration.ElaboratedComponent(
            componentType: ComponentTypeRegistry.EngineReference(kind),
            instancePath:  "X1",
            nodes:         [1, 2],
            parameters:    parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
            model:         model);

        var vals = new List<double>();
        foreach (double omega in new[] { 2 * System.Math.PI * 1e9, 2 * System.Math.PI * 4.3e9 })
        {
            var probe = new StampProbe();
            model.Stamp(probe, ec, omega);
            vals.AddRange(probe.Entries);
        }
        return [.. vals];
    }

    private static double UnitScale(string unit) => unit switch
    {
        "Ω"  => 1.0,
        "nH" => 1e-9,
        "pF" => 1e-12,
        _    => 1.0,
    };

    /// <summary>
    /// A recording <see cref="CircuitRF.Core.IMnaContext"/>. Records the VALUES stamped rather than
    /// solving anything — this test is about whether a parameter is wired through at all, and a
    /// solve would let two different stamps produce one number.
    /// </summary>
    private sealed class StampProbe : CircuitRF.Core.IMnaContext
    {
        public List<double> Entries { get; } = [];
        private int _branches;

        private void Rec(System.Numerics.Complex z) { Entries.Add(z.Real); Entries.Add(z.Imaginary); }

        public void AddAdmittance(int a, int b, System.Numerics.Complex y) { Entries.Add(a); Entries.Add(b); Rec(y); }
        public void AddBlockAdmittance(int r, int c, System.Numerics.Complex y) { Entries.Add(r); Entries.Add(c); Rec(y); }
        public int  AddBranch() => _branches++;
        public void AddBranchCurrent(int br, int from, int to) { Entries.Add(br); Entries.Add(from); Entries.Add(to); }
        public void AddConstraint(int br, int n, System.Numerics.Complex k) { Entries.Add(br); Entries.Add(n); Rec(k); }
        public void AddNodeBranchCoupling(int n, int br, System.Numerics.Complex k) { Entries.Add(n); Entries.Add(br); Rec(k); }
        public void AddBranchConstraint(int br, int ob, System.Numerics.Complex k) { Entries.Add(br); Entries.Add(ob); Rec(k); }
        public void AddCurrentInjection(int n, System.Numerics.Complex j) { Entries.Add(n); Rec(j); }
        public void AddSourceValue(int br, System.Numerics.Complex v) { Entries.Add(br); Rec(v); }
    }
}
