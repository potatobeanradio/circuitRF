using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// MN-2's design-layer gates: what a <c>Match</c> refuses, what it contains, and how many internal
/// nets it asks the elaborator for. The stamp itself is <c>Engine.Tests</c>' — nothing here solves.
/// </summary>
public class MatchComponentTests
{
    private static string Cnl(string design) => $"""
        Term:T1  p1 0  Num=1 Z=50
        Term:T2  p2 0  Num=2 Z=50
        Match:MN1  p1 p2  Design={design}
        """;

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static MatchModel ModelOf(MatchDesign design)
    {
        var netlist = Elaborate(Cnl(MatchEmbedding.Encode(design)));
        return (MatchModel)netlist.Components.Single(c => c.InstancePath == "MN1").Model;
    }

    // ── §1: the refusals ──────────────────────────────────────────────────────

    /// <summary>
    /// A <c>Design</c> that will not decode refuses AT ELABORATION, names the instance, and — the
    /// half that matters — never falls back to a default network. A fallback would simulate
    /// perfectly and be a different circuit, with nothing anywhere saying so (match.md §7.2).
    /// </summary>
    [Fact]
    public void ACorruptDesign_RefusesAtElaboration_NamesTheInstance_AndSubstitutesNothing()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Elaborate(Cnl("not-a-design")));

        Assert.Contains("MN1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Design", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Designer", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Valid base64 that is not a design is the same refusal — the decode has to READ the
    /// payload, not merely un-base64 it.</summary>
    [Fact]
    public void AWellFormedButMeaninglessPayload_IsAlsoRefused()
    {
        string payload = Convert.ToBase64String("{\"nonsense\":true"u8.ToArray()).TrimEnd('=');
        var ex = Assert.ThrowsAny<Exception>(() => Elaborate(Cnl(payload)));
        Assert.Contains("MN1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty <c>Design</c> refuses too, and says which component to open.</summary>
    [Fact]
    public void AMissingDesign_Refuses()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Elaborate("""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p2 0  Num=2 Z=50
            Match:MN1  p1 p2
            """));
        Assert.Contains("MN1", ex.Message, StringComparison.Ordinal);
    }

    // ── §2: the default design ────────────────────────────────────────────────

    /// <summary>
    /// The default a freshly-placed <c>Match</c> carries synthesises cleanly. Until MN-3 lands there
    /// is no way to give a placed component a design, so one that arrived refusing would be one
    /// nobody could repair from the schematic.
    /// </summary>
    [Fact]
    public void TheDefaultDesign_SynthesisesAndDecodesRoundTrip()
    {
        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.DefaultPayload, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(1.8e9, decoded!.F1);
        Assert.Equal(2.2e9, decoded.F2);
        Assert.Equal(4, decoded.Order);
        Assert.Equal(ResponseShape.ChebyshevFano, decoded.Response);
        Assert.Equal(50.0, decoded.Term1.R);
        Assert.Equal(10.0, decoded.Term2.R);
        Assert.False(decoded.Term1.HasReactance);
        Assert.False(decoded.Term2.HasReactance);

        // 2026-08-19: the default now ARRIVES with a solution applied, because 50 Ω into 10 Ω is a
        // real transformation and an unapplied one opens the Designer on "Π N² not reached".
        Assert.NotEmpty(decoded.Transforms);
        Assert.Single(decoded.AppliedSolutions);

        var rebuilt = MatchRebuild.Rebuild(decoded);
        Assert.Null(rebuilt.Refusal);
        Assert.NotNull(rebuilt.Network);
        Assert.Empty(rebuilt.Notes);
        Assert.True(rebuilt.OnTarget,
            "the shipped default must open ON TARGET — its own applied transform is what gets it there");
    }

    /// <summary>
    /// The default's own shape, stated so a change to it is a deliberate one: an order-4 bandpass
    /// ladder with one Norton transform already applied — nine elements, and the two internal nets
    /// its series arms need.
    /// </summary>
    /// <remarks>
    /// This used to assert ZERO internal nodes, which was a property of the OLD 50 Ω-to-50 Ω default
    /// (shunt-series-shunt, one series arm running pin to pin). Internal nets are not a cost worth
    /// choosing a default for — they are numbered and eliminated like any other — and the property
    /// that actually matters, that the thing simulates and matches, is asserted above.
    /// </remarks>
    [Fact]
    public void TheDefaultDesign_HasTheLadderItsOrderImplies()
    {
        var model = ModelOf(MatchEmbedding.DefaultDesign());
        Assert.Equal(9, model.StampedElements.Count);
        Assert.Equal(2, model.InternalNodeCount);
    }

    // ── §0.1: absorbed elements are not ours ──────────────────────────────────

    /// <summary>
    /// The golden §4.9 interstage design: nine elements in the ladder, TWO of them supplied by the
    /// external terminations, so the component contains seven.
    ///
    /// <para>The flag is what decides, not the name — <c>CFano</c> is ours and is kept, while the
    /// ordinarily-named <c>C1</c> and <c>C4</c> are the terminations' own and are dropped.</para>
    /// </summary>
    [Fact]
    public void TheGoldenDesign_ContainsTheLadderMinusTheTwoAbsorbedReactances()
    {
        var design = MatchAbcdOracle.GoldenDesign();
        var rebuilt = MatchRebuild.Rebuild(design);
        var model = ModelOf(design);

        Assert.Equal(9, rebuilt.Network!.Elements.Count);
        Assert.Equal(2, rebuilt.Network.Elements.Count(e => e.IsAbsorbed));

        Assert.Equal(7, model.StampedElements.Count);
        Assert.DoesNotContain(model.StampedElements, e => e.IsAbsorbed);
        Assert.DoesNotContain(model.StampedElements, e => e.Name is "C1" or "C4");
        Assert.Contains(model.StampedElements, e => e.Name == "CFano");
    }

    /// <summary>
    /// Two series arms in the stamped ladder, so one internal net. Worth pinning as a NUMBER because
    /// it is the thing the elaborator mints against: derived from the finished ladder, not from
    /// <see cref="MatchDesign.Order"/> — the surplus element here is proof the two differ.
    /// </summary>
    [Fact]
    public void TheGoldenDesign_NeedsOneInternalNode()
    {
        Assert.Equal(1, ModelOf(MatchAbcdOracle.GoldenDesign()).InternalNodeCount);
    }

    /// <summary>
    /// The elaborator mints them, keyed on the instance path, so two instances of ONE design never
    /// share an internal net. Sharing one would connect two independent matching networks through
    /// their own middles and still solve.
    /// </summary>
    [Fact]
    public void TwoInstancesOfOneDesign_GetSeparateInternalNets()
    {
        string design = MatchEmbedding.Encode(MatchAbcdOracle.GoldenDesign());
        var netlist = Elaborate($"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p3 0  Num=2 Z=50
            Match:MN1  p1 p2  Design={design}
            Match:MN2  p2 p3  Design={design}
            """);

        int a = netlist.Nodes.GetOrAssign("__match_MN1_0");
        int b = netlist.Nodes.GetOrAssign("__match_MN2_0");
        Assert.NotEqual(a, b);

        var mn1 = netlist.Components.Single(c => c.InstancePath == "MN1");
        var mn2 = netlist.Components.Single(c => c.InstancePath == "MN2");
        Assert.Equal(3, mn1.Nodes.Length);
        Assert.Equal(3, mn2.Nodes.Length);
        Assert.Equal(a, mn1.Nodes[2]);
        Assert.Equal(b, mn2.Nodes[2]);
    }

    // ── §2: the echo parameters are not an input ──────────────────────────────

    /// <summary>
    /// <c>F1</c>/<c>F2</c>/<c>Order</c> are ECHO: the Designer writes them so the design can be shown
    /// on the schematic, and the model never reads them back. An echo contradicting the design must
    /// therefore change nothing at all — which is what makes <c>Design</c> authoritative rather than
    /// merely first.
    /// </summary>
    [Fact]
    public void TheEchoParameters_AreNeverReadBack()
    {
        string design = MatchEmbedding.Encode(MatchEmbedding.DefaultDesign());
        var netlist = Elaborate($"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p2 0  Num=2 Z=50
            Match:MN1  p1 p2  Design={design} F1=9 GHz F2=11 GHz Order=6 Response=Bessel R1=1 R2=1
            """);

        var model = (MatchModel)netlist.Components.Single(c => c.InstancePath == "MN1").Model;
        Assert.Equal(1.8e9, model.Design.F1);
        Assert.Equal(2.2e9, model.Design.F2);
        Assert.Equal(4, model.Design.Order);
        Assert.Equal(ResponseShape.ChebyshevFano, model.Design.Response);
        Assert.Equal(9, model.StampedElements.Count);
    }
}
