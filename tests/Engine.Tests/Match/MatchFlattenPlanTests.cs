using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Match;

/// <summary>
/// MN-5's two engine-level gates (brief §5), through <see cref="MatchFlattenPlan"/> — the shared,
/// framework-free walk the Ui turns into placed components.
///
/// <para><b>Why the plan and not the cell.</b> Flatten writes a cell folder through
/// <c>src/Ui</c>'s persistence, and this project cannot reference the UI layer. Testing the plan is
/// not a weaker substitute for testing the cell: the plan IS the topology the cell is laid out from,
/// one element per component and one net per wire, so a defect in it is a defect in every cell
/// flatten will ever write. The end-to-end check — real files, real extraction, real placement —
/// lives beside it in <c>tests/Ui.Tests/Match/MatchFlattenTests.cs</c>, where those things exist.</para>
/// </summary>
public class MatchFlattenPlanTests(ITestOutputHelper output)
{
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>match.md §4.9's interstage problem — one parallel absorbed end and one series.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static MatchNetwork Ladder(MatchDesign design)
    {
        var rebuilt = MatchRebuild.Rebuild(design);
        Assert.Null(rebuilt.Refusal);
        return rebuilt.Network!;
    }

    private static readonly double[] Band =
        [1e9, 2e9, 3.3e9, 3.8e9, 4.06202e9, 4.5e9, 5.0e9, 7e9, 12e9];

    // ── the plan as a netlist ─────────────────────────────────────────────────

    /// <summary>
    /// The plan's LIVE content: one <c>L</c> or <c>C</c> per stamped element, on the plan's own nets.
    /// Nothing is combined — a series arm's L and C are two lines, exactly as the flattened cell
    /// writes two components.
    /// </summary>
    private static string LadderLines(MatchFlattenPlan plan, string tag)
    {
        var sb = new StringBuilder();
        foreach (var fe in plan.Elements)
        {
            string type = fe.Element.Type == ElementType.L ? "L" : "C";
            sb.AppendLine($"{type}:{tag}{fe.Element.Name}  {Net(fe.NetA, tag)} {Net(fe.NetB, tag)}  " +
                          $"{type}={N(fe.Element.Value)}");
        }
        return sb.ToString();
    }

    /// <summary>The plan's DISABLED content, written out — what "enable both Terms" produces.</summary>
    private static string TerminationLines(MatchFlattenPlan plan, string tag)
    {
        var sb = new StringBuilder();
        foreach (var t in plan.Terminations)
        {
            if (t.Absorbed is { } absorbed)
            {
                string type = absorbed.Type == ElementType.L ? "L" : "C";
                sb.AppendLine($"{type}:{tag}{absorbed.Name}  {Net(t.AbsorbedNetA!, tag)} " +
                              $"{Net(t.AbsorbedNetB!, tag)}  {type}={N(absorbed.Value)}");
            }
            sb.AppendLine($"Term:{tag}T{t.End}  {Net(t.TermHighNet, tag)} 0  Num={t.End} Z={N(t.R)}");
        }
        return sb.ToString();
    }

    /// <summary>Namespaces a plan net so two copies of one plan can share a netlist.</summary>
    private static string Net(string net, string tag) => net == "0" ? "0" : tag + net;

    private static Complex[,][] Sweep(string cnl, double[] frequencies)
    {
        var ds = SParameterEngine.Run(Elaborate(cnl), frequencies);
        var s = ds["S"];
        var result = new Complex[2, 2][];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                result[i, j] = new Complex[frequencies.Length];
                for (int f = 0; f < frequencies.Length; f++)
                    result[i, j][f] = (Complex)s[f, i, j];
            }
        return result;
    }

    private static double WorstDifference(Complex[,][] a, Complex[,][] b)
    {
        double worst = 0.0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                for (int f = 0; f < a[i, j].Length; f++)
                    worst = Math.Max(worst, (a[i, j][f] - b[i, j][f]).Magnitude);
        return worst;
    }

    // ── §5: component ≡ flattened cell ────────────────────────────────────────

    /// <summary>
    /// <b>The Match component and the flatten plan give identical S-parameters to 1e-12.</b> That is
    /// the whole point of the feature and the justification for MN-2 stamping elementwise rather
    /// than as one ABCD block — with a lumped block this equality would be an accident waiting to
    /// break.
    /// </summary>
    [Theory]
    [InlineData(false)]   // §4.9's interstage design — two absorbed ends, a CFano, two series arms
    [InlineData(true)]    // the shipped default — 50/50 resistive, nothing absorbed
    public void TheComponent_AndTheFlattenPlan_AgreeToOnePartInATrillion(bool useDefault)
    {
        var design = useDefault ? MatchEmbedding.DefaultDesign() : Golden();
        var plan = MatchFlattenPlan.Build(Ladder(design));

        var component = Sweep($"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p2 0  Num=2 Z=50
            Match:MN1  p1 p2  Design={MatchEmbedding.Encode(design)}
            """, Band);

        var flattened = Sweep($"""
            Term:T1  F{plan.Port1Net} 0  Num=1 Z=50
            Term:T2  F{plan.Port2Net} 0  Num=2 Z=50
            {LadderLines(plan, "F")}
            """, Band);

        double worst = WorstDifference(component, flattened);
        output.WriteLine($"worst |ΔS| over {Band.Length} frequencies: {worst:E3}");
        Assert.True(worst < 1e-12, $"the component and the flatten plan differ by {worst:E3}");
    }

    /// <summary>
    /// <b>The absorbed reactances are NOT in the cell's live content.</b> Stated from both sides,
    /// because the mistake is invertible: writing them too produces a cell that looks right beside
    /// the Designer's preview and is a different circuit the moment it is placed.
    /// </summary>
    [Fact]
    public void ThePlansLiveContent_ExcludesTheAbsorbedReactances()
    {
        var design = Golden();
        var ladder = Ladder(design);
        var plan = MatchFlattenPlan.Build(ladder);

        Assert.Equal(2, ladder.Elements.Count(e => e.IsAbsorbed));
        Assert.Equal(ladder.Elements.Count - 2, plan.Elements.Count);
        Assert.DoesNotContain(plan.Elements, fe => fe.Element.IsAbsorbed);

        // Both absorbed elements ARE carried — on the terminations, which is where they belong.
        var carried = plan.Terminations.Select(t => t.Absorbed?.Name).ToList();
        foreach (var e in ladder.Elements.Where(e => e.IsAbsorbed))
            Assert.Contains(e.Name, carried);

        // A CFano surplus element is OURS and stays in the live content — the rule is read off the
        // absorbed flag, never off a name.
        var fano = ladder.Elements.FirstOrDefault(e => e.IsExcess);
        if (fano is not null)
        {
            output.WriteLine($"excess element {fano.Name} is stamped, as it must be");
            Assert.Contains(plan.Elements, fe => fe.Element.Name == fano.Name);
        }
    }

    // ── §2: the terminations, switched back on ────────────────────────────────

    /// <summary>
    /// <b>Enabling both Terms reproduces the Designer's own response.</b> This is what makes writing
    /// the terminations disabled rather than omitting them worth doing: the first thing anyone wants
    /// after flattening is to run the cell alone and see the plot they designed to.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnablingTheTerminations_ReproducesTheDesignersOwnResponse(bool useDefault)
    {
        var design = useDefault ? MatchEmbedding.DefaultDesign() : Golden();
        var ladder = Ladder(design);
        var plan = MatchFlattenPlan.Build(ladder);

        var netlist = Elaborate($"""
            {LadderLines(plan, "E")}
            {TerminationLines(plan, "E")}
            """);
        var ds = SParameterEngine.Run(netlist, Band);

        double worst = 0.0;
        for (int f = 0; f < Band.Length; f++)
        {
            var s11 = (Complex)ds["S"][f, 0, 0];
            var s21 = (Complex)ds["S"][f, 1, 0];
            var (expected11, expected21) = MatchResponse.At(ladder, Band[f]);
            worst = Math.Max(worst, Math.Max((s11 - expected11).Magnitude, (s21 - expected21).Magnitude));
        }

        output.WriteLine($"worst |ΔS| vs MatchResponse over {Band.Length} frequencies: {worst:E3}");
        Assert.True(worst < 1e-9,
            $"the re-enabled cell must reproduce the Designer's response; it differs by {worst:E3}");
    }

    /// <summary>
    /// The termination the SYNTHESIS assumed, not a resistive approximation of it: a series-topology
    /// end puts its reactance BETWEEN the interface net and the reference resistance, and a parallel
    /// one puts both across it. Getting that backwards still simulates — into a different network.
    /// </summary>
    [Fact]
    public void ASeriesEnd_PutsTheReferenceResistanceBehindItsReactance()
    {
        var plan = MatchFlattenPlan.Build(Ladder(Golden()));

        var parallelEnd = plan.Terminations.Single(t => t.End == 1);
        Assert.NotNull(parallelEnd.Absorbed);
        Assert.True(parallelEnd.Absorbed!.IsShunt);
        Assert.Equal(parallelEnd.PortNet, parallelEnd.TermHighNet);
        Assert.Equal(MatchFlattenPlan.GroundNet, parallelEnd.AbsorbedNetB);

        var seriesEnd = plan.Terminations.Single(t => t.End == 2);
        Assert.NotNull(seriesEnd.Absorbed);
        Assert.False(seriesEnd.Absorbed!.IsShunt);
        Assert.NotEqual(seriesEnd.PortNet, seriesEnd.TermHighNet);
        Assert.Equal(seriesEnd.PortNet, seriesEnd.AbsorbedNetA);
        Assert.Equal(seriesEnd.TermHighNet, seriesEnd.AbsorbedNetB);

        output.WriteLine(
            $"end 1: Term on {parallelEnd.TermHighNet}; " +
            $"end 2: {seriesEnd.Absorbed.Name} {seriesEnd.AbsorbedNetA}→{seriesEnd.AbsorbedNetB}, " +
            $"Term on {seriesEnd.TermHighNet}");
    }

    /// <summary>
    /// A resistive end carries no reactance at all, and its <c>Term</c> sits straight on the
    /// interface net — the shipped default's own shape.
    /// </summary>
    [Fact]
    public void AResistiveEnd_CarriesNoReactance()
    {
        var plan = MatchFlattenPlan.Build(Ladder(MatchEmbedding.DefaultDesign()));

        Assert.All(plan.Terminations, t =>
        {
            Assert.Null(t.Absorbed);
            Assert.Equal(t.PortNet, t.TermHighNet);
            Assert.Equal(50.0, t.R, 12);
        });
    }
}
