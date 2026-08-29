using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

/// <summary>
/// MN-3's gates (brief §8). <b>View-model tests, not pixel tests</b> — everything the window has to
/// promise is a property of <see cref="MatchDesignerViewModel"/>, and the two places where a pixel
/// would be the only oracle (the pictogram, the ladder drawing) are asserted through the layout model
/// the renderer reads, so the assertion is about meaning rather than about a colour.
/// </summary>
public class MatchDesignerTests(ITestOutputHelper output)
{
    // ── fixtures ──────────────────────────────────────────────────────────────

    /// <summary>match.md §4.9's interstage problem — the acceptance anchor.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static EditableComponent PlaceMatch(MatchDesign? design = null)
    {
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        if (design is not null)
            comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        return comp;
    }

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign? design = null)
    {
        var model = new SchematicEditModel();
        var comp = PlaceMatch(design);
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        // The response feasibility and the solutions are computed on a worker now; a test asserting
        // on either is asserting on nothing until that pass has landed.
        designer.WaitForAnalysis();
        return (vm, comp, designer);
    }

    /// <summary>Picks a transformable pair by its display name, so a test says which one it means.</summary>
    private static MatchAvailablePair Pair(MatchDesignerViewModel d, string display)
    {
        var pairs = d.AvailablePairs();
        var hit = pairs.FirstOrDefault(p => p.Display == display);
        Assert.True(hit is not null,
            $"'{display}' is not available; the ladder offers {string.Join(", ", pairs.Select(p => p.Display))}");
        return hit!;
    }

    private static MatchDesign StoredDesign(EditableComponent comp)
    {
        string payload = comp.Parameters.First(p => p.Name == "Design").Expression;
        Assert.True(MatchEmbedding.TryDecode(payload, out var d));
        return d!;
    }

    // ── §0.3 — everything the user sets survives a save and a reload ──────────

    /// <summary>
    /// The brief's headline guarantee, through the UI: two transforms (one pi, one T, one locked),
    /// link on, a Q-adjusted solution applied — close the window, open a NEW one on the same
    /// component, and every one of those is back.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ASessionRoundTrip_BringsBackEveryTransform_ItsForm_ItsLock_TheLinkStateAndTheBadges()
    {
        var (vm, comp, designer) = Open(Golden());

        var pairs = designer.AvailablePairs();
        Assert.True(pairs.Count >= 2, $"the golden ladder offers {pairs.Count} pairs");
        output.WriteLine("pairs: " + string.Join(", ", pairs.Select(p => p.Display)));

        designer.LinkTransforms = true;
        designer.AddTransform(pairs[0]);
        var second = designer.AvailablePairs().FirstOrDefault();
        Assert.NotNull(second);
        designer.AddTransform(second!);

        designer.Transforms[1].Form = TransformForm.T;
        designer.Transforms[1].Locked = true;

        // A solution's fingerprint has to be in AppliedSolutions for the badge to survive.
        designer.WaitForAnalysis();
        var solution = designer.Solutions.FirstOrDefault();
        Assert.NotNull(solution);
        solution!.Apply();
        string appliedFingerprint = solution.Solution.Fingerprint;

        var before = StoredDesign(comp);
        designer.Dispose();

        var reopened = new MatchDesignerViewModel();
        reopened.SetTarget(vm, comp);
        reopened.WaitForAnalysis();

        Assert.Equal(before.Transforms.Count, reopened.Design.Transforms.Count);
        for (int i = 0; i < before.Transforms.Count; i++)
        {
            Assert.Equal(before.Transforms[i].ElementA, reopened.Design.Transforms[i].ElementA);
            Assert.Equal(before.Transforms[i].ElementB, reopened.Design.Transforms[i].ElementB);
            Assert.Equal(before.Transforms[i].Form,     reopened.Design.Transforms[i].Form);
            Assert.Equal(before.Transforms[i].Locked,   reopened.Design.Transforms[i].Locked);
            Assert.Equal(before.Transforms[i].N,        reopened.Design.Transforms[i].N, 12);
        }
        Assert.Equal(before.LinkTransforms, reopened.Design.LinkTransforms);
        Assert.Equal(before.QAdjust, reopened.Design.QAdjust, 12);
        Assert.Contains(appliedFingerprint, reopened.Design.AppliedSolutions);

        // The badge itself, not just the fingerprint behind it.
        var badges = reopened.Solutions
            .Where(s => s.Solution.Fingerprint == appliedFingerprint)
            .Select(s => s.Badge).ToList();
        Assert.NotEmpty(badges);
        Assert.All(badges, b => Assert.True(
            b is MatchSolutionBadge.Current or MatchSolutionBadge.PreviouslyApplied,
            $"badge was {b}"));

        reopened.Dispose();
    }

    // ── §0.3 — nothing lives only in the view-model ───────────────────────────

    /// <summary>
    /// Every committed edit writes <c>Design</c>. Asserted by making one edit of each kind and reading
    /// the component's own parameter back — never the view-model's copy.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void EveryCommittedEdit_WritesTheDesignParameter()
    {
        var (_, comp, designer) = Open(Golden());

        designer.Order = 2;
        Assert.Equal(2, StoredDesign(comp).Order);

        designer.Term1.Resistance = 150.0;
        Assert.Equal(150.0, StoredDesign(comp).Term1.R, 9);

        designer.AllowNegativeComponents = true;
        Assert.True(StoredDesign(comp).AllowNegativeComponents);

        designer.LinkTransforms = false;
        Assert.False(StoredDesign(comp).LinkTransforms);

        designer.PlotPoints = 201;
        Assert.Equal(201, StoredDesign(comp).PlotPoints);

        // ONE MORE than there were, not exactly one. The termination edit above can now leave the
        // design on an auto-solved rack that already carries transforms (2026-08-28: a termination
        // edit moves onto a solution that reaches the target, widening past the design's own family
        // rather than presenting a mismatch), and what this line is about is that AddTransform
        // reaches the stored parameter — not how many were there before it.
        int had = StoredDesign(comp).Transforms.Count;
        var pair = designer.AvailablePairs().First();
        designer.AddTransform(pair);
        Assert.Equal(had + 1, StoredDesign(comp).Transforms.Count);

        int last = designer.Transforms.Count - 1;
        designer.Transforms[last].Locked = true;
        Assert.True(StoredDesign(comp).Transforms[last].Locked);

        designer.Transforms[last].Form = TransformForm.T;
        Assert.Equal(TransformForm.T, StoredDesign(comp).Transforms[last].Form);

        designer.Dispose();
    }

    /// <summary>
    /// The echo parameters follow the design — <b>from the first committed edit onward</b>.
    /// </summary>
    /// <remarks>
    /// Opening the Designer deliberately writes nothing. A hand-authored <c>Design</c> whose echoes
    /// were never filled in therefore still shows its old labels until the first edit, and that is the
    /// lesser of the two evils: the alternative is a commit — an undo entry, and a dirty document —
    /// produced by nothing more than looking at a component.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheEchoParameters_FollowTheDesign_FromTheFirstCommittedEdit()
    {
        var (_, comp, designer) = Open(Golden());
        string Value(string n) => comp.Parameters.First(p => p.Name == n).Expression;

        Assert.Equal("1.8", Value("F1"));          // the placement default; opening wrote nothing

        designer.Term2.Resistance = 2.5;

        Assert.Equal("3.3", Value("F1"));
        Assert.Equal("5", Value("F2"));
        Assert.Equal("4", Value("Order"));
        Assert.Equal("ChebyshevFano", Value("Response"));
        Assert.Equal("200", Value("R1"));
        Assert.Equal("2.5", Value("R2"));

        designer.Dispose();
    }

    // ── §1 — undo goes to the schematic's own stack ───────────────────────────

    [Trait("Category", "Benchmark")]
    [Fact]
    public void ADesignerEdit_UndoesFromTheSchematicsOwnStack()
    {
        var (vm, comp, designer) = Open(Golden());
        Assert.False(vm.UndoRedo.CanUndo);

        designer.Order = 2;
        Assert.Equal(2, StoredDesign(comp).Order);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.Equal(4, StoredDesign(comp).Order);
        // ...and the window followed the model rather than keeping its own copy.
        Assert.Equal(4, designer.Design.Order);

        designer.Dispose();
    }

    // ── §5 — the transform rack ───────────────────────────────────────────────

    /// <summary>With link on and exactly one transform, N is fully determined (match.md §4.8).</summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void LinkWithOneTransform_DisablesTheSliderAndTheNumericBox()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = true;
        designer.AddTransform(Pair(designer, "L1 / L2"));

        Assert.Single(designer.Transforms);
        Assert.False(designer.Transforms[0].CanEditN);
        Assert.Contains("fully determined", designer.Transforms[0].DisabledReason);

        // A second one makes both movable again — PROVIDED it has room of its own. With link on, one
        // transform's travel is what the OTHERS can absorb (2026-08-20), so a second pair whose
        // positivity range is a single point gives nothing back and both rows stay determined. The
        // pairs are named rather than taken from AvailablePairs().First(), which on this fixture
        // picks exactly such a pair and made this assertion pass for the wrong reason.
        designer.AddTransform(Pair(designer, "L3 / L4"));
        Assert.True(designer.Transforms[0].CanEditN);
        Assert.True(designer.Transforms[1].CanEditN);

        designer.Dispose();
    }

    /// <summary>
    /// Dragging one slider leaves <c>Π N²</c> on target and never writes a locked row.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void LinkRedistributes_KeepsTheProductOnTarget_AndNeverWritesALockedRow()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = true;
        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.AddTransform(Pair(designer, "L3 / L4"));
        designer.AddTransform(Pair(designer, "C2 / C3"));
        Assert.Equal(3, designer.Transforms.Count);

        // Lock the step-DOWN pair. It is the one whose N the linkage would otherwise pin at its own
        // bound, so locking it is both the realistic gesture and the one that leaves N2 free to
        // absorb what N1 gives up.
        double lockedN = designer.Transforms[2].N;
        designer.Transforms[2].Locked = true;

        output.WriteLine($"before: achieved {designer.Rebuild!.Achieved:G8} " +
                         $"required {designer.Rebuild.Required:G8}");

        double n0 = designer.Transforms[0].N;
        designer.SetTransformN(0, n0 * 0.9);

        foreach (var t in designer.Transforms)
            output.WriteLine($"  {t.Label} on ({t.Record.ElementA}, {t.Record.ElementB}) N={t.N:G8} locked={t.Locked} " +
                             $"range=[{t.NMin:G6},{t.NMax:G6}]");
        output.WriteLine($"after:  achieved {designer.Rebuild!.Achieved:G8} " +
                         $"required {designer.Rebuild.Required:G8}");

        Assert.Equal(lockedN, designer.Transforms[2].N, 12);
        Assert.NotEqual(n0, designer.Transforms[0].N, 6);
        Assert.True(designer.Rebuild.OnTarget,
            $"achieved {designer.Rebuild.Achieved:G12} vs required {designer.Rebuild.Required:G12}");

        designer.Dispose();
    }

    /// <summary>
    /// The slider's range is the RECOMPUTED one, and the proof is that it MOVES when an earlier
    /// transform's N moves (MN-1 §7.2). A stored bound could not do that.
    /// </summary>
    /// <remarks>
    /// The pair used here is one of the first transform's own PRODUCTS, and deliberately so. A pair
    /// that lies entirely on one side of an earlier transform has an invariant range — the positivity
    /// threshold is a RATIO of the two element values, and absorbing the ideal transformer scales a
    /// whole side by N² — so it would be the one pair that cannot tell a recomputed bound from a
    /// stored one.
    /// </remarks>
    [Fact]
    public void SliderBounds_AreRecomputedAgainstTheLadderAsItStands()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = false;

        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.SetTransformN(0, 3.0);

        var productPair = designer.AvailablePairs()
            .First(p => p.ElementA.StartsWith("L1_N1_", StringComparison.Ordinal));
        designer.AddTransform(productPair);

        var atThree = designer.Transforms[1].Range;
        Assert.NotNull(atThree);
        output.WriteLine($"N1 = 3.0  ->  {productPair.Display} range " +
                         $"[{atThree!.Min:G8}, {atThree.Max:G8}]");

        // Move the FIRST transform and look again. Same pair, same row, different bound.
        designer.SetTransformN(0, 4.0);
        var atFour = designer.Transforms[1].Range;
        Assert.NotNull(atFour);
        output.WriteLine($"N1 = 4.0  ->  {productPair.Display} range " +
                         $"[{atFour!.Min:G8}, {atFour.Max:G8}]");

        Assert.False(
            Math.Abs(atThree.Min - atFour.Min) < 1e-12 && Math.Abs(atThree.Max - atFour.Max) < 1e-12,
            "the second transform's range did not move when the first did — it is a stored bound");

        designer.Dispose();
    }

    /// <summary>
    /// The premise, now through the UI: a Norton transform changes element values and leaves the
    /// two-port's transfer function alone. Same S-parameters, from the same engine, either side of a
    /// slider move.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheResponseDoesNotMoveWhenASliderDoes()
    {
        var (_, _, designer) = Open(Golden());
        designer.PlotPoints = 51;
        designer.LinkTransforms = true;
        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.AddTransform(Pair(designer, "L3 / L4"));
        Assert.True(designer.Rebuild!.OnTarget, "the two transforms reach the required ratio");

        var before = designer.ResponseSnp;
        Assert.NotNull(before);

        double n0 = designer.Transforms[0].N;
        designer.SetTransformN(0, n0 * 0.95);
        Assert.NotEqual(n0, designer.Transforms[0].N, 6);

        var after = designer.ResponseSnp;
        Assert.NotNull(after);

        double worst = 0.0;
        for (int f = 0; f < before!.Frequencies.Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    worst = Math.Max(worst, (before.Matrices[f][i, j] - after!.Matrices[f][i, j]).Magnitude);

        output.WriteLine($"N1 {n0:G8} -> {designer.Transforms[0].N:G8}; " +
                         $"worst |ΔS| over {before.Frequencies.Length} points = {worst:E3}");
        Assert.True(worst < 1e-9, $"the response moved by {worst:E3}");

        designer.Dispose();
    }

    /// <summary>The plots are held for the duration of a drag and refreshed on release (brief §5).</summary>
    [Fact]
    public void DuringADrag_TheLadderTracksLive_AndThePlotsCatchUpOnRelease()
    {
        var (_, _, designer) = Open(Golden());
        designer.PlotPoints = 51;
        designer.LinkTransforms = false;
        designer.AddTransform(designer.AvailablePairs().First());

        var snpBefore = designer.ResponseSnp;
        string valueBefore = designer.Elements[0].ValueText;

        designer.BeginTransformDrag();
        designer.SetTransformN(0, designer.Transforms[0].N * 0.8);

        Assert.Same(snpBefore, designer.ResponseSnp);              // the plot is held
        Assert.NotEqual(valueBefore, designer.Elements[0].ValueText); // the values are not

        designer.EndTransformDrag();
        Assert.NotSame(snpBefore, designer.ResponseSnp);

        designer.Dispose();
    }

    // ── §3 — order parity and the response selector ───────────────────────────

    /// <summary>
    /// Switching a termination's topology changes the parity the order has to have; the Designer
    /// adjusts it, <b>silently</b>, and stores what it adjusted to.
    /// </summary>
    /// <remarks>
    /// <b>This test used to demand an explanatory line, and now demands that there is none</b>
    /// (owner-reported, 2026-08-28: <i>"I don't want to see messages like this after I make changes
    /// just because the order changed. I can clearly see the order changed because a different
    /// solution card is now selected, so cluttering the UI with this message is bad UX"</i>).
    ///
    /// <para>The line was written before the Solutions panel became the specification, when the order
    /// was a control the user set by hand and nothing else on screen said it had moved. It now moves
    /// BECAUSE a card is applied, that card is the bold green-bordered row in the list, and it names
    /// its own order — so the sentence restated something already on screen, in the one column where
    /// height is scarce. The parity rule is expressed by <see cref="MatchDesignerViewModel.OrderOptions"/>
    /// offering what it offers, which is what the assertions below check.</para>
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ATopologyChange_AdjustsTheOrder_AndSaysNothingAboutIt()
    {
        var (_, comp, designer) = Open(Golden());
        Assert.Equal(4, designer.Order);                                  // mixed pair -> even
        Assert.Equal([2, 4, 6], designer.OrderOptions);

        designer.Term2.Topology = TerminationTopology.Parallel;           // now a LIKE pair -> odd

        Assert.Equal([3, 5], designer.OrderOptions);
        Assert.Contains(designer.Order, designer.OrderOptions);
        Assert.Equal(designer.Order, StoredDesign(comp).Order);

        designer.Dispose();
    }

    /// <summary>
    /// A response that cannot absorb both ends at the current order is shown DISABLED with the
    /// numeric reason in its tooltip, never silently missing (match.md §6.6).
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void AnInfeasibleResponse_IsDisabledWithItsNumbersInTheTooltip()
    {
        var (_, _, designer) = Open(Golden());

        // Every family is offered, always — that is the point of the rule.
        Assert.Equal(4, designer.ResponseOptions.Count);

        var disabled = designer.ResponseOptions.Where(o => !o.IsEnabled).ToList();
        foreach (var o in designer.ResponseOptions)
            output.WriteLine($"{o.Shape,-18} enabled={o.IsEnabled}  {o.Tooltip}");

        Assert.NotEmpty(disabled);
        foreach (var o in disabled)
        {
            Assert.NotNull(o.Refusal);
            Assert.NotEmpty(o.Refusal!.Numbers);
            // The tooltip is the refusal's own message, so it carries the refusal's own numbers.
            Assert.Equal(o.Refusal.Message, o.Tooltip);
            Assert.Matches(@"\d", o.Tooltip);
        }

        designer.Dispose();
    }

    // ── §6 — the status strip ─────────────────────────────────────────────────

    [Fact]
    public void TheStatusStrip_StatesQAndTheMatch_AndFlagsNoTermination_WhenTheDesignIsFine()
    {
        var (_, _, designer) = Open(Golden());
        output.WriteLine(designer.Status.Text);

        Assert.False(designer.Status.IsRefused);
        Assert.True(designer.Status.Q1 > 0);
        Assert.True(designer.Status.Q2 > 0);
        Assert.True(designer.Status.WorstReturnLossDb < 0);
        Assert.Contains("Π N²", designer.Status.RatioText);

        designer.Dispose();
    }

    /// <summary>
    /// A refusal appears in the strip with its numbers, and the affected termination turns red
    /// (match.md §9.7).
    /// </summary>
    [Fact]
    public void ARefusal_SurfacesInTheStatusStrip_AndTurnsTheRightTerminationRed()
    {
        // Termination 2's 10 pF at 1.25 ohm is a Q of 3.13; asking Bessel to absorb it is the
        // refusal match.md §6.4 says will be the common one.
        var (_, _, designer) = Open(Golden());
        designer.Response = ResponseShape.Bessel;

        output.WriteLine(designer.Status.Text);
        Assert.True(designer.Status.IsRefused, "Bessel was expected to refuse the golden far end");

        var refusal = designer.Status.Refusal!;
        Assert.NotEmpty(refusal.Numbers);
        Assert.Contains(refusal.Message, designer.Status.Text);

        int end = refusal.End ?? 0;
        Assert.True(end is 1 or 2, "the refusal names an end");
        Assert.Equal(end == 1, designer.Term1.IsFlagged);
        Assert.Equal(end == 2, designer.Term2.IsFlagged);

        designer.Dispose();
    }

    /// <summary>
    /// A family with nothing to offer says so — <b>and the panel does not go empty for it</b>.
    /// </summary>
    /// <remarks>
    /// <b>Rewritten 2026-08-28.</b> This used to assert an empty <c>Solutions</c> and the sentence
    /// "No solutions available for order 4" beside it, on a Bessel design. Both halves were about a
    /// panel that showed ONE order in ONE family: selecting a family that refuses left the list with
    /// nothing in it, and the refusal was the only thing on screen.
    ///
    /// <para>The list now spans every order and every family, so a Bessel that refuses is a family
    /// with no rows among rows that other families produced — which is a strictly better answer to
    /// "why can't I use Bessel here?" than an empty panel was. What is asserted is that pair: the
    /// family genuinely refuses (its verdict carries MN-1's numbers), and the panel is not empty
    /// because of it. <c>SolutionsRefusal</c> is now reserved for the case where the WHOLE
    /// cross-product came back empty — see <c>LandSearchComplete</c>.</para>
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void WhenAFamilyFindsNothing_ItSaysSo_AndTheListIsNotEmptyForIt()
    {
        var design = Golden();
        design.Response = ResponseShape.Bessel;
        var (_, _, designer) = Open(design);
        designer.WaitForAnalysis();

        var bessel = designer.ResponseOptions.Single(o => o.Shape == ResponseShape.Bessel);
        Assert.False(bessel.IsEnabled, "the fixture has to be a family that refuses to mean anything");
        Assert.NotEmpty(bessel.Tooltip);

        // No Bessel rows AT THIS ORDER — which is what the verdict above says. Bessel at some other
        // order is a different question and one the list is now entitled to answer.
        Assert.DoesNotContain(designer.AllSolutions,
                              r => r.Response == ResponseShape.Bessel && r.Order == designer.Design.Order);
        Assert.NotEmpty(designer.AllSolutions);
        Assert.Equal("", designer.SolutionsRefusal);
        output.WriteLine(designer.SolutionsSummary);

        designer.Dispose();
    }

    // ── §4 — absorbed elements are identified in WORDS and by POSITION, never by dimming ──

    /// <summary>
    /// Round 3 inverted this test (owner, 2026-08-20: "do not render any component as dimmed"). What
    /// it holds now is the pair of things that replaced the wash: <b>both presentations still name
    /// the same elements</b>, the legend NAMES them rather than asking the user to spot a brightness
    /// difference, and every glyph is drawn in the one symbol colour.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void AbsorbedElements_AreNamedByTheLegend_AndDrawnLikeEverythingElse()
    {
        var (_, _, designer) = Open(Golden());

        var absorbedInGrid = designer.Elements.Where(e => e.Role == MatchElementRole.Absorbed).ToList();
        Assert.Equal(2, absorbedInGrid.Count);      // the golden design absorbs one element per end

        var absorbedInLadder = designer.Ladder.Elements
            .Where(e => e.Role == MatchElementRole.Absorbed).ToList();

        // The two presentations name the SAME elements, which is the property that matters.
        Assert.Equal(
            absorbedInGrid.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal),
            absorbedInLadder.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));

        // NOTHING is dimmed — every element, absorbed or not, is the schematic's own symbol colour.
        Assert.All(designer.Ladder.Elements,
                   e => Assert.Equal(ColorRole.SchematicSymbolLine, e.ColorRoleKey));
        Assert.All(absorbedInGrid,
                   e => Assert.Equal(ColorRole.SchematicSymbolLine, e.ColorRoleKey));

        // The legend states it in words and names them, rather than leaving it to be inferred.
        Assert.True(designer.Ladder.HasAbsorbed);
        output.WriteLine(designer.LadderLegend);
        Assert.DoesNotContain("Dimmed", designer.LadderLegend, StringComparison.OrdinalIgnoreCase);
        foreach (var e in absorbedInLadder)
            Assert.Contains(e.Name, designer.LadderLegend, StringComparison.Ordinal);

        designer.Dispose();
    }

    /// <summary>
    /// <b>An absorbed element is drawn next to the termination that supplies it</b> (owner,
    /// 2026-08-20: "the absorbed parasitic component must always be placed adjacent to its
    /// corresponding R termination"). End 1's is the leftmost column, end 2's the rightmost.
    /// </summary>
    /// <remarks>
    /// It is not where the synthesis leaves it: an arm is emitted L-then-C, so an end arm whose
    /// absorbed half is the C has its own L standing between the parasitic and the termination, and
    /// <c>WithEndSplits</c> then inserts the Fano/detune element further out still. The re-ordering
    /// is display-only and provably circuit-preserving — see
    /// <c>MatchLadderLayout.DisplayOrder</c>.
    /// </remarks>
    [Fact]
    public void AnAbsorbedElement_SitsBesideItsOwnTermination()
    {
        var (_, _, designer) = Open(Golden());
        var elements = designer.Ladder.Elements;

        foreach (var e in elements)
            output.WriteLine($"{e.Name,-10} x {e.X,7:F0} {(e.IsShunt ? "shunt" : "series")} {e.Role}");

        var order = MatchLadderLayout.DisplayOrder(designer.Rebuild!.Network!.Elements);
        int end1 = order.ToList().FindIndex(e => e.AbsorbedEnd == 1);
        int end2 = order.ToList().FindIndex(e => e.AbsorbedEnd == 2);
        Assert.Equal(0, end1);
        Assert.Equal(order.Count - 1, end2);

        // ...which in world terms is the column nearest each TermG.
        Assert.Equal(elements.Min(e => e.X), elements.Single(e => e.Name == order[end1].Name).X, 6);
        Assert.Equal(elements.Max(e => e.X), elements.Single(e => e.Name == order[end2].Name).X, 6);

        designer.Dispose();
    }

    /// <summary>
    /// The re-ordering above steps over elements it PROVABLY commutes with and nothing else: two
    /// adjacent ladder elements share an arm exactly when they share an orientation, so a walk that
    /// stops at the first orientation change never crosses a node.
    /// </summary>
    [Fact]
    public void DisplayOrder_NeverStepsOverAnOrientationChange()
    {
        var absorbed1 = new MatchElement { Name = "Ca", Type = ElementType.C, IsShunt = false, Value = 1e-12, AbsorbedEnd = 1 };
        var blocker   = new MatchElement { Name = "Lb", Type = ElementType.L, IsShunt = true,  Value = 1e-9 };
        var tail      = new MatchElement { Name = "Cc", Type = ElementType.C, IsShunt = true,  Value = 2e-12 };

        // The absorbed element is already at index 1, behind a SHUNT element it does not share a node
        // with — so it must not move at all.
        var order = MatchLadderLayout.DisplayOrder([blocker, absorbed1, tail]);
        Assert.Equal(["Lb", "Ca", "Cc"], order.Select(e => e.Name));

        // With a same-orientation neighbour in front of it, it walks past exactly that one.
        var sameArm = new MatchElement { Name = "Ld", Type = ElementType.L, IsShunt = false, Value = 1e-9 };
        var order2 = MatchLadderLayout.DisplayOrder([sameArm, absorbed1, blocker]);
        Assert.Equal(["Ca", "Ld", "Lb"], order2.Select(e => e.Name));
    }

    /// <summary>Brackets are drawn under the products a transform created, and stacked when they
    /// would overlap (§4).</summary>
    [Fact]
    public void TransformBrackets_SitUnderTheirOwnProducts_AndStackWhenTheyOverlap()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = false;
        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.AddTransform(Pair(designer, "L3 / L4"));

        var brackets = designer.Ladder.Brackets;
        Assert.Equal(2, brackets.Count);
        foreach (var b in brackets)
            output.WriteLine($"{b.Label}: x {b.X0:F0}..{b.X1:F0} row {b.Row}");

        Assert.Equal(["N1", "N2"], brackets.Select(b => b.Label));
        Assert.All(brackets, b => Assert.True(b.X1 > b.X0));

        // Two brackets that overlap in x must not share a row; two that do not may.
        for (int i = 0; i < brackets.Count; i++)
            for (int j = i + 1; j < brackets.Count; j++)
                if (brackets[i].X0 < brackets[j].X1 && brackets[j].X0 < brackets[i].X1)
                    Assert.NotEqual(brackets[i].Row, brackets[j].Row);

        designer.Dispose();
    }

    // ── the grid's own promises ───────────────────────────────────────────────

    [Fact]
    public void TheGrid_CopiesAsCsv_AndHasNoStandardValueColumn()
    {
        var (_, _, designer) = Open(Golden());
        string csv = designer.ElementsCsv;
        output.WriteLine(csv);

        Assert.StartsWith("instance,type,orientation,value,unit,note", csv);
        Assert.Equal(designer.Elements.Count + 1, csv.TrimEnd().Split('\n').Length);
        Assert.DoesNotContain("E12", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E24", csv, StringComparison.OrdinalIgnoreCase);

        // An element is listed under the SAME name the schematic labels it with — "L1", never
        // "MN1.L1" (owner, 2026-08-20). A Match contains no sub-instances, so the qualified spelling
        // was a path to something that does not exist.
        Assert.DoesNotContain("MN1.", csv, StringComparison.Ordinal);
        Assert.All(designer.Elements, r => Assert.Equal(r.Name, r.Instance));

        // Value and unit are one column in the VIEW and two in the CSV, on purpose: a spreadsheet
        // wants to sort on the number.
        Assert.Equal("14.9 pF", designer.Elements.Single(r => r.Name == "C1").ValueWithUnit);

        designer.Dispose();
    }

    // ── §9 — what is wired but not built ──────────────────────────────────────

    /// <summary>
    /// <b>Nothing in the footer is a placeholder any more.</b> Both Flatten and Probe are built, and
    /// on this bare fixture — a <c>Match</c> alone in a scratch schematic with nothing wired to
    /// either pin — each is disabled for a REAL reason it names, not for a brief it waits on.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite for each in turn: MN-4 built the probe and MN-5 built
    /// the flatten. Kept rather than deleted because the property worth holding is the one that
    /// survived both — <b>a disabled control in this window states a condition the user can act
    /// on</b>. The full sets live in <c>MatchProbeTests</c> and <c>MatchFlattenTests</c>.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ADisabledFooterControl_NamesAConditionTheUserCanActOn_NeverABriefItWaitsOn()
    {
        var (_, _, designer) = Open(Golden());

        // Flatten: this fixture's schematic was never saved, so there is nowhere to put a cell.
        Assert.False(designer.CanFlatten);
        Assert.Contains("Save this schematic", designer.FlattenTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("MN-5", designer.FlattenTooltip, StringComparison.Ordinal);

        // Probe: nothing is wired to MN1.
        Assert.False(designer.Term1.CanProbe);
        Assert.Equal(MatchProbeBlock.NetIsBare, designer.Term1.Availability.Block);
        Assert.DoesNotContain("MN-4", designer.Term1.ProbeTooltip);

        designer.Dispose();
    }

    // ── the response netlist is the FULL design ───────────────────────────────

    /// <summary>
    /// The plotted netlist contains the absorbed elements and terminates in R1 and R2 — it is the
    /// response the user is judging, not the component's own (match.md §9.6).
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ThePlottedNetlist_IsTheFullDesign_TerminatedInR1AndR2()
    {
        var (_, _, designer) = Open(Golden());
        var network = designer.Rebuild!.Network!;
        string cnl = MatchDesignerViewModel.BuildNetlist(network);
        output.WriteLine(cnl);

        int elementLines = cnl.Split('\n').Count(l => l.StartsWith("L:", StringComparison.Ordinal)
                                                   || l.StartsWith("C:", StringComparison.Ordinal));
        Assert.Equal(network.Elements.Count, elementLines);
        Assert.True(network.Elements.Any(e => e.IsAbsorbed), "the golden design absorbs something");

        Assert.Contains($"Num=1 Z={network.R1.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", cnl);
        Assert.Contains($"Num=2 Z={network.R2.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", cnl);

        // ...and the far port really did reach the requested termination.
        Assert.Equal(designer.Design.Term2.R, network.R2, 6);

        designer.Dispose();
    }

    /// <summary>Both plots exist, with the traces §9.6 names — and no trace renormalises.</summary>
    [Fact]
    public void BothPlots_CarryTheirTraces_AndNoTraceRenormalises()
    {
        var (_, _, designer) = Open(Golden());
        designer.PlotPoints = 101;

        Assert.Equal(2, designer.MagnitudePlot.Traces.Count);
        Assert.Equal(2, designer.PhasePlot.Traces.Count);
        Assert.All(designer.MagnitudePlot.Traces, t => Assert.False(t.Z0OverrideEnabled));
        Assert.All(designer.PhasePlot.Traces, t => Assert.False(t.Z0OverrideEnabled));

        // The two ports genuinely differ, and every trace records that rather than flattening it.
        var net = designer.Rebuild!.Network!;
        foreach (var t in designer.MagnitudePlot.Traces)
        {
            Assert.NotNull(t.SourceZ0PerPort);
            Assert.Equal(net.R1, t.SourceZ0PerPort![0].Real, 6);
            Assert.Equal(net.R2, t.SourceZ0PerPort[1].Real, 6);
        }

        Assert.Equal(DependentVarFormat.Db, designer.MagnitudePlot.Traces[0].YAxis);
        Assert.Equal(DependentVarFormat.Phase, designer.PhasePlot.Traces[0].YAxis);
        // NAMED WITH ITS UNIT since 2026-08-28 (owner: the right y-axis label needs the group delay's
        // units on it), and named from the application's own derived-parameter table rather than
        // written out here — the axis label, the marker readout and the info box all derive from this
        // one string, so a literal in this test would be a second spelling to drift from.
        Assert.Equal(MatchDesignerViewModel.TraceGroupDelayName,
                     designer.PhasePlot.Traces[1].CubeName);
        Assert.Contains("(ns)", designer.PhasePlot.Traces[1].CubeName!, StringComparison.Ordinal);
        Assert.True(designer.PhasePlot.Traces[1].UseSecondaryAxis);

        designer.Dispose();
    }
}
